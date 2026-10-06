using System.Runtime.InteropServices;

namespace Oxp3GamePower.Desktop;
internal enum InputAction { Left, Right, Up, Down, Select, Back, Settings }

internal sealed class ControllerInput
{
    [StructLayout(LayoutKind.Sequential)] internal struct Gamepad { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LeftX, LeftY, RightX, RightY; }
    [StructLayout(LayoutKind.Sequential)] private struct State { public uint Packet; public Gamepad Pad; }
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint index, out State state);
    private readonly InputEdges edges = new();
    private uint? slot;
    public void Reset() { edges.Reset(); slot = null; }
    internal static int ConnectedCount()
    {
        int count = 0;
        for (uint i = 0; i < 4; i++) if (XInputGetState(i, out _) == 0) count++;
        return count;
    }
    public IEnumerable<InputAction> Poll()
    {
        for (uint i = 0; i < 4; i++)
        {
            if (XInputGetState(i, out var state) != 0) continue;
            if (slot != i) { edges.Reset(); slot = i; }
            return edges.Read(state.Pad, Environment.TickCount64);
        }
        Reset(); return [];
    }
    internal sealed class InputEdges
    {
        private bool primed, blocked;
        private ushort previous;
        private InputAction? direction;
        private long repeatAt;
        public void Reset() { primed = blocked = false; previous = 0; direction = null; }
        internal InputAction[] Read(Gamepad pad, long now)
        {
            ushort buttons = pad.Buttons;
            InputAction? next = (buttons & 4) != 0 ? InputAction.Left : (buttons & 8) != 0 ? InputAction.Right : (buttons & 1) != 0 ? InputAction.Up : (buttons & 2) != 0 ? InputAction.Down : null;
            if (!next.HasValue && Math.Max(Math.Abs((int)pad.LeftX), Math.Abs((int)pad.LeftY)) > 16000)
                next = Math.Abs((int)pad.LeftX) > Math.Abs((int)pad.LeftY) ? pad.LeftX < 0 ? InputAction.Left : InputAction.Right : pad.LeftY < 0 ? InputAction.Down : InputAction.Up;
            // A controller held when the window gains focus must be released
            // before it can write wattage, navigate, or repeat.
            if (!primed) { primed = true; blocked = buttons != 0 || next.HasValue; previous = buttons; return []; }
            if (blocked) { blocked = buttons != 0 || next.HasValue; previous = buttons; return []; }
            var actions = new List<InputAction>();
            ushort pressed = (ushort)(buttons & ~previous); previous = buttons;
            if ((pressed & 0x2000) != 0) actions.Add(InputAction.Back);
            else if ((pressed & 0x10) != 0) actions.Add(InputAction.Settings);
            else if ((pressed & 0x1000) != 0) actions.Add(InputAction.Select);
            if (actions.Count == 0 && next.HasValue && (next != direction || now >= repeatAt))
            { actions.Add(next.Value); repeatAt = now + (next != direction ? 400 : 110); }
            direction = next;
            return actions.ToArray();
        }
    }
}
