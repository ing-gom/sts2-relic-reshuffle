using System;
using System.Collections.Generic;
using Godot;                       // Engine, SceneTree
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;  // LocalContext
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Rooms;    // CombatRoom
using MegaCrit.Sts2.Core.Runs;     // RunManager, IRunState

namespace Sts2RelicReshuffle;

/// <summary>
/// The one place the re-roll happens: a prefix on <c>CombatRoom.StartCombat</c>.
///
/// ★WHY HERE AND NOT AT <c>BeforeCombatStart</c> — this is the trap that decides whether the mod works.
/// Relic effects do NOT all fire at the same hook. Bronze Scales applies its Thorns in
/// <c>AfterRoomEntered(room is CombatRoom)</c>, and 35 relics share that shape. StartCombat's body runs
/// <c>SetUpCombat</c> → <c>Hook.AfterRoomEntered</c> → <c>AfterCombatRoomLoaded</c> →
/// <c>Hook.BeforeCombatStart</c>, so swapping at BeforeCombatStart would arrive AFTER AfterRoomEntered
/// had already dispatched and those 35 relics would sit inert for the whole fight. A prefix on the outer
/// StartCombat stub runs before any of that. (An async method's prefix patches the stub, which executes
/// before the state machine starts — so this really is the earliest point inside the call.)
///
/// The swap is picked up correctly because the game builds its hook listener list at DISPATCH time
/// (<c>IterateHookListeners</c> reads <c>player.Relics</c> then, filtering only <c>!IsMelted</c>), so any
/// relic present before the dispatch gets its hooks called.
///
/// ★WHY EVERY PLAYER, NOT JUST THE LOCAL ONE — in co-op each peer sends its OWN player state and applies
/// the others' during <c>CombatStateSynchronizer.StartSync</c>/<c>WaitForSync</c>, and that exchange
/// completes in <c>EnterMapPointInternal</c> BEFORE the room is even created. A local-only re-roll would
/// therefore never reach the other peer for this fight, and the two machines would simulate different
/// relics in lockstep. Instead every peer re-rolls every player from the same derivation. That is sound
/// precisely because the sync just finished: all peers enter this method holding identical relic lists.
///
/// ★SAVE/LOAD NEEDS NO SPECIAL HANDLING, and it is worth writing down why rather than rediscovering it.
/// There is no mid-combat save: the run is saved at ROOM ENTRY (before this prefix runs) and again after
/// a combat is WON, where the room is first marked pre-finished. So the two reload paths are
///   · from the room-entry save — StartCombat runs again with the same seed, floor and relic list, and
///     the derivation is reproducible, so the player gets the SAME relics they had before saving;
///   · from the post-combat save — the room is pre-finished, which routes to StartPreFinishedCombat,
///     a separate method that never calls StartCombat. No second roll happens at all.
/// Determinism is therefore doing double duty: it is what makes co-op converge AND what stops a reload
/// from quietly handing the player a different loadout.
/// </summary>
[HarmonyPatch(typeof(CombatRoom))]
internal static class CombatEntryPatch
{
    /// <summary>
    /// Which (floor, player) pairs have already been reshuffled in the CURRENT run, so entering the same
    /// combat twice cannot roll twice.
    ///
    /// ★WHY FLOOR-KEYED RATHER THAN ROOM-KEYED. The obvious guard is "remember the CombatRoom instance",
    /// and it is wrong. A combat-reset / undo mod that rebuilds the room hands us a NEW CombatRoom at the
    /// SAME floor — the guard misses, the reshuffle runs again, and this time its inputs are the relics
    /// the FIRST pass produced, so it rolls somewhere else entirely. The player would watch their relics
    /// change every time they rewound a fight. Keying on the floor makes a re-entry a no-op, which is
    /// also the honest answer: the same fight on the same floor is the same fight.
    ///
    /// ★AND WHY IT IS SCOPED TO A RUN INSTANCE. Loading a save builds a NEW IRunState, and the relics in
    /// that save are PRE-reshuffle (the game saves at room entry, never mid-combat). If the record
    /// survived the load we would skip the reshuffle and hand the player their real relics back. So the
    /// record resets whenever the run object changes identity, and the reload then re-derives — landing
    /// on the same relics as before, because the derivation is reproducible (solo assert 8).
    /// </summary>
    /// <summary>Per (floor, player): the inventory this mod LEFT that player holding after rolling.
    /// ★NOT a plain "already rolled here" flag — see <see cref="ReshuffleOnce"/> for why the difference
    /// matters to combat-restart mods.</summary>
    private static readonly Dictionary<(int floor, ulong netId), string> _rolled = new();

    /// <summary>The relic ids a player holds, in slot order. Cheap, and order-sensitive because the
    /// engine's checksum is too.</summary>
    private static string Signature(Player p)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var r in p.Relics) { sb.Append(r?.Id.Entry ?? "?"); sb.Append(','); }
            return sb.ToString();
        }
        catch { return "(unreadable)"; }
    }
    private static IRunState? _rolledRun;

    /// <summary>Test-only breadcrumb. The two co-op instances share one godot.log and clobber each
    /// other's lines, so "did this peer run the reshuffle?" could not be answered from the log at all.
    /// The self-test reads this and puts it in its own per-role result file.</summary>
    internal static string Trace = "(prefix never ran)";

    [HarmonyPrefix]
    [HarmonyPatch("StartCombat")]
    private static void Prefix(CombatRoom __instance)
    {
        try
        {
            if (__instance == null) return;

            IRunState? runState = RunManager.Instance?.State;
            if (runState == null) { Trace = "prefix: runState null"; return; }

            Trace = $"prefix: entered, players={runState.Players.Count}, floor={runState.TotalFloor}";
            ReshuffleOnce(runState);
        }
        catch (Exception e)
        {
            // A failed re-roll must never block combat entry — the player just fights with what they had.
            Trace += " | THREW: " + e.GetType().Name + ": " + e.Message;
            MainFile.Logger.Error($"[{MainFile.ModId}] combat-entry re-roll failed: {e}");
        }
    }

    /// <summary>
    /// The reshuffle proper, idempotent per (run, floor, player). Separated from the Harmony prefix so
    /// the self-test can call it twice and prove the second call changes nothing.
    ///
    /// ★THE GUARD COMPARES INVENTORIES, NOT A FLAG, and that is what makes combat-restart and undo mods
    /// work. A plain "already rolled on this floor" flag answers the wrong question. Re-entering the same
    /// room must not roll again — rolling with the ALREADY-reshuffled relics as sources would derive a
    /// different result and the player's relics would churn every re-entry. But a restart mod that
    /// restores the pre-combat snapshot puts the ORIGINAL relics back, and a flag would then skip,
    /// leaving the player to fight with un-reshuffled relics: the mod silently off for that fight.
    ///
    /// Recording what we left them holding distinguishes the two on sight:
    ///   · inventory == what we left  → this is a re-entry, nothing to do;
    ///   · inventory != what we left  → someone put different relics there, roll from those.
    ///
    /// Rolling again after a restore is safe precisely because the derivation is deterministic in
    /// (seed, floor, NetId, slot, source id): the same pre-roll inventory yields the same post-roll one,
    /// so the restart lands on the state the player had, rather than a fresh shuffle.
    ///
    /// ★It also covers save/load without a special case. The room-entry save holds pre-roll relics and
    /// the post-combat save holds post-roll ones; both compare correctly against what we recorded, and a
    /// reload into a fresh process has an empty table and simply rolls (deterministically, same result).
    /// </summary>
    internal static void ReshuffleOnce(IRunState runState)
    {
        if (!ReferenceEquals(runState, _rolledRun))
        {
            _rolled.Clear();
            _rolledRun = runState;
        }

        bool recorded = false;
        foreach (var player in runState.Players)
        {
            if (player == null) continue;

            var key = (runState.TotalFloor, player.NetId);
            if (_rolled.TryGetValue(key, out string? left) && left == Signature(player))
            {
                Trace += $" | {player.NetId}:guarded";
                MainFile.Logger.Info($"[{MainFile.ModId}] floor {runState.TotalFloor}: {player.NetId} still holds what the re-roll left — re-entry left as is.");
                continue;
            }

            var swaps = ReshuffleService.Reroll(player);
            _rolled[key] = Signature(player);
            Trace += $" | {player.NetId}:{swaps.Count}swaps";
            if (swaps.Count == 0)
            {
                MainFile.Logger.Info($"[{MainFile.ModId}] floor {runState.TotalFloor}: no re-roll for {player.NetId} (nothing eligible).");
                continue;
            }
            MainFile.Logger.Info(
                $"[{MainFile.ModId}] floor {runState.TotalFloor} re-roll for {player.NetId} " +
                $"[{(HostReshuffleConfig.UseHost ? "host cfg" : "local cfg")}: {HostReshuffleConfig.Describe()}]: " +
                string.Join(", ", swaps.ConvertAll(s => s.ToString())));

            // Record EVERY player, keyed by NetId. Which entry to show is decided when the panel is
            // opened — see ReshuffleHistory for why deciding "is this me?" here loses the feature on a
            // co-op client (its identity has not settled at this point in room entry).
            ReshuffleHistory.Record(runState.TotalFloor, player.NetId, swaps);
            recorded = true;
            Trace += "(recorded)";
        }

        // Reveal + flash the top-bar button, once, and only if THIS machine's player actually had
        // something reshuffled. Deferred a beat for the same reason the recording no longer branches on
        // identity: right now ReshuffleHistory.Current may not resolve yet, but a second later it will.
        if (recorded) SchedulePulse();
    }

    /// <summary>Flash the log button shortly after the reshuffle, once the local identity has settled.</summary>
    private static void SchedulePulse()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            var timer = tree.CreateTimer(1.2, processAlways: true);
            timer.Timeout += () =>
            {
                if (ReshuffleHistory.Current != null) NReshuffleSummaryButton.Pulse();
                else NReshuffleSummaryButton.PulseTrace = "skipped: Current was null at pulse time";
            };
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] pulse scheduling failed: {e.Message}"); }
    }

    /// <summary>Test-only: forget the per-run record so a self-test can exercise the same floor twice.</summary>
    internal static void ResetGuardForTest()
    {
        _rolled.Clear();
        _rolledRun = null;
    }


}
