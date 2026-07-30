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
/// ★★TARGET EVERY <c>AfterObtained</c> OVERRIDE, NOT JUST THE FLAGGED ONES. The first version keyed on
/// <c>HasUponPickupEffect</c> and that was wrong: 33 relics run a pickup payload without setting the flag
/// — Gnarled Hammer enchants a card, Signet Ring hands over 999 gold, Large Capsule gives two relics.
/// Gnarled Hammer is Shop rarity, so it is genuinely stocked in the grab bag and the loop was reachable
/// in ordinary play (buy, enchant, get reshuffled, buy again). Declaring the override IS the payload, so
/// the declaring-type test alone is the right filter — 80 relics in the current build.
///
/// ★SUPPRESSING A NON-REWARD PAYLOAD IS HARMLESS. A few of these overrides are just "if I was obtained
/// mid-combat, apply my effect now" fixups (Belt Buckle, Snecko Eye). Skipping one on a REPEAT pickup
/// costs nothing: their real hook is <c>BeforeCombatStart</c>, which still runs.
/// </summary>
[HarmonyPatch]
internal static class RepeatPickupPatch
{
    /// <summary>Test-only: how many payout methods were actually patched. An assert on this catches the
    /// silent failure where a game update renames something and the whole guard quietly targets nothing.</summary>
    internal static int PatchedCount;

    /// <summary>Test-only: every (relic, owner) whose payload this peer actually suppressed.
    /// ★It lives here rather than in the log because two co-op instances write the SAME godot.log and
    /// clobber each other's lines — a log-based count is not admissible evidence about which peer did
    /// what. The result file is per-role, so this is.</summary>
    internal static readonly List<string> SuppressedForTest = new();

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
                // DeclaringType == t means this type overrides the base's do-nothing implementation.
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
        MainFile.Logger.Info($"[{MainFile.ModId}] pickup-payload guard armed on {found.Count} relic(s).");
        return found;
    }

    /// <summary>Test-only: is this relic's payload covered by the guard? Lets an assert name the exact
    /// relic that motivated the fix (Gnarled Hammer) instead of only checking a count.</summary>
    internal static bool CoversForTest(RelicModel relic)
    {
        try
        {
            var m = relic.GetType().GetMethod("AfterObtained",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            return m != null && m.DeclaringType == relic.GetType();
        }
        catch { return false; }
    }

    private static bool Prefix(RelicModel __instance, ref Task __result)
    {
        try
        {
            // Obtain calls AddRelicInternal before awaiting the payout, so the owner is already attached.
            // No owner means we cannot consult that player's history — run vanilla rather than guess.
            Player? owner = __instance.Owner;
            if (owner == null) return true;

            if (!SpentRewardLedger.IsRepeatPickup(owner, __instance)) return true;

            try { SuppressedForTest.Add($"{__instance.Id.Entry}@{owner.NetId}"); } catch { }
            MainFile.Logger.Info(
                $"[{MainFile.ModId}] {__instance.Id.Entry} already ran its pickup payload this run — " +
                "obtained again, payload suppressed.");
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
