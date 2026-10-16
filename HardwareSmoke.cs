using System;
using System.Linq;
using System.Threading;

namespace SmartHdrManager;
internal static class HardwareSmoke
{
    // Explicit opt-in diagnostic, never invoked by startup/discovery. Caller must establish no remote session.
    public static void Run(string key, string output)
    {
        var before = HdrController.Inspect();
        var target = before.Single(d => d.Key == key && d.QueryOk && d.Supported && !d.Virtual);
        if (before.Any(d => d.Virtual) || System.Windows.Forms.SystemInformation.TerminalServerSession)
            throw new InvalidOperationException("Hardware smoke test refused during virtual/remote desktop topology.");
        bool enabled = false, restored = false, othersUnchanged = false;
        try {
            enabled = HdrController.SetVerified(key, !target.Enabled);
            Thread.Sleep(1500);
            var after = HdrController.Inspect();
            othersUnchanged = before.Where(d => d.Key != key).All(d => after.Any(a => a.Key == d.Key && a.QueryOk && a.Enabled == d.Enabled));
        }
        finally {
            var current = HdrController.Inspect().SingleOrDefault(d => d.Key == key);
            // Do not overwrite a new external state. Restore only the value written by this test.
            if (current is { QueryOk: true } && current.Enabled != target.Enabled)
                restored = HdrController.SetVerified(key, target.Enabled);
            else restored = current is { QueryOk: true } && current.Enabled == target.Enabled;
            SafeGamesContext.Atomic(output, new { enabled, restored, othersUnchanged, before, after = HdrController.Inspect() });
        }
    }
}
