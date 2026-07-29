using Godot;
using MegaCrit.Sts2.Core.Modding;
using Sts2.ModKit.Bootstrap;
using Sts2.ModKit.Config;

namespace Sts2RelicReshuffle;

/// <summary>
/// Entry point. ModBootstrap.Run does harmony.PatchAll(assembly), which installs the combat-entry
/// re-roll (<see cref="CombatEntryPatch"/>) and the co-op config broadcast
/// (<see cref="RoomEnterConfigBroadcastPatch"/>), then we register the ModConfig entries.
/// </summary>
[ModInitializer(nameof(Initialize))]
public class MainFile
{
    public const string ModId = "Sts2RelicReshuffle";

    private const string KeyEnabled = "reshuffleEnabled";
    private const string KeyKeepStarter = "keepStarter";
    private const string KeyKeepAncient = "keepAncient";
    private const string KeyCombatOnly = "combatRelevantOnly";
    private const string KeyKeepForged = "keepForged";
    private const string KeyShowBanner = "showBanner";

    /// <summary>The combat-start readout, mounted once on the SceneTree root so it outlives room changes.
    /// Null until the scene tree exists (and in headless test runs).</summary>
    public static ReshuffleBanner? Banner { get; private set; }

    public static readonly MegaCrit.Sts2.Core.Logging.Logger Logger
        = ModBootstrap.CreateLogger(ModId);

    public static void Initialize() =>
        ModBootstrap.Run(ModId, Logger, typeof(MainFile).Assembly, body: () =>
        {
            Logger.Info($"[{ModId}] relic reshuffle active.");
            if (Engine.GetMainLoop() is not SceneTree tree) return;

            Banner = new ReshuffleBanner { Name = $"{ModId}_Banner" };
            tree.Root.CallDeferred(Node.MethodName.AddChild, Banner);

            // Defer so ModConfig has finished its own Initialize before we Register().
            tree.CreateTimer(0.0).Timeout += RegisterConfig;
#if RESHUFFLE_SELFTEST
            // Debug-only (the csproj strips this file in Release, so a stale flag can never hijack a run).
            SoloTest.ArmIfRequested();
            // selftest.coop.flag + --fastmp host/join: autonomous 2-instance convergence test.
            CoopTest.ArmIfRequested();
#endif
        });

    private static void RegisterConfig()
    {
        // Register FIRST so a read returns the saved-or-default value — a GetValue before registration
        // returns default(T) = 0/false for an unknown key, which for a bool silently inverts an
        // intended-ON default. See [[feedback_modconfig_read_after_register]].
        ModConfigBridge.For(ModId, "Relic Reshuffle", Logger)
            .Toggle(KeyEnabled, "Enable relic reshuffle",
                defaultValue: true,
                onChanged: v => ReshuffleConfig.Enabled = v)
                .Description("Master switch. When ON, your relics are re-rolled every time you enter a fight; each one becomes a DIFFERENT relic of the SAME rarity, so your relic count and overall power level never change. The rolled relics stay with you until the next fight. In co-op the HOST's setting applies to everyone — relic effects are simulated on both machines, so the two players must run the same rules.")
            .Toggle(KeyKeepStarter, "Keep starter relics",
                defaultValue: true,
                onChanged: v => ReshuffleConfig.KeepStarter = v)
                .Description("Pin your character's starting relic (Burning Blood and friends) so it is never re-rolled. Default ON — your starter is part of your character's identity. Turn OFF for a fully chaotic run.")
            .Toggle(KeyKeepAncient, "Keep Ancient relics",
                defaultValue: true,
                onChanged: v => ReshuffleConfig.KeepAncient = v)
                .Description("Pin Ancient ('고대의 존재') relics so they are never re-rolled, and never handed out by a re-roll. Default ON — these are run-defining picks with no comparable same-rarity peers to swap between.")
            .Toggle(KeyCombatOnly, "Only roll into combat-useful relics",
                defaultValue: true,
                onChanged: v => ReshuffleConfig.CombatRelevantOnly = v)
                .Description("Exclude relics that do nothing during a fight (shop discounts, campfire and reward modifiers) from the pool of relics you can be given. Default ON — without it a rare slot can land on a shop relic and read as an empty slot for the whole fight. Turn OFF for a wider, swingier pool.")
            .Toggle(KeyKeepForged, "Keep forged relics (Relic Forge)",
                defaultValue: true,
                onChanged: v => ReshuffleConfig.KeepForged = v)
                .Description("Pin relics that the Relic Forge mod has given a prefix or curse. Default ON — a forge record is attached to that specific relic, so re-rolling it destroys an upgrade you paid for. No effect if Relic Forge isn't installed.")
            .Toggle(KeyShowBanner, "Show what changed at combat start",
                defaultValue: true,
                onChanged: v => ReshuffleConfig.ShowBanner = v)
                .Description("Show a short panel at the start of each fight listing which relic became which. Default ON — without it the only clue is that your relic bar looks different, which asks you to have memorized it. Turn OFF for a cleaner screen. Your own setting always applies, in co-op too.")
            .Register();

        ReshuffleConfig.Enabled = ModConfigBridge.GetValue<bool>(ModId, KeyEnabled, true);
        ReshuffleConfig.KeepStarter = ModConfigBridge.GetValue<bool>(ModId, KeyKeepStarter, true);
        ReshuffleConfig.KeepAncient = ModConfigBridge.GetValue<bool>(ModId, KeyKeepAncient, true);
        ReshuffleConfig.CombatRelevantOnly = ModConfigBridge.GetValue<bool>(ModId, KeyCombatOnly, true);
        ReshuffleConfig.KeepForged = ModConfigBridge.GetValue<bool>(ModId, KeyKeepForged, true);
        ReshuffleConfig.ShowBanner = ModConfigBridge.GetValue<bool>(ModId, KeyShowBanner, true);

        Logger.Info($"[{ModId}] enabled={ReshuffleConfig.Enabled}, keepStarter={ReshuffleConfig.KeepStarter}, " +
                    $"keepAncient={ReshuffleConfig.KeepAncient}, combatOnly={ReshuffleConfig.CombatRelevantOnly}, " +
                    $"keepForged={ReshuffleConfig.KeepForged}, " +
                    $"relicForge={(RelicForgeBridge.IsPresent() ? "present" : "absent")}.");
    }
}
