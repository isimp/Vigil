using System;

namespace Vigil.Core
{
    public enum RecordingMode { On, Off, Auto }

    /// <summary>
    /// Whether Vigil records right now. On records all the time, Off never, Auto only while there
    /// is something to record: on a player's machine while in a world, on a dedicated server while
    /// players are connected, for a while after the last one left, and during the server's start.
    /// A deep capture, a load test or a player simulation always records.
    /// </summary>
    public static class RecordingPolicy
    {
        public const double ServerStartSeconds = 300;
        public const double ServerLingerSeconds = 120;

        public static bool Records(RecordingMode mode, bool server, bool inWorld, int players, double secondsSinceLastPlayer, double secondsSinceStart, bool busy)
        {
            if (busy) return true;
            switch (mode)
            {
                case RecordingMode.On: return true;
                case RecordingMode.Off: return false;
            }
            if (!server) return inWorld;
            return players > 0 || secondsSinceStart < ServerStartSeconds || secondsSinceLastPlayer < ServerLingerSeconds;
        }

        public static bool TryParse(string word, out RecordingMode mode)
        {
            switch ((word ?? "").Trim().ToLowerInvariant())
            {
                case "on": mode = RecordingMode.On; return true;
                case "off": mode = RecordingMode.Off; return true;
                case "auto": mode = RecordingMode.Auto; return true;
                default: mode = RecordingMode.Auto; return false;
            }
        }

        /// <summary>The recording line of "vigil status", which the server also sends in its status: the switch, whether it records, and why.</summary>
        public static string Status(RecordingMode mode, bool recording, string why) =>
            "Recording: " + mode.ToString().ToLowerInvariant() + ", " + (recording ? "recording" : "not recording") + " (" + why + ")";

        /// <summary>The switch named in a line <see cref="Status"/> wrote, so the panel can light it; false for a line that names none.</summary>
        public static bool TryReadStatus(string status, out RecordingMode mode)
        {
            const string lead = "Recording: ";
            mode = RecordingMode.Auto;
            if (status == null || !status.StartsWith(lead, StringComparison.Ordinal)) return false;
            int comma = status.IndexOf(',', lead.Length);
            return TryParse(comma < 0 ? status.Substring(lead.Length) : status.Substring(lead.Length, comma - lead.Length), out mode);
        }
    }
}
