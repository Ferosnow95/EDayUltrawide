// EDayUltrawide - install / update / uninstall (the one exe is its own setup program).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;

namespace EDayUltrawide
{
    internal sealed class SetupInfo
    {
        public string Content, WinGdk;
        public bool ExeExists, ConfigHooked, RegistryHooked, GameRunning, AppRunning;
        public string InstalledVersion;
        public bool Any { get { return ExeExists || ConfigHooked || RegistryHooked; } }
        public bool Complete { get { return ExeExists && ConfigHooked && RegistryHooked; } }
    }

    internal static partial class Program
    {
        const string InstalledName = "EDayUltrawide.exe";

        static string WinGdkOf(string content) { return Path.Combine(content, @"FairlightConcept\Binaries\WinGDK"); }

        /// <summary>True when this exe sits in the game's WinGDK folder (the installed copy).</summary>
        static bool IsInstalledLocation()
        {
            try
            {
                if (BaseDir.IndexOf(@"\FairlightConcept\Binaries\WinGDK", StringComparison.OrdinalIgnoreCase) < 0) return false;
                return ContentDir() != null;
            }
            catch { return false; }
        }

        internal static bool IsContentDir(string dir)
        {
            try
            {
                return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "MicrosoftGame.config")) &&
                       File.Exists(Path.Combine(WinGdkOf(dir), "GoWEDay.exe"));
            }
            catch { return false; }
        }

        /// <summary>The game's Content folder(s): the usual Xbox library locations on every fixed drive, then Gaming Services' package roots.</summary>
        internal static List<string> FindContentDirs()
        {
            var res = new List<string>();
            Action<string> add = p =>
            {
                if (string.IsNullOrEmpty(p)) return;
                p = p.Replace(@"\\?\", "").TrimEnd('\\');
                if (IsContentDir(p) && !res.Any(r => string.Equals(r, p, StringComparison.OrdinalIgnoreCase))) res.Add(p);
            };
            try
            {
                foreach (var d in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
                    foreach (var sub in new[] { @"XboxGames\Gears of War- E-Day\Content", @"Games\Gears of War- E-Day\Content", @"Gears of War- E-Day\Content",
                                                @"Xbox Games\Gears of War- E-Day\Content", @"XboxGames\Gears of War E-Day\Content", @"Games\Gears of War E-Day\Content" })
                        add(Path.Combine(d.Name, sub));
            }
            catch { }
            if (res.Count == 0)
            {
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
                                            if (pk != null && r != null && pk.StartsWith(PackageName + "_", StringComparison.OrdinalIgnoreCase)) add(r);
                                        }
                }
                catch { }
            }
            return res;
        }

        internal static SetupInfo Inspect(string content)
        {
            var i = new SetupInfo { Content = content, WinGdk = WinGdkOf(content) };
            try
            {
                string exe = Path.Combine(i.WinGdk, InstalledName);
                i.ExeExists = File.Exists(exe);
                if (i.ExeExists) { try { i.InstalledVersion = FileVersionInfo.GetVersionInfo(exe).FileVersion; } catch { } }
                string cfg = File.ReadAllText(Path.Combine(content, "MicrosoftGame.config"));
                i.ConfigHooked = (LauncherConfig.Current(cfg) ?? "").IndexOf("EDayUltrawide", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { }
            try { string key; i.RegistryHooked = (ReadLaunchTarget(out key) ?? "").IndexOf("EDayUltrawide", StringComparison.OrdinalIgnoreCase) >= 0; } catch { }
            i.GameRunning = FindGameRunning();
            try
            {
                int me = Process.GetCurrentProcess().Id;
                i.AppRunning = Process.GetProcessesByName("EDayUltrawide").Any(p => p.Id != me);
            }
            catch { }
            return i;
        }

        internal static string MyVersion { get { return Version; } }

        static bool IsAdmin()
        {
            try { using (var id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); Log.Line("removed " + path); } }
            catch (Exception e) { Log.Line("could not remove " + path + ": " + e.Message); }
        }

        // ------------------------------------------------------------------ elevated steps (run by "--do-install" / "--do-uninstall")

        /// <summary>Copies this exe into the game's WinGDK folder and points the Xbox launch at it. Needs administrator rights (registry).</summary>
        static int SetupInstall(string content)
        {
            try
            {
                if (!IsContentDir(content)) throw new InvalidOperationException("That is not the Gears of War: E-Day game folder (no Content\\MicrosoftGame.config / WinGDK\\GoWEDay.exe): " + content);
                contentOverride = content;
                if (FindGameRunning()) throw new InvalidOperationException("The game is running. Close it completely, then try again.");
                string win = WinGdkOf(content), dest = Path.Combine(win, InstalledName), self = System.Reflection.Assembly.GetEntryAssembly().Location;
                Log.Line("Installing v" + Version + " into " + win);
                if (!SamePath(self, dest))
                {
                    try { File.Copy(self, dest, true); }
                    catch (IOException e) { throw new InvalidOperationException("EDayUltrawide.exe in the game folder is in use (" + e.Message + "). Close the game and the Xbox app's launch window, then try again."); }
                }
                Log.Line("Copied " + dest);
                // files left by earlier test builds
                foreach (var f in new[] { "EDayUltrawide.exe.config", "EDayUltrawide-README.txt", "Dump-Memory.bat", "Launch-EDay-Ultrawide.bat", "EDayUltrawide.admin.log" }) TryDelete(Path.Combine(win, f));
                try { foreach (var f in Directory.GetFiles(win, "EDayUltrawide-*.exe*")) TryDelete(f); } catch { }
                int r = Hook(true, true);
                if (r != 0) throw new InvalidOperationException("Could not set the Xbox launch entry (see " + Log.FilePath + ").");
                Log.Line("INSTALL OK");
                return 0;
            }
            catch (Exception e) { Log.Line("ERROR: " + e.Message); return 1; }
        }

        /// <summary>Puts the original launcher back and deletes everything the mod put in the game folder.</summary>
        static int SetupUninstall(string content, bool detached = false)
        {
            try
            {
                if (detached) WaitForOtherCopies();
                if (!IsContentDir(content)) throw new InvalidOperationException("That is not the Gears of War: E-Day game folder: " + content);
                contentOverride = content;
                if (FindGameRunning()) throw new InvalidOperationException("The game is running. Close it completely, then try again.");
                string win = WinGdkOf(content);
                Log.Line("Uninstalling from " + win);
                int r = Hook(false, true);
                if (r != 0) throw new InvalidOperationException("Could not restore the Xbox launch entry (see " + Log.FilePath + "). Nothing was deleted.");
                string cfgPath = Path.Combine(content, "MicrosoftGame.config");
                // the file is back to the original launcher: the saved copy is not needed any more
                try { if ((LauncherConfig.Current(File.ReadAllText(cfgPath)) ?? "").IndexOf("EDayUltrawide", StringComparison.OrdinalIgnoreCase) < 0) TryDelete(cfgPath + ".edayultrawide-original"); } catch { }
                string self = System.Reflection.Assembly.GetEntryAssembly().Location;
                foreach (var pat in new[] { "EDayUltrawide*.exe", "EDayUltrawide*.config", "EDayUltrawide*.ini", "EDayUltrawide*.log", "EDayUltrawide*.status", "EDayUltrawide*.txt", "Dump-Memory.bat", "Launch-EDay-Ultrawide.bat" })
                    foreach (var f in SafeFiles(win, pat))
                        if (!SamePath(f, self)) TryDelete(f);
                string dump = Path.Combine(win, "dump");
                try
                {
                    if (Directory.Exists(dump) && Directory.GetFiles(dump).All(f => Path.GetFileName(f).StartsWith("image.", StringComparison.OrdinalIgnoreCase)))
                    { Directory.Delete(dump, true); Log.Line("removed " + dump); }
                }
                catch (Exception e) { Log.Line("could not remove the dump folder: " + e.Message); }
                if (File.Exists(Path.Combine(win, InstalledName))) throw new InvalidOperationException("EDayUltrawide.exe is still in use and could not be deleted. Close it and run Uninstall again.");
                Log.Line("UNINSTALL OK");
                if (detached) { Say("E-Day Ultrawide was removed. The Xbox app starts the game normally again.", false); CleanUpSelf(); }
                return 0;
            }
            catch (Exception e)
            {
                Log.Line("ERROR: " + e.Message);
                if (detached) { Say("Uninstall failed: " + e.Message, true); CleanUpSelf(); }
                return 1;
            }
        }

        static void WaitForOtherCopies()
        {
            try
            {
                int me = Process.GetCurrentProcess().Id;
                for (int i = 0; i < 40 && Process.GetProcessesByName("EDayUltrawide").Any(p => p.Id != me); i++) System.Threading.Thread.Sleep(250);
            }
            catch { }
        }

        static void Say(string text, bool error)
        {
            try { System.Windows.Forms.MessageBox.Show(text, "E-Day Ultrawide", System.Windows.Forms.MessageBoxButtons.OK, error ? System.Windows.Forms.MessageBoxIcon.Error : System.Windows.Forms.MessageBoxIcon.Information); } catch { }
        }

        /// <summary>The temporary uninstaller copy removes itself a moment after it exits.</summary>
        static void CleanUpSelf()
        {
            try
            {
                string self = System.Reflection.Assembly.GetEntryAssembly().Location;
                if (Path.GetFileName(self).StartsWith("EDayUltrawide-Uninstall-", StringComparison.OrdinalIgnoreCase))
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + self + "\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }
        }

        static string[] SafeFiles(string dir, string pattern) { try { return Directory.GetFiles(dir, pattern); } catch { return new string[0]; } }

        // ------------------------------------------------------------------ helpers for the windows

        /// <summary>Last "ERROR:" line the elevated step wrote (for the window), or null.</summary>
        internal static string LastSetupError()
        {
            try
            {
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EDayUltrawide", "EDayUltrawide.setup.log");
                if (!File.Exists(p)) return null;
                using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    string last = null, line;
                    while ((line = sr.ReadLine()) != null) { int k = line.IndexOf("ERROR: ", StringComparison.Ordinal); if (k >= 0) last = line.Substring(k + 7); }
                    return last;
                }
            }
            catch { return null; }
        }

        /// <summary>Runs "exe args" as administrator (one Windows prompt) and waits. -1223 = prompt declined.</summary>
        internal static int RunElevated(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(exe) };
                using (var p = Process.Start(psi)) { p.WaitForExit(); return p.ExitCode; }
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return -1223; }
        }

        static string Q(string s) { return "\"" + s + "\""; }

        /// <summary>Install or update: elevated copy of this exe + launch hook. Returns null on success, else a message.</summary>
        internal static string InstallElevated(string content)
        {
            string self = System.Reflection.Assembly.GetEntryAssembly().Location;
            int r = RunElevated(self, "--do-install " + Q(content));
            if (r == 0) return null;
            if (r == -1223) return "The Windows permission prompt was declined, so nothing was changed.";
            return LastSetupError() ?? "The install did not finish (exit code " + r + ").";
        }

        /// <summary>Uninstall from a temporary copy (the installed exe can't delete itself). Returns null on success, else a message.</summary>
        internal static string UninstallElevated(string content)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "EDayUltrawide-Uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
            try
            {
                File.Copy(System.Reflection.Assembly.GetEntryAssembly().Location, tmp, true);
                int r = RunElevated(tmp, "--do-uninstall " + Q(content));
                if (r == 0) return null;
                if (r == -1223) return "The Windows permission prompt was declined, so nothing was changed.";
                return LastSetupError() ?? "The uninstall did not finish (exit code " + r + ").";
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        internal static void LaunchGameViaXbox()
        {
            Process.Start("explorer.exe", @"shell:AppsFolder\" + PackageFamily + "!" + PackageAppId);
        }

        /// <summary>Settings window of the installed copy (works without the game running).</summary>
        internal static void OpenInstalledApp(string content)
        {
            string exe = Path.Combine(WinGdkOf(content), InstalledName);
            if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false });
        }

        // ------------------------------------------------------------------ what the window's buttons call

        /// <summary>"Install hook": from the installed copy it only (re)sets the hook; from anywhere else it also copies this exe into the game folder.</summary>
        internal static string InstallFromWindow()
        {
            string c = ContentDir();
            if (c == null) return "The game folder was not found. Use \"Choose game folder\".";
            if (IsInstalledLocation()) return Hook(true) == 0 ? null : "The hook could not be installed (see the log).";
            return InstallElevated(c);
        }

        /// <summary>"Remove hook": restores the Xbox launch and deletes the mod's files. Run from the installed copy, the window closes and a temporary copy finishes the job. closeWindow is set in that case.</summary>
        internal static string RemoveFromWindow(out bool closeWindow)
        {
            closeWindow = false;
            string c = ContentDir();
            if (c == null) return "The game folder was not found. Use \"Choose game folder\".";
            if (!IsInstalledLocation()) return UninstallElevated(c);
            string tmp = Path.Combine(Path.GetTempPath(), "EDayUltrawide-Uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
            try
            {
                File.Copy(System.Reflection.Assembly.GetEntryAssembly().Location, tmp, true);
                Process.Start(new ProcessStartInfo(tmp, "--do-uninstall " + Q(c) + " --detached") { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetTempPath() });
                closeWindow = true;
                return null;
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) { try { File.Delete(tmp); } catch { } return "The Windows permission prompt was declined, so nothing was changed."; }
            catch (Exception e) { return e.Message; }
        }
    }
}
