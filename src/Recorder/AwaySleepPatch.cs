using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "Away players don't hold up sleep" (<see cref="AwaySleep"/>). On a player's game
    /// the clock watches the game menu (<c>Menu.IsVisible</c>) and the reporter tells the server's
    /// Vigil when the player is away or back. On the server (or a game hosting its world) a postfix on
    /// <c>Game.EverybodyIsTryingToSleep</c> turns its "no" into "yes" when every character out of bed
    /// belongs to an away player and somebody is in bed; it goes through the game's own list of
    /// characters (<c>ZNet.GetAllCharacterZDOS</c>) and its bed flag. Around <c>Game.UpdateSleeping</c>
    /// the players in bed are told who was left out when that skip starts. On a player's game a
    /// prefix on <c>Player.SetSleeping</c> keeps an away player out of bed awake, so they get no morning.
    /// Every target is found by name; when one is missing, nothing is hooked and the reason is kept.
    /// The self-test tries it for a few seconds as a trial (<see cref="StartTrial"/>), which the
    /// server keeps but never counts, and asks the server to put its sleep check through three cases
    /// inside one call (<see cref="ServerCheck"/>), so the real night is never skipped by a test.
    /// </summary>
    internal static class AwaySleepPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.awaysleep";
        private const string StandInName = "a stand-in sleeper";

        private static MethodInfo s_everybody;
        private static FieldInfo s_gameSleeping, s_playerSleeping;
        private static List<string> s_leftOut;
        private static Case s_case;
        private static double s_trialUntil = double.NegativeInfinity, s_trialLimit;

        /// <summary>The server's switch and minutes, kept current by the config entries.</summary>
        public static bool On;

        public static double LimitSeconds = AwaySleep.LimitSeconds(5);

        /// <summary>This game's player: how long the game menu has been open.</summary>
        public static readonly AwayClock Clock = new AwayClock();

        /// <summary>This game's player: what the server was last told.</summary>
        public static readonly AwayReporter Reporter = new AwayReporter();

        /// <summary>On the server: the players reported away.</summary>
        public static readonly AwayBook Book = new AwayBook();

        /// <summary>The server's answer to this game's last report.</summary>
        public static Dictionary<string, string> LastAnswer;

        /// <summary>Times this game's player was kept out of a night's sleep.</summary>
        public static int LeftOutCount;

        /// <summary>Why the hooks did not go in; null when they are in.</summary>
        public static string Problem { get; private set; } = "not put in yet";

        public static bool TrialActive => Time.realtimeSinceStartupAsDouble < s_trialUntil;

        /// <summary>For the self-test: this game counts as away after <paramref name="limit"/> s in the menu, reported as a trial, for at most <paramref name="seconds"/>.</summary>
        public static void StartTrial(double limit, double seconds)
        {
            s_trialLimit = limit;
            s_trialUntil = Time.realtimeSinceStartupAsDouble + seconds;
        }

        public static void EndTrial() => s_trialUntil = double.NegativeInfinity;

        public static void Hook()
        {
            try
            {
                s_everybody = AccessTools.Method(typeof(Game), "EverybodyIsTryingToSleep");
                var updateSleeping = AccessTools.Method(typeof(Game), "UpdateSleeping");
                var setSleeping = AccessTools.Method(typeof(Player), "SetSleeping", new[] { typeof(bool) });
                s_gameSleeping = AccessTools.Field(typeof(Game), "m_sleeping");
                s_playerSleeping = AccessTools.Field(typeof(Player), "m_sleeping");
                var missing = new List<string>();
                if (s_everybody == null || s_everybody.ReturnType != typeof(bool)) missing.Add("Game.EverybodyIsTryingToSleep");
                if (updateSleeping == null) missing.Add("Game.UpdateSleeping");
                if (setSleeping == null) missing.Add("Player.SetSleeping");
                if (s_gameSleeping == null || s_playerSleeping == null) missing.Add("the game's sleeping flags");
                if (missing.Count > 0)
                {
                    Problem = "not found (an update changed it): " + string.Join(", ", missing.ToArray());
                    return;
                }

                var harmony = new Harmony(HarmonyId);
                harmony.Patch(s_everybody, postfix: new HarmonyMethod(typeof(AwaySleepPatch), nameof(AfterEverybody)));
                harmony.Patch(updateSleeping, prefix: new HarmonyMethod(typeof(AwaySleepPatch), nameof(BeforeUpdateSleeping)), postfix: new HarmonyMethod(typeof(AwaySleepPatch), nameof(AfterUpdateSleeping)));
                harmony.Patch(setSleeping, prefix: new HarmonyMethod(typeof(AwaySleepPatch), nameof(BeforeSetSleeping)));
                Problem = null;
            }
            catch (Exception e)
            {
                Problem = e.GetType().Name + " " + e.Message;
            }
        }

        // ------------------------------------------------------------------ this player's game

        /// <summary>Every frame on a player's game: the menu clock, and a report to the server when the answer changes.</summary>
        public static void Update(Link link, double now)
        {
            bool inWorld = Player.m_localPlayer != null && ZNet.instance != null;
            bool trial = TrialActive;
            Clock.Update(inWorld, inWorld && Menu.IsVisible(), now, trial ? s_trialLimit : LimitSeconds);
            if (ZNet.instance != null && ZNet.instance.IsServer()) return;
            var report = Reporter.Next(On, Clock.Away, trial, link.Server.Has(Greeting.Away));
            if (report == null) return;
            link.Ask(Greeting.Away, new Dictionary<string, object> { { "away", report.Value.Away ? 1 : 0 }, { "trial", report.Value.Trial ? 1 : 0 } },
                (ok, reply) => LastAnswer = ok ? reply : null);
        }

        /// <summary>What the patch does on this game, in words.</summary>
        public static string State(Link link)
        {
            bool inWorld = Player.m_localPlayer != null && ZNet.instance != null;
            bool hosting = ZNet.instance != null && ZNet.instance.IsServer();
            if (hosting && Player.m_localPlayer == null)
            {
                Forget();
                return !On ? "off: the switch" : Problem != null ? "stepped aside: " + Problem : "working · on the server, " + Book.Count + " reported away";
            }
            string lacking = hosting ? null : link.Server.Lacking(Greeting.Away, "take away reports", adminOnly: false);
            double now = Time.realtimeSinceStartupAsDouble;
            return AwaySleep.StateLine(On, Problem, lacking, inWorld, Clock.MenuSeconds(now), LimitSeconds, Clock.Away && On);
        }

        private static bool BeforeSetSleeping(Player __instance, bool sleep)
        {
            if (__instance == null || __instance != Player.m_localPlayer) return true;
            if (!AwaySleep.LeftOutOfSleep(sleep, On || TrialActive, Clock.Away, __instance.InBed())) return true;
            LeftOutCount++;
            return false;
        }

        /// <summary>Whether the player is asleep by the game's own flag; for the self-test.</summary>
        public static bool Sleeping(Player p) => p != null && p.IsSleeping();

        /// <summary>Puts the game's own sleeping flag back without the morning, should a self-test leave it set.</summary>
        public static void ClearSleeping(Player p)
        {
            if (p != null && s_playerSleeping != null) s_playerSleeping.SetValue(p, false);
        }

        // ------------------------------------------------------------------ the server

        /// <summary>On the server: a player's game reports away or back.</summary>
        public static Dictionary<string, object> Reported(Dictionary<string, string> args, ZNetPeer from)
        {
            if (from == null) return new Dictionary<string, object> { { "ok", 0 }, { "text", "no such player on the server" } };
            bool away = args.TryGetValue("away", out string a) && a == "1";
            bool trial = args.TryGetValue("trial", out string t) && t == "1";
            Forget();
            string name = Recorder.PeerName(from);
            bool was = Book.IsAway(from.m_uid);
            Book.Report(from.m_uid, name, away, trial);
            if (!trial && was != Book.IsAway(from.m_uid))
                EventLog.Write("away", name, away ? "away: the game menu stayed open, not counted for sleep" + (On ? "" : " while the switch is on") : "back");
            Book.TryGet(from.m_uid, out var entry);
            return new Dictionary<string, object>
            {
                { "ok", 1 }, { "text", (away ? "away" : "back") + (trial ? " (a self-test, not counted)" : "") },
                { "away", entry != null && entry.Away ? 1 : 0 }, { "trial", entry != null && entry.Trial ? 1 : 0 }, { "counted", Book.IsAway(from.m_uid) ? 1 : 0 },
            };
        }

        /// <summary>Forgets the reports of players no longer connected.</summary>
        private static void Forget()
        {
            if (ZNet.instance == null || Book.Count == 0) return;
            Book.KeepOnly(ZNet.instance.GetPeers().Select(p => p.m_uid));
        }

        /// <summary>The self-test's case, set only inside its own call to the game's check.</summary>
        private sealed class Case
        {
            public bool StandIn;
        }

        /// <summary>The players as the game's check sees them: its own list of characters, each with the bed flag and the away report.</summary>
        private static List<Sleeper> Players(Case c)
        {
            var znet = ZNet.instance;
            var peers = znet.GetPeers();
            var list = new List<Sleeper>();
            foreach (var zdo in znet.GetAllCharacterZDOS())
            {
                if (zdo == null) continue;
                var peer = peers.FirstOrDefault(p => p.m_characterID == zdo.m_uid);
                bool inBed = zdo.GetBool(ZDOVars.s_inBed);
                if (peer != null) list.Add(new Sleeper(Recorder.PeerName(peer), inBed, Book.IsAway(peer.m_uid)));
                else list.Add(new Sleeper(Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "the host", inBed, Clock.Away && On));
            }
            if (c != null && c.StandIn) list.Add(new Sleeper(StandInName, true, false));
            return list;
        }

        private static void AfterEverybody(ref bool __result)
        {
            var c = s_case;
            if (c == null) s_leftOut = null;
            if (__result || (!On && c == null) || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            Forget();
            var v = AwaySleep.Judge(Players(c));
            if (!v.Skip) return;
            __result = true;
            if (c == null) s_leftOut = v.AwayNames;
        }

        private static void BeforeUpdateSleeping(Game __instance, out bool __state)
        {
            s_leftOut = null;
            __state = s_gameSleeping != null && (bool)s_gameSleeping.GetValue(__instance);
        }

        private static void AfterUpdateSleeping(Game __instance, bool __state)
        {
            var leftOut = s_leftOut;
            s_leftOut = null;
            if (__state || leftOut == null || leftOut.Count == 0 || s_gameSleeping == null || !(bool)s_gameSleeping.GetValue(__instance)) return;
            string line = AwaySleep.Notice(leftOut);
            int told = TellSleepers(line, null);
            EventLog.Write("sleep", "the server", line + "; told " + told + " in bed");
        }

        /// <summary>Shows <paramref name="line"/> top left to the players in bed, or to <paramref name="only"/> alone; the number told.</summary>
        private static int TellSleepers(string line, ZNetPeer only)
        {
            int told = 0;
            if (only != null)
            {
                ZRoutedRpc.instance?.InvokeRoutedRPC(only.m_uid, "ShowMessage", (int)MessageHud.MessageType.TopLeft, line);
                return 1;
            }
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (!peer.IsReady() || peer.m_characterID.IsNone()) continue;
                var zdo = ZDOMan.instance?.GetZDO(peer.m_characterID);
                if (zdo == null || !zdo.GetBool(ZDOVars.s_inBed)) continue;
                ZRoutedRpc.instance?.InvokeRoutedRPC(peer.m_uid, "ShowMessage", (int)MessageHud.MessageType.TopLeft, line);
                told++;
            }
            var own = Player.m_localPlayer;
            if (own != null && own.InBed() && MessageHud.instance != null)
            {
                MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, line);
                told++;
            }
            return told;
        }

        /// <summary>
        /// On the server, for an admin's self-test: the game's own sleep check asked three times inside
        /// this one call, with the asking player up, then reported away, each with a stand-in sleeper
        /// in bed, then away with nobody in bed; their own report is put back after. Then the notice is
        /// shown to them alone, marked as the self-test's.
        /// </summary>
        public static Dictionary<string, object> ServerCheck(ZNetPeer from)
        {
            var reply = new Dictionary<string, object> { { "ok", 1 } };
            if (Problem != null) return new Dictionary<string, object> { { "ok", 0 }, { "text", "stepped aside on the server: " + Problem } };
            if (from == null || Game.instance == null || ZNet.instance == null) return new Dictionary<string, object> { { "ok", 0 }, { "text", "no world or no such player on the server" } };
            var zdo = from.m_characterID.IsNone() ? null : ZDOMan.instance?.GetZDO(from.m_characterID);
            if (zdo == null) return new Dictionary<string, object> { { "ok", 0 }, { "text", "the server has no character of yours" } };
            if (zdo.GetBool(ZDOVars.s_inBed)) return new Dictionary<string, object> { { "ok", 1 }, { "inbed", 1 }, { "text", "you are in bed: get up to run the check" } };

            string name = Recorder.PeerName(from);
            var others = Players(null).Where(p => p.Name != name).ToList();
            reply["others"] = string.Join(", ", others.Select(p => p.Name + (p.InBed ? " (in bed)" : p.Away ? " (away)" : "")).ToArray());
            bool alone = others.Count == 0;
            Book.TryGet(from.m_uid, out var kept);
            var keptCopy = kept == null ? null : new AwayBook.Entry { Name = kept.Name, Away = kept.Away, Trial = kept.Trial };
            try
            {
                Ask(reply, "c1", "you up, a stand-in in bed", standIn: true, away: false, from, name, alone ? false : (bool?)null);
                Ask(reply, "c2", "you away, a stand-in in bed", standIn: true, away: true, from, name, alone ? true : (bool?)null);
                Ask(reply, "c3", "you away, nobody in bed", standIn: false, away: true, from, name, alone ? false : (bool?)null);
            }
            finally
            {
                s_case = null;
                s_leftOut = null;
                if (keptCopy == null) Book.Report(from.m_uid, name, false, false);
                else Book.Report(from.m_uid, keptCopy.Name, keptCopy.Away, keptCopy.Trial);
            }

            string line = "Vigil self-test: " + AwaySleep.Notice(new[] { name });
            TellSleepers(line, from);
            reply["notice"] = line;
            return reply;
        }

        /// <summary>One case: the game's check with this player's report and the stand-in as given, against the answer expected (alone: known; with others: as the rule has it for everyone online).</summary>
        private static void Ask(Dictionary<string, object> reply, string key, string label, bool standIn, bool away, ZNetPeer from, string name, bool? known)
        {
            Book.Report(from.m_uid, name, away, trial: false);
            var c = new Case { StandIn = standIn };
            s_case = c;
            bool got;
            try { got = (bool)s_everybody.Invoke(Game.instance, null); }
            finally { s_case = null; }
            var players = Players(c);
            bool expected = known ?? (AwaySleep.Judge(players).Skip || (players.Count(p => p.Name != StandInName) > 0 && players.Where(p => p.Name != StandInName).All(p => p.InBed)));
            var (passed, text) = AwaySleep.CheckLine(label, expected, got);
            reply[key] = text;
            reply[key + "ok"] = passed ? 1 : 0;
        }
    }
}
