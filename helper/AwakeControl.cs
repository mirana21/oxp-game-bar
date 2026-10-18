using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace OXP3.PowerWidget.Helper;

internal sealed record AwakeState([property: JsonPropertyName("keepAwake")] bool KeepAwake);

// A session-only lease owned by the helper, not the visible Game Bar page.
// No timer, synthetic input, saved preference or Windows power-plan changes.
internal sealed class AwakeControl : IDisposable
{
    private readonly object gate = new();
    private readonly Func<IDisposable> createRequest;
    private readonly bool observePowerEvents;
    private IDisposable? request;
    private bool disposed;
    public AwakeControl() : this(() => WindowsAwakeRequest.Create(), true) { }
    internal AwakeControl(Func<IDisposable> createRequest, bool observePowerEvents = false)
    { this.createRequest = createRequest; this.observePowerEvents = observePowerEvents; }
    internal AwakeState State { get { lock (gate) return new(request != null); } }
    internal object Command(JsonElement command)
    {
        var name = command.GetProperty("command").GetString();
        if (name == "getAwakeState") return State;
        if (name != "setKeepAwake") throw new ArgumentException("Unknown keep-awake command.");
        return Set(command.GetProperty("enabled").GetBoolean());
    }
    private AwakeState Set(bool enabled)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (enabled == (request != null)) return new(enabled);
            if (enabled)
            {
                if (observePowerEvents) SystemEvents.PowerModeChanged += PowerChanged;
                try { request = createRequest() ?? throw new IOException("Windows did not create a keep-awake request."); }
                catch { if (observePowerEvents) SystemEvents.PowerModeChanged -= PowerChanged; throw; }
            }
            else Release();
            return new(request != null);
        }
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs args)
    {
        // Windows terminates power requests on explicit sleep. Reset our state
        // as well so waking never presents an expired request as enabled.
        if (args.Mode == PowerModes.Suspend) StopForSuspend();
    }
    internal void StopForSuspend() { lock (gate) Release(); }
    private void Release()
    {
        var previous = request; request = null;
        if (previous == null) return;
        if (observePowerEvents) SystemEvents.PowerModeChanged -= PowerChanged;
        previous.Dispose();
    }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; Release(); } }
}

internal enum AwakeRequestType { Display = 0, System = 1 }
internal interface IAwakePowerApi
{
    SafeFileHandle Create();
    bool Set(SafeFileHandle handle, AwakeRequestType type);
    bool Clear(SafeFileHandle handle, AwakeRequestType type);
}

internal sealed class WindowsAwakeRequest : IDisposable
{
    private readonly SafeFileHandle handle;
    private readonly IAwakePowerApi api;
    private bool system, display, disposed;
    private WindowsAwakeRequest(SafeFileHandle handle, IAwakePowerApi api) { this.handle = handle; this.api = api; }
    internal static WindowsAwakeRequest Create(IAwakePowerApi? api = null)
    {
        api ??= new WindowsAwakeApi();
        var handle = api.Create();
        var request = new WindowsAwakeRequest(handle, api);
        try
        {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a Windows keep-awake request.");
            if (!api.Set(handle, AwakeRequestType.System)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not keep the system awake.");
            request.system = true;
            if (!api.Set(handle, AwakeRequestType.Display)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not keep the display awake.");
            request.display = true;
            return request;
        }
        catch { request.Dispose(); throw; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (display) api.Clear(handle, AwakeRequestType.Display);
            if (system) api.Clear(handle, AwakeRequestType.System);
        }
        finally { handle.Dispose(); } // Closing also releases any uncleared request.
    }
}

internal sealed class WindowsAwakeApi : IAwakePowerApi
{
    // REASON_CONTEXT is 32 bytes on the supported x64 platform: two DWORDs
    // followed by a 24-byte union. Only its simple-string member is used.
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext { public uint Version, Flags; public IntPtr SimpleReason, Reserved1, Reserved2; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(SafeFileHandle handle, AwakeRequestType type);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(SafeFileHandle handle, AwakeRequestType type);
    public SafeFileHandle Create()
    {
        var reason = Marshal.StringToHGlobalUni("Keep awake is enabled in OXP3 Game Power.");
        try { var context = new ReasonContext { Version = 0, Flags = 1, SimpleReason = reason }; return PowerCreateRequest(ref context); }
        finally { Marshal.FreeHGlobal(reason); }
    }
    public bool Set(SafeFileHandle handle, AwakeRequestType type) => PowerSetRequest(handle, type);
    public bool Clear(SafeFileHandle handle, AwakeRequestType type) => PowerClearRequest(handle, type);
}

internal static class AwakeControlTests
{
    private sealed class FakeApi : IAwakePowerApi
    {
        internal readonly SafeFileHandle Handle;
        internal readonly List<AwakeRequestType> Sets = new(), Clears = new();
        internal AwakeRequestType? FailSet;
        internal bool FailClear;
        internal FakeApi(bool invalid = false) { Handle = new SafeFileHandle(invalid ? new IntPtr(-1) : new IntPtr(42), false); }
        public SafeFileHandle Create() => Handle;
        public bool Set(SafeFileHandle handle, AwakeRequestType type) { Sets.Add(type); return FailSet != type; }
        public bool Clear(SafeFileHandle handle, AwakeRequestType type) { Clears.Add(type); return !FailClear; }
    }
    private static JsonElement Request(string command, bool enabled = false) => JsonSerializer.SerializeToElement(new { command, enabled });
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void Run()
    {
        var apis = new List<FakeApi>();
        using (var awake = new AwakeControl(() => { var api = new FakeApi(); apis.Add(api); return WindowsAwakeRequest.Create(api); }))
        {
            Assert(!awake.State.KeepAwake && apis.Count == 0, "Startup/read must not acquire a power request.");
            awake.Command(Request("setKeepAwake", true)); awake.Command(Request("setKeepAwake", true));
            Assert(awake.State.KeepAwake && apis.Count == 1 && apis[0].Sets.SequenceEqual(new[] { AwakeRequestType.System, AwakeRequestType.Display }), "Enable must hold both request types exactly once.");
            try { awake.Command(JsonSerializer.SerializeToElement(new { command = "setKeepAwake", enabled = "true" })); throw new Exception("Malformed command accepted."); } catch (InvalidOperationException) { }
            Assert(awake.State.KeepAwake && apis.Count == 1, "Malformed input must not mutate state.");
            awake.Command(Request("setKeepAwake", false)); awake.Command(Request("setKeepAwake", false));
            Assert(!awake.State.KeepAwake && apis[0].Handle.IsClosed && apis[0].Clears.Count == 2, "Off must release both requests without duplicate clears.");
            awake.Command(Request("setKeepAwake", true)); awake.StopForSuspend();
            Assert(!awake.State.KeepAwake && apis[1].Handle.IsClosed, "Explicit suspend must release/reset the session.");
            awake.Command(Request("setKeepAwake", true));
        }
        Assert(apis[2].Handle.IsClosed, "Helper disposal must release keep-awake.");
        Assert(JsonSerializer.SerializeToElement(new AwakeState(true)).GetProperty("keepAwake").GetBoolean(), "Pipe state must use the same JSON property name as the widget.");
        foreach (var failed in new[] { AwakeRequestType.System, AwakeRequestType.Display })
        {
            var api = new FakeApi { FailSet = failed };
            using var awake = new AwakeControl(() => WindowsAwakeRequest.Create(api));
            bool refused = false; try { awake.Command(Request("setKeepAwake", true)); } catch (Win32Exception) { refused = true; }
            Assert(refused && !awake.State.KeepAwake && api.Handle.IsClosed, "Partial acquisition failure must close the handle and stay off.");
            Assert(api.Clears.Count == (failed == AwakeRequestType.Display ? 1 : 0), "Only acquired requests may be cleared.");
        }
        var invalid = new FakeApi(true);
        try { WindowsAwakeRequest.Create(invalid); throw new Exception("Invalid handle accepted."); } catch (Win32Exception) { }
        Assert(invalid.Handle.IsClosed && invalid.Sets.Count == 0, "Invalid creation must not set requests.");
        var clearing = new FakeApi { FailClear = true };
        WindowsAwakeRequest.Create(clearing).Dispose();
        Assert(clearing.Handle.IsClosed && clearing.Clears.Count == 2, "Closing the handle must still release requests if explicit clears fail.");
        Console.WriteLine("PASS: session-only keep-awake defaults off, acquires system/display once, releases on off/suspend/disposal, validates input and cleans up partial failures. No Windows power request used.");
    }
    internal static async Task RoutingTest(string directory)
    {
        var api = new FakeApi();
        var native = new FakeNative { Unavailable = true };
        using var awake = new AwakeControl(() => WindowsAwakeRequest.Create(api));
        using var controller = new Controller(native, directory, awake: awake);
        var on = JsonSerializer.SerializeToElement(await controller.Command(Request("setKeepAwake", true), CancellationToken.None));
        var state = JsonSerializer.SerializeToElement(await controller.Command(Request("getAwakeState"), CancellationToken.None));
        Assert(on.GetProperty("keepAwake").GetBoolean() && state.GetProperty("keepAwake").GetBoolean(), "Controller must expose confirmed awake state with native power disconnected.");
        await controller.Command(Request("setKeepAwake", false), CancellationToken.None);
        Assert(native.Reads == 0 && native.Applications == 0 && api.Handle.IsClosed && Directory.GetFiles(directory).Length == 0, "Awake commands must not call ONEXConsole or persist settings.");
        Console.WriteLine("PASS: helper keep-awake commands work without a native power connection and do not read/write wattage or preferences.");
    }
}
