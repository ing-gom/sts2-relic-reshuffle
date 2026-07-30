using MegaCrit.Sts2.Core.Multiplayer.Game;   // NetGameType
using MegaCrit.Sts2.Core.Runs;               // RunManager

namespace Sts2RelicReshuffle;

/// <summary>
/// Co-op: make the reshuffle follow the HOST's settings so every client derives the same relics.
///
/// ★WHY THIS EXISTS. The reshuffle is deterministic given (seed, floor, NetId, slot, source relic) — but
/// only if every peer builds the SAME candidate pool. Both settings widen or narrow that pool, so two
/// players with different toggles would compute different relic sets, and since relic hooks are
/// simulated in lockstep that is an immediate desync rather than a cosmetic difference. The host
/// broadcasts its settings (<see cref="ReshuffleConfigSyncCmd"/>, verb <c>rs_config</c>) once per room
/// entry, every client caches them here, and derivation reads <c>ReshuffleConfig.Effective*</c>.
///
/// On the host and in single-player <see cref="UseHost"/> is false and everything falls through to the
/// local values, so those paths behave exactly as if this file did not exist.
/// </summary>
internal static class HostReshuffleConfig
{
    private static bool _received;

    public static bool IncludeAncient { get; private set; }
    public static bool IncludeEvent { get; private set; }

    public static bool IsHost => RunManager.Instance?.NetService?.Type == NetGameType.Host;

    /// <summary>True only for a co-op CLIENT that has actually received a broadcast. Single-player,
    /// fake-MP and the host all read their own config; a client that has not heard from the host yet
    /// also falls back locally rather than freezing on defaults it was never told.</summary>
    public static bool UseHost
    {
        get
        {
            if (!_received) return false;
            var run = RunManager.Instance;
            if (run == null) return false;
            if (run.IsSingleplayerOrFakeMultiplayer) return false;
            return !IsHost;
        }
    }

    /// <summary>Cache the host's settings. Runs on every peer (the host replays its own broadcast), but
    /// <see cref="UseHost"/> keeps the host reading its own values, so that replay is a harmless no-op.</summary>
    public static void ApplyFromHost(bool includeAncient, bool includeEvent)
    {
        _received = true;
        IncludeAncient = includeAncient;
        IncludeEvent = includeEvent;
    }

    /// <summary>One-line summary for the logs — the first thing to read when two peers disagree.</summary>
    public static string Describe()
        => $"includeAncient={ReshuffleConfig.EffectiveIncludeAncient}, includeEvent={ReshuffleConfig.EffectiveIncludeEvent}";
}
