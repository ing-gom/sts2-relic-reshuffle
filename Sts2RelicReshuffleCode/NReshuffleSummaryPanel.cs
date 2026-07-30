using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Models;   // RelicModel

namespace Sts2RelicReshuffle;

/// <summary>
/// The reshuffle log: a full-screen overlay showing what THIS fight's reshuffle changed.
/// Opened from the top-bar button (<see cref="NReshuffleSummaryButton"/>), closed by its button, the
/// close button, or Escape.
///
/// ★WHY THIS REPLACED THE AUTO-BANNER. The old readout appeared on its own and faded after five
/// seconds, anchored beneath the relic bar. The relic bar grows all run and eventually wraps, so there
/// was no stable place to anchor to — and a message that disappears is no use to someone who looked
/// away. Opening it on demand fixes both.
/// Modelled on Sts2RelicForge's forge summary so the two mods present the same way.
///
/// ★NO _Process. Built when opened, freed when closed; nothing ticks while it is shut.
/// Pure local UI — no commands, no run-state writes → co-op safe.
/// </summary>
internal sealed partial class NReshuffleSummaryPanel : CanvasLayer
{
    private static NReshuffleSummaryPanel? _open;

    /// <summary>Open if closed, close if open. Wired to the top-bar button's Pressed signal.</summary>
    public static void Toggle()
    {
        try
        {
            if (_open != null) { Close(); return; }
            if (Engine.GetMainLoop() is not SceneTree tree) return;

            var panel = new NReshuffleSummaryPanel { Layer = 120, Name = "Sts2RelicReshuffle_Summary" };
            _open = panel;
            tree.Root.AddChild(panel);
        }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] summary toggle failed: {e.Message}"); }
    }

    public static void Close()
    {
        var p = _open;
        _open = null;
        try { p?.QueueFree(); }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] summary close failed: {e.Message}"); }
    }

    public static bool IsOpen => _open != null;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        BuildUi();
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } k && k.Keycode == Key.Escape)
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    private void BuildUi()
    {
        Vector2 vp = GetViewportSize();

        // Dimmed backdrop. Also swallows clicks so a stray click behind the panel can't play a card.
        var bg = new ColorRect { Color = new Color(0f, 0f, 0f, 0.72f), MouseFilter = Control.MouseFilterEnum.Stop };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(bg);

        var frame = new PanelContainer();
        // PanelContainer inherits no visible style here, so the content would float on the dim backdrop
        // with nothing to sit on. Give it an explicit plate.
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.07f, 0.09f, 0.96f),
            BorderColor = new Color(1f, 0.86f, 0.35f, 0.45f),
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
        };
        style.SetBorderWidthAll(2);
        frame.AddThemeStyleboxOverride("panel", style);

        // Height follows the content up to a cap, past which the ScrollContainer takes over — a fixed
        // tall frame around three rows reads as broken, and an uncapped one runs off the screen once a
        // long run has twenty fights recorded.
        var current = ReshuffleHistory.Current;
        int lines = current == null ? 1 : 1 + current.Swaps.Count;
        // Sized as a FRACTION of the viewport, not in fixed pixels: this is a reading surface — relic
        // names run long in Korean and Chinese, and a narrow box wraps them into an unreadable column.
        float w = Mathf.Clamp(vp.X * 0.58f, 640f, 1100f);
        float chrome = 190f;                                   // title + close row + margins
        float maxH = Mathf.Min(vp.Y * 0.78f, 860f);
        float h = Mathf.Clamp(chrome + lines * RowHeight, 340f, maxH);
        frame.Size = new Vector2(w, h);
        frame.Position = new Vector2((vp.X - w) / 2f, (vp.Y - h) / 2f);
        AddChild(frame);

        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 26);
        frame.AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        margin.AddChild(root);

        var title = MakeLabel(Loc.Title, 32);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.AddThemeColorOverride("font_color", Gold);
        root.AddChild(title);

        // ★A variable-length list MUST scroll. A VBox's minimum size beats the panel's own bounds in
        // Godot, so an unscrolled list quietly grows the frame past the screen once a long run has
        // twenty-odd fights recorded. See [[feedback_godot_unscrolled_list_grows_panel]].
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        root.AddChild(scroll);

        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 18);
        scroll.AddChild(list);

        if (current == null)
        {
            var empty = MakeLabel(Loc.Empty, 20);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.AddThemeColorOverride("font_color", new Color(0.72f, 0.72f, 0.72f));
            list.AddChild(empty);
        }
        else list.AddChild(MakeFloorBlock(current));

        var close = new Button { Text = Loc.Close, CustomMinimumSize = new Vector2(200, 50) };
        close.Pressed += Close;
        var closeRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        closeRow.AddChild(close);
        root.AddChild(closeRow);
    }

    /// <summary>This fight's block: a floor heading, then its "old → new" rows.</summary>
    private static Control MakeFloorBlock(ReshuffleHistory.Entry entry)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 7);

        var head = MakeLabel(string.Format(Loc.FloorFormat, entry.Floor), 22);
        head.AddThemeColorOverride("font_color", Gold);
        box.AddChild(head);

        foreach (var (from, to) in entry.Swaps)
            box.AddChild(MakeRow(from, to));

        return box;
    }

    /// <summary>One "old → new" line: both icons, both localized names, the outgoing relic greyed.</summary>
    private static Control MakeRow(RelicModel from, RelicModel to)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 9);

        var oldIcon = MakeIcon(from);
        oldIcon.Modulate = new Color(1f, 1f, 1f, 0.55f);
        row.AddChild(oldIcon);

        var oldName = MakeLabel(TitleOf(from), 20);
        oldName.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        row.AddChild(oldName);

        var arrow = MakeLabel("→", 23);
        arrow.AddThemeColorOverride("font_color", Gold);
        row.AddChild(arrow);

        row.AddChild(MakeIcon(to));

        var newName = MakeLabel(TitleOf(to), 20);
        newName.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(newName);

        return row;
    }

    /// <summary>A relic's atlas icon at a fixed box size. ★ExpandMode is assigned BEFORE the size: the
    /// other way round, Godot clamps the TextureRect to the source texture's own dimensions and the icon
    /// renders full size, overflowing the row (the Sts2SlotMachine lesson).</summary>
    private static Control MakeIcon(RelicModel relic)
    {
        var rect = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        rect.CustomMinimumSize = new Vector2(IconSize, IconSize);
        try { rect.Texture = relic.Icon; }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] icon load failed for {relic.Id.Entry}: {e.Message}"); }
        return rect;
    }

    /// <summary>
    /// The relic's own localized title.
    ///
    /// ★MUST be <c>GetFormattedText()</c>, not <c>ToString()</c>. A LocString is a (table, key) pair and
    /// its ToString is a debug description — the first build of this UI rendered
    /// "LocString table relics entry AKABEKO.title" on every row, and every layout assert still passed
    /// because the text was the right SIZE. Only the screenshot showed it.
    /// </summary>
    private static string TitleOf(RelicModel relic)
    {
        try
        {
            string t = relic.Title?.GetFormattedText() ?? "";
            return string.IsNullOrWhiteSpace(t) ? relic.Id.Entry : t;
        }
        catch { return relic.Id.Entry; }
    }

    /// <summary>Icon box and the per-line height the frame budgets for. Kept together because the
    /// height estimate must track the icon size — they drifted apart once and the frame came out too
    /// short for its own rows.</summary>
    private const int IconSize = 46;
    private const float RowHeight = 52f;

    private static readonly Color Gold = new(1f, 0.86f, 0.35f);

    private static Label MakeLabel(string text, int fontSize)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        return label;
    }

    private Vector2 GetViewportSize()
    {
        try { return GetViewport().GetVisibleRect().Size; }
        catch { return new Vector2(1920, 1080); }
    }

    // ── test hooks ──────────────────────────────────────────────────────────────────────────────

    internal static Control? FrameForTest =>
        _open?.GetChildCount() > 1 ? _open.GetChild(1) as Control : null;

    /// <summary>Every label on the open panel, so the self-test can assert on what a player would READ
    /// (a layout assert cannot tell a resolved name from an unresolved LocString debug string).</summary>
    internal static List<string> TextsForTest()
    {
        var texts = new List<string>();
        if (_open == null) return texts;
        void Walk(Node n)
        {
            foreach (var child in n.GetChildren())
            {
                if (child is Label l) texts.Add(l.Text ?? "");
                Walk(child);
            }
        }
        Walk(_open);
        return texts;
    }

    /// <summary>Union of every visible descendant's global rect — what the panel actually paints.</summary>
    internal static Rect2 RenderedRectForTest()
    {
        var frame = FrameForTest;
        Rect2 acc = frame?.GetGlobalRect() ?? new Rect2();
        if (frame == null) return acc;
        void Walk(Node n)
        {
            foreach (var child in n.GetChildren())
            {
                if (child is Control c && c.Visible) acc = acc.Merge(c.GetGlobalRect());
                Walk(child);
            }
        }
        Walk(frame);
        return acc;
    }

    /// <summary>Panel text, English base with Korean and Chinese overrides — the same three-language
    /// policy as the settings and the sister mods. Everything else falls back to English.</summary>
    private static class Loc
    {
        private static string Pick(string en, string kor, string zh)
        {
            string l;
            try { l = TranslationServer.GetLocale() ?? "en"; } catch { l = "en"; }
            if (l.StartsWith("ko")) return kor;
            if (l.StartsWith("zh")) return zh;
            return en;
        }

        public static string Title => Pick("This fight's reshuffle", "이번 전투의 재편성", "本场战斗的重组");
        public static string Empty => Pick("Nothing was reshuffled for this fight.", "이번 전투에서는 재편성된 유물이 없습니다.", "本场战斗没有重组任何遗物。");
        public static string FloorFormat => Pick("Floor {0}", "{0}층", "第 {0} 层");
        public static string Close => Pick("Close", "닫기", "关闭");
    }
}
