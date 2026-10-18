using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// An admin sees the dedicated server's state in the panel: how it runs, what it records, and
    /// each player's connection as the server sees it. It travels as one line of named values, so a
    /// newer server can add a value without confusing an older player's game, and a value the server
    /// could not measure stays unknown instead of reading as zero.
    /// </summary>
    public sealed class ServerStatusTests
    {
        private static ServerStatus Sample()
        {
            var s = new ServerStatus
            {
                Version = "0.2.0", Fps = 30, FrameAvgMs = 33.3, FrameMaxMs = 41, CpuPct = 5.5, PrivateMb = 3200,
                MemLoadPct = 43, UptimeMinutes = 125, Recording = "auto, recording (auto: 2 players connected)",
                Incidents = 3, LastIncident = "21:15:22 silent: nothing received from D'Arcy for 21930 ms",
            };
            s.Players.Add(new ServerStatus.Player { Name = "Bjorn", PingMs = 31, QualityLocal = 1, QualityRemote = 0.98, QueueKb = 1.5, UnackedKb = 0.2, OutKbs = 45, InKbs = 120, ObjectsKnown = 15099, LongestGapMs = 67 });
            s.Players.Add(new ServerStatus.Player { Name = "D'Arcy", PingMs = 16, QueueKb = 3 });
            return s;
        }

        [Fact]
        public void The_server_tells_an_admin_what_its_join_packages_saved_and_an_older_server_tells_nothing()
        {
            var s = Sample();
            s.JoinPackages = "Drop That: built once in 819 ms, sent from the cache to 1 join (about 0.8 s of standing still saved) · Spawn That: built once in 766 ms, sent from the cache to 1 join (about 0.8 s of standing still saved)";
            Assert.Equal(s.JoinPackages, ServerStatus.Decode(s.Encode()).JoinPackages);
            Assert.Null(ServerStatus.Decode(Sample().Encode()).JoinPackages);
        }

        [Fact]
        public void TheServersStateArrivesAsItWasSent()
        {
            var back = ServerStatus.Decode(Sample().Encode());
            Assert.Equal("0.2.0", back.Version);
            Assert.Equal(30, back.Fps);
            Assert.Equal(41, back.FrameMaxMs);
            Assert.Equal(125, back.UptimeMinutes);
            Assert.Equal(3, back.Incidents);
            Assert.StartsWith("21:15:22 silent", back.LastIncident);
            Assert.Equal(2, back.Players.Count);
            Assert.Equal("Bjorn", back.Players[0].Name);
            Assert.Equal(15099, back.Players[0].ObjectsKnown);
            Assert.Equal(0.98, back.Players[0].QualityRemote);
            Assert.Equal("D'Arcy", back.Players[1].Name);
        }

        [Fact]
        public void AValueTheServerCouldNotMeasureStaysUnknown()
        {
            var back = ServerStatus.Decode(Sample().Encode());
            Assert.True(double.IsNaN(back.Players[1].UnackedKb));
            Assert.True(double.IsNaN(back.Players[1].QualityLocal));
        }

        [Fact]
        public void APlayerNameWithSeparatorsStaysReadable()
        {
            var s = new ServerStatus { Version = "0.2.0" };
            s.Players.Add(new ServerStatus.Player { Name = "a;b=c", PingMs = 10 });
            var back = ServerStatus.Decode(s.Encode());
            Assert.Single(back.Players);
            Assert.Equal("a,b:c", back.Players[0].Name);
            Assert.Equal(10, back.Players[0].PingMs);
        }

        [Fact]
        public void ValuesANewerServerAddsAreIgnored()
        {
            var back = ServerStatus.Decode(Sample().Encode() + ";weather=rain;p0.mood=happy;p7.name=Ghost");
            Assert.Equal(2, back.Players.Count);
            Assert.Equal("0.2.0", back.Version);
        }

        [Fact]
        public void AnEmptyServerHasNoPlayers()
        {
            var back = ServerStatus.Decode(new ServerStatus { Version = "0.2.0", Fps = 30 }.Encode());
            Assert.Empty(back.Players);
            Assert.Equal(30, back.Fps);
        }

        [Fact]
        public void ADamagedLineGivesWhatCanBeRead()
        {
            var back = ServerStatus.Decode("v=0.2.0;fps=abc;p0.name=Bjorn;p0.ping=x;garbage");
            Assert.Equal("0.2.0", back.Version);
            Assert.True(double.IsNaN(back.Fps));
            Assert.Single(back.Players);
            Assert.True(double.IsNaN(back.Players[0].PingMs));
        }
    }
}
