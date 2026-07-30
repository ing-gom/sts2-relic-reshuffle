using System;
using System.Collections.Generic;
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

    private const string KeyIncludeAncient = "includeAncient";
    private const string KeyIncludeEvent = "includeEvent";

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

    /// <summary>Per-language text. English is the entry's plain Label/Description, so it is not repeated
    /// here; "kor" matches exactly and "zh" PREFIX-matches both zhs and zht. Everything else falls back
    /// to English — the same three-language policy the sister mods use.</summary>
    private static Dictionary<string, string> L(string kor, string zh)
        => new() { ["kor"] = kor, ["zh"] = zh };

    /// <summary>
    /// Attach per-language label/description to the LAST-ADDED entry by REFLECTION, never a direct call.
    ///
    /// ★WHY REFLECTION. ModKit resolves first-wins across every installed sister mod's bundled copy, so
    /// on a player's machine an OLDER Sts2.ModKit without LocalizedLabels can shadow ours. A direct call
    /// then throws MissingMethodException at JIT time and kills the WHOLE config registration — the mod
    /// would ship with no settings at all, on exactly the machines with the most mods installed. With
    /// reflection an old ModKit merely means the settings stay English.
    /// See [[feedback_modkit_first_wins_version_skew]].
    /// </summary>
    private static void Loc(ConfigEntryBuilder b, Dictionary<string, string> labels,
                            Dictionary<string, string> descriptions)
    {
        try
        {
            var t = b.GetType();
            t.GetMethod("LocalizedLabels")?.Invoke(b, new object[] { labels });
            t.GetMethod("LocalizedDescriptions")?.Invoke(b, new object[] { descriptions });
        }
        catch (Exception e) { Logger.Info($"[{ModId}] config localization skipped (old ModKit loaded): {e.Message}"); }
    }

    private static void RegisterConfig()
    {
        // Register FIRST so a read returns the saved-or-default value — a GetValue before registration
        // returns default(T) = false for an unknown key, which for a bool silently inverts an intended-ON
        // default. See [[feedback_modconfig_read_after_register]]. (Both defaults here are OFF, so the
        // hazard is inert today — the ordering is kept because the next added option may not be.)
        var b = ModConfigBridge.For(ModId, "Relic Reshuffle", Logger);

        b.Toggle(KeyIncludeAncient, "Include Ancient relics",
            defaultValue: false,
            onChanged: v => ReshuffleConfig.IncludeAncient = v)
         .Description("Let Ancient relics take part in the reshuffle — they can be replaced, and a reshuffle can hand them out. Default OFF: they are run-defining picks, and most of them are rewards from the Ancient One rather than anything the relic pool normally offers. In co-op the HOST's setting applies to everyone.");
        Loc(b,
            L("고대의 존재 유물 포함", "包含远古遗物"),
            L("고대의 존재 유물도 재편성에 참여시킵니다 — 교체될 수도 있고, 재편성으로 받을 수도 있습니다. 기본값 꺼짐: 런을 규정하는 유물이고, 대부분 일반 유물 풀이 아니라 고대의 존재가 주는 보상입니다. 협동 플레이에서는 호스트 설정이 모두에게 적용됩니다.",
              "让远古遗物参与重组 — 既可能被替换，也可能通过重组获得。默认关闭：它们是决定整场游戏走向的遗物，且大多来自远古存在的奖励而非普通遗物池。多人合作中以房主的设置为准。"));

        b.Toggle(KeyIncludeEvent, "Include event relics",
            defaultValue: false,
            onChanged: v => ReshuffleConfig.IncludeEvent = v)
         .Description("Let relics that only come from events take part in the reshuffle. Default OFF: they are payoffs for specific events, so handing them out at random gives away content the run never earned. In co-op the HOST's setting applies to everyone.");
        Loc(b,
            L("이벤트 유물 포함", "包含事件遗物"),
            L("이벤트로만 얻는 유물도 재편성에 참여시킵니다. 기본값 꺼짐: 특정 이벤트의 보상이라, 무작위로 지급하면 런에서 얻은 적 없는 내용을 그냥 주게 됩니다. 협동 플레이에서는 호스트 설정이 모두에게 적용됩니다.",
              "让仅能通过事件获得的遗物参与重组。默认关闭：它们是特定事件的奖励，随机发放等于白送本局从未取得的内容。多人合作中以房主的设置为准。"));

        b.Register();

        ReshuffleConfig.IncludeAncient = ModConfigBridge.GetValue<bool>(ModId, KeyIncludeAncient, false);
        ReshuffleConfig.IncludeEvent = ModConfigBridge.GetValue<bool>(ModId, KeyIncludeEvent, false);

        Logger.Info($"[{ModId}] includeAncient={ReshuffleConfig.IncludeAncient}, " +
                    $"includeEvent={ReshuffleConfig.IncludeEvent}, " +
                    $"relicForge={(RelicForgeBridge.IsPresent() ? "present" : "absent")}.");
    }
}
