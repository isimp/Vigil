namespace Vigil.Core;

/// <summary>
/// When the view is checked again. It follows the player, so a move of one grid step starts a
/// new check, and it picks up pieces built or torn down nearby, so it is checked every few
/// seconds while the player stands still. A check underway is let finish first: restarting it on
/// every step would leave nothing shown while the player walks.
/// </summary>
internal static class Recheck
{
    public static bool Due(bool checking, bool everChecked, float now, float lastStart, float moved, float spacing, float every)
    {
        if (checking)
        {
            return false;
        }

        return !everChecked || moved >= spacing || now - lastStart >= every;
    }
}
