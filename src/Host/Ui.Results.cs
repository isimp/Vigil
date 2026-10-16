using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Vigil.Core;

namespace Vigil;

/// <summary>
/// Results in the panel: a tool's last result in a sunken box under the tool, an incident's under
/// its row once opened. The box's header holds a fold, the result's badge and time, the file it
/// was saved as with a button to open it, and a close button; its parts follow in order. A result
/// folded stays folded while the panel is closed and opened again, until a new one comes; a
/// result's table starts folded. Every part draws with a fixed number of rows for its data, so
/// IMGUI's layout pass and its drawing pass agree whatever the panel's width.
/// </summary>
internal static partial class Ui
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>The tables opened this session, by their result and title.</summary>
    private static readonly HashSet<string> OpenTables = new(StringComparer.Ordinal);

    /// <summary>When each shown result was made, by its fold name: a new one opens where the last was folded.</summary>
    private static readonly Dictionary<string, DateTime> Seen = new(StringComparer.Ordinal);

    /// <summary>Each table's column widths before scaling, measured once per table and scale.</summary>
    private static readonly Dictionary<TableBlock, float[]> TableWidths = new();

    private static float _tableWidthsScale;

    /// <summary>The folded cards and results ("card:Tab/Title", "result:tool"); the panel keeps them between openings.</summary>
    public static HashSet<string> Folds { get; set; } = new(StringComparer.Ordinal);

    public static void SetFolded(string name, bool folded)
    {
        if (folded)
        {
            Folds.Add(name);
        }
        else
        {
            Folds.Remove(name);
        }
    }

    /// <summary>A fold's triangle in <paramref name="icon"/>; true when <paramref name="area"/> was clicked (reported on the next layout pass).</summary>
    public static bool Fold(Rect icon, Rect area, bool open)
    {
        float size = Skin.U(12f);
        Skin.Icon(new Rect(icon.x + (icon.width - size) / 2f, icon.y + (icon.height - size) / 2f, size, size), open ? Skin.ChevronDown : Skin.ChevronRight, Skin.Dim);
        return Report(Key("fold"), GUI.Button(area, GUIContent.none, GUIStyle.none));
    }

    /// <summary>
    /// A result in its box. <paramref name="name"/> is its fold's name; <paramref name="close"/>
    /// runs when its close button is clicked (null for none). A result opened from a row that
    /// folds it already (an incident's) has no fold of its own.
    /// </summary>
    public static void ShowResult(Result r, string name, Action close, bool foldable = true)
    {
        if (r == null)
        {
            return;
        }

        if (!Seen.TryGetValue(name, out var seen) || seen != r.At)
        {
            Seen[name] = r.At;
            Folds.Remove(name);
        }

        bool open = !foldable || !Folds.Contains(name);
        int was = _rows;
        GUILayout.BeginVertical(Skin.ResultBox);
        if (ResultHeader(r, open, close, foldable))
        {
            Later(() => SetFolded(name, open));
        }

        if (open)
        {
            foreach (var block in r.Blocks)
            {
                Part(block, name);
            }
        }

        GUILayout.EndVertical();
        _rows = was;
    }

    /// <summary>The header line; true when the fold was clicked.</summary>
    private static bool ResultHeader(Result r, bool open, Action close, bool foldable)
    {
        var head = GUILayoutUtility.GetRect(0f, Skin.U(26f), GUILayout.ExpandWidth(true));
        float x = head.x, right = head.xMax, gap = Skin.U(8f);

        float shutWidth = Skin.U(24f);
        var shutRect = new Rect(right - shutWidth, head.y + Skin.U(2f), shutWidth, Skin.U(22f));
        if (close != null)
        {
            right = shutRect.x - Skin.U(2f);
        }

        Rect openRect = default;
        if (r.Path != null)
        {
            float openWidth = Skin.Quiet.CalcSize(new GUIContent(PathCopy.Idle)).x + Skin.U(IconSize + IconGap);
            openRect = new Rect(right - openWidth, head.y + Skin.U(2f), openWidth, Skin.U(22f));
            right = openRect.x - gap;
        }

        if (r.File != null)
        {
            string saved = "Saved as " + r.File;
            float savedWidth = Mathf.Min(Skin.CaptionRight.CalcSize(new GUIContent(saved)).x, Mathf.Max(0f, (right - x) * 0.55f));
            GUI.Label(new Rect(right - savedWidth, head.y, savedWidth, head.height), saved, Skin.CaptionRight);
            right -= savedWidth + gap;
        }

        var foldRect = new Rect(x, head.y, Mathf.Max(0f, right - x), head.height);
        if (foldable)
        {
            Skin.Icon(new Rect(x, head.y + (head.height - Skin.U(12f)) / 2f, Skin.U(12f), Skin.U(12f)), open ? Skin.ChevronDown : Skin.ChevronRight, Skin.Dim);
            x += Skin.U(18f);
        }
        if (r.Badge != null)
        {
            var badge = Skin.Badge(Skin.Of(r.BadgeTone));
            float width = Mathf.Min(badge.CalcSize(new GUIContent(r.Badge)).x, Mathf.Max(0f, right - x));
            GUI.Label(new Rect(x, head.y + (head.height - badge.fixedHeight) / 2f, width, badge.fixedHeight), r.Badge, badge);
            x += width + gap;
        }

        GUI.Label(new Rect(x, head.y, Mathf.Max(0f, right - x), head.height), r.At.ToString("HH:mm:ss", Ci), Skin.TileLabel);

        bool folded = foldable && Report(Key("fold"), GUI.Button(foldRect, GUIContent.none, GUIStyle.none));
        if (r.Path != null && Report(Key("copy"), IconButton(openRect, PathClipboard.Label(r.Path), Skin.Quiet)))
        {
            PathClipboard.Copy(r.Path);
        }

        if (close != null && Report(Key("close"), CloseButton(shutRect)))
        {
            Later(close);
        }

        return folded;
    }

    /// <summary>A small outlined button that is only a cross; true when clicked this pass.</summary>
    private static bool CloseButton(Rect rect)
    {
        bool clicked = GUI.Button(rect, GUIContent.none, Skin.Quiet);
        float size = Skin.U(10f);
        Skin.Icon(new Rect(rect.x + (rect.width - size) / 2f, rect.y + (rect.height - size) / 2f, size, size), Skin.CloseIcon, Skin.Dim);
        return clicked;
    }

    private static void Part(ResultBlock block, string name)
    {
        switch (block)
        {
            case VerdictBlock v:
                Verdict(v.Tone, v.Text);
                break;
            case TilesBlock t:
                Tiles(t);
                break;
            case BarsBlock b:
                Bars(b);
                break;
            case ColumnsBlock c:
                Columns(c);
                break;
            case RangesBlock g:
                Ranges(g);
                break;
            case StripBlock s:
                Strip(s);
                break;
            case TextBlock x:
                PartTitle(x.Title);
                GUILayout.Label(x.Text, Skin.Small);
                break;
            case TableBlock table:
                ResultTable(table, name);
                break;
        }
    }

    private static void PartTitle(string title)
    {
        if (title != null)
        {
            GUILayout.Label(title, Skin.SubHeading);
        }
    }

    /// <summary>Headline numbers side by side, each on a tile, its words under it (two lines at most).</summary>
    private static void Tiles(TilesBlock t)
    {
        if (t.Tiles.Count == 0)
        {
            return;
        }

        var rect = GUILayoutUtility.GetRect(0f, Skin.U(58f), GUILayout.ExpandWidth(true));
        rect = new Rect(rect.x, rect.y + Skin.U(4f), rect.width, rect.height - Skin.U(4f));
        float gap = Skin.U(8f), pad = Skin.U(8f);
        float width = (rect.width - gap * (t.Tiles.Count - 1)) / t.Tiles.Count;
        for (int i = 0; i < t.Tiles.Count; i++)
        {
            var tile = new Rect(rect.x + i * (width + gap), rect.y, width, rect.height);
            Skin.SmallBox(tile, Skin.Card);
            GUI.Label(new Rect(tile.x + pad, tile.y + Skin.U(4f), tile.width - pad * 2f, Skin.U(22f)), t.Tiles[i].Value, Skin.TileValue);
            GUI.Label(new Rect(tile.x + pad, tile.y + Skin.U(25f), tile.width - pad * 2f, tile.height - Skin.U(27f)), t.Tiles[i].Key, Skin.TileLabel);
        }
    }

    /// <summary>A bar a row to scale with the longest, its name on the left and its value on the right; a split bar in two colours with a legend.</summary>
    private static void Bars(BarsBlock b)
    {
        PartTitle(b.Title);
        if (b.Note != null)
        {
            GUILayout.Label(b.Note, Skin.Note);
        }

        if (b.PartName != null)
        {
            var legend = GUILayoutUtility.GetRect(0f, Skin.U(18f), GUILayout.ExpandWidth(true));
            float x = legend.x;
            foreach (var (text, colour) in new[] { (b.PartName, Skin.Light(0.4f)), (b.RestName, Skin.Accent) })
            {
                Skin.Fill(new Rect(x, legend.y + Skin.U(5f), Skin.U(10f), Skin.U(8f)), colour);
                float w = Skin.Caption.CalcSize(new GUIContent(text)).x;
                GUI.Label(new Rect(x + Skin.U(14f), legend.y, w, legend.height), text, Skin.Caption);
                x += Skin.U(14f) + w + Skin.U(16f);
            }
        }

        double max = 0;
        foreach (var bar in b.Bars)
        {
            max = Math.Max(max, bar.Value);
        }

        foreach (var bar in b.Bars)
        {
            var row = GUILayoutUtility.GetRect(0f, Skin.U(22f), GUILayout.ExpandWidth(true));
            float labelWidth = Mathf.Min(row.width * 0.36f, Skin.U(230f)), textWidth = Mathf.Min(row.width * 0.3f, Skin.U(210f)), gap = Skin.U(8f);
            GUI.Label(new Rect(row.x, row.y, labelWidth, row.height), bar.Label, Skin.Caption);
            GUI.Label(new Rect(row.xMax - textWidth, row.y, textWidth, row.height), bar.Text, Skin.CaptionRight);
            var track = new Rect(row.x + labelWidth + gap, row.y + Skin.U(7f), Mathf.Max(0f, row.width - labelWidth - textWidth - gap * 2f), Skin.U(8f));
            Skin.Fill(track, Skin.Light(0.08f));
            float whole = max > 0 ? (float)(bar.Value / max) * track.width : 0f;
            if (bar.Value > 0)
            {
                whole = Mathf.Max(whole, Skin.U(2f));
            }

            if (double.IsNaN(bar.Part))
            {
                Skin.Fill(new Rect(track.x, track.y, whole, track.height), b.PartName != null ? Skin.Accent : Skin.Alpha(Skin.Accent, 0.8f));
            }
            else
            {
                float part = bar.Value > 0 ? whole * Mathf.Clamp01((float)(bar.Part / bar.Value)) : 0f;
                Skin.Fill(new Rect(track.x, track.y, part, track.height), Skin.Light(0.4f));
                Skin.Fill(new Rect(track.x + part, track.y, whole - part, track.height), Skin.Accent);
            }
        }
    }

    /// <summary>Columns side by side in their tones, a dashed line at the threshold, the steps' names under them, and the numbers of the one under the mouse.</summary>
    private static void Columns(ColumnsBlock c)
    {
        PartTitle(c.Title);
        if (c.Columns.Count == 0)
        {
            return;
        }

        var rect = GUILayoutUtility.GetRect(0f, Skin.U(112f), GUILayout.ExpandWidth(true));
        var hoverLine = GUILayoutUtility.GetRect(0f, Skin.U(18f), GUILayout.ExpandWidth(true));
        float labels = Skin.U(16f), top = rect.y + Skin.U(14f), bottom = rect.yMax - labels;
        float height = bottom - top, slot = rect.width / c.Columns.Count;
        float width = Mathf.Min(slot * 0.62f, Skin.U(34f));
        double max = c.Max > 0 ? c.Max : 1;
        string hover = null;
        Skin.Fill(new Rect(rect.x, bottom, rect.width, Mathf.Max(1f, Skin.U(1f))), Skin.Light(0.12f));
        for (int i = 0; i < c.Columns.Count; i++)
        {
            var col = c.Columns[i];
            float h = Mathf.Clamp01((float)(col.Value / max)) * height;
            float x = rect.x + i * slot + (slot - width) / 2f;
            Skin.Fill(new Rect(x, bottom - h, width, h), ToneColour(col.Tone));
            GUI.Label(new Rect(rect.x + i * slot, bottom, slot, labels), col.Label, Skin.CaptionCentre);
            if (new Rect(rect.x + i * slot, rect.y, slot, rect.height).Contains(Event.current.mousePosition))
            {
                hover = col.Hover;
            }
        }

        if (!double.IsNaN(c.Threshold))
        {
            float y = bottom - Mathf.Clamp01((float)(c.Threshold / max)) * height;
            for (float x = rect.x; x < rect.xMax; x += Skin.U(10f))
            {
                Skin.Fill(new Rect(x, y, Mathf.Min(Skin.U(6f), rect.xMax - x), Mathf.Max(1f, Skin.U(1f))), Skin.Light(0.45f));
            }

            GUI.Label(new Rect(rect.x, y - Skin.U(16f), rect.width, Skin.U(15f)), c.ThresholdLabel, Skin.CaptionRight);
        }

        GUI.Label(hoverLine, hover ?? "Steps in " + (c.Unit ?? "order") + "; point at one for its numbers.", hover != null ? Skin.Caption : Skin.TileLabel);
    }

    /// <summary>A span a row, from its quiet value to its busy one, on one scale.</summary>
    private static void Ranges(RangesBlock g)
    {
        PartTitle(g.Title);
        double max = g.Max > 0 ? g.Max : 1;
        foreach (var range in g.Ranges)
        {
            var row = GUILayoutUtility.GetRect(0f, Skin.U(24f), GUILayout.ExpandWidth(true));
            float labelWidth = Mathf.Min(row.width * 0.3f, Skin.U(150f)), textWidth = Mathf.Min(row.width * 0.3f, Skin.U(170f)), gap = Skin.U(8f);
            GUI.Label(new Rect(row.x, row.y, labelWidth, row.height), range.Label, Skin.Caption);
            GUI.Label(new Rect(row.xMax - textWidth, row.y, textWidth, row.height), range.Text, Skin.CaptionRight);
            var track = new Rect(row.x + labelWidth + gap, row.y + row.height / 2f - Skin.U(1f), Mathf.Max(0f, row.width - labelWidth - textWidth - gap * 2f), Skin.U(2f));
            Skin.Fill(track, Skin.Light(0.08f));
            float from = track.x + Mathf.Clamp01((float)(range.From / max)) * track.width;
            float to = track.x + Mathf.Clamp01((float)(range.To / max)) * track.width;
            Skin.Fill(new Rect(Mathf.Min(from, to), track.y - Skin.U(2f), Mathf.Abs(to - from), Skin.U(6f)), Skin.Alpha(Skin.Caution, 0.55f));
            float dot = Skin.U(9f);
            Skin.Icon(new Rect(from - dot / 2f, track.y + Skin.U(1f) - dot / 2f, dot, dot), Skin.Circle, Skin.Faint);
            Skin.Icon(new Rect(to - dot / 2f, track.y + Skin.U(1f) - dot / 2f, dot, dot), Skin.Circle, Skin.Caution);
            if (!double.IsNaN(g.Mark))
            {
                float mark = track.x + Mathf.Clamp01((float)(g.Mark / max)) * track.width;
                Skin.Fill(new Rect(mark, row.y + Skin.U(4f), Mathf.Max(1f, Skin.U(1f)), row.height - Skin.U(8f)), Skin.Light(0.45f));
            }
        }

        if (!double.IsNaN(g.Mark) && g.MarkLabel != null)
        {
            GUILayout.Label("The line marks " + g.MarkLabel + ".", Skin.TileLabel);
        }
    }

    /// <summary>A thin column a second, oldest on the left, amber and red from its limits, and the second under the mouse.</summary>
    private static void Strip(StripBlock s)
    {
        PartTitle(s.Title);
        if (s.Values.Count == 0)
        {
            return;
        }

        var rect = GUILayoutUtility.GetRect(0f, Skin.U(46f), GUILayout.ExpandWidth(true));
        var under = GUILayoutUtility.GetRect(0f, Skin.U(16f), GUILayout.ExpandWidth(true));
        // Scaled to the red limit, or to the largest value when that is higher or there is no limit.
        double max = double.IsInfinity(s.Bad) || double.IsNaN(s.Bad) ? 0 : s.Bad;
        foreach (double v in s.Values)
        {
            if (!double.IsNaN(v))
            {
                max = Math.Max(max, v);
            }
        }

        max = max > 0 ? max : 1;
        float slot = rect.width / s.Values.Count, width = Mathf.Max(1f, slot - Mathf.Max(1f, Skin.U(1f)));
        string hover = null;
        for (int i = 0; i < s.Values.Count; i++)
        {
            double v = s.Values[i];
            bool measured = !double.IsNaN(v);
            if (measured)
            {
                float h = Mathf.Max(Skin.U(1f), Mathf.Clamp01((float)(v / max)) * rect.height);
                Skin.Fill(new Rect(rect.x + i * slot, rect.yMax - h, width, h), v >= s.Bad ? Skin.Bad : v >= s.Warn ? Skin.Caution : Skin.Light(0.22f));
            }

            if (new Rect(rect.x + i * slot, rect.y, slot, rect.height).Contains(Event.current.mousePosition))
            {
                int ago = s.Values.Count - 1 - i;
                hover = (ago == 0 ? s.Right : "-" + ago.ToString(Ci) + " s") + ": " + (measured ? v.ToString(v < 10 ? "0.0" : "0", Ci) + " " + s.Unit : "not measured");
            }
        }

        GUI.Label(under, s.Left, Skin.TileLabel);
        GUI.Label(under, s.Right, Skin.CaptionRight);
        if (hover != null)
        {
            GUI.Label(under, hover, Skin.CaptionCentre);
        }
    }

    /// <summary>A result's table behind a fold line with its title and size; folded until opened.</summary>
    private static void ResultTable(TableBlock t, string name)
    {
        string key = name + "/" + t.Title;
        bool open = OpenTables.Contains(key);
        var line = GUILayoutUtility.GetRect(0f, Skin.U(26f), GUILayout.ExpandWidth(true));
        float size = Skin.U(12f);
        Skin.Icon(new Rect(line.x, line.y + (line.height - size) / 2f, size, size), open ? Skin.ChevronDown : Skin.ChevronRight, Skin.Dim);
        GUI.Label(new Rect(line.x + Skin.U(18f), line.y, line.width - Skin.U(18f), line.height),
            (t.Title ?? "Detail") + "<color=#F5B857>   " + t.Rows.Count.ToString(Ci) + (t.Rows.Count == 1 ? " row" : " rows") + "</color>", Skin.Caption);
        if (Report(Key("table"), GUI.Button(line, GUIContent.none, GUIStyle.none)))
        {
            Later(() =>
            {
                if (!OpenTables.Remove(key))
                {
                    OpenTables.Add(key);
                }
            });
        }

        if (open && t.Rows.Count > 0)
        {
            var widths = WidthsOf(t);
            var columns = new List<Column>();
            for (int i = 0; i < t.Headers.Count; i++)
            {
                columns.Add(new Column(t.Headers[i], i == 0 ? 0f : widths[i], i < t.Right.Count && t.Right[i]));
            }

            Table(columns, t.Rows);
        }
    }

    /// <summary>Each column as wide as its widest cell or heading, at most 220 before scaling.</summary>
    private static float[] WidthsOf(TableBlock t)
    {
        if (!Mathf.Approximately(_tableWidthsScale, Skin.Scale) || TableWidths.Count > 32)
        {
            TableWidths.Clear();
            _tableWidthsScale = Skin.Scale;
        }

        if (TableWidths.TryGetValue(t, out var widths))
        {
            return widths;
        }

        widths = new float[t.Headers.Count];
        for (int i = 0; i < widths.Length; i++)
        {
            float w = Skin.CellHead.CalcSize(new GUIContent(t.Headers[i])).x;
            foreach (var row in t.Rows)
            {
                if (i < row.Length)
                {
                    w = Mathf.Max(w, Skin.Cell.CalcSize(new GUIContent(row[i])).x);
                }
            }

            widths[i] = Mathf.Min(w / Skin.Scale + 6f, 220f);
        }

        TableWidths[t] = widths;
        return widths;
    }

    /// <summary>A row of a list that opens what is under it: its triangle, then its cells as a <see cref="Table"/> lays them out; true when clicked.</summary>
    public static bool ExpandRow(IList<Column> columns, string[] cells, bool open)
    {
        var row = NextRow(Skin.U(26f));
        if (row.Contains(Event.current.mousePosition))
        {
            Skin.Fill(row, Skin.Light(0.03f));
        }

        float size = Skin.U(12f), indent = Skin.U(18f);
        Skin.Icon(new Rect(row.x, row.y + (row.height - size) / 2f, size, size), open ? Skin.ChevronDown : Skin.ChevronRight, Skin.Dim);
        if (Event.current.type == EventType.Repaint)
        {
            var area = new Rect(row.x + indent, row.y, row.width - indent, row.height);
            // Rows like this stand under a heading-only Table, one per entry: the set widths keep them lined up.
            Cells(area, columns, FixedWidths(columns, area.width), i => i < cells.Length ? cells[i] : "", false);
        }
        return Report(Key("row"), GUI.Button(row, GUIContent.none, GUIStyle.none));
    }

    /// <summary>A sentence with a round mark in its tone, wrapped to the width.</summary>
    public static void Verdict(ResultTone tone, string text)
    {
        // The mark sits beside the sentence's first line, however many lines it wraps to.
        GUILayout.BeginHorizontal();
        var at = GUILayoutUtility.GetRect(Skin.U(15f), Skin.U(18f), GUILayout.ExpandWidth(false));
        float size = Skin.U(9f);
        Skin.Icon(new Rect(at.x + Skin.U(4f), at.y + Skin.Body.margin.top + (Skin.Body.lineHeight - size) / 2f, size, size), Skin.Circle, Skin.Of(Skin.Of(tone)));
        GUILayout.Label(text, Skin.Body);
        GUILayout.EndHorizontal();
    }

    /// <summary>
    /// A row that opens what is under it and has a button of its own on the list's right edge: its
    /// triangle, a dot in a colour, a name, words, and a value beside the button. True when the
    /// button was clicked; <paramref name="toggled"/> is true when the rest of the row was.
    /// </summary>
    public static bool ExpandTool(Color dot, string name, string words, string value, bool open, string action, out bool toggled)
    {
        var row = NextRow(Skin.U(30f));
        float actionWidth = Skin.U(ActionWidth), gap = Skin.U(10f), line = Skin.U(Skin.LineHeight);
        var act = new Rect(row.xMax - actionWidth, row.y + (row.height - line) / 2f, actionWidth, line);
        var body = new Rect(row.x, row.y, Mathf.Max(0f, act.x - gap - row.x), row.height);
        if (body.Contains(Event.current.mousePosition))
        {
            Skin.Fill(body, Skin.Light(0.03f));
        }

        float size = Skin.U(12f), dotSize = Skin.U(9f), nameWidth = Skin.U(130f), valueWidth = Skin.U(90f);
        Skin.Icon(new Rect(row.x, row.y + (row.height - size) / 2f, size, size), open ? Skin.ChevronDown : Skin.ChevronRight, Skin.Dim);
        float x = row.x + Skin.U(18f);
        Skin.Icon(new Rect(x, row.y + (row.height - dotSize) / 2f, dotSize, dotSize), Skin.Circle, dot);
        x += dotSize + Skin.U(8f);
        GUI.Label(new Rect(x, row.y, nameWidth, row.height), name, Skin.Cell);
        x += nameWidth + gap;
        float wordsWidth = Mathf.Max(0f, body.xMax - valueWidth - gap - x);
        GUI.Label(new Rect(x, row.y, wordsWidth, row.height), words, Skin.Cell);
        GUI.Label(new Rect(body.xMax - valueWidth, row.y, valueWidth, row.height), value, Skin.CellRight);
        toggled = Report(Key("row"), GUI.Button(body, GUIContent.none, GUIStyle.none));
        return Report(Key("tool"), IconButton(act, action, Skin.Button));
    }

    /// <summary>
    /// A row like <see cref="ExpandTool"/> without a button of its own: its triangle, a dot in a
    /// colour, a name, words, and a value on the right edge. True when it was clicked.
    /// </summary>
    public static bool ExpandLine(Color dot, string name, string words, string value, bool open)
    {
        var row = NextRow(Skin.U(30f));
        if (row.Contains(Event.current.mousePosition))
        {
            Skin.Fill(row, Skin.Light(0.03f));
        }

        float size = Skin.U(12f), dotSize = Skin.U(9f), nameWidth = Skin.U(190f), valueWidth = Skin.U(90f), gap = Skin.U(10f);
        Skin.Icon(new Rect(row.x, row.y + (row.height - size) / 2f, size, size), open ? Skin.ChevronDown : Skin.ChevronRight, Skin.Dim);
        float x = row.x + Skin.U(18f);
        Skin.Icon(new Rect(x, row.y + (row.height - dotSize) / 2f, dotSize, dotSize), Skin.Circle, dot);
        x += dotSize + Skin.U(8f);
        GUI.Label(new Rect(x, row.y, nameWidth, row.height), name, Skin.Cell);
        x += nameWidth + gap;
        GUI.Label(new Rect(x, row.y, Mathf.Max(0f, row.xMax - valueWidth - gap - x), row.height), words, Skin.Cell);
        GUI.Label(new Rect(row.xMax - valueWidth, row.y, valueWidth, row.height), value, Skin.CellRight);
        return Report(Key("row"), GUI.Button(row, GUIContent.none, GUIStyle.none));
    }

    private static Color ToneColour(ResultTone tone) => tone switch
    {
        ResultTone.Good => Skin.Alpha(Skin.Good, 0.85f),
        ResultTone.Caution => Skin.Alpha(Skin.Caution, 0.9f),
        ResultTone.Bad => Skin.Alpha(Skin.Bad, 0.9f),
        _ => Skin.Light(0.14f),
    };
}
