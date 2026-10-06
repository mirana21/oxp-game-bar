using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Input;
using Windows.System;
using Oxp3.Controls;

namespace Oxp3PowerWidget
{
    public sealed class PowerPage : Page
    {
        private sealed class Change { public string GameId, Exe; public bool PerGame; public double Watts; }
        private readonly BridgeClient bridge = new BridgeClient();
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private readonly DispatcherTimer recoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        private bool recovering;
        private readonly DispatcherTimer writeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer brightnessTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        private readonly Slider brightness = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 1, Value = 50, IsEnabled = false };
        private readonly CheckBox nightLight = new CheckBox { Content = "Night light", IsEnabled = false };
        private readonly TextBlock brightnessLabel = Label("Brightness", 16), brightnessValue = Label("—%", 18);
        private readonly Border brightnessCard = new Border { BorderBrush = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6, 10, 6) };
        private readonly DispatcherTimer strengthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DeferredSlider brightnessEdit = new DeferredSlider(), strengthEdit = new DeferredSlider();
        private readonly Slider strength = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 1, Value = 50, IsEnabled = false };
        private readonly TextBlock strengthLabel = Label("Night light strength", 16), strengthValue = Label("—%", 18);
        private readonly Border strengthCard = new Border { Visibility = Visibility.Collapsed, BorderBrush = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6, 10, 6) };
        private bool displayPolling, nightWriting, updatingDisplay, brightnessAvailable, nightAvailable, strengthAvailable;
        private long nightRevision, powerRevision;
        private DateTimeOffset powerDue;
        private readonly XboxGameBarAppTargetTracker targetTracker;
        private readonly XboxGameBarWidget hostedWidget;
        private readonly TextBlock gameName = Label("No game running", 16);
        private readonly TextBlock powerLabel = Label("Power limit (TDP)", 16);
        private readonly Grid header = Columns(GridLength.Auto, new GridLength(1, GridUnitType.Star));
        private readonly ContentControl entryHeader = new ContentControl { IsTabStop = true, UseSystemFocusVisuals = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        private readonly TextBlock targetWatts = Label("— W", 18);
        private readonly TextBlock minimum = Label("4 W", 11), maximum = Label("35 W", 11);
        private readonly TextBlock connection = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        private readonly TextBlock powerUnavailable = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        private readonly StackPanel wattControls = new StackPanel();
        private readonly Slider slider = new Slider { Minimum = 4, Maximum = 35, StepFrequency = 1, Value = 17 };
        private readonly CheckBox perGameSwitch = new CheckBox { Content = "Use per-game wattage", IsEnabled = false };
        private readonly StackPanel mainPanel = new StackPanel { Spacing = 8 };
        private readonly Border wattCard = new Border { BorderBrush = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6, 10, 6) };
        private readonly Image gameIcon = new Image { Width = 32, Height = 32 };
        private readonly Border iconFrame = new Border { Width = 32, Height = 32, Margin = new Thickness(0, 0, 10, 0), Visibility = Visibility.Collapsed };
        private string gameId = "", exePath = "", iconData = "";
        private double? nativeValue;
        private Change pending;
        private DateTimeOffset feedbackUntil;
        private bool loaded, polling, writing, bridgeAvailable, connected, updatingSlider, usePerGame;
        private bool restartRequired, powerStateRead;
        private bool hostWriting;
        private string lastHostState = "", lastResponseState = "";
        private JsonObject resizeState;

        public PowerPage(XboxGameBarWidget widget)
        {
            hostedWidget = widget;
            targetTracker = new XboxGameBarAppTargetTracker(widget);
            widget.VisibleChanged += (_, __) => HostChanged(true);
            widget.WindowStateChanged += (_, __) => HostChanged(false);
            widget.GameBarDisplayModeChanged += (_, __) => HostChanged(false);
            widget.CompactModeEnabledChanged += (_, __) => HostChanged(false, true);
            BuildInterface();
            slider.ValueChanged += (_, __) =>
            {
                targetWatts.Text = Math.Round(slider.Value).ToString("0", CultureInfo.InvariantCulture) + " W";
                if (updatingSlider || !connected || !loaded) return;
                pending = new Change { GameId = gameId, Exe = exePath, PerGame = usePerGame, Watts = Math.Round(slider.Value) };
                powerRevision++; powerDue = DateTimeOffset.UtcNow.AddSeconds(1);
                SchedulePowerWrite(); UpdateControls();
            };
            perGameSwitch.Click += async (_, __) => await SetScopeAsync();
            brightness.ValueChanged += (_, __) =>
            {
                brightnessValue.Text = Math.Round(brightness.Value).ToString("0", CultureInfo.InvariantCulture) + "%";
                if (updatingDisplay || !loaded || !brightnessAvailable) return;
                brightnessEdit.Queue((int)Math.Round(brightness.Value), DateTimeOffset.UtcNow); ScheduleDisplayWrite(brightnessTimer, brightnessEdit);
            };
            brightnessTimer.Tick += async (_, __) => await FlushBrightnessAsync();
            strength.ValueChanged += (_, __) =>
            {
                strengthValue.Text = Math.Round(strength.Value).ToString("0", CultureInfo.InvariantCulture) + "%";
                if (updatingDisplay || !loaded || !strengthAvailable) return;
                strengthEdit.Queue((int)Math.Round(strength.Value), DateTimeOffset.UtcNow); ScheduleDisplayWrite(strengthTimer, strengthEdit);
            };
            strengthTimer.Tick += async (_, __) => await FlushStrengthAsync();
            nightLight.Click += async (_, __) => await SetNightLightAsync();
            PreviewKeyDown += NavigateKey;
            timer.Tick += async (_, __) =>
            {
                if (!loaded || !hostedWidget.Visible) { timer.Stop(); return; }
                await RefreshDisplayAsync(); await RefreshStateAsync();
            };
            recoveryTimer.Tick += async (_, __) =>
            {
                if (!loaded || hostedWidget.Visible) { recoveryTimer.Stop(); return; }
                if (recovering) return;
                recovering = true;
                try { await bridge.RequestAsync("ping"); }
                catch (Exception exception) { await LogAsync(exception); }
                finally { recovering = false; }
            };
            writeTimer.Tick += async (_, __) => await FlushChangesAsync();
            Loaded += async (_, __) =>
            {
                loaded = true;
                await WriteHostStateAsync();
                try { await App.StartHelperAsync(); }
                catch (Exception exception) { await ReportAsync("Helper could not start.", exception); }
                await RefreshDisplayAsync(); await RefreshStateAsync();
                if (loaded && hostedWidget.Visible) { timer.Start(); entryHeader.Focus(FocusState.Programmatic); }
                else if (loaded) recoveryTimer.Start();
                await ResetCardSizeAsync();
            };
            Unloaded += async (_, __) => { loaded = false; timer.Stop(); recoveryTimer.Stop(); await FlushBrightnessAsync(true); await FlushStrengthAsync(true); await FlushChangesAsync(true); };
            UpdateControls();
        }
        // Game Bar SDK events can arrive on a worker thread. Both timers and
        // XAML controls must be accessed through the page's UI dispatcher.
        private async void HostChanged(bool flushWhenHidden, bool modeChanged = false)
        {
            try
            {
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
                {
                    try
                    {
                        if (modeChanged) { ApplyDisplayMetrics(); await ResetCardSizeAsync(); }
                        if (flushWhenHidden)
                        {
                            timer.Stop();
                            recoveryTimer.Stop();
                            if (loaded && hostedWidget.Visible)
                            {
                                await RefreshDisplayAsync(); await RefreshStateAsync();
                                if (loaded && hostedWidget.Visible) { timer.Start(); entryHeader.Focus(FocusState.Programmatic); }
                            }
                            else if (!hostedWidget.Visible)
                            {
                                await FlushBrightnessAsync(true); await FlushStrengthAsync(true); await FlushChangesAsync(true);
                                if (loaded && !hostedWidget.Visible) recoveryTimer.Start();
                            }
                        }
                        await WriteHostStateAsync();
                    }
                    catch (Exception exception) { await LogAsync(exception); }
                });
            }
            catch (Exception exception) { await LogAsync(exception); }
        }
        private static SolidColorBrush Brush(byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(255, r, g, b));
        private static TextBlock Label(string text, double size) => new TextBlock { Text = text, FontSize = size, Foreground = new SolidColorBrush(Colors.White), TextWrapping = TextWrapping.Wrap };
        private static Grid Columns(params GridLength[] widths)
        {
            var grid = new Grid(); foreach (var width in widths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width }); return grid;
        }
        private void BuildInterface()
        {
            gameName.FontWeight = targetWatts.FontWeight = Windows.UI.Text.FontWeights.SemiBold;
            gameName.TextWrapping = connection.TextWrapping = TextWrapping.NoWrap;
            gameName.TextTrimming = connection.TextTrimming = TextTrimming.CharacterEllipsis;
            gameName.VerticalAlignment = VerticalAlignment.Center;
            minimum.Foreground = maximum.Foreground = Brush(0x8F, 0x9C, 0xA8);
            var root = new Grid { Margin = new Thickness(16, 12, 16, 12) };
            header.Margin = new Thickness(12, 0, 12, 0); header.Height = 34;
            iconFrame.Child = gameIcon; header.Children.Add(iconFrame);
            Grid.SetColumn(gameName, 1); gameName.Margin = new Thickness(0, 0, 8, 0); header.Children.Add(gameName);
            entryHeader.Content = header; mainPanel.Children.Add(entryHeader);
            perGameSwitch.Style = (Style)Application.Current.Resources["PowerScopeSwitch"];
            perGameSwitch.HorizontalAlignment = HorizontalAlignment.Stretch;
            perGameSwitch.Margin = new Thickness(0, 4, 0, 0);
            mainPanel.Children.Add(perGameSwitch);
            var wattPanel = new StackPanel();
            var caption = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto);
            powerLabel.VerticalAlignment = VerticalAlignment.Center;
            caption.Children.Add(powerLabel); Grid.SetColumn(targetWatts, 1); caption.Children.Add(targetWatts);
            wattControls.Children.Add(caption);
            slider.Style = (Style)Application.Current.Resources["PowerSlider"];
            slider.IsFocusEngagementEnabled = false;
            slider.IsThumbToolTipEnabled = false;
            slider.SmallChange = slider.LargeChange = 1;
            slider.Margin = new Thickness(-10, 7, -10, 2);
            slider.GotFocus += (_, __) => wattCard.BorderBrush = new SolidColorBrush(Colors.White);
            slider.LostFocus += (_, __) => wattCard.BorderBrush = new SolidColorBrush(Colors.Transparent);
            wattControls.Children.Add(slider);
            var range = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto);
            range.Children.Add(minimum); Grid.SetColumn(maximum, 1); range.Children.Add(maximum); wattControls.Children.Add(range);
            wattPanel.Children.Add(wattControls); wattPanel.Children.Add(powerUnavailable);
            AutomationProperties.SetLiveSetting(powerUnavailable, AutomationLiveSetting.Polite);
            wattCard.Child = wattPanel; mainPanel.Children.Add(wattCard);
            var displayPanel = new StackPanel();
            var displayCaption = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto);
            brightnessLabel.VerticalAlignment = VerticalAlignment.Center;
            displayCaption.Children.Add(brightnessLabel); Grid.SetColumn(brightnessValue, 1); displayCaption.Children.Add(brightnessValue);
            displayPanel.Children.Add(displayCaption);
            brightness.Style = (Style)Application.Current.Resources["PowerSlider"];
            brightness.IsFocusEngagementEnabled = false; brightness.IsThumbToolTipEnabled = false;
            brightness.SmallChange = brightness.LargeChange = 1; brightness.Margin = new Thickness(-10, 7, -10, 2);
            brightness.GotFocus += (_, __) => brightnessCard.BorderBrush = new SolidColorBrush(Colors.White);
            brightness.LostFocus += (_, __) => brightnessCard.BorderBrush = new SolidColorBrush(Colors.Transparent);
            displayPanel.Children.Add(brightness);
            brightnessCard.Child = displayPanel; mainPanel.Children.Add(brightnessCard);
            nightLight.Style = (Style)Application.Current.Resources["PowerScopeSwitch"]; nightLight.HorizontalAlignment = HorizontalAlignment.Stretch;
            mainPanel.Children.Add(nightLight);
            var strengthPanel = new StackPanel();
            var strengthCaption = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto);
            strengthLabel.VerticalAlignment = VerticalAlignment.Center;
            strengthCaption.Children.Add(strengthLabel); Grid.SetColumn(strengthValue, 1); strengthCaption.Children.Add(strengthValue);
            strengthPanel.Children.Add(strengthCaption);
            strength.Style = (Style)Application.Current.Resources["PowerSlider"];
            strength.IsFocusEngagementEnabled = false; strength.IsThumbToolTipEnabled = false;
            strength.SmallChange = strength.LargeChange = 1; strength.Margin = new Thickness(-10, 7, -10, 2);
            strength.GotFocus += (_, __) => strengthCard.BorderBrush = new SolidColorBrush(Colors.White);
            strength.LostFocus += (_, __) => strengthCard.BorderBrush = new SolidColorBrush(Colors.Transparent);
            strengthPanel.Children.Add(strength);
            strengthCard.Child = strengthPanel; mainPanel.Children.Add(strengthCard);
            AutomationProperties.SetName(slider, "Wattage");
            AutomationProperties.SetName(brightness, "Brightness"); AutomationProperties.SetName(nightLight, "Night light");
            AutomationProperties.SetName(strength, "Night light strength");
            mainPanel.Children.Add(connection);
            root.Children.Add(mainPanel); Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            entryHeader.TabIndex = 0; perGameSwitch.TabIndex = 1; slider.TabIndex = 2; brightness.TabIndex = 3; nightLight.TabIndex = 4; strength.TabIndex = 5;
            ApplyDisplayMetrics();
        }
        private void ApplyDisplayMetrics()
        {
            bool compact = hostedWidget.CompactModeEnabled;
            gameName.FontSize = compact ? 20 : 18;
            targetWatts.FontSize = compact ? 22 : 20;
            powerLabel.FontSize = perGameSwitch.FontSize = compact ? 18 : 16;
            minimum.FontSize = maximum.FontSize = compact ? 14 : 12;
            connection.FontSize = compact ? 14 : 12;
            brightnessLabel.FontSize = nightLight.FontSize = powerLabel.FontSize;
            brightnessValue.FontSize = targetWatts.FontSize;
            strengthLabel.FontSize = powerLabel.FontSize; strengthValue.FontSize = targetWatts.FontSize;
            header.Height = compact ? 40 : 36;
            gameIcon.Width = gameIcon.Height = iconFrame.Width = iconFrame.Height = compact ? 36 : 32;
            perGameSwitch.MinHeight = compact ? 52 : 48;
            nightLight.MinHeight = perGameSwitch.MinHeight;
            mainPanel.Spacing = compact ? 12 : 10;
        }
        private void UpdateControls()
        {
            bool powerReady = loaded && bridgeAvailable && connected;
            slider.IsEnabled = powerReady;
            wattControls.Opacity = powerReady ? 1 : 0.45;
            powerUnavailable.Text = powerReady ? "" : !powerStateRead ? "Connecting to power control…" : PowerAvailability.Message(false, restartRequired);
            powerUnavailable.Visibility = powerReady ? Visibility.Collapsed : Visibility.Visible;
            if (!powerReady) targetWatts.Text = powerStateRead ? "Unavailable" : "— W";
            perGameSwitch.IsEnabled = loaded && bridgeAvailable && connected && gameId.Length > 0 && !writing && pending == null;
            brightness.IsEnabled = loaded && brightnessAvailable;
            nightLight.IsEnabled = loaded && nightAvailable && !nightWriting;
            strength.IsEnabled = loaded && strengthAvailable && nightLight.IsChecked == true && !nightWriting;
        }
        private void Feedback(string text)
        {
            connection.Text = text;
            connection.Visibility = String.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            feedbackUntil = DateTimeOffset.UtcNow.AddSeconds(8);
        }
        private void Adjust(int delta) { if (slider.IsEnabled) slider.Value = Math.Max(slider.Minimum, Math.Min(slider.Maximum, Math.Round(slider.Value) + delta)); }
        private async void NavigateKey(object sender, KeyRoutedEventArgs e)
        {
            var key = e.Key;
            bool left = key == VirtualKey.Left || key == VirtualKey.GamepadDPadLeft || key == VirtualKey.GamepadLeftThumbstickLeft;
            bool right = key == VirtualKey.Right || key == VirtualKey.GamepadDPadRight || key == VirtualKey.GamepadLeftThumbstickRight;
            bool up = key == VirtualKey.Up || key == VirtualKey.GamepadDPadUp || key == VirtualKey.GamepadLeftThumbstickUp;
            bool down = key == VirtualKey.Down || key == VirtualKey.GamepadDPadDown || key == VirtualKey.GamepadLeftThumbstickDown;
            bool select = key == VirtualKey.Enter || key == VirtualKey.Space || key == VirtualKey.GamepadA;
            if (!(left || right || up || down || select)) return;
            e.Handled = true;
            Control[] candidates = new Control[] { perGameSwitch, slider, brightness, nightLight, strength };
            var controls = new System.Collections.Generic.List<Control>(); foreach (var c in candidates) if (c.IsEnabled) controls.Add(c);
            if (controls.Count == 0) return;
            int index = controls.IndexOf(FocusManager.GetFocusedElement() as Control);
            if (up || down) { controls[index < 0 ? (down && slider.IsEnabled ? controls.IndexOf(slider) : 0) : (index + (down ? 1 : controls.Count - 1)) % controls.Count].Focus(FocusState.Keyboard); return; }
            if (index < 0) { controls[0].Focus(FocusState.Keyboard); index = 0; }
            var focused = controls[index];
            if (left || right) { if (focused == slider) Adjust(left ? -1 : 1); else if (focused == brightness || focused == strength) { var control = (Slider)focused; control.Value = Math.Max(0, Math.Min(100, Math.Round(control.Value) + (left ? -1 : 1))); } return; }
            if (select && !e.KeyStatus.WasKeyDown)
            {
                if (focused == perGameSwitch) { perGameSwitch.IsChecked = perGameSwitch.IsChecked != true; await SetScopeAsync(); }
                else if (focused == nightLight) { nightLight.IsChecked = nightLight.IsChecked != true; await SetNightLightAsync(); }
            }
        }
        private async Task RefreshDisplayAsync()
        {
            if (!loaded || displayPolling || brightnessEdit.Busy || strengthEdit.Busy || nightWriting) return;
            long brightnessRead = brightnessEdit.Revision, strengthRead = strengthEdit.Revision, nightRead = nightRevision;
            displayPolling = true;
            try
            {
                var state = await bridge.RequestAsync("getDisplayState");
                if (!brightnessEdit.AcceptRead(brightnessRead) || !strengthEdit.AcceptRead(strengthRead) || nightWriting || nightRead != nightRevision) return;
                brightnessAvailable = state.GetNamedBoolean("brightnessAvailable", false); nightAvailable = state.GetNamedBoolean("nightLightAvailable", false);
                updatingDisplay = true;
                if (brightnessAvailable) brightness.Value = state.GetNamedNumber("brightness");
                brightnessValue.Text = brightnessAvailable ? Math.Round(brightness.Value).ToString("0", CultureInfo.InvariantCulture) + "%" : "—%";
                nightLight.IsChecked = nightAvailable && state.GetNamedBoolean("nightLight", false);
                var strengthState = state.GetNamedValue("nightLightStrength", JsonValue.CreateNullValue());
                strengthAvailable = strengthState.ValueType == JsonValueType.Number;
                if (strengthAvailable) strength.Value = strengthState.GetNumber();
                strengthValue.Text = strengthAvailable ? Math.Round(strength.Value).ToString("0", CultureInfo.InvariantCulture) + "%" : "—%";
                bool showStrength = nightLight.IsChecked == true;
                if ((strengthCard.Visibility == Visibility.Visible) != showStrength)
                {
                    strengthCard.Visibility = showStrength ? Visibility.Visible : Visibility.Collapsed;
                    _ = ResetCardSizeAsync();
                }
            }
            catch (Exception exception) { if (brightnessEdit.AcceptRead(brightnessRead) && strengthEdit.AcceptRead(strengthRead) && nightRead == nightRevision) brightnessAvailable = nightAvailable = strengthAvailable = false; await LogAsync(exception); }
            finally { updatingDisplay = displayPolling = false; UpdateControls(); }
        }
        private static void ScheduleDisplayWrite(DispatcherTimer write, DeferredSlider edit)
        {
            write.Stop(); if (!edit.Pending.HasValue) return;
            write.Interval = edit.Delay(DateTimeOffset.UtcNow); write.Start();
        }
        private async Task FlushBrightnessAsync(bool force = false)
        {
            brightnessTimer.Stop(); int value;
            if (!brightnessEdit.Begin(DateTimeOffset.UtcNow, force, out value)) { if (!brightnessEdit.Writing) ScheduleDisplayWrite(brightnessTimer, brightnessEdit); return; }
            try
            {
                var result = await bridge.RequestAsync("setBrightness", request => request["brightness"] = JsonValue.CreateNumberValue(value));
                if (result.GetNamedNumber("brightness", -1) != value) throw new IOException("Windows did not confirm the brightness.");
            }
            catch (Exception exception) { Feedback(exception.Message); await LogAsync(exception); }
            finally { brightnessEdit.Complete(); ScheduleDisplayWrite(brightnessTimer, brightnessEdit); if (!brightnessEdit.Busy) await RefreshDisplayAsync(); }
        }
        private async Task FlushStrengthAsync(bool force = false)
        {
            strengthTimer.Stop(); int value;
            if (!strengthEdit.Begin(DateTimeOffset.UtcNow, force, out value)) { if (!strengthEdit.Writing) ScheduleDisplayWrite(strengthTimer, strengthEdit); return; }
            try
            {
                var state = await bridge.RequestAsync("setNightLightStrength", request => request["strength"] = JsonValue.CreateNumberValue(value));
                if (state.GetNamedNumber("nightLightStrength", -1) != value) throw new IOException("Windows did not confirm Night light strength.");
            }
            catch (Exception exception) { Feedback(exception.Message); await LogAsync(exception); }
            finally { strengthEdit.Complete(); ScheduleDisplayWrite(strengthTimer, strengthEdit); if (!strengthEdit.Busy) await RefreshDisplayAsync(); }
        }
        private async Task SetNightLightAsync()
        {
            if (!nightLight.IsEnabled || updatingDisplay) return;
            bool desired = nightLight.IsChecked == true; nightRevision++; nightWriting = true; UpdateControls();
            try
            {
                var state = await bridge.RequestAsync("setNightLight", request => request["enabled"] = JsonValue.CreateBooleanValue(desired));
                if (state.GetNamedBoolean("nightLight", !desired) != desired) throw new IOException("Windows did not confirm Night light.");
            }
            catch (Exception exception) { Feedback(exception.Message); await LogAsync(exception); }
            finally { nightWriting = false; await RefreshDisplayAsync(); if (nightLight.IsEnabled) nightLight.Focus(FocusState.Keyboard); }
        }
        private async Task ShowIconAsync(string data, bool hasGame)
        {
            iconFrame.Visibility = hasGame && data.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (String.Equals(iconData, data, StringComparison.Ordinal)) return;
            iconData = data; gameIcon.Source = null;
            if (data.Length == 0 || data.Length > 32768) { iconFrame.Visibility = Visibility.Collapsed; return; }
            try
            {
                using (var stream = new MemoryStream(Convert.FromBase64String(data)))
                using (var random = stream.AsRandomAccessStream()) { var image = new BitmapImage(); await image.SetSourceAsync(random); gameIcon.Source = image; }
            }
            catch { iconFrame.Visibility = Visibility.Collapsed; }
        }
        private async Task RefreshStateAsync()
        {
            if (!loaded || polling || writing || pending != null) return;
            polling = true;
            long revision = powerRevision;
            try
            {
                await WriteHostStateAsync();
                ulong gameHwnd = 0; string targetName = ""; bool targetIsGame = false;
                try
                {
                    if (targetTracker.Setting == XboxGameBarAppTargetSetting.Enabled)
                    {
                        var target = targetTracker.GetTarget();
                        if (target != null) { targetName = target.DisplayName ?? ""; targetIsGame = target.IsGame; gameHwnd = target.Hwnd; }
                    }
                }
                catch (Exception exception) { await LogAsync(exception); }
                var state = await bridge.RequestAsync("getState", request =>
                {
                    if (gameHwnd == 0) return;
                    request["gameHwnd"] = JsonValue.CreateStringValue(gameHwnd.ToString(CultureInfo.InvariantCulture));
                    request["gameName"] = JsonValue.CreateStringValue(targetName); request["isGame"] = JsonValue.CreateBooleanValue(targetIsGame);
                });
                await WriteResponseStateAsync(state);
                if (writing || pending != null || revision != powerRevision) return;
                bridgeAvailable = true; connected = state.GetNamedBoolean("connected", false);
                restartRequired = state.GetNamedBoolean("restartRequired", false); powerStateRead = true;
                var actual = state.GetNamedValue("nativeWatts", JsonValue.CreateNullValue());
                nativeValue = connected && actual.ValueType == JsonValueType.Number ? (double?)actual.GetNumber() : null;
                var playingValue = state.GetNamedValue("playingGame", JsonValue.CreateNullValue());
                var playing = playingValue.ValueType == JsonValueType.Object ? playingValue.GetObject() : null;
                gameId = playing == null ? "" : playing.GetNamedString("id", ""); exePath = playing == null ? "" : playing.GetNamedString("exePath", "");
                gameName.Text = playing == null ? "No game running" : playing.GetNamedString("name", "Current game");
                await ShowIconAsync(playing == null ? "" : playing.GetNamedString("iconPng", ""), playing != null);
                if (writing || pending != null || revision != powerRevision) return;
                usePerGame = playing != null && state.GetNamedBoolean("usePerGame", false);
                updatingSlider = true; perGameSwitch.IsChecked = usePerGame;
                double min = state.GetNamedNumber("minWatts", 4), max = state.GetNamedNumber("maxWatts", 35);
                if (!Double.IsNaN(min) && !Double.IsInfinity(min) && !Double.IsNaN(max) && !Double.IsInfinity(max) && min >= 1 && min <= max) { slider.Minimum = Math.Min(slider.Minimum, min); slider.Maximum = max; slider.Minimum = min; }
                if (nativeValue.HasValue) slider.Value = nativeValue.Value;
                targetWatts.Text = Math.Round(slider.Value).ToString("0", CultureInfo.InvariantCulture) + " W";
                minimum.Text = slider.Minimum.ToString("0", CultureInfo.InvariantCulture) + " W"; maximum.Text = slider.Maximum.ToString("0", CultureInfo.InvariantCulture) + " W";
                if (connected && DateTimeOffset.UtcNow >= feedbackUntil) Feedback("");
            }
            catch (Exception exception) { if (revision == powerRevision && pending == null) { bridgeAvailable = connected = false; nativeValue = null; powerStateRead = true; } await LogAsync(exception); }
            finally { updatingSlider = polling = false; UpdateControls(); }
        }
        private void SchedulePowerWrite()
        {
            writeTimer.Stop(); if (pending == null) return;
            var now = DateTimeOffset.UtcNow;
            writeTimer.Interval = powerDue > now ? powerDue - now : TimeSpan.FromMilliseconds(10); writeTimer.Start();
        }
        private async Task FlushChangesAsync(bool force = false)
        {
            writeTimer.Stop();
            if (writing || pending == null) return;
            if (!force && DateTimeOffset.UtcNow < powerDue) { SchedulePowerWrite(); return; }
            long revision = powerRevision;
            var change = pending; pending = null; writing = true; UpdateControls();
            try
            {
                var result = await bridge.RequestAsync("adjustWatts", request =>
                {
                    request["gameId"] = JsonValue.CreateStringValue(change.GameId); request["exePath"] = JsonValue.CreateStringValue(change.Exe);
                    request["scope"] = JsonValue.CreateStringValue(change.PerGame ? "game" : "global"); request["watts"] = JsonValue.CreateNumberValue(change.Watts);
                });
                if (Math.Abs(result.GetNamedNumber("nativeWatts", -1) - change.Watts) > 0.1) throw new IOException("ONEXConsole did not confirm the wattage.");
                nativeValue = change.Watts;
                if (pending == null) Feedback("");
            }
            catch (Exception exception) { if (revision == powerRevision) pending = null; Feedback(exception.Message); await LogAsync(exception); }
            finally { writing = false; if (pending != null) SchedulePowerWrite(); else await RefreshStateAsync(); UpdateControls(); }
        }
        private async Task SetScopeAsync()
        {
            if (writing || pending != null || !perGameSwitch.IsEnabled) return;
            bool desired = perGameSwitch.IsChecked == true;
            powerRevision++;
            writing = true; UpdateControls();
            try
            {
                var result = await bridge.RequestAsync("setScope", request =>
                {
                    request["gameId"] = JsonValue.CreateStringValue(gameId); request["exePath"] = JsonValue.CreateStringValue(exePath);
                    request["usePerGame"] = JsonValue.CreateBooleanValue(desired);
                });
                nativeValue = result.GetNamedNumber("nativeWatts"); usePerGame = result.GetNamedBoolean("usePerGame");
                Feedback("");
            }
            catch (Exception exception) { Feedback(exception.Message); await LogAsync(exception); }
            finally { writing = false; await RefreshStateAsync(); (perGameSwitch.IsEnabled ? (Control)perGameSwitch : slider).Focus(FocusState.Keyboard); }
        }
        private async Task ReportAsync(string message, Exception exception)
        {
            Feedback(message);
            await LogAsync(exception);
        }

        // Diagnostic evidence distinguishes a hidden Game Bar host from missing rendering.
        private async Task WriteHostStateAsync()
        {
            if (hostWriting) return;
            hostWriting = true;
            try
            {
                var state = new JsonObject();
                state["package"] = JsonValue.CreateStringValue(Package.Current.Id.FullName);
                state["loaded"] = JsonValue.CreateBooleanValue(loaded);
                state["typography"] = new JsonObject
                {
                    ["game"] = JsonValue.CreateNumberValue(gameName.FontSize),
                    ["body"] = JsonValue.CreateNumberValue(powerLabel.FontSize),
                    ["watts"] = JsonValue.CreateNumberValue(targetWatts.FontSize),
                    ["range"] = JsonValue.CreateNumberValue(minimum.FontSize)
                };
                if (resizeState != null) state["resize"] = resizeState;
                try
                {
                    state["visible"] = JsonValue.CreateBooleanValue(hostedWidget.Visible);
                    state["uiPolling"] = JsonValue.CreateBooleanValue(timer.IsEnabled);
                    state["helperHeartbeat"] = JsonValue.CreateBooleanValue(recoveryTimer.IsEnabled);
                    state["windowState"] = JsonValue.CreateStringValue(hostedWidget.WindowState.ToString());
                    state["displayMode"] = JsonValue.CreateStringValue(hostedWidget.GameBarDisplayMode.ToString());
                    state["pinned"] = JsonValue.CreateBooleanValue(hostedWidget.Pinned);
                    state["favorited"] = JsonValue.CreateBooleanValue(hostedWidget.Favorited);
                    state["compactMode"] = JsonValue.CreateBooleanValue(hostedWidget.CompactModeEnabled);
                    state["targetTracking"] = JsonValue.CreateStringValue(targetTracker.Setting.ToString());
                    var bounds = Window.Current.Bounds;
                    state["width"] = JsonValue.CreateNumberValue(bounds.Width);
                    state["height"] = JsonValue.CreateNumberValue(bounds.Height);
                }
                catch (Exception exception) { state["sdkError"] = JsonValue.CreateStringValue(exception.ToString()); }
                string signature = state.Stringify();
                if (String.Equals(lastHostState, signature, StringComparison.Ordinal)) return;
                state["updatedUtc"] = JsonValue.CreateStringValue(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync("widget-host-state.json", CreationCollisionOption.OpenIfExists);
                await FileIO.WriteTextAsync(file, state.Stringify());
                lastHostState = signature;
            }
            catch (Exception exception) { await LogAsync(exception); }
            finally { hostWriting = false; }
        }

        private async Task ResetCardSizeAsync()
        {
            try
            {
                var version = Package.Current.Id.Version;
                string versionText = version.Major + "." + version.Minor + "." + version.Build + "." + version.Revision;
                bool compact = hostedWidget.CompactModeEnabled;
                // Apply bounds on every host creation. A one-time resize alone
                // let Game Bar fall back to the old manifest width on restart.
                hostedWidget.MinWindowSize = new Size(464, 450);
                hostedWidget.MaxWindowSize = new Size(compact ? 900 : 600, 640);
                bool expanded = strengthCard.Visibility == Visibility.Visible;
                string resizeKey = versionText + (compact ? ":compact" : ":desktop") + (expanded ? ":strength" : ":base");
                var settings = ApplicationData.Current.LocalSettings.Values;
                object previous;
                if (settings.TryGetValue("cardResizeVersion", out previous) && String.Equals(previous as string, resizeKey, StringComparison.Ordinal)) return;
                // One bounded migration per version, preserving later user resizing.
                double width = Math.Max(464, Window.Current.Bounds.Width), height = expanded ? 550 : 450;
                resizeState = new JsonObject
                {
                    ["requestedWidth"] = JsonValue.CreateNumberValue(width),
                    ["requestedHeight"] = JsonValue.CreateNumberValue(height),
                    ["compactMode"] = JsonValue.CreateBooleanValue(compact)
                };
                var operation = hostedWidget.TryResizeWindowAsync(new Size(width, height));
                var task = operation.AsTask();
                if (await Task.WhenAny(task, Task.Delay(3000)) == task)
                {
                    bool accepted = await task;
                    resizeState["accepted"] = JsonValue.CreateBooleanValue(accepted);
                    if (accepted) settings["cardResizeVersion"] = resizeKey;
                }
                else
                {
                    operation.Cancel();
                    _ = task.ContinueWith(failed => { var ignored = failed.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    resizeState["timedOut"] = JsonValue.CreateBooleanValue(true);
                }
            }
            catch (Exception exception)
            {
                if (resizeState == null) resizeState = new JsonObject();
                resizeState["error"] = JsonValue.CreateStringValue(exception.ToString());
                await LogAsync(exception);
            }
            await WriteHostStateAsync();
        }

        private async Task WriteResponseStateAsync(JsonObject state)
        {
            try
            {
                string signature = state.Stringify();
                if (String.Equals(lastResponseState, signature, StringComparison.Ordinal)) return;
                var evidence = JsonObject.Parse(signature);
                evidence["widgetPackage"] = JsonValue.CreateStringValue(Package.Current.Id.FullName);
                evidence["pipeName"] = JsonValue.CreateStringValue(BridgeClient.PipeName);
                evidence["receivedUtc"] = JsonValue.CreateStringValue(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync("widget-response-state.json", CreationCollisionOption.OpenIfExists);
                await FileIO.WriteTextAsync(file, evidence.Stringify());
                lastResponseState = signature;
            }
            catch (Exception exception) { await LogAsync(exception); }
        }

        private static async Task LogAsync(Exception exception)
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync("widget-errors.log", CreationCollisionOption.OpenIfExists);
                if ((await file.GetBasicPropertiesAsync()).Size > 262144) await FileIO.WriteTextAsync(file, "");
                await FileIO.AppendTextAsync(file, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + exception + Environment.NewLine);
            }
            catch { }
        }
    }
}
