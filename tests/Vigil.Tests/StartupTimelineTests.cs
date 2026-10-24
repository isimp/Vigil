using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The startup timeline: where the time from starting the game to its main menu went, read from
    /// the lines BepInEx logs while it loads the plugins (each "Loading [...]" line marks the start of
    /// one plugin), the patch batch StartupAccelerator reports, the bundle loads the cache saw, and the
    /// menu marks Vigil sets itself.
    /// </summary>
    public sealed class StartupTimelineTests
    {
        private static StartupEvent E(double ms, string kind, string text = "") => new StartupEvent(ms, kind, text);

        // Process started 20 s before the patcher; plugins A, B, C; StartupAccelerator patches after them; the menu follows.
        private static List<StartupEvent> Start() => new List<StartupEvent>
        {
            E(0, StartupLines.Started, "20000"),
            E(2000, StartupLines.Plugin, "Loading [Alpha 1.0.0] (a.alpha)"),
            E(2500, StartupLines.Bundle, "usual|75000000|1200|k1"),
            E(14000, StartupLines.Plugin, "Loading [Beta 2.1] (b.beta)"),
            E(14100, StartupLines.Plugin, "Loading [Gamma 0.3] (c.gamma)"),
            E(17000, StartupLines.PluginsDone, "Chainloader startup complete (3 loaded, 0 skipped, 0 failed)"),
            E(25600, StartupLines.Batch, "Batch-patched 1257 methods after Chainloader end in 8595 ms"),
            E(30000, StartupLines.MenuScene),
            E(40000, StartupLines.MenuStart),
            E(41000, StartupLines.MenuShown),
        };

        [Fact]
        public void A_plugin_takes_the_time_from_its_loading_line_to_the_next_plugins()
        {
            var r = StartupTimeline.Build(Start());
            Assert.Equal(new[] { "Alpha 1.0.0", "Beta 2.1", "Gamma 0.3" }, r.Plugins.Select(p => p.Name).ToArray());
            Assert.Equal(12000, r.Plugins[0].Ms, 1);
            Assert.Equal(100, r.Plugins[1].Ms, 1);
        }

        [Fact]
        public void The_last_plugin_ends_when_BepInEx_says_all_plugins_are_loaded()
        {
            var r = StartupTimeline.Build(Start());
            Assert.Equal(2900, r.Plugins[2].Ms, 1);
        }

        [Fact]
        public void The_time_before_the_first_plugin_belongs_to_the_game_engine_and_BepInEx()
        {
            var r = StartupTimeline.Build(Start());
            Assert.Equal(20000, r.EngineMs, 1);
            Assert.Equal(2000, r.BepInExMs, 1);
        }

        [Fact]
        public void StartupAccelerators_patch_batch_is_a_step_of_its_own()
        {
            var r = StartupTimeline.Build(Start());
            Assert.Equal(8595, r.PatchingMs, 1);
            Assert.Equal(1257, r.PatchedMethods);
        }

        [Fact]
        public void The_steps_add_up_to_the_whole_start()
        {
            var r = StartupTimeline.Build(Start());
            Assert.Equal(61000, r.TotalMs, 1);
            Assert.Equal(r.TotalMs, r.EngineMs + r.BepInExMs + r.PluginsMs + r.PatchingMs + r.MenuMs, 1);
            Assert.True(r.Finished);
        }

        [Fact]
        public void A_bundle_load_counts_toward_the_plugin_that_was_loading()
        {
            var r = StartupTimeline.Build(Start());
            var alpha = r.Plugins[0];
            Assert.Equal(1, alpha.Bundles);
            Assert.Equal(75000000, alpha.BundleBytes);
            Assert.Equal(1200, alpha.BundleMs, 1);
            Assert.Equal(0, r.Plugins[1].Bundles);
        }

        [Fact]
        public void The_bundle_outcomes_are_counted_for_the_cache_row()
        {
            var events = Start();
            events.Insert(4, E(14050, StartupLines.Bundle, "copy|10000000|30|k2"));
            events.Insert(5, E(14060, StartupLines.Bundle, "plain|2000000|5|"));
            var r = StartupTimeline.Build(events);
            Assert.Equal(1, r.FromCopies);
            Assert.Equal(1, r.LoadedUsual);
            Assert.Equal(1, r.NotLzma);
        }

        [Fact]
        public void A_start_that_ends_before_the_menu_still_reports_what_it_has()
        {
            var events = Start().Take(4).ToList();
            var r = StartupTimeline.Build(events);
            Assert.False(r.Finished);
            Assert.Equal(2, r.Plugins.Count);
            Assert.Equal(20000 + 14000, r.TotalMs, 1);
            Assert.Contains("did not reach the menu", StartupTimeline.Summary(r));
        }

        [Fact]
        public void Without_StartupAccelerator_the_patching_step_is_empty_and_the_menu_takes_the_rest()
        {
            var events = Start().Where(e => e.Kind != StartupLines.Batch).ToList();
            var r = StartupTimeline.Build(events);
            Assert.Equal(0, r.PatchingMs, 1);
            Assert.Equal(24000, r.MenuMs, 1);
        }

        [Fact]
        public void The_report_names_the_slowest_plugins_first()
        {
            var r = StartupTimeline.Build(Start());
            var lines = StartupTimeline.Lines(r, 15);
            int alpha = lines.FindIndex(l => l.Contains("Alpha 1.0.0")), gamma = lines.FindIndex(l => l.Contains("Gamma 0.3")), beta = lines.FindIndex(l => l.Contains("Beta 2.1"));
            Assert.True(alpha >= 0 && alpha < gamma && gamma < beta);
            Assert.Equal("Alpha 1.0.0 12 s, Gamma 0.3 2.9 s, Beta 2.1 0.1 s", StartupTimeline.Slowest(r, 3));
        }

        [Fact]
        public void The_summary_reads_as_minutes_and_its_parts()
        {
            var r = StartupTimeline.Build(Start());
            Assert.Equal("1 min 1 s to the menu (Unity 20 s, BepInEx 2.0 s, plugins 15 s, patching 8.6 s, menu 15 s)", StartupTimeline.Summary(r));
            Assert.Equal("1 min 1 s", StartupTimeline.Badge(r));
        }

        [Fact]
        public void Durations_read_as_tenths_below_ten_seconds_and_whole_seconds_above()
        {
            Assert.Equal("0.4 s", StartupTimeline.Duration(400));
            Assert.Equal("8.6 s", StartupTimeline.Duration(8595));
            Assert.Equal("36 s", StartupTimeline.Duration(36200));
            Assert.Equal("2 min 21 s", StartupTimeline.Duration(141000));
        }

        [Fact]
        public void Events_survive_the_round_trip_through_their_tab_separated_line()
        {
            var e = E(1234.5, StartupLines.Plugin, "Loading [Name\twith tab] (g)");
            var back = StartupEvent.Parse(e.Line());
            Assert.Equal(1234.5, back.Ms, 1);
            Assert.Equal(StartupLines.Plugin, back.Kind);
            Assert.Equal("Loading [Name with tab] (g)", back.Text);
            Assert.Null(StartupEvent.Parse("not a line"));
        }

        [Fact]
        public void Only_the_lines_that_mark_a_step_are_kept_from_the_log()
        {
            Assert.Equal(StartupLines.Plugin, StartupLines.Classify("BepInEx", "Loading [Jotunn 2.30.2] (com.jotunn.jotunn)"));
            Assert.Equal(StartupLines.PluginsDone, StartupLines.Classify("BepInEx", "Chainloader startup complete (130 loaded, 3 skipped, 0 failed)"));
            Assert.Equal(StartupLines.Batch, StartupLines.Classify("StartupAccelerator", "Batch-patched 1257 methods after Chainloader end in 8595 ms"));
            Assert.Null(StartupLines.Classify("BepInEx", "Loaded 1 patcher method from [Keepsake.Preloader 0.5.1.0]"));
            Assert.Null(StartupLines.Classify("SomeMod", "Loading [fake line from a mod]"));
            Assert.Null(StartupLines.Classify("StartupAccelerator", "something else"));
        }

        [Fact]
        public void A_second_patch_batch_after_the_menu_scene_counts_toward_the_menu()
        {
            var events = Start();
            events.Insert(8, E(32000, StartupLines.Batch, "Batch-patched 137 methods after FejdStartup.Awake in 728 ms"));
            var r = StartupTimeline.Build(events);
            Assert.Equal(8595, r.PatchingMs, 1);
            Assert.Equal(1257, r.PatchedMethods);
        }

        [Fact]
        public void A_patch_batch_only_after_the_menu_scene_is_part_of_the_menu_not_the_patching()
        {
            var events = Start().Where(e => e.Kind != StartupLines.Batch).ToList();
            events.Insert(7, E(32000, StartupLines.Batch, "Batch-patched 137 methods after FejdStartup.Awake in 728 ms"));
            var r = StartupTimeline.Build(events);
            Assert.Equal(0, r.PatchingMs, 1);
            Assert.Equal(24000, r.MenuMs, 1);
        }

        [Fact]
        public void The_join_takes_the_time_from_connecting_to_standing_in_the_world()
        {
            var events = Start();
            events.Add(E(90000, StartupLines.JoinStart));
            events.Add(E(91000, StartupLines.JoinStep, "ZNet.RPC_PeerInfo|10364"));
            events.Add(E(95000, StartupLines.JoinMessage, "Jotunn: com.jotunn.jotunn!ConfigSync|4022"));
            events.Add(E(95100, StartupLines.JoinMessage, "Spawn That!: RPC_ReceiveLocationsSpawnThat|727"));
            events.Add(E(128000, StartupLines.JoinWorld));
            var r = StartupTimeline.Build(events);
            Assert.True(r.Joined);
            Assert.Equal(38000, r.JoinMs, 1);
            Assert.Equal("38 s (handshake 10 s, Jotunn: com.jotunn.jotunn!ConfigSync 4.0 s, Spawn That!: RPC_ReceiveLocationsSpawnThat 0.7 s)", StartupTimeline.JoinLine(r));
        }

        [Fact]
        public void On_a_dedicated_server_the_start_runs_until_the_world_is_up()
        {
            var events = Start().Where(e => e.Kind != StartupLines.MenuStart && e.Kind != StartupLines.MenuShown).ToList();
            events.Add(E(41000, StartupLines.WorldUp));
            var r = StartupTimeline.Build(events);
            Assert.True(r.Finished);
            Assert.True(r.Server);
            Assert.Equal(61000, r.TotalMs, 1);
            Assert.Equal("1 min 1 s to the running world (Unity 20 s, BepInEx 2.0 s, plugins 15 s, patching 8.6 s, world 15 s)", StartupTimeline.Summary(r));
        }

        [Fact]
        public void Before_a_join_the_join_line_says_so()
        {
            var r = StartupTimeline.Build(Start());
            Assert.False(r.Joined);
            Assert.Equal("not joined a world yet this session", StartupTimeline.JoinLine(r));
        }
    }
}
