// The supported UWP build generates its entry point and dispatcher context from App.xaml.
// This entry point exists only for the standalone reference compilation check.
#if WIDGET_REFERENCE_CHECK
using System;
using System.Threading;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Oxp3PowerWidget
{
    internal static class Program
    {
        [MTAThread]
        private static void Main(string[] args)
        {
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherContext(Window.Current.Dispatcher));
                new App();
            });
        }

        private sealed class DispatcherContext : SynchronizationContext
        {
            private readonly CoreDispatcher dispatcher;
            public DispatcherContext(CoreDispatcher dispatcher) { this.dispatcher = dispatcher; }
            public override void Post(SendOrPostCallback callback, object state)
            {
                var operation = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => callback(state));
            }
            public override SynchronizationContext CreateCopy() { return new DispatcherContext(dispatcher); }
        }
    }
}
#endif
