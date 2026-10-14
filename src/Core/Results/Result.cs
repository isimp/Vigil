using System;
using System.Collections.Generic;

namespace Vigil.Core
{
    /// <summary>How a result, or a part of it, reads: nothing in particular, fine, worth a look, wrong.</summary>
    internal enum ResultTone
    {
        Quiet,
        Good,
        Caution,
        Bad,
    }

    /// <summary>
    /// What a tool found, as the panel shows it under the tool: a title and when, a few words with
    /// a tone for its header, the file it was saved as, and its parts in order. Each tool builds
    /// one from the same numbers it writes to its file, so the panel and the file agree.
    /// </summary>
    internal sealed class Result
    {
        public Result(string title, DateTime at)
        {
            Title = title;
            At = at;
        }

        public string Title { get; }

        public DateTime At { get; }

        /// <summary>A few words for the result's header, such as "upload fell behind"; null for none.</summary>
        public string Badge { get; set; }

        public ResultTone BadgeTone { get; set; }

        /// <summary>One line for a list of results, such as what triggered an incident; null for none.</summary>
        public string Summary { get; set; }

        /// <summary>The file's name, as the panel says it was saved; null when nothing was written.</summary>
        public string File { get; set; }

        /// <summary>The file's full path, for opening it.</summary>
        public string Path { get; set; }

        public List<ResultBlock> Blocks { get; } = new List<ResultBlock>();

        public T Add<T>(T block)
            where T : ResultBlock
        {
            Blocks.Add(block);
            return block;
        }
    }

    /// <summary>One part of a result, with an optional heading.</summary>
    internal abstract class ResultBlock
    {
        protected ResultBlock(string title)
        {
            Title = title;
        }

        public string Title { get; }
    }

    /// <summary>A finding in one sentence, with a mark for its tone.</summary>
    internal sealed class VerdictBlock : ResultBlock
    {
        public VerdictBlock(ResultTone tone, string text)
            : base(null)
        {
            Tone = tone;
            Text = text;
        }

        public ResultTone Tone { get; }

        public string Text { get; }
    }

    /// <summary>A few headline numbers side by side, each with its label.</summary>
    internal sealed class TilesBlock : ResultBlock
    {
        public TilesBlock()
            : base(null)
        {
        }

        public List<KeyValuePair<string, string>> Tiles { get; } = new List<KeyValuePair<string, string>>();

        public TilesBlock Add(string label, string value)
        {
            Tiles.Add(new KeyValuePair<string, string>(label, value));
            return this;
        }
    }

    /// <summary>
    /// Bars, one a row, longest first: a label, a bar to scale with the longest, and the value as
    /// words. A bar can be split in two parts (the game's own code and the mods', say); the block
    /// then names both parts.
    /// </summary>
    internal sealed class BarsBlock : ResultBlock
    {
        public BarsBlock(string title, string partName = null, string restName = null)
            : base(title)
        {
            PartName = partName;
            RestName = restName;
        }

        /// <summary>What the first part of a split bar stands for; null when the bars are not split.</summary>
        public string PartName { get; }

        public string RestName { get; }

        /// <summary>A line above the bars, such as a share left out of them so the rest compare; null for none.</summary>
        public string Note { get; set; }

        public List<Bar> Bars { get; } = new List<Bar>();

        public BarsBlock Add(string label, double value, string text, double part = double.NaN)
        {
            Bars.Add(new Bar(label, value, text, part));
            return this;
        }

        internal sealed class Bar
        {
            public Bar(string label, double value, string text, double part)
            {
                Label = label;
                Value = value;
                Text = text;
                Part = part;
            }

            public string Label { get; }

            public double Value { get; }

            public string Text { get; }

            /// <summary>The first part of the value, for a split bar; NaN otherwise.</summary>
            public double Part { get; }
        }
    }

    /// <summary>
    /// Columns side by side, one per step, to scale with <see cref="Max"/>, each in its tone, with
    /// a dashed line at <see cref="Threshold"/> named by <see cref="ThresholdLabel"/>.
    /// </summary>
    internal sealed class ColumnsBlock : ResultBlock
    {
        public ColumnsBlock(string title, double threshold, string thresholdLabel, double max)
            : base(title)
        {
            Threshold = threshold;
            ThresholdLabel = thresholdLabel;
            Max = max;
        }

        public double Threshold { get; }

        public string ThresholdLabel { get; }

        public double Max { get; }

        public List<Column> Columns { get; } = new List<Column>();

        /// <summary>Words under the last column's label, such as the unit.</summary>
        public string Unit { get; set; }

        public ColumnsBlock Add(string label, double value, ResultTone tone, string hover)
        {
            Columns.Add(new Column(label, value, tone, hover));
            return this;
        }

        internal sealed class Column
        {
            public Column(string label, double value, ResultTone tone, string hover)
            {
                Label = label;
                Value = value;
                Tone = tone;
                Hover = hover;
            }

            public string Label { get; }

            public double Value { get; }

            public ResultTone Tone { get; }

            /// <summary>What the column says when the mouse is over it.</summary>
            public string Hover { get; }
        }
    }

    /// <summary>Rows that each show a span, from a quiet value to a busy one, on one scale, with an optional mark (a budget).</summary>
    internal sealed class RangesBlock : ResultBlock
    {
        public RangesBlock(string title, double max, double mark, string markLabel)
            : base(title)
        {
            Max = max;
            Mark = mark;
            MarkLabel = markLabel;
        }

        public double Max { get; }

        /// <summary>Where the mark goes; NaN for none.</summary>
        public double Mark { get; }

        public string MarkLabel { get; }

        public List<Range> Ranges { get; } = new List<Range>();

        public RangesBlock Add(string label, double from, double to, string text)
        {
            Ranges.Add(new Range(label, from, to, text));
            return this;
        }

        internal sealed class Range
        {
            public Range(string label, double from, double to, string text)
            {
                Label = label;
                From = from;
                To = to;
                Text = text;
            }

            public string Label { get; }

            public double From { get; }

            public double To { get; }

            public string Text { get; }
        }
    }

    /// <summary>
    /// A value per second as thin columns, oldest on the left, amber from <see cref="Warn"/> and
    /// red from <see cref="Bad"/>, with words for the left and right ends.
    /// </summary>
    internal sealed class StripBlock : ResultBlock
    {
        public StripBlock(string title, IReadOnlyList<double> values, double warn, double bad, string unit, string left, string right)
            : base(title)
        {
            Values = values;
            Warn = warn;
            Bad = bad;
            Unit = unit;
            Left = left;
            Right = right;
        }

        public IReadOnlyList<double> Values { get; }

        public double Warn { get; }

        public double Bad { get; }

        public string Unit { get; }

        public string Left { get; }

        public string Right { get; }
    }

    /// <summary>Words: a reason, an explanation.</summary>
    internal sealed class TextBlock : ResultBlock
    {
        public TextBlock(string title, string text)
            : base(title)
        {
            Text = text;
        }

        public string Text { get; }
    }

    /// <summary>A table, folded until asked for: the detail behind the rest.</summary>
    internal sealed class TableBlock : ResultBlock
    {
        public TableBlock(string title, IReadOnlyList<string> headers, IReadOnlyList<bool> right)
            : base(title)
        {
            Headers = headers;
            Right = right;
        }

        public IReadOnlyList<string> Headers { get; }

        /// <summary>For each column, whether it lines up on the right, as numbers do.</summary>
        public IReadOnlyList<bool> Right { get; }

        public List<string[]> Rows { get; } = new List<string[]>();
    }
}
