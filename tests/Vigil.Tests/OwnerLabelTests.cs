using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// With "Look at it" on, a label under the crosshair says what the player looks at and whose
    /// game runs it: the player's name, the connection's ping when it is known, and how far that
    /// player is from the thing, which tells why a creature or cart jumps around. Your own game, the
    /// server and objects no game runs are said plainly.
    /// </summary>
    public sealed class OwnerLabelTests
    {
        [Fact]
        public void Another_players_thing_names_them_their_ping_and_how_far_they_are_from_it()
        {
            var label = OwnerLabel.Build("Boar", "D'Arcy", OwnerRole.Player, pingMs: 240.4, distanceM: 80.2);
            Assert.Equal("Boar", label.Thing);
            Assert.Equal("run by D'Arcy", label.Owner);
            Assert.Equal("ping 240 ms", label.Ping);
            Assert.Equal("80 m from it", label.Distance);
            Assert.Equal("run by D'Arcy · ping 240 ms · 80 m from it", label.Line);
        }

        [Fact]
        public void Your_own_thing_says_your_game_and_your_ping_but_no_distance()
        {
            var label = OwnerLabel.Build("Neck", "You", OwnerRole.You, 38, 3);
            Assert.Equal("run by your game · ping 38 ms", label.Line);
        }

        [Fact]
        public void The_server_and_nobody_are_said_plainly()
        {
            Assert.Equal("run by the server", OwnerLabel.Build("Troll", "The server", OwnerRole.Server, 12, double.NaN).Line);
            Assert.Equal("no game runs it", OwnerLabel.Build("Deer", "Nobody", OwnerRole.Nobody, double.NaN, double.NaN).Line);
        }

        [Fact]
        public void An_unknown_ping_or_distance_is_left_out()
        {
            Assert.Equal("run by D'Arcy", OwnerLabel.Build("Cart", "D'Arcy", OwnerRole.Player, double.NaN, double.NaN).Line);
            Assert.Equal("run by D'Arcy · 12 m from it", OwnerLabel.Build("Cart", "D'Arcy", OwnerRole.Player, double.NaN, 12).Line);
        }

        [Fact]
        public void A_slow_ping_is_amber_and_a_very_slow_one_red_as_on_the_HUD_line()
        {
            Assert.Equal(HudLine.Level.Fine, OwnerLabel.Build("Boar", "D'Arcy", OwnerRole.Player, 149, 5, pingLimitMs: 300).PingLevel);
            Assert.Equal(HudLine.Level.Warn, OwnerLabel.Build("Boar", "D'Arcy", OwnerRole.Player, 150, 5, pingLimitMs: 300).PingLevel);
            Assert.Equal(HudLine.Level.Bad, OwnerLabel.Build("Boar", "D'Arcy", OwnerRole.Player, 300, 5, pingLimitMs: 300).PingLevel);
            Assert.Equal(HudLine.Level.Fine, OwnerLabel.Build("Boar", "D'Arcy", OwnerRole.Player, 900, 5).PingLevel);
        }

        [Fact]
        public void A_thing_without_a_name_is_still_labelled()
        {
            Assert.Equal("something", OwnerLabel.Build("", "D'Arcy", OwnerRole.Player, 50, 5).Thing);
            Assert.Equal("something", OwnerLabel.Build(null, "D'Arcy", OwnerRole.Player, 50, 5).Thing);
        }
    }
}
