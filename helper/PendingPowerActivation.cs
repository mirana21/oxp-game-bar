using System.Diagnostics;
using System.Text.Json;

namespace OXP3.PowerWidget.Helper;

internal sealed class PendingPowerActivation
{
    private sealed record Pending(int NativeProcessId, DateTime NativeStartedUtc);
    private readonly string file;
    private readonly Func<int, DateTime?> started;

    internal PendingPowerActivation(string directory, Func<int, DateTime?>? started = null)
    {
        file = Path.Combine(directory, "pending-power-startup.json");
        this.started = started ?? NativeStart;
    }

    internal bool NeedsRestart()
    {
        try
        {
            if (!File.Exists(file)) return false;
            var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (pending == null || pending.NativeProcessId <= 0 || pending.NativeStartedUtc.Kind != DateTimeKind.Utc) return false;
            var actual = started(pending.NativeProcessId);
            // The instruction belongs to the uninstrumented native instance
            // left running by Setup. Do not repeat it after that instance exits.
            return actual.HasValue && Math.Abs((actual.Value.ToUniversalTime() - pending.NativeStartedUtc).TotalSeconds) < 1;
        }
        catch { return false; }
    }

    internal void Connected()
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch { /* A stale notice must never turn a working connection into a failure. */ }
    }

    private static DateTime? NativeStart(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.Equals("OneXConsole", StringComparison.OrdinalIgnoreCase) ? process.StartTime.ToUniversalTime() : null;
        }
        catch { return null; }
    }
}
