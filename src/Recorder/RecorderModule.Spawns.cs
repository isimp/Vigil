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

        private const float ArrivalRadius = 100f;
        private const int ArrivalsListed = 10;
        private readonly BaseArrivals _arrivalLog = new BaseArrivals();
        private List<BaseArrivals.Row> _arrivalRows = new List<BaseArrivals.Row>();
        private double _arrivalsAt = double.NegativeInfinity;

        private List<SpawnLog.Row> _spawnRows = new List<SpawnLog.Row>();
        private double _spawnRowsAt = double.NegativeInfinity;

        private void StartSpawnWatch()
        {
            if (_server) return;
            SpawnWatch.Hook();
            SpawnWatch.Spawned = r => EventLog.Write("spawn", r.Creature, SpawnLog.EventDetail(r));
        }

        /// <summary>Once a second on a player's game: creatures hostile to the player that stand inside a base area within 100 m, each kept once.</summary>
        private void UpdateArrivals()
        {
            if (_server || Now - _arrivalsAt < 1) return;
            _arrivalsAt = Now;
            var player = Player.m_localPlayer;
            if (player == null) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var me = player.transform.position;
                foreach (var c in Character.GetAllCharacters())
                {
                    if (c == null || c.IsPlayer() || c.IsDead() || c.IsTamed()) continue;
                    var p = c.transform.position;
                    if ((p - me).sqrMagnitude > ArrivalRadius * ArrivalRadius || !BaseAI.IsEnemy(c, player)) continue;
                    var area = EffectArea.IsPointInsideArea(p, EffectArea.Type.PlayerBase);
                    var view = c.GetComponent<ZNetView>();
                    if (area == null || view == null || !view.IsValid()) continue;
                    var zdo = view.GetZDO();
                    var where = new SpawnRecord();
                    SpawnWatch.NameBase(where, area);
                    var arrival = new Arrival
                    {
                        Id = zdo.m_uid.ToString(), At = Now, Creature = OwnershipView.DisplayName(c.gameObject),
                        Place = where.BasePiece != null ? SpawnShare.Place(where) : "a base", X = p.x, Z = p.z, Spawn = SpawnWatch.Log.Find(zdo.m_uid.ToString()),
                    };
                    if (arrival.Spawn == null)
                    {
                        string origin = ObjectInspector.Origin(c.gameObject, zdo, c);
                        if (origin != null && !origin.StartsWith("Not seen spawning", StringComparison.Ordinal)) arrival.Origin = origin;
                    }
                    if (_arrivalLog.Add(arrival)) EventLog.Write("base-arrival", arrival.Creature, arrival.Place + "; " + BaseArrivals.Spawned(arrival, Now));
                }
            }
            finally { _recorder?.AddOwnTime(watch.Elapsed.TotalMilliseconds); }
        }

        private PanelBadge SpawnsBadge()
        {
            int came = _arrivalLog.Count(Now, SpawnWindowSeconds);
            if (came > 0) return new PanelBadge(came + " came into a base", Skin.Tone.Accent);
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

            if (UnityEngine.Event.current.type == UnityEngine.EventType.Layout) _arrivalRows = _arrivalLog.Recent(Now, SpawnWindowSeconds);
            if (_arrivalRows.Count > 0)
            {
                Ui.Heading("Came into a base, last " + ConsoleHelp.SpawnedMinutes + " minutes");
                foreach (var a in _arrivalRows.Take(ArrivalsListed))
                {
                    string key = "arrival:" + a.Key;
                    string spawned = a.Spawned.StartsWith("by your game", StringComparison.Ordinal) ? "spawned " + a.Spawned : a.Spawned;
                    if (Ui.Tool(a.What + " into " + a.Into, a.When + " ago · " + spawned, _spawnPins.ContainsKey(key) ? "Unpin" : "Pin"))
                    {
                        var row = a;
                        Ui.Later(() => TogglePinAt(key, row.PinX, row.PinZ, row.PinLabel));
                    }
                }
                if (_arrivalRows.Count > ArrivalsListed) Ui.Note("And " + (_arrivalRows.Count - ArrivalsListed) + " more; vigil spawned lists them all.");
                Ui.Heading("Spawned, last " + ConsoleHelp.SpawnedMinutes + " minutes");
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
            Ui.Note("Only what your game spawns: an area another player's game runs spawns on their game. The game never spawns inside a base area unless the entry allows it, so what comes into a base spawned outside and walked in. Each spawn and arrival also goes into Vigil-events.tsv.");
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
            var lines = new List<string>();
            var came = _arrivalLog.Recent(Now, minutes * 60);
            if (came.Count > 0)
            {
                lines.Add("came into a base in the last " + minutes.ToString("0.#", CultureInfo.InvariantCulture) + " minutes, newest first:");
                lines.AddRange(came.Select(a => "  " + a.When.PadRight(8) + a.What + " into " + a.Into + "   " + a.Spawned));
            }
            if (rows.Count == 0)
            {
                lines.Add("nothing spawned on your game in the last " + minutes.ToString("0.#", CultureInfo.InvariantCulture) + " minutes");
                return string.Join("\n", lines);
            }
            lines.Add("spawned on your game in the last " + minutes.ToString("0.#", CultureInfo.InvariantCulture) + " minutes, newest first:");
            lines.AddRange(rows.Select(r => "  " + r.When.PadRight(8) + r.What + "   " + r.How + "   " + r.Where));
            int inBase = SpawnWatch.Log.InsideBase(Now, minutes * 60);
            if (inBase > 0) lines.Add("  " + inBase + " of them inside a base");
            return string.Join("\n", lines);
        }
    }
}
