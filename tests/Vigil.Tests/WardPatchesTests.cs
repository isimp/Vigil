using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The game patches on Protective Wards: starting repair at one ward starts it on every ward
    /// chained to it, each repairing its own area; wards can repair by themselves; a player without
    /// access can let themself into a ward with the server's password.
    /// </summary>
    public sealed class WardPatchesTests
    {
        private sealed class Ward
        {
            public string Name;
            public float X;
            public float Radius = 10f;
            public bool Active = true;
        }

        private static Ward W(string name, float x, bool active = true) => new Ward { Name = name, X = x, Active = active };

        private static bool Overlap(Ward a, Ward b) => Math.Abs(a.X - b.X) <= a.Radius + b.Radius;

        private static string Names(IEnumerable<Ward> wards) => string.Join(",", wards.Select(w => w.Name).OrderBy(n => n, StringComparer.Ordinal));

        [Fact]
        public void Starting_repair_at_one_ward_reaches_every_ward_chained_to_it()
        {
            var a = W("a", 0);
            var b = W("b", 15);
            var c = W("c", 30);
            var d = W("d", 45);

            var reached = WardRepair.Network(a, new[] { a, b, c, d }, w => w.Active, Overlap);

            Assert.Equal("a,b,c,d", Names(reached));
        }

        [Fact]
        public void A_switched_off_ward_breaks_the_chain_and_wards_apart_are_left_out()
        {
            var a = W("a", 0);
            var off = W("off", 15, active: false);
            var c = W("c", 30);
            var far = W("far", 500);

            var reached = WardRepair.Network(a, new[] { a, off, c, far }, w => w.Active, Overlap);

            Assert.Equal("a", Names(reached));
        }

        [Fact]
        public void The_started_ward_is_counted_once_even_when_the_list_holds_it_twice()
        {
            var a = W("a", 0);
            var b = W("b", 15);

            var reached = WardRepair.Network(a, new[] { a, b, a, b }, w => w.Active, Overlap);

            Assert.Equal(2, reached.Count);
        }

        [Fact]
        public void A_ward_started_for_repair_repairs_one_piece_a_turn_and_stops_when_none_is_damaged()
        {
            Assert.Equal(WardRepair.Step.RepairOne, WardRepair.Next(owner: true, active: true, started: true, auto: false, damaged: 3));
            Assert.Equal(WardRepair.Step.Finish, WardRepair.Next(owner: true, active: true, started: true, auto: false, damaged: 0));
        }

        [Fact]
        public void With_automatic_repair_every_active_ward_repairs_without_being_started()
        {
            Assert.Equal(WardRepair.Step.RepairOne, WardRepair.Next(owner: true, active: true, started: false, auto: true, damaged: 1));
            Assert.Equal(WardRepair.Step.Idle, WardRepair.Next(owner: true, active: true, started: false, auto: true, damaged: 0));
            Assert.Equal(WardRepair.Step.Idle, WardRepair.Next(owner: true, active: true, started: false, auto: false, damaged: 5));
        }

        [Fact]
        public void Only_the_game_that_owns_a_ward_repairs_for_it()
        {
            Assert.Equal(WardRepair.Step.Idle, WardRepair.Next(owner: false, active: true, started: true, auto: true, damaged: 4));
        }

        [Fact]
        public void A_ward_switched_off_while_repairing_stops_and_a_switched_off_ward_never_repairs_by_itself()
        {
            Assert.Equal(WardRepair.Step.Finish, WardRepair.Next(owner: true, active: false, started: true, auto: false, damaged: 4));
            Assert.Equal(WardRepair.Step.Idle, WardRepair.Next(owner: true, active: false, started: false, auto: true, damaged: 4));
        }

        [Fact]
        public void The_ward_shows_the_pieces_left_while_it_repairs_and_nothing_once_done()
        {
            Assert.Equal(2, WardRepair.LeftAfter(WardRepair.Step.RepairOne, 3));
            Assert.Equal(WardRepair.NotRepairing, WardRepair.LeftAfter(WardRepair.Step.RepairOne, 1));
            Assert.Equal(WardRepair.NotRepairing, WardRepair.LeftAfter(WardRepair.Step.Finish, 0));
            Assert.Equal(WardRepair.NotRepairing, WardRepair.LeftAfter(WardRepair.Step.Idle, 7));
        }

        [Fact]
        public void Alt_use_starts_the_whole_base_this_ward_or_Protective_Wards_own_repair_by_the_switches()
        {
            Assert.Equal(WardRepair.Start.WholeNetwork, WardRepair.OnStart(network: true, auto: false));
            Assert.Equal(WardRepair.Start.WholeNetwork, WardRepair.OnStart(network: true, auto: true));
            Assert.Equal(WardRepair.Start.ThisWard, WardRepair.OnStart(network: false, auto: true));
            Assert.Equal(WardRepair.Start.ProtectiveWards, WardRepair.OnStart(network: false, auto: false));
        }

        [Fact]
        public void The_seconds_per_piece_stay_in_their_range()
        {
            Assert.Equal(WardRepair.MinSeconds, WardRepair.ClampSeconds(0));
            Assert.Equal(WardRepair.MaxSeconds, WardRepair.ClampSeconds(100000));
            Assert.Equal(10, WardRepair.ClampSeconds(10));
        }

        [Fact]
        public void The_repair_state_says_off_working_stepped_aside_or_not_needed()
        {
            Assert.Equal("off: the server's switches", WardRepair.StateLine(false, false, true, "2.1.0", null));
            Assert.Equal("not needed: Protective Wards is not installed", WardRepair.StateLine(true, false, false, null, null));
            Assert.Equal("stepped aside: its repair changed", WardRepair.StateLine(true, true, true, "2.2.0", "its repair changed"));
            Assert.Equal("working · Protective Wards 2.1.0 · the whole connected base", WardRepair.StateLine(true, false, true, "2.1.0", null));
            Assert.Equal("working · Protective Wards 2.1.0 · wards repair by themselves", WardRepair.StateLine(false, true, true, "2.1.0", null));
            Assert.Equal("working · Protective Wards 2.1.0 · the whole connected base, wards repair by themselves", WardRepair.StateLine(true, true, true, "2.1.0", null));
        }

        [Fact]
        public void The_server_password_lets_a_player_in_only_when_it_matches_exactly()
        {
            Assert.True(WardPassword.Matches("Ymir!", "Ymir!"));
            Assert.False(WardPassword.Matches("Ymir!", "ymir!"));
            Assert.False(WardPassword.Matches("Ymir!", "Ymir! "));
            Assert.False(WardPassword.Matches("Ymir!", "Ymir"));
            Assert.False(WardPassword.Matches("Ymir!", ""));
            Assert.False(WardPassword.Matches("Ymir!", null));
        }

        [Fact]
        public void No_server_password_lets_nobody_in_and_an_overlong_guess_is_refused()
        {
            Assert.False(WardPassword.Matches("", ""));
            Assert.False(WardPassword.Matches(null, "anything"));
            string longOne = new string('x', WardPassword.MaxLength + 1);
            Assert.False(WardPassword.Matches(longOne, longOne));
        }

        [Fact]
        public void A_stranger_at_an_active_ward_is_asked_for_the_server_password()
        {
            Assert.True(WardPassword.Offer(serverHasOne: true, playerWard: true, enabled: true, hasAccess: false, playerId: 7, wardOffers: false));
        }

        [Fact]
        public void Nobody_is_asked_without_a_server_password_or_with_access_and_a_switched_off_ward_keeps_its_own_way_in()
        {
            Assert.False(WardPassword.Offer(serverHasOne: false, playerWard: true, enabled: true, hasAccess: false, playerId: 7, wardOffers: false));
            Assert.False(WardPassword.Offer(serverHasOne: true, playerWard: true, enabled: true, hasAccess: true, playerId: 7, wardOffers: false));
            Assert.False(WardPassword.Offer(serverHasOne: true, playerWard: true, enabled: false, hasAccess: false, playerId: 7, wardOffers: false));
            Assert.False(WardPassword.Offer(serverHasOne: true, playerWard: false, enabled: true, hasAccess: false, playerId: 7, wardOffers: false));
            Assert.False(WardPassword.Offer(serverHasOne: true, playerWard: true, enabled: true, hasAccess: false, playerId: 0, wardOffers: false));
        }

        [Fact]
        public void A_ward_with_its_own_password_still_asks_for_it()
        {
            Assert.True(WardPassword.Offer(serverHasOne: false, playerWard: true, enabled: true, hasAccess: false, playerId: 7, wardOffers: true));
        }

        [Fact]
        public void The_server_lets_in_on_its_password_and_leaves_the_ward_own_password_to_Protective_Wards()
        {
            var gate = new WardPassword.Gate();

            Assert.Equal(WardPassword.Answer.LetIn, gate.Judge("Ymir!", "Ymir!", playerId: 7, hasAccess: false, wardHasOwn: false, now: 0));
            Assert.Equal(WardPassword.Answer.Wrong, gate.Judge("Ymir!", "Odin", playerId: 8, hasAccess: false, wardHasOwn: false, now: 0));
            Assert.Equal(WardPassword.Answer.ProtectiveWards, gate.Judge("Ymir!", "the ward's own", playerId: 9, hasAccess: false, wardHasOwn: true, now: 0));
            Assert.Equal(WardPassword.Answer.ProtectiveWards, gate.Judge("Ymir!", "Ymir!", playerId: 10, hasAccess: true, wardHasOwn: false, now: 0));
            Assert.Equal(WardPassword.Answer.ProtectiveWards, gate.Judge("", "Ymir!", playerId: 11, hasAccess: false, wardHasOwn: false, now: 0));
        }

        [Fact]
        public void After_a_wrong_guess_the_player_waits_before_the_server_password_is_checked_again()
        {
            var gate = new WardPassword.Gate();

            Assert.Equal(WardPassword.Answer.Wrong, gate.Judge("Ymir!", "Odin", 7, false, false, now: 100));
            Assert.Equal(WardPassword.Answer.Wrong, gate.Judge("Ymir!", "Ymir!", 7, false, false, now: 100 + WardPassword.WaitSeconds - 0.1));
            Assert.Equal(WardPassword.Answer.LetIn, gate.Judge("Ymir!", "Ymir!", 8, false, false, now: 100.5));
            Assert.Equal(WardPassword.Answer.LetIn, gate.Judge("Ymir!", "Ymir!", 7, false, false, now: 100 + WardPassword.WaitSeconds + 0.1));
        }

        [Fact]
        public void A_waiting_player_at_a_ward_with_its_own_password_goes_to_Protective_Wards_without_the_server_password_checked()
        {
            var gate = new WardPassword.Gate();
            gate.Judge("Ymir!", "Odin", 7, false, wardHasOwn: true, now: 0);

            Assert.Equal(WardPassword.Answer.ProtectiveWards, gate.Judge("Ymir!", "Ymir!", 7, false, wardHasOwn: true, now: 1));
        }

        [Fact]
        public void The_password_state_says_whether_the_server_has_one()
        {
            Assert.Equal("not needed: Protective Wards is not installed", WardPassword.StateLine(true, false, null, null));
            Assert.Equal("off: the server has no ward password", WardPassword.StateLine(false, true, "2.1.0", null));
            Assert.Equal("stepped aside: its password prompt changed", WardPassword.StateLine(true, true, "2.2.0", "its password prompt changed"));
            Assert.Equal("working · Protective Wards 2.1.0 · players without access are asked for the server's password", WardPassword.StateLine(true, true, "2.1.0", null));
        }

        [Fact]
        public void Protective_Wards_210s_repair_is_found()
        {
            var shape = WardShape.FindRepair(Pw210);

            Assert.NotNull(shape);
            Assert.Equal(typeof(Pw.ProtectiveWards).GetMethod("PassiveRepairEffect"), shape.StartRepair);
            Assert.Equal("CanBeRepaired", shape.CanBeRepaired.Name);
            Assert.Equal("AreWardsOverlapping", shape.Overlapping.Name);
            Assert.Equal("IsActivePlayerWard", shape.Active.Name);
            Assert.Equal("IsPlayerWardPrefab", shape.PlayerWard.Name);
            Assert.Equal("wardIsRepairing", shape.Repairing.Name);
        }

        [Fact]
        public void Protective_Wards_210s_password_way_in_is_found()
        {
            var shape = WardShape.FindPassword(Pw210);

            Assert.NotNull(shape);
            Assert.Equal("ShouldOfferPasswordEntry", shape.Offers.Name);
            Assert.Equal("RPC_SubmitWardPasswordServer", shape.Submitted.Name);
            Assert.Equal("SendPasswordEntryResult", shape.Answer.Name);
            Assert.Equal(3, shape.RoutedPlayer.GetParameters().Length);
            Assert.Equal("HasAccessToWardOrConnectedWard", shape.Access.Name);
            Assert.Equal(2, shape.Access.GetParameters().Length);
            Assert.Equal("IsPasswordProtectionActive", shape.OwnPassword.Name);
            Assert.Equal("TryGetWard", shape.WardById.Name);
            Assert.Equal("HasDirectAccessToWardZdo", shape.DirectAccess.Name);
            Assert.Equal("AddPermitted", shape.Permit.Name);
            Assert.Equal("GetAllWards", shape.AllWards.Name);
            Assert.Equal("Success", Enum.ToObject(shape.Results, shape.LetIn).ToString());
            Assert.Equal("IncorrectPassword", Enum.ToObject(shape.Results, shape.Wrong).ToString());
        }

        [Fact]
        public void Another_shape_or_no_Protective_Wards_is_not_taken()
        {
            Assert.Null(WardShape.FindRepair(name => null));
            Assert.Null(WardShape.FindPassword(name => null));
            Assert.Null(WardShape.FindRepair(name => name == "ProtectiveWards.ProtectiveWards" ? typeof(Other.ProtectiveWards) : Pw210(name)));
            Assert.Null(WardShape.FindPassword(name => name == "ProtectiveWards.WardPasswordProtection" ? typeof(Other.WardPasswordProtection) : Pw210(name)));
        }

        [Fact]
        public void A_self_test_trial_switches_repair_on_for_this_game_while_it_lasts_at_its_own_pace()
        {
            var trial = new WardRepair.Trial();
            trial.Start(network: true, auto: false, seconds: 1, now: 100, lasts: 30);

            var on = trial.Effective(network: false, auto: false, seconds: 10, now: 110);

            Assert.True(on.Network);
            Assert.False(on.Auto);
            Assert.Equal(1, on.Seconds);
        }

        [Fact]
        public void The_trial_ends_by_itself_or_when_ended_and_the_servers_switches_come_back()
        {
            var trial = new WardRepair.Trial();
            trial.Start(network: false, auto: true, seconds: 1, now: 100, lasts: 30);

            var after = trial.Effective(network: false, auto: false, seconds: 10, now: 131);
            Assert.False(after.Auto);
            Assert.Equal(10, after.Seconds);

            trial.Start(network: false, auto: true, seconds: 1, now: 200, lasts: 30);
            trial.End();
            Assert.False(trial.Effective(false, false, 10, now: 201).Auto);
        }

        [Fact]
        public void A_trial_never_switches_off_what_the_server_has_on()
        {
            var trial = new WardRepair.Trial();
            trial.Start(network: false, auto: true, seconds: 1, now: 0, lasts: 30);

            var on = trial.Effective(network: true, auto: false, seconds: 10, now: 1);

            Assert.True(on.Network);
            Assert.True(on.Auto);

            trial.Start(network: true, auto: false, seconds: 1, now: 0, lasts: 30);
            Assert.True(trial.Effective(network: false, auto: true, seconds: 10, now: 1).Auto);
        }

        [Fact]
        public void Starting_repair_passes_when_every_ward_of_the_chain_this_game_owns_was_started()
        {
            var ok = WardRepair.ChainCheck(chain: 3, ownedHere: 2, startedHere: 2, damaged: 1);
            Assert.Equal(SelfTestReport.Result.Pass, ok.Result);
            Assert.Equal("3 wards in the chain, 1 damaged piece; the 2 owned by this game started, 1 asked through its owner's game", ok.Text);

            var lone = WardRepair.ChainCheck(chain: 1, ownedHere: 1, startedHere: 1, damaged: 2);
            Assert.Equal("1 ward, 2 damaged pieces; it started", lone.Text);
        }

        [Fact]
        public void Starting_repair_fails_when_a_ward_owned_here_did_not_start_or_nothing_was_started()
        {
            var missed = WardRepair.ChainCheck(chain: 3, ownedHere: 3, startedHere: 2, damaged: 1);
            Assert.Equal(SelfTestReport.Result.Fail, missed.Result);
            Assert.Equal("3 wards in the chain, 1 damaged piece; only 2 of the 3 owned by this game started", missed.Text);

            var none = WardRepair.ChainCheck(chain: 0, ownedHere: 0, startedHere: 0, damaged: 0);
            Assert.Equal(SelfTestReport.Result.Fail, none.Result);
            Assert.Equal("the ward's alt-use did not reach Vigil's repair", none.Text);
        }

        [Fact]
        public void The_servers_password_check_refuses_a_wrong_guess_and_says_when_nothing_could_be_tried()
        {
            Assert.Equal(SelfTestReport.Result.Pass, WardPassword.ServerCheck(true, null, "Ymir!", WardPassword.Answer.Wrong, wards: 4).Result);
            Assert.Equal("set; a wrong guess at a ward you have no access to was refused (4 wards on the server)",
                WardPassword.ServerCheck(true, null, "Ymir!", WardPassword.Answer.Wrong, wards: 4).Text);
            Assert.Equal("set; the guess went to the ward's own password, as it should",
                WardPassword.ServerCheck(true, null, "Ymir!", WardPassword.Answer.ProtectiveWards, wards: 4).Text);
            Assert.Equal(SelfTestReport.Result.Info, WardPassword.ServerCheck(true, null, "Ymir!", null, wards: 4).Result);
            Assert.Equal("set; you have access to every ward on the server (4), so no guess was tried",
                WardPassword.ServerCheck(true, null, "Ymir!", null, wards: 4).Text);
        }

        [Fact]
        public void The_servers_password_check_fails_on_a_wrong_guess_let_in_an_untypeable_password_or_a_missing_patch()
        {
            Assert.Equal(SelfTestReport.Result.Fail, WardPassword.ServerCheck(true, null, "Ymir!", WardPassword.Answer.LetIn, 4).Result);
            var tooLong = WardPassword.ServerCheck(true, null, new string('x', WardPassword.MaxLength + 1), null, 4);
            Assert.Equal(SelfTestReport.Result.Fail, tooLong.Result);
            Assert.Equal("the password is longer than " + WardPassword.MaxLength + " characters: nobody can type it", tooLong.Text);
            Assert.Equal(SelfTestReport.Result.Fail, WardPassword.ServerCheck(true, "its password prompt changed", "Ymir!", null, 4).Result);
            Assert.Equal("Protective Wards is not on the server", WardPassword.ServerCheck(false, null, "Ymir!", null, 0).Text);
            var none = WardPassword.ServerCheck(true, null, "", null, 4);
            Assert.Equal(SelfTestReport.Result.Info, none.Result);
            Assert.Equal("no password set on the server: nobody is asked", none.Text);
        }

        [Fact]
        public void The_server_says_it_answers_ward_checks_in_its_greeting()
        {
            Assert.Contains(Greeting.Wards, Greeting.Own.Split(','));
            Assert.True(Greeting.ForEveryone(Greeting.Wards));
        }

        [Fact]
        public void A_version_without_the_hover_count_or_the_prompt_check_is_not_taken()
        {
            Assert.Null(WardShape.FindRepair(name => name == "ProtectiveWards.ProtectiveWards" ? typeof(NoCount.ProtectiveWards) : Pw210(name)));
            Assert.Null(WardShape.FindPassword(name => name == "ProtectiveWards.WardPasswordProtection" ? typeof(NoPrompt.WardPasswordProtection) : Pw210(name)));
        }

        private static Type Pw210(string name)
        {
            switch (name)
            {
                case "ProtectiveWards.ProtectiveWards": return typeof(Pw.ProtectiveWards);
                case "ProtectiveWards.WardPasswordProtection": return typeof(Pw.WardPasswordProtection);
                case "ProtectiveWards.WardZdoUtils": return typeof(Pw.WardZdoUtils);
                default: return null;
            }
        }

        // Stand-ins with the shapes of Protective Wards 2.1.0's members (the game's types as plain classes).
        private sealed class PrivateArea { }

        private sealed class Player { }

        private sealed class Piece { }

        private sealed class ZDO { }

        private sealed class ZPackage { }

        private struct ZDOID { }

        private static class Pw
        {
            public class ProtectiveWards
            {
                internal static Dictionary<PrivateArea, int> wardIsRepairing = new Dictionary<PrivateArea, int>();

                internal readonly struct RoutedPlayerContext
                {
                    public readonly long PlayerID;
                    public readonly string PlayerName;
                }

                public static IEnumerator PassiveRepairEffect(PrivateArea ward, Player initiator) => null;

                private static bool CanBeRepaired(Piece piece, PrivateArea ward) => false;

                private static bool AreWardsOverlapping(PrivateArea ward, PrivateArea candidate) => false;

                public static bool IsActivePlayerWard(PrivateArea ward) => false;

                internal static bool IsPlayerWardPrefab(PrivateArea ward) => false;

                public static bool HasAccessToWardOrConnectedWard(PrivateArea ward, Player player) => false;

                public static bool HasAccessToWardOrConnectedWard(PrivateArea ward, Player player, int mode) => false;

                internal static bool TryGetRoutedPlayer(long sender, long claimedPlayerID, out RoutedPlayerContext player)
                {
                    player = default;
                    return false;
                }

                internal static bool TryGetRoutedPlayer(long sender, out RoutedPlayerContext player)
                {
                    player = default;
                    return false;
                }
            }

            public static class WardPasswordProtection
            {
                private enum PasswordEntryResult { Success, IncorrectPassword, Unavailable, AlreadyPermitted }

                private static bool ShouldOfferPasswordEntry(PrivateArea ward, Player player) => false;

                internal static bool IsPasswordProtectionActive(PrivateArea ward) => false;

                internal static bool IsPasswordProtectionActive(ZDO zdo) => false;

                private static void RPC_SubmitWardPasswordServer(long sender, ZPackage package) { }

                private static void SendPasswordEntryResult(long peerID, ZDOID wardID, PasswordEntryResult result) { }
            }

            public static class WardZdoUtils
            {
                internal static bool TryGetWard(ZDOID zdoID, out ZDO zdo)
                {
                    zdo = null;
                    return false;
                }

                internal static bool HasDirectAccessToWardZdo(ZDO zdo, long playerID) => false;

                internal static bool AddPermitted(ZDO zdo, long playerID, string playerName) => false;

                internal static IEnumerable<ZDO> GetAllWards() => Array.Empty<ZDO>();
            }
        }

        // 2.1.0's shapes with one member changed: the hover's count holds text, the prompt check is gone.
        private static class NoCount
        {
            public class ProtectiveWards
            {
                internal static Dictionary<PrivateArea, string> wardIsRepairing = new Dictionary<PrivateArea, string>();

                public static IEnumerator PassiveRepairEffect(PrivateArea ward, Player initiator) => null;

                private static bool CanBeRepaired(Piece piece, PrivateArea ward) => false;

                private static bool AreWardsOverlapping(PrivateArea ward, PrivateArea candidate) => false;

                public static bool IsActivePlayerWard(PrivateArea ward) => false;

                internal static bool IsPlayerWardPrefab(PrivateArea ward) => false;
            }
        }

        private static class NoPrompt
        {
            public static class WardPasswordProtection
            {
                private enum PasswordEntryResult { Success, IncorrectPassword, Unavailable, AlreadyPermitted }

                internal static bool IsPasswordProtectionActive(ZDO zdo) => false;

                private static void RPC_SubmitWardPasswordServer(long sender, ZPackage package) { }

                private static void SendPasswordEntryResult(long peerID, ZDOID wardID, PasswordEntryResult result) { }
            }
        }

        private static class Other
        {
            public class ProtectiveWards
            {
                public static void PassiveRepairEffect(PrivateArea ward) { }
            }

            public static class WardPasswordProtection
            {
                private static bool ShouldOfferPasswordEntry(PrivateArea ward) => false;
            }
        }
    }
}
