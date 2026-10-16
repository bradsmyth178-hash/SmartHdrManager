using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SmartHdrManager
{
    /// <summary>
    /// Owns the tray icon, the global hotkeys, the background engine and (only while it is open) the settings window.
    /// There is no ApplicationContext.MainForm, so closing the window never ends the process; only "Exit" does.
    /// </summary>
    internal sealed class TrayAppContext : ApplicationContext
    {
        private const int DisposeHiddenWindowAfterMs = 5000;

        private readonly HdrEngine _engine;
        private readonly EventWaitHandle _showEvent;
        private readonly CommandEvents _commands;
        private readonly TrayAndStartupManager _tray;
        private GlobalHotkeys? _hotkeys;
        private readonly System.Windows.Forms.Timer _timer;

        private MainForm? _form;
        private long _formHiddenAt;
        private bool _balloonShown;
        private bool _exiting;
        private string _lastTooltip = string.Empty;

        public TrayAppContext(HdrEngine engine, EventWaitHandle showEvent, CommandEvents commands, CliCommand startCommand, bool startHidden)
        {
            _engine = engine;
            _showEvent = showEvent;
            _commands = commands;

            _tray = new TrayAndStartupManager(ShowMain, SetPaused, Exit, SetOverride, GetOverride, () => Shortcut('O'));

            ApplyHotkeys();

            // Re-read the HDR state right away when monitors change or the PC wakes up
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            _timer = new System.Windows.Forms.Timer { Interval = 500 };
            _timer.Tick += OnTick;
            _timer.Start();

            _engine.Start();
            if (startCommand != CliCommand.None) RunCommand(startCommand);
            if (!startHidden) ShowMain();
        }

        /// <summary>
        /// Command-line actions (--force-hdr, --force-sdr, --auto, --toggle-pause), e.g. for Stream Deck buttons,
        /// desktop shortcuts or scripts. A second launch only signals the running instance and exits.
        /// </summary>
        private void RunCommand(CliCommand command)
        {
            _engine.Log("MANUAL", $"Command line action: {command}.");
            switch (command)
            {
                case CliCommand.ForceHdr:
                    SetOverride(HdrOverride.ForceHdr, null);
                    break;
                case CliCommand.ForceSdr:
                    SetOverride(HdrOverride.ForceSdr, null);
                    break;
                case CliCommand.Auto:
                    SetOverride(HdrOverride.None, null);
                    break;
                case CliCommand.TogglePause:
                    SetPaused(!_engine.Paused);
                    break;
            }
        }

        // The settings window only exists while it is open. When hidden it is disposed after a few seconds,
        // which frees its memory and window handles while the app sits in the tray.
        private void ShowMain()
        {
            if (_exiting) return;
            if (_form == null || _form.IsDisposed)
            {
                _form = new MainForm(_engine, SetPaused, SetOverride, ApplyHotkeys);
                _form.HiddenToTray += OnFormHidden;
            }
            _form.ShowFromTray();
        }

        // ---- keyboard shortcuts (Win+Alt or Ctrl+Alt + H / I / O; can be changed or switched off in Settings) ----
        private string HotkeyMode => _engine.Read(c => HotkeySettings.Normalize(c.HotkeyModifier));

        private string Shortcut(char key) => HotkeySettings.Describe(HotkeyMode, key);

        /// <summary>(Re-)registers the shortcuts with the current setting. Falls back to polling for any that are taken.</summary>
        private void ApplyHotkeys()
        {
            _hotkeys?.Dispose();
            _hotkeys = null;

            string mode = HotkeyMode;
            if (mode == HotkeySettings.Off)
            {
                _engine.PollHotkeys = false;
                _engine.PollOverrideHotkey = false;
                _engine.Log("INFO", "Keyboard shortcuts are off.");
                return;
            }

            try
            {
                _hotkeys = new GlobalHotkeys(HotkeySettings.ModifierFlags(mode), _engine.HotkeyAddCurrent, _engine.HotkeyIgnoreCurrent, OnOverrideHotkey);
                _engine.PollHotkeys = !_hotkeys.Registered;
                _engine.PollOverrideHotkey = !_hotkeys.OverrideRegistered;
                _engine.Log("INFO", $"Shortcuts: {Shortcut('H')} use HDR for the game in front, {Shortcut('I')} never HDR, {Shortcut('O')} HDR override.");
                if (!_hotkeys.Registered)
                    _engine.Log("INFO", $"{Shortcut('H')} / {Shortcut('I')} are used by another program – using keyboard polling instead.");
                if (!_hotkeys.OverrideRegistered)
                    _engine.Log("INFO", $"{Shortcut('O')} is used by another program – using keyboard polling instead.");
            }
            catch (Exception ex)
            {
                _engine.PollHotkeys = true;
                _engine.PollOverrideHotkey = true;
                _engine.Log("ERROR", $"Shortcuts could not be registered ({ex.Message}) – using keyboard polling instead.");
            }
        }

        // ---- manual override (tray menu + shortcut) ----
        private void SetOverride(HdrOverride mode, TimeSpan? duration)
        {
            _engine.SetOverride(mode, duration);
            if (_form != null && !_form.IsDisposed && _form.Visible) _form.RefreshView();
        }

        private (HdrOverride Mode, bool Timed) GetOverride()
        {
            var s = _engine.Status;
            return (_engine.OverrideMode, s.OverrideRemainingMs >= 0);
        }

        private void OnOverrideHotkey()
        {
            var mode = _engine.CycleOverride();
            System.Media.SystemSounds.Asterisk.Play();
            _tray.ShowBalloon("HDR override", mode switch
            {
                HdrOverride.ForceHdr => $"HDR forced ON until you turn it back ({Shortcut('O')} again: SDR).",
                HdrOverride.ForceSdr => $"SDR forced until you turn it back ({Shortcut('O')} again: automatic).",
                _ => "Back to automatic HDR switching."
            });
            if (_form != null && !_form.IsDisposed && _form.Visible) _form.RefreshView();
        }

        private void OnFormHidden(object? sender, EventArgs e)
        {
            _formHiddenAt = Environment.TickCount64;
            if (_balloonShown) return;
            _balloonShown = true;
            _tray.ShowBalloon("Smart HDR Manager", "Still running in the system tray. Click the icon to open it again.");
        }

        private void SetPaused(bool paused)
        {
            _engine.Paused = paused;
            _tray.SetPaused(paused);
            _engine.Log("PAUSE", paused ? "Automation PAUSED – HDR is no longer switched automatically." : "Automation ACTIVE again.");
            if (_form != null && !_form.IsDisposed && _form.Visible) _form.RefreshView();
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _engine.InvalidateHdrState();

        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                _engine.Log("INFO", "Resumed from sleep – re-checking the HDR state.");
                _engine.InvalidateHdrState();
            }
        }

        private void OnTick(object? sender, EventArgs e)
        {
            // A second launch asks the running instance to show its window
            if (_showEvent.WaitOne(0)) ShowMain();

            // ... or to run a command-line action (--force-hdr, --force-sdr, --auto, --toggle-pause)
            var command = _commands.TakeSignaled();
            if (command != CliCommand.None) RunCommand(command);

            // Only does anything if the app was built as a console exe (see ConsoleWindowHider)
            ConsoleWindowHider.HideIfMinimized();

            var s = _engine.Status;
            string state = s.Override == HdrOverride.ForceHdr ? "HDR forced on"
                : s.Override == HdrOverride.ForceSdr ? "SDR forced"
                : s.Paused ? "paused" : s.HdrOn ? "HDR on" : "HDR off";
            string tooltip = $"Smart HDR Manager – {state}";
            if (tooltip != _lastTooltip)
            {
                _lastTooltip = tooltip;
                _tray.UpdateTooltip(tooltip);
            }
            _tray.UpdateState(s.HdrOn, s.Paused && s.Override == HdrOverride.None);

            if (_form == null) return;

            if (_form.IsDisposed)
            {
                _form = null;
            }
            else if (_form.Visible)
            {
                _form.RefreshView();
            }
            else if (Environment.TickCount64 - _formHiddenAt > DisposeHiddenWindowAfterMs)
            {
                var form = _form;
                _form = null;
                form.HiddenToTray -= OnFormHidden;
                form.AllowExit();
                form.Dispose();
            }
        }

        private void Exit()
        {
            if (_exiting) return;
            _exiting = true;

            _timer.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _hotkeys?.Dispose();
            _engine.Stop();

            if (_form != null && !_form.IsDisposed)
            {
                _form.AllowExit();
                _form.Close();
                _form.Dispose();
            }
            _form = null;

            _tray.Dispose();
            ExitThread();
        }
    }

    internal enum CliCommand { None, ForceHdr, ForceSdr, Auto, TogglePause }

    /// <summary>Named events through which a second launch passes a command-line action to the running tray instance.</summary>
    internal sealed class CommandEvents : IDisposable
    {
        private readonly (CliCommand Command, EventWaitHandle Event)[] _events =
        {
            (CliCommand.ForceHdr, new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SmartHdrManager.ForceHdr")),
            (CliCommand.ForceSdr, new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SmartHdrManager.ForceSdr")),
            (CliCommand.Auto, new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SmartHdrManager.AutoHdr")),
            (CliCommand.TogglePause, new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SmartHdrManager.TogglePause"))
        };

        public void Signal(CliCommand command)
        {
            foreach (var (c, e) in _events)
                if (c == command) e.Set();
        }

        /// <summary>Returns one pending command (and consumes it), or None. Called twice a second by the tray timer.</summary>
        public CliCommand TakeSignaled()
        {
            foreach (var (c, e) in _events)
                if (e.WaitOne(0)) return c;
            return CliCommand.None;
        }

        public static CliCommand Parse(string[] args)
        {
            foreach (var a in args)
            {
                switch (a.Trim().ToLowerInvariant())
                {
                    case "--force-hdr": return CliCommand.ForceHdr;
                    case "--force-sdr": return CliCommand.ForceSdr;
                    case "--auto": return CliCommand.Auto;
                    case "--toggle-pause": return CliCommand.TogglePause;
                }
            }
            return CliCommand.None;
        }

        public void Dispose()
        {
            foreach (var (_, e) in _events) e.Dispose();
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "--set-hdr") {
                using var manualMutex = new Mutex(true, @"Local\SmartHdrManager.SingleInstance", out bool available);
                if (!available || !bool.TryParse(args[2], out bool value)) { Environment.ExitCode = 2; return; }
                Environment.ExitCode = HdrController.SetVerified(args[1], value) ? 0 : 1; return;
            }
            if (args.Length == 3 && args[0] == "--hardware-smoke") {
                using var testMutex = new Mutex(true, @"Local\SmartHdrManager.SingleInstance", out bool available);
                if (!available) throw new InvalidOperationException("Exit HDR Manager before the explicit hardware smoke test.");
                HardwareSmoke.Run(args[1], args[2]); return;
            }
            if (args.Length == 2 && args[0] == "--inspect") {
                SafeGamesContext.Atomic(args[1], HdrController.Inspect()); return;
            }
            if (args.Length == 2 && args[0] == "--scan-manifests") {
                SafeGamesContext.Atomic(args[1], GameLibraryScanner.Scan(null)); return;
            }
            if (!args.Contains("--legacy-engine")) {
                using var safeMutex = new Mutex(true, @"Local\SmartHdrManager.SingleInstance", out bool first);
                // An exit request with no owner must not poison the next startup.
                if (first && args.Contains("--exit")) return;
                if (args.Length == 2 && args[0] == "--launch") SafeGamesContext.Queue(args[1]);
                if (args.Length == 1 && args[0] is "--exit" or "--pause" or "--resume" or "--show" or "--recover" or "--hdr" or "--sdr" or "--auto") SafeGamesContext.Queue(args[0]);
                if (!first) { if (args.Length == 0) SafeGamesContext.Queue("--show"); return; }
                if (args.Contains("--exit")) return;
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var safe = new SafeGamesContext(args.Contains("--tray") || args.Contains("--launch"));
                Application.Run(safe); return;
            }
            var command = CommandEvents.Parse(args);
            // A command-line action never opens the window: the app (re)starts quietly in the tray
            bool startHidden = command != CliCommand.None || args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

            using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SmartHdrManager.ShowWindow");
            using var commands = new CommandEvents();
            using var mutex = new Mutex(true, @"Local\SmartHdrManager.SingleInstance", out bool isFirstInstance);
            if (!isFirstInstance)
            {
                if (command != CliCommand.None) commands.Signal(command);   // the running instance applies it
                else if (!startHidden) showEvent.Set();                    // bring the running instance's window to the front
                return;
            }

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);   // physical pixels: needed for the fullscreen check
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Errors must never pop up dialogs or vanish silently while the app lives in the tray: write them to the log file.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => HdrEngine.WriteFileLog($"ERROR  UI exception: {e.Exception}");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => HdrEngine.WriteFileLog($"FATAL  {e.ExceptionObject}");

            // Built as console exe? Take the console out of the taskbar/Alt-Tab (no-op for WinExe).
            if (ConsoleWindowHider.HasConsole)
                ConsoleWindowHider.HideCompletely();

            var engine = new HdrEngine();
            using var context = new TrayAppContext(engine, showEvent, commands, command, startHidden);
            Application.Run(context);
        }
    }
}
