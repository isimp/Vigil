using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The patch "OdinOnDemand follows the game's volume": OdinOnDemand plays through mixers of its
    /// own that Valheim's master and music volume never reach, so Vigil sets each mixer's level by them,
    /// and OdinOnDemand's master volume slider becomes the game's music slider, moving one moves the
    /// other; and AzuMiscPatches' mute in background gives the sound back at the master volume instead
    /// of at 100 %, so Vigil puts it back at 100 %, as Valheim keeps it.
    /// </summary>
    public sealed class OdinVolumeTests
    {
        [Fact]
        public void The_mixer_level_is_the_games_master_times_music_volume()
        {
            Assert.Equal(-11.06f, OdinVolume.Db(0.4f, 0.7f), 2);
            Assert.Equal(0f, OdinVolume.Db(1f, 1f), 3);
            Assert.Equal(-6.02f, OdinVolume.Db(0.5f, 1f), 2);
        }

        [Fact]
        public void A_volume_of_nothing_is_the_mixers_floor_and_levels_stay_within_the_mixers_range()
        {
            Assert.Equal(OdinVolume.MinDb, OdinVolume.Db(0f, 1f));
            Assert.Equal(OdinVolume.MinDb, OdinVolume.Db(0.01f, 0.0001f));
            Assert.Equal(0f, OdinVolume.Db(2f, 3f), 3);
        }

        [Fact]
        public void OdinOnDemands_slider_shows_the_music_volume_and_its_label_says_so()
        {
            Assert.Equal("Music Volume 70 %", OdinVolume.SliderLabel(0.7f));
            Assert.Equal("Music Volume 100 %", OdinVolume.SliderLabel(1f));
            Assert.Equal("Music Volume 100 %", OdinVolume.SliderLabel(1.4f));
            Assert.Equal("Music Volume 0 %", OdinVolume.SliderLabel(-0.2f));
            Assert.True(OdinVolume.IsVolumeLabel("Master Volume"));
            Assert.True(OdinVolume.IsVolumeLabel(OdinVolume.SliderLabel(0.35f)));
            Assert.False(OdinVolume.IsVolumeLabel("Listening Distance"));
            Assert.False(OdinVolume.IsVolumeLabel(null));
        }

        [Fact]
        public void Moving_the_slider_sets_the_music_while_the_patch_works_and_gives_its_own_slider_back_after()
        {
            Assert.True(OdinVolume.IsMusicSlider(OdinVolume.SliderMax));
            Assert.False(OdinVolume.IsMusicSlider(OdinVolume.OwnMax));
            Assert.Equal(OdinVolume.SliderMove.SetMusic, OdinVolume.Moved(true, OdinVolume.IsMusicSlider(1f)));
            Assert.Equal(OdinVolume.SliderMove.GiveBack, OdinVolume.Moved(false, OdinVolume.IsMusicSlider(1f)));
            Assert.Equal(OdinVolume.SliderMove.OodsOwn, OdinVolume.Moved(true, OdinVolume.IsMusicSlider(15f)));
            Assert.Equal(OdinVolume.SliderMove.OodsOwn, OdinVolume.Moved(false, OdinVolume.IsMusicSlider(15f)));
        }

        [Fact]
        public void The_music_is_saved_once_the_slider_rests_a_second_or_the_panel_closes()
        {
            Assert.False(OdinVolume.SaveDue(true, 0.5f, panelClosed: false));
            Assert.True(OdinVolume.SaveDue(true, 1f, panelClosed: false));
            Assert.True(OdinVolume.SaveDue(true, 0.1f, panelClosed: true));
            Assert.False(OdinVolume.SaveDue(false, 5f, panelClosed: true));
        }

        [Fact]
        public void The_level_now_reads_as_master_times_music()
        {
            Assert.Equal("master 40 % × music 70 % = radios at 28 % of their own slider", OdinVolume.NowLine(0.4f, 0.7f));
        }

        [Fact]
        public void OdinOnDemands_own_masters_read_as_kept_for_when_the_patch_is_off()
        {
            Assert.Equal("screens −7.5 dB, radios −0.3 dB, girdle +10.9 dB: unused while the patch is on, left in OdinOnDemand's config for when it is off",
                OdinVolume.OwnMastersLine(-7.535f, -0.3461543f, 10.88423f));
            Assert.Equal("screens 0 dB, radios +1 dB, girdle 0 dB: unused while the patch is on, left in OdinOnDemand's config for when it is off",
                OdinVolume.OwnMastersLine(0f, 1f, -0.04f));
        }

        [Fact]
        public void The_sound_goes_back_to_full_only_after_azus_restore_to_the_master_volume()
        {
            Assert.True(OdinVolume.RestoreListener(true, 0.4f, 0.4f, azuMuteOn: true));
            Assert.False(OdinVolume.RestoreListener(false, 0f, 0.4f, azuMuteOn: true));
            Assert.False(OdinVolume.RestoreListener(true, 0.4f, 0.4f, azuMuteOn: false));
            Assert.False(OdinVolume.RestoreListener(true, 0.75f, 0.4f, azuMuteOn: true));
            Assert.False(OdinVolume.RestoreListener(true, 1f, 1f, azuMuteOn: true));
        }

        [Fact]
        public void The_state_says_off_working_stepped_aside_or_not_needed()
        {
            Assert.Equal("off: the server's switch", OdinVolume.StateLine(false, true, "1.3.0", null, true, "2.0.0"));
            Assert.Equal("working · OdinOnDemand 1.3.0 hooked · AzuMiscPatches 2.0.0 hooked", OdinVolume.StateLine(true, true, "1.3.0", null, true, "2.0.0"));
            Assert.Equal("working · OdinOnDemand 1.3.0 hooked · AzuMiscPatches not installed, nothing to give back", OdinVolume.StateLine(true, true, "1.3.0", null, false, null));
            Assert.Equal("not needed: OdinOnDemand is not installed", OdinVolume.StateLine(true, false, null, null, true, "2.0.0"));
            Assert.Equal("stepped aside: OdinOnDemand's volume update was not found (an update changed it)", OdinVolume.StateLine(true, true, "1.4.0", "OdinOnDemand's volume update was not found (an update changed it)", true, "2.0.0"));
        }
    }
}
