using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The game patch "Wide containers fit the frame": a container grid wider than its frame (such as a
    /// tombstone ExtraSlots widened) is shrunk evenly until every column fits, instead of the game
    /// centring it and cutting both sides off.
    /// </summary>
    public sealed class ContainerFitTests
    {
        [Fact]
        public void A_grid_that_fits_its_frame_keeps_its_size()
        {
            Assert.Equal(1f, ContainerFit.Scale(slotsWidth: 560f, viewWidth: 560f));
            Assert.Equal(1f, ContainerFit.Scale(slotsWidth: 350f, viewWidth: 560f));
        }

        [Fact]
        public void A_sixteen_column_tomb_in_an_eight_column_frame_is_shown_at_half_size()
        {
            Assert.Equal(0.5f, ContainerFit.Scale(slotsWidth: 1120f, viewWidth: 560f), 3);
        }

        [Fact]
        public void A_twelve_column_tomb_is_shown_at_two_thirds_size()
        {
            Assert.Equal(2f / 3f, ContainerFit.Scale(slotsWidth: 840f, viewWidth: 560f), 3);
        }

        [Fact]
        public void A_grid_without_slots_or_a_frame_without_width_keeps_its_size()
        {
            Assert.Equal(1f, ContainerFit.Scale(slotsWidth: 0f, viewWidth: 560f));
            Assert.Equal(1f, ContainerFit.Scale(slotsWidth: 1120f, viewWidth: 0f));
        }

        [Fact]
        public void The_shrunk_grid_is_moved_by_the_difference_between_the_frame_centre_and_its_own()
        {
            Assert.Equal(40f, ContainerFit.Shift(viewCentre: 300f, slotsLeft: 20f, slotsRight: 500f), 3);
            Assert.Equal(-10f, ContainerFit.Shift(viewCentre: 300f, slotsLeft: 70f, slotsRight: 550f), 3);
        }

        [Fact]
        public void A_grid_already_centred_within_half_a_pixel_is_left_where_it_is()
        {
            Assert.Equal(0f, ContainerFit.Shift(viewCentre: 300f, slotsLeft: 60.2f, slotsRight: 540.4f));
        }

        [Fact]
        public void The_state_says_what_the_patch_does_on_this_game()
        {
            Assert.Equal("off: this game's switch (Patches.WideContainerFit)", ContainerFit.StateLine(on: false, problem: null, fitted: 0));
            Assert.Equal("stepped aside: the game's slot grid changed (an update)", ContainerFit.StateLine(on: true, problem: "the game's slot grid changed (an update)", fitted: 0));
            Assert.Equal("working · a container wider than its frame is shrunk until every column fits", ContainerFit.StateLine(on: true, problem: null, fitted: 0));
            Assert.Equal("working · a container wider than its frame is shrunk until every column fits · 3 shown shrunk", ContainerFit.StateLine(on: true, problem: null, fitted: 3));
        }
    }
}
