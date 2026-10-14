// EDayUltrawide - pure helpers (no Win32), unit-tested on Linux.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace EDayUltrawide
{
    public sealed class Pattern
    {
        public readonly byte[] Bytes;
        public readonly bool[] Mask; // true = must match
        public readonly string Name;

        public Pattern(string name, string text)
        {
            Name = name;
            var parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            Bytes = new byte[parts.Length];
            Mask = new bool[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "??" || parts[i] == "?") continue;
                Bytes[i] = byte.Parse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                Mask[i] = true;
            }
        }

        public int Length { get { return Bytes.Length; } }

        public bool MatchAt(byte[] data, int at)
        {
            if (at < 0 || at + Bytes.Length > data.Length) return false;
            for (int i = 0; i < Bytes.Length; i++)
                if (Mask[i] && data[at + i] != Bytes[i]) return false;
            return true;
        }

        /// <summary>Match offsets that start before maxStart and fit inside data[0..dataLen).</summary>
        public List<int> FindAll(byte[] data, int dataLen, int maxStart)
        {
            var hits = new List<int>();
            int first = -1;
            for (int i = 0; i < Mask.Length; i++) if (Mask[i]) { first = i; break; }
            if (first < 0) return hits;
            byte f = Bytes[first];
            int end = Math.Min(Math.Min(dataLen, data.Length) - Bytes.Length, maxStart - 1);
            for (int i = 0; i <= end; i++)
            {
                if (data[i + first] != f) continue;
                if (MatchAt(data, i)) hits.Add(i);
            }
            return hits;
        }

        public List<int> FindAll(byte[] data) { return FindAll(data, data.Length, data.Length); }
    }

    public static class Sig
    {
        // The game clamps the cinematic view aspect: aspect = max(min(aspect, CAP), FLOOR).
        // CAP is 2.388889 (3440x1440) in the Steam build; "Cinematic Ultrawide Support" stops there.
        //   41 C6 86 xx xx 00 00 01        mov byte [r14+...], 1
        //   C4 C1 7A 10 86 xx xx 00 00     vmovss xmm0, [r14+...]
        //   C5 FA 5D 0D <disp32>           vminss xmm1, xmm0, [rip+CAP]   <- disp at +21, instr end at +25
        //   C5 F8 29 74 24 xx              vmovaps [rsp+..], xmm6
        //   C5 F2 5F 35 <disp32>           vmaxss xmm6, xmm1, [rip+FLOOR]
        // Derived from the Steam build (CL-4894958) bytes; struct offsets wildcarded for other builds.
        public static readonly Pattern CapStrict = new Pattern("cap-strict",
            "41 C6 86 ?? ?? 00 00 01 C4 C1 7A 10 86 ?? ?? 00 00 C5 FA 5D 0D ?? ?? ?? ?? C5 F8 29 74 24 ?? C5 F2 5F 35");
        public const int CapStrictDisp = 21, CapStrictEnd = 25;

        // Looser fallback: any vminss xmm1,xmm0,[rip+x] followed (within the pattern) by vmaxss xmm6,xmm1,[rip+y].
        public static readonly Pattern CapLoose = new Pattern("cap-loose",
            "C5 FA 5D 0D ?? ?? ?? ?? C5 F8 29 74 24 ?? C5 F2 5F 35");
        public const int CapLooseDisp = 4, CapLooseEnd = 8;

        // Xbox app / GDK build (same CL-4894958, different compile): the two clamps are adjacent and the
        // struct pointer is rsi:  C6 86 06 02 00 00 01 | C5 FA 10 86 08 02 00 00 | C5 FA 5D 0D <cap> | C5 F2 5F 35 <floor>
        // This pair occurs 14x in the GDK image; only one has cap ~2.39 and floor 16:9, so values decide.
        public static readonly Pattern CapPair = new Pattern("cap-pair", "C5 FA 5D 0D ?? ?? ?? ?? C5 F2 5F 35");

        public sealed class Layout
        {
            public Pattern P; public int CapDisp, CapEnd, FloorDisp, FloorEnd;
        }

        /// <summary>Tried in order. Every candidate must also have a 16:9 floor next to its cap.</summary>
        public static readonly Layout[] CapLayouts =
        {
            new Layout { P = CapStrict, CapDisp = 21, CapEnd = 25, FloorDisp = 35, FloorEnd = 39 },
            new Layout { P = CapLoose,  CapDisp = 4,  CapEnd = 8,  FloorDisp = 18, FloorEnd = 22 },
            new Layout { P = CapPair,   CapDisp = 4,  CapEnd = 8,  FloorDisp = 12, FloorEnd = 16 },
        };

        public static bool SixteenNine(float v) { return v > 1.77f && v < 1.79f; }

        /// <summary>Cap value that is either the game's ~21:9 cap or our own already-written wide value.</summary>
        public static bool CapOrPatched(float v) { return PlausibleCap(v) || (v > 2.5f && v < 8f); }

        public const float SteamCap = 2.3888888f; // 8E E3 18 40

        public static bool PlausibleCap(float v)
        {
            return v > 2.2f && v < 2.5f; // 2.333 (21:9), 2.370 (64:27), 2.389 (3440x1440), 2.4
        }

        public static long ResolveRip(byte[] data, int dispOffset, int instrEnd, long blockAddress, int matchOffset)
        {
            int disp = BitConverter.ToInt32(data, matchOffset + dispOffset);
            return blockAddress + matchOffset + instrEnd + disp;
        }

        /// <summary>Counts rip-relative disp32 fields in a block that point at target (assumes no trailing immediate).</summary>
        public static int CountRipRefs(byte[] data, int dataLen, int maxStart, long blockAddress, long target)
        {
            int n = 0;
            int end = Math.Min(Math.Min(dataLen, data.Length) - 4, maxStart - 1);
            for (int i = 0; i <= end; i++)
            {
                long t = blockAddress + i + 4 + BitConverter.ToInt32(data, i);
                if (t == target) n++;
            }
            return n;
        }
    }

    public static class Maths
    {
        /// <summary>Cap value to write: screen aspect with a hair of headroom so min(aspect,cap)==aspect.</summary>
        public static float CapFor(float screenAspect, float originalCap)
        {
            if (!(screenAspect > 0f) || float.IsInfinity(screenAspect)) return originalCap;
            if (screenAspect <= originalCap) return originalCap; // nothing to fix on 21:9 or narrower
            return screenAspect * 1.0005f;
        }

        public static float Aspect(int w, int h)
        {
            return (w > 0 && h > 0) ? (float)w / h : 0f;
        }

        public static bool SameFloat(float a, float b) { return Math.Abs(a - b) < 1e-5f; }
    }

    public sealed class Config
    {
        public bool Enabled = true;
        public float ForceAspectRatio = 0f; // 0 = auto from the game window / monitor
        public string ToggleKey = "F8";
        public string StatusKey = "F7";
        public bool Verbose = false;
        public int AttachTimeoutSeconds = 180;
        public bool OnlineMode = false; // Mode=Online: Xbox-app launches go to the normal anti-cheat launcher
        public string OnlineKey = "Shift"; // hold while pressing Play for a one-off online launch
        public bool CutsceneHorPlus = true;      // widen cutscene FOV so vertical framing matches the basis aspect
        public float CutsceneFovBasis = 16f / 9f; // aspect whose vertical framing is kept (16:9 = the game's open-matte view)
        public float CutsceneFovPercent = 100f;   // user zoom on top of Hor+: 100 = exact basis framing, >100 wider, <100 tighter
        public float GameplayFov = 0f;            // 0 = the game's own setting; otherwise degrees on the game's FOV slider scale (60-150)

        public const float CutscenePercentMin = 50f, CutscenePercentMax = 150f, GameplayMin = 60f, GameplayMax = 150f;
        public static bool ValidGameplay(float v) { return !float.IsNaN(v) && !float.IsInfinity(v) && (v == 0f || (v >= GameplayMin && v <= GameplayMax)); }
        public static bool ValidPercent(float v) { return !float.IsNaN(v) && v >= CutscenePercentMin && v <= CutscenePercentMax; }

        public Config Clone() { return (Config)MemberwiseClone(); }

        public string ToIni()
        {
            var ci = CultureInfo.InvariantCulture;
            var b = new System.Text.StringBuilder();
            b.AppendLine("; EDayUltrawide settings. The app (EDayUltrawide.exe) edits this file; changes are picked up while the game runs.");
            b.AppendLine("Mode=" + (OnlineMode ? "Online" : "Offline") + "              ; Offline = start with the fix (anti-cheat off); Online = normal launch, no fix");
            b.AppendLine("OnlineKey=" + OnlineKey + "          ; hold while pressing Play for a one-off online launch");
            b.AppendLine("Enabled=" + (Enabled ? 1 : 0) + "                ; 1 = ultrawide cutscene fix on");
            b.AppendLine("ForceAspectRatio=" + (ForceAspectRatio > 0 ? ForceAspectRatio.ToString("0.####", ci) : "0") + "     ; 0 = auto; or 32:9 / 5120x1440 / 3.5556");
            b.AppendLine("CutsceneHorPlus=" + (CutsceneHorPlus ? 1 : 0) + "        ; 1 = keep the 16:9 vertical framing on wide screens");
            b.AppendLine("CutsceneFovBasis=" + (Math.Abs(CutsceneFovBasis - 16f / 9f) < 0.0005f ? "16:9" : CutsceneFovBasis.ToString("0.####", ci)) + "      ; aspect whose vertical framing is kept");
            b.AppendLine("CutsceneFovPercent=" + CutsceneFovPercent.ToString("0.#", ci) + "   ; cutscene FOV: 100 = exact framing, 50-150");
            b.AppendLine("GameplayFov=" + GameplayFov.ToString("0.#", ci) + "          ; gameplay FOV in degrees (game slider scale), 0 = game's own, 60-150");
            b.AppendLine("ToggleKey=" + ToggleKey + "             ; in-game: switch the whole fix off/on");
            b.AppendLine("StatusKey=" + StatusKey + "             ; in-game: write a status line to the log");
            b.AppendLine("Verbose=" + (Verbose ? 1 : 0));
            b.AppendLine("AttachTimeoutSeconds=" + AttachTimeoutSeconds);
            return b.ToString();
        }

        public static Config Parse(string text, List<string> warnings)
        {
            var c = new Config();
            if (text == null) return c;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                int sc = v.IndexOf(';'); if (sc >= 0) v = v.Substring(0, sc).Trim();
                try
                {
                    switch (k.ToLowerInvariant())
                    {
                        case "enabled": c.Enabled = Bool(v); break;
                        case "forceaspectratio": c.ForceAspectRatio = ParseAspect(v); break;
                        case "togglekey": c.ToggleKey = v; break;
                        case "statuskey": c.StatusKey = v; break;
                        case "verbose": c.Verbose = Bool(v); break;
                        case "mode":
                            if (v.Equals("online", StringComparison.OrdinalIgnoreCase)) c.OnlineMode = true;
                            else if (v.Equals("offline", StringComparison.OrdinalIgnoreCase) || v.Equals("fix", StringComparison.OrdinalIgnoreCase)) c.OnlineMode = false;
                            else throw new FormatException();
                            break;
                        case "onlinekey": c.OnlineKey = v; break;
                        case "cutscenehorplus": c.CutsceneHorPlus = Bool(v); break;
                        case "cutscenefovbasis":
                            { float b = ParseAspect(v); if (!(b >= 1f && b <= 4f)) throw new FormatException(); c.CutsceneFovBasis = b; }
                            break;
                        case "cutscenefovpercent":
                            { float pc = float.Parse(v, CultureInfo.InvariantCulture); if (!ValidPercent(pc)) throw new FormatException(); c.CutsceneFovPercent = pc; }
                            break;
                        case "gameplayfov":
                            { float g = float.Parse(v, CultureInfo.InvariantCulture); if (!ValidGameplay(g)) throw new FormatException(); c.GameplayFov = g; }
                            break;
                        case "attachtimeoutseconds": c.AttachTimeoutSeconds = Math.Max(10, Math.Min(600, int.Parse(v, CultureInfo.InvariantCulture))); break;
                        default: warnings.Add("Unknown setting: " + k); break;
                    }
                }
                catch (Exception) { warnings.Add("Bad value for " + k + ": " + v); }
            }
            return c;
        }

        static bool Bool(string v)
        {
            v = v.ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "on";
        }

        /// <summary>Accepts 3.5556, 32:9, 5120x1440 or 0.</summary>
        public static float ParseAspect(string v)
        {
            char[] seps = { ':', 'x', 'X', '/' };
            int s = v.IndexOfAny(seps);
            if (s > 0)
            {
                float a = float.Parse(v.Substring(0, s), CultureInfo.InvariantCulture);
                float b = float.Parse(v.Substring(s + 1), CultureInfo.InvariantCulture);
                return b > 0 ? a / b : 0f;
            }
            float f = float.Parse(v, CultureInfo.InvariantCulture);
            if (f != 0f && (f < 1f || f > 8f)) throw new FormatException();
            return f;
        }
    }

    /// <summary>Cutscene Hor+ : vtable hook on UCineCameraComponent::GetCameraView that widens View.FOV after the original runs.</summary>
    public static class HorPlus
    {
        // UCineCameraComponent::GetCameraView(this, DeltaTime, FMinimalViewInfo&) in the Xbox build (RVA 0x1411218);
        // the body pins it to the cine camera class.
        public static readonly Pattern CineGetView = new Pattern("cine-getview",
            "48 89 5C 24 08 57 48 83 EC 30 C5 F8 29 74 24 20 C5 F8 28 F1 49 8B F8 48 8B D9 E8 ?? ?? ?? ?? 4C 8B C7 48 8B CB C5 F8 28 CE E8 ?? ?? ?? ?? 48 8B 03 4C 8B C7 C5 F8 28 CE 48 8B CB FF 90 ?? ?? ?? ?? F6 83 ?? ?? 00 00 01");

        // UCameraComponent::GetCameraView: View.FOV(+0x30) = FieldOfView; View.AspectRatio(+0x5C) = AspectRatio.
        // Presence proves FMinimalViewInfo.FOV is at +0x30 in this build.
        public static readonly Pattern FovToView = new Pattern("fov-to-view",
            "C5 FA 10 8B ?? ?? 00 00 C5 FA 11 4F 30 8B 83 ?? ?? 00 00 89 47 5C");
        public const int ViewFovOffset = 0x30;

        // asm/horplus.asm assembled with nasm (125 bytes). 0x1122334455667788 = config address (2x).
        public const string WrapperHex =
            "534883ec204c89c349ba8877665544332211f049ff421041ff520849ba887766554433221141833a00744c4885db74474963422cf30f100403410f2f42307637410f2f42347330f3410f114238d9040341d84a20d9f2ddd841d84a24d9e8d9f341d84a28d91c03f30f100403f3410f11423cf049ff42184883c4205bc3";
        public const long Marker = 0x1122334455667788L;
        public const int ConfigSize = 0x40;

        public static byte[] Wrapper(long configAddress)
        {
            var b = new byte[WrapperHex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(WrapperHex.Substring(i * 2, 2), 16);
            byte[] mk = BitConverter.GetBytes(Marker), rep = BitConverter.GetBytes(configAddress);
            int n = 0;
            for (int i = 0; i + 8 <= b.Length; i++)
            {
                bool m = true;
                for (int j = 0; j < 8 && m; j++) m = b[i + j] == mk[j];
                if (m) { Array.Copy(rep, 0, b, i, 8); n++; i += 7; }
            }
            if (n != 2) throw new InvalidOperationException("wrapper relocation count " + n);
            return b;
        }

        public static byte[] Config(bool enabled, long original, float k, int fovOffset)
        {
            var c = new byte[ConfigSize];
            BitConverter.GetBytes(enabled ? 1 : 0).CopyTo(c, 0x00);
            BitConverter.GetBytes(original).CopyTo(c, 0x08);
            BitConverter.GetBytes((float)(Math.PI / 360.0)).CopyTo(c, 0x20);
            BitConverter.GetBytes(k).CopyTo(c, 0x24);
            BitConverter.GetBytes((float)(360.0 / Math.PI)).CopyTo(c, 0x28);
            BitConverter.GetBytes(fovOffset).CopyTo(c, 0x2C);
            BitConverter.GetBytes(5f).CopyTo(c, 0x30);
            BitConverter.GetBytes(170f).CopyTo(c, 0x34);
            return c;
        }

        // The game's ultrawide composition tilt (same function as the cap, right after it):
        //   cam.SensorVerticalOffset(+0xCB0) = (aspect * 0.28125 - 0.5) * SensorHeight * strength
        //   C5 FA 10 05 <c=0.28125> | C4 E2 49 AB 05 <0.5> (vfmsub213ss xmm0,xmm6,..) | vmulss [rbp+0xCA8] | vmulss [rsi+0x200] | vmovss [rbp+0xCB0] | vmovss [rdi+0x3A04],xmm6
        // 0 at 16:9; meant for the Vert- 21:9 crop. With Hor+ the frame is 16:9-tall again, so the tilt must be the basis' value.
        public static readonly Pattern TiltSite = new Pattern("tilt-site",
            "C5 FA 10 05 ?? ?? ?? ?? C4 E2 49 AB 05 ?? ?? ?? ?? C5 FA 59 85 ?? ?? 00 00 C5 FA 59 8E ?? ?? 00 00 C5 FA 11 8D ?? ?? 00 00 C5 FA 11 B7");
        public const int TiltDisp = 4, TiltEnd = 8, TiltHalfDisp = 13, TiltHalfEnd = 17;
        public const float TiltOriginal = 0.28125f; // 9/32

        /// <summary>Coefficient so that render*c - 0.5 == basis*0.28125 - 0.5 (the tilt the game would use at the basis aspect).</summary>
        public static float TiltCoefficient(float renderAspect, float basisAspect)
        {
            if (!(renderAspect > 0f) || !(basisAspect > 0f) || renderAspect <= basisAspect) return TiltOriginal;
            return TiltOriginal * basisAspect / renderAspect;
        }

        public static float TiltOf(float aspect, float c) { return aspect * c - 0.5f; }

        /// <summary>K = render aspect / basis aspect (never below 1: never narrows the view).</summary>
        public static float K(float renderAspect, float basisAspect)
        {
            if (!(renderAspect > 0f) || !(basisAspect > 0f)) return 1f;
            return Math.Max(1f, renderAspect / basisAspect);
        }

        /// <summary>The FOV the wrapper produces (reference implementation of the asm).</summary>
        public static double Widen(double fovDeg, double k)
        {
            return 2.0 * Math.Atan(Math.Tan(fovDeg * Math.PI / 360.0) * k) * 180.0 / Math.PI;
        }

        /// <summary>8-byte-aligned positions (absolute) in a block whose qword equals value.</summary>
        public static List<long> FindQword(byte[] data, int dataLen, int maxStart, long blockAddress, long value)
        {
            var r = new List<long>();
            int start = (int)((8 - (blockAddress & 7)) & 7);
            int end = Math.Min(Math.Min(dataLen, data.Length) - 8, maxStart - 1);
            for (int i = start; i <= end; i += 8)
                if (BitConverter.ToInt64(data, i) == value) r.Add(blockAddress + i);
            return r;
        }
    }

    public static class LauncherConfig
    {
        static readonly System.Text.RegularExpressions.Regex Exe = new System.Text.RegularExpressions.Regex(
            "(<Executable\\s+Name=\")([^\"]+)(\")", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>Returns the config with the single Executable Name replaced, or null if the layout is unexpected.</summary>
        public static string Rewrite(string xml, string newExe)
        {
            if (xml == null) return null;
            var m = Exe.Matches(xml);
            if (m.Count != 1) return null;
            string cur = m[0].Groups[2].Value;
            if (!cur.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            return xml.Substring(0, m[0].Groups[2].Index) + newExe + xml.Substring(m[0].Groups[2].Index + m[0].Groups[2].Length);
        }

        public static string Current(string xml)
        {
            var m = Exe.Matches(xml ?? "");
            return m.Count == 1 ? m[0].Groups[2].Value : null;
        }
    }

    public static class Keys
    {
        public static int VirtualKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            name = name.Trim().ToUpperInvariant();
            if (name.Length >= 2 && name[0] == 'F')
            {
                int n;
                if (int.TryParse(name.Substring(1), out n) && n >= 1 && n <= 24) return 0x70 + n - 1;
            }
            switch (name)
            {
                case "INSERT": return 0x2D;
                case "HOME": return 0x24;
                case "END": return 0x23;
                case "PAGEUP": return 0x21;
                case "PAGEDOWN": return 0x22;
                case "NONE": return 0;
                case "SHIFT": return 0x10;
                case "CTRL": case "CONTROL": return 0x11;
                case "ALT": return 0x12;
            }
            if (name.Length == 1 && ((name[0] >= 'A' && name[0] <= 'Z') || (name[0] >= '0' && name[0] <= '9'))) return name[0];
            return 0;
        }
    }

    /// <summary>Gameplay camera manager: FOV = base FOV (+0x340) x multiplier (+0x5EA8); the game's FOV slider sets the multiplier.</summary>
    public static class GameplayScan
    {
        // Field offsets inside the camera manager. They move between game builds, so they are read from the setter's own
        // instructions (UseOffsetsFrom); the values here are the 1.1.3.0 defaults.
        public static int BaseOff = 0x340, MultOff = 0x5EA8;
        public static int Span { get { return Math.Max(BaseOff, MultOff) + 4; } }
        public const float StrictMin = 59.99f, StrictMax = 90.01f; // the game's own slider range (60-90)

        // vmin/vmax clamp of the slider value, divide by base FOV, store multiplier, ret  (virtual setter)
        public static readonly Pattern Setter = new Pattern("gameplay-fov-setter",
            "C5 F2 5D 05 ?? ?? ?? ?? C5 FA 5F 05 ?? ?? ?? ?? C5 FA 5E 89 ?? ?? ?? ?? C5 FA 11 89 ?? ?? ?? ?? C3");
        const int SetterBaseDisp = 20, SetterMultDisp = 28;

        /// <summary>Takes the base-FOV and multiplier offsets from the matched setter bytes. False if they look wrong.</summary>
        public static bool UseOffsetsFrom(byte[] setter, out string why)
        {
            why = null;
            int b = BitConverter.ToInt32(setter, SetterBaseDisp), m = BitConverter.ToInt32(setter, SetterMultDisp);
            if (b <= 0 || m <= 0 || b > 0x20000 || m > 0x20000 || b == m || (b & 3) != 0 || (m & 3) != 0)
            { why = "gameplay FOV setter offsets look wrong (base 0x" + b.ToString("X") + ", multiplier 0x" + m.ToString("X") + ")"; return false; }
            BaseOff = b; MultOff = m;
            return true;
        }

        public static bool PlausibleBase(float b) { return b >= 30f && b <= 150f; }
        public static bool PlausibleMult(float m) { return m >= 0.25f && m <= 5f; }
        public static bool Strict(float b, float m) { float p = b * m; return PlausibleBase(b) && PlausibleMult(m) && p >= StrictMin && p <= StrictMax; }
        public static bool Loose(float b, float m) { float p = b * m; return PlausibleBase(b) && PlausibleMult(m) && p >= 30f && p <= 165f; }

        /// <summary>Object starts (offsets, 8-aligned, &lt; maxStart) whose vtable pointer is in [vLo,vHi] and whose FOV fields look right.</summary>
        public static List<int> FindObjects(byte[] data, int dataLen, int maxStart, long vLo, long vHi)
        {
            var hits = new List<int>();
            int last = Math.Min(Math.Min(dataLen, data.Length) - Span, maxStart - 1);
            ulong lo = (ulong)vLo, width = (ulong)(vHi - vLo);
            for (int p = 0; p <= last; p += 8)
            {
                ulong v = BitConverter.ToUInt64(data, p);
                if (v - lo > width) continue;
                float b = BitConverter.ToSingle(data, p + BaseOff), m = BitConverter.ToSingle(data, p + MultOff);
                if (Loose(b, m)) hits.Add(p);
            }
            return hits;
        }
    }
}
