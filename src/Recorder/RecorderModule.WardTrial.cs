using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Vigil.Core;
using Check = Vigil.Core.SelfTestReport.Check;
using Outcome = Vigil.Core.SelfTestReport.Result;

namespace Vigil.Recorder
{
    /// <summary>
    /// The self-test of the Protective Wards patches, for one player at one of their own wards. Both
    /// repairs run as a trial on this game alone (<see cref="WardRepair.Trial"/>, 1 s a piece): a piece
    /// this game owns in the ward's area loses 1 health, then repairing by itself has to bring it back
    /// without a start, and the ward's own alt-use (<c>PrivateArea.Interact</c>, as a player's Alt+E)
    /// has to start the chain, show the count in Protective Wards' hover, bring the piece back and stop.
    /// A piece the test left damaged gets its health back at the end. The password test asks the
    /// server to send a wrong guess through Protective Wards' own handler at a ward the player has no
    /// access to, and checks the prompt at such a ward nearby; letting in with the right password is
    /// left to a second player, since it would add the tester to someone's ward for good.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private const double WardTrialSeconds = 1, WardTrialLasts = 40;

        private IEnumerator WardAutoTried(List<Check> checks)
        {
            const string what = "a ward repairing by itself";
            if (!WardTrialReady(checks, what, out var player, out var ward)) yield break;
            if (WardRepairPatch.IsStarted(ward) || WardRepairPatch.Shows(ward))
            {
                checks.Add(new Check(Here, what, Outcome.Info, "the ward is repairing already: run it again once it is done"));
                yield break;
            }
            var wear = WardRepairPatch.DamageOne(ward, out string why);
            if (wear == null)
            {
                checks.Add(new Check(Here, what, Outcome.Info, why));
                yield break;
            }
            float started = Time.realtimeSinceStartup;
            try
            {
                WardRepairPatch.StartTrial(network: false, auto: true, WardTrialSeconds, WardTrialLasts);
                float until = started + 10f;
                while (WardRepairPatch.Health(wear) < wear.m_health && Time.realtimeSinceStartup < until) yield return null;
                bool whole = WardRepairPatch.Health(wear) >= wear.m_health;
                checks.Add(whole
                    ? new Check(Here, what, Outcome.Pass, "a piece 1 health down was repaired by its ward without a start, in " + (Time.realtimeSinceStartup - started).ToString("0.0", Ic) + " s (" + WardTrialSeconds.ToString("0", Ic) + " s a piece for the test)")
                    : new Check(Here, what, Outcome.Fail, "a piece 1 health down was still damaged after 10 s"));
            }
            finally
            {
                WardRepairPatch.EndTrial();
                WardRepairPatch.MakeWhole(wear);
            }
        }

        private IEnumerator WardChainTried(List<Check> checks)
        {
            const string what = "a ward's alt-use starting repair on the whole connected base";
            if (!WardTrialReady(checks, what, out var player, out var ward)) yield break;
            if (WardRepairPatch.IsStarted(ward) || WardRepairPatch.Shows(ward))
            {
                checks.Add(new Check(Here, what, Outcome.Info, "the ward is repairing already: run it again once it is done"));
                yield break;
            }
            var wear = WardRepairPatch.DamageOne(ward, out string why);
            if (wear == null)
            {
                checks.Add(new Check(Here, what, Outcome.Info, why));
                yield break;
            }
            try
            {
                WardRepairPatch.StartTrial(network: true, auto: false, WardTrialSeconds, WardTrialLasts);
                ward.Interact(player, false, true);
                var chain = WardRepairPatch.LastChain ?? new List<PrivateArea>();
                bool shown = WardRepairPatch.Shows(ward);
                var ownedHere = chain.Where(w => w != null && w.GetComponent<ZNetView>()?.IsOwner() == true).ToList();
                float until = Time.realtimeSinceStartup + 3f;
                while (ownedHere.Any(w => !WardRepairPatch.StartedHere(w)) && Time.realtimeSinceStartup < until) yield return null;
                var verdict = WardRepair.ChainCheck(chain.Count, ownedHere.Count, ownedHere.Count(WardRepairPatch.StartedHere), WardRepairPatch.LastDamaged);
                checks.Add(new Check(Here, what, verdict.Result, verdict.Text));
                if (chain.Count == 0) yield break;
                checks.Add(shown
                    ? new Check(Here, "the ward's hover shows the pieces left", Outcome.Pass, "Protective Wards' own hover list holds the ward")
                    : new Check(Here, "the ward's hover shows the pieces left", Outcome.Fail, "Protective Wards' hover list does not hold the ward"));

                float from = Time.realtimeSinceStartup;
                until = from + 10f;
                while (WardRepairPatch.Health(wear) < wear.m_health && Time.realtimeSinceStartup < until) yield return null;
                bool whole = WardRepairPatch.Health(wear) >= wear.m_health;
                checks.Add(whole
                    ? new Check(Here, "the started ward repairing the damaged piece", Outcome.Pass, "whole again after " + (Time.realtimeSinceStartup - from).ToString("0.0", Ic) + " s")
                    : new Check(Here, "the started ward repairing the damaged piece", Outcome.Fail, "still damaged after 10 s"));

                until = Time.realtimeSinceStartup + 10f;
                while ((WardRepairPatch.IsStarted(ward) || WardRepairPatch.Left(ward) != WardRepair.NotRepairing) && Time.realtimeSinceStartup < until) yield return null;
                int left = WardRepairPatch.Left(ward);
                checks.Add(!WardRepairPatch.IsStarted(ward) && left == WardRepair.NotRepairing
                    ? new Check(Here, "the ward stopping once nothing is left", Outcome.Pass, "stopped, and its hover no longer counts")
                    : left > 0
                        ? new Check(Here, "the ward stopping once nothing is left", Outcome.Info, "still repairing " + left + " more damaged pieces in its area; it stops when they are whole")
                        : new Check(Here, "the ward stopping once nothing is left", Outcome.Fail, "still marked started 10 s after its last piece"));
            }
            finally
            {
                WardRepairPatch.EndTrial();
                WardRepairPatch.MakeWhole(wear);
            }
        }

        private bool WardTrialReady(List<Check> checks, string what, out Player player, out PrivateArea ward)
        {
            player = Player.m_localPlayer;
            ward = null;
            if (player == null || ZNet.instance == null)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "join a world first"));
                return false;
            }
            if (!WardRepairPatch.Found)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "Protective Wards is not installed"));
                return false;
            }
            if (!WardRepairPatch.Ready)
            {
                checks.Add(new Check(Here, what, Outcome.Fail, "stepped aside: " + WardRepairPatch.Problem));
                return false;
            }
            ward = WardRepairPatch.OwnWardAround(player, out string why);
            if (ward == null) checks.Add(new Check(Here, what, Outcome.Info, why));
            return ward != null;
        }

        private IEnumerator WardPasswordTried(List<Check> checks)
        {
            const string what = "the server's ward password";
            var player = Player.m_localPlayer;
            if (player == null || ZNet.instance == null)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "join a world first"));
                yield break;
            }
            if (!WardPasswordPatch.Found)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "Protective Wards is not installed"));
                yield break;
            }

            const string prompt = "the password prompt at a ward you have no access to";
            bool? offered = WardPasswordPatch.OfferedNearby(player);
            checks.Add(offered == null
                ? new Check(Here, prompt, Outcome.Info, "no active ward of someone else within 64 m: not tried")
                : offered == WardPasswordPatch.ServerHasOne
                    ? new Check(Here, prompt, Outcome.Pass, offered.Value ? "offered, as the server has a password" : "not offered, as the server has no password")
                    : new Check(Here, prompt, Outcome.Fail, offered.Value ? "offered, though the server has no password" : "not offered, though the server has a password"));

            if (!CanAsk(checks, what, Greeting.Wards, adminOnly: false)) yield break;
            var a = Ask(Greeting.Wards, null);
            while (!a.Done) yield return null;
            if (!a.Ok)
            {
                checks.Add(new Check(Here, what, Outcome.Fail, Why(a)));
                yield break;
            }
            a.Reply.TryGetValue("result", out string result);
            checks.Add(new Check(Here, what, Enum.TryParse(result, out Outcome outcome) ? outcome : Outcome.Fail, Why(a)));
        }
    }
}
