using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patches "Wards repair the whole connected base" and "Wards repair by themselves"
    /// (<see cref="WardRepair"/>) on Protective Wards. A prefix on its repair loop
    /// (<c>PassiveRepairEffect</c>, started by a ward's alt-use) hands it an empty loop and instead asks
    /// each ward of the used ward's chain (<see cref="WardRepair.Network"/>, by Protective Wards' own
    /// active and overlap rules) to start, through an RPC on the ward that its owner's Vigil answers by
    /// marking the ward's ZDO. Every frame the game takes the turn of at most one ward it owns and
    /// whose time has come: one damaged piece of its area that Protective Wards would repair
    /// (<c>CanBeRepaired</c>) is repaired, and the pieces left go into the ZDO. Every second the left
    /// counts of loaded wards are put into Protective Wards' own hover list (<c>wardIsRepairing</c>), so
    /// its hover shows "Repairing N" on every game and does not offer to start again. A ward owned by a
    /// game without Vigil is not repaired by these patches.
    /// </summary>
    internal static class WardRepairPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.wardrepair";
        internal const string Guid = "shudnal.ProtectiveWards";
        private const string StartRpc = "Vigil_WardRepair";

        private static readonly int s_startedKey = "vigil_ward_repair".GetStableHashCode();
        private static readonly int s_leftKey = "vigil_ward_repair_left".GetStableHashCode();

        private static WardShape.RepairShape s_shape;
        private static Func<Piece, PrivateArea, bool> s_canBeRepaired;
        private static Func<PrivateArea, PrivateArea, bool> s_overlapping;
        private static Func<PrivateArea, bool> s_active, s_playerWard;
        private static IDictionary s_repairing;
        private static List<PrivateArea> s_allAreas;
        private static readonly HashSet<PrivateArea> s_shown = new HashSet<PrivateArea>();
        private static readonly Dictionary<PrivateArea, double> s_nextTurn = new Dictionary<PrivateArea, double>();
        private static readonly List<Piece> s_pieces = new List<Piece>();
        private static double s_nextShow;
        private static readonly HashSet<PrivateArea> s_startedHere = new HashSet<PrivateArea>();

        /// <summary>The server's switches and seconds per piece, kept current by the config entries.</summary>
        public static bool Network, Auto;

        public static double Seconds = WardRepair.DefaultSeconds;

        /// <summary>The self-test's trial, on this game alone.</summary>
        public static readonly WardRepair.Trial Trial = new WardRepair.Trial();

        /// <summary>The last start through a ward's alt-use: the wards asked and the damaged pieces found.</summary>
        public static List<PrivateArea> LastChain { get; private set; }

        public static int LastDamaged { get; private set; }

        /// <summary>Whether the hooks are in and Protective Wards' members are at hand.</summary>
        public static bool Ready => s_shape != null;

        private static WardRepair.Switches Now() => Trial.Effective(Network, Auto, Seconds, Time.realtimeSinceStartupAsDouble);

        public static bool Found { get; private set; }

        public static string Version { get; private set; }

        /// <summary>Why the patch stepped aside; null when it is in (or Protective Wards is absent).</summary>
        public static string Problem { get; private set; }

        /// <summary>Pieces repaired by these patches on this game since the start.</summary>
        public static int Repaired { get; private set; }

        public static string State => WardRepair.StateLine(Network, Auto, Found, Version, Problem);

        public static void Hook(Action<string> log)
        {
            var info = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == Guid);
            Found = info != null;
            Version = info?.Metadata.Version.ToString();
            if (!Found) return;
            try
            {
                s_shape = WardShape.FindRepair(TypeLookup.Loaded.Find);
                var areas = AccessTools.Field(typeof(PrivateArea), "m_allAreas");
                var awake = AccessTools.Method(typeof(PrivateArea), "Awake");
                if (s_shape == null || areas == null || awake == null)
                {
                    Problem = "its repair changed (an update): " + (s_shape == null ? "Protective Wards' repair members" : "the game's ward list");
                    s_shape = null;
                    return;
                }
                s_allAreas = (List<PrivateArea>)areas.GetValue(null);
                Areas = s_allAreas;
                s_repairing = (IDictionary)s_shape.Repairing.GetValue(null);
                s_canBeRepaired = (Func<Piece, PrivateArea, bool>)Delegate.CreateDelegate(typeof(Func<Piece, PrivateArea, bool>), s_shape.CanBeRepaired);
                s_overlapping = (Func<PrivateArea, PrivateArea, bool>)Delegate.CreateDelegate(typeof(Func<PrivateArea, PrivateArea, bool>), s_shape.Overlapping);
                s_active = (Func<PrivateArea, bool>)Delegate.CreateDelegate(typeof(Func<PrivateArea, bool>), s_shape.Active);
                s_playerWard = (Func<PrivateArea, bool>)Delegate.CreateDelegate(typeof(Func<PrivateArea, bool>), s_shape.PlayerWard);

                var harmony = new Harmony(HarmonyId);
                harmony.Patch(s_shape.StartRepair, prefix: new HarmonyMethod(typeof(WardRepairPatch), nameof(BeforeStartRepair)));
                harmony.Patch(awake, postfix: new HarmonyMethod(typeof(WardRepairPatch), nameof(AfterAwake)));
                foreach (var area in s_allAreas) Register(area);
                log?.Invoke("Vigil: the ward repair patches are hooked into Protective Wards " + Version + ".");
            }
            catch (Exception e)
            {
                Problem = "Protective Wards could not be hooked: " + e.GetType().Name + " " + e.Message;
                s_shape = null;
            }
        }

        private static void AfterAwake(PrivateArea __instance) => Register(__instance);

        private static void Register(PrivateArea area)
        {
            var view = area != null ? area.GetComponent<ZNetView>() : null;
            if (view == null || !view.IsValid()) return;
            view.Register(StartRpc, sender => Started(area));
        }

        /// <summary>On the game that owns the ward: mark it started, and take its turn soon.</summary>
        private static void Started(PrivateArea area)
        {
            if (s_shape == null || area == null) return;
            var view = area.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || !view.IsOwner()) return;
            view.GetZDO().Set(s_startedKey, true);
            s_nextTurn[area] = Time.realtimeSinceStartupAsDouble;
            s_startedHere.Add(area);
        }

        private static bool BeforeStartRepair(PrivateArea ward, Player initiator, ref IEnumerator __result)
        {
            var on = Now();
            var start = WardRepair.OnStart(on.Network, on.Auto);
            if (s_shape == null || ward == null || start == WardRepair.Start.ProtectiveWards) return true;
            __result = Nothing();
            var wards = start == WardRepair.Start.WholeNetwork
                ? WardRepair.Network(ward, s_allAreas.ToList(), w => w != null && s_active(w) && s_playerWard(w), s_overlapping)
                : new List<PrivateArea> { ward };
            int damaged = wards.SelectMany(Damaged).Distinct().Count();
            LastChain = wards;
            LastDamaged = damaged;
            if (damaged == 0)
            {
                string none = Localization.instance != null ? Localization.instance.Localize("$msg_doesnotneedrepair") : "Doesn't need repair";
                if (initiator != null && none.Length > 0) initiator.Message(MessageHud.MessageType.Center, char.ToUpperInvariant(none[0]) + none.Substring(1));
                return false;
            }
            foreach (var w in wards)
            {
                var view = w.GetComponent<ZNetView>();
                if (view != null && view.IsValid()) view.InvokeRPC(StartRpc);
            }
            Show(ward, damaged);
            if (initiator != null)
            {
                initiator.Message(MessageHud.MessageType.TopLeft, wards.Count == 1
                    ? "Repairing " + damaged + " damaged pieces"
                    : "Repairing " + damaged + " damaged pieces with " + wards.Count + " connected wards");
            }
            return false;
        }

        private static IEnumerator Nothing()
        {
            yield break;
        }

        private static List<Piece> Damaged(PrivateArea area)
        {
            s_pieces.Clear();
            Piece.GetAllPiecesInRadius(area.transform.position, area.m_radius, s_pieces);
            return s_pieces.Where(p => p != null && s_canBeRepaired(p, area)).ToList();
        }

        /// <summary>Every frame on a player's game or a host: the turn of at most one owned ward, and once a second the hover counts.</summary>
        public static void Update()
        {
            if (s_shape == null) return;
            var on = Now();
            if (!on.Network && !on.Auto)
            {
                if (s_shown.Count > 0) HideAll();
                return;
            }
            double now = Time.realtimeSinceStartupAsDouble;
            if (now >= s_nextShow)
            {
                s_nextShow = now + 1;
                ShowCounts();
            }
            foreach (var area in s_allAreas)
            {
                if (area == null) continue;
                var view = area.GetComponent<ZNetView>();
                if (view == null || !view.IsValid() || !view.IsOwner() || !s_playerWard(area)) continue;
                if (!s_nextTurn.TryGetValue(area, out double due))
                {
                    // A ward seen first waits a share of a turn, so the wards of a base take turns apart.
                    s_nextTurn[area] = now + UnityEngine.Random.Range(0f, (float)on.Seconds);
                    continue;
                }
                if (now < due) continue;
                s_nextTurn[area] = now + on.Seconds;
                Turn(area, view.GetZDO(), on.Auto);
                return;
            }
        }

        private static void Turn(PrivateArea area, ZDO zdo, bool auto)
        {
            bool started = zdo.GetBool(s_startedKey);
            bool active = s_active(area);
            List<Piece> damaged = (started || auto) && active ? Damaged(area) : new List<Piece>();
            var step = WardRepair.Next(owner: true, active, started, auto, damaged.Count);
            if (step == WardRepair.Step.RepairOne)
            {
                foreach (var piece in damaged)
                {
                    var wear = piece.GetComponent<WearNTear>();
                    if (wear == null || !wear.Repair()) continue;
                    piece.m_placeEffect.Create(piece.transform.position, piece.transform.rotation);
                    Repaired++;
                    break;
                }
            }
            if (step == WardRepair.Step.Finish) zdo.Set(s_startedKey, false);
            int left = WardRepair.LeftAfter(step, damaged.Count);
            if (zdo.GetInt(s_leftKey, WardRepair.NotRepairing) != left) zdo.Set(s_leftKey, left);
            if (left == WardRepair.NotRepairing) Hide(area);
            else Show(area, left);
        }

        private static void ShowCounts()
        {
            foreach (var gone in s_shown.Where(a => a == null).ToList()) s_shown.Remove(gone);
            foreach (var gone in s_nextTurn.Keys.Where(a => a == null).ToList()) s_nextTurn.Remove(gone);
            foreach (var area in s_allAreas)
            {
                if (area == null) continue;
                var view = area.GetComponent<ZNetView>();
                if (view == null || !view.IsValid()) continue;
                int left = view.GetZDO().GetInt(s_leftKey, WardRepair.NotRepairing);
                if (left > 0) Show(area, left);
                else Hide(area);
            }
        }

        private static void Show(PrivateArea area, int left)
        {
            s_repairing[area] = left;
            s_shown.Add(area);
        }

        private static void Hide(PrivateArea area)
        {
            if (s_shown.Remove(area)) s_repairing.Remove(area);
        }

        private static void HideAll()
        {
            foreach (var area in s_shown) if (area != null) s_repairing.Remove(area);
            s_shown.Clear();
        }

        // ------------------------------------------------------------------ for the self-test

        /// <summary>The game's list of loaded wards (once hooked).</summary>
        public static List<PrivateArea> Areas { get; private set; }

        /// <summary>Starts the self-test's trial: wards owned here take their turns at its pace from now.</summary>
        public static void StartTrial(bool network, bool auto, double seconds, double lasts)
        {
            Trial.Start(network, auto, seconds, Time.realtimeSinceStartupAsDouble, lasts);
            s_nextTurn.Clear();
            s_startedHere.Clear();
            LastChain = null;
            LastDamaged = 0;
        }

        public static void EndTrial() => Trial.End();

        /// <summary>Whether this game's Vigil marked the ward started since the trial began.</summary>
        public static bool StartedHere(PrivateArea area) => s_startedHere.Contains(area);

        /// <summary>Whether Protective Wards' hover list holds the ward (its "Repairing N").</summary>
        public static bool Shows(PrivateArea area) => s_repairing != null && s_repairing.Contains(area);

        public static bool IsStarted(PrivateArea area) => Zdo(area)?.GetBool(s_startedKey) ?? false;

        public static int Left(PrivateArea area) => Zdo(area)?.GetInt(s_leftKey, WardRepair.NotRepairing) ?? WardRepair.NotRepairing;

        private static ZDO Zdo(PrivateArea area)
        {
            var view = area != null ? area.GetComponent<ZNetView>() : null;
            return view != null && view.IsValid() ? view.GetZDO() : null;
        }

        /// <summary>The nearest active ward around the player that they made and this game owns, or null with the reason.</summary>
        public static PrivateArea OwnWardAround(Player player, out string why)
        {
            why = null;
            long me = player.GetPlayerID();
            Vector3 at = player.transform.position;
            var mine = s_allAreas.Where(a => a != null && s_playerWard(a) && s_active(a) && Creator(a) == me
                    && Vector3.Distance(a.transform.position, at) <= a.m_radius)
                .OrderBy(a => Vector3.Distance(a.transform.position, at)).ToList();
            var owned = mine.FirstOrDefault(a => a.GetComponent<ZNetView>().IsOwner());
            if (owned != null) return owned;
            why = mine.Count == 0
                ? "stand inside an active ward you made to run it"
                : "the ward around you is run by another player's game: run it where you are alone";
            return null;
        }

        private static long Creator(PrivateArea area)
        {
            var piece = area.GetComponent<Piece>();
            return piece != null ? piece.GetCreator() : 0;
        }

        /// <summary>
        /// Takes 1 health off a piece in the ward's area that this game owns, a player placed and is
        /// whole, and that the ward would repair; null with the reason when there is none.
        /// </summary>
        public static WearNTear DamageOne(PrivateArea ward, out string why)
        {
            why = null;
            s_pieces.Clear();
            Piece.GetAllPiecesInRadius(ward.transform.position, ward.m_radius, s_pieces);
            var candidates = s_pieces.Where(p => p != null && p.gameObject != ward.gameObject && p.IsPlacedByPlayer())
                .OrderBy(p => Vector3.Distance(p.transform.position, ward.transform.position)).Take(40).ToList();
            foreach (var piece in candidates)
            {
                var wear = piece.GetComponent<WearNTear>();
                var view = piece.GetComponent<ZNetView>();
                if (wear == null || view == null || !view.IsValid() || !view.IsOwner() || Health(wear) < wear.m_health) continue;
                SetHealth(view, wear.m_health - 1f);
                if (s_canBeRepaired(piece, ward)) return wear;
                SetHealth(view, wear.m_health);
            }
            why = candidates.Count == 0 ? "no piece placed by a player in the ward's area" : "no whole piece in the ward's area that this game owns and the ward would repair";
            return null;
        }

        /// <summary>The piece's health from its ZDO.</summary>
        public static float Health(WearNTear wear)
        {
            var view = wear != null ? wear.GetComponent<ZNetView>() : null;
            return view != null && view.IsValid() ? view.GetZDO().GetFloat(ZDOVars.s_health, wear.m_health) : float.NaN;
        }

        /// <summary>Gives a piece its health back when the test left it damaged.</summary>
        public static void MakeWhole(WearNTear wear)
        {
            var view = wear != null ? wear.GetComponent<ZNetView>() : null;
            if (view == null || !view.IsValid() || Health(wear) >= wear.m_health) return;
            if (view.IsOwner()) SetHealth(view, wear.m_health);
            else wear.Repair();
        }

        private static void SetHealth(ZNetView view, float health)
        {
            view.GetZDO().Set(ZDOVars.s_health, health);
            view.InvokeRPC(ZNetView.Everybody, "RPC_HealthChanged", health);
        }
    }
}
