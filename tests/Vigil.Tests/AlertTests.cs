using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// An admin hears about trouble on the server without asking: a player dropping, a connection
    /// struggling, the server falling behind or short of memory, its log flooding, spawns inside
    /// bases, a shared report, a crash. The server keeps them a week for an admin who joins later;
    /// the admin's game sums them up per kind, counts the new ones in the HUD line and shows the
    /// kinds set "On screen" as a line at the top left.
    /// </summary>
    public sealed class AlertTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 14, 22, 0, 0, DateTimeKind.Utc);

        private static Alert Incident(string kind, string detail, string who = null, int minutesAgo = 0) =>
            AlertSources.FromIncident(kind, detail, "Vigil-incident-215000-" + kind + ".txt", who, Now.AddMinutes(-minutesAgo));

        private static AlertShow Defaults(AlertKind kind) => AlertKinds.Default(kind);

        private static Alert Make(AlertKind kind, string text, int minutesAgo, string subject = null) =>
            new Alert { Kind = kind, Level = AlertKinds.Level(kind), Text = text, Subject = subject, Utc = Now.AddMinutes(-minutesAgo) };

        private static AlertBook Book(params Alert[] alerts)
        {
            var book = new AlertBook();
            foreach (var a in alerts) book.Add(a);
            return book;
        }

        // ------------------------------------------------------------------ what becomes an alert

        [Fact]
        public void A_player_dropping_without_a_goodbye_is_a_bad_alert_naming_them_with_its_report()
        {
            var a = Incident("drop", "D'Arcy's connection died: nothing arrived for 31 s, all at once", "D'Arcy");
            Assert.Equal(AlertKind.Drop, a.Kind);
            Assert.Equal(AlertLevel.Bad, a.Level);
            Assert.Equal("D'Arcy", a.Subject);
            Assert.Equal("D'Arcy's connection died: nothing arrived for 31 s, all at once", a.Text);
            Assert.Equal("Vigil-incident-215000-drop.txt", a.File);
        }

        [Fact]
        public void A_long_server_frame_alerts_only_from_one_second_on()
        {
            Assert.Null(Incident("hitch", "a 997 ms frame (limit 250 ms)"));
            var a = Incident("hitch", "a 1062 ms frame (limit 250 ms)");
            Assert.Equal(AlertKind.Behind, a.Kind);
            Assert.Equal(AlertLevel.Warn, a.Level);
            Assert.Equal("The server stood still for 1.1 s", a.Text);
        }

        [Fact]
        public void A_slow_stretch_on_the_server_is_an_alert_saying_how_slow()
        {
            var a = Incident("slow", "under 20 fps for 5 seconds in a row (now 14.2 fps)");
            Assert.Equal(AlertKind.Behind, a.Kind);
            Assert.Equal("The server ran under 20 fps for 5 seconds in a row (now 14.2 fps)", a.Text);
        }

        [Fact]
        public void Memory_pressure_on_the_server_is_a_bad_alert_without_the_report_wording()
        {
            var a = Incident("memory", "machine memory 96% full (310 MB free); reported once until it has eased for a minute");
            Assert.Equal(AlertKind.Memory, a.Kind);
            Assert.Equal(AlertLevel.Bad, a.Level);
            Assert.Equal("The server's machine memory 96% full (310 MB free)", a.Text);
        }

        [Fact]
        public void Connection_trouble_names_the_player_without_the_limit_wording()
        {
            Assert.Equal("D'Arcy has a ping of 450 ms", Incident("ping", "D'Arcy has a ping of 450 ms").Text);
            var queue = Incident("send-queue", "123.4 kB waiting to be sent to D'Arcy (limit 64 kB, above the world-update batch the game keeps queued on purpose); reported once until it has eased for a minute");
            Assert.Equal(AlertKind.Connection, queue.Kind);
            Assert.Equal(AlertLevel.Warn, queue.Level);
            Assert.Equal("123.4 kB waiting to be sent to D'Arcy", queue.Text);
            Assert.Equal("Nothing received from D'Arcy for 5000 ms", Incident("silent", "nothing received from D'Arcy for 5000 ms (limit 3000 ms)").Text);
        }

        [Fact]
        public void Connection_alerts_are_about_the_player_they_name()
        {
            Assert.Equal("D'Arcy", Incident("ping", "D'Arcy has a ping of 450 ms").Subject);
            Assert.Equal("Old Bjorn", Incident("send-queue", "64.8 kB waiting to be sent to Old Bjorn (limit 64 kB); reported once until it has eased for a minute").Subject);
            Assert.Equal("D'Arcy", Incident("silent", "nothing received from D'Arcy for 2469 ms (limit 1500 ms)").Subject);
        }

        [Fact]
        public void A_player_going_silent_alerts_once_not_every_second_and_again_after_ten_minutes()
        {
            var book = new AlertBook();
            int kept = 0;
            for (int s = 0; s < 30; s++)
                if (book.Add(AlertSources.FromIncident("silent", "nothing received from D'Arcy for " + (2469 + s * 1000) + " ms (limit 1500 ms)", null, null, Now.AddSeconds(s)))) kept++;
            Assert.Equal(1, kept);
            Assert.True(book.Add(AlertSources.FromIncident("silent", "nothing received from Bjorn for 1600 ms (limit 1500 ms)", null, null, Now.AddSeconds(31))));
            Assert.False(book.Add(AlertSources.FromIncident("send-queue", "64.8 kB waiting to be sent to D'Arcy (limit 64 kB)", null, null, Now.AddMinutes(5))));
            Assert.True(book.Add(AlertSources.FromIncident("silent", "nothing received from D'Arcy for 1600 ms (limit 1500 ms)", null, null, Now.AddMinutes(11))));
        }

        [Fact]
        public void A_flood_in_the_server_s_log_is_an_alert_naming_the_line_repeated()
        {
            var a = Incident("log-flood", "260 log lines in one second, most repeated: Unity Log: Destroying abandoned zdo x143");
            Assert.Equal(AlertKind.LogFlood, a.Kind);
            Assert.Equal("The server's log: 260 log lines in one second, most repeated: Unity Log: Destroying abandoned zdo x143", a.Text);
        }

        [Fact]
        public void A_long_line_is_cut_so_an_answer_of_alerts_stays_small()
        {
            var a = Incident("log-flood", "300 log lines in one second, most repeated: " + new string('x', 1000));
            Assert.Equal(AlertSources.MaxText, a.Text.Length);
            Assert.EndsWith("...", a.Text);
            Assert.True(AlertBook.SentAtOnce * AlertSources.MaxText <= 32 * 1024);
        }

        [Fact]
        public void Hand_marks_self_tests_and_a_player_s_own_ping_are_no_alerts()
        {
            Assert.Null(Incident("manual", "marked by hand"));
            Assert.Null(Incident("selftest", "self-test"));
            Assert.Null(Incident("ping", "ping 300 ms (limit 250 ms)"));
        }

        [Fact]
        public void Spawns_inside_a_base_become_one_alert_per_place_naming_the_creatures()
        {
            SharedSpawn Spawn(string creature, string piece, bool inside = true, bool cancelled = false) => new SharedSpawn
            {
                Player = "D'Arcy", Utc = Now,
                Record = new SpawnRecord { Creature = creature, InsideBase = inside, Cancelled = cancelled, BasePiece = piece, BaseBuilderId = 7 },
            };
            var spawns = new[]
            {
                Spawn("Greyling", "Longhouse"), Spawn("Neck", "Longhouse"), Spawn("Greyling", "Longhouse"),
                Spawn("Troll", "Tower"), Spawn("Boar", null, inside: false), Spawn("Draugr", "Tower", cancelled: true),
            };
            var alerts = AlertSources.FromSpawns(spawns, id => id == 7 ? "Bjorn" : null, Now);
            Assert.Equal(2, alerts.Count);
            Assert.All(alerts, a => Assert.Equal(AlertKind.BaseSpawns, a.Kind));
            Assert.Equal("Greyling x2, Neck spawned inside Longhouse by Bjorn", alerts[0].Text);
            Assert.Equal("Longhouse by Bjorn", alerts[0].Subject);
            Assert.Equal("Troll spawned inside Tower by Bjorn", alerts[1].Text);
        }

        [Fact]
        public void A_shared_report_is_an_alert_naming_the_player_and_what_they_shared()
        {
            var a = AlertSources.FromShared("shared/Bjorn/Vigil-incident-211400-manual.txt", Now);
            Assert.Equal(AlertKind.Shared, a.Kind);
            Assert.Equal(AlertLevel.Info, a.Level);
            Assert.Equal("Bjorn shared a lag report", a.Text);
            Assert.Equal("shared/Bjorn/Vigil-incident-211400-manual.txt", a.File);
            Assert.Equal("Bjorn shared an environment report", AlertSources.FromShared("shared/Bjorn/Vigil-environment-211400.txt", Now).Text);
        }

        [Fact]
        public void Reports_a_player_s_game_shares_on_its_own_are_no_alerts()
        {
            foreach (var kind in new[] { "drop", "hitch", "memory", "flood", "slow", "silent", "queue", "ping" })
                Assert.Null(AlertSources.FromShared("shared/Bjorn/Vigil-incident-211400-" + kind + ".txt", Now));
        }

        [Fact]
        public void A_server_run_that_ended_without_a_shutdown_is_a_bad_alert_with_its_last_sign_of_life()
        {
            var started = new DateTime(2026, 10, 14, 4, 12, 0);
            var a = AlertSources.FromCrash("2026-10-14 02:10:05", new DateTime(2026, 10, 14, 3, 58, 30), started, Now);
            Assert.Equal(AlertKind.Crash, a.Kind);
            Assert.Equal(AlertLevel.Bad, a.Level);
            Assert.Equal("The server's run from 02:10 ended without a shutdown (last sign of life 03:58); it started again at 04:12", a.Text);
            var unknown = AlertSources.FromCrash(null, null, started, Now);
            Assert.Equal("The server's last run ended without a shutdown; it started again at 04:12", unknown.Text);
        }

        [Fact]
        public void The_previous_run_s_start_and_last_sign_of_life_are_read_before_the_new_run_overwrites_them()
        {
            string dir = Path.Combine(Path.GetTempPath(), "VigilAlerts-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var first = new InstrumentJournal(dir)) Assert.Null(first.PreviousStarted);
                string running = Path.Combine(dir, "session.running");
                string started = File.ReadAllText(running);
                var lastSeen = new DateTime(2026, 10, 14, 3, 58, 30);
                File.SetLastWriteTime(running, lastSeen);
                using (var second = new InstrumentJournal(dir))
                {
                    Assert.False(second.PreviousEndedCleanly);
                    Assert.Equal(started, second.PreviousStarted);
                    Assert.Equal(lastSeen, second.PreviousLastSeen);
                    File.SetLastWriteTime(running, lastSeen);
                    second.Heartbeat();
                    Assert.True(File.GetLastWriteTime(running) > lastSeen);
                    second.MarkCleanExit();
                }
                using (var third = new InstrumentJournal(dir))
                {
                    Assert.True(third.PreviousEndedCleanly);
                    Assert.Null(third.PreviousStarted);
                    Assert.Null(third.PreviousLastSeen);
                }
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
        }

        // ------------------------------------------------------------------ the server's book

        [Fact]
        public void Alerts_get_rising_ids_even_within_one_millisecond()
        {
            var book = Book(Make(AlertKind.Drop, "a", 0), Make(AlertKind.Drop, "b", 0), Make(AlertKind.Memory, "c", 0));
            var ids = book.All.Select(a => a.Id).ToList();
            Assert.Equal(ids.OrderBy(i => i).Distinct(), ids);
            Assert.Equal(new DateTimeOffset(Now).ToUnixTimeMilliseconds(), ids[0]);
        }

        [Fact]
        public void The_server_keeps_a_week_of_alerts_and_at_most_500()
        {
            var book = new AlertBook();
            book.Add(Make(AlertKind.Drop, "old", 60 * 24 * 8));
            book.Add(Make(AlertKind.Drop, "recent", 60 * 24 * 6));
            Assert.Equal(2, book.All.Count);
            book.Add(Make(AlertKind.Drop, "now", 0));
            Assert.Equal(new[] { "recent", "now" }, book.All.Select(a => a.Text));
            for (int i = 0; i < 600; i++) book.Add(Make(AlertKind.Drop, "n" + i, 0));
            Assert.Equal(AlertBook.Kept, book.All.Count);
            Assert.Equal("n599", book.All.Last().Text);
        }

        [Fact]
        public void The_server_falling_behind_alerts_at_most_once_in_ten_minutes()
        {
            var book = new AlertBook();
            Assert.True(book.Add(Make(AlertKind.Behind, "first", 30)));
            Assert.False(book.Add(Make(AlertKind.Behind, "again", 25)));
            Assert.True(book.Add(Make(AlertKind.Behind, "later", 19)));
            Assert.True(book.Add(Make(AlertKind.Drop, "drops always", 19)));
            Assert.True(book.Add(Make(AlertKind.Drop, "drops always", 19)));
        }

        [Fact]
        public void Spawns_at_one_base_alert_once_an_hour_but_another_base_alerts_at_once()
        {
            var book = new AlertBook();
            Assert.True(book.Add(Make(AlertKind.BaseSpawns, "x", 50, "Longhouse by Bjorn")));
            Assert.False(book.Add(Make(AlertKind.BaseSpawns, "y", 20, "Longhouse by Bjorn")));
            Assert.True(book.Add(Make(AlertKind.BaseSpawns, "z", 20, "Tower by D'Arcy")));
            Assert.True(book.Add(Make(AlertKind.BaseSpawns, "w", -15, "Longhouse by Bjorn")));
        }

        [Fact]
        public void An_admin_joining_later_gets_what_happened_since_they_last_heard_the_newest_when_too_many()
        {
            var book = Book(Make(AlertKind.Drop, "a", 30), Make(AlertKind.Drop, "b", 20), Make(AlertKind.Drop, "c", 10));
            long heard = book.All[0].Id;
            Assert.Equal(new[] { "b", "c" }, book.Since(heard).Select(a => a.Text));
            Assert.Equal(new[] { "a", "b", "c" }, book.Since(0).Select(a => a.Text));
            Assert.Equal(new[] { "c" }, book.Since(0, 1).Select(a => a.Text));
        }

        [Fact]
        public void Alerts_outlast_a_restart_through_their_file()
        {
            var a = Make(AlertKind.Drop, "D'Arcy's connection died:\tnothing\r\narrived", 5, "D'Arcy");
            a.File = "Vigil-incident-215000-drop.txt";
            var book = Book(a, Make(AlertKind.Crash, "crash", 60 * 24 * 8));
            var lines = book.All.Select(AlertBook.Line).ToList();
            var again = new AlertBook();
            again.Load(lines.Concat(new[] { "garbage", "" }), Now);
            var read = again.All.Single();
            Assert.Equal(book.All.First(x => x.Kind == AlertKind.Drop).Id, read.Id);
            Assert.Equal(AlertKind.Drop, read.Kind);
            Assert.Equal(AlertLevel.Bad, read.Level);
            Assert.Equal("D'Arcy", read.Subject);
            Assert.Equal("D'Arcy's connection died: nothing  arrived", read.Text);
            Assert.Equal("Vigil-incident-215000-drop.txt", read.File);
            Assert.Equal(a.Utc, read.Utc);
            again.Add(Make(AlertKind.Drop, "next", 0));
            Assert.True(again.All.Last().Id > read.Id);
        }

        [Fact]
        public void Alerts_reach_the_admin_s_game_intact()
        {
            var a = Make(AlertKind.Shared, "Bjorn shared a lag report", 3, "Bjorn");
            a.File = "shared/Bjorn/Vigil-incident-211400-manual.txt";
            var book = Book(a, Make(AlertKind.Drop, "x; y = z", 1));
            var d = KeyValues.Decode(KeyValues.Encode(AlertBook.Encode(book.All)));
            var got = AlertBook.Decode(d);
            Assert.Equal(2, got.Count);
            Assert.Equal(book.All[0].Id, got[0].Id);
            Assert.Equal(AlertKind.Shared, got[0].Kind);
            Assert.Equal(AlertLevel.Info, got[0].Level);
            Assert.Equal("Bjorn", got[0].Subject);
            Assert.Equal(a.File, got[0].File);
            Assert.Equal(a.Utc, got[0].Utc);
            Assert.Equal("x, y : z", got[1].Text);
            Assert.Null(got[1].File);
        }

        [Fact]
        public void A_report_button_finds_the_incident_after_a_restart_moved_it()
        {
            var here = new HashSet<string> { "Vigil-incident-1.txt", "Vigil-runs/2026-10-14_16-07-42/Vigil-incident-2.txt", "shared/Bjorn/Vigil-environment-1.txt" };
            var runs = new[] { "2026-10-14_16-07-42", "2026-10-14_15-41-38" };
            Assert.Equal("Vigil-incident-1.txt", AlertSources.ReportPath("Vigil-incident-1.txt", here.Contains, runs));
            Assert.Equal("Vigil-runs/2026-10-14_16-07-42/Vigil-incident-2.txt", AlertSources.ReportPath("Vigil-incident-2.txt", here.Contains, runs));
            Assert.Equal("shared/Bjorn/Vigil-environment-1.txt", AlertSources.ReportPath("shared/Bjorn/Vigil-environment-1.txt", here.Contains, runs));
            Assert.Null(AlertSources.ReportPath("Vigil-incident-3.txt", here.Contains, runs));
            Assert.Null(AlertSources.ReportPath(null, here.Contains, runs));
        }

        // ------------------------------------------------------------------ the admin's card

        private static AlertView View(IEnumerable<Alert> alerts, long cleared = 0, long opened = 0, Func<AlertKind, AlertShow> show = null, DateTime? keptFrom = null) =>
            AlertView.Build(alerts, cleared, opened, keptFrom, show ?? Defaults, Now, TimeSpan.Zero);

        [Fact]
        public void With_nothing_to_report_the_card_says_since_when()
        {
            var v = View(new Alert[0], keptFrom: Now.AddHours(-19).AddMinutes(-50));
            Assert.True(v.Calm);
            Assert.Equal("Nothing since 02:10", v.Verdict);
            Assert.Empty(v.Groups);
            Assert.Equal(0, v.New);
            Assert.Equal("since 02:10, 7 days kept", v.Since);
        }

        [Fact]
        public void The_card_leads_with_the_worst_kind_and_says_how_often()
        {
            var book = Book(Make(AlertKind.Connection, "D'Arcy: 1.2 MB waiting", 46), Make(AlertKind.Drop, "D'Arcy dropped, line silent 31 s", 45, "D'Arcy"),
                Make(AlertKind.Drop, "D'Arcy dropped, line silent 30 s", 20, "D'Arcy"), Make(AlertKind.Behind, "The server stood still for 1.1 s", 5));
            var v = View(book.All);
            Assert.False(v.Calm);
            Assert.Equal(AlertLevel.Bad, v.VerdictLevel);
            Assert.Equal("Players dropped, 2 since 21:15. Last: D'Arcy dropped, line silent 30 s", v.Verdict);
            Assert.Equal(new[] { AlertKind.Drop, AlertKind.Behind, AlertKind.Connection }, v.Groups.Select(g => g.Kind));
            var drops = v.Groups[0];
            Assert.Equal(2, drops.Count);
            Assert.Equal("21:40", drops.Last);
            Assert.Equal(new[] { "D'Arcy dropped, line silent 30 s", "D'Arcy dropped, line silent 31 s" }, drops.Entries.Select(a => a.Text));
            Assert.Equal("The server stood still for 1.1 s", View(new[] { book.All[3] }).Verdict);
        }

        [Fact]
        public void Kinds_without_alerts_are_summed_up_in_one_line_and_kinds_switched_off_left_out()
        {
            var book = Book(Make(AlertKind.Drop, "d", 5), Make(AlertKind.LogFlood, "f", 4));
            AlertShow Show(AlertKind k) => k == AlertKind.LogFlood || k == AlertKind.Crash ? AlertShow.Off : Defaults(k);
            var v = View(book.All, show: Show);
            Assert.Equal(new[] { AlertKind.Drop }, v.Groups.Select(g => g.Kind));
            Assert.Equal("Connections, falling behind, memory, spawns in bases and shared reports: none", v.Quiet);
            Assert.Equal(1, v.New);
        }

        [Fact]
        public void Seen_clears_the_card_but_alerts_after_it_still_show()
        {
            var book = Book(Make(AlertKind.Drop, "before", 30), Make(AlertKind.Memory, "after", 10));
            var v = View(book.All, cleared: book.All[0].Id);
            Assert.Equal(new[] { "after" }, v.Groups.SelectMany(g => g.Entries).Select(a => a.Text));
            Assert.Equal("since 21:30, 7 days kept", v.Since);
            Assert.True(View(book.All, cleared: book.All[1].Id).Calm);
        }

        [Fact]
        public void A_Seen_older_than_what_the_server_keeps_counts_from_its_oldest_alert()
        {
            long longAgo = new DateTimeOffset(Now.AddDays(-8)).ToUnixTimeMilliseconds();
            var v = View(new Alert[0], cleared: longAgo, keptFrom: Now.AddHours(-19).AddMinutes(-50));
            Assert.Equal("Nothing since 02:10", v.Verdict);
        }

        [Fact]
        public void The_HUD_count_holds_alerts_since_the_card_was_opened_coloured_by_the_worst()
        {
            var book = Book(Make(AlertKind.Drop, "seen already", 30), Make(AlertKind.Connection, "new", 10), Make(AlertKind.Shared, "new too", 5));
            var v = View(book.All, opened: book.All[0].Id);
            Assert.Equal(2, v.New);
            Assert.Equal(AlertLevel.Warn, v.NewLevel);
            Assert.Equal(AlertLevel.Bad, View(book.All).NewLevel);
            Assert.Equal(0, View(book.All, opened: book.All[2].Id).New);
            Assert.Equal(1, View(book.All, opened: book.All[0].Id, show: k => k == AlertKind.Shared ? AlertShow.Off : Defaults(k)).New);
        }

        [Fact]
        public void Times_from_another_day_name_the_day()
        {
            Assert.Equal("21:40", AlertView.When(Now.AddMinutes(-20), Now, TimeSpan.Zero));
            Assert.Equal("Tue 21:40", AlertView.When(Now.AddDays(-1).AddMinutes(-20), Now, TimeSpan.Zero));
            Assert.Equal("22:40", AlertView.When(Now.AddMinutes(-20), Now, TimeSpan.FromHours(1)));
            Assert.Equal("Wed 23:40", AlertView.When(Now.AddMinutes(-20), Now, TimeSpan.FromHours(2)));
        }

        [Fact]
        public void By_default_drops_falling_behind_memory_and_crashes_show_on_screen_and_the_rest_only_count()
        {
            var onScreen = AlertKinds.All.Where(k => AlertKinds.Default(k) == AlertShow.OnScreen);
            Assert.Equal(new[] { AlertKind.Drop, AlertKind.Behind, AlertKind.Memory, AlertKind.Crash }, onScreen);
            Assert.All(AlertKinds.All.Except(onScreen), k => Assert.Equal(AlertShow.Count, AlertKinds.Default(k)));
            Assert.Equal(AlertKinds.All.Length, AlertKinds.All.Select(AlertKinds.Key).Distinct().Count());
        }

        // ------------------------------------------------------------------ on screen

        [Fact]
        public void Only_kinds_set_on_screen_show_a_line_at_the_top_left()
        {
            var arrived = new[] { Make(AlertKind.Drop, "D'Arcy dropped", 0), Make(AlertKind.Connection, "D'Arcy struggles", 0) };
            Assert.Equal(new[] { "Vigil: D'Arcy dropped" }, AlertNotice.Lines(arrived, false, Defaults));
            Assert.Empty(AlertNotice.Lines(arrived, false, k => AlertShow.Count));
        }

        [Fact]
        public void Alerts_waiting_when_the_admin_joins_show_as_one_line()
        {
            var one = new[] { Make(AlertKind.Drop, "D'Arcy dropped", 60) };
            Assert.Equal(new[] { "Vigil: while you were away: D'Arcy dropped" }, AlertNotice.Lines(one, true, Defaults));
            var three = new[] { Make(AlertKind.Drop, "a", 60), Make(AlertKind.Connection, "b", 50), Make(AlertKind.Memory, "c", 40) };
            Assert.Equal(new[] { "Vigil: 2 alerts while you were away; the newest: c" }, AlertNotice.Lines(three, true, Defaults));
        }

        [Fact]
        public void Many_alerts_at_once_show_two_lines_and_how_many_more()
        {
            var arrived = Enumerable.Range(1, 5).Select(i => Make(AlertKind.Drop, "d" + i, 0)).ToList();
            Assert.Equal(new[] { "Vigil: d1", "Vigil: d2", "Vigil: and 3 more alerts" }, AlertNotice.Lines(arrived, false, Defaults));
            Assert.Equal(3, AlertNotice.Lines(arrived.Take(3).ToList(), false, Defaults).Count);
        }

        [Fact]
        public void The_HUD_line_counts_new_alerts_in_their_worst_colour()
        {
            var limits = new HudLine.Limits { SlowFps = 20, PingMs = 250, QueueKb = 64, MemoryLoadPct = 95 };
            var bad = HudLine.Build(new HudLine.Values { Alerts = 2, AlertWorst = AlertLevel.Bad, Recording = true }, limits);
            Assert.Contains(bad, s => s.Text == "2 alerts" && s.Level == HudLine.Level.Bad);
            var one = HudLine.Build(new HudLine.Values { Alerts = 1, AlertWorst = AlertLevel.Warn, Recording = true }, limits);
            Assert.Contains(one, s => s.Text == "1 alert" && s.Level == HudLine.Level.Warn);
            Assert.DoesNotContain(HudLine.Build(new HudLine.Values { Recording = true }, limits), s => s.Text.Contains("alert"));
        }

        // ------------------------------------------------------------------ what this game remembers

        [Fact]
        public void The_admin_s_game_remembers_per_server_what_it_opened_and_cleared()
        {
            var marks = AlertMarks.Parse(null);
            Assert.Equal(0, marks.Opened("Midgard"));
            marks.SetOpened("Midgard", 42);
            marks.SetCleared("Midgard", 40);
            marks.SetOpened("Other world", 7);
            var again = AlertMarks.Parse(marks.Format());
            Assert.Equal(42, again.Opened("Midgard"));
            Assert.Equal(40, again.Cleared("Midgard"));
            Assert.Equal(7, again.Opened("Other world"));
            Assert.Equal(0, again.Cleared("Other world"));
            Assert.Equal(0, AlertMarks.Parse("broken\tline\nMidgard\tx\t3").Opened("Midgard"));
        }
    }
}
