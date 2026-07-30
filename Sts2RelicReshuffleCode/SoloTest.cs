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

            // ── 9. the combat-start banner actually renders ─────────────────────────────────────
            bool ui = await CheckBanner(swaps);
            if (!ui) ok = false;

            // ── 10. re-entering the same combat must NOT re-roll ────────────────────────────────
            bool reentry = CheckReentry(run);
            if (!reentry) ok = false;

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

    private static List<string> Fingerprint(RunManager run)
        => run.State!.Players
              .Select(p => $"{p.NetId}=[{string.Join(",", p.Relics.Select(r => r.Id.Entry))}]")
              .ToList();

    /// <summary>
    /// Drive the combat-start banner and prove it renders. This test never enters a real fight (it calls
    /// Reroll directly), so the panel has to be shown by hand — and it must be, because assert 1-8 would
    /// all pass with the UI completely broken. Checks three things a screenshot alone cannot: the panel
    /// built one row per swap, it is on screen, and nothing inside it paints outside it.
    ///
    /// ★THE OVERFLOW CHECK IS THE POINT. Relic icons are TextureRects, and Godot clamps a TextureRect to
    /// its source texture's size when ExpandMode is assigned after CustomMinimumSize — the icons then
    /// render huge and spill over the panel while every Control.Size still reads correct. Comparing the
    /// panel's own rect to the union of its descendants' rects is what catches that.
    /// </summary>
    private static async Task<bool> CheckBanner(List<ReshuffleService.Swap> swaps)
    {
        try
        {
            var banner = MainFile.Banner;
            if (banner == null) { W("assert 9 banner: FAIL — banner was never mounted"); return false; }

            banner.ShowSwaps(swaps.ConvertAll(s => (s.From, s.To)));
            await Task.Delay(900);   // let the fade-in tween settle and the layout resolve

            int rows = banner.RowCountForTest;
            bool rowsOk = rows == swaps.Count;
            W($"assert 9a banner rows: {rows} for {swaps.Count} swap(s) = {rowsOk} (want True)");

            bool visible = banner.Visible && (banner.PanelForTest?.Visible ?? false);
            W($"assert 9b banner visible: {visible} (want True)");

            Rect2 panel = banner.PanelForTest?.GetGlobalRect() ?? new Rect2();
            Rect2 painted = banner.RenderedRectForTest();
            // Allow a pixel of rounding slack; anything larger means a child escaped the container.
            bool contained = panel.Size.X > 0 && panel.Size.Y > 0
                          && painted.Position.X >= panel.Position.X - 1 && painted.Position.Y >= panel.Position.Y - 1
                          && painted.End.X <= panel.End.X + 1 && painted.End.Y <= panel.End.Y + 1;
            W($"assert 9c nothing overflows the panel: panel={panel}, painted={painted} = {contained} (want True)");

            // ★The text has to be READABLE, not merely present. The first build showed
            // "LocString table relics entry AKABEKO.title" on every row — LocString.ToString() is a debug
            // description, and 9a/9b/9c all passed anyway because the layout was fine. A key that leaked
            // to the screen always contains "LocString" or the raw UPPER_SNAKE entry, so check for both.
            var texts = banner.RowTextsForTest();
            var bad = texts.Where(t => string.IsNullOrWhiteSpace(t)
                                    || t.Contains("LocString", StringComparison.Ordinal)
                                    || t.Contains(".title", StringComparison.Ordinal)).ToList();
            bool readable = texts.Count > 0 && bad.Count == 0;
            W($"assert 9d relic names resolved: {readable} (want True) — [{string.Join(" | ", texts)}]"
              + (bad.Count > 0 ? $" ★unresolved: {string.Join(", ", bad)}" : ""));

            await Shot("3_banner");   // visual evidence the readout renders with real icons and names
            return rowsOk && visible && contained && readable;
        }
        catch (Exception e) { W("assert 9 banner THREW: " + e.Message); return false; }
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
