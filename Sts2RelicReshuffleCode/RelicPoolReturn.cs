using System;
using System.Collections.Generic;
using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;   // RelicRarity
using MegaCrit.Sts2.Core.Models;            // RelicModel

namespace Sts2RelicReshuffle;

/// <summary>
/// Put a relic the reshuffle took away back into the player's draw pool, so it can be offered again.
///
/// ★WHY. Vanilla removes a relic from <c>RelicGrabBag</c> the moment you obtain it, and never returns it.
/// That rule is correct in vanilla because obtaining a relic means KEEPING it — "already obtained" and
/// "currently owned" are the same thing, so a relic leaving the pool costs you nothing. This mod breaks
/// that premise: relics come and go every fight. Left alone, every relic that ever passed through your
/// hands is gone from shops and rewards forever, and the pool narrows all run until late shops have
/// almost nothing to offer.
///
/// So the pool is redefined to mean [i]not currently owned[/i] rather than [i]never obtained[/i]. Combined
/// with the offer-time filter (<see cref="RelicOfferFilterPatch"/>) the bag converges on exactly that.
///
/// ★ONE-TIME REWARD RELICS ARE NEVER RETURNED, and this is the important carve-out. Relics whose whole
/// payload is dispensed at pickup (Strawberry, Mango, Pandora's Box…) already paid out. Returning one to
/// the pool would let a player collect the reward, have the reshuffle take the relic away, buy it again,
/// and collect the reward a second time — an unbounded max-HP / gold / potion loop. Their reward is spent,
/// so they stay out.
///
/// ★REFLECTION, GUARDED. RelicGrabBag exposes Populate / Remove / MoveToFallback but no Add, so the
/// rarity deques have to be reached directly. Every failure degrades to "don't return it" — the previous
/// behaviour — rather than throwing inside a combat entry.
/// </summary>
internal static class RelicPoolReturn
{
    private static bool _probed;
    private static FieldInfo? _dequesField;

    private static Dictionary<RelicRarity, List<RelicModel>>? Deques(object bag)
    {
        if (!_probed)
        {
            _probed = true;
            try
            {
                _dequesField = bag.GetType().GetField("_deques",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (_dequesField == null)
                    MainFile.Logger.Warn($"[{MainFile.ModId}] RelicGrabBag._deques not found — " +
                                         "swapped-away relics will not return to the pool (game update?).");
            }
            catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] grab-bag probe failed: {e.Message}"); }
        }
        try { return _dequesField?.GetValue(bag) as Dictionary<RelicRarity, List<RelicModel>>; }
        catch { return null; }
    }

    /// <summary>
    /// Return <paramref name="relic"/> to <paramref name="player"/>'s draw pool. No-op for one-time
    /// reward relics (see the class remark) and for rarities the bag does not stock.
    /// </summary>
    public static void TryReturn(Player player, RelicModel relic)
    {
        try
        {
            if (relic == null) return;
            if (relic.HasUponPickupEffect) return;      // its reward is already spent — must not be re-bought
            if (relic.SpawnsPets || relic.AddsPet) return;

            object bag = player.RelicGrabBag;
            var deques = Deques(bag);
            if (deques == null) return;

            // Only put it back where the game already keeps that rarity. Inventing a deque for a rarity
            // the bag never stocks (Starter / Ancient / Event / None) would offer relics vanilla wouldn't.
            if (!deques.TryGetValue(relic.Rarity, out var deque) || deque == null) return;

            // The bag holds canonical prototypes, not owned instances — putting a mutable copy in would
            // carry per-instance state (wax, melted, forge records) into a future offer.
            RelicModel proto = relic.CanonicalInstance ?? relic;

            // Remove first so a relic that cycles through the pool more than once can never end up with
            // two entries. Remove() matches by Id across every deque.
            player.RelicGrabBag.Remove(proto);
            // Append: deterministic on every co-op peer, which matters because the deque order decides
            // future offers and both peers derive the same reshuffle.
            deque.Add(proto);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] returning {relic?.Id.Entry} to the pool failed: {e.Message}");
        }
    }

    /// <summary>Test-only: is this relic currently in the player's draw pool?</summary>
    internal static bool? IsInPool(Player player, RelicModel relic)
    {
        try
        {
            var deques = Deques(player.RelicGrabBag);
            if (deques == null) return null;
            foreach (var kv in deques)
                foreach (var r in kv.Value)
                    if (r.Id == relic.Id) return true;
            return false;
        }
        catch { return null; }
    }
}
