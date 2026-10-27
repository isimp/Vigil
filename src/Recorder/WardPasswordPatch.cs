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
        private static Func<PrivateArea, Player, bool> s_access, s_offers;
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
                s_access = (Func<PrivateArea, Player, bool>)Delegate.CreateDelegate(typeof(Func<PrivateArea, Player, bool>), s_shape.Access);
                harmony.Patch(s_shape.Offers, postfix: new HarmonyMethod(typeof(WardPasswordPatch), nameof(AfterOffers)));
                s_offers = (Func<PrivateArea, Player, bool>)Delegate.CreateDelegate(typeof(Func<PrivateArea, Player, bool>), s_shape.Offers);
                harmony.Patch(s_shape.Submitted, prefix: new HarmonyMethod(typeof(WardPasswordPatch), nameof(BeforeSubmitted)));
                log?.Invoke("Vigil: the ward password patch is hooked into Protective Wards " + Version + ".");
            }
            catch (Exception e)
            {
                Problem = "Protective Wards could not be hooked: " + e.GetType().Name + " " + e.Message;
                s_shape = null;
                return;
            }
            HookDoors(log);
        }

        // ------------------------------------------------------------------ doors a ward locks

        private static WardShape.DoorShape s_door;
        private static Func<PrivateArea, bool> s_active;
        private static Func<PrivateArea, Vector3, float, bool> s_inside;
        private static Func<Component, Player, bool> s_madeBy;
        private static Func<PrivateArea, Player, bool> s_prompt;
        private static BepInEx.Configuration.ConfigEntry<bool> s_locksDoors;

        /// <summary>The game's list of loaded wards, the one Protective Wards' door lock walks.</summary>
        private static readonly AccessTools.FieldRef<List<PrivateArea>> s_allAreas =
            AccessTools.StaticFieldRefAccess<List<PrivateArea>>(AccessTools.Field(typeof(PrivateArea), "m_allAreas"));

        /// <summary>Why the door part stepped aside while the ward part works; null when it works.</summary>
        public static string DoorProblem { get; private set; }

        /// <summary>
        /// At a door a ward locks, the player is asked for that ward's password too: a postfix on
        /// <c>Door.GetHoverText</c> adds Protective Wards' own "enter password" line for alt-use, and a
        /// prefix on <c>Player.Interact</c> opens its prompt for that ward on alt-use. The player's
        /// interact step is patched, not the door's, because every prefix of the door runs even after
        /// one skips it, and Protective Wards' own then shows its "no access" message over the prompt.
        /// </summary>
        private static void HookDoors(Action<string> log)
        {
            try
            {
                s_door = WardShape.FindDoor(TypeLookup.Loaded.Find);
                var interact = AccessTools.Method(typeof(Player), "Interact", new[] { typeof(GameObject), typeof(bool), typeof(bool) });
                if (s_door == null || interact == null)
                {
                    DoorProblem = "its door lock changed (an update)";
                    s_door = null;
                    return;
                }
                s_active = (Func<PrivateArea, bool>)Delegate.CreateDelegate(typeof(Func<PrivateArea, bool>), s_door.Active);
                s_inside = (Func<PrivateArea, Vector3, float, bool>)Delegate.CreateDelegate(typeof(Func<PrivateArea, Vector3, float, bool>), s_door.Inside);
                s_madeBy = (Func<Component, Player, bool>)Delegate.CreateDelegate(typeof(Func<Component, Player, bool>), s_door.MadeBy);
                s_prompt = (Func<PrivateArea, Player, bool>)Delegate.CreateDelegate(typeof(Func<PrivateArea, Player, bool>), s_door.Prompt);
                var harmony = new Harmony(HarmonyId);
                harmony.Patch(AccessTools.Method(typeof(Door), nameof(Door.GetHoverText)), postfix: new HarmonyMethod(typeof(WardPasswordPatch), nameof(AfterDoorHover)));
                harmony.Patch(interact, prefix: new HarmonyMethod(typeof(WardPasswordPatch), nameof(BeforeInteract)) { priority = Priority.First });
                log?.Invoke("Vigil: doors a ward locks ask for its password too.");
            }
            catch (Exception e)
            {
                DoorProblem = "its doors could not be hooked: " + e.GetType().Name + " " + e.Message;
                s_door = null;
            }
        }

        /// <summary>The ward the player is asked about at the door (<see cref="WardPassword.DoorWard"/>), or null.</summary>
        public static PrivateArea DoorWard(Door door, Player player)
        {
            if (s_door == null || door == null || player == null) return null;
            // The setting is read when first needed: Protective Wards binds it after Vigil hooks.
            if (s_locksDoors == null) s_locksDoors = s_door.LocksDoors.GetValue(null) as BepInEx.Configuration.ConfigEntry<bool>;
            bool wardsLockDoors = door.m_checkGuardStone || (s_locksDoors?.Value ?? true);
            if (!wardsLockDoors) return null;
            var at = door.transform.position;
            return WardPassword.DoorWard(s_allAreas(),
                ward => WardPassword.LocksDoor(true, s_active(ward), s_inside(ward, at, 0f), s_access(ward, player), s_madeBy(door, player)),
                ward => s_offers(ward, player));
        }

        private static void AfterDoorHover(Door __instance, ref string __result)
        {
            if (string.IsNullOrEmpty(__result) || Localization.instance == null || DoorWard(__instance, Player.m_localPlayer) == null) return;
            // Localized at each hover, as the game's own lines are: the key names follow keyboard or gamepad.
            __result += Localization.instance.Localize(WardPassword.DoorHoverLine);
        }

        private static bool BeforeInteract(Player __instance, GameObject go, bool hold, bool alt)
        {
            if (!WardPassword.DoorUseAsks(hold, alt) || go == null || __instance != Player.m_localPlayer || __instance.InAttack() || __instance.InDodge()) return true;
            var door = go.GetComponentInParent<Interactable>() as Door;
            var ward = DoorWard(door, __instance);
            return ward == null || !s_prompt(ward, __instance);
        }

        /// <summary>The ward Protective Wards' open prompt is for (none when closed or unreadable).</summary>
        public static ZDOID PromptWard => s_door?.PromptWard != null ? (ZDOID)s_door.PromptWard.GetValue(null) : ZDOID.None;

        public static bool SeesPrompt => s_door?.PromptWard != null;

        /// <summary>Closes Protective Wards' prompt, as its own cancel does.</summary>
        public static void ClosePrompt() => s_door?.ClosePrompt?.Invoke(null, null);

        public static bool DoorsReady => s_door != null;

        public static string DoorState => WardPassword.DoorStateLine(ServerHasOne, Found, Version, Problem, DoorProblem);

        private static void AfterOffers(PrivateArea ward, Player player, ref bool __result)
        {
            if (__result || !ServerHasOne || s_access == null || ward == null || player == null || ward.m_ownerFaction != Character.Faction.Players) return;
            var view = ward.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || !view.GetZDO().GetBool(ZDOVars.s_enabled)) return;
            __result = WardPassword.Offer(ServerHasOne, playerWard: true, enabled: true, s_access(ward, player), player.GetPlayerID(), wardOffers: false);
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
                    string at = Site(args.TryGetValue("spots", out string spots) ? spots : "", out string none);
                    if (at != null) reply["spot"] = at;
                    else reply["why"] = none;
                    return reply;
                case "made":
                    if (!s_placed.TryGetValue(playerId, out var list)) s_placed[playerId] = list = new List<WardTestSite.Id>();
                    // A game asks again until the server has every object; the ones it has are not added twice.
                    var taken = WardTestSite.ParseIds(idText).Where(id => list.Contains(id) || Accepted(id, playerId, from.m_uid)).ToList();
                    list.AddRange(taken.Where(id => !list.Contains(id)).ToList());
                    SavePlaced();
                    reply["recorded"] = taken.Count;
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
            bool testWard = WardTestSite.TakesGuess(s_placed.TryGetValue(playerId, out var mine) && mine.Contains(ward), zdo.GetLong(ZDOVars.s_creator));
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

        /// <summary>The spot as "x,z", or null with the reason in <paramref name="why"/>.</summary>
        private static string Site(string spotText, out string why)
        {
            why = null;
            var spots = WardTestSite.ParseSpots(spotText);
            if (spots.Count == 0)
            {
                why = "the server got no spots from your game";
                return null;
            }
            if (s_shape.AllWards == null || s_shape.Range == null || s_shape.DefaultRadius == null)
            {
                why = "the server cannot read the wards' areas (Protective Wards changed)";
                return null;
            }
            // Only switched-on wards chain or give connected access, so only they need room.
            float fallback = (float)s_shape.DefaultRadius.Invoke(null, null);
            var wards = ((IEnumerable)s_shape.AllWards.Invoke(null, null)).Cast<ZDO>().Where(z => z != null && z.GetBool(ZDOVars.s_enabled))
                .Select(z => new WardTestSite.Circle(z.GetPosition().x, z.GetPosition().z, Math.Max(fallback, (float)s_shape.Range.Invoke(null, new object[] { z }))))
                .ToList();
            var spot = WardTestSite.Pick(spots, wards, WardTestSite.TestRadius, WardTestMargin);
            if (spot == null)
            {
                why = WardTestSite.NoSpotLine(WardTestSite.BestClearance(spots, wards), WardTestSite.TestRadius, WardTestMargin, spots.Count);
                return null;
            }
            return WardTestSite.FormatSpots(new[] { spot.Value });
        }

        /// <summary>Metres between the test ward's area and any real ward's.</summary>
        private const float WardTestMargin = 4f;

        /// <summary>Removes what the player's tests placed (all of it when <paramref name="only"/> is null) and forgets it.</summary>
        private static void Remove(long playerId, List<WardTestSite.Id> only)
        {
            if (!s_placed.TryGetValue(playerId, out var list)) return;
            foreach (var id in list.Where(i => only == null || only.Contains(i)).ToList())
            {
                Destroy(id, playerId);
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
            foreach (var kv in s_placed.ToList())
            {
                foreach (var id in kv.Value) removed += Destroy(id, kv.Key) ? 1 : 0;
            }
            s_placed.Clear();
            SavePlaced();
            return removed;
        }

        /// <summary>Whether the object the asker's test names is one it placed (<see cref="WardTestSite.Accepts"/>).</summary>
        private static bool Accepted(WardTestSite.Id id, long playerId, long peer)
        {
            var zdo = ZDOMan.instance?.GetZDO(new ZDOID(id.User, id.Number));
            return zdo != null && WardTestSite.Accepts(PrefabName(zdo), zdo.GetLong(ZDOVars.s_creator), playerId, zdo.GetOwner() == peer);
        }

        private static string PrefabName(ZDO zdo) => ZNetScene.instance?.GetPrefab(zdo.GetPrefab())?.name ?? "";

        /// <summary>Removes a recorded object while it still is a test object (<see cref="WardTestSite.MayRemove"/>).</summary>
        private static bool Destroy(WardTestSite.Id id, long recordedFor)
        {
            var zdo = ZDOMan.instance?.GetZDO(new ZDOID(id.User, id.Number));
            if (zdo == null || !WardTestSite.MayRemove(PrefabName(zdo), zdo.GetLong(ZDOVars.s_creator), recordedFor)) return false;
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
        public static bool Offered(PrivateArea ward, Player player) => s_offers != null && s_offers(ward, player);

        /// <summary>Protective Wards' own access check (direct or through connected wards).</summary>
        public static bool HasAccess(PrivateArea ward, Player player) => s_access != null && s_access(ward, player);

        public static bool Ready => s_shape != null;
    }
}
