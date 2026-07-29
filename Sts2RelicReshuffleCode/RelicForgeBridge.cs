using System;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicReshuffle;

/// <summary>
/// Optional integration with the sibling Sts2RelicForge mod, entirely by reflection so this mod needs no
/// build reference on it and degrades to a clean no-op when RelicForge isn't installed.
///
/// Two questions, both about relics we must NOT re-roll:
///   · <see cref="IsCompanion"/> — RelicForge's companion prefixes graft a HIDDEN donor relic into
///     player.Relics so its native hooks fire. The player does not own it: no inventory icon, never
///     serialized, re-derived from its host's forge record on load. Rolling one away removes a relic
///     that reappears on the next load while the replacement is kept — a duplication exploit.
///     RelicForge excludes companions from every one of its own player.Relics scans; so must we.
///   · <see cref="IsForged"/> — a forge record (prefix / curse / reforge count) lives on the relic
///     INSTANCE, not on its id. Re-rolling a forged relic silently destroys something the player spent
///     gold on, so by default forged relics are pinned (<see cref="ReshuffleConfig.KeepForged"/>).
///
/// Binds to RelicForge's public facade <c>Sts2RelicForge.RelicForgeApi</c>. A missing method or an
/// absent RelicForge disables that check — it never throws.
/// </summary>
internal static class RelicForgeBridge
{
    private static bool _probed;
    private static bool _present;
    private static MethodInfo? _isCompanion, _getDescriptor;

    private static void Probe()
    {
        if (_probed) return;
        _probed = true;
        try
        {
            Type? t = null;
            Assembly? forgeAsm = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType("Sts2RelicForge.RelicForgeApi");
                if (t != null) { forgeAsm = asm; break; }
            }
            if (t == null) return;   // RelicForge not installed — both checks no-op
            _present = true;

            var pub = BindingFlags.Public | BindingFlags.Static;
            var relic = new[] { typeof(RelicModel) };
            _isCompanion   = t.GetMethod("IsCompanion", pub, null, relic, null);
            _getDescriptor = t.GetMethod("GetDescriptor", pub, null, relic, null);

            // RelicForge before v1.0.19 has no IsCompanion on the public facade, and an un-updated
            // install is the COMMON case (the mods ship separately on the Workshop). Failing open there
            // would leave the companion duplication live, so fall back to the internal service — same
            // method, same semantics, the one RelicForge itself calls everywhere.
            _isCompanion ??= forgeAsm?.GetType("Sts2RelicForge.RelicForgeService")
                ?.GetMethod("IsCompanion", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                            null, relic, null);
        }
        catch { /* reflection into a sibling mod must never break combat entry */ }
    }

    /// <summary>True if the Sts2RelicForge public API was found among the loaded assemblies.</summary>
    public static bool IsPresent() { try { Probe(); } catch { } return _present; }

    /// <summary>True if RelicForge reports this relic as a hidden companion (a donor the player does not
    /// own). False when RelicForge isn't installed.</summary>
    public static bool IsCompanion(RelicModel r)
    {
        try { Probe(); return _isCompanion != null && _isCompanion.Invoke(null, new object[] { r }) is true; }
        catch { return false; }
    }

    /// <summary>True if RelicForge has attached a forge record (prefix / curse) to this relic instance.
    /// False when RelicForge isn't installed or the relic is plain vanilla.</summary>
    public static bool IsForged(RelicModel r)
    {
        try
        {
            Probe();
            if (_getDescriptor == null) return false;
            return _getDescriptor.Invoke(null, new object[] { r }) is string d && !string.IsNullOrEmpty(d);
        }
        catch { return false; }
    }
}
