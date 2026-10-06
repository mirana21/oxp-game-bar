namespace Oxp3GamePower.Desktop;

internal static class ControllerTests
{
    internal static void Run()
    {
        var edges = new ControllerInput.InputEdges();
        ControllerInput.Gamepad pad = default;
        void Expect(long time, params InputAction[] expected)
        {
            if (!edges.Read(pad, time).SequenceEqual(expected)) throw new Exception("Unexpected controller action at " + time);
        }
        // Focus gained with A held must not save anything, even after a long hold.
        pad.Buttons = 0x1000; Expect(0); Expect(5000);
        pad.Buttons = 0; Expect(5001);
        pad.Buttons = 0x1000; Expect(5002, InputAction.Select); Expect(5003); Expect(9000);
        pad.Buttons = 0; Expect(9001);
        pad.Buttons = 8; Expect(9002, InputAction.Right); Expect(9401); Expect(9402, InputAction.Right); Expect(9511); Expect(9512, InputAction.Right);
        pad.Buttons = 4; Expect(9513, InputAction.Left);
        // Switching away and back with direction held must wait for release.
        edges.Reset(); Expect(9600); Expect(10000);
        pad.Buttons = 0; Expect(10001);
        pad.LeftX = 7000; Expect(10002); // Stick drift remains inside the dead zone.
        pad.LeftX = 22000; Expect(10003, InputAction.Right);
        pad.LeftX = 0; Expect(10004);
        pad.Buttons = 0x1008; Expect(10005, InputAction.Select); // A + right must not also change watts.
        pad.Buttons = 0; Expect(10006);
        pad.Buttons = 0x2000; Expect(10007, InputAction.Back);
        pad.Buttons = 0; Expect(10008);
        pad.Buttons = 0x10; Expect(10009, InputAction.Settings);
        Console.WriteLine("PASS: held-button focus guard, confirmation edges, direction repeat, dead zone, back and settings. XInput controllers connected: " + ControllerInput.ConnectedCount());
    }
}
