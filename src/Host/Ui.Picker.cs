using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Vigil.Core;

namespace Vigil;

internal static partial class Ui
{
    private const float PickRowHeight = 22f, PickHeadHeight = 20f, PickListHeight = 200f, PickChevron = 22f;

    /// <summary>A picked setting's state: what the field holds, whether its list is open, what the list is narrowed to, and where the list is scrolled. The caller keeps one per field.</summary>
    public sealed class Picker
    {
        public string Value = "";
        public bool Open;
        public string Filter = "";
        public Vector2 Scroll;
    }

    /// <summary>
    /// A tool whose setting is typed or picked from a list: a field and a chevron that opens the
    /// list under the row. Typing opens it too, narrowed to what is typed (<paramref name="groups"/>
    /// gives the list for a filter); picking a line puts it in the field and closes the list.
    /// </summary>
    public static bool PickTool(string name, string description, string action, Picker picker, float width, Func<string, IList<PickGroup>> groups, bool primary = false)
    {
        float fieldWidth = Skin.U(width), chevron = Skin.U(PickChevron), gap = Skin.U(4f);
        bool pressed = ToolRow(name, description, action, primary, true, fieldWidth + gap + chevron, out var middle, false);
        string before = picker.Value ?? "";
        picker.Value = GUI.TextField(new Rect(middle.x, middle.y, fieldWidth, middle.height), before, Skin.Field);
        if (picker.Value != before)
        {
            Later(() =>
            {
                picker.Open = true;
                picker.Filter = picker.Value;
            });
        }

        var chevronRect = new Rect(middle.x + fieldWidth + gap, middle.y, chevron, middle.height);
        bool clicked = GUI.Button(chevronRect, GUIContent.none, Skin.Button);
        float icon = Skin.U(IconSize);
        Skin.Icon(new Rect(chevronRect.x + (chevron - icon) / 2f, chevronRect.y + (chevronRect.height - icon) / 2f, icon, icon), Skin.ChevronDown, Skin.Dim);
        if (Report(Key("pickopen"), clicked))
        {
            bool open = !picker.Open;
            Later(() =>
            {
                picker.Open = open;
                picker.Filter = "";
            });
        }

        if (picker.Open)
        {
            PickList(picker, groups(picker.Filter) ?? new List<PickGroup>());
        }

        return pressed;
    }

    private static void PickList(Picker picker, IList<PickGroup> groups)
    {
        float row = Skin.U(PickRowHeight), head = Skin.U(PickHeadHeight), pad = Skin.U(8f);
        float content = groups.Sum(g => head + g.Items.Count * row);
        float view = Mathf.Min(content, Skin.U(PickListHeight));
        var box = GUILayoutUtility.GetRect(0f, view, GUILayout.ExpandWidth(true));
        Skin.Fill(box, Skin.Sunken);
        float inner = box.width - (content > view ? Skin.U(14f) : 0f);
        picker.Scroll = GUI.BeginScrollView(box, picker.Scroll, new Rect(0f, 0f, inner, content));
        float y = 0f;
        foreach (var g in groups)
        {
            GUI.Label(new Rect(pad, y, inner - 2f * pad, head), g.Title, Skin.RowDesc);
            y += head;
            foreach (var item in g.Items)
            {
                var r = new Rect(0f, y, inner, row);
                bool hover = r.Contains(Event.current.mousePosition);
                bool chosen = string.Equals(item.Name, picker.Value, StringComparison.Ordinal);
                if (hover || chosen)
                {
                    Skin.Fill(r, chosen ? Skin.Raised : Skin.RaisedHover);
                }

                float noteWidth = string.IsNullOrEmpty(item.Note) ? 0f : Skin.InlineSmall.CalcSize(new GUIContent(item.Note)).x;
                GUI.Label(new Rect(pad, y, inner - 3f * pad - noteWidth, row), item.Name, Skin.InlineBody);
                if (noteWidth > 0f)
                {
                    GUI.Label(new Rect(inner - pad - noteWidth, y, noteWidth, row), item.Note, Skin.InlineSmall);
                }

                if (Report(Key("pick"), GUI.Button(r, GUIContent.none, GUIStyle.none)))
                {
                    string picked = item.Name;
                    Later(() =>
                    {
                        picker.Value = picked;
                        picker.Open = false;
                        picker.Filter = "";
                    });
                }

                y += row;
            }
        }

        GUI.EndScrollView();
    }
}
