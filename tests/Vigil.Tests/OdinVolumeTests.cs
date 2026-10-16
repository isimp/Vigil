using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The patch "OdinOnDemand follows the game's volume": OdinOnDemand plays through mixers of its
    /// own that Valheim's master and music volume never reach, so Vigil moves each mixer's level by them;
    /// and AzuMiscPatches' mute in background gives the sound back at the master volume instead of at
    /// 100 %, so Vigil puts it back at 100 %, as Valheim keeps it.
    /// </summary>
    public sealed class OdinVolumeTests
    {
        [Fact]
        public void The_mixer_level_is_its_own_moved_by_master_times_music_volume()
        {
            Assert.Equal(-11.06f, OdinVolume.Db(0f, 0.4f, 0.7f), 2);
            Assert.Equal(-5f, OdinVolume.Db(-5f, 1f, 1f), 3);
            Assert.Equal(6.0f - 6.02f, OdinVolume.Db(6f, 0.5f, 1f), 2);
        }

        [Fact]
        public void A_volume_of_nothing_is_the_mixers_floor_and_levels_stay_within_the_mixers_range()
        {
            Assert.Equal(OdinVolume.MinDb, OdinVolume.Db(0f, 0f, 1f));
            Assert.Equal(OdinVolume.MinDb, OdinVolume.Db(-75f, 0.01f, 0.1f));
            Assert.Equal(OdinVolume.MaxDb, OdinVolume.Db(40f, 1f, 1f));
            Assert.Equal(-5f, OdinVolume.Db(-5f, 2f, 3f), 3);
        }

        [Fact]
        public void The_level_now_reads_as_master_times_music()
        {
            Assert.Equal("master 40 % × music 70 % = radios at 28 % of their own level", OdinVolume.NowLine(0.4f, 0.7f));
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
