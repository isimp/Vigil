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
        public void A_door_inside_an_active_ward_the_player_has_no_access_to_is_locked()
        {
            Assert.True(WardPassword.LocksDoor(wardsLockDoors: true, active: true, inside: true, hasAccess: false, madeDoor: false));
        }

        [Fact]
        public void A_door_is_not_locked_with_access_outside_the_area_at_a_switched_off_ward_or_for_its_builder()
        {
            Assert.False(WardPassword.LocksDoor(wardsLockDoors: true, active: true, inside: true, hasAccess: true, madeDoor: false));
            Assert.False(WardPassword.LocksDoor(wardsLockDoors: true, active: true, inside: false, hasAccess: false, madeDoor: false));
            Assert.False(WardPassword.LocksDoor(wardsLockDoors: true, active: false, inside: true, hasAccess: false, madeDoor: false));
            Assert.False(WardPassword.LocksDoor(wardsLockDoors: true, active: true, inside: true, hasAccess: false, madeDoor: true));
            Assert.False(WardPassword.LocksDoor(wardsLockDoors: false, active: true, inside: true, hasAccess: false, madeDoor: false));
        }

        [Fact]
        public void At_a_locked_door_the_player_is_asked_about_the_first_ward_that_locks_it()
        {
            var a = W("a", 0);
            var b = W("b", 5);
            var c = W("c", 9);

            var asked = WardPassword.DoorWard(new[] { a, b, c }, locks: w => w != a, asks: w => true);

            Assert.Same(b, asked);
        }

        [Fact]
        public void A_door_asks_nothing_when_no_ward_locks_it_or_the_locking_ward_takes_no_password()
        {
            var a = W("a", 0);
            var b = W("b", 5);

            Assert.Null(WardPassword.DoorWard(new[] { a, b }, locks: w => false, asks: w => true));
            // The door stays locked by the first ward whatever a later one would take, so a later one is never asked about.
            Assert.Null(WardPassword.DoorWard(new[] { a, b }, locks: w => true, asks: w => w == b));
        }

        [Fact]
        public void Only_alt_use_of_a_door_opens_the_password_prompt_and_plain_use_stays_the_doors()
        {
            Assert.True(WardPassword.DoorUseAsks(hold: false, alt: true));
            Assert.False(WardPassword.DoorUseAsks(hold: false, alt: false));
            Assert.False(WardPassword.DoorUseAsks(hold: true, alt: true));
        }

        [Fact]
        public void The_door_hover_line_names_alt_use_and_Protective_Wards_own_password_words()
        {
            Assert.Equal("\n[<color=yellow><b>$KEY_AltPlace + $KEY_Use</b></color>] $pw_ward_password_enter", WardPassword.DoorHoverLine);
        }

        [Fact]
        public void The_self_test_passes_a_door_that_asks_exactly_when_the_server_has_a_password()
        {
            Assert.Equal(SelfTestReport.Result.Pass, WardPassword.DoorPromptCheck(serverHasOne: true, hoverLine: true, opened: true).Result);
            Assert.Equal(SelfTestReport.Result.Pass, WardPassword.DoorPromptCheck(serverHasOne: false, hoverLine: false, opened: false).Result);
        }

        [Fact]
        public void The_self_test_fails_a_door_without_the_line_or_the_prompt_or_asking_without_a_password()
        {
            var noLine = WardPassword.DoorPromptCheck(serverHasOne: true, hoverLine: false, opened: true);
            Assert.Equal(SelfTestReport.Result.Fail, noLine.Result);
            Assert.Equal("the door's hover has no password line", noLine.Text);
            var noPrompt = WardPassword.DoorPromptCheck(serverHasOne: true, hoverLine: true, opened: false);
            Assert.Equal(SelfTestReport.Result.Fail, noPrompt.Result);
            Assert.Equal("alt-use at the door opened no password prompt for its ward", noPrompt.Text);
            Assert.Equal(SelfTestReport.Result.Fail, WardPassword.DoorPromptCheck(serverHasOne: false, hoverLine: true, opened: false).Result);
            Assert.Equal(SelfTestReport.Result.Fail, WardPassword.DoorPromptCheck(serverHasOne: false, hoverLine: false, opened: true).Result);
        }

        [Fact]
        public void The_door_state_follows_the_ward_part_and_names_its_own_trouble()
        {
            Assert.Equal("working · Protective Wards 2.1.0 · alt-use at a door a ward locks asks for that ward's password",
                WardPassword.DoorStateLine(serverHasOne: true, found: true, version: "2.1.0", problem: null, doorProblem: null));
            Assert.Equal("stepped aside: its door lock changed (an update)",
                WardPassword.DoorStateLine(serverHasOne: true, found: true, version: "2.1.0", problem: null, doorProblem: "its door lock changed (an update)"));
            Assert.Equal("off: the server has no ward password",
                WardPassword.DoorStateLine(serverHasOne: false, found: true, version: "2.1.0", problem: null, doorProblem: "x"));
            Assert.Equal("not needed: Protective Wards is not installed",
                WardPassword.DoorStateLine(serverHasOne: true, found: false, version: null, problem: null, doorProblem: null));
        }

        [Fact]
        public void The_self_test_says_so_when_it_cannot_see_which_prompt_opened()
        {
            var unseen = WardPassword.DoorPromptCheck(serverHasOne: true, hoverLine: true, opened: null);
            Assert.Equal(SelfTestReport.Result.Info, unseen.Result);
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
            Assert.Equal("GetConfiguredWardRange", shape.Range.Name);
            Assert.Equal("GetWardDefaultRadius", shape.DefaultRadius.Name);
            Assert.Equal("HasWardAdminAccess", shape.Admin.Name);
            Assert.Equal("Success", Enum.ToObject(shape.Results, shape.LetIn).ToString());
            Assert.Equal("IncorrectPassword", Enum.ToObject(shape.Results, shape.Wrong).ToString());
        }

        [Fact]
        public void Protective_Wards_210s_door_lock_and_prompt_are_found()
        {
            var shape = WardShape.FindDoor(Pw210);

            Assert.NotNull(shape);
            Assert.Equal("TryHandleInteraction", shape.Prompt.Name);
            Assert.Equal("IsActivePlayerWard", shape.Active.Name);
            Assert.Equal("IsPointInsideWardArea", shape.Inside.Name);
            Assert.Equal("PrivateArea", shape.Inside.GetParameters()[0].ParameterType.Name);
            Assert.Equal("IsObjectOwnedByPlayerWithWardAccess", shape.MadeBy.Name);
            Assert.Equal("wardAccessProtectDoors", shape.LocksDoors.Name);
            Assert.Equal("s_promptWardID", shape.PromptWard.Name);
            Assert.Equal("ClosePrompt", shape.ClosePrompt.Name);
        }

        [Fact]
        public void A_version_without_the_door_lock_setting_is_not_taken_and_the_prompt_lookups_are_optional()
        {
            Assert.Null(WardShape.FindDoor(name => null));
            Assert.Null(WardShape.FindDoor(name => name == "ProtectiveWards.ProtectiveWards" ? typeof(NoCount.ProtectiveWards) : Pw210(name)));
            Assert.Null(WardShape.FindDoor(name => name == "ProtectiveWards.ProtectiveWards" ? typeof(NotBoolDoors.ProtectiveWards) : Pw210(name)));
            Assert.Null(WardShape.FindDoor(name => name == "ProtectiveWards.ProtectiveWards" ? typeof(NoBuilderCheck.ProtectiveWards) : Pw210(name)));
            var shape = WardShape.FindDoor(name => name == "ProtectiveWards.WardPasswordProtection" ? typeof(NoPromptState.WardPasswordProtection) : Pw210(name));
            Assert.NotNull(shape);
            Assert.Null(shape.PromptWard);
            Assert.Null(shape.ClosePrompt);
            var odd = WardShape.FindDoor(name => name == "ProtectiveWards.WardPasswordProtection" ? typeof(OddPromptState.WardPasswordProtection) : Pw210(name));
            Assert.Null(odd.PromptWard);
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
        public void A_self_test_trial_switches_repair_on_for_its_own_test_ward_at_its_own_pace()
        {
            var trial = new WardRepair.Trial();
            trial.Start(network: true, auto: false, seconds: 1, now: 100, lasts: 30);

            var on = trial.Effective(network: false, auto: false, seconds: 10, now: 110, testWard: true);

            Assert.True(on.Network);
            Assert.False(on.Auto);
            Assert.Equal(1, on.Seconds);
        }

        [Fact]
        public void A_trial_leaves_the_players_real_wards_to_the_servers_switches()
        {
            var trial = new WardRepair.Trial();
            trial.Start(network: true, auto: true, seconds: 1, now: 100, lasts: 30);

            var real = trial.Effective(network: false, auto: false, seconds: 10, now: 110, testWard: false);

            Assert.False(real.Network);
            Assert.False(real.Auto);
            Assert.Equal(10, real.Seconds);
        }

        [Fact]
        public void The_trial_ends_by_itself_or_when_ended_and_the_servers_switches_come_back()
        {
            var trial = new WardRepair.Trial();
            trial.Start(network: false, auto: true, seconds: 1, now: 100, lasts: 30);

            var after = trial.Effective(network: false, auto: false, seconds: 10, now: 131, testWard: true);
            Assert.False(after.Auto);
            Assert.Equal(10, after.Seconds);

            trial.Start(network: false, auto: true, seconds: 1, now: 200, lasts: 30);
            trial.End();
            Assert.False(trial.Effective(false, false, 10, now: 201, testWard: true).Auto);
        }

        [Fact]
        public void A_trial_never_switches_off_what_the_server_has_on()
        {
            var trial = new WardRepair.Trial();
            trial.Start(network: false, auto: true, seconds: 1, now: 0, lasts: 30);

            var on = trial.Effective(network: true, auto: false, seconds: 10, now: 1, testWard: true);

            Assert.True(on.Network);
            Assert.True(on.Auto);

            trial.Start(network: true, auto: false, seconds: 1, now: 0, lasts: 30);
            Assert.True(trial.Effective(network: false, auto: true, seconds: 10, now: 1, testWard: true).Auto);
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
        public void The_test_spots_lie_in_the_players_loaded_zones_nearest_first_and_off_the_player()
        {
            // The player at (10, -20) stands in zone (0, 0); with one zone around, the loaded ground spans -96 to 96.
            var spots = WardTestSite.Candidates(10, -20, zoneSize: 64, activeArea: 1, step: 16, minDistance: 12, inset: 4);

            Assert.NotEmpty(spots);
            Assert.All(spots, s => Assert.InRange(s.X, -92f, 92f));
            Assert.All(spots, s => Assert.InRange(s.Z, -92f, 92f));
            Assert.All(spots, s => Assert.True(Dist(s.X, s.Z, 10, -20) >= 12));
            for (int i = 1; i < spots.Count; i++)
                Assert.True(Dist(spots[i - 1].X, spots[i - 1].Z, 10, -20) <= Dist(spots[i].X, spots[i].Z, 10, -20));
        }

        [Fact]
        public void The_test_ward_goes_where_its_area_touches_no_real_wards_area()
        {
            var spots = new List<WardTestSite.Spot> { new WardTestSite.Spot(0, 0), new WardTestSite.Spot(100, 0), new WardTestSite.Spot(200, 0) };
            var wards = new List<WardTestSite.Circle> { new WardTestSite.Circle(30, 0, 32), new WardTestSite.Circle(100, 60, 32) };

            var picked = WardTestSite.Pick(spots, wards, testRadius: 32, margin: 4);

            Assert.NotNull(picked);
            Assert.Equal(200f, picked.Value.X);
        }

        [Fact]
        public void The_test_objects_ids_reach_the_server_whole_through_the_links_encoding()
        {
            var ids = new List<WardTestSite.Id> { new WardTestSite.Id(123456789012, 7), new WardTestSite.Id(-5, 4000000000) };

            var line = KeyValues.Encode(new Dictionary<string, object> { { "step", "made" }, { "ids", WardTestSite.FormatIds(ids) } });
            KeyValues.Decode(line).TryGetValue("ids", out string arrived);

            Assert.Equal(ids, WardTestSite.ParseIds(arrived));
        }

        [Fact]
        public void The_players_spots_reach_the_server_whole_through_the_links_encoding()
        {
            var spots = WardTestSite.Candidates(3072.5f, -3000.25f, 64, 1, 8, 12, 6);

            var line = KeyValues.Encode(new Dictionary<string, object> { { "step", "site" }, { "spots", WardTestSite.FormatSpots(spots) } });
            KeyValues.Decode(line).TryGetValue("spots", out string arrived);

            Assert.Equal(spots, WardTestSite.ParseSpots(arrived));
        }

        [Fact]
        public void The_servers_spot_reaches_the_player_whole_through_the_links_encoding()
        {
            var spot = new WardTestSite.Spot(-3016f, 2944.5f);

            var line = KeyValues.Encode(new Dictionary<string, object> { { "ok", 1 }, { "spot", WardTestSite.FormatSpots(new[] { spot }) } });
            KeyValues.Decode(line).TryGetValue("spot", out string arrived);

            Assert.Equal(new[] { spot }, WardTestSite.ParseSpots(arrived));
        }

        [Fact]
        public void The_server_records_only_test_objects_the_asker_placed_and_owns()
        {
            const long me = 4242;
            Assert.True(WardTestSite.Accepts(WardTestSite.WardPrefab, creator: me, asker: me, ownedByAsker: true));
            Assert.True(WardTestSite.Accepts(WardTestSite.WardPrefab, creator: WardTestSite.Stranger, asker: me, ownedByAsker: true));
            Assert.True(WardTestSite.Accepts(WardTestSite.WallPrefab, creator: me, asker: me, ownedByAsker: true));
            Assert.True(WardTestSite.Accepts(WardTestSite.DoorPrefab, creator: WardTestSite.Stranger, asker: me, ownedByAsker: true));
        }

        [Fact]
        public void A_door_is_recorded_and_removed_only_as_the_made_up_strangers()
        {
            const long me = 4242, friend = 777;
            // A door the tester built would open for the tester, so the test's door is the stranger's.
            Assert.False(WardTestSite.Accepts(WardTestSite.DoorPrefab, creator: me, asker: me, ownedByAsker: true));
            Assert.False(WardTestSite.Accepts(WardTestSite.DoorPrefab, creator: WardTestSite.Stranger, asker: me, ownedByAsker: false));
            Assert.True(WardTestSite.MayRemove(WardTestSite.DoorPrefab, creator: WardTestSite.Stranger, recordedFor: me));
            Assert.False(WardTestSite.MayRemove(WardTestSite.DoorPrefab, creator: friend, recordedFor: me));
            Assert.False(WardTestSite.MayRemove(WardTestSite.DoorPrefab, creator: me, recordedFor: me));
        }

        [Fact]
        public void A_real_ward_or_piece_is_never_recorded_as_a_test_object()
        {
            const long me = 4242, friend = 777;
            Assert.False(WardTestSite.Accepts(WardTestSite.WardPrefab, creator: friend, asker: me, ownedByAsker: true));
            Assert.False(WardTestSite.Accepts(WardTestSite.WardPrefab, creator: me, asker: me, ownedByAsker: false));
            Assert.False(WardTestSite.Accepts(WardTestSite.WallPrefab, creator: WardTestSite.Stranger, asker: me, ownedByAsker: true));
            Assert.False(WardTestSite.Accepts("piece_chest_wood", creator: me, asker: me, ownedByAsker: true));
        }

        [Fact]
        public void The_server_removes_a_recorded_object_only_while_it_still_is_a_test_object()
        {
            const long me = 4242, friend = 777;
            Assert.True(WardTestSite.MayRemove(WardTestSite.WardPrefab, creator: WardTestSite.Stranger, recordedFor: me));
            Assert.True(WardTestSite.MayRemove(WardTestSite.WallPrefab, creator: me, recordedFor: me));
            Assert.False(WardTestSite.MayRemove(WardTestSite.WardPrefab, creator: friend, recordedFor: me));
            Assert.False(WardTestSite.MayRemove("piece_chest_wood", creator: me, recordedFor: me));
        }

        [Fact]
        public void The_right_password_is_typed_only_at_a_recorded_ward_of_the_made_up_stranger()
        {
            Assert.True(WardTestSite.TakesGuess(recorded: true, creator: WardTestSite.Stranger));
            Assert.False(WardTestSite.TakesGuess(recorded: true, creator: 4242));
            Assert.False(WardTestSite.TakesGuess(recorded: false, creator: WardTestSite.Stranger));
        }

        [Fact]
        public void A_ward_looks_up_the_pieces_in_its_area_again_after_a_while_not_on_every_turn()
        {
            Assert.False(WardRepair.PiecesDue(builtAt: 100, now: 100 + WardRepair.PieceListSeconds - 1));
            Assert.True(WardRepair.PiecesDue(builtAt: 100, now: 100 + WardRepair.PieceListSeconds));
            Assert.True(WardRepair.PiecesDue(builtAt: double.NegativeInfinity, now: 0));
        }

        [Fact]
        public void The_test_ward_is_small_enough_to_fit_beside_a_base_and_still_covers_its_wall()
        {
            Assert.InRange(WardTestSite.TestRadius, WardTestSite.WallOffset + 1f, 8f);
        }

        [Fact]
        public void Without_a_spot_the_test_says_how_near_it_came_and_what_it_needs()
        {
            var spots = new List<WardTestSite.Spot> { new WardTestSite.Spot(0, 0), new WardTestSite.Spot(60, 0) };
            var wards = new List<WardTestSite.Circle> { new WardTestSite.Circle(25, 0, 20), new WardTestSite.Circle(100, 0, 32) };

            // Room at (0, 0): 5 m to the first ward's area; at (60, 0): 8 m to the second's. The best spot counts.
            Assert.Equal(8f, WardTestSite.BestClearance(spots, wards), 3);
            Assert.Equal("no spot in the zones around you keeps a test ward clear of the switched-on wards: the best is 5 m from a ward's area, the test needs 9 m (10 spots looked at)",
                WardTestSite.NoSpotLine(5f, testRadius: 5f, margin: 4f, spots: 10));
            Assert.True(float.IsPositiveInfinity(WardTestSite.BestClearance(spots, new List<WardTestSite.Circle>())));
        }

        [Fact]
        public void No_test_spot_when_every_spot_lies_too_near_a_ward()
        {
            var spots = new List<WardTestSite.Spot> { new WardTestSite.Spot(0, 0), new WardTestSite.Spot(50, 0) };
            var wards = new List<WardTestSite.Circle> { new WardTestSite.Circle(25, 0, 32) };

            Assert.Null(WardTestSite.Pick(spots, wards, testRadius: 32, margin: 4));
        }

        [Fact]
        public void The_test_objects_ids_travel_to_the_server_and_back_whole()
        {
            var ids = new List<WardTestSite.Id> { new WardTestSite.Id(123456789012, 7), new WardTestSite.Id(-5, 4000000000) };

            var back = WardTestSite.ParseIds(WardTestSite.FormatIds(ids));

            Assert.Equal(ids, back);
            Assert.Empty(WardTestSite.ParseIds(""));
            Assert.Empty(WardTestSite.ParseIds("garbage;1:x;1:2:3"));
        }

        [Fact]
        public void The_server_sends_a_guess_only_at_a_ward_the_self_test_placed()
        {
            Assert.Null(WardPassword.ServerGuess(WardPassword.Step.Right, testWard: false, "Ymir!", out string why));
            Assert.Equal("only a ward the self-test placed takes a self-test guess", why);
            Assert.Equal("Ymir!", WardPassword.ServerGuess(WardPassword.Step.Right, testWard: true, "Ymir!", out _));
            Assert.Equal("Ymir!", WardPassword.ServerGuess(WardPassword.Step.Early, testWard: true, "Ymir!", out _));
        }

        [Fact]
        public void The_wrong_guess_is_never_the_password()
        {
            string wrong = WardPassword.ServerGuess(WardPassword.Step.Wrong, true, "Ymir!", out _);
            Assert.NotEqual("Ymir!", wrong);
            Assert.NotEqual(wrong, WardPassword.ServerGuess(WardPassword.Step.Wrong, true, wrong, out _));
        }

        [Fact]
        public void No_guess_without_a_password_a_player_can_type()
        {
            Assert.Null(WardPassword.ServerGuess(WardPassword.Step.Wrong, true, "", out string none));
            Assert.Equal("no password set on the server: nobody is asked", none);
            Assert.Null(WardPassword.ServerGuess(WardPassword.Step.Wrong, true, new string('x', WardPassword.MaxLength + 1), out string tooLong));
            Assert.Equal("the server's password is longer than " + WardPassword.MaxLength + " characters: nobody can type it", tooLong);
        }

        [Fact]
        public void A_wrong_guess_is_refused_the_right_one_waits_after_it_and_then_lets_in()
        {
            Assert.Equal(SelfTestReport.Result.Pass, WardPassword.StepCheck(WardPassword.Step.Wrong, WardPassword.Answer.Wrong, admin: false).Result);
            Assert.Equal(SelfTestReport.Result.Pass, WardPassword.StepCheck(WardPassword.Step.Early, WardPassword.Answer.Wrong, admin: false).Result);
            Assert.Equal(SelfTestReport.Result.Pass, WardPassword.StepCheck(WardPassword.Step.Right, WardPassword.Answer.LetIn, admin: false).Result);
            Assert.Equal("a wrong password was refused", WardPassword.StepCheck(WardPassword.Step.Wrong, WardPassword.Answer.Wrong, false).Text);
            Assert.Equal("the right password right after a wrong one was refused: the 2 s wait holds", WardPassword.StepCheck(WardPassword.Step.Early, WardPassword.Answer.Wrong, false).Text);
            Assert.Equal("the right password put you on the ward's list", WardPassword.StepCheck(WardPassword.Step.Right, WardPassword.Answer.LetIn, false).Text);
        }

        [Fact]
        public void A_guess_answered_the_wrong_way_or_not_at_all_fails()
        {
            Assert.Equal(SelfTestReport.Result.Fail, WardPassword.StepCheck(WardPassword.Step.Wrong, WardPassword.Answer.LetIn, false).Result);
            Assert.Equal(SelfTestReport.Result.Fail, WardPassword.StepCheck(WardPassword.Step.Early, WardPassword.Answer.LetIn, false).Result);
            Assert.Equal(SelfTestReport.Result.Fail, WardPassword.StepCheck(WardPassword.Step.Right, WardPassword.Answer.Wrong, false).Result);
            Assert.Equal(SelfTestReport.Result.Fail, WardPassword.StepCheck(WardPassword.Step.Wrong, WardPassword.Answer.ProtectiveWards, false).Result);
            Assert.Equal("the guess did not reach the server's check", WardPassword.StepCheck(WardPassword.Step.Right, null, false).Text);
            Assert.Equal(SelfTestReport.Result.Fail, WardPassword.StepCheck(WardPassword.Step.Right, null, false).Result);
        }

        [Fact]
        public void A_tester_the_server_counts_as_ward_admin_is_told_no_guess_was_tried()
        {
            var admin = WardPassword.StepCheck(WardPassword.Step.Wrong, null, admin: true);
            Assert.Equal(SelfTestReport.Result.Info, admin.Result);
            Assert.Equal("the server counts you as a ward admin (god mode opens every ward), so no guess was tried", admin.Text);
        }

        [Fact]
        public void The_server_says_it_runs_the_ward_test_in_its_greeting()
        {
            Assert.Contains(Greeting.WardTest, Greeting.Own.Split(','));
            Assert.True(Greeting.ForEveryone(Greeting.WardTest));
        }

        private static double Dist(float ax, float az, float bx, float bz) => Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

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

        private struct Vector3 { }

        private class Component { }

        private sealed class ConfigEntry<T>
        {
            public T Value { get; set; }
        }

        private static class Pw
        {
            public class ProtectiveWards
            {
                internal static Dictionary<PrivateArea, int> wardIsRepairing = new Dictionary<PrivateArea, int>();

                public static ConfigEntry<bool> wardAccessProtectDoors = new ConfigEntry<bool>();

                public static bool IsPointInsideWardArea(PrivateArea ward, Vector3 point, float radius = 0f) => false;

                public static bool IsPointInsideWardArea(ZDO ward, Vector3 point, float radius = 0f) => false;

                public static bool IsObjectOwnedByPlayerWithWardAccess(Component component, Player interactingPlayer) => false;

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

                public static bool HasWardAdminAccess(long playerID) => false;

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

                private static ZDOID s_promptWardID;

                private static bool ShouldOfferPasswordEntry(PrivateArea ward, Player player) => false;

                internal static bool TryHandleInteraction(PrivateArea ward, Player player) => s_promptWardID.Equals(default(ZDOID));

                private static void ClosePrompt() { }

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

                internal static float GetConfiguredWardRange(ZDO zdo) => 32f;

                internal static float GetWardDefaultRadius() => 32f;
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

        // 2.1.0's door lock with its "Doors" setting holding a number, or without the builder's exemption.
        private static class NotBoolDoors
        {
            public class ProtectiveWards
            {
                public static ConfigEntry<int> wardAccessProtectDoors = new ConfigEntry<int>();

                public static bool IsActivePlayerWard(PrivateArea ward) => false;

                public static bool IsPointInsideWardArea(PrivateArea ward, Vector3 point, float radius = 0f) => false;

                public static bool IsObjectOwnedByPlayerWithWardAccess(Component component, Player interactingPlayer) => false;
            }
        }

        private static class NoBuilderCheck
        {
            public class ProtectiveWards
            {
                public static ConfigEntry<bool> wardAccessProtectDoors = new ConfigEntry<bool>();

                public static bool IsActivePlayerWard(PrivateArea ward) => false;

                public static bool IsPointInsideWardArea(PrivateArea ward, Vector3 point, float radius = 0f) => false;
            }
        }

        // 2.1.0's prompt without the fields the self-test reads: the door way in still works.
        private static class NoPromptState
        {
            public static class WardPasswordProtection
            {
                internal static bool TryHandleInteraction(PrivateArea ward, Player player) => false;
            }
        }

        // The prompt's ward kept as something other than a ZDOID: the self-test cannot read it.
        private static class OddPromptState
        {
            public static class WardPasswordProtection
            {
                private static int s_promptWardID;

                internal static bool TryHandleInteraction(PrivateArea ward, Player player) => s_promptWardID > 0;
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
