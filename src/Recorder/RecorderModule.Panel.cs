using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The recorder in the panel, on the server's admin channel and in the self-test. Buttons run the
    /// same console commands; the server's state is asked for while the HUD line or the Network tab
    /// shows it, and only by an admin.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private Recorder.Settings _settings;
        private ServerStatus _serverStatus;
        private double _serverStatusAt = double.NegativeInfinity, _serverAskedAt = double.NegativeInfinity;
        private bool _serverAsking;
        private string _deepSeconds = "20", _censusRadius = "60", _playersMax = "16";
        private int _pattern, _loadTest;
        private bool _loadForce, _loadDeep;

        private static double Now => Time.realtimeSinceStartupAsDouble;

        /// <summary>The recorder's incident limits, which the HUD line colours by.</summary>
        internal HudLine.Limits Limits => new HudLine.Limits
        {
            SlowFps = _settings.SlowFps, PingMs = _settings.PingMs, QueueKb = _settings.QueueKb, MemoryLoadPct = _settings.MemoryLoadPct,
        };

        /// <summary>The last second's numbers for the HUD line; left unknown when the recorder has not recorded for a few seconds.</summary>
        internal HudLine.Values HudValues()
        {
            var v = new HudLine.Values { Recording = ProbeState.Active };
            var row = _recorder?.Last;
            if (row != null && Time.realtimeSinceStartup - _recorder.LastAt < 3f)
            {
                v.Fps = Num(row, "fps");
                v.FrameAvgMs = Num(row, "dt_avg_ms");
                v.FrameMaxMs = Num(row, "dt_max_ms");
                v.PingMs = Num(row, "ping_ms");
                v.QueueKb = Num(row, "send_queue_kb");
                v.MemLoadPct = Num(row, "mem_load");
            }
            if (_serverStatus != null && Now - _serverStatusAt < 15) v.ServerFps = _serverStatus.Fps;
            AlertHud(v);
            return v;
        }

        /// <summary>Every frame on a player's game: asks the server for its state every few seconds while something shows it.</summary>
        internal void PollServer(bool wanted, double every)
        {
            if (_server || !wanted || _serverAsking || Now - _serverAskedAt < every) return;
            if (_host.Link.Server.Lacking(Greeting.Status, "show its status", adminOnly: true) != null) return;
            AskServerStatus();
        }

        private void AskServerStatus()
        {
            _serverAsking = true;
            _serverAskedAt = Now;
            _host.Link.Ask("status", null, (ok, reply) =>
            {
                _serverAsking = false;
                if (!ok) return;
                _serverStatus = ServerStatus.From(reply);
                _serverStatusAt = Now;
            });
        }

        // ------------------------------------------------------------------ console: the server's switch

        /// <summary>"vigil server on|off|auto|status": the dedicated server's recording switch, through the admin channel.</summary>
        private string ServerCommand(string rest)
        {
            if (_server) return "Vigil: this is the server; use vigil on, off or auto";
            string word = string.IsNullOrEmpty(rest) ? "status" : rest.Trim().ToLowerInvariant();
            if (word != "status" && !RecordingPolicy.TryParse(word, out _)) return "Vigil: use: vigil server on|off|auto|status";
            string lacking = _host.Link.Server.Lacking(Greeting.Recording, "switch its recording", adminOnly: true);
            if (lacking != null) return "Vigil: " + lacking;
            _host.Link.Ask("recording", new Dictionary<string, object> { { "cmd", word } }, (ok, reply) =>
                _host.Reply("Vigil on the server: " + (reply.TryGetValue("text", out string t) ? t : ok ? "done" : "failed")));
            return "Vigil: asking the server's Vigil (" + word + ")";
        }

        // ------------------------------------------------------------------ the server's side of the admin channel

        public override Dictionary<string, object> Admin(string command, Dictionary<string, string> args, ZNetPeer from)
        {
            if (!_server) return null;
            if (command == "status") return StatusReply();
            if (command == "reports") return ReportsReply();
            if (command == "report") return ReportPartReply(args);
            if (command == "share") return ShareReply(args, from);
            if (command == Greeting.SpawnLog) return SpawnLogReply(args, from);
            if (command == SpawnView.Command) return SpawnViewReply(args);
            if (command == Greeting.Leftovers) return LeftoversReply(args, from);
            if (command == "parity") return ParityReply();
            if (command == Greeting.Alerts) return AlertsReply(args);
            if (command == "link")
            {
                args.TryGetValue("who", out string who);
                var peer = ZNet.instance?.GetPeers().FirstOrDefault(p => Recorder.PeerName(p) == who);
                if (peer == null) return new Dictionary<string, object> { { "text", "no player " + who + " on the server" }, { "ok", 0 } };
                var reply = new Dictionary<string, object>();
                foreach (var kv in LinkHistory.Format(DropWatch.Recent(peer.m_uid))) reply[kv.Key] = kv.Value;
                return reply;
            }
            if (command == "recording")
            {
                args.TryGetValue("cmd", out string word);
                string text = RecordingCommand(string.IsNullOrEmpty(word) ? "status" : word);
                if (word != null && word != "status") _host.Log.LogInfo("Vigil: recording switched to " + word + " by " + (from?.m_playerName ?? "an admin"));
                return new Dictionary<string, object> { { "text", text } };
            }
            return null;
        }

        private Dictionary<string, object> StatusReply()
        {
            var s = new ServerStatus
            {
                Version = VigilVersion.Value, Recording = RecordingStatus(), Incidents = _recorder.IncidentsWritten,
                LastIncident = _recorder.RecentIncidents.LastOrDefault(), UptimeMinutes = Time.realtimeSinceStartup / 60.0,
            };
            var row = _recorder.Last;
            if (row != null)
            {
                s.Fps = Num(row, "fps"); s.FrameAvgMs = Num(row, "dt_avg_ms"); s.FrameMaxMs = Num(row, "dt_max_ms");
                s.CpuPct = Num(row, "proc_cpu"); s.PrivateMb = Num(row, "private_mb"); s.MemLoadPct = Num(row, "mem_load");
            }
            foreach (var p in _recorder.LastPeers)
            {
                s.Players.Add(new ServerStatus.Player
                {
                    Name = Str(p, "peer"), PingMs = Num(p, "ping_ms"), QualityLocal = Num(p, "quality_local"), QualityRemote = Num(p, "quality_remote"),
                    QueueKb = Num(p, "send_queue_kb"), UnackedKb = Num(p, "unacked_kb"), OutKbs = Num(p, "app_out_kb"), InKbs = Num(p, "app_in_kb"),
                    ObjectsKnown = Num(p, "zdos_known"), LongestGapMs = Num(p, "max_gap_ms"),
                });
            }
            var d = new Dictionary<string, object>();
            foreach (var kv in s.Values()) d[kv.Key] = kv.Value;
            return d;
        }

        // ------------------------------------------------------------------ panel

        public override IEnumerable<PanelSection> Sections()
        {
            if (!_server) yield return new PanelSection("Overview", "Alerts", DrawAlerts, AlertsBadge);
            yield return new PanelSection("Overview", "Recording", DrawRecording, null, PanelZone.Measure);
            if (!_server) yield return new PanelSection("Overview", "Sharing", DrawSharing, SharingBadge);
            if (!_server)
            {
                yield return new PanelSection("Network", "This connection", DrawConnection, ConnectionBadge);
                yield return new PanelSection("Network", "The server", DrawServer, ServerBadge);
                yield return new PanelSection("Network", "The server's reports", DrawReports, ReportsBadge);
                yield return new PanelSection("World", "Who runs what", DrawOwnership, OwnershipBadge, PanelZone.Measure);
                yield return new PanelSection("World", "Inspect an object", DrawObject, ObjectBadge);
                yield return new PanelSection("World", "Weather and light", DrawWeather, WeatherBadge);
                yield return new PanelSection("World", "Spawns near you", DrawSpawns, SpawnsBadge);
                yield return new PanelSection("World", "Spawns across the server", DrawSpawnsAcross, SpawnsAcrossBadge);
                yield return new PanelSection("World", "What could spawn here", DrawSpawnable, SpawnableBadge);
                yield return new PanelSection("World", "Leftovers in the world", DrawLeftovers, LeftoversBadge, PanelZone.Change);
                yield return new PanelSection("Health", "Mods and configs", DrawParity, ParityBadge);
                yield return new PanelSection("Tools", "Inspect", DrawInspect, null, PanelZone.Measure);
                yield return new PanelSection("Tools", "Load test", DrawLoad, LoadBadge, PanelZone.Load);
            }
        }

        private static readonly string[] Switches = { "On", "Off", "Auto" };
        private static readonly string[] PatternNames = { "Stay", "Spread", "Travel", "Teleport" };

        /// <summary>
        /// The recording switch as a choice of three, the one set lit (none when not known); the word
        /// for the console when another is picked, null otherwise. The choices follow <see cref="RecordingMode"/>'s order.
        /// </summary>
        private static string SwitchChoice(int set)
        {
            int picked = Ui.Choice(set, Switches);
            return picked != set && picked >= 0 ? Switches[picked].ToLowerInvariant() : null;
        }

        /// <summary>Whether Vigil records right now, by the same rule UpdateRecording applies on the next frame.</summary>
        private bool RecordingNow() => RecordingPolicy.Records(_recording.Value, _server, InWorld(), _players, Time.realtimeSinceStartup - _lastPlayerSeen,
            Time.realtimeSinceStartup, _recorder.DeepActive || _load.Running);

        private void DrawRecording()
        {
            bool on = RecordingNow();
            Ui.BeginLine();
            Ui.Dot(on ? Skin.Tone.Good : Skin.Tone.Quiet);
            Ui.Text(on ? "Recording" : "Not recording");
            Ui.Note(Why());
            GUILayout.FlexibleSpace();
            string word = SwitchChoice((int)_recording.Value);
            if (word != null) _host.RunShown(word);
            Ui.EndLine();

            Ui.Heading("Write a report");
            if (Ui.Tool("Lag report", "The last 300 seconds, frame by frame, written now; also " + MarkKeyName(), "Write", primary: true))
            {
                var written = _host.RunForIncident("mark panel");
                if (written != null) _openIncident = written;
            }
            if (_recorder.DeepActive) Ui.Tool("Deep capture", "Timing every game method mods patch; the report follows when it ends", "Running", enabled: false);
            else if (Ui.Tool("Deep capture", "Times every game method mods patch, and which mod adds what", "Start", ref _deepSeconds, 40, "s")) _host.RunShown("deep " + _deepSeconds);
            ShowResult(ResultStore.Deep);

            Ui.Heading("This session's incidents");
            var recent = Results.Incidents;
            if (recent.Count == 0) Ui.Note("None so far.");
            foreach (var incident in recent)
            {
                bool open = ReferenceEquals(incident, _openIncident);
                if (Ui.ExpandRow(IncidentColumns, new[] { incident.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture), incident.Badge, incident.Summary }, open))
                    Ui.Later(() => _openIncident = open ? null : incident);
                if (open)
                {
                    Ui.ShowResult(incident, "result:incident", () => _openIncident = null, foldable: false);
                    DrawSendIncident(incident);
                }
            }
            Ui.BeginLine();
            GUILayout.FlexibleSpace();
            if (Ui.Quiet("Open the recordings folder")) Shell.Open(_dir);
            Ui.EndLine();
        }

        private bool Connected => _recorder.Last != null && ZNet.instance != null && !ZNet.instance.IsServer();

        private PanelBadge ConnectionBadge() =>
            Connected ? new PanelBadge("ping " + Ui.Num(Num(_recorder.Last, "ping_ms"), "0", " ms"), Skin.Tone.Quiet) : new PanelBadge("not connected", Skin.Tone.Quiet);

        private void DrawConnection()
        {
            if (!Connected)
            {
                Ui.Note("Join a server to see the connection to it, as Steam and the game see it.");
                return;
            }
            var row = _recorder.Last;
            Ui.Heading("The line");
            Ui.Row("Ping", Ui.Num(Num(row, "ping_ms"), "0", " ms"));
            Ui.Row("Quality", Ui.Num(Num(row, "quality_local"), "0.00") + " here, " + Ui.Num(Num(row, "quality_remote"), "0.00") + " at the server");
            Ui.Row("Longest silence", Ui.Num(Num(row, "max_gap_ms"), "0", " ms"));
            Ui.Heading("Traffic");
            Ui.Row("Up / down", Ui.Num(Num(row, "sock_out_kbs"), "0", " kB/s") + " / " + Ui.Num(Num(row, "sock_in_kbs"), "0", " kB/s"));
            Ui.Row("Steam's send rate", Ui.Num(Num(row, "steam_rate_kbs"), "0", " kB/s"));
            Ui.Heading("Waiting to go out");
            Ui.Row("In Steam", Ui.Num(Num(row, "pending_kb"), "0.0", " kB") + ", " + Ui.Num(Num(row, "queue_wait_ms"), "0", " ms") + " wait");
            Ui.Row("Sent, not confirmed", Ui.Num(Num(row, "unacked_kb"), "0.0", " kB"));
            Ui.Row("The game's queue", Ui.Num(Num(row, "send_queue_kb"), "0.0", " kB"));
            DrawOwnLink();
        }

        private PanelBadge ServerBadge()
        {
            var link = _host.Link.Server;
            switch (link.State)
            {
                case GreetingState.Answered: return new PanelBadge("Vigil " + link.Version + (link.IsAdmin ? ", admin" : ""), link.IsAdmin ? Skin.Tone.Good : Skin.Tone.Quiet);
                case GreetingState.NoAnswer: return new PanelBadge("no Vigil there", Skin.Tone.Caution);
                case GreetingState.Waiting: return new PanelBadge("asking", Skin.Tone.Quiet);
                default: return new PanelBadge("not connected", Skin.Tone.Quiet);
            }
        }

        private void DrawServer()
        {
            string lacking = _host.Link.Server.Lacking(Greeting.Status, "show its status", adminOnly: true);
            if (lacking != null)
            {
                Ui.Note(Sentence(lacking) + " The server's state shows here for its admins, when it runs Vigil too.");
                return;
            }
            var s = _serverStatus;
            if (s == null)
            {
                Ui.Note("Asking the server ...");
                return;
            }
            Ui.Row("Frames", Ui.Num(s.Fps, "0", " fps") + ", " + Ui.Num(s.FrameAvgMs, "0.0", " ms") + " on average, longest " + Ui.Num(s.FrameMaxMs, "0", " ms"));
            Ui.Row("Process", Ui.Num(s.CpuPct, "0", "% CPU") + ", " + Ui.Num(s.PrivateMb, "0", " MB") + ", machine memory " + Ui.Num(s.MemLoadPct, "0", "%"));
            Ui.Row("Running for", Ui.Num(s.UptimeMinutes, "0", " min"));
            var last = s.LastIncident != null ? IncidentLine.Parse(s.LastIncident) : null;
            Ui.Row("Incidents", Ui.Num(s.Incidents, "0") + (last != null ? ", the last at " + last.Time + ": " + last.Kind : ""));
            Ui.BeginFormLine("Recording");
            string word = SwitchChoice(RecordingPolicy.TryReadStatus(s.Recording, out var mode) ? (int)mode : -1);
            if (word != null)
            {
                _host.RunShown("server " + word);
                // Asked after the switch, so the server answers with it changed.
                AskServerStatus();
            }
            int comma = (s.Recording ?? "").IndexOf(", ", StringComparison.Ordinal);
            Ui.Note(comma < 0 ? "" : s.Recording.Substring(comma + 2));
            Ui.EndLine();

            Ui.Heading("Players as the server sees them");
            if (s.Players.Count == 0) Ui.Note("Nobody connected.");
            else
            {
                // The headings start where an open-able row's cells do, past its triangle.
                Ui.Table(new[] { new Ui.Column("", 8, false) }.Concat(PlayerColumns).ToList(), new List<string[]>());
                foreach (var p in s.Players)
                {
                    bool open = p.Name == _linkWho;
                    string[] cells =
                    {
                        p.Name, Ui.Num(p.PingMs, "0", " ms"), Ui.Num(p.QualityLocal, "0.00") + " / " + Ui.Num(p.QualityRemote, "0.00"),
                        Ui.Num(p.QueueKb, "0.0", " kB"), Ui.Num(p.UnackedKb, "0.0", " kB"), Ui.Num(p.InKbs, "0", " kB/s"), Ui.Num(p.OutKbs, "0", " kB/s"), Ui.Num(p.ObjectsKnown, "0"),
                    };
                    string who = p.Name;
                    if (Ui.ExpandRow(PlayerColumns, cells, open)) Ui.Later(() => { _linkWho = open ? null : who; _linkResult = null; _linkAskedAt = double.NegativeInfinity; });
                    if (open) DrawLink(who);
                }
            }
            Ui.BeginLine();
            Ui.Note(string.Format(CultureInfo.InvariantCulture, "As of {0:0} s ago.", Now - _serverStatusAt));
            GUILayout.FlexibleSpace();
            if (Ui.Quiet("Refresh")) AskServerStatus();
            Ui.EndLine();
        }

        private static readonly Ui.Column[] PlayerColumns =
        {
            new Ui.Column("Player", 0, false), new Ui.Column("Ping", 58, true), new Ui.Column("Quality", 82, true), new Ui.Column("Queue", 62, true),
            new Ui.Column("Unconfirmed", 84, true), new Ui.Column("Up", 64, true), new Ui.Column("Down", 64, true), new Ui.Column("Objects", 60, true),
        };

        // The player whose last minute is open under their row, and that minute as the server last sent it.
        private string _linkWho;
        private Result _linkResult;
        private double _linkAskedAt = double.NegativeInfinity;
        private bool _linkAsking;

        // This game's own connection's last minute, built once a second.
        private Result _ownLink;
        private double _ownLinkAt = double.NegativeInfinity;

        /// <summary>A player's last minute under their row, asked from the server every 2 s while it is open.</summary>
        private void DrawLink(string who)
        {
            string lacking = _host.Link.Server.Lacking(Greeting.Link, "show a player's last minute", adminOnly: true);
            if (lacking != null)
            {
                Ui.Note(Sentence(lacking));
                return;
            }
            if (!_linkAsking && Now - _linkAskedAt >= 2)
            {
                _linkAsking = true;
                _linkAskedAt = Now;
                _host.Link.Ask("link", new Dictionary<string, object> { { "who", who } }, (ok, reply) =>
                {
                    _linkAsking = false;
                    if (_linkWho != who) return;
                    _linkResult = ok ? LinkResult.Build(who, LinkHistory.Parse(reply), DateTime.Now)
                        : LinkResult.Build(who, new DropAnalysis.Sample[0], DateTime.Now);
                });
            }
            if (_linkResult == null) Ui.Note("Asking the server ...");
            else Ui.ShowResult(_linkResult, "result:link", null, foldable: false);
        }

        /// <summary>This game's connection to the server over the last minute, from its own seconds.</summary>
        private void DrawOwnLink()
        {
            var server = ZNet.instance?.GetServerPeer();
            if (server == null) return;
            if (Event.current.type == EventType.Layout && Now - _ownLinkAt >= 1)
            {
                _ownLink = LinkResult.Build("This connection", DropWatch.Recent(server.m_uid), DateTime.Now);
                _ownLinkAt = Now;
            }
            if (_ownLink == null) return;
            Ui.Heading("The last minute");
            Ui.ShowResult(_ownLink, "result:own-link", null, foldable: false);
        }

        private void DrawInspect()
        {
            if (Ui.Tool("Count objects", "Loaded objects within the radius by type and mod; 0 counts all", "Run", ref _censusRadius, 50, "m"))
                _host.RunForResult("census " + _censusRadius, ResultStore.Census);
            ShowResult(ResultStore.Census);
            if (Ui.Tool("What fills memory", "Textures, meshes and sounds by size and mod; can stall a moment", "Run")) _host.RunForResult("memory", ResultStore.Memory);
            ShowResult(ResultStore.Memory);
            if (Ui.Tool("Harmony patch map", "Every patch on a game method, and its mod", "Run")) _host.RunForResult("patches", ResultStore.Patches);
            ShowResult(ResultStore.Patches);
            bool tracing = MaterialTrace.Running;
            if (Ui.Tool("Material copies", tracing ? "Tracing who gives objects their own material copies" : "Who gives objects their own material copies, until stopped", tracing ? "Stop" : "Start"))
            {
                if (tracing) _host.RunForResult("trace materials stop", ResultStore.Trace);
                else _host.RunShown("trace materials");
            }
            ShowResult(ResultStore.Trace);
            Ui.Note("Each also writes a file into the recordings folder.");
        }

        private static ResultStore Results => ResultStore.Shared;

        private static readonly Ui.Column[] IncidentColumns = { new Ui.Column(null, 64, false), new Ui.Column(null, 76, false), new Ui.Column(null, 0, false) };

        /// <summary>The incident whose result is open under its row; null for none.</summary>
        private Result _openIncident;

        /// <summary>A tool's last result under its row, with its close button.</summary>
        private static void ShowResult(string tool) => Ui.ShowResult(Results.Last(tool), "result:" + tool, () => Results.Forget(tool));

        private PanelBadge LoadBadge() =>
            _load.Running ? new PanelBadge("running", Skin.Tone.Accent)
            : _host.Link.Server.Lacking(Greeting.Load, "run a load test", adminOnly: true) != null ? new PanelBadge("not available", Skin.Tone.Quiet) : null;

        private void DrawLoad()
        {
            string lacking = _host.Link.Server.Lacking(Greeting.Load, "run a load test", adminOnly: true);
            if (lacking != null)
            {
                Ui.Note(Sentence(lacking) + " A load test needs a dedicated server that runs Vigil, and you as one of its admins.");
                return;
            }
            if (_load.Running)
            {
                Ui.BeginLine();
                Ui.Dot(Skin.Tone.Accent);
                Ui.Text("A test is running. Stand still until it ends.");
                GUILayout.FlexibleSpace();
                if (Ui.Button("How far is it")) _host.RunShown("load status");
                // Stopping brings the form back above this line: it happens before the next layout pass.
                if (Ui.Button("Stop the test")) Ui.Later(() => _host.RunShown("load stop"));
                Ui.EndLine();
                return;
            }

            Ui.BeginFormLine("Test");
            _loadTest = Ui.Choice(_loadTest, LoadForm.Names);
            Ui.EndLine();
            var test = (LoadForm.Test)_loadTest;
            string playersLacking = test == LoadForm.Test.Players ? _host.Link.Server.Lacking(Greeting.Players, "simulate players", adminOnly: true) : null;
            if (test == LoadForm.Test.Players)
            {
                Ui.BeginFormLine("Players");
                Ui.Note("up to");
                _playersMax = Ui.Field(_playersMax, 40);
                Ui.Note("moving");
                _pattern = Ui.Choice(_pattern, PatternNames);
                Ui.EndLine();
            }
            Ui.BeginFormLine("Options");
            _loadForce = Ui.Toggle(_loadForce, "Run with other players online");
            _loadDeep = Ui.Toggle(_loadDeep, "Deep capture on both machines");
            Ui.EndLine();
            Ui.BeginFormLine("");
            if (playersLacking != null) Ui.Note(Sentence(playersLacking));
            else if (Ui.Primary("Start test"))
            {
                // A started test replaces the form above with its running line: it starts before the next layout pass.
                string command = LoadForm.Command(test, _playersMax, LoadTest.Patterns[_pattern], _loadForce, _loadDeep);
                Ui.Later(() => _host.RunShown(command));
            }
            Ui.Note("Stand still, at least 400 m from the world's centre.");
            Ui.EndLine();
            ShowResult(ResultStore.Load);
        }

        /// <summary>A reason as the panel shows it: capitalised, with a full stop.</summary>
        private static string Sentence(string text) =>
            string.IsNullOrEmpty(text) ? "" : char.ToUpperInvariant(text[0]) + text.Substring(1) + (text.EndsWith(".", StringComparison.Ordinal) ? "" : ".");

        // ------------------------------------------------------------------ self-test

        public override void SelfTest(List<SelfTestReport.Check> checks)
        {
            const string area = "Recorder";
            checks.Add(Patching.Problems.Count == 0
                ? new SelfTestReport.Check(area, "hooks put in", SelfTestReport.Result.Pass, null)
                : new SelfTestReport.Check(area, "hooks put in", SelfTestReport.Result.Warn, Patching.Problems.Count + " not: " + string.Join("; ", Patching.Problems.Take(3).ToArray())));
            IReadOnlyList<string> failed = (IReadOnlyList<string>)_timer?.Failed ?? new List<string>();
            checks.Add(failed.Count == 0
                ? new SelfTestReport.Check(area, "timers on game and mod methods", SelfTestReport.Result.Pass, null)
                : new SelfTestReport.Check(area, "timers on game and mod methods", SelfTestReport.Result.Warn, failed.Count + " not: " + string.Join("; ", failed.Take(3).ToArray())));
            var own = _timer?.Slots.Where(s => s.Mod == OwnTime.Mod && !s.Coroutine && s.RunCalls + s.FrameCalls + s.SecondCalls > 0).ToList() ?? new List<PhaseTimer.Slot>();
            checks.Add(own.Count > 0
                ? new SelfTestReport.Check(area, "Vigil's own work in the per-mod timing", SelfTestReport.Result.Pass, own.Count + " parts timed, such as " + own[0].Label)
                : new SelfTestReport.Check(area, "Vigil's own work in the per-mod timing", ProbeState.Active ? SelfTestReport.Result.Warn : SelfTestReport.Result.Info,
                    ProbeState.Active ? "nothing timed yet" : "not recording now"));
            if (!_server)
            {
                checks.Add(SpawnWatch.HookProblem == null
                    ? new SelfTestReport.Check(area, "spawn hooks", SelfTestReport.Result.Pass, SpawnWatch.Log.Records.Count + " spawns seen this session")
                    : new SelfTestReport.Check(area, "spawn hooks", SelfTestReport.Result.Warn, SpawnWatch.HookProblem + "; the spawn log stays empty"));
            }
            checks.Add(Writable(_dir)
                ? new SelfTestReport.Check(area, "recordings folder writable", SelfTestReport.Result.Pass, _dir)
                : new SelfTestReport.Check(area, "recordings folder writable", SelfTestReport.Result.Fail, _dir));
            checks.Add(new SelfTestReport.Check(area, "recording", SelfTestReport.Result.Info, RecordingStatus()));
            if (!ExceptionWatch.Works) checks.Add(new SelfTestReport.Check(area, "exceptions counted", SelfTestReport.Result.Info, "this runtime does not report them as they are thrown"));
            checks.Add(_journal.PreviousEndedCleanly
                ? new SelfTestReport.Check(area, "previous session quit normally", SelfTestReport.Result.Pass, null)
                : new SelfTestReport.Check(area, "previous session quit normally", SelfTestReport.Result.Warn, "it did not (crash, kill or power loss)"));
            SteamChecks(area, checks);
        }

        private void SteamChecks(string area, List<SelfTestReport.Check> checks)
        {
            const string what = "Steam's own numbers per connection";
            var znet = ZNet.instance;
            if (znet == null) { checks.Add(new SelfTestReport.Check(area, what, SelfTestReport.Result.Info, "not connected")); return; }
            var sockets = _server
                ? znet.GetPeers().Where(p => p?.m_socket != null && !Bots.IsBot(p)).Select(p => p.m_socket).ToList()
                : new List<ISocket> { znet.GetServerPeer()?.m_socket }.Where(s => s != null).ToList();
            if (sockets.Count == 0) { checks.Add(new SelfTestReport.Check(area, what, SelfTestReport.Result.Info, _server ? "nobody connected" : "not connected to a server")); return; }
            int readable = 0;
            string why = null;
            foreach (var socket in sockets)
            {
                if (SteamLink.TryRead(socket, out _, out string reason)) readable++;
                else why ??= reason;
            }
            string detail = readable + " of " + sockets.Count + " readable" + (why != null ? ": " + why : "");
            checks.Add(new SelfTestReport.Check(area, what, readable == sockets.Count ? SelfTestReport.Result.Pass : readable == 0 ? SelfTestReport.Result.Fail : SelfTestReport.Result.Warn, detail));
        }

        private static bool Writable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, "Vigil-state", "write.probe");
                Directory.CreateDirectory(Path.GetDirectoryName(probe));
                File.WriteAllText(probe, "probe");
                File.Delete(probe);
                return true;
            }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------ row values

        internal static double Num(TimelineTable.Row row, string column)
        {
            try
            {
                switch (row.Get(column))
                {
                    case null: return double.NaN;
                    case double d: return d;
                    case float f: return f;
                    case int i: return i;
                    case long l: return l;
                    case IConvertible c when !(c is string): return c.ToDouble(CultureInfo.InvariantCulture);
                    case string s: return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
                    default: return double.NaN;
                }
            }
            catch (Exception) { return double.NaN; }
        }

        private static string Str(TimelineTable.Row row, string column)
        {
            try { return row.Get(column)?.ToString(); }
            catch (Exception) { return null; }
        }
    }
}
