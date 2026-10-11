# Changelog

## v0.3.1 — 2026-10-11
- **Fixed:** holding Shift (or Mode=Online) now gives a normal online launch with anti-cheat on. The offline anti-cheat switch was leaking into it.

## v0.3.0 — 2026-10-11
**Now a window app with separate cutscene and gameplay FOV.**
- **New:** window with a cutscene FOV slider (50–150 %) and a gameplay FOV override (60–150°), applied live and saved to `EDayUltrawide.ini`.
- **New:** start-the-game, install/remove launch hook, "always start online" and open-log buttons.
- **Changed:** settings are re-read while the game runs, so editing the ini by hand also works live.

## v0.2.1
- **Fixed:** cutscenes sitting low / cut off at the bottom. The vertical tilt the game adds on wide screens is now zero at the 16:9 basis.

## v0.2.0
- **New:** cutscene Hor+: the camera keeps the 16:9 top-to-bottom view, so 32:9 no longer crops the top and bottom.

## v0.1.x
- Removes the black bars in cutscenes on screens wider than 21:9.
- Starts with the Xbox app via a launch hook (the Game Pass build only runs when the Xbox app starts it).
