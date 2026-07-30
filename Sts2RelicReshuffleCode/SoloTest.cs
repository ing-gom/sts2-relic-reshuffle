using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;          // RelicCmd
using MegaCrit.Sts2.Core.Entities.Players;  // Player
using MegaCrit.Sts2.Core.Entities.Relics;   // RelicRarity
using MegaCrit.Sts2.Core.Helpers;           // TaskHelper
using MegaCrit.Sts2.Core.Localization;      // LocString
using MegaCrit.Sts2.Core.Models;            // ModelDb, RelicModel, ActModel, ModifierModel
using MegaCrit.Sts2.Core.Nodes;             // NGame
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;   // NMainMenu (run-start readiness gate)
using MegaCrit.Sts2.Core.Runs;              // RunManager, GameMode

namespace Sts2RelicReshuffle;

/// <summary>
/// solo-verify battery for the re-roll. Armed by a `selftest.sp.flag` next to the DLL; writes
/// RESULT: OK/FAIL to `selftest.sp.txt`.
///
/// It calls <see cref="ReshuffleService.Reroll"/> directly rather than fighting through a real combat:
/// that method IS what <see cref="CombatEntryPatch"/> invokes, and driving a fight would add a pile of
/// screen automation without testing anything more. The eight checks below each pin one promise the mod
/// makes, and every one of them is a bug that would otherwise only surface mid-run:
///
///   1. COUNT + RARITY INVARIANT — the whole balance argument. If this drifts, the mod is handing out
///      free power (or confiscating it) instead of shuffling.
///   2. EVERYTHING ACTUALLY CHANGED — a re-roll that returns the same relic is a silent no-op.
///   3. NO DUPLICATES — two copies of one relic is a state the game never produces on its own.
///   4. NO ONE-TIME REWARD RELICS HANDED OUT — they would be inert icons (we add silently, so their
///      AfterObtained payload never fires).
///   5. WALLET UNTOUCHED — gold / max HP / potion slots identical across the swap. This is the direct
///      evidence that no AfterObtained fired, i.e. the "이미 받은 1회성 보상이 중복 적용되지 않는다"
///      requirement, measured rather than argued.
///   6. STARTER PINNED — KeepStarter default.
///   7. STACKABLE PINNED WITH ITS STACK — the user's explicit requirement: a stack built outside combat
///      must survive. Tested with a real stack (count bumped to 3 first), not just presence.
///   8. DETERMINISM — restore the original relics and re-roll on the same floor; the result must be
///      identical. This is the co-op contract in miniature: both peers derive rather than negotiate, so
///      a non-reproducible roll is a desync, and nothing else in the test would catch it.
/// </summary>
internal static class SoloTest
{
    private static readonly StringBuilder _out = new();
    private static bool _started, _done, _dumped;

    private static string ModDir() => Path.GetDirectoryName(typeof(SoloTest).Assembly.Location) ?? ".";

    /// <summary>Call from mod init. No-op unless `selftest.sp.flag` sits next to the mod DLL.</summary>
    public static void ArmIfRequested()
    {
        try
        {
            if (!File.Exists(Path.Combine(ModDir(), "selftest.sp.flag"))) return;
            W("solo selftest armed");
            Poll();
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] solo arm failed: {e.Message}"); }
    }

    private static void Poll()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || _done) return;
        try
        {
            var run = RunManager.Instance;
            if (!_started && (run == null || !run.IsInProgress))
            {
                // NGame existing is NOT enough: ModelDb.Init() runs later, so firing on NGame alone hits an
                // EMPTY model registry (AllCharacters throws KeyNotFoundException on CHARACTER.IRONCLAD).
                if (NGame.Instance == null) { W("waiting for NGame…"); }
                else if (FlatCount() == 0) { W("waiting for ModelDb to populate…"); }
                // ★ModelDb-populated is STILL not enough in a heavy modded install: the registry fills
                // long before the main menu finishes loading, and firing the run start before the menu
                // exists means the run never enters — the watchdog then blames 'starting single-player
                // run' and sends you hunting mod soup when the real cause is timing.
                else if (FindNode<NMainMenu>(tree.Root) == null) { W("waiting for main menu…"); }
                else { _started = true; W("starting single-player run…"); TaskHelper.RunSafely(StartRunThenTest()); }
            }
        }
        catch (Exception e) { W("tick exception: " + e.Message); }
        if (!_done) tree.CreateTimer(2.0).Timeout += Poll;
    }

    private static async Task StartRunThenTest()
    {
        bool ok = true;
        try
        {
            var character = ResolveCharacter();
            if (character == null) { W("FAIL: no CharacterModel resolvable"); Flush(false); return; }
            var acts = ActModel.GetDefaultList().ToList();
            await NGame.Instance.StartNewSingleplayerRun(character, shouldSave: false, acts,
                Array.Empty<ModifierModel>(), "SOLOTEST", GameMode.Standard, 0);
            await Task.Delay(3000);   // let the map / first room settle

            var run = RunManager.Instance;
            if (run?.IsInProgress != true || (run.State?.Players?.Count ?? 0) == 0)
            { W("run did not start"); Flush(false); return; }
            var player = run.State!.Players.First();
            W($"run started: {player.Character?.Id.Entry}, floor {run.State.TotalFloor}");
            W($"target pool: {ReshuffleService.DescribePool(player)}");
            // Sample the button BEFORE anything reshuffles. Asserting this later is worthless: assert 10
            // calls the patch entry point, which records and pulses, so by then the button is legitimately
            // visible and the check would fail for a reason that is not a bug.
            _buttonHiddenAtStart = ButtonHidden();
            await Shot("1_run");

            // ── SETUP ────────────────────────────────────────────────────────────────────────────
            // Grant one plain relic per rarity so the rarity-preservation check has something to prove,
            // plus a real stack. Obtained through RelicCmd (the honest pickup path) — these are picked
            // to have no pickup effect and no pickup UI, so nothing prompts and nothing hangs.
            foreach (var rarity in new[] { RelicRarity.Common, RelicRarity.Uncommon, RelicRarity.Rare })
            {
                var proto = PickPlainRelic(player, rarity);
                if (proto == null) { W($"note: no plain {rarity} relic available to grant"); continue; }
                await RelicCmd.Obtain(proto.ToMutable(), player);
                await Task.Delay(200);
            }

            RelicModel? stackable = await GrantStack(player);

            W("owned before: " + Describe(player));

            // ── 0. a normally-obtained relic must still be eligible ─────────────────────────────
            // ★THE REGRESSION THIS EXISTS FOR. The relics above were granted through RelicCmd.Obtain —
            // the real pickup path — and with Sts2RelicForge installed that path attaches a forge record
            // to essentially EVERY relic. The pin rule used to be "has a forge record", which therefore
            // froze the player's whole inventory: only relics this mod had itself swapped in stayed
            // eligible, so each fight re-rolled one slot and by act 2 nothing was eligible at all.
            // An earlier run of this very battery showed the symptom (3 eligible relics, 2 swapped) and
            // it was written off as a test artifact. Asserting it makes that impossible to wave away.
            // Rarity None is excluded structurally, not by a pin: rarity-preserving swaps need a pool of
            // same-rarity peers and no None-rarity pool exists, so those relics can never be a source.
            var pickups = player.Relics
                .Where(r => r.Rarity != RelicRarity.Starter && r.Rarity != RelicRarity.None)
                .ToList();
            var frozen = pickups.Where(r => !ReshuffleService.IsSwappableSource(r, player)).ToList();
            bool eligibleOk = pickups.Count > 0 && frozen.Count == 0;
            W($"assert 0 every normal pickup is eligible: {eligibleOk} (want True) — {pickups.Count} pickup(s)"
              + (frozen.Count > 0
                 ? "  ★FROZEN: " + string.Join(", ", frozen.Select(r => $"{r.Id.Entry}(forge={RelicForgeBridge.DescriptorOf(r) ?? "-"})"))
                 : ""));
            if (!eligibleOk) ok = false;

            var before = Snapshot(player);
            long goldBefore = player.Gold;
            int maxHpBefore = player.Creature?.MaxHp ?? -1;
            int potionsBefore = player.MaxPotionCount;
            int stackBefore = stackable?.StackCount ?? -1;

            // ── RE-ROLL ──────────────────────────────────────────────────────────────────────────
            var swaps = ReshuffleService.Reroll(player);
            await Task.Delay(300);
            W($"re-roll produced {swaps.Count} swap(s): " + string.Join(", ", swaps.Select(s => s.ToString())));
            W("owned after:  " + Describe(player));
            var after = Snapshot(player);

            if (swaps.Count == 0)
            {
                W("FAIL: nothing was re-rolled — the eligibility filter rejected every owned relic.");
                Flush(false); return;
            }

            // ── 1. count + rarity multiset invariant ────────────────────────────────────────────
            string rarBefore = RarityHistogram(before);
            string rarAfter = RarityHistogram(after);
            bool inv = before.Count == after.Count && rarBefore == rarAfter;
            W($"assert 1 count+rarity invariant: {before.Count}/{rarBefore} -> {after.Count}/{rarAfter} = {inv} (want True)");
            if (!inv) ok = false;

            // ── 2. every swapped relic really changed ───────────────────────────────────────────
            var unchanged = swaps.Where(s => string.Equals(s.FromEntry, s.ToEntry, StringComparison.Ordinal)).ToList();
            W($"assert 2 all swaps changed the relic: {unchanged.Count == 0} (want True)"
              + (unchanged.Count > 0 ? $" — no-ops: {string.Join(", ", unchanged.Select(s => s.FromEntry))}" : ""));
            if (unchanged.Count > 0) ok = false;

            // ── 3. no duplicate relic ids ───────────────────────────────────────────────────────
            var dupes = after.GroupBy(r => r.entry).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            W($"assert 3 no duplicate relics: {dupes.Count == 0} (want True)"
              + (dupes.Count > 0 ? $" — duplicated: {string.Join(", ", dupes)}" : ""));
            if (dupes.Count > 0) ok = false;

            // ── 4. no one-time reward relic was handed out ──────────────────────────────────────
            var pickup = player.Relics.Where(r => r.HasUponPickupEffect)
                                      .Select(r => r.Id.Entry)
                                      .Where(e => swaps.Any(s => s.ToEntry == e)).ToList();
            W($"assert 4 no one-time-reward relic rolled in: {pickup.Count == 0} (want True)"
              + (pickup.Count > 0 ? $" — leaked: {string.Join(", ", pickup)}" : ""));
            if (pickup.Count > 0) ok = false;

            // ── 5. nothing was granted on the side (proves AfterObtained never fired) ───────────
            bool wallet = player.Gold == goldBefore
                       && (player.Creature?.MaxHp ?? -1) == maxHpBefore
                       && player.MaxPotionCount == potionsBefore;
            W($"assert 5 no side effects: gold {goldBefore}->{player.Gold}, maxHp {maxHpBefore}->"
              + $"{player.Creature?.MaxHp ?? -1}, potionSlots {potionsBefore}->{player.MaxPotionCount} = {wallet} (want True)");
            if (!wallet) ok = false;

            // ── 6. starter relic pinned ─────────────────────────────────────────────────────────
            var starterBefore = before.Where(r => r.rarity == RelicRarity.Starter).Select(r => r.entry).OrderBy(x => x, StringComparer.Ordinal);
            var starterAfter = after.Where(r => r.rarity == RelicRarity.Starter).Select(r => r.entry).OrderBy(x => x, StringComparer.Ordinal);
            bool starterOk = starterBefore.SequenceEqual(starterAfter, StringComparer.Ordinal);
            W($"assert 6 starter relics pinned: [{string.Join(",", starterBefore)}] -> [{string.Join(",", starterAfter)}] = {starterOk} (want True)");
            if (!starterOk) ok = false;

            // ── 7. stackable relic pinned, stack intact ─────────────────────────────────────────
            // ── 7. a stack is never LOST ────────────────────────────────────────────────────────
            // The invariant is not "stackables are pinned" — they are allowed to swap now — but "an
            // accumulated stack survives whatever happens". Either the relic stays and keeps its count,
            // or it was swapped and the incoming relic inherited it.
            if (stackable == null)
            {
                W("assert 7 stack preserved: SKIPPED (no stackable relic available to grant)");
            }
            else
            {
                var swappedTo = swaps.FirstOrDefault(s => s.FromEntry == stackable.Id.Entry);
                var live = player.Relics.FirstOrDefault(r => r.Id.Entry ==
                    (swappedTo.To != null ? swappedTo.To.Id.Entry : stackable.Id.Entry));
                bool stackOk = live != null && live.StackCount == stackBefore;
                W($"assert 7 stack {stackBefore} preserved across the reshuffle: "
                  + $"{stackable.Id.Entry}{(swappedTo.To != null ? " -> " + swappedTo.ToEntry : " (not swapped)")}, "
                  + $"now={live?.StackCount ?? -1} = {stackOk} (want True)");
                if (!stackOk) ok = false;
            }

            // ── 8. determinism: same inputs -> same roll ────────────────────────────────────────
            bool det = await CheckDeterminism(player, before);
            if (!det) ok = false;

            // ── 9. the reshuffle log actually renders ───────────────────────────────────────────
            bool ui = await CheckPanel(swaps);
            if (!ui) ok = false;

            // ── 10. re-entering the same combat must NOT re-roll ────────────────────────────────
            bool reentry = CheckReentry(run);
            if (!reentry) ok = false;

            // ── 15. a relic the reshuffle took away returns to the draw pool ────────────────────
            bool returned = await CheckPoolReturn(player);
            if (!returned) ok = false;

            // ── 16. a one-time reward is paid once per run, however often the relic is obtained ──
            bool onceOnly = await CheckRepeatPickup(player);
            if (!onceOnly) ok = false;

            // ── 14. a relic you already hold is never offered ───────────────────────────────────
            bool offers = CheckOfferFilter(player);
            if (!offers) ok = false;

            // ── 13. the top-bar button is hidden until a reshuffle happens ──────────────────────
            bool btn = CheckButtonVisibility();
            if (!btn) ok = false;

            // ── 12. the stack carry-over branch actually works ──────────────────────────────────
            bool carried = CheckStackCarryOver(player);
            if (!carried) ok = false;

            // ── 11. owning an entire rarity still reshuffles (runs LAST — it wrecks the inventory) ──
            bool collector = CheckOwnsEverything(player);
            if (!collector) ok = false;

            await Shot("2_final");
            W($"=== solo test done: {(ok ? "PASS" : "FAIL")} ===");
            Flush(ok);
        }
        catch (Exception e) { W("test exception: " + e); Flush(false); }
    }

    /// <summary>
    /// Restore the pre-roll relics and re-roll twice: the two rolls must come out identical. In co-op
    /// every peer derives its own roll from (seed, floor, NetId, slot, source id) and none of them talk
    /// to each other about the result, so a roll that isn't reproducible from the same inputs is exactly
    /// a desync — and it would look like a perfectly healthy run right up until two players disagreed
    /// about what a relic did.
    ///
    /// ★WHY IT COMPARES ROLL 2 vs ROLL 3, NOT ROLL 1 vs ROLL 2 — the first measurement of this got a
    /// false FAIL worth remembering. The setup grants relics through <c>RelicCmd.Obtain</c>, which is the
    /// seam Sts2RelicForge patches to forge a pickup; the restore uses <c>AddRelicInternal</c>, which
    /// bypasses that patch. So a relic that RelicForge had forged (and that <c>KeepForged</c> therefore
    /// pinned) came back UNFORGED and became eligible, and roll 2 legitimately swapped one relic more
    /// than roll 1. The derivation was identical for every shared source — only the eligible SET moved.
    /// Restoring the same way before both compared rolls removes that asymmetry, so this measures the
    /// derivation and nothing else.
    /// </summary>
    private static async Task<bool> CheckDeterminism(Player player, List<(string entry, RelicRarity rarity)> before)
    {
        try
        {
            if (!await Restore(player, before)) return true;   // couldn't set up — skip, reported inside
            var rollA = ReshuffleService.Reroll(player);

            if (!await Restore(player, before)) return true;
            var rollB = ReshuffleService.Reroll(player);

            bool same = rollA.Count == rollB.Count
                     && rollA.Zip(rollB, (a, b) => a.FromEntry == b.FromEntry && a.ToEntry == b.ToEntry).All(x => x);
            W($"assert 8 determinism (same floor, same inventory -> same roll): {same} (want True)");
            W("  roll A: " + string.Join(", ", rollA.Select(s => s.ToString())));
            W("  roll B: " + string.Join(", ", rollB.Select(s => s.ToString())));
            return same;
        }
        catch (Exception e) { W("assert 8 determinism THREW: " + e.Message); return false; }
    }

    /// <summary>
    /// Enter the same combat twice and prove the second entry changes nothing.
    ///
    /// ★WHAT THIS PROTECTS. A combat-reset / undo mod (or any code that rebuilds the room without
    /// advancing the floor) calls the entry path again at the SAME floor. Without a guard the reshuffle
    /// re-runs, and its inputs are now the relics the FIRST pass produced — so it lands somewhere else
    /// and the player's relics change every time they rewind a fight. The guard is floor-scoped exactly
    /// so this is a no-op, and this assert is what proves it rather than assuming it.
    ///
    /// Run ORDER matters: this must come after the reshuffle under test, because it asserts that a
    /// SECOND call is inert. It calls the patch's own entry point, not Reroll, since the guard is the
    /// thing being measured.
    /// </summary>
    private static bool CheckReentry(RunManager run)
    {
        try
        {
            // The earlier asserts drove ReshuffleService directly, so the patch's guard is still empty.
            // Prime it with a real first entry, then measure only what the RE-entries do. Capturing
            // `before` any earlier would fold that first (legitimate) reshuffle into the comparison and
            // the assert would fail for the wrong reason.
            CombatEntryPatch.ResetGuardForTest();
            var start = Fingerprint(run);
            CombatEntryPatch.ReshuffleOnce(run.State!);   // first entry to this floor — SHOULD reshuffle
            var before = Fingerprint(run);

            CombatEntryPatch.ReshuffleOnce(run.State!);   // simulates a reset mod re-entering this combat
            CombatEntryPatch.ReshuffleOnce(run.State!);   // and again, for good measure
            var after = Fingerprint(run);

            // Guard against a vacuous pass: if the first entry changed nothing, two no-ops would
            // trivially "match" and the assert would prove nothing about the guard.
            bool firstDidWork = !start.SequenceEqual(before, StringComparer.Ordinal);
            bool same = before.SequenceEqual(after, StringComparer.Ordinal);
            W($"assert 10a first entry to a floor reshuffles: {firstDidWork} (want True)");
            W($"assert 10b re-entering the same floor does NOT re-roll: {same} (want True)");
            if (!same)
            {
                W("  before re-entry: " + string.Join(" | ", before));
                W("  after re-entry:  " + string.Join(" | ", after));
            }
            return same && firstDidWork;
        }
        catch (Exception e) { W("assert 10 re-entry THREW: " + e.Message); return false; }
    }

    /// <summary>
    /// The collector's endgame: give the player EVERY Common in their pool, then reshuffle.
    ///
    /// ★WHY IT MATTERS. The normal draw only picks relics the player does not own — that is what makes a
    /// re-roll always change something and never duplicate. Own the whole rarity and that filter empties,
    /// so without the rotation fallback these slots would freeze silently, and the mod would stop working
    /// precisely for the most invested players. This asserts the three things the rotation must hold:
    /// the multiset is unchanged, nothing duplicated, and every slot actually moved.
    ///
    /// Destructive (it rewrites the whole inventory), so it runs last.
    /// </summary>
    private static bool CheckOwnsEverything(Player player)
    {
        try
        {
            var commons = ReshuffleService.TargetPoolForTest(player, RelicRarity.Common);
            if (commons.Count < 2)
            {
                W($"assert 11 owns-everything: SKIPPED (Common pool has {commons.Count})");
                return true;
            }

            foreach (var r in player.Relics.ToList())
                if (r.Rarity == RelicRarity.Common) player.RemoveRelicInternal(r);
            foreach (var proto in commons)
                player.AddRelicInternal(proto.ToMutable());

            var before = player.Relics.Where(r => r.Rarity == RelicRarity.Common)
                                      .Select(r => r.Id.Entry).ToList();
            CombatEntryPatch.ResetGuardForTest();
            var swaps = ReshuffleService.Reroll(player);
            var after = player.Relics.Where(r => r.Rarity == RelicRarity.Common)
                                     .Select(r => r.Id.Entry).ToList();

            bool sameSet = before.OrderBy(x => x, StringComparer.Ordinal)
                                 .SequenceEqual(after.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal);
            bool noDupes = after.Distinct(StringComparer.Ordinal).Count() == after.Count;
            int moved = before.Zip(after, (b, a) => b == a ? 0 : 1).Sum();
            bool allMoved = moved == before.Count;

            W($"assert 11a owns {before.Count} Common(s), multiset unchanged: {sameSet} (want True)");
            W($"assert 11b no duplicates after rotation: {noDupes} (want True)");
            W($"assert 11c every slot moved: {moved}/{before.Count} = {allMoved} (want True), swaps={swaps.Count}");
            if (!sameSet || !noDupes || !allMoved)
                W("  before: " + string.Join(",", before) + "\n  after:  " + string.Join(",", after));
            return sameSet && noDupes && allMoved;
        }
        catch (Exception e) { W("assert 11 owns-everything THREW: " + e.Message); return false; }
    }

    /// <summary>
    /// Force a stackable → stackable swap and prove the accumulated count carries over.
    ///
    /// ★WHY IT HAS TO BE FORCED. Assert 7 only shows that a stack was not LOST, and it passes by taking
    /// the "wasn't swapped" branch — CIRCLET is rarity None, so a rarity-preserving swap can never move
    /// it. All three stackable relics in the game are rarity None or Ancient with no stackable
    /// same-rarity peer, which makes the carry-over code unreachable in normal play. Left at that it
    /// would ship never having executed once, and "a stack is never lost" would be a property of the
    /// current relic table rather than of this code. So the swap is driven directly, past eligibility.
    /// </summary>
    private static bool CheckStackCarryOver(Player player)
    {
        try
        {
            var stackables = FlatModels().OfType<RelicModel>()
                .Where(r => r.IsStackable && !r.HasUponPickupEffect)
                .GroupBy(r => r.Id.Entry).Select(g => g.First())
                .OrderBy(r => r.Id.Entry, StringComparer.Ordinal).ToList();
            if (stackables.Count < 2)
            {
                W($"assert 12 stack carry-over: SKIPPED (need 2 stackable relics, found {stackables.Count})");
                return true;
            }

            var sourceProto = stackables[0];
            var targetProto = stackables[1];

            // Put a real stack on the board: a fresh instance starts at 1, so bump it to 4.
            foreach (var r in player.Relics.ToList())
                if (r.Id.Entry == sourceProto.Id.Entry || r.Id.Entry == targetProto.Id.Entry)
                    player.RemoveRelicInternal(r);
            var live = sourceProto.ToMutable();
            player.AddRelicInternal(live);
            live.IncrementStackCount();
            live.IncrementStackCount();
            live.IncrementStackCount();
            int before = live.StackCount;

            var fresh = ReshuffleService.ForceSwapForTest(player, live, targetProto);
            bool swapped = fresh != null && fresh.Id.Entry == targetProto.Id.Entry;
            bool carried = swapped && fresh!.StackCount == before;
            W($"assert 12 stack carry-over {sourceProto.Id.Entry}(x{before}) -> {targetProto.Id.Entry}: "
              + $"swapped={swapped}, newStack={fresh?.StackCount ?? -1} (want {before}) = {carried} (want True)");

            // Also confirm the game agrees — the relic the player now holds carries the count, not just
            // the object we were handed back.
            var held = player.Relics.FirstOrDefault(r => r.Id.Entry == targetProto.Id.Entry);
            bool heldOk = held != null && held.StackCount == before;
            W($"assert 12b inventory copy carries the stack: held={held?.StackCount ?? -1} = {heldOk} (want True)");

            return carried && heldOk;
        }
        catch (Exception e) { W("assert 12 stack carry-over THREW: " + e.Message); return false; }
    }

    /// <summary>
    /// The top-bar log button must be attached but HIDDEN outside a fight, and appear when a reshuffle
    /// is recorded — it has nothing to say on the map or in a shop, and left visible it kept offering
    /// the previous fight's list.
    ///
    /// ⚠️SCOPE: this measures the SHOW path only. Hiding is wired to the game's own
    /// CombatManager.CombatEnded event, which needs a real fight to fire — this battery never enters
    /// one (it drives Reroll directly), so that half is verified by construction, not by measurement.
    /// </summary>
    private static bool CheckButtonVisibility()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) { W("assert 13 button: SKIPPED (no scene tree)"); return true; }
            var node = FindByName(tree.Root, "NReshuffleSummaryButton");
            if (node is not Control button)
            {
                // The top bar only exists inside a run's UI; if it hasn't built yet there is nothing to
                // assert rather than something to fail.
                W("assert 13 button: SKIPPED (top-bar button not attached yet)");
                return true;
            }

            bool hiddenFirst = _buttonHiddenAtStart ?? true;
            W($"assert 13a button hidden before any reshuffle: {hiddenFirst} (want True)"
              + (_buttonHiddenAtStart == null ? " [sampled: button not attached at run start]" : ""));

            NReshuffleSummaryButton.Pulse();
            bool shown = button.Visible;
            W($"assert 13b button shown after a reshuffle is recorded: {shown} (want True)");

            return hiddenFirst && shown;
        }
        catch (Exception e) { W("assert 13 button THREW: " + e.Message); return false; }
    }

    /// <summary>True/false if the button exists, null if the top bar hasn't built it yet.</summary>
    private static bool? ButtonHidden()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return null;
            return FindByName(tree.Root, "NReshuffleSummaryButton") is Control c ? !c.Visible : (bool?)null;
        }
        catch { return null; }
    }

    private static bool? _buttonHiddenAtStart;

    private static Node? FindByName(Node n, string name)
    {
        if (n.Name == name) return n;
        foreach (var c in n.GetChildren())
        {
            var r = FindByName(c, name);
            if (r != null) return r;
        }
        return null;
    }

    /// <summary>
    /// Draw relics the way a shop does and prove none of them is one the player already holds.
    ///
    /// ★WHAT THIS PROTECTS. The game keeps "never offer a relic you own" by REMOVING the relic from the
    /// grab bag inside RelicCmd.Obtain — the draw filter itself is `_ => true` and never looks at
    /// ownership. This mod grants relics through AddRelicInternal, which skips that removal, so without
    /// the offer filter a reshuffled-in relic stays in the pool and a later shop can sell you a second
    /// copy of something you are holding.
    ///
    /// Drives RelicFactory directly: that is the single funnel every shop / treasure / reward draw goes
    /// through, and it consumes real bag entries, so this runs late and on a run that is being discarded.
    /// </summary>
    private static bool CheckOfferFilter(Player player)
    {
        try
        {
            var owned = new HashSet<string>(player.Relics.Select(r => r.Id.Entry), StringComparer.Ordinal);
            if (owned.Count == 0) { W("assert 14 offer filter: SKIPPED (player owns nothing)"); return true; }

            var offered = new List<string>();
            foreach (var rarity in new[] { RelicRarity.Common, RelicRarity.Uncommon, RelicRarity.Rare })
                for (int i = 0; i < 6; i++)
                {
                    var r = MegaCrit.Sts2.Core.Factories.RelicFactory.PullNextRelicFromFront(player, rarity);
                    if (r != null) offered.Add(r.Id.Entry);
                }

            var collisions = offered.Where(e => owned.Contains(e)).Distinct(StringComparer.Ordinal).ToList();
            bool clean = offered.Count > 0 && collisions.Count == 0;
            W($"assert 14a no owned relic offered: {clean} (want True) — {offered.Count} draw(s), owned {owned.Count}"
              + (collisions.Count > 0 ? $"  ★OFFERED WHILE OWNED: {string.Join(", ", collisions)}" : ""));

            // ★14a alone could pass by luck: it draws 18 times from deques holding 20-35 entries, so
            // missing every owned relic is entirely possible even with no filter at all. This checks the
            // MECHANISM directly — the filter must reject a relic the player is holding — so the pair is
            // not vacuous. (Twice this session an assert "passed" while proving nothing.)
            var sample = player.Relics.FirstOrDefault(r => r.Rarity != RelicRarity.None);
            var filter = RelicOfferFilterPatch.FilterForTest(player);
            bool rejects = sample != null && !filter(sample.CanonicalInstance ?? sample);
            W($"assert 14b filter rejects the owned relic {sample?.Id.Entry ?? "(none)"}: {rejects} (want True)");

            // And it must NOT reject something the player does not hold, or every draw would degrade.
            var unowned = ReshuffleService.TargetPoolForTest(player, RelicRarity.Rare)
                                         .FirstOrDefault(r => !owned.Contains(r.Id.Entry));
            bool accepts = unowned == null || filter(unowned);
            W($"assert 14c filter accepts the unowned relic {unowned?.Id.Entry ?? "(none)"}: {accepts} (want True)");

            return clean && rejects && accepts;
        }
        catch (Exception e) { W("assert 14 offer filter THREW: " + e.Message); return false; }
    }

    /// <summary>
    /// A relic the reshuffle takes away must become obtainable again — and a one-time reward relic must
    /// NOT.
    ///
    /// ★BOTH HALVES MATTER. Returning relics to the pool is the point (vanilla drops them forever, which
    /// only makes sense when obtaining means keeping). But returning a relic whose payload already paid
    /// out would let the player collect it, lose it to a reshuffle, buy it again and collect twice — an
    /// unbounded loop. So the test pins the carve-out as well as the feature.
    /// </summary>
    private static async Task<bool> CheckPoolReturn(Player player)
    {
        try
        {
            // A plain relic obtained the honest way: Obtain removes it from the pool.
            var proto = PickPlainRelic(player, RelicRarity.Uncommon);
            if (proto == null) { W("assert 15 pool return: SKIPPED (no plain Uncommon to grant)"); return true; }
            await RelicCmd.Obtain(proto.ToMutable(), player);
            await Task.Delay(200);
            var owned = player.Relics.FirstOrDefault(r => r.Id.Entry == proto.Id.Entry);
            if (owned == null) { W("assert 15 pool return: SKIPPED (grant did not land)"); return true; }

            bool? beforeIn = RelicPoolReturn.IsInPool(player, owned);
            if (beforeIn == null) { W("assert 15 pool return: SKIPPED (grab bag not reachable)"); return true; }
            W($"assert 15a obtained relic left the pool: {beforeIn == false} (want True) — {proto.Id.Entry}");

            // Take it away the way a reshuffle would, then it should be back.
            var target = ReshuffleService.TargetPoolForTest(player, RelicRarity.Uncommon)
                .FirstOrDefault(r => r.Id.Entry != proto.Id.Entry
                                  && player.Relics.All(o => o.Id.Entry != r.Id.Entry));
            if (target == null) { W("assert 15 pool return: SKIPPED (no swap target)"); return true; }
            ReshuffleService.ForceSwapForTest(player, owned, target);
            bool afterIn = RelicPoolReturn.IsInPool(player, proto) == true;
            W($"assert 15b swapped-away relic returned to the pool: {afterIn} (want True)");

            // A one-time reward relic returns like everything else — the exploit is closed at the payout
            // (assert 16), not by holding the relic out of the pool.
            var oneTime = FlatModels().OfType<RelicModel>()
                .FirstOrDefault(r => r.HasUponPickupEffect && r.Rarity == RelicRarity.Common);
            bool oneTimeOk = true;
            if (oneTime == null) W("assert 15c one-time return: SKIPPED (no Common pickup-effect relic)");
            else
            {
                // ★It must be OUT of the pool first, or this proves nothing: a pickup-effect relic that
                // was never obtained is already in the pool, so "is it in the pool?" would read true no
                // matter what TryReturn did. (First write of this check failed for exactly that reason.)
                player.RelicGrabBag.Remove(oneTime);
                bool clearedFirst = RelicPoolReturn.IsInPool(player, oneTime) == false;

                var live = oneTime.ToMutable();
                player.AddRelicInternal(live);                       // silent: its payload never fires
                RelicPoolReturn.TryReturn(player, live);
                bool backIn = RelicPoolReturn.IsInPool(player, oneTime) == true;
                oneTimeOk = clearedFirst && backIn;
                W($"assert 15c one-time reward relic returns to the pool ({oneTime.Id.Entry}): "
                  + $"clearedFirst={clearedFirst}, backIn={backIn} = {oneTimeOk} (want True)");
                player.RemoveRelicInternal(live);
            }

            // A pet relic still stays out — there is no "already spawned" state to read, so the pool is
            // the only place to stop it.
            var pet = FlatModels().OfType<RelicModel>().FirstOrDefault(r => r.SpawnsPets || r.AddsPet);
            bool petOk = true;
            if (pet == null) W("assert 15d pet carve-out: SKIPPED (no pet relic)");
            else
            {
                player.RelicGrabBag.Remove(pet);
                bool petCleared = RelicPoolReturn.IsInPool(player, pet) == false;
                RelicPoolReturn.TryReturn(player, pet.ToMutable());   // must be REFUSED
                bool petOut = RelicPoolReturn.IsInPool(player, pet) == false;
                petOk = petCleared && petOut;
                W($"assert 15d pet relic NOT returned ({pet.Id.Entry}): "
                  + $"clearedFirst={petCleared}, stillOut={petOut} = {petOk} (want True)");
            }

            return beforeIn == false && afterIn && oneTimeOk && petOk;
        }
        catch (Exception e) { W("assert 15 pool return THREW: " + e.Message); return false; }
    }

    /// <summary>
    /// Obtaining a one-time reward relic a second time must hand over nothing, and the relic must say so.
    ///
    /// ★THIS IS THE ONE THAT MAKES POOL RETURN SAFE. Assert 15c proves a spent Strawberry goes back into
    /// the draw pool; on its own that is an unbounded max-HP loop. The guard lives in RepeatPickupPatch,
    /// and nothing else in the battery would notice if it stopped working — the reshuffle would keep
    /// passing every other assert while quietly printing free max HP.
    ///
    /// Max HP is the measurement because it is the whole payload of Strawberry / Mango / Pear: an
    /// integer on the creature that moves if and only if the payout ran. No mocking, no reflection into
    /// the patch — the assert reads the same number the player would.
    /// </summary>
    private static async Task<bool> CheckRepeatPickup(Player player)
    {
        try
        {
            // ★A silent-targeting guard. TargetMethods finds payout methods reflectively, so a renamed
            // property in a game update would patch NOTHING and every other assert here would still pass
            // (the second payout simply runs and max HP moves — which 16d catches, but only if a relic
            // was found at all). Pin the count so the failure names itself.
            bool armed = RepeatPickupPatch.PatchedCount > 0;
            W($"assert 16a payout guard armed on {RepeatPickupPatch.PatchedCount} relic(s): {armed} (want True)");

            var proto = FlatModels().OfType<RelicModel>().FirstOrDefault(r =>
                r.HasUponPickupEffect &&
                (r.Id.Entry == "STRAWBERRY" || r.Id.Entry == "MANGO" || r.Id.Entry == "PEAR"));
            if (proto == null)
            {
                W("assert 16 repeat pickup: SKIPPED (no max-HP pickup relic in ModelDb)");
                return armed;
            }

            int hp0 = player.Creature.MaxHp;
            await RelicCmd.Obtain(proto.ToMutable(), player);
            await Task.Delay(300);
            int hp1 = player.Creature.MaxHp;

            SpentRewardLedger.InvalidateForTest();
            int picks1 = SpentRewardLedger.TimesPicked(player, proto.Id.Entry);
            bool firstPaid = hp1 > hp0 && picks1 >= 1;
            W($"assert 16b first pickup pays ({proto.Id.Entry}): maxHp {hp0}->{hp1}, "
              + $"historyPicks={picks1} = {firstPaid} (want True)");
            // picks1 == 0 means RelicCmd.Obtain wrote no history entry here, so the ledger has nothing to
            // read and the guard below cannot possibly engage — say so rather than let 16d look like a
            // suppression failure.
            if (picks1 == 0) W("  ^ no pick was recorded in the run history — the ledger is blind in this harness");

            // 16c: the copy that legitimately paid out is a normal relic and must NOT be marked.
            var held = player.Relics.FirstOrDefault(r => r.Id.Entry == proto.Id.Entry);
            if (held == null) { W("assert 16 repeat pickup: SKIPPED (first grant did not land)"); return armed; }
            string marker = SpentMarkerPrefix();
            bool quietWhenEarned = !(held.HoverTip.Description ?? "").StartsWith(marker, StringComparison.Ordinal);
            W($"assert 16c the copy that paid out is NOT marked spent: {quietWhenEarned} (want True)");

            // Take it away the way a reshuffle does, then buy it back.
            player.RemoveRelicInternal(held);
            RelicPoolReturn.TryReturn(player, held);
            await RelicCmd.Obtain(proto.ToMutable(), player);
            await Task.Delay(300);
            int hp2 = player.Creature.MaxHp;

            SpentRewardLedger.InvalidateForTest();
            int picks2 = SpentRewardLedger.TimesPicked(player, proto.Id.Entry);
            bool suppressed = hp2 == hp1;
            W($"assert 16d second pickup pays nothing: maxHp {hp1}->{hp2}, "
              + $"historyPicks={picks2} = {suppressed} (want True)");

            // 16e: and the inert copy now says so, in the game's own words.
            var again = player.Relics.FirstOrDefault(r => r.Id.Entry == proto.Id.Entry);
            string desc = again?.HoverTip.Description ?? "";
            bool marked = marker.Length > 0 && desc.StartsWith(marker, StringComparison.Ordinal);
            W($"assert 16e the re-obtained copy is marked spent: {marked} (want True) "
              + $"— tooltip gate: {SpentRewardTooltipPatch.LastSkip}");
            if (!marked)
            {
                // Escaped: the console this lands on is cp949 and eats the Korean outright, which would
                // hide the very difference being diagnosed.
                W($"  marker[{marker.Length}] = {Esc(marker)}");
                W($"  desc  [{desc.Length}] = {Esc(desc.Length > 80 ? desc.Substring(0, 80) : desc)}");
                W($"  contains-marker={desc.Contains(marker, StringComparison.Ordinal)}");
            }

            // ── 17. the payload relics the game does NOT flag ────────────────────────────────────
            // ★THE REGRESSION THIS EXISTS FOR. The guard originally keyed on HasUponPickupEffect, which
            // 47 relics set — but 33 more run a pickup payload without it. Gnarled Hammer (Shop rarity,
            // so genuinely stocked in the grab bag) enchants a card on pickup: buy, enchant, let the
            // reshuffle take it, buy again, enchant again. Every assert above passed while that was live.
            bool unflagged = await CheckUnflaggedPayload(player);

            return armed && firstPaid && quietWhenEarned && suppressed && marked && unflagged;
        }
        catch (Exception e) { W("assert 16 repeat pickup THREW: " + e.Message); return false; }
    }

    /// <summary>The red "all used up" line the tooltip patch prepends, rendered in whatever language the
    /// game is running. Derived from the same loc entry rather than hard-coded, so the assert does not
    /// quietly become English-only.</summary>
    /// <summary>
    /// A relic with a pickup payload but WITHOUT <c>HasUponPickupEffect</c> must not re-run it either.
    ///
    /// Signet Ring is the measurement because its entire payload is <c>PlayerCmd.GainGold(999)</c> — an
    /// integer that moves if and only if the payload ran, and no selection prompt to hang a headless run.
    /// Gnarled Hammer is the relic that actually motivated the fix (it is the only one of these in a
    /// rarity the grab bag stocks), so its coverage is pinned by name rather than inferred from a count.
    /// </summary>
    private static async Task<bool> CheckUnflaggedPayload(Player player)
    {
        try
        {
            var proto = FlatModels().OfType<RelicModel>()
                .FirstOrDefault(r => r.Id.Entry == "SIGNET_RING" && !r.HasUponPickupEffect);
            if (proto == null) { W("assert 17 unflagged payload: SKIPPED (SIGNET_RING not found unflagged)"); return true; }

            long g0 = player.Gold;
            await RelicCmd.Obtain(proto.ToMutable(), player);
            await Task.Delay(300);
            long g1 = player.Gold;
            bool firstPaid = g1 > g0;
            W($"assert 17a unflagged payload pays once ({proto.Id.Entry}): gold {g0}->{g1} = {firstPaid} (want True)");

            var held = player.Relics.FirstOrDefault(r => r.Id.Entry == proto.Id.Entry);
            if (held == null) { W("assert 17 unflagged payload: SKIPPED (grant did not land)"); return firstPaid; }
            player.RemoveRelicInternal(held);
            RelicPoolReturn.TryReturn(player, held);

            await RelicCmd.Obtain(proto.ToMutable(), player);
            await Task.Delay(300);
            long g2 = player.Gold;
            bool suppressed = g2 == g1;
            W($"assert 17b re-obtaining it pays nothing: gold {g1}->{g2} = {suppressed} (want True)");

            var hammer = FlatModels().OfType<RelicModel>().FirstOrDefault(r => r.Id.Entry == "GNARLED_HAMMER");
            bool covered = hammer == null || RepeatPickupPatch.CoversForTest(hammer);
            W($"assert 17c GNARLED_HAMMER (Shop rarity, the reachable one) is covered: {covered} (want True)"
              + (hammer == null ? " — SKIPPED, relic not in ModelDb" : ""));

            return firstPaid && suppressed && covered;
        }
        catch (Exception e) { W("assert 17 unflagged payload THREW: " + e.Message); return false; }
    }

    /// <summary>Render a string so a cp949 console cannot silently drop the part that matters.</summary>
    private static string Esc(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c < 32 || c > 126) sb.Append("\\u").Append(((int)c).ToString("X4"));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string SpentMarkerPrefix()
    {
        var probe = new LocString("gameplay_ui", "RELIC_USED_UP");
        // RELIC_USED_UP is a red line, then a newline, then {description}. Render it with a sentinel
        // and cut there — ★NOT with an empty string: the formatter substitutes an empty variable with
        // a literal U+0001 placeholder, so the "prefix" would carry a trailing character the real
        // tooltip never has, and StartsWith would report False for a tooltip that is exactly right.
        const string Sentinel = "@@DESC@@";
        probe.Add("description", Sentinel);
        string rendered = probe.GetFormattedText();
        int at = rendered.IndexOf(Sentinel, StringComparison.Ordinal);
        return at >= 0 ? rendered.Substring(0, at) : rendered;
    }

    private static List<string> Fingerprint(RunManager run)
        => run.State!.Players
              .Select(p => $"{p.NetId}=[{string.Join(",", p.Relics.Select(r => r.Id.Entry))}]")
              .ToList();

    /// <summary>
    /// Open the reshuffle log and prove it renders. This test never enters a real fight (it calls
    /// Reroll directly), so the panel has to be shown by hand — and it must be, because assert 1-8 would
    /// all pass with the UI completely broken. Checks three things a screenshot alone cannot: the panel
    /// built one row per swap, it is on screen, and nothing inside it paints outside it.
    ///
    /// ★THE OVERFLOW CHECK IS THE POINT. Relic icons are TextureRects, and Godot clamps a TextureRect to
    /// its source texture's size when ExpandMode is assigned after CustomMinimumSize — the icons then
    /// render huge and spill over the panel while every Control.Size still reads correct. Comparing the
    /// panel's own rect to the union of its descendants' rects is what catches that.
    /// </summary>
    private static async Task<bool> CheckPanel(List<ReshuffleService.Swap> swaps)
    {
        try
        {
            // Seed the log the way a real fight would, then open the panel from code (the top-bar
            // button only exists inside a run's UI, and the test drives the panel, not the button).
            ReshuffleHistory.ResetForTest();
            ReshuffleHistory.Record(RunManager.Instance!.State!.TotalFloor,
                RunManager.Instance!.State!.Players.First().NetId, swaps);

            if (NReshuffleSummaryPanel.IsOpen) NReshuffleSummaryPanel.Close();
            NReshuffleSummaryPanel.Toggle();
            await Task.Delay(900);   // let the layout resolve

            var frame = NReshuffleSummaryPanel.FrameForTest;
            if (frame == null) { W("assert 9 panel: FAIL — panel frame not built"); return false; }

            var texts = NReshuffleSummaryPanel.TextsForTest();
            int rows = 0;
            foreach (var s2 in swaps) if (texts.Contains(s2.To.Title?.GetFormattedText() ?? "")) rows++;
            bool rowsOk = rows == swaps.Count;
            W($"assert 9a panel lists {rows} of {swaps.Count} swap(s) = {rowsOk} (want True)");

            bool visible = NReshuffleSummaryPanel.IsOpen && frame.Visible;
            W($"assert 9b panel open and visible: {visible} (want True)");

            Rect2 panel = frame.GetGlobalRect();
            Rect2 painted = NReshuffleSummaryPanel.RenderedRectForTest();
            // Allow a pixel of rounding slack; anything larger means a child escaped the container.
            bool contained = panel.Size.X > 0 && panel.Size.Y > 0
                          && painted.Position.X >= panel.Position.X - 1 && painted.Position.Y >= panel.Position.Y - 1
                          && painted.End.X <= panel.End.X + 1 && painted.End.Y <= panel.End.Y + 1;
            W($"assert 9c nothing overflows the panel: panel={panel}, painted={painted} = {contained} (want True)");

            // ★The text has to be READABLE, not merely present. The first build showed
            // "LocString table relics entry AKABEKO.title" on every row — LocString.ToString() is a debug
            // description, and 9a/9b/9c all passed anyway because the layout was fine. A key that leaked
            // to the screen always contains "LocString" or the raw UPPER_SNAKE entry, so check for both.
            var bad = texts.Where(t => string.IsNullOrWhiteSpace(t)
                                    || t.Contains("LocString", StringComparison.Ordinal)
                                    || t.Contains(".title", StringComparison.Ordinal)).ToList();
            bool readable = texts.Count > 0 && bad.Count == 0;
            W($"assert 9d relic names resolved: {readable} (want True) — [{string.Join(" | ", texts)}]"
              + (bad.Count > 0 ? $" ★unresolved: {string.Join(", ", bad)}" : ""));

            await Shot("3_panel");   // visual evidence the log renders with real icons and names
            bool okAll = rowsOk && visible && contained && readable;
            NReshuffleSummaryPanel.Close();
            return okAll;
        }
        catch (Exception e) { W("assert 9 panel THREW: " + e.Message); return false; }
    }

    /// <summary>Rebuild the given relic inventory exactly (same ids, same order). False = could not, and
    /// the caller should skip rather than report a failure it didn't actually measure.</summary>
    private static async Task<bool> Restore(Player player, List<(string entry, RelicRarity rarity)> target)
    {
        foreach (var r in player.Relics.ToList())
            player.RemoveRelicInternal(r);
        foreach (var (entry, _) in target)
        {
            var proto = FlatRelic(entry);
            if (proto == null) { W($"assert 8 determinism: SKIPPED (cannot re-create {entry})"); return false; }
            player.AddRelicInternal(proto.ToMutable());
        }
        await Task.Delay(200);
        return true;
    }

    /// <summary>Give the player a stackable relic and bump it to 3, so assert 7 tests a real accumulated
    /// stack rather than the trivial count-of-1 case.</summary>
    private static async Task<RelicModel?> GrantStack(Player player)
    {
        try
        {
            var proto = FlatModels().OfType<RelicModel>()
                .FirstOrDefault(r => r.IsStackable && !r.HasUponPickupEffect);
            if (proto == null) { W("note: no stackable relic found in ModelDb"); return null; }

            var granted = proto.ToMutable();
            await RelicCmd.Obtain(granted, player);
            await Task.Delay(200);
            granted.IncrementStackCount();
            granted.IncrementStackCount();
            W($"granted stackable {granted.Id.Entry} at stack {granted.StackCount}");
            return granted;
        }
        catch (Exception e) { W("stackable grant failed: " + e.Message); return null; }
    }

    /// <summary>A relic of <paramref name="rarity"/> from this player's own pool that can be granted
    /// without any pickup effect or prompt — the test wants inventory, not a reward screen.</summary>
    private static RelicModel? PickPlainRelic(Player player, RelicRarity rarity)
    {
        try
        {
            var owned = new HashSet<string>(player.Relics.Select(r => r.Id.Entry), StringComparer.Ordinal);
            return FlatModels().OfType<RelicModel>()
                .Where(r => r.Rarity == rarity
                         && !r.HasUponPickupEffect && !r.SpawnsPets && !r.AddsPet && !r.IsStackable
                         && !owned.Contains(r.Id.Entry))
                .OrderBy(r => r.Id.Entry, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static List<(string entry, RelicRarity rarity)> Snapshot(Player player)
        => player.Relics.Select(r => (r.Id.Entry, r.Rarity)).ToList();

    private static string RarityHistogram(List<(string entry, RelicRarity rarity)> relics)
        => string.Join("/", relics.GroupBy(r => r.rarity)
                                  .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal)
                                  .Select(g => $"{g.Key}:{g.Count()}"));

    private static string Describe(Player player)
        => string.Join(", ", player.Relics.Select(r => $"{r.Id.Entry}({r.Rarity}{(r.IsStackable ? $" x{r.StackCount}" : "")})"));

    /// <summary>A CharacterModel to start the run with. ModelDb.AllCharacters is a fixed Character&lt;T&gt;()
    /// array, so ANY mod that de-registers or fails to build one of the base characters makes it throw
    /// KeyNotFoundException — exactly what a real modded install does. Fall back to a flat scan.</summary>
    private static CharacterModel? ResolveCharacter()
    {
        try
        {
            var c = ModelDb.AllCharacters.FirstOrDefault();
            if (c != null) { W("character via AllCharacters: " + c.Id.Entry); return c; }
        }
        catch (Exception e) { W($"AllCharacters THREW ({e.GetType().Name}: {e.Message}) — falling back to a flat scan."); }

        foreach (var m in FlatModels())
            if (m is CharacterModel cm) { W("character via flat scan: " + cm.Id.Entry); return cm; }
        return null;
    }

    /// <summary>A relic prototype by entry, WITHOUT touching ModelDb.AllRelics (pool-derived, and it
    /// throws on the same broken installs — see <see cref="ResolveCharacter"/>).</summary>
    private static RelicModel? FlatRelic(string entry)
    {
        foreach (var m in FlatModels())
            if (m is RelicModel r && string.Equals(r.Id.Entry, entry, StringComparison.OrdinalIgnoreCase))
                return r;
        return null;
    }

    private static System.Collections.IDictionary? ContentById()
    {
        try
        {
            var f = typeof(ModelDb).GetField("_contentById", BindingFlags.NonPublic | BindingFlags.Static);
            return f?.GetValue(null) as System.Collections.IDictionary;
        }
        catch { return null; }
    }

    private static int FlatCount() => ContentById()?.Count ?? 0;

    /// <summary>Depth-first search for the first node of type T — used to gate the run start on the main
    /// menu actually existing.</summary>
    private static T? FindNode<T>(Node n) where T : class
    {
        if (n is T t) return t;
        foreach (var c in n.GetChildren())
        {
            var r = FindNode<T>(c);
            if (r != null) return r;
        }
        return null;
    }

    private static IEnumerable<object> FlatModels()
    {
        var d = ContentById();
        if (d == null) { W("flat model scan: ModelDb._contentById NOT FOUND (field renamed?)"); yield break; }
        if (!_dumped)
        {
            _dumped = true;
            int chars = 0, relics = 0;
            foreach (var v in d.Values) { if (v is CharacterModel) chars++; else if (v is RelicModel) relics++; }
            W($"flat model scan: {d.Count} models registered (CharacterModel={chars}, RelicModel={relics})");
        }
        foreach (var v in d.Values) yield return v;
    }

    private static async Task Shot(string name)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return;
            await Task.Delay(120);
            var img = tree.Root.GetTexture()?.GetImage();
            if (img == null) { W($"shot {name}: null image"); return; }
            string p = Path.Combine(ModDir(), $"selftest.sp.{name}.png");
            var err = img.SavePng(p);
            W($"shot {name}: {(err == Error.Ok ? $"saved {img.GetWidth()}x{img.GetHeight()} -> {Path.GetFileName(p)}" : "err " + err)}");
        }
        catch (Exception e) { W($"shot {name} failed: {e.Message}"); }
    }

    private static void W(string line)
    {
        _out.AppendLine(line);
        MainFile.Logger.Info($"[{MainFile.ModId}] SOLO | {line}");
    }

    private static void Flush(bool ok)
    {
        _done = true;
        _out.Insert(0, (ok ? "RESULT: OK\n" : "RESULT: FAIL\n"));
        try { File.WriteAllText(Path.Combine(ModDir(), "selftest.sp.txt"), _out.ToString()); } catch { }
    }
}
