using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests;

/// <summary>
/// A spot is marked when the game could spawn a creature there: outside every base's spawn
/// protection and with nothing solid above or below it. The check runs a share of the spots a
/// frame so the game never stutters, and the marks change only once a check is complete.
/// </summary>
public sealed class SurveyTests
{
    private static List<GridSpot> Row(int n) => Enumerable.Range(0, n).Select(i => new GridSpot(i * 2f, 0f)).ToList();

    [Fact]
    public void OnlySpotsOutsideEveryBaseAndUnderOpenSkyAreMarked()
    {
        var kinds = new[] { Ground.Open, Ground.Protected, Ground.Covered, Ground.Unloaded, Ground.Open };
        var survey = new Survey(Row(kinds.Length), (x, z) => new Sample(kinds[(int)(x / 2f)], 30f, 0f, 1f, 0f));

        survey.Step(100);

        Assert.True(survey.Done);
        Assert.Equal(new[] { 0f, 8f }, survey.Open.Select(o => o.X));
        Assert.Equal(2, survey.Count(Ground.Open));
        Assert.Equal(1, survey.Count(Ground.Protected));
        Assert.Equal(1, survey.Count(Ground.Covered));
    }

    [Fact]
    public void AMarkSitsOnTheGroundTheGameWouldSpawnOn()
    {
        var survey = new Survey(Row(1), (x, z) => new Sample(Ground.Open, 41.5f, 0.1f, 0.99f, 0f));

        survey.Step(1);

        var mark = Assert.Single(survey.Open);
        Assert.Equal(41.5f, mark.Y);
        Assert.Equal(0.1f, mark.NormalX);
        Assert.Equal(0.99f, mark.NormalY);
    }

    [Fact]
    public void ACheckSpreadsOverFramesWithinItsShare()
    {
        int asked = 0;
        var survey = new Survey(Row(10), (x, z) => { asked++; return new Sample(Ground.Open, 0f, 0f, 1f, 0f); });

        survey.Step(4);
        Assert.Equal(4, asked);
        Assert.False(survey.Done);

        survey.Step(4);
        survey.Step(4);
        Assert.Equal(10, asked);
        Assert.True(survey.Done);

        survey.Step(4);
        Assert.Equal(10, asked);
    }

    [Fact]
    public void NothingIsShownUntilTheCheckHasFinished()
    {
        var survey = new Survey(Row(10), (x, z) => new Sample(Ground.Open, 0f, 0f, 1f, 0f));

        survey.Step(9);

        Assert.False(survey.Done);
        Assert.Empty(survey.Open);

        survey.Step(9);

        Assert.Equal(10, survey.Open.Count);
    }

    [Fact]
    public void AnEmptyAreaFinishesAtOnceWithNothingMarked()
    {
        var survey = new Survey(new List<GridSpot>(), (x, z) => new Sample(Ground.Open, 0f, 0f, 1f, 0f));

        survey.Step(10);

        Assert.True(survey.Done);
        Assert.Empty(survey.Open);
    }
}
