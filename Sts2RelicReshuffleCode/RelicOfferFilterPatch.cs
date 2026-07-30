using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;    // RelicRarity
using MegaCrit.Sts2.Core.Factories;          // RelicFactory
using MegaCrit.Sts2.Core.Models;             // RelicModel

namespace Sts2RelicReshuffle;

/// <summary>
/// Never offer the player a relic they are currently holding.
///
/// ★THIS RESTORES A VANILLA INVARIANT THIS MOD BREAKS — it is not a new rule. The game keeps "you are
/// never offered a relic you already own" without any filter at all: <c>RelicCmd.Obtain</c> removes the
/// obtained relic from the player's <c>RelicGrabBag</c>, and every shop / treasure / reward draw pulls
/// from that bag with the filter <c>_ => true</c>. Ownership is simply never consulted.
///
/// The reshuffle grants relics through <c>AddRelicInternal</c> (deliberately — see ReshuffleService for
/// why the Obtain path cannot be used), which does NOT remove them from the bag. So a relic handed to you
/// by a reshuffle stays in the pool and a later shop can offer it back while you are holding it — and
/// buying it calls Obtain, producing a genuine second copy.
///
/// ★WHY NOT JUST MIRROR OBTAIN AND REMOVE FROM THE BAG. That was the obvious fix and it is worse than the
/// bug: the reshuffle grants a couple of relics per fight across ~40 fights, while each rarity deque holds
/// only 20-35 entries. Removing on every grant would DRAIN the bag, and real shop / reward draws would
/// degrade to RelicFactory.FallbackRelic (Circlet). Filtering at offer time consumes nothing —
/// <c>PullFromFront</c> skips entries the filter rejects without removing them.
///
/// ★AND IT DEGRADES GRACEFULLY ON ITS OWN. If the filter leaves a rarity with no candidates, the game's
/// own <c>GetAvailableDeque</c> walks up the rarity ladder (Shop → Common → Uncommon → Rare) and then to
/// the MP fallback deque. So "you own every common still in the bag" turns into an uncommon offer rather
/// than anything broken — no guard of ours required.
///
/// Read-only with respect to run state: it only narrows which bag entry a draw picks. Both peers own the
/// same relics at any room boundary, so this cannot diverge in co-op.
/// </summary>
[HarmonyPatch(typeof(RelicFactory))]
internal static class RelicOfferFilterPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(RelicFactory.PullNextRelicFromFront),
                  new[] { typeof(Player), typeof(RelicRarity), typeof(Func<RelicModel, bool>) })]
    private static void FrontPrefix(Player player, ref Func<RelicModel, bool> filter)
        => filter = ExcludeOwned(player, filter);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(RelicFactory.PullNextRelicFromBack),
                  new[] { typeof(Player), typeof(RelicRarity), typeof(Func<RelicModel, bool>) })]
    private static void BackPrefix(Player player, ref Func<RelicModel, bool> filter)
        => filter = ExcludeOwned(player, filter);

    /// <summary>
    /// Wrap the caller's filter so owned relics are skipped. The owned set is snapshotted ONCE per draw:
    /// the filter is invoked for every deque entry, and rebuilding it each time would turn one pull into
    /// a scan of the inventory per candidate.
    ///
    /// ★Hidden Sts2RelicForge companions are NOT counted as owned. A companion is a donor instance grafted
    /// onto a forged host — the player does not own it, no icon, never serialized. Counting it would bar
    /// the REAL relic from ever being offered again, which is the mirror bug Sts2RelicTransmute documents.
    /// </summary>
    private static Func<RelicModel, bool> ExcludeOwned(Player player, Func<RelicModel, bool>? inner)
    {
        HashSet<string> owned;
        try
        {
            owned = new HashSet<string>(
                player.Relics.Where(r => !RelicForgeBridge.IsCompanion(r)).Select(r => r.Id.Entry),
                StringComparer.Ordinal);
        }
        catch (Exception e)
        {
            // Never break relic generation over this — fall through to the game's own behaviour.
            MainFile.Logger.Warn($"[{MainFile.ModId}] owned-relic snapshot failed: {e.Message}");
            return inner ?? (_ => true);
        }

        return r => r != null
                 && (inner == null || inner(r))
                 && !owned.Contains(r.Id.Entry);
    }

    /// <summary>Test-only: the filter this patch would apply, so the self-test can assert on it without
    /// having to drain the real grab bag.</summary>
    internal static Func<RelicModel, bool> FilterForTest(Player player)
        => ExcludeOwned(player, null);
}
