using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Vigil can be switched on, off, or to auto, where it records only while there is something
    /// to record: on a player's machine while in a world, on a dedicated server while players are
    /// connected (and shortly after), plus the server's start. A deep capture, a load test or a
    /// player simulation always records, whatever the switch says.
    /// </summary>
    public sealed class RecordingPolicyTests
    {
        private static bool Server(RecordingMode m, int players, double sinceLastPlayer = double.PositiveInfinity, double sinceStart = 3600, bool busy = false) =>
            RecordingPolicy.Records(m, server: true, inWorld: false, players: players, secondsSinceLastPlayer: sinceLastPlayer, secondsSinceStart: sinceStart, busy: busy);

        [Fact]
        public void On_records_an_empty_server()
        {
            Assert.True(Server(RecordingMode.On, 0));
        }

        [Fact]
        public void Off_records_nothing_even_with_players()
        {
            Assert.False(Server(RecordingMode.Off, 5));
        }

        [Fact]
        public void A_test_or_deep_capture_records_even_when_switched_off()
        {
            Assert.True(Server(RecordingMode.Off, 0, busy: true));
        }

        [Fact]
        public void Auto_records_a_server_while_players_are_connected()
        {
            Assert.True(Server(RecordingMode.Auto, 1));
        }

        [Fact]
        public void Auto_sleeps_on_an_empty_server()
        {
            Assert.False(Server(RecordingMode.Auto, 0, sinceLastPlayer: 600));
        }

        [Fact]
        public void Auto_keeps_recording_for_a_while_after_the_last_player_left()
        {
            // The save and the clean-up after a player leaves are part of the story.
            Assert.True(Server(RecordingMode.Auto, 0, sinceLastPlayer: RecordingPolicy.ServerLingerSeconds - 1));
            Assert.False(Server(RecordingMode.Auto, 0, sinceLastPlayer: RecordingPolicy.ServerLingerSeconds + 1));
        }

        [Fact]
        public void Auto_records_the_servers_start()
        {
            Assert.True(Server(RecordingMode.Auto, 0, sinceStart: RecordingPolicy.ServerStartSeconds - 1));
            Assert.False(Server(RecordingMode.Auto, 0, sinceStart: RecordingPolicy.ServerStartSeconds + 1));
        }

        [Fact]
        public void Auto_records_a_players_game_in_a_world_and_not_in_the_menu()
        {
            Assert.True(RecordingPolicy.Records(RecordingMode.Auto, false, inWorld: true, 0, double.PositiveInfinity, 3600, false));
            Assert.False(RecordingPolicy.Records(RecordingMode.Auto, false, inWorld: false, 0, double.PositiveInfinity, 3600, false));
        }

        [Fact]
        public void The_switch_reads_on_off_and_auto_and_nothing_else()
        {
            Assert.True(RecordingPolicy.TryParse("OFF", out var m));
            Assert.Equal(RecordingMode.Off, m);
            Assert.True(RecordingPolicy.TryParse("auto", out m));
            Assert.Equal(RecordingMode.Auto, m);
            Assert.False(RecordingPolicy.TryParse("maybe", out _));
        }

        [Fact]
        public void The_panel_lights_the_switch_the_status_was_written_with()
        {
            foreach (var mode in new[] { RecordingMode.On, RecordingMode.Off, RecordingMode.Auto })
            {
                string status = RecordingPolicy.Status(mode, recording: mode != RecordingMode.Off, why: "a reason, with a comma");
                Assert.True(RecordingPolicy.TryReadStatus(status, out var read));
                Assert.Equal(mode, read);
            }
        }

        [Fact]
        public void The_panel_lights_the_switch_of_a_server_that_runs_vigil_0_2_0()
        {
            Assert.True(RecordingPolicy.TryReadStatus("Recording: off, not recording (switched off)", out var m));
            Assert.Equal(RecordingMode.Off, m);
        }

        [Fact]
        public void A_status_that_names_no_switch_lights_none()
        {
            Assert.False(RecordingPolicy.TryReadStatus(null, out _));
            Assert.False(RecordingPolicy.TryReadStatus("", out _));
            Assert.False(RecordingPolicy.TryReadStatus("-", out _));
            Assert.False(RecordingPolicy.TryReadStatus("Recording: maybe, recording (x)", out _));
        }
    }
}
