using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Vigil.Spawns;

/// <summary>Whether a key press is meant for the spawn marks rather than for typing or a menu.</summary>
internal static class Guard
{
    public static bool MayAct(Player player)
    {
        return player && !player.IsDead()
            && !global::Console.IsVisible()
            && !TextInput.IsVisible()
            && !(Chat.instance && Chat.instance.HasFocus())
            && !Menu.IsVisible()
            && !TextFieldFocused();
    }

    /// <summary>A text field of the game or of another mod has the keyboard, such as a search box.</summary>
    private static bool TextFieldFocused()
    {
        var selected = EventSystem.current ? EventSystem.current.currentSelectedGameObject : null;
        if (!selected)
        {
            return false;
        }

        return (selected.TryGetComponent(out TMP_InputField tmp) && tmp.isFocused)
            || (selected.TryGetComponent(out InputField field) && field.isFocused);
    }
}
