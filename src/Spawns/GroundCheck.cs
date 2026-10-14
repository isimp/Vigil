using Vigil.Core;
using UnityEngine;

namespace Vigil.Spawns;

/// <summary>
/// Checks a spot the way the game checks a spawn point (<c>SpawnSystem.IsSpawnPointGood</c>): the
/// ground height comes from <c>ZoneSystem.GetGroundData</c>, a spot with anything solid above or
/// below it is refused by <c>ZoneSystem.IsBlocked</c>, and one inside a player base area by
/// <c>EffectArea.IsPointInsideArea</c> with the same arguments. What else a spawn asks for
/// (biome, altitude, forest, distance from players) depends on the creature and is left out.
/// </summary>
internal static class GroundCheck
{
    public static Sample At(float x, float z)
    {
        var zones = ZoneSystem.instance;
        if (!zones)
        {
            return new Sample(Ground.Unloaded, 0f, 0f, 1f, 0f);
        }

        var point = new Vector3(x, 0f, z);
        zones.GetGroundData(ref point, out var normal, out _, out _, out var heightmap);
        if (!heightmap)
        {
            return new Sample(Ground.Unloaded, 0f, 0f, 1f, 0f);
        }

        if (zones.IsBlocked(point))
        {
            return new Sample(Ground.Covered, point.y, normal.x, normal.y, normal.z);
        }

        if (EffectArea.IsPointInsideArea(point, EffectArea.Type.PlayerBase))
        {
            return new Sample(Ground.Protected, point.y, normal.x, normal.y, normal.z);
        }

        return new Sample(Ground.Open, point.y, normal.x, normal.y, normal.z);
    }
}
