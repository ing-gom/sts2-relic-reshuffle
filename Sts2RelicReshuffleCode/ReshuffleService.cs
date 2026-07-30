using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;      // RelicRarity
using MegaCrit.Sts2.Core.Models;               // RelicModel, ModelDb
using MegaCrit.Sts2.Core.Models.RelicPools;    // SharedRelicPool, EventRelicPool
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

    /// <summary>Keeps the rotation's RNG stream distinct from the per-slot draw, so a slot that falls
    /// through to the rotation doesn't reuse the stream position its normal draw would have had.</summary>
    private const int RotationSalt = 0x0B17A7;

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
        // changed" is a rule you read once. What stays fixed is decided by CATEGORY (starters, relics
        // the player paid to re-forge, and — unless a setting opens them up — Ancient and event relics),
        // never by a random subset.
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

        // Slots that found no UNOWNED same-rarity relic — i.e. the player already holds everything of
        // that rarity. Handled after the main pass; see PermuteExhausted.
        var exhausted = new List<RelicModel>();

        for (int slot = 0; slot < sources.Count; slot++)
        {
            RelicModel source = sources[slot];
            if (!pool.TryGetValue(source.Rarity, out var candidates)) continue;

            // Fresh filtered view per slot — `taken` grows as we go.
            var available = candidates.Where(c => !taken.Contains(c.Id.Entry)).ToList();
            if (available.Count == 0) { exhausted.Add(source); continue; }

            // Seeded per slot AND per source id: two slots of the same rarity must not collapse onto the
            // same draw, and re-entering the same floor must reproduce the same result.
            var rng = ReshuffleRng.From((int)seed, floor, who, slot, ReshuffleRng.Hash(source.Id.Entry));
            RelicModel chosen = available[rng.Next(available.Count)];

            RelicModel? fresh = ApplySwap(player, source, chosen);
            if (fresh == null) continue;

            taken.Add(chosen.Id.Entry);
            swaps.Add(new Swap(source, fresh!));
        }

        swaps.AddRange(PermuteExhausted(player, exhausted, seed, floor, who));
        return swaps;
    }

    /// <summary>
    /// Handle the collector's endgame: a player who already owns every relic of a rarity.
    ///
    /// ★WHY NOT JUST LEAVE THEM. The main pass only ever picks a relic the player does NOT own, which is
    /// what guarantees a re-roll always CHANGES something and never mints a duplicate. Once the player
    /// holds the whole rarity, that filter empties and those slots would silently freeze — the mod would
    /// quietly stop working exactly for the most invested players.
    ///
    /// ★WHY A ROTATION RATHER THAN A RE-DRAW. Allowing owned relics back into the draw would let two
    /// slots land on the same id. Rotating the exhausted slots' own relics among themselves is a
    /// permutation by construction: every slot ends up holding a relic it did not hold before, the
    /// multiset is unchanged, and a duplicate is impossible. A lone exhausted slot has nothing to trade
    /// with, so it keeps what it has.
    ///
    /// Targets are read from a snapshot taken BEFORE any mutation, so the rotation is applied against
    /// the original assignment rather than a half-updated inventory.
    /// </summary>
    private static List<Swap> PermuteExhausted(Player player, List<RelicModel> exhausted,
                                               uint seed, int floor, int who)
    {
        var result = new List<Swap>();
        if (exhausted.Count < 2) return result;

        foreach (var group in exhausted.GroupBy(r => r.Rarity))
        {
            // Stable order so every co-op peer rotates the same list the same way.
            var members = group.OrderBy(r => r.Id.Entry, StringComparer.Ordinal).ToList();
            int n = members.Count;
            if (n < 2) continue;

            // Rotate by a deterministic offset in [1, n-1]: never 0, so no slot keeps its own relic.
            var rng = ReshuffleRng.From((int)seed, floor, who, RotationSalt, n,
                                        ReshuffleRng.Hash(group.Key.ToString()));
            int shift = 1 + rng.Next(n - 1);

            // Snapshot the canonical prototypes before mutating anything.
            var protos = members.Select(m => m.CanonicalInstance).ToList();

            for (int i = 0; i < n; i++)
            {
                RelicModel source = members[i];
                RelicModel? proto = protos[(i + shift) % n];
                if (proto == null) continue;
                RelicModel? fresh = ApplySwap(player, source, proto);
                if (fresh == null) continue;
                result.Add(new Swap(source, fresh));
            }
            MainFile.Logger.Info(
                $"[{MainFile.ModId}] {group.Key}: player owns the whole rarity — rotated {n} relic(s) by {shift}.");
        }
        return result;
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

            // Carry an accumulated stack across the swap. A fresh instance arrives at StackCount 1, so
            // without this a stackable relic would silently lose progress the player built up outside
            // combat. Only meaningful when BOTH sides stack — pushing a count onto a relic that does not
            // stack would show a number the relic has no meaning for.
            if (source.IsStackable && fresh.IsStackable && source.StackCount > 1)
            {
                for (int s = 1; s < source.StackCount; s++) fresh.IncrementStackCount();
                MainFile.Logger.Info($"[{MainFile.ModId}] carried stack {source.StackCount} from {source.Id.Entry} to {fresh.Id.Entry}.");
            }
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
    ///   · <b>Starter</b> — always fixed. The starter relic IS the character.
    ///   · <b>Ancient / Event</b> — fixed unless the matching setting opts them in. Ancients are
    ///     run-defining; event relics are rewards a specific event handed over.
    ///   · <b>Wax</b> — a wax relic is on a melt countdown. Swapping a live one for a permanent relic is
    ///     a free upgrade, and swapping a melted one launders a corpse into a working relic.
    ///   · <b>Melted</b> — already dead; the game filters it out of hook dispatch. Reviving it as a live
    ///     relic would be a strict gain for having lost something.
    ///   · <b>RelicForge companion</b> — a hidden donor instance the player does not own, re-derived from
    ///     its host on load. Removing one duplicates a relic on the next load. See
    ///     [[project_sts2_relic_forge]]; RelicForge excludes companions from every player.Relics scan
    ///     and so must we.
    ///   · <b>RelicForge re-forged / cleansed</b> — the player spent gold on that specific instance, and
    ///     a re-roll would destroy it. Note this is INVESTMENT, not "has a forge record": RelicForge
    ///     attaches a record to nearly every pickup, and pinning those froze whole inventories.
    ///   · <b>Non-vanilla</b> — see <see cref="GameAssembly"/>; deleting another mod's relic is not ours
    ///     to do.
    /// </summary>
    public static bool IsSwappableSource(RelicModel r, Player player)
    {
        if (r == null) return false;
        if (r.GetType().Assembly != GameAssembly) return false;
        if (r.IsWax || r.IsMelted) return false;
        // Rarity None has no same-rarity pool, so it can never be swapped under a rarity-preserving
        // rule. This is what keeps CIRCLET out (it is the None-rarity stackable) — worth naming,
        // because it is also why the stack carry-over below can never actually fire today.
        if (r.Rarity == RelicRarity.None) return false;
        if (r.Rarity == RelicRarity.Starter) return false;
        if (r.Rarity == RelicRarity.Ancient && !ReshuffleConfig.EffectiveIncludeAncient) return false;
        if (r.Rarity == RelicRarity.Event && !ReshuffleConfig.EffectiveIncludeEvent) return false;
        if (RelicForgeBridge.IsCompanion(r)) return false;
        if (RelicForgeBridge.IsPlayerInvested(r)) return false;
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
            bool includeAncient = ReshuffleConfig.EffectiveIncludeAncient;
            bool includeEvent = ReshuffleConfig.EffectiveIncludeEvent;

            var candidates = ModelDb.RelicPool<SharedRelicPool>().GetUnlockedRelics(player.UnlockState)
                .Concat(player.Character.RelicPool.GetUnlockedRelics(player.UnlockState));

            // ★The Ancient / Event toggles have to reach EventRelicPool to mean anything. Measured:
            // that pool holds 94 Ancient + 32 Event relics, while SharedRelicPool contributes only
            // 2 Ancient + 1 Event. Widening the rarity filter alone would leave both options almost
            // inert, so the pool itself is widened when either is switched on.
            if (includeAncient || includeEvent)
                candidates = candidates.Concat(ModelDb.RelicPool<EventRelicPool>().GetUnlockedRelics(player.UnlockState));

            foreach (RelicModel proto in candidates)
            {
                if (proto == null) continue;
                if (proto.GetType().Assembly != GameAssembly) continue;
                if (proto.Rarity == RelicRarity.None) continue;
                if (proto.Rarity == RelicRarity.Starter) continue;   // never minted by a re-roll
                if (proto.Rarity == RelicRarity.Ancient && !includeAncient) continue;
                // ★FRESNEL_LENS is an Event relic sitting in SharedRelicPool, so this filter is load
                // bearing even though EventRelicPool is only read when a toggle asks for it. Without
                // it, that one relic leaks in regardless of the setting.
                if (proto.Rarity == RelicRarity.Event && !includeEvent) continue;
                if (!RelicClassifier.IsValidTarget(proto)) continue;
                if (!RelicClassifier.HasCombatValue(proto)) continue;
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

    /// <summary>Test-only view of the candidate pool for one rarity (the pool builder is private so the
    /// production surface stays small; the self-test needs it to construct an "owns everything" state).</summary>
    internal static List<RelicModel> TargetPoolForTest(Player player, RelicRarity rarity)
        => BuildTargetPool(player, player.RunState).TryGetValue(rarity, out var list)
            ? list
            : new List<RelicModel>();

    /// <summary>Pool sizes per rarity, for the startup log and the self-test.</summary>
    public static string DescribePool(Player player)
    {
        var pool = BuildTargetPool(player, player.RunState);
        if (pool.Count == 0) return "(empty)";
        return string.Join(", ", pool.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
                                     .Select(kv => $"{kv.Key}={kv.Value.Count}"));
    }
}
