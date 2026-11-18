using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal static class PowerBridge
{
    private const int MinWatts = 4, MaxWatts = 35;
    private const string Vendor = @"C:\Program Files\OneXConsole";
    private const string NativeTools = Vendor + @"\resources\resources";
    private const string Plugin = NativeTools + @"\tools\intel\IntelPowerPlugin.exe";
    private const string Version = "0.10.4-fix3";
    private const string ModuleHash = "D6ED85D65AB17A22F813EF98207D6D537155EE2DED5976A21CB48413C9B92E5F";
    private const string PipeName = "OXP3.OneXConsole.Power.v1";
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 2048, RecursionLimit = 8 };
    private static byte[] Module;
    private static DateTime VendorStamp, PluginStamp;
    private static int SessionId;

    [DllImport(NativeTools + @"\PawnIOLib.dll")]
    private static extern int pawnio_open(out IntPtr handle);
    [DllImport(NativeTools + @"\PawnIOLib.dll")]
    private static extern int pawnio_load(IntPtr handle, byte[] blob, UIntPtr size);
    [DllImport(NativeTools + @"\PawnIOLib.dll")]
    private static extern int pawnio_execute(IntPtr handle, string name, ulong[] input, UIntPtr inputCount,
        [Out] ulong[] output, UIntPtr outputCount, out UIntPtr returned);
    [DllImport(NativeTools + @"\PawnIOLib.dll")]
    private static extern int pawnio_close(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientId);

    private static int Main()
    {
        try
        {
            if (!IsSupported())
                return 20;

            using (var instance = new Mutex(false, @"Local\OXP3.StockPowerBridge"))
            {
                bool owned;
                try { owned = instance.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) return 0;
                try
                {
                    VendorStamp = File.GetLastWriteTimeUtc(Path.Combine(Vendor, "OneXConsole.exe"));
                    PluginStamp = File.GetLastWriteTimeUtc(Plugin);
                    using (var self = Process.GetCurrentProcess()) SessionId = self.SessionId;
                    Module = File.ReadAllBytes(Path.Combine(NativeTools, @"tools\pawnio\IntelMSR.bin"));
                    using (var sha256 = SHA256.Create())
                    {
                        var hash = BitConverter.ToString(sha256.ComputeHash(Module)).Replace("-", "");
                        if (hash != ModuleHash) throw new IOException("The installed power readback module differs from the reviewed version.");
                    }
                    Serve();
                }
                finally { instance.ReleaseMutex(); }
            }
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                var directory = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')).Parent.FullName;
                File.AppendAllText(Path.Combine(directory, "stock-power-bridge.log"),
                    DateTime.UtcNow.ToString("o") + " " + ex.Message + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return 1;
        }
    }

    private static bool IsSupported()
    {
        var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            return false;

        var product = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS", "SystemProductName", null) as string;
        var cpu = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
        return product == "ONEXPLAYER 3" && cpu != null && cpu.IndexOf("Arc(TM) G3 Extreme", StringComparison.OrdinalIgnoreCase) >= 0
            && FileVersionInfo.GetVersionInfo(Path.Combine(Vendor, "OneXConsole.exe")).FileVersion == Version;
    }

    private static bool VendorReady()
    {
        if (File.GetLastWriteTimeUtc(Path.Combine(Vendor, "OneXConsole.exe")) != VendorStamp ||
            File.GetLastWriteTimeUtc(Plugin) != PluginStamp)
            return false;

        var processes = Process.GetProcessesByName("CompatLayerCT");
        try
        {
            foreach (var process in processes)
            {
                if (process.SessionId != SessionId) continue;
                try
                {
                    if (string.Equals(process.MainModule.FileName, Path.Combine(NativeTools, "CompatLayerCT.exe"), StringComparison.OrdinalIgnoreCase) &&
                        DateTime.UtcNow - process.StartTime.ToUniversalTime() >= TimeSpan.FromSeconds(20))
                        return true;
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            }
            return false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static object State()
    {
        if (!VendorReady())
            return new { nativeConnected = false };
        var limits = ReadLimits();
        return new { nativeConnected = true, tdp = limits[0], minTdp = MinWatts, maxTdp = MaxWatts, stateKind = "readback" };
    }

    private static ulong Register(IntPtr handle, ulong address)
    {
        var output = new ulong[1];
        UIntPtr actual;
        Marshal.ThrowExceptionForHR(pawnio_execute(handle, "ioctl_read_msr", new[] { address }, new UIntPtr(1), output, new UIntPtr(1), out actual));
        if (actual.ToUInt64() != 1) throw new IOException("Incomplete power-limit readback.");
        return output[0];
    }

    private static int[] ReadLimits()
    {
        IntPtr handle;
        Marshal.ThrowExceptionForHR(pawnio_open(out handle));
        try
        {
            Marshal.ThrowExceptionForHR(pawnio_load(handle, Module, new UIntPtr((uint)Module.Length)));
            var scale = Math.Pow(2, -(int)(Register(handle, 0x606) & 15UL));
            var limits = Register(handle, 0x610);
            if ((limits & (1UL << 15)) == 0 || (limits & (1UL << 47)) == 0)
                throw new IOException("The processor does not report both enabled power limits.");
            var sustained = (limits & 0x7fffUL) * scale;
            var boost = ((limits >> 32) & 0x7fffUL) * scale;
            if (sustained != Math.Truncate(sustained) || boost != Math.Truncate(boost))
                throw new IOException("The processor returned a fractional limit outside the widget's whole-watt controls.");
            return new[] { (int)sustained, (int)boost };
        }
        finally { pawnio_close(handle); }
    }

    private static bool CanWrite(NamedPipeServerStream pipe)
    {
        uint pid;
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid) || pid == 0) return false;
        try
        {
            using (var client = Process.GetProcessById((int)pid))
            {
                var exe = client.MainModule.FileName;
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + "\\";
                if (!exe.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
                var relative = exe.Substring(root.Length).Split('\\');
                return relative.Length == 3 && relative[0].StartsWith("OXP3.PowerWidget_", StringComparison.OrdinalIgnoreCase) &&
                    relative[0].EndsWith("_pv4gfha69c7qe", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(relative[1], "Helper", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(relative[2], "OXP3.PowerWidget.Helper.exe", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static object Apply(int watts)
    {
        if (!VendorReady()) throw new InvalidOperationException("The OneXConsole power service is starting or updating.");
        if (watts < MinWatts || watts > MaxWatts)
            throw new ArgumentOutOfRangeException("watts", "The reviewed power range is 4–35 W.");

        // The ONEXPLAYER 3 OEM profile pairs each PL1 with PL2 one watt higher.
        // Leave PL4 and the rest of the vendor's power and fan policy alone.
        var before = ReadLimits();
        if (before[0] < 3 || before[0] > 40 || before[1] < before[0] || before[1] > 41)
            throw new IOException("The current limits are outside the reviewed restoration range.");
        if (before[0] == watts && before[1] == watts + 1)
            return State();

        try
        {
            SetLimits(watts, watts + 1);
            var actual = ReadLimits();
            if (actual[0] != watts || actual[1] != watts + 1)
                throw new IOException("The processor did not confirm the requested limits.");
            return State();
        }
        catch
        {
            var live = ReadLimits();
            if ((live[0] == watts || live[1] == watts + 1) &&
                (live[0] == watts || live[0] == before[0]) && (live[1] == watts + 1 || live[1] == before[1]))
                SetLimits(before[0], before[1]);
            throw;
        }
    }

    private static void SetLimits(int sustained, int boost)
    {
        if (sustained < 3 || sustained > 40 || boost < sustained || boost > 41)
            throw new InvalidOperationException("The previous power limits are outside the reviewed restoration range.");
        var start = new ProcessStartInfo(Plugin)
        {
            Arguments = string.Format(CultureInfo.InvariantCulture, "-PL1 {0} -PL2 {1}", sustained, boost),
            WorkingDirectory = Path.GetDirectoryName(Plugin),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using (var process = Process.Start(start))
        {
            if (!process.WaitForExit(3500))
            {
                process.Kill();
                process.WaitForExit();
                throw new IOException("The signed power component did not finish in time.");
            }
            if (process.ExitCode != 0)
                throw new IOException("The signed power component failed.");
        }
    }

    private static string ReadRequest(NamedPipeServerStream pipe)
    {
        var result = new List<byte>();
        var buffer = new byte[256];
        var clock = Stopwatch.StartNew();
        while (result.Count < 1024)
        {
            var remaining = 1500 - (int)clock.ElapsedMilliseconds;
            if (remaining <= 0) throw new IOException("Request timed out.");
            var read = pipe.ReadAsync(buffer, 0, buffer.Length);
            if (!read.Wait(remaining)) throw new IOException("Request timed out.");
            var count = read.GetAwaiter().GetResult();
            if (count == 0) break;
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] == '\n') return Encoding.UTF8.GetString(result.ToArray());
                result.Add(buffer[i]);
            }
        }
        throw new IOException("Invalid power request.");
    }

    private static void Serve()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        using (var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 2048, 2048, security))
        {
            for (;;)
            {
                pipe.WaitForConnection();
                try
                {
                    object reply;
                    try
                    {
                        var request = Json.Deserialize<Dictionary<string, object>>(ReadRequest(pipe));
                        object command, watts, result;
                        if (request.TryGetValue("command", out command) && (command as string) == "getState")
                            result = State();
                        else if ((command as string) == "setWatts" && request.TryGetValue("watts", out watts) && watts is int && CanWrite(pipe))
                            result = Apply((int)watts);
                        else throw new InvalidOperationException("This client is not authorized for the requested power command.");
                        reply = new { ok = true, result = result };
                    }
                    catch (Exception ex) { reply = new { ok = false, error = ex.GetBaseException().Message }; }
                    var bytes = Encoding.UTF8.GetBytes(Json.Serialize(reply) + "\n");
                    pipe.Write(bytes, 0, bytes.Length);
                }
                catch (IOException) { }
                finally { pipe.Disconnect(); }
            }
        }
    }
}
