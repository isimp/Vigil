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
    /// has to see itself on the ward's list. God mode, which opens every ward to a ward admin, is off
    /// while the password test runs and back on after.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private const double WardTrialSeconds = 1, WardTrialLasts = 40;
        private const string WardTestWall = "woodwall";
        private const long WardTestStranger = 1;
        private static readonly HarmonyLib.AccessTools.FieldRef<Piece, long> s_pieceCreator = HarmonyLib.AccessTools.FieldRefAccess<Piece, long>("m_creator");

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
                var wardObject = WardTestPlace("guard_stone", spot.At, player.GetPlayerID(), placed);
                var wallAt = spot.At + new Vector3(3f, 0f, 0f);
                wallAt.y = ZoneSystem.instance.GetGroundHeight(wallAt) - 0.05f;
                var wallObject = WardTestPlace(WardTestWall, wallAt, player.GetPlayerID(), placed);
                ward = wardObject?.GetComponent<PrivateArea>();
                wall = wallObject?.GetComponent<WearNTear>();
                if (ward == null || wall == null)
                {
                    checks.Add(new Check(Here, what, Outcome.Fail, "the game has no " + (ward == null ? "ward" : WardTestWall) + " to place"));
                    yield break;
                }
                WardTestEnable(ward);
                WardRepairPatch.TestWards.Add(ward);
                yield return WardTestTell("made", placed);
                yield return null;

                if (!WardRepairPatch.WouldRepair(wall.GetComponent<Piece>(), ward))
                {
                    checks.Add(new Check(Here, what, Outcome.Fail, "Protective Wards would not repair the test's wall for its ward"));
                    yield break;
                }

                // Repairing by itself: 1 health off, no start.
                WardRepairPatch.Damage(wall);
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
                var ward = WardTestPlace("guard_stone", spot.At, WardTestStranger, placed)?.GetComponent<PrivateArea>();
                if (ward == null)
                {
                    checks.Add(new Check(Here, what, Outcome.Fail, "the game has no ward to place"));
                    yield break;
                }
                WardTestEnable(ward);
                yield return WardTestTell("made", placed);
                yield return null;

                const string prompt = "the password prompt at a ward you have no access to";
                bool access = WardPasswordPatch.HasAccess(ward, player);
                bool offered = WardPasswordPatch.Offered(ward, player);
                checks.Add(access
                    ? new Check(Here, prompt, Outcome.Fail, "you have access to a ward nobody gave you")
                    : offered == WardPasswordPatch.ServerHasOne
                        ? new Check(Here, prompt, Outcome.Pass, offered ? "offered, as the server has a password" : "not offered, as the server has no password")
                        : new Check(Here, prompt, Outcome.Fail, offered ? "offered, though the server has no password" : "not offered, though the server has a password"));
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
            }
            finally
            {
                WardTestRemove(placed);
                if (god && Player.m_localPlayer != null) Player.m_localPlayer.SetGodMode(true);
            }
            if (god) checks.Add(new Check(Here, "god mode during the password test", Outcome.Info, "off while it ran (it opens every ward to a ward admin), on again now"));
        }

        private bool _wardLeftoversDone;

        /// <summary>On the dedicated server, once its world has been up a minute: removes what self-tests cut short left.</summary>
        private void RemoveWardTestLeftovers()
        {
            if (!_server || _wardLeftoversDone || ZNet.instance == null || ZDOMan.instance == null || Time.realtimeSinceStartup < 60f) return;
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
                .Select(s => s.X.ToString("R", CultureInfo.InvariantCulture) + "," + s.Z.ToString("R", CultureInfo.InvariantCulture))
                .ToList();
            if (dry.Count == 0)
            {
                spot.Why = "no dry ground in the zones around you";
                yield break;
            }
            var a = Ask(Greeting.WardTest, new Dictionary<string, object> { { "step", "site" }, { "spots", string.Join(";", dry.ToArray()) } });
            while (!a.Done) yield return null;
            if (!a.Ok)
            {
                spot.Why = Why(a);
                spot.Outcome = Outcome.Fail;
                yield break;
            }
            a.Reply.TryGetValue("spot", out string text);
            string[] xz = (text ?? "").Split(',');
            if (xz.Length != 2 || !float.TryParse(xz[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(xz[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
            {
                spot.Why = "every dry spot in the zones around you lies too near a ward: run it a little away from bases";
                yield break;
            }
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

        private IEnumerator WardTestTell(string step, List<GameObject> placed)
        {
            var ids = placed.Where(o => o != null).Select(WardTestId).ToList();
            var a = Ask(Greeting.WardTest, new Dictionary<string, object> { { "step", step }, { "ids", Vigil.Core.WardTestSite.FormatIds(ids) } });
            while (!a.Done) yield return null;
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
