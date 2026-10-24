using System;
using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The patch on Jotunn's config sync: for every synced setting, Jotunn checks
    /// <c>config.Keys.Contains(...)</c>, and BepInEx's <c>Keys</c> copies every key of the file each time,
    /// so a sync of thousands of settings copies thousands of keys thousands of times. Inside Jotunn's
    /// sync, <c>Keys</c> is one snapshot of the file's keys per sync instead, answering every check the
    /// same way the copy would.
    /// </summary>
    public sealed class KeySnapshotTests
    {
        [Fact]
        public void A_snapshot_answers_every_check_as_the_copy_of_the_keys_would()
        {
            var keys = new[] { new Def("General", "Lock"), new Def("Piece 1", "Recipe"), new Def("Piece 1", "Enabled") };
            var snapshot = new KeySnapshot<Def>(keys);
            ICollection<Def> copy = keys.ToArray();

            foreach (var ask in new[] { new Def("General", "Lock"), new Def("general", "LOCK"), new Def("Piece 1", "Recipe"), new Def("Piece 2", "Recipe"), new Def("", "") })
            {
                Assert.Equal(copy.Contains(ask), snapshot.Contains(ask));
            }
        }

        [Fact]
        public void A_snapshot_lists_the_keys_in_the_order_copied_and_cannot_be_changed()
        {
            var keys = new[] { "c", "a", "b" };
            ICollection<string> snapshot = new KeySnapshot<string>(keys);

            Assert.Equal(new[] { "c", "a", "b" }, snapshot.ToArray());
            Assert.Equal(3, snapshot.Count);
            Assert.True(snapshot.IsReadOnly);
            var into = new string[4];
            snapshot.CopyTo(into, 1);
            Assert.Equal(new[] { null, "c", "a", "b" }, into);
            Assert.Throws<NotSupportedException>(() => snapshot.Add("d"));
            Assert.Throws<NotSupportedException>(() => snapshot.Remove("a"));
            Assert.Throws<NotSupportedException>(() => snapshot.Clear());
            keys[0] = "changed";
            Assert.Equal("c", snapshot.First());
        }

        [Fact]
        public void A_files_snapshot_is_made_once_per_sync_and_again_when_its_count_of_settings_changed()
        {
            var snapshots = new KeySnapshots<object, string>();
            var file = new object();
            int copies = 0;
            string[] Copy() { copies++; return new[] { "a", "b" }; }

            var first = snapshots.Get(file, 2, Copy);
            var second = snapshots.Get(file, 2, Copy);
            Assert.Same(first, second);
            Assert.Equal(1, copies);

            var third = snapshots.Get(file, 3, Copy);
            Assert.NotSame(first, third);
            Assert.Equal(2, copies);

            snapshots.Clear();
            snapshots.Get(file, 3, Copy);
            Assert.Equal(3, copies);
            Assert.NotSame(first, snapshots.Get(new object(), 2, Copy));
        }

        [Fact]
        public void The_first_checks_are_asked_both_ways_and_a_disagreement_brings_back_the_copy_for_good()
        {
            var agreement = new KeyCheckAgreement(2);
            // A type whose hash disagrees with its equality: the set misses what the copy finds.
            var keys = new[] { new Odd("a"), new Odd("b") };
            var snapshot = new KeySnapshot<Odd>(keys, agreement);

            // The snapshot's own Contains is what is under test, so its answers are taken as they come.
            bool a = snapshot.Contains(new Odd("a"));
            Assert.True(a);
            Assert.False(agreement.UseKey);
            Assert.Equal("the snapshot of the keys answered a check differently from the copy, so Jotunn's own way is back", agreement.Problem);
            bool b = snapshot.Contains(new Odd("b")), c = snapshot.Contains(new Odd("c"));
            Assert.True(b);
            Assert.False(c);
        }

        [Fact]
        public void Checks_that_agree_are_counted_and_comparing_stops_after_the_first_ones()
        {
            var agreement = new KeyCheckAgreement(2);
            var snapshot = new KeySnapshot<string>(new[] { "a", "b" }, agreement);

            snapshot.Contains("a");
            snapshot.Contains("x");
            Assert.False(agreement.Comparing);
            snapshot.Contains("b");
            Assert.True(agreement.UseKey);
            Assert.Equal(2, agreement.Agreed);
            Assert.Equal(3, agreement.Answered);
        }

        [Fact]
        public void The_state_says_off_not_needed_stepped_aside_waiting_or_working()
        {
            Assert.Equal("off: this game's switch (Patches.JotunnConfigSync)", KeyCheckAgreement.StateLine(false, true, "2.30.2", null, 0, 0));
            Assert.Equal("not needed: Jotunn is not installed", KeyCheckAgreement.StateLine(true, false, null, null, 0, 0));
            Assert.Equal("stepped aside: Jotunn's config sync was not found (an update changed it)",
                KeyCheckAgreement.StateLine(true, true, "2.31.0", "Jotunn's config sync was not found (an update changed it)", 0, 0));
            Assert.Equal("in · Jotunn 2.30.2: waiting for synced settings to arrive", KeyCheckAgreement.StateLine(true, true, "2.30.2", null, 0, 0));
            Assert.Equal("working · Jotunn 2.30.2: 2,178 checks answered from one snapshot of the keys per sync, the first 3 asked both ways with the same answer",
                KeyCheckAgreement.StateLine(true, true, "2.30.2", null, 2178, 3));
        }

        private sealed class Def : IEquatable<Def>
        {
            public Def(string section, string key)
            {
                Section = section;
                Key = key;
            }

            public string Section { get; }

            public string Key { get; }

            public bool Equals(Def other) => other != null && string.Equals(Section, other.Section, StringComparison.Ordinal) && string.Equals(Key, other.Key, StringComparison.Ordinal);

            public override bool Equals(object obj) => Equals(obj as Def);

            public override int GetHashCode() => (Section + "\u0001" + Key).GetHashCode(StringComparison.Ordinal);
        }

        private sealed class Odd : IEquatable<Odd>
        {
            private static int s_next;
            private readonly int _hash = ++s_next;

            public Odd(string name)
            {
                Name = name;
            }

            public string Name { get; }

            public bool Equals(Odd other) => other != null && other.Name == Name;

            public override bool Equals(object obj) => Equals(obj as Odd);

            public override int GetHashCode() => _hash;
        }
    }
}
