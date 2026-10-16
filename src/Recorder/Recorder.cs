using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Vigil.Core;
using UnityEngine;

namespace Vigil.Recorder
{
    /// <summary>
    /// The flight recorder. Every frame: length, garbage collections, physics steps, drawing time
    /// and the timed work that took longest, kept for the last few thousand frames. Every second:
    /// one row in Vigil-timeline.tsv with frame, engine, game-phase, mod, network, object,
    /// log, process and context numbers (and on a server one row per player in
    /// Vigil-peers.tsv). When something goes wrong (a long frame, a slow stretch, the server
    /// going quiet, ping or send queue piling up, a log flood, memory pressure) or the "lag now"
    /// key is pressed, a detailed Vigil-incident-*.txt is written with the minutes leading up
    /// to it. A slow stretch can also start a short deep capture of every mod patch.
    /// </summary>
    internal sealed class Recorder : IDisposable
    {
        internal sealed class Settings
        {
            public float HitchMs = 250, SlowFps = 20, SlowSeconds = 5, SilentMs = 1500, PingMs = 400,
                QueueKb = 64, LogFloodPerSec = 200, MemoryLoadPct = 95, FaultsPerSec = 60000;
            public bool AutoDeep = true;
            public float AutoDeepCooldownMinutes = 15, DeepSeconds = 20, DeepBudgetMs = 4;
            public int MaxIncidents = 60;
            public float IncidentCooldownSeconds = 20;
        }

        private const int FrameCapacity = 4096;
        private const int TopPerFrame = 20;
        private const int RowCapacity = 300;

        private readonly string _dir;
        private readonly Settings _s;
        private readonly InstrumentJournal _journal;
        private readonly PhaseTimer _timer;
        private readonly NetMonitor _net;
        private readonly LogTap _log;
        private readonly SystemStats _sys;
        private readonly EngineStats _engine;   // null on a dedicated server
        private readonly WorldContext _world;
        private readonly RpcProbe _rpc;
        private readonly Func<float, string> _uploads;
        private readonly bool _server;
        private readonly IncidentPolicy _policy;
        // Lasting states (memory, send queue, ping, log flood) are reported when they begin.
        private readonly ConditionGate _gate = new ConditionGate(60);
        // A server is not judged for its first minute: loading the world stalls and floods by design.
        private readonly Settling _serverStart = new Settling(60);
        private bool _serverSeen;
        private readonly TimelineTable _table;
        private readonly TimelineTable _peerTable;
        private readonly StreamWriter _timeline;
        private readonly StreamWriter _peers;
        private readonly RingBuffer<string> _rows = new RingBuffer<string>(RowCapacity);
        private readonly DateTime _started = DateTime.Now;
        private readonly float _startedAt = Time.realtimeSinceStartup;

        // frame history (structure of arrays, no allocation per frame)
        private readonly float[] _fTime = new float[FrameCapacity], _fDt = new float[FrameCapacity], _fRender = new float[FrameCapacity];
        // Time Vigil itself spent in each frame writing reports (main thread, outside every timer).
        private readonly float[] _fOwn = new float[FrameCapacity];
        private float _ownMs;

        // Time Vigil's tools spent on the game's thread, and the frame they spent it in.
        private float _toolMs;
        private int _toolFrame;

        /// <summary>
        /// Time a tool of Vigil's took on the game's thread (the census, the memory list, the patch
        /// map), from the console or the panel: like a report's own writing time, the frame it
        /// lengthened is not a hitch for it.
        /// </summary>
        public void AddOwnTime(double ms)
        {
            if (_toolFrame != Time.frameCount) { _ownMs += _toolMs; _toolMs = 0; _toolFrame = Time.frameCount; }
            _toolMs += (float)ms;
        }
        private readonly int[] _fGc = new int[FrameCapacity], _fFixed = new int[FrameCapacity];
        private readonly byte[] _fSit = new byte[FrameCapacity];
        private readonly int[] _fTopId = new int[FrameCapacity * TopPerFrame];
        private readonly float[] _fTopMs = new float[FrameCapacity * TopPerFrame];
        private readonly int[] _fTopCalls = new int[FrameCapacity * TopPerFrame];
        private long _frameNo;
        private int _topCount;
        private int _curTopBase;

        // per-second accumulators
        private float _secStart;
        private int _secFrames, _secOver33, _secOver50, _secOver100, _secOver250, _secGc, _secFixed, _secFixedMax;
        private float _secDtSum, _secDtMax;

        /// <summary>Each recorded second's longest frame, for an incident's strip in the panel (the second under way is added to it there).</summary>
        private readonly RingBuffer<double> _secondWorst = new RingBuffer<double>(90);
        private int _fixedThisFrame;
        private int _lastGc;
        private string _note;
        private int _slowSeconds;
        private long _probeTicks, _probeRunTicks;
        private long _frameWorkTicks;
        private readonly Dictionary<string, long> _secModTicks = new Dictionary<string, long>();
        private readonly Dictionary<string, long> _secModAlloc = new Dictionary<string, long>();

        // session
        private readonly FrameStats _framesWorld = new FrameStats(), _framesAll = new FrameStats(), _ping = new FrameStats();
        private double _maxQueueKb, _maxGapMs, _maxWorkingSet, _maxPrivate, _maxFaults, _maxMemLoad;
        private string _maxGapWhen = "";
        private float _inWorldSeconds;
        private float _deepUntil = -1, _lastAutoDeep = -1e9f;
        private string _deepReason;
        private int _deepFramesStart;
        private bool _deepBlocked;
        private readonly List<string> _deepReports = new List<string>();

        // ---- load test (LoadTest): it reads each finished second and the timers' second, and while
        // it runs its own slowdown is not reported as incidents (its report covers that time).
        /// <summary>Each finished timeline row, with the second's length and frame count.</summary>
        public Action<TimelineTable.Row, float, int> SecondRow;

        /// <summary>The last finished second's timeline row, for the panel and the HUD line; null before the first.</summary>
        public TimelineTable.Row Last { get; private set; }

        /// <summary>When <see cref="Last"/> was taken, in seconds since the game started.</summary>
        public float LastAt { get; private set; } = float.NegativeInfinity;

        /// <summary>Incident reports written on their own or by hand this session.</summary>
        public int IncidentsWritten => _policy.Written;

        /// <summary>On a dedicated server: the last finished second's row per connected player.</summary>
        public List<TimelineTable.Row> LastPeers { get; private set; } = new List<TimelineTable.Row>();

        private List<TimelineTable.Row> _peersThisSecond = new List<TimelineTable.Row>();

        /// <summary>The incidents written this session, oldest first, at most <see cref="RecentKept"/>: "time kind: detail (file)".</summary>
        public List<string> RecentIncidents { get; } = new List<string>();

        public const int RecentKept = 20;
        /// <summary>Just before the timers' per-second values reset, with the second's frame count.</summary>
        public Action<int> SlotsSecond;
        public bool LoadBusy;
        /// <summary>An incident report is on disk; called from the thread that wrote it.</summary>
        public Action<string> ReportWritten;
        /// <summary>An incident happened (kind, detail, report file or null when its limits kept it from being written, the player a drop is about); called on the game's thread.</summary>
        public Action<string, string, string, string> IncidentSeen;
        public string LoadNote;
        public bool DeepActive => _timer.DeepActive;
        public bool DeepRunning => _timer.DeepRunning;
        /// <summary>Ends a running deep capture now and writes its report.</summary>
        public void FinishDeepNow() { if (_timer.DeepRunning) FinishDeep(); }
        private bool Busy(float now) => DeepBusy(now) || LoadBusy;

        public Recorder(string dir, Settings settings, InstrumentJournal journal, PhaseTimer timer, NetMonitor net, LogTap log,
            SystemStats sys, EngineStats engine, WorldContext world, RpcProbe rpc, Func<float, string> uploads, bool server, bool deepBlocked, Action<string> warn)
        {
            _dir = dir; _s = settings; _journal = journal; _timer = timer; _net = net; _log = log; _sys = sys; _engine = engine;
            _world = world; _rpc = rpc; _uploads = uploads; _server = server; _deepBlocked = deepBlocked; _warn = warn;
            _policy = new IncidentPolicy(_s.IncidentCooldownSeconds, _s.MaxIncidents);
            _table = new TimelineTable(Columns());
            _timeline = Open("Vigil-timeline.tsv");
            Try(() => _timeline?.WriteLine(_table.Header()));
            if (_server)
            {
                _peerTable = new TimelineTable(new[] { "time", "peer", "ping_ms", "quality_local", "quality_remote", "sock_out_kbs", "sock_in_kbs",
                    "send_queue_kb", "steam_rate_kbs", "pending_kb", "unacked_kb", "queue_wait_ms", "app_in_kb", "app_out_kb", "pkts_in", "pkts_out", "max_gap_ms", "zdos_known", "x", "z" });
                _peers = Open("Vigil-peers.tsv");
                Try(() => _peers?.WriteLine(_peerTable.Header()));
            }
            _secStart = Time.realtimeSinceStartup;
            _lastGc = GC.CollectionCount(0);
        }

        private StreamWriter Open(string name)
        {
            try { return new StreamWriter(new FileStream(Path.Combine(_dir, name), FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true }; }
            catch (Exception) { return null; }
        }

        private IEnumerable<string> Columns()
        {
            var c = new List<string> { "time", "t", "situation", "fps", "frames", "dt_avg_ms", "dt_max_ms", "over_33", "over_50", "over_100", "over_250",
                "gc", "heap_mb", "fixed_steps", "fixed_max", "render_ms", "cameras", "cpu_main_ms", "cpu_render_ms", "gpu_ms" };
            if (_engine != null) c.AddRange(_engine.Counters.Select(x => x.Column));
            else c.AddRange(new[] { "alloc_kb_s", "allocs_s", "gc_used_mb", "gc_reserved_mb", "unity_used_mb", "gfx_used_mb", "draw_calls", "batches", "setpass", "tris_k", "shadow_casters", "main_thread_ms" });
            c.AddRange(PhaseTimer.GamePhases.Select(p => p[2]));
            c.AddRange(new[] { "mods_ms", "top_mod", "top_mod_ms", "top_method", "top_method_ms", "coroutines_ms", "top_coroutine", "top_coroutine_ms", "probe_ms",
                "alloc_top", "alloc_top_kb", "exceptions", "top_exception", "handler_top", "handler_top_ms", "file_writes", "top_file",
                "ping_ms", "quality_local", "quality_remote", "sock_out_kbs", "sock_in_kbs", "send_queue_kb", "steam_rate_kbs", "pending_kb", "unacked_kb", "queue_wait_ms", "max_gap_ms", "gap_from",
                "pkts_in", "pkts_out", "app_in_kb", "app_out_kb", "zdo_down_kb", "zdos_down", "zdos_sent", "zdos_recv", "rpc_out", "rpc_in", "top_rpc_out", "top_rpc_in", "net_top_out", "net_top_out_kb", "net_top_in", "net_top_in_kb",
                "zdos", "instances", "created", "destroyed", "characters", "ai", "items", "pieces", "ships", "heightmaps", "zones", "peers",
                "log_lines", "log_warn", "log_err", "top_log", "top_log_n",
                "proc_cpu", "sys_cpu", "ws_mb", "private_mb", "faults_s", "mem_load", "avail_mb", "threads", "disk_free_gb", "load_1m",
                "x", "y", "z", "biome", "interior", "on_ship", "speed", "env", "fog", "day", "event", "paused", "focused", "target_fps", "vsync", "gfx", "ui", "incident", "note" });
            return c;
        }

        // ------------------------------------------------------------------ per frame

        public void OnFixedUpdate() { if (ProbeState.Active) _fixedThisFrame++; }

        private bool _paused;
        private float _pausedAt;
        public bool Paused => _paused;

        /// <summary>Called first in each Update: closes the frame before.</summary>
        public void OnUpdate()
        {
            float nowWall = Time.realtimeSinceStartup;
            // Not recording: nothing per frame, nothing per second. On resuming, whatever was half
            // measured when it stopped is dropped and the next second starts clean.
            if (!ProbeState.Active)
            {
                if (!_paused) { _paused = true; _pausedAt = nowWall; }
                return;
            }
            if (_paused)
            {
                _paused = false;
                _timer.DiscardPending();
                ResetSecond(nowWall);
                _lastGc = GC.CollectionCount(0);
                _fixedThisFrame = 0;
                _slowSeconds = 0;
                try { _engine?.EndSecond(); } catch (Exception) { }
                _note = string.Format(CultureInfo.InvariantCulture, "recording resumed after {0:0} s off", nowWall - _pausedAt);
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            float now = nowWall;
            float dtMs = Time.unscaledDeltaTime * 1000f;
            int gcNow = GC.CollectionCount(0);
            int gc = gcNow - _lastGc; _lastGc = gcNow;
            int fixedSteps = _fixedThisFrame; _fixedThisFrame = 0;
            byte sit = SituationCode();

            if (_engine != null) _engine.EndFrame();
            int idx = (int)(_frameNo % FrameCapacity);
            _curTopBase = idx * TopPerFrame;
            _topCount = 0;
            for (int i = 0; i < TopPerFrame; i++) { _fTopId[_curTopBase + i] = -1; _fTopMs[_curTopBase + i] = 0; _fTopCalls[_curTopBase + i] = 0; }
            _timer.EndFrame(Sink);
            _fTime[idx] = now; _fDt[idx] = dtMs; _fGc[idx] = gc; _fFixed[idx] = fixedSteps; _fSit[idx] = sit;
            _fRender[idx] = _engine != null ? _engine.LastRenderMs : 0f;
            // A tool run earlier in this frame stalls the next one, so its time waits for that frame.
            if (_toolFrame < Time.frameCount) { _ownMs += _toolMs; _toolMs = 0; }
            float ownMs = _ownMs;
            _fOwn[idx] = ownMs;
            _ownMs = 0;
            _frameNo++;

            _secFrames++; _secDtSum += dtMs; if (dtMs > _secDtMax) _secDtMax = dtMs;
            if (dtMs > 33) _secOver33++;
            if (dtMs > 50) _secOver50++;
            if (dtMs > 100) _secOver100++;
            if (dtMs > 250) _secOver250++;
            _secGc += gc; _secFixed += fixedSteps; if (fixedSteps > _secFixedMax) _secFixedMax = fixedSteps;
            _framesAll.Add(dtMs);
            if (sit == 4 && !_serverSeen) { _serverSeen = true; _serverStart.Started("server", now); }
            // A starting server stalls and floods while it loads the world; that is not reported.
            bool live = sit == 0 || (sit == 4 && _serverStart.Settled("server", now));
            if (live) { _framesWorld.Add(dtMs); _inWorldSeconds += dtMs / 1000f; }

            // A frame that is long only because Vigil wrote a report in it is not a hitch.
            if (live && dtMs - ownMs >= _s.HitchMs && !Busy(now))
                Incident("hitch", string.Format(CultureInfo.InvariantCulture, "a {0:0} ms frame (limit {1:0} ms)", dtMs, _s.HitchMs), idx);

            StepDeep(now);
            if (now - _secStart >= 1f)
            {
                try { EndSecond(now); }
                finally { ResetSecond(now); }
            }
            long probe = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            _probeTicks += probe;
            _probeRunTicks += probe;
            _frameWorkTicks += probe;
        }

        /// <summary>The recorder's own frame work over the session (probe_ms summed), for Vigil's own cost.</summary>
        public long ProbeRunTicks => _probeRunTicks;

        /// <summary>Frames over the session, and their mean length.</summary>
        public long FramesAll => _framesAll.Count;

        public double MeanFrameMs => _framesAll.MeanMs;

        /// <summary>When this session started, and how long ago in minutes.</summary>
        public DateTime Started => _started;

        public double MinutesRunning => Math.Max(1.0 / 60, (Time.realtimeSinceStartup - _startedAt) / 60.0);

        /// <summary>The ticks of this per-frame work (probe_ms) since the last call, which Vigil's own row in the per-mod timing leaves out.</summary>
        public long TakeFrameWorkTicks()
        {
            long t = _frameWorkTicks;
            _frameWorkTicks = 0;
            return t;
        }

        private void Sink(PhaseTimer.Slot slot, long ticks)
        {
            float ms = (float)PhaseTimer.TicksToMs(ticks);
            if (slot.Mod != null && slot.Parent == null)
            {
                _secModTicks.TryGetValue(slot.Mod, out long t);
                _secModTicks[slot.Mod] = t + ticks;
            }
            // keep the TopPerFrame largest, smallest last
            int n = _topCount;
            if (n == TopPerFrame && ms <= _fTopMs[_curTopBase + n - 1]) return;
            int pos = n < TopPerFrame ? n : TopPerFrame - 1;
            while (pos > 0 && _fTopMs[_curTopBase + pos - 1] < ms)
            {
                _fTopMs[_curTopBase + pos] = _fTopMs[_curTopBase + pos - 1];
                _fTopId[_curTopBase + pos] = _fTopId[_curTopBase + pos - 1];
                _fTopCalls[_curTopBase + pos] = _fTopCalls[_curTopBase + pos - 1];
                pos--;
            }
            _fTopMs[_curTopBase + pos] = ms; _fTopId[_curTopBase + pos] = slot.Id; _fTopCalls[_curTopBase + pos] = slot.FrameCalls;
            if (_topCount < TopPerFrame) _topCount++;
        }

        private static byte SituationCode()
        {
            switch (WorldContext.Situation())
            {
                case "in world": return 0;
                case "teleporting": return 1;
                case "loading": return 2;
                case "server": return 4;
                default: return 3;
            }
        }

        private static string SituationName(byte b) =>
            b == 0 ? "in world" : b == 1 ? "teleporting" : b == 2 ? "loading" : b == 4 ? "server" : "menu";

        // ------------------------------------------------------------------ per second

        private void EndSecond(float now)
        {
            float span = now - _secStart;
            var ci = CultureInfo.InvariantCulture;
            var row = _table.NewRow();
            void Set(string col, object v) { try { row.Set(col, v is double d && double.IsNaN(d) ? null : v); } catch (ArgumentException) { } }

            Set("time", DateTime.Now.ToString("HH:mm:ss"));
            Set("t", Math.Round(now - _startedAt, 1));
            string situation = WorldContext.Situation();
            Set("situation", situation);
            Set("fps", _secFrames / Math.Max(span, 0.001f));
            Set("frames", _secFrames);
            Set("dt_avg_ms", _secFrames > 0 ? _secDtSum / _secFrames : 0f);
            Set("dt_max_ms", _secDtMax);
            Set("over_33", _secOver33); Set("over_50", _secOver50); Set("over_100", _secOver100); Set("over_250", _secOver250);
            Set("gc", _secGc);
            Set("heap_mb", GC.GetTotalMemory(false) / 1048576.0);
            Set("fixed_steps", _secFixed); Set("fixed_max", _secFixedMax);

            if (_engine != null)
            {
                Set("render_ms", _engine.SecondFrames > 0 ? _engine.SecondRenderMs / _engine.SecondFrames : 0);
                Set("cameras", _engine.LastCameras);
                if (_engine.SecondTimingFrames > 0)
                {
                    Set("cpu_main_ms", _engine.SecondMainMs / _engine.SecondTimingFrames);
                    Set("cpu_render_ms", _engine.SecondRenderThreadMs / _engine.SecondTimingFrames);
                    Set("gpu_ms", _engine.SecondGpuMs / _engine.SecondTimingFrames);
                }
                foreach (var c in _engine.Counters) Set(c.Column, _engine.SecondValue(c));
                _engine.EndSecond();
            }

            // game phases: ms per frame this second
            double modsMs = 0; string topMethod = ""; double topMethodMs = 0;
            double coMs = 0, topCoMs = 0; string topCo = "";
            _secModAlloc.Clear();
            foreach (var slot in _timer.Slots)
            {
                if (slot.SecondCalls == 0) continue;
                if (slot.Mod != null && slot.Parent == null && slot.SecondAlloc > 0)
                {
                    _secModAlloc.TryGetValue(slot.Mod, out long b);
                    _secModAlloc[slot.Mod] = b + slot.SecondAlloc;
                }
                double ms = PhaseTimer.TicksToMs(slot.SecondTicks);
                if (slot.Column != null) Set(slot.Column, ms / Math.Max(1, _secFrames));
                if (slot.Mod != null && slot.Parent == null) modsMs += ms;
                if (slot.Coroutine) { coMs += ms; if (ms > topCoMs) { topCoMs = ms; topCo = slot.Label; } }
                if (slot.Parent == null && ms > topMethodMs) { topMethodMs = ms; topMethod = slot.Label; }
            }
            Set("mods_ms", modsMs / Math.Max(1, _secFrames));
            Set("coroutines_ms", coMs / Math.Max(1, _secFrames)); Set("top_coroutine", topCo); Set("top_coroutine_ms", topCoMs / Math.Max(1, _secFrames));
            if (_secModTicks.Count > 0)
            {
                var top = _secModTicks.OrderByDescending(k => k.Value).First();
                Set("top_mod", top.Key); Set("top_mod_ms", PhaseTimer.TicksToMs(top.Value) / Math.Max(1, _secFrames));
            }
            Set("top_method", topMethod); Set("top_method_ms", topMethodMs / Math.Max(1, _secFrames));
            Set("probe_ms", PhaseTimer.TicksToMs(_probeTicks) / Math.Max(1, _secFrames));
            _probeTicks = 0;
            _secModTicks.Clear();
            if (_secModAlloc.Count > 0)
            {
                var top = _secModAlloc.OrderByDescending(k => k.Value).First();
                Set("alloc_top", top.Key); Set("alloc_top_kb", top.Value / 1024.0);
            }
            try { SlotsSecond?.Invoke(_secFrames); } catch (Exception) { }
            _timer.EndSecond();
            ExceptionWatch.EndSecond(out string topExc, out long excCount);
            Set("exceptions", excCount); Set("top_exception", topExc);
            HandlerTime.EndSecond(out string topHandler, out double handlerMs);
            Set("handler_top", topHandler); Set("handler_top_ms", handlerMs);
            FileChurn.EndSecond(out string topFile, out long fileWrites);
            Set("file_writes", fileWrites); Set("top_file", topFile);
            CreationCost.EndSecond();

            NetworkColumns(Set, now);
            _rpc.EndSecond(out int rpcOut, out string topOut, out int rpcIn, out string topIn);
            Set("rpc_out", rpcOut); Set("rpc_in", rpcIn); Set("top_rpc_out", topOut); Set("top_rpc_in", topIn);
            NetAttribution.EndSecond(out string netOut, out double netOutKb, out string netIn, out double netInKb);
            Set("net_top_out", netOut); Set("net_top_out_kb", netOutKb); Set("net_top_in", netIn); Set("net_top_in_kb", netInKb);

            Set("created", _world.TakeCreated()); Set("destroyed", _world.TakeDestroyed());
            _world.Fill(Set);

            _log.EndSecond(out int lines, out int warn, out int err, out string topLog, out int topLogN);
            Set("log_lines", lines); Set("log_warn", warn); Set("log_err", err); Set("top_log", topLog); Set("top_log_n", topLogN);

            _sys.Sample();
            Set("proc_cpu", _sys.ProcessCpuPct); Set("sys_cpu", _sys.SystemCpuPct); Set("ws_mb", _sys.WorkingSetMb);
            Set("private_mb", _sys.PrivateMb); Set("faults_s", _sys.PageFaultsPerSec); Set("mem_load", _sys.MemoryLoadPct);
            Set("avail_mb", _sys.AvailableMb); Set("threads", _sys.Threads);
            Set("disk_free_gb", _sys.DiskFreeGb); Set("load_1m", _sys.LoadAverage);
            Max(ref _maxWorkingSet, _sys.WorkingSetMb); Max(ref _maxPrivate, _sys.PrivateMb); Max(ref _maxFaults, _sys.PageFaultsPerSec); Max(ref _maxMemLoad, _sys.MemoryLoadPct);

            string deepNote = _timer.DeepRunning ? "deep capture measuring" : _timer.DeepActive ? "deep capture busy with its timers" : null;
            string notes = string.Join("; ", new[] { _note, deepNote, LoadNote }.Where(n => n != null).ToArray());
            if (notes.Length > 0) Set("note", notes);
            _note = null;

            // checks that need a whole second
            bool live = situation == "in world" || (situation == "server" && _serverStart.Settled("server", now));
            float fps = _secFrames / Math.Max(span, 0.001f);
            bool limited = Application.targetFrameRate > 0 && Application.targetFrameRate <= _s.SlowFps + 1;
            bool paused = false; try { paused = Game.IsPaused(); } catch (Exception) { }
            // A deep capture slows the game by design; that is not a slow stretch to report.
            if (live && !_server && fps < _s.SlowFps && Application.isFocused && !paused && !limited && !Busy(now)) _slowSeconds++; else _slowSeconds = 0;
            string incident = null;
            if (_slowSeconds >= _s.SlowSeconds)
            {
                incident = "slow";
                Incident("slow", string.Format(ci, "under {0:0} fps for {1} seconds in a row (now {2:0.0} fps)", _s.SlowFps, _slowSeconds, fps), -1);
                MaybeAutoDeep("a slow stretch (" + fps.ToString("0.0", ci) + " fps)");
                _slowSeconds = 0;
            }
            double t = now - _startedAt;
            bool flood = live && lines >= _s.LogFloodPerSec;
            if (flood) incident = "log-flood";
            if (_gate.Update("log-flood", flood, t))
                Incident("log-flood", lines + " log lines in one second, most repeated: " + topLog + " x" + topLogN, -1);
            bool memory = _sys.MemoryLoadPct >= _s.MemoryLoadPct || _sys.PageFaultsPerSec >= _s.FaultsPerSec;
            if (memory) incident = "memory";
            if (_gate.Update("memory", memory, t))
                Incident("memory", string.Format(ci, "machine memory {0:0}% full ({1:0} MB free){2}; reported once until it has eased for a minute",
                    _sys.MemoryLoadPct, _sys.AvailableMb, double.IsNaN(_sys.PageFaultsPerSec) ? "" : string.Format(ci, ", {0:0} page faults/s", _sys.PageFaultsPerSec)), -1);
            if (_lastNetIncident != null) { incident = _lastNetIncident; _lastNetIncident = null; }
            if (_lastFrameIncident != null) { incident = incident == null ? _lastFrameIncident : incident + "+" + _lastFrameIncident; _lastFrameIncident = null; }
            Set("incident", incident);

            string line = _table.Format(row);
            _rows.Add(line);
            Try(() => _timeline?.WriteLine(line));
            _secondWorst.Add(_secDtMax);
            Last = row;
            if (!_server && ZNet.instance != null && ZNet.instance.GetServerPeer() is ZNetPeer serverPeer) DropWatch.Add(serverPeer.m_uid, row, now);
            LastAt = now;
            try { SecondRow?.Invoke(row, span, _secFrames); } catch (Exception) { }

        }

        private string _lastNetIncident, _lastFrameIncident;

        private void ResetSecond(float now)
        {
            _secStart = now;
            try { _net.EndSecond(); } catch (Exception) { }
            _secFrames = _secOver33 = _secOver50 = _secOver100 = _secOver250 = _secGc = _secFixed = _secFixedMax = 0;
            _secDtSum = _secDtMax = 0;
        }

        private void NetworkColumns(Action<string, object> set, float now)
        {
            var ci = CultureInfo.InvariantCulture;
            var znet = ZNet.instance;
            double gap = _net.MaxGapMs(out string gapFrom);
            set("max_gap_ms", gap); set("gap_from", gapFrom);
            if (gap > _maxGapMs) { _maxGapMs = gap; _maxGapWhen = DateTime.Now.ToString("HH:mm:ss") + " from " + gapFrom; }

            long pin = 0, pout = 0, bin = 0, bout = 0;
            foreach (var kv in _net.Connections) { pin += kv.Value.PacketsIn; pout += kv.Value.PacketsOut; bin += kv.Value.BytesIn; bout += kv.Value.BytesOut; }
            set("pkts_in", pin); set("pkts_out", pout); set("app_in_kb", bin / 1024.0); set("app_out_kb", bout / 1024.0);
            set("zdo_down_kb", _net.ZdoDownBytes / 1024.0); set("zdos_down", _net.ZdoDownCount);
            if (ZDOMan.instance != null) { set("zdos_sent", ZDOMan.instance.GetSentZDOs()); set("zdos_recv", ZDOMan.instance.GetRecvZDOs()); }
            if (znet == null) return;

            bool live = (WorldContext.Situation() == "in world" || (_server && _serverStart.Settled("server", now))) && !LoadBusy;
            double settledGap = _net.MaxGapMs(out string settledGapFrom, settledOnly: true);
            double settledQueueKb = 0; string settledQueuePeer = "";
            double worstQueueKb = 0;
            string worstQueuePeer = "";
            _knownZdos = _server ? KnownZdos() : null;
            foreach (var peer in znet.GetPeers())
            {
                if (peer?.m_socket == null) continue;
                double q = peer.m_socket.GetSendQueueSize() / 1024.0;
                if (q > worstQueueKb) { worstQueueKb = q; worstQueuePeer = PeerName(peer); }
                if (_net.Settled(peer.m_rpc) && q > settledQueueKb) { settledQueueKb = q; settledQueuePeer = PeerName(peer); }
                if (_server) PeerRow(peer);
            }
            if (_server) { LastPeers = _peersThisSecond; _peersThisSecond = new List<TimelineTable.Row>(); }
            set("send_queue_kb", worstQueueKb);
            if (worstQueueKb > _maxQueueKb) _maxQueueKb = worstQueueKb;

            if (!_server)
            {
                try
                {
                    znet.GetNetStats(out float localQ, out float remoteQ, out int ping, out float outB, out float inB);
                    set("ping_ms", ping); set("quality_local", localQ); set("quality_remote", remoteQ);
                    set("sock_out_kbs", outB / 1024.0); set("sock_in_kbs", inB / 1024.0);
                    if (ping > 0) _ping.Add(ping);
                    bool high = live && ping >= _s.PingMs;
                    if (high) _lastNetIncident = "ping";
                    if (_gate.Update("ping", high, now - _startedAt))
                        Incident("ping", "ping " + ping + " ms (limit " + _s.PingMs.ToString("0", ci) + " ms)", -1);
                }
                catch (Exception) { }
                var sp = znet.GetServerPeer();
                if (sp?.m_socket != null)
                    PeerPart("Steam connection status", () =>
                    {
                        if (!SteamLink.TryRead(sp.m_socket, out var st, out string why))
                        {
                            if (_peerPartFailed.Add("Steam connection status: " + why))
                                Try(() => _warn?.Invoke("Vigil: Steam's connection status not read for the server: " + why));
                            return;
                        }
                        set("steam_rate_kbs", st.RateKbs); set("pending_kb", st.PendingKb); set("unacked_kb", st.UnackedKb); set("queue_wait_ms", st.WaitMs);
                    });
            }
            bool queued = live && settledQueueKb >= _s.QueueKb;
            if (queued) _lastNetIncident = "send-queue";
            if (_gate.Update("send-queue", queued, now - _startedAt))
                Incident("send-queue", string.Format(ci, "{0:0.0} kB waiting to be sent to {1} (limit {2:0} kB, above the world-update batch the game keeps queued on purpose); reported once until it has eased for a minute",
                    settledQueueKb, settledQueuePeer, _s.QueueKb), -1);
            if (live && settledGap >= _s.SilentMs && znet.GetPeers().Count > 0)
            {
                _lastNetIncident = "silent";
                Incident("silent", string.Format(ci, "nothing received from {0} for {1:0} ms (limit {2:0} ms)", settledGapFrom, settledGap, _s.SilentMs), -1);
            }
        }

        private readonly Action<string> _warn;
        private Dictionary<ZNetPeer, int> _knownZdos;
        private static readonly System.Reflection.FieldInfo s_zdoPeers = HarmonyLib.AccessTools.Field(typeof(ZDOMan), "m_peers");

        /// <summary>How many world objects the server keeps in sync with each player (ZDOMan's per-peer table).</summary>
        private static Dictionary<ZNetPeer, int> KnownZdos()
        {
            var d = new Dictionary<ZNetPeer, int>();
            try
            {
                if (ZDOMan.instance == null || !(s_zdoPeers?.GetValue(ZDOMan.instance) is System.Collections.IList peers)) return d;
                foreach (var zp in peers)
                {
                    var t = zp.GetType();
                    if (t.GetField("m_peer")?.GetValue(zp) is ZNetPeer np && t.GetField("m_zdos")?.GetValue(zp) is System.Collections.ICollection zdos)
                        d[np] = zdos.Count;
                }
            }
            catch (Exception) { }
            return d;
        }

        internal static string PeerName(ZNetPeer p) =>
            p.m_server ? "server" : (string.IsNullOrEmpty(p.m_playerName) ? "peer " + p.m_uid : p.m_playerName);

        private void PeerRow(ZNetPeer peer)
        {
            if (_peers == null) return;
            var row = _peerTable.NewRow();
            row.Set("time", DateTime.Now.ToString("HH:mm:ss"));
            row.Set("peer", PeerName(peer));
            // Each part on its own: a socket type that cannot answer one question must not blank the row.
            int ping = -1;
            // Steam's own numbers first: the game's GetConnectionQuality asks the client interface,
            // which a dedicated server does not have.
            bool steam = false;
            PeerPart("Steam connection status", () =>
            {
                if (!SteamLink.TryRead(peer.m_socket, out var st, out string why))
                {
                    // simulated players have no Steam socket by design
                    if (!(peer.m_socket is BotSocket) && _peerPartFailed.Add("Steam connection status: " + why))
                        Try(() => _warn?.Invoke("Vigil: Steam's connection status not read for " + PeerName(peer) + ": " + why));
                    return;
                }
                steam = true;
                NoteWrappers(peer, st.Wrappers);
                ping = (int)st.PingMs;
                row.Set("ping_ms", st.PingMs); row.Set("quality_local", st.QualityLocal); row.Set("quality_remote", st.QualityRemote);
                row.Set("sock_out_kbs", st.OutKbs); row.Set("sock_in_kbs", st.InKbs);
                row.Set("steam_rate_kbs", st.RateKbs); row.Set("pending_kb", st.PendingKb); row.Set("unacked_kb", st.UnackedKb); row.Set("queue_wait_ms", st.WaitMs);
            });
            if (!steam)
                PeerPart("connection quality", () =>
                {
                    peer.m_socket.GetConnectionQuality(out float lq, out float rq, out ping, out float outB, out float inB);
                    row.Set("ping_ms", ping); row.Set("quality_local", lq); row.Set("quality_remote", rq);
                    row.Set("sock_out_kbs", outB / 1024.0); row.Set("sock_in_kbs", inB / 1024.0);
                });
            PeerPart("send queue", () => row.Set("send_queue_kb", peer.m_socket.GetSendQueueSize() / 1024.0));
            PeerPart("traffic", () =>
            {
                foreach (var kv in _net.Connections)
                    if (kv.Key == peer.m_rpc)
                    {
                        row.Set("app_in_kb", kv.Value.BytesIn / 1024.0); row.Set("app_out_kb", kv.Value.BytesOut / 1024.0);
                        row.Set("pkts_in", kv.Value.PacketsIn); row.Set("pkts_out", kv.Value.PacketsOut); row.Set("max_gap_ms", kv.Value.MaxGapMs);
                    }
            });
            PeerPart("position", () =>
            {
                var pos = peer.GetRefPos();
                row.Set("x", Mathf.RoundToInt(pos.x)); row.Set("z", Mathf.RoundToInt(pos.z));
            });
            PeerPart("objects known", () =>
            {
                if (_knownZdos != null && _knownZdos.TryGetValue(peer, out int known)) row.Set("zdos_known", known);
            });
            PeerPart("ping check", () =>
            {
                if (_gate.Update("ping " + PeerName(peer), ping >= _s.PingMs && _net.Settled(peer.m_rpc) && !LoadBusy, Time.realtimeSinceStartup - _startedAt))
                    Incident("ping", PeerName(peer) + " has a ping of " + ping + " ms", -1);
            });
            _peersThisSecond.Add(row);
            DropWatch.Add(peer.m_uid, row, Time.realtimeSinceStartup);
            Try(() => _peers.WriteLine(_peerTable.Format(row)));
        }

        private readonly HashSet<string> _peerPartFailed = new HashSet<string>();
        private readonly Dictionary<long, int> _wrappersNoted = new Dictionary<long, int>();

        /// <summary>
        /// The event log names the wrappers other mods keep around a player's Steam socket whenever
        /// their number changes: during a join the ServerSync copies take theirs off one by one, and
        /// the last count stays for the session.
        /// </summary>
        private void NoteWrappers(ZNetPeer peer, List<Type> wrappers)
        {
            int n = wrappers?.Count ?? 0;
            if (_wrappersNoted.TryGetValue(peer.m_uid, out int last) ? last == n : n == 0) return;
            _wrappersNoted[peer.m_uid] = n;
            var named = new List<KeyValuePair<string, string>>();
            if (wrappers != null)
                foreach (var t in wrappers) named.Add(new KeyValuePair<string, string>(t.Name, t.Assembly.GetName().Name));
            EventLog.Write("steam-link", PeerName(peer), SocketChain.Describe(named));
        }

        /// <summary>One part of a player row; the first failure of each part is named once in the game's log.</summary>
        private void PeerPart(string part, Action a)
        {
            try { a(); }
            catch (Exception e)
            {
                if (_peerPartFailed.Add(part)) Try(() => _warn?.Invoke("Vigil: per-player " + part + " unavailable: " + e.GetType().Name + " " + e.Message));
            }
        }

        private static void Max(ref double max, double v) { if (!double.IsNaN(v) && v > max) max = v; }

        // ------------------------------------------------------------------ incidents

        // The drop being written, for its report's section and its result in the panel.
        private DropAnalysis _drop;
        private string _dropWho;
        private IReadOnlyList<DropAnalysis.Sample> _dropSamples;

        /// <summary>A connection that ended without a goodbye: an incident report of its own, always written.</summary>
        public void Drop(string who, DropAnalysis drop, IReadOnlyList<DropAnalysis.Sample> samples)
        {
            _drop = drop;
            _dropSamples = samples;
            _dropWho = who;
            try { Incident("drop", drop.Headline, -1, manual: true); }
            finally { _drop = null; _dropSamples = null; _dropWho = null; }
        }

        public void Mark(string note)
        {
            _note = string.IsNullOrEmpty(note) ? "marked" : note;
            Incident("manual", "marked by hand" + (string.IsNullOrEmpty(note) ? "" : ": " + note), -1, manual: true);
        }

        private void Incident(string kind, string detail, int frameIdx, bool manual = false)
        {
            float now = Time.realtimeSinceStartup;
            if (kind == "hitch") _lastFrameIncident = "hitch";
            bool write = _policy.ShouldWrite(kind, now, manual);
            // Told before the report's limits apply: an admin hears of a drop after the session's last report too, only without its file.
            string stamp = DateTime.Now.ToString("HHmmss");
            try { IncidentSeen?.Invoke(kind, detail, write ? "Vigil-incident-" + stamp + "-" + kind + ".txt" : null, _dropWho); } catch (Exception) { }
            if (!write) return;
            // The game waits while this runs, so only the pieces that need the game's own data are
            // taken here, each section timed; joining them (the long timeline rows and log lines)
            // and writing the file happen on another thread. The time spent here is recorded with
            // the frame, so later reports say it was Vigil's and hitch detection leaves it out.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var costs = new List<KeyValuePair<string, double>>();
            List<object> parts;
            try { parts = IncidentParts(kind, detail, frameIdx, costs); }
            catch (Exception e) { parts = new List<object> { "Vigil incident " + kind + ": " + detail + "\r\n(report failed: " + e + ")\r\n" }; }
            string path = Path.Combine(_dir, "Vigil-incident-" + stamp + "-" + kind + ".txt");
            string file = Path.GetFileName(path);
            RecentIncidents.Add(DateTime.Now.ToString("HH:mm:ss") + " " + kind + ": " + detail + " (" + file + ")");
            if (RecentIncidents.Count > RecentKept) RecentIncidents.RemoveAt(0);
            double resultFrom = watch.Elapsed.TotalMilliseconds;
            if (ResultStore.Shared.Shown)
            try
            {
                int worst = frameIdx >= 0 ? frameIdx : WorstFrame(3f);
                var result = _drop != null ? CaptureResult.Drop(DateTime.Now, _drop, _dropSamples) : CaptureResult.Incident(DateTime.Now, kind, detail, worst >= 0 ? Breakdown(worst) : null,
                    _secondWorst.ToArray().Concat(new[] { (double)_secDtMax }).ToList());
                result.File = file;
                result.Path = path;
                ResultStore.Shared.AddIncident(result);
            }
            catch (Exception) { }
            costs.Add(new KeyValuePair<string, double>("panel result", watch.Elapsed.TotalMilliseconds - resultFrom));
            // One line in the game's log too, so a server's journal shows that something happened and where to look.
            double before = watch.Elapsed.TotalMilliseconds;
            try { _warn?.Invoke("Vigil " + kind + ": " + detail + " (" + file + ")"); } catch (Exception) { }
            costs.Add(new KeyValuePair<string, double>("game log line", watch.Elapsed.TotalMilliseconds - before));
            double mainMs = watch.Elapsed.TotalMilliseconds;
            _ownMs += (float)mainMs;
            ThreadPool.QueueUserWorkItem(_ => OwnThreads.Timed(() =>
            {
                try
                {
                    var sb = new StringBuilder(512 * 1024);
                    foreach (var p in parts)
                    {
                        if (p is string s) sb.Append(s);
                        else if (p is string[] lines) foreach (var l in lines) sb.Append(l).Append("\r\n");
                    }
                    sb.Append(IncidentCostLine(mainMs, costs));
                    File.WriteAllText(path, sb.ToString());
                    ReportWritten?.Invoke(path);
                }
                catch (Exception) { }
                EventLog.Write("incident", kind, detail + " -> " + file);
            }));
        }

        private static string IncidentCostLine(double mainMs, List<KeyValuePair<string, double>> costs)
        {
            var ci = CultureInfo.InvariantCulture;
            return string.Format(ci, "\r\n== This report ==\r\nVigil held the game for {0:0.0} ms to take it ({1}); it was put together and written on another thread.\r\n",
                mainMs, string.Join(", ", costs.Where(c => c.Value >= 0.05).OrderByDescending(c => c.Value)
                    .Select(c => c.Key + " " + c.Value.ToString("0.0", ci) + " ms").ToArray()));
        }

        /// <summary>
        /// The report's pieces in order: text, or arrays of lines that the writing thread joins.
        /// Each section's time on the game's thread goes into <paramref name="costs"/>.
        /// </summary>
        private List<object> IncidentParts(string kind, string detail, int frameIdx, List<KeyValuePair<string, double>> costs)
        {
            var ci = CultureInfo.InvariantCulture;
            var parts = new List<object>();
            var lap = System.Diagnostics.Stopwatch.StartNew();
            void Done(string section) { costs.Add(new KeyValuePair<string, double>(section, lap.Elapsed.TotalMilliseconds)); lap.Reset(); lap.Start(); }
            var sb = new StringBuilder();
            sb.AppendFormat("Vigil incident: {0}   {1}   ({2:0} s into the session)\r\n", kind, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Time.realtimeSinceStartup - _startedAt);
            sb.AppendLine("What triggered it: " + detail);
            sb.AppendLine("Situation: " + WorldContext.Situation());
            sb.AppendLine();
            if (_drop != null)
            {
                sb.AppendLine("== How the connection ended ==");
                sb.AppendLine(_drop.Headline + ".");
                foreach (var l in _drop.Lines) sb.AppendLine("   " + l);
                sb.AppendLine("   Its last seconds (t counts back from the end):");
                foreach (var l in DropAnalysis.Table(_dropSamples)) sb.AppendLine("   " + l);
                sb.AppendLine();
            }

            // the worst recent frame, explained
            int worst = frameIdx;
            if (worst < 0) worst = WorstFrame(3f);
            if (worst >= 0)
            {
                var a = HitchAnalysis.Explain(Breakdown(worst));
                sb.AppendLine("== The longest frame in the last 3 seconds ==");
                sb.AppendLine(a.Headline);
                foreach (var c in a.Causes) sb.AppendLine("   - " + c);
                sb.AppendLine("   timed work in that frame (largest first):");
                for (int i = 0; i < TopPerFrame; i++)
                {
                    int id = _fTopId[worst * TopPerFrame + i];
                    if (id < 0) break;
                    var slot = _timer.Slots[id];
                    int calls = _fTopCalls[worst * TopPerFrame + i];
                    sb.AppendFormat(ci, "      {0,8:0.0} ms  {1}{2}{3}\r\n", _fTopMs[worst * TopPerFrame + i], slot.Label,
                        calls > 1 ? "   " + calls + " calls" : "",
                        slot.Parent != null ? "   (inside " + slot.Parent + ")" : "");
                }
                sb.AppendLine();
            }
            Done("the worst frame");

            sb.AppendLine("== Frames over 50 ms in the last 30 seconds ==");
            sb.AppendLine("   seconds ago   ms   gc  physics-steps  drawing-ms  situation  biggest timed work");
            float now = Time.realtimeSinceStartup;
            foreach (int i in RecentFrames(30f))
            {
                if (_fDt[i] < 50) continue;
                int id = _fTopId[i * TopPerFrame];
                sb.AppendFormat(ci, "   {0,8:0.0} {1,7:0} {2,3} {3,6} {4,10:0.0}  {5,-11} {6}{7}\r\n", now - _fTime[i], _fDt[i], _fGc[i], _fFixed[i], _fRender[i],
                    SituationName(_fSit[i]), id >= 0 ? _timer.Slots[id].Label + " " + _fTopMs[i * TopPerFrame].ToString("0.0", ci) + " ms" : "",
                    _fOwn[i] >= 1 ? "   (Vigil's own report or tool: " + _fOwn[i].ToString("0", ci) + " ms)" : "");
            }
            sb.AppendLine();
            Done("frames over 50 ms");

            sb.AppendLine("== The last " + _rows.Count + " seconds, one row per second (paste into a spreadsheet) ==");
            sb.AppendLine(_table.Header());
            parts.Add(sb.ToString());
            sb.Clear();
            parts.Add(_rows.ToArray());
            sb.AppendLine();
            Done("timeline rows");

            sb.AppendLine("== Network messages (routed RPCs) in the current 10 s window ==");
            sb.Append(_rpc.Peek(10f));
            Done("routed messages");
            sb.AppendLine("== Traffic by mod (every package, bytes) in the current 10 s window ==");
            sb.Append(NetAttribution.Peek(10f));
            sb.AppendLine();
            Done("traffic by mod");
            sb.AppendLine("== World objects this game uploaded in the current 10 s window ==");
            sb.Append(_uploads(10f));
            sb.AppendLine();
            Done("uploads");
            sb.AppendLine("== Time handling incoming network messages in the current 10 s window ==");
            sb.Append(HandlerTime.Peek(10f));
            sb.AppendLine("== World objects created in the current 10 s window, time per type ==");
            sb.Append(CreationCost.Peek(10f));
            sb.AppendLine("== Exceptions thrown in the current 10 s window, caught or not ==");
            sb.Append(ExceptionWatch.Works ? ExceptionWatch.Peek(10f) : "   not reported by this runtime\r\n");
            sb.AppendLine("== File writes in the current 10 s window (config, worlds, characters) ==");
            sb.Append(FileChurn.Peek(10f));
            sb.AppendLine();
            Done("handlers, objects, exceptions, files");
            sb.AppendLine("== Connections ==");
            foreach (var kv in _net.Connections)
                sb.AppendFormat(ci, "   {0}: this second {1} packets / {2:0.0} kB in, {3} packets / {4:0.0} kB out, longest silence {5:0} ms; session {6:0.0} MB in, {7:0.0} MB out\r\n",
                    kv.Value.Name, kv.Value.PacketsIn, kv.Value.BytesIn / 1024.0, kv.Value.PacketsOut, kv.Value.BytesOut / 1024.0, kv.Value.MaxGapMs,
                    kv.Value.RunBytesIn / 1048576.0, kv.Value.RunBytesOut / 1048576.0);
            sb.AppendLine();
            Done("connections");

            sb.AppendLine("== Time per mod and per game phase over the session so far (ms per frame) ==");
            sb.Append(TimingTable(20));
            sb.AppendLine();
            Done("time per mod");
            sb.AppendLine("== Log: most repeated messages in the current window ==");
            sb.Append(_log.SpamWindowPeek());
            sb.AppendLine();
            sb.AppendLine("== Log: the last 150 lines ==");
            parts.Add(sb.ToString());
            sb.Clear();
            parts.Add(_log.Recent(150).Select(l => "   " + l).ToArray());
            sb.AppendLine();
            sb.AppendLine("== Incidents so far ==");
            foreach (var kv in _policy.AllSeen) sb.AppendLine("   " + kv.Key + ": " + kv.Value + " seen, " + _policy.Suppressed(kv.Key) + " not written (too soon after the last)");
            parts.Add(sb.ToString());
            Done("log");
            return parts;
        }

        private IEnumerable<int> RecentFrames(float seconds)
        {
            long count = Math.Min(_frameNo, FrameCapacity);
            float now = Time.realtimeSinceStartup;
            for (long n = _frameNo - count; n < _frameNo; n++)
            {
                int i = (int)(n % FrameCapacity);
                if (now - _fTime[i] <= seconds) yield return i;
            }
        }

        private int WorstFrame(float seconds)
        {
            int best = -1; float ms = 0;
            // Ranked without the time Vigil itself spent in a frame: an earlier report is not the longest frame.
            foreach (int i in RecentFrames(seconds)) if (_fDt[i] - _fOwn[i] > ms) { ms = _fDt[i] - _fOwn[i]; best = i; }
            return best;
        }

        private FrameBreakdown Breakdown(int i)
        {
            var f = new FrameBreakdown
            {
                DtMs = _fDt[i], GcCollections = _fGc[i], FixedSteps = _fFixed[i], RenderMs = _fRender[i], OwnMs = _fOwn[i],
                PageFaultsPerSec = double.IsNaN(_sys.PageFaultsPerSec) ? 0 : (float)_sys.PageFaultsPerSec,
                Situation = _fSit[i] == 0 || _fSit[i] == 4 ? null : SituationName(_fSit[i]),
            };
            for (int k = 0; k < TopPerFrame; k++)
            {
                int id = _fTopId[i * TopPerFrame + k];
                if (id < 0) break;
                var slot = _timer.Slots[id];
                var t = new Timed(slot.Label, _fTopMs[i * TopPerFrame + k], slot.Parent, _fTopCalls[i * TopPerFrame + k]);
                if (slot.Mod != null) f.Mods.Add(t); else f.Phases.Add(t);
            }
            return f;
        }

        private string TimingTable(int top)
        {
            var ci = CultureInfo.InvariantCulture;
            long frames = Math.Max(1, _framesAll.Count);
            var sb = new StringBuilder();
            var byMod = _timer.Slots.Where(s => s.Mod != null && s.Parent == null && !s.Deep).GroupBy(s => s.Mod)
                .Select(g => new { g.Key, Ms = PhaseTimer.TicksToMs(g.Sum(s => s.RunTicks)) }).OrderByDescending(x => x.Ms).Take(top);
            sb.AppendLine("   mods (their Update/FixedUpdate/LateUpdate/OnGUI):");
            foreach (var m in byMod) sb.AppendFormat(ci, "      {0,8:0.000} ms/frame  {1}\r\n", m.Ms / frames, m.Key);
            if (PhaseTimer.AllocationsMeasured)
            {
                // Garbage the collector later has to clear: a mod allocating a lot causes collection pauses elsewhere.
                double minutes = Math.Max(1.0 / 60, (Time.realtimeSinceStartup - _startedAt) / 60.0);
                sb.AppendLine("   memory allocated by mods' Unity callbacks and coroutines (MB per minute):");
                foreach (var m in _timer.Slots.Where(s => s.Mod != null && s.Parent == null && s.RunAlloc > 0).GroupBy(s => s.Mod)
                             .Select(g => new { g.Key, Mb = g.Sum(s => s.RunAlloc) / 1048576.0 }).OrderByDescending(x => x.Mb).Take(top))
                    sb.AppendFormat(ci, "      {0,8:0.00} MB/min  {1}\r\n", m.Mb / minutes, m.Key);
            }
            sb.AppendLine("   single methods (game and mods):");
            foreach (var s in _timer.Slots.Where(s => !s.Deep).OrderByDescending(s => s.RunTicks).Take(top))
                sb.AppendFormat(ci, "      {0,8:0.000} ms/frame  {1,9:0.0} calls/frame  {2}{3}\r\n", PhaseTimer.TicksToMs(s.RunTicks) / frames,
                    (double)s.RunCalls / frames, s.Label, s.Parent != null ? "   (inside " + s.Parent + ")" : "");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ deep capture

        private void MaybeAutoDeep(string reason)
        {
            if (!_s.AutoDeep || _deepBlocked || _timer.DeepActive) return;
            if (Time.realtimeSinceStartup - _lastAutoDeep < _s.AutoDeepCooldownMinutes * 60) return;
            _lastAutoDeep = Time.realtimeSinceStartup;
            StartDeep(_s.DeepSeconds, "started on its own after " + reason);
        }

        public string StartDeep(float seconds, string reason)
        {
            if (_timer.DeepActive) return "a deep capture is already running";
            if (_deepBlocked) return "deep capture is off for this session: the last session died while one was running";
            Try(() => File.WriteAllText(Path.Combine(_dir, "Vigil-state", "deep.active"), DateTime.Now.ToString("HH:mm:ss")));
            int n = _timer.BeginDeep();
            _deepSeconds = seconds;
            _deepUntil = -1;
            _deepReason = reason;
            _deepDropDone = false;
            _deepSplit = 0;
            _deepInstallStarted = Time.realtimeSinceStartup;
            return "deep capture: putting timers on " + n + " patched game methods a few per frame; the capture runs " +
                seconds.ToString("0", CultureInfo.InvariantCulture) + " s once they are all in, then Vigil-deep-*.txt is written";
        }

        private float _deepSeconds, _deepInstallStarted, _deepRunStarted, _deepQuietUntil = -1;

        /// <summary>A deep capture is under way or just ended; its own slowdown is not reported as a problem.</summary>
        private bool DeepBusy(float now) => _timer.DeepActive || now < _deepQuietUntil;
        private bool _deepDropDone;
        // Methods split into the game's code and the mods' share: the ones that took the most time in the first 2 s.
        private const int SplitMethods = 25;
        private int _deepSplit;
        private long _deepSplitFrame;

        /// <summary>Each frame while a deep capture is under way: install, measure, drop hot hooks, finish, remove.</summary>
        private void StepDeep(float now)
        {
            if (!_timer.DeepActive) return;
            if (_timer.StepDeep(_s.DeepBudgetMs))
            {
                _deepRunStarted = now;
                _deepUntil = now + _deepSeconds;
                _deepFramesStart = (int)_frameNo;
                Hud(string.Format(CultureInfo.InvariantCulture, "Vigil: deep capture measuring for {0:0} s", _deepSeconds));
            }
            if (_timer.DeepRunning && !_deepDropDone && now - _deepRunStarted >= 2f)
            {
                _deepDropDone = true;
                _timer.DropHotDeepHooks(_frameNo - _deepFramesStart);
                _deepSplit = _timer.SplitTop(SplitMethods);
                _deepSplitFrame = _frameNo;
            }
            if (_timer.DeepRunning && _deepUntil > 0 && now >= _deepUntil) FinishDeep();
            if (!_timer.DeepActive)
            {
                // all hooks are off again
                _deepRunStarted = 0;
                _deepQuietUntil = now + 10f;
                _lastAutoDeep = now; // the automatic cooldown counts from the end of any capture
                Try(() => File.Delete(Path.Combine(_dir, "Vigil-state", "deep.active")));
            }
        }

        private void FinishDeep()
        {
            var ci = CultureInfo.InvariantCulture;
            long frames = Math.Max(1, _frameNo - _deepFramesStart);
            var patched = _timer.Slots.Where(s => s.PatchedBy != null && s.DeepCalls > 0 && !s.Dropped).ToList();
            var sb = new StringBuilder();
            sb.AppendFormat("Vigil deep capture   {0}   {1} frames, {2}\r\n", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), frames, _deepReason);
            sb.AppendLine("Every game method that mods patch was timed whole: the game's own code plus every mod patch on it. Times are per frame, averaged over the capture.");
            sb.AppendLine("A method that is slow here and cheap in vanilla points at the mods listed on it. The timing adds a little to methods called very often.");
            sb.AppendFormat(ci, "Second line, for the {0} methods that took the most time in the first 2 s and the {1} that took {2:0} ms or more in a single frame later:\r\n",
                _deepSplit, _timer.SpikeSplits, DeepSplit.SpikeMs);
            sb.AppendLine("\"game code\" is the stretch between the last prefix and the first postfix (the game's own code, with any transpiler changes in it),");
            sb.AppendLine("\"prefixes/postfixes\" is the rest, what the mods' prefixes and postfixes cost; \"original skipped\" means a mod's prefix stopped the");
            sb.AppendLine("game's code from running. The split is measured from when it went on (for a single-frame stall, from the next time it runs) and shown");
            sb.AppendLine("as a share of the whole time. \"worst frame\" is the method's longest single frame, where it stands out from the average.");
            sb.AppendLine();
            sb.AppendLine("== Patched game methods, most time first ==");
            foreach (var s in patched.OrderByDescending(s => s.DeepTicks).Take(80))
            {
                double whole = PhaseTimer.TicksToMs(s.DeepTicks) / frames;
                string worst = DeepSplit.DescribeWorst(whole, PhaseTimer.TicksToMs(s.DeepMaxFrameTicks));
                sb.AppendFormat(ci, "   {0,8:0.000} ms/frame  {1,10:0.0} calls/frame  {2}   patched by {3}{4}\r\n",
                    whole, (double)s.DeepCalls / frames, s.Label, s.PatchedBy, worst.Length > 0 ? "   (" + worst + ")" : "");
                string split = s.Split ? DeepSplit.DescribeShare(whole, s.SplitWholeTicks, s.OrigTicks, s.SplitCalls, s.OrigRuns) : "";
                if (split.Length > 0) sb.AppendLine("               " + split);
            }
            sb.AppendLine();
            sb.AppendFormat(ci, "== Longest single frames: patched game methods that took {0:0} ms or more in one frame (an action's stall) ==\r\n", DeepSplit.SpikeMs);
            foreach (var s in patched.Where(s => PhaseTimer.TicksToMs(s.DeepMaxFrameTicks) >= DeepSplit.SpikeMs).OrderByDescending(s => s.DeepMaxFrameTicks).Take(40))
            {
                sb.AppendFormat(ci, "   {0,8:0.0} ms  {1}   patched by {2}\r\n", PhaseTimer.TicksToMs(s.DeepMaxFrameTicks), s.Label, s.PatchedBy);
                string share = s.Split ? DeepSplit.DescribeShareOfCalls(s.SplitWholeTicks, s.OrigTicks, s.SplitCalls) : "";
                if (share.Length > 0) sb.AppendLine("               " + share);
            }
            sb.AppendLine();
            var own = PatchTime.ByMod(_timer.PatchTimings(), Math.Max(1, _frameNo - _deepSplitFrame), PhaseTimer.PairMs);
            sb.AppendLine("== By mod: their own patch code (prefixes, postfixes, finalizers) on the split methods, per frame ==");
            sb.AppendLine("   each patch method timed by itself from when the split went on, the timers' own cost per call taken off");
            if (own.Count == 0) sb.AppendLine("   none timed: no mod patches the split methods, or the capture ended before their timers went on");
            foreach (var row in own) sb.AppendLine(PatchTime.Line(row));
            sb.AppendLine("   Transpilers, timed only inside the game's methods they change: " + PatchTime.TranspilerLine(PhaseTimer.TranspilersByMod()));
            sb.AppendLine();
            sb.AppendLine("== By mod: game methods each mod patches, timed whole (a method patched by several mods counts for each) ==");
            var perMod = new Dictionary<string, double>();
            foreach (var s in patched)
                foreach (var m in s.PatchedBy.Split(new[] { ", " }, StringSplitOptions.None))
                    perMod[m] = (perMod.TryGetValue(m, out double v) ? v : 0) + PhaseTimer.TicksToMs(s.DeepTicks);
            foreach (var kv in perMod.OrderByDescending(k => k.Value))
                sb.AppendFormat(ci, "   {0,8:0.000} ms/frame  {1}\r\n", kv.Value / frames, kv.Key);
            sb.AppendLine();
            sb.AppendLine("== Mods' own Update/FixedUpdate/LateUpdate/OnGUI during the capture ==");
            foreach (var s in _timer.Slots.Where(s => s.Mod != null && s.PatchKind == null && s.DeepCalls > 0).OrderByDescending(s => s.DeepTicks).Take(30))
                sb.AppendFormat(ci, "   {0,8:0.000} ms/frame  {1,10:0.0} calls/frame  {2}\r\n", PhaseTimer.TicksToMs(s.DeepTicks) / frames, (double)s.DeepCalls / frames, s.Label);
            if (_timer.DeepDropped.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("== Called too often to time cheaply: timers taken off after 2 s (the timer would have cost more than the method) ==");
                foreach (var d in _timer.DeepDropped) sb.AppendLine("   " + d);
            }
            sb.AppendLine();
            sb.AppendFormat(ci, "Timers were put in over {0:0.0} s, a few per frame, before the capture started.\r\n", _deepRunStarted - _deepInstallStarted);
            _timer.StopDeep();
            _deepUntil = -1;
            string path = Path.Combine(_dir, "Vigil-deep-" + DateTime.Now.ToString("HHmmss") + ".txt");
            _deepReports.Add(Path.GetFileName(path));
            if (ResultStore.Shared.Shown)
            try
            {
                var rows = patched.Select(s => new CaptureResult.DeepRow(s.Label, s.PatchedBy, PhaseTimer.TicksToMs(s.DeepTicks) / frames, (double)s.DeepCalls / frames,
                    PhaseTimer.TicksToMs(s.DeepMaxFrameTicks), s.Split && s.SplitWholeTicks > 0 ? Math.Min(1.0, Math.Max(0.0, (double)s.OrigTicks / s.SplitWholeTicks)) : double.NaN)).ToList();
                var result = CaptureResult.Deep(DateTime.Now, frames, _deepReason, rows);
                result.File = Path.GetFileName(path);
                result.Path = path;
                ResultStore.Shared.Put(ResultStore.Deep, result);
            }
            catch (Exception) { }
            string text = sb.ToString();
            ThreadPool.QueueUserWorkItem(_ => OwnThreads.Timed(() => { try { File.WriteAllText(path, text); } catch (Exception) { } }));
            Hud("Vigil: deep capture finished, report " + Path.GetFileName(path));
        }

        // A line at the top left of a player's screen; a server has none.
        private void Hud(string text)
        {
            if (_server) return;
            try { MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, text); } catch (Exception) { }
        }

        // ------------------------------------------------------------------ summary

        public string Status()
        {
            var ci = CultureInfo.InvariantCulture;
            return string.Format(ci, "Vigil: {0} incidents written, session frames {1}; ping median {2:0} ms; deep capture {3}",
                _policy.Written, _framesWorld.Describe(), _ping.Percentile(50),
                _timer.DeepRunning ? "running" : _timer.DeepActive ? "busy putting timers in or taking them out (" + _timer.DeepPending + " to go)" : "off");
        }

        public string Summary(IEnumerable<string> header)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendFormat("Vigil session summary - started {0}, written {1}, in world {2:0.0} min\r\n",
                _started.ToString("yyyy-MM-dd HH:mm:ss"), DateTime.Now.ToString("HH:mm:ss"), _inWorldSeconds / 60f);
            foreach (var h in header) sb.AppendLine(h);
            sb.AppendLine();
            sb.AppendLine("Frames in world:  " + _framesWorld.Describe());
            sb.AppendLine("Frames overall:   " + _framesAll.Describe());
            sb.AppendLine();
            sb.AppendLine("Incidents (reports are Vigil-incident-*.txt):");
            if (!_policy.AllSeen.Any()) sb.AppendLine("   none");
            foreach (var kv in _policy.AllSeen) sb.AppendLine("   " + kv.Key + ": " + kv.Value + " seen, " + (kv.Value - _policy.Suppressed(kv.Key)) + " written");
            if (_deepReports.Count > 0) sb.AppendLine("Deep captures: " + string.Join(", ", _deepReports.ToArray()));
            sb.AppendLine();
            sb.AppendLine("Network:");
            sb.AppendFormat(ci, "   ping: median {0:0} ms, 95% under {1:0} ms, worst {2:0} ms\r\n", _ping.Percentile(50), _ping.Percentile(95), _ping.Max);
            sb.AppendFormat(ci, "   largest send queue {0:0.0} kB; longest silence {1:0} ms ({2})\r\n", _maxQueueKb, _maxGapMs, _maxGapWhen);
            foreach (var kv in _net.Connections)
                sb.AppendFormat(ci, "   {0}: {1:0.0} MB received, {2:0.0} MB sent\r\n", kv.Value.Name, kv.Value.RunBytesIn / 1048576.0, kv.Value.RunBytesOut / 1048576.0);
            sb.AppendFormat(ci, "   world updates received: {0:0.0} MB\r\n", _net.RunZdoDownBytes / 1048576.0);
            sb.AppendLine("   message names: " + NetAttribution.Book.CountLine());
            var steady = RpcProbe.Steady.Steady();
            sb.AppendLine("   steady messages (in most 10 s windows of the last 12 minutes, at a similar rate): " + (steady.Count == 0 ? "none" : steady.Count.ToString(ci)));
            foreach (var row in steady.Take(25)) sb.AppendLine("      " + SteadyMessages.Line(row));
            sb.AppendLine("   routed messages:");
            sb.Append(_rpc.Summary(Mathf.Max(1f, _inWorldSeconds)));
            sb.AppendLine("   traffic by mod (every package, bytes):");
            sb.Append(NetAttribution.Summary(Mathf.Max(1f, _inWorldSeconds)));
            sb.AppendLine("   world objects received, by prefab (bytes of each object's data, as for uploads):");
            sb.Append(_net.ZdoDownSummary(Mathf.Max(1f, _inWorldSeconds)));
            sb.AppendLine();
            sb.AppendLine("Where frame time went (whole session):");
            sb.Append(TimingTable(25));
            sb.AppendLine();
            float sessionSeconds = Mathf.Max(1f, Time.realtimeSinceStartup - _startedAt);
            sb.AppendLine("Time handling incoming network messages, by mod and message:");
            sb.Append(HandlerTime.Summary(sessionSeconds));
            sb.AppendLine();
            sb.AppendLine("World objects created, time per type (instantiating the prefab and every Awake on it):");
            sb.Append(CreationCost.Summary(sessionSeconds));
            sb.AppendLine();
            sb.AppendLine("Exceptions thrown, caught or not, by type and thrower:");
            sb.Append(!ExceptionWatch.Works ? "   not reported by this runtime\r\n" : ExceptionWatch.Summary(sessionSeconds));
            if (ExceptionWatch.Works && !ExceptionWatch.KnowsThrower) sb.AppendLine("   (this runtime does not say which method threw)");
            sb.AppendLine();
            sb.AppendLine("File writes (config, worlds, characters; notifications, so one write can count more than once):");
            sb.Append(FileChurn.Summary(sessionSeconds));
            sb.AppendLine();
            sb.AppendLine("Harmony patches changed while the game was running (each rebuilds the method and re-runs every transpiler on it):");
            sb.Append(PatchWatch.Summary());
            sb.AppendLine();
            sb.AppendLine("Log: " + _log.Totals() + "; most repeated:");
            sb.Append(_log.SpamSummary(sessionSeconds));
            sb.AppendLine();
            sb.AppendFormat(ci, "Process: working set up to {0:0} MB, private memory up to {1:0} MB, page faults up to {2:0}/s, machine memory up to {3:0}% full ({4})\r\n",
                _maxWorkingSet, _maxPrivate, _maxFaults, _maxMemLoad, _sys.Describe());
            if (_engine != null) sb.AppendLine("Unity counters available: " + _engine.Availability());
            if (_timer.Missing.Count > 0) sb.AppendLine("Game methods not found in this version (not timed): " + string.Join(", ", _timer.Missing.ToArray()));
            if (_timer.Failed.Count > 0) sb.AppendLine("Hooks not installed: " + string.Join("; ", _timer.Failed.ToArray()));
            sb.AppendLine("Timed: " + _timer.Slots.Count(s => s.Mod == null) + " game methods, " + _timer.ModHooks + " mod callbacks." +
                (PhaseTimer.AllocationsMeasured ? " Allocations measured per method." : " Allocations per method: not reported by this runtime."));
            sb.AppendLine("Mods and versions: Vigil-mods.tsv. Config files: Vigil-configs.tsv. Machine: Vigil-system.txt. Harmony patches: Vigil-patches.tsv.");
            return sb.ToString();
        }

        private static void Try(Action a) { try { a(); } catch (Exception) { } }

        public void Dispose()
        {
            if (_timer.DeepRunning) FinishDeep();
            // A normal quit with hooks still in place is not a crash; the marker is only for a crash.
            if (_timer.DeepActive) Try(() => File.Delete(Path.Combine(_dir, "Vigil-state", "deep.active")));
            Try(() => _timeline?.Dispose());
            Try(() => _peers?.Dispose());
        }
    }
}
