using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Time spent per network message handler, per object type created and per throwing mod is
    /// summed by label; the labels that cost the most come first, with how often and how bad at worst.
    /// </summary>
    public sealed class ValueTallyTests
    {
        [Fact]
        public void The_label_with_the_most_total_time_comes_first()
        {
            var t = new ValueTally("ms");
            t.Add("cheap but frequent", 0.1);
            t.Add("cheap but frequent", 0.1);
            t.Add("cheap but frequent", 0.1);
            t.Add("one slow call", 5.0);
            string s = t.Summary(10f, 5);
            Assert.True(s.IndexOf("one slow call") < s.IndexOf("cheap but frequent"));
            Assert.Contains("worst 5.0 ms", s);
            Assert.Contains("4 calls", s);
        }

        [Fact]
        public void Without_a_unit_it_counts_and_ranks_by_count()
        {
            var t = new ValueTally(null);
            t.Add("NullReferenceException @ ModA", 0);
            t.Add("NullReferenceException @ ModA", 0);
            t.Add("KeyNotFoundException @ ModB", 0);
            string s = t.Summary(2f, 5);
            Assert.True(s.IndexOf("ModA") < s.IndexOf("ModB"));
            Assert.DoesNotContain(" ms", s);
            Assert.Contains("1.0/s", s);
        }

        [Fact]
        public void A_window_ends_into_the_session_and_peek_leaves_it_running()
        {
            var t = new ValueTally("ms");
            t.Add("a", 1);
            Assert.Contains("a", t.Peek(1f, 5));
            Assert.Contains("a", t.EndWindow(1f, 5));
            Assert.DoesNotContain(" a ", t.Peek(1f, 5));
            t.Add("b", 2);
            string run = t.Summary(2f, 5);
            Assert.Contains("a", run);
            Assert.Contains("b", run);
        }

        [Fact]
        public void The_top_of_each_second_is_reported_and_then_cleared()
        {
            var t = new ValueTally("ms");
            t.Add("x", 1); t.Add("y", 3); t.Add("y", 1);
            t.EndSecond(out string top, out double value, out long count);
            Assert.Equal("y", top);
            Assert.Equal(4, value, 3);
            Assert.Equal(3, count);
            t.EndSecond(out top, out value, out count);
            Assert.Equal("", top);
            Assert.Equal(0, count);
        }

        [Fact]
        public void The_worst_value_survives_smaller_ones_after_it()
        {
            var t = new ValueTally("ms");
            t.Add("x", 5.0);
            t.Add("x", 1.0);
            Assert.Contains("worst 5.0 ms", t.Summary(1f, 5));
        }

        [Fact]
        public void More_labels_than_shown_are_mentioned()
        {
            var t = new ValueTally(null);
            for (int i = 0; i < 7; i++) t.Add("k" + i, 0);
            Assert.Contains("2 more", t.Summary(1f, 5));
        }
    }
}
