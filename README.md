# OPERATOR VR Mod

Play **OPERATOR** (Steam) in PC VR. It's built and tested with a **Meta Quest 3 through Virtual Desktop**, played with **Touch motion controllers** or a **PS5 DualSense pad**.

This is an unofficial fan mod. It isn't made by or connected with the developers of OPERATOR.

> **Single-player and private co-op only.** Please don't use mods in public multiplayer. OPERATOR has online play, and modded clients may be flagged there. You use this at your own risk.

## What it does

- **Full head tracking.** The game is rendered for both eyes on your headset.
- **Steady VR view.** The view doesn't bob when you aim with the pad, and you can recenter with **F10**.
- **VR-friendly graphics:**
  - Effects that look wrong or cost too much in VR are switched off: motion blur, depth of field, screen-space reflections, volumetric clouds and tree/grass shadows.
  - Soft ambient-occlusion shading is kept.
  - A render-scale setting trades sharpness for speed.
- **Menus float in front of you,** with a pointer you move with the right stick.
- **Mission screen.** Missions are chosen on a large screen over the window blinds in the operations room. It shows time of day, preparation, enemy skill and count, infil points and the briefing.
- **Weapon bench in VR.** The gun is shown smaller and turns like a turntable, with the parts list on the wall behind it.
- **Scopes:**
  - Magnified scopes stay sharp.
  - Flip-up magnifiers go dark while flipped aside.
  - Sight brightness can be set from the pad.
- **Pad extras** (with the included Steam Input layout): lean with a held stick click while aiming, push-to-talk, next-weapon, manual and emergency reload, and a run toggle.
- **On-screen extras:** a small status display with weapon, fire mode and grenades, plus screenshots (F3) and recording (Shift+F4).

## What you need

- **OPERATOR** on Steam.
- **BepInEx 6 (IL2CPP build)**. Tested with `6.0.0-be.791`. It's the mod loader.
- **A PC VR headset** with an OpenXR runtime. Tested on a Quest 3 with **Virtual Desktop** (its VDXR runtime). SteamVR and Quest Link should also work, but haven't been tested as much.
- **Touch controllers or a gamepad.** Meta/Oculus Touch controllers (see [Motion controllers](#motion-controllers-oculus-touch-new-in-110)), or a gamepad through Steam Input; a PS5 DualSense with the included layout is recommended.
- **A reasonably strong PC.** Tested on an RTX 5060 Ti: a steady 40 fps at 80 Hz with Virtual Desktop SSW (frame smoothing).

## Installing

1. **Install BepInEx 6 IL2CPP:**
   - Download the "BepInEx Unity (IL2CPP) for Windows x64" bleeding-edge build from [builds.bepinex.dev](https://builds.bepinex.dev/projects/bepinex_be). Build 791 or close to it is fine.
   - Unzip it into the OPERATOR game folder, next to `OPERATOR.exe`. To find that folder: in Steam, right-click OPERATOR, then **Manage → Browse local files**.
   - Start the game once and wait at the main menu. The first start takes a few minutes while BepInEx sets itself up. Then quit.
2. **Install the mod:**
   - Download the latest `OPERATOR-VR-Mod-vX.Y.Z.zip` from the **Releases** page on the right.
   - Unzip it into the game folder, so that `BepInEx\plugins\OperatorVR\OperatorVR.dll` exists.
3. **Install the pad layout (optional, recommended):** see [Pad layout](#pad-layout) below.
4. **Start VR:**
   - Start your VR runtime first (for example, connect Virtual Desktop).
   - Launch OPERATOR from Steam.
   - In the main menu, press **F4** to start VR. To start it automatically, add `--vr` to the game's Steam launch options.

### Using Virtual Desktop's runtime (recommended for Quest)

Open `BepInEx\config\com.operatorvr.mod.cfg` and set `Runtime = VirtualDesktop` under `[VR]`. This bypasses SteamVR and is usually faster. Recommended Virtual Desktop settings:
- **80 Hz** refresh rate.
- **Synchronous Spacewarp (SSW): Always enabled.**

## Keys

| Key | What it does |
|---|---|
| F4 | Start VR |
| F9 | Stop VR (back to flat screen) |
| F10 | Recenter the view |
| F3 | Screenshot of both eyes (saved in `BepInEx\OperatorVR_eyes`) |
| Shift+F4 | Start or stop recording |
| F7 | Change how far away the menus float |

## Pad layout

The file `steam-input/OPERATOR-PS5-layout.vdf` is a Steam Input layout for the PS5 DualSense. The mod's pad features rely on the keys it sends. The main buttons:

| Button | Action |
|---|---|
| Left stick | Move |
| Right stick | Look and aim; also moves the menu pointer |
| R2 / L2 | Shoot / aim down the sights. R2 also selects on the mission screen |
| Cross | Kick. Hold to vault. Tap while pushing forward to toggle running |
| Circle | Crouch (hold: prone). On the mission screen: back |
| Square | Reload |
| Triangle | Next weapon (hold: sidearm) |
| R1 | Grenade |
| D-pad up / down | Switch sights or flip the magnifier / fire mode |
| D-pad left / right | Laser / flashlight |
| Left touchpad | Top: inspect weapon. Other sides: manual reload |
| L1 (hold) | Second layer for the buttons below |
| L1 + Square | Manual reload |
| L1 + D-pad up | Night vision |
| L1 + D-pad right / left | Sight brighter / dimmer |
| L3 (left-stick click) | Not aiming: push-to-talk. Aiming: hold to lean left |
| R3 (right-stick click) | Not aiming: use/interact. Aiming: hold to lean right |
| Share | Health and map screen |
| Options | Menu |

**To use the layout:**
1. Close Steam completely.
2. Copy the file to `Steam\steamapps\common\Steam Controller Configs\<your Steam account number>\config\1913370\`.
3. Start Steam, open OPERATOR's controller settings, and pick the layout from **Your Layouts**.

If you use another pad, map its buttons to the same keyboard keys (they're listed in the `.vdf`).

## Motion controllers (Oculus Touch), new in 1.1.0

Version 1.1.0 adds full motion controls for **Oculus/Meta Touch controllers**. The PS5 pad still works exactly as before.

**To switch them on:** set `TouchControls = true` under `[Controls]` in `BepInEx\config\com.operatorvr.mod.cfg`. The file `OPERATOR-VR-Touch-Controls.png` in the zip shows every button.

- **Aim with your hand.** The gun points where the right controller points.
- **Virtual stock.** Press the right grip: your right hand locks as the stock and your left hand steers the gun, which makes it much steadier. Press again to let go. **GUNSTOCK ACTIVATED** shows at the lower right while it's on.
- **Lean by tilting your head** left or right (`HeadLeanAngle`, 8° by default).
- **Vibration.** Both controllers kick when you fire, with short buzzes for the stock, double-taps and Interact.
- **Tap, double-tap and hold actions:**
  - **B:** tap to crouch, double-tap to vault, hold to kick.
  - **X:** tap to reload, hold to check the magazine, double-tap to chamber a round or unjam.
  - **Y:** tap to change fire mode, double-tap for the health and map screen.
- **Two stick D-pads.** With a thumb resting on one controller's thumbrest, the other controller's stick becomes a D-pad. That gives you sights and magnifier, lasers, grenades, recenter, night vision, next weapon and the tablet.
- **Menus and laptops:** point the laser and pull the right trigger to click. The right stick scrolls lists. Dragging works too, for example medication on the health screen.
- **Weapon bench:** point at the gun, hold the trigger and swing the controller to turn it.
- **Interact:** click the right stick when the prompt shows (TAKE, OPEN, MODIFY WEAPON).

Every button can be remapped (`TouchMap`, `TouchDoubleTap`, `TouchHold`), and each setting is explained in the settings file.

## Settings

All settings are in `BepInEx\config\com.operatorvr.mod.cfg`. The file is created on the first start, and each setting is explained there. The most useful ones:

- `[Rendering] RenderScale`: sharpness against speed. 1.0 is full resolution; try 0.8–0.9 on slower PCs.
- `[Rendering] AmbientOcclusion`, `TreeShadows`: switch shading and tree shadows on or off.
- `[Menus] BenchGunSize`, `HealthArrowSize`: the weapon bench gun's size and the health-screen pointer's size.
- `[Controls] LeanHoldTime`: how long a stick click must be held to lean.

## Known limits

- Rendering is multi-pass (each eye drawn separately), so VR needs a strong PC. The game wasn't built with single-pass stereo.
- The game's own ambient occlusion (HTrace) only drew in one eye, so it's replaced by HDRP's own.
- Built for the current game version. A game update may break parts of it until the mod is updated.

## Building from source

1. Install the .NET 6 SDK and set up BepInEx as above, so that `BepInEx\interop` has been generated.
2. Run:
   ```
   dotnet build OperatorVR.csproj -c Release -p:GameDir="C:\path\to\OPERATOR"
   ```
   The built DLL is copied into the game's `BepInEx\plugins\OperatorVR` folder. Add `-p:SkipInstall=true` to build without copying.

## Licence

MIT. See [LICENSE](LICENSE).
