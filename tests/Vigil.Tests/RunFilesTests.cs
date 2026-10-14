using System;
using System.IO;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The report tool reads every kept session, including those NetProbe recorded before Vigil
    /// replaced it, whose files carry NetProbe's name.
    /// </summary>
    public sealed class RunFilesTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "VigilRun-" + Guid.NewGuid().ToString("N"));

        public RunFilesTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (Exception) { }
        }

        private void Put(string name, string text = "x") => File.WriteAllText(Path.Combine(_dir, name), text);

        [Fact]
        public void A_session_Vigil_recorded_is_read()
        {
            Put("Vigil-summary.txt", "vigil");
            Assert.Equal("vigil", File.ReadAllText(RunFiles.Find(_dir, "summary.txt")));
        }

        [Fact]
        public void A_session_NetProbe_recorded_is_read_too()
        {
            Put("NetProbe-timeline.tsv", "netprobe");
            Assert.Equal("netprobe", File.ReadAllText(RunFiles.Find(_dir, "timeline.tsv")));
        }

        [Fact]
        public void Vigils_own_file_is_read_where_both_are_there()
        {
            Put("NetProbe-summary.txt", "netprobe");
            Put("Vigil-summary.txt", "vigil");
            Assert.Equal("vigil", File.ReadAllText(RunFiles.Find(_dir, "summary.txt")));
        }

        [Fact]
        public void A_file_the_session_does_not_have_is_named_under_Vigils_name()
        {
            Assert.Equal(Path.Combine(_dir, "Vigil-mods.tsv"), RunFiles.Find(_dir, "mods.tsv"));
        }

        [Fact]
        public void Every_incident_of_the_session_is_listed_whatever_its_name()
        {
            Put("NetProbe-incident-211502-silent.txt");
            Put("Vigil-incident-091200-hitch.txt");
            Put("Vigil-summary.txt");
            var names = RunFiles.All(_dir, "incident-*.txt").Select(Path.GetFileName).ToList();
            Assert.Equal(new[] { "NetProbe-incident-211502-silent.txt", "Vigil-incident-091200-hitch.txt" }, names);
        }

        [Fact]
        public void A_missing_session_folder_has_no_files()
        {
            Assert.Empty(RunFiles.All(Path.Combine(_dir, "gone"), "incident-*.txt"));
        }
    }
}
