using System;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests;

/// <summary>
/// The view checks spots on the ground around the player, out to the radius set in the config.
/// The spots sit on a fixed world grid so the marks stay where they are while the player walks.
/// </summary>
public sealed class SampleGridTests
{
    [Theory]
    [InlineData(40f, 2f)]
    [InlineData(12.5f, 1f)]
    [InlineData(128f, 4f)]
    public void EverySpotCheckedLiesWithinTheRadiusYouSet(float radius, float spacing)
    {
        var spots = SampleGrid.Around(3090.3f, -2905.7f, radius, spacing);

        Assert.NotEmpty(spots);
        Assert.All(spots, s => Assert.True(Distance(s, 3090.3f, -2905.7f) <= radius + 1e-3f));
    }

    [Fact]
    public void TheWholeCircleIsCheckedWithNoHoleWiderThanTheSpacing()
    {
        const float radius = 40f, spacing = 2f, cx = 103.7f, cz = -55.2f;
        var spots = SampleGrid.Around(cx, cz, radius, spacing);

        // Any place well inside the circle has a checked spot within half a grid diagonal.
        var random = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            double angle = random.NextDouble() * Math.PI * 2, r = Math.Sqrt(random.NextDouble()) * (radius - spacing);
            float x = cx + (float)(Math.Cos(angle) * r), z = cz + (float)(Math.Sin(angle) * r);
            Assert.Contains(spots, s => Distance(s, x, z) <= spacing * 0.7072f);
        }
    }

    [Fact]
    public void TheMarksStayPutWhileYouWalk()
    {
        var before = SampleGrid.Around(10.2f, 20.9f, 30f, 2f);
        var after = SampleGrid.Around(11.7f, 19.4f, 30f, 2f);

        // Spots near both positions are the very same spots, not shifted copies.
        var shared = before.Where(s => Distance(s, 11f, 20f) < 20f).ToList();
        Assert.NotEmpty(shared);
        Assert.All(shared, s => Assert.Contains(s, after));
        Assert.All(before, s =>
        {
            Assert.Equal(0f, s.X % 2f, 3);
            Assert.Equal(0f, s.Z % 2f, 3);
        });
    }

    [Fact]
    public void ALargerRadiusReachesFartherOut()
    {
        float near = SampleGrid.Around(0f, 0f, 20f, 2f).Max(s => Distance(s, 0f, 0f));
        float far = SampleGrid.Around(0f, 0f, 60f, 2f).Max(s => Distance(s, 0f, 0f));

        Assert.True(far > 55f && near < 20.01f);
    }

    [Theory]
    [InlineData(40f, 0f)]
    [InlineData(40f, -2f)]
    [InlineData(40f, float.NaN)]
    [InlineData(0f, 2f)]
    [InlineData(float.NaN, 2f)]
    [InlineData(float.PositiveInfinity, 2f)]
    public void ANonsenseRadiusOrSpacingChecksNothingInsteadOfFreezing(float radius, float spacing)
    {
        Assert.Empty(SampleGrid.Around(0f, 0f, radius, spacing));
    }

    private static float Distance(GridSpot s, float x, float z) => (float)Math.Sqrt(((s.X - x) * (s.X - x)) + ((s.Z - z) * (s.Z - z)));
}
