# EDayUltrawide

Ultrawide (32:9) cutscene fix and independent cutscene / gameplay FOV app for Gears of War: E-Day (Xbox app / Game Pass build, also recognises Steam). Single-player / offline only.

Build: `cd src && dotnet build -c Release` (net48, WinExe). Tests: `cd tests && dotnet run -c Release`.

```
EDayUltrawide v0.3.0 - ultrawide fix + FOV control app for Gears of War: E-Day
=====================================================================
Removes the black side bars in cutscenes on screens wider than 21:9 (32:9, 48:9...).
Made for the Xbox app / Game Pass build (WinGDK\GoWEDay.exe); also recognises the Steam build.

STATUS: bars fix confirmed in game (Xbox build 1.1.3.0). Cutscene FOV, composition tilt and the new gameplay FOV
control are tested offline/emulated only - please report how they look.

THE APP (v0.3.0)
  EDayUltrawide.exe is now a small window app. The Xbox app still starts it when you press Play (it starts the game and
  shows its window minimised in the taskbar - click it to adjust). Opened by hand it edits the same settings and has a
  "Start the game" button. Everything saves to EDayUltrawide.ini and applies LIVE while the game runs.
    Cutscenes  Cutscene FOV 50-150 %: 100 % = same top-to-bottom view as 16:9 (the Hor+ result), higher = wider, lower = tighter.
               "Keep the 16:9 vertical framing" turns the Hor+ correction itself on/off.
    Gameplay   "Override the gameplay FOV" 60-150 deg on the game's own FOV-slider scale (the game's slider stops at 90).
               Unticked = the game's own value; the app puts it back when you untick it or close the game.
    Game       start the game, install/remove the Xbox launch hook, "Always start online" (Mode=Online), open the log.
  Closing the window while the game runs only minimises it (the fix lives in that window); it closes with the game.
  Gameplay FOV is found by scanning the game's memory for the camera manager once you are in a level (takes a few seconds,
  the log says "Gameplay camera manager at ..."). If that line never appears, report it with EDayUltrawide.log attached.

SINGLE-PLAYER / OFFLINE ONLY
  The launcher starts the game with Easy Anti-Cheat switched off (same method as the Steam mod),
  so online play, co-op and multiplayer are unavailable in that session. To play online, close the
  game and start it normally from the Xbox app. The tool refuses to touch the game if anti-cheat is running.

REQUIREMENTS
  - Windows 10/11 (.NET Framework 4.8 is built in).
  - In the game: Graphics settings > "Cinematic Ultrawide Support" = ON.

INSTALL
  The tool lives next to the game exe:
    G:\Games\Gears of War- E-Day\Content\FairlightConcept\Binaries\WinGDK\EDayUltrawide.exe
  and the game's launcher config (Content\MicrosoftGame.config) points at it instead of the
  anti-cheat launcher start_protected_game.exe. Original saved as
  Content\MicrosoftGame.config.edayultrawide-original (another copy in the EDayUltrawide folder).
  Why: the Xbox build only gets its "GDK runtime" when the Xbox app launches it. Started any other way
  (directly, or even inside its package) it crashes with "GDK runtime is not available".

USE
  1. Start the game from the Xbox app as usual (Play).
  2. A minimised "E-Day Ultrawide" console starts the game offline with anti-cheat off, waits for
     the game's code to unpack, finds the cutscene aspect cap and raises it to your screen's aspect.
     If the game says anti-cheat did not load, click OK - that is expected.
  3. The console closes itself when the game exits. Log: EDayUltrawide.log in the WinGDK folder.

ONLINE PLAY
  - One-off: hold Shift while clicking Play -> normal anti-cheat launch, online works, no fix.
  - Always: set Mode=Online in EDayUltrawide.ini (WinGDK folder).
  - Remove the hook completely: double-click EDayUltrawide-Restore.exe (puts the original config
    back). EDayUltrawide-InstallHook.exe re-installs it. A game update or Xbox app "Repair" also
    restores the original config - then run InstallHook again.

KEYS (game window focused)
  F8  toggle the whole fix on/off (bars back, cutscene FOV and gameplay FOV back to the game's own) for an A/B check
  F7  write a STATUS line to the log

SETTINGS (EDayUltrawide.ini - the window edits it; you can also edit it by hand while the game runs)
  Enabled, CutsceneHorPlus, CutsceneFovBasis, CutsceneFovPercent (50-150), GameplayFov (0 = game's own, 60-150)
  ForceAspectRatio   0 = auto from the game window; or 32:9 / 5120x1440 / 3.5556
  Mode, OnlineKey, ToggleKey, StatusKey, Verbose, AttachTimeoutSeconds

LOG
  EDayUltrawide.log (previous run: EDayUltrawide.prev.log). Key lines:
    "Cap found ..."  where the cap lives in your build and how many code sites share it
    "APPLIED: cinematic cap 2.3889 -> 3.5573 ... read-back OK"   the fix is in memory
    "Cap was changed by the game"   the game rewrote it (the app re-applies it; report it if it keeps happening)

HOW IT WORKS
  The game clamps the cinematic view aspect with  min(aspect, 2.388889)  - 3440x1440 is the widest
  "Cinematic Ultrawide Support" allows. The tool finds that instruction by byte signature at runtime
  (no hard-coded addresses, so it survives patches and works on the Xbox build), follows it to the
  constant, and writes your aspect there. Data only - no game code is modified.

CUTSCENE FOV (Hor+, v0.2.0)
  With the cap raised the game keeps the cutscene camera's horizontal FOV, so 32:9 cropped the top and
  bottom. The tool swaps one vtable pointer (UCineCameraComponent::GetCameraView, data only) to a small
  wrapper that runs the game's function and then sets  FOV' = 2*atan(tan(FOV/2) * aspect/basis),
  so the vertical framing equals the 16:9 framing (CutsceneFovBasis=16:9). F8 turns both off/on.
  The log shows lines like "Cutscene camera: FOV 40.0 -> 72.1 deg".
  The game also tilts cutscene framing upward as the screen gets wider (vertical lens shift
  = (aspect*9/32 - 0.5) x sensor height) to keep faces in a cropped 21:9 frame. With Hor+ the frame is
  16:9-tall again, so v0.2.1 sets that tilt to what the game uses at the basis aspect (0 for 16:9) by
  changing its 9/32 constant (one code reference, data only). F8 restores it too.

KNOWN LIMITS
  - Pre-rendered movies (if any) stay 16:9.
  - If a gameplay camera were also a cine camera it would widen too; the log line above would then
    appear outside cutscenes - set CutsceneHorPlus=0 and report it.

TROUBLESHOOTING
  "GDK runtime is not available": the game was started outside the Xbox app. Launch from the Xbox app.
  "launch hook is not installed": a game update/repair restored the config - run EDayUltrawide-InstallHook.exe.
  If the game shows a crash dialog during startup, the tool logs its text and closes it.
  "Anti-cheat is active": an online launch is running; quit it and press Play without Shift.
  "Cap not found": double-click EDayUltrawide-Dump.exe with the game running and report it.
  Windows SmartScreen may warn about an unsigned exe: More info > Run anyway.

CREDITS
  Cap technique discovered by Killswitch - "32x9 Cutscenes and Gameplay FOV Adjuster"
  (nexusmods.com/gearsofwareday/mods/19, Steam only). This is an independent implementation;
  none of that mod's code is included.

```
