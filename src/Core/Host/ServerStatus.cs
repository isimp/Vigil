using System.Collections.Generic;
using System.Globalization;

namespace Vigil.Core
{
    /// <summary>
    /// The dedicated server's state as an admin sees it in the panel: how it runs, what it records,
    /// and each player's connection as the server sees it. It travels as one line of named values
    /// (<see cref="KeyValues"/>), players as "p0.name", "p0.ping" and so on, so a newer server can add
    /// a value without confusing an older game; a value not measured stays NaN.
    /// </summary>
    internal sealed class ServerStatus
    {
        internal sealed class Player
        {
            public string Name;
            public double PingMs = double.NaN, QualityLocal = double.NaN, QualityRemote = double.NaN, QueueKb = double.NaN, UnackedKb = double.NaN,
                OutKbs = double.NaN, InKbs = double.NaN, ObjectsKnown = double.NaN, LongestGapMs = double.NaN;
        }

        public string Version, Recording, LastIncident;
        public double Fps = double.NaN, FrameAvgMs = double.NaN, FrameMaxMs = double.NaN, CpuPct = double.NaN, PrivateMb = double.NaN,
            MemLoadPct = double.NaN, UptimeMinutes = double.NaN, Incidents = double.NaN;

        public List<Player> Players { get; } = new List<Player>();

        public string Encode() => KeyValues.Encode(Values());

        /// <summary>The named values <see cref="Encode"/> writes, for a reply that carries other values too.</summary>
        public List<KeyValuePair<string, object>> Values()
        {
            var v = new List<KeyValuePair<string, object>>
            {
                Pair("v", Version), Pair("fps", Fps), Pair("dt_avg", FrameAvgMs), Pair("dt_max", FrameMaxMs), Pair("cpu", CpuPct),
                Pair("mem_mb", PrivateMb), Pair("mem_load", MemLoadPct), Pair("up_min", UptimeMinutes), Pair("rec", Recording),
                Pair("incidents", Incidents), Pair("last_incident", LastIncident),
            };
            for (int i = 0; i < Players.Count; i++)
            {
                var p = Players[i];
                string k = "p" + i.ToString(CultureInfo.InvariantCulture) + ".";
                v.Add(Pair(k + "name", p.Name ?? "?"));
                v.Add(Pair(k + "ping", p.PingMs));
                v.Add(Pair(k + "ql", p.QualityLocal));
                v.Add(Pair(k + "qr", p.QualityRemote));
                v.Add(Pair(k + "queue", p.QueueKb));
                v.Add(Pair(k + "unacked", p.UnackedKb));
                v.Add(Pair(k + "out", p.OutKbs));
                v.Add(Pair(k + "in", p.InKbs));
                v.Add(Pair(k + "known", p.ObjectsKnown));
                v.Add(Pair(k + "gap", p.LongestGapMs));
            }
            return v;
        }

        public static ServerStatus Decode(string line) => From(KeyValues.Decode(line));

        /// <summary>The status from named values already read, as a reply delivers them.</summary>
        public static ServerStatus From(Dictionary<string, string> d)
        {
            var s = new ServerStatus
            {
                Version = Text(d, "v"), Fps = KeyValues.Number(d, "fps"), FrameAvgMs = KeyValues.Number(d, "dt_avg"), FrameMaxMs = KeyValues.Number(d, "dt_max"),
                CpuPct = KeyValues.Number(d, "cpu"), PrivateMb = KeyValues.Number(d, "mem_mb"), MemLoadPct = KeyValues.Number(d, "mem_load"),
                UptimeMinutes = KeyValues.Number(d, "up_min"), Recording = Text(d, "rec"), Incidents = KeyValues.Number(d, "incidents"),
                LastIncident = Text(d, "last_incident"),
            };
            for (int i = 0; ; i++)
            {
                string k = "p" + i.ToString(CultureInfo.InvariantCulture) + ".";
                string name = Text(d, k + "name");
                if (name == null) break;
                s.Players.Add(new Player
                {
                    Name = name, PingMs = KeyValues.Number(d, k + "ping"), QualityLocal = KeyValues.Number(d, k + "ql"),
                    QualityRemote = KeyValues.Number(d, k + "qr"), QueueKb = KeyValues.Number(d, k + "queue"), UnackedKb = KeyValues.Number(d, k + "unacked"),
                    OutKbs = KeyValues.Number(d, k + "out"), InKbs = KeyValues.Number(d, k + "in"), ObjectsKnown = KeyValues.Number(d, k + "known"),
                    LongestGapMs = KeyValues.Number(d, k + "gap"),
                });
            }
            return s;
        }

        private static KeyValuePair<string, object> Pair(string key, object value) => new KeyValuePair<string, object>(key, value);

        private static string Text(Dictionary<string, string> d, string key) => d.TryGetValue(key, out string s) ? s : null;
    }
}
