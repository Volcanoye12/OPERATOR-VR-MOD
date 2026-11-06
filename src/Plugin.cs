using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace OperatorVR;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "com.operatorvr.mod";
    public const string Name = "OperatorVR";
    public const string Version = "1.2.1";

    internal static ManualLogSource Logger;
    internal static ConfigEntry<OpenXRNative.RenderMode> StereoRenderMode;
    internal static ConfigEntry<bool> SkipExtraCameras;
    internal static ConfigEntry<float> RenderScale;
    internal static ConfigEntry<bool> AntiAliasing;
    internal static ConfigEntry<bool> VRPerformance, PlainLensEffects, ReflectionProbesOnce;
    internal static ConfigEntry<int> SunShadowCascades;
    internal static ConfigEntry<float> ViewDistance;
    internal static ConfigEntry<float> LodScale;
    internal static ConfigEntry<float> ShadowDistance;
    internal static ConfigEntry<bool> FoliageOcclusionCulling;
    internal static ConfigEntry<bool> Shadows;
    internal static ConfigEntry<bool> HideBody;
    internal static ConfigEntry<bool> FreeLeftHand;
    internal static ConfigEntry<bool> GunFollowsHand, SteadyGun;
    internal static ConfigEntry<bool> HideLeftArm;
    internal static ConfigEntry<bool> HideChest;
    internal static ConfigEntry<bool> HeadAimPitch;
    internal static ConfigEntry<bool> SteadyEyeHeight, LockEyeWhenAiming, ExactMagCount;
    internal static ConfigEntry<string> RecenterKey;
    internal static ConfigEntry<bool> CustomisationScreen, Character3D, CheapScopes, BenchAutoOpen;
    internal static ConfigEntry<float> BenchAutoOpenDistance;
    internal static ConfigEntry<string> BenchAutoOpenName, BenchAutoOpenAt;
    internal static ConfigEntry<int> SunShadowFrames;
    internal static ConfigEntry<bool> WeaponBenchOnTop;
    internal static ConfigEntry<float> BenchGunSize;
    internal static ConfigEntry<float> HealthArrowSize;
    internal static ConfigEntry<bool> PlainBackdrops;
    internal static ConfigEntry<float> BenchStepBack;
    internal static ConfigEntry<float> LeanHoldTime;
    internal static ConfigEntry<float> BenchWeaponForward, BenchSpinSpeed, BenchDragGain, LaserDragSpeed;
    internal static ConfigEntry<string> MenuClickButton;
    internal static ConfigEntry<bool> GazeDot;
    internal static ConfigEntry<bool> Diagnostics;
    internal static ConfigEntry<bool> PadRangeSwitch;
    internal static ConfigEntry<bool> FlipWithSightSwitch, DarkMagnifierAside, KeepMagnifierLive;
    internal static ConfigEntry<bool> SharpScopes, ScopeGuard;
    internal static ConfigEntry<int> ScopeAsideFrames;
    internal static ConfigEntry<float> Saturation, Contrast, NightVisionLensScale, FoveatedRendering, DlssPercent;
    internal static ConfigEntry<bool> AllowDLSS;
    internal static ConfigEntry<bool> NightVisionFill;
    internal static ConfigEntry<string> EmergencyReloadKey, ManualReloadKey, TabletKey, RunKey, NextWeaponKey;
    internal static ConfigEntry<float> GunShiftRight, GunShiftUp, GunShiftForward, RunSpeed;
    internal static ConfigEntry<string> AimEye;
    internal static ConfigEntry<bool> AutoStart;
    internal static ConfigEntry<string> Runtime;
    internal static ConfigEntry<float> AutoStartDelay;
    internal static ConfigEntry<bool> SunGlare;
    internal static ConfigEntry<bool> GroundUsesTerrainMaterial, TerrainGrass;
    internal static ConfigEntry<float> InteractReach;
    internal static ConfigEntry<bool> PauseInMainMenu, InteractPastGun;
    internal static ConfigEntry<bool> GroundLod, GroundShadows, GroundTessellation, GrenadeCount, MagModel, StickClickLean, SunShadows, StatusHud, Compass, TalkIndicator, RecenterOnLoad, WeaponPicture, SunShadowsOutdoors, AmbientOcclusion, TreeShadows;
    internal static ConfigEntry<float> AmbientOcclusionStrength, WeaponPictureBrightness, Character3DCloser;
    internal static ConfigEntry<float> OnlineViewDistance, OnlineLodScale, HudDistance, HudSize;
    internal static ConfigEntry<bool> TouchAsGamepad;
    internal static ConfigEntry<bool> TouchControls;
    internal static ConfigEntry<string> TouchMap;
    internal static ConfigEntry<string> TouchDoubleTap, TouchHold;
    internal static ConfigEntry<string> TouchDpadButton, TouchLeftDpadButton;
    internal static ConfigEntry<string> VoiceMic;
    internal static ConfigEntry<float> TouchTurnSpeed;
    internal static ConfigEntry<float> TouchLookUpDown;
    internal static ConfigEntry<bool> HandAimPitch;
    internal static ConfigEntry<float> HandAimGain;
    internal static ConfigEntry<bool> HandAimYaw;
    internal static ConfigEntry<bool> InteractWithHead;
    internal static ConfigEntry<bool> HoldToInteract;
    internal static ConfigEntry<float> TouchTurnDegrees;
    internal static ConfigEntry<float> HandAimPitchOffset;
    internal static ConfigEntry<bool> VirtualStock;
    internal static ConfigEntry<string> VirtualStockButton;
    internal static ConfigEntry<bool> Haptics, HapticsBothHands, HeadLean, HandAimSmoothing, HandAimNoSway;
    internal static ConfigEntry<float> HandAimMinCutoff, HandAimBeta, LeftHandMovement, LeftHandSmoothing;
    internal static ConfigEntry<bool> AutoAds, AutoSwitchControls;
    internal static ConfigEntry<float> AutoAdsDistance, AutoAdsAngle;
    internal static ConfigEntry<float> HapticsShot, HeadLeanAngle, LeanSpeed, MagLabelHeight;
    internal static ConfigEntry<bool> VirtualStockToggle;
    internal static ConfigEntry<OpenXRNative.DepthSubmissionMode> DepthSubmission;
    internal static ConfigEntry<bool> PositionalTracking;
    internal static ConfigEntry<bool> UseGamePitch;
    internal static ConfigEntry<bool> FixUpsideDown;
    internal static ConfigEntry<float> MenuDistance;
    internal static ConfigEntry<float> MenuWidth, TabScreenScale;
    internal static ConfigEntry<bool> LaptopPanel, MissionScreen, MissionScreenOnWindow;
    internal static ConfigEntry<bool> MenusOnTop;
    internal static ConfigEntry<float> LaptopPanelDistance, MissionScreenMaxWidth, MaxAimUpDown, HideTorsoPitch;
    internal static ConfigEntry<float> LaptopPanelDegrees;

    public override void Load()
    {
        Logger = Log;
        Logger.LogInfo($"{Name} {Version} loading");

        // The game's shaders lack single-pass instanced variants (only the left eye draws), so default to multi-pass
        StereoRenderMode = Config.Bind("Rendering", "StereoRenderMode", OpenXRNative.RenderMode.MultiPass,
            "MultiPass renders each eye separately and works with the game's shaders. " +
            "SinglePassInstanced is faster but only draws the left eye in this game. Takes effect the next time VR starts (F4).");

        RenderScale = Config.Bind("Rendering", "RenderScale", 0.8f,
            "Size of each eye's image compared with what the headset asks for (0.3 to 1.5). Lower = faster but blurrier. " +
            "Takes effect the next time VR starts (F4).");
        AntiAliasing = Config.Bind("Rendering", "AntiAliasing", true,
            "If the game's anti-aliasing is off, use SMAA while VR runs (edges shimmer much more in VR).");

        VRPerformance = Config.Bind("Rendering", "VRPerformance", true,
            "While VR runs, turn off effects that are drawn once per eye and cost a lot: volumetric clouds, " +
            "screen-space reflections, contact shadows, screen-space GI and the volumetric part of fog (distance fog stays).");

        ViewDistance = Config.Bind("Rendering", "ViewDistance", 500f,
            "While VR runs, nothing further than this (metres) is drawn. The game uses 20000; every object is prepared " +
            "once per eye, so a dense forest out to 20 km held the woodland at ~38 fps. 0 = leave the game's.");
        LodScale = Config.Bind("Rendering", "LodScale", 0.85f,
            "While VR runs, the game's level-of-detail bias is multiplied by this: below 1, distant objects switch to " +
            "simpler models sooner. 1 = leave it.");
        ShadowDistance = Config.Bind("Rendering", "ShadowDistance", 60f,
            "While VR runs, shadows are drawn only this far (metres); shadows are drawn once per eye. 0 = leave the game's.");
        // 2026-11-04 frame-rate pass ("Should we not get rid of the HDRP?" - it can't be removed, so the costly parts
        // that were still on go, each behind its own switch to compare fps in the log)
        SunShadowCascades = Config.Bind("Rendering", "SunShadowCascades", 2,
            "While VR runs, the sun's shadows are drawn in this many layers (1-4; the game uses up to 4). Fewer is faster; " +
            "shadows past a few metres get a little softer. 0 = leave the game's.");
        PlainLensEffects = Config.Bind("Rendering", "PlainLensEffects", true,
            "While VR runs, the camera-lens effects are off: chromatic aberration, film grain, vignette, lens distortion, " +
            "panini projection. Each is a full-screen pass per eye, and they look wrong in a headset anyway.");
        ReflectionProbesOnce = Config.Bind("Rendering", "ReflectionProbesOnce", true,
            "While VR runs, reflection probes the game re-captures every frame (6 extra views each) are captured once " +
            "when they switch on instead.");

        Shadows = Config.Bind("Rendering", "Shadows", false,
            "Draw shadows in VR. Off by default: shadows are drawn once per eye and looked unsteady in the headset. " +
            "When on, Rendering.ShadowDistance still limits how far they reach.");

        HideBody = Config.Bind("Rendering", "HideBody", true,
            "In VR, hide your own body and gear (helmet, vest, backpack, legs) and keep only the arms, hands, the weapon " +
            "in your hands, the wrist GPS and the tablet.");
        HideLeftArm = Config.Bind("Rendering", "HideLeftArm", false,
            "In VR, hide the left upper arm too and keep only the left forearm, wrist GPS and glove/hand.");
        HideChest = Config.Bind("Rendering", "HideChest", true,
            "In VR, hide the shirt's chest and back but keep its sleeves (your upper arms): the body is shrunk away just " +
            "for drawing. Turn off if the shoulders look stretched or odd.");
        SunGlare = Config.Bind("Rendering", "SunGlare", false,
            "Keep bloom, lens flares and the sun's halo in the sky in VR. Off by default: looking towards the sun was " +
            "dazzling in the headset. The sunlight itself stays.");

        TerrainGrass = Config.Bind("Rendering", "TerrainGrass", false,
            "Experimental, leave off: outdoors in VR, keep the game's grass and small ground details (only the ground " +
            "surface is replaced by the stand-in). Tried 2026-11-01: the right eye stayed on the loading screen outdoors.");
        GroundLod = Config.Bind("Rendering", "GroundLod", true,
            "Outdoors in VR, draw the far parts of the stand-in ground with less detail (8 m instead of 2 m between points).");
        SunShadows = Config.Bind("Rendering", "SunShadows", true,
            "With Rendering.Shadows off, still let the sun cast shadows. Without them it lights the inside of houses " +
            "through roofs and walls, and day maps look overexposed indoors. Rendering.ShadowDistance limits their reach.");
        TreeShadows = Config.Bind("Rendering", "TreeShadows", false,
            "In VR, let trees and grass cast shadows. Off: they cast none, while buildings and everything else still do " +
            "(the sun keeps its shadows, so nothing goes dark).");
        SunShadowsOutdoors = Config.Bind("Rendering", "SunShadowsOutdoors", true,
            "With Rendering.SunShadows: keep the sun's shadows in the open too. Off = the sun casts shadows only while " +
            "you're under a roof (so houses aren't lit through it), and in the open there are no tree shadows. " +
            "On by default: with it off, Georgia looked too dark in the open (2026-11-03).");
        AmbientOcclusion = Config.Bind("Rendering", "AmbientOcclusion", true,
            "In VR, soft shading in corners, under things and where walls meet the floor (HDRP's ambient occlusion, " +
            "drawn for both eyes). Makes rooms look more solid; costs a little GPU time.");
        AmbientOcclusionStrength = Config.Bind("Rendering", "AmbientOcclusionStrength", 0.8f,
            "How dark that shading is (0 to 4; 1 is HDRP's usual).");
        GroundShadows = Config.Bind("Rendering", "GroundShadows", false,
            "Let the stand-in ground cast shadows (it always receives them). Off saves drawing it again for shadows.");
        GroundTessellation = Config.Bind("Rendering", "GroundTessellation", false,
            "Keep the terrain material's tessellation (extra small bumps made on the graphics card) on the stand-in ground.");
        InteractReach = Config.Bind("Controls", "InteractReach", 1.8f,
            "Shortest reach (metres) for using things (doors, laptops). The game's own is 1.2 m, which in VR fell short " +
            "of the outdoor laptop on its table. 0 keeps the game's.");
        InteractPastGun = Config.Bind("Controls", "InteractPastGun", true,
            "The aim for using things passes through your own held gun, which in VR is often held out across it.");
        GroundUsesTerrainMaterial = Config.Bind("Rendering", "GroundUsesTerrainMaterial", true,
            "Draw the stand-in ground (used instead of Unity Terrain in VR) with the terrain's own material, which blends " +
            "paths and grass smoothly. Turn off if the ground shows pink, black or invisible: it then uses one plain " +
            "texture per patch instead.");

        FoliageOcclusionCulling = Config.Bind("Rendering", "FoliageOcclusionCulling", false,
            "Leave the tree/grass renderer's GPU occlusion culling on in VR. Off by default: it hides objects using the " +
            "previous frame, which in VR is the other eye, so trees and grass popped in and out.");

        DepthSubmission = Config.Bind("Rendering", "DepthSubmission", OpenXRNative.DepthSubmissionMode.None,
            "Whether each eye's depth image is sent to the VR runtime as well. None by default: in some levels the " +
            "right eye stopped updating. Takes effect the next time VR starts (F4).");

        SkipExtraCameras = Config.Bind("Rendering", "SkipExtraCameras", true,
            "While VR runs, stop other cameras (ones that render into textures, e.g. in-game screens) from rendering. " +
            "In some levels one of them left the right eye frozen and the left eye flickering.");

        Runtime = Config.Bind("VR", "Runtime", "SteamVR",
            "Which VR runtime OPERATOR uses: SteamVR (the PC's default), VirtualDesktop (Virtual Desktop's own, VDXR: " +
            "no SteamVR in between, often faster on a Quest), or the full path of a runtime's .json file. Only affects " +
            "this game.");
        PauseInMainMenu = Config.Bind("VR", "PauseInMainMenu", true,
            "When you leave a match for the main menu (which can't be clicked in VR), pause VR there so the menu is " +
            "used on the desktop, and start VR again by itself once you're back in the game.");
        AutoStart = Config.Bind("VR", "AutoStart", false,
            "Start VR by itself (as if F4 was pressed) AutoStartDelay seconds after the game starts. The Steam launch " +
            "option --vr does the same without changing this (--vr=45 also sets the delay).");
        AutoStartDelay = Config.Bind("VR", "AutoStartDelay", 30f,
            "Seconds after the game starts before VR starts by itself (with AutoStart or the --vr launch option).");

        PositionalTracking = Config.Bind("Head", "PositionalTracking", true,
            "Move the view when you lean or move your head, not just when you turn it.");
        FixUpsideDown = Config.Bind("Head", "FixUpsideDown", true,
            "If the VR runtime reports the headset upside down when VR starts or you recenter (F10), turn tracking the " +
            "right way up. Some runs SteamVR/Virtual Desktop did this (head below the floor, world upside down).");
        RecenterKey = Config.Bind("Head", "RecenterKey", "U",
            "Pressing this key (a letter or digit) recentres the view like F10: where your head is now becomes the " +
            "standing spot, facing forward. U is unused by the game; the PS5 layout sends it on L1 + Create. Empty = F10 only.");
        SteadyEyeHeight = Config.Bind("Head", "SteadyEyeHeight", true,
            "Keep your eye height steady while the game's aim (right stick / mouse) looks up or down: the game bends the " +
            "character to look down, which dropped the VR view to about 3/4 height. Crouching and going prone still lower it.");
        // Off: the user found aiming worse with it ("didn't work at all") on 2026-11-01
        LockEyeWhenAiming = Config.Bind("Head", "LockEyeWhenAiming", false,
            "While aiming down the sights, keep your eye exactly where the game lines the sights up (your head's " +
            "sideways/forward/up movement is faded out until you lower the gun; turning your head still works).");
        MaxAimUpDown = Config.Bind("Head", "MaxAimUpDown", 0f,
            "In VR, the right stick can aim at most this many degrees up or down (the game's own limit is wider). " +
            "Further down, the character bends so far forward that the view ends up inside your own body. 0 = the game's.");
        HideTorsoPitch = Config.Bind("Head", "HideTorsoPitch", 30f,
            "In VR, with the right stick aiming more than this many degrees up or down (not down the sights), hide your " +
            "character's shirt/torso: the game bends the body over while the view stays at standing height, so you saw " +
            "it twisted. The arms, hands and gun stay. 0 = never hide it.");
        UseGamePitch = Config.Bind("Head", "UseGamePitch", false,
            "If true, moving the mouse up/down also tilts the view. Off by default because it tilts the whole world in VR.");

        TouchAsGamepad = Config.Bind("Controls", "TouchAsGamepad", false,
            "Experimental, off: feed the Oculus Touch controllers to the game as a virtual gamepad. OPERATOR doesn't read " +
            "gamepads through Unity's Input System (a PS5 pad reaches it via Steam Input as keyboard/mouse), so this does " +
            "nothing useful and it stopped the menus working.");

        // Off by default: the user plays with a PS5 pad (through Steam Input); the Touch controls never felt right
        TouchControls = Config.Bind("Controls", "TouchControls", false,
            "Play with the Oculus Touch controllers: they press the keys you have bound in the game. Left stick walks, " +
            "right stick turns, buttons as in TouchMap. Off = play with a gamepad (e.g. a PS5 pad through Steam Input); " +
            "the hand aim, the menu laser and the free left hand are then off too.");
        TouchMap = Config.Bind("Controls", "TouchMap",
            "RightTrigger=Shoot, LeftTrigger=Aim, A=FlashlightsToggle,B=Crouch, X=Reload, Y=Firemode, LeftGrip=Sprint, LeftStickClick=Prone, " +
            "RightStickClick=Interact, DpadUp=SwitchBetweenSights, DpadLeft=LasersToggle, " +
            "DpadRight=SwitchGrenades, DpadDown=Recenter, LeftDpadUp=ToggleNVGs, LeftDpadLeft=NextWeapon, " +
            "LeftDpadDown=OpenATAK, LeftDpadRight=SwitchGrenades, " +
            "Menu=Escape",
            "Touch button = game action. Buttons: A, B, X, Y, RightTrigger, LeftTrigger, RightGrip, LeftGrip, " +
            "RightStickClick, LeftStickClick, LeftThumbrest, RightThumbrest, Menu, the virtual D-pad DpadUp, DpadDown, " +
            "DpadLeft, DpadRight (right stick while TouchDpadButton is held; DpadUp = sight switch: flips a magnifier " +
            "up, the next push flips it down) and the left one LeftDpadUp/Down/Left/Right (left stick while " +
            "TouchLeftDpadButton is held). Actions: any of the game's (WalkForward, Sprint, Crouch, Prone, Kick, " +
            "HealthMenu, Interact, Aim, Shoot, Reload, Firemode, Vault, LeanLeft, LeanRight, FreeLean, SwitchPrimary, " +
            "SwitchSecondary, SwitchGrenades, ToggleNVGs, OpenATAK, CheckMag, Inspect, ...), Escape, Recenter (the " +
            "view, like F10), NextWeapon (the next weapon you carry, as NextWeaponKey) or Run (click to start " +
            "running, click again to walk; also stops when you stop moving forward, as RunKey) or ChamberRound (unjam / " +
            "chamber a round, Left Shift + T in order, as ManualReloadKey).");
        // Y double tap = the game's menu: the left menu button doesn't reach the game under SteamVR/Virtual Desktop
        TouchDoubleTap = Config.Bind("Controls", "TouchDoubleTap",
            "X=LeftShift+T, Y=Hold:Tab, LeftDpadRight=LeftShift+T, B=Hold:Space",
            "Touch button = keys pressed together when you double-tap it (instead of its normal action on the second tap). " +
            "Keys use Unity names joined with +, e.g. LeftShift+T, LeftControl+R, Mouse2. Hold: in front (B=Hold:Space, " +
            "vault) keeps them down while the button stays held (at least 0.6 s). Separate entries with commas.");
        TouchHold = Config.Bind("Controls", "TouchHold", "B=Space",
            "Touch button = keys tapped when you hold it down (instead of holding its own action), e.g. B=Space: tap B " +
            "crouches, hold B kicks. Keys as in TouchDoubleTap. Separate entries with commas.");
        TouchDpadButton = Config.Bind("Controls", "TouchDpadButton", "LeftThumbrest",
            "Hold this Touch button to make the right stick a virtual D-pad (DpadUp/Down/Left/Right in TouchMap); the " +
            "right stick doesn't turn you while it's held. LeftThumbrest = left thumb resting on its thumbrest; " +
            "LeftGrip also works. Don't also map it in TouchMap. Empty = no virtual D-pad.");
        TouchLeftDpadButton = Config.Bind("Controls", "TouchLeftDpadButton", "RightThumbrest",
            "Hold this Touch button to make the left stick a second virtual D-pad (LeftDpadUp/Down/Left/Right in " +
            "TouchMap); you don't walk while it's held. RightThumbrest = right thumb resting on its thumbrest. " +
            "Empty = no left virtual D-pad.");
        TouchTurnSpeed = Config.Bind("Controls", "TouchTurnSpeed", 900f,
            "How fast the right stick turns you (mouse pixels per second at full tilt).");
        TouchLookUpDown = Config.Bind("Controls", "TouchLookUpDown", 0f,
            "How much the right stick's up/down moves the view up/down (0 = not at all; in VR you look up and down with your head).");

        HandAimPitch = Config.Bind("Controls", "HandAimPitch", true,
            "Aim up and down by pointing the right controller: the game's aim (which the gun follows) is steered to " +
            "where your hand points. The view still follows your head. Left/right stays on the right stick.");
        HandAimYaw = Config.Bind("Controls", "HandAimYaw", true,
            "Aim left and right by pointing the right controller too. The view then turns only with the right stick " +
            "(TouchTurnDegrees) and your head, not with the gun; walking follows the gun's direction.");
        InteractWithHead = Config.Bind("Controls", "InteractWithHead", false,
            "Old way, off: swap the camera to your head only inside the game's interaction checks. It made the shoot " +
            "house switch's prompt flash and not take the press; HoldToInteract replaces it.");
        HoldToInteract = Config.Bind("Controls", "HoldToInteract", true,
            "Hold the Interact button (A): the gun's aim swings to where you look, Interact is pressed once it's there, " +
            "and while you keep holding, the right stick drags things like the range target handle. Let go to aim with " +
            "your hand again.");
        TouchTurnDegrees = Config.Bind("Controls", "TouchTurnDegrees", 90f,
            "With HandAimYaw on: how fast the right stick turns you, in degrees per second at full tilt.");
        HandAimGain = Config.Bind("Controls", "HandAimGain", 4f,
            "How hard the up/down hand aim pulls (mouse pixels per degree of difference each frame). Too high = the aim " +
            "wobbles, too low = it lags. Negative if the aim moves the wrong way.");
        HandAimPitchOffset = Config.Bind("Controls", "HandAimPitchOffset", 0f,
            "Degrees added to the hand's up/down angle, if the gun ends up aiming a bit higher or lower than you point.");
        VirtualStock = Config.Bind("Controls", "VirtualStock", true,
            "With the Touch controllers: while VirtualStockButton is held, the right controller is a stock locked in " +
            "place against your head, and moving the left controller left/right/up/down aims the gun (along the line " +
            "from the stock through the left hand). Let go to aim with the right controller again.");
        VirtualStockButton = Config.Bind("Controls", "VirtualStockButton", "RightGrip",
            "The Touch button held for the virtual stock (A, B, X, Y, RightGrip, LeftGrip, ...). Don't also map it in TouchMap.");
        LeftHandMovement = Config.Bind("Controls", "LeftHandMovement", 0.6f,
            "How much the character's free left hand moves compared with your real left controller (0.1 to 1; 1 = the " +
            "same). Lower = calmer, smaller movements around the gun's grip.");
        LeftHandSmoothing = Config.Bind("Controls", "LeftHandSmoothing", 6f,
            "How quickly the character's free left hand catches up with your controller (per second; 0.5 to 20). Lower = " +
            "slower and smoother, higher = snappier.");
        AutoSwitchControls = Config.Bind("Controls", "AutoSwitchControls", true,
            "Switch between the Touch controllers and a gamepad by themselves: pressing anything on the PS5 pad makes the " +
            "Touch controllers step aside; pressing A, B, X, Y or a trigger on a Touch controller hands control back. " +
            "(Both thumbsticks held in for 2 s, or F8, switch by hand either way.)");
        AutoAds = Config.Bind("Controls", "AutoAds", false,
            "With the Touch controllers: raise the gun to your face (right controller near your eyes, pointing where you " +
            "look) and the game aims down the sights, as if the Aim button were held; lower it and it stops. The Aim " +
            "button still works too.");
        AutoAdsDistance = Config.Bind("Controls", "AutoAdsDistance", 0.4f,
            "Auto-ADS: how close the right controller has to come to your eyes, in metres. Bigger = aims sooner.");
        AutoAdsAngle = Config.Bind("Controls", "AutoAdsAngle", 35f,
            "Auto-ADS: how far (degrees) the controller may point away from where you look and still aim.");
        HandAimNoSway = Config.Bind("Controls", "HandAimNoSway", false,
            "With the Touch controllers: switch off the game's weapon sway (the gun swinging on springs as the view moves, " +
            "and bobbing as you walk) while you aim with your hand. Off by default: the same script also brings the gun " +
            "in towards the eye, so turning it off left the gun sitting too far away.");
        HandAimSmoothing = Config.Bind("Controls", "HandAimSmoothing", true,
            "With the Touch controllers: smooth the hand aim, so the small shake of a hand held out doesn't make the gun " +
            "float about, while a deliberate swing still follows straight away.");
        HandAimMinCutoff = Config.Bind("Controls", "HandAimMinCutoff", 1.2f,
            "How hard the hand aim is smoothed when your hand is nearly still: lower = steadier but a touch slower to " +
            "settle (0.5 to 3; 1.2 by default).");
        HandAimBeta = Config.Bind("Controls", "HandAimBeta", 0.05f,
            "How quickly the smoothing lets go as your hand moves faster: higher = less lag on quick swings, but a bit " +
            "more shake comes through (0 to 0.3; 0.05 by default).");
        Haptics = Config.Bind("Controls", "Haptics", true,
            "With the Touch controllers: the controllers vibrate when you fire, and give short buzzes as feedback (the " +
            "virtual stock on or off, a double tap recognised, Interact sent).");
        HapticsShot = Config.Bind("Controls", "HapticsShot", 0.7f,
            "How strong the kick in the right controller is for each shot (0 = none, 1 = strongest).");
        HapticsBothHands = Config.Bind("Controls", "HapticsBothHands", true,
            "The left controller feels each shot too (a little lighter than the right).");
        HeadLean = Config.Bind("Controls", "HeadLean", false,
            "With the Touch controllers: tilt your head to the side past HeadLeanAngle to lean that way (the game's Lean " +
            "Left / Lean Right); straighten your head to stand up straight again.");
        MagLabelHeight = Config.Bind("Controls", "MagLabelHeight", 0.04f,
            "When checking a magazine, how far above the magazine (metres) the round count and turning magazine show.");
        HeadLeanAngle = Config.Bind("Controls", "HeadLeanAngle", 8f,
            "How far (degrees) you tilt your head sideways to lean. You straighten up below about half of this.");
        LeanSpeed = Config.Bind("Controls", "LeanSpeed", 2.5f,
            "How fast you lean with a weapon in hand, times the game's own speed (1 = the game's; 0.25 to 5).");
        VirtualStockToggle = Config.Bind("Controls", "VirtualStockToggle", true,
            "On: press VirtualStockButton once to turn the virtual stock on, press it again to turn it off. Off: the " +
            "stock is on only while the button is held.");
        FreeLeftHand = Config.Bind("Controls", "FreeLeftHand", true,
            "In VR the character's left hand follows the left controller when you take it off the gun, and grips the gun " +
            "again when you bring it back to the front of the gun. F3 switches it off/on while playing.");
        // 0 = where the game holds it: the user asked for the right arm back as it was ("aiming terrible" with a shift)
        // Off: the user didn't want the gun following their head while aiming
        HeadAimPitch = Config.Bind("Controls", "HeadAimPitch", false,
            "With a gamepad (TouchControls off): while aiming down the sights, the gun's up/down aim follows your head, " +
            "so the reticle is where you look. Off = the right stick aims up/down all the time.");
        // Off by default: the game's own double-tap of R (double-tap Square on the pad) is the emergency reload. Steam's
        // double-press on Square (for the manual reload) missed taps held ~0.2 s and sent R twice, dropping the magazine
        EmergencyReloadKey = Config.Bind("Controls", "EmergencyReloadKey", "",
            "Pressing this key (a letter or digit) does the game's emergency reload (R twice quickly). Bind a gamepad " +
            "button to it in Steam Input. Empty = off.");
        RunKey = Config.Bind("Controls", "RunKey", "O",
            "Pressing this key (a letter or digit) starts running (Left Shift held), console style: it stops by itself " +
            "when you stop moving forward, or on another press. O is unused by the game; the PS5 layout sends it on a " +
            "left-stick click. Empty = off.");
        // 1 for a fresh install (a gameplay change, unfair online); the user's own settings file keeps their 1.3
        RunSpeed = Config.Bind("Controls", "RunSpeed", 1f,
            "Jogging and sprinting speed times this (1 = the game's own speed; 0.5 to 3). Walking isn't changed.");
        StatusHud = Config.Bind("Menus", "StatusHud", true,
            "While playing, show a small display at the lower left of the view: weapon and fire mode, health, and " +
            "warnings for bleeding and broken limbs.");
        Compass = Config.Bind("Menus", "Compass", true,
            "While playing, show a compass strip along the top of the view.");
        HudDistance = Config.Bind("Menus", "HudDistance", 0.8f,
            "How far from the eye (metres) the status display and compass sit; they keep their place in view.");
        HudSize = Config.Bind("Menus", "HudSize", 0.8f,
            "How big the status display and compass look (1 = the first version's size).");
        WeaponPicture = Config.Bind("Menus", "WeaponPicture", true,
            "In the status display, show a small copy of the weapon in your hand, side on, to the left of its name.");
        WeaponPictureBrightness = Config.Bind("Menus", "WeaponPictureBrightness", 1.3f,
            "How much brighter the weapon picture's colours are than the real gun's (1 = the same, 1.3 = 30% brighter; " +
            "0.5 to 3). Gun finishes are dark, so the small copy was hard to make out.");
        TalkIndicator = Config.Bind("Menus", "TalkIndicator", true,
            "Show a red 'TALKING' in the status display while push-to-talk is held.");
        RecenterOnLoad = Config.Bind("Head", "RecenterOnLoad", true,
            "Recenter the view by itself a few seconds after each level loads.");
        OnlineViewDistance = Config.Bind("Rendering", "OnlineViewDistance", 150f,
            "Draw distance (metres) while other human players are in the game, where the processor is the limit. " +
            "0 keeps Rendering.ViewDistance.");
        OnlineLodScale = Config.Bind("Rendering", "OnlineLodScale", 0.6f,
            "While other human players are in the game, multiply the level-of-detail bias by this too (lower = far " +
            "objects switch to simpler versions sooner). 1 = same as offline.");
        VoiceMic = Config.Bind("Controls", "VoiceMic", "Virtual Desktop",
            "The game's voice chat uses the microphone whose name contains this (the Quest's microphone through Virtual " +
            "Desktop by default). Empty leaves the game's own choice.");
        GrenadeCount = Config.Bind("Controls", "GrenadeCount", true,
            "While you hold a grenade, show small copies of it above your hand, one per grenade of that kind you have " +
            "left, with the number.");
        StickClickLean = Config.Bind("Controls", "StickClickLean", true,
            "While aiming down the sights, the left-stick click leans left and the right-stick click leans right (again to " +
            "straighten up; stopping aiming straightens up too). Not aiming, the right-stick click is Use. Needs the pad " +
            "layout's left-stick click to send 9 and the right-stick click 7.");
        LeanHoldTime = Config.Bind("Controls", "LeanHoldTime", 0.3f,
            "Seconds a stick click must be held while aiming before you lean, so a stick pushed in by accident doesn't " +
            "lean you (0 = lean on the click at once). Straightening up is still a plain click.");
        MagModel = Config.Bind("Controls", "MagModel", true,
            "When checking a magazine, show a turning copy of the magazine itself beside the count (off: the drawing " +
            "with a line per round).");
        ExactMagCount = Config.Bind("Controls", "ExactMagCount", true,
            "When you check a magazine, show the exact number of rounds left (and whether one is chambered) under the " +
            "game's own message.");
        NextWeaponKey = Config.Bind("Controls", "NextWeaponKey", "L",
            "Pressing this key (a letter or digit) switches to the next weapon you carry (primary, second primary, " +
            "sidearm, special purpose; empty slots skipped). L is unused by the game; the PS5 layout sends it on a " +
            "Triangle tap. Empty = off.");
        TabletKey = Config.Bind("Controls", "TabletKey", "J",
            "While this key (a letter or digit) is held, the tablet is out (the game's OpenATAK, M). J is unused by the " +
            "game; the PS5 layout sends it on L1 + D-pad down. Empty = off.");
        // V: unused by the game; the user's pad sends it on L1 + Square
        ManualReloadKey = Config.Bind("Controls", "ManualReloadKey", "V",
            "Pressing this key (a letter or digit) does the game's manual reload / chamber a round (Left Shift + T, " +
            "pressed in order). Bind a gamepad button to it in Steam Input. Empty = off.");
        GunShiftRight = Config.Bind("Controls", "GunShiftRight", 0f,
            "In VR, move the gun (with your hands on it) this far to the right of where the game holds it, in metres " +
            "(negative = left). Ctrl + Left/Right changes it by 1 cm while playing.");
        GunShiftUp = Config.Bind("Controls", "GunShiftUp", 0f,
            "In VR, move the gun this far up (negative = down), in metres. Ctrl + Up/Down changes it by 1 cm while playing.");
        // Centre by default: moving the gun to one eye put red dots ~3 cm off the game's aim line too
        AimEye = Config.Bind("Controls", "AimEye", "Centre",
            "When aiming down the sights in VR, line them up with this eye (Right, Left, or Centre = between the eyes, " +
            "as the game does). Use your dominant eye.");
        GunShiftForward = Config.Bind("Controls", "GunShiftForward", 0f,
            "In VR, move the gun this far forward (negative = back towards you), in metres.");
        // Off: in Georgia (2026-11-03) the gun drifted off the game's aim line, "kept shooting to my right" and the
        // sights wouldn't come up properly
        SteadyGun = Config.Bind("Controls", "SteadyGun", false,
            "In VR, keep the gun steady in your view: the game's bob, sway and quick jumps are taken out, and slow " +
            "changes (crouching, a weapon change) come through over about a second. Not while aiming down the sights.");
        GunFollowsHand = Config.Bind("Controls", "GunFollowsHand", false,
            "Experimental, off: in VR move the gun to where your right hand really is. The game's hand rig hangs off " +
            "the camera, so only the gun moved and the hands and aim went wrong. F2 switches it on/off while playing.");

        MenuDistance = Config.Bind("Menus", "Distance", 1.5f,
            "How far in front of you (metres) the floating menus appear.");
        MenuWidth = Config.Bind("Menus", "Width", 1.8f,
            "How wide (metres) the floating menus are.");
        TabScreenScale = Config.Bind("Menus", "TabScreenScale", 1.4f,
            "The in-game Tab menu (health page and map) is this many times wider than other menus while it's open " +
            "(1 = same size; 0.5 to 3).");
        MenusOnTop = Config.Bind("Menus", "OnTop", true,
            "Draw the floating menus, HUD, prompts, pointer and laser on top of everything in VR, like in flat mode, " +
            "so your own arms and gun can't hide them.");
        CustomisationScreen = Config.Bind("Menus", "CustomisationScreen", true,
            "On the character customisation page in VR, show the game's flat view of your character behind the panels, " +
            "so the floating page looks like the flat screen.");
        SunShadowFrames = Config.Bind("Rendering", "SunShadowFrames", 0,
            "Experimental, leave at 0: the sun's shadows drawn once and shared by both eyes, redrawn every this many " +
            "frames. Tried at 2 (2026-11-05): the gun, hands and nearby ground flickered and the GPU saved nothing. " +
            "0 = the game's way.");
        CheapScopes = Config.Bind("Rendering", "CheapScopes", true,
            "In VR a scope's picture skips the effects you can't see in a small lens (the same ones the eyes skip, plus " +
            "ambient occlusion and decals), so aiming costs less.");
        Character3D = Config.Bind("Menus", "Character3D", true,
            "On the customisation page in VR, show your character on the left as a 3D model (a copy in his current " +
            "clothes and pose, turned as the game shows him) instead of a flat picture. The right side stays flat.");
        Character3DCloser = Config.Bind("Menus", "Character3DCloser", -0.1f,
            "How far (metres) the 3D character is brought towards you from the page. Bigger = closer (and so bigger); " +
            "below 0 = further back behind the page's front.");
        Diagnostics = Config.Bind("General", "Diagnostics", false,
            "Extra test tools used while building the mod (menu/laptop/interaction logging, the unused virtual gamepad). " +
            "Off for speed. Takes effect at the next game start.");
        ScopeGuard = Config.Bind("Rendering", "ScopeGuard", true,
            "In VR, while you aim, keep the sight working: if the game aims through a hidden sight it switches sights, " +
            "and if the scope you're looking through has its camera off it switches it on (a black scope picture).");
        SharpScopes = Config.Bind("Rendering", "SharpScopes", true,
            "In VR, keep magnified scopes and magnifiers drawing at full resolution with their blur overlay off (they " +
            "were blurry).");
        AllowDLSS = Config.Bind("Rendering", "AllowDLSS", false,
            "Experimental: let NVIDIA DLSS run in VR (draws at DlssPercent of the resolution and upscales). Off = DLSS " +
            "and dynamic resolution are switched off in VR, as they were suspected of stale pictures early on.");
        DlssPercent = Config.Bind("Rendering", "DlssPercent", 67f,
            "With AllowDLSS: the resolution DLSS draws at before upscaling, in percent (33 to 100).");
        FoveatedRendering = Config.Bind("Rendering", "FoveatedRendering", 0.5f,
            "Fixed foveated rendering in VR (NVIDIA cards, through the driver's variable rate shading): full detail in the " +
            "middle of each lens, coarser towards the edges, to save graphics card time. 0 = off, 0.5 = medium, " +
            "1 = strongest (smallest sharp area). Takes effect on the next F8.");
        NightVisionFill = Config.Bind("Rendering", "NightVisionFill", true,
            "In VR, while your night-vision goggles are on, make their picture fill your view (the goggles follow your " +
            "head, their lenses are enlarged and the housing hidden). Off = the game's own small tubes in front of you.");
        NightVisionLensScale = Config.Bind("Rendering", "NightVisionLensScale", 6f,
            "How many times bigger the goggles' lenses are made in VR so they cover your view (1 to 30).");
        Saturation = Config.Bind("Rendering", "Saturation", 25f,
            "In VR, added to the game's colour saturation (HDRP Color Adjustments scale; the game's own is -15). " +
            "Higher = more vivid; 0 = the game's own.");
        Contrast = Config.Bind("Rendering", "Contrast", 0f,
            "In VR, added to the game's contrast (the game's own is 50). 0 = the game's own (and pain effects untouched).");
        DarkMagnifierAside = Config.Bind("Rendering", "DarkMagnifierAside", true,
            "In VR, a flip-up magnifier flipped aside doesn't draw its lens, so its magnified picture doesn't catch " +
            "your eye; it shows again when flipped up.");
        KeepMagnifierLive = Config.Bind("Rendering", "KeepMagnifierLive", true,
            "In VR, a flip-up magnifier that is up keeps a live picture even when you're not aiming down the sights " +
            "(the game stops drawing it then, and the lens showed a frozen picture). Also keeps a fixed scope (ACOG...) " +
            "on the gun in hand live while you're not aimed in.");
        ScopeAsideFrames = Config.Bind("Rendering", "ScopeAsideFrames", 0,
            "While you're not aimed in: 0 = the scope's lens is dark and nothing extra is drawn (fastest; you see " +
            "through it once you lift it to your eye); 1 or more = its picture is redrawn every this many frames " +
            "(1 = every frame, costs the most; 3 = a third of that). Aimed in, the game draws it every frame as usual.");
        FlipWithSightSwitch = Config.Bind("Controls", "FlipWithSightSwitch", true,
            "The switch-sights key (Caps Lock, D-pad up on the PS5 layout) also flips a flip-up magnifier, which has its " +
            "own key in the game.");
        PadRangeSwitch = Config.Bind("Controls", "PadRangeSwitch", true,
            "With a gamepad, at the shoot house switch: hold Interact (F, R3 on the PS5 layout) with the aim level to " +
            "send the target away, or aimed at the floor to bring it back.");
        GazeDot = Config.Bind("Menus", "GazeDot", true,
            "In VR, show a small dot where the game aims (the right stick with a gamepad, your right hand with the Touch " +
            "controllers) near things you can use: white normally, green when there's something to use there (press Interact).");
        BenchSpinSpeed = Config.Bind("Menus", "BenchSpinSpeed", 150f,
            "With the Touch controllers, on the weapon bench the right stick's left/right turns the weapon round, this " +
            "many degrees per second at full tilt (negative turns it the other way).");
        BenchDragGain = Config.Bind("Menus", "BenchDragGain", 3f,
            "With the Touch controllers, on the weapon bench: hold the click with the laser on the weapon (not the page) " +
            "and swing the controller left/right to turn the weapon, this many degrees per degree of swing (negative " +
            "turns it the other way).");
        LaserDragSpeed = Config.Bind("Menus", "LaserDragSpeed", 2.5f,
            "With the Touch controllers, how fast things you drag with the laser move (e.g. medication on the health " +
            "screen), compared with the laser itself. 1 = the mouse movement the laser makes; higher = faster.");
        MenuClickButton = Config.Bind("Menus", "MenuClickButton", "RightTrigger",
            "With the Touch controllers, the button that clicks in menus, on laptops and on the weapon bench (where the " +
            "laser points). RightTrigger, A, ...");
        BenchWeaponForward = Config.Bind("Menus", "BenchWeaponForward", 0.3f,
            "In VR, while the weapon bench page is open, the weapon is moved this many metres out from the wall towards " +
            "you so it can be turned without going through the wall (0 = where the game puts it; up to 0.6).");
        BenchAutoOpen = Config.Bind("Menus", "BenchAutoOpen", true,
            "In VR the armoury's weapon bench opens by itself when you walk up to it and face it (no Interact press). " +
            "After you leave it, step away and come back to open it again.");
        BenchAutoOpenDistance = Config.Bind("Menus", "BenchAutoOpenDistance", 1.5f,
            "How close (metres) you need to be to the weapon bench for it to open by itself.");
        BenchAutoOpenName = Config.Bind("Menus", "BenchAutoOpenName", "",
            "Which weapon table opens by itself (the end of its name as the log lists it, e.g. 'WeaponModdingTable (8)'). " +
            "Empty = the table nearest BenchAutoOpenAt; the others open with Interact.");
        BenchAutoOpenAt = Config.Bind("Menus", "BenchAutoOpenAt", "-17.5, -15.0",
            "With BenchAutoOpenName empty: the weapon table nearest this spot in the room (x, z in metres) opens by itself. " +
            "The default is the main bench in the Operation Room. Empty = the table nearest the mission laptop.");
        WeaponBenchOnTop = Config.Bind("Menus", "WeaponBenchOnTop", true,
            "At the armoury bench in VR, draw the weapon modding list on top of everything, so the table can't hide " +
            "the bottom of it.");
        PlainBackdrops = Config.Bind("Menus", "PlainBackdrops", true,
            "In VR, swap the blurred picture-of-the-scene backing of menus (the health and map screen) for a plain dark " +
            "one. The blurred picture is a flat copy of the view, which looks wrong and can feel sickening in 3D.");
        HealthArrowSize = Config.Bind("Menus", "HealthArrowSize", 0.5f,
            "Size of the yellow arrow (mouse pointer) on the health screen, compared with other menus. Smaller so it " +
            "doesn't cover the writing on an injured body part.");
        BenchGunSize = Config.Bind("Menus", "BenchGunSize", 0.75f,
            "At the armoury bench in VR, show the weapon at this size (1 = life size), shrunk about its middle, so it " +
            "doesn't cover the modding page behind it.");
        BenchStepBack = Config.Bind("Menus", "BenchStepBack", 0f,
            "At the armoury bench in VR, your view steps back this many metres while the modding page is open, so the " +
            "weapon isn't right in your face. The page stays on the wall. 0 = off (0.3 made the list too small to read, " +
            "2026-11-03).");
        MissionScreen =Config.Bind("Menus", "MissionScreen", true,
            "In VR, a big mission screen stands over each mission laptop as you walk up (operation, mission, plan, " +
            "deploy). Standing only: Interact at the laptop hands the pad to the screen instead of sitting you down " +
            "(stick, Cross, Circle; Circle on the first page hands it back). Off = sit at the laptop as the game does.");
        MissionScreenOnWindow = Config.Bind("Menus", "MissionScreenOnWindow", true,
            "Put the mission screen over the big white window on the wall near the laptop, filling it, when one is " +
            "found. Off (or none found) = it stands in the air just beyond the laptop.");
        MissionScreenMaxWidth = Config.Bind("Menus", "MissionScreenMaxWidth", 0f,
            "On the window: the mission screen covers the whole white blind; a number here (metres) caps its width. 0 = no cap.");
        LaptopPanel = Config.Bind("Menus", "LaptopPanel", true,
            "While you sit at a mission laptop in VR, its screen floats in front of you, bigger, instead of staying on the laptop.");
        LaptopPanelDistance = Config.Bind("Menus", "LaptopPanelDistance", 0.4f,
            "How far in front of you (metres) the laptop screen floats. Keep it closer than the laptop, or the laptop hides it.");
        LaptopPanelDegrees = Config.Bind("Menus", "LaptopPanelDegrees", 75f,
            "How much of your view (degrees across) the floating laptop screen takes up.");

        // MonoBehaviour that runs inside the game so we can inspect XR/HDRP state once Unity is up
        AddComponent<VRDiagnostics>();

        // Manual OpenXR start/stop (F4 / F9)
        OpenXRNative.RegisterResolver();
        AddComponent<OpenXRBootstrap>();

        // Head looks around in the world; the game still drives body movement
        AddComponent<HeadTracking>();

        // Logs menus/canvases whenever they change (diagnostics only: it searched every canvas twice a second)
        if (Diagnostics.Value)
            AddComponent<UiDiagnostics>();

        // Overlay menus -> floating panels while VR runs
        AddComponent<VRMenus>();

        // Depth of field / motion blur off while in VR
        AddComponent<VREffects>();

        // Mouse drives the floating menu / laptop screen with a visible dot
        AddComponent<VRPointer>();

        // Right-controller laser for the same menus/screens; trigger clicks
        AddComponent<VRControllers>();

        // Laptop screen floats in front of you, bigger, while you sit at it
        AddComponent<LaptopPanel>();

        // The aim can't bend the character so far that the view is inside its body
        AddComponent<PitchLimit>();

        // Our own mission screen at the laptop, driving the laptop's own state
        AddComponent<OperatorVR.MissionScreen>();

        // The customisation page shows the flat character view behind its panels
        AddComponent<CustomisationScreen>();

        // The armoury bench's modding list drawn over the table
        AddComponent<WeaponBench>();

        // With a gamepad, a dot where you look (green when there's something to use)
        AddComponent<GazeDot>();

        // Menus, pointer and laser drawn over the player's own arms and gun
        AddComponent<MenusOnTop>();

        // Only the arms, hands and weapon of your own body are shown
        AddComponent<BodyHider>();

        // The left hand follows the left controller off the gun and grips it again when brought back
        AddComponent<LeftHand>();

        // The gun goes where the right controller is
        AddComponent<RightHand>();

        // The gun, with the hands on it, a little to the right (or wherever Controls.GunShift* says)
        AddComponent<GunShift>();

        // Touch sticks and buttons press the game's bound keys (TouchGamepad, a virtual gamepad, is ignored by the
        // game, so it's only added with diagnostics on)
        if (Diagnostics.Value)
            AddComponent<TouchGamepad>();
        AddComponent<TouchKeys>();

        // Controller vibration: each shot, and short buzzes as feedback
        AddComponent<Haptics>();

        // Touch controllers <-> PS5 pad while playing: both thumbsticks held 2 s, or F8
        AddComponent<ControlSwitch>();

        // With a gamepad, the game's up/down aim follows the head
        AddComponent<HeadAim>();

        // Gamepad helpers: a key for the emergency reload (R twice)
        AddComponent<PadMacros>();

        // With a gamepad, the shoot house's target switch by aiming level or down while holding Interact
        AddComponent<PadRange>();
        AddComponent<OperatorVR.RunSpeed>();

        // Magnified scopes and magnifiers kept sharp in VR
        AddComponent<ScopeSharp>();
        AddComponent<MagnifierLive>();
        AddComponent<OperatorVR.ScopeGuard>();
        AddComponent<MagLabel>();
        AddComponent<GrenadeLabel>();
        AddComponent<AimLean>();
        AddComponent<VoiceMic>();
        AddComponent<StatusHud>();
        AddComponent<ColourBoost>();
        AddComponent<NightVision>();

        // Temporary: logs what laptop clicks hit
        if (Diagnostics.Value)
            AddComponent<LaptopDiagnostics>();

        // Other cameras rendering into textures broke the right eye in levels
        AddComponent<ExtraCameras>();

        // Unity can't draw Terrain in multi-pass XR, which lost the right eye in levels; hide it (F6 cycles modes)
        AddComponent<VRTerrain>();

        // Tree/grass renderer's occlusion culling uses the other eye's depth in VR; F6 toggles its crossfade
        AddComponent<VRFoliage>();

        // Temporary: saves each eye's rendered images to PNG (F3, and 20 s into each scene)
        AddComponent<EyeCapture>();
        var harmony = new HarmonyLib.Harmony(Guid);
        LaptopClickLog.Apply(harmony);

        // Temporary: logs the XR passes HDRP builds per eye (right eye stuck in some levels)
        XRPassLog.Apply(harmony);
        ExtraCameras.Apply(harmony);

        // Foveated rendering through NVIDIA's variable rate shading, per eye
        NvFoveation.Apply(harmony);

        // Interactables are found from where you look, not where the hand-aimed gun points
        GazeInteraction.Apply(harmony);

        // The left arm is bent after the game's own hand IK
        LeftHand.Apply(harmony);
        RightHand.Apply(harmony);
        GunShift.Apply(harmony);

        // Temporary: what the game's interaction scan hits (the laptop stopped offering its prompt)
        InteractScanLog.Apply(harmony);
        PadMacros.Apply(harmony);
        MagCount.Apply(harmony);
        WeaponBench.Apply(harmony);
        OperatorVR.CustomisationScreen.Apply(harmony);
        OperatorVR.MissionScreen.Apply(harmony);
        VRFoliage.Apply(harmony);
        VRPointer.ApplyPointerOverPatch(harmony);

    }
}
