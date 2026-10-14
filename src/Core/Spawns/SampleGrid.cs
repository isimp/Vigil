using System;
using System.Collections.Generic;

namespace Vigil.Core;

/// <summary>A spot on the ground grid, by its world x and z.</summary>
internal readonly struct GridSpot : IEquatable<GridSpot>
{
    public GridSpot(float x, float z)
    {
        X = x;
        Z = z;
    }

    public float X { get; }

    public float Z { get; }

    public bool Equals(GridSpot other) => X.Equals(other.X) && Z.Equals(other.Z);

    public override bool Equals(object obj) => obj is GridSpot other && Equals(other);

    public override int GetHashCode() => (X.GetHashCode() * 397) ^ Z.GetHashCode();

    public static bool operator ==(GridSpot left, GridSpot right) => left.Equals(right);

    public static bool operator !=(GridSpot left, GridSpot right) => !left.Equals(right);
}

/// <summary>
/// The spots to check around the player: every point of a world grid with the given spacing
/// that lies within the radius. The grid is anchored to the world, not to the player, so a spot
/// is the same spot from wherever it is seen and its mark does not move as the player walks.
/// </summary>
internal static class SampleGrid
{
    public static List<GridSpot> Around(float centerX, float centerZ, float radius, float spacing)
    {
        var spots = new List<GridSpot>();
        if (!(radius > 0f) || !(spacing > 0f) || float.IsInfinity(radius) || float.IsNaN(centerX) || float.IsNaN(centerZ))
        {
            return spots;
        }

        int minX = (int)Math.Ceiling((centerX - radius) / spacing), maxX = (int)Math.Floor((centerX + radius) / spacing);
        int minZ = (int)Math.Ceiling((centerZ - radius) / spacing), maxZ = (int)Math.Floor((centerZ + radius) / spacing);
        float limit = radius * radius;
        for (int i = minX; i <= maxX; i++)
        {
            float x = i * spacing, dx = x - centerX;
            for (int k = minZ; k <= maxZ; k++)
            {
                float z = k * spacing, dz = z - centerZ;
                if ((dx * dx) + (dz * dz) <= limit)
                {
                    spots.Add(new GridSpot(x, z));
                }
            }
        }

        return spots;
    }
}
