using System.IO;
using System.IO.Pipes;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Oxp3.Controls;

namespace Oxp3GamePower.Desktop;

public partial class MainWindow : Window
{
    private sealed record Change(string GameId, string Exe, bool PerGame, int Watts);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer inputTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer writeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ControllerInput controller = new();
    private readonly DispatcherTimer brightnessTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer strengthTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DeferredSlider brightnessEdit = new(), strengthEdit = new();
    private bool brightnessAvailable, nightAvailable, strengthAvailable, displayRefreshing, displayUpdating, nightWriting;
    private long nightRevision, powerRevision;
    private DateTimeOffset powerDue;
    private bool refreshing, writing, connected, updating, perGame, closing;
    private bool restartRequired, powerStateRead;
    private readonly Func<object, Task<JsonElement>>? requestOverride;
    private string gameId = "", exePath = "", iconData = "";
    private int? nativeWatts;
    private Change? pending;
    private DateTime feedbackUntil;
    public MainWindow() : this(null) { }
    internal MainWindow(Func<object, Task<JsonElement>>? requestOverride)
    {
        this.requestOverride = requestOverride;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            var helpers = Process.GetProcessesByName("OXP3.PowerWidget.Helper");
            bool helperRunning = helpers.Length > 0;
            foreach (var helper in helpers) helper.Dispose();
            if (!helperRunning && requestOverride == null)
            {
                try { Process.Start(new ProcessStartInfo("ms-gamebar:activate/OXP3.PowerWidget_pv4gfha69c7qe_App_PowerProfiles") { UseShellExecute = true }); }
                catch (Exception error) { Feedback("Could not start the helper: " + error.Message); }
            }
            await RefreshDisplay(); await Refresh(); timer.Start(); inputTimer.Start(); GameHeader.Focus();
        };
        Closed += (_, _) => { timer.Stop(); inputTimer.Stop(); writeTimer.Stop(); brightnessTimer.Stop(); strengthTimer.Stop(); };
        Closing += async (_, e) =>
        {
            if (pending == null && !writing && !brightnessEdit.Busy && !strengthEdit.Busy && !nightWriting) return;
            e.Cancel = true; closing = true; UpdateControls();
            while (writing || pending != null) { if (!writing) await FlushChanges(true); else await Task.Delay(50); }
            while (brightnessEdit.Busy || strengthEdit.Busy || nightWriting) { await FlushBrightness(true); await FlushStrength(true); await Task.Delay(50); }
            Close();
        };
        Deactivated += async (_, _) => { controller.Reset(); await FlushBrightness(true); await FlushStrength(true); await FlushChanges(true); };
        Activated += (_, _) => GameHeader.Focus();
        timer.Tick += async (_, _) => { await RefreshDisplay(); await Refresh(); };
        brightnessTimer.Tick += async (_, _) => await FlushBrightness();
        strengthTimer.Tick += async (_, _) => await FlushStrength();
        writeTimer.Tick += async (_, _) => await FlushChanges();
        inputTimer.Tick += (_, _) =>
        {
            if (!IsActive) { controller.Reset(); return; }
            foreach (var action in controller.Poll()) Navigate(action);
        };
        UpdateControls();
    }
    private Task<JsonElement> Request(object request) => requestOverride?.Invoke(request) ?? RequestHelper(request);
    private static async Task<JsonElement> RequestHelper(object request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var pipe = new NamedPipeClientStream(".", "OXP3.PowerWidget.Bridge.v1", PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request)).WaitAsync(timeout.Token);
        var response = await reader.ReadLineAsync(timeout.Token);
        if (response is null || response.Length > 65536) throw new IOException("Invalid helper response.");
        using var json = JsonDocument.Parse(response);
        if (!json.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(json.RootElement.GetProperty("error").GetString());
        return json.RootElement.GetProperty("result").Clone();
    }
    private void UpdateControls()
    {
        // The slider stays free while a previous value is being applied.
        Watts.IsEnabled = connected && !closing;
        WattControls.Opacity = connected ? 1 : 0.45;
        PowerUnavailable.Text = connected ? "" : !powerStateRead ? "Connecting to power control…" : PowerAvailability.Message(false, restartRequired);
        PowerUnavailable.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        if (!connected) TargetWatts.Text = powerStateRead ? "Unavailable" : "— W";
        PerGame.IsEnabled = connected && gameId.Length > 0 && !writing && pending == null && !closing;
        Brightness.IsEnabled = brightnessAvailable && !closing;
        NightLight.IsEnabled = nightAvailable && !nightWriting && !closing;
        Strength.IsEnabled = strengthAvailable && NightLight.IsChecked == true && !nightWriting && !closing;
    }
    private async Task RefreshDisplay()
    {
        if (displayRefreshing || brightnessEdit.Busy || strengthEdit.Busy || nightWriting || closing) return;
        long brightnessRead = brightnessEdit.Revision, strengthRead = strengthEdit.Revision, nightRead = nightRevision;
        displayRefreshing = true;
        try
        {
            var state = await Request(new { command = "getDisplayState" });
            if (!brightnessEdit.AcceptRead(brightnessRead) || !strengthEdit.AcceptRead(strengthRead) || nightWriting || nightRead != nightRevision) return;
            brightnessAvailable = state.GetProperty("brightnessAvailable").GetBoolean(); nightAvailable = state.GetProperty("nightLightAvailable").GetBoolean();
            displayUpdating = true;
            if (brightnessAvailable) Brightness.Value = state.GetProperty("brightness").GetInt32();
            BrightnessValue.Text = brightnessAvailable ? $"{Math.Round(Brightness.Value)}%" : "—%";
            NightLight.IsChecked = nightAvailable && state.GetProperty("nightLight").GetBoolean();
            var value = state.GetProperty("nightLightStrength"); strengthAvailable = value.ValueKind == JsonValueKind.Number;
            if (strengthAvailable) Strength.Value = value.GetInt32();
            StrengthValue.Text = strengthAvailable ? $"{Math.Round(Strength.Value)}%" : "—%";
            bool showStrength = NightLight.IsChecked == true;
            if ((StrengthCard.Visibility == Visibility.Visible) != showStrength) { StrengthCard.Visibility = showStrength ? Visibility.Visible : Visibility.Collapsed; Height = showStrength ? 590 : 480; }
        }
        catch (Exception error) { if (brightnessEdit.AcceptRead(brightnessRead) && strengthEdit.AcceptRead(strengthRead) && nightRead == nightRevision) { brightnessAvailable = nightAvailable = strengthAvailable = false; Feedback(error.Message); } }
        finally { displayUpdating = displayRefreshing = false; UpdateControls(); }
    }
    private void BrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessValue == null) return;
        BrightnessValue.Text = $"{Math.Round(e.NewValue)}%";
        if (displayUpdating || !brightnessAvailable || closing) return;
        brightnessEdit.Queue((int)Math.Round(e.NewValue), DateTimeOffset.UtcNow); ScheduleDisplayWrite(brightnessTimer, brightnessEdit);
    }
    private static void ScheduleDisplayWrite(DispatcherTimer write, DeferredSlider edit)
    {
        write.Stop(); if (edit.Pending.HasValue) { write.Interval = edit.Delay(DateTimeOffset.UtcNow); write.Start(); }
    }
    private async Task FlushBrightness(bool force = false)
    {
        brightnessTimer.Stop();
        if (!brightnessEdit.Begin(DateTimeOffset.UtcNow, force, out int value)) { if (!brightnessEdit.Writing) ScheduleDisplayWrite(brightnessTimer, brightnessEdit); return; }
        try
        {
            var state = await Request(new { command = "setBrightness", brightness = value });
            if (state.GetProperty("brightness").GetInt32() != value) throw new IOException("Windows did not confirm the brightness.");
        }
        catch (Exception error) { Feedback(error.Message); }
        finally { brightnessEdit.Complete(); ScheduleDisplayWrite(brightnessTimer, brightnessEdit); if (!brightnessEdit.Busy) await RefreshDisplay(); }
    }
    private void StrengthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (StrengthValue == null) return;
        StrengthValue.Text = $"{Math.Round(e.NewValue)}%";
        if (displayUpdating || !strengthAvailable || closing) return;
        strengthEdit.Queue((int)Math.Round(e.NewValue), DateTimeOffset.UtcNow); ScheduleDisplayWrite(strengthTimer, strengthEdit);
    }
    private async Task FlushStrength(bool force = false)
    {
        strengthTimer.Stop();
        if (!strengthEdit.Begin(DateTimeOffset.UtcNow, force, out int value)) { if (!strengthEdit.Writing) ScheduleDisplayWrite(strengthTimer, strengthEdit); return; }
        try
        {
            var state = await Request(new { command = "setNightLightStrength", strength = value });
            if (state.GetProperty("nightLightStrength").GetInt32() != value) throw new IOException("Windows did not confirm Night light strength.");
        }
        catch (Exception error) { Feedback(error.Message); }
        finally { strengthEdit.Complete(); ScheduleDisplayWrite(strengthTimer, strengthEdit); if (!strengthEdit.Busy) await RefreshDisplay(); }
    }
    private async void NightLightChanged(object sender, RoutedEventArgs e)
    {
        if (displayUpdating || !NightLight.IsEnabled) return;
        bool value = NightLight.IsChecked == true; nightRevision++; nightWriting = true; UpdateControls();
        try
        {
            var state = await Request(new { command = "setNightLight", enabled = value });
            if (state.GetProperty("nightLight").GetBoolean() != value) throw new IOException("Windows did not confirm Night light.");
        }
        catch (Exception error) { Feedback(error.Message); }
        finally { nightWriting = false; await RefreshDisplay(); NightLight.Focus(); }
    }
    private void Feedback(string message)
    {
        Status.Text = message;
        Status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        feedbackUntil = DateTime.UtcNow.AddSeconds(8);
    }
    private async Task Refresh()
    {
        if (refreshing || writing || pending != null || closing) return;
        refreshing = true;
        long revision = powerRevision;
        try
        {
            var state = await Request(new { command = "getState" });
            if (writing || pending != null || revision != powerRevision) return;
            connected = state.GetProperty("connected").GetBoolean();
            restartRequired = state.TryGetProperty("restartRequired", out var needsRestart) && needsRestart.GetBoolean(); powerStateRead = true;
            nativeWatts = connected ? state.GetProperty("nativeWatts").GetInt32() : null;
            var game = state.GetProperty("playingGame");
            gameId = game.ValueKind == JsonValueKind.Object ? game.GetProperty("id").GetString()! : "";
            exePath = gameId.Length > 0 ? game.GetProperty("exePath").GetString()! : "";
            GameName.Text = gameId.Length > 0 ? game.GetProperty("name").GetString()! : "No game running";
            ShowIcon(gameId.Length > 0 && game.TryGetProperty("iconPng", out var icon) ? icon.GetString() ?? "" : "");
            perGame = gameId.Length > 0 && state.GetProperty("usePerGame").GetBoolean();
            updating = true;
            PerGame.IsChecked = perGame;
            Watts.Minimum = Math.Min(Watts.Minimum, state.GetProperty("minWatts").GetInt32());
            Watts.Maximum = state.GetProperty("maxWatts").GetInt32();
            Watts.Minimum = state.GetProperty("minWatts").GetInt32();
            if (nativeWatts.HasValue) Watts.Value = nativeWatts.Value;
            TargetWatts.Text = $"{Math.Round(Watts.Value)} W";
            MinimumWatts.Text = $"{Watts.Minimum:0} W"; MaximumWatts.Text = $"{Watts.Maximum:0} W";
            if (connected && DateTime.UtcNow >= feedbackUntil) Feedback("");
        }
        catch (Exception) { if (revision == powerRevision && pending == null) { connected = false; nativeWatts = null; powerStateRead = true; } }
        finally { updating = refreshing = false; UpdateControls(); }
    }
    private void ShowIcon(string data)
    {
        if (iconData == data) return;
        iconData = data; GameIcon.Source = null; GameIcon.Visibility = Visibility.Collapsed;
        if (data.Length == 0 || data.Length > 32768) return;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(data));
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            GameIcon.Source = image; GameIcon.Visibility = Visibility.Visible;
        }
        catch (Exception) { }
    }
    private void TargetChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TargetWatts is null) return;
        TargetWatts.Text = $"{Math.Round(e.NewValue)} W";
        if (updating || !connected || closing) return;
        pending = new Change(gameId, exePath, perGame, (int)Math.Round(e.NewValue));
        powerRevision++; powerDue = DateTimeOffset.UtcNow.AddSeconds(1); SchedulePowerWrite(); UpdateControls();
    }
    private void SchedulePowerWrite()
    {
        writeTimer.Stop(); if (pending == null) return;
        var now = DateTimeOffset.UtcNow; writeTimer.Interval = powerDue > now ? powerDue - now : TimeSpan.FromMilliseconds(10); writeTimer.Start();
    }
    private async Task FlushChanges(bool force = false)
    {
        writeTimer.Stop();
        if (writing || pending == null) return;
        if (!force && DateTimeOffset.UtcNow < powerDue) { SchedulePowerWrite(); return; }
        long revision = powerRevision;
        var change = pending; pending = null; writing = true; UpdateControls();
        try
        {
            var result = await Request(new { command = "adjustWatts", gameId = change.GameId, exePath = change.Exe, scope = change.PerGame ? "game" : "global", watts = change.Watts });
            if (result.GetProperty("nativeWatts").GetInt32() != change.Watts) throw new IOException("ONEXConsole did not confirm the wattage.");
            nativeWatts = change.Watts;
            if (pending == null) Feedback("");
        }
        catch (Exception error) { if (revision == powerRevision) pending = null; Feedback(error.Message); }
        finally
        {
            writing = false;
            if (pending != null) SchedulePowerWrite(); else await Refresh();
            UpdateControls();
        }
    }
    private async void ScopeChanged(object sender, RoutedEventArgs e)
    {
        if (updating || !PerGame.IsEnabled || writing || pending != null) return;
        bool desired = PerGame.IsChecked == true;
        powerRevision++;
        writing = true; UpdateControls();
        try
        {
            var result = await Request(new { command = "setScope", gameId, exePath, usePerGame = desired });
            nativeWatts = result.GetProperty("nativeWatts").GetInt32(); perGame = result.GetProperty("usePerGame").GetBoolean();
            Feedback("");
        }
        catch (Exception error) { Feedback(error.Message); }
        finally { writing = false; await Refresh(); PerGame.Focus(); }
    }
    private void Adjust(int delta) { if (Watts.IsEnabled) Watts.Value = Math.Clamp(Math.Round(Watts.Value) + delta, Watts.Minimum, Watts.Maximum); }
    private void NavigateKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        InputAction? action = key switch { Key.Left => InputAction.Left, Key.Right => InputAction.Right, Key.Up => InputAction.Up, Key.Down => InputAction.Down, Key.Enter => InputAction.Select, Key.Space => InputAction.Select, Key.Escape => InputAction.Back, Key.F10 => InputAction.Settings, _ => null };
        if (action.HasValue) { e.Handled = true; if (!e.IsRepeat || action.Value <= InputAction.Down) Navigate(action.Value); }
    }
    private void Navigate(InputAction action)
    {
        if (closing) return;
        if (action == InputAction.Settings) return;
        if (action == InputAction.Back) { Watts.Focus(); return; }
        Control[] controls = [PerGame, Watts, Brightness, NightLight, Strength];
        controls = controls.Where(c => c.IsEnabled).ToArray();
        if (controls.Length == 0) return;
        int index = Array.FindIndex(controls, c => c.IsKeyboardFocusWithin);
        if (action == InputAction.Up || action == InputAction.Down)
        {
            int next = index < 0 ? (action == InputAction.Down && Watts.IsEnabled ? Array.IndexOf(controls, Watts) : 0) : (index + (action == InputAction.Down ? 1 : controls.Length - 1)) % controls.Length;
            controls[next].Focus(); return;
        }
        if (index < 0) { controls[0].Focus(); index = 0; }
        if (action == InputAction.Left || action == InputAction.Right) { if (controls[index] == Watts) Adjust(action == InputAction.Left ? -1 : 1); else if (controls[index] == Brightness || controls[index] == Strength) { var control = (Slider)controls[index]; control.Value = Math.Clamp(Math.Round(control.Value) + (action == InputAction.Left ? -1 : 1), 0, 100); } return; }
        if (action == InputAction.Select)
        {
            if (controls[index] == PerGame) { PerGame.IsChecked = PerGame.IsChecked != true; ScopeChanged(PerGame, new RoutedEventArgs()); }
            else if (controls[index] == NightLight) { NightLight.IsChecked = NightLight.IsChecked != true; NightLightChanged(NightLight, new RoutedEventArgs()); }
        }
    }
}
