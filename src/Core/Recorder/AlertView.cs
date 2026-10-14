using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Vigil.Core
{
    /// <summary>
    /// The admin's Alerts card: a verdict, one line per kind with its count and last time, the
    /// kinds without alerts summed up in one line, and since when. Alerts up to the one cleared
    /// with Seen are left out, and kinds switched off neither show nor count. The new ones (after
    /// the newest the card showed when it was last open) are the HUD line's count.
    /// </summary>
    internal sealed class AlertView
    {
        internal sealed class Group
        {
            public AlertKind Kind;
            public string Name, Last;
            public int Count;
            public AlertLevel Level;

            /// <summary>Newest first.</summary>
            public List<Alert> Entries = new List<Alert>();
        }

        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        /// <summary>True when there is nothing to report.</summary>
        public bool Calm;
        public string Verdict;
        public AlertLevel VerdictLevel;

        /// <summary>Worst level first, then the most recent.</summary>
        public List<Group> Groups = new List<Group>();

        /// <summary>The kinds without alerts in one line; null when every kind shown has some.</summary>
        public string Quiet;

        /// <summary>The footer: since when the card counts, and how long the server keeps alerts.</summary>
        public string Since;
        public int New;
        public AlertLevel NewLevel;

        /// <param name="alerts">The alerts this game has from the server.</param>
        /// <param name="clearedUpTo">The newest alert cleared with Seen; 0 for none.</param>
        /// <param name="openedUpTo">The newest alert the card showed when it was last open; 0 for none.</param>
        /// <param name="keptFromUtc">The oldest alert the server keeps; null when it keeps none.</param>
        /// <param name="show">How this game tells of each kind; kinds switched off are left out.</param>
        /// <param name="nowUtc">The time now.</param>
        /// <param name="offset">This machine's offset from UTC, for the times shown.</param>
        public static AlertView Build(IEnumerable<Alert> alerts, long clearedUpTo, long openedUpTo, DateTime? keptFromUtc, Func<AlertKind, AlertShow> show,
            DateTime nowUtc, TimeSpan offset)
        {
            var v = new AlertView();
            var shown = (alerts ?? Enumerable.Empty<Alert>()).Where(a => a.Id > clearedUpTo && show(a.Kind) != AlertShow.Off).OrderBy(a => a.Id).ToList();
            var fresh = shown.Where(a => a.Id > openedUpTo).ToList();
            v.New = fresh.Count;
            v.NewLevel = fresh.Count > 0 ? fresh.Max(a => a.Level) : AlertLevel.Info;

            v.Groups = shown.GroupBy(a => a.Kind).Select(g => new Group
            {
                Kind = g.Key, Name = AlertKinds.Name(g.Key), Count = g.Count(), Level = g.Max(a => a.Level),
                Entries = g.OrderByDescending(a => a.Id).ToList(), Last = When(g.Max(a => a.Utc), nowUtc, offset),
            }).OrderByDescending(g => g.Level).ThenByDescending(g => g.Entries[0].Id).ToList();

            var quiet = AlertKinds.All.Where(k => show(k) != AlertShow.Off && v.Groups.All(g => g.Kind != k)).Select(AlertKinds.Short).ToList();
            v.Quiet = quiet.Count == 0 ? null : Upper(Join(quiet)) + ": none";

            DateTime? from = clearedUpTo > 0 ? AlertBook.TimeOf(clearedUpTo) : keptFromUtc;
            if (from.HasValue && keptFromUtc.HasValue && keptFromUtc.Value > from.Value) from = keptFromUtc;
            string since = from.HasValue ? When(from.Value, nowUtc, offset) : null;
            v.Since = (since != null ? "since " + since + ", " : "") + "7 days kept";

            if (v.Groups.Count == 0)
            {
                v.Calm = true;
                v.Verdict = since != null ? "Nothing since " + since : "Nothing to report";
                return v;
            }

            var top = v.Groups[0];
            v.VerdictLevel = top.Level;
            v.Verdict = top.Count == 1 ? top.Entries[0].Text
                : top.Name + ", " + top.Count.ToString(Ci) + " since " + When(top.Entries[top.Count - 1].Utc, nowUtc, offset) + ". Last: " + top.Entries[0].Text;
            return v;
        }

        /// <summary>A time as this machine's clock shows it: hours and minutes today, with the day's short name before.</summary>
        public static string When(DateTime utc, DateTime nowUtc, TimeSpan offset) => Clock(utc + offset, nowUtc + offset);

        /// <summary>A clock time, with the day's short name when it is not on <paramref name="today"/>'s date.</summary>
        public static string Clock(DateTime time, DateTime today) =>
            time.Date == today.Date ? time.ToString("HH:mm", Ci) : time.ToString("ddd HH:mm", Ci);

        private static string Join(List<string> parts) =>
            parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[parts.Count - 1];

        private static string Upper(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    /// <summary>
    /// The lines an admin's game shows at the top left as alerts arrive: one per alert of a kind set
    /// on screen, at most <see cref="MaxLines"/> at once; the alerts waiting at a join as one line.
    /// </summary>
    internal static class AlertNotice
    {
        public const int MaxLines = 3;
        private const string Prefix = "Vigil: ";

        /// <param name="arrived">The alerts new to this game, oldest first.</param>
        /// <param name="firstAnswer">True for the server's first answer after joining, which holds what happened while away.</param>
        /// <param name="show">How this game tells of each kind; only the kinds set on screen get a line.</param>
        public static List<string> Lines(IReadOnlyList<Alert> arrived, bool firstAnswer, Func<AlertKind, AlertShow> show)
        {
            var loud = (arrived ?? Array.Empty<Alert>()).Where(a => show(a.Kind) == AlertShow.OnScreen).ToList();
            var lines = new List<string>();
            if (loud.Count == 0) return lines;
            if (firstAnswer)
            {
                lines.Add(loud.Count == 1 ? Prefix + "while you were away: " + loud[0].Text
                    : Prefix + loud.Count.ToString(CultureInfo.InvariantCulture) + " alerts while you were away; the newest: " + loud[loud.Count - 1].Text);
                return lines;
            }
            if (loud.Count <= MaxLines) return loud.Select(a => Prefix + a.Text).ToList();
            lines.AddRange(loud.Take(MaxLines - 1).Select(a => Prefix + a.Text));
            lines.Add(Prefix + "and " + (loud.Count - (MaxLines - 1)).ToString(CultureInfo.InvariantCulture) + " more alerts");
            return lines;
        }
    }

    /// <summary>
    /// What an admin's game remembers per server (by its world's name): the newest alert the card
    /// showed when it was last open, and the newest one cleared with Seen. One line per server.
    /// </summary>
    internal sealed class AlertMarks
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private readonly Dictionary<string, long[]> _marks = new Dictionary<string, long[]>(StringComparer.Ordinal);

        public long Opened(string server) => Get(server)[0];

        public long Cleared(string server) => Get(server)[1];

        public void SetOpened(string server, long id) => Set(server, 0, id);

        public void SetCleared(string server, long id) => Set(server, 1, id);

        public string Format()
        {
            var sb = new StringBuilder();
            foreach (var kv in _marks) sb.Append(kv.Key).Append('\t').Append(kv.Value[0].ToString(Ci)).Append('\t').Append(kv.Value[1].ToString(Ci)).Append('\n');
            return sb.ToString();
        }

        public static AlertMarks Parse(string text)
        {
            var marks = new AlertMarks();
            foreach (var line in (text ?? "").Split('\n'))
            {
                var v = line.TrimEnd('\r').Split('\t');
                if (v.Length == 3 && v[0].Length > 0 && long.TryParse(v[1], NumberStyles.Integer, Ci, out long opened) && long.TryParse(v[2], NumberStyles.Integer, Ci, out long cleared))
                    marks._marks[v[0]] = new[] { opened, cleared };
            }
            return marks;
        }

        private long[] Get(string server) => server != null && _marks.TryGetValue(server, out var m) ? m : new long[2];

        private void Set(string server, int which, long id)
        {
            if (server == null) return;
            if (!_marks.TryGetValue(server, out var m)) _marks[server] = m = new long[2];
            m[which] = id;
        }
    }
}
