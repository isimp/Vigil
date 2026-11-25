using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The player wants the build menu to open and search without the game forcing every canvas
    /// to update and the whole button grid to lay out again, only to link the buttons for the
    /// arrow keys and a controller. With the patch on, the linking waits for the frame after the
    /// request and follows the game's rules on the grid as the screen layout left it: the columns
    /// are the shown buttons on the first row, and each button links to its neighbours, the
    /// grid's edge links taking over at the sides.
    /// </summary>
    public sealed class GridLinksTests
    {
        [Fact]
        public void The_columns_are_the_buttons_on_the_first_row()
        {
            Assert.Equal(3, GridLinks.Columns(new[] { 0f, 0f, 0.005f, -80f, -80f }));
            Assert.Equal(1, GridLinks.Columns(new[] { 0f }));
            Assert.Equal(1, GridLinks.Columns(new[] { 0f, -80f, -160f }));
            Assert.Equal(0, GridLinks.Columns(new float[0]));
        }

        [Fact]
        public void A_button_links_to_its_neighbours_and_the_edges_take_over_at_the_sides()
        {
            // 7 buttons in 3 columns: rows 0 1 2 / 3 4 5 / 6
            Assert.Equal(new GridLinks.Links(-1, 1, -1, 3), GridLinks.Neighbours(0, 7, 3, wrap: false));
            Assert.Equal(new GridLinks.Links(1, -1, -1, 5), GridLinks.Neighbours(2, 7, 3, wrap: false));
            Assert.Equal(new GridLinks.Links(-1, -1, 3, -1), GridLinks.Neighbours(6, 7, 3, wrap: false));
        }

        [Fact]
        public void Down_from_a_row_above_a_short_last_row_goes_to_the_last_button()
        {
            // 7 buttons in 3 columns: rows 0 1 2 / 3 4 5 / 6. Nothing stands under 4 and 5, yet a
            // row follows, so down reaches the last button rather than leaving the grid.
            Assert.Equal(new GridLinks.Links(3, 5, 1, 6), GridLinks.Neighbours(4, 7, 3, wrap: false));
            Assert.Equal(6, GridLinks.Neighbours(5, 7, 3, wrap: false).Down);
            Assert.Equal(-1, GridLinks.Neighbours(6, 7, 3, wrap: false).Down);
            // A full last row has nothing further down.
            Assert.Equal(-1, GridLinks.Neighbours(4, 6, 3, wrap: false).Down);
        }

        [Fact]
        public void One_column_links_up_and_down_only()
        {
            Assert.Equal(new GridLinks.Links(-1, -1, 0, 2), GridLinks.Neighbours(1, 3, 1, wrap: false));
        }

        [Fact]
        public void A_wrapping_grid_leads_past_a_row_end_into_the_next_row_and_back()
        {
            // 7 buttons in 3 columns: rows 0 1 2 / 3 4 5 / 6
            Assert.Equal(3, GridLinks.Neighbours(2, 7, 3, wrap: true).WrapRight);
            Assert.Equal(6, GridLinks.Neighbours(5, 7, 3, wrap: true).WrapRight);
            Assert.Equal(2, GridLinks.Neighbours(3, 7, 3, wrap: true).WrapLeft);
            Assert.Equal(5, GridLinks.Neighbours(6, 7, 3, wrap: true).WrapLeft);
        }

        [Fact]
        public void Wrapping_stops_at_the_grid_ends_and_never_leads_back_to_the_first_button()
        {
            Assert.Equal(-1, GridLinks.Neighbours(6, 7, 3, wrap: true).WrapRight);
            Assert.Equal(-1, GridLinks.Neighbours(0, 7, 3, wrap: true).WrapLeft);
            // In one column every button starts a row; the second button's left stays at the edge.
            Assert.Equal(-1, GridLinks.Neighbours(1, 3, 1, wrap: true).WrapLeft);
            Assert.Equal(1, GridLinks.Neighbours(2, 3, 1, wrap: true).WrapLeft);
        }

        [Fact]
        public void A_grid_that_does_not_wrap_and_buttons_inside_a_row_lead_nowhere_by_wrapping()
        {
            var plain = GridLinks.Neighbours(2, 7, 3, wrap: false);
            Assert.Equal(-1, plain.WrapRight);
            Assert.Equal(-1, GridLinks.Neighbours(3, 7, 3, wrap: false).WrapLeft);
            var inside = GridLinks.Neighbours(4, 7, 3, wrap: true);
            Assert.Equal(-1, inside.WrapLeft);
            Assert.Equal(-1, inside.WrapRight);
        }

        [Fact]
        public void A_side_link_goes_to_the_neighbour_then_the_edge_link_then_the_wrap()
        {
            Assert.Equal("b1", GridLinks.Side(1, "edge", true, 4, i => "b" + i));
            Assert.Equal("edge", GridLinks.Side(-1, "edge", true, 4, i => "b" + i));
            Assert.Equal("b4", GridLinks.Side(-1, "gone", false, 4, i => "b" + i));
            Assert.Null(GridLinks.Side(-1, "gone", false, -1, i => "b" + i));
        }

        [Fact]
        public void A_request_is_linked_in_a_later_frame_only_and_none_waits_without_one()
        {
            Assert.False(GridLinks.Due(requestFrame: 100, now: 100));
            Assert.True(GridLinks.Due(requestFrame: 100, now: 101));
            Assert.False(GridLinks.Due(requestFrame: -1, now: 101));
        }

        [Fact]
        public void The_state_line_counts_the_requests_put_off_and_the_last_linking()
        {
            Assert.Equal("off: this game's switch (Patches.BuildMenuNavigationLater)", GridLinks.StateLine(false, null, 0, 0, 0));
            Assert.Equal("working · nothing put off yet", GridLinks.StateLine(true, null, 0, 0, 0));
            Assert.Equal("working · 12 navigation setups put off to the next frame · the last linked 1,166 buttons in 1.4 ms",
                GridLinks.StateLine(true, null, 12, 1.42, 1166));
            Assert.Equal("stepped aside: the build menu changed (an update)", GridLinks.StateLine(true, "the build menu changed (an update)", 3, 1, 10));
        }
    }
}
