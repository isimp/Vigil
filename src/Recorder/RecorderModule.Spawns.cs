using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// "Spawns near you" in the panel's World tab and "vigil spawned": what this game spawned
    /// (<see cref="SpawnWatch"/>) and where each came from, newest first, with the spawns another
    /// mod's check cancelled apart. Every spawn that appears also goes into the events file.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private static readonly Ui.Column[] SpawnColumns =
        {
            new Ui.Column("When", 55, false), new Ui.Column("What", 150, false), new Ui.Column("How", 0, false), new Ui.Column("Where", 190, false),
        };

        private const int SpawnRowsListed = 20;
        private const double SpawnWindowSeconds = ConsoleHelp.SpawnedMinutes * 60.0;

        private List<SpawnLog.Row> _spawnRows = new List<SpawnLog.Row>();
        private double _spawnRowsAt = double.NegativeInfinity;

        private void StartSpawnWatch()
        {
            if (_server) return;
            SpawnWatch.Hook();
            SpawnWatch.Spawned = r => EventLog.Write("spawn", r.Creature, SpawnLog.EventDetail(r));
        }

        private PanelBadge SpawnsBadge()
        {
            int inBase = SpawnWatch.Log.InsideBase(Now, SpawnWindowSeconds);
            if (inBase > 0) return new PanelBadge(inBase + " inside a base", Skin.Tone.Accent);
            int count = SpawnWatch.Log.Records.Count(r => Now - r.At <= SpawnWindowSeconds);
            return new PanelBadge(count + " in " + ConsoleHelp.SpawnedMinutes + " min", Skin.Tone.Quiet);
        }

        private void DrawSpawns()
        {
            if (SpawnWatch.HookProblem != null)
            {
                Ui.Note("The spawn hooks are not in: " + SpawnWatch.HookProblem + ".");
                return;
            }

            // Read on a layout pass only, so the pass that draws the card draws what was laid out.
            if (UnityEngine.Event.current.type == UnityEngine.EventType.Layout && Now - _spawnRowsAt >= 1)
            {
                _spawnRowsAt = Now;
                _spawnRows = SpawnWatch.Log.Recent(Now, SpawnWindowSeconds);
            }

            var rows = _spawnRows;
            if (rows.Count == 0) Ui.Note("Nothing spawned on your game in the last " + ConsoleHelp.SpawnedMinutes + " minutes.");
            else
            {
                Ui.Table(SpawnColumns, rows.Take(SpawnRowsListed).Select(r => new[]
                {
                    r.When, Mark(r, r.What), Mark(r, r.How), Mark(r, r.Where),
                }));
                if (rows.Count > SpawnRowsListed) Ui.Note("And " + (rows.Count - SpawnRowsListed).ToString(CultureInfo.InvariantCulture) + " more; vigil spawned lists them all.");
            }
            Ui.Note("Only what your game spawns: an area another player's game runs spawns on their game. Each spawn also goes into Vigil-events.tsv.");
        }

        private static string Mark(SpawnLog.Row row, string text) =>
            row.InsideBase ? "<color=#E8B04A>" + text + "</color>" : row.Cancelled ? "<color=#A8A9B2>" + text + "</color>" : text;

        /// <summary>"vigil spawned [minutes]": the spawn log's rows in lines.</summary>
        private string SpawnedCommand(string rest)
        {
            string trimmed = (rest ?? "").Trim();
            if (trimmed.StartsWith("server", StringComparison.OrdinalIgnoreCase)) return SpawnedServerCommand(trimmed.Substring("server".Length));
            if (SpawnWatch.HookProblem != null) return "the spawn hooks are not in: " + SpawnWatch.HookProblem;
            double minutes = ConsoleHelp.SpawnedMinutes;
            if (!string.IsNullOrWhiteSpace(rest) && double.TryParse(rest.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m > 0) minutes = m;
            var rows = SpawnWatch.Log.Recent(Now, minutes * 60);
            if (rows.Count == 0) return "nothing spawned on your game in the last " + minutes.ToString("0.#", CultureInfo.InvariantCulture) + " minutes";
            var lines = new List<string> { "spawned on your game in the last " + minutes.ToString("0.#", CultureInfo.InvariantCulture) + " minutes, newest first:" };
            lines.AddRange(rows.Select(r => "  " + r.When.PadRight(8) + r.What + "   " + r.How + "   " + r.Where));
            int inBase = SpawnWatch.Log.InsideBase(Now, minutes * 60);
            if (inBase > 0) lines.Add("  " + inBase + " of them inside a base");
            return string.Join("\n", lines);
        }
    }
}
