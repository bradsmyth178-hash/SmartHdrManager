using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace SmartHdrManager
{
    /// <summary>
    /// Finds installed games in the Steam, Epic Games, GOG and Xbox (PC Game Pass) libraries, with their real names
    /// and the executable(s) that run while playing. Read-only: only manifest files and the registry are read.
    /// </summary>
    public static class GameLibraryScanner
    {
        // Steam entries that are tools / runtimes, not games
        private static readonly HashSet<string> SteamNonGameAppIds = new()
        {
            "228980",   // Steamworks Common Redistributables
            "250820",   // SteamVR
            "431960",   // Wallpaper Engine
            "1070560",  // Steam Linux Runtime
            "1391110",  // Steam Linux Runtime - Soldier
            "1628350",  // Steam Linux Runtime - Sniper
            "1493710",  // Proton Experimental
        };

        private static readonly string[] NonGameNameParts =
        {
            "redistributable", "proton", "steam linux runtime", "dedicated server", "soundtrack", " sdk", "benchmark tool"
        };

        // Executables that are never the game itself
        private static readonly string[] ExcludedExeParts =
        {
            "unins", "uninst", "setup", "install", "redist", "vcredist", "vc_redist", "dxsetup", "directx", "dotnet",
            "crash", "report", "bugsplat", "sentry", "easyanticheat", "eac_", "battleye", "beservice", "anticheat",
            "prereq", "cleanup", "touchup", "helper", "updater", "update", "cefprocess", "webhelper", "quicksfv",
            "dxwebsetup", "physx", "ue4prereq", "ueprereq", "unitycrashhandler", "crs-handler", "cmd", "python"
        };

        // Executables found earlier, by game id: reused when the main exe still exists, so a quick refresh
        // does not have to search every install folder again (a full rescan passes null).
        [ThreadStatic] private static IReadOnlyDictionary<string, LibraryGame>? _previous;

        public static List<LibraryGame> Scan(IReadOnlyDictionary<string, LibraryGame>? previous, Action<string>? log = null)
        {
            _previous = previous;
            var games = new List<LibraryGame>();
            Try(() => games.AddRange(ScanSteam()), "Steam", log);
            Try(() => games.AddRange(ScanEpic()), "Epic Games", log);
            Try(() => games.AddRange(ScanGog()), "GOG", log);
            Try(() => games.AddRange(ScanXbox()), "Xbox", log);

            _previous = null;

            // One entry per id, games without a detectable executable are dropped
            return games
                .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void Try(Action action, string source, Action<string>? log)
        {
            try { action(); }
            catch (Exception ex) { log?.Invoke($"{source} library could not be read: {ex.Message}"); }
        }

        // ------------------------------------------------------------------
        // Steam: libraryfolders.vdf -> steamapps\appmanifest_*.acf -> steamapps\common\<installdir>
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanSteam()
        {
            string? steamPath =
                Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string ??
                Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
            if (string.IsNullOrWhiteSpace(steamPath)) yield break;
            steamPath = steamPath.Replace('/', '\\');

            var libraries = new List<string> { steamPath };
            string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\""))
                    libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }

            foreach (string library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string steamapps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamapps)) continue;

                foreach (string acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
                {
                    string text;
                    try { text = File.ReadAllText(acf); } catch { continue; }

                    string appId = AcfValue(text, "appid");
                    string name = AcfValue(text, "name");
                    string installDir = AcfValue(text, "installdir");
                    if (appId.Length == 0 || name.Length == 0 || installDir.Length == 0) continue;
                    if (SteamNonGameAppIds.Contains(appId) || IsNonGameName(name)) continue;

                    string folder = Path.Combine(steamapps, "common", installDir);
                    if (!Directory.Exists(folder)) continue;

                    string id = $"steam:{appId}";
                    if (TryReuse(id, name, "Steam", out var cached))
                    {
                        yield return cached;
                        continue;
                    }

                    var exes = FindGameExecutables(folder, name, installDir);
                    yield return Make(id, name, "Steam", exes);
                }
            }
        }

        private static string AcfValue(string text, string key)
        {
            var m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s+\"(.*?)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\").Trim() : string.Empty;
        }

        // ------------------------------------------------------------------
        // Epic Games: ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item (JSON, includes the launch exe)
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanEpic()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(dir)) yield break;

            foreach (string file in Directory.EnumerateFiles(dir, "*.item"))
            {
                LibraryGame? game = null;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    string name = Str(root, "DisplayName");
                    string location = Str(root, "InstallLocation");
                    string launch = Str(root, "LaunchExecutable");
                    string appName = Str(root, "AppName");
                    string mainApp = Str(root, "MainGameAppName");
                    bool incomplete = root.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True;

                    // Skip DLC / add-ons (they point at the main game) and unfinished installs
                    bool isDlc = mainApp.Length > 0 && appName.Length > 0 && !mainApp.Equals(appName, StringComparison.OrdinalIgnoreCase);
                    if (!incomplete && !isDlc && name.Length > 0 && location.Length > 0 && !IsNonGameName(name))
                    {
                        var exes = new List<string>();
                        if (launch.Length > 0)
                        {
                            string exe = Path.Combine(location, launch.Replace('/', '\\'));
                            if (File.Exists(exe)) exes.Add(exe);
                        }
                        if (exes.Count == 0 && Directory.Exists(location))
                            exes = FindGameExecutables(location, name, Path.GetFileName(location.TrimEnd('\\')));
                        else if (exes.Count == 1)
                            AddShippingCounterpart(exes, location);

                        game = Make($"epic:{(appName.Length > 0 ? appName : name)}", name, "Epic Games", exes);
                    }
                }
                catch { }

                if (game != null) yield return game;
            }
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

        // ------------------------------------------------------------------
        // GOG Galaxy: HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\<id> (gameName, exe, path, dependsOn)
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanGog()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
            if (key == null) yield break;

            foreach (string id in key.GetSubKeyNames())
            {
                using var game = key.OpenSubKey(id);
                if (game == null) continue;

                string name = game.GetValue("gameName") as string ?? string.Empty;
                string exe = game.GetValue("exe") as string ?? string.Empty;
                string path = game.GetValue("path") as string ?? string.Empty;
                string dependsOn = game.GetValue("dependsOn") as string ?? string.Empty;
                if (name.Length == 0 || dependsOn.Length > 0 || IsNonGameName(name)) continue;   // dependsOn = DLC

                var exes = new List<string>();
                if (exe.Length > 0 && File.Exists(exe)) exes.Add(exe);
                if (exes.Count == 0 && Directory.Exists(path)) exes = FindGameExecutables(path, name, Path.GetFileName(path.TrimEnd('\\')));
                else if (exes.Count == 1 && Directory.Exists(path)) AddShippingCounterpart(exes, path);

                yield return Make($"gog:{id}", name, "GOG", exes);
            }
        }

        // ------------------------------------------------------------------
        // Xbox app / PC Game Pass: <drive>:\XboxGames\<game>\Content\MicrosoftGame.config
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanXbox()
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                string root = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
                if (!Directory.Exists(root)) continue;

                foreach (string gameDir in Directory.EnumerateDirectories(root))
                {
                    LibraryGame? game = null;
                    try
                    {
                        string content = Path.Combine(gameDir, "Content");
                        string config = Path.Combine(content, "MicrosoftGame.config");
                        if (!File.Exists(config)) continue;

                        var doc = XDocument.Load(config);
                        string folderName = Path.GetFileName(gameDir);
                        string? display = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals")
                            ?.Attribute("DefaultDisplayName")?.Value;
                        string name = !string.IsNullOrWhiteSpace(display) && !display.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase)
                            ? display
                            : folderName;

                        var exes = doc.Descendants()
                            .Where(e => e.Name.LocalName == "Executable")
                            .Select(e => e.Attribute("Name")?.Value)
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .Select(v => Path.Combine(content, v!.Replace('/', '\\')))
                            .Where(p => !IsExcludedExe(Path.GetFileNameWithoutExtension(p)))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(3)
                            .ToList();

                        if (!IsNonGameName(name))
                            game = Make($"xbox:{folderName}", name, "Xbox", exes);
                    }
                    catch { }

                    if (game != null) yield return game;
                }
            }
        }

        // ------------------------------------------------------------------
        // Exe locations Windows already knows about (no disk search, only registry reads):
        //  - Game Bar's list of games you have played (GameConfigStore, "MatchedExeFullPath")
        //  - the Explorer program-name cache (MuiCache, value names are full exe paths)
        //  - the Program Compatibility Assistant list of programs that were started
        //  - "DisplayIcon" of installed programs (Apps & features)
        // Returns process name (lower case, no .exe) -> full path, only for files that still exist.
        // ------------------------------------------------------------------
        public static Dictionary<string, string> FindKnownExePaths()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                path = path.Trim().Trim('"');
                int comma = path.LastIndexOf(".exe,", StringComparison.OrdinalIgnoreCase);   // "C:\x\game.exe,0"
                if (comma >= 0) path = path.Substring(0, comma + 4);
                if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
                string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                if (name.Length == 0 || result.ContainsKey(name)) return;
                try { if (File.Exists(path)) result[name] = path; } catch { }
            }

            // Game Bar / Game Mode: one subkey per game that was played
            try
            {
                using var store = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
                if (store != null)
                {
                    foreach (string sub in store.GetSubKeyNames())
                    {
                        using var game = store.OpenSubKey(sub);
                        Add(game?.GetValue("MatchedExeFullPath") as string);
                    }
                }
            }
            catch { }

            // Explorer's MuiCache: value names like "C:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe.FriendlyAppName"
            try
            {
                using var mui = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache");
                if (mui != null)
                {
                    foreach (string valueName in mui.GetValueNames())
                    {
                        int dot = valueName.LastIndexOf(".exe.", StringComparison.OrdinalIgnoreCase);
                        if (dot > 0) Add(valueName.Substring(0, dot + 4));
                    }
                }
            }
            catch { }

            // Program Compatibility Assistant: value names are full paths of started programs
            try
            {
                using var pca = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store");
                if (pca != null)
                    foreach (string valueName in pca.GetValueNames()) Add(valueName);
            }
            catch { }

            // Installed programs (machine-wide and per user, 64- and 32-bit)
            foreach (var (hive, keyPath) in new[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
            })
            {
                try
                {
                    using var uninstall = hive.OpenSubKey(keyPath);
                    if (uninstall == null) continue;
                    foreach (string sub in uninstall.GetSubKeyNames())
                    {
                        using var app = uninstall.OpenSubKey(sub);
                        Add(app?.GetValue("DisplayIcon") as string);
                    }
                }
                catch { }
            }

            return result;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------
        private static bool TryReuse(string id, string name, string source, out LibraryGame game)
        {
            game = null!;
            var prev = _previous;
            if (prev == null || !prev.TryGetValue(id, out var old) || old.Processes.Count == 0) return false;
            if (string.IsNullOrEmpty(old.ExePath) || !File.Exists(old.ExePath)) return false;

            game = new LibraryGame
            {
                Id = id,
                Name = name.Trim(),
                Source = source,
                Processes = new List<string>(old.Processes),
                ExePath = old.ExePath
            };
            return true;
        }

        private static LibraryGame Make(string id, string name, string source, List<string> exePaths)
        {
            return new LibraryGame
            {
                Id = id,
                Name = name.Trim(),
                Source = source,
                Processes = exePaths
                    .Select(p => Path.GetFileNameWithoutExtension(p).ToLowerInvariant())
                    .Where(p => p.Length > 0)
                    .Distinct()
                    .ToList(),
                ExePath = exePaths.FirstOrDefault(File.Exists)
            };
        }

        private static bool IsNonGameName(string name)
        {
            string n = " " + name.ToLowerInvariant();
            return NonGameNameParts.Any(n.Contains);
        }

        private static bool IsExcludedExe(string fileNameWithoutExt)
        {
            string n = fileNameWithoutExt.ToLowerInvariant();
            return ExcludedExeParts.Any(n.Contains);
        }

        private static string Normalize(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        /// <summary>
        /// Picks the most likely game executable(s) in an install folder: names similar to the game,
        /// Unreal "-Win64-Shipping" binaries (the process that actually runs), and large files score higher;
        /// launchers, installers, crash reporters etc. are ignored.
        /// </summary>
        public static List<string> FindGameExecutables(string folder, string gameName, string installDir)
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
            string nameKey = Normalize(gameName);
            string dirKey = Normalize(installDir);

            var candidates = new List<(string Path, double Score)>();
            foreach (string exe in Directory.EnumerateFiles(folder, "*.exe", options))
            {
                string file = Path.GetFileNameWithoutExtension(exe);
                if (IsExcludedExe(file)) continue;

                long size;
                try { size = new FileInfo(exe).Length; } catch { continue; }
                if (size < 300 * 1024) continue;

                string key = Normalize(file);
                double score = Math.Log10(size);   // ~5.5 (300 KB) .. ~8.5 (300 MB)

                if (key.Length > 0 && (key == nameKey || key == dirKey)) score += 6;
                else if (key.Length >= 3 && (nameKey.Contains(key) || dirKey.Contains(key) || key.Contains(dirKey))) score += 3;

                if (file.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith("-WinGDK-Shipping", StringComparison.OrdinalIgnoreCase)) score += 5;
                if (key.Contains("launcher")) score -= 4;
                if (key.Contains("server") || key.Contains("editor") || key.Contains("tool")) score -= 3;

                int depth = exe.Substring(folder.Length).Count(c => c == '\\');
                score -= Math.Max(0, depth - 1) * 0.3;

                candidates.Add((exe, score));
            }

            var result = candidates.OrderByDescending(c => c.Score).Take(1).Select(c => c.Path).ToList();
            if (result.Count == 1) AddShippingCounterpart(result, folder);
            return result;
        }

        /// <summary>Unreal games: "Game.exe" only starts "Game-Win64-Shipping.exe" (the real process) and the other way round.</summary>
        private static void AddShippingCounterpart(List<string> exes, string folder)
        {
            try
            {
                string main = Path.GetFileNameWithoutExtension(exes[0]);
                var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };

                if (main.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase)) return;

                string? shipping = Directory.EnumerateFiles(folder, main + "-Win64-Shipping.exe", options).FirstOrDefault()
                                   ?? Directory.EnumerateFiles(folder, main + "-WinGDK-Shipping.exe", options).FirstOrDefault();
                if (shipping != null && !exes.Contains(shipping, StringComparer.OrdinalIgnoreCase))
                    exes.Add(shipping);
            }
            catch { }
        }
    }
}
