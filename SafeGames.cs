using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmartHdrManager;

public sealed class GamePolicy
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string InstallPath { get; set; } = "";
    public string Launch { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string Icon { get; set; } = "";
    public List<string> Executables { get; set; } = new();
    public string Policy { get; set; } = "Unknown";
    public string Confidence { get; set; } = "Unverified; leave display unchanged";
    public string Display { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool MoveToDisplay { get; set; } = true;
    public string Notes { get; set; } = "";
}
public sealed class SafeConfiguration
{
    public string ManagedDisplay { get; set; } = "";
    public List<string> Roots { get; set; } = new();
    public List<GamePolicy> Games { get; set; } = new();
    public bool AllowPhysicalWhileStreamingHostRuns { get; set; }
    public string StreamingLog { get; set; } = "";
}

internal sealed class SafeGamesContext : ApplicationContext
{
    internal static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartHdrManagerSafe");
    internal static readonly string ConfigFile = Path.Combine(Data, "games.json");
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private SafeConfiguration config;
    private readonly DisplayLease lease = new();
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly Dictionary<string, long> pending = new();
    private readonly Dictionary<string, int> pendingDirect = new();
    private bool paused;
    private bool recovery;
    private bool? manual;
    private long releaseAt;
    private string status = "Starting";
    private string requester = "";
    private string lastStatus = "";
    private Form? window;
    private Label? statusLabel;
    private readonly Control dispatcher = new();
    private bool busy;
    private bool exiting;
    private string lastLaunchResult = "";
    private string lastLaunchedId = "";
    private bool lastLaunchObserved;
    private readonly Dictionary<string, (string Display, int Attempts)> placements = new();
    private void Ui(Action action) { if (!dispatcher.IsDisposed) dispatcher.BeginInvoke(action); }

    public SafeGamesContext(bool hidden)
    {
        _ = dispatcher.Handle;
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Path.Combine(Data, "requests"));
        config = Load();
        string journal = Path.Combine(Data, "recovery.json");
        if (File.Exists(journal)) {
            // A previous process may have died while another controller was active. Never replay automatically.
            recovery = true; paused = true;
            status = "Recovery journal found: inspect diagnostics; resume explicitly. No HDR writes made.";
        }
        tray = new NotifyIcon { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            Visible = true, Text = "Smart HDR Manager — safe games" };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Games, policies and display status", null, (_, _) => Show());
        menu.Items.Add("Pause and restore", null, (_, _) => Queue("--pause"));
        menu.Items.Add("Resume automation", null, (_, _) => Queue("--resume"));
        menu.Items.Add("Request HDR (managed display)", null, (_, _) => Queue("--hdr"));
        menu.Items.Add("Request SDR (managed display)", null, (_, _) => Queue("--sdr"));
        menu.Items.Add("Release manual override and restore", null, (_, _) => Queue("--auto"));
        menu.Items.Add("Rescan game libraries", null, (_, _) => Rescan());
        menu.Items.Add("Open local diagnostics", null, (_, _) => Open(Data));
        menu.Items.Add("Recover previous state (review journal first)", null, (_, _) => Queue("--recover"));
        menu.Items.Add("Exit and restore", null, (_, _) => Queue("--exit"));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Show();
        timer.Tick += async (_, _) => {
            if (busy || exiting) return;
            busy = true;
            try { await Task.Run(Tick); } catch (Exception e) { status = "Paused operation: " + e.Message; }
            finally { busy = false; }
        };
        timer.Start();
        if (!hidden) Show();
    }
    private static SafeConfiguration Load() => File.Exists(ConfigFile)
        ? JsonSerializer.Deserialize<SafeConfiguration>(File.ReadAllText(ConfigFile), Json) ?? new() : new();
    internal static void Atomic(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid() + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void Checkpoint()
    {
        string file = Path.Combine(Data, "recovery.json");
        if (lease.Written == null) { if (!recovery && File.Exists(file)) File.Delete(file); return; }
        Atomic(file, new { Display = config.ManagedDisplay, lease.Previous, lease.Written, lease.Reason });
    }
    private static bool HostRunning()
    {
        foreach (string n in new[] { "sunshine", "apollo", "VibePollo" }) {
            var processes = Process.GetProcessesByName(n);
            bool found = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            if (found) return true;
        }
        return SystemInformation.TerminalServerSession;
    }
    private bool StreamingBlocked(List<LiveDisplay> displays)
    {
        if (displays.Any(d => d.Virtual) || SystemInformation.TerminalServerSession) return true;
        if (!HostRunning()) return false;
        StreamState evidence = StreamState.Unknown;
        try {
            if (!string.IsNullOrEmpty(config.StreamingLog)) {
                using var stream = new FileStream(config.StreamingLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length <= 262144) { using var reader = new StreamReader(stream); evidence = StreamingEvidence.Parse(reader.ReadToEnd()); }
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return evidence == StreamState.Active || (evidence == StreamState.Unknown && !config.AllowPhysicalWhileStreamingHostRuns);
    }
    private void Tick()
    {
        var displays = HdrController.Inspect();
        var matches = displays.Where(d => d.Key.Equals(config.ManagedDisplay, StringComparison.OrdinalIgnoreCase)).ToArray();
        var d = matches.Length == 1 ? matches[0] : null;
        bool? actual = d is { Virtual: false, QueryOk: true, Supported: true } ? d.Enabled : null;
        bool blocked = StreamingBlocked(displays);
        long now = Environment.TickCount64;
        // A streaming host can change topology/HDR itself. Yield without even restoring while it may be active.
        if (blocked) {
            status = "Suspended: streaming host/virtual display present. Physical HDR unchanged.";
            DrainRequests(false); UpdateStatus(displays); return;
        }
        DrainRequests(!paused && !recovery);
        if (exiting) return;
        // Commands can write HDR. Never compare a pre-command observation with a newly acquired lease.
        displays = HdrController.Inspect();
        matches = displays.Where(x => x.Key.Equals(config.ManagedDisplay, StringComparison.OrdinalIgnoreCase)).ToArray();
        d = matches.Length == 1 ? matches[0] : null;
        actual = d is { Virtual: false, QueryOk: true, Supported: true } ? d.Enabled : null;
        var active = new List<GamePolicy>();
        var alive = new HashSet<string>();
        bool lastLaunchAlive = false;
        var names = config.Games.Where(g => g.Enabled).SelectMany(g => g.Executables)
            .Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses()) {
            using (p) try {
                if (!names.Contains(p.ProcessName)) continue;
                string? path = p.MainModule?.FileName;
                var game = config.Games.FirstOrDefault(g => g.Enabled && g.Executables.Contains(path ?? "", StringComparer.OrdinalIgnoreCase));
                if (game == null) continue;
                if (game.Id == lastLaunchedId) lastLaunchAlive = true;
                string session = p.Id + ":" + p.StartTime.ToUniversalTime().Ticks;
                alive.Add(session);
                placements.TryGetValue(session, out var placement);
                string? located = HdrController.WindowDisplay(p.MainWindowHandle);
                if (p.MainWindowHandle != IntPtr.Zero) {
                    pending.Remove(game.Id); pendingDirect.Remove(game.Id);
                    if (!paused && !recovery && game.MoveToDisplay && placement.Attempts < 3 && placement.Display == null && located != game.Display) {
                        HdrController.PlaceGameWindow(p.MainWindowHandle, game.Display);
                        placement.Attempts++;
                        located = HdrController.WindowDisplay(p.MainWindowHandle);
                    }
                    if (located == game.Display) placement.Display = located;
                    placements[session] = placement;
                } else located = placement.Display;
                if (game.Id == lastLaunchedId && p.MainWindowHandle != IntPtr.Zero) {
                    lastLaunchObserved = true;
                    lastLaunchResult = located == game.Display
                        ? game.Name + ": window verified on selected display"
                        : game.Name + ": window is on another or unidentified display; select the intended monitor in the game's video settings";
                }
                if (game.Display != config.ManagedDisplay || located != game.Display) continue;
                active.Add(game);
            } catch (System.ComponentModel.Win32Exception) { } catch (InvalidOperationException) { }
        }
        foreach (string dead in placements.Keys.Where(k => !alive.Contains(k)).ToArray()) placements.Remove(dead);
        if (lastLaunchObserved && !lastLaunchAlive) { lastLaunchResult = "Game session ended"; lastLaunchObserved = false; lastLaunchedId = ""; }
        foreach (var id in pending.Where(kv => kv.Value < now).Select(kv => kv.Key).ToArray()) pending.Remove(id);
        foreach (var entry in pendingDirect.ToArray()) {
            try { using var p = Process.GetProcessById(entry.Value); if (!p.HasExited) continue; } catch (ArgumentException) { }
            pending.Remove(entry.Key); pendingDirect.Remove(entry.Key);
        }
        bool? desired = paused || recovery ? null : manual ?? GameDemand.Decide(config.ManagedDisplay,
            active.Select(g => new GameObservation(g.Display, g.Policy, g.Enabled)), blocked, paused, pending.Count > 0);
        requester = string.Join(", ", active.Select(g => g.Name));
        if (pending.Count > 0) requester = "Pre-launch reservation";
        if (desired != null) releaseAt = now + 5000;
        else if (!paused && !recovery && now < releaseAt && lease.Written != null) desired = lease.Written;
        lease.Reconcile(actual, desired, now, v => HdrController.SetVerified(config.ManagedDisplay, v), Checkpoint);
        status = $"{d?.Name ?? "Selected display unavailable"}: actual={actual?.ToString() ?? "unknown"}, desired={desired?.ToString() ?? "none"}; {lease.Reason}; {requester}; {HdrController.LastError}";
        UpdateStatus(displays);
    }
    private void UpdateStatus(List<LiveDisplay> displays)
    {
        Ui(() => { if (statusLabel != null && !statusLabel.IsDisposed) statusLabel.Text = status + "\n" + lastLaunchResult; });
        string signature = status + lastLaunchResult + JsonSerializer.Serialize(displays);
        if (signature == lastStatus) return;
        lastStatus = signature;
        Atomic(Path.Combine(Data, "status.json"), new { status, requester, paused, recovery, lastLaunchResult, lease.Previous, lease.Written, displays });
    }
    private void DrainRequests(bool permitted)
    {
        foreach (var file in Directory.GetFiles(Path.Combine(Data, "requests"), "*.json")) {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromMinutes(2)) { File.Delete(file); continue; }
            string id = JsonSerializer.Deserialize<string>(File.ReadAllText(file)) ?? "";
            File.Delete(file);
            if (id == "--show") { Ui(Show); continue; }
            if (id == "--exit") { exiting = true; pending.Clear(); manual = null; Release(); Ui(ExitThread); return; }
            if (id == "--pause") { paused = true; manual = null; pending.Clear(); Release(); continue; }
            if (id == "--resume") { if (recovery) { status = "Review and recover the existing journal before resuming."; continue; } paused = false; manual = null; lease.Blocked = false; continue; }
            if (id == "--hdr" || id == "--sdr") { manual = id == "--hdr"; paused = false; continue; }
            if (id == "--auto") { manual = null; Release(); continue; }
            if (id == "--recover") { Recover(); continue; }
            if (id == "--reload") { Release(); if (lease.Written == null) { config = Load(); Ui(() => { window?.Close(); Show(); }); } continue; }
            var g = config.Games.SingleOrDefault(g => g.Id == id);
            if (g == null || string.IsNullOrEmpty(g.Launch)) { status = "Launch identity unresolved"; continue; }
            if (g.Launch.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !File.Exists(g.Launch)) {
                lastLaunchResult = "Launch target missing: " + g.Name; continue;
            }
            const string layers = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
            using var userLayers = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(layers);
            using var machineLayers = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(layers);
            string compatibility = (userLayers?.GetValue(g.Launch) as string ?? "") + (machineLayers?.GetValue(g.Launch) as string ?? "");
            if (compatibility.Contains("RUNASADMIN", StringComparison.OrdinalIgnoreCase)) {
                lastLaunchResult = g.Name + ": existing Run as administrator setting prevents non-elevated launch. No HDR change made."; continue;
            }
            bool hdr = g.Enabled && g.Policy is "Native HDR" or "Auto HDR";
            if (hdr && (!permitted || paused || recovery || StreamingBlocked(HdrController.Inspect()) || g.Display != config.ManagedDisplay)) {
                Ui(() => MessageBox.Show("HDR pre-launch is suspended or the selected display is excluded. Resume locally or change this game's policy before launching.", "Launch withheld")); continue;
            }
            if (hdr) {
                var matches = HdrController.Inspect().Where(d => d.Key == g.Display && !d.Virtual && d.QueryOk && d.Supported).ToArray();
                if (matches.Length != 1) { status = "Pre-launch target unavailable"; continue; }
                lease.Reconcile(matches[0].Enabled, true, Environment.TickCount64, v => HdrController.SetVerified(g.Display, v), Checkpoint);
                if (lease.Blocked || !HdrController.Inspect().Any(d => d.Key == g.Display && d.QueryOk && d.Enabled)) {
                    status = "Pre-launch HDR verification failed"; continue;
                }
            }
            try {
                // Direct executables must never raise a UAC prompt. ERROR_ELEVATION_REQUIRED is a failed launch.
                using var started = Process.Start(new ProcessStartInfo(g.Launch, g.Arguments) { UseShellExecute = !g.Launch.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                    WorkingDirectory = string.IsNullOrEmpty(g.WorkingDirectory) ? Path.GetDirectoryName(g.Launch) ?? "" : g.WorkingDirectory });
                if (hdr) {
                    pending[g.Id] = Environment.TickCount64 + 60000;
                    if (started != null && g.Executables.Contains(g.Launch, StringComparer.OrdinalIgnoreCase)) pendingDirect[g.Id] = started.Id;
                }
                lastLaunchResult = "Launch requested: " + g.Name + "; actual game window still requires verification";
                lastLaunchedId = g.Id; lastLaunchObserved = false;
            } catch (Exception e) { lastLaunchResult = "Launch failed for " + g.Name + ": " + e.Message; pending.Remove(g.Id); pendingDirect.Remove(g.Id); }
        }
    }
    private void Release()
    {
        var ds = HdrController.Inspect();
        if (StreamingBlocked(ds)) return;
        var dsMatch = ds.Where(d => d.Key == config.ManagedDisplay && d.QueryOk && !d.Virtual).ToArray();
        if (dsMatch.Length == 1)
            lease.Reconcile(dsMatch[0].Enabled, null, Environment.TickCount64 + 3000,
                v => HdrController.SetVerified(config.ManagedDisplay, v), Checkpoint);
    }
    private void Recover()
    {
        string file = Path.Combine(Data, "recovery.json");
        if (!File.Exists(file)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var r = doc.RootElement;
        string key = r.GetProperty("Display").GetString() ?? "";
        var ds = HdrController.Inspect();
        if (StreamingBlocked(ds)) { Ui(() => MessageBox.Show("Recovery suspended while streaming may be active.")); return; }
        var matches = ds.Where(d => d.Key == key && d.QueryOk && d.Supported && !d.Virtual).ToArray();
        if (matches.Length != 1) return;
        bool written = r.GetProperty("Written").GetBoolean();
        bool previous = r.GetProperty("Previous").GetBoolean();
        if (matches[0].Enabled != written) { Ui(() => MessageBox.Show("State differs from the journal; leaving it unchanged.")); return; }
        if (HdrController.SetVerified(key, previous)) { File.Delete(file); recovery = false; status = "Recovered; paused"; }
    }
    private static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private void Rescan()
    {
        string script = Path.Combine(AppContext.BaseDirectory, "Discover-Games.ps1");
        Process.Start(new ProcessStartInfo("pwsh.exe") { ArgumentList = { "-NoProfile", "-File", script, "-ConfigPath", ConfigFile, "-AppPath", Environment.ProcessPath! }, UseShellExecute = false });
        MessageBox.Show("Rescan started. Click Reload after it finishes. Existing policies are preserved.");
    }
    private void Show()
    {
        if (window is { IsDisposed: false }) { window.Show(); window.Activate(); return; }
        window = new Form { Text = "Smart HDR Manager — games and display policies", Width = 1100, Height = 700 };
        var editing = JsonSerializer.Deserialize<SafeConfiguration>(JsonSerializer.Serialize(config), Json)!;
        statusLabel = new Label { Dock = DockStyle.Top, Height = 70, Text = status };
        var grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, DataSource = new System.ComponentModel.BindingList<GamePolicy>(editing.Games), AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells };
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Game", DataPropertyName = "Name", ReadOnly = true });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Automate", DataPropertyName = "Enabled" });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Move to display", DataPropertyName = "MoveToDisplay" });
        var policies = new DataGridViewComboBoxColumn { HeaderText = "HDR policy", DataPropertyName = "Policy" };
        policies.Items.AddRange("Unknown", "SDR", "Native HDR", "Auto HDR"); grid.Columns.Add(policies);
        var targets = HdrController.Inspect().Select(d => new { Label = d.Name + (d.Key == config.ManagedDisplay ? " (managed)" : " (excluded)"), d.Key }).ToList();
        foreach (var missing in config.Games.Select(g => g.Display).Distinct().Where(k => !targets.Any(d => d.Key == k))) targets.Add(new { Label = "Unavailable", Key = missing });
        grid.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "Display", DataPropertyName = "Display", DataSource = targets, DisplayMember = "Label", ValueMember = "Key" });
        foreach (string property in new[] { "Confidence", "Launch", "Arguments", "Notes" }) grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = property, DataPropertyName = property });
        grid.DataError += (_, e) => { e.ThrowException = false; status = "Invalid policy/display value: reload or edit local configuration."; };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 85 };
        void Button(string text, Action a) { var b = new Button { Text = text, AutoSize = true }; b.Click += (_, _) => a(); buttons.Controls.Add(b); }
        Button("Save policies", () => { grid.EndEdit(); editing.AllowPhysicalWhileStreamingHostRuns = config.AllowPhysicalWhileStreamingHostRuns; Atomic(ConfigFile + ".bak", Load()); Atomic(ConfigFile, editing); Queue("--reload"); });
        Button("Reload", () => Queue("--reload"));
        Button("Launch selected", () => { if (grid.CurrentRow?.DataBoundItem is GamePolicy g) Queue(g.Id); });
        Button("Rescan", Rescan);
        Button("Pause / restore", () => Queue("--pause"));
        Button("Resume", () => Queue("--resume"));
        Button("Open Games Library", () => Open(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Games Library")));
        Button("Local diagnostics", () => Open(Data));
        var allow = new CheckBox { Text = "Allow physical HDR while streaming host runs (local session only)", AutoSize = true, Checked = config.AllowPhysicalWhileStreamingHostRuns };
        allow.CheckedChanged += (_, _) => { config.AllowPhysicalWhileStreamingHostRuns = allow.Checked; Atomic(ConfigFile, config); };
        buttons.Controls.Add(allow);
        window.Controls.Add(grid); window.Controls.Add(statusLabel); window.Controls.Add(buttons); window.Show();
    }
    internal static void Queue(string id) => Atomic(Path.Combine(Data, "requests", Guid.NewGuid() + ".json"), id);
    protected override void ExitThreadCore()
    {
        timer.Stop(); pending.Clear(); manual = null;
        tray.Visible = false; tray.Dispose(); window?.Dispose(); dispatcher.Dispose(); base.ExitThreadCore();
    }
}
