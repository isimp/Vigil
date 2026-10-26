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
    /// game without Vigil is not repaired by these patches. Each ward's view and kind are read once,
    /// the wards are looked through four times a second, and a turn checks the ward's kept list of
    /// pieces (<see cref="WardRepair.PieceListSeconds"/>) rather than every loaded piece of the game.
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
        private static readonly Dictionary<PrivateArea, WardState> s_states = new Dictionary<PrivateArea, WardState>();
        private static readonly List<Piece> s_pieces = new List<Piece>();
        private static double s_nextShow, s_nextScan;

        /// <summary>Seconds between two looks through the loaded wards for a turn that is due.</summary>
        private const double ScanSeconds = 0.25;

        /// <summary>What a ward's turns need, read once: its view, whether it is a player's ward, its pieces.</summary>
        private sealed class WardState
        {
            public ZNetView View;
            public bool PlayerWard;
            public double NextTurn = double.NaN;
            public double PiecesAt = double.NegativeInfinity;
            public readonly List<KeyValuePair<Piece, WearNTear>> Pieces = new List<KeyValuePair<Piece, WearNTear>>();
        }

        private static WardState StateOf(PrivateArea area)
        {
            if (s_states.TryGetValue(area, out var state)) return state;
            state = new WardState { View = area.GetComponent<ZNetView>(), PlayerWard = s_playerWard(area) };
            s_states[area] = state;
            return state;
        }
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

        /// <summary>The self-test's own wards on this game; the trial covers only these.</summary>
        public static readonly HashSet<PrivateArea> TestWards = new HashSet<PrivateArea>();

        private static WardRepair.Switches Now(PrivateArea area) =>
            Trial.Effective(Network, Auto, Seconds, Time.realtimeSinceStartupAsDouble, area != null && TestWards.Contains(area));

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
            var state = StateOf(area);
            var view = state.View;
            if (view == null || !view.IsValid() || !view.IsOwner()) return;
            view.GetZDO().Set(s_startedKey, true);
            state.NextTurn = Time.realtimeSinceStartupAsDouble;
            state.PiecesAt = double.NegativeInfinity;
            s_nextScan = 0;
            s_startedHere.Add(area);
        }

        private static bool BeforeStartRepair(PrivateArea ward, Player initiator, ref IEnumerator __result)
        {
            var on = Now(ward);
            var start = WardRepair.OnStart(on.Network, on.Auto);
            if (s_shape == null || ward == null || start == WardRepair.Start.ProtectiveWards) return true;
            __result = Nothing();
            var wards = start == WardRepair.Start.WholeNetwork
                ? WardRepair.Network(ward, s_allAreas.ToList(), w => w != null && s_active(w) && s_playerWard(w), s_overlapping)
                : new List<PrivateArea> { ward };
            var damagedHere = DamagedNow(ward);
            int damaged = wards.SelectMany(w => w == ward ? damagedHere : DamagedNow(w)).Distinct().Count();
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
            // The used ward shows its own count at once, as every ward does after its first turn.
            if (damagedHere.Count > 0) Show(ward, damagedHere.Count);
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

        /// <summary>The damaged pieces of the ward's area Protective Wards would repair, looked up through every loaded piece.</summary>
        private static List<Piece> DamagedNow(PrivateArea area)
        {
            s_pieces.Clear();
            Piece.GetAllPiecesInRadius(area.transform.position, area.m_radius, s_pieces);
            return s_pieces.Where(p => p != null && s_canBeRepaired(p, area)).ToList();
        }

        /// <summary>The same from the ward's kept list: health first (a value read), Protective Wards' rules only for the damaged.</summary>
        private static List<Piece> Damaged(PrivateArea area, WardState state, double now)
        {
            if (WardRepair.PiecesDue(state.PiecesAt, now))
            {
                state.PiecesAt = now;
                state.Pieces.Clear();
                s_pieces.Clear();
                Piece.GetAllPiecesInRadius(area.transform.position, area.m_radius, s_pieces);
                foreach (var piece in s_pieces)
                {
                    var wear = piece != null ? piece.GetComponent<WearNTear>() : null;
                    if (wear != null) state.Pieces.Add(new KeyValuePair<Piece, WearNTear>(piece, wear));
                }
            }
            var damaged = new List<Piece>();
            foreach (var kv in state.Pieces)
            {
                if (kv.Value == null || kv.Value.GetHealthPercentage() >= 1f) continue;
                if (s_canBeRepaired(kv.Key, area)) damaged.Add(kv.Key);
            }
            return damaged;
        }

        /// <summary>Every frame on a player's game or a host: the turn of at most one owned ward, and once a second the hover counts.</summary>
        public static void Update()
        {
            if (s_shape == null) return;
            if (TestWards.Count > 0) KeepTestWardsSmall();
            double now = Time.realtimeSinceStartupAsDouble;
            if (!Network && !Auto && !(Trial.Active(now) && TestWards.Count > 0))
            {
                if (s_shown.Count > 0) HideAll();
                return;
            }
            if (now >= s_nextShow)
            {
                s_nextShow = now + 1;
                ShowCounts();
            }
            if (now < s_nextScan) return;
            s_nextScan = now + ScanSeconds;
            foreach (var area in s_allAreas)
            {
                if (area == null) continue;
                var state = StateOf(area);
                var view = state.View;
                if (!state.PlayerWard || view == null || !view.IsValid() || !view.IsOwner()) continue;
                var on = Now(area);
                if (!on.Network && !on.Auto) continue;
                if (double.IsNaN(state.NextTurn))
                {
                    // A ward seen first waits a share of a turn, so the wards of a base take turns apart.
                    state.NextTurn = now + UnityEngine.Random.Range(0f, (float)on.Seconds);
                    continue;
                }
                if (now < state.NextTurn) continue;
                state.NextTurn = now + on.Seconds;
                Turn(area, state, view.GetZDO(), on.Auto, now);
                return;
            }
        }

        private static void Turn(PrivateArea area, WardState state, ZDO zdo, bool auto, double now)
        {
            bool started = zdo.GetBool(s_startedKey);
            bool active = s_active(area);
            List<Piece> damaged = (started || auto) && active ? Damaged(area, state, now) : new List<Piece>();
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
            s_shown.RemoveWhere(a => a == null);
            if (s_states.Count > s_allAreas.Count)
            {
                foreach (var gone in s_states.Keys.Where(a => a == null).ToList()) s_states.Remove(gone);
            }
            foreach (var area in s_allAreas)
            {
                if (area == null) continue;
                var view = StateOf(area).View;
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
            foreach (var area in TestWards)
            {
                if (area == null) continue;
                var state = StateOf(area);
                state.NextTurn = double.NaN;
                state.PiecesAt = double.NegativeInfinity;
            }
            s_nextScan = 0;
            s_startedHere.Clear();
            LastChain = null;
            LastDamaged = 0;
        }

        public static void EndTrial() => Trial.End();

        /// <summary>
        /// Gives the self-test's wards their small radius (<see cref="WardTestSite.TestRadius"/>) again:
        /// Protective Wards sets a ward's radius back to its own whenever it refreshes the ward.
        /// </summary>
        public static void KeepTestWardsSmall()
        {
            foreach (var area in TestWards)
            {
                if (area != null && area.m_radius != WardTestSite.TestRadius) area.m_radius = WardTestSite.TestRadius;
            }
        }

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

        /// <summary>Takes 1 health off a piece this game owns; false when it cannot (not owned, or already damaged).</summary>
        public static bool Damage(WearNTear wear)
        {
            var view = wear != null ? wear.GetComponent<ZNetView>() : null;
            if (view == null || !view.IsValid() || !view.IsOwner() || Health(wear) < wear.m_health) return false;
            SetHealth(view, wear.m_health - 1f);
            return true;
        }

        /// <summary>Whether Protective Wards would repair the piece for the ward.</summary>
        public static bool WouldRepair(Piece piece, PrivateArea ward) => s_canBeRepaired != null && s_canBeRepaired(piece, ward);

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
