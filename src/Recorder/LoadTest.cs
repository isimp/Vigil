using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Vigil.Core;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Vigil.Recorder
{
    /// <summary>
    /// The load test ("vigil load" in a player's console): the player's game and the server,
    /// both running Vigil, raise world-update traffic step by step, first from the player to the
    /// server, then back, and measure how far each falls behind and how fast it catches up. Both
    /// write a report (Vigil-load-*.txt) and a per-second table (Vigil-load-*.tsv) holding
    /// both machines' numbers.
    /// The traffic is temporary world objects without a type (prefab 0): no game builds an object
    /// for them or logs anything about them, they are not kept, so the world save never holds them,
    /// and the server removes a player's ones as soon as that player's connection drops. A beacon
    /// object among them carries a number that goes up ten times a second; the number the other
    /// machine reports holding tells how old its copy of the world is.
    /// </summary>
    internal sealed class LoadTest
    {
        private const string RpcRequest = "Vigil_LoadRequest", RpcReply = "Vigil_LoadReply", RpcPing = "Vigil_LoadPing",
            RpcPong = "Vigil_LoadPong", RpcStop = "Vigil_LoadStop";
        private static readonly int s_markHash = "vigil_load".GetStableHashCode();
        private static readonly int s_seqHash = "vigil_seq".GetStableHashCode();
        private static readonly int s_dataHash = "vigil_data".GetStableHashCode();
        private const int MarkUp = 1, MarkDown = 2;
        private const int Payload = 200;
        private const double BeaconEvery = 0.1, PingEvery = 0.5, ReplyTimeout = 10, SilenceTimeout = 15, DeepWaitLimit = 180;
        private const float DeepSeconds = 3600;
        private const double SpreadMeters = 3, MovedWarnMeters = 32;

        private static LoadTest s_current;

        private readonly bool _server;
        private readonly string _dir;
        private readonly string _version;
        private readonly Recorder _recorder;
        private readonly PhaseTimer _timer;
        private readonly Action<string> _log;
        private ZRoutedRpc _registeredOn;
        private Session _s;
        private int _errors;

        private sealed class TestObject
        {
            public ZDO Zdo;
            public ZDOID Id;
            public byte[] A, B;
            public bool UseB;
            public uint Count;
        }

        private sealed class Session
        {
            public bool Server;
            public long Peer;
            public string PeerName = "";
            public string Mode;
            public bool Force, Deep, Accepted, DeepHere, DeepReady;
            public LoadPlan Plan;
            public LoadSchedule Schedule;
            public readonly LoadLedger Ledger = new LoadLedger();
            public readonly StageCosts Costs = new StageCosts();
            public readonly LoadGuard Guard = new LoadGuard();
            public double Requested, LastHeard, LastPing, LastBeacon, LastFrame, WaitStarted;
            public readonly List<TestObject> Objects = new List<TestObject>();
            public readonly HashSet<ZDOID> Owned = new HashSet<ZDOID>();
            public ZDO Beacon;
            public ZDOID BeaconId = ZDOID.None;
            public int Lost;
            public readonly SeqClock Clock = new SeqClock(1500);
            public ZDOID RemoteBeacon = ZDOID.None;
            public int RemoteSeqHeld;
            public double Budget;
            public int Next;
            public Vector3 Center, StartPos;
            public long SentBytes, RecvBytes, TestTicks;
            public readonly List<double> Rtt = new List<double>(), Stale = new List<double>();
            public Dictionary<string, string> Remote = new Dictionary<string, string>();
            public Dictionary<string, object> Mine = new Dictionary<string, object>();
            public string AbortToSend;
            public readonly Dictionary<int, double> WorstMs = new Dictionary<int, double>();
            public readonly Dictionary<int, string> Worst = new Dictionary<int, string>();
            public readonly List<string> Notes = new List<string>();
            public double MaxMoved;
            public StreamWriter Tsv;
            public TimelineTable Table;
            public string Stamp;
            public DateTime StartedAt = DateTime.Now;
            // player simulation
            public bool Players;
            public string Pattern = "stay";
            public int MaxPlayers = ConsoleHelp.PlayersDefault, ObjectsPerPlayer = ConsoleHelp.ObjectsDefault;
            public double LinkKbps = ConsoleHelp.LinkDefault;
            public Bots Bots;
            public double WorkMs = double.NaN;
        }

        public static readonly string[] Patterns = { "stay", "spread", "travel", "teleport" };

        /// <param name="server">True on a dedicated server.</param>
        /// <param name="dir">Where the reports go.</param>
        /// <param name="version">Vigil's version, sent to the other machine.</param>
        /// <param name="recorder">The recorder whose seconds the test reads.</param>
        /// <param name="timer">The timers, for the deep captures and the per-step method times.</param>
        /// <param name="log">Writes a line to the game's log.</param>
        public LoadTest(bool server, string dir, string version, Recorder recorder, PhaseTimer timer, Action<string> log)
        {
            _server = server; _dir = dir; _version = version; _recorder = recorder; _timer = timer; _log = log;
            s_current = this;
            recorder.SecondRow = OnSecond;
            recorder.SlotsSecond = OnSlots;
        }

        /// <summary>The number after <paramref name="word"/> in the arguments, or <paramref name="fallback"/>.</summary>
        private static int After(string[] args, string word, int fallback)
        {
            int i = Array.IndexOf(args, word);
            return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
        }

        private static double Now => Time.realtimeSinceStartupAsDouble;
        public bool Running => _s != null;

        // ------------------------------------------------------------------ console

        public string Command(string rest)
        {
            var args = (rest ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (args.Contains("stop"))
            {
                if (_s == null) return "no load test is running";
                Finish("stopped from the console", tellOther: true);
                return "load test stopped; its report is in " + _dir;
            }
            if (args.Contains("status")) return Status();
            if (_server) return "a load test is started from a player's game connected to this server";
            if (_s != null) return "a load test is already running (vigil load stop ends it)";
            var znet = ZNet.instance;
            if (znet == null || Player.m_localPlayer == null || ZRoutedRpc.instance == null || ZDOMan.instance == null) return "join a world on the server first";
            if (znet.IsServer()) return "this game hosts the world itself; the load test needs a dedicated server";
            var pos = Player.m_localPlayer.transform.position;
            string refusal = LoadAdmission.Refusal(0, true, false, new Vector2(pos.x, pos.z).magnitude);
            if (refusal != null) return "load test refused: " + refusal;
            EnsureRegistered();
            bool players = args.Contains("players");
            string mode = players ? "players" : args.Contains("up") ? "up" : args.Contains("down") ? "down" : "both";
            var s = NewSession(false, mode, args.Contains("force"), args.Contains("deep"), After(args, "players", ConsoleHelp.PlayersDefault));
            if (players)
            {
                s.Pattern = Patterns.FirstOrDefault(args.Contains) ?? "stay";
                s.ObjectsPerPlayer = Math.Max(0, Math.Min(100, After(args, "objects", ConsoleHelp.ObjectsDefault)));
                s.LinkKbps = Math.Max(16, Math.Min(100000, After(args, "link", ConsoleHelp.LinkDefault)));
            }
            s.Requested = Now;
            s.StartPos = pos;
            _s = s;
            Send(s, RpcRequest, new Dictionary<string, object>
            {
                { "v", _version }, { "mode", mode }, { "force", s.Force ? 1 : 0 }, { "deep", s.Deep ? 1 : 0 },
                { "max", s.MaxPlayers }, { "pattern", s.Pattern }, { "objects", s.ObjectsPerPlayer }, { "link", s.LinkKbps },
            });
            string what = players
                ? string.Format(CultureInfo.InvariantCulture, "player simulation (up to {0} simulated players, {1}, {2} objects each, {3:0} kB/s links", s.MaxPlayers, s.Pattern, s.ObjectsPerPlayer, s.LinkKbps)
                : "load test (" + mode;
            return string.Format(CultureInfo.InvariantCulture, "{0}{1}{2}): asking the server; it runs about {3:0} minutes{4}. Stand still; vigil load stop ends it.",
                what, s.Force ? ", force" : "", s.Deep ? ", deep" : "", s.Plan.TotalSeconds / 60, s.Deep ? " once both deep captures are measuring" : "");
        }


        private bool IsAdmin(ZNetPeer peer)
        {
            try { return peer?.m_socket != null && ZNet.instance.IsAdmin(peer.m_socket.GetHostName()); }
            catch (Exception) { return false; }
        }


        private string Status()
        {
            var s = _s;
            if (s == null) return "no load test is running";
            if (!s.Accepted) return "load test: waiting for the server's answer";
            if (!s.Schedule.Started) return "load test: waiting for the deep captures to measure";
            var st = s.Schedule.Current(Now);
            return "load test: " + (st != null ? st.Name + string.Format(CultureInfo.InvariantCulture, ", {0:0} s in", s.Schedule.SecondsIntoStage(Now)) : "ending")
                + (s.Schedule.AbortReason != null ? " (stopping early: " + s.Schedule.AbortReason + ")" : "");
        }

        private Session NewSession(bool server, string mode, bool force, bool deep, int maxPlayers = ConsoleHelp.PlayersDefault)
        {
            bool players = mode == "players";
            var plan = players ? LoadPlan.ForPlayers(maxPlayers) : LoadPlan.For(mode);
            var s = new Session { Server = server, Mode = plan.Directions, Force = force, Deep = deep, Plan = plan, Schedule = new LoadSchedule(plan) };
            s.Players = players;
            s.MaxPlayers = Math.Max(1, Math.Min(LoadPlan.MaxPlayers, maxPlayers));
            s.Stamp = DateTime.Now.ToString("HHmmss");
            s.LastFrame = Now;
            return s;
        }

        // ------------------------------------------------------------------ messages

        private void EnsureRegistered()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || rpc == _registeredOn) return;
            if (_registeredOn != null && _s != null) Finish("the connection ended", false);
            _registeredOn = rpc;
            rpc.Register<string>(RpcRequest, (sender, line) => Safe(() => OnRequest(sender, line)));
            rpc.Register<string>(RpcReply, (sender, line) => Safe(() => OnReply(line)));
            rpc.Register<string>(RpcPing, (sender, line) => Safe(() => OnPing(sender, line)));
            rpc.Register<string>(RpcPong, (sender, line) => Safe(() => OnPong(line)));
            rpc.Register<string>(RpcStop, (sender, line) => Safe(() => OnStop(sender, line)));
        }

        private void Send(Session s, string rpc, IEnumerable<KeyValuePair<string, object>> values)
        {
            string line = KeyValues.Encode(values);
            if (s.Server) ZRoutedRpc.instance?.InvokeRoutedRPC(s.Peer, rpc, line);
            else ZRoutedRpc.instance?.InvokeRoutedRPC(rpc, line);
        }

        private void OnRequest(long sender, string line)
        {
            if (!_server) return;
            var d = KeyValues.Decode(line);
            var znet = ZNet.instance;
            var peer = znet?.GetPeer(sender);
            if (peer == null || ZDOMan.instance == null) return;
            int others = znet.GetPeers().Count(p => p != null && p.m_uid != sender);
            bool force = KeyValues.Number(d, "force") == 1, deep = KeyValues.Number(d, "deep") == 1;
            var pos = peer.m_refPos;
            string who = string.IsNullOrEmpty(peer.m_playerName) ? "peer " + sender : peer.m_playerName;
            string refusal = !IsAdmin(peer) ? "only an admin of this server (adminlist.txt) can start one"
                : LoadAdmission.Refusal(others - Bots.Live, force, _s != null, new Vector2(pos.x, pos.z).magnitude);
            d.TryGetValue("mode", out string mode);
            Session s = null;
            if (refusal == null)
            {
                s = NewSession(true, mode, force, deep, (int)Num(d, "max", ConsoleHelp.PlayersDefault));
                if (s.Players)
                {
                    d.TryGetValue("pattern", out string pattern);
                    s.Pattern = Patterns.Contains(pattern) ? pattern : "stay";
                    s.ObjectsPerPlayer = (int)Num(d, "objects", ConsoleHelp.ObjectsDefault);
                    s.LinkKbps = Num(d, "link", ConsoleHelp.LinkDefault);
                    s.Bots = new Bots(s.Pattern, s.ObjectsPerPlayer, s.LinkKbps, pos, peer.m_simulationDistance);
                    refusal = s.Bots.Start();
                    if (refusal != null) s.Bots.Stop();
                }
            }
            if (refusal != null)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcReply, KeyValues.Encode(new Dictionary<string, object> { { "ok", 0 }, { "why", refusal } }));
                _log("Vigil: load test from " + who + " refused: " + refusal);
                return;
            }
            s.Peer = sender;
            s.PeerName = who;
            s.Center = pos;
            s.LastHeard = s.WaitStarted = Now;
            if (others > 0) s.Notes.Add(others + " other player" + (others == 1 ? " was" : "s were") + " online; the test was started with force.");
            d.TryGetValue("v", out string theirs);
            if (theirs != _version) s.Notes.Add("The player's Vigil is " + (theirs ?? "unknown") + ", this one " + _version + ".");
            _s = s;
            s.Accepted = true;
            s.Beacon = CreateBeacon(s);
            if (deep) StartDeepHere(s);
            if (!s.DeepHere) s.Schedule.Start(Now);
            OpenTable(s);
            Send(s, RpcReply, new Dictionary<string, object>
            {
                { "ok", 1 }, { "db_u", s.Beacon.m_uid.UserID }, { "db_i", s.Beacon.m_uid.ID }, { "started", s.Schedule.Started ? 1 : 0 },
                { "kind", s.Players ? "players" : "traffic" },
            });
            _log("Vigil: load test started by " + who + " (" + s.Mode + (force ? ", force" : "") + (deep ? ", deep" : "") + ")");
        }

        private void OnReply(string line)
        {
            var s = _s;
            if (s == null || s.Server || s.Accepted) return;
            var d = KeyValues.Decode(line);
            if (KeyValues.Number(d, "ok") != 1)
            {
                d.TryGetValue("why", out string why);
                Abandon("the server refused it: " + (why ?? "no reason given"));
                return;
            }
            d.TryGetValue("kind", out string kind);
            if (s.Players && kind != "players")
            {
                // A server Vigil from before the player simulation runs a traffic test instead.
                try { Send(s, RpcStop, new Dictionary<string, object> { { "why", "the player's game asked for a player simulation" } }); } catch (Exception) { }
                Abandon("the server's Vigil is older than " + _version + " and has no player simulation; update it first");
                return;
            }
            s.Accepted = true;
            s.LastHeard = s.WaitStarted = Now;
            s.RemoteBeacon = BeaconId(d, "db");
            s.Center = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : s.StartPos;
            s.Beacon = CreateBeacon(s);
            if (s.Deep) StartDeepHere(s);
            if (KeyValues.Number(d, "started") == 1) s.Schedule.Start(Now);
            OpenTable(s);
            Message("Vigil: load test running (" + s.Mode + "); stand still until it reports back");
        }

        private void OnPing(long sender, string line)
        {
            var s = _s;
            if (s == null || !s.Server || sender != s.Peer) return;
            double now = Now;
            var d = KeyValues.Decode(line);
            s.LastHeard = now;
            s.Remote = d;
            if (s.RemoteBeacon == ZDOID.None) s.RemoteBeacon = BeaconId(d, "ub");
            s.DeepReady = KeyValues.Number(d, "deep_ready") == 1;
            double held = KeyValues.Number(d, "down_seq");
            double age = held > 0 ? s.Clock.Age((int)held, now) : double.NaN;
            if (!double.IsNaN(age)) s.Stale.Add(age);
            if (d.TryGetValue("abort", out string abort) && !string.IsNullOrEmpty(abort) && s.Schedule.AbortReason == null) s.Schedule.Abort(abort, now);
            var reply = new Dictionary<string, object>(s.Mine);
            d.TryGetValue("t", out string echo);
            reply["t"] = echo;
            reply["up_seq"] = s.RemoteSeqHeld;
            reply["started"] = s.Schedule.Started ? 1 : 0;
            if (s.Schedule.AbortReason != null) reply["abort"] = s.Schedule.AbortReason;
            Send(s, RpcPong, reply);
        }

        private void OnPong(string line)
        {
            var s = _s;
            if (s == null || s.Server || !s.Accepted) return;
            double now = Now;
            var d = KeyValues.Decode(line);
            s.LastHeard = now;
            s.Remote = d;
            double t = KeyValues.Number(d, "t");
            if (!double.IsNaN(t)) s.Rtt.Add((now - t) * 1000);
            double held = KeyValues.Number(d, "up_seq");
            double age = held > 0 ? s.Clock.Age((int)held, now) : double.NaN;
            if (!double.IsNaN(age)) s.Stale.Add(age);
            if (KeyValues.Number(d, "started") == 1) s.Schedule.Start(now);
            if (d.TryGetValue("abort", out string abort) && !string.IsNullOrEmpty(abort) && s.Schedule.AbortReason == null) s.Schedule.Abort(abort, now);
        }

        private void OnStop(long sender, string line)
        {
            var s = _s;
            if (s == null || (s.Server && sender != s.Peer)) return;
            KeyValues.Decode(line).TryGetValue("why", out string why);
            // The other machine's run ended as planned (or after an abort both sides know of): same ending here.
            if (why == "done" || (why != null && why.StartsWith("stopped early", StringComparison.Ordinal))) Finish(why, tellOther: false);
            else Finish((s.Server ? "the player's game ended it: " : "the server ended it: ") + (why ?? "no reason given"), tellOther: false);
        }

        private static double Num(Dictionary<string, string> d, string key, double fallback)
        {
            double v = KeyValues.Number(d, key);
            return double.IsNaN(v) ? fallback : v;
        }

        private static ZDOID BeaconId(Dictionary<string, string> d, string prefix)
        {
            if (d.TryGetValue(prefix + "_u", out string u) && d.TryGetValue(prefix + "_i", out string i)
                && long.TryParse(u, NumberStyles.Integer, CultureInfo.InvariantCulture, out long uid)
                && uint.TryParse(i, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint id))
                return new ZDOID(uid, id);
            return ZDOID.None;
        }

        // ------------------------------------------------------------------ per frame

        public void Update()
        {
            EnsureRegistered();
            var s = _s;
            if (s == null) return;
            long t0 = Stopwatch.GetTimestamp();
            try { Step(s, Now); }
            catch (Exception e) { Error(e); }
            s.TestTicks += Stopwatch.GetTimestamp() - t0;
        }

        private void Step(Session s, double now)
        {
            if (ZNet.instance == null || ZDOMan.instance == null) { Finish("the world closed", false); return; }
            if (!s.Accepted)
            {
                if (now - s.Requested > ReplyTimeout) Abandon("the server did not answer; it needs Vigil " + _version + " or newer");
                return;
            }
            if (s.Server && ZNet.instance.GetPeer(s.Peer) == null) { Finish("the player left", false); return; }
            if (now - s.LastHeard > SilenceTimeout) { Finish(s.Server ? "the player's game stopped answering" : "the server stopped answering", true); return; }

            if (!s.Server) s.DeepReady = !s.DeepHere || _recorder.DeepRunning;
            if (s.Server && !s.Schedule.Started)
            {
                bool mine = !s.DeepHere || _recorder.DeepRunning;
                bool late = now - s.WaitStarted > DeepWaitLimit;
                if ((mine && s.DeepReady) || late)
                {
                    if (!(mine && s.DeepReady)) s.Notes.Add("The deep captures were not both measuring after 180 s; the test started anyway.");
                    s.Schedule.Start(now);
                }
            }

            var stage = s.Schedule.Current(now);
            // Both machines run the same schedule; the player's game tells the server when its own run is over.
            if (s.Schedule.Started && stage == null) { Finish(s.Schedule.AbortReason == null ? "done" : "stopped early: " + s.Schedule.AbortReason, tellOther: !s.Server); return; }
            _recorder.LoadBusy = true;
            _recorder.LoadNote = stage != null ? "load test: " + stage.Name : "load test: waiting for the deep captures";

            if (now - s.LastBeacon >= BeaconEvery && s.Beacon != null)
            {
                s.LastBeacon = now;
                if (Ours(s.Beacon, s.BeaconId)) s.Beacon.Set(s_seqHash, s.Clock.Next(now));
                else { s.Beacon = null; s.Notes.Add("The game removed this machine's beacon object during the test; its staleness stops there."); }
            }
            if (s.RemoteBeacon != ZDOID.None)
            {
                var copy = ZDOMan.instance.GetZDO(s.RemoteBeacon);
                if (copy != null) s.RemoteSeqHeld = copy.GetInt(s_seqHash);
            }

            double dt = Math.Min(0.5, Math.Max(0, now - s.LastFrame));
            s.LastFrame = now;
            if (s.Bots != null)
            {
                s.Bots.SetTarget(stage != null && stage.Kind == LoadPlan.Kind.Load ? stage.Players : 0);
                s.Bots.Step(now, dt);
            }
            string mineDir = s.Server ? "down" : "up";
            if (stage != null && stage.Kind == LoadPlan.Kind.Load && stage.Direction == mineDir)
            {
                EnsureObjects(s, LoadPlan.ObjectsFor(stage.RateKbps, Payload));
                s.Budget += stage.RateKbps * 1024 * dt;
                // Each object changes at most once a frame: only its latest state is ever sent.
                int left = s.Objects.Count;
                while (s.Budget >= Payload && left-- > 0 && s.Objects.Count > 0)
                {
                    int i = s.Next % s.Objects.Count;
                    var o = s.Objects[i];
                    if (!Ours(o.Zdo, o.Id)) { s.Objects.RemoveAt(i); s.Lost++; continue; }
                    Touch(o);
                    s.Next++;
                    s.Budget -= Payload;
                }
                s.Budget = Math.Min(s.Budget, Payload);
            }
            else s.Budget = 0;

            if (!s.Server && now - s.LastPing >= PingEvery)
            {
                s.LastPing = now;
                var ping = new Dictionary<string, object>(s.Mine)
                {
                    ["t"] = now, ["down_seq"] = s.RemoteSeqHeld, ["deep_ready"] = s.DeepReady ? 1 : 0,
                    ["ub_u"] = s.Beacon?.m_uid.UserID ?? 0, ["ub_i"] = s.Beacon?.m_uid.ID ?? 0,
                };
                if (s.AbortToSend != null) ping["abort"] = s.AbortToSend;
                Send(s, RpcPing, ping);
            }
        }

        private ZDO CreateObject(Session s, Vector3 at)
        {
            var off = Random.insideUnitCircle * (float)SpreadMeters;
            var zdo = ZDOMan.instance.CreateNewZDO(at + new Vector3(off.x, 0.5f, off.y), 0);
            zdo.Set(s_markHash, s.Server ? MarkDown : MarkUp);
            s.Owned.Add(zdo.m_uid);
            return zdo;
        }

        // A world object the game removes goes back to a pool and can return as another object, so
        // the test touches an object only while it still carries the id it was made with: it never
        // writes into, or removes, an object that is not its own.
        private static bool Ours(ZDO zdo, ZDOID id) => zdo != null && id != ZDOID.None && zdo.m_uid == id && zdo.IsValid();

        private ZDO CreateBeacon(Session s)
        {
            var zdo = CreateObject(s, s.Center);
            s.BeaconId = zdo.m_uid;
            return zdo;
        }

        private void EnsureObjects(Session s, int count)
        {
            while (s.Objects.Count < count)
            {
                var zdo = CreateObject(s, s.Center);
                var o = new TestObject { Zdo = zdo, Id = zdo.m_uid, A = new byte[Payload], B = new byte[Payload] };
                var rng = new System.Random(s.Objects.Count);
                rng.NextBytes(o.A);
                rng.NextBytes(o.B);
                o.Zdo.Set(s_dataHash, o.A);
                // The game takes a byte array as changed only when it is a different array, so the
                // next change uses the other buffer.
                o.UseB = true;
                s.Objects.Add(o);
            }
        }

        private static void Touch(TestObject o)
        {
            var buf = o.UseB ? o.B : o.A;
            o.UseB = !o.UseB;
            o.Count++;
            buf[0] = (byte)o.Count; buf[1] = (byte)(o.Count >> 8); buf[2] = (byte)(o.Count >> 16); buf[3] = (byte)(o.Count >> 24);
            o.Zdo.Set(s_dataHash, buf);
        }

        private void StartDeepHere(Session s)
        {
            string r = _recorder.StartDeep(DeepSeconds, "for the load test");
            if (_recorder.DeepActive) s.DeepHere = true;
            else s.Notes.Add("No deep capture on this machine: " + r + ".");
        }

        // ------------------------------------------------------------------ counting (Harmony hooks)

        /// <summary>A world object was written into a send to another machine (ZDOMan.SendZDOs).</summary>
        public static void CountSent(ZDO zdo, int bytes)
        {
            var s = s_current?._s;
            if (s != null && zdo != null && s.Owned.Contains(zdo.m_uid)) s.SentBytes += bytes;
        }

        /// <summary>A world object arrived from another machine (ZDO.Deserialize inside ZDOMan.RPC_ZDOData).</summary>
        public static void CountReceived(ZDO zdo, ZPackage pkg)
        {
            var s = s_current?._s;
            if (s == null || zdo == null || pkg == null || zdo.GetPrefab() != 0) return;
            if (zdo.GetInt(s_markHash) == (s.Server ? MarkUp : MarkDown)) s.RecvBytes += pkg.Size();
        }

        // ------------------------------------------------------------------ per second

        private void OnSlots(int frames)
        {
            var s = _s;
            if (s == null || !s.Accepted) return;
            // The work per frame: the timers that nothing else timed runs around, added up.
            double top = 0;
            foreach (var slot in _timer.Slots)
                if (slot.SecondCalls > 0 && slot.Parent == null) top += PhaseTimer.TicksToMs(slot.SecondTicks);
            s.WorkMs = top / Math.Max(1, frames);
            if (!s.Schedule.Started) return;
            var stage = s.Schedule.Current(Now);
            if (stage == null) return;
            s.Costs.AddFrames(stage.Index, frames);
            foreach (var slot in _timer.Slots)
                if (slot.SecondCalls > 0) s.Costs.Add(stage.Index, slot.Label, PhaseTimer.TicksToMs(slot.SecondTicks), slot.SecondCalls);
        }

        private static double V(TimelineTable.Row row, string column)
        {
            try
            {
                var v = row.Get(column);
                if (v == null) return double.NaN;
                if (v is IConvertible c && !(v is string)) return c.ToDouble(CultureInfo.InvariantCulture);
                return double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN;
            }
            catch (Exception) { return double.NaN; }
        }

        // Steam's own view of the connection toward the other machine, and the settings that bound
        // it: the send-rate floor and the game's world-update batch limit. When Steam's view cannot
        // be read, the report's notes say why, once.
        private static void Link(Session s, Dictionary<string, object> mine, ISocket socket, bool server)
        {
            string problem = null;
            try
            {
                if (SteamLink.TryRead(socket, out var st, out string why))
                {
                    mine[server ? LoadMetrics.ServerOut : LoadMetrics.ClientOut] = st.OutKbs;
                    mine[server ? LoadMetrics.ServerRate : LoadMetrics.ClientRate] = st.RateKbs;
                    mine[server ? LoadMetrics.ServerPending : LoadMetrics.ClientPending] = st.PendingKb;
                    mine[server ? LoadMetrics.ServerUnacked : LoadMetrics.ClientUnacked] = st.UnackedKb;
                    mine[server ? LoadMetrics.ServerPing : LoadMetrics.ClientPing] = st.PingMs;
                }
                else problem = why;
            }
            catch (Exception e) { problem = e.GetType().Name + " " + e.Message; }
            if (problem != null)
            {
                string note = "Steam's view of the connection could not be read on " + (server ? "the server" : "the player's game") + ": " + problem + ".";
                if (!s.Notes.Contains(note)) s.Notes.Add(note);
            }
            try
            {
                SteamLink.Rates(out double floor, out double _);
                mine[server ? LoadMetrics.ServerFloor : LoadMetrics.ClientFloor] = floor;
            }
            catch (Exception) { }
            try { mine[server ? LoadMetrics.ServerBatch : LoadMetrics.ClientBatch] = SteamLink.BatchKb(); } catch (Exception) { }
        }

        private static double ToDouble(object v) =>
            v is double d ? d : v is IConvertible c && !(v is string) ? c.ToDouble(CultureInfo.InvariantCulture) : double.NaN;

        private static double MaxKnown(double a, double b) => double.IsNaN(a) ? b : double.IsNaN(b) ? a : Math.Max(a, b);

        private void OnSecond(TimelineTable.Row row, float span, int frames)
        {
            var s = _s;
            if (s == null || !s.Accepted) return;
            try { Second(s, row, Math.Max(span, 0.001f), frames); }
            catch (Exception e) { Error(e); }
        }

        private void Second(Session s, TimelineTable.Row row, float span, int frames)
        {
            double now = Now;
            var mine = new Dictionary<string, object>();
            string p = s.Server ? "srv_" : "cli_";
            mine[p + "fps"] = V(row, "fps");
            mine[p + "frame_ms"] = V(row, "dt_avg_ms");
            mine[p + "frame_max_ms"] = V(row, "dt_max_ms");
            mine[p + "over_50"] = V(row, "over_50");
            mine[p + "over_100"] = V(row, "over_100");
            mine[p + "cpu"] = V(row, "proc_cpu");
            mine[p + "zdoman_ms"] = V(row, "ph_zdoman");
            mine[p + "zdo_apply_ms"] = V(row, "ph_zdo_apply");
            mine[p + "gc"] = V(row, "gc");
            mine[p + "test_ms"] = PhaseTimer.TicksToMs(s.TestTicks) / Math.Max(1, frames);
            mine[p + "work_ms"] = s.WorkMs;
            s.TestTicks = 0;
            if (s.Server)
            {
                mine[LoadMetrics.ServerSyncList] = V(row, "ph_zdo_synclist");
                mine[LoadMetrics.ServerRpc] = V(row, "ph_rpc_handle");
                mine[LoadMetrics.ServerZones] = V(row, "ph_zones");
                mine[LoadMetrics.ServerHeap] = V(row, "heap_mb");
                mine[LoadMetrics.ServerMemory] = V(row, "ws_mb");
                mine[LoadMetrics.ServerZdos] = V(row, "zdos");
                mine[LoadMetrics.ServerLoad] = V(row, "load_1m");
                mine[LoadMetrics.ServerSend] = V(row, "ph_zdo_send");
                mine[LoadMetrics.ServerRelease] = V(row, "ph_zdo_release");
                mine[LoadMetrics.ServerRoute] = V(row, "ph_route");
                mine[LoadMetrics.ServerLogErrors] = V(row, "log_err");
                mine[LoadMetrics.ServerPeers] = ZNet.instance?.GetPeers().Count ?? 0;
                var peer = ZNet.instance?.GetPeer(s.Peer);
                if (peer?.m_socket != null)
                {
                    try { mine[LoadMetrics.ServerQueue] = peer.m_socket.GetSendQueueSize() / 1024.0; } catch (Exception) { }
                    Link(s, mine, peer.m_socket, server: true);
                }
            }
            else
            {
                mine[LoadMetrics.ClientScene] = V(row, "ph_scene");
                mine[LoadMetrics.ClientQueue] = V(row, "send_queue_kb");
                var sp = ZNet.instance?.GetServerPeer();
                if (sp?.m_socket != null) Link(s, mine, sp.m_socket, server: false);
                mine[LoadMetrics.ClientFocused] = V(row, "focused");
                mine[LoadMetrics.ClientFrameCap] = V(row, "target_fps");
                mine[LoadMetrics.ClientMachineCpu] = V(row, "sys_cpu");
                mine[LoadMetrics.ClientMemory] = V(row, "mem_load");
                if (Player.m_localPlayer != null)
                {
                    double moved = Vector3.Distance(Player.m_localPlayer.transform.position, s.StartPos);
                    mine[LoadMetrics.ClientMoved] = moved;
                    s.MaxMoved = Math.Max(s.MaxMoved, moved);
                }
            }

            var stage = s.Schedule.Started ? s.Schedule.Current(now) : null;
            string dir = s.Server ? "down" : "up", other = s.Server ? "up" : "down";
            mine[dir + "_target_kbs"] = stage != null && stage.Kind == LoadPlan.Kind.Load && stage.Direction == dir ? stage.RateKbps : 0;
            mine[dir + "_sent_kbs"] = s.SentBytes / 1024.0 / span;
            mine[other + "_recv_kbs"] = s.RecvBytes / 1024.0 / span;
            mine[dir + "_stale_s"] = s.Stale.Count > 0 ? s.Stale.Max() : double.NaN;
            if (!s.Server) mine[LoadMetrics.Rtt] = s.Rtt.Count > 0 ? s.Rtt.Average() : double.NaN;
            s.SentBytes = 0; s.RecvBytes = 0; s.Stale.Clear(); s.Rtt.Clear();
            var arrivals = new List<double>();
            if (s.Bots != null) foreach (var kv in s.Bots.Second(now, span, arrivals)) mine[kv.Key] = kv.Value;
            s.Mine = mine;
            if (stage == null) return;

            int sec = (int)Math.Floor(s.Schedule.SecondsIntoStage(now));
            var all = new Dictionary<string, double>();
            foreach (var kv in mine) all[kv.Key] = ToDouble(kv.Value);
            foreach (var key in LoadMetrics.All) if (!all.ContainsKey(key) && key != LoadMetrics.BotArrival) all[key] = KeyValues.Number(s.Remote, key);
            foreach (var kv in all) s.Ledger.Add(stage.Index, sec, kv.Key, kv.Value);
            // One value per simulated player whose area finished arriving this second.
            foreach (var a in arrivals) s.Ledger.Add(stage.Index, sec, LoadMetrics.BotArrival, a);

            if (s.Tsv != null)
            {
                var r = s.Table.NewRow();
                r.Set("time", DateTime.Now.ToString("HH:mm:ss"));
                r.Set("stage", stage.Name);
                r.Set("second", sec);
                foreach (var key in LoadMetrics.All) if (all.TryGetValue(key, out double v) && !double.IsNaN(v)) r.Set(key, v);
                try { s.Tsv.WriteLine(s.Table.Format(r)); } catch (Exception) { }
            }

            double worst = V(row, "dt_max_ms");
            if (!double.IsNaN(worst) && (!s.WorstMs.TryGetValue(stage.Index, out double was) || worst > was))
            {
                s.WorstMs[stage.Index] = worst;
                s.Worst[stage.Index] = string.Format(CultureInfo.InvariantCulture, "{0} {1:0} ms frame; busiest that second: {2} ({3:0.0} ms/frame), top mod {4}",
                    DateTime.Now.ToString("HH:mm:ss"), worst, row.Get("top_method") ?? "-", V(row, "top_method_ms"), row.Get("top_mod") ?? "-");
            }

            string why = s.Guard.Check(all[LoadMetrics.ServerFrame], MaxKnown(all[LoadMetrics.UpStale], all[LoadMetrics.DownStale]),
                all[LoadMetrics.ClientFps], all[LoadMetrics.Rtt]);
            if (why != null && s.Schedule.AbortReason == null)
            {
                s.Schedule.Abort(why, now);
                s.AbortToSend = why;
            }
        }

        // ------------------------------------------------------------------ end

        private void OpenTable(Session s)
        {
            s.Table = new TimelineTable(new[] { "time", "stage", "second" }.Concat(LoadMetrics.All));
            try
            {
                s.Tsv = new StreamWriter(new FileStream(Path.Combine(_dir, "Vigil-load-" + s.Stamp + ".tsv"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                s.Tsv.WriteLine(s.Table.Header());
            }
            catch (Exception) { s.Tsv = null; }
        }

        /// <summary>The game is closing: ends a running test and writes its report.</summary>
        public void Shutdown()
        {
            if (_s != null) Finish("the game closed", tellOther: true);
        }

        private void Abandon(string why)
        {
            _s = null;
            _recorder.LoadBusy = false;
            _recorder.LoadNote = null;
            Message("Vigil: load test not started: " + why);
        }

        private void Finish(string reason, bool tellOther)
        {
            var s = _s;
            if (s == null) return;
            _s = null;
            _recorder.LoadBusy = false;
            _recorder.LoadNote = null;
            if (tellOther)
            {
                try { Send(s, RpcStop, new Dictionary<string, object> { { "why", reason } }); } catch (Exception) { }
            }
            if (s.Bots != null)
            {
                try { s.Bots.Stop(); } catch (Exception e) { _log("Vigil: simulated players not all removed: " + e.GetType().Name + " " + e.Message); }
                s.Notes.AddRange(s.Bots.Notes);
            }
            try
            {
                if (ZDOMan.instance != null)
                {
                    foreach (var o in s.Objects) if (Ours(o.Zdo, o.Id)) ZDOMan.instance.DestroyZDO(o.Zdo);
                    if (Ours(s.Beacon, s.BeaconId)) ZDOMan.instance.DestroyZDO(s.Beacon);
                }
                if (s.Lost > 0) s.Notes.Add(s.Lost + " test objects were removed by the game during the test and left alone from then on.");
            }
            catch (Exception) { }
            if (s.DeepHere) { try { _recorder.FinishDeepNow(); } catch (Exception) { } }
            string file = "";
            if (s.Accepted)
            {
                try { file = WriteReport(s, reason); } catch (Exception e) { _log("Vigil: load test report not written: " + e.GetType().Name + " " + e.Message); }
            }
            try { s.Tsv?.Dispose(); } catch (Exception) { }
            string what = reason == "done" ? "finished" : "ended (" + reason + ")";
            Message("Vigil: load test " + what + (file.Length > 0 ? ", report " + file : ""));
        }

        private string WriteReport(Session s, string reason)
        {
            var ci = CultureInfo.InvariantCulture;
            var plan = s.Plan;
            var sb = new StringBuilder();
            sb.AppendFormat("Vigil {0}   {1}   {2}\r\n", s.Players ? "player simulation" : "load test", s.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                s.Server ? "written by the server, started by " + s.PeerName : "written by the player's game");
            sb.AppendFormat("{0}{1}. Deep capture: {2}. Ended: {3}.\r\n",
                s.Players ? string.Format(ci, "Up to {0} simulated players, {1}, {2} changing objects each, {3:0} kB/s links", s.MaxPlayers, s.Pattern, s.ObjectsPerPlayer, s.LinkKbps)
                    : "Directions: " + s.Mode,
                s.Force ? " (started with force)" : "",
                s.Deep ? (s.DeepHere ? "on (Vigil-deep-*.txt on this machine covers the whole test)" : "asked for, not available here") : "off", reason);
            if (s.Players)
            {
                sb.AppendLine("Each simulated player is a connection the server treats as a player: it is in the player list, gets the world updates for the area");
                sb.AppendLine("around it and every routed message through a link of the rate above, and sends the server its character " + Bots.CharacterHz.ToString("0", ci)
                    + " times a second and its objects " + Bots.ObjectHz.ToString("0", ci) + " times a second each, through the same code that takes in real players' updates.");
                sb.AppendLine("Patterns: stay = around you; spread = one per busy place (the zones holding the most objects, your bases first); travel = running between");
                sb.AppendLine("busy places and around you; teleport = one per busy place, jumping to another every " + Bots.TeleportSeconds.ToString("0", ci) + " s.");
                sb.AppendLine("Not simulated: the work a real player's game makes the server do on joining (the mods' join messages), and everything that runs on the");
                sb.AppendLine("players' own machines (creatures, physics). The server made no terrain for them and they took over no real objects.");
                sb.AppendFormat(ci, "Steps: baseline {0:0} s, then the player count doubles every {1:0} s up to the maximum, then {2:0} s without them. Values per step leave out its first {3} s.\r\n",
                    LoadPlan.BaselineSeconds, LoadPlan.PlayerStepSeconds, LoadPlan.RecoverySeconds, LoadVerdict.SettleSeconds);
                sb.AppendLine("\"stale\" is how old your copy of a beacon object next to you was: how far behind your world updates ran.");
            }
            else
            {
                sb.AppendLine("Traffic: temporary world objects without a type, " + Payload + " bytes of data each, each changed " + LoadPlan.ObjectHz.ToString("0", ci)
                    + " times a second; more objects each step. No game builds them, the world save never holds them.");
                sb.AppendLine("A beacon object among them carries a number that goes up ten times a second; how old the other machine's copy of it is (\"stale\")");
                sb.AppendLine("shows how far behind the world updates are; it includes the reply that reported it, so a calm connection shows about half the round trip.");
                sb.AppendFormat(ci, "Steps: baseline {0:0} s, then {1} kB/s for {2:0} s each, then {3:0} s without load, per direction. Values per step leave out its first {4} s.\r\n",
                    LoadPlan.BaselineSeconds, string.Join("/", LoadPlan.Rates.Select(r => r.ToString("0", ci)).ToArray()), LoadPlan.StepSeconds, LoadPlan.RecoverySeconds, LoadVerdict.SettleSeconds);
            }
            sb.AppendLine("The other machine's numbers arrive with its messages, up to a second late at each step change.");
            sb.AppendLine();
            sb.AppendLine("== Verdict ==");
            var verdict = s.Players ? LoadVerdict.JudgePlayers(plan, s.Ledger, s.Schedule.AbortReason, s.Pattern) : LoadVerdict.Judge(plan, s.Ledger, s.Schedule.AbortReason);
            foreach (var l in verdict) sb.AppendLine(l);
            sb.AppendLine();
            if (s.Players)
            {
                sb.AppendLine("== Simulated players per step (players; world data sent to them all, kB/s; queue per player, kB; objects each knows; their updates per second) ==");
                Table(sb, plan, s.Ledger, LoadMetrics.Bots, LoadMetrics.BotDown, LoadMetrics.BotQueue, LoadMetrics.BotKnown, LoadMetrics.BotUpdates);
                sb.AppendLine("== Your world updates per step (stale in s; round trip in ms; data from the server, kB/s) ==");
                Table(sb, plan, s.Ledger, LoadMetrics.DownStale, LoadMetrics.Rtt, LoadMetrics.ClientQueue);
            }
            else
            {
                sb.AppendLine("== Traffic per step (kB/s; stale in s; round trip in ms) ==");
                Table(sb, plan, s.Ledger, LoadMetrics.UpTarget, LoadMetrics.UpSent, LoadMetrics.UpRecv, LoadMetrics.UpStale, LoadMetrics.Rtt);
                Table(sb, plan, s.Ledger, LoadMetrics.DownTarget, LoadMetrics.DownSent, LoadMetrics.DownRecv, LoadMetrics.DownStale, LoadMetrics.Rtt);
            }
            sb.AppendLine();
            sb.AppendLine("== Server per step (frame times in ms, work and phases in ms per frame, over_50/100 = frames per second that long) ==");
            Table(sb, plan, s.Ledger, LoadMetrics.ServerFps, LoadMetrics.ServerFrame, LoadMetrics.ServerFrameMax, LoadMetrics.ServerOver50, LoadMetrics.ServerOver100, LoadMetrics.ServerCpu);
            Table(sb, plan, s.Ledger, LoadMetrics.ServerWork, LoadMetrics.ServerZdoMan, LoadMetrics.ServerZdoApply, LoadMetrics.ServerSyncList, LoadMetrics.ServerSend, LoadMetrics.ServerRelease);
            Table(sb, plan, s.Ledger, LoadMetrics.ServerRoute, LoadMetrics.ServerRpc, LoadMetrics.ServerZones, LoadMetrics.ServerTestCost, LoadMetrics.ServerLogErrors);
            Table(sb, plan, s.Ledger, LoadMetrics.ServerGc, LoadMetrics.ServerHeap, LoadMetrics.ServerMemory, LoadMetrics.ServerZdos, LoadMetrics.ServerPeers, LoadMetrics.ServerLoad);
            sb.AppendLine();
            sb.AppendLine("== The player's game per step (focused 1 = in front; frame cap in fps; sys_cpu and mem_load are the whole computer's, %) ==");
            Table(sb, plan, s.Ledger, LoadMetrics.ClientFps, LoadMetrics.ClientFrame, LoadMetrics.ClientFrameMax, LoadMetrics.ClientOver50, LoadMetrics.ClientOver100, LoadMetrics.ClientCpu);
            Table(sb, plan, s.Ledger, LoadMetrics.ClientWork, LoadMetrics.ClientZdoMan, LoadMetrics.ClientZdoApply, LoadMetrics.ClientScene, LoadMetrics.ClientTestCost, LoadMetrics.ClientGc);
            Table(sb, plan, s.Ledger, LoadMetrics.ClientFocused, LoadMetrics.ClientFrameCap, LoadMetrics.ClientMachineCpu, LoadMetrics.ClientMemory, LoadMetrics.ClientMoved);
            sb.AppendLine();
            sb.AppendLine("== The connection per step, from Steam on each side toward the other ==");
            sb.AppendLine("   queue = waiting or sent and not yet confirmed, as the game counts it (kB); pending = waiting in Steam (kB); unacked = sent, not yet confirmed (kB);");
            sb.AppendLine("   out = what Steam put on the wire (kB/s); rate = the send rate Steam allowed itself (kB/s); floor = its configured minimum (kB/s);");
            sb.AppendLine("   ping = Steam's own (ms, the line without the queue); batch = the game's world-update batch limit (kB). The round trip in the traffic table includes the queue.");
            Table(sb, plan, s.Ledger, LoadMetrics.ServerQueue, LoadMetrics.ServerPending, LoadMetrics.ServerUnacked, LoadMetrics.ServerOut, LoadMetrics.ServerRate, LoadMetrics.ServerPing);
            Table(sb, plan, s.Ledger, LoadMetrics.ServerFloor, LoadMetrics.ServerBatch);
            Table(sb, plan, s.Ledger, LoadMetrics.ClientQueue, LoadMetrics.ClientPending, LoadMetrics.ClientUnacked, LoadMetrics.ClientOut, LoadMetrics.ClientRate, LoadMetrics.ClientPing);
            Table(sb, plan, s.Ledger, LoadMetrics.ClientFloor, LoadMetrics.ClientBatch);

            var baseline = plan.Stages.First(st => st.Kind == LoadPlan.Kind.Baseline);
            string here = s.Server ? "the server" : "the player's game";
            sb.AppendLine();
            sb.AppendLine("== On " + here + ": what grew the most under load (ms per frame at baseline -> at the step, calls per frame) ==");
            foreach (var dirName in new[] { "up", "down", "players" })
            {
                var heaviest = plan.Stages.LastOrDefault(st => st.Kind == LoadPlan.Kind.Load && st.Direction == dirName && s.Costs.Top(st.Index, 1).Count > 0);
                if (heaviest == null) continue;
                sb.AppendLine("   " + heaviest.Name + ":");
                foreach (var c in s.Costs.Growth(baseline.Index, heaviest.Index, 15))
                    sb.AppendFormat(ci, "      {0,8:0.000} -> {1,8:0.000}  ({2:+0.000;-0.000;0.000})  {3,9:0.0} calls  {4}\r\n",
                        c.BaselineMsPerFrame, c.MsPerFrame, c.MsPerFrame - c.BaselineMsPerFrame, c.CallsPerFrame, c.Label);
            }
            sb.AppendLine();
            sb.AppendLine("== On " + here + ": costliest timed methods per step (ms per frame, calls per frame) ==");
            foreach (var st in plan.Stages)
            {
                var top = s.Costs.Top(st.Index, 8);
                if (top.Count == 0) continue;
                sb.AppendLine("   " + st.Name + ":");
                foreach (var c in top) sb.AppendFormat(ci, "      {0,8:0.000}  {1,9:0.0} calls  {2}\r\n", c.MsPerFrame, c.CallsPerFrame, c.Label);
            }
            sb.AppendLine();
            sb.AppendLine("== On " + here + ": longest frame per step ==");
            foreach (var st in plan.Stages) if (s.Worst.TryGetValue(st.Index, out string w)) sb.AppendLine("   " + st.Name.PadRight(16) + " " + w);

            if (!s.Server && s.MaxMoved > MovedWarnMeters)
                s.Notes.Add(string.Format(ci, "You moved up to {0:0} m from where the test started. The server's objects stay where it put them, so it stops sending them once you are out of range: the down numbers may be low for that reason.", s.MaxMoved));
            sb.AppendLine();
            sb.AppendLine("== Notes ==");
            foreach (var n in s.Notes) sb.AppendLine("   " + n);
            sb.AppendLine("   Per second: Vigil-load-" + s.Stamp + ".tsv. The other machine writes its own report with the same numbers and its own method times.");
            if (s.Server) sb.AppendLine("   Hitch and network incidents are not written while a test runs; this report covers that time.");

            string file = "Vigil-load-" + s.Stamp + ".txt";
            File.WriteAllText(Path.Combine(_dir, file), sb.ToString());
            if (ResultStore.Shared.Shown)
            try
            {
                var result = s.Players ? LoadResult.BuildPlayers(plan, s.Ledger, s.Schedule.AbortReason, s.Pattern, DateTime.Now) : LoadResult.Build(plan, s.Ledger, s.Schedule.AbortReason, DateTime.Now);
                result.File = file;
                result.Path = Path.Combine(_dir, file);
                ResultStore.Shared.Put(ResultStore.Load, result);
            }
            catch (Exception e) { _log("Vigil: load test result not shown in the panel: " + e.GetType().Name + " " + e.Message); }
            return file;
        }

        private static void Table(StringBuilder sb, LoadPlan plan, LoadLedger ledger, params string[] metrics)
        {
            foreach (var line in LoadVerdict.Table(plan, ledger, metrics)) sb.AppendLine(line);
        }

        // ------------------------------------------------------------------ helpers

        private void Message(string text)
        {
            _log(text);
            if (_server) return;
            try { MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, text); } catch (Exception) { }
        }

        private void Safe(Action a)
        {
            try { a(); } catch (Exception e) { Error(e); }
        }

        private void Error(Exception e)
        {
            if (_errors++ < 3) _log("Vigil: load test error: " + e);
        }
    }
}
