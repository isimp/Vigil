using System;
using System.Collections.Generic;

namespace Vigil.Core
{
    /// <summary>A place a radio's sound comes from.</summary>
    internal readonly struct SoundPlace
    {
        public readonly float X, Y, Z;

        public SoundPlace(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    /// <summary>A playing radio: its listening distance and the places its sound comes from.</summary>
    internal readonly struct FadeSource
    {
        public readonly float MaxDistance;
        public readonly IReadOnlyList<SoundPlace> Places;

        public FadeSource(float maxDistance, IReadOnlyList<SoundPlace> places)
        {
            MaxDistance = maxDistance;
            Places = places;
        }
    }

    /// <summary>
    /// The game patch "OdinOnDemand fades the game's music by where it plays". OdinOnDemand (1.3.0)
    /// lowers the game's music by the player's distance to the nearest playing radio, measured to the
    /// radio itself, within its listening distance / 1.35, from its lowest fade volume at the radio
    /// to 0 dB at the edge. A radio with linked speakers plays from the speakers' middle (Center) or
    /// from each speaker and itself (Each Speaker), so next to a far speaker the game's music played
    /// at full beside the stream. The patch measures to the nearest place the sound comes from and
    /// keeps OdinOnDemand's curve.
    /// </summary>
    internal static class OdinMusicFade
    {
        /// <summary>OdinOnDemand fades within the listening distance divided by this.</summary>
        public const float RangeDivisor = 1.35f;

        /// <summary>The factor on the game's music: the nearest radio's sound decides, with that radio's range.</summary>
        public static float Gain(float x, float y, float z, IReadOnlyList<FadeSource> playing, float lowestDb)
        {
            float nearest = float.MaxValue, maxDistance = 0f;
            foreach (var radio in playing)
            {
                foreach (var p in radio.Places)
                {
                    float dx = p.X - x, dy = p.Y - y, dz = p.Z - z;
                    float d = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (d >= nearest) continue;
                    nearest = d;
                    maxDistance = radio.MaxDistance;
                }
            }
            float range = maxDistance / RangeDivisor;
            if (nearest == float.MaxValue || range <= 0f || nearest > range) return 1f;
            float t = Math.Max(0f, Math.Min(1f, nearest / range));
            float db = lowestDb + (0f - lowestDb) * t;
            return (float)Math.Pow(10.0, db / 20.0);
        }

        /// <summary>What the patch does on this game, in words.</summary>
        public static string StateLine(bool on, bool oodFound, string oodVersion, string problem, bool fadeOff)
        {
            if (!on) return "off: this game's switch (Patches.OdinOnDemandMusicFade)";
            if (!oodFound) return "not needed: OdinOnDemand is not installed";
            if (problem != null) return "stepped aside: " + problem;
            return "working · OdinOnDemand " + oodVersion + (fadeOff ? " · its Music Fade Type is None, so it leaves the music alone" : "");
        }
    }
}
