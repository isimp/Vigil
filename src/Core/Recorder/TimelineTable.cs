using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Vigil.Core
{
    /// <summary>
    /// A tab-separated table with a fixed set of named columns: one header line, then one line
    /// per row. Numbers are written with a point and at most two decimals; text loses tabs and
    /// line breaks so a value can never shift the columns.
    /// </summary>
    public sealed class TimelineTable
    {
        private readonly string[] _columns;
        private readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.Ordinal);

        public sealed class Row
        {
            private readonly TimelineTable _table;
            internal readonly object[] Values;

            internal Row(TimelineTable table)
            {
                _table = table;
                Values = new object[table._columns.Length];
            }

            public void Set(string column, object value) => Values[_table.IndexOf(column)] = value;
            public object Get(string column) => Values[_table.IndexOf(column)];
        }

        public TimelineTable(IEnumerable<string> columns)
        {
            _columns = columns.ToArray();
            for (int i = 0; i < _columns.Length; i++)
            {
                if (_index.ContainsKey(_columns[i])) throw new ArgumentException("column named twice: " + _columns[i]);
                _index[_columns[i]] = i;
            }
        }

        public IReadOnlyList<string> Columns => _columns;

        public string Header() => string.Join("\t", _columns);

        public Row NewRow() => new Row(this);

        public string Format(Row row)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < row.Values.Length; i++)
            {
                if (i > 0) sb.Append('\t');
                sb.Append(Text(row.Values[i]));
            }
            return sb.ToString();
        }

        private int IndexOf(string column)
        {
            if (column == null || !_index.TryGetValue(column, out int i)) throw new ArgumentException("no such column: " + column);
            return i;
        }

        private static string Text(object v)
        {
            var ci = CultureInfo.InvariantCulture;
            switch (v)
            {
                case null: return "";
                case bool b: return b ? "1" : "0";
                case float f: return f.ToString("0.##", ci);
                case double d: return d.ToString("0.##", ci);
                case decimal m: return m.ToString("0.##", ci);
                case IFormattable fm: return fm.ToString(null, ci);
                default:
                    var s = v.ToString();
                    return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            }
        }
    }
}
