using System;

namespace Vigil.Core
{
    /// <summary>
    /// A radio's play position as the game's main thread read it from its Unity sound source, with
    /// the clock reading of that moment and what is needed to carry it forward.
    /// </summary>
    internal sealed class PlayNote
    {
        public readonly int Position;
        public readonly long Ticks;
        public readonly bool Playing;
        public readonly float Pitch;
        public readonly int Frequency;
        public readonly int ClipFrames;

        public PlayNote(int position, long ticks, bool playing, float pitch, int frequency, int clipFrames)
        {
            Position = position;
            Ticks = ticks;
            Playing = playing;
            Pitch = pitch;
            Frequency = frequency;
            ClipFrames = clipFrames;
        }
    }

    /// <summary>
    /// The game patch "OdinOnDemand's stream cannot crash the game". OdinOnDemand (1.3.0) plays a
    /// stream through a looping Unity clip whose samples Unity asks for on its sound thread; the
    /// answer (<c>PcmRingBuffer.Read</c>) reads the sound source's <c>timeSamples</c> there to know
    /// how far ahead of the heard sound Unity reads. Unity's sound sources belong to the main
    /// thread, which stops them and swaps their clips, and the game crashed inside that read. With
    /// the patch, the main thread notes the position each frame and the sound thread carries the
    /// note forward by the time passed.
    /// </summary>
    internal static class OdinAudioClock
    {
        /// <summary>
        /// Where the clip is heard now: the noted frame, moved on by the time since the note at the
        /// clip's rate and the source's pitch while it plays, round the looping clip. Without a note
        /// the stream is new and plays from its start.
        /// </summary>
        public static int Position(PlayNote note, long nowTicks, long ticksPerSecond)
        {
            if (note == null) return 0;
            if (!note.Playing || note.ClipFrames <= 0 || note.Frequency <= 0 || ticksPerSecond <= 0) return note.Position;
            long elapsed = nowTicks - note.Ticks;
            if (elapsed <= 0) return note.Position;
            double moved = (double)elapsed / ticksPerSecond * note.Frequency * Math.Max(0f, note.Pitch);
            return (int)((note.Position + moved) % note.ClipFrames);
        }

        /// <summary>What the patch does on this game, in words.</summary>
        public static string StateLine(bool on, bool oodFound, string oodVersion, string problem)
        {
            if (!on) return "off: this game's switch (Patches.OdinOnDemandSafeStream)";
            if (!oodFound) return "not needed: OdinOnDemand is not installed";
            if (problem != null) return "stepped aside: " + problem;
            return "working · OdinOnDemand " + oodVersion + " · its stream reads no sound source off the main thread";
        }
    }
}
