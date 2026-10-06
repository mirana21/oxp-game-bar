using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace Oxp3PowerWidget
{
    // This client has no hardware access and never edits ONEXConsole presets.
    // A packaged full-trust bridge authenticates the package SID and owns all native calls.
    internal sealed class BridgeClient
    {
        // This server is a full-trust desktop process. LOCAL would be rewritten
        // into the AppContainer's pipe namespace and miss the desktop endpoint.
        internal const string PipeName = "OXP3.PowerWidget.Bridge.v1";
        private const int MaximumReplyBytes = 4 * 1024 * 1024;
        private readonly SemaphoreSlim requests = new SemaphoreSlim(1, 1);

        public async Task<JsonObject> RequestAsync(string command, Action<JsonObject> arguments = null)
        {
            var request = new JsonObject();
            request.Add("command", JsonValue.CreateStringValue(command));
            if (arguments != null) arguments(request);
            using (var deadline = new CancellationTokenSource(command == "pickExe" ? 600000 : 25000))
            {
                await requests.WaitAsync(deadline.Token);
                try
                {
                    using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        try { await pipe.ConnectAsync(750, deadline.Token); }
                        catch (TimeoutException)
                        {
                            // The full-trust helper can exit independently of
                            // the still-loaded widget. Recover on the next poll
                            // without requiring a new Game Bar launch.
                            await App.StartHelperAsync();
                            await pipe.ConnectAsync(5000, deadline.Token);
                        }
                        byte[] requestBytes = Encoding.UTF8.GetBytes(request.Stringify() + "\n");
                        await pipe.WriteAsync(requestBytes, 0, requestBytes.Length, deadline.Token);
                        await pipe.FlushAsync(deadline.Token);

                        using (var bytes = new MemoryStream())
                        {
                            var buffer = new byte[4096];
                            bool completed = false;
                            while (!completed)
                            {
                                int count = await pipe.ReadAsync(buffer, 0, buffer.Length, deadline.Token);
                                if (count == 0) throw new IOException("The bridge closed before replying.");
                                int newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
                                int length = newline < 0 ? count : newline;
                                if (bytes.Length + length > MaximumReplyBytes)
                                    throw new IOException("The bridge reply is too large.");
                                bytes.Write(buffer, 0, length);
                                completed = newline >= 0;
                            }
                            var reply = JsonObject.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
                            if (!reply.GetNamedBoolean("ok", false))
                                throw new InvalidOperationException(reply.GetNamedString("error", "The bridge rejected the request."));
                            return reply.GetNamedObject("result", new JsonObject());
                        }
                    }
                }
                finally { requests.Release(); }
            }
        }
    }
}
