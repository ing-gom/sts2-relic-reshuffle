using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Models;   // RelicModel

namespace Sts2RelicReshuffle;

/// <summary>
/// The combat-start readout: a transient panel listing what each of your relics just turned into.
///
/// ★WHY IT EXISTS. Without it the mod is silent. The relic bar does change, but it changes during a room
/// transition while the player is looking at a loading fade, so the only signal is "the icons are
/// different now" — which asks the player to have memorized their own inventory. The single question
/// this mod has to answer at the start of every fight is *what did I just lose and what did I get*, and
/// that is a diff, not a state. So the panel shows pairs, not a list.
///
/// Mounted once on the SceneTree root as its own CanvasLayer (same pattern as Sts2NetWorth's overlay) so
/// it survives room/scene changes and never has to be re-parented to the combat scene.
///
/// ★NO _Process. The panel is event-driven end to end: built on demand, faded by a Tween, dismissed by a
/// one-shot SceneTree timer. A per-frame tick here would be a hot path for something that changes once
/// per combat — see [[feedback_perf_guard]].
/// </summary>
public partial class ReshuffleBanner : CanvasLayer
{
    /// <summary>Seconds the panel stays fully visible before it fades out.</summary>
    private const float HoldSeconds = 5.0f;
    private const float FadeSeconds = 0.45f;

    /// <summary>Rows beyond this get a scrollbar instead of growing the panel. ★A VBox's minimum size
    /// beats the panel's anchors in Godot, so an unscrolled variable-length list silently pushes the
    /// panel off-screen once a late-game inventory has a dozen relics —
    /// see [[feedback_godot_unscrolled_list_grows_panel]].</summary>
    private const int MaxVisibleRows = 6;
    private const int RowHeight = 44;

    private PanelContainer? _panel;
    private VBoxContainer? _rows;
    private Tween? _tween;
    private ulong _generation;

    private static readonly string Locale = SafeLocale();

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        Layer = 120;   // above the combat HUD, below modal dialogs
        Visible = false;
    }

    /// <summary>
    /// Show the swap list. Safe to call again while a previous banner is still fading — the generation
    /// counter makes the older dismissal a no-op so a fast second combat can't blank the new panel.
    /// </summary>
    public void ShowSwaps(List<(RelicModel from, RelicModel to)> swaps)
    {
        try
        {
            if (swaps == null || swaps.Count == 0) return;

            _generation++;
            ulong mine = _generation;

            Rebuild(swaps);
            Visible = true;

            _tween?.Kill();
            _tween = CreateTween();
            _tween.TweenProperty(_panel, "modulate:a", 1.0f, FadeSeconds).From(0.0f);

            // One-shot dismissal. GetTree() can be null if we were removed mid-flight; guard it rather
            // than throwing inside a Harmony-driven call path.
            var tree = GetTree();
            if (tree == null) return;
            var timer = tree.CreateTimer(HoldSeconds + FadeSeconds, processAlways: true);
            timer.Timeout += () => FadeOut(mine);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] banner show failed: {e.Message}");
        }
    }

    private void FadeOut(ulong generation)
    {
        try
        {
            if (generation != _generation) return;   // a newer banner took over
            _tween?.Kill();
            _tween = CreateTween();
            _tween.TweenProperty(_panel, "modulate:a", 0.0f, FadeSeconds);
            _tween.TweenCallback(Callable.From(() => { if (generation == _generation) Visible = false; }));
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[{MainFile.ModId}] banner fade failed: {e.Message}");
            Visible = false;
        }
    }

    private void Rebuild(List<(RelicModel from, RelicModel to)> swaps)
    {
        if (_panel == null) BuildShell();
        foreach (var child in _rows!.GetChildren()) child.QueueFree();

        foreach (var (from, to) in swaps)
            _rows.AddChild(MakeRow(from, to));

        // Height follows the row count up to the cap, past which the ScrollContainer takes over.
        int visible = Math.Min(swaps.Count, MaxVisibleRows);
        _scroll!.CustomMinimumSize = new Vector2(0, visible * RowHeight);
    }

    private ScrollContainer? _scroll;

    private void BuildShell()
    {
        _panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        // Left edge, below the top bar: the one region a combat never uses (cards are bottom, enemies
        // right, the party sprites sit lower-left).
        _panel.Position = new Vector2(24, 112);
        _panel.CustomMinimumSize = new Vector2(430, 0);
        AddChild(_panel);

        var margin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_right", 14);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        _panel.AddChild(margin);

        var root = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.AddThemeConstantOverride("separation", 6);
        margin.AddChild(root);

        var title = MakeLabel(Header(), 20);
        title.AddThemeColorOverride("font_color", new Color(1f, 0.86f, 0.35f));
        root.AddChild(title);

        _scroll = new ScrollContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        root.AddChild(_scroll);

        _rows = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _rows.AddThemeConstantOverride("separation", 4);
        _rows.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _scroll.AddChild(_rows);
    }

    /// <summary>One "old → new" line: both icons, both localized names, with the lost relic dimmed.</summary>
    private static Control MakeRow(RelicModel from, RelicModel to)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 6);

        var oldIcon = MakeIcon(from);
        oldIcon.Modulate = new Color(1f, 1f, 1f, 0.45f);
        row.AddChild(oldIcon);

        var oldName = MakeLabel(TitleOf(from), 15);
        oldName.AddThemeColorOverride("font_color", new Color(0.62f, 0.62f, 0.62f));
        row.AddChild(oldName);

        var arrow = MakeLabel("→", 17);
        arrow.AddThemeColorOverride("font_color", new Color(1f, 0.86f, 0.35f));
        row.AddChild(arrow);

        row.AddChild(MakeIcon(to));

        var newName = MakeLabel(TitleOf(to), 15);
        newName.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(newName);

        return row;
    }

    /// <summary>A relic's atlas icon at a fixed box size. ★ExpandMode is assigned BEFORE the size: set
    /// the other way round, Godot clamps the TextureRect to the source texture's own dimensions and the
    /// icon renders at full size, overflowing the row (the Sts2SlotMachine lesson).</summary>
    private static Control MakeIcon(RelicModel relic)
    {
        var rect = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        rect.CustomMinimumSize = new Vector2(32, 32);
        try { rect.Texture = relic.Icon; }
        catch (Exception e) { MainFile.Logger.Warn($"[{MainFile.ModId}] icon load failed for {relic.Id.Entry}: {e.Message}"); }
        return rect;
    }

    /// <summary>
    /// The relic's own localized title, so the panel speaks whatever language the game is in.
    ///
    /// ★MUST be <c>GetFormattedText()</c>, not <c>ToString()</c>. A LocString is a (table, key) pair and
    /// its ToString is a debug description — the first build of this panel rendered
    /// "LocString table relics entry AKABEKO.title" on every row. Every layout assert still passed,
    /// because the text was the right SIZE; only the screenshot showed it. GetFormattedText goes through
    /// LocManager.SmartFormat, which resolves the key and substitutes any variables.
    ///
    /// Falls back to the raw entry id, so a missing translation degrades to something readable rather
    /// than blanking the row.
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

    /// <summary>Panel header. The mod ships no .pck, so there is no localization file to read from —
    /// three inline strings cover the languages this mod is actually played in and everything else gets
    /// English rather than a missing-key placeholder.</summary>
    private static string Header() => Locale switch
    {
        var l when l.StartsWith("ko") => "유물 재편성",
        var l when l.StartsWith("zh") => "遗物重组",
        var l when l.StartsWith("ja") => "レリック再編成",
        _ => "Relics Reshuffled",
    };

    private static string SafeLocale()
    {
        try { return TranslationServer.GetLocale() ?? "en"; } catch { return "en"; }
    }

    /// <summary>Test-only view of the built panel. ★Comparing <c>_panel.Size</c> alone would not catch
    /// the failure this exists for: a TextureRect whose ExpandMode was set too late renders at the
    /// source texture's full size and spills OUTSIDE its container while every Control.Size reads
    /// correct. The self-test walks the subtree's global rects instead.</summary>
    internal Control? PanelForTest => _panel;
    internal int RowCountForTest => _rows?.GetChildCount() ?? 0;

    /// <summary>Every label string currently on the panel, so the self-test can assert on what the
    /// player would actually READ. Layout asserts cannot: an unresolved LocString debug string
    /// ("LocString table relics entry AKABEKO.title") lays out perfectly and is pure noise on screen.</summary>
    internal List<string> RowTextsForTest()
    {
        var texts = new List<string>();
        void Walk(Node n)
        {
            foreach (var child in n.GetChildren())
            {
                if (child is Label l) texts.Add(l.Text ?? "");
                Walk(child);
            }
        }
        if (_rows != null) Walk(_rows);
        return texts;
    }

    /// <summary>Union of every visible descendant's global rect — what the panel actually paints.</summary>
    internal Rect2 RenderedRectForTest()
    {
        Rect2 acc = _panel?.GetGlobalRect() ?? new Rect2();
        void Walk(Node n)
        {
            foreach (var child in n.GetChildren())
            {
                if (child is Control c && c.Visible) acc = acc.Merge(c.GetGlobalRect());
                Walk(child);
            }
        }
        if (_panel != null) Walk(_panel);
        return acc;
    }

    private static Label MakeLabel(string text, int fontSize)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        return label;
    }
}
