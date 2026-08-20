# Sons of the Forest VR Mod (Unofficial)

> **Bring full VR support to *Sons of the Forest* using SteamVR and OpenVR — without official VR mode.**

This mod injects a complete VR pipeline into *Sons of the Forest* (PC version), enabling immersive gameplay using **Valve Index**, **HTC Vive**, **Meta Quest (via Air Link/Virtual Desktop)**, and other **OpenVR-compatible headsets**.

Built with **MelonLoader**, **Il2CppInterop**, and **SteamVR Plugin**, it replaces mouse/keyboard input with natural hand-based interaction and stereo rendering — all while preserving the original game logic.

---

## ✅ Current Features (0.0.3 Alpha)

- **Full Stereo Rendering**  
  Dedicated left/right eye cameras driven by the headset's own geometry: per-eye offsets and projection
  matrices read from OpenVR, render targets sized from the runtime's recommendation, and native texture
  submission to the compositor at end of frame.  
  The eye cameras mirror the render configuration of the active game camera, including HDRP volumes and
  post-processing, so the VR view matches what the flat view would show.

- **Full 6DoF Head Tracking**  
  Position as well as rotation, measured from a neutral pose captured at scene start and clamped so the
  view cannot drift off the character's head.

- **VR Locomotion & Movement**
    - Left thumbstick: walk forward/backward, strafe left/right
    - Right thumbstick **forward**: toggle sprint
    - The character turns with the head while moving, and stays put while you simply look around

- **Real Body Turn Detection**  
  The two controllers stand in for the shoulders: when the line between them turns with the head, the
  player has turned their whole body in the room and the character follows. A glance over the shoulder
  leaves the character alone, which also keeps scripted animations intact.

- **Snap Turning**  
  Rotate your view in fixed increments (default: 30°) using the **right thumbstick (left/right)** — reduces motion sickness.

- **Menus and Inventory in World Space**  
  Flat menus are moved onto a panel anchored in the room rather than pinned to the head, pointed at with
  the left thumbstick and clicked with the left trigger. The inventory is presented on the ground in
  front of the player, with the pointer driven by gaze.

- **Cutscene and Menu Awareness**  
  Outside of gameplay the game keeps its own camera: the rig follows it instead of steering it, head
  movement never reaches the character, and gameplay input is not injected into menus.

- **Configurable at Runtime**  
  Every tunable value lives in `UserData/MelonPreferences.cfg` under `[SonsVR]` — resolution scale, field
  of view, comfort settings, menu placement, and the diagnostics below. No rebuild needed.

- **Built-in Diagnostics**  
  A frame profiler that separates the mod's own cost from time spent waiting on the compositor, a camera
  census, and a watchdog that reports where the main thread stopped if the game ever locks up.

- **Gesture-Based Actions**
    - **Jump**: Raise both controllers quickly upward
    - **Melee Attack**: Swing right hand fast (velocity-based detection)
    - **Interact/Use**: Squeeze **left or right grip** → mapped to `E`
    - **Inventory**: Press **Y button (left controller)** → mapped to `I`
    - **Crouch**: Push **right thumbstick down** → mapped to `Left Ctrl`


- **Action and Key Mapping**
- FORWARD: W Key              : Left.Thumb-UP 
- BACKWARD: S Key             : Left.Thumb-DoWN
- RIGHT: D Key                : Left.Thumb-RIGHT 
- LEFT: A Key                 : Left.Thumb-LEFT 
- CROUCH: Left Ctrl Key       : Right.Thumb-DOWN
- DROP: G Key                 : Left.GRIP or Right.GRIP  Long grip release 
- INVENTORY: I Key            : Left.Y-BUTTON 
- JUMP: Space Bar Key         : "GESTURE MUVE UP BOTH MC"
- LIGHTER: L Key              : Right.B-BUTTON
- RELOAD: R Key               : Right.A-BUTTON
- RUN: Left Shift Key         : Right.Thumb-UP
- TAKE: E Key                 : Left.GRIP or Right.GRIP
- DISMANTLE: C Key 
- UTILITY: UNBOUND 
- GPS TRACKER: M Key          : Left.GRIP on Right Sholder 
- WALKIE-TALKIE: T Key        : Right.GRIP on Left Sholder 
- BOOK: B Key                 : Right.GRIP on Right Sholder
- ROTATE RIGHT: R Key         : Right.A-BUTTON
- ROTATE LEFT: Q Key 
- PRIMARY ACTION: LMB         : Right.TRIGGER
- SECONDARY ACTION: RMB       : Left.TRIGGER  and "MELE ATTACK GESTURE"
- INTERACT: LMB               : Right.TRIGGER 
- ALTERNATE INTERACT: RMB     : Left.TRIGGER
- PLACE ELEMENT: LMB          : Right.TRIGGER 
- TOGGLE PLACE MODE: RMB      : Left.TRIGGER 
- TERTIARY ACTION: MMB 
- SLEEP: Z Key
- SAVE: E Key                 : Left.GRIP or Right.GRIP 
- SKIP: S Key                 : Left.Thumb-DoWN
- RESET: UNBOUND 
- SELECT: E Key               :Left.GRIP or Right.GRIP 
- BACK: Left Arrow Key 
- CANCEL STRUCTURE: X Key 
- TOGGLE BOOK MODE: X Key 
- BOOK FLIP NEXT PAGE: RMB     : Left.TRIGGER 
- BOOK FLIP PREVIOUS PAGE: LMB : Right.TRIGGER 
- PLAC: C Key 
- CYCLE GRAB BAG CATEGORY: Q Key


- **Automatic Camera & Player Detection**  
  Dynamically finds the game’s `MainCameraFP` and `LocalPlayer` across scenes (main world, caves, interiors).

- **Mouse & Keyboard Emulation**  
  All VR gestures are translated into synthetic keyboard/mouse events the game understands — **no game code changes needed**.

- **Compatibility**  
  Works with existing Sons of the Forest mods and saves. No modification of game files required.

---

## ⚙️ Requirements

- **Game**: *Sons of the Forest* (Steam, PC version)
- **VR Headset**: Any **OpenVR-compatible** device (Index, Vive, Quest via SteamVR, etc.)
- **Mod Loader**: [MelonLoader](https://melonwiki.xyz/) (v0.6.0)
- **Runtime**: .NET 6+ 

---

## 📥 Installation

1. Install **MelonLoader** into your *Sons of the Forest* folder.
2. Copy the `SonsVR_Mod.dll` file into `SonsOfTheForest/Mods/`.
3. Ensure `openvr_api.dll` and `SonsSteamVR_IL2CPP.dll` is present in `SonsOfTheForest/UserLibs/` (included in release).
4. Launch the game in **VR mode** (via SteamVR).

> 💡 **Note**: The game must be launched **through SteamVR**. Do not use desktop mode.

---

## 🛠 Configuration

Settings live in `SonsOfTheForest/UserData/MelonPreferences.cfg`, section `[SonsVR]`, written on first
launch. Edit between sessions; values in the file win over the defaults in code.

The ones worth knowing:

| Setting | Default | What it does |
|---|---|---|
| `RenderScale` | `0.70` | Multiplier on the eye resolution SteamVR asks for. Lower is faster and softer. |
| `FovTangentHorizontal` / `FovTangentVertical` | `1.0` | Render less than the full field of view. `0.90` / `0.86` drops the outer edge, where the lens shows little, and saves roughly a quarter of the pixels. Push too far and a black border becomes visible. |
| `DesktopMirror` | `false` | Strip the game camera and show the left eye on the monitor instead. Removes a whole render pass. |
| `PositionalTrackingRange` | `0.30` | How far in metres the view may move off the character's head. `0` pins it. |
| `MenuInWorldSpace` | `true` | Put flat menus on a panel in the room so they can be seen in the headset. |
| `MenuHeightOffset` | `0.50` | Raises the viewpoint while a menu is open, so it is not read from inside the scenery. |
| `InventoryLift` | `0.15` | Clearance kept above the ground, so inventory items do not sink into snow or tall grass. |
| `Profile` | `true` | Report per-frame cost to the MelonLoader log. |

Every switch is documented in the file itself.

---

## 🔨 Building

The project references the IL2CPP assemblies of an installed, MelonLoader-patched copy of the game.
Those are not, and must not be, part of this repository.

Two properties at the top of `SonsVR_Mod.csproj` point at them:

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

## 📋 TODO / Roadmap

### Planned Features & Fixes

- ✅ **External configuration** — done, via MelonPreferences.

- ✅ **Camera & position handling** — done. Camera selection now judges what a camera *draws*, not where it
  lives: cameras rendering nothing, or only interface layers, are skipped, which covers loading screens
  and overlays. Cutscene and menu cameras are followed rather than steered.

- ✅ **UI in VR** — done for menus, options and inventory, on world-anchored panels.

- 🎮 **VR-native settings menu**  
  Comfort options exposed in the headset rather than in a text file.

- 🕹 **Implement Missing Game Actions**  
  Map critical gameplay actions (e.g., building, crafting, weapon switching, context actions) to motion controller inputs.

- 🤖 **Full 6DOF Arm & Hand IK**  
  Implement inverse kinematics for arms/hands to match real-world controller poses (not just camera rotation).

- ⚡ **Optimize Stereo Rendering Pipeline**  
  Measurement so far: the mod's own code costs around 0.35 ms of a 20 ms frame, so the budget is spent in
  the engine rendering two extra cameras, not in managed code. What has helped, in order:
    - Neutralising the game camera and mirroring an eye to the desktop instead
    - Field of view tangents, dropping the outer edge the lens barely shows
    - Turning off per-eye effects that make no sense in a headset: motion blur, depth of field, screen
      space reflections
  
  Still open:
    - Shadow cost per eye, the largest remaining per-camera item
    - The hidden area mesh the runtime provides, for the corners the lens never shows
    - Single-pass stereo, which needs native XR and is not available: the provider has to be present at
      build time and cannot be added to a shipped game

### Additional Suggested Improvements

- 🧭 **Comfort Options**  
  Add smooth turning, vignette-based tunneling, and height calibration.

- 🗣 **VR Audio Support**  
  Integrate HRTF or SteamVR audio listeners for spatial sound.

- 🧱 **UI in VR**  
  Render game HUD/UI on a virtual screen or world-locked panel (avoid screen-space overlay).

- 🔄 **Pause & Menu Handling**  
  Auto-disable HMD look during pause/inventory to prevent disorientation.

- 📊 **Performance HUD**  
  Optional FPS/MSAA/eye texture stats for debugging.

---

## ⚠️ Known Limitations

- **Weapon/Tool alignment**: Held items are not yet aligned to hand poses (6DOF).
- **Screen space interface**: menus and the inventory are handled, but individual overlays the game draws
  straight to the screen can still be missing from the headset.
- **Cutscenes**: the view follows the position of the cutscene camera but takes its heading from the
  headset, so the framing a cutscene intended is not always what the player is looking at. This is
  deliberate — inheriting a tumbling camera is the fastest way to make someone ill.
- **Performance**: two full render passes per frame. Native single-pass stereo is not reachable from a
  mod on a shipped build; see the roadmap for what has been done instead.
- **No official VR support**: everything here is injected into a game that has no VR mode.

---

## 🙌 Credits

- **Mod Author**: Antonio Mauriello (`Anthony`)
- **Based on**: MelonLoader, Il2CppInterop, SteamVR Unity Plugin
- **Special Thanks**: MelonLoader & Sons of the Forest modding communities

---

## 📜 License

Copyright (C) 2025 Antonio Mauriello

This program is free software: you can redistribute it and/or modify  
it under the terms of the **GNU General Public License as published by**  
the Free Software Foundation, **version 2** of the License.

This program is distributed in the hope that it will be useful,  
but WITHOUT ANY WARRANTY; without even the implied warranty of  
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the  
GNU General Public License for more details.

You should have received a copy of the GNU General Public License  
along with this program.  If not, see <https://www.gnu.org/licenses/gpl-2.0.html>.

*Sons of the Forest* is a trademark of Endnight Games Ltd.  
This mod is **not affiliated** with Endnight Games, Steam, or Valve Corporation.
---

> 🌐 **Enjoy VR survival horror — now in full immersion!**  
> *"The forest remembers..." — now in stereo 3D.*