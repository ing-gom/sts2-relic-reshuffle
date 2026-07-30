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
///
/// ★VISIBLE ONLY DURING A FIGHT. It has nothing to say anywhere else: the log describes the reshuffle
/// that produced the relics you are fighting with, and on the map or in a shop that reshuffle is over.
/// Left permanently visible it also kept offering the PREVIOUS fight's list, which is worse than
/// offering nothing. So it is hidden by default, shown when a reshuffle is recorded, and hidden again on
/// the game's own <c>CombatManager.CombatEnded</c> event — the same signal the native combat UI uses,
/// rather than a Harmony patch of our own.
///
/// Pure local UI — no commands, no run-state writes → co-op safe.
/// </summary>
internal sealed partial class NReshuffleSummaryButton : TextureButton
{
    private const float Gap = 10f;
    private const float HoverScale = 1.15f;

    private static NReshuffleSummaryButton? _instance;

    /// <summary>Test-only breadcrumb: what Pulse actually managed to do. The co-op result file is the
    /// only reliable channel (two instances share one godot.log), and "the button did not appear" has
    /// too many possible causes to guess between.</summary>
    internal static string PulseTrace = "(pulse never called)";

    /// <summary>Set when a fight's reshuffle has been announced, cleared when the fight ends. A rebuilt
    /// button consults this (via <see cref="ReshuffleHistory.Current"/>) rather than starting hidden.</summary>
    private static bool _shownForFight;

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
        // ★ADOPT an existing button instead of returning blind. NTopBar._Ready can fire more than once
        // (measured on a co-op CLIENT, whose run UI is rebuilt), and the old code returned here WITHOUT
        // updating _instance — leaving the static pointing at a freed node while a live, hidden button
        // sat in the tree. Pulse then found an invalid instance and did nothing, so the client never saw
        // the log button at all even though everything upstream had recorded correctly.
        if (host.GetNodeOrNull(nameof(NReshuffleSummaryButton)) is NReshuffleSummaryButton existing)
        {
            _instance = existing;
            return;
        }

        // Hidden until a fight actually reshuffles something.
        var btn = new NReshuffleSummaryButton { _bar = bar, Name = nameof(NReshuffleSummaryButton), Visible = false };
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

    /// <summary>Reveal the button and flash it gold. Called when a reshuffle is recorded — event-driven
    /// on purpose, so nothing has to tick every frame just to notice a change
    /// ([[feedback_perf_guard]]).</summary>
    public static void Pulse()
    {
        var btn = Resolve();
        // ★Fall back to a NAME lookup. Resolve() matches by TYPE, which fails if the mod assembly ends
        // up loaded twice — the node in the tree is then a different Type object with the same name, and
        // every typed search silently misses it. Visibility only needs a Control, so take whichever we
        // can get and let the tween be the part that needs the real type.
        Control? ctrl = btn ?? FindNamed();
        if (ctrl == null) { PulseTrace = "no button found (typed and named both missed)"; return; }
        try
        {
            ctrl.Visible = true;
            PulseTrace = $"shown via {(btn != null ? "typed" : "named")} lookup";
            btn?.SubscribeCombatEnd();
            _shownForFight = true;
            if (btn == null) return;   // no typed instance: visibility is set, skip the tween

            var tween = btn.CreateTween();
            tween.SetLoops(3);
            tween.TweenProperty(btn, "modulate", new Color(1f, 0.86f, 0.35f), 0.35f);
            tween.TweenProperty(btn, "modulate", Colors.White, 0.35f);
        }
        catch (Exception e)
        {
            PulseTrace = "threw: " + e.Message;
            MainFile.Logger.Warn($"[{MainFile.ModId}] summary pulse failed: {e.Message}");
        }
    }

    /// <summary>The button located by NODE NAME rather than type — see the remark in Pulse.</summary>
    private static Control? FindNamed()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return null;
            return Walk(tree.Root) as Control;
        }
        catch { return null; }

        static Node? Walk(Node n)
        {
            if (n.Name == nameof(NReshuffleSummaryButton)) return n;
            foreach (var c in n.GetChildren())
            {
                var r = Walk(c);
                if (r != null) return r;
            }
            return null;
        }
    }

    /// <summary>Hide the button, drop the record and close the panel when the fight is over. Wired to
    /// CombatManager.CombatEnded (see the class remark on why the button is combat-only).</summary>
    private void OnCombatEnded(MegaCrit.Sts2.Core.Rooms.CombatRoom _)
    {
        try
        {
            Visible = false;
            _shownForFight = false;
            if (NReshuffleSummaryPanel.IsOpen) NReshuffleSummaryPanel.Close();
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] summary hide-on-combat-end failed: {e.Message}"); }
    }

    /// <summary>Subscribe once. Deferred to the first Pulse rather than done in _Ready: the top bar is
    /// built before a combat exists, so CombatManager.Instance can still be null there — by the time a
    /// reshuffle is recorded it certainly is not.</summary>
    private void SubscribeCombatEnd()
    {
        if (_subscribed) return;
        try
        {
            var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
            if (cm == null) return;
            cm.CombatEnded += OnCombatEnded;
            _subscribed = true;
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] CombatEnded subscribe failed: {e.Message}"); }
    }

    private bool _subscribed;

    /// <summary>The live button, re-found in the scene tree if the cached instance went stale. Belt and
    /// braces alongside the adoption in <see cref="Attach"/>: a static reference to a Godot node can be
    /// outlived by a UI rebuild, and this feature is invisible when that happens.</summary>
    private static NReshuffleSummaryButton? Resolve()
    {
        if (_instance != null && GodotObject.IsInstanceValid(_instance)) return _instance;
        try
        {
            if (Engine.GetMainLoop() is SceneTree tree)
            {
                var found = FindIn(tree.Root);
                if (found != null) { _instance = found; return found; }
            }
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] button resolve failed: {e.Message}"); }
        return null;
    }

    private static NReshuffleSummaryButton? FindIn(Node n)
    {
        if (n is NReshuffleSummaryButton b) return b;
        foreach (var c in n.GetChildren())
        {
            var r = FindIn(c);
            if (r != null) return r;
        }
        return null;
    }

    public override void _Ready()
    {
        // ★VISIBILITY IS DERIVED, NOT COMMANDED. Pulse() showing the button once is not enough: measured
        // on a co-op client, the top bar is rebuilt AFTER the reshuffle, so Attach created a fresh
        // HIDDEN button in the new bar and the one Pulse had shown was gone — the trace said
        // "shown via typed lookup" while the button on screen was a different, hidden node. A node that
        // decides its own visibility from the record at _Ready cannot be undone by being rebuilt.
        Visible = ReshuffleHistory.Current != null;
        SubscribeCombatEnd();

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
