using System.Diagnostics;

namespace OXP3.PowerWidget.Helper;

internal static class StartupRecovery
{
    private static readonly object Gate = new();
    private static long nextAttempt;

    internal static void Request()
    {
        lock (Gate)
        {
            if (Environment.TickCount64 < nextAttempt) return;
            nextAttempt = Environment.TickCount64 + 60000;
        }
        // Setup registers this same-user elevated action. Starting it needs
        // no elevation prompt and never terminates a running native session.
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
                Arguments = "/Run /TN OXP3GamePowerRecovery",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch { /* Connection state remains unavailable; the next request can retry. */ }
    }
}
