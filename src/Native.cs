// EDayUltrawide - Win32 interop and external process memory access.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EDayUltrawide
{
    internal static class Native
    {
        public const uint PROCESS_VM_READ = 0x0010, PROCESS_VM_WRITE = 0x0020, PROCESS_VM_OPERATION = 0x0008,
            PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_QUERY_LIMITED = 0x1000, SYNCHRONIZE = 0x00100000;
        public const uint MEM_COMMIT = 0x1000, PAGE_NOACCESS = 0x01, PAGE_GUARD = 0x100, PAGE_READWRITE = 0x04;
        public const uint PAGE_EXECUTE = 0x10, PAGE_EXECUTE_READ = 0x20, PAGE_EXECUTE_READWRITE = 0x40, PAGE_EXECUTE_WRITECOPY = 0x80;

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress, AllocationBase;
            public uint AllocationProtect;
            public ushort PartitionId;
            public IntPtr RegionSize;
            public uint State, Protect, Type;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("kernel32", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32", SetLastError = true)] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] buf, UIntPtr size, out UIntPtr read);
        [DllImport("kernel32", SetLastError = true)] public static extern bool WriteProcessMemory(IntPtr h, IntPtr a, byte[] buf, UIntPtr size, out UIntPtr written);
        [DllImport("kernel32", SetLastError = true)] public static extern bool VirtualProtectEx(IntPtr h, IntPtr a, UIntPtr size, uint prot, out uint old);
        [DllImport("kernel32", SetLastError = true)] public static extern UIntPtr VirtualQueryEx(IntPtr h, IntPtr a, out MEMORY_BASIC_INFORMATION mbi, UIntPtr len);
        [DllImport("user32")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
        [DllImport("user32")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32")] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
        [DllImport("user32")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32")] public static extern bool SetProcessDPIAware();
        [DllImport("kernel32", SetLastError = true)] public static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr a, UIntPtr size, uint type, uint prot);
        [DllImport("kernel32", SetLastError = true)] public static extern bool FlushInstructionCache(IntPtr h, IntPtr a, UIntPtr size);
        [DllImport("ntdll")] public static extern int NtSuspendProcess(IntPtr h);
        [DllImport("ntdll")] public static extern int NtResumeProcess(IntPtr h);
        public const uint PROCESS_SUSPEND_RESUME = 0x0800, MEM_RESERVE = 0x2000, PAGE_EXECUTE_READ_ = 0x20;
        [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern int GetCurrentPackageFullName(ref int length, System.Text.StringBuilder name);
        [DllImport("kernel32")] public static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32")] public static extern bool AllocConsole();
        [DllImport("kernel32")] public static extern bool AttachConsole(int pid);
        [DllImport("user32")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);
        [DllImport("user32", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, System.Text.StringBuilder lParam, uint flags, uint timeout, out IntPtr result);

        public static string ClassName(IntPtr h)
        {
            var sb = new System.Text.StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>Window or control text, also for other processes' controls (WM_GETTEXT is marshalled).</summary>
        public static string Text(IntPtr h)
        {
            var sb = new System.Text.StringBuilder(1024);
            IntPtr r;
            SendMessageTimeout(h, 0x000D /*WM_GETTEXT*/, (IntPtr)sb.Capacity, sb, 0x0002 /*SMTO_ABORTIFHUNG*/, 500, out r);
            return sb.ToString();
        }

        public static bool Readable(uint protect)
        {
            if ((protect & PAGE_GUARD) != 0 || (protect & 0xFF) == PAGE_NOACCESS || protect == 0) return false;
            return (protect & 0xFF) != PAGE_EXECUTE; // execute-only is not readable
        }

        public static bool Executable(uint protect)
        {
            uint p = protect & 0xFF;
            return p == PAGE_EXECUTE || p == PAGE_EXECUTE_READ || p == PAGE_EXECUTE_READWRITE || p == PAGE_EXECUTE_WRITECOPY;
        }
    }

    internal sealed class Region
    {
        public long Base, Size;
        public uint Protect;
    }

    /// <summary>Handle to the game process plus helpers. Never caches game objects across frames: only addresses inside the main module.</summary>
    internal sealed class GameMemory : IDisposable
    {
        public readonly Process Game;
        public readonly IntPtr Handle;
        public readonly long ModuleBase;
        public readonly long ModuleSize;
        public readonly string ModulePath;
        public readonly string FileVersion;

        public GameMemory(Process game, bool write)
        {
            Game = game;
            uint access = Native.PROCESS_VM_READ | Native.PROCESS_QUERY_INFORMATION | Native.SYNCHRONIZE;
            if (write) access |= Native.PROCESS_VM_WRITE | Native.PROCESS_VM_OPERATION | Native.PROCESS_SUSPEND_RESUME;
            Handle = Native.OpenProcess(access, false, game.Id);
            if (Handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the game process (is anti-cheat running?)");
            var main = game.MainModule;
            ModuleBase = main.BaseAddress.ToInt64();
            ModuleSize = main.ModuleMemorySize;
            ModulePath = main.FileName;
            try { FileVersion = main.FileVersionInfo.FileVersion ?? "?"; } catch { FileVersion = "?"; }
        }

        public byte[] Read(long address, int size)
        {
            var buf = new byte[size];
            UIntPtr n;
            if (!Native.ReadProcessMemory(Handle, new IntPtr(address), buf, (UIntPtr)(ulong)size, out n) || n.ToUInt64() != (ulong)size)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Read failed at 0x" + address.ToString("X"));
            return buf;
        }

        public bool TryRead(long address, byte[] buf, int size)
        {
            UIntPtr n;
            return Native.ReadProcessMemory(Handle, new IntPtr(address), buf, (UIntPtr)(ulong)size, out n) && n.ToUInt64() == (ulong)size;
        }

        public float ReadFloat(long address) { return BitConverter.ToSingle(Read(address, 4), 0); }

        /// <summary>Writes a float to (possibly read-only) data memory, restores protection, verifies by read-back.</summary>
        public void WriteFloatVerified(long address, float value)
        {
            var bytes = BitConverter.GetBytes(value);
            uint old;
            if (!Native.VirtualProtectEx(Handle, new IntPtr(address), (UIntPtr)4u, Native.PAGE_READWRITE, out old))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualProtectEx failed");
            try
            {
                UIntPtr n;
                if (!Native.WriteProcessMemory(Handle, new IntPtr(address), bytes, (UIntPtr)4u, out n) || n.ToUInt64() != 4)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteProcessMemory failed");
            }
            finally
            {
                uint dummy;
                Native.VirtualProtectEx(Handle, new IntPtr(address), (UIntPtr)4u, old, out dummy);
            }
            if (!Maths.SameFloat(ReadFloat(address), value)) throw new InvalidOperationException("Read-back verification failed");
        }

        /// <summary>Plain write to writable memory, verified by read-back.</summary>
        public void WriteBytes(long address, byte[] data)
        {
            UIntPtr n;
            if (!Native.WriteProcessMemory(Handle, new IntPtr(address), data, (UIntPtr)(ulong)data.Length, out n) || n.ToUInt64() != (ulong)data.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteProcessMemory failed at 0x" + address.ToString("X"));
            var back = Read(address, data.Length);
            for (int i = 0; i < data.Length; i++) if (back[i] != data[i]) throw new InvalidOperationException("Read-back mismatch at 0x" + address.ToString("X"));
        }

        /// <summary>Writes to read-only data (e.g. a vtable slot): unprotect, write, restore protection, verify.</summary>
        public void WriteProtected(long address, byte[] data)
        {
            uint old;
            if (!Native.VirtualProtectEx(Handle, new IntPtr(address), (UIntPtr)(ulong)data.Length, Native.PAGE_READWRITE, out old))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualProtectEx failed");
            try { WriteBytes(address, data); }
            finally { uint d; Native.VirtualProtectEx(Handle, new IntPtr(address), (UIntPtr)(ulong)data.Length, old, out d); }
        }

        public long Alloc(int size, uint protect)
        {
            IntPtr p = Native.VirtualAllocEx(Handle, IntPtr.Zero, (UIntPtr)(uint)size, Native.MEM_COMMIT | Native.MEM_RESERVE, protect);
            if (p == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx failed");
            return p.ToInt64();
        }

        public void Protect(long address, int size, uint protect)
        {
            uint old;
            if (!Native.VirtualProtectEx(Handle, new IntPtr(address), (UIntPtr)(uint)size, protect, out old))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualProtectEx failed");
        }

        /// <summary>Runs action with every game thread parked, so a pointer swap can never be half-seen.</summary>
        public void Suspended(Action action)
        {
            if (Native.NtSuspendProcess(Handle) < 0) throw new InvalidOperationException("Could not pause the game for the hook swap.");
            try { action(); }
            finally { Native.NtResumeProcess(Handle); }
        }

        public long ReadInt64(long address) { return BitConverter.ToInt64(Read(address, 8), 0); }

        public List<Region> ModuleRegions()
        {
            var list = new List<Region>();
            long a = ModuleBase, end = ModuleBase + ModuleSize;
            int mbiSize = Marshal.SizeOf(typeof(Native.MEMORY_BASIC_INFORMATION));
            while (a < end)
            {
                Native.MEMORY_BASIC_INFORMATION mbi;
                if (Native.VirtualQueryEx(Handle, new IntPtr(a), out mbi, (UIntPtr)(uint)mbiSize) == UIntPtr.Zero) break;
                long rb = mbi.BaseAddress.ToInt64(), rs = mbi.RegionSize.ToInt64();
                if (rs <= 0) break;
                long s = Math.Max(rb, a), e = Math.Min(rb + rs, end);
                if (mbi.State == Native.MEM_COMMIT && Native.Readable(mbi.Protect))
                    list.Add(new Region { Base = s, Size = e - s, Protect = mbi.Protect });
                a = rb + rs;
            }
            return list;
        }

        /// <summary>
        /// Calls visit(block, dataLen, maxStart, blockAddress) over every readable module region in chunks.
        /// Each chunk carries 256 bytes of look-ahead; matches must START before maxStart so none is reported twice.
        /// </summary>
        public void ForEachChunk(Func<Region, bool> regionFilter, Action<byte[], int, int, long> visit)
        {
            const int Chunk = 16 * 1024 * 1024, Overlap = 256;
            var buf = new byte[Chunk + Overlap];
            foreach (var r in ModuleRegions())
            {
                if (regionFilter != null && !regionFilter(r)) continue;
                for (long off = 0; off < r.Size; off += Chunk)
                {
                    int len = (int)Math.Min(Chunk + Overlap, r.Size - off);
                    if (!TryRead(r.Base + off, buf, len)) continue;
                    visit(buf, len, (int)Math.Min(Chunk, len), r.Base + off);
                }
            }
        }

        /// <summary>Committed, read-write, private memory outside the game's image (the engine heap), in address order.</summary>
        public List<Region> HeapRegions()
        {
            var list = new List<Region>();
            long a = 0x10000; const long Max = 0x7FFFFFFE0000L;
            int mbiSize = Marshal.SizeOf(typeof(Native.MEMORY_BASIC_INFORMATION));
            while (a < Max)
            {
                Native.MEMORY_BASIC_INFORMATION mbi;
                if (Native.VirtualQueryEx(Handle, new IntPtr(a), out mbi, (UIntPtr)(uint)mbiSize) == UIntPtr.Zero) break;
                long rb = mbi.BaseAddress.ToInt64(), rs = mbi.RegionSize.ToInt64();
                if (rs <= 0) break;
                if (mbi.State == Native.MEM_COMMIT && mbi.Type == 0x20000 /*MEM_PRIVATE*/ && (mbi.Protect & 0xFF) == Native.PAGE_READWRITE && (mbi.Protect & Native.PAGE_GUARD) == 0)
                    list.Add(new Region { Base = rb, Size = rs, Protect = mbi.Protect });
                a = rb + rs;
            }
            return list;
        }

        /// <summary>
        /// Like ForEachChunk but over the engine heap. Each block carries 'overlap' extra bytes; visit(block, dataLen, maxStart, blockAddress).
        /// A block that cannot be read in one piece is retried in 256 KB pieces (with overlap) so one bad page does not hide the rest.
        /// </summary>
        public void ForEachHeapChunk(int overlap, Func<bool> cancel, Action<byte[], int, int, long> visit)
        {
            const int Chunk = 16 * 1024 * 1024, Piece = 256 * 1024;
            var buf = new byte[Chunk + overlap];
            foreach (var r in HeapRegions())
            {
                for (long off = 0; off < r.Size; off += Chunk)
                {
                    if (cancel != null && cancel()) return;
                    int len = (int)Math.Min(Chunk + overlap, r.Size - off);
                    if (TryRead(r.Base + off, buf, len)) { visit(buf, len, (int)Math.Min(Chunk, len), r.Base + off); continue; }
                    int want = (int)Math.Min(Chunk, r.Size - off);
                    for (int po = 0; po < want; po += Piece)
                    {
                        int pl = Math.Min(Piece + overlap, (int)(r.Size - off - po));
                        if (TryRead(r.Base + off + po, buf, pl)) visit(buf, pl, Math.Min(Piece, pl), r.Base + off + po);
                    }
                }
            }
        }

        public void Dispose() { if (Handle != IntPtr.Zero) Native.CloseHandle(Handle); }

        // ---- window / aspect helpers ----

        public IntPtr MainWindow()
        {
            IntPtr best = IntPtr.Zero; long bestArea = 0; uint me = (uint)Game.Id;
            Native.EnumWindows((h, l) =>
            {
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                if (pid != me || !Native.IsWindowVisible(h)) return true;
                Native.RECT r;
                if (Native.GetClientRect(h, out r))
                {
                    long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                    if (area > bestArea) { bestArea = area; best = h; }
                }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        /// <summary>Game window client aspect; else its monitor; else primary screen.</summary>
        public float DetectAspect(out string source)
        {
            IntPtr w = MainWindow();
            if (w != IntPtr.Zero)
            {
                Native.RECT r;
                if (Native.GetClientRect(w, out r) && r.Right - r.Left >= 640 && r.Bottom - r.Top >= 360)
                {
                    source = "game window " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top);
                    return Maths.Aspect(r.Right - r.Left, r.Bottom - r.Top);
                }
                var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO)) };
                if (Native.GetMonitorInfo(Native.MonitorFromWindow(w, 2), ref mi))
                {
                    int mw = mi.rcMonitor.Right - mi.rcMonitor.Left, mh = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                    source = "game monitor " + mw + "x" + mh;
                    return Maths.Aspect(mw, mh);
                }
            }
            int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
            source = "primary monitor " + sw + "x" + sh;
            return Maths.Aspect(sw, sh);
        }

        public bool IsForeground()
        {
            uint pid; Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
            return pid == (uint)Game.Id;
        }
    }
}
