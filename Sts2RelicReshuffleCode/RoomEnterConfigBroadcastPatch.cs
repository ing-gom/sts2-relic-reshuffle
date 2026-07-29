using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;   // RunManager

namespace Sts2RelicReshuffle;

/// <summary>
/// Host: re-broadcast our settings on EVERY room entry, so clients hold the host's config before the
/// next combat re-rolls (see <see cref="HostReshuffleConfig"/>). Runs on all peers;
/// <see cref="ReshuffleConfigBroadcaster.BroadcastIfHost"/> is a no-op off the host.
///
/// ★KNOWN WINDOW: the broadcast rides the synchronized action queue, so it is not guaranteed to have
/// been replayed by the time the SAME room's combat starts. In practice that leaves exactly one exposed
/// case — the run's very first fight, on a host whose settings differ from the defaults. Until the
/// broadcast lands a client falls back to its own config (see <see cref="HostReshuffleConfig.UseHost"/>),
/// which matches the host whenever both run stock settings. Any divergence is also self-healing: peers
/// re-exchange full player state at every room boundary, so it cannot outlive one combat. Closing the
/// window entirely would mean awaiting a broadcast inside room entry, which is not worth blocking on.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterRoom))]
internal static class RoomEnterConfigBroadcastPatch
{
    private static void Prefix()
    {
        try
        {
            ReshuffleConfigBroadcaster.BroadcastIfHost();
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] room-enter config broadcast failed: {e.Message}");
        }
    }
}
