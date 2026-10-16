# Safe games mode (this fork)

This fork retains VantaCode420's MIT-licensed native display implementation and library code. The default entry point is a small games-only tray controller. The original UI/engine remains available with `--legacy-engine` for comparison; its video/foreground policies are not recommended for strict multi-monitor control. No video automation runs in safe mode.

## Build and install

Use the .NET 10 SDK on Windows:

```powershell
dotnet run --project tests/PolicyTests.csproj -c Release
dotnet publish SmartHdrManager.csproj -c Release -r win-x64 --self-contained true -o publish-safe
./Setup-Local.ps1 -ReleaseFolder ./publish-safe -DryRun
./Setup-Local.ps1 -ReleaseFolder ./publish-safe
```

Installation is per-user under `%LOCALAPPDATA%\Programs\SmartHdrManagerSafe`. Startup uses the user's Startup folder. PowerShell 7 is required for library rescanning. No service or elevation is used. The installer backs up the previous installation under the local data directory. Exit other HDR controllers first.

Configuration and diagnostics are local under `%LOCALAPPDATA%\SmartHdrManagerSafe`. Inspect available displays with `SmartHdrManager.exe --inspect displays.json`. Set `ManagedDisplay` to the exact device interface Key in `games.json`; the initial empty selection intentionally controls nothing. Populate `Roots` with chosen library folders, then run `Discover-Games.ps1 -DryRun` to preview or without DryRun to save and create Desktop/Games Library launch icons. The scanner preserves existing per-game policies and launch overrides. It does not scan all drives or take ownership of WindowsApps.

## Daily use

Launch using Games Library icons for HDR verification **before** game initialization. Ordinary launcher/Playnite launches use passive detection and cannot guarantee pre-initialization HDR availability. Existing Playnite entries and settings are preserved; this release does not install a Playnite plugin.

Double-click the tray icon for policies, display overrides, launch targets/arguments, enable flags and optional one-time window placement. Choose Native HDR only with verified support and enable HDR inside the game. Auto HDR is an explicit policy, not a compatibility detector or converter installer. Unknown makes no HDR request. SDR requests SDR while its window is on the managed screen. Concurrent native/Auto HDR sessions take priority over SDR on that screen. A program on an excluded display cannot demand a change.

Pause and restore, Resume, manual HDR/SDR, release override, Rescan, diagnostics and Exit and restore are in the tray menu. After rescan use Reload. Direct executable launch never requests elevation: applications requiring administrator rights report a launch failure. No security settings are modified.

Window placement is best effort using SetWindowPos, without changing primary monitor, display mode or desktop layout. Games can reposition or recreate their windows; exclusive fullscreen and game-specific output selectors require configuration in the game. Setting a display in the app cannot override every rendering engine.

## Ownership and safety

Each write resolves fresh QueryDisplayConfig paths, selects exactly one persisted device interface identity, rejects virtual/ambiguous/missing targets, and uses DisplayConfigSetDeviceInfo with explicit desired state and bounded readback. There is no keyboard HDR shortcut. Device/port identity changes require explicit reselection; there is no fallback to primary or all screens. Per-display query failures remain visible in diagnostics.

The worker serializes commands; transitions debounce for three seconds and release after five seconds without game demand. Overlapping sessions retain HDR. Prior state and write ownership are recorded before a write, and normal release/pause/exit restores only when the actual state still matches the app's last write. An observed manual change yields ownership. An external controller writing the same value cannot be distinguished by Windows state alone.

Crash journals pause startup for explicit recovery. A stopped process cannot restore instantly. Recovery compares the actual state and refuses ambiguous cases. A failed restoration retains the journal and stops after three attempts. Review local diagnostics before recovery. Resume cannot silently discard a crash journal.

Remote terminal sessions or active indirect/virtual displays suspend physical writes, including restoration. A running streaming host requires an explicitly configured local log containing a complete startup marker and balanced client events; unreadable/rotated/unrecognized logs suspend by default. The local-session override allows unknown evidence, never known active sessions or virtual displays. This log heuristic is conservative but cannot guarantee detection of every host version. Do not enable the override while streaming.

## Uninstall / rollback

```powershell
./Setup-Local.ps1 -Uninstall -DryRun
./Setup-Local.ps1 -Uninstall
```

This removes only startup/launch icons targeting this installation and retains binaries/configuration/backups. Restore a backed-up installation using Setup-Local with that directory as ReleaseFolder. Keep the original configuration backup when reverting. No game files or saves are removed.

## Validation and limits

The console suite uses injected observations and writes: it is simulated policy testing, not hardware certification. Native APIs, actual game launch/rendering, reconnect, sleep and remote streaming require local smoke tests. Do not infer HDR rendering from Windows HDR state or successful process launch. Library discovery is bounded and heuristic; stale manifests, generic executables, emulator launches and protected packages may need explicit review. No telemetry, cloud account or runtime service is added.

API references: [QueryDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig), [display device info types](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ne-wingdi-displayconfig_device_info_type), [SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos).
