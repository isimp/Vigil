using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// "Rested and comfort" in the panel's World tab: where the player stands as the game reads it for
    /// rest (assembly_valheim 1.0.20): shelter is 80 % cover from the character's centre and a roof
    /// (<c>Player.InShelter</c>), comfort counts only in shelter and takes the best piece of each comfort
    /// group within 10 m (<c>SE_Rested.CalculateComfortLevel</c>). Read again every 2 s while it shows,
    /// and compared with the game's own count, which a mod may change. Only reads.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private RulesPart _rested;
        private int _restedGame;
        private double _restedAt = double.NegativeInfinity;

        private PanelBadge RestedBadge() => _rested == null ? null : new PanelBadge("comfort " + _rested.Number, Skin.Tone.Quiet);

        private void DrawRested()
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                Ui.Note("Join a world to read where you stand.");
                return;
            }

            // Read on a layout pass only, so the pass that draws the card draws what was laid out.
            if (Event.current.type == EventType.Layout && Now - _restedAt >= 2)
            {
                _restedAt = Now;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    _rested = ReadRested(player);
                    _restedGame = player.GetComfortLevel();
                }
                catch (Exception) { _rested = null; }
                _recorder?.AddOwnTime(watch.Elapsed.TotalMilliseconds);
            }

            if (_rested == null)
            {
                Ui.Note("Could not read where you stand.");
                return;
            }
            DrawPart(_rested);
            if (_restedGame != _rested.Number)
                Ui.Note("The game's own count says " + _restedGame + ": a mod changes comfort or shelter, so the list may not tell the whole story.");
        }

        private static RulesPart ReadRested(Player player)
        {
            var centre = player.GetCenterPoint();
            int blocked = ObjectParts.CastCover(centre, null, out _).Count(r => r.Blocked);
            var pieces = new List<Piece>();
            Piece.GetAllComfortPiecesInRadius(player.transform.position, 10f, pieces);
            return ObjectRules.Rested(new ComfortFacts
            {
                Blocked = blocked, Roof = Cover.IsUnderRoof(centre),
                Pieces = pieces.Where(p => p != null).Select(p => new ComfortPiece
                {
                    Name = Localization.instance != null ? Localization.instance.Localize(p.m_name) : p.m_name, Group = p.m_comfortGroup.ToString(),
                    GroupIndex = (int)p.m_comfortGroup, Comfort = p.GetComfort(), Inactive = p.m_comfortObject != null && !p.m_comfortObject.activeInHierarchy,
                }).ToList(),
                Groups = Enum.GetNames(typeof(Piece.ComfortGroup)).Where(n => n != "None").ToArray(),
            });
        }
    }
}
