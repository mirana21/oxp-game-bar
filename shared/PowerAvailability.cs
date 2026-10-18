namespace Oxp3.Controls
{
    public static class PowerAvailability
    {
        public static string Message(bool connected, bool restartRequired)
        {
            if (connected) return "";
            return restartRequired
                ? "Restart ONEXConsole or Windows to reconnect power control."
                : "Power control unavailable. Check that ONEXConsole is running.";
        }
    }
}
