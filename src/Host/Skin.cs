using System;
using System.Collections.Generic;
using UnityEngine;
using Vigil.Core;

namespace Vigil;

/// <summary>
/// The panel's look, the same as Scry's: its colours, the game's own fonts (its serif for text, its
/// runic face for the title) and rounded shapes made in code, so Vigil ships no pictures. The
/// styles are built at the panel's scale rather than drawn scaled, since text is sharper drawn at
/// its real size; they are made inside OnGUI, where Unity's own skin exists, and made again when
/// the scale changes or the game's fonts turn up after a first try without them.
/// </summary>
internal static class Skin
{
    public static readonly Color Backdrop = new(0.066f, 0.071f, 0.086f, 0.97f);

    /// <summary>Behind a section: between the panel and a button, so buttons in it still stand out.</summary>
    public static readonly Color Card = new(0.137f, 0.146f, 0.174f, 1f);

    public static readonly Color Raised = new(0.150f, 0.160f, 0.190f, 1f);
    public static readonly Color RaisedHover = new(0.190f, 0.200f, 0.240f, 1f);
    public static readonly Color Outline = new(1f, 1f, 1f, 0.13f);
    public static readonly Color Text = new(0.930f, 0.910f, 0.870f, 1f);
    public static readonly Color Dim = new(0.890f, 0.885f, 0.900f, 1f);
    public static readonly Color Faint = new(0.780f, 0.785f, 0.815f, 1f);
    public static readonly Color Accent = new(0.960f, 0.720f, 0.340f, 1f);

    /// <summary>A button that cannot be undone, in a warmer amber than the accent, so it is not taken for an ordinary one.</summary>
    public static readonly Color Warn = new(0.985f, 0.600f, 0.260f, 1f);

    public static readonly Color OnAccent = new(0.110f, 0.080f, 0.040f, 1f);

    /// <summary>A well below the panel: a text box, a check box, the answer.</summary>
    public static readonly Color Sunken = new(0.055f, 0.060f, 0.073f, 1f);

    public static readonly Color SunkenHover = new(0.060f, 0.065f, 0.080f, 1f);

    /// <summary>Between the rows of a list or a table.</summary>
    public static readonly Color Hairline = new(1f, 1f, 1f, 0.10f);

    public static readonly Color Good = new(0.608f, 0.839f, 0.486f, 1f);
    public static readonly Color Caution = new(0.910f, 0.690f, 0.290f, 1f);
    public static readonly Color Bad = new(0.910f, 0.384f, 0.310f, 1f);

    /// <summary>What a badge or a mark says by its colour: nothing in particular, the accent, fine, worth a look, wrong.</summary>
    internal enum Tone
    {
        Quiet,
        Accent,
        Good,
        Caution,
        Bad,
    }

    public static Color Of(Tone tone) => tone switch
    {
        Tone.Accent => Accent,
        Tone.Good => Good,
        Tone.Caution => Caution,
        Tone.Bad => Bad,
        _ => Faint,
    };

    /// <summary>How tall a line of buttons, fields and the words between them is, before scaling.</summary>
    public const float LineHeight = 26f;

    private static float _builtScale = -1f;
    private static float _fontsLookedAt = float.NegativeInfinity;
    private static Font _body, _heading;
    private static Texture2D _rounded, _small, _pill, _bar, _tick, _chevronRight, _chevronDown;
    private static readonly Dictionary<string, Texture2D> Icons = new(StringComparer.Ordinal);
    private static readonly Dictionary<TintKey, Texture2D> Tinted = new();
    private static readonly Dictionary<Tone, GUIStyle> Badges = new();
    private static readonly GUIStyle Sliced = new();
    private static readonly RectOffset BoxBorder = new(10, 10, 10, 10);
    private static readonly RectOffset SmallBorder = new(6, 6, 6, 6);

    /// <summary>The scale the styles were built at: the panel's size setting.</summary>
    public static float Scale { get; private set; } = 1f;

    /// <summary>Unity's skin with the game's font and slim scrollbars, put in place while the panel draws.</summary>
    public static GUISkin Gui { get; private set; }

    public static GUIStyle Title { get; private set; }

    public static GUIStyle Version { get; private set; }

    public static GUIStyle TabText { get; private set; }

    public static GUIStyle TabTextOn { get; private set; }

    public static GUIStyle CardTitle { get; private set; }

    public static GUIStyle SubHeading { get; private set; }

    public static GUIStyle Body { get; private set; }

    public static GUIStyle Small { get; private set; }

    /// <summary><see cref="Body"/> on a line of buttons: one row high, centred on it, not wrapped.</summary>
    public static GUIStyle InlineBody { get; private set; }

    /// <summary><see cref="Small"/> on a line of buttons.</summary>
    public static GUIStyle InlineSmall { get; private set; }

    public static GUIStyle RowLabel { get; private set; }

    public static GUIStyle RowValue { get; private set; }

    public static GUIStyle Button { get; private set; }

    public static GUIStyle Primary { get; private set; }

    public static GUIStyle Danger { get; private set; }

    public static GUIStyle Close { get; private set; }

    /// <summary>A small flat button, lit when the mouse is over it: the answer's Clear.</summary>
    public static GUIStyle Quiet { get; private set; }

    public static GUIStyle Field { get; private set; }

    /// <summary>A check box's words, with room on the left for its box.</summary>
    public static GUIStyle Check { get; private set; }

    public static GUIStyle CardBox { get; private set; }

    public static GUIStyle Well { get; private set; }

    public static GUIStyle WellTitle { get; private set; }

    public static GUIStyle Answer { get; private set; }

    /// <summary>A tool's name in a list row.</summary>
    public static GUIStyle RowName { get; private set; }

    /// <summary>The line under a tool's name that says what it gives.</summary>
    public static GUIStyle RowDesc { get; private set; }

    /// <summary>A hint: faint, wrapped.</summary>
    public static GUIStyle Note { get; private set; }

    public static GUIStyle SegmentOff { get; private set; }

    public static GUIStyle SegmentOn { get; private set; }

    /// <summary>Only the margins a choice of several keeps on a line.</summary>
    public static GUIStyle ChoiceTrack { get; private set; }

    public static GUIStyle CellHead { get; private set; }

    public static GUIStyle CellHeadRight { get; private set; }

    public static GUIStyle Cell { get; private set; }

    public static GUIStyle CellRight { get; private set; }

    /// <summary>A table cell that wraps its words, its row as tall as its longest cell.</summary>
    public static GUIStyle CellWrap { get; private set; }

    /// <summary>The sign in a check's mark: "!", "x" or "i".</summary>
    public static GUIStyle MarkGlyph { get; private set; }

    public static Texture2D Tick => _tick;

    public static Texture2D Circle => _pill;

    /// <summary>A small triangle pointing right: something folded that a click opens.</summary>
    public static Texture2D ChevronRight => _chevronRight;

    /// <summary>A small triangle pointing down: something open that a click folds.</summary>
    public static Texture2D ChevronDown => _chevronDown;

    /// <summary>A tool's result under it: a sunken box.</summary>
    public static GUIStyle ResultBox { get; private set; }

    /// <summary>A headline number in a result's tile, and the words under it.</summary>
    public static GUIStyle TileValue { get; private set; }

    public static GUIStyle TileLabel { get; private set; }

    /// <summary>Small words in charts: a bar's name, its value on the right, the words under columns.</summary>
    public static GUIStyle Caption { get; private set; }

    public static GUIStyle CaptionRight { get; private set; }

    public static GUIStyle CaptionCentre { get; private set; }

    /// <summary>A card's zone, in its colour at the right of its title.</summary>
    public static GUIStyle ZoneTag { get; private set; }

    /// <summary>The colour of a card's zone: its edge and its tag; clear for a card that only looks.</summary>
    public static Color Of(PanelZone zone) => zone switch
    {
        PanelZone.Measure => Accent,
        PanelZone.Load => Warn,
        PanelZone.Change => Bad,
        _ => Color.clear,
    };

    /// <summary>A result's tone as a mark's.</summary>
    public static Tone Of(Core.ResultTone tone) => tone switch
    {
        Core.ResultTone.Good => Tone.Good,
        Core.ResultTone.Caution => Tone.Caution,
        Core.ResultTone.Bad => Tone.Bad,
        _ => Tone.Quiet,
    };

    /// <summary>A small label in a tone, on a tint of it: the state of a section beside its title.</summary>
    public static GUIStyle Badge(Tone tone) => Badges.TryGetValue(tone, out var style) ? style : null;

    /// <summary>A length at the panel's scale.</summary>
    public static float U(float v) => v * Scale;

    /// <summary>Makes sure the styles exist at this scale. Only callable from OnGUI.</summary>
    public static void Ensure(float scale)
    {
        bool fontsMissing = _body == null || _heading == null;
        bool lookAgain = fontsMissing && Time.realtimeSinceStartup - _fontsLookedAt > 5f;
        if (Gui != null && Mathf.Approximately(scale, _builtScale) && !lookAgain)
        {
            return;
        }

        if (fontsMissing)
        {
            FindFonts();
        }

        Build(scale);
    }

    private static void Build(float scale)
    {
        _builtScale = scale;
        Scale = scale;
        MakeTextures();
        if (Gui == null)
        {
            Gui = UnityEngine.Object.Instantiate(GUI.skin);
            Gui.hideFlags = HideFlags.HideAndDontSave;
        }

        Gui.font = _body;

        int Px(float v) => Mathf.Max(1, Mathf.RoundToInt(v * scale));

        GUIStyle Style(string name, float size, Color color, FontStyle weight = FontStyle.Normal, TextAnchor anchor = TextAnchor.UpperLeft)
        {
            var style = new GUIStyle(Gui.label)
            {
                name = "vigil-" + name,
                font = _body,
                fontSize = Px(size),
                fontStyle = weight,
                alignment = anchor,
                wordWrap = false,
                richText = true,
                clipping = TextClipping.Clip,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(Px(6f), Px(6f), Px(3f), Px(3f)),
            };
            // Every state, so a pressed tab or a ticked box never shows Unity's own text colour.
            style.normal.textColor = style.hover.textColor = style.active.textColor = style.focused.textColor = color;
            style.onNormal.textColor = style.onHover.textColor = style.onActive.textColor = style.onFocused.textColor = color;
            return style;
        }

        GUIStyle Inline(string name, float size, Color color)
        {
            var style = Style(name, size, color, FontStyle.Normal, TextAnchor.MiddleLeft);
            style.fixedHeight = Px(LineHeight);
            style.margin = new RectOffset(Px(6f), Px(6f), Px(4f), Px(4f));
            return style;
        }

        Title = Style("title", 24f, Accent, FontStyle.Bold, TextAnchor.MiddleLeft);
        if (_heading != null)
        {
            Title.font = _heading;
        }

        Version = Style("version", 12f, Faint, FontStyle.Normal, TextAnchor.MiddleLeft);
        TabText = Style("tab", 14f, Dim, FontStyle.Normal, TextAnchor.MiddleCenter);
        // IMGUI shows a hover colour only for a state that has a background, so a clear one.
        TabText.hover.textColor = Text;
        TabText.hover.background = Tint(_small, Color.clear, null);
        TabTextOn = Style("tab-on", 14f, Accent, FontStyle.Bold, TextAnchor.MiddleCenter);

        CardTitle = Style("card-title", 14f, Text, FontStyle.Bold);
        CardTitle.margin = new RectOffset(Px(6f), Px(6f), 0, Px(8f));
        SubHeading = Style("sub-heading", 12f, Faint, FontStyle.Bold);
        SubHeading.margin = new RectOffset(Px(6f), Px(6f), Px(10f), Px(4f));
        Body = Style("body", 13f, Text);
        Body.wordWrap = true;
        Small = Style("small", 12f, Dim);
        Small.wordWrap = true;
        InlineBody = Inline("inline-body", 13f, Text);
        InlineSmall = Inline("inline-small", 12f, Faint);
        RowLabel = Style("row-label", 13f, Faint);
        RowLabel.margin = new RectOffset(Px(6f), Px(6f), Px(2f), Px(2f));
        RowValue = Style("row-value", 13f, Text);
        RowValue.wordWrap = true;
        RowValue.margin = RowLabel.margin;

        // Buttons are outlined in the accent with words in it: clearly something to press, quieter than the one filled Primary a tab.
        Button = Boxed(Inline("button", 13f, Accent), Color.clear, Alpha(Accent, 0.12f), Alpha(Accent, 0.22f), Px, Alpha(Accent, 0.55f), Alpha(Accent, 0.85f));
        Primary = Boxed(Inline("primary", 13f, OnAccent), Accent, Shade(Accent, 1.08f), Shade(Accent, 0.85f), Px);
        Primary.fontStyle = FontStyle.Bold;
        Danger = Boxed(Inline("danger", 13f, OnAccent), Warn, Shade(Warn, 1.08f), Shade(Warn, 0.85f), Px);
        Danger.fontStyle = FontStyle.Bold;

        Close = Boxed(Style("close", 20f, Dim, FontStyle.Normal, TextAnchor.MiddleCenter), Color.clear, RaisedHover, Shade(RaisedHover, 1.3f), Px);
        Close.hover.textColor = Text;
        Close.padding = new RectOffset(0, 0, 0, Px(2f));
        Quiet = Boxed(Style("quiet", 12f, Dim, FontStyle.Normal, TextAnchor.MiddleCenter), Color.clear, Light(0.06f), Light(0.10f), Px, Light(0.16f), Light(0.28f));
        Quiet.hover.textColor = Text;
        Quiet.padding = new RectOffset(Px(8f), Px(8f), 0, 0);
        Quiet.fixedHeight = Px(22f);
        Quiet.margin = new RectOffset(Px(6f), Px(6f), Px(6f), Px(6f));

        Field = new GUIStyle(Gui.textField)
        {
            name = "vigil-field",
            font = _body,
            fontSize = Px(13f),
            alignment = TextAnchor.MiddleLeft,
            fixedHeight = Px(LineHeight),
            padding = new RectOffset(Px(8f), Px(8f), 0, 0),
            margin = new RectOffset(Px(6f), Px(6f), Px(4f), Px(4f)),
            border = new RectOffset(10, 10, 10, 10),
            clipping = TextClipping.Clip,
        };
        Field.normal.background = Tint(_rounded, Sunken, Light(0.14f));
        Field.hover.background = Tint(_rounded, SunkenHover, Light(0.22f));
        Field.focused.background = Tint(_rounded, SunkenHover, Alpha(Accent, 0.65f));
        Field.onNormal.background = Field.normal.background;
        Field.normal.textColor = Field.hover.textColor = Field.focused.textColor = Text;
        Gui.settings.cursorColor = Accent;
        Gui.settings.selectionColor = Alpha(Accent, 0.35f);

        Check = Inline("check", 13f, Dim);
        Check.padding = new RectOffset(Px(24f), 0, 0, 0);
        Check.hover.textColor = Text;
        Check.hover.background = Check.onHover.background = Tint(_small, Color.clear, null);
        Check.active.textColor = Check.onNormal.textColor = Check.onHover.textColor = Check.onActive.textColor = Text;

        CardBox = new GUIStyle
        {
            name = "vigil-card",
            border = new RectOffset(10, 10, 10, 10),
            padding = new RectOffset(Px(14f), Px(14f), Px(12f), Px(12f)),
            margin = new RectOffset(0, 0, 0, Px(10f)),
        };
        CardBox.normal.background = Tint(_rounded, Card, null);
        Well = new GUIStyle
        {
            name = "vigil-well",
            border = new RectOffset(10, 10, 10, 10),
            padding = new RectOffset(Px(12f), Px(12f), Px(8f), Px(10f)),
        };
        Well.normal.background = Tint(_rounded, Sunken, Outline);
        WellTitle = Style("well-title", 12f, Faint, FontStyle.Bold, TextAnchor.MiddleLeft);
        WellTitle.fixedHeight = Px(22f);
        WellTitle.margin = new RectOffset(0, 0, 0, Px(4f));
        Answer = Style("answer", 12f, Text);
        Answer.wordWrap = true;
        Answer.margin = new RectOffset(0, 0, 0, 0);

        RowName = Style("row-name", 13f, Text, FontStyle.Normal, TextAnchor.MiddleLeft);
        RowDesc = Style("row-desc", 12f, Faint, FontStyle.Normal, TextAnchor.MiddleLeft);
        Note = Style("note", 12f, Faint);
        Note.wordWrap = true;

        // A choice of several: the options on a sunken track, the chosen one on a tint of the accent.
        SegmentOff = Style("segment-off", 12f, Dim, FontStyle.Normal, TextAnchor.MiddleCenter);
        SegmentOff.padding = new RectOffset(Px(10f), Px(10f), 0, 0);
        SegmentOff.border = SmallBorder;
        SegmentOff.hover.background = Tint(_small, Light(0.06f), null);
        SegmentOff.active.background = Tint(_small, Light(0.10f), null);
        SegmentOff.hover.textColor = Text;
        SegmentOn = Style("segment-on", 12f, Accent, FontStyle.Normal, TextAnchor.MiddleCenter);
        SegmentOn.padding = SegmentOff.padding;
        SegmentOn.border = SmallBorder;
        SegmentOn.normal.background = SegmentOn.hover.background = SegmentOn.active.background = Tint(_small, Alpha(Accent, 0.18f), null);
        ChoiceTrack = new GUIStyle { name = "vigil-choice", margin = new RectOffset(Px(6f), Px(6f), Px(4f), Px(4f)) };

        CellHead = Style("cell-head", 12f, Faint, FontStyle.Normal, TextAnchor.MiddleLeft);
        CellHeadRight = Style("cell-head-right", 12f, Faint, FontStyle.Normal, TextAnchor.MiddleRight);
        Cell = Style("cell", 13f, Text, FontStyle.Normal, TextAnchor.MiddleLeft);
        CellRight = Style("cell-right", 13f, Text, FontStyle.Normal, TextAnchor.MiddleRight);
        CellWrap = Style("cell-wrap", 13f, Text);
        CellWrap.wordWrap = true;
        CellWrap.margin = new RectOffset(0, 0, 0, 0);
        CellWrap.padding = new RectOffset(0, 0, Px(5f), Px(5f));
        MarkGlyph = Style("mark", 11f, OnAccent, FontStyle.Bold, TextAnchor.MiddleCenter);
        MarkGlyph.clipping = TextClipping.Overflow;

        ResultBox = new GUIStyle
        {
            name = "vigil-result",
            border = new RectOffset(10, 10, 10, 10),
            padding = new RectOffset(Px(12f), Px(12f), Px(6f), Px(10f)),
            margin = new RectOffset(0, 0, Px(2f), Px(8f)),
        };
        ResultBox.normal.background = Tint(_rounded, Sunken, Outline);
        TileValue = Style("tile-value", 16f, Text, FontStyle.Bold, TextAnchor.MiddleLeft);
        TileLabel = Style("tile-label", 11f, Faint, FontStyle.Normal, TextAnchor.MiddleLeft);
        Caption = Style("caption", 12f, Dim, FontStyle.Normal, TextAnchor.MiddleLeft);
        CaptionRight = Style("caption-right", 12f, Dim, FontStyle.Normal, TextAnchor.MiddleRight);
        CaptionCentre = Style("caption-centre", 11f, Faint, FontStyle.Normal, TextAnchor.MiddleCenter);
        ZoneTag = Style("zone-tag", 11f, Faint, FontStyle.Normal, TextAnchor.MiddleRight);
        ZoneTag.margin = new RectOffset(Px(6f), 0, 0, Px(8f));

        Badges.Clear();
        foreach (Tone tone in Enum.GetValues(typeof(Tone)))
        {
            var badge = Style("badge", 11f, Of(tone), FontStyle.Normal, TextAnchor.MiddleCenter);
            badge.border = SmallBorder;
            badge.padding = new RectOffset(Px(8f), Px(8f), 0, 0);
            badge.fixedHeight = Px(18f);
            badge.margin = new RectOffset(Px(8f), 0, Px(1f), 0);
            badge.normal.background = Tint(_small, Alpha(Of(tone), 0.15f), null);
            Badges[tone] = badge;
        }

        // Scrollbars: a slim thumb and no arrows; none across, as every text wraps.
        Gui.verticalScrollbar = new GUIStyle { fixedWidth = Px(8f), border = new RectOffset(4, 4, 4, 4), margin = new RectOffset(Px(6f), 0, 0, 0) };
        Gui.verticalScrollbar.normal.background = Tint(_bar, Light(0.03f), null);
        // IMGUI makes a thumb its share of the track plus its padding, so the padding is its
        // shortest length: a long tab keeps a thumb that can be seen and caught.
        Gui.verticalScrollbarThumb = new GUIStyle { fixedWidth = Px(8f), border = new RectOffset(4, 4, 4, 4), padding = new RectOffset(0, 0, Px(16f), Px(16f)) };
        Gui.verticalScrollbarThumb.normal.background = Tint(_bar, Light(0.16f), null);
        Gui.verticalScrollbarThumb.hover.background = Tint(_bar, Light(0.26f), null);
        Gui.verticalScrollbarThumb.active.background = Tint(_bar, Alpha(Accent, 0.7f), null);
        Gui.verticalScrollbarUpButton = new GUIStyle { fixedHeight = 0f, fixedWidth = 0f };
        Gui.verticalScrollbarDownButton = new GUIStyle { fixedHeight = 0f, fixedWidth = 0f };
    }

    /// <summary>A text style on a rounded box: its fill normal, under the mouse and pressed, and its outline if it has one (brighter under the mouse).</summary>
    private static GUIStyle Boxed(GUIStyle text, Color normal, Color hover, Color active, Func<float, int> px, Color? outline = null, Color? hoverOutline = null)
    {
        var style = new GUIStyle(text)
        {
            border = new RectOffset(10, 10, 10, 10),
            padding = new RectOffset(px(12f), px(12f), 0, 0),
        };
        style.hover.textColor = text.normal.textColor;
        style.active.textColor = text.normal.textColor;
        style.normal.background = Tint(_rounded, normal, outline);
        style.hover.background = Tint(_rounded, hover, hoverOutline ?? outline);
        style.active.background = Tint(_rounded, active, hoverOutline ?? outline);
        return style;
    }

    /// <summary>A flat fill, for lines and small marks too thin for the rounded box's corners.</summary>
    public static void Fill(Rect rect, Color color)
    {
        if (Event.current.type != EventType.Repaint)
        {
            return;
        }

        var was = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = was;
    }

    /// <summary>A rounded box in a fill, with an outline when one is given.</summary>
    public static void Box(Rect rect, Color fill, Color? outline = null)
    {
        if (Event.current.type != EventType.Repaint)
        {
            return;
        }

        Sliced.normal.background = Tint(_rounded, fill, outline);
        Sliced.border = BoxBorder;
        Sliced.Draw(rect, false, false, false, false);
    }

    /// <summary>
    /// A small rounded box (a check box, a mark, a choice's track), from a smaller shape: the big
    /// box's corners are wider than half of such a box and would meet in the middle.
    /// </summary>
    public static void SmallBox(Rect rect, Color fill, Color? outline = null)
    {
        if (Event.current.type != EventType.Repaint)
        {
            return;
        }

        Sliced.normal.background = Tint(_small, fill, outline);
        Sliced.border = SmallBorder;
        Sliced.Draw(rect, false, false, false, false);
    }

    public static void Icon(Rect rect, Texture2D texture, Color color)
    {
        if (Event.current.type != EventType.Repaint || texture == null)
        {
            return;
        }

        var was = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, true);
        GUI.color = was;
    }

    /// <summary>A colour darkened (or brightened) by a factor, its strength kept.</summary>
    public static Color Shade(Color color, float factor) =>
        new(Mathf.Clamp01(color.r * factor), Mathf.Clamp01(color.g * factor), Mathf.Clamp01(color.b * factor), color.a);

    /// <summary>A colour at another strength, its hue kept.</summary>
    public static Color Alpha(Color color, float alpha) => new(color.r, color.g, color.b, alpha);

    /// <summary>White at a strength, laid over the panel's dark to lift what is under it.</summary>
    public static Color Light(float alpha) => new(1f, 1f, 1f, alpha);

    /// <summary>The game's serif for text and its runic face for the title; Unity's default font stands in for either one not loaded yet.</summary>
    private static void FindFonts()
    {
        _fontsLookedAt = Time.realtimeSinceStartup;
        foreach (var font in Resources.FindObjectsOfTypeAll<Font>())
        {
            if (font == null)
            {
                continue;
            }

            string name = font.name;
            if (_body == null && name.Equals("AveriaSerifLibre-Regular", StringComparison.OrdinalIgnoreCase))
            {
                _body = font;
            }

            if (_heading == null && (name.Equals("Norsebold", StringComparison.OrdinalIgnoreCase) || name.Equals("Norse", StringComparison.OrdinalIgnoreCase)))
            {
                _heading = font;
            }
        }
    }

    // ------------------------------------------------------------------ shapes

    private static void MakeTextures()
    {
        if (_rounded != null)
        {
            return;
        }

        _rounded = Shape(32, 32, (x, y) => RoundedCoverage(x, y, 32, 32, 8f));
        _small = Shape(16, 16, (x, y) => RoundedCoverage(x, y, 16, 16, 4.5f));
        _pill = Shape(32, 32, (x, y) => RoundedCoverage(x, y, 32, 32, 16f));
        _tick = Shape(32, 32, TickCoverage);
        _chevronRight = Shape(32, 32, (x, y) => InTriangle(x, y, 11f, 7f, 11f, 25f, 23f, 16f));
        _chevronDown = Shape(32, 32, (x, y) => InTriangle(x, y, 7f, 22f, 25f, 22f, 16f, 10f));

        // A scrollbar's track and thumb: as wide as the bar, so its slices hold only the rounded ends.
        _bar = Shape(8, 16, (x, y) => RoundedCoverage(x, y, 8, 16, 4f));

        // The buttons' icons, drawn on a 32 grid with y up, strokes 2.2 wide.
        const float s = 2.2f;
        Icons["play"] = Shape(32, 32, (x, y) => InTriangle(x, y, 10f, 6f, 10f, 26f, 25f, 16f));
        Icons["stop"] = Shape(32, 32, (x, y) => RoundedCoverage(x - 8f, y - 8f, 16, 16, 2.5f) && x >= 8f && x <= 24f && y >= 8f && y <= 24f);
        Icons["doc"] = Shape(32, 32, (x, y) =>
            NearSegment(x, y, 9f, 5f, 23f, 5f, s) || NearSegment(x, y, 9f, 5f, 9f, 27f, s) || NearSegment(x, y, 23f, 5f, 23f, 21f, s)
            || NearSegment(x, y, 9f, 27f, 17f, 27f, s) || NearSegment(x, y, 17f, 27f, 23f, 21f, s)
            || NearSegment(x, y, 13f, 16f, 19f, 16f, s * 0.8f) || NearSegment(x, y, 13f, 11f, 19f, 11f, s * 0.8f));
        Icons["external"] = Shape(32, 32, (x, y) =>
            NearSegment(x, y, 7f, 7f, 7f, 22f, s) || NearSegment(x, y, 7f, 7f, 22f, 7f, s) || NearSegment(x, y, 22f, 7f, 22f, 14f, s)
            || NearSegment(x, y, 7f, 22f, 14f, 22f, s) || NearSegment(x, y, 14f, 14f, 25f, 25f, s)
            || NearSegment(x, y, 18f, 25f, 25f, 25f, s) || NearSegment(x, y, 25f, 25f, 25f, 18f, s));
        Icons["folder"] = Shape(32, 32, (x, y) =>
            NearSegment(x, y, 5f, 7f, 27f, 7f, s) || NearSegment(x, y, 5f, 7f, 5f, 24f, s) || NearSegment(x, y, 27f, 7f, 27f, 20f, s)
            || NearSegment(x, y, 5f, 24f, 13f, 24f, s) || NearSegment(x, y, 13f, 24f, 16f, 20f, s) || NearSegment(x, y, 16f, 20f, 27f, 20f, s));
        Icons["refresh"] = Shape(32, 32, RefreshCoverage);
        Icons["reset"] = Shape(32, 32, (x, y) => RefreshCoverage(32f - x, y));
        Icons["close"] = Shape(32, 32, (x, y) => NearSegment(x, y, 9f, 9f, 23f, 23f, s) || NearSegment(x, y, 9f, 23f, 23f, 9f, s));
        Icons["search"] = Shape(32, 32, (x, y) => Ring(x, y, 14f, 18f, 6.6f, 8.8f) || NearSegment(x, y, 19.8f, 12.2f, 26f, 6f, s * 1.1f));
        Icons["download"] = Shape(32, 32, (x, y) => NearSegment(x, y, 16f, 27f, 16f, 12f, s) || InTriangle(x, y, 9f, 14f, 23f, 14f, 16f, 6.5f) || NearSegment(x, y, 6f, 4.5f, 26f, 4.5f, s));
        Icons["eye"] = Shape(32, 32, EyeCoverage);
        Icons["eye-off"] = Shape(32, 32, (x, y) => EyeCoverage(x, y) || NearSegment(x, y, 6f, 6f, 26f, 26f, s));
        Icons["upload"] = Shape(32, 32, (x, y) => NearSegment(x, y, 16f, 8f, 16f, 20f, s) || InTriangle(x, y, 9f, 18f, 23f, 18f, 16f, 26.5f) || NearSegment(x, y, 6f, 4.5f, 26f, 4.5f, s));
        Icons["check"] = Shape(32, 32, TickCoverage);
        Icons["compare"] = Shape(32, 32, (x, y) => BoxCoverage(x, y, 5.5f, 6f, 13.5f, 26f, s * 0.9f) || BoxCoverage(x, y, 18.5f, 6f, 26.5f, 26f, s * 0.9f));
        Icons["copy"] = Shape(32, 32, (x, y) => BoxCoverage(x, y, 12f, 5f, 26f, 21f, s * 0.9f)
            || (BoxCoverage(x, y, 6f, 11f, 20f, 27f, s * 0.9f) && !(x > 12f - s * 2f && y < 21f + s * 2f)));
        Icons["trash"] = Shape(32, 32, (x, y) =>
            NearSegment(x, y, 5.5f, 23f, 26.5f, 23f, s) || NearSegment(x, y, 12.5f, 26.5f, 19.5f, 26.5f, s)
            || NearSegment(x, y, 8.5f, 23f, 10f, 4.5f, s) || NearSegment(x, y, 23.5f, 23f, 22f, 4.5f, s) || NearSegment(x, y, 10f, 4.5f, 22f, 4.5f, s)
            || NearSegment(x, y, 16f, 9.5f, 16f, 18f, s * 0.8f));
        Icons["pin"] = Shape(32, 32, PinCoverage);
        Icons["pin-off"] = Shape(32, 32, (x, y) => PinCoverage(x, y) || NearSegment(x, y, 6f, 26f, 26f, 6f, s));
        Icons["wait"] = Shape(32, 32, (x, y) =>
            Ring(x, y, 16f, 16f, 8.6f, 10.8f) || NearSegment(x, y, 16f, 16f, 16f, 22.5f, s * 0.9f) || NearSegment(x, y, 16f, 16f, 20.5f, 16f, s * 0.9f));
    }

    /// <summary>A circling arrow, open at the top right with its head there; drawn mirrored it turns back (reset).</summary>
    private static bool RefreshCoverage(float x, float y) =>
        (Ring(x, y, 16f, 16f, 7.4f, 9.6f) && !(x > 16f && y > 16f && y - 16f > (x - 16f) * 0.27f))
        || InTriangle(x, y, 20.8f, 17.3f, 27.6f, 19.1f, 21.5f, 22.5f);

    /// <summary>A map pin: a round head with a hole, on a point below it.</summary>
    private static bool PinCoverage(float x, float y) =>
        (Ring(x, y, 16f, 19.5f, 0f, 8f) || InTriangle(x, y, 9.6f, 16.5f, 22.4f, 16.5f, 16f, 3.5f)) && Distance(x, y, 16f, 19.5f) > 3.2f;

    /// <summary>A rectangle's outline.</summary>
    private static bool BoxCoverage(float x, float y, float x0, float y0, float x1, float y1, float radius) =>
        NearSegment(x, y, x0, y0, x1, y0, radius) || NearSegment(x, y, x1, y0, x1, y1, radius)
        || NearSegment(x, y, x1, y1, x0, y1, radius) || NearSegment(x, y, x0, y1, x0, y0, radius);

    /// <summary>An eye: a lens two arcs make, and its pupil.</summary>
    private static bool EyeCoverage(float x, float y)
    {
        bool InLens(float r) => Distance(x, y, 16f, 6f) <= r && Distance(x, y, 16f, 26f) <= r;
        return (InLens(15.5f) && !InLens(13.2f)) || Distance(x, y, 16f, 16f) <= 3.6f;
    }

    private static bool Ring(float x, float y, float cx, float cy, float inner, float outer)
    {
        float d = Distance(x, y, cx, cy);
        return d >= inner && d <= outer;
    }

    private static float Distance(float x, float y, float cx, float cy) => Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));

    /// <summary>The icon a button's words call for, by its first word (<see cref="ButtonIcon"/>); null for words with none.</summary>
    public static Texture2D IconFor(string label)
    {
        string name = ButtonIcon.For(label);
        return name != null && Icons.TryGetValue(name, out var icon) ? icon : null;
    }

    /// <summary>The close icon, for buttons that are only it.</summary>
    public static Texture2D CloseIcon => Icons.TryGetValue("close", out var icon) ? icon : null;

    /// <summary>Whether a point is inside a triangle: on the same side of all three edges.</summary>
    private static bool InTriangle(float x, float y, float ax, float ay, float bx, float by, float cx, float cy)
    {
        float d1 = (x - bx) * (ay - by) - (ax - bx) * (y - by);
        float d2 = (x - cx) * (by - cy) - (bx - cx) * (y - cy);
        float d3 = (x - ax) * (cy - ay) - (cx - ax) * (y - ay);
        bool negative = d1 < 0 || d2 < 0 || d3 < 0, positive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(negative && positive);
    }

    /// <summary>A tick: a short stroke down to the right, a long one up to the right.</summary>
    private static bool TickCoverage(float x, float y) =>
        NearSegment(x, y, 7f, 17f, 13f, 10.5f, 2.6f) || NearSegment(x, y, 13f, 10.5f, 25f, 23f, 2.6f);

    private static bool NearSegment(float x, float y, float ax, float ay, float bx, float by, float radius)
    {
        float dx = bx - ax, dy = by - ay;
        float t = Mathf.Clamp01(((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy));
        float px = ax + t * dx - x, py = ay + t * dy - y;
        return px * px + py * py <= radius * radius;
    }

    private static bool RoundedCoverage(float x, float y, float w, float h, float r)
    {
        float cx = Mathf.Clamp(x, r, w - r);
        float cy = Mathf.Clamp(y, r, h - r);
        float dx = x - cx;
        float dy = y - cy;
        return dx * dx + dy * dy <= r * r;
    }

    /// <summary>A white shape whose alpha is the coverage function, sampled 16 times a pixel for smooth edges.</summary>
    private static Texture2D Shape(int width, int height, Func<float, float, bool> inside)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };
        var pixels = new Color32[width * height];
        const int samples = 4;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int hits = 0;
                for (int sy = 0; sy < samples; sy++)
                {
                    for (int sx = 0; sx < samples; sx++)
                    {
                        if (inside(x + (sx + 0.5f) / samples, y + (sy + 0.5f) / samples))
                        {
                            hits++;
                        }
                    }
                }

                pixels[y * width + x] = new Color32(255, 255, 255, (byte)(hits * 255 / (samples * samples)));
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        return texture;
    }

    /// <summary>A copy of a white shape in a colour, with an optional outline, made once per colour.</summary>
    private static Texture2D Tint(Texture2D shape, Color fill, Color? outline)
    {
        var key = new TintKey(shape, fill, outline);
        if (Tinted.TryGetValue(key, out var known) && known != null)
        {
            return known;
        }

        var source = shape.GetPixels32();
        var pixels = new Color32[source.Length];
        int w = shape.width;
        int h = shape.height;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float a = source[y * w + x].a / 255f;
                var color = fill;
                if (outline.HasValue && a > 0f)
                {
                    // An outline where the shape's coverage drops off within a pixel or so of its edge.
                    int edge = EdgeDistance(source, w, h, x, y);
                    if (edge <= 1)
                    {
                        color = Color.Lerp(outline.Value, fill, edge == 1 ? 0.35f : 0f);
                    }
                }

                pixels[y * w + x] = new Color(color.r, color.g, color.b, color.a * a);
            }
        }

        var texture = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };
        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        Tinted[key] = texture;
        return texture;
    }

    private static int EdgeDistance(Color32[] pixels, int w, int h, int x, int y)
    {
        for (int d = 0; d <= 1; d++)
        {
            for (int oy = -d - 1; oy <= d + 1; oy++)
            {
                for (int ox = -d - 1; ox <= d + 1; ox++)
                {
                    int nx = x + ox;
                    int ny = y + oy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h || pixels[ny * w + nx].a < 128)
                    {
                        return d;
                    }
                }
            }
        }

        return 2;
    }

    /// <summary>A shape in a fill and an outline, as a key for its tinted copy.</summary>
    private readonly struct TintKey : IEquatable<TintKey>
    {
        private readonly int _shape;
        private readonly Color _fill;
        private readonly Color _outline;
        private readonly bool _outlined;

        public TintKey(Texture2D shape, Color fill, Color? outline)
        {
            _shape = shape.GetInstanceID();
            _fill = fill;
            _outline = outline ?? default;
            _outlined = outline.HasValue;
        }

        public bool Equals(TintKey other) => _shape == other._shape && _fill == other._fill && _outline == other._outline && _outlined == other._outlined;

        public override bool Equals(object obj) => obj is TintKey other && Equals(other);

        public override int GetHashCode() => ((_shape * 31 + _fill.GetHashCode()) * 31 + _outline.GetHashCode()) * 2 + (_outlined ? 1 : 0);
    }
}
