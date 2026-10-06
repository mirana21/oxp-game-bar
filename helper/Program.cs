using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;

namespace OXP3.PowerWidget.Helper;

internal sealed record SavedGame(string Id, string Name, string Store, string ExePath, string ProcessName, int Watts, bool UsePerGame = true);
internal sealed record Preferences(bool Enabled, List<SavedGame> Games, int? GlobalWatts = null, bool ScopeMode = false);
internal sealed record Candidate(string Path, string ProcessName, int Rank, string Reason);
internal sealed record Game(string Id, string Name, string Store, string InstallPath, List<Candidate> Candidates);
internal sealed record PlayingGame(string Id, string Name, string Store, string ExePath, int? SavedWatts);
internal sealed record NativeState(int Tdp, int MinTdp, int MaxTdp);
internal interface INativePower { Task<NativeState> Read(CancellationToken token); Task<NativeState> Apply(int watts, CancellationToken token); }

internal sealed class NativePower : INativePower
{
    private readonly string legacyTokenPath;
    private readonly string pipeName;
    public NativePower(string directory, string pipeName = "OXP3.OneXConsole.Power.v1") { legacyTokenPath = Path.Combine(directory, "bridge-token.txt"); this.pipeName = pipeName; }
    public Task<NativeState> Read(CancellationToken token) => Request("getState", null, token);
    public Task<NativeState> Apply(int watts, CancellationToken token) => Request("setWatts", watts, token);
    private async Task<NativeState> Request(string command, int? watts, CancellationToken cancellation, string? legacyToken = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        var request = new Dictionary<string, object?> { ["command"] = command, ["watts"] = watts };
        if (legacyToken != null) request["token"] = legacyToken;
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n");
        await pipe.WriteAsync(payload, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        var line = await reader.ReadLineAsync(timeout.Token);
        if (line == null || line.Length > 65536) throw new IOException("Invalid response from ONEXConsole.");
        using var reply = JsonDocument.Parse(line);
        if (!reply.RootElement.GetProperty("ok").GetBoolean())
        {
            var error = reply.RootElement.GetProperty("error").GetString();
            // A still-running pre-0.1.0.20 bridge can continue during upgrade.
            // Fresh installations never create, require, or send a key.
            if (legacyToken == null && error == "Bridge authentication failed." && File.Exists(legacyTokenPath))
                return await Request(command, watts, cancellation, File.ReadAllText(legacyTokenPath).Trim());
            throw new InvalidOperationException(error);
        }
        var result = reply.RootElement.GetProperty("result");
        if (result.TryGetProperty("nativeConnected", out var ready) && !ready.GetBoolean()) throw new InvalidOperationException("ONEXConsole power controls are not ready.");
        var state = new NativeState(result.GetProperty("tdp").GetInt32(), result.GetProperty("minTdp").GetInt32(), result.GetProperty("maxTdp").GetInt32());
        if (state.Tdp < 1 || state.MinTdp < 1 || state.MaxTdp < state.MinTdp) throw new IOException("Invalid power limits from ONEXConsole.");
        return state;
    }
}

internal sealed class Controller
{
    private readonly DisplayControl display = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly INativePower native;
    private readonly PendingPowerActivation powerActivation;
    private readonly string file;
    private readonly SemaphoreSlim gate = new(1);
    private readonly List<Game> catalog = new();
    private Preferences preferences;
    private NativeState? state;
    private bool connected, scanning;
    private string? discoveryModule, scanError;
    private string status = "Connecting to ONEXConsole…";
    private SavedGame? active;
    private string? sessionKey;
    private int? restoreWatts;
    private bool applyFailed;
    private string? lastPlayingId;
    private string iconPath = "", iconPng = "";
    private string? scopeSession;
    private int? scopeLastApplied;

    // One local executable icon, cached for the current game. No network or
    // library search is needed, and icon failures never block power controls.
    private string GameIcon(string? path)
    {
        path ??= "";
        if (String.Equals(iconPath, path, StringComparison.OrdinalIgnoreCase)) return iconPng;
        iconPath = path; iconPng = "";
        if (path.Length == 0 || !File.Exists(path)) return iconPng;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return iconPng;
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            if (stream.Length <= 24576) iconPng = Convert.ToBase64String(stream.ToArray());
        }
        catch (Exception) { /* Keep the game name and controls usable. */ }
        return iconPng;
    }

    public Controller(INativePower native, string directory, PendingPowerActivation? powerActivation = null)
    {
        this.native = native;
        this.powerActivation = powerActivation ?? new PendingPowerActivation(directory);
        Directory.CreateDirectory(directory);
        file = Path.Combine(directory, "remembered-watts.json");
        preferences = File.Exists(file)
            ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(file), JsonOptions) ?? throw new IOException("Saved wattages are unreadable.")
            : new Preferences(false, new List<SavedGame>());
        ValidatePreferences(preferences);
    }

    private static void ValidatePreferences(Preferences p)
    {
        if (p.Games == null || p.Games.Any(g => string.IsNullOrWhiteSpace(g.Id) || string.IsNullOrWhiteSpace(g.ProcessName) || !Path.IsPathRooted(g.ExePath) || g.Watts < 1))
            throw new IOException("Saved wattages contain invalid values. The file has not been changed.");
        if (p.Games.Select(g => g.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != p.Games.Count)
            throw new IOException("Saved wattages contain duplicate game IDs.");
        if (p.GlobalWatts.HasValue && p.GlobalWatts < 1) throw new IOException("Global wattage is outside the supported range.");
    }

    public async Task Scan(string modulePath, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { scanning = true; discoveryModule = modulePath; scanError = null; } finally { gate.Release(); }
        try
        {
            // A fixed script; paths are passed as environment data, never interpolated into shell code.
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            // Process-scoped policy for our packaged, read-only scanner. No
            // machine/user execution policy is changed, and errors must fail
            // the scan instead of being mistaken for an empty game library.
            start.ArgumentList.RemoveAt(start.ArgumentList.Count - 1);
            start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("Bypass"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("$ErrorActionPreference='Stop'; [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding; Import-Module -Name $env:OXP3_DISCOVERY_MODULE -Force; ConvertTo-Json -InputObject @(Get-InstalledGameCatalog) -Depth 8 -Compress");
            start.Environment["OXP3_DISCOVERY_MODULE"] = modulePath;
            start.Environment["PSModulePath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "Modules");
            using var process = Process.Start(start) ?? throw new IOException("Could not scan installed games.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(timeout.Token); } catch { if (!process.HasExited) process.Kill(true); throw; }
            var output = await outputTask; var errors = await errorTask;
            if (process.ExitCode != 0) throw new IOException("Game scan failed: " + errors.Trim());
            var games = JsonSerializer.Deserialize<List<Game>>(output, JsonOptions) ?? new List<Game>();
            await gate.WaitAsync(token);
            try { var manual = catalog.Where(g => g.Store == "Manual" || g.Store == "Detected").ToList(); catalog.Clear(); catalog.AddRange(games); catalog.AddRange(manual); } finally { gate.Release(); }
        }
        catch (Exception e)
        {
            await gate.WaitAsync(CancellationToken.None); try { scanError = e.Message; } finally { gate.Release(); }
            throw;
        }
        finally { await gate.WaitAsync(CancellationToken.None); try { scanning = false; } finally { gate.Release(); } }
    }

    public async Task<object> Command(JsonElement request, CancellationToken token)
    {
        var displayCommand = request.GetProperty("command").GetString();
        if (displayCommand == "ping") return new { alive = true };
        if (displayCommand is "getDisplayState" or "setBrightness" or "setNightLight" or "setNightLightStrength") return await display.Command(request, token);
        await gate.WaitAsync(token);
        try
        {
            string command = request.GetProperty("command").GetString() ?? "";
            switch (command)
            {
                case "getState":
                {
                    await Refresh(token);
                    PlayingGame? playing = null;
                    if (request.TryGetProperty("gameHwnd", out var hwndValue))
                    {
                        ulong hwnd = 0;
                        if (hwndValue.ValueKind == JsonValueKind.String) ulong.TryParse(hwndValue.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out hwnd);
                        else if (hwndValue.ValueKind == JsonValueKind.Number) hwndValue.TryGetUInt64(out hwnd);
                        var isGame = request.TryGetProperty("isGame", out var gameFlag) && gameFlag.ValueKind == JsonValueKind.True;
                        var title = request.TryGetProperty("gameName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
                        playing = Program.ResolveGameTarget(hwnd, title, isGame, catalog, preferences.Games);
                    }
                    playing ??= Program.FindPlaying(catalog, preferences.Games, active?.Id ?? lastPlayingId);
                    lastPlayingId = playing?.Id;
                    var usePerGame = playing != null && preferences.Enabled && preferences.Games.Any(g => g.Id == playing.Id && SamePath(g.ExePath, playing.ExePath) && g.UsePerGame);
                    var restartRequired = !connected && powerActivation.NeedsRestart();
                    return new { connected, restartRequired, powerMessage = Oxp3.Controls.PowerAvailability.Message(connected, restartRequired), nativeWatts = state?.Tdp, minWatts = state?.MinTdp ?? 4, maxWatts = state?.MaxTdp ?? 35, enabled = preferences.Enabled, globalWatts = preferences.GlobalWatts ?? restoreWatts ?? state?.Tdp, usePerGame, activeGameId = active?.Id ?? "", activeGameName = active?.Name ?? "", playingGame = playing == null ? null : new { id = playing.Id, name = playing.Name, store = playing.Store, exePath = playing.ExePath, savedWatts = playing.SavedWatts, iconPng = GameIcon(playing.ExePath) }, scanning, status, version = "0.1.0.26", helperPackage = Native.PackageFamilyName ?? "" };
                }
                case "setScope":
                case "adjustWatts":
                    return await ChangeScopedPower(request, command == "setScope", token);
                case "listGames":
                    return new { games = BuildCatalog(), scanning, error = scanError ?? "" };
                case "rescanGames":
                    if (!scanning && discoveryModule != null)
                    {
                        scanning = true;
                        _ = Task.Run(async () => { try { await Scan(discoveryModule, token); } catch { /* Scan records the error for the UI. */ } });
                    }
                    return new { scanning };
                case "saveWatts":
                {
                    var id = Required(request, "gameId"); var exe = Path.GetFullPath(Required(request, "exePath"));
                    if (!File.Exists(exe) || !string.Equals(Path.GetExtension(exe), ".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose an existing game executable.");
                    var watts = Watts(request);
                    await Refresh(token);
                    if (connected && state != null && (watts < state.MinTdp || watts > state.MaxTdp)) throw new ArgumentException("Wattage is outside ONEXConsole's current limits.");
                    var game = catalog.FirstOrDefault(g => g.Id == id);
                    var old = preferences.Games.FirstOrDefault(g => g.Id == id);
                    if (game == null && old == null) throw new ArgumentException("Game is not in the library. Add its executable first.");
                    if (game != null && !game.Candidates.Any(c => string.Equals(c.Path, exe, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Select an executable belonging to this game, or add it manually.");
                    var item = new SavedGame(id, game?.Name ?? old!.Name, game?.Store ?? old!.Store, exe, Path.GetFileNameWithoutExtension(exe), watts);
                    var next = preferences.Games.Where(g => g.Id != id).ToList(); next.Add(item);
                    Save(preferences with { Games = next });
                    status = "Remembered " + watts + " W for " + item.Name + ".";
                    // The watcher applies it only if this game's executable is running and automation is enabled.
                    return new { remembered = true, watts, applied = false };
                }
                case "applyWatts":
                {
                    var watts = Watts(request); await Apply(watts, token);
                    return new { applied = true, nativeWatts = state!.Tdp };
                }
                case "forgetWatts":
                {
                    var id = Required(request, "gameId");
                    Save(preferences with { Games = preferences.Games.Where(g => g.Id != id).ToList() });
                    catalog.RemoveAll(g => g.Id == id && g.Store == "Manual");
                    status = "Forgot the remembered wattage.";
                    return new { forgotten = true };
                }
                case "setEnabled":
                {
                    var enabled = request.GetProperty("enabled").GetBoolean();
                    if (enabled == preferences.Enabled) return new { enabled };
                    if (enabled) { await Refresh(token); if (!connected) throw new InvalidOperationException("Connect the ONEXConsole bridge before enabling automatic changes."); }
                    // Stop future automatic writes even if restoration or saving preferences fails.
                    var next = preferences with { Enabled = enabled };
                    if (!enabled) preferences = next;
                    Save(next);
                    string? warning = null;
                    if (!enabled && restoreWatts.HasValue)
                    {
                        try
                        {
                            await Refresh(token);
                            if (connected && state != null && active != null && state.Tdp == active.Watts)
                                await Apply(Math.Clamp(restoreWatts.Value, state.MinTdp, state.MaxTdp), token);
                        }
                        catch (Exception e) { warning = "Automatic changes paused; previous wattage could not be restored: " + e.Message; }
                    }
                    active = null; sessionKey = null; restoreWatts = null; applyFailed = false;
                    status = warning ?? (enabled ? "Automatic wattage switching enabled." : "Automatic wattage switching paused.");
                    return new { enabled, warning = warning ?? "" };
                }
                case "addExe":
                {
                    var exe = Path.GetFullPath(Required(request, "exePath"));
                    if (!File.Exists(exe) || !string.Equals(Path.GetExtension(exe), ".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose an existing game executable.");
                    var name = request.TryGetProperty("name", out var n) ? n.GetString() : null;
                    name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(exe) : name.Trim();
                    var id = "manual:" + exe.ToLowerInvariant();
                    if (!catalog.Any(g => g.Id == id)) catalog.Add(new Game(id, name, "Manual", Path.GetDirectoryName(exe)!, new List<Candidate> { new(exe, Path.GetFileNameWithoutExtension(exe), 100, "Selected executable") }));
                    return new { gameId = id };
                }
                default: throw new ArgumentException("Unknown command.");
            }
        }
        finally { gate.Release(); }
    }

    private static bool SamePath(string a, string b) => String.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // Scope is validated in the helper, including after a queued slider change.
    // A game exiting or a switch changing must never turn an old game write
    // into a global write. Disabled game values remain stored for later reuse.
    private async Task<object> ChangeScopedPower(JsonElement request, bool changeScope, CancellationToken token)
    {
        string expectedId = request.TryGetProperty("gameId", out var idValue) ? idValue.GetString() ?? "" : "";
        var playing = Program.FindPlaying(catalog, preferences.Games, lastPlayingId);
        if (!String.Equals(expectedId, playing?.Id ?? "", StringComparison.Ordinal)) throw new InvalidOperationException("The current game changed. Try the control again.");
        if (changeScope && playing == null) throw new InvalidOperationException("Open a game to use a per-game wattage.");
        if (playing != null && (!request.TryGetProperty("exePath", out var pathValue) || !SamePath(pathValue.GetString() ?? "", playing.ExePath))) throw new InvalidOperationException("The current game's executable changed.");
        await Refresh(token);
        if (!connected || state == null) throw new InvalidOperationException(status);
        int baseline = preferences.GlobalWatts ?? restoreWatts ?? state.Tdp;
        var stored = playing == null ? null : preferences.Games.FirstOrDefault(g => g.Id == playing.Id && SamePath(g.ExePath, playing.ExePath));
        bool currentScope = preferences.Enabled && stored?.UsePerGame == true;
        bool gameScope = changeScope ? request.GetProperty("usePerGame").GetBoolean() : request.GetProperty("scope").GetString() == "game";
        if (!changeScope && gameScope != currentScope) throw new InvalidOperationException("The wattage scope changed. Try the slider again.");
        if (gameScope && playing == null) throw new InvalidOperationException("Open a game to use a per-game wattage.");
        // Preserve external changes to the global setting before entering a game scope.
        if (!currentScope && (!scopeLastApplied.HasValue || state.Tdp != scopeLastApplied.Value || scopeSession?.StartsWith("global:", StringComparison.Ordinal) == true)) baseline = state.Tdp;
        int watts = changeScope ? (gameScope ? stored?.Watts ?? state.Tdp : baseline) : Watts(request);
        if (watts < state.MinTdp || watts > state.MaxTdp) throw new ArgumentException("Wattage is outside ONEXConsole's current limits.");
        if (state.Tdp != watts) await Apply(watts, token);
        // Migrate old paused mappings without re-enabling them accidentally.
        var games = preferences.Games.Select(g => !preferences.ScopeMode ? g with { UsePerGame = preferences.Enabled && g.UsePerGame } : g).ToList();
        if (playing != null && (gameScope || changeScope && stored != null))
        {
            var item = stored ?? new SavedGame(playing.Id, playing.Name, playing.Store, playing.ExePath, Path.GetFileNameWithoutExtension(playing.ExePath), watts);
            item = item with { UsePerGame = gameScope, Watts = gameScope ? watts : item.Watts };
            games.RemoveAll(g => g.Id == item.Id); games.Add(item);
            active = gameScope ? item : null;
        }
        else active = null;
        Save(new Preferences(true, games, gameScope || changeScope ? baseline : watts, true));
        var matched = playing == null ? (Game: (SavedGame?)null, Pid: 0) : Program.FindRunning(Program.PlayingCandidates(catalog, preferences.Games), playing.Id);
        scopeSession = ScopeKey(playing?.Id ?? "", matched.Pid, gameScope, gameScope ? watts : 0);
        scopeLastApplied = watts;
        sessionKey = null; restoreWatts = null; applyFailed = false;
        status = gameScope ? "Wattage remembered for this game." : "Global wattage updated.";
        return new { applied = true, nativeWatts = state!.Tdp, globalWatts = preferences.GlobalWatts, usePerGame = gameScope };
    }

    private static string ScopeKey(string id, int pid, bool gameScope, int watts) => (gameScope ? "game:" : "global:") + id + ":" + pid + (gameScope ? ":" + watts : "");

    private async Task TickScopes(Func<List<SavedGame>, string?, (SavedGame? Game, int Pid)> running, CancellationToken token)
    {
        var match = running(Program.PlayingCandidates(catalog, preferences.Games), lastPlayingId ?? active?.Id);
        var saved = match.Game == null ? null : preferences.Games.FirstOrDefault(g => g.Id == match.Game.Id && SamePath(g.ExePath, match.Game.ExePath));
        bool gameScope = preferences.Enabled && saved?.UsePerGame == true;
        var key = ScopeKey(match.Game?.Id ?? "", match.Pid, gameScope, gameScope ? saved!.Watts : 0);
        await Refresh(token);
        if (!connected || state == null) return;
        if (key == scopeSession)
        {
            if (!gameScope && preferences.GlobalWatts != state.Tdp)
            {
                Save(preferences with { GlobalWatts = state.Tdp }); // Native manual global changes are authoritative.
                scopeLastApplied = state.Tdp;
            }
            return; // No repeated writes, and manual native changes during a game are preserved.
        }
        int global = preferences.GlobalWatts ?? state.Tdp;
        if (scopeLastApplied.HasValue && state.Tdp != scopeLastApplied.Value)
        {
            global = state.Tdp;
            Save(preferences with { GlobalWatts = global });
        }
        int desired = Math.Clamp(gameScope ? saved!.Watts : global, state.MinTdp, state.MaxTdp);
        if (state.Tdp != desired) await Apply(desired, token);
        active = gameScope ? saved : null; scopeSession = key; scopeLastApplied = desired;
    }

    private object[] BuildCatalog()
    {
        var games = catalog.ToList();
        foreach (var saved in preferences.Games)
            if (!games.Any(g => g.Id == saved.Id)) games.Add(new Game(saved.Id, saved.Name, saved.Store, Path.GetDirectoryName(saved.ExePath)!, new List<Candidate> { new(saved.ExePath, saved.ProcessName, 100, "Remembered executable") }));
        return games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Select(g => {
            var saved = preferences.Games.FirstOrDefault(p => p.Id == g.Id);
            return (object)new { id = g.Id, name = g.Name, store = g.Store, installPath = g.InstallPath, candidates = g.Candidates.Select(c => new { path = c.Path, processName = c.ProcessName, rank = c.Rank, reason = c.Reason }).ToArray(), savedWatts = (int?)saved?.Watts, usePerGame = preferences.Enabled && saved?.UsePerGame == true, selectedExePath = saved?.ExePath ?? "" };
        }).ToArray();
    }

    private void Save(Preferences next)
    {
        ValidatePreferences(next);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(next, JsonOptions), new UTF8Encoding(false));
        if (File.Exists(file)) File.Replace(temp, file, file + ".previous", true); else File.Move(temp, file);
        preferences = next;
    }
    private static string Required(JsonElement request, string name) => request.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()) ? p.GetString()! : throw new ArgumentException("Missing " + name + ".");
    private static int Watts(JsonElement request) => request.TryGetProperty("watts", out var p) && p.TryGetInt32(out var w) && w >= 1 ? w : throw new ArgumentException("Wattage must be a positive whole number within ONEXConsole's current limits.");
    private async Task Refresh(CancellationToken token)
    {
        try { state = await native.Read(token); connected = true; powerActivation.Connected(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            connected = false; state = null;
            status = e is OperationCanceledException
                ? (IsNativeRunning()
                    ? "ONEXConsole is running, but its connection is not active."
                    : "ONEXConsole is not running.")
                : e.Message;
        }
    }
    private static bool IsNativeRunning()
    {
        var processes = Process.GetProcessesByName("OneXConsole");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private async Task Apply(int watts, CancellationToken token)
    {
        await Refresh(token);
        if (!connected || state == null) throw new InvalidOperationException(status);
        if (watts < state.MinTdp || watts > state.MaxTdp) throw new ArgumentException("Wattage is outside ONEXConsole's current limits.");
        try
        {
            var result = await native.Apply(watts, token);
            if (result.Tdp != watts) throw new IOException("ONEXConsole did not confirm the requested wattage.");
            state = result; connected = true; status = "ONEXConsole set to " + watts + " W.";
        }
        catch { connected = false; state = null; status = "Wattage change was not confirmed by ONEXConsole."; throw; }
    }

    public async Task Tick(Func<List<SavedGame>, string?, (SavedGame? Game, int Pid)> running, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (preferences.ScopeMode) { await TickScopes(running, token); return; }
            if (!preferences.Enabled) return;
            var match = running(preferences.Games, active?.Id);
            if (match.Game == null)
            {
                if (sessionKey != null && restoreWatts.HasValue)
                {
                    await Refresh(token);
                    // Preserve a manual change made in ONEXConsole while a game was running.
                    if (connected && state != null && active != null && state.Tdp == active.Watts) await Apply(Math.Clamp(restoreWatts.Value, state.MinTdp, state.MaxTdp), token);
                }
                active = null; sessionKey = null; restoreWatts = null; applyFailed = false; return;
            }
            var key = match.Game.Id + ":" + match.Pid + ":" + match.Game.Watts;
            if (key == sessionKey && !applyFailed) return;
            await Refresh(token);
            if (!connected || state == null) { applyFailed = true; return; }
            if (active != null && state.Tdp != active.Watts) restoreWatts = state.Tdp;
            restoreWatts ??= state.Tdp;
            if (state.Tdp != match.Game.Watts) await Apply(match.Game.Watts, token);
            active = match.Game; sessionKey = key; applyFailed = false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) { applyFailed = true; status = e.Message; }
        finally { gate.Release(); }
    }
}

internal static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Contains("--self-test")) { await SelfTest.Run(); return; }
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OXP3PowerWidget");
        Directory.CreateDirectory(directory);
        using var mutex = new Mutex(true, "Local\\OXP3.PowerWidget.Helper", out var created);
        if (!created) return;
        var controller = new Controller(new NativePower(Native.SharedDataDirectory), directory);
        using var cancellation = new CancellationTokenSource();
        var root = AppContext.BaseDirectory;
        var module = Path.Combine(root, "GameDiscovery.psm1");
        var scanner = Task.Run(async () => { try { await controller.Scan(module, cancellation.Token); } catch (Exception e) { Log(directory, e.Message); } });
        var watcher = Task.Run(async () => { while (!cancellation.IsCancellationRequested) { await controller.Tick(FindRunning, cancellation.Token); await Task.Delay(2000, cancellation.Token); } });
        try { await Serve(controller, directory, cancellation.Token); }
        catch (Exception e) { Log(directory, "Helper startup/server failure: " + e); }
        finally { cancellation.Cancel(); }
    }

    internal static (SavedGame? Game, int Pid) FindRunning(List<SavedGame> games, string? activeId)
    {
        if (games.Count == 0) return (null, 0);
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var foreground);
        var matches = new List<(SavedGame Game, int Pid, DateTime Started)>();
        var byName = games.GroupBy(game => game.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        // GetProcessesByName enumerates the entire process list internally.
        // Take one snapshot, then query executable paths only for known names.
        foreach (var p in Process.GetProcesses())
            using (p) try {
                if (!byName.TryGetValue(p.ProcessName, out var candidates)) continue;
                var path = Native.GetProcessPath(p.Id);
                foreach (var game in candidates)
                    if (string.Equals(path, game.ExePath, StringComparison.OrdinalIgnoreCase)) matches.Add((game, p.Id, p.StartTime));
            } catch (System.ComponentModel.Win32Exception) { } catch (InvalidOperationException) { }
        var match = matches.OrderByDescending(m => m.Pid == foreground).ThenByDescending(m => m.Game.Id == activeId).ThenByDescending(m => m.Started).FirstOrDefault();
        return (match.Game, match.Pid);
    }

    internal static List<SavedGame> PlayingCandidates(List<Game> catalog, List<SavedGame> saved)
    {
        var candidates = new List<SavedGame>();
        foreach (var game in catalog)
            foreach (var exe in game.Candidates.Where(c => c.Rank > 0 && !c.Reason.Contains("Launcher or utility", StringComparison.OrdinalIgnoreCase) && !c.Reason.Contains("Support executable", StringComparison.OrdinalIgnoreCase)))
                candidates.Add(new SavedGame(game.Id, game.Name, game.Store, exe.Path, exe.ProcessName, 4));
        foreach (var game in saved)
            if (!candidates.Any(c => c.Id == game.Id && string.Equals(c.ExePath, game.ExePath, StringComparison.OrdinalIgnoreCase))) candidates.Add(game);
        return candidates;
    }

    internal static PlayingGame? FindPlaying(List<Game> catalog, List<SavedGame> saved, string? activeId)
    {
        var match = FindRunning(PlayingCandidates(catalog, saved), activeId);
        if (match.Game == null) return null;
        var remembered = saved.FirstOrDefault(g => g.Id == match.Game.Id && string.Equals(g.ExePath, match.Game.ExePath, StringComparison.OrdinalIgnoreCase));
        var game = catalog.FirstOrDefault(g => g.Candidates.Any(c => string.Equals(c.Path, match.Game.ExePath, StringComparison.OrdinalIgnoreCase)));
        return new PlayingGame(match.Game.Id, game?.Name ?? match.Game.Name, match.Game.Store, match.Game.ExePath, remembered?.Watts);
    }

    internal static PlayingGame? ResolveGameTarget(ulong hwnd, string? title, bool isGame, List<Game> catalog, List<SavedGame> saved)
    {
        var window = new IntPtr(unchecked((long)hwnd));
        if (hwnd == 0 || !Native.IsWindow(window)) return null;
        Native.GetWindowThreadProcessId(window, out var pid);
        if (pid == 0) return null;
        var exe = Native.GetProcessPath(pid);
        if (exe == null || !Path.IsPathRooted(exe) || !string.Equals(Path.GetExtension(exe), ".exe", StringComparison.OrdinalIgnoreCase)) return null;
        var game = catalog.FirstOrDefault(g => g.Candidates.Any(c => string.Equals(c.Path, exe, StringComparison.OrdinalIgnoreCase)));
        var remembered = saved.FirstOrDefault(g => string.Equals(g.ExePath, exe, StringComparison.OrdinalIgnoreCase));
        if (remembered != null) return new PlayingGame(remembered.Id, game?.Name ?? remembered.Name, remembered.Store, exe, remembered.Watts);
        if (game != null) return new PlayingGame(game.Id, game.Name, game.Store, exe, null);
        if (!isGame) return null;
        // Game Bar supplies the real target HWND. A title alone never selects a
        // process, and an unknown ordinary app never becomes a game mapping.
        var id = "detected:" + exe.ToLowerInvariant();
        var label = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(exe) : title.Trim();
        if (label.Length > 160) label = label[..160];
        game = new Game(id, label, "Detected", Path.GetDirectoryName(exe)!, new List<Candidate> { new(exe, Path.GetFileNameWithoutExtension(exe), 100, "Game Bar current game") });
        catalog.Add(game);
        return new PlayingGame(id, label, game.Store, exe, null);
    }

    internal static async Task Serve(Controller controller, string directory, CancellationToken token, string? pipeName = null, TimeSpan? connectionTimeout = null)
    {
        while (!token.IsCancellationRequested)
        {
            using var pipe = Native.CreatePipe(pipeName: pipeName);
            await pipe.WaitForConnectionAsync(token);
            // A client can disappear while a reply is buffered. StreamWriter's
            // Dispose may flush again and throw; that must not stop the server.
            try { await ServeConnection(pipe, controller, directory, token, connectionTimeout ?? TimeSpan.FromSeconds(20)); }
            catch (IOException e) { Log(directory, "Client disconnected: " + e.Message); }
            catch (InvalidOperationException e) { Log(directory, "Client connection failure: " + e.Message); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
    }
    private static async Task ServeConnection(NamedPipeServerStream pipe, Controller controller, string directory, CancellationToken token, TimeSpan connectionTimeout)
    {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(connectionTimeout);
            try
            {
                var line = await ReadBounded(reader, timeout.Token);
                using var request = JsonDocument.Parse(line);
                object result;
                if (request.RootElement.GetProperty("command").GetString() == "pickExe")
                {
                    timeout.CancelAfter(TimeSpan.FromMinutes(10));
                    var chosen = await PickExecutable().WaitAsync(timeout.Token);
                    if (chosen == null) result = new { cancelled = true };
                    else
                    {
                        var added = JsonSerializer.SerializeToElement(await controller.Command(JsonSerializer.SerializeToElement(new { command = "addExe", exePath = chosen }), timeout.Token));
                        result = new { cancelled = false, gameId = added.GetProperty("gameId").GetString() };
                    }
                }
                else result = await controller.Command(request.RootElement, timeout.Token);
                await WriteReply(pipe, new { ok = true, result }, timeout.Token);
            }
            catch (Exception e)
            {
                Log(directory, e.ToString());
                // A deadline cancels the actual pipe IO. Do not start another
                // write on an expired connection, or dispose a buffered writer
                // while its previous non-cancellable write is still running.
                if (timeout.IsCancellationRequested) return;
                try { await WriteReply(pipe, new { ok = false, error = e.Message }, timeout.Token); } catch (IOException) { } catch (OperationCanceledException) { }
            }
    }
    private static async Task WriteReply(NamedPipeServerStream pipe, object reply, CancellationToken token)
    {
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply) + "\n"), token);
        await pipe.FlushAsync(token);
    }
    private static Task<string?> PickExecutable()
    {
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new System.Windows.Forms.OpenFileDialog
                {
                    Title = "Choose the game's executable",
                    Filter = "Game executable (*.exe)|*.exe",
                    CheckFileExists = true,
                    Multiselect = false,
                    RestoreDirectory = true
                };
                result.SetResult(dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.FileName : null);
            }
            catch (Exception e) { result.SetException(e); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return result.Task;
    }
    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken token)
    {
        var sb = new StringBuilder(); var buffer = new char[1];
        while (sb.Length < 32768) { var n = await reader.ReadAsync(buffer.AsMemory(0, 1), token); if (n == 0) throw new IOException("Incomplete request."); if (buffer[0] == '\n') return sb.ToString(); sb.Append(buffer[0]); }
        throw new IOException("Request too large.");
    }
    private static void Log(string directory, string message)
    {
        var path = Path.Combine(directory, "helper.log");
        if (File.Exists(path) && new FileInfo(path).Length > 262144) File.Move(path, path + ".previous", true);
        File.AppendAllText(path, DateTimeOffset.UtcNow.ToString("O") + " " + message + Environment.NewLine);
    }
}

internal static class Native
{
    internal const string PipeName = "OXP3.PowerWidget.Bridge.v1";
    [DllImport("shell32.dll", ExactSpelling = true)] private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);
    internal static string SharedDataDirectory
    {
        get
        {
            var localAppData = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
            // The unpackaged ONEXConsole companion owns this token. .NET's
            // default LocalApplicationData points inside this package's cache.
            int result = SHGetKnownFolderPath(ref localAppData, 0x00010000, IntPtr.Zero, out var path);
            Marshal.ThrowExceptionForHR(result);
            try { return Path.Combine(Marshal.PtrToStringUni(path) ?? throw new IOException("The shared app-data path is unavailable."), "OXP3PowerWidget"); }
            finally { Marshal.FreeCoTaskMem(path); }
        }
    }
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out int id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
    internal static string? GetProcessPath(int id)
    {
        using var process = OpenProcess(0x1000, false, id); // Query-only access; no module-memory inspection.
        if (process.IsInvalid) return null;
        var path = new StringBuilder(32768); int size = path.Capacity;
        return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int GetCurrentPackageFamilyName(ref int length, StringBuilder name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int GetCurrentPackageFullName(ref int length, StringBuilder name);
    internal static string? PackageFamilyName
    {
        get { int length = 256; var name = new StringBuilder(length); return GetCurrentPackageFamilyName(ref length, name) == 0 ? name.ToString() : null; }
    }
    internal static string? PackageFullName
    {
        get { int length = 1024; var name = new StringBuilder(length); return GetCurrentPackageFullName(ref length, name) == 0 ? name.ToString() : null; }
    }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", ExactSpelling = true, SetLastError = true)] private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr text);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr ptr);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr ptr);
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipe(string name, uint mode, uint pipeMode, uint maxInstances, uint outSize, uint inSize, uint timeout, ref SecurityAttributes security);
    internal static string AppContainerSid(string family)
    {
        // Windows can report S_OK with a null SID when a packaged caller derives
        // its own PFN. Read the OS's installed identity for this exact package.
        if (string.Equals(PackageFamilyName, family, StringComparison.OrdinalIgnoreCase) && PackageFullName is string fullName)
            return InstalledPackageSid(fullName);
        int result = DeriveAppContainerSidFromAppContainerName(family, out var sid);
        if (result != 0 || sid == IntPtr.Zero) throw new InvalidOperationException("Could not derive the widget's package identity: " + result);
        try
        {
            if (!ConvertSidToStringSid(sid, out var text)) throw new System.ComponentModel.Win32Exception();
            try { return Marshal.PtrToStringUni(text) ?? throw new IOException("The package identity was empty."); }
            finally { LocalFree(text); }
        }
        finally { FreeSid(sid); }
    }
    internal static string InstalledPackageSid(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName) || fullName.IndexOfAny(new[] { '\\', '/' }) >= 0)
            throw new ArgumentException("Invalid installed package identity.");
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        // ApplicationsEx is the Windows 11 key; older documentation calls it ApplicationEx.
        foreach (var branch in new[] { "ApplicationsEx", "ApplicationEx" })
        {
            using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\SecurityManager\CapAuthz\" + branch + "\\" + fullName, false);
            if (key?.GetValue("PackageSid") is not string value) continue;
            var sid = new SecurityIdentifier(value).Value;
            if (!sid.StartsWith("S-1-15-2-", StringComparison.Ordinal)) throw new IOException("Windows returned an invalid package SID.");
            return sid;
        }
        throw new IOException("Windows has no registered SID for the widget package.");
    }
    internal static NamedPipeServerStream CreatePipe(string? packageFamily = null, string? pipeName = null)
    {
        string user = WindowsIdentity.GetCurrent().User!.Value;
        string rules = "D:P(A;;GA;;;SY)(A;;GA;;;" + user + ")";
        var family = packageFamily ?? PackageFamilyName;
        if (family != null) rules += "(A;;GRGW;;;" + AppContainerSid(family) + ")";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(rules, 1, out var descriptor, out _)) throw new System.ComponentModel.Win32Exception();
        try
        {
            var attrs = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe("\\\\.\\pipe\\" + (pipeName ?? PipeName), 0x40000003, 0, 1, 65536, 65536, 0, ref attrs);
            if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception();
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally { LocalFree(descriptor); }
    }
}

internal sealed class FakeNative : INativePower
{
    internal int Watts = 15, Applications, Reads, MaxTdp = 35;
    internal bool FailNext, Unavailable;
    public Task<NativeState> Read(CancellationToken token) { Reads++; return Unavailable ? throw new IOException("Simulated unavailable native connection.") : Task.FromResult(new NativeState(Watts, 4, MaxTdp)); }
    public Task<NativeState> Apply(int watts, CancellationToken token) { if (FailNext) { FailNext = false; throw new IOException("Simulated unavailable native connection."); } Watts = watts; Applications++; return Read(token); }
}
internal static class SelfTest
{
    private static JsonElement Request(object value) => JsonSerializer.SerializeToElement(value);
    private static async Task UnicodeDiscoveryTest(string root)
    {
        Directory.CreateDirectory(root);
        const string title = "The Witcher 3: Wild Hunt \u2014 Remastered / Pok\u00E9mon\u2122 / \u6E38\u620F \U0001F3AE";
        var exe = Path.Combine(root, "game-\u00FC\u6E38.exe");
        File.WriteAllBytes(exe, Array.Empty<byte>());
        var module = Path.Combine(root, "UnicodeFixture.psm1");
        string Quote(string text) => "'" + text.Replace("'", "''") + "'";
        var script = "function Get-InstalledGameCatalog { [pscustomobject]@{ Id='unicode:test'; Name=" + Quote(title) + "; Store='Test'; InstallPath=" + Quote(root) + "; Candidates=@([pscustomobject]@{Path=" + Quote(exe) + ";ProcessName='game';Rank=100;Reason='Game binary'}) } }; Export-ModuleMember -Function Get-InstalledGameCatalog";
        // PowerShell 5.1 needs the BOM for Unicode in script source. Launcher
        // metadata tests separately cover the actual BOM-less UTF-8 files.
        File.WriteAllText(module, script, new UTF8Encoding(true));
        var native = new FakeNative();
        var controller = new Controller(native, root);
        await controller.Scan(module, CancellationToken.None);
        var catalog = Request(await controller.Command(Request(new { command = "listGames" }), CancellationToken.None));
        var game = catalog.GetProperty("games")[0];
        Assert(game.GetProperty("name").GetString() == title && game.GetProperty("candidates")[0].GetProperty("path").GetString() == exe, "PowerShell scan output must preserve Unicode titles and executable paths.");
        Assert(native.Applications == 0, "Discovery tests must not change wattage or launch a game.");
        Console.WriteLine("PASS: Unicode PowerShell output survives helper scan and JSON serialization without changing power.");
    }
    private static async Task KeylessNativeTest(string root)
    {
        var name = "OXP3.Keyless.Test." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var response = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(timeout.Token);
            using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            using var request = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            Assert(request.RootElement.GetProperty("command").GetString() == "getState", "Native read command must be retained.");
            Assert(!request.RootElement.TryGetProperty("token", out _), "The new bridge must work without sending a shared key.");
            await writer.WriteLineAsync("{\"ok\":true,\"result\":{\"nativeConnected\":true,\"tdp\":12,\"minTdp\":4,\"maxTdp\":35}}");
        });
        var missingDirectory = Path.Combine(root, "no-key-directory");
        var state = await new NativePower(missingDirectory, name).Read(timeout.Token);
        await response;
        Assert(state.Tdp == 12 && !Directory.Exists(missingDirectory), "A native connection must not require or create a key directory.");
    }
    internal static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "OXP3PowerWidget-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var exe = Path.Combine(root, "game.exe"); File.WriteAllBytes(exe, Array.Empty<byte>());
        var native = new FakeNative(); var controller = new Controller(native, root); var token = CancellationToken.None;
        await KeylessNativeTest(root);
        await NativeTimeoutTest(root);
        await PowerAvailabilityTests(Path.Combine(root, "activation"));
        await UnicodeDiscoveryTest(Path.Combine(root, "unicode"));
        var pingNative = new FakeNative { Unavailable = true };
        var pingController = new Controller(pingNative, Path.Combine(root, "ping"));
        var pong = Request(await pingController.Command(Request(new { command = "ping" }), token));
        Assert(pong.GetProperty("alive").GetBoolean() && pingNative.Reads == 0 && pingNative.Applications == 0, "Helper heartbeat must not read or write native power.");
        NightLightCodec.SelfTest();
        var slider = new Oxp3.Controls.DeferredSlider(); var now = DateTimeOffset.UtcNow;
        long oldRead = slider.Revision;
        slider.Queue(59, now); Assert(!slider.AcceptRead(oldRead), "Old brightness poll must not replace a local edit.");
        Assert(!slider.Begin(now.AddMilliseconds(999), false, out _), "Brightness must wait for a full idle second.");
        slider.Queue(60, now.AddMilliseconds(900));
        Assert(!slider.Begin(now.AddMilliseconds(1100), false, out _), "Every change must reset the debounce deadline.");
        Assert(slider.Begin(now.AddMilliseconds(1900), false, out var target) && target == 60, "Only the final value should be applied.");
        slider.Queue(61, now.AddMilliseconds(1950)); slider.Complete();
        Assert(slider.Pending == 61 && !slider.AcceptRead(oldRead), "Completion of an older write must retain the latest edit.");
        Assert(slider.Begin(now.AddMilliseconds(2950), false, out target) && target == 61, "A subsequent edit must get its own idle deadline.");
        slider.Complete(); Assert(!slider.AcceptRead(oldRead) && slider.AcceptRead(slider.Revision), "A delayed pre-edit poll must still be rejected after the write completes.");
        Console.WriteLine("PASS: one-second slider debounce, repeated-input deadline reset, late refresh rejection, newer edits surviving an older write, Night light strength bounds and schedule preservation.");
        var testPipeName = Native.PipeName + ".Test." + Guid.NewGuid().ToString("N");
        Assert(Native.AppContainerSid("OXP3.PowerWidget_pv4gfha69c7qe").StartsWith("S-1-15-2-", StringComparison.Ordinal), "The widget package SID must be converted safely.");
        using (var secured = Native.CreatePipe("OXP3.PowerWidget_pv4gfha69c7qe", testPipeName)) Assert(secured.IsAsync, "The packaged pipe ACL must be created without a startup crash.");
        using (var current = Process.GetCurrentProcess())
        {
            var realPath = current.MainModule!.FileName;
            Assert(string.Equals(Native.GetProcessPath(current.Id), realPath, StringComparison.OrdinalIgnoreCase), "Query-only process identification must return the executable path.");
            var exact = new SavedGame("exact", "Current process", "Test", realPath, current.ProcessName, 20);
            var wrong = exact with { Id = "wrong", ExePath = Path.Combine(root, Path.GetFileName(realPath)) };
            File.WriteAllBytes(wrong.ExePath, Array.Empty<byte>());
            Assert(Program.FindRunning(new List<SavedGame> { wrong }, null).Game == null, "A matching process name from a different path must not activate a wattage.");
            Assert(Program.FindRunning(new List<SavedGame> { exact }, null).Game?.Id == exact.Id, "An exact running executable must be recognized.");
            var libraryGame = new Game("detected", "Detected game", "Test", Path.GetDirectoryName(realPath)!, new List<Candidate> { new(realPath, current.ProcessName, 100, "Game binary") });
            Assert(Program.FindPlaying(new List<Game> { libraryGame }, new List<SavedGame>(), null)?.Id == "detected", "An unsaved running game must be detected from its installed executable.");
            var unicodeGame = libraryGame with { Name = "The Witcher 3: Wild Hunt \u2014 Remastered" };
            var oldMapping = exact with { Id = "manual:old", Name = "Corrupt saved title", Watts = 17 };
            var displayed = Program.FindPlaying(new List<Game> { unicodeGame }, new List<SavedGame> { oldMapping }, oldMapping.Id);
            Assert(displayed?.Name == unicodeGame.Name && displayed.Id == oldMapping.Id && displayed.SavedWatts == 17, "Fresh Unicode metadata must replace a stale display name without changing the saved game identity or wattage.");
        }
        var added = JsonSerializer.SerializeToElement(await controller.Command(Request(new { command = "addExe", exePath = exe, name = "Test game" }), token));
        var id = added.GetProperty("gameId").GetString()!;
        await controller.Command(Request(new { command = "saveWatts", gameId = id, exePath = exe, watts = 20 }), token);
        Assert(native.Applications == 0, "Saving an offline game must not change power.");
        await controller.Command(Request(new { command = "setEnabled", enabled = true }), token);
        var game = new SavedGame(id, "Test game", "Manual", exe, "game", 20);
        await controller.Tick((_, _) => (game, 101), token); Assert(native.Watts == 20, "Launch must apply remembered watts.");
        await controller.Command(Request(new { command = "setEnabled", enabled = true }), token);
        await controller.Tick((_, _) => (game, 101), token); Assert(native.Applications == 1, "Watcher must not repeatedly write power.");
        await controller.Tick((_, _) => (null, 0), token); Assert(native.Watts == 15, "Exit must restore earlier watts.");
        await controller.Tick((_, _) => (game, 102), token); native.Watts = 17;
        await controller.Tick((_, _) => (null, 0), token); Assert(native.Watts == 17, "Manual ONEXConsole change must be preserved.");
        await controller.Tick((_, _) => (game, 103), token); native.Watts = 19;
        await controller.Command(Request(new { command = "setEnabled", enabled = false }), token);
        Assert(native.Watts == 19, "Pausing must preserve a manual ONEXConsole change.");
        await controller.Command(Request(new { command = "setEnabled", enabled = true }), token);
        await controller.Tick((_, _) => (game, 104), token); native.Watts = 23;
        var other = game with { Id = "other", Name = "Other game", Watts = 10 };
        await controller.Tick((_, _) => (other, 105), token);
        await controller.Tick((_, _) => (null, 0), token);
        Assert(native.Watts == 23, "Switching games must retain the latest manual native target as the restore value.");
        bool rejected = false; try { await controller.Command(Request(new { command = "applyWatts", watts = 99 }), token); } catch (ArgumentException) { rejected = true; }
        Assert(rejected, "Out-of-range power must be rejected.");
        var reload = new Controller(native, root); var games = JsonSerializer.SerializeToElement(await reload.Command(Request(new { command = "listGames" }), token));
        Assert(games.GetProperty("games")[0].GetProperty("savedWatts").GetInt32() == 20, "Saved watts must survive restart.");
        await reload.Command(Request(new { command = "forgetWatts", gameId = id }), token);
        var forgotten = JsonSerializer.SerializeToElement(await reload.Command(Request(new { command = "listGames" }), token));
        Assert(forgotten.GetProperty("games").GetArrayLength() == 0, "Forgetting a remembered executable must remove the mapping.");
        await ScopeTests(Path.Combine(root, "scopes"), token);
        await LimitTests(Path.Combine(root, "limits"), token);
        await StalledClientTests(Path.Combine(root, "stalled-clients"), exe);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Program.Serve(reload, root, deadline.Token, testPipeName);
        using (var aborted = new NamedPipeClientStream(".", testPipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await aborted.ConnectAsync(deadline.Token);
            // An incomplete request forces the error reply after this client
            // disconnects. The next real request must still succeed.
            await aborted.WriteAsync(Encoding.UTF8.GetBytes("{"), deadline.Token);
        }
        using (var client = new NamedPipeClientStream(".", testPipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(deadline.Token);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, false, 4096, true);
            await writer.WriteLineAsync("{\"command\":\"getState\"}");
            var line = await reader.ReadLineAsync().WaitAsync(deadline.Token);
            using var reply = JsonDocument.Parse(line!);
            Assert(reply.RootElement.GetProperty("ok").GetBoolean() && reply.RootElement.GetProperty("result").GetProperty("nativeWatts").GetInt32() == 23, "Secure Windows pipe must carry native-state replies.");
        }
        deadline.Cancel();
        try { await server; } catch (OperationCanceledException) { }
        Console.WriteLine("PASS: global/game scope, retained disabled game settings, automatic restore, startup persistence, stale scope rejection, failed write handling, legacy save/restore, secure pipe and disconnected client recovery.");
        // Delete only the exact task-owned test folder, after resolving its path under the system temp directory.
        var resolved = Path.GetFullPath(root); var allowed = Path.GetFullPath(Path.GetTempPath());
        if (resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("OXP3PowerWidget-test-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
    }
    private static async Task PowerAvailabilityTests(string root)
    {
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "pending-power-startup.json");
        var started = DateTime.UtcNow;
        DateTime? current = started;
        var activation = new PendingPowerActivation(root, pid => pid == 42 ? current : null);
        File.WriteAllText(marker, JsonSerializer.Serialize(new { nativeProcessId = 42, nativeStartedUtc = started }));
        Assert(activation.NeedsRestart(), "A deferred first installation must retain its restart reason.");
        var native = new FakeNative { Unavailable = true };
        var controller = new Controller(native, root, activation);
        var state = JsonSerializer.SerializeToElement(await controller.Command(Request(new { command = "getState" }), CancellationToken.None));
        Assert(!state.GetProperty("connected").GetBoolean() && state.GetProperty("restartRequired").GetBoolean(), "A pending activation must never report power available.");
        Assert(state.GetProperty("powerMessage").GetString() == "Restart Windows to enable power control.", "Unavailable power must explain the required restart.");
        bool rejected = false;
        try { await controller.Command(Request(new { command = "adjustWatts", scope = "global", gameId = "", exePath = "", watts = 16 }), CancellationToken.None); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected && native.Applications == 0 && !File.Exists(Path.Combine(root, "remembered-watts.json")), "Unavailable power must reject writes without changing preferences.");
        current = started.AddMinutes(1);
        Assert(!activation.NeedsRestart(), "A replaced native instance must not inherit an old restart instruction.");
        current = null;
        Assert(!activation.NeedsRestart(), "An exited native instance must not inherit an old restart instruction.");
        current = started; native.Unavailable = false;
        state = JsonSerializer.SerializeToElement(await controller.Command(Request(new { command = "getState" }), CancellationToken.None));
        Assert(state.GetProperty("connected").GetBoolean() && !state.GetProperty("restartRequired").GetBoolean() && state.GetProperty("powerMessage").GetString() == "" && !File.Exists(marker), "A working native connection must clear the pending notice.");
        native.Unavailable = true;
        state = JsonSerializer.SerializeToElement(await controller.Command(Request(new { command = "getState" }), CancellationToken.None));
        Assert(!state.GetProperty("restartRequired").GetBoolean() && state.GetProperty("powerMessage").GetString()!.Contains("unavailable"), "A later connection failure must show ordinary unavailability rather than demand another restart.");
        File.WriteAllText(marker, "{broken");
        Assert(!activation.NeedsRestart(), "A damaged optional notice must not invent a restart requirement.");
        Console.WriteLine("PASS: deferred activation reason, unavailable writes rejected, stale notices expired and connected recovery clears restart state.");
    }

    private static async Task NativeTimeoutTest(string root)
    {
        var name = "OXP3.NativeTimeout.Test." + Guid.NewGuid().ToString("N");
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var response = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(testTimeout.Token);
            using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, true);
            await reader.ReadLineAsync(testTimeout.Token);
            // A native peer that sends part of a response, then stalls.
            await server.WriteAsync(Encoding.UTF8.GetBytes("{\"ok\":"), testTimeout.Token);
            await server.FlushAsync(testTimeout.Token);
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        bool cancelled = false;
        try { await new NativePower(root, name).Read(deadline.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { cancelled = true; }
        await response;
        Assert(cancelled, "A stalled native response must cancel the underlying IO within the request deadline.");
        server.Disconnect();
        Console.WriteLine("PASS: stalled native response cancels without pending buffered IO.");
    }

    private static async Task ScopeTests(string root, CancellationToken token)
    {
        var native = new FakeNative(); var controller = new Controller(native, root);
        async Task<JsonElement> Command(object request) => JsonSerializer.SerializeToElement(await controller.Command(Request(request), token));
        await Command(new { command = "adjustWatts", gameId = "", exePath = "", scope = "global", watts = 17 });
        Assert(native.Watts == 17, "A slider without a game must apply global wattage directly.");
        bool rejected = false;
        try { await Command(new { command = "setScope", gameId = "", exePath = "", usePerGame = true }); } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "Per-game scope must be unavailable when no game is running.");
        using var current = Process.GetCurrentProcess(); var exe = current.MainModule!.FileName;
        var added = await Command(new { command = "addExe", exePath = exe, name = "Scope test game" }); var id = added.GetProperty("gameId").GetString()!;
        async Task Toggle(bool on) => await Command(new { command = "setScope", gameId = id, exePath = exe, usePerGame = on });
        await Toggle(true);
        await Command(new { command = "adjustWatts", gameId = id, exePath = exe, scope = "game", watts = 20 });
        var state = await Command(new { command = "getState" });
        Assert(state.GetProperty("globalWatts").GetInt32() == 17 && native.Watts == 20, "Game slider changes must not overwrite the global wattage.");
        var game = new SavedGame(id, "Scope test game", "Manual", exe, current.ProcessName, 20);
        int writes = native.Applications;
        await controller.Tick((_, _) => (game, current.Id), token); await controller.Tick((_, _) => (game, current.Id), token);
        Assert(native.Applications == writes, "A confirmed slider change must not cause repeated automatic writes.");
        await Toggle(false);
        state = await Command(new { command = "getState" });
        Assert(native.Watts == 17 && !state.GetProperty("usePerGame").GetBoolean() && state.GetProperty("playingGame").GetProperty("savedWatts").GetInt32() == 20, "Turning off game scope must restore global watts while retaining the game value.");
        await Command(new { command = "adjustWatts", gameId = id, exePath = exe, scope = "global", watts = 18 });
        await Toggle(true); Assert(native.Watts == 20, "Re-enabling a game must reuse its remembered value, not overwrite it with global watts.");
        rejected = false;
        try { await Command(new { command = "adjustWatts", gameId = id, exePath = exe, scope = "global", watts = 22 }); } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected && native.Watts == 20, "A delayed write from the previous scope must be rejected.");
        rejected = false;
        try { await Command(new { command = "adjustWatts", gameId = "a game that exited", exePath = exe, scope = "game", watts = 22 }); } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected && native.Watts == 20, "A delayed write for a different or exited game must be rejected.");
        await controller.Tick((_, _) => (null, 0), token); Assert(native.Watts == 18, "Game exit must restore the persistent global wattage.");
        var reload = new Controller(native, root); native.Watts = 20;
        await reload.Tick((_, _) => (null, 0), token); Assert(native.Watts == 18, "Startup without a game must restore global watts even if the native database retained game watts.");
        await reload.Tick((_, _) => (game, current.Id), token); Assert(native.Watts == 20, "A remembered enabled game must switch automatically after helper restart.");
        native.Watts = 23;
        await reload.Tick((_, _) => (null, 0), token); Assert(native.Watts == 23, "A manual native change must be preserved on exit.");
        var persisted = new Controller(native, root);
        var result = JsonSerializer.SerializeToElement(await persisted.Command(Request(new { command = "getState" }), token));
        Assert(result.GetProperty("globalWatts").GetInt32() == 23 && result.GetProperty("usePerGame").GetBoolean(), "Scope choice and global watts must survive reload.");
        await persisted.Command(Request(new { command = "setScope", gameId = id, exePath = exe, usePerGame = false }), token);
        native.FailNext = true; rejected = false;
        try { await persisted.Command(Request(new { command = "adjustWatts", gameId = id, exePath = exe, scope = "global", watts = 24 }), token); } catch (IOException) { rejected = true; }
        result = JsonSerializer.SerializeToElement(await persisted.Command(Request(new { command = "getState" }), token));
        Assert(rejected && native.Watts == 23 && result.GetProperty("globalWatts").GetInt32() == 23 && !result.GetProperty("usePerGame").GetBoolean(), "An unconfirmed native write must not be persisted as a new global setting.");
    }
    private static async Task StalledClientTests(string root, string exe)
    {
        Directory.CreateDirectory(root);
        var preferences = new Preferences(false, new List<SavedGame> {
            new("large-reply", new string('x', 1024 * 1024), "Test", exe, "game", 15)
        });
        File.WriteAllText(Path.Combine(root, "remembered-watts.json"), JsonSerializer.Serialize(preferences));
        var native = new FakeNative(); var controller = new Controller(native, root);
        var name = Native.PipeName + ".Stalled.Test." + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serving = Program.Serve(controller, root, lifetime.Token, name, TimeSpan.FromMilliseconds(250));
        async Task CheckAlive()
        {
            using var next = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await next.ConnectAsync(lifetime.Token);
            await next.WriteAsync(Encoding.UTF8.GetBytes("{\"command\":\"getState\"}\n"), lifetime.Token);
            using var reader = new StreamReader(next, Encoding.UTF8, false, 4096, true);
            var reply = await reader.ReadLineAsync(lifetime.Token);
            Assert(JsonDocument.Parse(reply!).RootElement.GetProperty("ok").GetBoolean(), "The helper must serve the next request after a client deadline.");
        }
        using (var unfinished = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await unfinished.ConnectAsync(lifetime.Token);
            await unfinished.WriteAsync(Encoding.UTF8.GetBytes("{"), lifetime.Token);
            await Task.Delay(500, lifetime.Token); // Client remains connected but never finishes its request.
            await CheckAlive();
        }
        using (var unread = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await unread.ConnectAsync(lifetime.Token);
            await unread.WriteAsync(Encoding.UTF8.GetBytes("{\"command\":\"listGames\"}\n"), lifetime.Token);
            await Task.Delay(500, lifetime.Token); // Client never drains a reply larger than the pipe buffer.
            await CheckAlive();
        }
        Assert(!serving.IsFaulted && native.Applications == 0, "Stalled clients must not terminate the helper or change wattage.");
        lifetime.Cancel();
        try { await serving; } catch (OperationCanceledException) { }
        Console.WriteLine("PASS: stalled reads and blocked replies cancel actual pipe IO; subsequent requests succeed without helper termination or power changes.");
    }
    private static async Task LimitTests(string root, CancellationToken token)
    {
        var native = new FakeNative(); var controller = new Controller(native, root);
        async Task<JsonElement> Command(object request) => JsonSerializer.SerializeToElement(await controller.Command(Request(request), token));
        await Command(new { command = "adjustWatts", gameId = "", exePath = "", scope = "global", watts = 17 });
        int writes = native.Applications;
        native.MaxTdp = 40;
        var state = await Command(new { command = "getState" });
        await controller.Tick((_, _) => (null, 0), token);
        Assert(state.GetProperty("maxWatts").GetInt32() == 40 && native.Watts == 17 && native.Applications == writes, "Raising the native maximum must update limits without changing watts.");
        await Command(new { command = "adjustWatts", gameId = "", exePath = "", scope = "global", watts = 40 });
        var reload = new Controller(native, root);
        await reload.Tick((_, _) => (null, 0), token);
        state = JsonSerializer.SerializeToElement(await reload.Command(Request(new { command = "getState" }), token));
        Assert(native.Watts == 40 && state.GetProperty("globalWatts").GetInt32() == 40, "A 40 W global target must survive helper restart.");
        using var current = Process.GetCurrentProcess(); string exe = current.MainModule!.FileName;
        var added = await Command(new { command = "addExe", exePath = exe, name = "Limit test game" }); string id = added.GetProperty("gameId").GetString()!;
        await Command(new { command = "saveWatts", gameId = id, exePath = exe, watts = 40 });
        var savedReload = new Controller(native, root);
        var games = JsonSerializer.SerializeToElement(await savedReload.Command(Request(new { command = "listGames" }), token));
        Assert(games.GetProperty("games")[0].GetProperty("savedWatts").GetInt32() == 40, "A saved 40 W game target must survive helper restart.");
        native.Watts = 17; native.MaxTdp = 35; writes = native.Applications;
        state = await Command(new { command = "getState" });
        bool rejected = false;
        try { await Command(new { command = "applyWatts", watts = 40 }); } catch (ArgumentException) { rejected = true; }
        Assert(state.GetProperty("maxWatts").GetInt32() == 35 && rejected && native.Watts == 17 && native.Applications == writes, "A reduced native limit must reject a stale high request without changing watts.");
        Console.WriteLine("PASS: dynamic 35/40 W limits, unchanged watts on range refresh, persisted 40 W global/game targets, stale requests rejected against native limits.");
    }
    private static void Assert(bool valid, string message) { if (!valid) throw new Exception(message); }
}
