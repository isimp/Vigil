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

        // ------------------------------------------------------------------ the server's part of the self-test

        private static readonly Dictionary<long, List<WardTestSite.Id>> s_placed = new Dictionary<long, List<WardTestSite.Id>>();
        private static bool s_placedRead;

        /// <summary>The server's record of what self-tests placed (Vigil-state), so leftovers of a test cut short are removed.</summary>
        public static string RecordFile;

        /// <summary>
        /// The server's part of a player's ward self-test, by step: "site" finds a spot clear of every
        /// ward of the world (<see cref="WardTestSite.Pick"/>) after removing what this player's earlier
        /// tests left; "made" records the objects the test placed; "gone" removes what is left of them
        /// and forgets them; "wrong", "early" and "right" send Protective Wards' own package with a
        /// guess (<see cref="WardPassword.ServerGuess"/>) for the asker at a recorded ward through its
        /// patched handler and answer what the patch decided.
        /// </summary>
        public static Dictionary<string, object> ServerStep(Dictionary<string, string> args, ZNetPeer from)
        {
            if (s_shape == null) return Failed(Found ? "stepped aside on the server: " + Problem : "Protective Wards is not on the server");
            var character = from != null && !from.m_characterID.IsNone() ? ZDOMan.instance?.GetZDO(from.m_characterID) : null;
            if (character == null) return Failed("the server has no character of yours");
            long playerId = character.GetLong(ZDOVars.s_playerID);
            ReadPlaced();
            args.TryGetValue("step", out string step);
            args.TryGetValue("ids", out string idText);
            var reply = new Dictionary<string, object> { { "ok", 1 } };
            switch (step)
            {
                case "site":
                    Remove(playerId, null);
                    reply["spot"] = Site(args.TryGetValue("spots", out string spots) ? spots : "");
                    return reply;
                case "made":
                    if (!s_placed.TryGetValue(playerId, out var list)) s_placed[playerId] = list = new List<WardTestSite.Id>();
                    list.AddRange(WardTestSite.ParseIds(idText));
                    SavePlaced();
                    return reply;
                case "gone":
                    Remove(playerId, WardTestSite.ParseIds(idText));
                    return reply;
                case "wrong":
                    return Guess(WardPassword.Step.Wrong, args, from.m_uid, playerId, reply);
                case "early":
                    return Guess(WardPassword.Step.Early, args, from.m_uid, playerId, reply);
                case "right":
                    return Guess(WardPassword.Step.Right, args, from.m_uid, playerId, reply);
                default:
                    return Failed("unknown step " + step);
            }
        }

        private static Dictionary<string, object> Guess(WardPassword.Step step, Dictionary<string, string> args, long sender, long playerId, Dictionary<string, object> reply)
        {
            var ward = WardTestSite.ParseIds(args.TryGetValue("ward", out string w) ? w : "").FirstOrDefault();
            var zdo = ZDOMan.instance.GetZDO(new ZDOID(ward.User, ward.Number));
            bool admin = s_shape.Admin != null && (bool)s_shape.Admin.Invoke(null, new object[] { playerId });
            reply["known"] = zdo != null ? 1 : 0;
            reply["admin"] = admin ? 1 : 0;
            if (zdo == null || admin) return reply;
            bool testWard = s_placed.TryGetValue(playerId, out var mine) && mine.Contains(ward);
            string guess = WardPassword.ServerGuess(step, testWard, Password, out string why);
            if (guess == null)
            {
                reply["why"] = why;
                return reply;
            }
            var package = new ZPackage();
            package.Write(zdo.m_uid);
            package.Write(playerId);
            package.Write(guess);
            package.SetPos(0);
            s_lastJudged = null;
            s_shape.Submitted.Invoke(null, new object[] { sender, package });
            reply["judged"] = s_lastJudged?.ToString() ?? "";
            reply["access"] = (bool)s_shape.DirectAccess.Invoke(null, new object[] { zdo, playerId }) ? 1 : 0;
            return reply;
        }

        private static string Site(string spotText)
        {
            var spots = new List<WardTestSite.Spot>();
            foreach (string pair in spotText.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] xz = pair.Split(',');
                if (xz.Length == 2 && float.TryParse(xz[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(xz[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                {
                    spots.Add(new WardTestSite.Spot(x, z));
                }
            }
            if (s_shape.AllWards == null || s_shape.Range == null || s_shape.DefaultRadius == null) return "";
            float fallback = (float)s_shape.DefaultRadius.Invoke(null, null);
            float testRadius = Math.Max(fallback, (float)s_shape.Range.Invoke(null, new object[] { null }));
            var wards = ((IEnumerable)s_shape.AllWards.Invoke(null, null)).Cast<ZDO>().Where(z => z != null)
                .Select(z => new WardTestSite.Circle(z.GetPosition().x, z.GetPosition().z, Math.Max(fallback, (float)s_shape.Range.Invoke(null, new object[] { z }))))
                .ToList();
            var spot = WardTestSite.Pick(spots, wards, testRadius, WardTestMargin);
            return spot == null ? "" : spot.Value.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + spot.Value.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Metres between the test ward's area and any real ward's.</summary>
        private const float WardTestMargin = 4f;

        /// <summary>Removes what the player's tests placed (all of it when <paramref name="only"/> is null) and forgets it.</summary>
        private static void Remove(long playerId, List<WardTestSite.Id> only)
        {
            if (!s_placed.TryGetValue(playerId, out var list)) return;
            foreach (var id in list.Where(i => only == null || only.Contains(i)).ToList())
            {
                Destroy(id);
                list.Remove(id);
            }
            if (list.Count == 0) s_placed.Remove(playerId);
            SavePlaced();
        }

        /// <summary>On the server once its world is up: removes everything earlier self-tests left.</summary>
        public static int RemoveLeftovers()
        {
            ReadPlaced();
            int removed = 0;
            foreach (var id in s_placed.Values.SelectMany(l => l).ToList()) removed += Destroy(id) ? 1 : 0;
            s_placed.Clear();
            SavePlaced();
            return removed;
        }

        private static bool Destroy(WardTestSite.Id id)
        {
            var zdo = ZDOMan.instance?.GetZDO(new ZDOID(id.User, id.Number));
            if (zdo == null) return false;
            zdo.SetOwner(ZDOMan.GetSessionID());
            ZDOMan.instance.DestroyZDO(zdo);
            return true;
        }

        private static void ReadPlaced()
        {
            if (s_placedRead || string.IsNullOrEmpty(RecordFile)) return;
            s_placedRead = true;
            try
            {
                if (!System.IO.File.Exists(RecordFile)) return;
                foreach (string line in System.IO.File.ReadAllLines(RecordFile))
                {
                    string[] two = line.Split('\t');
                    if (two.Length != 2 || !long.TryParse(two[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long player)) continue;
                    if (!s_placed.TryGetValue(player, out var list)) s_placed[player] = list = new List<WardTestSite.Id>();
                    list.AddRange(WardTestSite.ParseIds(two[1]));
                }
            }
            catch (Exception) { }
        }

        private static void SavePlaced()
        {
            if (string.IsNullOrEmpty(RecordFile)) return;
            try
            {
                System.IO.File.WriteAllLines(RecordFile, s_placed.Where(kv => kv.Value.Count > 0)
                    .Select(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" + WardTestSite.FormatIds(kv.Value)).ToArray());
            }
            catch (Exception) { }
        }

        private static Dictionary<string, object> Failed(string text) => new Dictionary<string, object> { { "ok", 0 }, { "text", text } };

        // ------------------------------------------------------------------ the player's part of the self-test

        /// <summary>Protective Wards' own prompt check, as patched, for the player at the ward.</summary>
        public static bool Offered(PrivateArea ward, Player player) => s_shape != null && (bool)s_shape.Offers.Invoke(null, new object[] { ward, player });

        /// <summary>Protective Wards' own access check (direct or through connected wards).</summary>
        public static bool HasAccess(PrivateArea ward, Player player) => s_shape != null && (bool)s_shape.Access.Invoke(null, new object[] { ward, player });

        public static bool Ready => s_shape != null;
    }
}
