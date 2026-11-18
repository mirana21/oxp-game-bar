using System;
using System.Diagnostics;
using System.IO;

internal static class MaintenanceLauncher
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
            return 87;

        string directory = AppDomain.CurrentDomain.BaseDirectory;
        string scriptName;
        switch (args[0])
        {
            case "update":
                // The versioned launcher stays in place while Setup upgrades its scripts.
                directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "OXP3 Game Power Setup", "Scripts");
                scriptName = "Update.ps1";
                break;
            case "recover":
                scriptName = "recover-startup.ps1";
                break;
            case "startup":
                scriptName = "startup-native.ps1";
                break;
            default:
                return 87;
        }

        string script = Path.Combine(directory, scriptName);
        if (!File.Exists(script))
            return 2;

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var info = new ProcessStartInfo(Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"));
        info.Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"";
        info.WorkingDirectory = directory;
        info.UseShellExecute = false;
        // Use CREATE_NO_WINDOW. Hiding an already-created console can flash.
        info.CreateNoWindow = true;

        try
        {
            using (Process process = Process.Start(info))
            {
                process.WaitForExit();
                return process.ExitCode;
            }
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            return error.NativeErrorCode;
        }
    }
}
