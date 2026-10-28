using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The player wants ComfortMe's hover highlight not to cost a few milliseconds on every frame.
    /// Outside its comfort tab ComfortMe switches off the highlight plate on every piece button of
    /// the build menu, every frame, hammer out or not. A plate only lights through a lift, so once
    /// every button has been cleared nothing is lit until the next lift; the clearing runs again
    /// only after a lift or after the menu's buttons were rebuilt (a button back from the menu's
    /// pool may still carry a lit plate). With the patch off, every clearing runs.
    /// </summary>
    public sealed class ComfortPlatesTests
    {
        [Fact]
        public void The_first_clearing_runs_and_a_second_one_with_nothing_lit_since_is_skipped()
        {
            var p = new ComfortPlates();
            Assert.True(p.ClearingRuns(on: true));
            p.Cleared();
            Assert.False(p.ClearingRuns(on: true));
            Assert.False(p.ClearingRuns(on: true));
        }

        [Fact]
        public void A_lit_plate_makes_the_next_clearing_run()
        {
            var p = new ComfortPlates();
            p.Cleared();
            p.Lifted(true);
            Assert.True(p.ClearingRuns(on: true));
            p.Cleared();
            Assert.False(p.ClearingRuns(on: true));
        }

        [Fact]
        public void A_plate_switched_off_lights_nothing_and_keeps_the_clearing_skipped()
        {
            var p = new ComfortPlates();
            p.Cleared();
            p.Lifted(false);
            Assert.False(p.ClearingRuns(on: true));
        }

        [Fact]
        public void Rebuilt_buttons_make_the_next_clearing_run()
        {
            var p = new ComfortPlates();
            p.Cleared();
            p.ButtonsRebuilt();
            Assert.True(p.ClearingRuns(on: true));
        }

        [Fact]
        public void With_the_patch_off_every_clearing_runs()
        {
            var p = new ComfortPlates();
            p.Cleared();
            Assert.True(p.ClearingRuns(on: false));
        }

        [Fact]
        public void The_state_line_counts_the_clearings_skipped_and_what_one_costs_when_run()
        {
            Assert.Equal("off: this game's switch (Patches.ComfortMeHighlight)", ComfortPlates.StateLine(false, true, "1.3.1", null, 0, 0, 0));
            Assert.Equal("not needed: ComfortMe is not installed", ComfortPlates.StateLine(true, false, null, null, 0, 0, 0));
            Assert.Equal("stepped aside: its highlight code changed (an update)", ComfortPlates.StateLine(true, true, "1.3.1", "its highlight code changed (an update)", 0, 0, 0));
            Assert.Equal("working · ComfortMe 1.3.1 · no clearing yet", ComfortPlates.StateLine(true, true, "1.3.1", null, 0, 0, 0));
            Assert.Equal("working · ComfortMe 1.3.1 · 990 of 1000 highlight clearings skipped, 2.50 ms each when run", ComfortPlates.StateLine(true, true, "1.3.1", null, 10, 990, 25));
        }
    }
}
