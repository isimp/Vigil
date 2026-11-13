using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The patch "OdinOnDemand's stream cannot crash the game": OdinOnDemand 1.3.0 asks the radio's
    /// Unity sound source for its play position from Unity's sound thread, and the game crashed
    /// there. With the patch on, the game's main thread notes the play position once a frame and
    /// the sound thread works it out from that note, so it never asks Unity.
    /// </summary>
    public sealed class OdinAudioClockTests
    {
        private const long TicksPerSecond = 10_000_000;
        private const int ClipFrames = 192_000;
        private const int Rate = 48_000;

        private static PlayNote Note(int position, bool playing, float pitch = 1f, long at = 0) =>
            new PlayNote(position, at, playing, pitch, Rate, ClipFrames);

        [Fact]
        public void A_playing_radio_has_moved_on_by_the_time_passed_since_the_note()
        {
            // Noted at 1000 frames; 50 ms later the stream has played 2400 more.
            Assert.Equal(3400, OdinAudioClock.Position(Note(1000, true), TicksPerSecond / 20, TicksPerSecond));
        }

        [Fact]
        public void A_stopped_or_paused_radio_stays_where_it_was_noted()
        {
            Assert.Equal(1000, OdinAudioClock.Position(Note(1000, false), TicksPerSecond * 3, TicksPerSecond));
        }

        [Fact]
        public void The_position_runs_round_the_looping_clip()
        {
            // 1 s before the clip's end, 2.5 s later: 1.5 s into the next round.
            Assert.Equal(72_000, OdinAudioClock.Position(Note(ClipFrames - Rate, true), TicksPerSecond * 5 / 2, TicksPerSecond));
        }

        [Fact]
        public void A_long_main_thread_stall_still_gives_a_place_on_the_clip()
        {
            // The earlier crashes came during frames of several seconds: 9 s on a 4 s clip.
            int p = OdinAudioClock.Position(Note(0, true), TicksPerSecond * 9, TicksPerSecond);
            Assert.Equal(48_000, p);
        }

        [Fact]
        public void A_radio_heard_faster_or_slower_moves_by_its_pitch()
        {
            Assert.Equal(2400, OdinAudioClock.Position(Note(0, true, pitch: 0.5f), TicksPerSecond / 10, TicksPerSecond));
        }

        [Fact]
        public void A_fresh_stream_with_no_note_yet_starts_at_its_beginning()
        {
            // A new clip plays from 0, which is also what Unity would answer before the first frame noted it.
            Assert.Equal(0, OdinAudioClock.Position(null, TicksPerSecond, TicksPerSecond));
        }

        [Fact]
        public void A_clock_reading_earlier_than_the_note_does_not_move_it_back()
        {
            Assert.Equal(1000, OdinAudioClock.Position(Note(1000, true, at: TicksPerSecond), 0, TicksPerSecond));
        }

        [Fact]
        public void A_note_without_clip_facts_keeps_its_position()
        {
            var odd = new PlayNote(1000, 0, true, 1f, 0, 0);
            Assert.Equal(1000, OdinAudioClock.Position(odd, TicksPerSecond, TicksPerSecond));
        }

        [Fact]
        public void The_state_line_says_whether_it_works_and_why_not()
        {
            Assert.StartsWith("off", OdinAudioClock.StateLine(false, true, "1.3.0", null));
            Assert.StartsWith("not needed", OdinAudioClock.StateLine(true, false, null, null));
            Assert.Equal("stepped aside: its code changed", OdinAudioClock.StateLine(true, true, "1.3.1", "its code changed"));
            Assert.StartsWith("working · OdinOnDemand 1.3.0", OdinAudioClock.StateLine(true, true, "1.3.0", null));
        }
    }
}
