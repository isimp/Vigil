using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>Reads a Vigil timeline (tab-separated, header first) back for reports.</summary>
    public sealed class TimelineReader
    {
        public sealed class Row
        {
            private readonly Dictionary<string, int> _index;
            private readonly string[] _cells;

            internal Row(Dictionary<string, int> index, string[] cells) { _index = index; _cells = cells; }

            /// <summary>The cell's text, or null when the column or cell is missing or empty.</summary>
            public string this[string column] =>
                _index.TryGetValue(column, out int i) && i < _cells.Length && _cells[i].Length > 0 ? _cells[i] : null;

            public double? Number(string column) =>
                double.TryParse(this[column], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : (double?)null;
        }

        public sealed class ColumnStats { public int Count; public double Max; public double Mean; }

        private readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.Ordinal);

        public List<Row> Rows { get; } = new List<Row>();
        public IReadOnlyCollection<string> Columns => _index.Keys;

        public static TimelineReader Parse(string tsv)
        {
            var r = new TimelineReader();
            var lines = (tsv ?? "").Split('\n');
            if (lines.Length == 0) return r;
            var header = lines[0].TrimEnd('\r').Split('\t');
            for (int i = 0; i < header.Length; i++) if (!r._index.ContainsKey(header[i])) r._index[header[i]] = i;
            for (int l = 1; l < lines.Length; l++)
            {
                string line = lines[l].TrimEnd('\r');
                if (line.Length == 0) continue;
                r.Rows.Add(new Row(r._index, line.Split('\t')));
            }
            return r;
        }

        /// <summary>The <paramref name="n"/> rows with the largest value in <paramref name="column"/>, optionally only where another column has a given value.</summary>
        public List<Row> Worst(string column, int n, (string column, string value)? onlyWhere = null)
        {
            if (!_index.ContainsKey(column)) return new List<Row>();
            return Rows.Where(r => r.Number(column).HasValue && (onlyWhere == null || r[onlyWhere.Value.column] == onlyWhere.Value.value))
                .OrderByDescending(r => r.Number(column).Value).Take(n).ToList();
        }

        public ColumnStats Stats(string column)
        {
            var values = Rows.Select(r => r.Number(column)).Where(v => v.HasValue).Select(v => v.Value).ToList();
            return new ColumnStats { Count = values.Count, Max = values.Count > 0 ? values.Max() : 0, Mean = values.Count > 0 ? values.Average() : 0 };
        }
    }
}
