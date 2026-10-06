using System;

namespace Oxp3.Controls
{
    // Shared by the Game Bar widget and the desktop test window. UI-thread only.
    internal sealed class DeferredSlider
    {
        internal long Revision { get; private set; }
        internal int? Pending { get; private set; }
        internal bool Writing { get; private set; }
        private DateTimeOffset due;
        internal bool Busy => Pending.HasValue || Writing;
        internal void Queue(int value, DateTimeOffset now)
        {
            Revision++; Pending = value; due = now.AddSeconds(1);
        }
        internal TimeSpan Delay(DateTimeOffset now) => due > now ? due - now : TimeSpan.FromMilliseconds(10);
        internal bool AcceptRead(long revision) => revision == Revision && !Busy;
        internal bool Begin(DateTimeOffset now, bool force, out int value)
        {
            value = 0;
            if (Writing || !Pending.HasValue || (!force && now < due)) return false;
            value = Pending.Value; Pending = null; Writing = true; return true;
        }
        // A failed older write must not discard a newer queued value.
        internal void Complete() { Writing = false; }
    }
}
