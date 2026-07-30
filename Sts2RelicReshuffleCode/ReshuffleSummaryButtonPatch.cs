using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;               // PreloadManager (icon fallback)
using MegaCrit.Sts2.Core.HoverTips;            // HoverTip, HoverTipAlignment
using MegaCrit.Sts2.Core.Nodes.CommonUi;       // NTopBar
using MegaCrit.Sts2.Core.Nodes.HoverTips;      // NHoverTipSet

namespace Sts2RelicReshuffle;

/// <summary>
/// Puts the "what changed this fight" trigger in the top bar's right-hand button cluster
/// (map / deck / pause).
///
/// ★WHY THE TOP BAR. The readout used to be anchored under the relic bar, which grows all run and
/// eventually wraps — there is no stable spot down there. The top-bar cluster is fixed for the whole
/// run regardless of how many relics you hold, and it is where players already look for "open a list".
/// Same placement Sts2RelicForge uses for its forge summary, so the two mods behave alike.
///
/// Attached from NTopBar._Ready so it exists for the whole run.
/// </summary>
[HarmonyPatch(typeof(NTopBar), "_Ready")]
internal static class ReshuffleSummaryButtonPatch
{
    private static void Postfix(NTopBar __instance)
    {
        try { NReshuffleSummaryButton.Attach(__instance); }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] summary button attach failed: {e.Message}"); }
    }
}

/// <summary>
/// The top-bar toggle button. Pulses gold when a new reshuffle is recorded, so the player learns
/// something changed without a panel covering the board.
/// Pure local UI — no commands, no run-state writes → co-op safe.
/// </summary>
internal sealed partial class NReshuffleSummaryButton : TextureButton
{
    private const float Gap = 10f;
    private const float HoverScale = 1.15f;

    private static NReshuffleSummaryButton? _instance;

    private NTopBar _bar = null!;
    private bool _positioned;   // anchor computed once, after the bar's layout settles
    private bool _inFlow;       // true when a BoxContainer lays us out (no manual anchor)

    public static void Attach(NTopBar bar)
    {
        var deck = (Control?)bar.Deck ?? bar.Map;
        if (deck == null || deck.GetParent() is not Control parent) return;

        // ★NEVER become a child of a native button's own wrapper. Each sits inside a MarginContainer,
        // and a container positions ALL its children — a sibling added there renders exactly ON TOP of
        // the deck button, and any manual GlobalPosition is re-clobbered every layout pass. RelicForge
        // shipped that overlap once; this is the same fix.
        var target = parent is Container && parent.GetParent() is Control grand ? grand : parent;
        var host = target is BoxContainer ? target : bar;
        if (host.GetNodeOrNull(nameof(NReshuffleSummaryButton)) != null) return;   // already attached

        var btn = new NReshuffleSummaryButton { _bar = bar, Name = nameof(NReshuffleSummaryButton) };
        if (host is BoxContainer box)
        {
            var slot = parent is Container ? parent : deck;
            box.AddChild(btn);
            box.MoveChild(btn, slot.GetIndex());   // sit just left of the deck slot
            btn._inFlow = true;
        }
        else bar.AddChild(btn);

        _instance = btn;
        MainFile.Logger.Info($"[{MainFile.ModId}] summary button attached (host {host.GetType().Name}, inFlow {btn._inFlow}).");
    }

    /// <summary>Flash the button gold. Called when a reshuffle is recorded — event-driven on purpose, so
    /// nothing has to tick every frame just to notice a change ([[feedback_perf_guard]]).</summary>
    public static void Pulse()
    {
        var btn = _instance;
        if (btn == null || !GodotObject.IsInstanceValid(btn)) return;
        try
        {
            var tween = btn.CreateTween();
            tween.SetLoops(3);
            tween.TweenProperty(btn, "modulate", new Color(1f, 0.86f, 0.35f), 0.35f);
            tween.TweenProperty(btn, "modulate", Colors.White, 0.35f);
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] summary pulse failed: {e.Message}"); }
    }

    public override void _Ready()
    {
        TextureNormal = LoadIcon();
        IgnoreTextureSize = true;
        StretchMode = StretchModeEnum.KeepAspectCentered;

        Pressed += NReshuffleSummaryPanel.Toggle;
        MouseEntered += () => { Scale = Vector2.One * HoverScale; ShowTipBelow(); };
        MouseExited += () => { Scale = Vector2.One; NHoverTipSet.Remove(this); };
    }

    /// <summary>Show the tip directly BELOW the icon, and grow it LEFTWARD on the right half of the
    /// screen so its 360px body stays clear of the native cluster's tips and never clips the edge. The
    /// built-in alignments do neither — see [[reference_sts2_hovertip_alignment]].</summary>
    private void ShowTipBelow()
    {
        var tip = NHoverTipSet.CreateAndShow(this, MakeTip(), HoverTipAlignment.None);
        if (tip == null) return;
        const float tipWidth = 360f;
        const float gap = 8f;
        float vpWidth = GetViewportRect().Size.X;
        float dx = (GlobalPosition.X > vpWidth * 0.5f) ? (Size.X * Scale.X - tipWidth) : 0f;
        tip.GlobalPosition = GlobalPosition + new Vector2(dx, Size.Y * Scale.Y + gap);
    }

    /// <summary>Match the native buttons' size and, when not laid out by a BoxContainer, anchor just
    /// LEFT of the whole native cluster in GLOBAL coordinates — local Position is a trap, the buttons'
    /// local X does not follow their visual order. Runs each frame until the bar's layout yields real
    /// sizes, then freezes.</summary>
    public override void _Process(double delta)
    {
        if (_positioned) return;
        var cluster = new List<Control>();
        foreach (Control? b in new Control?[] { _bar.Map, _bar.Deck, _bar.Pause })
            if (b != null && b.Size.Y > 1f) cluster.Add(b);
        if (cluster.Count == 0) return;   // layout not settled yet

        float side = float.MaxValue;
        foreach (var b in cluster) side = Math.Min(side, b.Size.Y);
        side *= 0.82f;
        CustomMinimumSize = new Vector2(side, side);
        Size = new Vector2(side, side);
        PivotOffset = new Vector2(side / 2f, side / 2f);
        if (_inFlow) SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;

        if (!_inFlow)
        {
            float leftEdge = float.MaxValue, centerY = 0f;
            foreach (var b in cluster)
            {
                leftEdge = Math.Min(leftEdge, b.GlobalPosition.X);
                centerY += b.GlobalPosition.Y + b.Size.Y * b.Scale.Y / 2f;
            }
            centerY /= cluster.Count;
            GlobalPosition = new Vector2(leftEdge - side - Gap, centerY - side / 2f);
        }
        _positioned = true;
    }

    private static string TryLocale()
    {
        try { return TranslationServer.GetLocale() ?? "en"; } catch { return "en"; }
    }

    private static IHoverTip MakeTip()
    {
        string l = TryLocale();
        bool ko = l.StartsWith("ko"), zh = l.StartsWith("zh");
        var t = new HoverTip();   // setters reachable — ModKit publicizes sts2
        t.Title = ko ? "이번 전투의 재편성" : zh ? "本场战斗的重组" : "This fight's reshuffle";
        t.Description = ko
            ? "이번 전투에 들어오면서 어떤 유물이 무엇으로 바뀌었는지 봅니다."
            : zh ? "查看进入本场战斗时哪些遗物变成了什么。"
                 : "See which relic became which when you entered this fight.";
        t.Id = "sts2rr_summary_btn";
        return t;
    }

    /// <summary>Loose mods/Sts2RelicReshuffle/reshuffle_icon.png (swappable without a rebuild), falling
    /// back to a pck texture so the button never renders blank.</summary>
    private static Texture2D? LoadIcon()
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(typeof(NReshuffleSummaryButton).Assembly.Location);
            if (!string.IsNullOrEmpty(dir))
            {
                string file = System.IO.Path.Combine(dir, "reshuffle_icon.png");
                if (System.IO.File.Exists(file))
                {
                    var img = Image.LoadFromFile(file);
                    if (img != null) return ImageTexture.CreateFromImage(img);
                }
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] summary icon load failed: {e.Message}"); }
        return PreloadManager.Cache.GetTexture2D("res://images/ui/rest_site/option_reforge.png");
    }
}
