using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// A mod that is removed leaves its objects in the world; the game cannot build them and warns
    /// about them over and over. The cleanup picks out exactly those objects near the player, and nothing
    /// else, so they can be listed and then removed on purpose.
    /// </summary>
    public sealed class OrphansTests
    {
        private const int Horse = 439296140; // "Missing prefab hash: 439296140" in the game's log
        private const int Chest = 1000;
        private const int Wall = 2000;

        private static readonly HashSet<int> Installed = new HashSet<int> { Chest, Wall };

        private static List<WorldObject> World() => new List<WorldObject>
        {
            new WorldObject(1, Chest, 0, 0, 5),
            new WorldObject(2, Horse, 0, 0, 30),
            new WorldObject(3, Wall, 0, 0, 2),
            new WorldObject(4, Horse, 0, 0, 10),
            new WorldObject(5, 777, 300, 0, 0),
        };

        private static List<WorldObject> Pick(float radius = 100, int? only = null) =>
            Orphans.Select(World(), h => Installed.Contains(h), 0, 0, 0, radius, only);

        [Fact]
        public void OnlyObjectsNoInstalledModProvidesArePicked()
        {
            Assert.All(Pick(1000), o => Assert.DoesNotContain(o.Hash, Installed));
            Assert.Equal(3, Pick(1000).Count);
        }

        [Fact]
        public void ObjectsBeyondTheRadiusAreLeftAlone()
        {
            Assert.DoesNotContain(Pick(100), o => o.Id == 5);
        }

        [Fact]
        public void TheNearestComesFirst()
        {
            Assert.Equal(new long[] { 4, 2 }, Pick(100).Select(o => o.Id).ToArray());
        }

        [Fact]
        public void OnePrefabCanBeTargetedAlone()
        {
            Assert.Equal(new long[] { 4, 2, }, Pick(1000, only: Horse).Select(o => o.Id).ToArray());
            Assert.Equal(new long[] { 5 }, Pick(1000, only: 777).Select(o => o.Id).ToArray());
        }

        [Fact]
        public void AWorldWithoutLeftoversGivesNothing()
        {
            var clean = new List<WorldObject> { new WorldObject(1, Chest, 0, 0, 1) };

            Assert.Empty(Orphans.Select(clean, h => Installed.Contains(h), 0, 0, 0, 100, null));
        }

        [Fact]
        public void EachPickKnowsHowFarAwayItIs()
        {
            Assert.Equal(10, Pick(100).First().Distance, 3);
        }

        [Fact]
        public void TheLeftoverHorseIsRecognisedByName()
        {
            Assert.Equal("rae_OdinHorse (OdinHorse, removed)", KnownPrefabs.NameOf(Horse));
        }

        [Fact]
        public void AnUnknownLeftoverIsShownByItsHash()
        {
            Assert.Equal("unknown prefab, hash 777", KnownPrefabs.NameOf(777));
        }

        [Fact]
        public void HashesAreTheGamesOwn()
        {
            Assert.Equal(Horse, StableHash.Of("rae_OdinHorse"));
        }
    }
}
