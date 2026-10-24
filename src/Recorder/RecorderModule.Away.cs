using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Vigil.Core;
using Check = Vigil.Core.SelfTestReport.Check;
using Outcome = Vigil.Core.SelfTestReport.Result;

namespace Vigil.Recorder
{
    /// <summary>
    /// The self-test of "Away players don't hold up sleep" (<see cref="AwaySleepPatch"/>), for one player
    /// alone. On this game: the game menu opened for real with a limit of seconds instead of minutes,
    /// the server told "away" as a self-test (kept, never counted for sleep), the game's own "sleep now"
    /// tried on the player and refused, the menu closed and the server told "back". On the server, for
    /// admins: the game's own sleep check asked with the player up, away, and away with nobody in bed,
    /// against a stand-in sleeper, all inside one call, and the notice shown to the player alone.
    /// The trial ends on its own when its seconds run out, so a routine cut short leaves nothing counted.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private const double AwayTrialLimit = 1.5, AwayTrialSeconds = 30;

        private IEnumerator AwaySleepTried(List<Check> checks)
        {
            const string what = "away players and sleep, with the server";
            var player = Player.m_localPlayer;
            if (player == null || Menu.instance == null || ZNet.instance == null)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "join a world first"));
                yield break;
            }
            if (AwaySleepPatch.Problem != null)
            {
                checks.Add(new Check(Here, what, Outcome.Fail, "stepped aside: " + AwaySleepPatch.Problem));
                yield break;
            }
            if (!CanAsk(checks, what, Greeting.Away, adminOnly: false)) yield break;
            if (player.InBed())
            {
                checks.Add(new Check(Here, what, Outcome.Info, "you are in bed: get up to run it"));
                yield break;
            }
            if (Menu.IsVisible())
            {
                checks.Add(new Check(Here, what, Outcome.Info, "the game menu is open already: close it to run it"));
                yield break;
            }

            // The menu, for real: away after the trial's seconds, and the server told so as a self-test.
            AwaySleepPatch.LastAnswer = null;
            AwaySleepPatch.StartTrial(AwayTrialLimit, AwayTrialSeconds);
            Menu.instance.Show();
            float until = Time.realtimeSinceStartup + 5f;
            while (!AwaySleepPatch.Clock.Away && Time.realtimeSinceStartup < until) yield return null;
            bool counted = AwaySleepPatch.Clock.Away;
            checks.Add(counted
                ? new Check(Here, "the game menu open counts as away", Outcome.Pass, "away after " + AwayTrialLimit.ToString("0.0", Ic) + " s in the menu (the server's " + _awayMinutes.Value.ToString(Ic) + " min in play)")
                : new Check(Here, "the game menu open counts as away", Outcome.Fail, Menu.IsVisible() ? "the menu is open, but the player is not away after 5 s" : "the menu did not stay open"));

            until = Time.realtimeSinceStartup + 10f;
            while (!Said(AwaySleepPatch.LastAnswer, "away", "1") && Time.realtimeSinceStartup < until) yield return null;
            var heard = AwaySleepPatch.LastAnswer;
            checks.Add(Said(heard, "away", "1") && Said(heard, "trial", "1") && Said(heard, "counted", "0")
                ? new Check(Here, "the server heard \"away\"", Outcome.Pass, "kept as the self-test's, not counted for sleep")
                : new Check(Here, "the server heard \"away\"", Outcome.Fail, heard == null ? "no answer in 10 s" : "it answered " + Words(heard)));

            // The game's own "sleep now" on this player while away and out of bed: kept awake, no morning.
            int leftOut = AwaySleepPatch.LeftOutCount;
            player.SetSleeping(true);
            bool asleep = AwaySleepPatch.Sleeping(player);
            if (asleep) AwaySleepPatch.ClearSleeping(player);
            checks.Add(!asleep && AwaySleepPatch.LeftOutCount == leftOut + 1
                ? new Check(Here, "an away player out of bed gets no morning", Outcome.Pass, "the game's \"sleep now\" left you awake, so no Rested and no good morning")
                : new Check(Here, "an away player out of bed gets no morning", Outcome.Fail, asleep ? "the game put you to sleep (put back without the morning)" : "kept awake, but not by this patch"));

            // Back: the menu closed, present again, the server told so.
            Menu.instance.Hide();
            until = Time.realtimeSinceStartup + 10f;
            while (!Said(AwaySleepPatch.LastAnswer, "away", "0") && Time.realtimeSinceStartup < until) yield return null;
            AwaySleepPatch.EndTrial();
            var back = AwaySleepPatch.LastAnswer;
            checks.Add(!AwaySleepPatch.Clock.Away && Said(back, "away", "0")
                ? new Check(Here, "closing the menu: back, and the server heard it", Outcome.Pass, "present again")
                : new Check(Here, "closing the menu: back, and the server heard it", Outcome.Fail, AwaySleepPatch.Clock.Away ? "still away with the menu closed" : back == null ? "no answer in 10 s" : "it answered " + Words(back)));

            // The server's own sleep check, three cases inside one call.
            const string server = "the server's sleep check";
            if (!CanAsk(checks, server, Greeting.Away, adminOnly: true)) yield break;
            var a = Ask(AwaySleep.CheckCommand, null);
            while (!a.Done) yield return null;
            if (!a.Ok)
            {
                checks.Add(new Check(Here, server, Outcome.Fail, Why(a)));
                yield break;
            }
            if (Said(a.Reply, "inbed", "1"))
            {
                checks.Add(new Check(Here, server, Outcome.Info, Why(a)));
                yield break;
            }
            a.Reply.TryGetValue("others", out string others);
            string with = string.IsNullOrEmpty(others) ? "alone on the server" : "with " + others + " online, judged with them";
            foreach (string key in new[] { "c1", "c2", "c3" })
            {
                a.Reply.TryGetValue(key, out string line);
                checks.Add(new Check(Here, server, Said(a.Reply, key + "ok", "1") ? Outcome.Pass : Outcome.Fail, (line ?? "no answer for " + key) + " (" + with + ")"));
            }
            a.Reply.TryGetValue("notice", out string notice);
            checks.Add(notice != null
                ? new Check(Here, "the sleepers' notice", Outcome.Pass, "shown to you top left: " + notice)
                : new Check(Here, "the sleepers' notice", Outcome.Fail, "the server sent none"));
        }

        private static bool Said(Dictionary<string, string> reply, string key, string value) =>
            reply != null && reply.TryGetValue(key, out string v) && v == value;

        private static string Words(Dictionary<string, string> reply) =>
            reply.TryGetValue("text", out string t) ? t : string.Join(", ", reply);
    }
}
