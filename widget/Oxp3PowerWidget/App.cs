using System;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Oxp3PowerWidget
{
    public sealed partial class App : Application
    {
        private XboxGameBarWidget widget;

        public App()
        {
#if !WIDGET_REFERENCE_CHECK
            InitializeComponent();
#endif
            RequestedTheme = ApplicationTheme.Dark;
            UnhandledException += (_, error) =>
            {
                // Preserve the actual WinRT/XAML failure for diagnosis. Do not
                // swallow it and leave a partially initialized power control.
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "activation-error.log"), DateTimeOffset.UtcNow.ToString("O") + " " + error.Message + Environment.NewLine + error.Exception + Environment.NewLine); } catch { }
            };
        }

        internal static async System.Threading.Tasks.Task StartHelperAsync()
        {
            await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            var protocol = args as IProtocolActivatedEventArgs;
            if (args.Kind != ActivationKind.Protocol || protocol == null ||
                !String.Equals(protocol.Uri.Scheme, "ms-gamebarwidget", StringComparison.OrdinalIgnoreCase))
                return;

            var activation = args as XboxGameBarWidgetActivatedEventArgs;
            if (activation == null || !activation.IsLaunchActivation) return;
            var frame = new Frame();
            Window.Current.Content = frame;
            widget = new XboxGameBarWidget(activation, Window.Current.CoreWindow, frame);
            frame.Content = new PowerPage(widget);
            Window.Current.Closed += (_, __) => widget = null;
            Window.Current.Activate();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            if (args.PrelaunchActivated) return;
            // This app is hosted only by the Game Bar Widgets menu.
            Exit();
        }
    }
}
