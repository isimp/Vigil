using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// For a capture of what this game sends, each kind of world object shows how often it was sent,
    /// how big it was each time, and how much of that the game's own values and each mod's values take
    /// (a value counts for whoever's code holds its name; unnamed values are unknown); and which values,
    /// by changing, made objects send again, the game's movement included. A value costs its 4-byte name
    /// and its value as the game writes it.
    /// </summary>
    public sealed class ObjectDataTests
    {
        private static KeyValuePair<string, int> P(string owner, int bytes) => new KeyValuePair<string, int>(owner, bytes);

        [Fact]
        public void A_value_costs_its_name_and_its_value_as_the_game_writes_it()
        {
            Assert.Equal(8, ValueSize.Float);
            Assert.Equal(16, ValueSize.Vec3);
            Assert.Equal(20, ValueSize.Quat);
            Assert.Equal(8, ValueSize.Int);
            Assert.Equal(12, ValueSize.Long);
            Assert.Equal(4 + 1 + 5, ValueSize.String("hello"));
            Assert.Equal(4 + 1 + 7, ValueSize.String("grüße"));
            Assert.Equal(4 + 2 + 200, ValueSize.String(new string('a', 200)));
            Assert.Equal(4 + 4 + 10, ValueSize.Bytes(new byte[10]));
            Assert.Equal(4 + 4, ValueSize.Bytes(null));
        }

        private static ObjectDataView Wolves()
        {
            var t = new ObjectDataTally();
            for (int i = 0; i < 60; i++)
            {
                long id = i % 3;
                t.Add("Wolf", id, 246, new[] { P("Spawn That", 26), P("Seasonality", 14), P(ObjectDataTally.Unknown, 8) });
            }
            for (int i = 0; i < 10; i++) t.Add("VikingShip", 7, 313, new[] { P("Longship Upgrades", 112) });
            for (int i = 0; i < 20; i++) t.Add("Fish2", 100 + i, 94, new KeyValuePair<string, int>[0]);
            for (int i = 0; i < 30; i++) t.Changed("Wolf", "position", ObjectDataTally.Game);
            for (int i = 0; i < 10; i++) t.Changed("Boar", "position", ObjectDataTally.Game);
            for (int i = 0; i < 3; i++) t.Changed("Wolf", "seasonal_coat", "Seasonality");
            return t.View(10);
        }

        [Fact]
        public void Each_kind_shows_its_sends_size_and_who_takes_how_much_of_it_the_most_bytes_first()
        {
            var v = Wolves();
            Assert.Equal(new[] { "Wolf", "VikingShip", "Fish2" }, v.Prefabs.Select(p => p.Prefab));
            var wolf = v.Prefabs[0];
            Assert.Equal(3, wolf.Objects);
            Assert.Equal(6.0, wolf.SendsPerSecond, 6);
            Assert.Equal(246, wolf.BytesEach, 6);
            Assert.Equal(198, wolf.GameEach, 6);
            Assert.Equal("Wolf ×3", ObjectDataView.ObjectLabel(wolf));
            Assert.Equal("Spawn That 26 B · Seasonality 14 B · unknown 8 B", ObjectDataView.ModsLine(wolf));
            Assert.Equal("none", ObjectDataView.ModsLine(v.Prefabs[2]));
            Assert.Equal(201, v.Prefabs[1].GameEach, 6);
        }

        [Fact]
        public void Values_that_changed_say_whose_they_are_on_what_and_how_often_the_most_first()
        {
            var v = Wolves();
            var first = v.Changes[0];
            Assert.Equal("position", first.Value);
            Assert.Equal(ObjectDataTally.Game, first.Whose);
            Assert.Equal("Wolf, Boar", first.On);
            Assert.Equal(4.0, first.PerSecond, 6);
            Assert.Equal("seasonal_coat", v.Changes[1].Value);
            Assert.Equal("Seasonality", v.Changes[1].Whose);
            Assert.Equal(0.3, v.Changes[1].PerSecond, 6);
        }

        [Fact]
        public void A_mod_share_that_is_not_there_on_every_send_is_averaged_over_all_sends()
        {
            var t = new ObjectDataTally();
            t.Add("Boar", 1, 200, new[] { P("Spawn That", 20) });
            t.Add("Boar", 1, 180, new KeyValuePair<string, int>[0]);
            var boar = t.View(1).Prefabs[0];
            Assert.Equal(190, boar.BytesEach, 6);
            Assert.Equal("Spawn That 10 B", ObjectDataView.ModsLine(boar));
            Assert.Equal(180, boar.GameEach, 6);
        }

        [Fact]
        public void Only_the_kinds_and_changes_asked_for_are_listed()
        {
            var t = new ObjectDataTally();
            for (int k = 0; k < 12; k++) t.Add("Kind" + k, k, 100 + k, new KeyValuePair<string, int>[0]);
            for (int k = 0; k < 12; k++) t.Changed("Kind" + k, "v" + k, ObjectDataTally.Game);
            var v = t.View(1, prefabs: 8, changes: 10);
            Assert.Equal(8, v.Prefabs.Count);
            Assert.Equal(10, v.Changes.Count);
            Assert.Equal(12, v.Kinds);
        }

        [Fact]
        public void An_empty_capture_lists_nothing()
        {
            var v = new ObjectDataTally().View(60);
            Assert.Empty(v.Prefabs);
            Assert.Empty(v.Changes);
            Assert.Equal(0, v.Sends);
        }
    }
}
