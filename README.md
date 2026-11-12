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
  - The game's haze is made lighter in VR, so near and mid-range views are clear and colourful.
  - A render-scale setting trades sharpness for speed.
- **Menus float in front of you,** with a pointer you move with the right stick.
- **Mission screen.** Missions are chosen on a large screen over the window blinds in the operations room, in a cinematic military style: big mission titles, step tabs, numbered mission cards, an intel panel with the briefing, and the mission's overhead recon map behind the plan. It shows time of day, preparation, enemy skill and count, and infil points, and the button hints match your controller (Touch or pad).
- **Weapon bench in VR.** The gun is shown smaller and turns like a turntable, with the parts list on the wall behind it, restyled to match the mission screen (solid dark cards, gold highlights). Works at every bench, including the caged areas and the range. The gun is hidden while the magazine manager is open.
- **Faster in VR (new in 1.2.0):** foveated rendering on NVIDIA cards (full detail in the middle of each lens, less towards the edges) and lighter scope rendering.
- **Scopes:**
  - Magnified scopes stay sharp.
  - A scope shows its magnified picture when you lift it to your eye, and a black lens when it's lowered (which costs nothing to draw).
  - Flip-up magnifiers go dark while flipped aside, and stay live while flipped up.
  - Sight brightness can be set from the pad.
- **Character customisation in 3D (new in 1.2.0):** your character stands on the left of the customisation page as a 3D model you can turn, with the clothing panels on the right.
- **Pad extras** (with the included Steam Input layout): lean with a held stick click while aiming, push-to-talk, next-weapon, manual and emergency reload, and a run toggle.
- **On-screen extras:** a small status display with a picture of your weapon, its fire mode and your grenades, plus screenshots (F3) and recording (Shift+F4).
- **Switch controllers mid-game (new in 1.2.0):** press anything on the PS5 pad and the Touch controllers step aside; press A, B, X, Y or a trigger on a Touch controller to take over again.

## What you need

- **OPERATOR** on Steam.
- **BepInEx 6 (IL2CPP build)**. Tested with `6.0.0-be.791`. It's the mod loader.
- **A PC VR headset** with an OpenXR runtime. Tested on a Quest 3 with **Virtual Desktop** (its VDXR runtime). SteamVR and Quest Link should also work, but haven't been tested as much.
- **Touch controllers or a gamepad.** Meta/Oculus Touch controllers (see [Motion controllers](#motion-controllers-oculus-touch-new-in-110)), or a gamepad through Steam Input; a PS5 DualSense with the included layout is recommended.
- **A reasonably strong PC.** Tested on an RTX 5060 Ti with a Ryzen 7 5700X: a steady 45 fps at 90 Hz with Virtual Desktop SSW (frame smoothing) at full render scale, or about 50 fps at 120 Hz with `RenderScale = 0.9`.

## Installing

1. **Install BepInEx 6 IL2CPP:**
   - Download the "BepInEx Unity (IL2CPP) for Windows x64" bleeding-edge build from [builds.bepinex.dev](https://builds.bepinex.dev/projects/bepinex_be). Build 791 or close to it is fine.
   - **It must be the x64 version.** OPERATOR is a 64-bit game, so the x86 BepInEx won't load and the mod won't start. If you installed x86 by mistake: delete the `BepInEx` folder and `winhttp.dll` from the game folder, install the x64 build, then add the mod again.
   - Unzip it into the OPERATOR game folder, next to `OPERATOR.exe`. To find that folder: in Steam, right-click OPERATOR, then **Manage → Browse local files**.
   - Start the game once and wait at the main menu. The first start takes a few minutes while BepInEx sets itself up. Then quit.
2. **Install the mod and OpenXR installer:**
   - Download the latest `OPERATOR-VR-Mod-vX.Y.Z.zip` from the **Releases** page on the right.
   - Unzip it into the game folder, so that `BepInEx\plugins\OperatorVR\OperatorVR.dll` exists.
   - In that same game folder, double-click `Install-OpenXR.bat`. It downloads the official Unity OpenXR 1.19.0 files and installs them where the mod expects them. Internet access is needed for this one-time step.
3. **Install the pad layout (optional, recommended):** see [Pad layout](#pad-layout) below.
4. **Start VR:**
   - Start your VR runtime first (for example, connect Virtual Desktop).
   - Launch OPERATOR from Steam.
   - In the main menu, press **F4** to start VR. To start it automatically, add `--vr` to the game's Steam launch options.

### Using Virtual Desktop's runtime (recommended for Quest)

Open `BepInEx\config\com.operatorvr.mod.cfg` and set `Runtime = VirtualDesktop` under `[VR]`. This bypasses SteamVR and is usually faster. Recommended Virtual Desktop settings:
- **90 Hz** refresh rate (the game then runs at a steady 45 fps). 120 Hz also works with `RenderScale = 0.9`, at about 50 fps.
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
- **Hand reloading (new in 1.3.0, Touch only).** See "Reloading by hand" below.
- **Front of the gun:** bring your left hand to the front of the rifle (or SMG) and click the **left trigger** once to grip it. Move the hand away and it lets go. (The pistol is gripped by itself.) Away from the front, the left trigger aims down the sights.
- **Run:** tap **A** while pushing the left stick forward; let the stick come back to stop. **Hold A** for the gun's torch.
- **Recenter:** hold the **right grip** for a second, or hold the **right stick in** and click the **left stick**.
- **Switch weapon by reaching:** right hand over your right shoulder + right grip = main / second weapon; right hand down at your right hip + right grip = pistol.
- **Lean** by holding a stick click while aiming. Leaning by tilting your head is also there but off by default (`HeadLean = true`, `HeadLeanAngle`).
- **Vibration.** Both controllers kick when you fire, with short buzzes for magazines, double-taps and Interact.
- **Tap, double-tap and hold actions:**
  - **B:** tap to crouch, double-tap to vault, hold to kick.
  - **X:** tap to drop the magazine (with hand reloading), hold to check the magazine, double-tap to chamber a round or unjam.
  - **Y:** tap to change fire mode, hold to zoom a scope that has a zoom (e.g. the Elcan 1x/4x), double-tap for the health and map screen.
- **Two stick D-pads.** With a thumb resting on one controller's thumbrest, the other controller's stick becomes a D-pad. That gives you sights and magnifier, lasers, grenades, recenter, night vision, next weapon and the tablet.
- **Menus and laptops:** point the laser and pull the right trigger to click. The right stick scrolls lists. Dragging works too, for example medication on the health screen.
- **Weapon bench:** point at the gun, hold the trigger and swing the controller to turn it.
- **Interact:** click the right stick when the prompt shows (TAKE, OPEN, MODIFY WEAPON).

### Reloading by hand

1. **Drop the old magazine:** tap **X**.
2. **Take a new one:** your spare magazines hang at your left hip. Reach down to them and squeeze the **left grip**. The magazine is in your hand.
3. **Load it:** bring it under the gun, top end first. When it catches on the magazine well (a buzz), **push it up** a few centimetres. It clicks in and the gun reloads.

**Check a magazine:** with the left hand at the gun's magazine, squeeze the **left grip**: it comes out into your hand. Hold it up to see how many rounds are left. Push it back up into the gun to put it back. Let go while it's still at the gun and it slides back in; let go away from the gun and it drops.

**Picked up again:** a magazine you dropped can be picked up off the floor (reach down to it and squeeze the left grip), as long as you haven't loaded another one since.

The pistol, rifles and SMGs all work this way. If a magazine won't go in, tap **X** again while it's out and the game reloads for you. With the PS5 pad nothing changes. `ManualReload = false` switches hand reloading off.
- **Two stick D-pads.** With a thumb resting on one controller's thumbrest, the other controller's stick becomes a D-pad. That gives you sights and magnifier, lasers, grenades, recenter, night vision, next weapon and the tablet.
- **Menus and laptops:** point the laser and pull the right trigger to click. The right stick scrolls lists. Dragging works too, for example medication on the health screen.
- **Weapon bench:** point at the gun, hold the trigger and swing the controller to turn it.
- **Interact:** click the right stick when the prompt shows (TAKE, OPEN, MODIFY WEAPON).

Every button can be remapped (`TouchMap`, `TouchDoubleTap`, `TouchHold`), and each setting is explained in the settings file.

## What's new in 1.2.2

- **You hold your breath automatically when you aim** down the sights.

## What's new in 1.2.1

- **All scopes go black when lowered** and show their magnified picture when you lift them to your eye: fixed scopes like the ACOG, the game's other scope type, and flip-up magnifiers (a magnifier no longer shows a soft, low-detail picture while flipped up but not aimed, which also cost about 6 fps).
- **The main weapon bench opens by itself** when you walk up to it and face it (`BenchAutoOpen`, `BenchAutoOpenAt`). The other tables open with Interact as usual.
- **Smoother:** fewer small hitches. Scopes, magnifiers and the mission screen no longer search the whole level every second, and a rendering error the game logs every frame while you aim no longer costs main-thread time.

## What's new in 1.2.0

- **Speed:** foveated rendering through the NVIDIA driver (`FoveatedRendering`, 0 = off, 0.5 = medium). On the test PC the graphics card time per frame dropped from 18–20 ms to about 16–17 ms, enough for a steady 45 fps at 90 Hz (it was 40 at 80 Hz).
- **Speed:** the mod's own work per frame was trimmed (about 2.6 ms → 2.1 ms), and a scope's picture skips effects you can't see in a small lens (`CheapScopes`).
- **Scopes:** a fixed scope (ACOG, Elcan…) shows its picture when you lift it to your eye and a black lens when lowered (`ScopeAsideFrames = 0`; set 3 to keep a live, every-third-frame picture instead). This fixed the scope picture freezing when the gun was lowered.
- **Magnifiers:** a flipped-up magnifier keeps a live picture when you're not aiming.
- **Customisation page:** your character is shown in 3D on the left, in his current clothes, turned as you drag him (`Character3D`, `Character3DCloser` for his distance). The panels on the right stay flat.
- **Status display:** the weapon picture is 30% brighter so it's easier to make out (`WeaponPictureBrightness`).
- **Controllers:** switch between Touch and the PS5 pad mid-game, automatically or by holding both thumbsticks for 2 seconds (`AutoSwitchControls`).

## Settings

All settings are in `BepInEx\config\com.operatorvr.mod.cfg`. The file is created on the first start, and each setting is explained there. The most useful ones:

- `[Rendering] RenderScale`: sharpness against speed. 1.0 is full resolution; try 0.8–0.9 on slower PCs.
- `[Rendering] FoveatedRendering`: NVIDIA cards only. 0.5 is a good start; 0 switches it off.
- `[Rendering] AmbientOcclusion`, `TreeShadows`: switch shading and tree shadows on or off.
- `[Menus] BenchGunSize`, `HealthArrowSize`: the weapon bench gun's size and the health-screen pointer's size.
- `[Controls] LeanHoldTime`: how long a stick click must be held to lean.
- `[Rendering] HazeThinning`: how much lighter the haze is in VR (4 by default; 1 = the game's haze). `NoHaze` switches it off completely.
- `[Menus] BenchTheme`: the weapon bench restyle (on by default).
- `[Controls] GunShiftForward`: move the gun further from your face (metres). Ctrl + Shift + Up/Down changes it while playing.

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
