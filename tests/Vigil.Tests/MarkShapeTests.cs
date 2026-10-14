using System;
using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests;

/// <summary>
/// An open spot is shown either as a pillar standing up from the ground, which stays easy to see
/// on hills and in hollows, or as a flat dot on the ground. A pillar keeps a dot at its foot so
/// the exact spot is still visible.
/// </summary>
public sealed class MarkShapeTests
{
    private const float Spacing = 2f, Height = 1.5f, Water = 30f;

    // A spot on a slope tilted about 35 degrees, well above the water.
    private static readonly Mark Slope = Tilted(10f, 42f, -6f, 0.5f, 0.82f, 0.28f);

    [Fact]
    public void APillarStandsStraightUpFromItsSpotWhateverTheSlope()
    {
        var pillar = PillarPart(MarkShape.Build(new[] { Slope }, MarkStyle.Pillars, Spacing, Height, Water));

        Assert.All(pillar, v =>
        {
            Assert.InRange(v.X, Slope.X - 0.5f, Slope.X + 0.5f);
            Assert.InRange(v.Z, Slope.Z - 0.5f, Slope.Z + 0.5f);
        });
        Assert.Equal(Slope.Y, pillar.Min(v => v.Y), 2);
        Assert.Equal(Slope.Y + Height, pillar.Max(v => v.Y), 2);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(4f)]
    public void APillarIsAsTallAsSet(float height)
    {
        var pillar = PillarPart(MarkShape.Build(new[] { Slope }, MarkStyle.Pillars, Spacing, height, Water));

        Assert.Equal(height, pillar.Max(v => v.Y) - pillar.Min(v => v.Y), 2);
    }

    [Fact]
    public void APillarCanBeSeenFromEverySide()
    {
        var shape = MarkShape.Build(new[] { Slope }, MarkStyle.Pillars, Spacing, Height, Water);
        var faces = Faces(shape).Where(f => Math.Abs(f.Ny) < 0.01f).ToList();

        // Two upright faces crossing at a right angle: from any side one of them faces the viewer.
        Assert.Equal(2, faces.Count);
        Assert.Equal(0f, (faces[0].Nx * faces[1].Nx) + (faces[0].Nz * faces[1].Nz), 2);
    }

    [Fact]
    public void APillarKeepsADotOnTheGroundAtItsFoot()
    {
        var shape = MarkShape.Build(new[] { Slope }, MarkStyle.Pillars, Spacing, Height, Water);

        Assert.Single(Faces(shape), f => Math.Abs(f.Ny - Slope.NormalY) < 0.01f);
    }

    [Fact]
    public void ThePillarOverWaterRisesFromTheSurface()
    {
        var seabed = Tilted(0f, 12f, 0f, 0f, 1f, 0f);
        var pillar = PillarPart(MarkShape.Build(new[] { seabed }, MarkStyle.Pillars, Spacing, Height, Water));

        Assert.Equal(Water, pillar.Min(v => v.Y), 2);
        Assert.Equal(Water + Height, pillar.Max(v => v.Y), 2);
    }

    [Fact]
    public void DotsLieFlatOnTheGroundAndRiseNoHigher()
    {
        var shape = MarkShape.Build(new[] { Slope }, MarkStyle.Dots, Spacing, Height, Water);

        Assert.Equal(4, shape.Positions.Length / 3);
        var face = Assert.Single(Faces(shape));
        Assert.Equal(Slope.NormalX, face.Nx, 2);
        Assert.Equal(Slope.NormalY, face.Ny, 2);
        Assert.Equal(Slope.NormalZ, face.Nz, 2);
        Assert.All(Vertices(shape), v => Assert.True(v.Y < Slope.Y + 1f));
    }

    [Fact]
    public void ADotSitsJustAboveTheGroundSoTheGroundDoesNotHideIt()
    {
        var shape = MarkShape.Build(new[] { Slope }, MarkStyle.Dots, Spacing, Height, Water);

        Assert.All(Vertices(shape), v =>
        {
            float above = ((v.X - Slope.X) * Slope.NormalX) + ((v.Y - Slope.Y) * Slope.NormalY) + ((v.Z - Slope.Z) * Slope.NormalZ);
            Assert.InRange(above, 0.03f, 0.2f);
        });
    }

    [Fact]
    public void ADotOverWaterLiesFlatOnTheSurface()
    {
        var seabed = Tilted(0f, 12f, 0f, 0.6f, 0.8f, 0f);
        var shape = MarkShape.Build(new[] { seabed }, MarkStyle.Dots, Spacing, Height, Water);

        Assert.All(Vertices(shape), v => Assert.InRange(v.Y, Water, Water + 0.2f));
    }

    [Fact]
    public void EverySpotGetsItsOwnMark()
    {
        var marks = Enumerable.Range(0, 5).Select(i => Tilted(i * 2f, 40f, 0f, 0f, 1f, 0f)).ToArray();

        foreach (var style in new[] { MarkStyle.Pillars, MarkStyle.Dots })
        {
            var one = MarkShape.Build(new[] { marks[0] }, style, Spacing, Height, Water);
            var five = MarkShape.Build(marks, style, Spacing, Height, Water);

            Assert.Equal(one.Positions.Length * 5, five.Positions.Length);
            Assert.Equal(one.Triangles.Length * 5, five.Triangles.Length);
            Assert.Equal(five.Positions.Length / 3 * 2, five.Uvs.Length);
            Assert.All(five.Triangles, t => Assert.InRange(t, 0, (five.Positions.Length / 3) - 1));
            Assert.All(Enumerable.Range(0, 5), i =>
                Assert.Contains(Vertices(five), v => Math.Abs(v.X - (i * 2f)) < Spacing / 2f));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryPartOfAMarkIsDrawnWhole(bool pillars)
    {
        var shape = MarkShape.Build(new[] { Slope }, pillars ? MarkStyle.Pillars : MarkStyle.Dots, Spacing, Height, Water);

        // Each square is two triangles that together use all four of its corners.
        for (int t = 0; t < shape.Triangles.Length; t += 6)
        {
            Assert.Equal(4, shape.Triangles.Skip(t).Take(6).Distinct().Count());
        }
    }

    [Fact]
    public void NoSpotsMakeNoShape()
    {
        var shape = MarkShape.Build(Array.Empty<Mark>(), MarkStyle.Pillars, Spacing, Height, Water);

        Assert.Empty(shape.Positions);
        Assert.Empty(shape.Triangles);
    }

    private static Mark Tilted(float x, float y, float z, float nx, float ny, float nz)
    {
        float length = (float)Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        return new Mark(x, y, z, nx / length, ny / length, nz / length);
    }

    private static List<(float X, float Y, float Z)> Vertices(MarkShape.Data shape) =>
        Enumerable.Range(0, shape.Positions.Length / 3)
            .Select(i => (shape.Positions[i * 3], shape.Positions[(i * 3) + 1], shape.Positions[(i * 3) + 2]))
            .ToList();

    /// <summary>The vertices of the upright faces, the ones that make the pillar.</summary>
    private static List<(float X, float Y, float Z)> PillarPart(MarkShape.Data shape)
    {
        var vertices = Vertices(shape);
        var faces = Faces(shape);
        var upright = new HashSet<int>();
        for (int f = 0; f < faces.Count; f++)
        {
            if (Math.Abs(faces[f].Ny) < 0.01f)
            {
                for (int k = 0; k < 6; k++)
                {
                    upright.Add(shape.Triangles[(f * 6) + k]);
                }
            }
        }

        Assert.NotEmpty(upright);
        return upright.Select(i => vertices[i]).ToList();
    }

    /// <summary>Each quad (two triangles) with its unit normal.</summary>
    private static List<(float Nx, float Ny, float Nz)> Faces(MarkShape.Data shape)
    {
        var v = Vertices(shape);
        var faces = new List<(float, float, float)>();
        for (int t = 0; t < shape.Triangles.Length; t += 6)
        {
            var a = v[shape.Triangles[t]];
            var b = v[shape.Triangles[t + 1]];
            var c = v[shape.Triangles[t + 2]];
            float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, wx = c.X - a.X, wy = c.Y - a.Y, wz = c.Z - a.Z;
            float nx = (uy * wz) - (uz * wy), ny = (uz * wx) - (ux * wz), nz = (ux * wy) - (uy * wx);
            float length = (float)Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
            nx /= length;
            ny /= length;
            nz /= length;
            if (ny < 0f || (Math.Abs(ny) < 0.01f && (nx < 0f || (Math.Abs(nx) < 0.01f && nz < 0f))))
            {
                nx = -nx;
                ny = -ny;
                nz = -nz;
            }

            faces.Add((nx, ny, nz));
        }

        return faces;
    }
}
