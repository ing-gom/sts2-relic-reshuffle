using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Models;   // RelicModel
using MegaCrit.Sts2.Core.Runs;     // RunManager

namespace Sts2RelicReshuffle;

/// <summary>
/// What the CURRENT fight's reshuffle changed, per player.
///
/// ★WHY ONLY THE CURRENT FIGHT. Keeping the whole run's history answers "what did I have three fights
/// ago?", which nobody needs mid-combat — those relics are gone and nothing can be done about them. The
/// question that matters while the fight is on screen is "what am I holding, and what did it replace".
///
/// ★WHY KEYED BY NetId AND RESOLVED AT READ TIME. The first version recorded only the local player's
/// swaps, deciding "local" at reshuffle time. Measured in a two-instance co-op run, that silently lost
/// the whole feature on the CLIENT: at <c>CombatRoom.StartCombat</c> the client's identity has not
/// settled, so the check said "not me" for its own player and nothing was recorded — while the same
/// check run a few seconds later reported IsMe=true and a matching NetId. Collection must not depend on
/// a value that is still resolving. So every player's swaps are stored, and WHICH one to show is decided
/// when the panel opens, by which time the identity is reliable.
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

    private static readonly Dictionary<ulong, Entry> _byPlayer = new();
    private static int _floor = -1;

    /// <summary>Test-only breadcrumb of every mutation, in order. Two co-op instances share one
    /// godot.log and clobber each other, so this is the only reliable way to see what happened on a
    /// given peer. It is what revealed that records were being made and then wiped.</summary>
    internal static string Trace = "";

    /// <summary>Bounded append. The trace lives for the whole run, and a 50-fight run would otherwise
    /// grow it without limit for no benefit — only the recent tail is ever diagnostic.</summary>
    private static void Append(string s)
    {
        Trace += s;
        if (Trace.Length > 400) Trace = "…" + Trace.Substring(Trace.Length - 300);
    }

    /// <summary>Bumped whenever something is recorded, so callers can notice a change without polling.</summary>
    public static int Version { get; private set; }


    public static void Record(int floor, ulong netId, List<ReshuffleService.Swap> swaps)
    {
        if (swaps == null || swaps.Count == 0) return;

        // A different floor is a different fight: drop the previous one rather than mixing them.
        if (floor != _floor)
        {
            _byPlayer.Clear();
            _floor = floor;
        }

        var entry = new Entry { Floor = floor };
        foreach (var s in swaps) entry.Swaps.Add((s.From, s.To));
        _byPlayer[netId] = entry;
        Version++;
        Append($"+{netId}@f{floor};");
    }

    /// <summary>
    /// This machine's player's entry for the CURRENT fight, or null.
    ///
    /// ★STALENESS IS DECIDED BY COMPARISON, NOT BY WIPING. An earlier version cleared the record when the
    /// fight ended, and in a two-instance co-op run the client's records were measurably present and then
    /// gone — the log showed both players recorded, and a moment later the dictionary was empty. Whatever
    /// wiped it, a design where "is this still relevant?" is answered by a mutation somebody else can
    /// perform is fragile by construction. So nothing clears the record: it simply stops being CURRENT
    /// once the floor moves on.
    ///
    /// Identity is resolved HERE, not at record time — see the class remark. Falls back to "the only
    /// record there is" so single-player works even if the net identity cannot be read.
    /// </summary>
    public static Entry? Current
    {
        get
        {
            if (_byPlayer.Count == 0) return null;

            int floorNow = -1;
            try { floorNow = RunManager.Instance?.State?.TotalFloor ?? -1; } catch { }
            if (floorNow >= 0 && _floor != floorNow) return null;   // recorded for a different fight

            ulong me = 0;
            try { me = RunManager.Instance?.NetService?.NetId ?? 0; } catch { }
            if (_byPlayer.TryGetValue(me, out var mine)) return mine;
            return _byPlayer.Count == 1 ? _byPlayer.Values.First() : null;
        }
    }

    /// <summary>True if anything at all was recorded for this fight (any player).</summary>
    public static bool HasAny => _byPlayer.Count > 0;

    /// <summary>Wipe everything. Used only when a run ends or a test resets — NOT on combat end; see
    /// <see cref="Current"/> for why staleness is a comparison rather than a wipe.</summary>
    public static void Clear()
    {
        _byPlayer.Clear();
        _floor = -1;
        Append("CLEAR;");
    }

    internal static void ResetForTest() => Clear();
}
