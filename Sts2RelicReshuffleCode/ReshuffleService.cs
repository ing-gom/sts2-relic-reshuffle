using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;      // RelicRarity
using MegaCrit.Sts2.Core.Models;               // RelicModel, ModelDb
using MegaCrit.Sts2.Core.Models.RelicPools;    // SharedRelicPool
using MegaCrit.Sts2.Core.Runs;                 // IRunState

namespace Sts2RelicReshuffle;

/// <summary>
/// The re-roll itself: swap each of a player's eligible relics for a DIFFERENT relic of the SAME rarity.
///
/// ★WHY RARITY-PRESERVING: it makes the swing about *which* relics you hold, never *how many* or *how
/// strong*. Free-for-all randomization hands act-1 players a pair of rares and act-3 players a pair of
/// commons before the boss; neither is a decision, both are just the seed deciding the run. Preserving
/// rarity also makes the whole thing self-describing — the count and rarity multiset are invariants, so
/// there is no "original loadout" to store, restore, or reconcile after a save/load. The relics we hand
/// out are ordinary vanilla relics, so the game's own serialization carries them for free.
///
/// ★WHY THE INTERNAL ADD/REMOVE PATH (<c>AddRelicInternal</c>/<c>RemoveRelicInternal</c>) rather than
/// <c>RelicCmd.Obtain</c>/<c>Remove</c>:
///   1. <c>RelicCmd.Obtain</c> awaits <c>AfterObtained()</c> — the one-time pickup payload. Re-firing
///      that every combat is infinite max-HP / gold / potions. The silent path can never fire it, which
///      is a structural guarantee rather than a filter we have to keep correct.
///   2. Both RelicCmd entry points append to <c>CurrentMapPointHistoryEntry</c> (RelicChoices /
///      RelicsRemoved). At one obtain+remove per relic per combat that is unbounded history spam.
///   3. Nothing is lost: <c>RelicModel.AfterRemoved()</c> is overridden by ZERO relics in the game
///      (verified against the decompiled source — the eleven AfterRemoved overrides are all
///      <c>PowerModel.AfterRemoved(Creature)</c>), so skipping it changes no behaviour.
/// The internal path still raises RelicObtained/RelicRemoved, so the inventory UI updates normally.
/// </summary>
internal static class ReshuffleService
{
    /// <summary>One replacement. Carries the MODELS, not just their ids, because the combat-start banner
    /// needs each relic's icon and localized title — and the outgoing relic has already left the player's
    /// inventory by then, so there would be nothing left to look it up from.</summary>
    internal readonly struct Swap
    {
        public readonly RelicModel From;
        public readonly RelicModel To;
        public Swap(RelicModel from, RelicModel to) { From = from; To = to; }
        public string FromEntry => From.Id.Entry;
        public string ToEntry => To.Id.Entry;
        public override string ToString() => $"{FromEntry}->{ToEntry}";
    }

    /// <summary>The game's own assembly. Relics defined elsewhere belong to another mod, and both sides
    /// of the swap are restricted to vanilla: rolling a modded relic OUT would delete it permanently
    /// (its rarity pool can't produce it again), and rolling one IN would hand it over outside whatever
    /// conditions that mod grants it under.</summary>
    private static readonly System.Reflection.Assembly GameAssembly = typeof(RelicModel).Assembly;

    /// <summary>
    /// Re-roll <paramref name="player"/>'s relics in place. Returns the swaps performed, oldest slot
    /// first, for logging and the combat-start readout.
    ///
    /// ★DETERMINISM: every choice below is derived from (run seed, floor, player NetId, slot ordinal,
    /// source relic id) — values every co-op peer already agrees on — so each client computes the same
    /// result independently and no packet is needed. Nothing here consumes <c>RunState.Rng</c>, so the
    /// run's own draws (rewards, shops, map) are bit-identical to a vanilla run of the same seed.
    /// </summary>
    public static List<Swap> Reroll(Player player)
    {
        var swaps = new List<Swap>();
        var runState = player.RunState;
        if (runState == null) return swaps;

        // Snapshot first: the loop below mutates player.Relics. EVERY eligible relic is replaced —
        // a partial reshuffle is deliberately not offered, because "some of your relics changed" makes
        // the player audit their own inventory every fight to find out which ones, while "all of them
        // changed" is a rule you read once. What stays fixed is decided by the pin toggles (starter /
        // Ancient / stackable / forged), which name a CATEGORY rather than a random subset.
        var sources = player.Relics.Where(r => IsSwappableSource(r, player)).ToList();
        if (sources.Count == 0) return swaps;

        uint seed = runState.Rng.Seed;
        int floor = runState.TotalFloor;
        // NetId is a ulong; fold both halves so two players can never collide on the low 32 bits.
        int who = unchecked((int)(player.NetId ^ (player.NetId >> 32)));

        var pool = BuildTargetPool(player, runState);
        if (pool.Count == 0)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] no eligible target relics for {player.NetId} — re-roll skipped.");
            return swaps;
        }

        // Ids already spoken for: everything the player currently holds (so a re-roll always *changes*
        // the relic) plus everything handed out earlier in this same pass (so we never mint a duplicate).
        var taken = new HashSet<string>(
            player.Relics.Where(r => !RelicForgeBridge.IsCompanion(r)).Select(r => r.Id.Entry),
            StringComparer.Ordinal);

        for (int slot = 0; slot < sources.Count; slot++)
        {
            RelicModel source = sources[slot];
            if (!pool.TryGetValue(source.Rarity, out var candidates)) continue;

            // Fresh filtered view per slot — `taken` grows as we go.
            var available = candidates.Where(c => !taken.Contains(c.Id.Entry)).ToList();
            if (available.Count == 0) continue;

            // Seeded per slot AND per source id: two slots of the same rarity must not collapse onto the
            // same draw, and re-entering the same floor must reproduce the same result.
            var rng = ReshuffleRng.From((int)seed, floor, who, slot, ReshuffleRng.Hash(source.Id.Entry));
            RelicModel chosen = available[rng.Next(available.Count)];

            RelicModel? fresh = ApplySwap(player, source, chosen);
            if (fresh == null) continue;

            taken.Add(chosen.Id.Entry);
            swaps.Add(new Swap(source, fresh!));
        }

        return swaps;
    }

    /// <summary>Replace <paramref name="source"/> with a fresh mutable copy of <paramref name="proto"/>
    /// at the same inventory slot. Returns the relic actually granted, or null (leaving the player
    /// untouched) if anything throws — a single bad relic must not abort combat entry for the rest of
    /// them. The caller needs the returned instance, not the prototype: the banner renders the relic the
    /// player now owns.</summary>
    private static RelicModel? ApplySwap(Player player, RelicModel source, RelicModel proto)
    {
        try
        {
            // player.Relics is IReadOnlyList — no IndexOf, so scan by reference to find the slot.
            int index = -1;
            for (int i = 0; i < player.Relics.Count; i++)
                if (ReferenceEquals(player.Relics[i], source)) { index = i; break; }
            if (index < 0) return null;   // already gone (shouldn't happen; a snapshot went stale)

            RelicModel fresh = proto.ToMutable();
            // Inherit the slot's provenance so inventory ordering and floor-based tooltips stay sane;
            // the player really has held *a* relic in this slot since that floor.
            fresh.FloorAddedToDeck = source.FloorAddedToDeck;

            player.RemoveRelicInternal(source);
            player.AddRelicInternal(fresh, index);
            return fresh;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] swap {source.Id.Entry} -> {proto.Id.Entry} failed: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Whether an owned relic may be rolled AWAY. Each rejection below is a concrete failure mode:
    ///
    ///   · <b>Stackable</b> — the player's requirement, and the right call: a stack built up outside
    ///     combat (Circlet and friends) is progress, and a fresh instance would arrive at StackCount 1.
    ///   · <b>Starter / Ancient</b> — configurable, both default to fixed. Starters ARE the character;
    ///     Ancients are run-defining and have no meaningful same-rarity peer set.
    ///   · <b>Wax</b> — a wax relic is on a melt countdown. Swapping a live one for a permanent relic is
    ///     a free upgrade, and swapping a melted one launders a corpse into a working relic.
    ///   · <b>Melted</b> — already dead; the game filters it out of hook dispatch. Reviving it as a live
    ///     relic would be a strict gain for having lost something.
    ///   · <b>RelicForge companion</b> — a hidden donor instance the player does not own, re-derived from
    ///     its host on load. Removing one duplicates a relic on the next load. See
    ///     [[project_sts2_relic_forge]]; RelicForge excludes companions from every player.Relics scan
    ///     and so must we.
    ///   · <b>RelicForge forged</b> — configurable. The forge record lives on the relic INSTANCE, so a
    ///     re-roll silently destroys a prefix the player paid gold for.
    ///   · <b>Non-vanilla</b> — see <see cref="GameAssembly"/>; deleting another mod's relic is not ours
    ///     to do.
    /// </summary>
    public static bool IsSwappableSource(RelicModel r, Player player)
    {
        if (r == null) return false;
        if (r.GetType().Assembly != GameAssembly) return false;
        if (r.IsStackable) return false;
        if (r.IsWax || r.IsMelted) return false;
        if (r.Rarity == RelicRarity.None) return false;
        if (r.Rarity == RelicRarity.Starter && ReshuffleConfig.EffectiveKeepStarter) return false;
        if (r.Rarity == RelicRarity.Ancient && ReshuffleConfig.EffectiveKeepAncient) return false;
        if (RelicForgeBridge.IsCompanion(r)) return false;
        if (ReshuffleConfig.EffectiveKeepForged && RelicForgeBridge.IsForged(r)) return false;
        return true;
    }

    /// <summary>
    /// Relics this player could legitimately be handed, grouped by rarity.
    ///
    /// ★THE POOL IS THE PLAYER'S OWN, not "all relics": <c>SharedRelicPool</c> plus
    /// <c>player.Character.RelicPool</c>, filtered by their unlock state — exactly the list
    /// <c>RelicGrabBag.Populate</c> builds for real relic rewards. Anything else would hand an Ironclad
    /// a Defect orb relic. <c>IsAllowed(runState)</c> then applies the game's own per-run gating on top.
    /// </summary>
    private static Dictionary<RelicRarity, List<RelicModel>> BuildTargetPool(Player player, IRunState runState)
    {
        var result = new Dictionary<RelicRarity, List<RelicModel>>();
        try
        {
            var candidates = ModelDb.RelicPool<SharedRelicPool>().GetUnlockedRelics(player.UnlockState)
                .Concat(player.Character.RelicPool.GetUnlockedRelics(player.UnlockState));

            bool combatOnly = ReshuffleConfig.EffectiveCombatRelevantOnly;

            foreach (RelicModel proto in candidates)
            {
                if (proto == null) continue;
                if (proto.GetType().Assembly != GameAssembly) continue;
                if (proto.Rarity == RelicRarity.None) continue;
                if (proto.Rarity == RelicRarity.Starter) continue;   // never minted by a re-roll
                if (proto.Rarity == RelicRarity.Ancient && ReshuffleConfig.EffectiveKeepAncient) continue;
                if (!RelicClassifier.IsValidTarget(proto)) continue;
                if (combatOnly && !RelicClassifier.HasCombatValue(proto)) continue;
                if (!proto.IsAllowed(runState)) continue;

                if (!result.TryGetValue(proto.Rarity, out var bucket))
                    result[proto.Rarity] = bucket = new List<RelicModel>();
                bucket.Add(proto);
            }

            // Stable order on every peer. ModelDb enumeration order is not contractually fixed, and the
            // draw indexes into this list, so an ordering difference between clients is a desync.
            foreach (var bucket in result.Values)
                bucket.Sort((a, b) => string.CompareOrdinal(a.Id.Entry, b.Id.Entry));
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] target pool build failed: {e.Message}");
        }
        return result;
    }

    /// <summary>Pool sizes per rarity, for the startup log and the self-test.</summary>
    public static string DescribePool(Player player)
    {
        var pool = BuildTargetPool(player, player.RunState);
        if (pool.Count == 0) return "(empty)";
        return string.Join(", ", pool.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
                                     .Select(kv => $"{kv.Key}={kv.Value.Count}"));
    }
}
