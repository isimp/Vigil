using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// An admin lists the server's reports in the panel (this session's and the last ones before
    /// the server restarted), each named by its kind and what it says first, and fetches one in
    /// pieces over the admin channel into their own recordings folder. The server hands out its own
    /// report files and nothing else.
    /// </summary>
    public sealed class ReportCatalogTests
    {
        [Fact]
        public void Each_report_is_named_by_its_kind_and_other_files_are_not_reports()
        {
            Assert.Equal("drop", ReportCatalog.KindOf("Vigil-incident-211504-drop.txt"));
            Assert.Equal("hitch", ReportCatalog.KindOf("Vigil-incident-163839-hitch.txt"));
            Assert.Equal("lag report", ReportCatalog.KindOf("Vigil-incident-162001-manual.txt"));
            Assert.Equal("load test", ReportCatalog.KindOf("Vigil-load-163001.txt"));
            Assert.Equal("deep capture", ReportCatalog.KindOf("Vigil-deep-163410.txt"));
            Assert.Equal("self-test", ReportCatalog.KindOf("Vigil-selftest.txt"));
            Assert.Equal("summary", ReportCatalog.KindOf("Vigil-summary.txt"));
            Assert.Null(ReportCatalog.KindOf("Vigil-timeline.tsv"));
            Assert.Null(ReportCatalog.KindOf("Vigil-load-163001.tsv"));
            Assert.Null(ReportCatalog.KindOf("valheim.db"));
        }

        [Fact]
        public void The_server_hands_out_its_own_reports_and_nothing_else()
        {
            Assert.True(ReportCatalog.Servable("Vigil-incident-211504-drop.txt"));
            Assert.True(ReportCatalog.Servable("Vigil-runs/20261013-220715/Vigil-load-163001.txt"));
            Assert.False(ReportCatalog.Servable("../worlds_local/world.db"));
            Assert.False(ReportCatalog.Servable("Vigil-runs/../../config/x.txt"));
            Assert.False(ReportCatalog.Servable("Vigil-runs/../Vigil-summary.txt"));
            Assert.False(ReportCatalog.Servable("Vigil-runs/C:/Vigil-summary.txt"));
            Assert.False(ReportCatalog.Servable("C:/Windows/win.ini"));
            Assert.False(ReportCatalog.Servable("/etc/passwd"));
            Assert.False(ReportCatalog.Servable("Vigil-runs/a/b/Vigil-summary.txt"));
            Assert.False(ReportCatalog.Servable("other/Vigil-summary.txt"));
            Assert.False(ReportCatalog.Servable("Vigil-timeline.tsv"));
            Assert.False(ReportCatalog.Servable(null));
        }

        [Fact]
        public void A_report_says_first_what_it_is_about()
        {
            Assert.Equal("D'Arcy's connection died: nothing arrived for 30 s, all at once",
                ReportCatalog.Headline("drop", new[] { "Vigil incident: drop   2026-10-13 21:15:34", "What triggered it: D'Arcy's connection died: nothing arrived for 30 s, all at once", "Situation: in world" }));
            Assert.Equal("The server kept up with 16 simulated players (teleport)",
                ReportCatalog.Headline("load test", new[] { "Vigil player simulation   ...", "", "== Verdict ==", "The server kept up with 16 simulated players (teleport)", "   more" }));
            Assert.Equal("Vigil deep capture   2026-10-13 16:34:10   1200 frames", ReportCatalog.Headline("deep capture", new[] { "Vigil deep capture   2026-10-13 16:34:10   1200 frames" }));
        }

        [Fact]
        public void The_list_arrives_newest_first_as_the_server_sent_it()
        {
            var entries = new List<ReportCatalog.Entry>
            {
                new ReportCatalog.Entry { Path = "Vigil-incident-211504-drop.txt", Size = 52000, Modified = new DateTime(2026, 10, 13, 21, 15, 34), Kind = "drop", Headline = "a; b = c" },
                new ReportCatalog.Entry { Path = "Vigil-runs/20261013-220715/Vigil-load-163001.txt", Size = 9000, Modified = new DateTime(2026, 10, 13, 16, 34, 6), Kind = "load test", Headline = "kept up" },
            };
            var back = ReportCatalog.ParseList(KeyValues.Decode(KeyValues.Encode(ReportCatalog.FormatList(entries).Select(kv => new KeyValuePair<string, object>(kv.Key, kv.Value)))));
            Assert.Equal(2, back.Count);
            Assert.Equal("Vigil-incident-211504-drop.txt", back[0].Path);
            Assert.Equal(52000, back[0].Size);
            Assert.Equal(new DateTime(2026, 10, 13, 21, 15, 34), back[0].Modified);
            Assert.Equal("drop", back[0].Kind);
            Assert.Equal("Vigil-runs/20261013-220715/Vigil-load-163001.txt", back[1].Path);
        }

        [Fact]
        public void A_report_fetched_in_pieces_arrives_byte_for_byte()
        {
            string text = string.Join("\r\n", Enumerable.Range(0, 3000).Select(i => "line " + i + "; a=b\tÄÖü " + new string('x', i % 50)));
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            int parts = ReportCatalog.Parts(bytes.Length);
            Assert.True(parts > 2);
            var got = new List<byte>();
            for (int p = 0; p < parts; p++)
            {
                string sent = ReportCatalog.EncodePart(bytes, p);
                string travelled = KeyValues.Decode(KeyValues.Encode(new[] { new KeyValuePair<string, object>("data", sent) }))["data"];
                got.AddRange(ReportCatalog.DecodePart(travelled));
            }

            Assert.Equal(bytes, got.ToArray());
        }
    }
}
