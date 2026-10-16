using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The server lists, for its admins, the messages that keep coming at a fixed rate whatever the
    /// players do, per sender: in most 10 s windows of the last 12 minutes (72 windows), at a similar
    /// rate, after at least a minute; a burst now and then, or a rate that swings, is no steady sender.
    /// The rows go to the admin's panel over Vigil's link and into the summary.
    /// </summary>
    public sealed class SteadyMessagesTests
    {
        private static SteadyMessages Run(int windows, System.Func<int, int> perWindow, string mod = "Shield Sledding", string message = "ShieldSledding_State", string from = "Olebole", int bytes = 50)
        {
            var s = new SteadyMessages();
            for (int w = 0; w < windows; w++)
            {
                for (int i = 0; i < perWindow(w); i++) s.Add(mod, message, from, bytes);
                s.EndWindow(10f);
            }
            return s;
        }

        [Fact]
        public void A_message_in_every_window_at_a_similar_rate_is_a_steady_sender()
        {
            var row = Assert.Single(Run(72, w => 55).Steady());
            Assert.Equal("Shield Sledding", row.Mod);
            Assert.Equal("ShieldSledding_State", row.Message);
            Assert.Equal("Olebole", row.From);
            Assert.Equal(5.5, row.PerSecond, 3);
            Assert.Equal(16.1, row.KbPerMinute, 1);
            Assert.Equal(72, row.Seen);
            Assert.Equal(72, row.Of);
        }

        [Fact]
        public void Bursts_now_and_then_are_no_steady_sender()
        {
            Assert.Empty(Run(72, w => w % 2 == 0 ? 50 : 0).Steady());
        }

        [Fact]
        public void A_rate_that_swings_is_no_steady_sender()
        {
            Assert.Empty(Run(72, w => w % 2 == 0 ? 2 : 40).Steady());
        }

        [Fact]
        public void A_new_message_needs_a_minute_before_it_can_be_steady()
        {
            Assert.Empty(Run(5, w => 10).Steady());
            Assert.Single(Run(6, w => 10).Steady());
        }

        [Fact]
        public void Only_the_last_twelve_minutes_count()
        {
            var gone = Run(90, w => w < 10 ? 10 : 0);
            Assert.Empty(gone.Steady());
            Assert.Equal(0, gone.Tracked);
            var row = Assert.Single(Run(100, w => 10).Steady());
            Assert.Equal(72, row.Of);
            Assert.Equal(1.0, Assert.Single(Run(100, w => w < 10 ? 100 : 10).Steady()).PerSecond, 6);
        }

        [Fact]
        public void A_sender_missing_from_a_few_windows_is_still_steady_and_says_in_how_many_it_came()
        {
            var row = Assert.Single(Run(72, w => w % 10 == 5 ? 0 : 10).Steady());
            Assert.Equal(65, row.Seen);
            Assert.Equal(72, row.Of);
        }

        [Fact]
        public void The_most_frequent_sender_comes_first_and_each_sender_counts_apart()
        {
            var s = new SteadyMessages();
            for (int w = 0; w < 10; w++)
            {
                for (int i = 0; i < 7; i++) s.Add("Seasonality", "RPC_SetLastSeasonChange", "the server", 30);
                for (int i = 0; i < 14; i++) s.Add("Network", "Network_MapPos", "the server", 70);
                for (int i = 0; i < 3; i++) s.Add("Network", "Network_MapPos", "Dom", 70);
                s.EndWindow(10f);
            }
            Assert.Equal(new[] { "Network_MapPos the server", "RPC_SetLastSeasonChange the server", "Network_MapPos Dom" },
                s.Steady().Select(r => r.Message + " " + r.From).ToArray());
        }

        [Fact]
        public void The_rows_cross_the_link_and_read_as_one_line_each()
        {
            var rows = Run(72, w => 55).Steady();
            var back = SteadyMessages.DecodeRows(KeyValues.Decode(KeyValues.Encode(SteadyMessages.EncodeRows(rows))));
            var row = Assert.Single(back);
            Assert.Equal("ShieldSledding_State", row.Message);
            Assert.Equal(5.5, row.PerSecond, 2);
            Assert.Equal(72, row.Of);
            Assert.Equal("Shield Sledding · ShieldSledding_State   from Olebole   5.5/s   16.1 kB/min   72 of 72 windows", SteadyMessages.Line(row));
        }

        [Fact]
        public void The_server_tells_it_can_list_steady_messages()
        {
            Assert.Contains(Greeting.Steady, Greeting.Own.Split(','));
        }
    }
}
