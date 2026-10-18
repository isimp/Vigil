using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Drop That and Spawn That send each joining player their config packages, built from scratch
    /// on the server's main loop (0.7 to 0.9 s each, every other player standing still meanwhile).
    /// The same bytes come out every time, so on a server Vigil keeps them from the first build and
    /// hands them to every later join, until the mod reloads its configs, the world's location count
    /// changes or the world session ends. A dedicated server builds them ahead, a minute after its
    /// world is up, while nobody is connected.
    /// </summary>
    public sealed class JoinPackagesTests
    {
        private static JoinStamp Stamp(int world = 1, int locations = 5000, int reloads = 0) => new JoinStamp(world, locations, reloads);

        private static List<JoinPackage> Built() => new List<JoinPackage>
        {
            new JoinPackage("RPC_DropThat_ReceiveGeneralConfig", new byte[] { 1, 2, 3 }),
            new JoinPackage("RPC_DropThat_ReceiveSimpleLocations", new byte[] { 4, 5 }),
        };

        [Fact]
        public void A_join_after_the_first_gets_the_packages_built_for_the_first()
        {
            var cache = new JoinPackages("Drop That");
            Assert.False(cache.Has(Stamp()));
            cache.Keep(Stamp(), Built(), buildMs: 812);
            Assert.True(cache.Has(Stamp()));
            var reused = cache.Reuse();
            Assert.Equal(new[] { "RPC_DropThat_ReceiveGeneralConfig", "RPC_DropThat_ReceiveSimpleLocations" }, reused.Select(p => p.Target));
            Assert.Equal(new byte[] { 1, 2, 3 }, reused[0].Bytes);
            Assert.Equal(1, cache.Builds);
            Assert.Equal(1, cache.Reuses);
        }

        [Fact]
        public void A_reload_of_the_mods_configs_builds_them_again_for_the_next_join()
        {
            var cache = new JoinPackages("Drop That");
            cache.Keep(Stamp(reloads: 0), Built(), 812);
            Assert.False(cache.Has(Stamp(reloads: 1)));
        }

        [Fact]
        public void A_change_in_the_worlds_location_count_builds_them_again()
        {
            var cache = new JoinPackages("Spawn That");
            cache.Keep(Stamp(locations: 0), Built(), 700);
            Assert.False(cache.Has(Stamp(locations: 5000)));
        }

        [Fact]
        public void A_new_world_session_starts_with_nothing_kept()
        {
            var cache = new JoinPackages("Spawn That");
            cache.Keep(Stamp(world: 1), Built(), 700);
            Assert.False(cache.Has(Stamp(world: 2)));
        }

        [Fact]
        public void A_dedicated_server_builds_them_ahead_a_minute_after_its_world_is_up_while_nobody_is_connected()
        {
            Assert.True(JoinWarmUp.Due(dedicated: true, secondsSinceWorldUp: 60, peers: 0, kept: false));
            Assert.False(JoinWarmUp.Due(dedicated: true, secondsSinceWorldUp: 59, peers: 0, kept: false));
        }

        [Fact]
        public void Building_ahead_waits_while_anyone_is_connected_or_joining()
        {
            Assert.False(JoinWarmUp.Due(dedicated: true, secondsSinceWorldUp: 600, peers: 1, kept: false));
        }

        [Fact]
        public void A_players_hosted_world_never_builds_ahead_since_the_host_would_feel_it()
        {
            Assert.False(JoinWarmUp.Due(dedicated: false, secondsSinceWorldUp: 600, peers: 0, kept: false));
        }

        [Fact]
        public void Building_ahead_comes_at_most_every_five_minutes_so_a_changing_world_is_not_rebuilt_frame_after_frame()
        {
            Assert.False(JoinWarmUp.Due(dedicated: true, secondsSinceWorldUp: 600, peers: 0, kept: false, secondsSinceLastAhead: 299));
            Assert.True(JoinWarmUp.Due(dedicated: true, secondsSinceWorldUp: 600, peers: 0, kept: false, secondsSinceLastAhead: 300));
        }

        [Fact]
        public void Packages_already_kept_are_not_built_ahead_again()
        {
            Assert.False(JoinWarmUp.Due(dedicated: true, secondsSinceWorldUp: 600, peers: 0, kept: true));
        }

        [Fact]
        public void When_a_part_the_cache_uses_is_missing_the_mod_is_left_to_itself_and_the_parts_are_named()
        {
            Assert.Null(JoinPackages.LeftAlone("Drop That", new List<string>()));
            Assert.Equal("Drop That builds its own join packages: not found in this version: SyncManager.PackageHandlers, OutgoingMessage.Target",
                JoinPackages.LeftAlone("Drop That", new List<string> { "SyncManager.PackageHandlers", "OutgoingMessage.Target" }));
        }

        [Fact]
        public void The_event_lines_say_what_was_built_and_what_a_join_got()
        {
            var cache = new JoinPackages("Drop That");
            cache.Keep(Stamp(), new List<JoinPackage> { new JoinPackage("RPC_A", new byte[700 * 1024]), new JoinPackage("RPC_B", new byte[524 * 1024]) }, 812.4);
            Assert.Equal("Drop That: join packages built in 812 ms ahead of the first join (2 packages, 1.2 MB); later joins get the same", cache.BuiltLine("ahead of the first join"));
            cache.Reuse();
            Assert.Equal("Drop That: 2 join packages kept from the first build (812 ms) sent", cache.ReusedLine());
        }

        [Fact]
        public void The_patches_tab_says_on_this_game_which_mods_it_found_and_that_it_works_on_the_server()
        {
            Assert.Equal("Drop That 3.1.7 and Spawn That 1.2.20: every part found; it works where players join, on the server",
                JoinPackages.GameLine(new JoinMod("Drop That", "3.1.7", null), new JoinMod("Spawn That", "1.2.20", null)));
            Assert.Equal("Drop That 3.1.7: every part found; it works where players join, on the server. Spawn That is not installed",
                JoinPackages.GameLine(new JoinMod("Drop That", "3.1.7", null), new JoinMod("Spawn That", null, "not installed")));
            Assert.Equal("Neither Drop That nor Spawn That is installed",
                JoinPackages.GameLine(new JoinMod("Drop That", null, "not installed"), new JoinMod("Spawn That", null, "not installed")));
        }

        [Fact]
        public void A_mod_left_to_itself_is_named_with_the_reason_on_this_game()
        {
            var spawnThat = new JoinMod("Spawn That", "1.3.0", "Spawn That builds its own join packages: not found in this version: DataTransferService.AddToQueue");
            Assert.Equal("Drop That 3.1.7: every part found; it works where players join, on the server. Spawn That builds its own join packages: not found in this version: DataTransferService.AddToQueue",
                JoinPackages.GameLine(new JoinMod("Drop That", "3.1.7", null), spawnThat));
            Assert.True(spawnThat.LeftAlone);
            Assert.False(new JoinMod("Drop That", null, "not installed").LeftAlone);
        }

        [Fact]
        public void The_server_row_reads_the_servers_lines_or_says_the_server_does_not_tell()
        {
            Assert.Equal("Drop That: built once in 819 ms · Spawn That: built once in 766 ms",
                JoinPackages.ServerLine(new[] { "Drop That: built once in 819 ms", "Spawn That: built once in 766 ms" }));
            Assert.Equal("Drop That: built once in 819 ms", JoinPackages.ServerRow("Drop That: built once in 819 ms", asked: true));
            Assert.Equal("the server's Vigil does not tell this (older than 0.37.0)", JoinPackages.ServerRow(null, asked: true));
            Assert.Equal("asking the server", JoinPackages.ServerRow(null, asked: false));
        }

        [Fact]
        public void The_summary_line_gives_the_builds_the_reuses_and_the_time_saved()
        {
            var cache = new JoinPackages("Spawn That");
            Assert.Equal("Spawn That: no join packages built yet", cache.SummaryLine());
            cache.Keep(Stamp(), Built(), 734);
            cache.Reuse(); cache.Reuse(); cache.Reuse();
            Assert.Equal("Spawn That: built once in 734 ms, sent from the cache to 3 joins (about 2.2 s of standing still saved)", cache.SummaryLine());
            cache.Keep(Stamp(reloads: 1), Built(), 700);
            Assert.Equal("Spawn That: built 2 times (last 700 ms), sent from the cache to 3 joins (about 2.2 s of standing still saved)", cache.SummaryLine());
        }
    }
}
