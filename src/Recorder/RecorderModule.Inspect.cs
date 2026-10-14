using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// "Inspect an object" in the panel's World tab: the world object that was under the crosshair when the
    /// panel opened (<see cref="ObjectInspector"/>), read again every 2 s while the tab shows, with
    /// how often it changed over the last 10 s; and "Busiest objects", which watches every loaded
    /// object around the player for 10 s and ranks them by what sending their changes takes
    /// (<see cref="BusiestObjects"/>). Both only read. "vigil inspect" and "vigil busiest" do the same
    /// from the console.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private static readonly Ui.Column[] FoldColumns = { new Ui.Column(null, 0, false) };

        private static readonly Ui.Column[] ValueColumns =
        {
            new Ui.Column("Name", 0, false), new Ui.Column("Named in", 130, false), new Ui.Column("Type", 60, false), new Ui.Column("Value", 170, true),
        };

        private static readonly Ui.Column[] ScriptColumns =
        {
            new Ui.Column("Mod", 150, false), new Ui.Column("Count", 50, true), new Ui.Column("Scripts", 0, false),
        };

        private const int ValuesListed = 40;

        private readonly ObjectInspector _inspector = new ObjectInspector();
        private ObjectInspector.Facts _facts;
        private double _factsAt = double.NegativeInfinity, _sampledAt = double.NegativeInfinity;
        private bool _valuesOpen = true, _recordOpen = true, _scriptsOpen;
        private string _busiestRadiusText = ConsoleHelp.BusiestRadius.ToString(CultureInfo.InvariantCulture);

        // A busiest-objects watch under way: the first reading, when it ends, how far it looks, who asked.
        private Dictionary<string, BusiestObjects.Start> _busyStart;
        private double _busyFrom, _busyUntil;
        private float _busyRadius;
        private bool _busyToConsole;

        /// <summary>The panel just opened: the object under the crosshair now is the one to inspect, and the ground there the spot to check for spawns.</summary>
        internal void PanelOpened()
        {
            if (_server) return;
            try { InspectNow(); } catch (Exception) { }
            try { PickSpotNow(); } catch (Exception) { }
        }

        private void InspectNow()
        {
            _inspector.Pick(Player.m_localPlayer != null ? _look.Pick() : null, Now);
            _sampledAt = Now;
            _facts = null;
            _factsAt = double.NegativeInfinity;
        }

        /// <summary>Every frame on a player's game: samples the inspected object once a second, ends a busiest-objects watch when due.</summary>
        private void UpdateInspect()
        {
            if (_server) return;
            if (_inspector.Picked && Now - _sampledAt >= 1)
            {
                _sampledAt = Now;
                _inspector.Sample(Now);
            }
            if (_busyStart != null && Now >= _busyUntil) FinishBusiest();
        }

        private PanelBadge ObjectBadge()
        {
            if (_facts == null) return null;
            var rate = _inspector.Rate(_facts.SizeBytes, out double seconds, out _);
            return new PanelBadge(seconds < 1.5 ? _facts.Name : _facts.Name + ": " + rate.PerSecond.ToString("0.#", CultureInfo.InvariantCulture) + " changes/s", Skin.Tone.Quiet);
        }

        private void DrawObject()
        {
            if (Player.m_localPlayer == null)
            {
                Ui.Note("Join a world to inspect its objects.");
                return;
            }

            // Read on a layout pass only, so the pass that draws the card draws what was laid out.
            if (Event.current.type == EventType.Layout && _inspector.Picked && Now - _factsAt >= 2)
            {
                _factsAt = Now;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try { _facts = _inspector.Read(_ownership, PingOf, Limits.PingMs); } catch (Exception) { _facts = null; }
                _recorder?.AddOwnTime(watch.Elapsed.TotalMilliseconds);
            }

            string what = !_inspector.Picked ? "Nothing picked"
                : _facts == null ? "Gone"
                : _facts.Name;
            string about = !_inspector.Picked ? "Nothing with an owner was under your crosshair when the panel opened. Aim at something and open the panel again."
                : _facts == null ? "It was unloaded or destroyed since the panel opened"
                : string.Format(CultureInfo.InvariantCulture, "What was under your crosshair when the panel opened, {0:0} m away", _facts.Distance);
            if (Ui.Tool(what, about, "Again")) Ui.Later(InspectNow);
            if (_facts != null) DrawFacts(_facts);
            DrawBusiest();
        }

        private void DrawFacts(ObjectInspector.Facts facts)
        {
            Ui.Row("Prefab", facts.Prefab + "   (" + (facts.Mod ?? "the game, or no mod matched") + ")");
            Ui.Row("Run by", facts.RunBy);
            Ui.Row("Kept", facts.Kept);
            Ui.Row("Size", facts.SizeBytes.ToString(CultureInfo.InvariantCulture) + " bytes, all of it each time it is sent");
            Ui.Row("Changes", ChangeWords(facts));
            foreach (var extra in facts.Extras) Ui.Row(extra.Key, extra.Value);

            bool valuesOpen = _valuesOpen;
            if (Ui.ExpandRow(FoldColumns, new[] { "Stored values (" + facts.Values.Count.ToString(CultureInfo.InvariantCulture) + ")" }, valuesOpen))
                Ui.Later(() => _valuesOpen = !valuesOpen);
            if (valuesOpen)
            {
                if (facts.Values.Count == 0) Ui.Note("It stores no values of its own.");
                else Ui.Table(ValueColumns, facts.Values.Take(ValuesListed).Select(v => new[] { v.Name, v.NamedIn ?? "", v.Type, v.Value }));
                if (facts.Values.Count > ValuesListed) Ui.Note("And " + (facts.Values.Count - ValuesListed).ToString(CultureInfo.InvariantCulture) + " more; vigil inspect lists them all.");
                if (facts.Values.Any(v => v.Name.StartsWith("#", StringComparison.Ordinal)))
                    Ui.Note("A number instead of a name: no text in the game's or the mods' code makes that number, so a mod puts the name together while it runs.");
                Ui.BeginLine();
                Ui.Note(ObjectInspector.NamesReading ? "Reading the names from the game and the mods ..." : "Names: " + ObjectInspector.NamesNote + ".");
                GUILayout.FlexibleSpace();
                if (Ui.Quiet("Read names again")) Ui.Later(ReadNamesAgain);
                Ui.EndLine();
            }

            bool recordOpen = _recordOpen;
            if (Ui.ExpandRow(FoldColumns, new[] { "The game's record of it" }, recordOpen)) Ui.Later(() => _recordOpen = !recordOpen);
            if (recordOpen)
            {
                foreach (var row in facts.Record) Ui.Row(row.Key, row.Value);
            }

            bool scriptsOpen = _scriptsOpen;
            if (Ui.ExpandRow(FoldColumns, new[] { "Scripts on it by mod (" + ScriptsByMod.Summary(facts.Scripts) + ")" }, scriptsOpen))
                Ui.Later(() => _scriptsOpen = !scriptsOpen);
            if (scriptsOpen) Ui.Table(ScriptColumns, facts.Scripts.Select(s => new[] { s.Owner, s.Count.ToString(CultureInfo.InvariantCulture), s.Types }));
        }

        /// <summary>How often the inspected object changed over the watch so far, and what sending it takes at most.</summary>
        private string ChangeWords(ObjectInspector.Facts facts)
        {
            var rate = _inspector.Rate(facts.SizeBytes, out double seconds, out int ownerChanges);
            if (seconds < 1.5) return "watching, the first numbers follow in a moment";
            if (rate.Reset) return "it was made anew meanwhile; watching again";
            string text = string.Format(CultureInfo.InvariantCulture, "{0} in the last {1:0} s ({2:0.#}/s), about {3:0.0} kB/s at most to send",
                rate.Changes, seconds, rate.PerSecond, rate.BytesPerSecond / 1024.0);
            if (ownerChanges > 0) text += string.Format(CultureInfo.InvariantCulture, "; owner changed {0} {1}", ownerChanges, ownerChanges == 1 ? "time" : "times");
            return text;
        }

        private void DrawBusiest()
        {
            if (_busyStart != null)
            {
                Ui.Tool("Busiest objects nearby", string.Format(CultureInfo.InvariantCulture, "Watching {0} objects, {1:0} s left", _busyStart.Count, Math.Max(0, _busyUntil - Now)), "Watching", enabled: false);
            }
            else if (Ui.Tool("Busiest objects nearby", "Watches every object around you for 10 s: those whose changes take the most to send", "Run", ref _busiestRadiusText, 50, "m"))
            {
                _host.RunForResult("busiest " + _busiestRadiusText, ResultStore.Busiest);
            }
            ShowResult(ResultStore.Busiest);
        }

        /// <summary>Starts reading the value names again; the card reads the object again once they are in.</summary>
        private void ReadNamesAgain()
        {
            ObjectInspector.ReadNames();
            _factsAt = double.NegativeInfinity;
        }

        // ------------------------------------------------------------------ console and the watch

        /// <summary>"vigil inspect names": reads the value names again, in the background.</summary>
        private string ReadNamesCommand()
        {
            if (ObjectInspector.NamesReading) return "the names are being read already";
            ReadNamesAgain();
            return "reading the value names from the game and the mods again, in the background; vigil inspect uses them once they are in";
        }

        /// <summary>"vigil inspect": the object under the crosshair now, in lines; the World tab shows it from then on too.</summary>
        private string InspectCommand()
        {
            InspectNow();
            if (!_inspector.Picked) return "nothing with an owner is under your crosshair (looked 50 m)";
            var facts = _inspector.Read(_ownership, PingOf, Limits.PingMs);
            if (facts == null) return "the object is gone";
            var lines = new List<string>
            {
                facts.Name + "   " + facts.Prefab + "   (" + (facts.Mod ?? "the game, or no mod matched") + ")",
                "  run by: " + OwnershipLook.Plain(facts.RunBy),
                "  kept: " + facts.Kept,
                "  size: " + facts.SizeBytes.ToString(CultureInfo.InvariantCulture) + " bytes, all of it each time it is sent",
            };
            lines.AddRange(facts.Extras.Select(e => "  " + e.Key.ToLowerInvariant() + ": " + e.Value));
            lines.Add("  stored values (" + facts.Values.Count.ToString(CultureInfo.InvariantCulture) + "):");
            lines.AddRange(facts.Values.Select(v => "    " + v.Name + "   (" + (v.NamedIn ?? "") + ")   " + v.Type + "   " + v.Value));
            lines.Add("  names: " + (ObjectInspector.NamesReading ? "being read" : ObjectInspector.NamesNote));
            lines.Add("  the game's record:");
            lines.AddRange(facts.Record.Select(r => "    " + r.Key.ToLowerInvariant() + ": " + r.Value));
            lines.Add("  scripts by mod: " + ScriptsByMod.Summary(facts.Scripts));
            lines.Add("  how often it changes: the panel's World tab watches it from now on");
            return string.Join("\n", lines);
        }

        /// <summary>"vigil busiest [radius]": reads every loaded object around the player now and again after the watch.</summary>
        private string StartBusiest(string rest, bool toConsole)
        {
            if (_busyStart != null) return string.Format(CultureInfo.InvariantCulture, "already watching, {0:0} s left", Math.Max(0, _busyUntil - Now));
            if (Player.m_localPlayer == null || ZNetScene.instance == null) return "load into a world first";
            float radius = ConsoleHelp.BusiestRadius;
            if (!string.IsNullOrWhiteSpace(rest) && float.TryParse(rest.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float r)) radius = Mathf.Max(0f, r);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _busyStart = BusiestFirst(radius);
            _recorder?.AddOwnTime(watch.Elapsed.TotalMilliseconds);
            _busyRadius = radius;
            _busyFrom = Now;
            _busyUntil = Now + ConsoleHelp.BusiestSeconds;
            _busyToConsole = toConsole;
            return string.Format(CultureInfo.InvariantCulture, "watching {0} objects {1} for {2} s; the result follows",
                _busyStart.Count, radius > 0 ? "within " + radius.ToString("0", CultureInfo.InvariantCulture) + " m" : "loaded", ConsoleHelp.BusiestSeconds);
        }

        private void FinishBusiest()
        {
            var start = _busyStart;
            _busyStart = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string answer;
            try
            {
                var report = BusiestObjects.Rank(start, BusiestSecond(_busyRadius, start), Now - _busyFrom);
                var result = BusiestResult.Build(DateTime.Now, report, _busyRadius);
                string file = "Vigil-busiest-" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + ".tsv";
                result.File = file;
                result.Path = Path.Combine(_dir, file);
                File.WriteAllText(result.Path, BusiestTable(report));
                ResultStore.Shared.Put(ResultStore.Busiest, result);
                var verdict = result.Blocks.OfType<VerdictBlock>().FirstOrDefault();
                answer = "Vigil: busiest objects: " + (verdict?.Text ?? "done") + " (" + file + ")";
            }
            catch (Exception e)
            {
                answer = "Vigil: busiest objects failed: " + e.GetType().Name + " " + e.Message;
            }
            _recorder?.AddOwnTime(watch.Elapsed.TotalMilliseconds);
            if (_busyToConsole) _host.Reply(answer);
        }

        /// <summary>The first reading: each loaded object within the radius (0 for all) by id, with its change counter and place.</summary>
        private static Dictionary<string, BusiestObjects.Start> BusiestFirst(float radius)
        {
            var start = new Dictionary<string, BusiestObjects.Start>();
            var center = Player.m_localPlayer.transform.position;
            foreach (var kv in OwnershipView.Loaded())
            {
                var zdo = kv.Key;
                if (zdo == null || kv.Value == null) continue;
                var pos = zdo.GetPosition();
                if (radius > 0 && (pos - center).sqrMagnitude > radius * radius) continue;
                start[zdo.m_uid.ToString()] = new BusiestObjects.Start(zdo.DataRevision, pos.x, pos.y, pos.z);
            }
            return start;
        }

        /// <summary>The second reading, with each object's type and owner, and its size where it changed.</summary>
        private List<BusiestObjects.Seen> BusiestSecond(float radius, Dictionary<string, BusiestObjects.Start> start)
        {
            var seen = new List<BusiestObjects.Seen>();
            if (Player.m_localPlayer == null) return seen;
            var center = Player.m_localPlayer.transform.position;
            var names = OwnershipView.PlayerNames();
            long me = ZNet.GetUID(), server = OwnershipView.ServerUid();
            foreach (var kv in OwnershipView.Loaded())
            {
                var zdo = kv.Key;
                if (zdo == null || kv.Value == null) continue;
                var pos = zdo.GetPosition();
                float distance = Vector3.Distance(pos, center);
                if (radius > 0 && distance > radius) continue;
                string id = zdo.m_uid.ToString();
                bool changed = start.TryGetValue(id, out var first) && zdo.DataRevision != first.Revision;
                string type = _ownership.TypeOf(kv.Value) ?? OwnershipView.DisplayName(kv.Value.gameObject);
                seen.Add(new BusiestObjects.Seen(id, type, OwnershipCount.Name(zdo.GetOwner(), names, me, server), zdo.DataRevision,
                    changed ? ObjectInspector.SizeOf(zdo) : 0, distance, pos.x, pos.y, pos.z));
            }
            return seen;
        }

        private static string BusiestTable(BusiestObjects.Report report)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("object\ttype\trun_by\tchanges\tper_second\tsize_bytes\tbytes_per_second_at_most\tdistance_m\tmoved\n");
            foreach (var o in report.Objects)
            {
                sb.Append(o.Id).Append('\t').Append(o.Type).Append('\t').Append(o.Owner).Append('\t')
                    .Append(o.Changes.ToString(ci)).Append('\t').Append(o.PerSecond.ToString("0.00", ci)).Append('\t')
                    .Append(o.SizeBytes.ToString(ci)).Append('\t').Append(o.BytesPerSecond.ToString("0", ci)).Append('\t')
                    .Append(o.Distance.ToString("0.0", ci)).Append('\t').Append(o.Moved ? "yes" : "no").Append('\n');
            }
            return sb.ToString();
        }
    }
}
