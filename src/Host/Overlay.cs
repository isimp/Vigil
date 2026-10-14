using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Vigil;

/// <summary>
/// A uGUI text over the game's screen in the font of the game's messages, on its own overlay canvas
/// scaled from 1920 by 1080: the HUD line and the label under the crosshair. It can be made once the
/// game's messages exist (<see cref="MessageHud"/>).
/// </summary>
internal static class Overlay
{
    /// <summary>A new text under a root of its own (kept across scenes); null while the game's font is not there.</summary>
    public static TMP_Text Make(string name)
    {
        var font = MessageHud.instance && MessageHud.instance.m_messageText ? MessageHud.instance.m_messageText.font : null;
        if (font == null)
        {
            return null;
        }

        var root = new GameObject(name);
        Object.DontDestroyOnLoad(root);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 5000;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 1f;

        var go = new GameObject("text");
        go.transform.SetParent(root.transform, false);
        var text = go.AddComponent<TextMeshProUGUI>();
        text.font = font;
        text.richText = true;
        text.raycastTarget = false;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.color = Color.white;
        return text;
    }

    /// <summary>The root a text from <see cref="Make"/> sits under.</summary>
    public static GameObject RootOf(TMP_Text text) => text.transform.parent.gameObject;

    /// <summary>Whether the player hid the game's HUD; then Vigil's texts hide too.</summary>
    public static bool GameHudHidden()
    {
        try
        {
            return global::Hud.IsUserHidden();
        }
        catch (System.Exception)
        {
            return false;
        }
    }
}
