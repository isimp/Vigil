using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Vigil.Core;
using Check = Vigil.Core.SelfTestReport.Check;
using Outcome = Vigil.Core.SelfTestReport.Result;

namespace Vigil.Recorder
{
    /// <summary>
    /// The self-test of the Protective Wards patches, wherever the player stands. The server picks a
    /// spot in the player's loaded zones clear of every ward of the world (<see cref="WardTestSite"/>),
    /// the test places its own objects there, tells the server what it placed, and removes them at
    /// the end; the server removes what a test cut short left. Repair: a ward of the player's and a
    /// wooden wall; the wall loses 1 health, a ward repairing by itself has to bring it back, then the
    /// ward's own alt-use (<c>PrivateArea.Interact</c>, as Alt+E) has to start it, show the count,
    /// bring the wall back and stop. Both run as a trial on the test ward alone
    /// (<see cref="WardRepair.Trial"/>). Password: a ward of nobody the player knows; Protective Wards'
    /// prompt has to be offered, then the server sends a wrong password, the right one at once (refused,
    /// the wait after a wrong guess) and the right one after the wait (let in), and the player's game
    /// has to see itself on the ward's list. A door of the stranger's in that ward has to show the
    /// password line and open the prompt for the ward on alt-use (<c>Player.Interact</c>, as Shift+E)
    /// before, and open on plain use after. God mode, which opens every ward to a ward admin, is off
    /// while the password test runs and back on after.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private const double WardTrialSeconds = 1, WardTrialLasts = 40;
        private static readonly HarmonyLib.AccessTools.FieldRef<Piece, long> s_pieceCreator = HarmonyLib.AccessTools.FieldRefAccess<Piece, long>("m_creator");
        private static readonly Action<Player, GameObject, bool, bool> s_playerInteract = HarmonyLib.AccessTools.MethodDelegate<Action<Player, GameObject, bool, bool>>(
            HarmonyLib.AccessTools.Method(typeof(Player), "Interact", new[] { typeof(GameObject), typeof(bool), typeof(bool) }));

        private IEnumerator WardRepairTried(List<Check> checks)
        {
            const string what = "ward repair, at a ward the self-test places";
            var player = Player.m_localPlayer;
            if (!WardTestCan(checks, what, player, WardRepairPatch.Found, WardRepairPatch.Ready, WardRepairPatch.Problem)) yield break;
            var spot = new WardTestSpot();
            yield return WardTestFindSpot(spot, player);
            if (spot.Why != null)
            {
                checks.Add(new Check(Here, what, spot.Outcome, spot.Why));
                yield break;
            }

            var placed = new List<GameObject>();
            PrivateArea ward = null;
            WearNTear wall = null;
            try
            {
                var wardObject = WardTestPlace(Vigil.Core.WardTestSite.WardPrefab, spot.At, player.GetPlayerID(), placed);
                var wallAt = spot.At + new Vector3(Vigil.Core.WardTestSite.WallOffset, 0f, 0f);
                wallAt.y = ZoneSystem.instance.GetGroundHeight(wallAt) - 0.05f;
                var wallObject = WardTestPlace(Vigil.Core.WardTestSite.WallPrefab, wallAt, player.GetPlayerID(), placed);
                ward = wardObject?.GetComponent<PrivateArea>();
                wall = wallObject?.GetComponent<WearNTear>();
                if (ward == null || wall == null)
                {
                    checks.Add(new Check(Here, what, Outcome.Fail, "the game has no " + (ward == null ? "ward" : Vigil.Core.WardTestSite.WallPrefab) + " to place"));
                    yield break;
                }
                WardTestEnable(ward);
                WardRepairPatch.TestWards.Add(ward);
                WardRepairPatch.KeepTestWardsSmall();
                yield return WardTestTell("made", placed);
                yield return null;

                // Repairing by itself: 1 health off, no start. Protective Wards repairs only a damaged
                // piece, so whether it would repair the wall is asked after the damage.
                var wallPiece = wall.GetComponent<Piece>();
                if (!WardRepairPatch.Damage(wall) || !WardRepairPatch.WouldRepair(wallPiece, ward))
                {
                    string state = "creator " + (wallPiece != null ? wallPiece.GetCreator().ToString(Ic) : "none") + ", health "
                        + (wall.GetHealthPercentage() * 100f).ToString("0.#", Ic) + " %";
                    checks.Add(new Check(Here, what, Outcome.Fail, "Protective Wards would not repair the test's wall, 1 health down, for its ward (" + state + ")"));
                    yield break;
                }
                WardRepairPatch.StartTrial(network: false, auto: true, WardTrialSeconds, WardTrialLasts);
                float from = Time.realtimeSinceStartup;
                while (WardRepairPatch.Health(wall) < wall.m_health && Time.realtimeSinceStartup < from + 10f) yield return null;
                checks.Add(WardRepairPatch.Health(wall) >= wall.m_health
                    ? new Check(Here, "a ward repairing by itself", Outcome.Pass, "the wall, 1 health down, was repaired without a start in " + (Time.realtimeSinceStartup - from).ToString("0.0", Ic) + " s (" + WardTrialSeconds.ToString("0", Ic) + " s a piece for the test)")
                    : new Check(Here, "a ward repairing by itself", Outcome.Fail, "the wall, 1 health down, was still damaged after 10 s"));
                WardRepairPatch.EndTrial();
                WardRepairPatch.MakeWhole(wall);
                from = Time.realtimeSinceStartup;
                while ((WardRepairPatch.IsStarted(ward) || WardRepairPatch.Shows(ward)) && Time.realtimeSinceStartup < from + 3f) yield return null;

                // The ward's alt-use: start, count, repair, stop.
                WardRepairPatch.Damage(wall);
                WardRepairPatch.StartTrial(network: true, auto: false, WardTrialSeconds, WardTrialLasts);
                WardRepairPatch.KeepTestWardsSmall();
                ward.Interact(player, false, true);
                var chain = WardRepairPatch.LastChain ?? new List<PrivateArea>();
                bool shown = WardRepairPatch.Shows(ward);
                var ownedHere = chain.Where(w => w != null && w.GetComponent<ZNetView>()?.IsOwner() == true).ToList();
                from = Time.realtimeSinceStartup;
                while (ownedHere.Any(w => !WardRepairPatch.StartedHere(w)) && Time.realtimeSinceStartup < from + 3f) yield return null;
                const string start = "a ward's alt-use starting repair on its connected base";
                var verdict = WardRepair.ChainCheck(chain.Count, ownedHere.Count, ownedHere.Count(WardRepairPatch.StartedHere), WardRepairPatch.LastDamaged);
                int real = chain.Count(w => w != null && !WardRepairPatch.TestWards.Contains(w));
                checks.Add(real > 0
                    ? new Check(Here, start, Outcome.Fail, "the test ward's chain reached " + real + " real wards")
                    : new Check(Here, start, verdict.Result, verdict.Text));
                if (chain.Count == 0) yield break;
                checks.Add(shown
                    ? new Check(Here, "the ward's hover shows the pieces left", Outcome.Pass, "Protective Wards' own hover list holds the ward")
                    : new Check(Here, "the ward's hover shows the pieces left", Outcome.Fail, "Protective Wards' hover list does not hold the ward"));
                from = Time.realtimeSinceStartup;
                while (WardRepairPatch.Health(wall) < wall.m_health && Time.realtimeSinceStartup < from + 10f) yield return null;
                checks.Add(WardRepairPatch.Health(wall) >= wall.m_health
                    ? new Check(Here, "the started ward repairing the damaged wall", Outcome.Pass, "whole again after " + (Time.realtimeSinceStartup - from).ToString("0.0", Ic) + " s")
                    : new Check(Here, "the started ward repairing the damaged wall", Outcome.Fail, "still damaged after 10 s"));
                from = Time.realtimeSinceStartup;
                while ((WardRepairPatch.IsStarted(ward) || WardRepairPatch.Left(ward) != WardRepair.NotRepairing) && Time.realtimeSinceStartup < from + 10f) yield return null;
                checks.Add(!WardRepairPatch.IsStarted(ward) && WardRepairPatch.Left(ward) == WardRepair.NotRepairing
                    ? new Check(Here, "the ward stopping once nothing is left", Outcome.Pass, "stopped, and its hover no longer counts")
                    : new Check(Here, "the ward stopping once nothing is left", Outcome.Fail, "still repairing 10 s after its last piece"));
            }
            finally
            {
                WardRepairPatch.EndTrial();
                if (ward != null) WardRepairPatch.TestWards.Remove(ward);
                WardTestRemove(placed);
            }
        }

        private IEnumerator WardPasswordTried(List<Check> checks)
        {
            const string what = "the server's ward password, at a ward the self-test places";
            var player = Player.m_localPlayer;
            if (!WardTestCan(checks, what, player, WardPasswordPatch.Found, WardPasswordPatch.Ready, WardPasswordPatch.Problem)) yield break;
            var spot = new WardTestSpot();
            yield return WardTestFindSpot(spot, player);
            if (spot.Why != null)
            {
                checks.Add(new Check(Here, what, spot.Outcome, spot.Why));
                yield break;
            }

            var placed = new List<GameObject>();
            bool god = player.InGodMode();
            try
            {
                if (god) player.SetGodMode(false);
                var ward = WardTestPlace(Vigil.Core.WardTestSite.WardPrefab, spot.At, Vigil.Core.WardTestSite.Stranger, placed)?.GetComponent<PrivateArea>();
                if (ward == null)
                {
                    checks.Add(new Check(Here, what, Outcome.Fail, "the game has no ward to place"));
                    yield break;
                }
                var doorAt = spot.At + new Vector3(Vigil.Core.WardTestSite.WallOffset, 0f, 0f);
                doorAt.y = ZoneSystem.instance.GetGroundHeight(doorAt) - 0.05f;
                var door = WardTestPlace(Vigil.Core.WardTestSite.DoorPrefab, doorAt, Vigil.Core.WardTestSite.Stranger, placed)?.GetComponent<Door>();
                WardTestEnable(ward);
                WardRepairPatch.TestWards.Add(ward);
                WardRepairPatch.KeepTestWardsSmall();
                yield return WardTestTell("made", placed);
                yield return null;

                const string prompt = "the password prompt at a ward you have no access to";
                WardRepairPatch.KeepTestWardsSmall();
                bool access = WardPasswordPatch.HasAccess(ward, player);
                bool offered = WardPasswordPatch.Offered(ward, player);
                checks.Add(access
                    ? new Check(Here, prompt, Outcome.Fail, "you have access to a ward nobody gave you")
                    : offered == WardPasswordPatch.ServerHasOne
                        ? new Check(Here, prompt, Outcome.Pass, offered ? "offered, as the server has a password" : "not offered, as the server has no password")
                        : new Check(Here, prompt, Outcome.Fail, offered ? "offered, though the server has no password" : "not offered, though the server has a password"));
                if (!access) WardTestDoorAsks(checks, door, ward, player);
                if (!WardPasswordPatch.ServerHasOne)
                {
                    checks.Add(new Check(Here, what, Outcome.Info, "no password set on the server: nobody is asked, so no guess was tried"));
                    yield break;
                }

                string wardId = WardTestSite.FormatIds(new[] { WardTestId(ward.gameObject) });
                foreach (var step in new[] { WardPassword.Step.Wrong, WardPassword.Step.Early, WardPassword.Step.Right })
                {
                    if (step == WardPassword.Step.Right) yield return new WaitForSecondsRealtime((float)WardPassword.WaitSeconds + 0.3f);
                    Answer a = null;
                    float until = Time.realtimeSinceStartup + (step == WardPassword.Step.Wrong ? 8f : 2f);
                    while (true)
                    {
                        a = Ask(Greeting.WardTest, new Dictionary<string, object> { { "step", step.ToString().ToLowerInvariant() }, { "ward", wardId } });
                        while (!a.Done) yield return null;
                        bool retry = a.Ok && (Said(a.Reply, "known", "0") || Said(a.Reply, "admin", "1"));
                        if (!retry || Time.realtimeSinceStartup >= until) break;
                        yield return new WaitForSecondsRealtime(1f);
                    }
                    string line = "the server's ward password: " + step.ToString().ToLowerInvariant() + " guess";
                    if (!a.Ok)
                    {
                        checks.Add(new Check(Here, line, Outcome.Fail, Why(a)));
                        yield break;
                    }
                    if (Said(a.Reply, "known", "0"))
                    {
                        checks.Add(new Check(Here, line, Outcome.Fail, "the server never learned of the test ward"));
                        yield break;
                    }
                    if (a.Reply.TryGetValue("why", out string why))
                    {
                        checks.Add(new Check(Here, line, Outcome.Info, why));
                        yield break;
                    }
                    a.Reply.TryGetValue("judged", out string judged);
                    WardPassword.Answer? answer = Enum.TryParse(judged, out WardPassword.Answer parsed) ? parsed : (WardPassword.Answer?)null;
                    var verdict = WardPassword.StepCheck(step, answer, Said(a.Reply, "admin", "1"));
                    checks.Add(new Check(Here, line, verdict.Result, verdict.Text));
                    if (verdict.Result != Outcome.Pass) yield break;
                }

                float from = Time.realtimeSinceStartup;
                while (!WardPasswordPatch.HasAccess(ward, player) && Time.realtimeSinceStartup < from + 5f) yield return null;
                checks.Add(WardPasswordPatch.HasAccess(ward, player)
                    ? new Check(Here, "your game seeing you on the ward's list", Outcome.Pass, "after " + (Time.realtimeSinceStartup - from).ToString("0.0", Ic) + " s")
                    : new Check(Here, "your game seeing you on the ward's list", Outcome.Fail, "not after 5 s"));
                if (door != null && WardPasswordPatch.DoorsReady) yield return WardTestDoorOpens(checks, door, player);
            }
            finally
            {
                WardRepairPatch.TestWards.RemoveWhere(w => w == null || placed.Contains(w.gameObject));
                WardTestRemove(placed);
                if (god && Player.m_localPlayer != null) Player.m_localPlayer.SetGodMode(true);
            }
            if (god) checks.Add(new Check(Here, "god mode during the password test", Outcome.Info, "off while it ran (it opens every ward to a ward admin), on again now"));
        }

        /// <summary>The test's locked door: the hover line and, on alt-use through the player's interact step, the prompt for its ward.</summary>
        private static void WardTestDoorAsks(List<Check> checks, Door door, PrivateArea ward, Player player)
        {
            const string what = "the password at a door the ward locks";
            if (door == null)
            {
                checks.Add(new Check(Here, what, Outcome.Fail, "the game has no " + Vigil.Core.WardTestSite.DoorPrefab + " to place"));
                return;
            }
            if (!WardPasswordPatch.DoorsReady)
            {
                checks.Add(new Check(Here, what, Outcome.Warn, WardPasswordPatch.DoorState));
                return;
            }
            bool line = door.GetHoverText().Contains(Localization.instance.Localize(WardPassword.DoorHoverLine).Trim());
            bool? opened = null;
            try
            {
                s_playerInteract(player, door.gameObject, false, true);
                var wardId = ward.GetComponent<ZNetView>().GetZDO().m_uid;
                if (WardPasswordPatch.SeesPrompt) opened = WardPasswordPatch.PromptWard == wardId;
            }
            finally
            {
                WardPasswordPatch.ClosePrompt();
            }
            var verdict = WardPassword.DoorPromptCheck(WardPasswordPatch.ServerHasOne, line, opened);
            checks.Add(new Check(Here, what, verdict.Result, verdict.Text));
        }

        /// <summary>After the password: plain use through the player's interact step opens the test's door.</summary>
        private static IEnumerator WardTestDoorOpens(List<Check> checks, Door door, Player player)
        {
            const string what = "the door the ward locked, after the password";
            var view = door.GetComponent<ZNetView>();
            if (WardPasswordPatch.DoorWard(door, player) != null)
            {
                checks.Add(new Check(Here, what, Outcome.Fail, "still locked by a ward"));
                yield break;
            }
            s_playerInteract(player, door.gameObject, false, false);
            float from = Time.realtimeSinceStartup;
            while (view != null && view.IsValid() && view.GetZDO().GetInt(ZDOVars.s_state) == 0 && Time.realtimeSinceStartup < from + 3f) yield return null;
            checks.Add(view != null && view.IsValid() && view.GetZDO().GetInt(ZDOVars.s_state) != 0
                ? new Check(Here, what, Outcome.Pass, "opened on plain use")
                : new Check(Here, what, Outcome.Fail, "did not open on plain use within 3 s"));
        }

        private bool _wardLeftoversDone;

        /// <summary>On the dedicated server, once its world has been up a minute: removes what self-tests cut short left.</summary>
        private void RemoveWardTestLeftovers()
        {
            if (!_server || _wardLeftoversDone || ZNet.instance == null || ZDOMan.instance == null || Time.realtimeSinceStartup < 60f) return;
            // Not before the world's objects are loaded: the record is forgotten once this has run.
            if (s_allObjects(ZDOMan.instance).Count == 0) return;
            _wardLeftoversDone = true;
            int removed = WardPasswordPatch.RemoveLeftovers();
            if (removed > 0) _host.Log.LogInfo("Vigil: removed " + removed + " objects an earlier ward self-test left in the world.");
        }

        // ------------------------------------------------------------------ the test's own objects

        private sealed class WardTestSpot
        {
            public Vector3 At;
            public string Why;
            public Outcome Outcome = Outcome.Info;
        }

        private bool WardTestCan(List<Check> checks, string what, Player player, bool found, bool ready, string problem)
        {
            if (player == null || ZNet.instance == null || ZoneSystem.instance == null || ZNetScene.instance == null)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "join a world first"));
                return false;
            }
            if (!found)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "Protective Wards is not installed"));
                return false;
            }
            if (!ready)
            {
                checks.Add(new Check(Here, what, Outcome.Fail, "stepped aside: " + problem));
                return false;
            }
            return CanAsk(checks, what, Greeting.WardTest, adminOnly: false);
        }

        /// <summary>Asks the server for a spot among the dry grid points of the player's loaded zones.</summary>
        private IEnumerator WardTestFindSpot(WardTestSpot spot, Player player)
        {
            var at = player.transform.position;
            var zones = ZoneSystem.instance;
            var dry = Vigil.Core.WardTestSite.Candidates(at.x, at.z, ZoneSystem.c_ZoneSize, activeArea: 1, step: 8, minDistance: 12, inset: 6)
                .Where(s => zones.GetGroundHeight(new Vector3(s.X, 0f, s.Z)) > zones.m_waterLevel + 1f)
                .Take(120)
                .ToList();
            if (dry.Count == 0)
            {
                spot.Why = "no dry ground in the zones around you";
                yield break;
            }
            var a = Ask(Greeting.WardTest, new Dictionary<string, object> { { "step", "site" }, { "spots", Vigil.Core.WardTestSite.FormatSpots(dry) } });
            while (!a.Done) yield return null;
            if (!a.Ok)
            {
                spot.Why = Why(a);
                spot.Outcome = Outcome.Fail;
                yield break;
            }
            if (a.Reply.TryGetValue("why", out string none))
            {
                spot.Why = none;
                yield break;
            }
            a.Reply.TryGetValue("spot", out string text);
            var picked = Vigil.Core.WardTestSite.ParseSpots(text);
            if (picked.Count != 1)
            {
                spot.Why = "the server's spot did not arrive whole: " + (text ?? "nothing");
                spot.Outcome = Outcome.Fail;
                yield break;
            }
            float x = picked[0].X, z = picked[0].Z;
            spot.At = new Vector3(x, zones.GetGroundHeight(new Vector3(x, 0f, z)) - 0.05f, z);
        }

        private static GameObject WardTestPlace(string prefabName, Vector3 at, long creator, List<GameObject> placed)
        {
            var prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null) return null;
            var made = UnityEngine.Object.Instantiate(prefab, at, Quaternion.identity);
            placed.Add(made);
            // The game's SetCreator also looks the creator up in the world's player history, which a
            // player's game need not hold; the test sets the creator as that does, without the lookup.
            var piece = made.GetComponent<Piece>();
            var view = made.GetComponent<ZNetView>();
            if (piece != null && view != null && view.IsValid())
            {
                view.GetZDO().Set(ZDOVars.s_creator, creator);
                s_pieceCreator(piece) = creator;
            }
            return made;
        }

        private static void WardTestEnable(PrivateArea ward)
        {
            var view = ward.GetComponent<ZNetView>();
            if (view != null && view.IsValid()) view.GetZDO().Set(ZDOVars.s_enabled, true);
        }

        private static WardTestSite.Id WardTestId(GameObject made)
        {
            var id = made.GetComponent<ZNetView>()?.GetZDO()?.m_uid ?? ZDOID.None;
            return new WardTestSite.Id(id.UserID, id.ID);
        }

        /// <summary>Tells the server what the test placed, again until it has recorded all of it (a new object reaches the server a moment later).</summary>
        private IEnumerator WardTestTell(string step, List<GameObject> placed)
        {
            var ids = placed.Where(o => o != null).Select(WardTestId).ToList();
            float until = Time.realtimeSinceStartup + 6f;
            while (true)
            {
                var a = Ask(Greeting.WardTest, new Dictionary<string, object> { { "step", step }, { "ids", Vigil.Core.WardTestSite.FormatIds(ids) } });
                while (!a.Done) yield return null;
                if (!a.Ok || Said(a.Reply, "recorded", ids.Count.ToString(CultureInfo.InvariantCulture)) || Time.realtimeSinceStartup >= until) yield break;
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        /// <summary>Removes the test's objects on this game and tells the server they are gone.</summary>
        private void WardTestRemove(List<GameObject> placed)
        {
            var ids = placed.Where(o => o != null).Select(WardTestId).ToList();
            foreach (var made in placed.Where(o => o != null))
            {
                var view = made.GetComponent<ZNetView>();
                if (view != null && view.IsValid() && !view.IsOwner()) view.ClaimOwnership();
                ZNetScene.instance?.Destroy(made);
            }
            placed.Clear();
            if (ids.Count > 0) Ask(Greeting.WardTest, new Dictionary<string, object> { { "step", "gone" }, { "ids", Vigil.Core.WardTestSite.FormatIds(ids) } });
        }
    }
}
