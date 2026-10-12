// EDayUltrawide - 32:9 / ultrawide cutscene and FOV fix for Gears of War: E-Day (Xbox app / Game Pass build).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace EDayUltrawide
{
    internal static class Log
    {
        static StreamWriter file;
        public static bool Verbose;
        static readonly List<string> tail = new List<string>();
        public static int TailVersion;
        public static string[] Tail() { lock (tail) return tail.ToArray(); }

        public static string FilePath { get; private set; }

        public static void Open(string dir, string name = "EDayUltrawide")
        {
            try
            {
                string path = Path.Combine(dir, name + ".log"), prev = Path.Combine(dir, name + ".prev.log");
                try
                {
                    if (File.Exists(path)) { if (File.Exists(prev)) File.Delete(prev); File.Move(path, prev); }
                    file = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true }; FilePath = path;
                }
                catch (IOException)
                {
                    // Another EDayUltrawide window still holds the log: write a separate one instead of none.
                    string alt = Path.Combine(dir, name + "." + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + ".log");
                    file = new StreamWriter(alt, false, new UTF8Encoding(false)) { AutoFlush = true }; FilePath = alt;
                    try { Console.WriteLine("(main log busy - writing " + Path.GetFileName(alt) + ")"); } catch { }
                }
            }
            catch (Exception e) { try { Console.WriteLine("(log file unavailable: " + e.Message + ")"); } catch { } }
        }

        public static void Line(string s)
        {
            string l = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + s;
            try { Console.WriteLine(l); } catch { }
            lock (tail) { tail.Add(l); if (tail.Count > 200) tail.RemoveRange(0, tail.Count - 200); TailVersion++; }
            try { if (file != null) file.WriteLine(l); } catch { }
        }

        public static void Debug(string s) { if (Verbose) Line("[v] " + s); }
    }

    internal sealed class CapSite
    {
        public long Instr, Const;
        public float Original;
        public string PatternName;
        public int RipRefs = -1;
    }

    internal static partial class Program
    {
        static readonly string[] GameNames = { "GoWEDay", "GoWEDay-Steam" };
        const string PackageName = "Microsoft.Fairlightbasegame";
        const string PackageAppId = "AppGearsofWarEDayShipping";

        static string BaseDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        static Config cfg { get { return Settings.Current; } }
        static volatile bool stopRequested;
        static string[] passthrough;

        const string PackageFamily = "Microsoft.Fairlightbasegame_8wekyb3d8bbwe";
        const string HookExe = @"FairlightConcept\Binaries\WinGDK\EDayUltrawide.exe";
        const string OriginalExe = "start_protected_game.exe";

        /// <summary>The game's Content folder (holds MicrosoftGame.config), searched upwards / sideways from this exe.</summary>
        static string contentOverride;

        /// <summary>Where settings, log and status live: next to the exe when it is the installed copy, otherwise in the game's WinGDK folder (so a copy run from Downloads edits the same settings).</summary>
        internal static string DataDir
        {
            get
            {
                try
                {
                    string cd = ContentDir();
                    if (cd == null || IsInstalledLocation() || InPackage(out _)) return BaseDir;
                    string win = WinGdkOf(cd);
                    if (!Directory.Exists(win)) return BaseDir;
                    string probe = Path.Combine(win, ".edayultrawide-write-test");
                    File.WriteAllText(probe, "x"); File.Delete(probe);
                    return win;
                }
                catch { return BaseDir; }
            }
        }

        /// <summary>The window's "Choose game folder" button: accepts the game's Content folder or the folder above it.</summary>
        internal static bool SetGameFolder(string dir)
        {
            foreach (var c in new[] { dir, dir == null ? null : Path.Combine(dir, "Content") })
                if (IsContentDir(c)) { contentOverride = c.TrimEnd('\\'); return true; }
            return false;
        }

        static string ContentDir()
        {
            if (contentOverride != null) return contentOverride;
            string[] c = { BaseDir, Path.Combine(BaseDir, @"..\..\.."), Path.Combine(BaseDir, @"..\Content"), Path.Combine(BaseDir, @"..\..\Content") };
            foreach (var d in c) if (File.Exists(Path.Combine(d, "MicrosoftGame.config"))) return Path.GetFullPath(d);
            return null;
        }

        static bool InPackage(out string fullName)
        {
            fullName = null;
            try
            {
                int len = 0;
                int r = Native.GetCurrentPackageFullName(ref len, null);
                if (r != 122 /*ERROR_INSUFFICIENT_BUFFER*/) return false;
                var sb = new StringBuilder(len);
                if (Native.GetCurrentPackageFullName(ref len, sb) != 0) return false;
                fullName = sb.ToString();
                return true;
            }
            catch { return false; }
        }

        /// <summary>Points MicrosoftGame.config's executable at this tool (install) or back at the anti-cheat launcher (uninstall).</summary>
        static int Hook(bool install, bool elevated = false)
        {
            string dir = ContentDir();
            if (dir == null) throw new InvalidOperationException("MicrosoftGame.config not found (Xbox app build only).");
            string cfgPath = Path.Combine(dir, "MicrosoftGame.config"), backup = cfgPath + ".edayultrawide-original";
            string text = File.ReadAllText(cfgPath);
            string next = LauncherConfig.Rewrite(text, install ? HookExe : OriginalExe);
            if (next == null) throw new InvalidOperationException("Unexpected MicrosoftGame.config layout; not changed.");
            if (install && !File.Exists(backup)) File.Copy(cfgPath, backup);
            if (next != text) File.WriteAllText(cfgPath, next, new UTF8Encoding(false));
            Log.Line("MicrosoftGame.config executable: " + LauncherConfig.Current(next));

            // The Xbox app actually launches what Gaming Services registered (HKLM GameConfig), not the file.
            string want = install ? HookExe : OriginalExe;
            string key, cur = ReadLaunchTarget(out key);
            if (key == null) throw new InvalidOperationException(@"Gaming Services launch entry for E-Day not found under HKLM\" + GameConfigRoot + ".");
            Log.Line("Registered launch target: " + cur + "   (" + key + ")");
            if (!string.Equals(cur, want, StringComparison.OrdinalIgnoreCase))
            {
                if (elevated) { RegistryHookElevated(install); cur = ReadLaunchTarget(out key); }
                else
                {
                Log.Line("Changing it needs administrator rights - approve the Windows prompt.");
                try
                {
                    var psi = new ProcessStartInfo(System.Reflection.Assembly.GetEntryAssembly().Location, "--registry-hook " + (install ? "install" : "uninstall"))
                    { UseShellExecute = true, Verb = "runas", WorkingDirectory = BaseDir };
                    using (var p = Process.Start(psi)) p.WaitForExit();
                }
                catch (Win32Exception e) when (e.NativeErrorCode == 1223)
                {
                    Log.Line("Administrator prompt was declined - nothing changed in the registry.");
                    return 1;
                }
                cur = ReadLaunchTarget(out key);
                }
            }
            bool ok = string.Equals(cur, want, StringComparison.OrdinalIgnoreCase);
            Log.Line("Registered launch target now: " + cur + (ok ? "  - OK" : "  - NOT CHANGED, see EDayUltrawide.admin.log"));
            if (!ok) return 1;
            Log.Line(install ? "Xbox launch hook INSTALLED: press Play in the Xbox app (hold Shift for a normal online launch)."
                             : "Xbox launch hook REMOVED: the Xbox app starts the game normally with anti-cheat again.");
            Log.Line("If the Xbox app still starts the old way, quit it fully (tray icon > Exit) and open it again.");
            return 0;
        }

        const string GameConfigRoot = @"SOFTWARE\Microsoft\GamingServices\GameConfig";

        /// <summary>Reads the registered executable for our app id; key = its registry path (null if not found).</summary>
        static string ReadLaunchTarget(out string keyPath)
        {
            keyPath = null;
            using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
            using (var gc = hklm.OpenSubKey(GameConfigRoot))
            {
                if (gc == null) return null;
                foreach (var pkg in gc.GetSubKeyNames().Where(n => n.StartsWith(PackageName + "_", StringComparison.OrdinalIgnoreCase)))
                    using (var ex = gc.OpenSubKey(pkg + @"\Executable"))
                    {
                        if (ex == null) continue;
                        foreach (var e in ex.GetSubKeyNames())
                            using (var k = ex.OpenSubKey(e))
                                if (k != null && string.Equals(k.GetValue("Id") as string, PackageAppId, StringComparison.OrdinalIgnoreCase))
                                {
                                    keyPath = GameConfigRoot + "\\" + pkg + @"\Executable\" + e;
                                    return k.GetValue("Name") as string;
                                }
                    }
            }
            return null;
        }

        /// <summary>Runs elevated: sets the registered launch executable and checks the file exists in the package root.</summary>
        static int RegistryHookElevated(bool install)
        {
            string want = install ? HookExe : OriginalExe;
            string key, cur = ReadLaunchTarget(out key);
            Log.Line("Elevated registry step (" + (install ? "install" : "uninstall") + "): " + key + "  Name = " + cur);
            if (key == null) { Log.Line("Launch entry not found."); return 1; }
            try
            {
                using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (var k = hklm.OpenSubKey(key, true))
                {
                    if (k == null) { Log.Line("Cannot open the key for writing."); return 1; }
                    k.SetValue("Name", want, Microsoft.Win32.RegistryValueKind.String);
                }
            }
            catch (Exception e) { Log.Line("Registry write failed: " + e.GetType().Name + ": " + e.Message); return 1; }
            string after = ReadLaunchTarget(out key);
            Log.Line("Name is now: " + after);
            // The launcher resolves the name against the package root; check our exe is visible there.
            try
            {
                using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (var root = hklm.OpenSubKey(@"SOFTWARE\Microsoft\GamingServices\PackageRepository\Root"))
                    if (root != null)
                        foreach (var a in root.GetSubKeyNames())
                            using (var ak = root.OpenSubKey(a))
                                foreach (var b in ak.GetSubKeyNames())
                                    using (var bk = ak.OpenSubKey(b))
                                    {
                                        string pk = bk.GetValue("Package") as string, r = bk.GetValue("Root") as string;
                                        if (pk == null || r == null || !pk.StartsWith(PackageName + "_", StringComparison.OrdinalIgnoreCase)) continue;
                                        string full = Path.Combine(r.Replace(@"\\?\", ""), want);
                                        Log.Line("Package root " + r + " -> " + want + (File.Exists(full) ? " exists" : " NOT FOUND"));
                                    }
            }
            catch (Exception e) { Log.Line("package root check: " + e.Message); }
            return string.Equals(after, want, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        const string Version = "0.4.0";
        static Thread engineThread;

        [STAThread]
        static int Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); } catch { }
            string[] flags = { "--auto", "--attach", "--dump", "--status", "--install-hook", "--uninstall-hook", "--probe", "--registry-hook", "--do-install", "--do-uninstall" };
            string mode;
            if (args.Length > 0 && flags.Contains(args[0].ToLowerInvariant())) { mode = args[0].ToLowerInvariant(); }
            else
            {
                passthrough = args; // arguments from the Xbox launcher go to the game
                mode = "--auto";
            }
            bool gui = mode == "--auto" || mode == "--attach";
            bool setupStep = mode == "--do-install" || mode == "--do-uninstall";
            if (!gui && mode != "--registry-hook" && !setupStep)
            {
                // tool modes print to a console window (the exe itself has no console)
                if (!Native.AttachConsole(-1)) Native.AllocConsole();
                try { Console.Title = "E-Day Ultrawide"; } catch { }
            }
            // Started from anywhere (Downloads, desktop): find the game folder so the window can install the hook and keep its files there.
            if (!setupStep && mode != "--registry-hook" && ContentDir() == null) { var found = FindContentDirs(); if (found.Count > 0) contentOverride = found[0]; }
            string setupLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EDayUltrawide");
            if (setupStep) { try { Directory.CreateDirectory(setupLog); } catch { } Log.Open(setupLog, "EDayUltrawide.setup"); }
            else Log.Open(DataDir, mode == "--registry-hook" ? "EDayUltrawide.admin" : "EDayUltrawide");
            if (!setupStep) foreach (var w in Settings.Init(DataDir)) Log.Line("config: " + w);
            Log.Verbose = cfg.Verbose;
            Log.Line("EDayUltrawide v" + Version + " (" + mode + ")  dir=" + BaseDir + (DataDir != BaseDir ? "  data=" + DataDir : ""));
            try { Console.CancelKeyPress += (s, e) => { e.Cancel = true; stopRequested = true; }; } catch { }

            try
            {
                switch (mode)
                {
                    case "--auto": return RunGui(false);
                    case "--do-install": return SetupInstall(args.Length > 1 ? args[1] : null);
                    case "--do-uninstall": return SetupUninstall(args.Length > 1 ? args[1] : null, args.Contains("--detached"));
                    case "--attach": return RunGui(true);
                    case "--dump": { int r = Dump(args.Length > 1 ? args[1] : Path.Combine(BaseDir, "dump")); PauseIfDoubleClicked(args); return r; }
                    case "--status": return Status();
                    case "--registry-hook": return RegistryHookElevated(args.Length > 1 && args[1] == "install");
                    case "--probe": { int r = Probe(); PauseIfDoubleClicked(passthrough ?? new string[0]); return r; }
                    case "--install-hook": { int r = Hook(true); PauseIfDoubleClicked(passthrough ?? new string[0]); return r; }
                    case "--uninstall-hook": { int r = Hook(false); PauseIfDoubleClicked(passthrough ?? new string[0]); return r; }
                    default:
                        Log.Line("Usage: EDayUltrawide.exe [--auto | --attach | --status | --dump [folder] | --install-hook | --uninstall-hook]");
                        return 1;
                }
            }
            catch (Exception e)
            {
                Log.Line("ERROR: " + e.Message);
                Log.Debug(e.ToString());
                if (gui) { try { System.Windows.Forms.MessageBox.Show(e.Message, "E-Day Ultrawide", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error); } catch { } }
                else PauseIfDoubleClicked(args);
                return 1;
            }
        }

        // ------------------------------------------------------------------ window + engine thread

        static int RunGui(bool attachOnly)
        {
            string pkg;
            bool inPackage = InPackage(out pkg);
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            var form = new MainForm(inPackage);
            // Started by the Xbox app (or told to attach): the fix runs here. Started by hand: the window only edits settings
            // (and can start the game); the instance the Xbox app starts does the work.
            if (inPackage) StartEngine(() => RunAuto());
            else if (attachOnly) StartEngine(() => RunAttached(WaitForGame(cfg.AttachTimeoutSeconds)));
            System.Windows.Forms.Application.Run(form);
            StopEngine();
            return 0;
        }

        internal static void StartEngine(Func<int> body)
        {
            if (Live.EngineRunning) return;
            Live.Error = null; Live.Exited = false; Live.EngineRunning = true; stopRequested = false; Live.Phase = "Starting...";
            engineThread = new Thread(() =>
            {
                try { body(); }
                catch (Exception e) { Live.Error = e.Message; Log.Line("ERROR: " + e.Message); Log.Debug(e.ToString()); }
                finally
                {
                    Live.GameAttached = false; Live.FixActive = false; Live.CutsceneHookOn = false; Live.GameplayAvailable = false;
                    Live.GameplayObjects = 0; Live.Phase = Live.Error != null ? "Stopped" : "Idle";
                    Live.Clear(DataDir);
                    Live.EngineRunning = false; Live.Exited = true;
                }
            }) { IsBackground = true, Name = "EDayUltrawide engine" };
            engineThread.Start();
        }

        internal static void StopEngine()
        {
            stopRequested = true;
            var t = engineThread;
            if (t != null && t.IsAlive) t.Join(4000);
        }

        internal static bool FindGameRunning() { try { return FindGame() != null; } catch { return false; } }
        internal static int RunAutoPublic() { return RunAuto(); }
        internal static int HookPublic(bool install) { return Hook(install); }
        internal static bool IsXboxBuild() { return ContentDir() != null; }
        internal static bool RunningInstalled { get { return IsInstalledLocation(); } }
        internal static bool AnythingInstalled { get { string c = ContentDir(); return c != null && Inspect(c).Any; } }

        /// <summary>Human text for the launch hook (Xbox app build only) and whether it is fully installed (config + registry).</summary>
        internal static string HookStatus(out bool installed)
        {
            installed = false;
            try
            {
                string dir = ContentDir();
                if (dir == null) return "game folder not found - use Choose game folder";
                bool inCfg = (LauncherConfig.Current(File.ReadAllText(Path.Combine(dir, "MicrosoftGame.config"))) ?? "").IndexOf("EDayUltrawide", StringComparison.OrdinalIgnoreCase) >= 0;
                string key; bool inReg = (ReadLaunchTarget(out key) ?? "").IndexOf("EDayUltrawide", StringComparison.OrdinalIgnoreCase) >= 0;
                installed = inCfg && inReg;
                if (installed) return "installed";
                if (inCfg || inReg) return "half installed - press Install hook to repair";
                return "not installed (the Xbox app starts the game normally)";
            }
            catch (Exception e) { return "unknown (" + e.Message + ")"; }
        }

        /// <summary>When started by double-click (no arguments), keep the window open so errors can be read.</summary>
        static void PauseIfDoubleClicked(string[] args)
        {
            if (args.Length > 0) return;
            try
            {
                Console.WriteLine();
                Console.WriteLine("Press any key to close (details are in EDayUltrawide.log).");
                Console.ReadKey(true);
            }
            catch { }
        }

        // ------------------------------------------------------------------ process discovery / launch

        static Process FindGame()
        {
            foreach (var n in GameNames)
            {
                var p = Process.GetProcessesByName(n);
                if (p.Length > 0) return p.OrderBy(x => SafeStart(x)).First();
            }
            return null;
        }

        static DateTime SafeStart(Process p) { try { return p.StartTime; } catch { return DateTime.MaxValue; } }

        static Process WaitForGame(int seconds)
        {
            var sw = Stopwatch.StartNew();
            bool said = false;
            while (sw.Elapsed.TotalSeconds < seconds && !stopRequested)
            {
                var g = FindGame();
                if (g != null) return g;
                if (!said) { Log.Line("Waiting for the game to start..."); said = true; }
                Thread.Sleep(500);
            }
            throw new InvalidOperationException("Game process not found (looked for GoWEDay.exe / GoWEDay-Steam.exe).");
        }

        static string LocateGameExe()
        {
            string[] candidates =
            {
                Path.Combine(BaseDir, "GoWEDay.exe"),
                Path.Combine(BaseDir, @"..\Content\FairlightConcept\Binaries\WinGDK\GoWEDay.exe"),
                Path.Combine(BaseDir, @"..\..\Content\FairlightConcept\Binaries\WinGDK\GoWEDay.exe"),
                Path.Combine(BaseDir, "GoWEDay-Steam.exe"),
                Path.Combine(BaseDir, @"..\FairlightConcept\Binaries\Win64\GoWEDay-Steam.exe"),
            };
            foreach (var c in candidates) if (File.Exists(c)) return Path.GetFullPath(c);
            return null;
        }

        static bool AntiCheatActive(Process game, out string why)
        {
            why = null;
            if (Process.GetProcessesByName("EasyAntiCheat_EOS").Length > 0) { why = "EasyAntiCheat_EOS.exe is running"; return true; }
            if (Process.GetProcessesByName("start_protected_game").Length > 0) { why = "start_protected_game.exe (EAC bootstrapper) is running"; return true; }
            if (game == null) return false;
            try
            {
                foreach (ProcessModule m in game.Modules)
                    if (m.ModuleName.IndexOf("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) >= 0) { why = "EAC module loaded in the game"; return true; }
            }
            catch (Exception e) { why = "cannot list game modules (" + e.Message + ") - treating as protected"; return true; }
            return false;
        }

        static int RunAuto()
        {
            Live.Phase = "Looking for the game...";
            var running = FindGame();
            if (running != null)
            {
                string dlg;
                if (FatalDialog(running, out dlg))
                {
                    Log.Line("A previous game instance is stuck on a crash dialog (" + dlg + "); closing it.");
                    try { running.Kill(); running.WaitForExit(5000); } catch { }
                }
                else
                {
                    Log.Line("Game already running (pid " + running.Id + "); attaching.");
                    return RunAttached(running);
                }
            }
            string exe = LocateGameExe();
            if (exe == null)
                throw new InvalidOperationException("Could not find GoWEDay.exe. Keep this folder inside the game folder (next to 'Content').");
            // (set per child process below: an online hand-off must NOT inherit it, or anti-cheat stays off)
            Environment.SetEnvironmentVariable("EOS_USE_ANTICHEATCLIENTNULL", null);

            // The Xbox app / Game Pass build (WinGDK) only gets its GDK runtime when launched by the Xbox app
            // (GameLaunchHelper -> MicrosoftGame.config executable). Started any other way it dies with
            // "LowLevelFatalError ... GDK runtime is not available" - even inside the package via
            // Invoke-CommandInDesktopPackage (tested). So on GDK this tool IS the configured executable:
            // the Xbox app starts it, and it starts GoWEDay.exe as a child, which inherits the runtime.
            bool gdk = exe.IndexOf(@"\WinGDK\", StringComparison.OrdinalIgnoreCase) >= 0;
            if (gdk)
            {
                string pkg;
                if (!InPackage(out pkg))
                {
                    string dir = ContentDir();
                    string cur = dir == null ? null : LauncherConfig.Current(File.ReadAllText(Path.Combine(dir, "MicrosoftGame.config")));
                    if (cur == null || cur.IndexOf("EDayUltrawide", StringComparison.OrdinalIgnoreCase) < 0)
                        throw new InvalidOperationException("The Xbox launch hook is not installed (MicrosoftGame.config starts '" + cur + "'). Double-click EDayUltrawide-InstallHook.exe first.");
                    Log.Line("Asking the Xbox app to start the game. The fix runs in the window the game's launcher opens; this one can close.");
                    Live.Phase = "Asked the Xbox app to start the game";
                    Process.Start("explorer.exe", @"shell:AppsFolder\" + PackageFamily + "!" + PackageAppId);
                    return 0;
                }
                Log.Line("Started by the Xbox app inside " + pkg + (passthrough != null && passthrough.Length > 0 ? "  args: " + string.Join(" ", passthrough) : ""));
                try { Native.ShowWindow(Native.GetConsoleWindow(), 7 /*SW_SHOWMINNOACTIVE*/); } catch { }

                int vkOnline = Keys.VirtualKey(cfg.OnlineKey);
                bool held = vkOnline != 0 && (Native.GetAsyncKeyState(vkOnline) & 0x8000) != 0;
                if (cfg.OnlineMode || held)
                {
                    string eac = Path.Combine(ContentDir() ?? Path.Combine(BaseDir, @"..\..\.."), OriginalExe);
                    Log.Line("ONLINE launch (" + (held ? cfg.OnlineKey + " held" : "Mode=Online") + "): handing over to " + eac + " - anti-cheat on, no fix.");
                    var on = new ProcessStartInfo(eac, Args(passthrough)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(eac) };
                    on.EnvironmentVariables.Remove("EOS_USE_ANTICHEATCLIENTNULL"); // normal anti-cheat launch
                    using (var p = Process.Start(on)) p.WaitForExit();
                    return 0;
                }

                Live.Phase = "Starting the game...";
                Log.Line("OFFLINE launch with cutscene fix (anti-cheat disabled): " + exe);
                var psi = new ProcessStartInfo(exe, Args(passthrough)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) };
                psi.EnvironmentVariables["EOS_USE_ANTICHEATCLIENTNULL"] = "1";
                var child = Process.Start(psi);
                Log.Line("Game started, pid " + child.Id + ".");
                Process g = SettleGame(90);
                if (g == null) throw new InvalidOperationException("The game did not stay up. Check EDayUltrawide.log.");
                return RunAttached(g);
            }

            Log.Line("Launching offline (anti-cheat disabled): " + exe);
            var spsi = new ProcessStartInfo(exe, Args(passthrough)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) };
            spsi.EnvironmentVariables["EOS_USE_ANTICHEATCLIENTNULL"] = "1";
            try { var c = Process.Start(spsi); Log.Line("Direct launch started pid " + c.Id + "."); }
            catch (Exception e) { Log.Line("Direct launch failed: " + e.Message); }
            Process game = SettleGame(30);
            if (game == null) throw new InvalidOperationException("The game did not start. Check EDayUltrawide.log.");
            return RunAttached(game);
        }

        /// <summary>Waits for a game process that stays up ~8 s without showing a crash dialog.</summary>
        static Process SettleGame(int seconds)
        {
            var sw = Stopwatch.StartNew();
            int seenPid = 0; var aliveSince = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds && !stopRequested)
            {
                Thread.Sleep(1000);
                var g = FindGame();
                if (g == null) { if (seenPid != 0) Log.Line("Game process " + seenPid + " exited during startup."); seenPid = 0; continue; }
                if (g.Id != seenPid) { seenPid = g.Id; aliveSince.Restart(); Log.Line("Game process appeared (pid " + g.Id + ")."); }
                string dlg;
                if (FatalDialog(g, out dlg))
                {
                    Log.Line("Game showed a crash dialog: " + dlg);
                    try { g.Kill(); g.WaitForExit(5000); } catch { }
                    return null;
                }
                try { if (!g.HasExited && aliveSince.Elapsed.TotalSeconds >= 8) return g; } catch { }
            }
            return null;
        }

        /// <summary>True if the process shows a message box whose text looks like an engine fatal error.</summary>
        static bool FatalDialog(Process p, out string text)
        {
            text = null;
            string found = null; uint pid = (uint)p.Id;
            Native.EnumWindows((h, l) =>
            {
                uint wp; Native.GetWindowThreadProcessId(h, out wp);
                if (wp != pid || !Native.IsWindowVisible(h)) return true;
                if (Native.ClassName(h) != "#32770") return true;
                var sb = new StringBuilder(Native.Text(h));
                Native.EnumChildWindows(h, (c, l2) => { string t = Native.Text(c); if (t.Length > 0) sb.Append(" | ").Append(t); return true; }, IntPtr.Zero);
                string all = sb.ToString();
                string low = all.ToLowerInvariant();
                if (low.Contains("fatal") || low.Contains("gdk runtime") || low.Contains("crash") || low.Contains("has stopped"))
                { found = all.Replace("\r", " ").Replace("\n", " "); return false; }
                return true;
            }, IntPtr.Zero);
            text = found;
            return found != null;
        }

        // ------------------------------------------------------------------ probe (read-only diagnostics)

        static int Probe()
        {
            Log.Line("PROBE (read-only)");
            // 1) running processes of interest + command lines + parents
            try
            {
                using (var s = new System.Management.ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name, CommandLine, ExecutablePath FROM Win32_Process"))
                    foreach (System.Management.ManagementObject o in s.Get())
                    {
                        string n = (o["Name"] as string ?? "").ToLowerInvariant();
                        if (n.Contains("gowe") || n.Contains("anticheat") || n.Contains("protected_game") || n.Contains("gamelaunchhelper") || n.Contains("edayultrawide") || n.Contains("gamingservices") || n == "xboxpcapp.exe" || n.Contains("xboxapp"))
                            Log.Line("proc " + o["ProcessId"] + " parent " + o["ParentProcessId"] + " " + o["Name"] + "  cmd=" + o["CommandLine"] + "  path=" + o["ExecutablePath"]);
                    }
            }
            catch (Exception e) { Log.Line("process list failed: " + e.Message); }

            // 2) Gaming Services registry: anything mentioning this game
            foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser })
            {
                try
                {
                    using (var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Registry64))
                    using (var gs = root.OpenSubKey(@"SOFTWARE\Microsoft\GamingServices"))
                    {
                        if (gs == null) { Log.Line(hive + @"\SOFTWARE\Microsoft\GamingServices: not present"); continue; }
                        Log.Line(hive + @"\SOFTWARE\Microsoft\GamingServices subkeys: " + string.Join(", ", gs.GetSubKeyNames()));
                        int budget = 400;
                        DumpMatching(gs, hive + @"\SOFTWARE\Microsoft\GamingServices", 0, false, ref budget);
                    }
                }
                catch (Exception e) { Log.Line(hive + " registry read failed: " + e.Message); }
            }

            // 3) the config file as the launcher sees it
            string dir = ContentDir();
            if (dir != null) Log.Line("MicrosoftGame.config executable now: " + LauncherConfig.Current(File.ReadAllText(Path.Combine(dir, "MicrosoftGame.config"))));
            string pkg; Log.Line("this process in package: " + (InPackage(out pkg) ? pkg : "no"));
            Log.Line("PROBE done.");
            return 0;
        }

        static void DumpMatching(Microsoft.Win32.RegistryKey k, string path, int depth, bool inside, ref int budget)
        {
            if (budget <= 0 || depth > 7) return;
            bool here = inside || path.IndexOf("fairlight", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("6BE41AB5", StringComparison.OrdinalIgnoreCase) >= 0;
            string[] vals;
            try { vals = k.GetValueNames(); } catch { vals = new string[0]; }
            foreach (var v in vals)
            {
                object o; try { o = k.GetValue(v); } catch { continue; }
                string s = o is string[] ? string.Join(" | ", (string[])o) : o is byte[] ? "<" + ((byte[])o).Length + " bytes>" : Convert.ToString(o, CultureInfo.InvariantCulture);
                bool hit = here || (s ?? "").IndexOf("fairlight", StringComparison.OrdinalIgnoreCase) >= 0 || (s ?? "").IndexOf("start_protected_game", StringComparison.OrdinalIgnoreCase) >= 0 || (s ?? "").IndexOf("GoWEDay", StringComparison.OrdinalIgnoreCase) >= 0;
                if (hit && budget-- > 0) Log.Line("reg " + path + "  " + (v.Length == 0 ? "(default)" : v) + " = " + s);
            }
            string[] subs;
            try { subs = k.GetSubKeyNames(); } catch { return; }
            foreach (var sk in subs)
            {
                try { using (var c = k.OpenSubKey(sk)) if (c != null) DumpMatching(c, path + "\\" + sk, depth + 1, here, ref budget); }
                catch (Exception e) { if (here) Log.Line("reg " + path + "\\" + sk + ": " + e.Message); }
            }
        }

        static string Args(string[] a)
        {
            if (a == null || a.Length == 0) return "";
            return string.Join(" ", a.Select(x => x.Length == 0 || x.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0 ? "\"" + x.Replace("\"", "\\\"") + "\"" : x));
        }

        // Kept for reference: package launch outside the Xbox launcher does NOT give the GDK runtime (tested 2026-10-10).
        static void LaunchInPackage(string exe)
        {
            string script =
                "$ErrorActionPreference = 'Stop'\n" +
                "try {\n" +
                "  $p = Get-AppxPackage -Name '" + PackageName + "' | Select-Object -First 1\n" +
                "  if (-not $p) { Write-Output 'Package " + PackageName + " is not registered for this Windows user.'; exit 3 }\n" +
                "  Write-Output ('Package: ' + $p.PackageFullName)\n" +
                "  Invoke-CommandInDesktopPackage -PackageFamilyName $p.PackageFamilyName -AppId '" + PackageAppId + "' -Command '" + exe.Replace("'", "''") + "' -PreventBreakaway\n" +
                "  Write-Output 'Invoke-CommandInDesktopPackage OK'\n" +
                "} catch { Write-Output ('Package launch failed: ' + $_.Exception.Message); exit 4 }\n";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            psi.EnvironmentVariables["EOS_USE_ANTICHEATCLIENTNULL"] = "1";
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                Log.Line("PowerShell exit " + p.ExitCode + (o.Trim().Length > 0 ? ": " + o.Trim().Replace("\r\n", " / ") : ""));
            }
        }

        // ------------------------------------------------------------------ cap discovery

        static CapSite FindCap(GameMemory m)
        {
            foreach (var lay in Sig.CapLayouts)
            {
                var pat = lay.P;
                var hits = new List<CapSite>();
                m.ForEachChunk(r => Native.Executable(r.Protect), (buf, len, maxStart, addr) =>
                {
                    foreach (int i in pat.FindAll(buf, len, maxStart))
                    {
                        long c = Sig.ResolveRip(buf, lay.CapDisp, lay.CapEnd, addr, i);
                        long fl = Sig.ResolveRip(buf, lay.FloorDisp, lay.FloorEnd, addr, i);
                        float v, fv;
                        try { v = m.ReadFloat(c); fv = m.ReadFloat(fl); } catch { continue; }
                        if (!Sig.SixteenNine(fv)) continue; // the cinematic clamp's floor is 16:9; other min/max pairs are not ours
                        Log.Debug(pat.Name + " hit at RVA 0x" + (addr + i - m.ModuleBase).ToString("X") + " -> const RVA 0x" + (c - m.ModuleBase).ToString("X") + " = " + v.ToString("R", CultureInfo.InvariantCulture));
                        hits.Add(new CapSite { Instr = addr + i, Const = c, Original = v, PatternName = pat.Name });
                    }
                });
                // Accept the constant if the game's value is a plausible ~21:9 cap, or already our wide value (re-attach).
                var good = hits.Where(h => Sig.CapOrPatched(h.Original)).ToList();
                var consts = good.Select(h => h.Const).Distinct().ToList();
                if (consts.Count == 1) return good[0];
                if (consts.Count > 1)
                {
                    var exact = good.Where(h => Maths.SameFloat(h.Original, Sig.SteamCap)).Select(h => h.Const).Distinct().ToList();
                    if (exact.Count == 1) return good.First(h => h.Const == exact[0]);
                    Log.Line(pat.Name + ": " + consts.Count + " different candidate constants - ambiguous, not patching:");
                    foreach (var h in good) Log.Line("   instr RVA 0x" + (h.Instr - m.ModuleBase).ToString("X") + " const RVA 0x" + (h.Const - m.ModuleBase).ToString("X") + " = " + h.Original);
                    return null;
                }
                if (hits.Count > 0) Log.Line(pat.Name + ": " + hits.Count + " pattern hit(s) but none points at a plausible cap value.");
            }
            return null;
        }

        static int CountRefs(GameMemory m, long target)
        {
            int n = 0;
            m.ForEachChunk(r => Native.Executable(r.Protect), (buf, len, maxStart, addr) => { n += Sig.CountRipRefs(buf, len, maxStart, addr, target); });
            return n;
        }

        static CapSite WaitForCap(GameMemory m, Process game)
        {
            var sw = Stopwatch.StartNew();
            bool said = false;
            while (!stopRequested)
            {
                if (game.HasExited) throw new InvalidOperationException("Game exited before the fix could be applied.");
                CapSite site = null;
                try { site = FindCap(m); } catch (Win32Exception e) { Log.Debug("scan: " + e.Message); }
                if (site != null) return site;
                if (sw.Elapsed.TotalSeconds > cfg.AttachTimeoutSeconds)
                    throw new InvalidOperationException("Cinematic aspect cap not found in this build. Double-click EDayUltrawide-Dump.exe while the game is running and check the log.");
                if (!said) { Log.Line("Waiting for the game code to unpack / signature to appear..."); said = true; }
                Thread.Sleep(1000);
            }
            return null;
        }

        // ------------------------------------------------------------------ main run

        static int RunAttached(Process game)
        {
            string why;
            if (AntiCheatActive(game, out why))
                throw new InvalidOperationException("Anti-cheat is active (" + why + "). This is an online launch - quit and press Play again without holding Shift (Mode=Offline).");
            var ci = CultureInfo.InvariantCulture;

            using (var m = new GameMemory(game, write: true))
            {
                Live.GamePid = game.Id; Live.GameAttached = true; Live.Phase = "Game found - waiting for it to unpack...";
                Log.Line("Game: " + Path.GetFileName(m.ModulePath) + " pid " + game.Id + "  version " + m.FileVersion + "  image 0x" + m.ModuleBase.ToString("X") + " size " + m.ModuleSize);
                var site = WaitForCap(m, game);
                if (site == null) return 0;
                site.RipRefs = CountRefs(m, site.Const);
                float original = Maths.SameFloat(site.Original, Sig.SteamCap) || Sig.PlausibleCap(site.Original) ? site.Original : Sig.SteamCap;
                Log.Line("Cap found (" + site.PatternName + "): code RVA 0x" + (site.Instr - m.ModuleBase).ToString("X") +
                         ", constant RVA 0x" + (site.Const - m.ModuleBase).ToString("X") + " = " + site.Original.ToString("0.000000", ci) +
                         "  (" + site.RipRefs + " code reference(s) to this constant)");
                if (site.RipRefs > 1) Log.Line("note: " + site.RipRefs + " possible references by byte scan (can include coincidental matches; Xbox build 1.1.3.0 has 1 real one).");
                Live.CapOriginal = original;

                // Cutscene camera hook (Hor+ / user zoom). Optional: if anything doesn't match, the bar fix still runs alone.
                CineHook hook = null;
                try
                {
                    string hw;
                    hook = CineHook.Locate(m, out hw);
                    if (hook == null) Log.Line("Cutscene FOV control unavailable: " + hw + ". Bars fix only.");
                    else
                    {
                        hook.Install(1f, false);
                        Log.Line("Cutscene FOV hook installed: GetCameraView RVA 0x" + (hook.Function - m.ModuleBase).ToString("X") + ", vtable slot RVA 0x" + (hook.Slot - m.ModuleBase).ToString("X") +
                                 (hook.Installed ? " - OK" : " - slot did not take"));
                        if (!hook.Installed) hook = null;
                    }
                }
                catch (Exception e) { Log.Line("Cutscene FOV hook failed: " + e.Message + ". Bars fix only."); hook = null; }
                Live.CutsceneHookOn = hook != null;

                // The game's vertical composition tilt for ultrawide crops (only meaningful with Hor+).
                long tiltConst = 0;
                if (hook != null)
                {
                    try
                    {
                        var t = new List<long>();
                        m.ForEachChunk(r => Native.Executable(r.Protect), (buf, len, maxStart, addr) =>
                        {
                            foreach (int i in HorPlus.TiltSite.FindAll(buf, len, maxStart))
                            {
                                long c = Sig.ResolveRip(buf, HorPlus.TiltDisp, HorPlus.TiltEnd, addr, i);
                                long h = Sig.ResolveRip(buf, HorPlus.TiltHalfDisp, HorPlus.TiltHalfEnd, addr, i);
                                float cv, hv;
                                try { cv = m.ReadFloat(c); hv = m.ReadFloat(h); } catch { continue; }
                                if (Math.Abs(hv - 0.5f) < 1e-6f && cv > 0f && cv <= HorPlus.TiltOriginal + 1e-6f) t.Add(c);
                            }
                        });
                        var distinct = t.Distinct().ToList();
                        if (distinct.Count == 1)
                        {
                            tiltConst = distinct[0];
                            Log.Line("Composition tilt found: constant RVA 0x" + (tiltConst - m.ModuleBase).ToString("X") + " = " + m.ReadFloat(tiltConst).ToString("0.######", ci));
                        }
                        else Log.Line("Composition tilt not found (" + distinct.Count + " candidates) - cutscenes may sit slightly low.");
                    }
                    catch (Exception e) { Log.Line("Composition tilt search failed: " + e.Message); }
                }

                // Gameplay FOV (camera manager multiplier).
                GameplayFov gp = null;
                try
                {
                    string gw;
                    gp = GameplayFov.Locate(m, out gw);
                    if (gp == null) Log.Line("Gameplay FOV control unavailable: " + gw + ".");
                    else Log.Line("Gameplay FOV setter found: RVA 0x" + (gp.Setter - m.ModuleBase).ToString("X") + ", vtable slot RVA 0x" + (gp.Slot - m.ModuleBase).ToString("X") + ".");
                }
                catch (Exception e) { Log.Line("Gameplay FOV setup failed: " + e.Message); gp = null; }
                Live.GameplayAvailable = gp != null;

                bool fixOn = true;           // F8 toggles the whole fix; Enabled=0 in the settings does the same
                float applied = 0f, lastLogCap = -1f, lastLogK = -1f;
                int resets = 0;

                Action<bool, float> setTilt = (on, render) =>
                {
                    if (tiltConst == 0) return;
                    float want = on ? HorPlus.TiltCoefficient(render, cfg.CutsceneFovBasis) : HorPlus.TiltOriginal;
                    if (!Maths.SameFloat(m.ReadFloat(tiltConst), want)) m.WriteFloatVerified(tiltConst, want);
                };

                // Cap + cutscene camera, from the current settings. Cheap and idempotent: runs on every settings change.
                Action applyCutscenes = () =>
                {
                    var c = cfg;
                    bool on = fixOn && c.Enabled;
                    string src = "ForceAspectRatio";
                    float ar = c.ForceAspectRatio > 0 ? c.ForceAspectRatio : m.DetectAspect(out src);
                    float want = on ? Maths.CapFor(ar, original) : original;
                    if (!Maths.SameFloat(m.ReadFloat(site.Const), want)) m.WriteFloatVerified(site.Const, want);
                    applied = want; Live.Aspect = ar; Live.CapApplied = want; Live.FixActive = on;
                    if (!Maths.SameFloat(want, lastLogCap))
                    {
                        lastLogCap = want;
                        Log.Line(on ? "APPLIED: cinematic cap " + original.ToString("0.0000", ci) + " -> " + want.ToString("0.0000", ci) + "  (aspect " + ar.ToString("0.0000", ci) + " from " + src + "); read-back OK"
                                    : "RESTORED: original cinematic cap " + original.ToString("0.0000", ci) + " (bars back).");
                    }
                    if (hook != null)
                    {
                        float render = Math.Min(ar, want);
                        float kHor = c.CutsceneHorPlus ? HorPlus.K(render, c.CutsceneFovBasis) : 1f;
                        float k = kHor * (c.CutsceneFovPercent / 100f);
                        bool hookOn = on && Math.Abs(k - 1f) > 0.0005f;
                        hook.SetK(k); hook.SetEnabled(hookOn);
                        setTilt(on && kHor > 1.0001f, render);
                        Live.CutsceneK = hookOn ? k : 1f;
                        if (!Maths.SameFloat(hookOn ? k : 1f, lastLogK))
                        {
                            lastLogK = hookOn ? k : 1f;
                            Log.Line("Cutscene FOV: " + (hookOn ? "K=" + k.ToString("0.0000", ci) + " (Hor+ " + kHor.ToString("0.0000", ci) + " x zoom " + c.CutsceneFovPercent.ToString("0.#", ci) + "%; e.g. 40.0 deg -> " + HorPlus.Widen(40, k).ToString("0.0", ci) + " deg)" : "game's own"));
                        }
                    }
                };

                int seenVersion = Settings.Version;
                applyCutscenes();
                Live.Phase = "Fix active";
                Log.Line("Running. " + cfg.ToggleKey + " = toggle fix, " + cfg.StatusKey + " = status line in the log. Settings changed in the window apply immediately.");

                int vkToggle = Keys.VirtualKey(cfg.ToggleKey), vkStatus = Keys.VirtualKey(cfg.StatusKey);
                bool tDown = false, sDown = false;
                long lastAdjusted = 0; float lastLoggedFov = -1f;
                var slow = Stopwatch.StartNew(); var mid = Stopwatch.StartNew(); var gpClock = Stopwatch.StartNew();
                bool gpWasOn = false;
                while (!stopRequested)
                {
                    if (game.WaitForExit(50)) { Log.Line("Game exited. Bye."); return 0; }
                    bool fg = m.IsForeground();
                    bool t = vkToggle != 0 && (Native.GetAsyncKeyState(vkToggle) & 0x8000) != 0;
                    bool s = vkStatus != 0 && (Native.GetAsyncKeyState(vkStatus) & 0x8000) != 0;
                    try
                    {
                        if (t && !tDown && fg) { fixOn = !fixOn; Log.Line("Fix " + (fixOn ? "ON" : "OFF") + " (" + cfg.ToggleKey + ")."); applyCutscenes(); }
                        if (s && !sDown && fg)
                        {
                            float cur = m.ReadFloat(site.Const);
                            string a; float ar = m.DetectAspect(out a);
                            Log.Line("STATUS: fix " + (Live.FixActive ? "ON" : "OFF") + ", cap now " + cur.ToString("0.0000", ci) +
                                     ", screen " + ar.ToString("0.0000", ci) + " (" + a + "), game-side resets seen: " + resets);
                            if (hook != null)
                            {
                                long c1, a1; float li, lo; hook.Stats(out c1, out a1, out li, out lo);
                                Log.Line("STATUS: cutscene camera views " + c1 + ", widened " + a1 + ", last FOV " + li.ToString("0.0", ci) + " -> " + lo.ToString("0.0", ci) + " (K=" + Live.CutsceneK.ToString("0.0000", ci) + ")");
                            }
                            if (gp != null)
                            {
                                int n; float bf, ff; gp.Snapshot(out n, out bf, out ff);
                                Log.Line("STATUS: gameplay camera managers " + n + ", base FOV " + bf.ToString("0.#", ci) + ", final " + ff.ToString("0.#", ci));
                            }
                        }

                        if (gpClock.ElapsedMilliseconds >= 250)
                        {
                            gpClock.Restart();
                            if (gp != null)
                            {
                                var c = cfg;
                                float want = fixOn && c.Enabled ? c.GameplayFov : 0f;
                                if (want > 0f || gpWasOn) gp.Tick(want);
                                gpWasOn = want > 0f;
                                int n; float bf, ff; gp.Snapshot(out n, out bf, out ff);
                                Live.GameplayObjects = n; Live.GameplayBase = bf; Live.GameplayFinal = ff;
                            }
                        }

                        if (mid.ElapsedMilliseconds >= 400)
                        {
                            mid.Restart();
                            Settings.PollFile();
                            if (Settings.Version != seenVersion) { seenVersion = Settings.Version; applyCutscenes(); }
                            if (hook != null)
                            {
                                long c1, a1; float li, lo; hook.Stats(out c1, out a1, out li, out lo);
                                Live.CineViews = c1; Live.CineAdjusted = a1; Live.CineIn = li; Live.CineOut = lo;
                            }
                        }

                        if (slow.ElapsedMilliseconds >= 2000)
                        {
                            slow.Restart();
                            if (hook != null)
                            {
                                long c1, a1; float li, lo; hook.Stats(out c1, out a1, out li, out lo);
                                if (a1 > lastAdjusted && Math.Abs(li - lastLoggedFov) > 0.5f)
                                {
                                    Log.Line("Cutscene camera: FOV " + li.ToString("0.0", ci) + " -> " + lo.ToString("0.0", ci) + " deg (" + (a1 - lastAdjusted) + " frames since last line)");
                                    lastLoggedFov = li;
                                }
                                lastAdjusted = a1;
                            }
                            if (Live.FixActive)
                            {
                                float cur = m.ReadFloat(site.Const);
                                if (!Maths.SameFloat(cur, applied)) { resets++; Log.Line("Cap was changed by the game (" + cur + "); re-applying (#" + resets + ")."); lastLogCap = -1f; applyCutscenes(); }
                                else if (cfg.ForceAspectRatio <= 0)
                                {
                                    string a; float ar = m.DetectAspect(out a);
                                    float want = Maths.CapFor(ar, original);
                                    if (Math.Abs(want - applied) > 0.002f) { Log.Line("Resolution changed (" + a + ")."); applyCutscenes(); }
                                }
                            }
                            Live.Publish(DataDir);
                        }
                    }
                    catch (Exception e)
                    {
                        if (game.HasExited) { Log.Line("Game exited. Bye."); return 0; }
                        Log.Line("loop error: " + e.Message);
                        Thread.Sleep(1000);
                    }
                    tDown = t; sDown = s;
                }
                try { fixOn = false; applyCutscenes(); } catch (Exception e) { Log.Line("restore failed: " + e.Message); }
                try { if (gp != null) gp.Restore(); } catch (Exception e) { Log.Line("gameplay restore failed: " + e.Message); }
                try { if (hook != null) { hook.Uninstall(); Log.Line("Cutscene FOV hook removed."); } } catch (Exception e) { Log.Line("hook removal failed: " + e.Message); }
                return 0;
            }
        }

        static int Status()
        {
            var g = FindGame();
            if (g == null) { Log.Line("Game is not running."); return 1; }
            string why;
            if (AntiCheatActive(g, out why)) { Log.Line("Anti-cheat active: " + why); return 1; }
            using (var m = new GameMemory(g, write: false))
            {
                var site = FindCap(m);
                if (site == null) { Log.Line("Cap not found."); return 1; }
                Log.Line("Cap constant RVA 0x" + (site.Const - m.ModuleBase).ToString("X") + " = " + site.Original + "  (" + CountRefs(m, site.Const) + " refs)");
            }
            return 0;
        }

        // ------------------------------------------------------------------ dump (for porting the cutscene-FOV correction)

        static int Dump(string dir)
        {
            var g = FindGame();
            if (g == null) throw new InvalidOperationException("Start the game first (with Launch-EDay-Ultrawide.bat), get to the main menu, then run the dump.");
            string why;
            if (AntiCheatActive(g, out why)) throw new InvalidOperationException("Anti-cheat active (" + why + ").");
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(dir, "image.*")) File.Delete(f);
            const int Part = 48 * 1024 * 1024;
            using (var m = new GameMemory(g, write: false))
            {
                var regions = m.ModuleRegions();
                var info = new StringBuilder();
                info.AppendLine("exe=" + m.ModulePath);
                info.AppendLine("version=" + m.FileVersion);
                info.AppendLine("base=0x" + m.ModuleBase.ToString("X"));
                info.AppendLine("size=" + m.ModuleSize);
                info.AppendLine("partsize=" + Part);
                foreach (var r in regions) info.AppendLine("region rva=0x" + (r.Base - m.ModuleBase).ToString("X") + " size=0x" + r.Size.ToString("X") + " protect=0x" + r.Protect.ToString("X"));
                var buf = new byte[1 << 20];
                int partNo = 0; long written = 0, unreadable = 0;
                FileStream fs = null;
                for (long rva = 0; rva < m.ModuleSize; rva += buf.Length)
                {
                    if (rva % Part == 0) { if (fs != null) fs.Dispose(); fs = File.Create(Path.Combine(dir, "image." + (partNo++).ToString("000"))); }
                    int len = (int)Math.Min(buf.Length, m.ModuleSize - rva);
                    // Copy readable sub-ranges page by page; leave the rest zeroed so file offset == RVA.
                    Array.Clear(buf, 0, len);
                    for (int p = 0; p < len; p += 4096)
                    {
                        int pl = Math.Min(4096, len - p);
                        var page = new byte[pl];
                        if (m.TryRead(m.ModuleBase + rva + p, page, pl)) Buffer.BlockCopy(page, 0, buf, p, pl); else unreadable += pl;
                    }
                    fs.Write(buf, 0, len);
                    written += len;
                }
                if (fs != null) fs.Dispose();
                File.WriteAllText(Path.Combine(dir, "image.txt"), info.ToString());
                Log.Line("Dumped " + written / (1024 * 1024) + " MB in " + partNo + " parts to " + dir + " (" + unreadable / 4096 + " unreadable pages zero-filled).");
            }
            return 0;
        }
    }
}
