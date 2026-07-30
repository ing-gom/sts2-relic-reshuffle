using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicReshuffle;

/// <summary>
/// Pay a one-time pickup reward at most once per run, no matter how many times the relic is obtained.
///
/// ★WHY. Relics the reshuffle takes away go back into the draw pool (<see cref="RelicPoolReturn"/>), which
/// is what keeps the pool from narrowing all run. That includes relics whose whole payload fires once at
/// pickup, and re-buying one of those would otherwise pay out again: collect Strawberry's max HP, let a
/// reshuffle take it, buy it back, collect again — unbounded, for the price of a shop slot. So the relic
/// is allowed to come back and its SECOND payout is suppressed instead, leaving a relic that is honestly
/// inert (and marked as such — see <see cref="SpentRewardTooltipPatch"/>).
///
/// ★WHY PATCH THE PAYOUT AND NOT <c>RelicCmd.Obtain</c>. Suppressing at Obtain would mean reimplementing
/// its body minus one await — the history append, grab-bag removal, pickup animation, sfx, mark-as-seen
/// and FloorAddedToDeck are all bookkeeping we would then own and have to keep in step with the game.
/// Patching <c>AfterObtained</c> touches exactly the thing that must not happen twice and leaves every
/// piece of vanilla bookkeeping to vanilla.
///
/// ★TARGETING IS PURELY REFLECTIVE, no instances needed. A relic can only have a pickup payout if it
/// overrides BOTH <c>HasUponPickupEffect</c> (the base returns false) and <c>AfterObtained</c> (the base
/// returns a completed task), so the declaring-type test finds precisely that set — 47 relics in the
/// current build. The prefix re-checks <c>HasUponPickupEffect</c> on the instance anyway, so a modded
/// relic that computes it per-instance still behaves.
/// </summary>
[HarmonyPatch]
internal static class RepeatPickupPatch
{
    /// <summary>Test-only: how many payout methods were actually patched. An assert on this catches the
    /// silent failure where a game update renames something and the whole guard quietly targets nothing.</summary>
    internal static int PatchedCount;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        var found = new List<MethodBase>();
        Type[] types;
        try { types = typeof(RelicModel).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t != null)!; }

        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (Type t in types)
        {
            if (t == null || t.IsAbstract || !typeof(RelicModel).IsAssignableFrom(t)) continue;
            try
            {
                // DeclaringType == t means this type overrides it rather than inheriting the base's false.
                PropertyInfo? flag = t.GetProperty("HasUponPickupEffect", Any);
                if (flag == null || flag.DeclaringType != t) continue;

                MethodInfo? payout = t.GetMethod("AfterObtained", Any, null, Type.EmptyTypes, null);
                if (payout == null || payout.DeclaringType != t) continue;

                found.Add(payout);
            }
            catch (Exception e)
            {
                MainFile.Logger.Warn($"[{MainFile.ModId}] payout-target probe failed for {t.Name}: {e.Message}");
            }
        }

        PatchedCount = found.Count;
        MainFile.Logger.Info($"[{MainFile.ModId}] one-time payout guard armed on {found.Count} relic(s).");
        return found;
    }

    private static bool Prefix(RelicModel __instance, ref Task __result)
    {
        try
        {
            if (!__instance.HasUponPickupEffect) return true;

            // Obtain calls AddRelicInternal before awaiting the payout, so the owner is already attached.
            // No owner means we cannot consult that player's history — run vanilla rather than guess.
            Player? owner = __instance.Owner;
            if (owner == null) return true;

            if (!SpentRewardLedger.IsRepeatPickup(owner, __instance)) return true;

            MainFile.Logger.Info(
                $"[{MainFile.ModId}] {__instance.Id.Entry} was already paid out this run — " +
                "obtained again, reward suppressed.");
            __result = Task.CompletedTask;
            return false;
        }
        catch (Exception e)
        {
            // Fail OPEN. A broken guard that pays the player twice is a bug; a broken guard that eats a
            // reward the player earned is worse, and unrecoverable for them.
            MainFile.Logger.Warn($"[{MainFile.ModId}] payout guard failed, letting it run: {e.Message}");
            return true;
        }
    }
}
