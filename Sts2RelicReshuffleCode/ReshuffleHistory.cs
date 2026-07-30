using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;   // RelicModel
using MegaCrit.Sts2.Core.Runs;     // IRunState

namespace Sts2RelicReshuffle;

/// <summary>
/// Every reshuffle the LOCAL player has had this run, newest floor first.
///
/// ★WHY A RECORD INSTEAD OF A POP-UP. The first version announced each reshuffle with a panel that
/// faded after five seconds, anchored under the relic bar. Two problems, and the second is the one that
/// killed the approach: a transient message is lost if you look away, and the relic bar GROWS through a
/// run — it eventually wraps — so "under the bar" is not a stable place to put anything. A list you can
/// open when you want has neither problem, and it can answer a question the pop-up never could:
/// "what did I have three fights ago?"
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

    private static readonly List<Entry> _entries = new();
    private static IRunState? _run;

    /// <summary>Newest first, so the panel opens on the fight the player is actually in.</summary>
    public static IReadOnlyList<Entry> Entries => _entries;

    /// <summary>Bumped whenever something is recorded, so the top-bar button can flag "there is
    /// something new here" without polling the list itself.</summary>
    public static int Version { get; private set; }

    public static void Record(IRunState run, int floor, List<ReshuffleService.Swap> swaps)
    {
        if (swaps == null || swaps.Count == 0) return;

        // A different run object means a new or loaded run — the old history belongs to a run that is
        // no longer on screen, and keeping it would make the panel lie.
        if (!ReferenceEquals(run, _run))
        {
            _entries.Clear();
            _run = run;
        }

        var entry = new Entry { Floor = floor };
        foreach (var s in swaps) entry.Swaps.Add((s.From, s.To));
        _entries.Insert(0, entry);
        Version++;
    }

    internal static void ResetForTest()
    {
        _entries.Clear();
        _run = null;
    }
}
