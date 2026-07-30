using System;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;      // LocalContext
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization; // LocString
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;         // RunManager

namespace Sts2RelicReshuffle;

/// <summary>
/// Mark a one-time reward relic whose reward this run has already been handed over.
///
/// ★WHY IT MATTERS HERE AND NOT IN VANILLA. Vanilla never shows you a relic you already collected, so
/// there is nothing to warn about. This mod puts swapped-away relics back in the pool, so Strawberry can
/// sit in a shop for 200 gold after its max HP is long since banked — and buying it now does nothing
/// (<see cref="RepeatPickupPatch"/>). Without a mark on the tooltip that is a gold trap with no tell.
///
/// ★REUSES THE GAME'S OWN STRING. <c>gameplay_ui / RELIC_USED_UP</c> is the phrasing the game already
/// uses for a spent relic ("[red]This relic was all used up.[/red]\n{description}") and it ships in all
/// 14 languages. Writing our own line would mean 14 translations to maintain and a second idiom for the
/// same idea.
///
/// ★NOT SHOWN ON THE COPY THAT PAID OUT. A relic sitting in your inventory that gave you its reward when
/// you picked it up is a perfectly normal relic and vanilla marks nothing — so neither do we. The mark
/// appears on offers (you do not hold it: buying it would be the wasted purchase) and on a re-obtained
/// copy (held, but picked twice, so this instance is the inert one).
/// </summary>
[HarmonyPatch(typeof(RelicModel), "get_HoverTip")]
internal static class SpentRewardTooltipPatch
{
    /// <summary>Test-only: which gate the last call fell out of. Every early return here is silent by
    /// design, so without this a missing mark is indistinguishable from a mark that was never wanted —
    /// which is exactly how the first version of this patch failed (identity unresolved, no log, assert
    /// just said False).</summary>
    internal static string LastSkip = "(never ran)";

    private static void Postfix(RelicModel __instance, ref HoverTip __result)
    {
        try
        {
            if (__instance == null) { LastSkip = "null instance"; return; }
            if (!__instance.HasUponPickupEffect) { LastSkip = "not a pickup-reward relic"; return; }

            // The getter already wraps melted / used-up relics in their own red line. Wrapping again
            // would stack two banners saying nearly the same thing.
            if (__instance.IsMelted) { LastSkip = "melted"; return; }
            if (__instance.IsMutable && __instance.IsUsedUp) { LastSkip = "already marked used up"; return; }

            Player? who = Who(__instance);
            if (who == null) { LastSkip = "no player resolvable"; return; }

            int picked = SpentRewardLedger.TimesPicked(who, __instance.Id.Entry);
            if (picked < 1) { LastSkip = "never picked this run"; return; }

            bool held = who.Relics.Any(r => r != null && r.Id.Entry == __instance.Id.Entry);
            if (held && picked < 2) { LastSkip = "held, picked once — this is the copy that paid out"; return; }

            var wrapped = new LocString("gameplay_ui", "RELIC_USED_UP");
            wrapped.Add("description", __result.Description ?? "");
            __result.Description = wrapped.GetFormattedText();
            LastSkip = "(marked)";
        }
        catch (Exception e)
        {
            // A tooltip is not worth breaking; leave the vanilla one intact.
            LastSkip = "threw: " + e.Message;
            MainFile.Logger.Warn($"[{MainFile.ModId}] spent-reward tooltip failed: {e.Message}");
        }
    }

    /// <summary>
    /// Whose history decides whether this relic is spent.
    ///
    /// ★OWNER FIRST, IDENTITY SECOND. Asking <c>LocalContext</c> looks like the obvious way to find "the
    /// player looking at this", and it is what the first version did — but LocalContext's NetId is not
    /// always resolved (it is null outright in a headless run), and every failure here is a silent skip.
    /// A relic the player is holding names its owner directly and needs no identity lookup at all, which
    /// covers the case the mark matters most for: the inert re-obtained copy sitting in the inventory.
    /// LocalContext is only consulted for relics nobody owns yet — a shop or reward offer.
    /// </summary>
    private static Player? Who(RelicModel relic)
    {
        try { if (relic.IsMutable && relic.Owner != null) return relic.Owner; }
        catch { /* canonical template — Owner asserts mutable; fall through */ }

        var players = RunManager.Instance?.State?.Players;
        if (players == null || players.Count == 0) return null;

        // Single-player leaves no ambiguity to resolve, so don't let an unresolved NetId cost the mark.
        return LocalContext.GetMe(players) ?? (players.Count == 1 ? players[0] : null);
    }
}
