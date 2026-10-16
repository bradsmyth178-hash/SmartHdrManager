using System;

namespace SmartHdrManager;

// Pure state machine: no platform calls, UI or filesystem. Tested with injected observations and writes.
public sealed class DisplayLease
{
    public bool? Previous { get; set; }
    public bool? Written { get; set; }
    public bool Blocked { get; set; }
    public string Reason { get; set; } = "Idle";
    public long LastAttempt { get; set; } = -10000;
    public bool? Desired { get; private set; }
    public bool? Actual { get; private set; }
    public int RestoreAttempts { get; set; }

    public void Reconcile(bool? actual, bool? desired, long now, Func<bool, bool> write, Action checkpoint)
    {
        Actual = actual; Desired = desired;
        if (actual == null) { Reason = "Unavailable / unsupported / query failed"; return; }
        if (Written != null && actual != Written) {
            Previous = Written = null; Blocked = true; Reason = "External change: automation yields"; checkpoint();
        }
        if (desired == null) {
            if (Written != null && Previous != null && actual == Written && actual != Previous) {
                if (RestoreAttempts >= 3) { Reason = "Restore retry limit reached; explicit recovery required"; return; }
                if (now - LastAttempt < 3000) return;
                LastAttempt = now;
                RestoreAttempts++;
                if (!write(Previous.Value)) { Reason = "Restore failed; ownership retained"; checkpoint(); return; }
            }
            Previous = Written = null; Blocked = false; RestoreAttempts = 0; Reason = "Released"; checkpoint(); return;
        }
        if (Blocked) return;
        if (actual == desired) { Reason = Written != null ? "Owned / verified" : "Already correct / not owned"; return; }
        if (now - LastAttempt < 3000) return;
        LastAttempt = now;
        Previous ??= actual;
        Written = desired;
        checkpoint(); // write-ahead recovery: restart never guesses whether an interrupted write succeeded
        if (!write(desired.Value)) { Blocked = true; Reason = "Write/readback failed; paused until release"; checkpoint(); return; }
        Actual = desired; Reason = "Owned / verified"; checkpoint();
    }
}
