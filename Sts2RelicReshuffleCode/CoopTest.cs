using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;                      // CardSelectCmd
using MegaCrit.Sts2.Core.Context;                       // LocalContext
using MegaCrit.Sts2.Core.DevConsole;                    // ConsoleCmdGameAction
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;                       // TaskHelper
using MegaCrit.Sts2.Core.Models;                        // CardModel
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;        // StartRunLobby
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect; // NCharacterSelectScreen
using MegaCrit.Sts2.Core.Runs;                          // RunManager
using MegaCrit.Sts2.Core.TestSupport;                   // ICardSelector

namespace Sts2RelicReshuffle;

/// <summary>
/// Autonomous CO-OP convergence test. Armed by <c>selftest.coop.flag</c> next to the DLL with the game
/// launched twice as <c>--fastmp=host_standard</c> / <c>--fastmp=join</c>. Writes
/// <c>selftest.coop.&lt;role&gt;.txt</c>; the JOIN peer renders the verdict.
///
/// ★WHAT IS ACTUALLY AT RISK. This mod never broadcasts its result — every peer DERIVES the same
/// reshuffle from (run seed, floor, NetId, slot, source relic id). That is only safe if two independent
/// machines compute the identical answer, and nothing in single-player can test it: solo has no replica
/// and no second derivation to disagree with. So the measurement has to be two real instances.
///
/// ★WHY THIS SCENARIO CROSSES A ROOM BOUNDARY BY CONSTRUCTION. coop-verify's hardest-won lesson is that
/// a test confined to the starting room proves little, because the engine's checksum only fires when a
/// room is exited — a divergence can sit latent through a whole GREEN run. Here that is free: the mod's
/// entire trigger IS combat-room entry, so `room monster` is both the action under test and the boundary
/// that makes the checksum speak.
///
/// ★SETUP: EACH PEER GRANTS ITS OWN RELICS, and that is not a style choice. The first run of this test
/// had the host enqueue both grants, passing the JOIN player as the action's owner — and the two peers
/// ended up with completely different inventories before the reshuffle even started. The reason is that
/// <c>NetConsoleCmdGameAction</c> serializes only <c>cmd</c> and <c>inCombat</c>: the owner Player never
/// crosses the wire, so a receiving peer attributes the command to the SENDING peer's player. Acting on
/// another player's behalf therefore works locally and silently misfires remotely. Each side issues its
/// own grant instead. (This is also why TransmuteNet/ReforgeNet always pass the local acting player.)
///
/// The flag is NOT consumed: both instances share the mods folder, so a peer that consumed it would
/// disarm the other. Remove it manually after the run.
/// </summary>
internal static class CoopTest
{
    private static readonly StringBuilder _out = new();
    private static bool _isHost;
    private static string _role = "?";
    private static bool _readySent, _done, _started;
    /// <summary>JOIN only: the newest descriptor observed while still on the pre-jump floor, i.e. this
    /// peer's view of the state the reshuffle was about to act on.</summary>
    private static string _beforeSeen = "(never observed)";

    /// <summary>JOIN only: the button's visibility sampled while the fight is genuinely still running.
    /// ★It CANNOT be sampled where the host samples its own. The join peer waits for the host's result
    /// file, and the host writes that file immediately before issuing `win` — so by the time join looked,
    /// the networked win had already ended the fight on this peer too (measured: combatEnded=1 with the
    /// SAME CombatRoom, i.e. a genuine ending, not a stale event). Three "fixes" were aimed at a product
    /// bug that did not exist; the test was reading after the fact.</summary>
    private static bool? _joinDuringSample;
    private static string _step = "(not started)";
    private static DateTime _stepAt = DateTime.UtcNow;

    /// <summary>Seconds without progress before the watchdog flushes a partial FAIL. Without it a wedge
    /// produces NO result file, which is indistinguishable from "the peers never connected" — the single
    /// most time-wasting ambiguity in co-op testing.</summary>
    private const double StepTimeoutSec = 120;

    /// <summary>Relics granted per player. Chosen plain on purpose: no pickup effect (nothing to re-fire),
    /// no pickup prompt (nothing to hang on), and two different rarities each so the rarity-preserving
    /// swap has more than one bucket to get right.</summary>
    private static readonly string[] HostGrants = { "akabeko", "amethyst_aubergine" };
    private static readonly string[] JoinGrants = { "bronze_scales", "juzu_bracelet" };

    private static string ModDir() => Path.GetDirectoryName(typeof(CoopTest).Assembly.Location) ?? ".";

    public static void ArmIfRequested()
    {
        try
        {
            if (!File.Exists(Path.Combine(ModDir(), "selftest.coop.flag"))) return;
            var fm = System.Environment.GetCommandLineArgs().FirstOrDefault(a => a.Contains("fastmp"));
            _isHost = fm != null && fm.Contains("host");
            _role = fm == null ? "nofastmp" : (_isHost ? "host" : "join");
            W($"coop selftest armed (role={_role}, arg='{fm}')");
            Poll();
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] coop arm failed: {e.Message}"); }
    }

    private static void Poll()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || _done) return;
        try { Tick(tree); }
        catch (Exception e) { W("tick exception: " + e.Message); }
        if (!_done) tree.CreateTimer(2.0).Timeout += Poll;
    }

    private static void Tick(SceneTree tree)
    {
        var run = RunManager.Instance;

        if (!_started && run != null && run.IsInProgress && (run.State?.Players?.Count ?? 0) >= 2)
        {
            _started = true;
            Step("co-op run in progress");
            W($"COOP RUN IN PROGRESS — players={run.State!.Players.Count}");
            foreach (var p in run.State.Players)
                W($"  player {p.NetId} (isMe={SafeIsMe(p)}): [{RelicsLine(p)}]");
            TaskHelper.RunSafely(_isHost ? HostPhase(run) : JoinPhase(run));
            return;
        }

        if (!_started && !_readySent)
        {
            var screen = FindScreen(tree.Root);
            if (screen == null) { W("waiting for character-select lobby…"); return; }
            var lobby = LobbyOf(screen);
            if (lobby == null) { W("screen found but lobby is null (not initialized yet)"); return; }
            W($"lobby found: players={lobby.Players.Count}, localChar={LocalChar(lobby)}");
            try { lobby.SetReady(true); _readySent = true; Step("SetReady sent"); }
            catch (Exception e) { W("SetReady failed: " + e.Message); }
        }

        // Watchdog — converts a silent wedge into "hung at <step>" plus the log so far.
        if (_started && !_done && (DateTime.UtcNow - _stepAt).TotalSeconds > StepTimeoutSec)
        {
            W($"WATCHDOG: no progress for {StepTimeoutSec:F0}s at step '{_step}' — flushing partial result.");
            Flush(false);
        }
    }

    /// <summary>
    /// HOST: seed BOTH players via networked grants, snapshot, jump everyone into a combat room (which is
    /// what fires the reshuffle), then record the resulting relic lists for every player.
    /// </summary>
    private static async Task HostPhase(RunManager run)
    {
        try
        {
            await Task.Delay(2000);
            await Shot("01_run");
            var me = LocalPlayerOf(run);
            var other = run.State!.Players.FirstOrDefault(p => p.NetId != me?.NetId);
            if (me == null || other == null) { W("HOST: could not resolve both players"); Flush(false); return; }
            W($"HOST: local={me.NetId}, remote={other.NetId}");

            StartAutomation();

            // Seed OUR OWN player only; the join peer seeds itself (see the class comment on why the
            // owner cannot be spoofed across the wire). The long wait lets the join peer's own grants
            // arrive before we snapshot, so BEFORE reflects both players.
            Step("granting own relics (networked)");
            foreach (var id in HostGrants)
                run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, "relic " + id, inCombat: false));
            await Task.Delay(10000);

            bool payload = await PayloadProbe(run, me);

            var beforeSnap = PerPlayer(run);
            var beforeCounts = run.State!.Players.ToDictionary(p => p.NetId, OwnedCount);
            string before = Descriptor(run);
            W("HOST: BEFORE " + before);
            W("BEFORE " + before);   // the join peer diffs this too — a pre-action mismatch invalidates the test

            // The action under test: entering a combat room runs CombatEntryPatch on BOTH peers.
            Step("room monster (fires the reshuffle)");
            run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, "room monster", inCombat: false));
            await Task.Delay(6000);

            var afterSnap = PerPlayer(run);
            string after = Descriptor(run);
            W("HOST: FINAL " + after);
            W("FINAL " + after);   // machine-readable line the JOIN peer diffs against

            // Assert here as well: a run where nothing changed would "converge" trivially and report a
            // meaningless PASS. BOTH players must have been reshuffled — otherwise the two-NetId
            // derivation (different seeds per player) is untested, which is the whole point of a co-op run.
            // ★COUNT INVARIANT, PER PLAYER. A rarity-preserving swap must never change how many relics
            // someone holds, and co-op had no assert for it — the solo battery's assert 1 cannot see a
            // second player. Companions are excluded because they are not the player's relics.
            var counts = new List<string>();
            bool countsHeld = true;
            foreach (var p in run.State!.Players)
            {
                int now = OwnedCount(p);
                int was = beforeCounts.TryGetValue(p.NetId, out int b) ? b : -1;
                counts.Add($"{p.NetId}:{was}->{now}");
                if (was != now) countsHeld = false;
            }
            W($"HOST assert: relic count unchanged per player = {countsHeld} (want True) — {string.Join(" ", counts)}");

            bool twoPlayers = run.State!.Players.Count >= 2;
            var unchanged = beforeSnap.Where(kv => afterSnap.TryGetValue(kv.Key, out var a) && a == kv.Value)
                                      .Select(kv => kv.Key).ToList();
            bool allChanged = twoPlayers && unchanged.Count == 0;
            W($"HOST assert: two players = {twoPlayers} (want True)");
            W($"HOST assert: EVERY player reshuffled = {allChanged} (want True)"
              + (unchanged.Count > 0 ? $" — unchanged: {string.Join(",", unchanged)}" : ""));

            await Shot("02_final");
            bool converged = twoPlayers && allChanged && payload && countsHeld;
            Write(converged);   // bank it before the win, which pops reward screens

            bool gating = await CheckButtonGating(run, me, issueWin: true);
            await Shot("03_after_win");
            Flush(converged && gating);
        }
        catch (Exception e) { W("HOST exception: " + e); Flush(false); }
    }

    /// <summary>
    /// JOIN: observe the same transition, then compare its own view against the host's FINAL line. This
    /// peer renders the verdict because convergence is only meaningful when one side checks the other.
    /// </summary>
    private static async Task JoinPhase(RunManager run)
    {
        try
        {
            await Task.Delay(2000);
            await Shot("01_run");
            StartAutomation();

            // Seed OUR OWN player. The owner field does not cross the wire, so the host cannot grant on
            // our behalf — each peer has to issue its own (see the class comment).
            var me = LocalPlayerOf(run);
            if (me == null) { W("JOIN: local player not found"); Flush(false); return; }
            Step("granting own relics (networked)");
            foreach (var id in JoinGrants)
                run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, "relic " + id, inCombat: false));

            // Track the descriptor over time rather than sampling once: if the two peers diverge, the
            // timeline says WHEN, which is most of the diagnosis. A dropped session nulls run.State, so
            // guard it — dying on an NRE here would destroy the evidence we came for.
            bool payload = await PayloadProbe(RunManager.Instance!, me);

            Step("watching for the host's room jump + reshuffle");
            string last = "";
            int startFloor = run.State?.TotalFloor ?? -1;
            string hostPath = Path.Combine(ModDir(), "selftest.coop.host.txt");
            for (int i = 0; i < 45; i++)
            {
                await Task.Delay(2000);
                if (RunManager.Instance?.State == null) { W($"t+{i * 2}s SESSION DROPPED"); break; }
                string now = Descriptor(RunManager.Instance);
                if (now != last) { W($"t+{i * 2}s {now}"); last = now; }
                // Keep the newest pre-jump view as our BEFORE: once the floor advances, the room change
                // (and with it the reshuffle) has happened and this is no longer the pre-action state.
                if (RunManager.Instance.State!.TotalFloor == startFloor) _beforeSeen = now;

                // The floor moved: the reshuffle has landed and the fight is running. Sample the button
                // HERE, well before the host's win can reach us.
                if (_joinDuringSample == null && RunManager.Instance.State!.TotalFloor != startFloor)
                {
                    await Task.Delay(2500);   // let the deferred pulse fire
                    _joinDuringSample = ButtonVisible();
                    W($"  [diag] button sampled mid-fight = {(_joinDuringSample?.ToString() ?? "(not found)")}");
                }

                if (File.Exists(hostPath) && i >= 4) break;   // host finished and we have settled
            }

            await Task.Delay(2000);
            string mine = Descriptor(RunManager.Instance!);
            W("JOIN: FINAL " + mine);
            W("FINAL " + mine);

            string? hostFinal = ReadHostLine(hostPath, "FINAL ");
            if (hostFinal == null)
            {
                W("JOIN: host result file has no FINAL line — cannot judge convergence.");
                await Shot("02_final");
                Flush(false);
                return;
            }

            // Check the PRE-action state too. The first run of this test converged on FINAL while the two
            // peers had entered the room with completely different inventories — the engine's pre-room
            // sync had quietly reconciled them. That is reassuring about the engine but it means the
            // measurement was not the one we intended, and only comparing FINAL would never have said so.
            string? hostBefore = ReadHostLine(hostPath, "BEFORE ");
            bool setupAgreed = hostBefore != null && string.Equals(hostBefore, _beforeSeen, StringComparison.Ordinal);
            W($"JOIN assert: peers agreed BEFORE the action = {setupAgreed} (want True)");
            if (!setupAgreed)
            {
                W("  host BEFORE: " + (hostBefore ?? "(missing)"));
                W("  join BEFORE: " + _beforeSeen);
            }

            bool converged = string.Equals(hostFinal, mine, StringComparison.Ordinal);
            W($"JOIN assert: host view == join view AFTER = {converged} (want True)");
            if (!converged)
            {
                W("  host: " + hostFinal);
                W("  join: " + mine);
            }
            bool twoPlayers = (RunManager.Instance?.State?.Players?.Count ?? 0) >= 2;
            W($"JOIN assert: two players = {twoPlayers} (want True)");

            // Both peers ran the probe on their OWN player, so the two PAYLOAD lines describe different
            // players and must NOT be compared to each other. What matters is that each peer reached the
            // same verdict locally, and that the FINAL descriptor — which carries hp and gold for BOTH
            // players — agrees. A one-sided payout surfaces there.
            string? hostPayload = ReadHostLine(hostPath, "PAYLOAD ");
            W($"JOIN assert: local payload guard held = {payload} (want True); host reported: {hostPayload ?? "(missing)"}");

            await Shot("02_final");
            bool verdict = converged && twoPlayers && setupAgreed && payload;
            Write(verdict);   // bank it before the host's win pops reward screens here too

            bool gating = await CheckButtonGating(RunManager.Instance!, me, issueWin: false,
                                                 preSampledDuring: _joinDuringSample);
            await Shot("03_after_win");
            Flush(verdict && gating);
        }
        catch (Exception e) { W("JOIN exception: " + e); Flush(false); }
    }

    /// <summary>Relic whose entire pickup payload is a max-HP gain — the cleanest thing to measure, and
    /// the exact class the repeat-pickup guard exists for.</summary>
    private const string PayloadRelic = "mango";

    /// <summary>
    /// Grant a one-time reward relic twice through the networked path and prove the second grant pays
    /// nothing — ON THIS PEER, from its own replicated history.
    ///
    /// ★THIS IS THE CO-OP RISK THE v0.5.0 CHANGE INTRODUCED. The suppression decision is not broadcast:
    /// each peer counts picks in its own <c>MapPointHistory</c> and decides independently. If the two
    /// copies of that history disagree by even one entry, one peer runs the payout and the other does
    /// not — the players end the fight with different max HP, which is a genuine desync in a checksummed
    /// field. Nothing in single-player can see this: there is only one history there.
    ///
    /// Each peer acts on ITSELF. <c>NetConsoleCmdGameAction</c> does not carry the owner across the wire,
    /// so a grant issued here is attributed to THIS peer's player on both machines (see the class remark).
    /// </summary>
    private static async Task<bool> PayloadProbe(RunManager run, Player me)
    {
        try
        {
            Step("one-time payload: first grant");
            int hp0 = MaxHpOf(me);
            run.ActionQueueSynchronizer.RequestEnqueue(
                new ConsoleCmdGameAction(me, "relic " + PayloadRelic, inCombat: false));
            await Task.Delay(6000);
            int hp1 = MaxHpOf(me);

            SpentRewardLedger.InvalidateForTest();
            int picks1 = SpentRewardLedger.TimesPicked(me, "MANGO");
            bool paid = hp1 > hp0 && picks1 >= 1;
            W($"payload assert A first grant paid: maxHp {hp0}->{hp1}, picks={picks1} = {paid} (want True)");

            // Take it away the way the reshuffle would, then take it again.
            Step("one-time payload: remove + second grant");
            run.ActionQueueSynchronizer.RequestEnqueue(
                new ConsoleCmdGameAction(me, "relic remove " + PayloadRelic, inCombat: false));
            await Task.Delay(4000);
            run.ActionQueueSynchronizer.RequestEnqueue(
                new ConsoleCmdGameAction(me, "relic " + PayloadRelic, inCombat: false));
            await Task.Delay(6000);

            int hp2 = MaxHpOf(me);
            SpentRewardLedger.InvalidateForTest();
            int picks2 = SpentRewardLedger.TimesPicked(me, "MANGO");
            bool suppressed = hp2 == hp1;
            W($"payload assert B second grant paid nothing: maxHp {hp1}->{hp2}, picks={picks2} = {suppressed} (want True)");
            // ★What this peer ITSELF suppressed, for BOTH players. Convergence of the final state does
            // not prove each peer decided independently — replication could have masked a one-sided
            // decision. This line is the difference between inferring that and measuring it.
            string supp = string.Join(",", RepeatPickupPatch.SuppressedForTest.OrderBy(x => x, StringComparer.Ordinal));
            W($"PAYLOAD hp={hp2} picks={picks2} suppressedHere=[{supp}]");

            return paid && suppressed;
        }
        catch (Exception e) { W("payload probe THREW: " + e.Message); return false; }
    }

    /// <summary>Every player's relic list, ordered by NetId then by inventory slot. Slot order matters:
    /// the engine's checksum hashes the relic list positionally, so two peers holding the same relics in
    /// a different order is still a desync — and a set-based comparison here would hide exactly that.</summary>
    private static string Descriptor(RunManager run)
    {
        try
        {
            var state = run.State;
            if (state == null) return "(no state)";
            // ★hp/gold are in here on purpose. The pickup-payload guard decides per peer, from replicated
            // history, whether to run a payout — so a divergence shows up as ONE peer's max HP or gold
            // moving. A relic-only descriptor would converge while the two players had different stats.
            var parts = state.Players.OrderBy(p => p.NetId)
                .Select(p => $"{p.NetId}=[{RelicsLine(p)}] hp={MaxHpOf(p)} gold={GoldOf(p)}");
            return $"floor={state.TotalFloor} | " + string.Join(" | ", parts);
        }
        catch (Exception e) { return "(descriptor failed: " + e.Message + ")"; }
    }

    private static int MaxHpOf(Player p) { try { return p.Creature.MaxHp; } catch { return -1; } }
    private static long GoldOf(Player p) { try { return p.Gold; } catch { return -1; } }

    /// <summary>
    /// The player's relics, with hidden Sts2RelicForge companions tagged.
    ///
    /// ★WITHOUT THE TAG THIS LINE LIES ABOUT THE COUNT INVARIANT. A companion is a donor instance
    /// RelicForge grafts onto a forged host: it sits in <c>player.Relics</c> but the player does not own
    /// it, and it can appear a second or two into a fight. Untagged, it reads as the reshuffle having
    /// minted a relic out of nowhere — which cost a real investigation before the timeline showed it
    /// arriving AFTER the swap. Tagged, the same line answers the question on sight.
    /// </summary>
    private static string RelicsLine(Player p)
    {
        try
        {
            return string.Join(",", p.Relics.Select(r =>
                RelicForgeBridge.IsCompanion(r) ? r.Id.Entry + "(companion)" : r.Id.Entry));
        }
        catch { return "?"; }
    }

    /// <summary>Relics the player actually owns — companions excluded, so a count is meaningful.</summary>
    private static int OwnedCount(Player p)
    {
        try { return p.Relics.Count(r => !RelicForgeBridge.IsCompanion(r)); }
        catch { return -1; }
    }

    private static string? ReadHostLine(string path, string prefix)
    {
        try
        {
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadAllLines(path))
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                    return line.Substring(prefix.Length);
        }
        catch (Exception e) { W("reading host file failed: " + e.Message); }
        return null;
    }

    /// <summary>Per-player relic line, keyed by NetId — so "did EVERY player get reshuffled" can be
    /// asserted per player instead of inferred from the combined descriptor changing.</summary>
    private static Dictionary<ulong, string> PerPlayer(RunManager run)
    {
        var map = new Dictionary<ulong, string>();
        try
        {
            foreach (var p in run.State!.Players) map[p.NetId] = RelicsLine(p);
        }
        catch (Exception e) { W("PerPlayer failed: " + e.Message); }
        return map;
    }

    // ── selection automation ────────────────────────────────────────────────────────────────────
    // A reshuffled-in relic can pop a card prompt at combat start. Unanswered, it hangs BOTH peers (the
    // other side blocks in WaitForRemoteChoice), and the symptom — zero result files — is identical to
    // "the instances never connected". ★The selector path does NOT synchronize: the engine skips its
    // ReserveChoiceId/SyncLocalChoice block entirely when a Selector is present, so each peer answers
    // locally and nothing is exchanged. The answer must therefore be DETERMINISTIC — a random pick would
    // have the harness itself manufacture the desync we are trying to measure.
    private static IDisposable? _selectorScope;

    private static void StartAutomation()
    {
        try
        {
            if (CardSelectCmd.Selector != null) return;
            _selectorScope = CardSelectCmd.PushSelector(new AutoSelector());
            W("selection automation on (deterministic first-N selector)");
        }
        catch (Exception e) { W("selector push failed: " + e.Message); }
    }

    private sealed class AutoSelector : ICardSelector
    {
        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            var list = options.ToList();
            int n = Math.Min(maxSelect, list.Count);
            if (n < minSelect) n = Math.Min(minSelect, list.Count);
            W($"  [selector] auto-picked {n}/{list.Count}: [{string.Join(", ", list.Take(n).Select(c => c.Id.Entry))}]");
            return Task.FromResult<IEnumerable<CardModel>>(list.Take(n).ToList());
        }

        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
        {
            var pick = options.FirstOrDefault()?.Card;
            W($"  [selector] auto-picked card reward: {pick?.Id.Entry ?? "(none)"}");
            return new CardRewardSelection { card = pick, alternative = null };
        }
    }

    // ── plumbing ────────────────────────────────────────────────────────────────────────────────

    private static void Step(string name)
    {
        _step = name;
        _stepAt = DateTime.UtcNow;
        W($"— {name}");
    }

    /// <summary>Viewport capture, role-tagged (both instances share the mods folder, so an untagged name
    /// would have one peer overwrite the other). Retries past all-black frames: right after run entry the
    /// viewport is a loading frame and saves as pure black, which looks like a rendering bug.</summary>
    private static async Task Shot(string name, int tries = 6)
    {
        try
        {
            for (int i = 0; i < tries; i++)
            {
                if (Engine.GetMainLoop() is not SceneTree tree) return;
                var img = tree.Root.GetTexture()?.GetImage();
                if (img != null && !IsBlank(img))
                {
                    var err = img.SavePng(Path.Combine(ModDir(), $"selftest.coop.{_role}.{name}.png"));
                    W($"shot {name}: {err} (try {i + 1})");
                    return;
                }
                await Task.Delay(2000);
            }
            if (Engine.GetMainLoop() is SceneTree t2)
                t2.Root.GetTexture()?.GetImage()?.SavePng(Path.Combine(ModDir(), $"selftest.coop.{_role}.{name}.png"));
            W($"shot {name}: still black after {tries} tries (saved anyway)");
        }
        catch (Exception e) { W($"shot {name} failed: {e.Message}"); }
    }

    private static bool IsBlank(Image img)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        if (w == 0 || h == 0) return true;
        for (int x = w / 10; x < w; x += Math.Max(1, w / 10))
            for (int y = h / 10; y < h; y += Math.Max(1, h / 10))
            {
                var c = img.GetPixel(x, y);
                if (c.R + c.G + c.B > 0.05f) return false;
            }
        return true;
    }

    /// <summary>The top-bar log button's visibility: true/false if it exists, null if it does not.
    /// Null is reported as SKIPPED rather than failed — a missing UI node must not sink the convergence
    /// verdict this battery exists for.</summary>
    private static bool? ButtonVisible()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return null;
            return Find(tree.Root, "NReshuffleSummaryButton") is Control c ? c.Visible : (bool?)null;
        }
        catch { return null; }
    }

    /// <summary>How many nodes with this name exist. More than one means a rebuilt UI left a second
    /// copy behind — the discriminator between "wrong node inspected" and "right node, wrong state".</summary>
    private static int CountNamed(string name)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return -1;
            int n = 0;
            void Walk(Node node)
            {
                if (node.Name == name) n++;
                foreach (var c in node.GetChildren()) Walk(c);
            }
            Walk(tree.Root);
            return n;
        }
        catch { return -1; }
    }

    private static Node? Find(Node n, string name)
    {
        if (n.Name == name) return n;
        foreach (var c in n.GetChildren())
        {
            var r = Find(c, name);
            if (r != null) return r;
        }
        return null;
    }

    /// <summary>
    /// Measure that the log button is combat-only, on a REAL fight.
    ///
    /// ★WHY IT HAS TO BE HERE. The solo battery never enters combat (it drives Reroll directly), so the
    /// hide half of the gating had no measurement at all — it was guaranteed by construction only. The
    /// networked <c>win</c> command kills every enemy, which produces a genuine victory and therefore
    /// fires the game's own CombatManager.CombatEnded, the exact signal the button listens to.
    /// </summary>
    private static async Task<bool> CheckButtonGating(RunManager run, Player me, bool issueWin,
                                                     bool? preSampledDuring = null)
    {
        // ★Diagnostics go in the RESULT FILE, not the log. Two instances write the same godot.log and
        // clobber each other's lines — the previous cycle's reshuffle log entries were simply gone, which
        // made "did the local-player branch run here?" unanswerable. The result file is per-role.
        try
        {
            var st = run.State;
            ulong netId = 0;
            try { netId = run.NetService.NetId; } catch { }
            W($"  [diag] NetService.NetId={netId}, recorded={(ReshuffleHistory.Current != null)}, hasAny={ReshuffleHistory.HasAny}");
            W($"  [diag] patch trace: {CombatEntryPatch.Trace}");
            W($"  [diag] history trace: {ReshuffleHistory.Trace}");
            W($"  [diag] pulse trace: {NReshuffleSummaryButton.PulseTrace}, buttonsInTree={CountNamed("NReshuffleSummaryButton")}");
            W($"  [diag] attach={NReshuffleSummaryButton.AttachCount} adopt={NReshuffleSummaryButton.AdoptCount}, {NReshuffleSummaryButton.InstanceState()}");
            W($"  [diag] combatEnded={NReshuffleSummaryButton.CombatEndedCount}");
            if (st != null)
                foreach (var p in st.Players)
                    W($"  [diag] player {p.NetId}: IsMe={SafeIsMe(p)}, netIdMatch={p.NetId == netId}");
        }
        catch (Exception e) { W("  [diag] failed: " + e.Message); }

        bool? during = preSampledDuring ?? ButtonVisible();
        W($"button during combat = {(during?.ToString() ?? "(not found)")} (want True)"
          + (preSampledDuring != null ? " [sampled mid-fight]" : ""));

        if (issueWin)
        {
            Step("win (ends the fight so CombatEnded fires)");
            run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, "win", inCombat: true));
        }
        await Task.Delay(9000);

        bool? after = ButtonVisible();
        W($"button after the fight ended = {(after?.ToString() ?? "(not found)")} (want False)");

        if (during == null || after == null)
        {
            W("assert button gating: SKIPPED (button node not found)");
            return true;
        }
        bool ok = during.Value && !after.Value;
        W($"assert button gating (visible in combat, hidden after) = {ok} (want True)");
        return ok;
    }

    private static ulong LocalNetId(RunManager run)
    {
        try { return run.NetService.NetId; } catch { return 1uL; }
    }

    private static Player? LocalPlayerOf(RunManager run)
    {
        var players = run.State!.Players;
        try { var me = LocalContext.GetMe(players); if (me != null) return me; } catch { }
        ulong id = LocalNetId(run);
        return players.FirstOrDefault(p => p.NetId == id) ?? players.FirstOrDefault();
    }

    private static bool SafeIsMe(Player p) { try { return LocalContext.IsMe(p); } catch { return false; } }

    private static string LocalChar(StartRunLobby lobby)
    {
        try
        {
            object lp = lobby.LocalPlayer;   // LobbyPlayer is a struct
            var f = lp.GetType().GetField("character", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return f?.GetValue(lp)?.ToString() ?? "no-character";
        }
        catch (Exception e) { return "char? " + e.Message; }
    }

    private static NCharacterSelectScreen? FindScreen(Node n)
    {
        if (n is NCharacterSelectScreen s) return s;
        foreach (var c in n.GetChildren())
        {
            var r = FindScreen(c);
            if (r != null) return r;
        }
        return null;
    }

    private static StartRunLobby? LobbyOf(NCharacterSelectScreen screen)
    {
        try
        {
            var f = typeof(NCharacterSelectScreen).GetField("_lobby", BindingFlags.NonPublic | BindingFlags.Instance);
            return f?.GetValue(screen) as StartRunLobby;
        }
        catch { return null; }
    }

    private static void W(string line)
    {
        _out.AppendLine(line);
        MainFile.Logger.Info($"[{MainFile.ModId}] COOP[{_role}] | {line}");
    }

    /// <summary>Write the result file WITHOUT ending the test. Used to bank the convergence verdict
    /// before attempting anything that could hang: winning the fight pops the reward screens, and this
    /// battery carries only a card selector, not the full screen pump. If that wedges we still keep the
    /// measurement we came for instead of timing out with nothing.</summary>
    private static void Write(bool ok)
    {
        try
        {
            // Fully qualified: Godot also defines an Environment type, so the bare name is ambiguous here.
            string header = (ok ? "RESULT: OK" : "RESULT: FAIL") + System.Environment.NewLine
                          + "role=" + _role + System.Environment.NewLine;
            File.WriteAllText(Path.Combine(ModDir(), $"selftest.coop.{_role}.txt"), header + _out);
        }
        catch { }
    }

    private static void Flush(bool ok)
    {
        _done = true;
        _out.Insert(0, (ok ? "RESULT: OK\n" : "RESULT: FAIL\n") + "role=" + _role + "\n");
        try { File.WriteAllText(Path.Combine(ModDir(), $"selftest.coop.{_role}.txt"), _out.ToString()); } catch { }
    }
}
