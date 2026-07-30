using System;
using System.Linq;
using MegaCrit.Sts2.Core.Context;              // LocalContext
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;                 // RunManager

namespace Sts2RelicReshuffle;

/// <summary>
/// NETWORKED transport carrying the HOST's reshuffle settings to every co-op client, so all clients
/// derive the same re-roll (see <see cref="HostReshuffleConfig"/>).
///
/// Rides the game's BUILT-IN <c>ConsoleCmdGameAction</c> wire type (a plain string on the synchronized
/// action queue), so this mod adds NO new <c>INetAction</c> subtype and never perturbs the net type-id
/// ordering — the same lockstep-safe trick Sts2RelicForge and Sts2RelicTransmute use. Issued
/// programmatically by <see cref="ReshuffleConfigBroadcaster"/>, not meant for manual typing;
/// <see cref="DebugOnly"/> is false only so it registers in normal (non-debug) co-op play.
///
/// Auto-registered by the game's DevConsole reflection over GetSubtypesInMods&lt;AbstractConsoleCmd&gt;().
/// </summary>
public sealed class ReshuffleConfigSyncCmd : AbstractConsoleCmd
{
    public const string Verb = "rs_config";

    public override string CmdName => Verb;
    public override string Args => "<includeAncient01> <includeEvent01>";
    public override string Description =>
        "Internal (networked): the host broadcasts its Relic Reshuffle pool settings so every client re-rolls identically.";
    public override bool IsNetworked => true;
    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (args.Length < 2)
            return new CmdResult(success: false, $"Usage: {Verb} {Args}");

        try
        {
            HostReshuffleConfig.ApplyFromHost(
                includeAncient: args[0] == "1",
                includeEvent: args[1] == "1");
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] {Verb} apply failed: {e.Message}");
            return new CmdResult(success: false, $"{Verb} error: {e.Message}");
        }

        return new CmdResult(success: true, $"{Verb} applied.");
    }
}

/// <summary>
/// Host-side helper: enqueue an <c>rs_config</c> so every client caches the host's settings. A no-op off
/// the host, so it is safe to call from any peer on a shared trigger.
/// </summary>
internal static class ReshuffleConfigBroadcaster
{
    public static void BroadcastIfHost()
    {
        if (!HostReshuffleConfig.IsHost) return;
        var run = RunManager.Instance;
        if (run == null) return;

        // Resolve OUR player as the action owner; without one the enqueue has no sender to attribute.
        var me = LocalContext.GetMe(run.State?.Players ?? Enumerable.Empty<Player>());
        if (me == null) return;   // not resolvable yet — the next room entry re-broadcasts

        string synced = string.Join(" ",
            ReshuffleConfigSyncCmd.Verb,
            ReshuffleConfig.IncludeAncient ? "1" : "0",
            ReshuffleConfig.IncludeEvent ? "1" : "0");

        // Fired from RunManager.EnterRoom, i.e. at a room boundary and never mid-combat.
        run.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, synced, inCombat: false));
    }
}
