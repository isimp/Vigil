using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The panel's tables size their columns by what is in them: every column first gets the width of
    /// its widest cell or heading, room to spare goes to the column meant to take the rest; when the
    /// card is too narrow, numbers keep their width and the widest text gives way first, never below a
    /// floor, and a cut text ends in "…" instead of a half letter.
    /// </summary>
    public sealed class TableLayoutTests
    {
        private static TableLayout.Col Text(float needed, bool flexible = false) => new TableLayout.Col(needed, flexible, number: false);
        private static TableLayout.Col Number(float needed) => new TableLayout.Col(needed, flexible: false, number: true);

        [Fact]
        public void When_everything_fits_each_column_gets_its_content_and_the_rest_goes_to_the_flexible_one()
        {
            var w = TableLayout.Widths(500, new[] { Text(100), Text(80, flexible: true), Number(40) }, minText: 60);
            Assert.Equal(new[] { 100f, 360f, 40f }, w);
        }

        [Fact]
        public void Without_a_flexible_column_the_last_one_takes_the_rest()
        {
            Assert.Equal(new[] { 100f, 140f }, TableLayout.Widths(240, new[] { Text(100), Number(40) }, minText: 60));
        }

        [Fact]
        public void When_too_narrow_numbers_keep_their_width_and_the_widest_text_gives_way_first()
        {
            var w = TableLayout.Widths(300, new[] { Text(400, flexible: true), Text(90), Number(50) }, minText: 60);
            Assert.Equal(new[] { 160f, 90f, 50f }, w);
        }

        [Fact]
        public void When_two_texts_are_too_wide_they_end_up_equally_wide()
        {
            var w = TableLayout.Widths(300, new[] { Text(400), Text(250), Number(50) }, minText: 60);
            Assert.Equal(new[] { 125f, 125f, 50f }, w);
        }

        [Fact]
        public void A_text_never_gets_narrower_than_the_floor_or_its_own_content()
        {
            var w = TableLayout.Widths(100, new[] { Text(400), Text(40), Number(50) }, minText: 60);
            Assert.Equal(new[] { 60f, 40f, 50f }, w);
        }

        [Fact]
        public void A_text_too_long_for_its_width_ends_in_an_ellipsis()
        {
            float Measure(string s) => s.Length * 10f;
            Assert.Equal("Shield", TableLayout.Fit("Shield", 60, Measure));
            Assert.Equal("Shield S…", TableLayout.Fit("Shield Sledding", 90, Measure));
            Assert.Equal("…", TableLayout.Fit("Shield Sledding", 5, Measure));
            Assert.Equal("<color=#fff>long text</color>", TableLayout.Fit("<color=#fff>long text</color>", 20, Measure));
        }
    }
}
