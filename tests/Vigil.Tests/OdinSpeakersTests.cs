using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The patch "OdinOnDemand plays from where its speakers stand": a radio keeps each linked
    /// speaker's place from the moment it was linked and plays from there, so a speaker moved with
    /// EasyRelocate went on sounding at its old place. With the patch on, the radio plays from where
    /// each speaker stands, and a player's own volume for a radio goes with the radio when it moves.
    /// </summary>
    public sealed class OdinSpeakersTests
    {
        private static SpeakerSpot Spot(string id, float x, float y, float z) => new SpeakerSpot(id, x, y, z);

        // ---- where the radio plays from ----

        [Fact]
        public void A_moved_speaker_plays_from_its_new_place()
        {
            var links = new List<SpeakerSpot> { Spot("a", 10f, 30f, 10f) };
            var loaded = new List<SpeakerSpot> { Spot("a", 25f, 31f, 12f) };
            var now = OdinSpeakers.Follow(links, loaded);
            Assert.NotNull(now);
            Assert.Equal("a", now[0].Guid);
            Assert.Equal(25f, now[0].X);
            Assert.Equal(31f, now[0].Y);
            Assert.Equal(12f, now[0].Z);
        }

        [Fact]
        public void A_speaker_that_stayed_changes_nothing()
        {
            var links = new List<SpeakerSpot> { Spot("a", 10f, 30f, 10f) };
            Assert.Null(OdinSpeakers.Follow(links, new List<SpeakerSpot> { Spot("a", 10f, 30f, 10f) }));
        }

        [Fact]
        public void Less_than_a_centimetre_is_not_a_move()
        {
            var links = new List<SpeakerSpot> { Spot("a", 10f, 30f, 10f) };
            Assert.Null(OdinSpeakers.Follow(links, new List<SpeakerSpot> { Spot("a", 10.009f, 30f, 10f) }));
            Assert.NotNull(OdinSpeakers.Follow(links, new List<SpeakerSpot> { Spot("a", 10.011f, 30f, 10f) }));
        }

        [Fact]
        public void A_speaker_too_far_to_be_loaded_keeps_its_saved_place()
        {
            var links = new List<SpeakerSpot> { Spot("a", 10f, 30f, 10f) };
            Assert.Null(OdinSpeakers.Follow(links, new List<SpeakerSpot> { Spot("b", 50f, 30f, 10f) }));
            Assert.Null(OdinSpeakers.Follow(links, new List<SpeakerSpot>()));
        }

        [Fact]
        public void Only_the_moved_speaker_changes_the_others_keep_their_places()
        {
            var links = new List<SpeakerSpot> { Spot("a", 10f, 30f, 10f), Spot("b", -4f, 30f, 2f) };
            var loaded = new List<SpeakerSpot> { Spot("b", -4f, 30f, 2f), Spot("a", 12f, 30f, 10f) };
            var now = OdinSpeakers.Follow(links, loaded);
            Assert.Equal(2, now.Length);
            Assert.Equal(12f, now[0].X);
            Assert.Equal("b", now[1].Guid);
            Assert.Equal(-4f, now[1].X);
            Assert.Equal(2f, now[1].Z);
        }

        [Fact]
        public void Two_loaded_speakers_with_one_id_leave_the_link_alone()
        {
            // A building mod that copies a piece with its saved data copies the speaker's id too: which one is meant is unknown.
            var links = new List<SpeakerSpot> { Spot("a", 10f, 30f, 10f) };
            var loaded = new List<SpeakerSpot> { Spot("a", 20f, 30f, 10f), Spot("a", 40f, 30f, 10f) };
            Assert.Null(OdinSpeakers.Follow(links, loaded));
        }

        [Fact]
        public void A_link_or_speaker_without_an_id_is_never_matched()
        {
            Assert.Null(OdinSpeakers.Follow(new List<SpeakerSpot> { Spot("", 10f, 30f, 10f) }, new List<SpeakerSpot> { Spot("", 20f, 30f, 10f) }));
            Assert.Null(OdinSpeakers.Follow(new List<SpeakerSpot> { Spot(null, 10f, 30f, 10f) }, new List<SpeakerSpot> { Spot(null, 20f, 30f, 10f) }));
        }

        [Fact]
        public void A_radio_is_redone_for_a_speaker_only_when_it_is_linked_to_it_and_plays_from_elsewhere()
        {
            var links = new List<SpeakerSpot> { Spot("a", 10f, 30f, 10f) };
            Assert.True(OdinSpeakers.Stale(links, Spot("a", 14f, 30f, 10f)));
            Assert.False(OdinSpeakers.Stale(links, Spot("a", 10f, 30f, 10f)));
            Assert.False(OdinSpeakers.Stale(links, Spot("b", 14f, 30f, 10f)));
            Assert.False(OdinSpeakers.Stale(links, Spot("", 14f, 30f, 10f)));
            Assert.False(OdinSpeakers.Stale(new List<SpeakerSpot> { Spot("", 10f, 30f, 10f) }, Spot("", 14f, 30f, 10f)));
        }

        // ---- a player's own volume ----

        [Fact]
        public void A_moved_radio_takes_the_players_own_volume_along()
        {
            Assert.True(OdinSpeakers.CarryVolume("OdinOnDemand.Volume.1.0,30.0,2.0", "OdinOnDemand.Volume.6.0,30.0,2.0", oldSaved: true));
        }

        [Fact]
        public void A_volume_is_carried_only_when_one_was_set_and_the_radio_has_a_new_key()
        {
            // Never set: OdinOnDemand's default stays. Moved less than its rounding (0.1 m): same key.
            Assert.False(OdinSpeakers.CarryVolume("OdinOnDemand.Volume.1.0,30.0,2.0", "OdinOnDemand.Volume.6.0,30.0,2.0", oldSaved: false));
            Assert.False(OdinSpeakers.CarryVolume("OdinOnDemand.Volume.1.0,30.0,2.0", "OdinOnDemand.Volume.1.0,30.0,2.0", oldSaved: true));
            // A cart without an id has no key at all.
            Assert.False(OdinSpeakers.CarryVolume(null, "OdinOnDemand.Volume.6.0,30.0,2.0", oldSaved: true));
            Assert.False(OdinSpeakers.CarryVolume("OdinOnDemand.Volume.1.0,30.0,2.0", null, oldSaved: true));
        }

        // ---- what the Patches tab says ----

        [Fact]
        public void The_patches_tab_says_what_the_patch_does_on_this_game()
        {
            Assert.Equal("off: this game's switch (Patches.OdinOnDemandSpeakersFollow)", OdinSpeakers.StateLine(false, true, "1.3.0", null, true, null, 0, 0));
            Assert.Equal("not needed: OdinOnDemand is not installed", OdinSpeakers.StateLine(true, false, null, null, true, null, 0, 0));
            Assert.Equal("stepped aside: OdinOnDemand's speaker code changed (an update)", OdinSpeakers.StateLine(true, true, "1.3.1", "OdinOnDemand's speaker code changed (an update)", true, null, 0, 0));
            Assert.Equal("working · OdinOnDemand 1.3.0 · EasyRelocate moves are followed at once · 3 speaker places followed, 1 volume carried",
                OdinSpeakers.StateLine(true, true, "1.3.0", null, true, null, 3, 1));
        }

        [Fact]
        public void Without_EasyRelocate_speakers_still_follow_when_they_load()
        {
            Assert.Equal("working · OdinOnDemand 1.3.0 · EasyRelocate not installed: a moved speaker is followed when it loads · 1 speaker place followed, 0 volumes carried",
                OdinSpeakers.StateLine(true, true, "1.3.0", null, false, null, 1, 0));
            Assert.Equal("working · OdinOnDemand 1.3.0 · EasyRelocate's move code changed (an update): a moved speaker is followed when it loads · 0 speaker places followed, 0 volumes carried",
                OdinSpeakers.StateLine(true, true, "1.3.0", null, true, "EasyRelocate's move code changed (an update)", 0, 0));
        }
    }
}
