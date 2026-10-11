// EDayUltrawide - cutscene Hor+ hook (vtable slot swap; game code is never modified).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EDayUltrawide
{
    internal sealed class CineHook
    {
        readonly GameMemory m;
        public long Function, Slot, Code, Cfg;
        public bool Installed;

        CineHook(GameMemory mem) { m = mem; }

        /// <summary>Finds the cine camera view function, proves the View.FOV offset and finds its single vtable slot. Null + reason if anything is off.</summary>
        public static CineHook Locate(GameMemory m, out string why)
        {
            why = null;
            var fns = new List<long>(); int fovSites = 0;
            m.ForEachChunk(r => Native.Executable(r.Protect), (buf, len, maxStart, addr) =>
            {
                foreach (int i in HorPlus.CineGetView.FindAll(buf, len, maxStart)) fns.Add(addr + i);
                fovSites += HorPlus.FovToView.FindAll(buf, len, maxStart).Count;
            });
            if (fns.Count != 1) { why = "cine camera view function: " + fns.Count + " matches (need exactly 1)"; return null; }
            if (fovSites < 1) { why = "View.FOV offset (+0x30) not confirmed in this build"; return null; }
            long fn = fns[0];
            var slots = new List<long>();
            m.ForEachChunk(r => !Native.Executable(r.Protect), (buf, len, maxStart, addr) => slots.AddRange(HorPlus.FindQword(buf, len, maxStart, addr, fn)));
            if (slots.Count != 1) { why = "vtable slot: " + slots.Count + " pointers to the function (need exactly 1)"; return null; }
            return new CineHook(m) { Function = fn, Slot = slots[0] };
        }

        public void Install(float k, bool enabled)
        {
            long cur = m.ReadInt64(Slot);
            if (cur != Function) throw new InvalidOperationException("vtable slot holds 0x" + cur.ToString("X") + ", expected the original function");
            Cfg = m.Alloc(0x1000, Native.PAGE_READWRITE);
            m.WriteBytes(Cfg, HorPlus.Config(enabled, Function, k, HorPlus.ViewFovOffset));
            Code = m.Alloc(0x1000, Native.PAGE_READWRITE);
            var w = HorPlus.Wrapper(Cfg);
            m.WriteBytes(Code, w);
            m.Protect(Code, 0x1000, Native.PAGE_EXECUTE_READ_);
            Native.FlushInstructionCache(m.Handle, new IntPtr(Code), (UIntPtr)(uint)w.Length);
            m.Suspended(() =>
            {
                if (m.ReadInt64(Slot) != Function) throw new InvalidOperationException("vtable slot changed; hook cancelled");
                m.WriteProtected(Slot, BitConverter.GetBytes(Code));
            });
            Installed = m.ReadInt64(Slot) == Code;
        }

        public void SetEnabled(bool on) { if (Cfg != 0) m.WriteBytes(Cfg, BitConverter.GetBytes(on ? 1 : 0)); }
        public void SetK(float k) { if (Cfg != 0) m.WriteBytes(Cfg + 0x24, BitConverter.GetBytes(k)); }

        public void Stats(out long calls, out long adjusted, out float lastIn, out float lastOut)
        {
            var b = m.Read(Cfg, HorPlus.ConfigSize);
            calls = BitConverter.ToInt64(b, 0x10); adjusted = BitConverter.ToInt64(b, 0x18);
            lastIn = BitConverter.ToSingle(b, 0x38); lastOut = BitConverter.ToSingle(b, 0x3C);
        }

        /// <summary>Puts the original pointer back (game alive). Wrapper memory is left allocated: a thread may still be inside it.</summary>
        public void Uninstall()
        {
            if (!Installed) return;
            SetEnabled(false);
            m.Suspended(() => { if (m.ReadInt64(Slot) == Code) m.WriteProtected(Slot, BitConverter.GetBytes(Function)); });
            Installed = false;
        }
    }
}
