namespace Oxp3GamePower.Desktop;
public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        if (e.Args.Contains("--controller-self-test"))
        {
            try { ControllerTests.Run(); Shutdown(0); }
            catch (Exception error) { Console.Error.WriteLine(error); Shutdown(1); }
            return;
        }
        base.OnStartup(e);
        int preview = Array.IndexOf(e.Args, "--preview-power-state");
        if (preview >= 0 && preview + 1 < e.Args.Length)
        {
            MainWindow = new MainWindow(PowerPreview.Requests(e.Args[preview + 1])); MainWindow.Show(); return;
        }
        MainWindow = new MainWindow(); MainWindow.Show();
    }
}
