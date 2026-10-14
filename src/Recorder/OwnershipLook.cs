using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// "Look at it": a label under the crosshair naming what the player looks at and whose game runs
    /// it (<see cref="OwnerLabel"/>), with that connection's ping and, for another player, how far
    /// they are from it. It casts its own ray from the camera, as far as the game looks for a
    /// creature to hover (50 m) and on the same layers, and takes the first thing hit that is not
    /// the player; the game's own hover reaches anything but creatures only within 5 m. The first
    /// thing hit being ground or rock shows no label. Looked again every 0.15 s; hidden with the
    /// game's HUD and while the panel is open.
    /// </summary>
    internal sealed class OwnershipLook
    {
        private const float Every = 0.15f, Reach = 50f;

        private static readonly string[] Layers =
        {
            "item", "piece", "piece_nonsolid", "Default", "static_solid", "Default_small", "character", "character_net", "terrain", "vehicle", "character_ghost",
        };

        private readonly RaycastHit[] _hits = new RaycastHit[32];
        private int _mask = -1;
        private TMP_Text _text;
        private GameObject _root;
        private string _shown;
        private float _next;

        /// <summary>Every frame on a player's game: looks again when due and shows or hides the label.</summary>
        public void Update(bool on, OwnershipView view, Func<OwnerRole, string, double> pingOf, double pingLimitMs)
        {
            if (!on || Player.m_localPlayer == null || Overlay.GameHudHidden())
            {
                Show(null);
                return;
            }

            if (Time.unscaledTime < _next)
            {
                return;
            }

            _next = Time.unscaledTime + Every;
            var target = Pick();
            Show(target == null ? null : Words(target, view, pingOf, pingLimitMs));
        }

        /// <summary>The world object under the crosshair, or null when the first thing there has no owner.</summary>
        public ZNetView Pick()
        {
            var camera = GameCamera.instance;
            var player = Player.m_localPlayer;
            if (camera == null || player == null)
            {
                return null;
            }

            if (_mask == -1) _mask = LayerMask.GetMask(Layers);
            int n = Physics.RaycastNonAlloc(camera.transform.position, camera.transform.forward, _hits, Reach, _mask);
            Array.Sort(_hits, 0, n, Nearest.First);
            for (int i = 0; i < n; i++)
            {
                var hit = _hits[i];
                var body = hit.collider.attachedRigidbody;
                if (body != null && body.gameObject == player.gameObject) continue;
                ZNetView view = body != null ? body.GetComponent<ZNetView>() : null;
                if (view == null) view = hit.collider.GetComponentInParent<ZNetView>();
                return view != null && view.IsValid() ? view : null;
            }

            return null;
        }

        /// <summary>The label's text for one object: its name, then its owner in that game's colour, the ping in its level's colour and the distance.</summary>
        public static string Words(ZNetView target, OwnershipView view, Func<OwnerRole, string, double> pingOf, double pingLimitMs)
        {
            var label = Label(target, pingOf, pingLimitMs);
            return label.Thing + "\n<size=80%>" + OwnerLine(label, view, target.GetZDO().GetOwner()) + "</size>";
        }

        /// <summary>Whose game runs the object, its ping and that player's distance, coloured as on the label; the inspector shows it too.</summary>
        public static string OwnerLine(ZNetView target, OwnershipView view, Func<OwnerRole, string, double> pingOf, double pingLimitMs) =>
            OwnerLine(Label(target, pingOf, pingLimitMs), view, target.GetZDO().GetOwner());

        private static string OwnerLine(OwnerLabel label, OwnershipView view, long owner)
        {
            var parts = new List<string> { "<color=#" + ColorUtility.ToHtmlStringRGB(view.ColourOf(owner)) + ">" + label.Owner + "</color>" };
            if (label.Ping != null) parts.Add(Ui.Colour(label.Ping, label.PingLevel));
            if (label.Distance != null) parts.Add(label.Distance);
            return string.Join(" · ", parts);
        }

        private static OwnerLabel Label(ZNetView target, Func<OwnerRole, string, double> pingOf, double pingLimitMs)
        {
            long owner = target.GetZDO().GetOwner();
            long me = ZNet.GetUID(), server = OwnershipView.ServerUid();
            var role = OwnershipCount.RoleOf(owner, me, server);
            string name = OwnershipCount.Name(owner, OwnershipView.PlayerNames(), me, server);
            double distance = double.NaN;
            if (role == OwnerRole.Player)
            {
                var runner = OwnershipView.PlayerOf(owner);
                if (runner != null) distance = Vector3.Distance(runner.transform.position, target.transform.position);
            }

            return OwnerLabel.Build(OwnershipView.DisplayName(target.gameObject), name, role, pingOf(role, name), distance, pingLimitMs);
        }

        /// <summary>The label's words on one line without their colours, for the self-test's report.</summary>
        public static string Plain(string words) => System.Text.RegularExpressions.Regex.Replace(words, "<[^>]*>", "").Replace("\n", ", ");

        public void Destroy()
        {
            if (_root) UnityEngine.Object.Destroy(_root);
        }

        private void Show(string words)
        {
            if (words == null)
            {
                if (_root) _root.SetActive(false);
                _shown = null;
                return;
            }

            if (!Ensure()) return;
            _root.SetActive(true);
            if (words == _shown) return;
            _shown = words;
            _text.text = "<mark=#000000A0 padding=\"12,12,5,5\">" + words + "</mark>";
        }

        private bool Ensure()
        {
            if (_root) return true;
            var text = Overlay.Make("Vigil look label");
            if (text == null) return false;
            _root = Overlay.RootOf(text);
            _text = text;
            _text.fontSize = 18;
            _text.alignment = TextAlignmentOptions.Top;
            var rt = _text.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -36f);
            rt.sizeDelta = new Vector2(1000f, 80f);
            return true;
        }

        private sealed class Nearest : IComparer<RaycastHit>
        {
            public static readonly Nearest First = new Nearest();

            public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
        }
    }
}
