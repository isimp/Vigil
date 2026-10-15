using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vigil.Core
{
    /// <summary>What an admin hears about, in the order the card's settings list them.</summary>
    internal enum AlertKind { Drop, Connection, Behind, Memory, LogFlood, BaseSpawns, Shared, Crash }

    internal enum AlertLevel { Info, Warn, Bad }

    /// <summary>How an admin's game tells of one kind: a line at the top left and the count, the count only, or nothing.</summary>
    internal enum AlertShow { OnScreen, Count, Off }

    /// <summary>One thing the server's Vigil saw that its admins should hear about.</summary>
    internal sealed class Alert
    {
        /// <summary>Unique and rising on one server: the time in Unix milliseconds, one more than the last when two share it.</summary>
        public long Id;
        public DateTime Utc;
        public AlertKind Kind;
        public AlertLevel Level;

        /// <summary>The player or place it is about, for kinds that stay quiet a while per subject; may be null.</summary>
        public string Subject;
        public string Text;

        /// <summary>The report behind it, relative to the server's recordings folder; null when there is none.</summary>
        public string File;

        public Alert Copy() => (Alert)MemberwiseClone();
    }

    /// <summary>The words, defaults and quiet times of each kind.</summary>
    internal static class AlertKinds
    {
        public static readonly AlertKind[] All = (AlertKind[])Enum.GetValues(typeof(AlertKind));

        /// <summary>The kind's name in config keys and between the two machines.</summary>
        public static string Key(AlertKind kind) => kind switch
        {
            AlertKind.Drop => "drop", AlertKind.Connection => "connection", AlertKind.Behind => "behind", AlertKind.Memory => "memory",
            AlertKind.LogFlood => "logflood", AlertKind.BaseSpawns => "basespawns", AlertKind.Shared => "shared", _ => "crash",
        };

        /// <summary>The kind's key in the config's Alerts section.</summary>
        public static string ConfigKey(AlertKind kind) => kind switch
        {
            AlertKind.Drop => "Drops", AlertKind.Connection => "Connections", AlertKind.Behind => "FallingBehind", AlertKind.Memory => "Memory",
            AlertKind.LogFlood => "LogFloods", AlertKind.BaseSpawns => "SpawnsInBases", AlertKind.Shared => "SharedReports", _ => "Crashes",
        };

        public static AlertKind? FromKey(string key) => All.Where(k => Key(k) == key).Select(k => (AlertKind?)k).FirstOrDefault();

        /// <summary>The kind's line on the card.</summary>
        public static string Name(AlertKind kind) => kind switch
        {
            AlertKind.Drop => "Players dropped", AlertKind.Connection => "Connections struggling", AlertKind.Behind => "Server falling behind",
            AlertKind.Memory => "Server memory nearly full", AlertKind.LogFlood => "Log floods on the server", AlertKind.BaseSpawns => "Spawns inside bases",
            AlertKind.Shared => "Shared reports", _ => "Server crashes",
        };

        /// <summary>The kind in the line that sums up the quiet ones.</summary>
        public static string Short(AlertKind kind) => kind switch
        {
            AlertKind.Drop => "drops", AlertKind.Connection => "connections", AlertKind.Behind => "falling behind", AlertKind.Memory => "memory",
            AlertKind.LogFlood => "log floods", AlertKind.BaseSpawns => "spawns in bases", AlertKind.Shared => "shared reports", _ => "crashes",
        };

        /// <summary>The kind's row under "What to tell me".</summary>
        public static string Setting(AlertKind kind) => kind switch
        {
            AlertKind.Drop => "A player dropped (no goodbye)", AlertKind.Connection => "A player's connection struggling",
            AlertKind.Behind => "The server falling behind", AlertKind.Memory => "The server's memory nearly full",
            AlertKind.LogFlood => "A mod flooding the server's log", AlertKind.BaseSpawns => "Spawns inside bases",
            AlertKind.Shared => "A player sharing a report", _ => "The server starting after a crash",
        };

        public static AlertShow Default(AlertKind kind) =>
            kind == AlertKind.Drop || kind == AlertKind.Behind || kind == AlertKind.Memory || kind == AlertKind.Crash ? AlertShow.OnScreen : AlertShow.Count;

        public static AlertLevel Level(AlertKind kind) => kind switch
        {
            AlertKind.Drop or AlertKind.Memory or AlertKind.Crash => AlertLevel.Bad,
            AlertKind.Shared => AlertLevel.Info,
            _ => AlertLevel.Warn,
        };

        /// <summary>
        /// How long a kind stays quiet after an alert about the same subject. A server saving its
        /// world stands still for a second every so often, spawns in one base come in waves, and a
        /// silent player is reported every second until they are back or gone; one alert each tells
        /// enough. The other kinds are already reported once until they ease.
        /// </summary>
        public static double QuietSeconds(AlertKind kind) =>
            kind == AlertKind.Behind || kind == AlertKind.Connection ? 600 : kind == AlertKind.BaseSpawns ? 3600 : 0;
    }

    /// <summary>Turns what the server's Vigil sees into alerts.</summary>
    internal static class AlertSources
    {
        /// <summary>A server frame shorter than this is no alert: world saves stand still a few hundred milliseconds by design.</summary>
        public const double LongFrameMs = 1000;

        /// <summary>An alert's words are cut to this, so an answer with a hundred of them stays as small as a report's piece.</summary>
        public const int MaxText = 240;

        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static readonly Regex FrameMs = new Regex(@"^a (\d+) ms frame", RegexOptions.CultureInvariant);
        private static readonly Regex PingOf = new Regex(@"^(.+) has a ping of ", RegexOptions.CultureInvariant);
        private static readonly Regex QueueTo = new Regex(@" waiting to be sent to (.+)$", RegexOptions.CultureInvariant);
        private static readonly Regex SilentFrom = new Regex(@"^nothing received from (.+) for \d+ ms", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// The alert for one of the server's incident reports (<paramref name="who"/> is the player a drop is about);
        /// null for kinds that are none (a hand mark, a self-test, a frame under <paramref name="longFrameMs"/>).
        /// </summary>
        public static Alert FromIncident(string kind, string detail, string file, string who, DateTime utc, double longFrameMs = LongFrameMs)
        {
            detail ??= "";
            switch (kind)
            {
                case "drop":
                    return Make(AlertKind.Drop, detail, who, file, utc);
                case "hitch":
                    var m = FrameMs.Match(detail);
                    if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Integer, Ci, out double ms) || ms < longFrameMs) return null;
                    return Make(AlertKind.Behind, "The server stood still for " + (ms / 1000).ToString("0.0", Ci) + " s", null, file, utc);
                case "slow":
                    return Make(AlertKind.Behind, "The server ran " + Short(detail), null, file, utc);
                case "memory":
                    return Make(AlertKind.Memory, "The server's " + Short(detail), null, file, utc);
                case "log-flood":
                    return Make(AlertKind.LogFlood, "The server's log: " + detail, null, file, utc);
                case "ping":
                    // A player's game words its own ping without a name; only the server's line names one.
                    var p = PingOf.Match(detail);
                    return p.Success ? Make(AlertKind.Connection, detail, p.Groups[1].Value, file, utc) : null;
                case "send-queue":
                    return Make(AlertKind.Connection, Short(detail), Player(QueueTo, Short(detail)), file, utc);
                case "silent":
                    return Make(AlertKind.Connection, Short(detail), Player(SilentFrom, detail), file, utc);
                default:
                    return null;
            }
        }

        /// <summary>One alert per base that spawns from a shared spawn log came inside, naming the creatures; cancelled spawns and those outside bases are none.</summary>
        public static List<Alert> FromSpawns(IEnumerable<SharedSpawn> spawns, Func<long, string> names, DateTime utc)
        {
            var places = new List<KeyValuePair<string, List<string>>>();
            foreach (var s in spawns ?? Enumerable.Empty<SharedSpawn>())
            {
                var r = s?.Record;
                if (r == null || !r.InsideBase || r.Cancelled) continue;
                string place = SpawnShare.Place(r, names);
                int at = places.FindIndex(p => p.Key == place);
                if (at < 0)
                {
                    places.Add(new KeyValuePair<string, List<string>>(place, new List<string>()));
                    at = places.Count - 1;
                }
                places[at].Value.Add(string.IsNullOrEmpty(r.Creature) ? "a creature" : r.Creature);
            }
            return places.Select(p =>
            {
                var creatures = p.Value.Select((c, i) => (c, i)).GroupBy(x => x.c).OrderByDescending(g => g.Count()).ThenBy(g => g.First().i)
                    .Select(g => g.Count() > 1 ? g.Key + " x" + g.Count().ToString(Ci) : g.Key);
                return Make(AlertKind.BaseSpawns, string.Join(", ", creatures) + " spawned inside " + p.Key, p.Key, null, utc);
            }).ToList();
        }

        /// <summary>
        /// The alert for a report a player sent by hand (a lag report, an environment report), from
        /// where the server kept it (shared/&lt;player&gt;/&lt;file&gt;); null for the incident reports a
        /// player's game shares on its own, which arrive every few minutes and are listed among the
        /// server's reports instead.
        /// </summary>
        public static Alert FromShared(string path, DateTime utc)
        {
            string player = ReportCatalog.From(path) ?? "A player";
            string kind = ReportCatalog.KindOf(path?.Substring(path.LastIndexOf('/') + 1)) ?? "report";
            if (kind != "lag report" && kind != "environment") return null;
            string what = kind.EndsWith("report", StringComparison.Ordinal) ? kind : kind + " report";
            string article = "aeiou".IndexOf(what[0]) >= 0 ? "an " : "a ";
            return Make(AlertKind.Shared, player + " shared " + article + what, player, path, utc);
        }

        /// <summary>
        /// The alert at a server's start when its previous run ended without a shutdown: when that
        /// run began and when it last wrote its running note, both on the server's clock.
        /// </summary>
        public static Alert FromCrash(string previousStarted, DateTime? lastSeen, DateTime startedNow, DateTime utc)
        {
            bool known = DateTime.TryParseExact(previousStarted, "yyyy-MM-dd HH:mm:ss", Ci, DateTimeStyles.None, out var began);
            string run = known ? "The server's run from " + AlertView.Clock(began, startedNow) : "The server's last run";
            string seen = lastSeen.HasValue ? " (last sign of life " + AlertView.Clock(lastSeen.Value, startedNow) + ")" : "";
            return Make(AlertKind.Crash, run + " ended without a shutdown" + seen + "; it started again at " + AlertView.Clock(startedNow, startedNow), null, null, utc);
        }

        /// <summary>
        /// Where an alert's report is now: where it was written, or, after a restart moved the
        /// session's files, the newest session folder holding it (<paramref name="runs"/> newest
        /// first); null when it is gone.
        /// </summary>
        public static string ReportPath(string file, Func<string, bool> exists, IEnumerable<string> runs)
        {
            if (string.IsNullOrEmpty(file)) return null;
            if (exists(file)) return file;
            if (file.IndexOf('/') >= 0) return null;
            return runs.Select(r => "Vigil-runs/" + r + "/" + file).FirstOrDefault(exists);
        }

        private static string Player(Regex rule, string text)
        {
            var m = rule.Match(text ?? "");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static Alert Make(AlertKind kind, string text, string subject, string file, DateTime utc) =>
            new Alert { Kind = kind, Level = AlertKinds.Level(kind), Text = Cut(Upper(text)), Subject = subject, File = file, Utc = utc };

        /// <summary>An incident's detail without the limit and how often it is reported, which an alert leaves to the report.</summary>
        private static string Short(string detail)
        {
            foreach (string cut in new[] { "; reported once", " (limit " })
            {
                int at = detail.IndexOf(cut, StringComparison.Ordinal);
                if (at >= 0) detail = detail.Substring(0, at);
            }
            return detail;
        }

        private static string Cut(string s) => s == null || s.Length <= MaxText ? s : s.Substring(0, MaxText - 3) + "...";

        private static string Upper(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    /// <summary>
    /// The server's alerts of the last week, at most <see cref="Kept"/>, kept in a file across
    /// restarts so an admin who joins later hears what happened meanwhile.
    /// </summary>
    internal sealed class AlertBook
    {
        public const int Kept = 500, SentAtOnce = 100;
        public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private readonly List<Alert> _alerts = new List<Alert>();
        private long _lastId;

        /// <summary>Oldest first.</summary>
        public IReadOnlyList<Alert> All => _alerts;

        /// <summary>When the oldest kept alert came; null when there is none.</summary>
        public DateTime? OldestUtc => _alerts.Count > 0 ? _alerts[0].Utc : (DateTime?)null;

        /// <summary>Keeps an alert and gives it its id; false when its kind is still quiet for its subject, or it is older than a week.</summary>
        public bool Add(Alert alert)
        {
            if (alert == null) return false;
            double quiet = AlertKinds.QuietSeconds(alert.Kind);
            if (quiet > 0 && _alerts.Any(a => a.Kind == alert.Kind && a.Subject == alert.Subject && a.Utc <= alert.Utc && (alert.Utc - a.Utc).TotalSeconds < quiet)) return false;
            alert.Id = Math.Max(_lastId + 1, new DateTimeOffset(DateTime.SpecifyKind(alert.Utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds());
            _lastId = alert.Id;
            _alerts.Add(alert);
            Prune(_alerts.Max(a => a.Utc));
            return _alerts.Contains(alert);
        }

        /// <summary>The alerts after <paramref name="id"/>, oldest first; the newest <paramref name="max"/> when there are more.</summary>
        public List<Alert> Since(long id, int max = SentAtOnce)
        {
            var newer = _alerts.Where(a => a.Id > id).ToList();
            return newer.Skip(Math.Max(0, newer.Count - max)).ToList();
        }

        /// <summary>The time an id stands for (ids are Unix milliseconds).</summary>
        public static DateTime TimeOf(long id) => DateTimeOffset.FromUnixTimeMilliseconds(id).UtcDateTime;

        /// <summary>Reads the file's lines back, leaving out lines it cannot read and alerts older than a week.</summary>
        public void Load(IEnumerable<string> lines, DateTime nowUtc)
        {
            _alerts.Clear();
            foreach (var a in (lines ?? Enumerable.Empty<string>()).Select(Parse).Where(a => a != null).OrderBy(a => a.Id)) _alerts.Add(a);
            Prune(nowUtc);
            _lastId = _alerts.Count > 0 ? _alerts[_alerts.Count - 1].Id : 0;
        }

        /// <summary>One alert as a line of the file.</summary>
        public static string Line(Alert a) => string.Join("\t", new[]
        {
            a.Id.ToString(Ci), a.Utc.ToString("o", Ci), AlertKinds.Key(a.Kind), ((int)a.Level).ToString(Ci), Clean(a.Subject), Clean(a.Text), Clean(a.File),
        });

        public static Alert Parse(string line)
        {
            var v = (line ?? "").Split('\t');
            if (v.Length < 7 || !long.TryParse(v[0], NumberStyles.Integer, Ci, out long id) || !DateTime.TryParse(v[1], Ci, DateTimeStyles.RoundtripKind, out var utc)) return null;
            var kind = AlertKinds.FromKey(v[2]);
            if (kind == null || !int.TryParse(v[3], NumberStyles.Integer, Ci, out int level)) return null;
            return new Alert { Id = id, Utc = utc, Kind = kind.Value, Level = (AlertLevel)level, Subject = Text(v[4]), Text = Text(v[5]) ?? "", File = Text(v[6]) };
        }

        /// <summary>Alerts as named values for an answer: "n", then "a0.id", "a0.t" and so on.</summary>
        public static Dictionary<string, object> Encode(IEnumerable<Alert> alerts)
        {
            var d = new Dictionary<string, object>();
            int i = 0;
            foreach (var a in alerts)
            {
                string k = "a" + i.ToString(Ci) + ".";
                d[k + "id"] = a.Id;
                d[k + "t"] = new DateTimeOffset(DateTime.SpecifyKind(a.Utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                d[k + "k"] = AlertKinds.Key(a.Kind);
                d[k + "l"] = (int)a.Level;
                d[k + "s"] = a.Subject;
                d[k + "x"] = a.Text;
                d[k + "f"] = a.File;
                i++;
            }
            d["n"] = i;
            return d;
        }

        /// <summary>The alerts of an answer; kinds a newer server knows and this game does not are left out.</summary>
        public static List<Alert> Decode(IDictionary<string, string> d)
        {
            var list = new List<Alert>();
            for (int i = 0; ; i++)
            {
                string k = "a" + i.ToString(Ci) + ".";
                if (!d.TryGetValue(k + "id", out string idText) || !long.TryParse(idText, NumberStyles.Integer, Ci, out long id)) break;
                var kind = AlertKinds.FromKey(d.TryGetValue(k + "k", out string key) ? key : null);
                if (kind == null) continue;
                double t = KeyValues.Number(d, k + "t"), level = KeyValues.Number(d, k + "l");
                list.Add(new Alert
                {
                    Id = id, Kind = kind.Value, Level = double.IsNaN(level) ? AlertKinds.Level(kind.Value) : (AlertLevel)(int)level,
                    Utc = double.IsNaN(t) ? TimeOf(id) : TimeOf((long)t),
                    Subject = d.TryGetValue(k + "s", out string s) ? s : null, Text = d.TryGetValue(k + "x", out string x) ? x : "", File = d.TryGetValue(k + "f", out string f) ? f : null,
                });
            }
            return list;
        }

        private void Prune(DateTime newestUtc)
        {
            _alerts.RemoveAll(a => a.Utc < newestUtc - KeepFor);
            if (_alerts.Count > Kept) _alerts.RemoveRange(0, _alerts.Count - Kept);
        }

        private static string Clean(string s) => s == null ? "" : s.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");

        private static string Text(string s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
