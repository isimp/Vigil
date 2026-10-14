using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;
using Vigil.Spawns;

namespace Vigil.Recorder
{
    /// <summary>
    /// On a player's game: whose game runs the world objects around the player. Each loaded object
    /// has an owner, the game that simulates it and sends its changes; that is the player whose
    /// character that game runs, this game, the server or nobody. The view counts them by game, kind
    /// and type within a radius (<see cref="OwnershipCount"/>) and, while one game is marked, stands a
    /// pillar in that game's colour on each of its objects of the kinds ticked. An object's kind and
    /// type are looked up once (its components do not change); players' own characters are left out.
    /// </summary>
    internal sealed class OwnershipView
    {
        /// <summary>The owners' colours, by <see cref="OwnershipCount.Row.Colour"/>: this game's in the accent.</summary>
        public static readonly Color[] Palette =
        {
            new(0.96f, 0.72f, 0.34f), new(0.35f, 0.80f, 0.95f), new(0.92f, 0.45f, 0.85f), new(0.55f, 0.86f, 0.40f), new(0.95f, 0.42f, 0.35f),
            new(0.48f, 0.58f, 1.00f), new(0.88f, 0.88f, 0.88f), new(0.30f, 0.78f, 0.66f), new(0.68f, 0.48f, 0.98f),
        };

        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> s_instances =
            AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");

        private readonly OwnershipCount _count = new OwnershipCount();
        private readonly MarkMesh[] _meshes = new MarkMesh[OwnershipCount.Colours];
        private readonly Dictionary<int, (OwnedKind? Kind, string Type)> _known = new Dictionary<int, (OwnedKind? Kind, string Type)>();

        /// <summary>The game whose objects are marked; null for none.</summary>
        public long? MarkedOwner { get; private set; }

        public bool MarksOn => MarkedOwner.HasValue;

        /// <summary>How many objects carry a mark now.</summary>
        public int MarkedCount { get; private set; }

        public List<OwnershipCount.Row> Rows { get; private set; } = new List<OwnershipCount.Row>();

        public float CountedRadius { get; private set; }

        /// <summary>When the last count was taken (realtime seconds).</summary>
        public double CountedAt { get; private set; } = double.NegativeInfinity;

        /// <summary>Marks one game's objects, or takes the marks away when that game's are shown.</summary>
        public void Mark(long owner)
        {
            MarkedOwner = MarkedOwner == owner ? null : owner;
            if (!MarksOn)
            {
                ClearMarks();
            }

            CountedAt = double.NegativeInfinity;
        }

        /// <summary>Counts again, and rebuilds the marks, on the next frame (the kinds to mark changed).</summary>
        public void Recount() => CountedAt = double.NegativeInfinity;

        /// <summary>A game's colour, the same as its line's and its marks'.</summary>
        public Color ColourOf(long owner) => Palette[_count.ColourOf(owner, ZNet.GetUID())];

        /// <summary>Counts the objects within the radius around the local player, and rebuilds the marks while a game is marked.</summary>
        public void Refresh(float radius, double now, MarkKinds kinds, OwnershipFilter filter = null)
        {
            CountedAt = now;
            CountedRadius = radius;
            var player = Player.m_localPlayer;
            if (ZNetScene.instance == null || player == null || ZNet.instance == null)
            {
                Rows = new List<OwnershipCount.Row>();
                return;
            }

            var center = player.transform.position;
            float r2 = radius * radius;
            var seen = new List<OwnershipCount.Seen>();
            var marks = MarksOn ? new List<Mark>() : null;
            long me = ZNet.GetUID(), server = ServerUid();
            var players = PlayerNames();
            var names = new Dictionary<long, string>();
            filter ??= new OwnershipFilter();
            if (_known.Count > 50000) _known.Clear();
            foreach (var kv in s_instances(ZNetScene.instance))
            {
                var view = kv.Value;
                if (view == null || kv.Key == null) continue;
                var pos = view.transform.position;
                if ((pos - center).sqrMagnitude > r2) continue;
                var (kind, type) = Known(view);
                if (kind == null) continue;
                long owner = kv.Key.GetOwner();
                seen.Add(new OwnershipCount.Seen(owner, kind.Value, type));
                if (marks != null && owner == MarkedOwner && filter.MarksIt(kind.Value, kinds))
                {
                    if (!names.TryGetValue(owner, out string name)) names[owner] = name = OwnershipCount.Name(owner, players, me, server);
                    if (filter.Keeps(kind.Value, type, name)) marks.Add(new Mark(pos.x, pos.y, pos.z, 0f, 1f, 0f));
                }
            }

            Rows = _count.Count(seen, players, me, server, filter);
            if (marks != null) BuildMarks(marks);
        }

        /// <summary>The players this game can see, by the id of the game that runs each one's character.</summary>
        public static Dictionary<long, string> PlayerNames()
        {
            var names = new Dictionary<long, string>();
            foreach (var p in Player.GetAllPlayers())
            {
                var zdo = p ? p.GetComponent<ZNetView>()?.GetZDO() : null;
                if (zdo != null) names[zdo.GetOwner()] = p.GetPlayerName();
            }

            return names;
        }

        /// <summary>The character of the player whose game has this id, when this game can see it; null otherwise.</summary>
        public static Player PlayerOf(long owner)
        {
            foreach (var p in Player.GetAllPlayers())
            {
                var zdo = p ? p.GetComponent<ZNetView>()?.GetZDO() : null;
                if (zdo != null && zdo.GetOwner() == owner) return p;
            }

            return null;
        }

        /// <summary>Every loaded world object with its record; empty outside a world.</summary>
        public static Dictionary<ZDO, ZNetView> Loaded() =>
            ZNetScene.instance != null ? s_instances(ZNetScene.instance) : new Dictionary<ZDO, ZNetView>();

        public static long ServerUid() => ZNet.instance != null ? ZNet.instance.GetServerPeer()?.m_uid ?? 0 : 0;

        /// <summary>What a world object is called in the game (a creature's, item's or piece's own name), else its prefab's name.</summary>
        public static string DisplayName(GameObject go)
        {
            string key = null;
            var character = go.GetComponent<Character>();
            var item = go.GetComponent<ItemDrop>();
            var piece = go.GetComponent<Piece>();
            if (character != null) key = character.m_name;
            else if (item != null && item.m_itemData?.m_shared != null) key = item.m_itemData.m_shared.m_name;
            else if (piece != null) key = piece.m_name;
            string shown = !string.IsNullOrEmpty(key) && Localization.instance != null ? Localization.instance.Localize(key) : key;
            return string.IsNullOrEmpty(shown) || shown.StartsWith("[", StringComparison.Ordinal) ? Utils.GetPrefabName(go) : shown;
        }

        /// <summary>An object's type as the count names it, looked up once per object; null for players' own characters.</summary>
        public string TypeOf(ZNetView view) => Known(view).Type;

        private (OwnedKind? Kind, string Type) Known(ZNetView view)
        {
            int id = view.GetInstanceID();
            if (_known.TryGetValue(id, out var known)) return known;
            OwnedKind? kind;
            var go = view.gameObject;
            var character = go.GetComponent<Character>();
            if (character is Player) kind = null;
            else if (character != null || go.GetComponent<Fish>() != null) kind = OwnedKind.Creature;
            else if (go.GetComponent<ItemDrop>() != null) kind = OwnedKind.Item;
            else if (go.GetComponent<Ship>() != null || go.GetComponent<Vagon>() != null) kind = OwnedKind.Vehicle;
            else if (go.GetComponent<Piece>() != null) kind = OwnedKind.Piece;
            else kind = OwnedKind.Other;
            known = (kind, kind == null ? null : DisplayName(go));
            _known[id] = known;
            return known;
        }

        private void BuildMarks(List<Mark> marks)
        {
            MarkedCount = marks.Count;
            int colour = _count.ColourOf(MarkedOwner ?? 0, ZNet.GetUID());
            float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            for (int c = 0; c < OwnershipCount.Colours; c++)
            {
                if (c != colour || marks.Count == 0)
                {
                    _meshes[c]?.Clear();
                    continue;
                }

                (_meshes[c] ??= new MarkMesh()).Build(marks, MarkStyle.Pillars, 1.2f, 2.5f, Palette[c], water);
            }
        }

        private void ClearMarks()
        {
            MarkedCount = 0;
            foreach (var m in _meshes) m?.Clear();
        }

        /// <summary>Every frame while a game is marked.</summary>
        public void Draw()
        {
            if (!MarksOn) return;
            foreach (var m in _meshes) m?.Draw();
        }

        public void Destroy()
        {
            foreach (var m in _meshes) m?.Destroy();
        }
    }
}
