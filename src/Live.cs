// EDayUltrawide - state shared between the fix engine (background thread) and the window.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace EDayUltrawide
{
    /// <summary>Engine -> window status. Plain volatile fields; the window polls them.</summary>
    internal static class Live
    {
        public static volatile string Phase = "Idle";          // short human text
        public static volatile string Error;                    // last fatal engine error (null = none)
        public static volatile bool EngineRunning;              // engine thread alive in THIS process
        public static volatile bool GameAttached;
        public static volatile bool FixActive;
        public static volatile bool CutsceneHookOn;             // vtable hook installed
        public static volatile bool GameplayAvailable;          // setter + slot located
        public static int GamePid;
        public static float Aspect, CapApplied, CapOriginal, CutsceneK;
        public static long CineViews, CineAdjusted; public static float CineIn, CineOut;
        public static int GameplayObjects; public static float GameplayBase, GameplayFinal;
        public static volatile bool Exited;                     // engine finished (game closed)

        const string StatusFile = "EDayUltrawide.status";

        /// <summary>Engine process: publish a snapshot so a second window (opened by hand) can show it.</summary>
        public static void Publish(string dir)
        {
            try
            {
                var ci = CultureInfo.InvariantCulture; var b = new StringBuilder();
                b.AppendLine("time=" + DateTime.UtcNow.Ticks);
                b.AppendLine("phase=" + Phase);
                b.AppendLine("attached=" + (GameAttached ? 1 : 0));
                b.AppendLine("pid=" + GamePid);
                b.AppendLine("fix=" + (FixActive ? 1 : 0));
                b.AppendLine("cine=" + (CutsceneHookOn ? 1 : 0));
                b.AppendLine("gpavail=" + (GameplayAvailable ? 1 : 0));
                b.AppendLine("aspect=" + Aspect.ToString("0.####", ci));
                b.AppendLine("k=" + CutsceneK.ToString("0.####", ci));
                b.AppendLine("cineviews=" + CineViews);
                b.AppendLine("cinein=" + CineIn.ToString("0.#", ci));
                b.AppendLine("cineout=" + CineOut.ToString("0.#", ci));
                b.AppendLine("gpobjs=" + GameplayObjects);
                b.AppendLine("gpbase=" + GameplayBase.ToString("0.#", ci));
                b.AppendLine("gpfinal=" + GameplayFinal.ToString("0.#", ci));
                File.WriteAllText(Path.Combine(dir, StatusFile), b.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        public static void Clear(string dir) { try { File.Delete(Path.Combine(dir, StatusFile)); } catch { } }

        /// <summary>Window without an engine: read the other process's snapshot (null if absent or older than 6 s).</summary>
        public static Dictionary<string, string> ReadRemote(string dir)
        {
            try
            {
                string path = Path.Combine(dir, StatusFile);
                if (!File.Exists(path)) return null;
                var d = new Dictionary<string, string>();
                foreach (var line in File.ReadAllLines(path)) { int eq = line.IndexOf('='); if (eq > 0) d[line.Substring(0, eq)] = line.Substring(eq + 1); }
                long ticks; if (!d.ContainsKey("time") || !long.TryParse(d["time"], out ticks)) return null;
                if ((DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds > 6) return null;
                return d;
            }
            catch { return null; }
        }
    }

    /// <summary>The settings file, shared by the window and the engine (and by a second window in another process).</summary>
    internal static class Settings
    {
        static readonly object gate = new object();
        static Config current = new Config();
        static DateTime stamp = DateTime.MinValue;
        static string path;
        public static int Version;                    // bumped on every change (window edit or file edit)
        public static event Action Changed;           // may fire on any thread

        public static Config Current { get { lock (gate) return current; } }
        public static string Path_ { get { return path; } }

        public static List<string> Init(string dir)
        {
            path = System.IO.Path.Combine(dir, "EDayUltrawide.ini");
            var warn = new List<string>();
            lock (gate)
            {
                current = Config.Parse(File.Exists(path) ? File.ReadAllText(path) : null, warn);
                stamp = Stamp();
            }
            return warn;
        }

        static DateTime Stamp() { try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; } catch { return DateTime.MinValue; } }

        /// <summary>Window edit: keep in memory and write the file (so the next launch and other windows see it).</summary>
        public static void Update(Config next)
        {
            lock (gate)
            {
                current = next;
                try { File.WriteAllText(path, next.ToIni(), new UTF8Encoding(false)); stamp = Stamp(); }
                catch (Exception e) { Log.Line("Could not save settings: " + e.Message); }
                Version++;
            }
            var h = Changed; if (h != null) h();
        }

        /// <summary>Call now and then: picks up edits made by another process / by hand.</summary>
        public static void PollFile()
        {
            DateTime s = Stamp();
            if (s == stamp || s == DateTime.MinValue) return;
            var warn = new List<string>();
            try
            {
                string text = File.ReadAllText(path);
                lock (gate) { current = Config.Parse(text, warn); stamp = s; Version++; }
            }
            catch (IOException) { return; } // being written right now; try again next poll
            foreach (var w in warn) Log.Line("config: " + w);
            var h = Changed; if (h != null) h();
        }
    }
}
