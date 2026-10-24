using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vigil.Core;
using Vigil.Startup;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Holding config saves during a start: BepInEx rewrites a mod's whole settings file each time it
    /// binds one setting, so a mod with a thousand settings writes its file a thousand times. While the
    /// hold is on, a save only marks the file; when the start is over each file is written once, and
    /// the files end up as they would have without the hold.
    /// </summary>
    public sealed class SaveHoldTests
    {
        // Paths of the machine the tests run on: CI runs them on Linux, where a backslash is no separator.
        private static readonly string ConfigDir = Path.Combine(Path.GetTempPath(), "BepInEx", "config");
        private static readonly string OdinPath = Cfg("Raelaziel.OdinArchitect.cfg");
        private static readonly string VigilPath = Cfg("isimp.Vigil.cfg");
        private static readonly string SpawnPath = Cfg("spawn_that.simple.cfg");

        private static string Cfg(string name) => Path.Combine(ConfigDir, name);

        [Fact]
        public void A_file_saved_many_times_during_the_start_is_written_once_when_it_ends()
        {
            var t = new Rig();
            var odin = t.File(OdinPath, "OdinArchitect");
            t.Hold.Begin();
            for (int i = 0; i < 1325; i++)
            {
                odin.Text = "setting " + i;
                t.Save(odin);
            }

            Assert.Equal(0, t.Disk.Writes);
            var result = t.Hold.Release("the menu showed");

            Assert.Equal(1, t.Disk.Writes);
            Assert.Equal("setting 1324", t.Disk.Read(OdinPath));
            Assert.Equal(1325, result.Held);
            Assert.Equal(1, result.Files);
            Assert.Equal(1, result.Written);
            Assert.Equal("OdinArchitect", result.Most[0].Key);
            Assert.Equal(1325, result.Most[0].Value);
        }

        [Fact]
        public void Saves_go_straight_to_disk_before_the_hold_and_after_it_ends()
        {
            var t = new Rig();
            var odin = t.File(OdinPath, "OdinArchitect");
            t.Save(odin);
            Assert.Equal(1, t.Disk.Writes);

            t.Hold.Begin();
            t.Save(odin);
            Assert.Equal(1, t.Disk.Writes);
            t.Hold.Release("the menu showed");
            Assert.Equal(2, t.Disk.Writes);

            odin.Text = "changed in game";
            t.Save(odin);
            Assert.Equal(3, t.Disk.Writes);
            Assert.Equal("changed in game", t.Disk.Read(OdinPath));
            Assert.False(t.Hold.Holding);
        }

        [Fact]
        public void Of_two_settings_objects_on_one_file_the_last_to_save_is_what_the_file_holds()
        {
            var t = new Rig();
            var first = t.File(VigilPath, "Vigil");
            var second = t.File(VigilPath, "Vigil");
            t.Hold.Begin();
            first.Text = "first";
            t.Save(first);
            second.Text = "second";
            t.Save(second);

            var result = t.Hold.Release("the menu showed");

            Assert.Equal("second", t.Disk.Read(VigilPath));
            Assert.Equal(1, t.Disk.Writes);
            Assert.Equal(1, result.Files);
        }

        [Fact]
        public void A_setting_changed_and_then_read_back_during_the_start_keeps_its_value()
        {
            var t = new Rig();
            t.Disk.Write(OdinPath, "from the last session");
            var odin = t.File(OdinPath, "OdinArchitect");
            t.Hold.Begin();
            odin.Text = "set during the start";
            t.Save(odin);

            t.Reload(odin);

            Assert.Equal("set during the start", odin.Text);
            var result = t.Hold.Release("the menu showed");
            Assert.Equal(1, result.Flushed);
            Assert.Equal(0, result.Written);
            Assert.Equal("set during the start", t.Disk.Read(OdinPath));
        }

        [Fact]
        public void A_file_another_mod_writes_after_its_last_held_save_stays_as_they_wrote_it()
        {
            var t = new Rig();
            t.Disk.Write(VigilPath, "the old file");
            var vigil = t.File(VigilPath, "Vigil");
            t.Hold.Begin();
            vigil.Text = "bound defaults";
            t.Save(vigil);

            t.Disk.Write(VigilPath, "settings taken over");
            t.Reload(vigil);

            Assert.Equal("settings taken over", vigil.Text);
            var result = t.Hold.Release("the menu showed");
            Assert.Equal("settings taken over", t.Disk.Read(VigilPath));
            Assert.Equal(new[] { "Vigil" }, result.Kept);
        }

        [Fact]
        public void A_file_written_from_outside_after_its_last_held_save_is_not_written_over_at_the_end()
        {
            var t = new Rig();
            var odin = t.File(OdinPath, "OdinArchitect");
            t.Hold.Begin();
            odin.Text = "held";
            t.Save(odin);
            t.Disk.Write(OdinPath, "a backup put back");

            var result = t.Hold.Release("the menu showed");

            Assert.Equal("a backup put back", t.Disk.Read(OdinPath));
            Assert.Equal(new[] { "OdinArchitect" }, result.Kept);
            Assert.Equal(0, result.Written);
        }

        [Fact]
        public void A_file_written_from_outside_before_the_last_held_save_is_saved_over_as_usual()
        {
            var t = new Rig();
            var odin = t.File(OdinPath, "OdinArchitect");
            t.Hold.Begin();
            t.Save(odin);
            t.Disk.Write(OdinPath, "written from outside");
            odin.Text = "saved after it";
            t.Save(odin);

            var result = t.Hold.Release("the menu showed");

            Assert.Equal("saved after it", t.Disk.Read(OdinPath));
            Assert.Empty(result.Kept);
            Assert.Equal(1, result.Written);
        }

        [Fact]
        public void A_file_that_cannot_be_written_is_named_and_the_others_are_still_written()
        {
            var t = new Rig();
            var broken = t.File(Cfg("locked.cfg"), "Locked");
            broken.Fails = new IOException("the file is in use");
            var odin = t.File(OdinPath, "OdinArchitect");
            t.Hold.Begin();
            t.Save(broken);
            t.Save(odin);

            var result = t.Hold.Release("the menu showed");

            Assert.Equal("text of OdinArchitect", t.Disk.Read(OdinPath));
            Assert.Equal(new[] { "Locked: the file is in use" }, result.Failed);
            Assert.Equal(1, result.Written);
        }

        [Fact]
        public void A_hold_still_on_ten_minutes_after_it_began_ends_at_the_next_save()
        {
            var t = new Rig();
            var odin = t.File(OdinPath, "OdinArchitect");
            var vigil = t.File(VigilPath, "Vigil");
            t.Hold.Begin();
            t.Save(odin);
            t.Now = t.Now.AddMinutes(10).AddSeconds(1);

            t.Save(vigil);

            Assert.False(t.Hold.Holding);
            Assert.Equal(2, t.Disk.Writes);
            Assert.Equal("10 min passed", t.Hold.Result.Why);
        }

        [Fact]
        public void A_second_end_writes_nothing_more_and_keeps_the_first_result()
        {
            var t = new Rig();
            var odin = t.File(OdinPath, "OdinArchitect");
            t.Hold.Begin();
            t.Save(odin);
            var first = t.Hold.Release("the menu showed");

            var again = t.Hold.Release("the game quit");

            Assert.Same(first, again);
            Assert.Equal(1, t.Disk.Writes);
        }

        [Fact]
        public void A_held_file_a_mod_reads_past_BepInEx_is_written_first_and_the_mod_is_named()
        {
            var t = new Rig();
            t.Disk.Write(SpawnPath, "last session");
            var spawn = t.File(SpawnPath, "Spawn That");
            t.Hold.Begin();
            spawn.Text = "with this start's new defaults";
            t.Save(spawn);

            string seen = t.ReadPast(SpawnPath, "Spawn That");

            Assert.Equal("with this start's new defaults", seen);
            var result = t.Hold.Release("the menu showed");
            Assert.Equal(new[] { "Spawn That: spawn_that.simple.cfg" }, result.ReadPast);
            Assert.Equal(1, result.Flushed);
        }

        [Fact]
        public void A_check_with_no_mod_on_the_stack_leaves_the_file_held()
        {
            // Mono's file watcher looks for each watched file every 750 ms on a thread of its own: no mod is reading it.
            var t = new Rig();
            var bags = t.File(Cfg("vapok.mods.adventurebackpacks.cfg"), "Adventure Backpacks");
            t.Hold.Begin();
            t.Save(bags);

            t.ReadPast(bags.Path, null);

            Assert.Equal(0, t.Disk.Writes);
            Assert.True(t.Hold.IsWaiting(bags.Path));
            var result = t.Hold.Release("the menu showed");
            Assert.Empty(result.ReadPast);
            Assert.Equal(1, result.Written);
        }

        [Fact]
        public void A_file_with_no_held_save_is_read_as_it_is_and_nobody_is_named()
        {
            var t = new Rig();
            t.Disk.Write(SpawnPath, "last session");
            var odin = t.File(OdinPath, "OdinArchitect");
            t.Hold.Begin();
            t.Save(odin);

            Assert.False(t.Hold.Holds("spawn_that.simple.cfg"));
            Assert.Equal("last session", t.ReadPast(SpawnPath, "Spawn That"));
            Assert.Equal(1, t.Disk.Writes);
            Assert.Empty(t.Hold.Release("the menu showed").ReadPast);
        }

        [Fact]
        public void A_mod_reading_a_held_file_again_after_another_save_is_named_once()
        {
            var t = new Rig();
            var spawn = t.File(SpawnPath, "Spawn That");
            t.Hold.Begin();
            t.Save(spawn);
            t.ReadPast(SpawnPath, "Spawn That");
            spawn.Text = "changed again";
            t.Save(spawn);

            Assert.True(t.Hold.IsWaiting(SpawnPath));
            Assert.Equal("changed again", t.ReadPast(SpawnPath, "Spawn That"));
            Assert.False(t.Hold.IsWaiting(SpawnPath));
            Assert.Single(t.Hold.Release("the menu showed").ReadPast);
        }

        [Fact]
        public void A_held_file_written_from_outside_after_its_last_save_is_read_as_written_and_nobody_is_named()
        {
            var t = new Rig();
            var spawn = t.File(SpawnPath, "Spawn That");
            t.Hold.Begin();
            t.Save(spawn);
            t.Disk.Write(SpawnPath, "written by the mod itself");

            Assert.Equal("written by the mod itself", t.ReadPast(SpawnPath, "Spawn That"));
            var result = t.Hold.Release("the menu showed");
            Assert.Empty(result.ReadPast);
            Assert.Equal(new[] { "Spawn That" }, result.Kept);
        }

        [Fact]
        public void After_the_hold_a_read_past_BepInEx_writes_nothing()
        {
            var t = new Rig();
            var spawn = t.File(SpawnPath, "Spawn That");
            t.Hold.Begin();
            t.Save(spawn);
            t.Hold.Release("the menu showed");
            spawn.Text = "changed in game";

            t.ReadPast(SpawnPath, "Spawn That");

            Assert.Equal(1, t.Disk.Writes);
            Assert.False(t.Hold.Holds("spawn_that.simple.cfg"));
        }

        [Fact]
        public void The_report_names_the_mods_that_read_a_held_file_past_BepInEx()
        {
            var r = new SaveHoldResult("the menu showed", 3, 2, 2, 1, 12);
            r.ReadPast.Add("Spawn That: spawn_that.simple.cfg");

            Assert.Equal("Config saves: 1 file read past BepInEx, so written first: Spawn That: spawn_that.simple.cfg", r.Lines()[1]);
            Assert.Equal(new[] { "Spawn That: spawn_that.simple.cfg" }, SaveHoldResult.Parse(r.EventText()).ReadPast);
        }

        [Fact]
        public void The_report_line_says_how_many_saves_were_held_and_which_files_saved_most()
        {
            var t = new Rig();
            var odin = t.File(OdinPath, "OdinArchitect");
            var bags = t.File(Cfg("vapok.mods.adventurebackpacks.cfg"), "Adventure Backpacks");
            t.Hold.Begin();
            for (int i = 0; i < 1325; i++) t.Save(odin);
            for (int i = 0; i < 212; i++) t.Save(bags);
            var result = t.Hold.Release("the menu showed");

            var lines = result.Lines();

            Assert.Single(lines);
            Assert.StartsWith("Config saves: 1,537 held for 2 files, each written once when the menu showed (", lines[0]);
            Assert.EndsWith("); the most: OdinArchitect 1,325, Adventure Backpacks 212", lines[0]);
        }

        [Fact]
        public void Files_kept_or_not_written_get_a_second_line()
        {
            var r = new SaveHoldResult("the menu showed", 3, 3, 1, 0, 12);
            r.Kept.Add("Vigil");
            r.Failed.Add("Locked: the file is in use");

            var lines = r.Lines();

            Assert.Equal(2, lines.Count);
            Assert.Equal("Config saves: 1 file kept as another mod wrote it (Vigil); 1 file not written: Locked: the file is in use", lines[1]);
        }

        [Fact]
        public void The_result_comes_back_whole_from_its_startup_step()
        {
            var r = new SaveHoldResult("the menu showed", 2431, 141, 139, 2, 612.5);
            r.Most.Add(new KeyValuePair<string, int>("Odin|Architect", 1325));
            r.Most.Add(new KeyValuePair<string, int>("Adventure Backpacks", 212));
            r.Kept.Add("Vigil");
            r.Failed.Add("Locked: in use; twice");

            var back = SaveHoldResult.Parse(r.EventText());

            Assert.Equal("the menu showed", back.Why);
            Assert.Equal(new[] { 2431, 141, 139, 2 }, new[] { back.Held, back.Files, back.Written, back.Flushed });
            Assert.Equal(612.5, back.WriteMs, 1);
            Assert.Equal(new[] { "Odin Architect", "Adventure Backpacks" }, back.Most.Select(m => m.Key).ToArray());
            Assert.Equal(1325, back.Most[0].Value);
            Assert.Equal(new[] { "Vigil" }, back.Kept);
            Assert.Equal(new[] { "Locked: in use  twice" }, back.Failed);
            Assert.Null(SaveHoldResult.Parse("not a result"));
        }

        // A settings file as the hook sees it: its path, its text in memory, and BepInEx's own save.
        private sealed class FakeConfig
        {
            public string Path;
            public string Label;
            public string Text;
            public Exception Fails;
        }

        private sealed class FakeDisk
        {
            private readonly Dictionary<string, string> _files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _versions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public int Writes { get; private set; }

            public void Write(string path, string text)
            {
                _files[path] = text;
                _versions[path] = (_versions.TryGetValue(path, out int v) ? v : 0) + 1;
                Writes++;
            }

            public string Read(string path) => _files.TryGetValue(path, out var text) ? text : null;

            public string Stamp(string path) => _versions.TryGetValue(path, out int v) ? "v" + v : "";
        }

        // The hold with a stand-in disk and clock, driven the way the hook drives it.
        private sealed class Rig
        {
            public readonly FakeDisk Disk = new FakeDisk();
            public DateTime Now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
            public readonly SaveHold Hold;

            public Rig()
            {
                Hold = new SaveHold(Disk.Stamp, f => Write((FakeConfig)f), () => Now);
            }

            public FakeConfig File(string path, string label) => new FakeConfig { Path = path, Label = label, Text = "text of " + label };

            private void Write(FakeConfig c)
            {
                if (c.Fails != null) throw c.Fails;
                Disk.Write(c.Path, c.Text);
            }

            /// <summary>BepInEx's Save with the hook's prefix: written now unless the hold takes it.</summary>
            public void Save(FakeConfig c)
            {
                if (!Hold.Save(c, c.Path, c.Label)) Write(c);
            }

            /// <summary>BepInEx's Reload with the hook's prefix: what would be on disk is there first, then it is read.</summary>
            public void Reload(FakeConfig c)
            {
                Hold.BeforeRead(c.Path);
                c.Text = Disk.Read(c.Path);
            }

            /// <summary>A mod opening a settings file itself, with the guard's prefix: what it reads from disk.</summary>
            public string ReadPast(string path, string who)
            {
                if (Hold.Holds(System.IO.Path.GetFileName(path)) && Hold.IsWaiting(path)) Hold.ReadPast(path, who);
                return Disk.Read(path);
            }
        }
    }
}
