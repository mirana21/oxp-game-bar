using System.Text.Json;

namespace Oxp3GamePower.Desktop;

// Isolated UI fixtures. Never connect to the helper or change device settings.
internal static class PowerPreview
{
    internal static Func<object, Task<JsonElement>> Requests(string state)
    {
        if (state is not ("available" or "unavailable" or "restart-required")) throw new ArgumentException("Unknown power preview state.");
        bool keepAwake = false, hdr = false, night = false;
        int watts = 15, brightness = 69, strength = 50;
        return request =>
        {
            var json = JsonSerializer.SerializeToElement(request);
            var command = json.GetProperty("command").GetString();
            if (command == "setKeepAwake") keepAwake = json.GetProperty("enabled").GetBoolean();
            if (command is "getAwakeState" or "setKeepAwake") return Task.FromResult(JsonSerializer.SerializeToElement(new { keepAwake }));
            if (command == "setHdr") hdr = json.GetProperty("enabled").GetBoolean();
            if (command == "setNightLight") night = json.GetProperty("enabled").GetBoolean();
            if (command == "setNightLightStrength") strength = json.GetProperty("strength").GetInt32();
            if (command == "setBrightness") brightness = json.GetProperty("brightness").GetInt32();
            if (command is "getDisplayState" or "setHdr" or "setNightLight" or "setNightLightStrength" or "setBrightness") return Task.FromResult(JsonSerializer.SerializeToElement(new { brightnessAvailable = true, brightness, nightLightAvailable = true, nightLight = night, nightLightStrength = strength, hdrAvailable = true, hdr }));
            if (command == "adjustWatts") { watts = json.GetProperty("watts").GetInt32(); return Task.FromResult(JsonSerializer.SerializeToElement(new { nativeWatts = watts })); }
            if (command != "getState") throw new InvalidOperationException("This preview does not apply changes.");
            bool connected = state == "available";
            return Task.FromResult(JsonSerializer.SerializeToElement(new { connected, restartRequired = state == "restart-required", nativeWatts = connected ? (int?)watts : null, minWatts = 4, maxWatts = 35, playingGame = (object?)null, usePerGame = false }));
        };
    }
}
