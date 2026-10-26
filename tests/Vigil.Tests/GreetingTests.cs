using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// When the player's game connects, its Vigil greets the server's and learns its version, what it
    /// can do and whether this player is one of its admins. A feature asks for what it needs rather
    /// than for an equal version, so a server one version behind still serves what it has, and the
    /// player is told plainly what is missing, whether the server is still to answer, or that it
    /// has no Vigil at all.
    /// </summary>
    public sealed class GreetingTests
    {
        private static Greeting Answered(string caps = "load,players,recording,status,selftest", bool admin = true)
        {
            var g = new Greeting();
            g.Asked(0);
            g.Answer("0.2.0", caps, admin, 1);
            return g;
        }

        [Fact]
        public void AnAnsweringServerIsDescribedByItsVersionAndWhatItCanDo()
        {
            var g = Answered();
            Assert.Equal(GreetingState.Answered, g.State);
            Assert.Equal("Vigil 0.2.0 on the server, you are an admin: load, players, recording, status, selftest", g.Describe());
        }

        [Fact]
        public void APlayerWhoIsNoAdminIsToldSo()
        {
            Assert.Contains("you are not an admin", Answered(admin: false).Describe());
            Assert.False(Answered(admin: false).IsAdmin);
        }

        [Fact]
        public void AFeatureTheServerHasNeedsNothingMore()
        {
            Assert.Null(Answered().Lacking(Greeting.Status, "show its status"));
        }

        [Fact]
        public void AFeatureAnOlderServerLacksNamesItsVersion()
        {
            var g = Answered("load,players");
            Assert.Equal("the server's Vigil 0.2.0 cannot show its status yet; update it", g.Lacking(Greeting.Status, "show its status"));
        }

        [Fact]
        public void AnAdminFeatureIsRefusedToAPlayerWhoIsNoAdmin()
        {
            var g = Answered(admin: false);
            Assert.Equal("only the server's admins (adminlist.txt) can show its status", g.Lacking(Greeting.Status, "show its status", adminOnly: true));
            Assert.Null(Answered().Lacking(Greeting.Status, "show its status", adminOnly: true));
        }

        [Fact]
        public void WhileTheServerIsStillToAnswerTheFeatureSaysSo()
        {
            var g = new Greeting();
            g.Asked(10);
            g.Tick(15);
            Assert.Equal(GreetingState.Waiting, g.State);
            Assert.Equal("waiting for the server's Vigil to answer", g.Lacking(Greeting.Status, "show its status"));
        }

        [Fact]
        public void AServerWithoutVigilIsToldAfterTenSeconds()
        {
            var g = new Greeting();
            g.Asked(10);
            g.Tick(19.9);
            Assert.Equal(GreetingState.Waiting, g.State);
            g.Tick(20.1);
            Assert.Equal(GreetingState.NoAnswer, g.State);
            Assert.Equal("the server has no Vigil, or one too old to answer", g.Lacking(Greeting.Status, "show its status"));
        }

        [Fact]
        public void AnAnswerAfterGivingUpStillCounts()
        {
            var g = new Greeting();
            g.Asked(0);
            g.Tick(30);
            g.Answer("0.2.0", "status", true, 31);
            Assert.Equal(GreetingState.Answered, g.State);
            Assert.Null(g.Lacking(Greeting.Status, "show its status"));
        }

        [Fact]
        public void NotConnectedMeansNothingIsAsked()
        {
            var g = new Greeting();
            Assert.Equal(GreetingState.NotAsked, g.State);
            Assert.Equal("not connected to a dedicated server", g.Lacking(Greeting.Status, "show its status"));
        }

        [Fact]
        public void ANewConnectionForgetsTheOldServer()
        {
            var g = Answered();
            g.Reset();
            Assert.Equal(GreetingState.NotAsked, g.State);
            Assert.False(g.IsAdmin);
            Assert.Null(g.Version);
        }

        [Fact]
        public void CapabilitiesAreReadLeniently()
        {
            var g = new Greeting();
            g.Asked(0);
            g.Answer("0.3.0", " status , ,Future-Thing,load", false, 1);
            Assert.True(g.Has(Greeting.Status));
            Assert.True(g.Has(Greeting.Load));
            Assert.True(g.Has("future-thing"));
            Assert.False(g.Has(Greeting.Players));
        }

        [Fact]
        public void WhatThisVigilCanDoIsOneLine()
        {
            Assert.Equal("load,players,recording,status,selftest,link,reports,parity,leftovers,alerts,steady,away,wards", Greeting.Own);
        }
    }
}
