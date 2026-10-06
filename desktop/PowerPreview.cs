using System.Text.Json;

namespace Oxp3GamePower.Desktop;

// Read-only UI fixtures. Never connect to the helper or change device settings.
internal static class PowerPreview
{
    internal static Func<object, Task<JsonElement>> Requests(string state)
    {
        if (state is not ("available" or "unavailable" or "restart-required")) throw new ArgumentException("Unknown power preview state.");
        return request =>
        {
            var command = JsonSerializer.SerializeToElement(request).GetProperty("command").GetString();
            if (command == "getDisplayState") return Task.FromResult(JsonSerializer.SerializeToElement(new { brightnessAvailable = true, brightness = 69, nightLightAvailable = true, nightLight = false, nightLightStrength = 50 }));
            if (command != "getState") throw new InvalidOperationException("This preview does not apply changes.");
            bool connected = state == "available";
            return Task.FromResult(JsonSerializer.SerializeToElement(new { connected, restartRequired = state == "restart-required", nativeWatts = connected ? (int?)15 : null, minWatts = 4, maxWatts = 35, playingGame = (object?)null, usePerGame = false }));
        };
    }
}
