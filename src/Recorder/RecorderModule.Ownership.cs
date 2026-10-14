using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// "Who runs what" in the panel's World tab: whose game runs the objects around the player,
    /// counted every 2 s while the tab shows or a game's objects are marked (<see cref="OwnershipView"/>),
    /// one line per game with its own Mark button, and the label under the crosshair
    /// (<see cref="OwnershipLook"/>). Other players' pings come from the server's status, which only
    /// its admins get.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private static readonly (MarkKinds Kind, string Words)[] MarkChoices =
        {
            (MarkKinds.Creatures, "Creatures"), (MarkKinds.Vehicles, "Vehicles"), (MarkKinds.Items, "Items"), (MarkKinds.Pieces, "Pieces"), (MarkKinds.Other, "Other"),
        };

        private static readonly Ui.Column[] TypeColumns =
        {
            new Ui.Column("Type", 0, false), new Ui.Column("Count", 60, true), new Ui.Column("Kind", 80, true),
        };

        private readonly OwnershipView _ownership = new OwnershipView();
        private readonly OwnershipLook _look = new OwnershipLook();
        private ConfigEntry<float> _ownershipRadius;
        private ConfigEntry<bool> _lookAtIt;
        private ConfigEntry<MarkKinds> _markKinds;
        private string _ownershipRadiusText;
        private long? _ownerOpen;

        /// <summary>Whether the panel shows the World tab; the host sets it every frame.</summary>
        internal bool WorldShown { get; set; }

        /// <summary>Whether the World tab or the label want other players' pings, which the server's status carries.</summary>
        internal bool WantsPings => WorldShown || (_lookAtIt != null && _lookAtIt.Value);

        private void BindOwnership()
        {
            _ownershipRadius = Config.Bind("Ownership", "Radius", 64f,
                new ConfigDescription("How far around you the World tab counts whose game runs which objects, in metres.", new AcceptableValueRange<float>(8f, 300f)));
            _lookAtIt = Config.Bind("Ownership", "LookAtIt", false,
                "Shows a label under the crosshair: what you look at, whose game runs it, that connection's ping (for the server's admins) and how far that player is from it.");
            _markKinds = Config.Bind("Ownership", "MarkKinds", MarkKinds.Creatures | MarkKinds.Vehicles,
                "Which of a game's objects get a pillar when its line in the World tab is marked: Creatures, Vehicles (ships and carts), Items, Pieces, Other.");
            _ownershipRadiusText = _ownershipRadius.Value.ToString("0", CultureInfo.InvariantCulture);
        }

        /// <summary>Every frame on a player's game: counts while the tab shows or a game is marked, draws the marks and the label.</summary>
        private static readonly OwnedKind[] FilterKinds = { OwnedKind.Creature, OwnedKind.Vehicle, OwnedKind.Piece, OwnedKind.Item };
        private static readonly string[] FilterWords = { "All", "Creatures", "Vehicles", "Pieces", "Items" };
        private OwnershipFilter _ownerFilter = new OwnershipFilter();
        private string _ownerFindText = "";

        private void UpdateOwnership()
        {
            if (_server) return;
            if ((WorldShown || _ownership.MarksOn) && Now - _ownership.CountedAt >= 2)
            {
                TakeRadius();
                _ownership.Refresh(_ownershipRadius.Value, Now, _markKinds.Value, _ownerFilter);
            }
            _ownership.Draw();
            _look.Update(_lookAtIt.Value && !Panel.IsOpen, _ownership, PingOf, Limits.PingMs);
        }

        /// <summary>The radius typed in the card, taken at the next count; a number out of range is brought into it.</summary>
        private void TakeRadius()
        {
            if (!float.TryParse(_ownershipRadiusText, NumberStyles.Float, CultureInfo.InvariantCulture, out float r)) return;
            r = Mathf.Clamp(r, 8f, 300f);
            if (!Mathf.Approximately(r, _ownershipRadius.Value)) _ownershipRadius.Value = r;
        }

        /// <summary>A game's ping: this game's own from the recorder, another player's from the server's status; unknown otherwise.</summary>
        private double PingOf(OwnerRole role, string name)
        {
            if (role == OwnerRole.You)
            {
                var row = _recorder?.Last;
                return row != null && Time.realtimeSinceStartup - _recorder.LastAt < 3f ? Num(row, "ping_ms") : double.NaN;
            }
            if (role != OwnerRole.Player || _serverStatus == null || Now - _serverStatusAt > 15) return double.NaN;
            var player = _serverStatus.Players.Find(p => p.Name == name);
            return player?.PingMs ?? double.NaN;
        }

        private OwnershipVerdict.Verdict VerdictOf(OwnershipCount.Row row) => OwnershipVerdict.Of(row, PingOf(row.Role, row.Name), Limits.PingMs);

        private PanelBadge OwnershipBadge()
        {
            var badge = OwnershipVerdict.Badge(_ownership.Rows, VerdictOf);
            return badge == null ? null : new PanelBadge(badge.Text, Skin.Of(badge.Tone));
        }

        private void DrawOwnership()
        {
            if (Player.m_localPlayer == null)
            {
                Ui.Note("Join a world to see whose game runs the objects around you.");
                return;
            }
            if (Ui.Tool("Look at it", "A label under your crosshair: what it is and whose game runs it", _lookAtIt.Value ? "Hide" : "Show")) _lookAtIt.Value = !_lookAtIt.Value;

            Ui.BeginFormLine("Within");
            _ownershipRadiusText = Ui.Field(_ownershipRadiusText, 50);
            Ui.Note("m");
            Ui.EndLine();

            Ui.BeginFormLine("Show");
            int shown = Ui.Choice(_ownerFilter.Kind == null ? 0 : System.Array.IndexOf(FilterKinds, _ownerFilter.Kind.Value) + 1, FilterWords);
            Ui.EndLine();
            Ui.BeginFormLine("Find");
            string find = Ui.Field(_ownerFindText, 160);
            Ui.Note("a player or an object type");
            Ui.EndLine();
            OwnedKind? kindNow = shown <= 0 ? (OwnedKind?)null : FilterKinds[shown - 1];
            if (kindNow != _ownerFilter.Kind || find != _ownerFindText)
            {
                _ownerFindText = find;
                Ui.Later(() =>
                {
                    _ownerFilter = new OwnershipFilter { Kind = kindNow, Find = find };
                    _ownership.Recount();
                });
            }

            Ui.BeginFormLine("Marks for");
            var kinds = _markKinds.Value;
            var ticked = MarkKinds.None;
            foreach (var (kind, words) in MarkChoices)
            {
                if (Ui.Toggle((kinds & kind) != 0, words)) ticked |= kind;
            }
            Ui.EndLine();
            if (ticked != kinds)
            {
                _markKinds.Value = ticked;
                _ownership.Recount();
            }

            Ui.Heading(_ownerFilter.Heading(_ownership.CountedRadius > 0 ? _ownership.CountedRadius : _ownershipRadius.Value));
            if (_ownership.Rows.Count == 0) Ui.Note(_ownerFilter.IsEmpty ? "Counting ..." : "Nothing within reach matches.");
            foreach (var row in _ownership.Rows)
            {
                long owner = row.Owner;
                bool open = _ownerOpen == owner;
                bool marked = _ownership.MarkedOwner == owner;
                bool pressed = Ui.ExpandTool(OwnershipView.Palette[row.Colour], row.Name, OwnershipCount.Phrase(row), PingWords(row), open, marked ? "Unmark" : "Mark", out bool toggled);
                if (toggled) Ui.Later(() => _ownerOpen = open ? (long?)null : owner);
                if (pressed) Ui.Later(() => _ownership.Mark(owner));
                if (open) DrawOwnerLine(row, marked);
            }
            Ui.Note("A game runs what it owns: it moves it and sends its changes to everyone else. Pieces cost their owner wear and support checks, so they count here, but get marks only when ticked.");
        }

        /// <summary>The value beside a line's button: the game's ping in its level's colour, or why there is none.</summary>
        private string PingWords(OwnershipCount.Row row)
        {
            if (row.Role == OwnerRole.Nobody) return "no game";
            if (row.Role == OwnerRole.Server) return "";
            double ping = PingOf(row.Role, row.Name);
            if (double.IsNaN(ping)) return "";
            return Ui.Colour(ping.ToString("0", CultureInfo.InvariantCulture) + " ms", HudLine.AtOrAbove(ping, Limits.PingMs, Limits.PingMs / 2));
        }

        /// <summary>An opened line: what is worth a look, what its marks show, and its most common types.</summary>
        private void DrawOwnerLine(OwnershipCount.Row row, bool marked)
        {
            var verdict = VerdictOf(row);
            if (verdict != null) Ui.Verdict(verdict.Tone, verdict.Text);
            string kinds = string.Join(", ", MarkChoices.Where(c => (_markKinds.Value & c.Kind) != 0).Select(c => c.Words.ToLowerInvariant()).ToArray());
            Ui.Note(kinds.Length == 0 ? "Tick a kind under Marks for to mark anything."
                : marked ? string.Format(CultureInfo.InvariantCulture, "Marked: {0} on the ground ({1}).", _ownership.MarkedCount, kinds)
                : "Mark puts a pillar in this colour on each of its " + kinds + ".");
            var types = row.TopTypes(6).Select(t => new[] { t.Type, t.Count.ToString(CultureInfo.InvariantCulture), KindWord(t.Kind) }).ToList();
            Ui.Table(TypeColumns, types);
        }

        private static string KindWord(OwnedKind kind) => kind switch
        {
            OwnedKind.Creature => "creature",
            OwnedKind.Vehicle => "vehicle",
            OwnedKind.Item => "item",
            OwnedKind.Piece => "piece",
            _ => "other",
        };
    }
}
