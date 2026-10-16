# Safe games fork

This branch defaults to per-display game automation. See [safe-mode setup and limitations](SAFE-GAMES.md) and the [upstream audit](AUDIT.md). The original documentation below describes the legacy engine, not the default configuration.

# Smart HDR Manager

Turns Windows HDR on automatically when you need it – HDR games, HDR videos on YouTube, local HDR movies and (optionally) SDR videos with NVIDIA RTX Video HDR – and back to SDR when you are done. It lives in the system tray.

   ![Smart HDR Manager main window](docs/main.png)

> **Beta.** Tested on Windows 11 25H2 with an NVIDIA GeForce RTX 5070 Ti and one HDR monitor (ASUS PG32UCDMR).
> Not tested yet: AMD and Intel graphics, Windows on ARM (Snapdragon), Windows 10 / Windows 11 24H2 and older, several HDR monitors, laptops.
> If something does not work on your PC, please report it (see *Reporting a problem*).

   ☕ **Like the app?** You can support development on [Ko-fi](https://ko-fi.com/vantacode420).

---

## Download – which file?

| File | Size | Use it if … |
|---|---|---|
| `SmartHdrManager-v…-win-x64-standalone.zip` | large | **You are not sure.** Runs on any 64-bit Windows 10/11, nothing else to install. |
| `SmartHdrManager-v…-win-x64.zip` | small | You already have the **.NET 10 Desktop Runtime** installed ([download](https://dotnet.microsoft.com/download/dotnet/10.0) → ".NET Desktop Runtime", x64). |
| `SmartHdrManager-v…-win-arm64-standalone.zip` | large | *Experimental, untested.* Windows on ARM (e.g. Snapdragon X laptops), nothing else to install. |
| `SmartHdrManager-v…-win-arm64.zip` | small | *Experimental, untested.* Windows on ARM with the .NET 10 Desktop Runtime **Arm64** installed. |

Not sure if your PC is ARM? *Settings › System › About › System type* says "ARM-based processor" on ARM PCs – everyone else takes x64.

Requirements: Windows 10 or 11 (64-bit) and an HDR-capable monitor with HDR available in *Windows Settings › Display*.

## Installation

1. Unzip to a permanent folder, e.g. `C:\Tools\Smart HDR Manager`. (Don't run it from inside the zip or from Downloads – "Start with Windows" remembers the location.)
2. Start `SmartHdrManager.exe`.
3. **"Windows protected your PC"?** The app is not code-signed (signing costs money every year), so Windows does not know it yet. Click **More info › Run anyway**. You can check the file first: compare it with `SHA256SUMS.txt` from the release, or look at the VirusTotal link in the release notes.
4. Optional: in **Settings**, turn on **Start with Windows**.

Settings are stored in `%AppData%\SmartHdrManager\config.json`, the log in `%LocalAppData%\SmartHdrManager\log.txt`.
**Portable mode:** put an empty file named `portable.txt` next to the exe, and the settings are kept in that folder instead.

## How it works

| HDR turns on when … | How it is detected |
|---|---|
| A game from your list is running | Games tab: games from Steam, Epic, GOG and Xbox are found automatically – switch HDR on per game. |
| A real HDR video plays on YouTube | The browser script marks the tab (see below). |
| A local HDR movie plays full screen | Video player (VLC, mpv, MPC-HC, PotPlayer) + "HDR", "Dolby Vision", "DoVi" or "BT2020" in the window title. |
| *(optional)* Any video plays – for **RTX Video HDR** | Settings › RTX Video HDR. Browsers via the script, video players while they play. |

When the content ends, the app waits a few seconds (Settings › *Return to SDR after*) and switches back to SDR.

**Games tab:** **Scan games** re-reads the game libraries and your drives. **Add program** / **Add running** adds any other program. **Options** sets a per-game delay before HDR (for games that crash or flicker if HDR switches during startup) and how long HDR stays on after the game closes. **Never HDR** removes a game for good.

**Tray menu:** open the window, pause automation, **HDR override** (force HDR on or SDR for a while), exit.

**Keyboard shortcuts** (Settings › Keyboard shortcuts – Win+Alt, Ctrl+Alt or off):
`+ H` use HDR for the game in front · `+ I` never HDR for it · `+ O` override: HDR on → SDR → automatic.

**Command line** (for Stream Deck buttons, shortcuts or scripts – a second launch only passes the command to the running app):
```
SmartHdrManager.exe --force-hdr      HDR on until changed back
SmartHdrManager.exe --force-sdr      SDR until changed back
SmartHdrManager.exe --auto           back to automatic switching
SmartHdrManager.exe --toggle-pause   pause / resume automation
SmartHdrManager.exe --tray           start hidden in the tray
```

![Settings](docs/settings.png)

## RTX Video HDR (NVIDIA RTX graphics cards)

NVIDIA RTX Video HDR turns SDR videos into HDR – but only while Windows HDR is on. With **Settings › RTX Video HDR › Switch to HDR for videos**, the app turns HDR on only while a video plays:

- **Smart** – HDR videos, plus SDR videos when RTX Video HDR is switched on in the NVIDIA App.
- **HDR only** – only real HDR videos. **Always** – every video.
- **When:** *Full screen* (recommended) or *Any* (also windowed).
- Only the monitor that shows the video is switched (it must be enabled under Settings › Monitors).
- Games always come first. While **OBS** is running, videos never switch HDR (it would disturb a recording). On battery power it is off unless you change that.

Turn RTX Video HDR on in the **NVIDIA App › System › Video › RTX Video Enhancements › HDR**. In MPC-HC, use *MPC Video Renderer* (0.8.1 or newer) and enable its "RTX Video HDR" option. The app only *reads* the RTX Video HDR setting; it never changes driver settings.

---

## Browser script (needed for YouTube HDR and browser videos)

The file `SmartHdrManager_YouTube.user.js` is a small Tampermonkey script. While a video plays, it adds a marker to the tab title:
` [real_hdr_active]` for real HDR YouTube videos, ` [sdr_video_active]` for other videos. Smart HDR Manager reacts to that marker.

On YouTube it also makes the site offer the HDR version of a video while Windows is still in SDR. On other sites it only adds the marker – it reads nothing, sends nothing and changes nothing else. It runs on all sites so that videos on any site can be detected.

### Chrome, Brave, Opera, Vivaldi

1. **Install Tampermonkey** from the Chrome Web Store: <https://chromewebstore.google.com/detail/tampermonkey/dhdgffkkebhmkfjojejmpbldmpobfkfo> → **Add to Chrome** → **Add extension**.
2. **Allow user scripts** (required since Chrome 138 – without it, no script runs):
   - Open `chrome://extensions` (Brave: `brave://extensions`, Opera: `opera://extensions`, Vivaldi: `vivaldi://extensions`).
   - Click **Details** under Tampermonkey.
   - Turn on **Allow User Scripts**.
   - *Don't see that switch?* Your browser is older: turn on **Developer mode** (top right of the extensions page) instead.
3. **Add the script:**
   - Click the puzzle icon in the toolbar → **Tampermonkey** → **Create a new script…**
   - In the editor, select everything (**Ctrl+A**) and delete it.
   - Open `SmartHdrManager_YouTube.user.js` with Notepad, copy everything (**Ctrl+A**, **Ctrl+C**) and paste it into the editor (**Ctrl+V**).
   - Press **Ctrl+S** to save. The script now appears as *enabled* in the Tampermonkey dashboard.
4. **Reload** tabs that were already open (or restart the browser).

### Microsoft Edge

1. Install Tampermonkey from the Edge Add-ons store: <https://microsoftedge.microsoft.com/addons/detail/tampermonkey/iikmkjmpaadaobahmlepeloendndfphd> → **Get**.
2. Open `edge://extensions`, click **Details** under Tampermonkey and turn on **Allow User Scripts**. If there is no such switch, turn on **Developer mode** (left side of the extensions page).
3. Add the script as in step 3 above, then reload open tabs.

### Check that it works

Open any YouTube video and hover over the browser tab: the title ends with **[sdr_video_active]** while it plays, or **[real_hdr_active]** for an HDR video (search YouTube for "4K HDR"). No marker? Check that the script is enabled in the Tampermonkey dashboard and that *Allow User Scripts* / *Developer mode* is on.

**Updating the script:** open the Tampermonkey dashboard, click the script, replace its text with the new file and save.

---

## Safety and privacy

- No internet connection, no telemetry, no ads. Everything stays on your PC.
- **Anti-cheat safe:** the app never reads or changes game memory and injects nothing. It only reads process names and window titles, like Task Manager does.
- Some antivirus programs may be suspicious of an unsigned tray app that watches running programs and has global keyboard shortcuts. The release notes link a VirusTotal scan of exactly these files.

## Known limitations

- RTX Video HDR status is read only; the app cannot switch it.
- Video players are detected by their audio output. A video file without an audio track is not detected in "RTX Video HDR" mode.
- On sites other than YouTube, every video counts as SDR (HDR can't be detected reliably there).
- Netflix, Disney+ and Apple TV are not supported yet.
- HDR switching takes 1–2 seconds and the screen may go black briefly – that's Windows/the monitor, not the app.

## Troubleshooting

- **HDR does not turn on:** look at the **Log** tab – every decision is written there. Check *Settings › Monitors* (is your HDR monitor enabled?).
- **Colours look wrong after HDR:** the log shows a `COLOR` line after every switch back to SDR, comparing the display before and after. A reset calibration / night-light curve is restored automatically.
- **A game crashes or flickers at start:** Games tab › Options › *Delay before HDR turns on* = 3–5 s.
- Program lists (browsers, video players, SDR-only apps, apps that block video HDR) can be edited in `config.json` (Settings › Open config.json, then *Reload config.json*).

## Reporting a problem

Please include: Windows version, graphics card, monitor, what you did, what happened – and the log file (Settings › **Open log file**, or `%LocalAppData%\SmartHdrManager\log.txt`).

## Uninstall

1. Settings › turn off **Start with Windows** (removes the startup entry).
2. Exit the app (tray icon › Exit) and delete its folder.
3. Optional: delete `%AppData%\SmartHdrManager` (settings) and `%LocalAppData%\SmartHdrManager` (log).
4. In the browser: remove the script in the Tampermonkey dashboard.

## License

MIT – see `LICENSE.txt`.
