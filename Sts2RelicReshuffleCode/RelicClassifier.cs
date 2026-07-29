using System;
using System.Collections.Generic;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;

namespace Sts2RelicReshuffle;

/// <summary>
/// Decides whether a relic prototype is worth handing someone for a single fight.
///
/// ★THE PROBLEM: rarity-preserving swaps keep the power LEVEL constant but not the power's RELEVANCE.
/// Roll a rare slot onto a shop-discount or a rest-site relic and the player fights the elite a relic
/// down. Measured against the decompiled pool: of the relics that survive the other filters, a
/// meaningful slice have no combat-side hook at all. This class is what keeps them out.
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

    /// <summary>Relics that must never be rolled INTO. Each exclusion is a distinct failure, not taste:
    ///   · <c>HasUponPickupEffect</c> — its whole payload is dispensed at AfterObtained. We add relics
    ///     through the silent internal path precisely so that never re-fires, which leaves the relic as
    ///     an inert icon. (It also blocks the infinite-Strawberry exploit, for anyone who patches the
    ///     obtain path back in.)
    ///   · <c>SpawnsPets</c> / <c>AddsPet</c> — spawning a companion creature every combat entry, with
    ///     no matching despawn, accumulates junk in the combat state.
    ///   · <c>IsStackable</c> — a fresh instance arrives at StackCount 1, so rolling one in mints a
    ///     stack the player never earned. (Rolling one OUT is barred separately; see
    ///     <see cref="ReshuffleService.IsSwappableSource"/>.)</summary>
    public static bool IsValidTarget(RelicModel proto)
        => !proto.HasUponPickupEffect
        && !proto.SpawnsPets
        && !proto.AddsPet
        && !proto.IsStackable;
}
