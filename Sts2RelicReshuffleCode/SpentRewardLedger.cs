using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicReshuffle;

/// <summary>
/// How many times has this player actually PICKED this relic during this run?
///
/// ★WHY THIS EXISTS. Relics whose whole payload is dispensed once at pickup (Strawberry's max HP,
/// Alchemical Coffer's potions, …) are safe in vanilla because obtaining one means keeping it — you can
/// never be offered it twice. This mod hands relics in and out every fight, so the same relic can legally
/// pass through a shop a second time. Paying out again would be an unbounded max-HP / gold / potion loop,
/// so the second payout has to be suppressed — and suppressing it needs a record of the first.
///
/// ★THE RECORD IS THE GAME'S OWN, NOT OURS. <c>RelicCmd.Obtain</c> already appends
/// <c>ModelChoiceHistoryEntry(relic.Id, wasPicked: true)</c> to the player's map-point history, and that
/// history is part of the serialized run. Reading it instead of keeping a private ledger means the
/// suppression survives quitting and reloading for free — a private ledger would reset on load and hand
/// the exploit straight back. It is also per-player and replicated, so co-op peers read the same counts.
///
/// ★COUNT, DON'T FLAG — and this is the subtle part. Obtain writes the history entry BEFORE it awaits
/// <c>AfterObtained</c>, so by the time a payout runs its own pick is already recorded. A boolean
/// "was it ever picked" would therefore be true during the FIRST pickup and suppress the payout the
/// player legitimately earned. The question that actually distinguishes the two cases is "is this at
/// least the second pick", i.e. count &gt;= 2.
/// </summary>
internal static class SpentRewardLedger
{
    // The walk is cheap but the tooltip getter runs on hover, so memoize it and rebuild only when the
    // history visibly grew. Keyed on the run + player so a new run can never read a stale table.
    private static object? _run;
    private static ulong _who;
    private static long _stamp = long.MinValue;
    private static Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>Cheap fingerprint of "has the relic history changed". Counts only — walking the entries
    /// to build a real hash would defeat the point of caching.</summary>
    private static long Stamp(Player player)
    {
        var run = player.RunState;
        long acts = run.MapPointHistory.Count;
        long points = 0, picks = 0;
        if (acts > 0)
        {
            var last = run.MapPointHistory[(int)acts - 1];
            points = last.Count;
        }
        try
        {
            var cur = run.CurrentMapPointHistoryEntry;
            if (cur != null) picks = cur.GetEntry(player.NetId).RelicChoices.Count;
        }
        catch { /* player not in this entry yet — counts stay 0, the stamp still moves with points */ }
        return (acts * 1_000_003L) + (points * 10_007L) + picks;
    }

    private static Dictionary<string, int> Counts(Player player)
    {
        object run = player.RunState;
        long stamp = Stamp(player);
        if (ReferenceEquals(run, _run) && _who == player.NetId && _stamp == stamp) return _counts;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var act in player.RunState.MapPointHistory)
        {
            if (act == null) continue;
            foreach (var point in act)
            {
                if (point?.PlayerStats == null) continue;
                foreach (var stats in point.PlayerStats)
                {
                    if (stats == null || stats.PlayerId != player.NetId) continue;
                    foreach (var choice in stats.RelicChoices)
                    {
                        if (!choice.wasPicked) continue;   // offered and declined — nothing was paid out
                        string id = choice.choice.Entry;
                        counts[id] = counts.TryGetValue(id, out int n) ? n + 1 : 1;
                    }
                }
            }
        }

        _run = run; _who = player.NetId; _stamp = stamp; _counts = counts;
        return counts;
    }

    /// <summary>Times <paramref name="entry"/> was picked by this player this run. 0 on any failure —
    /// "no record" degrades to vanilla behaviour (the payout runs) rather than to silent suppression,
    /// which would rob the player of a reward they earned.</summary>
    public static int TimesPicked(Player player, string entry)
    {
        try { return Counts(player).TryGetValue(entry, out int n) ? n : 0; }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] pick-history read failed: {e.Message}");
            return 0;
        }
    }

    /// <summary>Is this pickup a repeat, i.e. must its one-time payout be suppressed? See the class
    /// remark for why the threshold is 2 and not 1.</summary>
    public static bool IsRepeatPickup(Player player, RelicModel relic)
        => relic != null && relic.HasUponPickupEffect && TimesPicked(player, relic.Id.Entry) >= 2;

    /// <summary>Has this relic's one-time reward already been handed over at some point this run? Unlike
    /// <see cref="IsRepeatPickup"/> this is asked about a relic the player is looking at rather than one
    /// being obtained, so a single recorded pick is enough.</summary>
    public static bool RewardAlreadyPaid(Player player, RelicModel relic)
        => relic != null && relic.HasUponPickupEffect && TimesPicked(player, relic.Id.Entry) >= 1;

    /// <summary>Test-only: drop the memo so an assert can observe a change it just made.</summary>
    internal static void InvalidateForTest() => _stamp = long.MinValue;
}
