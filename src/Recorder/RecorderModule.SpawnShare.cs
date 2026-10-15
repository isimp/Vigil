using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using Vigil.Core;
using Check = Vigil.Core.SelfTestReport.Check;
using Outcome = Vigil.Core.SelfTestReport.Result;

namespace Vigil.Recorder
{
    /// <summary>
    /// Shared spawn logs. A player whose sharing sends the spawn log passes the spawns their game
    /// made while on the server, and the ones mods cancelled, to the server's Vigil once a minute
    /// (<see cref="SpawnShare"/>). A server that takes shared reports keeps everyone's
    /// (<see cref="SpawnMerge"/>), appends each player's to shared/&lt;player&gt;/Vigil-spawnlog-&lt;start&gt;.tsv,
    /// and keeps the builders' names it sees in Vigil-state/players.tsv. Its admins see them merged in
    /// the World tab's "Spawns across the server": spawns inside bases by place, each with a pin for
    /// their own map, a line per game that opens to its spawns, and what mods cancelled.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private static readonly string[] SpawnViewWindows = { "1 h", "6 h", "Session" };
        private static readonly double[] SpawnViewSeconds = { 3600, 6 * 3600, 0 };
        private static readonly Ui.Column[] SpawnGameColumns =
        {
            new Ui.Column(null, 130, false), new Ui.Column(null, 90, false), new Ui.Column(null, 110, false), new Ui.Column(null, 0, false),
        };

        private static readonly Ui.Column[] SpawnCancelColumns = { new Ui.Column("Mod", 160, false), new Ui.Column("Cancelled", 0, false) };

        private const long SpawnLogFileBytes = 5L * 1024 * 1024;
        private const int SpawnLogFilesKept = 3, SpawnRowsSent = 60;

        // A player's side: up to which spawn (game clock) the server has the log, and the send under way.
        private double _spawnSentUpTo = double.NegativeInfinity, _spawnLogNextAt;
        private bool _spawnLogAsking, _spawnLogLinked;
        private int _spawnLogSent;
        private string _spawnLogError;

        // The server's side.
        private readonly SpawnMerge _spawnMerge = new SpawnMerge();
        private string _spawnLogStamp;
        private bool _spawnNamesRead;

        // An admin's card.
        private int _spawnWindow;
        private SpawnView _spawnView, _spawnViewShown;
        private string _spawnViewError, _spawnGameOpen, _spawnOpenShown;
        private List<SpawnLog.Row> _spawnGameRows, _spawnRowsShown;
        private double _spawnViewAt = double.NegativeInfinity;
        private bool _spawnViewAsking;
        private readonly Dictionary<string, Minimap.PinData> _spawnPins = new Dictionary<string, Minimap.PinData>(StringComparer.Ordinal);
        private Minimap _spawnPinsOn;

        // ------------------------------------------------------------------ the player's side

        /// <summary>Every frame on a player's game: once a minute, sends the spawns made since the last send when sharing asks for the spawn log.</summary>
        private void UpdateSpawnLogSharing()
        {
            if (_server) return;
            bool linked = _host.Link.Server.Lacking(Greeting.SpawnLog, "keep spawn logs", adminOnly: false) == null;
            if (linked && !_spawnLogLinked)
            {
                // A new connection: only the spawns made on this server go, the first send a minute on.
                _spawnSentUpTo = Math.Max(_spawnSentUpTo, Now - 15);
                _spawnLogNextAt = Now + SpawnShare.EverySeconds;
            }
            _spawnLogLinked = linked;
            if (!linked || !_share.Value || (_shareKinds.Value & ShareKinds.SpawnLogs) == 0 || _spawnLogAsking || _load.Running || Now < _spawnLogNextAt) return;
            _spawnLogNextAt = Now + SpawnShare.EverySeconds;
            var due = SpawnShare.Due(SpawnWatch.Log.Records, SpawnWatch.Log.CancelledRecords, _spawnSentUpTo, SpawnShare.MaxRows);
            if (due.Count == 0) return;
            double upTo = due[due.Count - 1].At;
            long me = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0;
            var args = new Dictionary<string, object> { { "rows", SpawnShare.Encode(due, Now, DateTime.UtcNow) }, { "me", me.ToString(CultureInfo.InvariantCulture) } };
            _spawnLogAsking = true;
            _host.Link.Ask(Greeting.SpawnLog, args, (ok, reply) =>
            {
                _spawnLogAsking = false;
                if (!ok)
                {
                    _spawnLogError = reply.TryGetValue("text", out string why) ? why : "no answer";
                    return;
                }
                _spawnSentUpTo = Math.Max(_spawnSentUpTo, upTo);
                _spawnLogSent += due.Count;
                _spawnLogError = null;
                if (due.Count == SpawnShare.MaxRows) _spawnLogNextAt = Now + 2; // more waiting: the next part soon
            });
        }

        private string SpawnLogState()
        {
            if (_spawnLogError != null) return "not taken: " + _spawnLogError;
            if (!_spawnLogLinked) return "this server does not keep spawn logs";
            return _spawnLogSent > 0 ? _spawnLogSent + " spawns sent this session" : "nothing sent yet; it goes once a minute";
        }

        // ------------------------------------------------------------------ the server's side

        private Dictionary<string, object> SpawnLogReply(Dictionary<string, string> args, ZNetPeer from)
        {
            if (!_accept.Value) return new Dictionary<string, object> { { "ok", 0 }, { "text", "this server does not take shared reports" } };
            string player = from?.m_playerName ?? "a player";
            args.TryGetValue("rows", out string rows);
            var spawns = SpawnShare.Decode(rows, player, SpawnShare.MaxRows);
            _spawnMerge.Add(player, spawns, DateTime.UtcNow);
            ReadSpawnNames();
            bool learned = args.TryGetValue("me", out string me) && long.TryParse(me, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && _spawnMerge.Learn(id, player);
            if (LearnPlayerNames() || learned) SaveSpawnNames();
            AddAlerts(AlertSources.FromSpawns(spawns, _spawnMerge.NameOf, DateTime.UtcNow));
            AppendSpawnLog(SharedStore.Folder(player), rows, spawns.Count);
            return new Dictionary<string, object> { { "kept", spawns.Count } };
        }

        private Dictionary<string, object> SpawnViewReply(Dictionary<string, string> args)
        {
            double window = KeyValues.Number(args, "window");
            if (double.IsNaN(window)) window = 3600;
            ReadSpawnNames();
            if (LearnPlayerNames()) SaveSpawnNames();
            if (args.TryGetValue("player", out string player) && !string.IsNullOrEmpty(player))
                return SpawnView.EncodeRows(_spawnMerge.Rows(player, DateTime.UtcNow, window).Take(SpawnRowsSent));
            return _spawnMerge.View(DateTime.UtcNow, window).Encode();
        }

        // The players on the server now, by the id and name their character carries.
        private bool LearnPlayerNames()
        {
            bool any = false;
            foreach (var peer in ZNet.instance != null ? ZNet.instance.GetPeers() : new List<ZNetPeer>())
            {
                var zdo = ZDOMan.instance?.GetZDO(peer.m_characterID);
                if (zdo != null) any |= _spawnMerge.Learn(zdo.GetLong(ZDOVars.s_playerID, 0L), zdo.GetString(ZDOVars.s_playerName, ""));
            }
            return any;
        }

        private string SpawnNamesFile => Path.Combine(Path.Combine(_dir, "Vigil-state"), "players.tsv");

        private void ReadSpawnNames()
        {
            if (_spawnNamesRead) return;
            _spawnNamesRead = true;
            try
            {
                if (!File.Exists(SpawnNamesFile)) return;
                foreach (var line in File.ReadAllLines(SpawnNamesFile))
                {
                    var v = line.Split('\t');
                    if (v.Length >= 2 && long.TryParse(v[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)) _spawnMerge.Learn(id, v[1]);
                }
            }
            catch (Exception) { }
        }

        private void SaveSpawnNames()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SpawnNamesFile));
                File.WriteAllLines(SpawnNamesFile, _spawnMerge.Names.Select(kv => kv.Key.ToString(CultureInfo.InvariantCulture) + "\t" + kv.Value.Replace('\t', ' ')).ToArray());
            }
            catch (Exception) { }
        }

        // One file a player and server start; the oldest beyond the newest three go when a new one starts.
        private void AppendSpawnLog(string folder, string rows, int count)
        {
            if (count == 0 || string.IsNullOrEmpty(rows)) return;
            try
            {
                if (_spawnLogStamp == null) _spawnLogStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string dir = Path.Combine(Path.Combine(_dir, ReportCatalog.SharedFolder), folder);
                string file = Path.Combine(dir, "Vigil-spawnlog-" + _spawnLogStamp + ".tsv");
                if (!File.Exists(file))
                {
                    Directory.CreateDirectory(dir);
                    foreach (var old in Directory.GetFiles(dir, "Vigil-spawnlog-*.tsv").OrderByDescending(f => f, StringComparer.Ordinal).Skip(SpawnLogFilesKept - 1)) File.Delete(old);
                }
                else if (new FileInfo(file).Length > SpawnLogFileBytes) return;
                File.AppendAllText(file, rows + "\n");
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ an admin's card

        private PanelBadge SpawnsAcrossBadge()
        {
            var view = _spawnViewShown;
            if (view == null) return null;
            if (view.InsideBases > 0) return new PanelBadge(view.InsideBases + " inside bases", Skin.Tone.Accent);
            return new PanelBadge(view.Games.Count + (view.Games.Count == 1 ? " game" : " games"), Skin.Tone.Quiet);
        }

        private void DrawSpawnsAcross()
        {
            string lacking = _host.Link.Server.Lacking(Greeting.SpawnLog, "show everyone's spawns", adminOnly: true);
            if (lacking != null)
            {
                Ui.Note(Sentence(lacking) + " Everyone's spawns show here for the server's admins, when the server's Vigil takes shared reports.");
                return;
            }

            Ui.BeginFormLine("Window");
            int picked = Ui.Choice(_spawnWindow, SpawnViewWindows);
            Ui.EndLine();
            if (picked >= 0 && picked != _spawnWindow)
            {
                int chosen = picked;
                Ui.Later(() => { _spawnWindow = chosen; _spawnViewAt = double.NegativeInfinity; _spawnGameRows = null; });
            }

            // Asked and read on a layout pass only, so the pass that draws the card draws what was laid out.
            if (Event.current.type == EventType.Layout)
            {
                CheckPinsMap();
                if (!_spawnViewAsking && Now - _spawnViewAt >= 10) AskSpawnView();
                _spawnViewShown = _spawnView;
                _spawnRowsShown = _spawnGameRows;
                _spawnOpenShown = _spawnGameOpen;
            }

            var view = _spawnViewShown;
            if (view == null)
            {
                Ui.Note(_spawnViewError != null ? Sentence(_spawnViewError) : "Asking the server ...");
                return;
            }

            Ui.Verdict(view.InsideBases > 0 ? ResultTone.Caution : ResultTone.Good, view.Verdict);
            if (view.Places.Count > 0)
            {
                Ui.Heading("Inside bases, by place");
                foreach (var place in view.Places)
                {
                    bool pinned = _spawnPins.ContainsKey(PinKey(place));
                    if (Ui.Tool(place.Label, place.Creatures + " · " + place.Ago + " ago", pinned ? "Unpin" : "Pin"))
                    {
                        var p = place;
                        Ui.Later(() => TogglePin(p));
                    }
                }
            }

            Ui.Heading("By game");
            if (view.Games.Count == 0) Ui.Note("No player sends a spawn log yet: each ticks Spawn log under Sharing on the Overview tab.");
            foreach (var g in view.Games)
            {
                bool open = _spawnOpenShown == g.Player;
                string inBase = g.InBase > 0 ? "<color=#E8B04A>" + g.InBase + " in a base</color>" : "none in a base";
                if (Ui.ExpandRow(SpawnGameColumns, new[] { g.Player, g.Spawns + (g.Spawns == 1 ? " spawn" : " spawns"), inBase, "sent " + g.LastSent }, open))
                {
                    string who = open ? null : g.Player;
                    Ui.Later(() => OpenSpawnGame(who));
                }
                if (!open) continue;
                var rows = _spawnRowsShown;
                if (rows == null) Ui.Note("Asking the server ...");
                else if (rows.Count == 0) Ui.Note("No spawns from this game in the window.");
                else Ui.WrapTable(SpawnColumns, rows.Select(r => new[] { r.When, Mark(r, r.What), Mark(r, r.How), Mark(r, r.Where) }));
            }

            if (view.Cancelled.Count > 0)
            {
                Ui.Heading("Cancelled by mods");
                Ui.Table(SpawnCancelColumns, view.Cancelled.Select(c => new[] { c.Mod, c.Count + " (" + c.Creatures + ")" }));
            }

            Ui.Note(string.Format(CultureInfo.InvariantCulture,
                "As of {0:0} s ago, from the players whose Vigil sends its spawn log (Sharing on the Overview tab), once a minute. Spawns run on the game of whoever's area it is.",
                Math.Max(0, Now - _spawnViewAt)));
        }

        private void AskSpawnView()
        {
            _spawnViewAsking = true;
            _spawnViewAt = Now;
            _host.Link.Ask(SpawnView.Command, new Dictionary<string, object> { { "window", SpawnViewSeconds[_spawnWindow] } }, (ok, reply) =>
            {
                _spawnViewAsking = false;
                if (!ok)
                {
                    _spawnViewError = reply.TryGetValue("text", out string why) ? why : "no answer";
                    return;
                }
                _spawnView = SpawnView.Decode(reply);
                _spawnViewError = null;
                if (_spawnGameOpen != null) AskSpawnGame(_spawnGameOpen);
            });
        }

        private void OpenSpawnGame(string player)
        {
            _spawnGameOpen = player;
            _spawnGameRows = null;
            if (player != null) AskSpawnGame(player);
        }

        private void AskSpawnGame(string player)
        {
            var args = new Dictionary<string, object> { { "window", SpawnViewSeconds[_spawnWindow] }, { "player", player } };
            _host.Link.Ask(SpawnView.Command, args, (ok, reply) =>
            {
                if (_spawnGameOpen != player) return;
                _spawnGameRows = ok ? SpawnView.DecodeRows(reply) : new List<SpawnLog.Row>();
            });
        }

        private static string PinKey(SpawnView.Place place) =>
            place.Label + "@" + Math.Round(place.X).ToString(CultureInfo.InvariantCulture) + "," + Math.Round(place.Z).ToString(CultureInfo.InvariantCulture);

        // A pin on this player's own map only (not saved, not shared), gone on leaving the world.
        private void TogglePin(SpawnView.Place place) => TogglePinAt(PinKey(place), place.X, place.Z, "Spawns: " + place.Label);

        private void TogglePinAt(string key, float x, float z, string label)
        {
            var map = Minimap.instance;
            if (map == null) return;
            CheckPinsMap();
            if (_spawnPins.TryGetValue(key, out var pin))
            {
                map.RemovePin(pin);
                _spawnPins.Remove(key);
                return;
            }
            _spawnPins[key] = map.AddPin(new Vector3(x, 0f, z), Minimap.PinType.Icon3, label, false, false);
        }

        // Pins go with the map when the world unloads; a new map starts with none of them.
        private void CheckPinsMap()
        {
            if (_spawnPinsOn == Minimap.instance) return;
            _spawnPins.Clear();
            _spawnPinsOn = Minimap.instance;
        }

        // ------------------------------------------------------------------ console

        /// <summary>"vigil spawned server [minutes]": everyone's spawn logs, merged by the server's Vigil, for its admins.</summary>
        private string SpawnedServerCommand(string rest)
        {
            if (_server) return "this is the server; its admins ask from their game";
            string lacking = _host.Link.Server.Lacking(Greeting.SpawnLog, "show everyone's spawns", adminOnly: true);
            if (lacking != null) return lacking;
            double minutes = 60;
            if (!string.IsNullOrWhiteSpace(rest) && double.TryParse(rest.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m > 0) minutes = m;
            _host.Link.Ask(SpawnView.Command, new Dictionary<string, object> { { "window", minutes * 60 } }, (ok, reply) =>
            {
                if (!ok)
                {
                    _host.Reply("Vigil on the server: " + (reply.TryGetValue("text", out string why) ? why : "no answer"));
                    return;
                }
                var view = SpawnView.Decode(reply);
                var lines = new List<string> { "Vigil on the server: " + view.Verdict };
                lines.AddRange(view.Places.Select(p => "  " + p.Label + ": " + p.Creatures + ", " + p.Ago + " ago"));
                lines.AddRange(view.Games.Select(g => "  " + g.Player + ": " + g.Spawns + " spawns, " + g.InBase + " in a base, sent " + g.LastSent));
                lines.AddRange(view.Cancelled.Select(c => "  cancelled by " + c.Mod + ": " + c.Count + " (" + c.Creatures + ")"));
                if (view.Games.Count == 0) lines.Add("  no player sends a spawn log yet");
                _host.Reply(string.Join("\n", lines));
            });
            return "asking the server's Vigil for everyone's spawns in the last " + minutes.ToString("0.#", CultureInfo.InvariantCulture) + " minutes";
        }

        // ------------------------------------------------------------------ self-test (full)

        private IEnumerator SpawnViewOnServer(List<Check> checks)
        {
            const string what = "everyone's spawn logs on the server";
            if (!CanAsk(checks, what, Greeting.SpawnLog, adminOnly: true)) yield break;
            var a = Ask(SpawnView.Command, new Dictionary<string, object> { { "window", 3600 } });
            while (!a.Done) yield return null;
            if (!a.Ok)
            {
                checks.Add(new Check(Here, what, Outcome.Fail, Why(a)));
                yield break;
            }
            var view = SpawnView.Decode(a.Reply);
            checks.Add(new Check(Here, what, SelfTestRoutines.Judge(true, a.Seconds),
                view.Games.Count + (view.Games.Count == 1 ? " game sends" : " games send") + " its spawn log; " + view.Verdict));
        }
    }
}
