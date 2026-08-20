# Sons of the Forest VR Mod (Unofficial)

> Full VR for *Sons of the Forest* on PC, through SteamVR. The game has no VR mode; this adds one.

Built with **MelonLoader** and **Il2CppInterop**, driving **OpenVR** directly. Works with any
OpenVR headset: Index, Vive, Quest over Link or Virtual Desktop, and others. No game file is
replaced, and existing saves and mods keep working.

**Version 0.0.4-alpha**

---

## Features

**Stereo rendering.** Two eye cameras driven by the headset's own geometry: per-eye offsets and
projection matrices from OpenVR, render targets sized from the runtime's recommendation, textures
submitted to the compositor at end of frame. The eye cameras copy the render configuration of the
active game camera, HDRP volumes and post-processing included, so the VR view matches what the flat
view would show.

**6DoF head tracking.** Position as well as rotation, measured from a neutral pose captured at scene
start and clamped so the view cannot drift off the character's head.

**Hands follow the controllers.** The game already carries Final IK on the player, so the existing
solvers are pointed at the controllers rather than a new rig being added. Elbows are aimed at goals
placed out to the side, which is what stops the arms folding across the chest.

**Real body turn detection.** The two controllers stand in for the shoulders: when the line between
them turns with the head, the player has turned their whole body in the room and the character
follows. A glance over the shoulder leaves the character alone, which also keeps scripted animations
intact.

**Turning.** Snap turning in fixed steps, or smooth turning, chosen from the settings panel.

**Everything flat is put in the room.** Menus, options, inventory, the heads up display and the
loading screen are moved onto world-anchored panels. A screen space canvas never reaches the eye
textures, so without this they exist only on the monitor.

**Laser pointer.** A beam from the controller drives the pointer on every panel. Pressing the other
trigger hands the beam to the other hand. The thumbstick still works when the beam is off the panel.

**Settings panel in the headset.** Around thirty settings on a two column panel, opened with a
controller, with sliders, checkboxes, step buttons, and OK and Cancel. Values apply as the slider
moves, so the effect is judged by looking at it; Cancel puts back what was there on opening. The
character is hidden while it is open, so a hand does not sit between the reader and the text.

**Cutscene and menu awareness.** Outside gameplay the game keeps its own camera: the rig follows it
instead of steering it, head movement never reaches the character, and gameplay input is not
injected into menus.

**Diagnostics.** A frame profiler separating the mod's own cost from time spent waiting on the
compositor, a camera census, a watchdog reporting where the main thread stopped if the game locks
up, and one-shot surveys of the player skeleton and the interface layout.

### Controls

| Action | Control |
|---|---|
| Move | Left thumbstick |
| Turn | Right thumbstick, left and right |
| Run | Right thumbstick up |
| Crouch | Right thumbstick down |
| Primary action, interact, place | Right trigger |
| Secondary action, melee | Left trigger, or a fast right hand swing |
| Take, use, save | Either grip |
| Drop | Either grip, held |
| Jump | Both controllers raised sharply |
| Inventory | Left Y |
| Lighter | Right B |
| Reload, rotate | Right A |
| GPS tracker | Left grip at the right shoulder |
| Walkie-talkie | Right grip at the left shoulder |
| Book | Right grip at the right shoulder |

Gestures and buttons are translated into the keyboard and mouse events the game already
understands, so no game code is changed.

---

## Requirements

- *Sons of the Forest* on Steam, PC
- An OpenVR headset, with SteamVR running
- MelonLoader 0.7.3, included in the installer

---

## Installation

Run `SONS_VR_oneclick.exe` from the release and point it at the game folder. It refuses any folder
without `SonsOfTheForest.exe`, since installing elsewhere succeeds and then quietly does nothing.

By hand instead: copy the contents of `dist/` into the game folder, keeping the structure.

**The first run needs an internet connection and takes several minutes**, with the window apparently
doing nothing. MelonLoader downloads the tools it reads the game with, then generates
`MelonLoader/Il2CppAssemblies/` from the copy of the game on that machine. Neither is shipped, and
neither could be: both belong to one install and one game version. Later starts are quick.

Start SteamVR first, then the game.

---

## Settings

Most settings are on the panel in the headset. All of them, including the ones that are not, live in
`UserData/MelonPreferences.cfg` under `[SonsVR]`, written on first launch, each with a description.
Values in the file win over the defaults in code, so a file written by an older version keeps its
values.

The ones worth knowing:

| Setting | Default | What it does |
|---|---|---|
| `RenderScale` | `0.70` | Multiplier on the eye resolution SteamVR asks for. Lower is faster and softer. |
| `FovTangentHorizontal` / `FovTangentVertical` | `0.90` / `0.86` | Render less than the full field of view, dropping the outer edge where the lens shows little. Worth roughly a quarter of the pixels. Push further and a black border appears. |
| `DesktopMirror` | `true` | Show an eye on the monitor instead of rendering the game camera again. Removes a whole render pass. |
| `PositionalTrackingRange` | `0.30` | How far in metres the view may move off the character's head. `0` pins it. |
| `PlayerEyeHeightOffset` | `0.19` | Raises the viewpoint: the game hangs its camera nearer the neck than the eyes. |
| `HudSize`, `HudOffsetX`, `HudOffsetY` | `0.47`, `0.078`, `-0.054` | Size and placement of the whole display. |
| `SettingsPanelHeight` / `SettingsPanelDistance` | `1.4` / `1.6` | Size and distance of the settings panel, in metres. Move them together. |
| `HandTracking` | `true` | Arms follow the controllers. |
| `SmoothTurn` | `false` | Smooth turning instead of snap. |
| `Profile` | `true` | Report per-frame cost to the MelonLoader log. |

---

## Building

The project references the IL2CPP assemblies of an installed, MelonLoader-patched copy of the game.
Those are not, and must not be, part of this repository.

| Property | Meaning |
|---|---|
| `RefDir` | Install whose `MelonLoader` folder provides the reference assemblies |
| `GameDir` | Playable install the built mod is copied into |

They may be the same directory. Override without editing the file:

```
dotnet build -p:GameDir="D:\Games\Sons Of The Forest"
```

A missing or wrong `RefDir` fails the build with a readable message rather than several hundred
"type not found" errors.

---

## Known limitations

- **Held items are not aligned to the hands.** The arms follow the controllers, but a weapon still
  sits where the animation puts it.
- **Two full render passes per frame.** Single-pass stereo needs a native XR provider, which has to
  be present when the game is built and cannot be added to a shipped one. What has been done
  instead: mirroring an eye to the desktop rather than rendering the game camera again, field of
  view tangents, and switching off per-eye effects that make no sense in a headset. The mod's own
  code measures around 0.35 ms of a 20 ms frame, so the cost is in the engine drawing two cameras.
  Shadows are the largest remaining per-camera item.
- **Individual screen space overlays can still be missing from the headset**, beyond the menus,
  inventory and display that are handled.
- **Cutscenes take their heading from the headset**, so the framing a cutscene intended is not
  always what the player is looking at. Deliberate: inheriting a tumbling camera is the fastest way
  to make someone ill.
- **No official VR support.** All of this is injected into a game that has no VR mode.

---

## Roadmap

- Held items aligned to the hands, which needs the weapon IK the game already runs to be reconciled
  with the hand IK this mod drives
- Shadow cost per eye
- The hidden area mesh the runtime provides, for the corners the lens never shows
- Remaining game actions on the controllers: building, crafting, weapon switching
- Comfort options: vignette tunnelling, height calibration
- Spatial audio through a SteamVR listener

---

## Credits

**Author:** Antonio Mauriello (`Anthony`)

Built on MelonLoader, Il2CppInterop and the SteamVR Unity plugin. Thanks to the MelonLoader and
Sons of the Forest modding communities.

---

## License

Copyright (C) 2025 Antonio Mauriello

This program is free software: you can redistribute it and/or modify it under the terms of the GNU
General Public License as published by the Free Software Foundation, version 2 of the License.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
General Public License for more details.

You should have received a copy of the GNU General Public License along with this program. If not,
see <https://www.gnu.org/licenses/gpl-2.0.html>.

*Sons of the Forest* is a trademark of Endnight Games Ltd. This mod is **not affiliated** with
Endnight Games, Steam, or Valve Corporation.
