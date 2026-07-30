using System;
using System.Collections.Generic;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicReshuffle;

/// <summary>
/// Does a relic do anything during a fight?
///
/// ★THIS NO LONGER FILTERS THE POOL, and that is a deliberate reversal. It used to: relics with no
/// combat-side hook were kept out on the theory that rarity-preserving swaps hold the power LEVEL
/// constant but not its RELEVANCE. That reasoning ignored the mod's own rule — a reshuffled relic is
/// KEPT until the next fight, so a shop, rest-site or map relic is in hand for exactly the part of the
/// run where it works. 26 of the 150 pooled-rarity relics were being withheld for no good reason.
///
/// What survives is the question itself, used by <see cref="SpentRewardTooltipPatch"/>: a relic whose
/// only function fires at pickup really is inert once the reshuffle grants it silently, and the tooltip
/// has to say so. "Has no combat hook AND its payload cannot run" is precisely "this icon does nothing".
///
/// ★HOW: by reflection over which hooks the relic type actually OVERRIDES, not by a hardcoded id list —
/// an id list silently rots on the next content patch, whereas a new relic that overrides a combat hook
/// classifies itself correctly the day it ships. We only need to name the hooks that are NOT combat
/// (rewards, shops, rest sites, plus the lifecycle/meta ones); everything else counts as combat value.
/// Cached per Type: this runs once per combat entry per relic, and reflection in that path is wasteful.
/// </summary>
internal static class RelicClassifier
{
    private static readonly Dictionary<Type, bool> _combatValue = new();

    /// <summary>Hooks that fire OUTSIDE a fight (reward screens, merchants, campfires, the map). A relic
    /// whose only overrides live here does nothing while you are swinging at something.
    ///
    /// ★NOTE <c>AfterRoomEntered</c> is deliberately absent — it IS a combat hook for our purposes.
    /// Bronze Scales applies its Thorns there, not at BeforeCombatStart, and 35 relics follow that shape.
    /// This is the same fact that forces the re-roll to happen before the AfterRoomEntered dispatch
    /// rather than at combat start; see <see cref="CombatEntryPatch"/>.</summary>
    private static readonly HashSet<string> NonCombatHooks = new(StringComparer.Ordinal)
    {
        // ── lifecycle / metadata, never an effect ──
        "IsAllowed", "ShouldFlush", "AfterCloned", "AfterObtained", "AfterRemoved",
        // ── rewards ──
        "TryModifyRewards", "TryModifyRewardsLate", "AfterModifyingRewards",
        "TryModifyCardRewardOptionsLate", "TryModifyCardBeingAddedToDeck",
        // ── merchant ──
        "ModifyMerchantCardCreationResults", "TryModifyMerchantPrices",
        // ── rest site ──
        "TryModifyRestSiteOptions", "TryModifyRestSiteHealRewards", "AfterRestSiteHeal",
        // ── map ──
        "AfterMapPointChosen",
    };

    /// <summary>True if this relic overrides at least one hook that can fire during a fight.</summary>
    public static bool HasCombatValue(RelicModel relic)
    {
        Type t = relic.GetType();
        if (_combatValue.TryGetValue(t, out bool cached)) return cached;

        bool result = false;
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public
                                     | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            // Walk the whole chain: a relic may inherit its behaviour from an intermediate base class,
            // and DeclaredOnly on the leaf alone would miss that.
            for (Type? cur = t; cur != null && cur != typeof(RelicModel); cur = cur.BaseType)
            {
                foreach (MethodInfo m in cur.GetMethods(flags))
                {
                    if (!m.IsVirtual) continue;
                    // An override redeclares a method whose base definition lives further up the chain.
                    if (m.GetBaseDefinition().DeclaringType == m.DeclaringType) continue;
                    string n = m.Name;
                    if (n.StartsWith("get_", StringComparison.Ordinal)) continue;   // property backing
                    if (n.StartsWith("set_", StringComparison.Ordinal)) continue;
                    if (NonCombatHooks.Contains(n)) continue;
                    result = true;
                    break;
                }
                if (result) break;
            }
        }
        catch (Exception e)
        {
            // Reflection over a modded relic type must never break combat entry. Fail OPEN (treat it as
            // useful) so the relic stays available rather than vanishing from the pool for a bad reason.
            MainFile.Logger.Warn($"[{MainFile.ModId}] combat-value probe failed for {t.Name}: {e.Message}");
            result = true;
        }

        _combatValue[t] = result;
        return result;
    }

}
