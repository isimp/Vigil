using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using UnityEngine;
using Vigil.Core;

namespace Vigil;

/// <summary>
/// The building blocks every area draws its sections with, in the panel's look (<see cref="Skin"/>).
/// A section is mostly one of three things: a list of tools, each a row with its name, a line on
/// what it gives, a setting if it has one and its button on a shared right edge
/// (<see cref="Tool(string, string, string, bool, bool, bool)"/>); a short form, its labels in a column of
/// their own (<see cref="BeginFormLine"/>); or values, as label and value rows or a table. Words put
/// on a line between <see cref="BeginLine"/> and <see cref="EndLine"/> sit centred beside the
/// buttons there.
/// </summary>
internal static partial class Ui
{
    private const float LabelWidth = 150, FormLabelWidth = 150, ActionWidth = 84, TagWidth = 80;

    private static bool _inLine;
    private static int _rows;
    private static string _section = "";
    private static readonly Dictionary<string, int> Ordinals = new();
    private static string _pressedKey;
    private static int _pressedValue;

    /// <summary>A section starts: its first row has no line above it, and no line of buttons is open.</summary>
    public static void StartSection(string title)
    {
        _inLine = false;
        _rows = 0;
        _section = title ?? "";
        Ordinals.Clear();
    }

    /// <summary>
    /// Changes a click asked for that reach above the control clicked (closing a result, opening
    /// one incident and so folding another), made before the next layout pass draws anything.
    /// </summary>
    private static readonly List<System.Action> Pending = new();

    public static void Later(System.Action change) => Pending.Add(change);

    /// <summary>The start of one of IMGUI's passes over the panel: on a layout pass, the changes waiting for it are made.</summary>
    public static void BeginPass()
    {
        if (Event.current.type == EventType.Layout)
        {
            MakeWaitingChanges();
        }
    }

    /// <summary>
    /// Makes the changes waiting for the next layout pass now: at the start of that pass, or when the
    /// panel closes before it (a closed panel draws no pass, and a change a click asked for still happens).
    /// </summary>
    public static void MakeWaitingChanges()
    {
        if (Pending.Count == 0)
        {
            return;
        }

        var changes = Pending.ToArray();
        Pending.Clear();
        foreach (var change in changes)
        {
            change();
        }
    }

    /// <summary>
    /// The end of one of IMGUI's passes over the panel: a click no control took back on this layout
    /// pass belonged to a control that is gone, and is dropped.
    /// </summary>
    public static void EndPass()
    {
        if (Event.current.type == EventType.Layout)
        {
            _pressedKey = null;
        }
    }

    /// <summary>
    /// A control's key: its section, its kind and how many of that kind came before it there. Not
    /// its words, which can change between a click and the pass that reports it (a button that
    /// asks twice, a state in a row's name).
    /// </summary>
    private static string Key(string kind)
    {
        string k = _section + "|" + kind;
        Ordinals.TryGetValue(k, out int n);
        Ordinals[k] = n + 1;
        return k + "|" + n.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// IMGUI lays the panel out in one pass and handles a click in the next, and a click that
    /// changes what is drawn (a table appears, a tab switches, an answer opens) would leave that
    /// pass drawing more than was laid out, which IMGUI reports as an error. So a click is kept and
    /// reported on the next layout pass instead, where whatever it changes is laid out afresh.
    /// </summary>
    private static bool Report(string key, bool clicked, int value, out int reported)
    {
        reported = value;
        if (clicked)
        {
            _pressedKey = key;
            _pressedValue = value;
            return false;
        }

        if (_pressedKey != key || Event.current.type != EventType.Layout)
        {
            return false;
        }

        _pressedKey = null;
        reported = _pressedValue;
        return true;
    }

    private static bool Report(string key, bool clicked) => Report(key, clicked, 1, out _);

    /// <summary>A heading inside a section; the rows under it start a list of their own.</summary>
    public static void Heading(string text)
    {
        GUILayout.Label(text, Skin.SubHeading);
        _rows = 0;
    }

    // Words on a line take only their own width, so the controls after them stay beside them.
    public static void Text(string text)
    {
        if (_inLine) GUILayout.Label(text, Skin.InlineBody, GUILayout.ExpandWidth(false));
        else GUILayout.Label(text, Skin.Body);
    }

    public static void Small(string text)
    {
        if (_inLine) GUILayout.Label(text, Skin.InlineSmall, GUILayout.ExpandWidth(false));
        else GUILayout.Label(text, Skin.Small);
    }

    /// <summary>A hint: faint, wrapped.</summary>
    public static void Note(string text)
    {
        if (_inLine) GUILayout.Label(text, Skin.InlineSmall, GUILayout.ExpandWidth(false));
        else GUILayout.Label(text, Skin.Note);
    }

    /// <summary>A label and its value on one row, the labels in a column of their own.</summary>
    public static void Row(string label, string value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, Skin.RowLabel, GUILayout.Width(Skin.U(LabelWidth)));
        GUILayout.Label(value, Skin.RowValue);
        GUILayout.EndHorizontal();
    }

    /// <summary>
    /// A label and short lines beside it, under each other, each a tag ("You", "Them") in a narrow
    /// column of its own and its words, which wrap, so a long value stays readable. Marked lines
    /// show their words in the accent.
    /// </summary>
    public static void Pairs(string label, IEnumerable<KeyValuePair<string, string>> lines, bool mark)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, Skin.RowLabel, GUILayout.Width(Skin.U(LabelWidth)));
        GUILayout.BeginVertical();
        foreach (var line in lines)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(line.Key, Skin.RowLabel, GUILayout.Width(Skin.U(TagWidth)));
            GUILayout.Label(mark ? "<color=#E8B04A>" + line.Value + "</color>" : line.Value, Skin.RowValue);
            GUILayout.EndHorizontal();
        }

        GUILayout.EndVertical();
        GUILayout.EndHorizontal();
    }

    /// <summary>Starts a line of buttons, fields and the words between them.</summary>
    public static void BeginLine()
    {
        GUILayout.BeginHorizontal();
        _inLine = true;
    }

    /// <summary>Starts a line of a form: its label in a column of its own, then the line's controls.</summary>
    public static void BeginFormLine(string label, float width = FormLabelWidth)
    {
        BeginLine();
        GUILayout.Label(label, Skin.InlineSmall, GUILayout.Width(Skin.U(width)));
    }

    public static void EndLine()
    {
        _inLine = false;
        GUILayout.EndHorizontal();
    }

    /// <summary>Forgets a line a section left open when it failed while drawing, so the next section's words are not drawn as if on it.</summary>
    public static void ForgetLine() => _inLine = false;

    public static bool Button(string text) => LaidOutButton(text, Skin.Button);

    /// <summary>The main thing a tab does, filled with the accent; one a tab at most.</summary>
    public static bool Primary(string text) => LaidOutButton(text, Skin.Primary);

    /// <summary>A button whose action cannot be undone. It shares its place with the button it stands in for while armed.</summary>
    public static bool Danger(string text) => LaidOutButton(text, Skin.Danger);

    /// <summary>A small outlined button for something beside the point: refresh, clear, open a folder.</summary>
    public static bool Quiet(string text) => LaidOutButton(text, Skin.Quiet);

    private const float IconSize = 12f, IconGap = 6f;

    private static readonly Dictionary<GUIStyle, GUIStyle> TextOnly = new();

    private static bool LaidOutButton(string text, GUIStyle style)
    {
        var size = style.CalcSize(new GUIContent(text));
        if (Skin.IconFor(text) != null)
        {
            size.x += Skin.U(IconSize + IconGap);
        }

        var rect = GUILayoutUtility.GetRect(size.x, style.fixedHeight > 0f ? style.fixedHeight : size.y, style, GUILayout.ExpandWidth(false));
        return Report(Key("button"), IconButton(rect, text, style));
    }

    /// <summary>
    /// A button with the icon its words call for (<see cref="Skin.IconFor"/>) before them, the two
    /// centred together; a plain button for words with none. True when clicked this pass.
    /// </summary>
    private static bool IconButton(Rect rect, string text, GUIStyle style)
    {
        var icon = Skin.IconFor(text);
        if (icon == null)
        {
            return GUI.Button(rect, text, style);
        }

        bool clicked = GUI.Button(rect, GUIContent.none, style);
        var words = WordsOf(style);
        float size = Skin.U(IconSize), gap = Skin.U(IconGap), textWidth = words.CalcSize(new GUIContent(text)).x;
        float x = rect.x + Mathf.Max(0f, (rect.width - size - gap - textWidth) / 2f);
        var colour = style.normal.textColor;
        Skin.Icon(new Rect(x, rect.y + (rect.height - size) / 2f, size, size), icon, GUI.enabled ? colour : Skin.Alpha(colour, colour.a * 0.5f));
        GUI.Label(new Rect(x + size + gap, rect.y, textWidth + 1f, rect.height), text, words);
        return clicked;
    }

    /// <summary>A button style's words alone: its font and colour, no box, no padding.</summary>
    private static GUIStyle WordsOf(GUIStyle style)
    {
        if (TextOnly.TryGetValue(style, out var words))
        {
            return words;
        }

        if (TextOnly.Count > 32)
        {
            TextOnly.Clear();
        }

        words = new GUIStyle(style)
        {
            padding = new RectOffset(0, 0, 0, 0),
            margin = new RectOffset(0, 0, 0, 0),
            border = new RectOffset(0, 0, 0, 0),
            alignment = TextAnchor.MiddleLeft,
            fixedWidth = 0f,
            fixedHeight = 0f,
        };
        words.normal.background = words.hover.background = words.active.background = words.focused.background = null;
        words.onNormal.background = words.onHover.background = words.onActive.background = words.onFocused.background = null;
        words.hover.textColor = words.active.textColor = style.normal.textColor;
        TextOnly[style] = words;
        return words;
    }

    public static string Field(string value, float width) => GUILayout.TextField(value ?? "", Skin.Field, GUILayout.Width(Skin.U(width)));

    /// <summary>A tool in a list: its name, what it gives, and its button on the list's right edge; <paramref name="danger"/> draws the button as one that cannot be undone.</summary>
    public static bool Tool(string name, string description, string action, bool primary = false, bool enabled = true, bool danger = false) =>
        ToolRow(name, description, action, primary, enabled, 0f, out _, danger);

    /// <summary>A tool with a setting, a small field (and its unit) just left of its button.</summary>
    public static bool Tool(string name, string description, string action, ref string value, float width, string unit, bool primary = false)
    {
        float unitWidth = string.IsNullOrEmpty(unit) ? 0f : Skin.InlineSmall.CalcSize(new GUIContent(unit)).x + Skin.U(6f);
        float fieldWidth = Skin.U(width);
        bool pressed = ToolRow(name, description, action, primary, true, fieldWidth + unitWidth, out var middle, false);
        value = GUI.TextField(new Rect(middle.x, middle.y, fieldWidth, middle.height), value ?? "", Skin.Field);
        if (unitWidth > 0f)
        {
            GUI.Label(new Rect(middle.x + fieldWidth + Skin.U(6f), middle.y, unitWidth, middle.height), unit, Skin.InlineSmall);
        }

        return pressed;
    }

    private static bool ToolRow(string name, string description, string action, bool primary, bool enabled, float middleWidth, out Rect middle, bool danger)
    {
        float height = Skin.U(description == null ? 34f : 46f);
        var row = NextRow(height);
        float line = Skin.U(Skin.LineHeight), gap = Skin.U(10f), actionWidth = Skin.U(ActionWidth);
        var act = new Rect(row.xMax - actionWidth, row.y + (height - line) / 2f, actionWidth, line);
        middle = new Rect(act.x - gap - middleWidth, act.y, middleWidth, line);
        float textWidth = Mathf.Max(0f, (middleWidth > 0f ? middle.x : act.x) - gap - row.x);
        if (description == null)
        {
            GUI.Label(new Rect(row.x, row.y, textWidth, height), name, Skin.RowName);
        }
        else
        {
            GUI.Label(new Rect(row.x, row.y + Skin.U(5f), textWidth, Skin.U(20f)), name, Skin.RowName);
            GUI.Label(new Rect(row.x, row.y + Skin.U(24f), textWidth, Skin.U(18f)), description, Skin.RowDesc);
        }

        bool was = GUI.enabled;
        GUI.enabled = was && enabled;
        string key = Key("tool");
        bool pressed = Report(key, IconButton(act, action, danger ? Skin.Danger : primary ? Skin.Primary : Skin.Button));
        GUI.enabled = was;
        return pressed;
    }

    /// <summary>The next row of a list, with a hairline above it unless it is the first.</summary>
    private static Rect NextRow(float height)
    {
        var row = GUILayoutUtility.GetRect(0f, height, GUILayout.ExpandWidth(true));
        if (_rows++ > 0)
        {
            Skin.Fill(new Rect(row.x, row.y, row.width, Mathf.Max(1f, Skin.U(1f))), Skin.Hairline);
        }

        return row;
    }

    /// <summary>A check box with its words; a click on either switches it.</summary>
    public static bool Toggle(bool value, string text)
    {
        var rect = GUILayoutUtility.GetRect(new GUIContent(text), Skin.Check, GUILayout.ExpandWidth(false));
        bool hover = rect.Contains(Event.current.mousePosition);
        float size = Skin.U(16f);
        var box = new Rect(rect.x, rect.y + (rect.height - size) / 2f, size, size);
        if (value)
        {
            Skin.SmallBox(box, hover ? Skin.Shade(Skin.Accent, 1.08f) : Skin.Accent);
            Skin.Icon(box, Skin.Tick, Skin.OnAccent);
        }
        else
        {
            Skin.SmallBox(box, hover ? Skin.SunkenHover : Skin.Sunken, Skin.Light(hover ? 0.45f : 0.3f));
        }

        string key = Key("toggle");
        bool now = GUI.Toggle(rect, value, text, Skin.Check);
        return Report(key, now != value, now ? 1 : 0, out int reported) ? reported == 1 : value;
    }

    /// <summary>One of several on a sunken track, the chosen one lit (none for -1); gives the one chosen now.</summary>
    public static int Choice(int selected, string[] options)
    {
        float pad = Skin.U(2f), gap = Skin.U(2f), height = Skin.U(Skin.LineHeight);
        var widths = new float[options.Length];
        float total = pad * 2f + gap * (options.Length - 1);
        for (int i = 0; i < options.Length; i++)
        {
            widths[i] = Skin.SegmentOff.CalcSize(new GUIContent(options[i])).x;
            total += widths[i];
        }

        var rect = GUILayoutUtility.GetRect(total, height, Skin.ChoiceTrack, GUILayout.ExpandWidth(false));
        Skin.SmallBox(rect, Skin.Sunken);
        string key = Key("choice");
        int clicked = -1;
        float x = rect.x + pad;
        for (int i = 0; i < options.Length; i++)
        {
            if (GUI.Button(new Rect(x, rect.y + pad, widths[i], height - pad * 2f), options[i], i == selected ? Skin.SegmentOn : Skin.SegmentOff))
            {
                clicked = i;
            }

            x += widths[i] + gap;
        }

        return Report(key, clicked >= 0 && clicked != selected, clicked, out int reported) ? reported : selected;
    }

    /// <summary>A round mark in a tone, on a line: a state at a glance.</summary>
    public static void Dot(Skin.Tone tone)
    {
        var rect = GUILayoutUtility.GetRect(Skin.U(9f), Skin.U(Skin.LineHeight), Skin.ChoiceTrack, GUILayout.ExpandWidth(false));
        float size = Skin.U(9f);
        Skin.Icon(new Rect(rect.x, rect.y + (rect.height - size) / 2f, size, size), Skin.Circle, Skin.Of(tone));
    }

    /// <summary>A column of a <see cref="Table"/>: its heading, its width before scaling (0 for the one that takes the rest), and whether it lines up on the right, as numbers do.</summary>
    internal sealed class Column
    {
        public Column(string header, float width, bool right)
        {
            Header = header;
            Width = width;
            Right = right;
        }

        public string Header { get; }

        public float Width { get; }

        public bool Right { get; }
    }

    /// <summary>
    /// A table: a row of headings unless every heading is null, then a row per entry, hairlines
    /// between. The columns are as wide as what is in them (<see cref="TableLayout"/>): the one of
    /// width 0 takes the room to spare, and when the room is short the numbers keep their width and the
    /// widest text gives way, cut with "…". A heading with no rows heads ExpandRows and keeps the set
    /// widths, as they do. Drawn on the repaint pass only, where the row's width is known.
    /// </summary>
    public static void Table(IList<Column> columns, IEnumerable<string[]> rows)
    {
        bool headed = false;
        foreach (var c in columns)
        {
            headed |= c.Header != null;
        }

        var list = rows as IList<string[]> ?? rows.ToList();
        bool paint = Event.current.type == EventType.Repaint;
        float[] widths = null;
        int was = _rows;
        _rows = 0;
        if (headed)
        {
            var head = NextRow(Skin.U(22f));
            if (paint)
            {
                // A heading with no rows heads a list of ExpandRows, which keep the set widths.
                widths = list.Count == 0 ? FixedWidths(columns, head.width) : MeasuredWidths(columns, list, head.width);
                Cells(head, columns, widths, i => columns[i].Header ?? "", true);
            }
        }

        foreach (var cells in list)
        {
            var row = NextRow(Skin.U(26f));
            if (!paint)
            {
                continue;
            }

            widths ??= MeasuredWidths(columns, list, row.width);
            Cells(row, columns, widths, i => i < cells.Length ? cells[i] : "", false);
        }

        _rows = was;
    }

    private const float CellGap = 10f, TextFloor = 60f;
    private static readonly Dictionary<(GUIStyle, string), float> TextWidths = new();

    // A text's width in a style, kept, since a table measures the same texts every frame.
    private static float TextWidth(GUIStyle style, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0f;
        }

        if (TextWidths.TryGetValue((style, text), out float w))
        {
            return w;
        }

        if (TextWidths.Count > 8000)
        {
            TextWidths.Clear();
        }

        w = style.CalcSize(new GUIContent(text)).x;
        TextWidths[(style, text)] = w;
        return w;
    }

    private static GUIStyle CellStyle(Column c, bool head) => head ? (c.Right ? Skin.CellHeadRight : Skin.CellHead) : (c.Right ? Skin.CellRight : Skin.Cell);

    private static float[] MeasuredWidths(IList<Column> columns, IList<string[]> rows, float rowWidth)
    {
        var cols = new TableLayout.Col[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            float needed = TextWidth(CellStyle(columns[i], true), columns[i].Header);
            foreach (var cells in rows)
            {
                if (i < cells.Length)
                {
                    needed = Mathf.Max(needed, TextWidth(CellStyle(columns[i], false), cells[i]));
                }
            }

            cols[i] = new TableLayout.Col(needed + Skin.U(2f), columns[i].Width <= 0f, columns[i].Right);
        }

        return TableLayout.Widths(rowWidth - Skin.U(CellGap) * Mathf.Max(0, columns.Count - 1), cols, Skin.U(TextFloor));
    }

    // The fixed widths and the rest, as WrapTable's rows lay out, for its row of headings.
    private static float[] FixedWidths(IList<Column> columns, float rowWidth)
    {
        float gap = Skin.U(CellGap), fixedWidth = 0f;
        foreach (var c in columns)
        {
            fixedWidth += Skin.U(c.Width) + gap;
        }

        float rest = Mathf.Max(Skin.U(TextFloor), rowWidth - fixedWidth);
        var widths = new float[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            widths[i] = columns[i].Width > 0f ? Skin.U(columns[i].Width) : rest;
        }

        return widths;
    }

    /// <summary>
    /// A table like <see cref="Table"/> whose cells wrap their words instead of being cut off, each
    /// row as tall as its longest cell; for cells whose length cannot be known beforehand.
    /// </summary>
    public static void WrapTable(IList<Column> columns, IEnumerable<string[]> rows)
    {
        bool headed = false;
        foreach (var c in columns)
        {
            headed |= c.Header != null;
        }

        int was = _rows;
        _rows = 0;
        if (headed)
        {
            var head = NextRow(Skin.U(22f));
            Cells(head, columns, FixedWidths(columns, head.width), i => columns[i].Header ?? "", true);
        }

        float gap = Skin.U(10f);
        foreach (var cells in rows)
        {
            GUILayout.BeginHorizontal();
            for (int i = 0; i < columns.Count; i++)
            {
                if (i > 0)
                {
                    GUILayout.Space(gap);
                }

                string text = i < cells.Length ? cells[i] : "";
                if (columns[i].Width > 0f)
                {
                    GUILayout.Label(text, Skin.CellWrap, GUILayout.Width(Skin.U(columns[i].Width)));
                }
                else
                {
                    GUILayout.Label(text, Skin.CellWrap, GUILayout.ExpandWidth(true));
                }
            }

            GUILayout.EndHorizontal();
            if (_rows++ > 0)
            {
                var row = GUILayoutUtility.GetLastRect();
                Skin.Fill(new Rect(row.x, row.y, row.width, Mathf.Max(1f, Skin.U(1f))), Skin.Hairline);
            }
        }

        _rows = was;
    }

    private static void Cells(Rect row, IList<Column> columns, float[] widths, System.Func<int, string> text, bool head)
    {
        float gap = Skin.U(CellGap), x = row.x;
        for (int i = 0; i < columns.Count; i++)
        {
            var style = CellStyle(columns[i], head);
            string t = TableLayout.Fit(text(i), widths[i], s => TextWidth(style, s));
            GUI.Label(new Rect(x, row.y, widths[i], row.height), t, style);
            x += widths[i] + gap;
        }
    }

    /// <summary>One check of a self-test: its mark, its area and what was checked, and the detail under it.</summary>
    public static void CheckRow(SelfTestReport.Check check)
    {
        if (_rows++ > 0)
        {
            var line = GUILayoutUtility.GetRect(0f, Mathf.Max(1f, Skin.U(1f)), GUILayout.ExpandWidth(true));
            Skin.Fill(line, Skin.Hairline);
        }

        GUILayout.BeginHorizontal();
        var at = GUILayoutUtility.GetRect(Skin.U(16f), Skin.U(16f), Skin.ChoiceTrack, GUILayout.ExpandWidth(false));
        Mark(new Rect(at.x, at.y, Skin.U(16f), Skin.U(16f)), check.Outcome);
        GUILayout.BeginVertical();
        string area = string.IsNullOrEmpty(check.Area) ? "" : "<color=#B3B4BD>" + check.Area + "</color>   ";
        GUILayout.Label(area + check.What, Skin.Body);
        if (!string.IsNullOrEmpty(check.Detail))
        {
            GUILayout.Label(check.Detail, Skin.Note);
        }

        GUILayout.EndVertical();
        GUILayout.EndHorizontal();
    }

    /// <summary>A result's mark: a tick on green, "!" on amber, "x" on red, "i" on grey.</summary>
    private static void Mark(Rect rect, SelfTestReport.Result result)
    {
        switch (result)
        {
            case SelfTestReport.Result.Pass:
                Skin.SmallBox(rect, Skin.Good);
                Skin.Icon(rect, Skin.Tick, Skin.OnAccent);
                break;
            case SelfTestReport.Result.Warn:
                Skin.SmallBox(rect, Skin.Caution);
                GUI.Label(rect, "!", Skin.MarkGlyph);
                break;
            case SelfTestReport.Result.Fail:
                Skin.SmallBox(rect, Skin.Bad);
                GUI.Label(rect, "×", Skin.MarkGlyph);
                break;
            default:
                Skin.SmallBox(rect, Skin.Faint);
                GUI.Label(rect, "i", Skin.MarkGlyph);
                break;
        }
    }

    /// <summary>The tone of a self-test's worst result.</summary>
    public static Skin.Tone ToneOf(SelfTestReport.Result result) => result switch
    {
        SelfTestReport.Result.Fail => Skin.Tone.Bad,
        SelfTestReport.Result.Warn => Skin.Tone.Caution,
        SelfTestReport.Result.Pass => Skin.Tone.Good,
        _ => Skin.Tone.Quiet,
    };

    /// <summary>The text coloured by its level: green, amber or red.</summary>
    public static string Colour(string text, HudLine.Level level) => "<color=" + Hex(level) + ">" + text + "</color>";

    public static string Hex(HudLine.Level level) => level == HudLine.Level.Bad ? "#E8624F" : level == HudLine.Level.Warn ? "#E8B04A" : "#9BD67C";

    /// <summary>A number for a row: "-" when not measured.</summary>
    public static string Num(double value, string format, string unit = "") =>
        double.IsNaN(value) || double.IsInfinity(value) ? "-" : value.ToString(format, CultureInfo.InvariantCulture) + unit;
}
