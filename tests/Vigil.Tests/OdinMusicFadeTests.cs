using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The patch "OdinOnDemand fades the game's music by where it plays": OdinOnDemand lowers the
    /// game's music the nearer the player stands to a playing radio, measured to the radio itself,
    /// so next to a far linked speaker the game's music played at full beside the stream. With the
    /// patch on, the distance is to where the radio's sound comes from.
    /// </summary>
    public sealed class OdinMusicFadeTests
    {
        private const float Lowest = -60f;

        private static FadeSource Radio(float maxDistance, params (float X, float Y, float Z)[] places)
        {
            var list = new List<SoundPlace>();
            foreach (var p in places) list.Add(new SoundPlace(p.X, p.Y, p.Z));
            return new FadeSource(maxDistance, list);
        }

        [Fact]
        public void Standing_by_a_far_speaker_lowers_the_games_music()
        {
            // Each Speaker: one output at the radio, one at the speaker 100 m away; the player stands 2 m from the speaker.
            var radio = Radio(50f, (0f, 30f, 0f), (100f, 30f, 0f));
            float gain = OdinMusicFade.Gain(98f, 30f, 0f, new List<FadeSource> { radio }, Lowest);
            Assert.True(gain < 0.01f, "gain " + gain);
        }

        [Fact]
        public void The_nearest_place_counts_in_whatever_order_the_radios_and_outputs_come()
        {
            var speakerFirst = Radio(50f, (100f, 30f, 0f), (0f, 30f, 0f));
            var radioFirst = Radio(50f, (0f, 30f, 0f), (100f, 30f, 0f));
            float a = OdinMusicFade.Gain(98f, 30f, 0f, new List<FadeSource> { speakerFirst }, Lowest);
            Assert.True(a < 0.01f, "gain " + a);
            Assert.Equal(a, OdinMusicFade.Gain(98f, 30f, 0f, new List<FadeSource> { radioFirst }, Lowest), 6);
            float b = OdinMusicFade.Gain(0f, 0f, 0f, new List<FadeSource> { Radio(10f, (8f, 0f, 0f)), Radio(81f, (30f, 0f, 0f)) }, Lowest);
            Assert.Equal(1f, b);
        }

        [Fact]
        public void In_center_mode_the_distance_is_to_the_middle_the_sound_comes_from()
        {
            // Radio at 0, speaker at 100: the sound comes from 50. Standing at 50 is at the sound.
            var center = Radio(50f, (50f, 30f, 0f));
            Assert.Equal(OdinMusicFade.Gain(0f, 30f, 0f, new List<FadeSource> { Radio(50f, (0f, 30f, 0f)) }, Lowest),
                OdinMusicFade.Gain(50f, 30f, 0f, new List<FadeSource> { center }, Lowest), 5);
            // Standing by the radio, 50 m from the sound and beyond its fade range, the music plays.
            Assert.Equal(1f, OdinMusicFade.Gain(0f, 30f, 0f, new List<FadeSource> { center }, Lowest));
        }

        [Fact]
        public void The_fade_follows_OdinOnDemands_own_curve()
        {
            // OdinOnDemand fades within its listening distance / 1.35, from the lowest dB at the sound to 0 dB at the edge.
            var radio = new List<FadeSource> { Radio(27f, (0f, 0f, 0f)) };
            Assert.Equal(0.001f, OdinMusicFade.Gain(0f, 0f, 0f, radio, Lowest), 5);
            Assert.Equal(0.031623f, OdinMusicFade.Gain(10f, 0f, 0f, radio, Lowest), 5);
            Assert.Equal(1f, OdinMusicFade.Gain(20f, 0f, 0f, radio, Lowest), 5);
            Assert.Equal(1f, OdinMusicFade.Gain(20.1f, 0f, 0f, radio, Lowest));
        }

        [Fact]
        public void The_radio_whose_sound_is_nearest_decides_with_its_own_range()
        {
            // A loud far-reaching radio 30 m away and a quiet short one 8 m away: the near one's range of 7.4 m leaves the music alone.
            var far = Radio(81f, (30f, 0f, 0f));
            var near = Radio(10f, (8f, 0f, 0f));
            Assert.Equal(1f, OdinMusicFade.Gain(0f, 0f, 0f, new List<FadeSource> { far, near }, Lowest));
            Assert.True(OdinMusicFade.Gain(0f, 0f, 0f, new List<FadeSource> { far }, Lowest) < 1f);
        }

        [Fact]
        public void Without_a_playing_radio_or_a_range_the_music_is_left_alone()
        {
            Assert.Equal(1f, OdinMusicFade.Gain(0f, 0f, 0f, new List<FadeSource>(), Lowest));
            Assert.Equal(1f, OdinMusicFade.Gain(0f, 0f, 0f, new List<FadeSource> { Radio(50f) }, Lowest));
            Assert.Equal(1f, OdinMusicFade.Gain(0f, 0f, 0f, new List<FadeSource> { Radio(0f, (0f, 0f, 0f)) }, Lowest));
        }

        [Fact]
        public void The_patches_tab_says_what_the_patch_does_on_this_game()
        {
            Assert.Equal("off: this game's switch (Patches.OdinOnDemandMusicFade)", OdinMusicFade.StateLine(false, true, "1.3.0", null, false));
            Assert.Equal("not needed: OdinOnDemand is not installed", OdinMusicFade.StateLine(true, false, null, null, false));
            Assert.Equal("stepped aside: OdinOnDemand's music fade code changed (an update)", OdinMusicFade.StateLine(true, true, "1.3.1", "OdinOnDemand's music fade code changed (an update)", false));
            Assert.Equal("working · OdinOnDemand 1.3.0", OdinMusicFade.StateLine(true, true, "1.3.0", null, false));
            Assert.Equal("working · OdinOnDemand 1.3.0 · its Music Fade Type is None, so it leaves the music alone", OdinMusicFade.StateLine(true, true, "1.3.0", null, true));
        }
    }
}
