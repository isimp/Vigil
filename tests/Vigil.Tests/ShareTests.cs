using System;
using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// A player who opts in sends their incident reports to the server's Vigil, so nobody has to
    /// pass files around: the kinds they chose go on their own, any one with a button. What waits
    /// for the server survives a restart, so a drop seen from the player's side goes at the next
    /// join. The server takes them only when its owner opted in too, keeps them per player under a
    /// limit, oldest going first, and lists them with its own reports.
    /// </summary>
    public sealed class ShareTests
    {
        [Fact]
        public void The_switch_sends_the_incident_kinds_the_player_chose_and_nothing_else()
        {
            var all = ShareKinds.LagReports | ShareKinds.Drops | ShareKinds.OtherIncidents;
            Assert.True(ShareQueue.Wanted(all, "Vigil-incident-162001-manual.txt"));
            Assert.True(ShareQueue.Wanted(all, "Vigil-incident-211504-drop.txt"));
            Assert.True(ShareQueue.Wanted(all, "Vigil-incident-163839-hitch.txt"));
            Assert.False(ShareQueue.Wanted(all, "Vigil-load-163001.txt"));
            Assert.False(ShareQueue.Wanted(all, "Vigil-summary.txt"));
            Assert.False(ShareQueue.Wanted(ShareKinds.LagReports, "Vigil-incident-211504-drop.txt"));
            Assert.True(ShareQueue.Wanted(ShareKinds.Drops, "Vigil-incident-211504-drop.txt"));
            Assert.False(ShareQueue.Wanted(ShareKinds.Drops | ShareKinds.LagReports, "Vigil-incident-163839-hitch.txt"));
            Assert.False(ShareQueue.Wanted(ShareKinds.None, "Vigil-incident-162001-manual.txt"));
        }

        [Fact]
        public void Incident_and_environment_reports_can_be_shared_with_the_button_and_nothing_else()
        {
            Assert.True(ShareQueue.Shareable("Vigil-incident-163839-hitch.txt"));
            Assert.True(ShareQueue.Shareable("Vigil-environment-101500.txt"));
            Assert.False(ShareQueue.Shareable("Vigil-load-163001.txt"));
            Assert.False(ShareQueue.Shareable("Vigil-timeline.tsv"));
        }

        [Fact]
        public void An_environment_report_goes_on_its_own_only_when_environment_reports_are_ticked()
        {
            var incidents = ShareKinds.LagReports | ShareKinds.Drops | ShareKinds.OtherIncidents;
            Assert.False(ShareQueue.Wanted(incidents, "Vigil-environment-101500.txt"));
            Assert.True(ShareQueue.Wanted(ShareKinds.EnvironmentReports, "Vigil-environment-101500.txt"));
            Assert.True(ShareQueue.Wanted(incidents | ShareKinds.EnvironmentReports, "Vigil-environment-101500.txt"));
            Assert.False(ShareQueue.Wanted(ShareKinds.EnvironmentReports, "Vigil-incident-163839-hitch.txt"));
            Assert.Equal("environment", ReportCatalog.KindOf("Vigil-environment-101500.txt"));
        }

        [Fact]
        public void What_waits_for_the_server_survives_a_restart_once_each_in_order()
        {
            var q = new ShareQueue();
            Assert.True(q.Add(@"C:\v\Vigil-incident-211504-drop.txt"));
            Assert.True(q.Add(@"C:\v\Vigil-incident-211600-hitch.txt"));
            Assert.False(q.Add(@"C:\v\Vigil-incident-211504-drop.txt"));
            var back = ShareQueue.Parse(q.Format());
            Assert.Equal(new[] { @"C:\v\Vigil-incident-211504-drop.txt", @"C:\v\Vigil-incident-211600-hitch.txt" }, back.Files);
        }

        [Fact]
        public void A_report_deleted_while_it_waited_is_skipped()
        {
            var q = new ShareQueue();
            q.Add("a.txt");
            q.Add("b.txt");
            Assert.Equal("b.txt", q.Next(f => f == "b.txt"));
            Assert.Equal(new[] { "b.txt" }, q.Files);
        }

        [Fact]
        public void A_players_folder_on_the_server_is_their_name_made_safe()
        {
            Assert.Equal("D'Arcy", SharedStore.Folder("D'Arcy"));
            Assert.Equal("a_b_c", SharedStore.Folder("a/b\\c"));
            Assert.Equal("__", SharedStore.Folder(".."));
            Assert.Equal("player", SharedStore.Folder("  "));
            Assert.Equal(32, SharedStore.Folder(new string('x', 80)).Length);
            Assert.True(ReportCatalog.Servable("shared/" + SharedStore.Folder("..") + "/Vigil-incident-1-drop.txt"));
        }

        [Fact]
        public void The_server_takes_incident_reports_within_its_limits_and_says_why_not_otherwise()
        {
            long limit = 50L * 1024 * 1024;
            Assert.Null(SharedStore.Refuse("Vigil-incident-211504-drop.txt", 500_000, limit));
            Assert.NotNull(SharedStore.Refuse("Vigil-load-163001.txt", 5_000, limit));
            Assert.NotNull(SharedStore.Refuse("../x/Vigil-incident-1-drop.txt", 5_000, limit));
            Assert.NotNull(SharedStore.Refuse("Vigil-incident-211504-drop.txt", SharedStore.MaxFileBytes + 1, limit));
            Assert.NotNull(SharedStore.Refuse("Vigil-incident-211504-drop.txt", 600_000, 500_000));
        }

        [Fact]
        public void A_players_oldest_reports_go_first_to_stay_under_the_limit()
        {
            var kept = new List<(string, long, DateTime)>
            {
                ("old.txt", 400, new DateTime(2026, 10, 1)),
                ("mid.txt", 400, new DateTime(2026, 10, 5)),
                ("new.txt", 400, new DateTime(2026, 10, 9)),
            };
            Assert.Equal(new[] { "old.txt", "mid.txt" }, SharedStore.Prune(kept, 600, 1200));
            Assert.Empty(SharedStore.Prune(kept, 0, 1200));
        }

        [Fact]
        public void Sharing_and_the_mod_comparison_are_what_every_player_may_ask_the_servers_Vigil_for()
        {
            Assert.True(Greeting.ForEveryone("share"));
            Assert.True(Greeting.ForEveryone("parity"));
            Assert.False(Greeting.ForEveryone("report"));
            Assert.False(Greeting.ForEveryone("reports"));
            Assert.False(Greeting.ForEveryone("status"));
            Assert.False(Greeting.ForEveryone(null));
        }

        [Fact]
        public void Shared_reports_are_listed_with_the_player_who_sent_them()
        {
            Assert.True(ReportCatalog.Servable("shared/D'Arcy/Vigil-incident-211504-drop.txt"));
            Assert.False(ReportCatalog.Servable("shared/D'Arcy/Vigil-load-163001.txt"));
            Assert.False(ReportCatalog.Servable("shared/Vigil-incident-211504-drop.txt"));
            Assert.Equal("D'Arcy", ReportCatalog.From("shared/D'Arcy/Vigil-incident-211504-drop.txt"));
            Assert.Null(ReportCatalog.From("Vigil-incident-211504-drop.txt"));
        }
    }
}
