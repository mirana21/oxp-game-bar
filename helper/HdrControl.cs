using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OXP3.PowerWidget.Helper;

internal sealed record HdrState(bool Available, bool? Enabled);
internal readonly record struct DisplayTarget(uint AdapterLow, int AdapterHigh, uint Id, bool Internal, bool Primary);
internal sealed record HdrCapabilities(bool Supported, bool Enabled, bool Limited, bool Modern);
internal interface IHdrApi
{
    IReadOnlyList<DisplayTarget> Targets();
    HdrCapabilities Read(DisplayTarget target);
    void Write(DisplayTarget target, bool enabled, bool modern);
}

internal sealed class HdrControl
{
    private readonly IHdrApi api;
    internal HdrControl() : this(new WindowsHdrApi()) { }
    internal HdrControl(IHdrApi api) { this.api = api; }
    private DisplayTarget Target()
    {
        var all = api.Targets().Distinct().ToArray();
        var builtIn = all.Where(t => t.Internal).ToArray();
        if (builtIn.Length == 1) return builtIn[0];
        var candidates = (builtIn.Length > 0 ? builtIn : all).Where(t => t.Primary).ToArray();
        if (candidates.Length == 1) return candidates[0];
        if (all.Length == 1) return all[0];
        throw new IOException("HDR is unavailable for this display.");
    }
    internal HdrState Read()
    {
        try { var value = api.Read(Target()); return new(value.Supported && !value.Limited, value.Supported ? value.Enabled : null); }
        catch { return new(false, null); }
    }
    internal void Set(bool enabled)
    {
        var target = Target();
        var current = api.Read(target);
        if (!current.Supported || current.Limited) throw new IOException("HDR is unavailable for this display.");
        if (current.Enabled == enabled) return;
        api.Write(target, enabled, current.Modern);
        // A mode transition can temporarily make display queries unavailable.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try { if (api.Read(target).Enabled == enabled) return; } catch { }
            Thread.Sleep(100);
        }
        throw new IOException("Windows did not confirm HDR. Check Windows display settings.");
    }
}

internal sealed class WindowsHdrApi : IHdrApi
{
    [StructLayout(LayoutKind.Sequential)] internal struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] internal struct Header { public uint Type, Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential)] internal struct Source { public Luid Adapter; public uint Id, Mode, Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rational { public uint Numerator, Denominator; }
    [StructLayout(LayoutKind.Sequential)] internal struct TargetInfo { public Luid Adapter; public uint Id, Mode, Technology, Rotation, Scaling; public Rational Refresh; public uint ScanLine; public int Available; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct PathInfo { public Source Source; public TargetInfo Target; public uint Flags; }
    [StructLayout(LayoutKind.Explicit, Size = 64)] internal struct ModeInfo { [FieldOffset(0)] public uint Type; }
    [StructLayout(LayoutKind.Sequential)] internal struct ColorInfo { public Header Header; public uint Flags, Encoding, Bits; }
    [StructLayout(LayoutKind.Sequential)] internal struct ColorInfo2 { public Header Header; public uint Flags, Encoding, Bits, ActiveMode; }
    [StructLayout(LayoutKind.Sequential)] internal struct SetColor { public Header Header; public uint Enabled; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct SourceName { public Header Header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PathInfo[] pathArray, ref uint modes, [Out] ModeInfo[] modeArray, IntPtr topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetColor(ref ColorInfo value);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetColor2(ref ColorInfo2 value);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)] private static extern int GetSource(ref SourceName value);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")] private static extern int SetDevice(ref SetColor value);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo value);
    private static Header For<T>(uint type, DisplayTarget target) => new() { Type = type, Size = (uint)Marshal.SizeOf<T>(), Adapter = new() { Low = target.AdapterLow, High = target.AdapterHigh }, Id = target.Id };
    public IReadOnlyList<DisplayTarget> Targets()
    {
        var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        var primary = GetMonitorInfo(MonitorFromPoint(default, 1), ref monitor) ? monitor.Device : "";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Check(GetDisplayConfigBufferSizes(2, out uint pathCount, out uint modeCount));
            if (pathCount > 64 || modeCount > 256) throw new IOException("Unexpected display topology.");
            var paths = new PathInfo[pathCount]; var modes = new ModeInfo[modeCount];
            int result = QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == 122) continue;
            Check(result);
            return paths.Take((int)pathCount).Where(p => p.Target.Available != 0).Select(p => {
                var target = new DisplayTarget(p.Target.Adapter.Low, p.Target.Adapter.High, p.Target.Id, p.Target.Technology is 11 or 13 or 0x80000000, false);
                var source = new SourceName { Header = new() { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = p.Source.Adapter, Id = p.Source.Id } };
                bool isPrimary = GetSource(ref source) == 0 && String.Equals(source.Name, primary, StringComparison.OrdinalIgnoreCase);
                return target with { Primary = isPrimary };
            }).ToArray();
        }
        throw new IOException("Display connections are changing. Try again.");
    }
    public HdrCapabilities Read(DisplayTarget target)
    {
        var modern = new ColorInfo2 { Header = For<ColorInfo2>(15, target) };
        int result = GetColor2(ref modern);
        if (result == 0) return Decode(modern.Flags, true);
        if (result is not (87 or 50)) Check(result);
        var legacy = new ColorInfo { Header = For<ColorInfo>(9, target) };
        Check(GetColor(ref legacy));
        // Wide-color-only composition must not be reported as HDR.
        return Decode(legacy.Flags, false);
    }
    internal static HdrCapabilities Decode(uint flags, bool modern) => modern
        ? new((flags & 16) != 0, (flags & 32) != 0, (flags & 8) != 0, true)
        : new((flags & 1) != 0 && (flags & 4) == 0, (flags & 2) != 0, (flags & 8) != 0, false);
    public void Write(DisplayTarget target, bool enabled, bool modern)
    {
        var value = new SetColor { Header = For<SetColor>(modern ? 16u : 10u, target), Enabled = enabled ? 1u : 0u };
        Check(SetDevice(ref value));
    }
    private static void Check(int result) { if (result != 0) throw new Win32Exception(result); }
}

internal static class HdrControlTests
{
    private sealed class FakeApi : IHdrApi
    {
        internal DisplayTarget BuiltIn = new(1, 0, 1, true, false), External = new(1, 0, 2, false, true);
        internal bool Supported = true, Enabled, Limited, Modern = true;
        internal List<(DisplayTarget Target, bool Enabled, bool Modern)> Writes = new();
        public IReadOnlyList<DisplayTarget> Targets() => [External, BuiltIn];
        public HdrCapabilities Read(DisplayTarget target) => new(Supported, Enabled, Limited, Modern);
        public void Write(DisplayTarget target, bool enabled, bool modern) { Writes.Add((target, enabled, modern)); Enabled = enabled; }
    }
    internal static void Run()
    {
        static void Assert(bool ok) { if (!ok) throw new Exception("HDR control regression failed."); }
        Assert(Marshal.SizeOf<WindowsHdrApi.PathInfo>() == 72 && Marshal.SizeOf<WindowsHdrApi.ModeInfo>() == 64 && Marshal.SizeOf<WindowsHdrApi.Header>() == 20);
        Assert(Marshal.SizeOf<WindowsHdrApi.ColorInfo>() == 32 && Marshal.SizeOf<WindowsHdrApi.ColorInfo2>() == 36 && Marshal.SizeOf<WindowsHdrApi.SetColor>() == 24 && Marshal.SizeOf<WindowsHdrApi.SourceName>() == 84);
        Assert(!WindowsHdrApi.Decode(1 | 2 | 64 | 128, true).Supported);
        Assert(WindowsHdrApi.Decode(16 | 32, true) == new HdrCapabilities(true, true, false, true));
        Assert(WindowsHdrApi.Decode(16 | 8, true).Limited);
        Assert(!WindowsHdrApi.Decode(1 | 2 | 4, false).Supported);
        Assert(WindowsHdrApi.Decode(1 | 2, false) == new HdrCapabilities(true, true, false, false));
        var api = new FakeApi(); var control = new HdrControl(api);
        Assert(control.Read() == new HdrState(true, false) && api.Writes.Count == 0);
        control.Set(true); control.Set(true);
        Assert(api.Writes.Count == 1 && api.Writes[0] == (api.BuiltIn, true, true));
        api.Modern = false; control.Set(false);
        Assert(api.Writes.Count == 2 && !api.Writes[1].Modern && !api.Enabled);
        foreach (bool limited in new[] { false, true }) {
            api.Supported = limited; api.Limited = limited;
            Assert(!control.Read().Available);
            try { control.Set(true); throw new Exception("Unavailable HDR accepted."); } catch (IOException) { }
        }
        Assert(api.Writes.Count == 2);
        Console.WriteLine("PASS: HDR ABI sizes, built-in display targeting, modern/legacy HDR versus wide-color flags, read-only polling, idempotence and unavailable/policy guards. No display changed.");
    }
}
