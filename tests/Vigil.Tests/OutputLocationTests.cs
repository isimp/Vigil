using System;
using System.IO;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The mod manager shares every text file in the profile with the players who subscribe to it,
    /// so on a player's machine the recordings live outside the profile, in the game's data folder;
    /// on a dedicated server they have their own folder inside BepInEx, where the server's tooling
    /// collects them. What NetProbe recorded before Vigil replaced it comes along: its earlier
    /// sessions stay comparable and the timers it learned to skip stay skipped.
    /// </summary>
    public sealed class OutputLocationTests : IDisposable
    {
        private readonly string _root;
        private string BepInEx => Path.Combine(_root, "BepInEx");
        private string OldPlayerFolder => Path.Combine(_root, "LocalLow", "NetProbe");
        private string New => Path.Combine(_root, "LocalLow", "Vigil");

        public OutputLocationTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "VigilOut-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(BepInEx);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (Exception) { }
        }

        private static void Put(string folder, string relative, string text = "x")
        {
            string p = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text);
        }

        private static string Read(string folder, string relative) => File.ReadAllText(Path.Combine(folder, relative));

        [Fact]
        public void A_players_recordings_go_to_the_games_own_data_folder()
        {
            Assert.Equal(Path.Combine("C:\\data", "Vigil"),
                OutputLocation.Choose("", server: false, bepinexRoot: "C:\\profile\\BepInEx", gameDataDir: "C:\\data"));
        }

        [Fact]
        public void A_dedicated_server_keeps_them_in_their_own_folder_inside_BepInEx()
        {
            Assert.Equal(Path.Combine("/srv/BepInEx", "Vigil"),
                OutputLocation.Choose(null, server: true, bepinexRoot: "/srv/BepInEx", gameDataDir: "/home/v/.config"));
        }

        [Fact]
        public void A_configured_folder_wins_on_both()
        {
            Assert.Equal("D:\\probe", OutputLocation.Choose("  D:\\probe ", server: false, bepinexRoot: "C:\\p", gameDataDir: "C:\\d"));
            Assert.Equal("/data/probe", OutputLocation.Choose("/data/probe", server: true, bepinexRoot: "/srv", gameDataDir: "/home"));
        }

        [Fact]
        public void A_players_earlier_NetProbe_sessions_stay_readable_in_Vigils_folder()
        {
            Put(OldPlayerFolder, "NetProbe-runs/2026-10-08_20-00-00/NetProbe-summary.txt", "old run");
            Put(OldPlayerFolder, "NetProbe-runs/2026-10-09_21-00-00/NetProbe-timeline.tsv", "older timeline");

            int moved = OutputLocation.TakeOver(OldPlayerFolder, New);

            Assert.Equal(2, moved);
            Assert.Equal("old run", Read(New, "Vigil-runs/2026-10-08_20-00-00/NetProbe-summary.txt"));
            Assert.Equal("older timeline", Read(New, "Vigil-runs/2026-10-09_21-00-00/NetProbe-timeline.tsv"));
        }

        [Fact]
        public void Timers_that_crashed_the_game_under_NetProbe_stay_skipped()
        {
            Put(OldPlayerFolder, "NetProbe-state/hooks.skip", "Some.Hook");

            OutputLocation.TakeOver(OldPlayerFolder, New);

            Assert.Equal("Some.Hook", Read(New, "Vigil-state/hooks.skip"));
        }

        [Fact]
        public void The_last_NetProbe_session_is_kept_as_the_newest_earlier_session()
        {
            Put(OldPlayerFolder, "NetProbe-summary.txt", "last session");
            Put(OldPlayerFolder, "NetProbe.txt", "uploads");
            Put(OldPlayerFolder, "NetProbe-runs/2026-10-08_20-00-00/NetProbe-summary.txt", "old run");

            OutputLocation.TakeOver(OldPlayerFolder, New);
            string kept = RunArchive.KeepPreviousRun(New, keep: 20);

            Assert.NotNull(kept);
            Assert.Equal("last session", File.ReadAllText(Path.Combine(kept, "NetProbe-summary.txt")));
            Assert.Equal("uploads", File.ReadAllText(Path.Combine(kept, "NetProbe.txt")));
            Assert.Empty(Directory.GetFiles(New));
            Assert.Equal(2, Directory.GetDirectories(Path.Combine(New, RunArchive.FolderName)).Length);
        }

        [Fact]
        public void The_emptied_NetProbe_folder_goes_away()
        {
            Put(OldPlayerFolder, "NetProbe-summary.txt");
            Put(OldPlayerFolder, "NetProbe-runs/2026-10-08_20-00-00/NetProbe-summary.txt");
            Put(OldPlayerFolder, "NetProbe-state/hooks.skip");

            OutputLocation.TakeOver(OldPlayerFolder, New);

            Assert.False(Directory.Exists(OldPlayerFolder));
        }

        [Fact]
        public void On_a_server_only_NetProbes_files_leave_the_BepInEx_folder()
        {
            Put(BepInEx, "NetProbe-summary.txt");
            Put(BepInEx, "NetProbe-peers.tsv");
            Put(BepInEx, "NetProbe-runs/2026-10-08_20-00-00/NetProbe-peers.tsv");
            Put(BepInEx, "NetProbe-state/hooks.skip");
            Put(BepInEx, "LogOutput.log");
            Put(BepInEx, "config/isimp.NetProbe.cfg");
            Put(BepInEx, "NetProbeExtras.cfg");
            Put(BepInEx, "NetProbeExtras.txt");
            string vigil = Path.Combine(BepInEx, "Vigil");

            OutputLocation.TakeOver(BepInEx, vigil);

            Assert.True(File.Exists(Path.Combine(vigil, "NetProbe-summary.txt")));
            Assert.True(File.Exists(Path.Combine(vigil, "NetProbe-peers.tsv")));
            Assert.True(File.Exists(Path.Combine(vigil, "Vigil-runs", "2026-10-08_20-00-00", "NetProbe-peers.tsv")));
            Assert.True(File.Exists(Path.Combine(vigil, "Vigil-state", "hooks.skip")));
            foreach (var stays in new[] { "LogOutput.log", "config/isimp.NetProbe.cfg", "NetProbeExtras.cfg", "NetProbeExtras.txt" })
                Assert.True(File.Exists(Path.Combine(BepInEx, stays)), stays + " must stay");
            Assert.True(Directory.Exists(BepInEx));
        }

        [Fact]
        public void A_file_already_at_the_new_place_is_never_overwritten()
        {
            Put(OldPlayerFolder, "NetProbe-state/hooks.skip", "from NetProbe");
            Put(New, "Vigil-state/hooks.skip", "already there");

            int moved = OutputLocation.TakeOver(OldPlayerFolder, New);

            Assert.Equal(0, moved);
            Assert.Equal("already there", Read(New, "Vigil-state/hooks.skip"));
            Assert.Equal("from NetProbe", Read(OldPlayerFolder, "NetProbe-state/hooks.skip"));
        }

        [Fact]
        public void A_configured_folder_NetProbe_shared_is_taken_over_in_place()
        {
            string shared = Path.Combine(_root, "recordings");
            Put(shared, "NetProbe-summary.txt", "last session");
            Put(shared, "NetProbe-runs/2026-10-08_20-00-00/NetProbe-summary.txt", "old run");
            Put(shared, "NetProbe-state/hooks.skip", "Some.Hook");

            OutputLocation.TakeOver(shared, shared);

            Assert.Equal("last session", Read(shared, "NetProbe-summary.txt"));
            Assert.Equal("old run", Read(shared, "Vigil-runs/2026-10-08_20-00-00/NetProbe-summary.txt"));
            Assert.Equal("Some.Hook", Read(shared, "Vigil-state/hooks.skip"));
            Assert.False(Directory.Exists(Path.Combine(shared, "NetProbe-runs")));
        }

        [Fact]
        public void Taking_over_twice_moves_nothing_more()
        {
            Put(OldPlayerFolder, "NetProbe-runs/2026-10-08_20-00-00/NetProbe-summary.txt");
            Assert.Equal(1, OutputLocation.TakeOver(OldPlayerFolder, New));
            Assert.Equal(0, OutputLocation.TakeOver(OldPlayerFolder, New));
        }

        [Fact]
        public void A_missing_old_folder_is_not_an_error()
        {
            Assert.Equal(0, OutputLocation.TakeOver(Path.Combine(_root, "nowhere"), New));
        }

        [Fact]
        public void Vigils_own_files_in_the_old_folder_are_left_alone()
        {
            Put(BepInEx, "Vigil-summary.txt", "not NetProbe's");
            string vigil = Path.Combine(BepInEx, "Vigil");
            OutputLocation.TakeOver(BepInEx, vigil);
            Assert.Equal("not NetProbe's", Read(BepInEx, "Vigil-summary.txt"));
            Assert.False(Directory.Exists(vigil) && Directory.GetFiles(vigil, "*", SearchOption.AllDirectories).Any());
        }
    }
}
