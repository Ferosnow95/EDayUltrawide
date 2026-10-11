using System; using System.Collections.Generic; using System.Linq; using EDayUltrawide;
static class T {
  static int fails = 0;
  static void Ok(bool c, string n){ Console.WriteLine((c?"PASS ":"FAIL ")+n); if(!c) fails++; }
  static int Main(){
    // Steam build bytes at RVA 148305041 (from the original mod's Context array)
    byte[] ctx = {65,198,134,6,2,0,0,1,196,193,122,16,134,8,2,0,0,197,250,93,13,50,149,74,5,197,248,41,116,36,48,197,242,95,53,232,169,7,4};
    long baseAddr = 0x140000000;
    var buf = new byte[4096]; int at = 1000; Array.Copy(ctx,0,buf,at,ctx.Length);
    long block = baseAddr + 148305041 - at;
    var hits = Sig.CapStrict.FindAll(buf);
    Ok(hits.Count==1 && hits[0]==at, "strict pattern finds the Steam cap site once");
    long c = Sig.ResolveRip(buf, Sig.CapStrictDisp, Sig.CapStrictEnd, block, hits[0]);
    Ok(c == baseAddr + 237079004, "rip resolution == original mod's LimitRva (0x"+(c-baseAddr).ToString("X")+")");
    var lh = Sig.CapLoose.FindAll(buf);
    Ok(lh.Count==1 && Sig.ResolveRip(buf, Sig.CapLooseDisp, Sig.CapLooseEnd, block, lh[0]) == c, "loose pattern resolves to the same constant");
    // other build: different struct offsets / stack slot still match strict
    var b2=(byte[])buf.Clone(); b2[at+3]=0x10; b2[at+4]=0x03; b2[at+13]=0x14; b2[at+30]=0x40;
    Ok(Sig.CapStrict.FindAll(b2).Count==1, "strict pattern tolerates shifted struct offsets");
    // overlap: a match starting at/after maxStart must not be reported
    Ok(Sig.CapStrict.FindAll(buf, buf.Length, at).Count==0 && Sig.CapStrict.FindAll(buf, buf.Length, at+1).Count==1, "maxStart boundary dedups chunk overlap");
    Ok(Sig.CapStrict.FindAll(buf, at+Sig.CapStrict.Length-1, 4096).Count==0 && Sig.CapStrict.FindAll(buf, at+Sig.CapStrict.Length, 4096).Count==1, "match must fit inside dataLen");
    // float bytes
    Ok(BitConverter.ToSingle(new byte[]{142,227,24,64},0)==Sig.SteamCap, "SteamCap == original bytes 8E E3 18 40");
    Ok(Sig.PlausibleCap(Sig.SteamCap) && Sig.PlausibleCap(2.3333333f) && !Sig.PlausibleCap(1.7777f) && !Sig.PlausibleCap(3.5555f), "plausible cap range");
    // rip ref counting: put 3 references to target
    var rb = new byte[256]; long rbase = 0x1000; long target = 0x5000;
    foreach (int p in new[]{10,50,200}) BitConverter.GetBytes((int)(target - (rbase+p+4))).CopyTo(rb,p);
    Ok(Sig.CountRipRefs(rb, rb.Length, rb.Length, rbase, target)==3, "CountRipRefs counts 3");
    Ok(Sig.CountRipRefs(rb, rb.Length, 100, rbase, target)==2, "CountRipRefs honours maxStart");
    // maths
    float w = Maths.CapFor(5120f/1440f, Sig.SteamCap);
    Ok(w > 5120f/1440f && w < 3.56f, "32:9 cap = "+w);
    Ok(Math.Min(5120f/1440f, w) == 5120f/1440f, "min(screenAR, cap) passes the full 32:9 aspect");
    Ok(Maths.CapFor(3440f/1440f, Sig.SteamCap)==Sig.SteamCap, "3440x1440 leaves cap untouched");
    Ok(Maths.CapFor(16f/9f, Sig.SteamCap)==Sig.SteamCap, "16:9 leaves cap untouched");
    Ok(Maths.CapFor(0f, Sig.SteamCap)==Sig.SteamCap && Maths.CapFor(float.PositiveInfinity, Sig.SteamCap)==Sig.SteamCap, "bad aspect ignored");
    Ok(Maths.CapFor(7680f/1440f, Sig.SteamCap) > 5.33f, "48:9 (triple) works");
    // config
    var warn=new List<string>();
    var cf = Config.Parse("[EDayUltrawide]\nEnabled=1\nForceAspectRatio=32:9 ; comment\nToggleKey=F9\nVerbose=true\nBogus=3\nAttachTimeoutSeconds=5\n", warn);
    Ok(cf.Enabled && Math.Abs(cf.ForceAspectRatio-3.5555556f)<1e-4 && cf.ToggleKey=="F9" && cf.Verbose && cf.AttachTimeoutSeconds==10, "config parse + clamp");
    Ok(warn.Count==1 && warn[0].Contains("Bogus"), "unknown key warned");
    Ok(Math.Abs(Config.ParseAspect("5120x1440")-3.5555556f)<1e-4 && Config.ParseAspect("0")==0f && Math.Abs(Config.ParseAspect("3.5556")-3.5556f)<1e-5, "aspect formats");
    var w2=new List<string>(); var bad=Config.Parse("ForceAspectRatio=99\n", w2);
    Ok(bad.ForceAspectRatio==0f && w2.Count==1, "out-of-range aspect rejected, stays auto");
    Ok(Config.Parse(null,new List<string>()).Enabled, "missing ini = defaults");
    Ok(Keys.VirtualKey("F8")==0x77 && Keys.VirtualKey("f7")==0x76 && Keys.VirtualKey("PageUp")==0x21 && Keys.VirtualKey("none")==0 && Keys.VirtualKey("K")==(int)'K', "key names");

    // launcher config rewrite on the real MicrosoftGame.config
    string real = System.IO.File.ReadAllText("/mnt/user-data/uploads/Gears of War- E-Day/Content/MicrosoftGame.config.edayultrawide-original");
    Ok(LauncherConfig.Current(real)=="start_protected_game.exe", "real config currently starts start_protected_game.exe");
    string hooked = LauncherConfig.Rewrite(real, @"FairlightConcept\Binaries\WinGDK\EDayUltrawide.exe");
    Ok(hooked!=null && LauncherConfig.Current(hooked)==@"FairlightConcept\Binaries\WinGDK\EDayUltrawide.exe", "hook rewrite sets our exe");
    Ok(hooked.Replace(@"FairlightConcept\Binaries\WinGDK\EDayUltrawide.exe","start_protected_game.exe")==real, "only the executable name changed (byte-identical otherwise)");
    Ok(LauncherConfig.Rewrite(hooked,"start_protected_game.exe")==real, "unhook restores the original exactly");
    Ok(hooked.Contains("Id=\"AppGearsofWarEDayShipping\""), "app id untouched");
    Ok(LauncherConfig.Rewrite("<x/>","a.exe")==null && LauncherConfig.Rewrite(real+real,"a.exe")==null, "unexpected layouts refused");
    var w3=new List<string>();
    Ok(Config.Parse("Mode=Online\nOnlineKey=Ctrl\n",w3).OnlineMode && w3.Count==0 && !Config.Parse("Mode=offline\n",w3).OnlineMode, "Mode parse");
    Ok(Keys.VirtualKey("Shift")==0x10 && Keys.VirtualKey("ctrl")==0x11, "modifier keys");

    // Xbox (GDK) build: run the real layouts over the full dumped image (file offset == RVA)
    string dumpPath = "dump/image.bin";
    if (System.IO.File.Exists(dumpPath)) {
      byte[] img = System.IO.File.ReadAllBytes(dumpPath);
      int t0 = 0x1000, t1 = 0xADA0000;
      byte[] text = new byte[t1 - t0]; Array.Copy(img, t0, text, 0, text.Length);
      var found = new List<(string,long,long)>();
      int pairRaw = 0;
      foreach (var lay in Sig.CapLayouts) {
        foreach (int i in lay.P.FindAll(text)) {
          if (lay.P == Sig.CapPair) pairRaw++;
          long cc = Sig.ResolveRip(text, lay.CapDisp, lay.CapEnd, t0, i);
          long f = Sig.ResolveRip(text, lay.FloorDisp, lay.FloorEnd, t0, i);
          if (cc < 0 || cc + 4 > img.Length || f < 0 || f + 4 > img.Length) continue;
          float cv = BitConverter.ToSingle(img, (int)cc), fv = BitConverter.ToSingle(img, (int)f);
          if (Sig.SixteenNine(fv) && Sig.CapOrPatched(cv)) found.Add((lay.P.Name, t0 + i, cc));
        }
      }
      Ok(pairRaw == 14, "GDK image: cap-pair raw matches = " + pairRaw + " (expected 14)");
      Ok(found.Count == 1 && found[0].Item1 == "cap-pair" && found[0].Item2 == 0x1f809fe && found[0].Item3 == 0xdfbeb7c,
         "GDK image: exactly one accepted site -> " + string.Join(", ", found.Select(x => x.Item1 + " @0x" + x.Item2.ToString("X") + " const 0x" + x.Item3.ToString("X"))));
      Ok(Sig.CountRipRefs(text, text.Length, text.Length, t0, 0xdfbeb7c) == 2, "GDK const has 2 disp32 hits (1 real + 1 coincidental byte pattern)");
      // re-attach after patching: patched value must still be found
      BitConverter.GetBytes(Maths.CapFor(5120f/1440f, Sig.SteamCap)).CopyTo(img, 0xdfbeb7c);
      float pv = BitConverter.ToSingle(img, 0xdfbeb7c);
      Ok(Sig.CapOrPatched(pv) && !Sig.PlausibleCap(pv), "patched value " + pv + " still accepted on re-attach");
    } else Console.WriteLine("SKIP GDK image tests (no dump)");
    // Steam bytes: floor offsets line up with the vmaxss in the original mod's context
    Ok(ctx[31]==0xC5 && ctx[32]==0xF2 && ctx[33]==0x5F && ctx[34]==0x35, "Steam layout floor disp at +35");

    // ---- cutscene Hor+ hook ----
    var asmBin = System.IO.File.ReadAllBytes("../asm/horplus.bin");
    var w0 = HorPlus.Wrapper(HorPlus.Marker);
    Ok(w0.SequenceEqual(asmBin), "WrapperHex == nasm output (" + asmBin.Length + " bytes)");
    var w1 = HorPlus.Wrapper(0x20000);
    Ok(w1.Length == asmBin.Length && !w1.SequenceEqual(asmBin), "wrapper relocates both config immediates");
    float k32 = HorPlus.K(5120f/1440f, 16f/9f);
    Ok(Math.Abs(k32 - 2f) < 1e-5 && HorPlus.K(16f/9f, 16f/9f) == 1f && HorPlus.K(1.5f, 16f/9f) == 1f && HorPlus.K(3440f/1440f, 16f/9f) > 1.34f, "K values (32:9 -> 2.0, never < 1)");
    var cfgb = HorPlus.Config(true, 0x11000, k32, 0x30);
    Ok(BitConverter.ToInt32(cfgb,0)==1 && BitConverter.ToInt64(cfgb,8)==0x11000 && BitConverter.ToSingle(cfgb,0x24)==k32 && BitConverter.ToInt32(cfgb,0x2C)==0x30, "config layout");
    System.IO.File.WriteAllBytes("/tmp/edw_wrapper.bin", w1);
    System.IO.File.WriteAllBytes("/tmp/edw_cfg.bin", cfgb);
    Ok(Math.Abs(HorPlus.Widen(40, 2) - 72.1048) < 1e-3, "Widen reference = 72.1048");
    var g = Config.Parse("CutsceneHorPlus=0\nCutsceneFovBasis=21:9\n", new List<string>());
    Ok(!g.CutsceneHorPlus && Math.Abs(g.CutsceneFovBasis - 21f/9f) < 1e-4 && Config.Parse(null, new List<string>()).CutsceneHorPlus, "HorPlus settings parse");
    if (System.IO.File.Exists(dumpPath)) {
      byte[] img2 = System.IO.File.ReadAllBytes(dumpPath);
      int a0 = 0x1000, a1 = 0xADA0000;
      byte[] tx = new byte[a1 - a0]; Array.Copy(img2, a0, tx, 0, tx.Length);
      var cg = HorPlus.CineGetView.FindAll(tx);
      Ok(cg.Count == 1 && a0 + cg[0] == 0x1411218, "GDK: cine GetCameraView unique @0x" + (cg.Count>0 ? (a0+cg[0]).ToString("X") : "-"));
      Ok(HorPlus.FovToView.FindAll(tx).Count == 1, "GDK: View.FOV(+0x30) write confirmed");
      byte[] rd = new byte[img2.Length - a1]; Array.Copy(img2, a1, rd, 0, rd.Length);
      long fnAbs = 0x140000000L + 0x1411218;
      // dump file offsets are RVAs, image base 0x140000000: pointers in data are absolute
      var sl = HorPlus.FindQword(rd, rd.Length, rd.Length, 0x140000000L + a1, fnAbs);
      Ok(sl.Count == 1 && sl[0] == 0x140000000L + 0xAFD4528, "GDK: single vtable slot @RVA 0x" + (sl.Count>0 ? (sl[0]-0x140000000L).ToString("X") : "-"));
      var sl2 = HorPlus.FindQword(rd, rd.Length, (int)(0xAFD4528 - a1), 0x140000000L + a1, fnAbs);
      Ok(sl2.Count == 0, "FindQword maxStart excludes the slot when it starts at the limit");
    }

    // ---- composition tilt ----
    float r32 = 5120f/1440f, b169 = 16f/9f, b219 = 21f/9f;
    float c32 = HorPlus.TiltCoefficient(r32, b169);
    Ok(Math.Abs(HorPlus.TiltOf(r32, c32)) < 1e-6 && Math.Abs(c32 - 0.140625f) < 1e-6, "tilt at 32:9 with 16:9 basis = 0 (c=" + c32 + ")");
    Ok(Math.Abs(HorPlus.TiltOf(r32, HorPlus.TiltCoefficient(r32, b219)) - HorPlus.TiltOf(b219, 0.28125f)) < 1e-6, "tilt with 21:9 basis = game's own 21:9 tilt");
    Ok(HorPlus.TiltCoefficient(b169, b169) == 0.28125f && HorPlus.TiltCoefficient(1.5f, b169) == 0.28125f, "no change at/below basis");
    Ok(Math.Abs(HorPlus.TiltOf(r32, 0.28125f) - 0.5f) < 1e-6 && Math.Abs(HorPlus.TiltOf(3440f/1440f, 0.28125f) - 0.171875f) < 1e-5, "game tilt: 0.5 at 32:9 vs 0.17 at its 21:9 cap");
    if (System.IO.File.Exists(dumpPath)) {
      byte[] img3 = System.IO.File.ReadAllBytes(dumpPath);
      int b0 = 0x1000, b1 = 0xADA0000;
      byte[] tx3 = new byte[b1 - b0]; Array.Copy(img3, b0, tx3, 0, tx3.Length);
      var th = HorPlus.TiltSite.FindAll(tx3);
      Ok(th.Count == 1 && b0 + th[0] == 0x1f80b8d, "GDK: tilt site unique @0x" + (th.Count>0 ? (b0+th[0]).ToString("X") : "-"));
      long tc = Sig.ResolveRip(tx3, HorPlus.TiltDisp, HorPlus.TiltEnd, b0, th[0]);
      long thf = Sig.ResolveRip(tx3, HorPlus.TiltHalfDisp, HorPlus.TiltHalfEnd, b0, th[0]);
      Ok(tc == 0xdfbe5b4 && BitConverter.ToSingle(img3, (int)tc) == 0.28125f && BitConverter.ToSingle(img3, (int)thf) == 0.5f, "GDK: tilt const 0xDFBE5B4 = 0.28125, half = 0.5");
      Ok(Sig.CountRipRefs(tx3, tx3.Length, tx3.Length, b0, tc) == 1, "GDK: tilt const referenced once (safe to patch)");
    }

    // ---- gameplay FOV ----
    {
      var rt = Config.Parse(null, new List<string>());
      rt.GameplayFov = 110f; rt.CutsceneFovPercent = 85f; rt.OnlineMode = true; rt.CutsceneHorPlus = false; rt.Enabled = false; rt.ForceAspectRatio = 32f/9f; rt.CutsceneFovBasis = 21f/9f;
      var gw_ = new List<string>();
      var grb_ = Config.Parse(rt.ToIni(), gw_);
      Ok(gw_.Count == 0 && grb_.GameplayFov == 110f && grb_.CutsceneFovPercent == 85f && grb_.OnlineMode && !grb_.CutsceneHorPlus && !grb_.Enabled && Math.Abs(grb_.ForceAspectRatio - 32f/9f) < 1e-3 && Math.Abs(grb_.CutsceneFovBasis - 21f/9f) < 1e-3, "settings round-trip through the ini (" + string.Join(";", gw_) + ")");
      var d0 = Config.Parse(null, new List<string>());
      Ok(d0.GameplayFov == 0f && d0.CutsceneFovPercent == 100f, "defaults: gameplay = game's own, cutscene 100%");
      var wb = new List<string>();
      Config.Parse("GameplayFov=45\nCutsceneFovPercent=200\nGameplayFov=abc\n", wb);
      Ok(wb.Count == 3, "out-of-range / bad FOV values are rejected with warnings");
      Ok(Config.Parse("GameplayFov=0", new List<string>()).GameplayFov == 0f && Config.Parse("GameplayFov=150", new List<string>()).GameplayFov == 150f && Config.Parse("GameplayFov=60", new List<string>()).GameplayFov == 60f, "gameplay FOV 0 / 60 / 150 accepted");

      // synthetic heap block with one real manager, a decoy with a wrong vtable, one with implausible FOV, plus our own modified one
      long vLo = 0x14C000000L - 0x1000, vHi = 0x14C000000L - 0x100, goodV = 0x14C000000L - 0x858;
      var heap = new byte[0x40000];
      Action<int, long, float, float> put = (at, v, b, m) => { BitConverter.GetBytes(v).CopyTo(heap, at); BitConverter.GetBytes(b).CopyTo(heap, at + GameplayScan.BaseOff); BitConverter.GetBytes(m).CopyTo(heap, at + GameplayScan.MultOff); };
      put(0x1000, goodV, 90f, 80f/90f);          // untouched: 80 deg
      put(0x10000, goodV + 0x1000000, 90f, 80f/90f); // wrong vtable
      put(0x18000, goodV, 90f, 9f);              // multiplier out of range
      put(0x20000, goodV, 90f, 130f/90f);        // already raised by us (130 deg)
      put(0x28008, goodV, 0f, 0f);               // zeroed class default object
      var ghits_ = GameplayScan.FindObjects(heap, heap.Length, heap.Length, vLo, vHi);
      Ok(ghits_.Count == 2 && ghits_[0] == 0x1000 && ghits_[1] == 0x20000, "gameplay manager scan finds the 2 real objects only (" + string.Join(",", ghits_) + ")");
      Ok(GameplayScan.Strict(90f, 80f/90f) && !GameplayScan.Strict(90f, 130f/90f) && GameplayScan.Loose(90f, 130f/90f), "strict = game's 60-90 range, loose = anything we could have set");
      var tail = GameplayScan.FindObjects(heap, 0x1000 + GameplayScan.Span, 0x1000 + 8, vLo, vHi);
      Ok(tail.Count == 1, "object that fits in the block is found at the block edge");
      var cut = GameplayScan.FindObjects(heap, 0x1000 + GameplayScan.Span - 4, 0x1000 + 8, vLo, vHi);
      Ok(cut.Count == 0, "object cut by the end of the block is not read out of bounds");
    }
    if (System.IO.File.Exists(dumpPath)) {
      byte[] img4 = System.IO.File.ReadAllBytes(dumpPath);
      int g0 = 0x1000, g1 = 0xADA0000;
      byte[] tx4 = new byte[g1 - g0]; Array.Copy(img4, g0, tx4, 0, tx4.Length);
      var sh = GameplayScan.Setter.FindAll(tx4);
      Ok(sh.Count == 1 && g0 + sh[0] == 0x3c2e8b8, "GDK: gameplay FOV setter unique @0x" + (sh.Count>0 ? (g0+sh[0]).ToString("X") : "-"));
      byte[] rd4 = new byte[img4.Length - g1]; Array.Copy(img4, g1, rd4, 0, rd4.Length);
      var ss = HorPlus.FindQword(rd4, rd4.Length, rd4.Length, 0x140000000L + g1, 0x140000000L + 0x3c2e8b8);
      Ok(ss.Count == 1 && ss[0] == 0x140000000L + 0xC71C5F0, "GDK: setter has one vtable slot @RVA 0xC71C5F0");
      long slot = ss[0], vt = 0x140000000L + 0xC71BD98;
      Ok(vt >= slot - 0x1000 && vt <= slot - 0x100, "GDK: manager vtable (RVA 0xC71BD98) lies inside the object window derived from the slot");
      Ok(BitConverter.ToSingle(img4, 0xC9B1D20) == 90f && BitConverter.ToSingle(img4, 0xC9B33A4) == 60f, "GDK: setter clamps to 60..90 (the game's slider range)");
    }
    Console.WriteLine(fails==0 ? "ALL PASSED" : fails+" FAILED");
    return fails;
  }
}
