using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// An admin opens a player in the Network tab and sees that player's last minute, as the
    /// server measured it: ping, connection quality, Steam's send rate, the data waiting to reach
    /// them and the data sent but not yet confirmed. The same view shows this game's own
    /// connection. The minute travels from the server as one line of named values.
    /// </summary>
    public sealed class LinkViewTests
    {
        private static List<DropAnalysis.Sample> Minute(int seconds)
        {
            var list = new List<DropAnalysis.Sample>();
            for (int i = 0; i < seconds; i++)
                list.Add(new DropAnalysis.Sample
                {
                    At = 100 + i, InKbs = 10 + i, OutKbs = 20, QueueKb = i, UnackedKb = i / 2.0, PingMs = 40 + i, MaxGapMs = 60,
                    RateKbs = 300, QualityLocal = 0.99, QualityRemote = 0.98,
                });
            return list;
        }

        [Fact]
        public void A_players_minute_arrives_as_it_was_measured()
        {
            var sent = Minute(60);
            var back = LinkHistory.Parse(LinkHistory.Format(sent));
            Assert.Equal(60, back.Count);
            Assert.Equal(sent.Select(s => s.PingMs), back.Select(s => s.PingMs));
            Assert.Equal(sent.Select(s => s.QueueKb), back.Select(s => s.QueueKb));
            Assert.Equal(0.98, back[59].QualityRemote, 2);
            Assert.Equal(300, back[0].RateKbs);
        }

        [Fact]
        public void A_value_the_server_did_not_measure_stays_unknown()
        {
            var sent = Minute(3);
            sent[1] = new DropAnalysis.Sample { At = 101, PingMs = double.NaN, InKbs = 5 };
            var back = LinkHistory.Parse(LinkHistory.Format(sent));
            Assert.True(double.IsNaN(back[1].PingMs));
            Assert.Equal(5, back[1].InKbs);
        }

        [Fact]
        public void A_cut_off_answer_gives_only_the_seconds_every_measurement_reached()
        {
            var values = LinkHistory.Format(Minute(5));
            values["ping"] = "40,41,42";
            Assert.Equal(3, LinkHistory.Parse(values).Count);
        }

        [Fact]
        public void An_answer_without_a_minute_reads_as_none()
        {
            Assert.Empty(LinkHistory.Parse(new Dictionary<string, string> { { "text", "no such player" } }));
            Assert.Empty(LinkHistory.Parse(null));
        }

        [Fact]
        public void The_view_shows_the_latest_values_and_each_seconds_ping_and_waiting_data()
        {
            var r = LinkResult.Build("Bjorn", Minute(60), new System.DateTime(2026, 10, 13, 22, 0, 0));
            var tiles = r.Blocks.OfType<TilesBlock>().Single().Tiles;
            Assert.Contains(new KeyValuePair<string, string>("Ping", "99 ms"), tiles);
            Assert.Contains(new KeyValuePair<string, string>("Steam's send rate", "300 kB/s"), tiles);
            Assert.Contains(new KeyValuePair<string, string>("Waiting", "59.0 kB"), tiles);
            var strips = r.Blocks.OfType<StripBlock>().ToList();
            Assert.Equal(Minute(60).Select(s => s.PingMs), strips.First(s => s.Title.StartsWith("Ping")).Values);
            Assert.Equal(Minute(60).Select(s => s.QueueKb), strips.First(s => s.Title.StartsWith("Waiting")).Values);
        }

        [Fact]
        public void A_view_with_nothing_measured_yet_says_so()
        {
            var r = LinkResult.Build("Bjorn", new List<DropAnalysis.Sample>(), new System.DateTime(2026, 10, 13, 22, 0, 0));
            Assert.Empty(r.Blocks.OfType<StripBlock>());
            Assert.Contains("Nothing measured", r.Blocks.OfType<TextBlock>().Single().Text);
        }
    }
}
