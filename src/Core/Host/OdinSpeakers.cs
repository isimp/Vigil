using System.Collections.Generic;

namespace Vigil.Core
{
    /// <summary>A speaker's id and place: one of a radio's saved links, or a speaker loaded in this game.</summary>
    internal readonly struct SpeakerSpot
    {
        public readonly string Guid;
        public readonly float X, Y, Z;

        public SpeakerSpot(string guid, float x, float y, float z)
        {
            Guid = guid;
            X = x;
            Y = y;
            Z = z;
        }
    }

    /// <summary>
    /// The game patch "OdinOnDemand plays from where its speakers stand". OdinOnDemand (1.3.0) saves
    /// each linked speaker's id and place in the radio's data when it is linked and plays from those
    /// saved places (their middle, or each one), working them out only when a link changes or the
    /// radio's data is read again. EasyRelocate moves a piece without making a new one, so the link
    /// still finds the speaker by its id, and the radio went on playing from its old place. The patch
    /// takes each linked speaker's place from the speaker while it is loaded, works the radio's
    /// places out again after a move and when a speaker loads, and saves the new place in the radio's
    /// data on the game that holds that data. A player's own volume for a radio is kept under the
    /// radio's place, so a moved radio lost it; the patch moves it to the new place.
    /// </summary>
    internal static class OdinSpeakers
    {
        /// <summary>A speaker that stands further than this from its saved place has moved.</summary>
        public const float MovedMetres = 0.01f;

        public static bool Moved(SpeakerSpot saved, SpeakerSpot now)
        {
            float dx = now.X - saved.X, dy = now.Y - saved.Y, dz = now.Z - saved.Z;
            return dx * dx + dy * dy + dz * dz > MovedMetres * MovedMetres;
        }

        /// <summary>
        /// The links as the radio should play them: each linked speaker loaded here, found by its id,
        /// at its place now; one not loaded, or whose id two loaded speakers share, at its saved place.
        /// Null when no speaker has moved.
        /// </summary>
        public static SpeakerSpot[] Follow(IReadOnlyList<SpeakerSpot> links, IReadOnlyList<SpeakerSpot> loaded)
        {
            SpeakerSpot[] now = null;
            for (int i = 0; i < links.Count; i++)
            {
                if (!Only(loaded, links[i].Guid, out var speaker) || !Moved(links[i], speaker)) continue;
                if (now == null)
                {
                    now = new SpeakerSpot[links.Count];
                    for (int j = 0; j < links.Count; j++) now[j] = links[j];
                }
                now[i] = new SpeakerSpot(links[i].Guid, speaker.X, speaker.Y, speaker.Z);
            }
            return now;
        }

        /// <summary>Whether a radio with these links plays this speaker from somewhere else than where it stands.</summary>
        public static bool Stale(IReadOnlyList<SpeakerSpot> links, SpeakerSpot speaker)
        {
            if (string.IsNullOrEmpty(speaker.Guid)) return false;
            foreach (var link in links)
                if (link.Guid == speaker.Guid && Moved(link, speaker)) return true;
            return false;
        }

        /// <summary>Whether a player's own volume, saved under the radio's key before a move, goes to its key after it.</summary>
        public static bool CarryVolume(string oldKey, string newKey, bool oldSaved) =>
            oldSaved && !string.IsNullOrEmpty(oldKey) && !string.IsNullOrEmpty(newKey) && oldKey != newKey;

        /// <summary>What the patch does on this game, in words.</summary>
        public static string StateLine(bool on, bool oodFound, string oodVersion, string problem, bool relocateFound, string relocateProblem, long followed, long carried)
        {
            if (!on) return "off: this game's switch (Patches.OdinOnDemandSpeakersFollow)";
            if (!oodFound) return "not needed: OdinOnDemand is not installed";
            if (problem != null) return "stepped aside: " + problem;
            string moves = !relocateFound ? "EasyRelocate not installed: a moved speaker is followed when it loads"
                : relocateProblem != null ? relocateProblem + ": a moved speaker is followed when it loads"
                : "EasyRelocate moves are followed at once";
            return "working · OdinOnDemand " + oodVersion + " · " + moves + " · " +
                Count(followed, "speaker place") + " followed, " + Count(carried, "volume") + " carried";
        }

        /// <summary>The one loaded speaker with this id; false for no id, none, or several.</summary>
        private static bool Only(IReadOnlyList<SpeakerSpot> loaded, string guid, out SpeakerSpot found)
        {
            found = default;
            if (string.IsNullOrEmpty(guid)) return false;
            int seen = 0;
            foreach (var s in loaded)
            {
                if (s.Guid != guid) continue;
                found = s;
                seen++;
            }
            return seen == 1;
        }

        private static string Count(long n, string what) => n + " " + what + (n == 1 ? "" : "s");
    }
}
