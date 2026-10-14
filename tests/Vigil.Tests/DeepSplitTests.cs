using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// A deep capture times a patched game method whole and, separately, only the stretch between
    /// the last prefix and the first postfix (the game's code with any transpiler changes). The
    /// difference is what the mods' prefixes and postfixes cost; the share of calls where the
    /// original never ran shows a mod replacing the game's code.
    /// </summary>
    public sealed class DeepSplitTests
    {
        [Fact]
        public void The_mods_share_is_the_whole_minus_the_games_code()
        {
            string s = DeepSplit.Describe(wholeMs: 3.8, originalMs: 0.4, calls: 100, originalRuns: 100);
            Assert.Contains("game code 0.400 ms", s);
            Assert.Contains("prefixes/postfixes 3.400 ms", s);
            Assert.DoesNotContain("skipped", s);
        }

        [Fact]
        public void Calls_where_the_original_never_ran_are_shown_as_a_share()
        {
            string s = DeepSplit.Describe(1.0, 0.2, calls: 200, originalRuns: 150);
            Assert.Contains("original skipped in 25% of calls", s);
        }

        [Fact]
        public void Timer_noise_never_shows_a_negative_mod_share()
        {
            string s = DeepSplit.Describe(0.10, 0.12, calls: 10, originalRuns: 10);
            Assert.Contains("prefixes/postfixes 0.000 ms", s);
        }

        [Fact]
        public void A_method_that_was_never_called_says_nothing()
        {
            Assert.Equal("", DeepSplit.Describe(0, 0, 0, 0));
        }

        [Fact]
        public void A_method_never_split_says_so()
        {
            Assert.Equal("", DeepSplit.Describe(1.0, 0, calls: 10, originalRuns: -1));
        }

        // The split goes on only once the capture knows which methods take the most time, so it
        // covers part of the capture: its share of the time it measured applies to the whole.

        [Fact]
        public void A_split_measured_over_part_of_the_capture_applies_its_share_to_the_whole_time()
        {
            string s = DeepSplit.DescribeShare(wholeMs: 2.0, splitWholeTicks: 1000, originalTicks: 250, calls: 40, originalRuns: 40);
            Assert.Contains("game code 0.500 ms", s);
            Assert.Contains("prefixes/postfixes 1.500 ms", s);
        }

        [Fact]
        public void A_method_that_got_no_split_time_says_nothing()
        {
            Assert.Equal("", DeepSplit.DescribeShare(2.0, splitWholeTicks: 0, originalTicks: 0, calls: 0, originalRuns: 0));
        }

        [Fact]
        public void Timer_noise_never_puts_the_games_code_above_the_whole_time()
        {
            string s = DeepSplit.DescribeShare(2.0, splitWholeTicks: 1000, originalTicks: 1100, calls: 5, originalRuns: 5);
            Assert.Contains("game code 2.000 ms", s);
            Assert.Contains("prefixes/postfixes 0.000 ms", s);
        }

        [Fact]
        public void A_split_still_reports_how_often_a_prefix_stopped_the_original()
        {
            string s = DeepSplit.DescribeShare(1.0, splitWholeTicks: 800, originalTicks: 200, calls: 100, originalRuns: 60);
            Assert.Contains("original skipped in 40% of calls", s);
        }
    }
}
