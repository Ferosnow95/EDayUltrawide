// EDayUltrawide - independent gameplay FOV (writes the camera manager's FOV multiplier; no game code is modified).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace EDayUltrawide
{
    /// <summary>
    /// The gameplay camera manager stores base FOV (+0x340) and a multiplier (+0x5EA8); the final FOV is base x multiplier and the in-game
    /// FOV slider (60-90) just sets the multiplier through a virtual setter. This finds the live manager object(s) in the heap and keeps the
    /// multiplier at desired/base. 0 = leave the game's own value. The object is found by its vtable (derived from the setter's vtable slot)
    /// plus plausibility of the two floats, so nothing is hard-coded to one build.
    /// </summary>
    internal sealed class GameplayFov
    {
        sealed class Obj { public long Addr; public float Original, Applied; public bool KnownOriginal; }

        readonly GameMemory m;
        readonly long vLo, vHi;
        long vptr;
        readonly Dictionary<long, Obj> objs = new Dictionary<long, Obj>();
        Task<List<long>> scan;
        readonly Stopwatch sinceScan = Stopwatch.StartNew();
        int backoffMs = 2000;
        float lastAppliedMult;
        public int Scans;

        public long Setter, Slot;
        public int Count { get { return objs.Count; } }
        public float LastBase, LastMult;

        GameplayFov(GameMemory mem, long setter, long slot)
        {
            m = mem; Setter = setter; Slot = slot;
            vLo = slot - 0x1000; vHi = slot - 0x100; // the object's vtable starts a few hundred bytes before the setter's slot
        }

        /// <summary>Finds the FOV setter (exactly one) and its vtable slot (exactly one). Null + reason otherwise.</summary>
        public static GameplayFov Locate(GameMemory m, out string why)
        {
            why = null;
            var fns = new List<long>();
            m.ForEachChunk(r => Native.Executable(r.Protect), (buf, len, maxStart, addr) =>
            {
                foreach (int i in GameplayScan.Setter.FindAll(buf, len, maxStart)) fns.Add(addr + i);
            });
            if (fns.Count != 1) { why = "gameplay FOV setter: " + fns.Count + " matches (need exactly 1)"; return null; }
            var slots = new List<long>();
            m.ForEachChunk(r => !Native.Executable(r.Protect), (buf, len, maxStart, addr) => slots.AddRange(HorPlus.FindQword(buf, len, maxStart, addr, fns[0])));
            if (slots.Count != 1) { why = "gameplay FOV setter vtable slot: " + slots.Count + " pointers (need exactly 1)"; return null; }
            return new GameplayFov(m, fns[0], slots[0]);
        }

        List<long> ScanHeap()
        {
            var found = new List<long>();
            m.ForEachHeapChunk(0x6000, null, (buf, len, maxStart, addr) =>
            {
                foreach (int i in GameplayScan.FindObjects(buf, len, maxStart, vLo, vHi)) found.Add(addr + i);
            });
            return found;
        }

        bool Read(long addr, out long vp, out float b, out float mult)
        {
            vp = 0; b = mult = 0f;
            try
            {
                vp = m.ReadInt64(addr); b = m.ReadFloat(addr + GameplayScan.BaseOff); mult = m.ReadFloat(addr + GameplayScan.MultOff);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Call every ~250 ms. desired: 0 = game's own FOV, otherwise degrees on the game's slider scale.</summary>
        public void Tick(float desired)
        {
            // 1) drop objects that vanished or stopped looking like the manager
            foreach (var addr in objs.Keys.ToList())
            {
                long vp; float b, mu;
                if (!Read(addr, out vp, out b, out mu) || vp != vptr || !GameplayScan.PlausibleBase(b) || !GameplayScan.PlausibleMult(mu)) objs.Remove(addr);
            }

            // 2) harvest a finished scan
            if (scan != null && scan.IsCompleted)
            {
                List<long> found = null;
                try { found = scan.Result; } catch (Exception e) { Log.Line("Gameplay FOV scan failed: " + (e.InnerException ?? e).Message); }
                scan = null; sinceScan.Restart();
                if (found != null) Adopt(found);
            }

            // 3) look for the manager when we have none (throttled: heap scans cost a couple of seconds)
            if (desired > 0f && objs.Count == 0 && scan == null && sinceScan.ElapsedMilliseconds >= backoffMs)
            {
                Scans++;
                scan = Task.Run(() => ScanHeap());
            }

            // 4) keep the multiplier
            foreach (var o in objs.Values.ToList())
            {
                long vp; float b, mu;
                if (!Read(o.Addr, out vp, out b, out mu)) continue;
                LastBase = b; LastMult = mu;
                bool ours = Maths.SameFloat(mu, o.Applied);
                if (!ours)
                {
                    // The game changed it (options menu / new level): its value is the new "original" if it is in the game's own range.
                    if (!GameplayScan.Strict(b, mu)) continue;
                    o.Original = mu; o.KnownOriginal = true;
                }
                if (desired <= 0f)
                {
                    if (ours && o.KnownOriginal && !Maths.SameFloat(mu, o.Original)) { Write(o, o.Original, "restored to the game's own FOV " + (b * o.Original).ToString("0.#", CultureInfo.InvariantCulture)); }
                    continue;
                }
                float want = desired / b;
                if (!GameplayScan.PlausibleMult(want)) continue;
                if (!Maths.SameFloat(mu, want)) Write(o, want, "FOV " + (b * mu).ToString("0.#", CultureInfo.InvariantCulture) + " -> " + desired.ToString("0.#", CultureInfo.InvariantCulture) + " deg (multiplier " + mu.ToString("0.####", CultureInfo.InvariantCulture) + " -> " + want.ToString("0.####", CultureInfo.InvariantCulture) + ", base " + b.ToString("0.#", CultureInfo.InvariantCulture) + ")");
                o.Applied = want; lastAppliedMult = want;
            }
        }

        void Write(Obj o, float mult, string what)
        {
            try
            {
                m.WriteFloatVerified(o.Addr + GameplayScan.MultOff, mult);
                o.Applied = mult;
                Log.Line("Gameplay camera 0x" + o.Addr.ToString("X") + ": " + what);
            }
            catch (Exception e) { Log.Line("Gameplay FOV write failed at 0x" + o.Addr.ToString("X") + ": " + e.Message); }
        }

        void Adopt(List<long> found)
        {
            var good = new List<KeyValuePair<long, long>>(); // address, vtable
            foreach (var a in found)
            {
                long vp; float b, mu;
                if (!Read(a, out vp, out b, out mu)) continue;
                // untouched objects must show the game's own slider range; ones we already changed show our last multiplier
                bool ok = GameplayScan.Strict(b, mu) || (lastAppliedMult > 0f && Maths.SameFloat(mu, lastAppliedMult));
                if (ok) good.Add(new KeyValuePair<long, long>(a, vp));
            }
            Log.Line("Gameplay FOV scan #" + Scans + ": " + found.Count + " candidate(s), " + good.Count + " plausible.");
            if (good.Count == 0) { backoffMs = Math.Min(backoffMs * 2, 30000); return; }
            // all instances of the manager class share one vtable: take the most common one
            vptr = vptr != 0 && good.Any(g => g.Value == vptr) ? vptr : good.GroupBy(g => g.Value).OrderByDescending(g => g.Count()).First().Key;
            backoffMs = 2000;
            foreach (var g in good.Where(g => g.Value == vptr).Take(8))
            {
                if (objs.ContainsKey(g.Key)) continue;
                long vp; float b, mu; Read(g.Key, out vp, out b, out mu);
                bool strict = GameplayScan.Strict(b, mu);
                objs[g.Key] = new Obj { Addr = g.Key, Original = strict ? mu : 0f, Applied = strict ? mu : mu, KnownOriginal = strict };
                Log.Line("Gameplay camera manager at 0x" + g.Key.ToString("X") + " (vtable RVA 0x" + (vptr - m.ModuleBase).ToString("X") + "): base FOV " +
                         b.ToString("0.#", CultureInfo.InvariantCulture) + " x multiplier " + mu.ToString("0.####", CultureInfo.InvariantCulture) + " = " + (b * mu).ToString("0.#", CultureInfo.InvariantCulture) + " deg");
            }
        }

        /// <summary>Puts the game's own multiplier back wherever we changed it.</summary>
        public void Restore()
        {
            foreach (var o in objs.Values.ToList())
            {
                long vp; float b, mu;
                if (Read(o.Addr, out vp, out b, out mu) && vp == vptr && o.KnownOriginal && Maths.SameFloat(mu, o.Applied) && !Maths.SameFloat(mu, o.Original))
                    Write(o, o.Original, "restored to the game's own FOV");
            }
        }

        /// <summary>Current game-side values for the UI (0 when no manager is known).</summary>
        public void Snapshot(out int count, out float baseFov, out float finalFov)
        {
            count = objs.Count; baseFov = LastBase; finalFov = LastBase * LastMult;
        }
    }
}
