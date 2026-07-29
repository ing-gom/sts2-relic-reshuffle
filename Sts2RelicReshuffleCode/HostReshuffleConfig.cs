using MegaCrit.Sts2.Core.Multiplayer.Game;   // NetGameType
using MegaCrit.Sts2.Core.Runs;               // RunManager

namespace Sts2RelicReshuffle;

/// <summary>
/// Co-op: make the re-roll follow the HOST's settings so every client derives the same relics.
///
/// ★WHY THIS EXISTS: the re-roll is deterministic given (seed, floor, NetId, slot, source id) — but only
/// if every peer builds the SAME candidate pool and picks the SAME number of slots. Both of those depend
/// on <see cref="ReshuffleConfig"/>, which is per-client ModConfig. Two players with different toggles
/// would compute different relic sets, and since relic hooks are simulated in lockstep that is an
/// immediate desync rather than a cosmetic difference. So the host broadcasts its settings
/// (<see cref="ReshuffleConfigSyncCmd"/>, verb <c>rs_config</c>) once per room entry, every client caches
/// them here, and all derivation reads <c>ReshuffleConfig.Effective*</c> instead of the raw fields.
///
/// On the host and in single-player <see cref="UseHost"/> is false and everything falls through to the
/// local values, so those paths behave exactly as if this file did not exist.
/// </summary>
internal static class HostReshuffleConfig
{
    private static bool _received;

    public static bool Enabled { get; private set; } = true;
    public static bool KeepStarter { get; private set; } = true;
    public static bool KeepAncient { get; private set; } = true;
    public static bool CombatRelevantOnly { get; private set; } = true;
    public static bool KeepForged { get; private set; } = true;

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
    public static void ApplyFromHost(bool enabled, bool keepStarter, bool keepAncient,
                                     bool combatRelevantOnly, bool keepForged)
    {
        _received = true;
        Enabled = enabled;
        KeepStarter = keepStarter;
        KeepAncient = keepAncient;
        CombatRelevantOnly = combatRelevantOnly;
        KeepForged = keepForged;
    }

    /// <summary>One-line summary for the logs — the first thing to read when two peers disagree.</summary>
    public static string Describe()
        => $"enabled={Enabled}, keepStarter={KeepStarter}, keepAncient={KeepAncient}, " +
           $"combatOnly={CombatRelevantOnly}, keepForged={KeepForged}";
}
