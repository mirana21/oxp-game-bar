using System.Management;
using System.Text.Json;
using Microsoft.Win32;

namespace OXP3.PowerWidget.Helper;

internal sealed record DisplayState(bool BrightnessAvailable, int? Brightness, bool NightLightAvailable, bool? NightLight, int? NightLightStrength);

internal sealed class DisplayControl
{
    private readonly SemaphoreSlim gate = new(1);
    private DisplayState? cached;
    private DateTime refreshed;
    internal async Task<object> Command(JsonElement request, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var command = request.GetProperty("command").GetString();
            if (command == "setBrightness")
            {
                int value = request.GetProperty("brightness").GetInt32();
                if (value < 0 || value > 100) throw new ArgumentException("Brightness must be between 0 and 100%.");
                await Task.Run(() => SetBrightness(value), token);
                cached = null;
            }
            else if (command == "setNightLight")
            {
                bool value = request.GetProperty("enabled").GetBoolean();
                NightLightCodec.Set(value);
                cached = null;
            }
            else if (command == "setNightLightStrength")
            {
                NightLightCodec.SetStrength(request.GetProperty("strength").GetInt32());
                cached = null;
            }
            if (cached == null || DateTime.UtcNow - refreshed >= TimeSpan.FromSeconds(2))
            {
                int? brightness = null, strength = null; bool? night = null;
                try { brightness = await Task.Run(ReadBrightness, token); } catch (Exception) { }
                try { var state = NightLightCodec.Read(); if (state.Usable) night = state.Active; } catch (Exception) { }
                try { strength = NightLightCodec.ReadStrength(); } catch (Exception) { }
                cached = new(brightness.HasValue, brightness, night.HasValue, night, strength);
                refreshed = DateTime.UtcNow;
            }
            return new { brightnessAvailable = cached.BrightnessAvailable, brightness = cached.Brightness, nightLightAvailable = cached.NightLightAvailable, nightLight = cached.NightLight, nightLightStrength = cached.NightLightStrength };
        }
        finally { gate.Release(); }
    }
    private static ManagementObjectSearcher Query(string query) => new("root\\wmi", query) { Options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(3) } };
    private static int? ReadBrightness()
    {
        using var query = Query("SELECT * FROM WmiMonitorBrightness WHERE Active=True");
        using var monitors = query.Get();
        foreach (ManagementObject monitor in monitors) using (monitor) return Convert.ToInt32(monitor["CurrentBrightness"]);
        return null;
    }
    private static void SetBrightness(int brightness)
    {
        using var query = Query("SELECT * FROM WmiMonitorBrightness WHERE Active=True");
        using var monitors = query.Get(); string? instance = null;
        foreach (ManagementObject monitor in monitors) using (monitor) { instance = (string)monitor["InstanceName"]; break; }
        if (instance == null) throw new IOException("Screen brightness is unavailable.");
        using var methodsQuery = Query("SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=True");
        using var methods = methodsQuery.Get();
        foreach (ManagementObject method in methods) using (method)
        {
            if (!String.Equals(instance, (string)method["InstanceName"], StringComparison.OrdinalIgnoreCase)) continue;
            using var input = method.GetMethodParameters("WmiSetBrightness");
            if (input == null) throw new IOException("Windows brightness controls are unavailable.");
            input["Timeout"] = (uint)0; input["Brightness"] = (byte)brightness;
            using var result = method.InvokeMethod("WmiSetBrightness", input, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(3) });
            // This device's WMI provider returns null after successfully applying
            // brightness. Readback is the confirmation, not an optional result.
            for (int attempt = 0; attempt < 10; attempt++) { if (ReadBrightness() == brightness) return; Thread.Sleep(80); }
            throw new IOException("Windows did not confirm the brightness.");
        }
        throw new IOException("Screen brightness is unavailable.");
    }
}

// Windows 11 CloudStore/Bond CompactBinary v1. Schema informed by
// Kevin Xiao's MIT-licensed win-nightlight-lib (see licenses/NightLight.txt).
// Preserve unmodelled fields byte-for-byte; only state, manual-transition time,
// and envelope timestamp change. Strength edits change only colour temperature.
internal static class NightLightCodec
{
    internal const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";
    internal const string SettingsPath = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.settings\windows.data.bluelightreduction.settings";
    internal sealed record State(bool Active, bool Usable);
    private sealed record Field(int Id, int Type, byte[] Value);
    private static readonly byte[] Header = [0x43, 0x42, 1, 0];
    private static byte[] RegistryData() { using var key = Registry.CurrentUser.OpenSubKey(RegistryPath); return key?.GetValue("Data") as byte[] ?? throw new IOException("Night light is unavailable."); }
    internal static State Read() => Decode(RegistryData());
    internal static int ReadStrength() { using var key = Registry.CurrentUser.OpenSubKey(SettingsPath); return DecodeStrength(key?.GetValue("Data") as byte[] ?? throw new IOException("Night light strength is unavailable.")); }
    internal static void SetStrength(int strength)
    {
        if (strength < 0 || strength > 100) throw new ArgumentException("Night light strength must be between 0 and 100%.");
        using var key = Registry.CurrentUser.OpenSubKey(SettingsPath, true) ?? throw new IOException("Night light strength is unavailable.");
        var data = key.GetValue("Data") as byte[] ?? throw new IOException("Night light strength is unavailable.");
        if (DecodeStrength(data) == strength) return;
        var updated = ChangeStrength(data, strength, DateTime.UtcNow);
        if (key.GetValue("Data") is not byte[] latest || !latest.SequenceEqual(data)) throw new IOException("Night light settings changed in Windows. Try again.");
        key.SetValue("Data", updated, RegistryValueKind.Binary);
        if (ReadStrength() != strength) throw new IOException("Windows did not confirm Night light strength.");
    }
    internal static void Set(bool enabled)
    {
        var data = RegistryData(); var state = Decode(data);
        if (!state.Usable) throw new IOException("Night light is unavailable.");
        if (state.Active == enabled) return;
        var updated = Change(data, enabled, DateTime.UtcNow);
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true) ?? throw new IOException("Night light is unavailable.");
        // Do not overwrite a concurrent Windows Settings/schedule transition.
        if (key.GetValue("Data") is not byte[] latest || !latest.SequenceEqual(data)) throw new IOException("Night light changed in Windows. Try again.");
        key.SetValue("Data", updated, RegistryValueKind.Binary);
        if (Read().Active != enabled) throw new IOException("Windows did not confirm Night light.");
    }
    private static (List<Field> Outer, List<Field> Container, List<Field> Wrapper, List<Field> Payload) Unwrap(byte[] data)
    {
        var outer = Parse(data, true);
        var container = Parse(Required(outer, 1, 10).Value);
        Required(container, 0, 6);
        var wrapper = Parse(Required(container, 1, 10).Value);
        var list = Required(wrapper, 1, 11).Value; int pos = 0;
        if (Byte(list, ref pos) != 14) throw Bad();
        int size = checked((int)Var(list, ref pos));
        if (list.Length - pos != size) throw Bad();
        var payload = Parse(list[pos..], true);
        return (outer, container, wrapper, payload);
    }
    internal static State Decode(byte[] data)
    {
        var fields = Unwrap(data).Payload;
        foreach (var field in fields) if ((field.Id == 0 || field.Id == 10) && field.Type != 16 || field.Id == 20 && field.Type != 6 || field.Id == 30 && field.Type != 2) throw Bad();
        var usable = fields.FirstOrDefault(f => f.Id == 30);
        return new(fields.Any(f => f.Id == 0), usable == null || usable.Value[0] != 0);
    }
    internal static byte[] Change(byte[] data, bool enabled, DateTime utc)
    {
        var (outer, container, wrapper, payload) = Unwrap(data);
        if (!Decode(data).Usable) throw Bad();
        if (enabled) { if (!payload.Any(f => f.Id == 0)) payload.Add(new(0, 16, [0])); }
        else payload.RemoveAll(f => f.Id == 0);
        Replace(payload, new(10, 16, [2])); // Zigzag int32 manual=1.
        Replace(payload, new(20, 6, EncodeVar(checked((ulong)utc.ToFileTimeUtc()))));
        return Rewrap(outer, container, wrapper, payload, utc);
    }
    internal static int DecodeStrength(byte[] data)
    {
        var fields = Unwrap(data).Payload;
        var temperature = fields.SingleOrDefault(f => f.Id == 40);
        if (temperature == null) return 50; // Windows' unset default strength.
        if (temperature.Type != 15) throw Bad();
        int pos = 0; ulong raw = Var(temperature.Value, ref pos);
        if (raw > ushort.MaxValue) throw Bad();
        int kelvin = (int)(raw >> 1) ^ -((int)raw & 1);
        if (kelvin < 1200 || kelvin > 6500) throw Bad();
        return (int)Math.Round((6500 - kelvin) * 100.0 / 5300, MidpointRounding.AwayFromZero);
    }
    internal static byte[] ChangeStrength(byte[] data, int strength, DateTime utc)
    {
        if (strength < 0 || strength > 100) throw new ArgumentException("Invalid Night light strength.");
        DecodeStrength(data);
        var (outer, container, wrapper, payload) = Unwrap(data);
        int kelvin = 6500 - strength * 53;
        Replace(payload, new(40, 15, EncodeVar((ulong)kelvin * 2)));
        return Rewrap(outer, container, wrapper, payload, utc);
    }
    private static byte[] Rewrap(List<Field> outer, List<Field> container, List<Field> wrapper, List<Field> payload, DateTime utc)
    {
        var inner = Encode(payload, true);
        Replace(wrapper, new(1, 11, new byte[] {14}.Concat(EncodeVar((ulong)inner.Length)).Concat(inner).ToArray()));
        Replace(container, new(1, 10, Encode(wrapper)));
        int pos = 0; ulong previous = Var(Required(container, 0, 6).Value, ref pos);
        ulong now = checked((ulong)new DateTimeOffset(utc).ToUnixTimeSeconds());
        Replace(container, new(0, 6, EncodeVar(Math.Max(now, checked(previous + 2)))));
        Replace(outer, new(1, 10, Encode(container)));
        return Encode(outer, true);
    }
    private static IOException Bad() => new("This Windows Night light format is unsupported.");
    private static Field Required(List<Field> fields, int id, int type) { var f = fields.SingleOrDefault(x => x.Id == id); return f != null && f.Type == type ? f : throw Bad(); }
    private static void Replace(List<Field> fields, Field value) { fields.RemoveAll(x => x.Id == value.Id); fields.Add(value); }
    private static byte Byte(byte[] data, ref int pos) => pos < data.Length ? data[pos++] : throw Bad();
    private static ulong Var(byte[] data, ref int pos)
    {
        ulong value = 0;
        for (int shift = 0; shift < 70; shift += 7) { byte b = Byte(data, ref pos); if (shift == 63 && b > 1) throw Bad(); value |= (ulong)(b & 127) << shift; if (b < 128) return value; }
        throw Bad();
    }
    private static byte[] EncodeVar(ulong value) { var bytes = new List<byte>(); while (value >= 128) { bytes.Add((byte)((value & 127) | 128)); value >>= 7; } bytes.Add((byte)value); return bytes.ToArray(); }
    private static void Advance(byte[] data, ref int pos, int size) { if (size < 0 || size > data.Length - pos) throw Bad(); pos += size; }
    private static void Skip(byte[] data, ref int pos, int type, int depth)
    {
        if (depth > 16) throw Bad();
        switch (type)
        {
            case 2: case 3: case 14: Advance(data, ref pos, 1); break;
            case 4: case 5: case 6: case 15: case 16: case 17: Var(data, ref pos); break;
            case 7: Advance(data, ref pos, 4); break;
            case 8: Advance(data, ref pos, 8); break;
            case 9: case 18: int count = checked((int)Var(data, ref pos)); Advance(data, ref pos, checked(count * (type == 18 ? 2 : 1))); break;
            case 10: Fields(data, ref pos, depth + 1); break;
            case 11: case 12:
                int element = Byte(data, ref pos); int length = checked((int)Var(data, ref pos));
                if (length > data.Length) throw Bad();
                for (int i = 0; i < length; i++) Skip(data, ref pos, element, depth + 1);
                break;
            case 13:
                int key = Byte(data, ref pos), value = Byte(data, ref pos), entries = checked((int)Var(data, ref pos));
                if (entries > data.Length) throw Bad();
                for (int i = 0; i < entries; i++) { Skip(data, ref pos, key, depth + 1); Skip(data, ref pos, value, depth + 1); }
                break;
            default: throw Bad();
        }
    }
    private static List<Field> Fields(byte[] data, ref int pos, int depth)
    {
        var fields = new List<Field>();
        while (true)
        {
            int header = Byte(data, ref pos); if (header == 0) return fields;
            int type = header & 31, id = header >> 5;
            if (id == 6) id = Byte(data, ref pos); else if (id == 7) id = Byte(data, ref pos) | Byte(data, ref pos) << 8;
            int start = pos; Skip(data, ref pos, type, depth);
            if (fields.Any(f => f.Id == id)) throw Bad();
            fields.Add(new(id, type, data[start..pos]));
        }
    }
    private static List<Field> Parse(byte[] data, bool marshalled = false)
    {
        if (data.Length > 65536) throw Bad(); int pos = 0;
        if (marshalled) { if (data.Length < 4 || !data[..4].SequenceEqual(Header)) throw Bad(); pos = 4; }
        var fields = Fields(data, ref pos, 0); if (pos != data.Length) throw Bad(); return fields;
    }
    private static byte[] Encode(List<Field> fields, bool marshalled = false)
    {
        var bytes = new List<byte>(); if (marshalled) bytes.AddRange(Header);
        foreach (var field in fields.OrderBy(f => f.Id))
        {
            if (field.Id <= 5) bytes.Add((byte)(field.Id << 5 | field.Type));
            else if (field.Id <= 255) { bytes.Add((byte)(192 | field.Type)); bytes.Add((byte)field.Id); }
            else { bytes.Add((byte)(224 | field.Type)); bytes.Add((byte)field.Id); bytes.Add((byte)(field.Id >> 8)); }
            bytes.AddRange(field.Value);
        }
        bytes.Add(0); return bytes.ToArray();
    }
    internal static void SelfTest()
    {
        var on = Convert.FromHexString("434201000A0201002A068995FCBE062A2B0E15434201001000D00A02C614A9F6E2D3EFEAE6ED0100000000");
        if (!Decode(on).Active) throw new Exception("Night light active fixture failed.");
        var off = Change(on, false, DateTime.UtcNow);
        if (Decode(off).Active || !Decode(Change(off, true, DateTime.UtcNow)).Active) throw new Exception("Night light transitions failed.");
        var withUnknown = Unwrap(on); withUnknown.Payload.Add(new(40, 9, [3, 97, 98, 99]));
        var payload = Encode(withUnknown.Payload, true);
        Replace(withUnknown.Wrapper, new(1, 11, new byte[] {14}.Concat(EncodeVar((ulong)payload.Length)).Concat(payload).ToArray()));
        Replace(withUnknown.Container, new(1, 10, Encode(withUnknown.Wrapper))); Replace(withUnknown.Outer, new(1, 10, Encode(withUnknown.Container)));
        var changed = Unwrap(Change(Encode(withUnknown.Outer, true), false, DateTime.UtcNow));
        if (!Required(changed.Payload, 40, 9).Value.SequenceEqual(new byte[] {3, 97, 98, 99})) throw new Exception("Unknown Night light field was lost.");
        try { Change([0x43, 0x42, 1, 0], true, DateTime.UtcNow); throw new Exception("Malformed state accepted."); } catch (IOException) { }
        var settings = Convert.FromHexString("434201000A0201002A06B096ECD5062A2B0E1543420100CA140E1500CA1E0E0700CA3200CA3C0000000000");
        if (DecodeStrength(settings) != 50) throw new Exception("Default Night light strength failed.");
        foreach (int strength in new[] {0, 49, 50, 51, 100})
        {
            var newSettings = ChangeStrength(settings, strength, DateTime.UtcNow);
            if (DecodeStrength(newSettings) != strength) throw new Exception("Night light strength roundtrip failed.");
            var expected = Encode(Unwrap(settings).Payload);
            var fields = Unwrap(newSettings).Payload; fields.RemoveAll(f => f.Id == 40);
            if (!Encode(fields).SequenceEqual(expected)) throw new Exception("Night light schedule fields changed.");
        }
        try { ChangeStrength(settings, 101, DateTime.UtcNow); throw new Exception("Out-of-range strength accepted."); } catch (ArgumentException) { }
    }
}
