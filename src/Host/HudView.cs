using System;
using System.Text;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using Vigil.Core;

namespace Vigil;

/// <summary>Where the HUD line sits on the screen.</summary>
internal enum HudCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// The HUD line: one short row in a corner of the screen with frame rate, frame time, ping, data
/// waiting to go out, machine memory and, for an admin, the server's frame rate, each green, amber
/// or red by the recorder's report limits (<see cref="HudLine"/>). It is a uGUI text with the game's
/// own font, rewritten twice a second, shown in a world and hidden with the game's HUD.
/// </summary>
internal sealed class HudView
{
    private const float Every = 0.5f;

    private readonly ConfigEntry<bool> _show;
    private readonly ConfigEntry<HudCorner> _corner;
    private readonly ConfigEntry<int> _size;
    private GameObject _root;
    private TMP_Text _text;
    private float _next;
    private HudCorner _placedAt = (HudCorner)(-1);
    private int _sizedAt = -1;

    public HudView(ConfigFile config)
    {
        _show = config.Bind("HUD", "Show", true, "Shows a line of frame rate, ping, waiting data, memory and the server's frame rate in a corner, green, amber or red by the report limits.");
        _corner = config.Bind("HUD", "Corner", HudCorner.BottomRight, "The corner of the screen the line sits in.");
        _size = config.Bind("HUD", "FontSize", 18, new ConfigDescription("Size of the line's text.", new AcceptableValueRange<int>(10, 40)));
    }

    public bool Shown => _show.Value;

    /// <summary>Whether the line could be made: it needs the game's font, which exists once the game's messages do.</summary>
    public bool Made => _text != null;

    public void Toggle() => _show.Value = !_show.Value;

    public void Update(Func<HudLine.Values> values, HudLine.Limits limits)
    {
        if (!_show.Value || Player.m_localPlayer == null || Overlay.GameHudHidden())
        {
            if (_root)
            {
                _root.SetActive(false);
            }

            return;
        }

        if (!Ensure())
        {
            return;
        }

        _root.SetActive(true);
        if (Time.unscaledTime < _next)
        {
            return;
        }

        _next = Time.unscaledTime + Every;
        Place();
        _text.text = Rich(HudLine.Build(values(), limits));
    }

    public void Destroy()
    {
        if (_root)
        {
            UnityEngine.Object.Destroy(_root);
        }
    }

    private static string Rich(System.Collections.Generic.List<HudLine.Segment> segments)
    {
        var sb = new StringBuilder("<mark=#000000A0 padding=\"10,10,4,4\">");
        for (int i = 0; i < segments.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("  ");
            }

            sb.Append(Ui.Colour(segments[i].Text, segments[i].Level));
        }

        return sb.Append("</mark>").ToString();
    }

    private bool Ensure()
    {
        if (_root)
        {
            return true;
        }

        var text = Overlay.Make("Vigil HUD line");
        if (text == null)
        {
            return false;
        }

        _root = Overlay.RootOf(text);
        _text = text;
        _placedAt = (HudCorner)(-1);
        return true;
    }

    private void Place()
    {
        if (_placedAt == _corner.Value && _sizedAt == _size.Value)
        {
            return;
        }

        _placedAt = _corner.Value;
        _sizedAt = _size.Value;
        _text.fontSize = _size.Value;
        bool top = _placedAt == HudCorner.TopLeft || _placedAt == HudCorner.TopRight;
        bool left = _placedAt == HudCorner.TopLeft || _placedAt == HudCorner.BottomLeft;
        var anchor = new Vector2(left ? 0 : 1, top ? 1 : 0);
        var rt = _text.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.anchoredPosition = new Vector2(left ? 24 : -24, top ? -24 : 24);
        rt.sizeDelta = new Vector2(900, _size.Value * 2);
        _text.alignment = (top ? (left ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.TopRight) : (left ? TextAlignmentOptions.BottomLeft : TextAlignmentOptions.BottomRight));
    }
}
