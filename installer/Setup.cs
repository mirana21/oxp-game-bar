using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class SetupProgram
{
    private const string ManifestHash = "@@MANIFESTSHA@@";
    internal static readonly string Root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    private static extern int GetCurrentPackageFullName(ref int length, IntPtr name);
    internal static string Hash(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    internal static void VerifyBundle()
    {
        var manifest = Path.Combine(Root, "release.json");
        if (Hash(manifest) != ManifestHash) throw new Exception("The installation bundle changed. Extract the original release ZIP again.");
        var serializer = new JavaScriptSerializer();
        dynamic release = serializer.DeserializeObject(File.ReadAllText(manifest));
        foreach (dynamic item in release["files"])
        {
            string relative = item["path"];
            var full = Path.GetFullPath(Path.Combine(Root, relative));
            if (!full.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase) || Hash(full) != (string)item["sha256"]) throw new Exception("An installation file changed: " + relative);
        }
    }
    private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
    internal static async Task<int> RunEngine(string mode, Action<string> log)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"));
        info.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File " + Quote(Path.Combine(Root, "Scripts", "SetupEngine.ps1")) + " -Mode " + mode;
        info.UseShellExecute = false; info.CreateNoWindow = true;
        info.RedirectStandardOutput = true; info.RedirectStandardError = true;
        using (var process = new Process())
        {
            process.StartInfo = info;
            process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) log(e.Data); };
            process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) log(e.Data); };
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            await Task.Run(() => process.WaitForExit());
            return process.ExitCode;
        }
    }
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            VerifyBundle();
            bool check = Array.IndexOf(args, "--check") >= 0;
            bool installTest = Array.IndexOf(args, "--install") >= 0;
            int length = 0;
            bool packaged = GetCurrentPackageFullName(ref length, IntPtr.Zero) != 15700;
            if (packaged)
            {
                if (check || installTest) throw new Exception("Command-line setup must run outside the packaged launcher.");
                return RunEngine("Bootstrap", s => {}).GetAwaiter().GetResult();
            }
            int cleanup = Array.IndexOf(args, "--cleanup-task");
            if (cleanup >= 0 && cleanup + 1 < args.Length && System.Text.RegularExpressions.Regex.IsMatch(args[cleanup+1], "^OXP3GamePowerSetup-[a-f0-9]{32}$"))
            {
                var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"));
                info.Arguments = "-NoProfile -NonInteractive -Command \"Unregister-ScheduledTask -TaskName '" + args[cleanup+1] + "' -Confirm:$false\"";
                info.UseShellExecute = false; info.CreateNoWindow = true;
                using (var child = Process.Start(info)) child.WaitForExit();
            }
            if (check || installTest)
            {
                var output = new StringBuilder();
                int exit = RunEngine(installTest ? "Install" : "Check", s => { lock(output) output.AppendLine(s); }).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(Root, installTest ? "setup-install-result.txt" : "setup-check-result.txt"), output.ToString());
                return exit;
            }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupWindow(Array.IndexOf(args, "--remove") >= 0));
            return 0;
        }
        catch (Exception error)
        {
            if (Array.IndexOf(args, "--check") >= 0 || Array.IndexOf(args, "--install") >= 0) File.WriteAllText(Path.Combine(Root, Array.IndexOf(args, "--install") >= 0 ? "setup-install-result.txt" : "setup-check-result.txt"), error.Message);
            else MessageBox.Show(error.Message, "OXP3 Game Power Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

internal sealed class SetupWindow : Form
{
    private readonly Button install = new Button { Text="Install / Repair", AutoSize=true };
    private readonly Button remove = new Button { Text="Remove", AutoSize=true };
    private readonly TextBox output = new TextBox { Multiline=true, ReadOnly=true, ScrollBars=ScrollBars.Vertical, Dock=DockStyle.Fill };
    private readonly Label status = new Label { Text="Ready to install", AutoSize=true };
    private bool busy;
    internal SetupWindow(bool startRemove)
    {
        Text="OXP3 Game Power Setup"; ClientSize=new Size(560,360); MinimumSize=new Size(550,350); Font=new Font("Segoe UI",10); StartPosition=FormStartPosition.CenterScreen;
        var layout=new TableLayoutPanel { Dock=DockStyle.Fill, Padding=new Padding(20), ColumnCount=1, RowCount=5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text="OXP3 Game Power", Font=new Font("Segoe UI",18), AutoSize=true });
        layout.Controls.Add(new Label { Text="Intel ONEXPLAYER 3 · ONEXConsole 0.10.3-fix2", AutoSize=true, Margin=new Padding(0,8,0,12) });
        layout.Controls.Add(status);
        output.Margin=new Padding(0,12,0,12); layout.Controls.Add(output);
        var actions=new FlowLayoutPanel { AutoSize=true, Dock=DockStyle.Fill }; actions.Controls.Add(install); actions.Controls.Add(remove); layout.Controls.Add(actions); Controls.Add(layout);
        install.Click += async delegate { await Execute("Install"); };
        remove.Click += async delegate { if(MessageBox.Show("Remove the widget and restore normal ONEXConsole startup? Saved wattages will be retained.",Text,MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes) await Execute("Remove"); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if(busy) e.Cancel=true; };
        if(startRemove) Shown += delegate { remove.PerformClick(); };
    }
    private async Task Execute(string mode)
    {
        busy=true; install.Enabled=false; remove.Enabled=false; output.Clear(); status.Text=mode=="Remove" ? "Removing..." : "Installing...";
        try
        {
            SetupProgram.VerifyBundle();
            int exit=await SetupProgram.RunEngine(mode, s => { if(!IsDisposed) BeginInvoke(new Action(() => output.AppendText(s+Environment.NewLine))); });
            status.Text=exit==0 ? (mode=="Remove" ? "Removed" : "Installed") : "Setup needs attention — see details below";
        }
        catch(Exception error) { output.AppendText(error.Message); status.Text="Setup needs attention"; }
        finally { busy=false; install.Enabled=true; remove.Enabled=true; }
    }
}
