using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Vigil.Core;
using UnityEngine;

namespace Vigil.Recorder
{
    /// <summary>
    /// Local diagnostic flight recorder for lag. It records continuously, so the next time the
    /// game lags the evidence is already on disk:
    /// Vigil-timeline.tsv (one row per second: frames, engine, game phases, mods, network,
    /// objects, log, process, context), Vigil-incident-*.txt (written on its own when
    /// something goes wrong, or with the "lag now" key / console command "vigil mark"),
    /// Vigil-deep-*.txt (every mod patch timed for a short while), Vigil-summary.txt,
    /// Vigil-rpc.txt and Vigil.txt (network messages and world-object uploads per 10 s),
    /// Vigil-mods.tsv / -configs.tsv / -system.txt (what the session ran with), and on a
    /// dedicated server Vigil-peers.tsv. Earlier sessions are kept in Vigil-runs/.
    /// It changes nothing in the game and sends nothing, except during a load test started with
    /// "vigil load" (Vigil-load-*.txt), which sends world-update traffic on purpose.
    /// </summary>
    internal sealed partial class RecorderModule : Module
    {
        private const float WindowSeconds = 10f;
        private const int Top = 10;
        private const int KeepRuns = 20;
        private const float SummaryEverySeconds = 60f;

        private sealed class Agg { public int Count; public long Bytes; }
        private sealed class One
        {
            public string Prefab; public int Count; public long Bytes; public Vector3 Pos; public bool Mine;
        }

        private static bool s_inSend;
        private static readonly Dictionary<int, string> s_names = new Dictionary<int, string>();
        private static readonly Dictionary<string, Agg> s_byPrefab = new Dictionary<string, Agg>();
        private static readonly Dictionary<ZDOID, One> s_byZdo = new Dictionary<ZDOID, One>();
        private static long s_bytes;
        private static int s_sends;
        private static long s_runBytes;
        private static string s_path;
        private static float s_windowStart;

        private RpcProbe _rpc;
        private InstrumentJournal _journal;
        private PhaseTimer _timer;
        private NetMonitor _net;
        private LogTap _log;
        private SystemStats _sys;
        private EngineStats _engine;
        private WorldContext _world;
        private Recorder _recorder;
        private LoadTest _load;
        private ConfigEntry<KeyboardShortcut> _markKey;
        private const string PhysicsStepId = "physics step markers";
        private ConfigEntry<bool> _timeModCallbacks;
        private ConfigEntry<bool> _timeCoroutines;
        private bool _watchExceptions, _watchFiles, _watchPatches, _patchMapWritten, _timePhysics;
        private string _dir;
        private string _summaryPath;
        private float _lastSummary;
        private bool _started;
        private bool _server;
        private int _recorderErrors;
        private readonly List<string> _sessionNotes = new List<string>();

        private ConfigEntry<RecordingMode> _recording;
        private VigilPlugin _host;

        private ConfigFile Config => _host.Config;
        private ManualLogSource Logger => _host.Log;

        public override string Area => ConsoleHelp.Recorder;

        public override string SwitchDescription =>
            "The recorder: timeline, incident reports, the lag-report key, deep captures, load tests and the inspect commands. " +
            "Off: no hooks at all and nothing recorded, from the next start on. To pause it while the game runs, use Recording > Mode instead.";

        public override bool RunsOnServer => true;

        /// <summary>Where the recordings go; null until the recorder has started.</summary>
        internal string Dir => _dir;

        public override void Start(VigilPlugin host)
        {
            _host = host;
            _server = host.IsServer;
            _recording = Config.Bind("Recording", "Mode", RecordingMode.Auto,
                "On: records all the time. Off: records nothing (the hooks stay in but do nothing; joins, leaves and saves are still listed). " +
                "Auto: records on a player's machine while in a world, and on a dedicated server while players are connected, for 2 minutes after the last one " +
                "leaves, and for the first 5 minutes after it starts. Deep captures, load tests and player simulations always record. " +
                "Console: vigil on / off / auto; an admin switches a server's with vigil server on / off / auto.");
            long keepBytes = Math.Max(10, Config.Bind("Output", "KeepMegabytes", 500,
                "Earlier sessions kept in Vigil-runs together stay under this size; the oldest go first, the newest always stays.").Value) * 1048576L;
            string folder = Config.Bind("Output", "Folder", "",
                "Where recordings go. Empty: on a player's machine the game's own data folder (LocalLow/IronGate/Valheim/Vigil), outside the " +
                "mod manager profile, whose text files are shared with every player who syncs it; on a dedicated server BepInEx/Vigil.").Value;
            string dir = _dir = OutputLocation.Choose(folder, _server, Paths.BepInExRootPath, Application.persistentDataPath);
            try { Directory.CreateDirectory(dir); }
            catch (Exception) { dir = _dir = Paths.BepInExRootPath; _sessionNotes.Add("The output folder could not be created; recordings are in the BepInEx folder."); }
            // What NetProbe recorded before Vigil replaced it comes along, earlier sessions and skipped hooks
            // included: from a configured folder, a player's old data folder, and the BepInEx folder, where a
            // dedicated server and older versions on a player's machine kept it.
            int migrated = 0;
            var legacy = new List<string>();
            if (!string.IsNullOrWhiteSpace(folder)) legacy.Add(folder.Trim());
            if (!_server) legacy.Add(Path.Combine(Application.persistentDataPath, OutputLocation.LegacyName));
            legacy.Add(Paths.BepInExRootPath);
            foreach (var from in legacy) migrated += OutputLocation.TakeOver(from, dir);
            if (migrated > 0) _sessionNotes.Add(migrated + " files NetProbe recorded were taken over into " + dir + ".");
            _sessionNotes.Add("Recordings: " + dir);
            // Before anything writes: the previous session's reports move to Vigil-runs/.
            StartSteps.Lap("recorder: folders and NetProbe's files");
            string previous = RunArchive.KeepPreviousRun(dir, KeepRuns, keepBytes);
            StartSteps.Lap("recorder: earlier sessions to Vigil-runs");
            if (previous != null) _sessionNotes.Add("The previous session's reports are in " + Path.Combine(RunArchive.FolderName, Path.GetFileName(previous)) + ".");

            string state = Path.Combine(dir, "Vigil-state");
            _journal = new InstrumentJournal(state);
            bool deepWasRunning = File.Exists(Path.Combine(state, "deep.active"));
            if (!_journal.PreviousEndedCleanly)
                _sessionNotes.Add("The previous session did not quit normally (crash, kill or power loss)" + (deepWasRunning ? ", while a deep capture was running: deep capture is off for this session." : "."));
            foreach (var id in _journal.NewlySkipped) _sessionNotes.Add("The previous session died while installing the timer on " + id + "; that timer is skipped from now on (list: Vigil-state/hooks.skip).");
            if (deepWasRunning) { try { File.Delete(Path.Combine(state, "deep.active")); } catch (Exception) { } }

            StartSteps.Lap("recorder: the journal");
            var settings = _settings = BindSettings();
            _timeModCallbacks = Config.Bind("Hooks", "ModCallbacks", true, "Time every mod's Update, FixedUpdate, LateUpdate and OnGUI. A hook that crashes the game is skipped from the next launch on; turn this off to avoid even that one crash.");
            _timeCoroutines = Config.Bind("Hooks", "Coroutines", true, "Time every coroutine step (Unity runs them all through one engine method), named by the mod and method that wrote it.");
            bool allocations = Config.Bind("Hooks", "Allocations", true, "Also measure the memory each timed method allocates (garbage the collector later has to clear), per mod.").Value;
            _watchExceptions = Config.Bind("Hooks", "Exceptions", true, "Count every exception thrown, caught or not, by type and by the mod method that threw it.").Value;
            _watchFiles = Config.Bind("Hooks", "FileWrites", true, "Count writes per file in the config, worlds and characters folders.").Value;
            _watchPatches = Config.Bind("Hooks", "PatchChanges", true, "Record the Harmony patch map once in world (Vigil-patches.tsv) and every patch added or removed while the game runs, with the mod that did it.").Value;
            _timePhysics = Config.Bind("Hooks", "PhysicsStep", true, "Time Unity's own physics step after each FixedUpdate (two markers in Unity's player loop): hitch reports and the timeline's physics_ms then tell physics from the rest of the untimed time.").Value;
            bool handlers = Config.Bind("Hooks", "MessageHandlers", true, "Time the handling of each kind of incoming network message, by mod.").Value;
            bool creation = Config.Bind("Hooks", "ObjectCreation", true, "Time the creation of each world object as it loads in, per object type.").Value;
            _markKey = Config.Bind("Lag report", "Key", new KeyboardShortcut(KeyCode.F9),
                "Press when the game lags: writes Vigil-incident-*-manual.txt with the minutes leading up to it. Console: vigil mark [note].");

            s_path = Path.Combine(dir, "Vigil.txt");
            File.WriteAllText(s_path, "Vigil " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                " - what this game uploads to the server, per " + WindowSeconds + " s window\r\n");
            _summaryPath = Path.Combine(dir, "Vigil-summary.txt");

            StartSteps.Lap("recorder: settings");
            var harmony = new Harmony("isimp.Vigil");
            Patching.Apply(harmony, AccessTools.Method(typeof(ZDOMan), "SendZDOs"), "world-object uploads",
                prefix: Patching.M(typeof(RecorderModule), nameof(SendPrefix)), finalizer: Patching.M(typeof(RecorderModule), nameof(SendFinalizer)));
            Patching.Apply(harmony, AccessTools.Method(typeof(ZDO), "Serialize"), "world-object uploads by prefab",
                postfix: Patching.M(typeof(RecorderModule), nameof(SerializePostfix)));
            _rpc = new RpcProbe(harmony, Path.Combine(dir, "Vigil-rpc.txt"));
            NetAttribution.Patch(harmony);
            ObjectCapture.Patch(harmony);
            _net = new NetMonitor(harmony);
            WorldContext.MissingPath = Path.Combine(dir, "Vigil-missing-prefabs.tsv");
            _world = new WorldContext(harmony);
            _log = new LogTap();
            BepInEx.Logging.Logger.Listeners.Add(_log);
            _sys = new SystemStats();
            if (!_server) { try { _engine = new EngineStats(); } catch (Exception e) { _sessionNotes.Add("Unity counters unavailable: " + e.GetType().Name); } }

            StartSteps.Lap("recorder: network, world and log probes");
            if (handlers) HandlerTime.Patch(harmony);
            if (creation) CreationCost.Patch(harmony);
            if (_watchExceptions)
            {
                try { ExceptionWatch.Start(); } catch (Exception e) { _sessionNotes.Add("Exceptions are not counted: " + e.GetType().Name); }
            }

            StartSteps.Lap("recorder: message, object and exception timers");
            _timer = new PhaseTimer(_journal, allocations);
            _ownHooks = new OwnHooks(_journal);
            _timer.HookGame();
            StartSteps.Lap("recorder: game timers");
            EventLog.Start(dir, harmony);
            Patching.Apply(harmony, AccessTools.Method(typeof(ZNet), "OnNewConnection"), "joins timed from the connection",
                postfix: Patching.M(typeof(RecorderModule), nameof(NewConnectionPostfix)));
            Patching.Apply(harmony, AccessTools.Method(typeof(ZNet), "StopAll"), "a deep capture ended on leaving the world",
                prefix: Patching.M(typeof(RecorderModule), nameof(LeavingPrefix)));
            DropWatch.Start(harmony);
            _recorder = new Recorder(dir, settings, _journal, _timer, _net, _log, _sys, _engine, _world, _rpc, UploadText, _server, deepWasRunning, s => Logger.LogWarning(s));
            DropWatch.Dropped = (who, drop, samples) => _recorder?.Drop(who, drop, samples);
            _recorder.ReportWritten = ReportWritten;
            StartSteps.Lap("recorder: events, drops, the recorder");
            BindSharing();
            BindStartup(harmony);
            StartSteps.Lap("recorder: sharing, startup timeline");
            BindFireSounds();
            if (!_server) FireSoundsPatch.Hook(harmony);
            StartSteps.Lap("recorder: fire sounds");
            BindOwnership();
            BindWeather();
            BindPatches();
            StartSteps.Lap("recorder: ownership, weather, patches");
            StartSpawnWatch();
            StartSteps.Lap("recorder: spawn watch");
            _load = new LoadTest(_server, dir, VigilVersion.Value, _recorder, _timer, s => Logger.LogInfo(s));
            BindAlerts();

            s_windowStart = Time.realtimeSinceStartup;
            _lastSummary = s_windowStart;
        }

        private Recorder.Settings BindSettings()
        {
            var s = new Recorder.Settings();
            s.HitchMs = Config.Bind("Incidents", "LongFrameMs", s.HitchMs, "A frame at least this long (in world) writes a report.").Value;
            s.SlowFps = Config.Bind("Incidents", "SlowFps", s.SlowFps, "Frame rate below this ...").Value;
            s.SlowSeconds = Config.Bind("Incidents", "SlowSeconds", s.SlowSeconds, "... for this many seconds in a row writes a report (not while unfocused, paused or capped).").Value;
            s.SilentMs = Config.Bind("Incidents", "SilentMs", s.SilentMs, "Nothing received from the server (or, on a server, from a player) for this long writes a report.").Value;
            s.PingMs = Config.Bind("Incidents", "PingMs", s.PingMs, "Ping at or above this writes a report.").Value;
            s.QueueKb = Config.Bind("Incidents", "SendBacklogKb", s.QueueKb, "Unsent data at or above this many kB writes a report. The game keeps up to one world-update batch queued on purpose (10 kB in vanilla, the Network mod's Batch Size when installed), so set this above that batch.").Value;
            s.LogFloodPerSec = Config.Bind("Incidents", "LogLinesPerSecond", s.LogFloodPerSec, "This many log lines in one second writes a report.").Value;
            s.MemoryLoadPct = Config.Bind("Incidents", "MemoryLoadPct", s.MemoryLoadPct, "Machine memory this full writes a report.").Value;
            s.FaultsPerSec = Config.Bind("Incidents", "PageFaultsPerSecond", s.FaultsPerSec, "This many page faults per second writes a report.").Value;
            s.MaxIncidents = Config.Bind("Incidents", "MaxReportsPerSession", s.MaxIncidents, "Reports written on their own per session (the key always works).").Value;
            s.IncidentCooldownSeconds = Config.Bind("Incidents", "QuietSeconds", s.IncidentCooldownSeconds, "After a report, the same kind waits this long before the next.").Value;
            s.AutoDeep = Config.Bind("Deep capture", "StartOnSlowStretch", s.AutoDeep, "On a slow stretch, time every mod patch for a while (Vigil-deep-*.txt). Console: vigil deep [seconds].").Value;
            s.DeepSeconds = Config.Bind("Deep capture", "Seconds", s.DeepSeconds, "How long a deep capture runs.").Value;
            s.AutoDeepCooldownMinutes = Config.Bind("Deep capture", "MinutesBetween", s.AutoDeepCooldownMinutes, "Minimum time between automatic deep captures.").Value;
            s.DeepBudgetMs = Config.Bind("Deep capture", "MsPerFrame", s.DeepBudgetMs, "Time per frame spent putting timers in and taking them out. Higher finishes sooner, lower keeps the frame rate steadier.").Value;
            return s;
        }

        /// <summary>The console words the recorder answers, after "vigil".</summary>
        public override string Command(string word, string rest)
        {
            if (word == "mark") { _recorder.Mark(rest); return "Vigil: lag report written (Vigil-incident-*-manual.txt)"; }
            if (word == "deep")
            {
                float secs = ConsoleHelp.DeepSeconds;
                float.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out secs);
                return "Vigil: " + _recorder.StartDeep(secs > 0 ? secs : ConsoleHelp.DeepSeconds, "started from the console");
            }
            if (word == "census") return "Vigil: " + Safe(() => StartCensus(rest, toConsole: true));
            if (word == "memory") return "Vigil: " + Own(() => SceneCensus.WriteMemory(_dir));
            if (word == "patches") return "Vigil: " + Own(() => PatchWatch.WriteMap(_dir));
            if (word == "startup") return "Vigil: " + Safe(() => StartupCommand(rest));
            if (word == "inspect" && rest.Trim().Equals("names", StringComparison.OrdinalIgnoreCase)) return "Vigil: " + Safe(ReadNamesCommand);
            if (word == "inspect") return "Vigil: " + Own(InspectCommand);
            if (word == "spawned") return "Vigil: " + Safe(() => SpawnedCommand(rest));
            if (word == "spawnable") return "Vigil: " + Own(SpawnableCommand);
            if (word == "leftovers") return "Vigil: " + Safe(() => LeftoversCommand(rest));
            if (word == "busiest") return "Vigil: " + Safe(() => StartBusiest(rest, toConsole: true));
            if (word == "weather") return "Vigil: " + Safe(() => WeatherCommand(rest));
            if (word == "trace") return "Vigil: " + Safe(() => TraceCommand(rest));
            if (word == "load") return "Vigil: " + Safe(() => _load.Command(rest));
            if (word == "on" || word == "off" || word == "auto") return "Vigil: " + RecordingCommand(word);
            if (word == "server") return ServerCommand(rest);
            return null;
        }

        public override string Status() => _recorder.Status() + "\n" + RecordingStatus() + "\nRecordings: " + _dir;

        // "vigil trace materials [seconds] | stop"
        private string TraceCommand(string rest)
        {
            var words = rest.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || words[0] != "materials") return "use: vigil trace materials [seconds] or vigil trace materials stop";
            if (words.Length > 1 && words[1] == "stop") return MaterialTrace.Stop();
            float secs = ConsoleHelp.TraceSeconds;
            if (words.Length > 1) float.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out secs);
            return MaterialTrace.Start(_dir, secs > 0 ? secs : ConsoleHelp.TraceSeconds);
        }

        internal string MarkKeyName()
        {
            try { return _markKey?.Value.ToString() ?? "F9"; } catch (Exception) { return "F9"; }
        }

        private static string Safe(Func<string> f)
        {
            try { return f(); } catch (Exception e) { return "failed: " + e.GetType().Name + " " + e.Message; }
        }

        /// <summary>Time another part of Vigil took on the game's thread (a self-test step): Vigil's own, so the frame it stalls is no hitch.</summary>
        public void AddOwnTime(double ms) => _recorder?.AddOwnTime(ms);

        public override long TakeTicksTimedElsewhere() => _recorder?.TakeFrameWorkTicks() ?? 0;

        /// <summary>A tool that walks the game's objects in one go: its time is Vigil's own, so the frame it stalls is no hitch.</summary>
        private string Own(Func<string> f)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try { return Safe(f); }
            finally { _recorder?.AddOwnTime(watch.Elapsed.TotalMilliseconds); }
        }

        private static void SendPrefix() { s_inSend = ProbeState.Active; }
        private static void SendFinalizer() { s_inSend = false; }

        internal static string NameOf(int hash)
        {
            if (hash == 0) return "load test objects (no type)";
            if (s_names.TryGetValue(hash, out var n)) return n;
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
            if (go == null) return "hash:" + hash;
            s_names[hash] = go.name;
            return go.name;
        }

        private static void SerializePostfix(ZDO __instance, ZPackage pkg)
        {
            if (!ProbeState.Active) return;
            if (!s_inSend) return;
            int size = pkg.Size();
            string prefab = NameOf(__instance.GetPrefab());
            s_bytes += size; s_sends++; s_runBytes += size;
            if (!s_byPrefab.TryGetValue(prefab, out var a)) s_byPrefab[prefab] = a = new Agg();
            a.Count++; a.Bytes += size;
            if (!s_byZdo.TryGetValue(__instance.m_uid, out var o)) s_byZdo[__instance.m_uid] = o = new One { Prefab = prefab };
            o.Count++; o.Bytes += size; o.Pos = __instance.GetPosition(); o.Mine = __instance.IsOwner();
            LoadTest.CountSent(__instance, size);
            ObjectCapture.Sent(__instance, size);
        }

        public override void FixedUpdate()
        {
            _recorder?.OnFixedUpdate();
        }

        // ---- recording switch (Recording > Mode): decided once a frame, here and nowhere else

        private int _players;
        private float _playersCheckedAt = -10f, _lastPlayerSeen = float.NegativeInfinity;
        private static bool s_peerArrived, s_leftWorld;

        // A player's game (a world host's too) ending its session, on logout or a lost connection; a
        // dedicated server has no world to leave. Acted on at the next frame, so quitting the game,
        // which has no next frame, takes no timers out.
        private static void LeavingPrefix(ZNet __instance)
        {
            try { if (!__instance.IsDedicated()) s_leftWorld = true; } catch (Exception) { }
        }

        /// <summary>The frame after leaving the world: a deep capture under way ends at once, all its timers out.</summary>
        private void EndDeepOnLeaving()
        {
            if (!s_leftWorld) return;
            s_leftWorld = false;
            var (phase, timers, ms) = _recorder?.EndDeepAtOnce("you left the world") ?? (DeepPhase.Idle, 0, 0);
            if (phase == DeepPhase.Idle) return;
            string line = DeepPlan.LeftLine(phase, timers, ms);
            EventLog.Write("deep", "you", line);
            Logger.LogInfo("Vigil: " + line);
        }

        // A connection on a server: the message-handler timer starts at once (the join's costliest
        // messages often come in this very frame), and the players are counted again next frame.
        private static void NewConnectionPostfix()
        {
            s_peerArrived = true;
            s_startupOwner?.JoinBegins();
            if (s_recordingMode != RecordingMode.Off && ZNet.instance != null && ZNet.instance.IsServer()) ProbeState.Joining = true;
        }

        private static RecordingMode s_recordingMode = RecordingMode.Auto;

        private void UpdateRecording()
        {
            float now = Time.realtimeSinceStartup;
            s_recordingMode = _recording.Value;
            if (now - _playersCheckedAt >= 1f || s_peerArrived)
            {
                _playersCheckedAt = now;
                s_peerArrived = false;
                var kinds = new List<PeerKind>();
                try
                {
                    if (_server && ZNet.instance != null)
                        foreach (var p in ZNet.instance.GetPeers())
                            if (p != null) kinds.Add(Bots.IsBot(p) ? PeerKind.Bot : p.IsReady() ? PeerKind.Playing : PeerKind.Joining);
                }
                catch (Exception) { }
                _players = RecordingPolicy.Players(kinds);
                ProbeState.Joining = _recording.Value != RecordingMode.Off && kinds.Contains(PeerKind.Joining);
                if (_players > 0) _lastPlayerSeen = now;
            }
            bool busy = _recorder.DeepActive || _load.Running;
            bool want = RecordingPolicy.Records(_recording.Value, _server, InWorld(), _players, now - _lastPlayerSeen, now, busy);
            if (want == ProbeState.Active) return;
            ProbeState.Active = want;
            if (want)
            {
                if (_watchFiles) FileChurn.Restart();
                try { _engine?.Resume(); } catch (Exception) { }
                s_byPrefab.Clear(); s_byZdo.Clear(); s_bytes = 0; s_sends = 0;
                s_windowStart = now;
                _lastSummary = now;
            }
            else
            {
                // The window in progress is written as its own before the pause, so none of it lands in the first window after.
                CloseWindow(now);
                FileChurn.Suspend();
                try { _engine?.Pause(); } catch (Exception) { }
                WriteSummary();
            }
            try { EventLog.Write("recording", want ? "on" : "off", Why()); } catch (Exception) { }
        }

        private static bool InWorld()
        {
            string sit = WorldContext.Situation();
            return sit == "in world" || sit == "teleporting" || sit == "loading";
        }

        private string Why()
        {
            if (_recorder.DeepActive || _load.Running) return "a deep capture or test is running";
            switch (_recording.Value)
            {
                case RecordingMode.On: return "switched on";
                case RecordingMode.Off: return "switched off";
            }
            if (!_server) return InWorld() ? "auto: in a world" : "auto: not in a world";
            float now = Time.realtimeSinceStartup;
            if (_players > 0) return "auto: " + _players + (_players == 1 ? " player" : " players") + " connected";
            if (now < RecordingPolicy.ServerStartSeconds) return "auto: the server is starting";
            if (now - _lastPlayerSeen < RecordingPolicy.ServerLingerSeconds) return "auto: a player left a short while ago";
            return "auto: no players connected";
        }

        // What the switch says right now, by the same rule UpdateRecording applies on the next frame.
        private string RecordingStatus()
        {
            return RecordingPolicy.Status(_recording.Value, RecordingNow(), Why());
        }

        /// <summary>
        /// "on", "off", "auto" or "status", from the console or from an admin over the network (on a
        /// server). It only sets the switch: recording starts or stops on the next frame, outside any
        /// timed call, since this runs inside one (the console or the network message handler).
        /// </summary>
        private string RecordingCommand(string word)
        {
            if (word != "status")
            {
                if (!RecordingPolicy.TryParse(word, out var mode)) return "use on, off, auto or status";
                _recording.Value = mode; // saved to the config, so it holds after a restart
            }
            return RecordingStatus();
        }

        private float _lastUpdateAt, _frameMs;

        public override void Update()
        {
            UpdateStartup();
            float frameAt = Time.realtimeSinceStartup;
            _frameMs = (frameAt - _lastUpdateAt) * 1000f;
            _lastUpdateAt = frameAt;
            if (!_started)
            {
                // Every plugin has finished loading by the first frame.
                _started = true;
                StartSteps.Begin();
                if (_timeCoroutines.Value && !_timer.HookCoroutines()) _sessionNotes.Add("Coroutines are not timed this session (see Hooks not installed).");
                if (_timeModCallbacks.Value)
                {
                    try { _timer.HookModCallbacks(); } catch (Exception e) { _sessionNotes.Add("Mod timers not installed: " + e.GetType().Name); }
                }
                else _sessionNotes.Add("Timers on mods' Update/FixedUpdate/LateUpdate/OnGUI are off (Hooks.ModCallbacks).");
                StartSteps.Lap("first frame: coroutine and mod timers");
                if (_watchPatches) PatchWatch.Start(new Harmony("isimp.Vigil.patches"), _journal, _sessionNotes);
                StartSteps.Lap("first frame: patch watch");
                if (!_timePhysics) _sessionNotes.Add("Unity's physics step is not timed (Hooks.PhysicsStep).");
                else if (_journal.Allowed(PhysicsStepId))
                {
                    _journal.Begin(PhysicsStepId);
                    try { if (!PhysicsStep.Install()) _sessionNotes.Add("Unity's physics step is not timed: " + PhysicsStep.Problem + "."); }
                    catch (Exception e) { _sessionNotes.Add("Unity's physics step is not timed: " + e.GetType().Name + " " + e.Message); }
                    finally { _journal.Done(PhysicsStepId); }
                }
                StartSteps.Lap("first frame: physics step timer");
                if (_watchFiles)
                {
                    var folders = new[]
                    {
                        new KeyValuePair<string, string>("config", Paths.ConfigPath),
                        new KeyValuePair<string, string>("worlds", Path.Combine(Application.persistentDataPath, "worlds_local")),
                        new KeyValuePair<string, string>("worlds", Path.Combine(Application.persistentDataPath, "worlds")),
                        new KeyValuePair<string, string>("characters", Path.Combine(Application.persistentDataPath, "characters_local")),
                        new KeyValuePair<string, string>("characters", Path.Combine(Application.persistentDataPath, "characters")),
                    };
                    if (FileChurn.Start(folders, _dir) == 0) _sessionNotes.Add("File writes are not counted: no folder could be watched.");
                }
                if (_watchExceptions && !ExceptionWatch.Works) _sessionNotes.Add("Exceptions are not counted: this runtime does not report them as they are thrown.");
                StartSteps.Lap("first frame: file watch");
                try { SessionInfo.Write(_dir, configs: !_startup.HoldingNow); } catch (Exception) { }
                StartSteps.Lap("first frame: mods and configs listed");
                StartSteps.End();
            }
            try { UpdateSharing(); } catch (Exception e) { _shareError = e.GetType().Name + " " + e.Message; _shareAsking = false; }
            try { UpdateSpawnLogSharing(); } catch (Exception e) { _spawnLogError = e.GetType().Name + " " + e.Message; _spawnLogAsking = false; }
            try { UpdateLeftovers(); } catch (Exception e) { _leftNote = "the reading stopped: " + e.GetType().Name + " " + e.Message; _leftState = LeftoverReport.None; }
            try { UpdateRestart(); } catch (Exception) { }
            try { UpdateOwnership(); } catch (Exception) { }
            try { EndDeepOnLeaving(); } catch (Exception e) { Logger.LogWarning("Vigil: ending the deep capture on leaving the world failed: " + e.GetType().Name + " " + e.Message); }
            try { UpdateInspect(); } catch (Exception) { }
            try { UpdateCensus(); } catch (Exception e) { _censusRun?.Stop(e.GetType().Name + " " + e.Message); _censusRun = null; }
            try { UpdateWeather(); } catch (Exception) { }
            try { UpdateJoinPackages(); } catch (Exception) { }
            try { UpdateArrivals(); } catch (Exception) { }
            try { UpdatePatches(); } catch (Exception) { }
            try { if (!_server) AwaySleepPatch.Update(_host.Link, Now); } catch (Exception) { }
            try { UpdateOwnCost(); } catch (Exception) { }
            NetAttribution.InWorld = _server ? ZNet.instance != null : Player.m_localPlayer != null;
            try { UpdateObjectData(); } catch (Exception) { }
            try { OdinVolumePatch.TrialTick(); } catch (Exception) { }
            try { UpdateAlerts(); } catch (Exception e) { _alertsError = e.GetType().Name + " " + e.Message; _alertsAsking = false; }
            if (_watchPatches && !_patchMapWritten && PatchMapDue())
            {
                // In world every mod has patched what it patches at load; from here on a change is made during play.
                _patchMapWritten = true;
                try { PatchWatch.WriteMap(_dir); } catch (Exception e) { _sessionNotes.Add("Patch map not written: " + e.GetType().Name); }
                PatchWatch.Arm();
            }
            try { UpdateRecording(); } catch (Exception) { }
            try { _recorder.OnUpdate(); }
            catch (Exception e)
            {
                // Reported once in the summary; never every frame (that would flood the very log we watch).
                if (_recorderErrors++ == 0) _sessionNotes.Add("Recorder error (first of possibly more): " + e);
            }
            try { _load.Update(); } catch (Exception) { }
            try { MaterialTrace.Update(); } catch (Exception) { }

            if (!_server && _markKey.Value.IsDown() && !Typing())
            {
                if (ProbeState.Active) _recorder.Mark("key");
                string msg = ProbeState.Active ? "Vigil: lag report saved" : "Vigil is not recording now (" + Why() + "); vigil on switches it on";
                try { MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, msg); } catch (Exception) { }
            }

            if (!ProbeState.Active) return;
            float now = Time.realtimeSinceStartup;
            if (now - _lastSummary >= SummaryEverySeconds) { _lastSummary = now; WriteSummary(); }
            // Due in a long frame, the window waits one frame, until the hitch report has read it.
            if (ReportWindow.CloseNow(now - s_windowStart, WindowSeconds, _frameMs, _settings.HitchMs)) CloseWindow(now);
        }

        // Ends the 10 s window: writes its lines, folds its tallies into the session and starts the next.
        private void CloseWindow(float now)
        {
            float span = now - s_windowStart;
            if (s_sends > 0) { try { File.AppendAllText(s_path, Report(span)); } catch (Exception) { } }
            _rpc.Report(span);
            _log.SpamWindow(span);
            HandlerTime.EndWindow(span);
            CreationCost.EndWindow(span);
            ExceptionWatch.EndWindow(span);
            FileChurn.EndWindow(span);
            s_byPrefab.Clear(); s_byZdo.Clear(); s_bytes = 0; s_sends = 0; s_windowStart = now;
        }

        // A player: the first frame in world. A dedicated server: two minutes after start, once the world has loaded.
        private bool PatchMapDue()
        {
            string sit = WorldContext.Situation();
            return sit == "in world" || (sit == "server" && Time.realtimeSinceStartup > 120f);
        }

        private static bool Typing()
        {
            try
            {
                return global::Console.IsVisible() || (Chat.instance != null && Chat.instance.HasFocus()) || TextInput.IsVisible();
            }
            catch (Exception) { return false; }
        }

        public override void Quit()
        {
            // A game closed before its menu showed: the held config saves are written now.
            try { _startup?.ReleaseSaves("the game quit"); } catch (Exception) { }
            try { _load?.Shutdown(); } catch (Exception) { }
            WriteSummary();
            FileChurn.Stop();
            _recorder?.Dispose();
            _journal?.MarkCleanExit();
            _journal?.Dispose();
        }

        private void WriteSummary()
        {
            try
            {
                var header = new List<string>(_sessionNotes);
                if (_recorderErrors > 1) header.Add("Recorder errors this session: " + _recorderErrors + ".");
                if (WorldContext.MissingCount > 0) header.Add(WorldContext.MissingCount + " objects in the world have a prefab no installed mod provides; where they are: Vigil-missing-prefabs.tsv.");
                foreach (var p in Patching.Problems) header.Add("Not measured: " + p);
                header.Add(string.Format(CultureInfo.InvariantCulture, "World-object uploads: {0:0.0} MB in all.", s_runBytes / 1048576.0));
                header.AddRange(OwnCostLines());
                header.AddRange(ObjectDataLines());
                header.AddRange(JoinPackagesLines());
                File.WriteAllText(_summaryPath, _recorder.Summary(header));
            }
            catch (Exception) { }
        }

        /// <summary>The upload report for the current window, without ending it.</summary>
        private static string UploadText(float span) => Report(span);

        private static string Report(float span)
        {
            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture, "\r\n[{0}] {1:0.0} s: {2} object updates, {3:0.0} kB ({4:0.0} kB/s), {5} distinct objects\r\n",
                DateTime.Now.ToString("HH:mm:ss"), span, s_sends, s_bytes / 1024.0, s_bytes / 1024.0 / span, s_byZdo.Count);
            sb.AppendLine(" by prefab (bytes):");
            foreach (var kv in s_byPrefab.OrderByDescending(k => k.Value.Bytes).Take(Top))
                sb.AppendFormat(CultureInfo.InvariantCulture, "   {0,-34} updates={1,-5} {2,8:0.0} kB  avg {3} B\r\n",
                    kv.Key, kv.Value.Count, kv.Value.Bytes / 1024.0, kv.Value.Bytes / Math.Max(1, kv.Value.Count));
            sb.AppendLine(" by single object (bytes):");
            foreach (var kv in s_byZdo.OrderByDescending(k => k.Value.Bytes).Take(Top))
            {
                var o = kv.Value;
                sb.AppendFormat(CultureInfo.InvariantCulture, "   {0,-34} updates={1,-4} {2,8:0.0} kB  avg {3} B  at ({4:0},{5:0},{6:0}) {7} id={8}\r\n",
                    o.Prefab, o.Count, o.Bytes / 1024.0, o.Bytes / Math.Max(1, o.Count),
                    o.Pos.x, o.Pos.y, o.Pos.z, o.Mine ? "mine" : "other", kv.Key);
            }
            return sb.ToString();
        }
    }
}
