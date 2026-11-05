using System;
using System.Threading;

namespace OperatorVR;

/// <summary>
/// The hook for the optional voice add-on (OperatorVR.Voice, a separate plugin kept out of the public release,
/// 2026-11-04: the user's own recordings and the speech commands are for a friend, not the upload). The main mod
/// has no speech or sound of its own: it raises these events when things happen, and takes commands the add-on
/// hears. With no add-on installed nothing listens and no command ever arrives.
/// </summary>
public static class VoiceBridge
{
    // ---- Events the add-on can answer (e.g. with a radio callout); raised on the game's thread ----

    /// <summary>The virtual stock switched on (or was asked for again while on).</summary>
    public static event Action StockActivated;

    /// <summary>A grenade came into your hand: "Frag", "Flashbang", "9-Bang" (or the game's own name).</summary>
    public static event Action<string> GrenadeInHand;

    /// <summary>The gun's laser went on (true) or off (false), or was asked for again.</summary>
    public static event Action<bool> LaserChanged;

    /// <summary>The magnifier was flipped up (true) or down (false) by a command.</summary>
    public static event Action<bool> MagnifierChanged;

    // ---- Commands the add-on sends (from any thread) ----

    private static int s_stock, s_laser, s_magnifier;
    private static string s_grenade;

    /// <summary>Virtual stock on (true) or off (false).</summary>
    public static void CommandStock(bool on)
    {
        // The virtual stock is a Touch thing: on the pad it's ignored rather than kept for when Touch comes back
        if (Plugin.TouchControls.Value)
            Interlocked.Exchange(ref s_stock, on ? 1 : 2);
    }

    /// <summary>Laser on (true) or off (false).</summary>
    public static void CommandLaser(bool on) => Interlocked.Exchange(ref s_laser, on ? 1 : 2);

    /// <summary>Magnifier up (true) or down (false).</summary>
    public static void CommandMagnifier(bool up) => Interlocked.Exchange(ref s_magnifier, up ? 1 : 2);

    /// <summary>Bring up this grenade: "Frag", "Flashbang" or "9-Bang".</summary>
    public static void CommandGrenade(string kind) => Interlocked.Exchange(ref s_grenade, kind);

    /// <summary>True while VR runs. It used to need the Touch controllers too, so the commands went silent whenever
    /// the mod switched to the PS5 pad ("my voice commands seem to disappear", 2026-11-05: a whole session in pad
    /// mode, never listening). Grenade, laser and magnifier commands work on either controller.</summary>
    public static bool Listening => OpenXRBootstrap.IsRunning;

    // ---- Used inside the main mod ----

    internal static int TakeStockCommand() => Interlocked.Exchange(ref s_stock, 0);
    internal static int TakeLaserCommand() => Interlocked.Exchange(ref s_laser, 0);
    internal static int TakeMagnifierCommand() => Interlocked.Exchange(ref s_magnifier, 0);
    internal static string TakeGrenadeCommand() => Interlocked.Exchange(ref s_grenade, null);

    internal static void Stock() => Raise(() => StockActivated?.Invoke());
    internal static void Grenade(string kind) => Raise(() => GrenadeInHand?.Invoke(kind));
    internal static void Laser(bool on) => Raise(() => LaserChanged?.Invoke(on));
    internal static void Magnifier(bool up) => Raise(() => MagnifierChanged?.Invoke(up));

    private static void Raise(Action a)
    {
        try { a(); }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Voice] add-on handler failed: {e.GetType().Name}: {e.Message}"); }
    }
}
