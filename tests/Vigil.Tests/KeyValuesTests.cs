using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The two machines exchange their measurements as one line of named values, so either side
    /// can add a value without breaking the other.
    /// </summary>
    public sealed class KeyValuesTests
    {
        [Fact]
        public void Values_survive_the_round_trip_in_any_culture()
        {
            var prev = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                string line = KeyValues.Encode(new Dictionary<string, object> { { "fps", 29.5 }, { "stage", 3 }, { "mode", "both" } });
                var d = KeyValues.Decode(line);
                Assert.Equal(29.5, KeyValues.Number(d, "fps"));
                Assert.Equal(3, KeyValues.Number(d, "stage"));
                Assert.Equal("both", d["mode"]);
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = prev; }
        }

        [Fact]
        public void A_value_the_other_side_does_not_send_reads_as_missing()
        {
            Assert.True(double.IsNaN(KeyValues.Number(KeyValues.Decode("a=1"), "b")));
            Assert.True(double.IsNaN(KeyValues.Number(KeyValues.Decode("a=x"), "a")));
        }

        [Fact]
        public void Separators_inside_a_text_value_cannot_break_the_line()
        {
            var d = KeyValues.Decode(KeyValues.Encode(new Dictionary<string, object> { { "why", "a=b; c" }, { "n", 1 } }));
            Assert.Equal(1, KeyValues.Number(d, "n"));
            Assert.Equal("a:b, c", d["why"]);
        }

        [Fact]
        public void Missing_numbers_are_left_out_of_the_line()
        {
            Assert.Equal("a=1", KeyValues.Encode(new Dictionary<string, object> { { "a", 1 }, { "b", double.NaN } }));
        }

        [Fact]
        public void A_broken_line_reads_as_empty_rather_than_failing()
        {
            Assert.Empty(KeyValues.Decode(null));
            Assert.Empty(KeyValues.Decode(";;=;x"));
        }
    }
}
