using System;
using System.Collections.Generic;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// "Busiest objects": every loaded object around the player read twice, a few seconds apart, and
    /// ranked by what sending its changes takes at most, by object, by type and by the game that runs it.
    /// </summary>
    internal static class BusiestObjects
    {
        public readonly struct Start
        {
            public Start(uint revision, float x, float y, float z)
            {
                Revision = revision;
                X = x;
                Y = y;
                Z = z;
            }

            public uint Revision { get; }

            public float X { get; }

            public float Y { get; }

            public float Z { get; }
        }

        public readonly struct Seen
        {
            public Seen(string id, string type, string owner, uint revision, int sizeBytes, float distance, float x, float y, float z)
            {
                Id = id;
                Type = type;
                Owner = owner;
                Revision = revision;
                SizeBytes = sizeBytes;
                Distance = distance;
                X = x;
                Y = y;
                Z = z;
            }

            public string Id { get; }

            public string Type { get; }

            public string Owner { get; }

            public uint Revision { get; }

            public int SizeBytes { get; }

            public float Distance { get; }

            public float X { get; }

            public float Y { get; }

            public float Z { get; }
        }

        public sealed class Row
        {
            public string Id, Type, Owner;
            public long Changes;
            public double PerSecond, BytesPerSecond;
            public int SizeBytes;
            public float Distance;
            public bool Moved;
        }

        public sealed class Group
        {
            public string Name;
            public int Objects;
            public double BytesPerSecond;
        }

        public sealed class Report
        {
            public List<Row> Objects = new List<Row>();
            public List<Group> Types = new List<Group>();
            public List<Group> Games = new List<Group>();
            public int Watched, Changed, CameOrWent;
            public double Seconds, TotalBytesPerSecond;
        }

        /// <summary>A move of more than this between the two readings counts as moved.</summary>
        private const float MovedMetres = 0.5f;

        /// <summary>
        /// The objects read at the start and at the end of a watch, ranked by what sending their
        /// changes takes at most (<see cref="ChangeRate"/>), then by changes. Only objects read both
        /// times count; the others came into view or went meanwhile.
        /// </summary>
        public static Report Rank(IReadOnlyDictionary<string, Start> start, IReadOnlyList<Seen> end, double seconds)
        {
            var report = new Report { Seconds = seconds };
            var both = new HashSet<string>();
            foreach (var seen in end)
            {
                if (!start.TryGetValue(seen.Id, out var first))
                {
                    report.CameOrWent++;
                    continue;
                }

                both.Add(seen.Id);
                report.Watched++;
                var rate = ChangeRate.Of(first.Revision, seen.Revision, seconds, seen.SizeBytes);
                if (rate.Changes == 0) continue;
                report.Changed++;
                float dx = seen.X - first.X, dy = seen.Y - first.Y, dz = seen.Z - first.Z;
                report.Objects.Add(new Row
                {
                    Id = seen.Id,
                    Type = seen.Type,
                    Owner = seen.Owner,
                    Changes = rate.Changes,
                    PerSecond = rate.PerSecond,
                    BytesPerSecond = rate.BytesPerSecond,
                    SizeBytes = seen.SizeBytes,
                    Distance = seen.Distance,
                    Moved = dx * dx + dy * dy + dz * dz > MovedMetres * MovedMetres,
                });
            }

            report.CameOrWent += start.Keys.Count(id => !both.Contains(id));
            report.Objects = report.Objects.OrderByDescending(o => o.BytesPerSecond).ThenByDescending(o => o.Changes).ThenBy(o => o.Id, StringComparer.Ordinal).ToList();
            report.Types = Sum(report.Objects, o => o.Type);
            report.Games = Sum(report.Objects, o => o.Owner);
            report.TotalBytesPerSecond = report.Objects.Sum(o => o.BytesPerSecond);
            return report;
        }

        private static List<Group> Sum(IEnumerable<Row> rows, Func<Row, string> by) =>
            rows.GroupBy(r => by(r) ?? "")
                .Select(g => new Group { Name = g.Key, Objects = g.Count(), BytesPerSecond = g.Sum(r => r.BytesPerSecond) })
                .OrderByDescending(g => g.BytesPerSecond).ThenBy(g => g.Name, StringComparer.Ordinal)
                .ToList();
    }
}
