# Upstream audit at 19b0276

Inspected upstream source, README, MIT licence and recent history. HEAD is the reported 2026-10-15 revision 19b0276; recent changes include ARM64 builds and README testing corrections. Core.cs, Engine.cs and VideoPlayback.cs are present. No prior ZIP patch was used.

| Report | Finding at upstream revision | Fork disposition |
|---|---|---|
| 1 Explicit missing monitor falls back | Confirmed: Core.BuildMatcher falls through to primary/all when no selected key is connected. | Strict matching in Core; default safe controller independently requires one exact live target. |
| 2 Legacy browser title detection lacks monitor resolution | Confirmed: Engine's browser token path produces a requirement for configured targets. | Disabled by using games-only default; legacy path retained only behind explicit flag. |
| 3 Video ranks before excluding screens | Confirmed: VideoPlayback.Find returns highest ranked candidate; Engine.DetectVideo subsequently calls ResolveVideoMonitor and may discard it without considering another. | Video disabled in default mode; legacy video remains uncorrected and untested. |
| 4 Force-SDR editor can affect different monitor | Confirmed: foreground process membership affects Decide/VideoBlockReason independently of editor monitor. It does not necessarily override every active-game path. | Safe mode has no global foreground editor rule. Screen-scoped SDR game/rule demand has lower priority than active HDR sessions. |
| 5 Normal exit lacks prior HDR restoration | Confirmed: Engine.Stop stops thread and saves, without per-display HDR ownership restoration. | Safe DisplayLease restores on normal release, pause and exit; write-ahead journal covers incomplete restoration. |
| 6 Detection after process launch too late | Confirmed architectural limitation, including configured launch delay. | Games Library pre-launch verifies before Process.Start; ordinary external launches retain this limitation. |
| 7 Game targets configured monitors, not window host | Confirmed: process/foreground requirements feed Target(s). | Full executable-path sessions resolve current window display. Optional best-effort window placement; no guarantee against a game's own output selector. |
| 8 Partial HDR query failures obscured | Confirmed aggregate query skipped failed target queries; callers can also collapse unavailable into false. | Selected aggregate query now fails; safe diagnostics retain QueryOk/Supported/Enabled separately and block writes on unknown observations. |

None of these reports was already fixed in the inspected upstream HEAD. The default controller supersedes unsafe policy paths rather than changing the legacy video subsystem. The old engine is not covered by the new state-machine tests and should not be used as the safe multi-monitor configuration.

Additional fixes found during local validation: native HDR changes execute on a serialized worker, not the WinForms UI thread; refresh observations after pre-launch writes before ownership reconciliation; do not queue an exit for a nonexistent instance; do not discard recovery journal on Resume. Native calls remain subject to driver behavior and cannot guarantee a bounded driver call duration, though readback/retry loops are bounded.
