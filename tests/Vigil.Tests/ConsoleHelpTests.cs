using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// "vigil help" in the console lists everything Vigil can do, one line each, grouped by area,
    /// and "vigil help &lt;command&gt;" explains one command with its options and defaults. The list
    /// is checked against the words the console really answers, and each word is answered by the
    /// area it is listed under, so a new command cannot go unlisted or land in the wrong place.
    /// </summary>
    public sealed class ConsoleHelpTests
    {
        private static string ProjectRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Vigil.csproj"))) return dir.FullName;
            throw new DirectoryNotFoundException("Vigil.csproj not found above " + AppContext.BaseDirectory);
        }

        // The words a source file compares the console's first word against (word == "...").
        private static string[] WordsIn(string relative)
        {
            string src = File.ReadAllText(Path.Combine(ProjectRoot(), relative));
            return Regex.Matches(src, "word == \"([a-z?]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToArray();
        }

        private static readonly (string File, string Area)[] Answerers =
        {
            ("src/VigilPlugin.cs", null),
            ("src/Recorder/RecorderModule.cs", ConsoleHelp.Recorder),
            ("src/Spawns/SpawnsModule.cs", ConsoleHelp.SpawnMarks),
            ("src/Cleanup/CleanupModule.cs", ConsoleHelp.Cleanup),
        };

        private static string[] AcceptedWords() => Answerers.SelectMany(a => WordsIn(a.File)).Distinct().ToArray();

        private static string Overview => string.Join("\n", ConsoleHelp.Lines("", "F9", "F6", "C:/rec"));
        private static string About(string topic) => string.Join("\n", ConsoleHelp.Lines(topic, "F9", "F6", "C:/rec"));

        [Fact]
        public void The_overview_lists_every_command_the_console_accepts()
        {
            var words = AcceptedWords();
            Assert.Contains("help", words);
            Assert.Contains("spawns", words);
            Assert.Contains("missing", words);
            foreach (var w in words.Where(w => w != "?"))
            {
                Assert.Matches(@"(^|\W)" + Regex.Escape(w) + @"(\W|$)", Overview);
                Assert.Contains(w, ConsoleHelp.Commands);
            }
        }

        [Fact]
        public void Tab_completion_offers_only_commands_the_console_accepts()
        {
            var words = AcceptedWords();
            foreach (var c in ConsoleHelp.Commands) Assert.True(words.Contains(c), c + " is offered but the console does not handle it");
        }

        [Fact]
        public void Each_command_is_answered_by_the_area_it_belongs_to()
        {
            foreach (var (file, area) in Answerers)
                foreach (var w in WordsIn(file).Where(w => w != "?"))
                    Assert.True(ConsoleHelp.Owner(w) == area, w + " is answered in " + file + " but belongs to " + (ConsoleHelp.Owner(w) ?? "Vigil itself"));
            foreach (var c in ConsoleHelp.Commands.Where(c => c != "status" && c != "selftest" && c != "help"))
                Assert.Contains(ConsoleHelp.Owner(c), ConsoleHelp.Areas);
        }

        [Fact]
        public void Inspecting_an_object_and_watching_the_busiest_are_inspect_commands_of_the_recorder()
        {
            var lines = ConsoleHelp.Lines("", "F9", "F6", "C:/rec");
            int inspect = lines.FindIndex(l => l == "Inspect"), spawn = lines.FindIndex(l => l == "Spawn marks");
            foreach (var word in new[] { "inspect", "busiest" })
            {
                Assert.Contains(word, ConsoleHelp.Commands);
                Assert.Equal(ConsoleHelp.Recorder, ConsoleHelp.Owner(word));
                Assert.InRange(lines.FindIndex(l => l.TrimStart().StartsWith("vigil " + word, StringComparison.Ordinal)), inspect, spawn);
            }
            Assert.Contains(ConsoleHelp.BusiestSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " s", About("busiest"));
        }

        [Fact]
        public void The_weather_is_an_inspect_command_whose_help_names_every_change_and_who_may_make_it()
        {
            var lines = ConsoleHelp.Lines("", "F9", "F6", "C:/rec");
            int inspect = lines.FindIndex(l => l == "Inspect"), spawn = lines.FindIndex(l => l == "Spawn marks");
            Assert.Contains("weather", ConsoleHelp.Commands);
            Assert.Equal(ConsoleHelp.Recorder, ConsoleHelp.Owner("weather"));
            Assert.InRange(lines.FindIndex(l => l.TrimStart().StartsWith("vigil weather", StringComparison.Ordinal)), inspect, spawn);
            string about = About("weather");
            foreach (var word in new[] { "report", "set", "fog", "wind", "time", "limit", "reset", "admins" }) Assert.Contains(word, about);
        }

        [Fact]
        public void The_spawn_log_is_an_inspect_command_of_the_recorder_and_names_every_source()
        {
            var lines = ConsoleHelp.Lines("", "F9", "F6", "C:/rec");
            int inspect = lines.FindIndex(l => l == "Inspect"), spawn = lines.FindIndex(l => l == "Spawn marks");
            Assert.Contains("spawned", ConsoleHelp.Commands);
            Assert.Equal(ConsoleHelp.Recorder, ConsoleHelp.Owner("spawned"));
            Assert.InRange(lines.FindIndex(l => l.TrimStart().StartsWith("vigil spawned", StringComparison.Ordinal)), inspect, spawn);
            string about = About("spawned");
            foreach (var word in new[] { "biome", "raid", "spawner structure", "fixed spawner", "base", "cancelled", "minutes" }) Assert.Contains(word, about);
            foreach (var word in new[] { "vigil spawned server", "admins", "spawn log", "Sharing" }) Assert.Contains(word, about);
        }

        [Fact]
        public void Reading_the_whole_world_is_a_recorder_command_for_admins_that_says_what_it_removes_and_how()
        {
            var lines = ConsoleHelp.Lines("", "F9", "F6", "C:/rec");
            Assert.Contains("leftovers", ConsoleHelp.Commands);
            Assert.Equal(ConsoleHelp.Recorder, ConsoleHelp.Owner("leftovers"));
            Assert.Contains(lines, l => l.TrimStart().StartsWith("vigil leftovers", StringComparison.Ordinal));
            string about = About("leftovers");
            foreach (var word in new[] { "admins", "scan", "remove <hash>", "bots", "saves the world", "player history", "load test" }) Assert.Contains(word, about);
        }

        [Fact]
        public void What_could_spawn_at_a_spot_is_an_inspect_command_of_the_recorder_that_says_what_it_cannot_see()
        {
            var lines = ConsoleHelp.Lines("", "F9", "F6", "C:/rec");
            int inspect = lines.FindIndex(l => l == "Inspect"), spawn = lines.FindIndex(l => l == "Spawn marks");
            Assert.Contains("spawnable", ConsoleHelp.Commands);
            Assert.Equal(ConsoleHelp.Recorder, ConsoleHelp.Owner("spawnable"));
            Assert.InRange(lines.FindIndex(l => l.TrimStart().StartsWith("vigil spawnable", StringComparison.Ordinal)), inspect, spawn);
            string about = About("spawnable");
            foreach (var word in new[] { "crosshair", "\"could\"", "\"not now\"", "\"no\"", "40 to 80 m", "mods", "cannot read" }) Assert.Contains(word, about);
        }

        [Fact]
        public void The_hammer_comparison_is_a_recorder_command_whose_help_says_what_to_do_and_what_it_writes()
        {
            var lines = ConsoleHelp.Lines("", "F9", "F6", "C:/rec");
            Assert.Contains("hammer", ConsoleHelp.Commands);
            Assert.Equal(ConsoleHelp.Recorder, ConsoleHelp.Owner("hammer"));
            int recorder = lines.FindIndex(l => l == "Recording"), load = lines.FindIndex(l => l == "Load tests");
            Assert.InRange(lines.FindIndex(l => l.TrimStart().StartsWith("vigil hammer", StringComparison.Ordinal)), recorder, load);
            string about = About("hammer");
            foreach (var word in new[] { "20 s", "tool out", "put away", "menu shut", "Vigil-deep-", "Vigil-events.tsv", "session summary" }) Assert.Contains(word, about);
        }

        [Fact]
        public void The_servers_fog_limit_and_reading_value_names_again_are_explained()
        {
            string weather = About("weather");
            Assert.Contains("limit server", weather);
            Assert.Contains("ServerSync", weather);
            Assert.Contains("vigil inspect names", About("inspect"));
        }

        [Fact]
        public void Every_command_has_its_own_explanation()
        {
            foreach (var c in ConsoleHelp.Commands)
                Assert.DoesNotContain("No help on", About(c));
        }

        [Fact]
        public void The_overview_groups_the_commands_by_area()
        {
            var lines = ConsoleHelp.Lines("", "F9", "F6", "C:/rec");
            int At(string heading) => lines.FindIndex(l => l == heading);
            int Command(string text) => lines.FindIndex(l => l.TrimStart().StartsWith(text, StringComparison.Ordinal));
            var headings = new[] { "Recording", "Load tests", "Inspect", "Spawn marks", "Cleanup" };
            for (int i = 0; i < headings.Length; i++)
            {
                Assert.True(At(headings[i]) >= 0, headings[i] + " is not a heading");
                if (i > 0) Assert.True(At(headings[i - 1]) < At(headings[i]), headings[i - 1] + " comes before " + headings[i]);
            }
            Assert.InRange(Command("vigil mark"), At("Recording"), At("Load tests"));
            Assert.InRange(Command("vigil load"), At("Load tests"), At("Inspect"));
            Assert.InRange(Command("vigil census"), At("Inspect"), At("Spawn marks"));
            Assert.InRange(Command("vigil spawns"), At("Spawn marks"), At("Cleanup"));
            Assert.True(Command("vigil missing") > At("Cleanup"));
        }

        [Fact]
        public void The_load_test_help_names_its_options_steps_and_length()
        {
            string t = About("load");
            foreach (var word in new[] { "up", "down", "force", "deep", "stop", "status", "players" }) Assert.Matches(@"\b" + word + @"\b", t);
            Assert.Contains("50/100/200/400/800 kB/s", t);
            Assert.Contains(string.Format("about {0:0} minutes", LoadPlan.For("both").TotalSeconds / 60), t);
            Assert.Contains("400 m", t);
        }

        [Fact]
        public void The_player_simulation_help_names_its_patterns_and_defaults()
        {
            string t = About("players");
            foreach (var p in new[] { "stay", "spread", "travel", "teleport" }) Assert.Contains(p, t);
            Assert.Contains("default " + ConsoleHelp.PlayersDefault, t);
            Assert.Contains("up to " + LoadPlan.MaxPlayers, t);
            Assert.Contains("objects <n>", t);
            Assert.Contains("default " + ConsoleHelp.ObjectsDefault, t);
            Assert.Contains("link <kB/s>", t);
            Assert.Contains("default " + ConsoleHelp.LinkDefault, t);
            Assert.Equal(t, About("load players"));
        }

        [Fact]
        public void The_lag_report_help_names_the_configured_key()
        {
            Assert.Contains("F9", About("mark"));
            Assert.Contains("Ctrl + F8", string.Join("\n", ConsoleHelp.Lines("mark", "Ctrl + F8", "F6", "C:/rec")));
            Assert.Contains("Ctrl + F8", string.Join("\n", ConsoleHelp.Lines("", "Ctrl + F8", "F6", "C:/rec")));
        }

        [Fact]
        public void The_spawn_mark_help_names_the_configured_key()
        {
            Assert.Contains("F6", About("spawns"));
            Assert.Contains("F7", string.Join("\n", ConsoleHelp.Lines("spawns", "F9", "F7", "C:/rec")));
            Assert.Contains("F7", string.Join("\n", ConsoleHelp.Lines("", "F9", "F7", "C:/rec")));
        }

        [Fact]
        public void The_cleanup_help_says_nothing_goes_without_the_word_remove()
        {
            string t = About("missing");
            Assert.Contains("vigil missing [remove] [radius] [hash]", t);
            Assert.Contains(ConsoleHelp.MissingRadius + " m", t);
            Assert.Contains("Nothing is removed without the word remove", t);
        }

        [Fact]
        public void The_self_test_help_says_it_can_check_the_server_too()
        {
            string t = About("selftest");
            Assert.Contains("vigil selftest [server]", t);
            Assert.Contains("admins", t);
            Assert.Null(ConsoleHelp.Owner("selftest"));
            Assert.Contains("vigil selftest", Overview);
        }

        [Fact]
        public void The_overview_names_the_panel_key()
        {
            Assert.Contains("Shift + F9", string.Join("\n", ConsoleHelp.Lines("", "F9", "F6", "C:/rec", "Shift + F9")));
        }

        [Fact]
        public void The_overview_says_where_the_recordings_are()
        {
            Assert.Contains("C:/rec", Overview);
        }

        [Fact]
        public void An_unknown_topic_says_so_and_shows_the_overview()
        {
            string t = About("frobnicate");
            Assert.Contains("No help on \"frobnicate\"", t);
            Assert.Contains("vigil load", t);
        }

        [Fact]
        public void Help_topics_ignore_case_and_a_leading_vigil()
        {
            Assert.Equal(About("load"), About("LOAD"));
            Assert.Equal(About("load"), About("vigil load"));
        }

        [Fact]
        public void Every_line_fits_the_console()
        {
            foreach (var topic in new[] { "" }.Concat(ConsoleHelp.Commands).Concat(new[] { "players" }))
                foreach (var line in ConsoleHelp.Lines(topic, "F9", "F6", "C:/Users/someone/AppData/LocalLow/IronGate/Valheim/Vigil"))
                    Assert.True(line.Length <= 100, "too long for the console (" + line.Length + "): " + line);
        }

        [Fact]
        public void The_prefab_lister_is_gone_from_the_console_and_its_help()
        {
            Assert.DoesNotContain("prefabs", ConsoleHelp.Commands);
            Assert.Null(ConsoleHelp.Owner("prefabs"));
            Assert.DoesNotContain("vigil prefabs", Overview);
        }
    }
}
