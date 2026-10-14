using System;
using System.Collections.Generic;

namespace Vigil.Core;

/// <summary>What the game would make of a spot as a place to spawn a creature.</summary>
internal enum Ground
{
    /// <summary>Outside every base's spawn protection, nothing solid above or below: a creature can spawn here.</summary>
    Open,

    /// <summary>Inside the spawn protection of a base.</summary>
    Protected,

    /// <summary>Something solid is above or below the spot, such as a roof or a floor.</summary>
    Covered,

    /// <summary>The ground there is not loaded, so nothing can be said about it.</summary>
    Unloaded,
}

/// <summary>One spot checked: what it is, and the ground height and slope there for its mark.</summary>
internal readonly struct Sample
{
    public Sample(Ground kind, float y, float normalX, float normalY, float normalZ)
    {
        Kind = kind;
        Y = y;
        NormalX = normalX;
        NormalY = normalY;
        NormalZ = normalZ;
    }

    public Ground Kind { get; }

    public float Y { get; }

    public float NormalX { get; }

    public float NormalY { get; }

    public float NormalZ { get; }
}

/// <summary>An open spot to mark: where it is on the ground and which way the ground faces.</summary>
internal readonly struct Mark
{
    public Mark(float x, float y, float z, float normalX, float normalY, float normalZ)
    {
        X = x;
        Y = y;
        Z = z;
        NormalX = normalX;
        NormalY = normalY;
        NormalZ = normalZ;
    }

    public float X { get; }

    public float Y { get; }

    public float Z { get; }

    public float NormalX { get; }

    public float NormalY { get; }

    public float NormalZ { get; }
}

/// <summary>
/// One check of the spots around the player, run a share at a time so it never holds up a frame.
/// Its open spots are given out only once every spot is checked, so the marks always show one
/// whole check and never a half-finished one.
/// </summary>
internal sealed class Survey
{
    private readonly IReadOnlyList<GridSpot> _spots;
    private readonly Func<float, float, Sample> _check;
    private readonly List<Mark> _found = new();
    private readonly int[] _counts = new int[Enum.GetValues(typeof(Ground)).Length];
    private int _next;

    public Survey(IReadOnlyList<GridSpot> spots, Func<float, float, Sample> check)
    {
        _spots = spots ?? throw new ArgumentNullException(nameof(spots));
        _check = check ?? throw new ArgumentNullException(nameof(check));
    }

    public bool Done => _next >= _spots.Count;

    /// <summary>The open spots, empty until <see cref="Done"/>.</summary>
    public IReadOnlyList<Mark> Open => Done ? _found : Array.Empty<Mark>();

    public int Count(Ground kind) => _counts[(int)kind];

    /// <summary>Checks up to <paramref name="share"/> more spots.</summary>
    public void Step(int share)
    {
        for (int n = 0; n < share && _next < _spots.Count; n++, _next++)
        {
            var spot = _spots[_next];
            var sample = _check(spot.X, spot.Z);
            _counts[(int)sample.Kind]++;
            if (sample.Kind == Ground.Open)
            {
                _found.Add(new Mark(spot.X, sample.Y, spot.Z, sample.NormalX, sample.NormalY, sample.NormalZ));
            }
        }
    }
}
