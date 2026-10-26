using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "Server password for wards" (<see cref="WardPassword"/>) on Protective Wards'
    /// own password prompt. On a player's game a postfix on <c>ShouldOfferPasswordEntry</c>, which its
    /// hover and its use both ask, offers the prompt at an active ward the player has no access to
    /// while the server has a password. On the server a prefix on <c>RPC_SubmitWardPasswordServer</c>
    /// reads the typed password, puts the package back where it was, and judges it
    /// (<see cref="WardPassword.Gate"/>): the server's password adds the player to that ward's
    /// permitted list and answers success through Protective Wards' own answer, as a ward's own
    /// password does; everything else goes on to Protective Wards. The password lives only in the
    /// server's config; the players' games are told only that there is one.
    /// </summary>
    internal static class WardPasswordPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.wardpassword";

        private static WardShape.PasswordShape s_shape;
        private static FieldInfo s_playerId, s_playerName;
        private static readonly WardPassword.Gate s_gate = new WardPassword.Gate();
        private static WardPassword.Answer? s_lastJudged;

        /// <summary>Synced from the server: it has a ward password.</summary>
        public static bool ServerHasOne;

        /// <summary>The server's ward password, read from its own config; unused on a player's game.</summary>
        public static string Password = "";

        public static bool Found { get; private set; }

        public static string Version { get; private set; }

        public static string Problem { get; private set; }

        /// <summary>On the server: players let in by its password since the start.</summary>
        public static int LetIn { get; private set; }

        public static string State => WardPassword.StateLine(ServerHasOne, Found, Version, Problem);

        public static void Hook(Action<string> log)
        {
            var info = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == WardRepairPatch.Guid);
            Found = info != null;
            Version = info?.Metadata.Version.ToString();
            if (!Found) return;
            try
            {
                s_shape = WardShape.FindPassword(TypeLookup.Loaded.Find);
                var context = s_shape?.RoutedPlayer.GetParameters()[2].ParameterType.GetElementType();
                s_playerId = context?.GetField("PlayerID");
                s_playerName = context?.GetField("PlayerName");
                if (s_shape == null || s_playerId == null || s_playerName == null)
                {
                    Problem = "its password prompt changed (an update)";
                    s_shape = null;
                    return;
                }
                var harmony = new Harmony(HarmonyId);
                harmony.Patch(s_shape.Offers, postfix: new HarmonyMethod(typeof(WardPasswordPatch), nameof(AfterOffers)));
                harmony.Patch(s_shape.Submitted, prefix: new HarmonyMethod(typeof(WardPasswordPatch), nameof(BeforeSubmitted)));
                log?.Invoke("Vigil: the ward password patch is hooked into Protective Wards " + Version + ".");
            }
            catch (Exception e)
            {
                Problem = "Protective Wards could not be hooked: " + e.GetType().Name + " " + e.Message;
                s_shape = null;
            }
        }

        private static void AfterOffers(PrivateArea ward, Player player, ref bool __result)
        {
            if (__result || !ServerHasOne || s_shape == null || ward == null || player == null) return;
            var view = ward.GetComponent<ZNetView>();
            if (view == null || !view.IsValid()) return;
            bool enabled = view.GetZDO().GetBool(ZDOVars.s_enabled);
            bool hasAccess = (bool)s_shape.Access.Invoke(null, new object[] { ward, player });
            __result = WardPassword.Offer(ServerHasOne, ward.m_ownerFaction == Character.Faction.Players, enabled, hasAccess, player.GetPlayerID(), wardOffers: false);
        }

        private static bool BeforeSubmitted(long sender, ZPackage package)
        {
            if (s_shape == null || string.IsNullOrEmpty(Password) || package == null) return true;
            int at = package.GetPos();
            ZDOID wardId;
            long claimed;
            string given;
            try
            {
                wardId = package.ReadZDOID();
                claimed = package.ReadLong();
                given = package.ReadString() ?? "";
            }
            catch (Exception)
            {
                return true;
            }
            finally
            {
                package.SetPos(at);
            }

            var routed = new object[] { sender, claimed, null };
            if (!(bool)s_shape.RoutedPlayer.Invoke(null, routed)) return true;
            long playerId = (long)s_playerId.GetValue(routed[2]);
            string playerName = (string)s_playerName.GetValue(routed[2]);
            var found = new object[] { wardId, null };
            if (!(bool)s_shape.WardById.Invoke(null, found)) return true;
            var zdo = (ZDO)found[1];
            bool hasAccess = (bool)s_shape.DirectAccess.Invoke(null, new object[] { zdo, playerId });
            bool wardHasOwn = (bool)s_shape.OwnPassword.Invoke(null, new object[] { zdo });

            var judged = s_gate.Judge(Password, given, playerId, hasAccess, wardHasOwn, Time.realtimeSinceStartupAsDouble);
            s_lastJudged = judged;
            switch (judged)
            {
                case WardPassword.Answer.LetIn:
                    s_shape.Permit.Invoke(null, new object[] { zdo, playerId, playerName });
                    s_shape.Answer.Invoke(null, new[] { sender, wardId, Enum.ToObject(s_shape.Results, s_shape.LetIn) });
                    LetIn++;
                    Debug.Log("[Vigil] " + playerName + " was let into ward " + wardId + " by the server's ward password.");
                    return false;
                case WardPassword.Answer.Wrong:
                    s_shape.Answer.Invoke(null, new[] { sender, wardId, Enum.ToObject(s_shape.Results, s_shape.Wrong) });
                    return false;
                default:
                    return true;
            }
        }

        // ------------------------------------------------------------------ for the self-test

        /// <summary>
        /// The server's side of a player's self-test: with a password set, a wrong guess for the asker at
        /// a ward they have no access to goes through Protective Wards' own handler, as the prompt sends
        /// it, and the patch's judgement comes back. The asker then waits as after any wrong guess.
        /// </summary>
        public static Dictionary<string, object> ServerCheck(ZNetPeer from)
        {
            WardPassword.Answer? judged = null;
            int wards = 0;
            bool reached = true;
            if (s_shape != null && WardPassword.Matches(Password, Password))
            {
                var zdo = from != null && !from.m_characterID.IsNone() ? ZDOMan.instance?.GetZDO(from.m_characterID) : null;
                if (zdo == null) return new Dictionary<string, object> { { "ok", 0 }, { "text", "the server has no character of yours" } };
                long playerId = zdo.GetLong(ZDOVars.s_playerID);
                var all = s_shape.AllWards != null ? ((IEnumerable)s_shape.AllWards.Invoke(null, null)).Cast<ZDO>().Where(w => w != null).ToList() : new List<ZDO>();
                wards = all.Count;
                var foreign = all.FirstOrDefault(w => !(bool)s_shape.DirectAccess.Invoke(null, new object[] { w, playerId }));
                if (foreign != null)
                {
                    string guess = "vigil self-test " + Password.Length;
                    if (guess == Password) guess += "!";
                    var package = new ZPackage();
                    package.Write(foreign.m_uid);
                    package.Write(playerId);
                    package.Write(guess);
                    package.SetPos(0);
                    s_lastJudged = null;
                    s_shape.Submitted.Invoke(null, new object[] { from.m_uid, package });
                    judged = s_lastJudged;
                    reached = judged != null;
                }
            }
            var verdict = !reached
                ? new WardVerdict(SelfTestReport.Result.Fail, "set; the guess did not reach the server's check (Protective Wards did not take you as the sender)")
                : WardPassword.ServerCheck(Found, Problem, Password, judged, wards);
            return new Dictionary<string, object> { { "ok", 1 }, { "result", verdict.Result.ToString() }, { "text", verdict.Text } };
        }

        /// <summary>
        /// On a player's game: Protective Wards' own prompt check, as patched, at the nearest active ward
        /// within 64 m that the player has no access to; null when there is none.
        /// </summary>
        public static bool? OfferedNearby(Player player)
        {
            var areas = WardRepairPatch.Areas;
            if (s_shape == null || areas == null || player == null) return null;
            Vector3 at = player.transform.position;
            var ward = areas.Where(a => a != null && a.m_ownerFaction == Character.Faction.Players && Vector3.Distance(a.transform.position, at) <= 64f && Enabled(a)
                    && !(bool)s_shape.Access.Invoke(null, new object[] { a, player }))
                .OrderBy(a => Vector3.Distance(a.transform.position, at)).FirstOrDefault();
            if (ward == null) return null;
            return (bool)s_shape.Offers.Invoke(null, new object[] { ward, player });
        }

        private static bool Enabled(PrivateArea ward)
        {
            var view = ward.GetComponent<ZNetView>();
            return view != null && view.IsValid() && view.GetZDO().GetBool(ZDOVars.s_enabled);
        }
    }
}
