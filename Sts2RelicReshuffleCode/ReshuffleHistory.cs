using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;   // RelicModel
using MegaCrit.Sts2.Core.Runs;     // IRunState

namespace Sts2RelicReshuffle;

/// <summary>
/// The LOCAL player's most recent reshuffle — the one that produced the relics they are fighting with
/// right now.
///
/// ★WHY ONLY THE CURRENT FIGHT. An earlier version kept the whole run's history. That answers "what did
/// I have three fights ago?", which turns out to be a question nobody needs mid-combat: the relics from
/// two floors back are gone and nothing can be done about them. The one question that matters while the
/// fight is on the screen is "what am I holding, and what did it replace" — so the log holds exactly
/// that, and the panel needs no scrolling through history to show it.
///
/// Keeps the RelicModel instances, not just ids, so the panel can render each relic's icon and localized
/// title exactly as the game would — the outgoing relic has already left the inventory by then.
///
/// LOCAL-ONLY and presentational: never read by the derivation, so it cannot affect co-op convergence.
/// </summary>
internal static class ReshuffleHistory
{
    internal sealed class Entry
    {
        public int Floor;
        public List<(RelicModel From, RelicModel To)> Swaps = new();
    }

    /// <summary>The current fight's reshuffle, or null if this fight didn't reshuffle anything.</summary>
    public static Entry? Current { get; private set; }

    /// <summary>Bumped whenever something is recorded, so the top-bar button can flag "there is
    /// something new here" without polling.</summary>
    public static int Version { get; private set; }

    public static void Record(IRunState run, int floor, List<ReshuffleService.Swap> swaps)
    {
        if (swaps == null || swaps.Count == 0) return;

        var entry = new Entry { Floor = floor };
        foreach (var s in swaps) entry.Swaps.Add((s.From, s.To));
        Current = entry;
        Version++;
    }

    /// <summary>Drop the record when a run ends or a different one is loaded, so the panel can never
    /// show a previous run's relics.</summary>
    public static void Clear() => Current = null;

    internal static void ResetForTest() => Current = null;
}
