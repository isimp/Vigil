using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests;

/// <summary>
/// When a check finishes the player is told how many open spots there are and how far the view
/// reaches. A spawn that is allowed inside bases (a raid, or one a spawn mod changed) is named,
/// because the marks cannot promise safety from it.
/// </summary>
public sealed class ReadoutTests
{
    [Fact]
    public void TheMessageSaysHowManyOpenSpotsAndHowFarTheViewReaches()
    {
        Assert.Equal("Spawn gaps: 37 open spots within 40 m", Readout.Summary(37, 40f, 0));
    }

    [Fact]
    public void OneOpenSpotIsSaidAsOne()
    {
        Assert.Equal("Spawn gaps: 1 open spot within 40 m", Readout.Summary(1, 40f, 0));
    }

    [Fact]
    public void NoOpenSpotsIsSaidPlainly()
    {
        Assert.Equal("No spawn gaps within 64 m", Readout.Summary(0, 64f, 0));
    }

    [Fact]
    public void SpawnsAllowedInsideBasesAreMentionedWithTheCount()
    {
        Assert.Equal("Spawn gaps: 3 open spots within 40 m. 2 spawns ignore bases, see the log",
            Readout.Summary(3, 40f, 2));
        Assert.Equal("No spawn gaps within 40 m. 1 spawn ignores bases, see the log",
            Readout.Summary(0, 40f, 1));
    }

    [Fact]
    public void ARadiusWithAFractionIsRoundedForTheMessage()
    {
        Assert.Equal("Spawn gaps: 2 open spots within 13 m", Readout.Summary(2, 12.6f, 0));
    }

    [Fact]
    public void OnlyEnabledSpawnsAllowedInsideBasesAreNamedAndEachOnce()
    {
        var entries = new List<SpawnEntry>
        {
            new("world", "Greydwarf", enabled: true, insideBases: false),
            new("raid army_eikthyr", "Boar", enabled: true, insideBases: true),
            new("raid army_eikthyr", "Boar", enabled: true, insideBases: true),
            new("world", "Neck", enabled: false, insideBases: true),
            new("world", "Deer", enabled: true, insideBases: true),
        };

        var named = Readout.IgnoringBases(entries);

        Assert.Equal(new[] { "raid army_eikthyr: Boar", "world: Deer" }, named);
    }

    [Fact]
    public void NoSpawnIsNamedWhenAllRespectBases()
    {
        var entries = new List<SpawnEntry> { new("world", "Greydwarf", enabled: true, insideBases: false) };

        Assert.Empty(Readout.IgnoringBases(entries));
    }
}
