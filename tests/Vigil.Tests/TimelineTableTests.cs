using System;
using System.Globalization;
using System.Threading;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The timeline is one row per second in a tab-separated file that opens straight in a
    /// spreadsheet. Its columns must stay where the header says, whatever is in a value.
    /// </summary>
    public sealed class TimelineTableTests
    {
        private static readonly string[] Cols = { "time", "fps", "worst_ms", "biome", "note" };

        [Fact]
        public void TheHeaderNamesEveryColumnInOrder()
        {
            var t = new TimelineTable(Cols);

            Assert.Equal("time\tfps\tworst_ms\tbiome\tnote", t.Header());
        }

        [Fact]
        public void ValuesLandUnderTheirOwnColumnWhateverOrderTheyAreSetIn()
        {
            var t = new TimelineTable(Cols);
            var row = t.NewRow();
            row.Set("note", "x"); row.Set("time", "18:00:01"); row.Set("fps", 44);

            Assert.Equal("18:00:01\t44\t\t\tx", t.Format(row));
        }

        [Fact]
        public void NumbersUseAPointWhateverTheSystemLanguage()
        {
            var before = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var t = new TimelineTable(Cols);
                var row = t.NewRow();
                row.Set("fps", 44.25); row.Set("worst_ms", 1234.5f);

                string line = t.Format(row);

                Assert.Contains("44.25", line);
                Assert.Contains("1234.5", line);
                Assert.DoesNotContain(",", line);
            }
            finally { Thread.CurrentThread.CurrentCulture = before; }
        }

        [Fact]
        public void TextWithTabsOrLineBreaksCannotShiftTheColumns()
        {
            var t = new TimelineTable(Cols);
            var row = t.NewRow();
            row.Set("biome", "Black\tForest"); row.Set("note", "two\r\nlines");

            string line = t.Format(row);

            Assert.Equal(Cols.Length, line.Split('\t').Length);
            Assert.DoesNotContain("\n", line);
        }

        [Fact]
        public void YesNoValuesReadAsOneAndZero()
        {
            var t = new TimelineTable(new[] { "a", "b" });
            var row = t.NewRow();
            row.Set("a", true); row.Set("b", false);

            Assert.Equal("1\t0", t.Format(row));
        }

        [Fact]
        public void LongDecimalsAreRoundedSoTheFileStaysReadable()
        {
            var t = new TimelineTable(new[] { "a" });
            var row = t.NewRow();
            row.Set("a", 1.0 / 3.0);

            Assert.Equal("0.33", t.Format(row));
        }

        [Fact]
        public void AMisspelledColumnIsRefusedInsteadOfSilentlyLost()
        {
            var t = new TimelineTable(Cols);
            var row = t.NewRow();

            Assert.Throws<ArgumentException>(() => row.Set("fsp", 1));
        }

        [Fact]
        public void AColumnNamedTwiceIsRefused()
        {
            Assert.Throws<ArgumentException>(() => new TimelineTable(new[] { "a", "b", "a" }));
        }

        [Fact]
        public void ARowCanBeReadBackByColumnName()
        {
            var t = new TimelineTable(Cols);
            var row = t.NewRow();
            row.Set("fps", 30);

            Assert.Equal(30, row.Get("fps"));
            Assert.Null(row.Get("note"));
        }
    }
}
