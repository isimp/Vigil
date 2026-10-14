using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// A beacon object carries a number that goes up ten times a second. The machine that writes
    /// it remembers when it wrote each number, so the number the other machine reports holding
    /// tells how old that machine's copy of the world is.
    /// </summary>
    public sealed class SeqClockTests
    {
        [Fact]
        public void The_age_of_a_held_number_is_the_time_since_it_was_written()
        {
            var c = new SeqClock(100);
            int a = c.Next(10.0);
            c.Next(10.1);
            c.Next(10.2);
            Assert.Equal(2.0, c.Age(a, 12.0), 6);
            Assert.Equal(3, c.Latest);
        }

        [Fact]
        public void A_number_never_written_has_no_age()
        {
            var c = new SeqClock(100);
            c.Next(1);
            Assert.True(double.IsNaN(c.Age(0, 5)));
            Assert.True(double.IsNaN(c.Age(7, 5)));
        }

        [Fact]
        public void A_number_older_than_the_memory_reports_at_least_the_oldest_age_known()
        {
            var c = new SeqClock(10);
            int first = c.Next(0);
            for (int i = 1; i <= 20; i++) c.Next(i);
            // Its time is forgotten, but it is at least as old as the oldest number still known.
            Assert.True(c.Age(first, 30) >= 30 - 11);
        }
    }
}
