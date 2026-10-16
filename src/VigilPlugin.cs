using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using ServerSync;
using UnityEngine;
using UnityEngine.Rendering;
using Vigil.Cleanup;
using Vigil.Core;
using Vigil.Recorder;
using Vigil.Spawns;

namespace Vigil;

/// <summary>
/// Vigil, a local tool: it records what the game does, so a lag can be read afterwards from the
/// files, and shows what the game sees around the player. Each area is a module with its own switch
/// under Modules in the config. The host starts the switched-on ones, passes them Unity's calls,
/// answers the "vigil" console command by handing each word to the area that owns it, draws the
/// panel and the HUD line on a player's game, and carries the conversation between a player's
/// Vigil and the server's (<see cref="Link"/>). Vigil replaces NetProbe, SpawnProof and Tidy,
/// takes over their settings and recordings at its first start, and does not load beside them,
/// which would put every hook in twice.
/// </summary>
[BepInPlugin(Guid, Name, VigilVersion.Value)]
[BepInIncompatibility("isimp.NetProbe")]
[BepInIncompatibility("isimp.SpawnProof")]
[BepInIncompatibility("isimp.Tidy")]
public sealed class VigilPlugin : BaseUnityPlugin
{
    public const string Guid = "isimp.Vigil";
    public const string Name = "Vigil";

    private static readonly char[] Space = { ' ' };

    private readonly List<Module> _running = new();
    private readonly HashSet<Module> _failed = new();
    private readonly List<Module> _all = new();
    private RecorderModule _recorder;
    private SpawnsModule _spawns;
    private Panel _panel;
    private HudView _hud;
    private SelfTestReport.Shown _selfTest, _serverSelfTest;
    private bool _linkFailed, _uiFailed, _panelWasOpen;

    internal ManualLogSource Log => Logger;

    /// <summary>A dedicated server: it has no graphics device.</summary>
    internal bool IsServer { get; private set; }

    internal Link Link { get; private set; }

    /// <summary>
    /// The settings the dedicated server decides for every player running Vigil, passed on by
    /// ServerSync the way most mods pass theirs. No player is turned away for lacking Vigil or
    /// for another version of it, and only the server's admins change these in game.
    /// </summary>
    internal ConfigSync Sync { get; private set; }

    private void Awake()
    {
        IsServer = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
        // A dedicated server has no panel to show tool and incident results in.
        ResultStore.Shared.Shown = !IsServer;
        TakeOverSettings();
        Sync = new ConfigSync(Guid) { DisplayName = Name, CurrentVersion = VigilVersion.Value, ModRequired = false, IsLocked = true };
        _recorder = new RecorderModule();
        _spawns = new SpawnsModule();
        _all.AddRange(new Module[] { _recorder, _spawns, new CleanupModule() });
        foreach (var module in _all)
        {
            if (!Config.Bind("Modules", module.Area, true, module.SwitchDescription).Value)
            {
                Logger.LogInfo("Vigil: " + module.Area + " is switched off in the config (Modules); it is not started.");
                continue;
            }

            if (IsServer && !module.RunsOnServer)
            {
                continue;
            }

            try
            {
                module.Start(this);
                _running.Add(module);
            }
            catch (Exception e)
            {
                _failed.Add(module);
                Logger.LogError("Vigil: " + module.Area + " could not start: " + e);
            }
        }

        Link = new Link(IsServer, Admin, s => Logger.LogInfo(s));
        if (!IsServer)
        {
            string folder = _recorder.Dir ?? Path.Combine(Application.persistentDataPath, OutputLocation.FolderName);
            _panel = new Panel(this, Config, Path.Combine(folder, OutputLocation.FolderName + "-state"));
            _hud = new HudView(Config);
            PanelInput.Patch(Guid + ".panel");
        }

        RegisterConsole();
    }

    /// <summary>
    /// At Vigil's first start, before anything reads a setting: what was set in NetProbe's and
    /// SpawnProof's config files is written into Vigil's under the places those settings have now,
    /// and the config reader takes it up. The old files are only read.
    /// </summary>
    private void TakeOverSettings()
    {
        try
        {
            if (File.Exists(Config.ConfigFilePath))
            {
                return;
            }

            var old = new Dictionary<string, string>();
            foreach (var name in new[] { ConfigCarryOver.NetProbeFile, ConfigCarryOver.SpawnProofFile })
            {
                string path = Path.Combine(Paths.ConfigPath, name);
                if (File.Exists(path))
                {
                    old[name] = File.ReadAllText(path);
                }
            }

            var values = ConfigCarryOver.Values(old);
            if (values.Count == 0)
            {
                return;
            }

            File.WriteAllText(Config.ConfigFilePath, ConfigCarryOver.NewFileText(values));
            Config.Reload();
            Logger.LogInfo("Vigil took over " + values.Count + " settings from " + string.Join(" and ", old.Keys) + ".");
        }
        catch (Exception e)
        {
            Logger.LogWarning("Vigil: the earlier settings were not taken over: " + e.Message);
        }
    }

    private void RegisterConsole()
    {
        try
        {
            _ = new Terminal.ConsoleCommand("vigil", "[help | status | selftest | on | off | auto | mark | deep | load | census | spawns | missing ...] Vigil; vigil help explains each",
                (Terminal.ConsoleEvent)(args => args.Context?.AddString(Run(args.Args.Skip(1).ToList()))),
                optionsFetcher: () => ConsoleHelp.Commands.ToList());

            // The names of the mods Vigil replaced still work: "netprobe mark" is "vigil mark", "tidy missing" is "vigil missing".
            foreach (var old in new[] { "netprobe", "tidy" })
            {
                _ = new Terminal.ConsoleCommand(old, "same as vigil", (Terminal.ConsoleEvent)(args => args.Context?.AddString(Run(args.Args.Skip(1).ToList()))),
                    isSecret: true, optionsFetcher: () => ConsoleHelp.Commands.ToList());
            }
        }
        catch (Exception e)
        {
            Logger.LogWarning("Vigil: console command not registered: " + e.GetType().Name);
        }
    }

    /// <summary>The answer to "vigil" followed by these words.</summary>
    internal string Run(List<string> words)
    {
        string word = words.Count > 0 ? words[0].ToLowerInvariant() : "status";
        string rest = words.Count > 1 ? string.Join(" ", words.Skip(1)) : "";
        if (word == "help" || word == "?")
        {
            return string.Join("\n", ConsoleHelp.Lines(rest, _recorder.MarkKeyName(), _spawns.KeyName(), _recorder.Dir ?? "(the recorder is not running)", _panel?.KeyName));
        }

        if (word == "status")
        {
            var lines = _running.Select(m => Safe(m, m.Status)).Where(s => !string.IsNullOrEmpty(s)).ToList();
            return lines.Count > 0 ? string.Join("\n", lines) : "Vigil " + VigilVersion.Value + ": every area is switched off (Modules in the config).";
        }

        if (word == "selftest")
        {
            string tier = rest.Trim().ToLowerInvariant();
            if (tier == "server")
            {
                return AskServerSelfTest();
            }

            return IsServer ? string.Join("\n", RunSelfTest()) : StartSelfTest(tier == "full", fromConsole: true);
        }

        string area = ConsoleHelp.Owner(word);
        if (area == null)
        {
            return "Vigil: no command \"" + word + "\". vigil help lists them all.";
        }

        var module = _running.FirstOrDefault(m => m.Area == area);
        if (module == null)
        {
            return IsServer
                ? "Vigil: " + area + " does not run on a dedicated server."
                : "Vigil: " + area + " is switched off (Modules > " + area + " in the config, from the next start).";
        }

        return Safe(module, () => module.Command(word, rest)) ?? "Vigil: no command \"" + word + "\". vigil help lists them all.";
    }

    /// <summary>Runs a console line for a panel button and shows the answer in the panel.</summary>
    internal void RunShown(string line) => Show(Run(line.Split(Space, StringSplitOptions.RemoveEmptyEntries).ToList()));

    /// <summary>
    /// Runs a console line for a tool that shows its result under it in the panel: the answer shows
    /// only when no result came (the tool failed, or had nothing to look at).
    /// </summary>
    internal void RunForResult(string line, string tool)
    {
        var before = ResultStore.Shared.Last(tool);
        string answer = Run(line.Split(Space, StringSplitOptions.RemoveEmptyEntries).ToList());
        if (!ResultStore.Shared.CameSince(tool, before))
        {
            Show(answer);
        }
    }

    /// <summary>Runs a console line that writes an incident (a lag report): gives the new incident, or shows the answer and gives null when none came.</summary>
    internal Result RunForIncident(string line)
    {
        var incidents = ResultStore.Shared.Incidents;
        var before = incidents.Count > 0 ? incidents[0] : null;
        string answer = Run(line.Split(Space, StringSplitOptions.RemoveEmptyEntries).ToList());
        if (incidents.Count > 0 && !ReferenceEquals(incidents[0], before))
        {
            return incidents[0];
        }

        Show(answer);
        return null;
    }

    /// <summary>Shows an answer at the bottom of the panel.</summary>
    internal void Show(string text) => _panel?.Show(text);

    /// <summary>Opens a card of the panel: its tab shown, the card unfolded.</summary>
    internal void Reveal(string tab, string card) => _panel?.Reveal(tab, card);

    /// <summary>An answer that came back from the server later: in the console, on screen and in the panel.</summary>
    internal void Reply(string text)
    {
        try
        {
            global::Console.instance?.Print(text);
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, text);
        }
        catch (Exception)
        {
            // the console or the message may not exist in the main menu
        }

        Show(text);
    }

    // ------------------------------------------------------------------ the server's side of the admin channel

    private Dictionary<string, object> Admin(string command, Dictionary<string, string> args, ZNetPeer from)
    {
        if (command == "selftest")
        {
            return new Dictionary<string, object> { { "text", string.Join("\n", RunSelfTest("the server")) } };
        }

        foreach (var m in _running)
        {
            Dictionary<string, object> answer = null;
            Guarded(m, () => answer = m.Admin(command, args, from));
            if (answer != null)
            {
                return answer;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ self-test

    private List<string> RunSelfTest(string machine = "this game") => WriteSelfTest(machine, InstallChecks());

    /// <summary>The self-test's report from its checks: into Vigil-selftest.txt (or -server.txt), the Health tab and the console's answer.</summary>
    private List<string> WriteSelfTest(string machine, List<SelfTestReport.Check> checks)
    {
        var lines = SelfTestReport.Lines(machine, checks);
        if (_recorder.Dir != null)
        {
            try
            {
                File.WriteAllText(Path.Combine(_recorder.Dir, (machine == "the server" ? "Vigil-selftest-server.txt" : "Vigil-selftest.txt")), string.Join("\r\n", lines) + "\r\n");
            }
            catch (Exception)
            {
                // the report is in the console and the panel either way
            }
        }

        _selfTest = machine == "this game" ? SelfTestReport.Read(lines) : _selfTest;
        return lines;
    }

    // ------------------------------------------------------------------ self-test routines

    private bool _selfTestRunning;
    private string _selfTestStep;

    /// <summary>
    /// Runs the self-test with its routines over the next frames: the checks of what is installed,
    /// then each area's routines in turn (the full tier adds those that ask the server), each given
    /// its time; the report follows in the Health tab, and in the console when asked from there.
    /// </summary>
    private string StartSelfTest(bool full, bool fromConsole)
    {
        if (_selfTestRunning)
        {
            return "Vigil: a self-test is running (" + _selfTestStep + ")";
        }

        StartCoroutine(SelfTestRun(full, fromConsole));
        return "Vigil: " + (full ? "full" : "quick") + " self-test running; the report follows";
    }

    private System.Collections.IEnumerator SelfTestRun(bool full, bool fromConsole)
    {
        _selfTestRunning = true;
        Vigil.Recorder.OwnTools.SelfTestRuns++;
        var whole = System.Diagnostics.Stopwatch.StartNew();

        // Each step's time is Vigil's own: the frame a slow step stalls is no hitch.
        var step = System.Diagnostics.Stopwatch.StartNew();
        var checks = new List<SelfTestReport.Check>();
        try
        {
            checks.AddRange(InstallChecks());
            var routines = new List<SelfTestRoutine> { new SelfTestRoutine(Name, "every panel tab draws", 10f, PanelTabs) };
            foreach (var m in _running)
            {
                try
                {
                    routines.AddRange(m.Routines(full));
                }
                catch (Exception e)
                {
                    checks.Add(new SelfTestReport.Check(m.Area, "routines", SelfTestReport.Result.Fail, e.GetType().Name + " " + e.Message));
                }
            }

            for (int i = 0; i < routines.Count; i++)
            {
                _selfTestStep = (i + 1) + " of " + routines.Count + ": " + routines[i].What;
                var run = Guarded(routines[i], checks);
                while (run.MoveNext())
                {
                    Vigil.Recorder.OwnTools.SelfTestFrames++;
                    _recorder.AddOwnTime(step.Elapsed.TotalMilliseconds);
                    yield return run.Current;
                    step.Restart();
                }
            }
        }
        finally
        {
            _selfTestRunning = false;
            _selfTestStep = null;
            Vigil.Recorder.OwnTools.SelfTestSeconds += whole.Elapsed.TotalSeconds;
        }

        var lines = WriteSelfTest("this game", checks);
        if (fromConsole)
        {
            Reply(string.Join("\n", lines));
        }

        _recorder.AddOwnTime(step.Elapsed.TotalMilliseconds);
    }

    /// <summary>A routine stepped frame by frame: one that throws or runs over its time becomes a check saying so.</summary>
    private static System.Collections.IEnumerator Guarded(SelfTestRoutine routine, List<SelfTestReport.Check> checks)
    {
        System.Collections.IEnumerator inner;
        try
        {
            inner = routine.Run(checks);
        }
        catch (Exception e)
        {
            checks.Add(new SelfTestReport.Check(routine.Area, routine.What, SelfTestReport.Result.Fail, e.GetType().Name + " " + e.Message));
            yield break;
        }

        float until = Time.realtimeSinceStartup + routine.Seconds;
        while (true)
        {
            object current;
            try
            {
                if (!inner.MoveNext())
                {
                    yield break;
                }

                current = inner.Current;
            }
            catch (Exception e)
            {
                checks.Add(new SelfTestReport.Check(routine.Area, routine.What, SelfTestReport.Result.Fail, e.GetType().Name + " " + e.Message));
                yield break;
            }

            if (Time.realtimeSinceStartup > until)
            {
                checks.Add(new SelfTestReport.Check(routine.Area, routine.What, SelfTestReport.Result.Warn,
                    "took longer than " + routine.Seconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s"));
                yield break;
            }

            yield return current;
        }
    }

    /// <summary>With the panel open: shows every tab for a few frames, and lists sections that failed to draw and IMGUI's own errors meanwhile.</summary>
    private System.Collections.IEnumerator PanelTabs(List<SelfTestReport.Check> checks)
    {
        const string what = "every panel tab draws";
        if (_panel == null || !Panel.IsOpen)
        {
            checks.Add(new SelfTestReport.Check(Name, what, SelfTestReport.Result.Info, "the panel was closed; run the self-test from it to check its tabs"));
            yield break;
        }

        var errors = new List<string>();
        void Tap(string message, string stack, LogType type)
        {
            if ((type == LogType.Error || type == LogType.Exception) && (message.Contains("GUI") || message.Contains("control")))
            {
                errors.Add(message.Split('\n')[0]);
            }
        }

        int start = _panel.TabIndex;
        _panel.DrawErrors.Clear();
        _panel.Collecting = true;
        Application.logMessageReceived += Tap;
        try
        {
            for (int t = 0; t < Panel.Tabs.Length; t++)
            {
                _panel.ShowTab(t);
                for (int f = 0; f < 4; f++)
                {
                    yield return null;
                }
            }

            _panel.ShowTab(start);
            yield return null;
        }
        finally
        {
            Application.logMessageReceived -= Tap;
            _panel.Collecting = false;
        }

        errors.AddRange(_panel.DrawErrors);
        checks.Add(errors.Count == 0
            ? new SelfTestReport.Check(Name, what, SelfTestReport.Result.Pass, Panel.Tabs.Length + " tabs")
            : new SelfTestReport.Check(Name, what, SelfTestReport.Result.Fail, string.Join("; ", errors.Distinct().Take(3).ToArray())));
    }

    private List<SelfTestReport.Check> InstallChecks()
    {
        var checks = new List<SelfTestReport.Check>();
        foreach (var m in _all)
        {
            var result = _failed.Contains(m) ? SelfTestReport.Result.Fail : _running.Contains(m) ? SelfTestReport.Result.Pass : SelfTestReport.Result.Info;
            string detail = _failed.Contains(m) ? "it failed; the log says why" : _running.Contains(m) ? null : IsServer && !m.RunsOnServer ? "not on a server" : "switched off";
            checks.Add(new SelfTestReport.Check(Name, m.Area + " running", result, detail));
        }

        if (!IsServer)
        {
            checks.Add(PanelInput.Problems.Count == 0
                ? new SelfTestReport.Check(Name, "the panel keeps keys and mouse from the game", SelfTestReport.Result.Pass, null)
                : new SelfTestReport.Check(Name, "the panel keeps keys and mouse from the game", SelfTestReport.Result.Warn, string.Join("; ", PanelInput.Problems)));
            checks.Add(!_hud.Shown
                ? new SelfTestReport.Check(Name, "HUD line", SelfTestReport.Result.Info, "switched off")
                : _hud.Made || Player.m_localPlayer == null
                    ? new SelfTestReport.Check(Name, "HUD line", SelfTestReport.Result.Pass, null)
                    : new SelfTestReport.Check(Name, "HUD line", SelfTestReport.Result.Warn, "the game's font is not there yet"));
            var server = Link.Server;
            checks.Add(new SelfTestReport.Check(Name, "the server's Vigil", server.State == GreetingState.Answered ? SelfTestReport.Result.Pass
                : server.State == GreetingState.NoAnswer ? SelfTestReport.Result.Warn : SelfTestReport.Result.Info, server.Describe()));
        }

        foreach (var m in _running)
        {
            Guarded(m, () => m.SelfTest(checks));
        }

        return checks;
    }

    private string AskServerSelfTest()
    {
        if (IsServer)
        {
            return string.Join("\n", RunSelfTest());
        }

        string lacking = Link.Server.Lacking(Greeting.SelfTest, "test itself", adminOnly: true);
        if (lacking != null)
        {
            return "Vigil: " + lacking;
        }

        Link.Ask("selftest", null, (ok, reply) =>
        {
            string text = reply.TryGetValue("text", out string t) ? t : ok ? "" : "failed";
            _serverSelfTest = SelfTestReport.Read(text.Split('\n').ToList());
            Reply(text);
        });
        return "Vigil: asking the server's Vigil to test itself";
    }

    // ------------------------------------------------------------------ panel

    /// <summary>The sections of one tab: each running area's, with Vigil's own on Overview last and on Health alone.</summary>
    internal IEnumerable<PanelSection> SectionsOn(string tab)
    {
        if (tab == "Health")
        {
            yield return new PanelSection(tab, "This game", DrawSelfTest, () => ReportBadge(_selfTest));
            if (!IsServer)
            {
                yield return new PanelSection(tab, "The server", DrawServerSelfTest, ServerReportBadge);
            }
        }

        foreach (var m in _running)
        {
            IEnumerable<PanelSection> sections;
            try
            {
                sections = m.Sections().Where(s => s.Tab == tab).ToList();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var s in sections)
            {
                yield return s;
            }
        }

        if (tab == "Overview")
        {
            yield return new PanelSection(tab, "Vigil", DrawVigil);
        }
    }

    private void DrawVigil()
    {
        Ui.Row("Running", _running.Count == 0 ? "nothing" : string.Join(", ", _running.Select(m => m.Area)));
        Ui.Row("The server", Link.Server.Describe());
        Ui.Row("Keys", "panel " + _panel.KeyName + ", lag report " + _recorder.MarkKeyName() + ", spawn marks " + _spawns.KeyName());
        Ui.BeginLine();
        if (Ui.Toggle(_hud.Shown, "Show the HUD line") != _hud.Shown)
        {
            _hud.Toggle();
        }

        Ui.EndLine();
    }

    private static PanelBadge ReportBadge(SelfTestReport.Shown shown) =>
        shown == null ? new PanelBadge("not run yet", Skin.Tone.Quiet) : new PanelBadge(shown.Summary, Ui.ToneOf(shown.Worst));

    private PanelBadge ServerReportBadge() =>
        Link.Server.Lacking(Greeting.SelfTest, "test itself", adminOnly: true) != null ? new PanelBadge("not available", Skin.Tone.Quiet) : ReportBadge(_serverSelfTest);

    private void DrawSelfTest()
    {
        if (_selfTestRunning)
        {
            Ui.Tool("Self-test running", _selfTestStep ?? "", "Running", enabled: false);
        }
        else
        {
            // A test that starts turns both rows into its one running row, so it starts before the
            // next layout pass rather than in the middle of this one.
            if (Ui.Tool("Quick self-test", "What Vigil put in, and each feature run once on what is around you; changes nothing", _selfTest == null ? "Run" : "Again", primary: true))
            {
                Ui.Later(() => StartSelfTest(full: false, fromConsole: false));
            }

            if (!IsServer && Ui.Tool("With the server", "Also asks the server, fetches one of its reports back, and sends a test report through sharing", "Run"))
            {
                Ui.Later(() => StartSelfTest(full: true, fromConsole: false));
            }
        }

        DrawReport(_selfTest);
    }

    private void DrawServerSelfTest()
    {
        string lacking = Link.Server.Lacking(Greeting.SelfTest, "test itself", adminOnly: true);
        if (lacking != null)
        {
            Ui.Note(char.ToUpperInvariant(lacking[0]) + lacking.Substring(1) + ". The server's Vigil tests itself for its admins.");
            return;
        }

        if (Ui.Tool("Test the server", "The server's Vigil checks itself and answers", _serverSelfTest == null ? "Run" : "Again"))
        {
            Show(AskServerSelfTest());
        }

        DrawReport(_serverSelfTest);
    }

    private static void DrawReport(SelfTestReport.Shown shown)
    {
        if (shown == null)
        {
            return;
        }

        Ui.Heading(shown.Checks.Count == 0 ? "Nothing was checked" : "Checks, what needs a look first");
        foreach (var check in shown.Checks)
        {
            Ui.CheckRow(check);
        }
    }

    // ------------------------------------------------------------------ Unity

    private void Update()
    {
        foreach (var m in _running)
        {
            long t = Ticks, a = PhaseTimer.AllocNow();
            Guarded(m, m.Update);
            Timed(m.Area, "Update", t, a, m.TakeTicksTimedElsewhere());
        }

        long linkAt = Ticks, linkAlloc = PhaseTimer.AllocNow();
        try
        {
            Link.Update();
        }
        catch (Exception e)
        {
            if (!_linkFailed)
            {
                _linkFailed = true;
                Logger.LogError("Vigil: the link to the server failed (logged once): " + e);
            }
        }

        Timed("Link", "Update", linkAt, linkAlloc, 0);

        if (IsServer)
        {
            return;
        }

        long panelAt = Ticks, panelAlloc = PhaseTimer.AllocNow();
        try
        {
            _panel.Update();
            bool opened = Panel.IsOpen && !_panelWasOpen;
            _panelWasOpen = Panel.IsOpen;
            if (_running.Contains(_recorder))
            {
                bool network = _panel.OpenTab == "Network";
                _recorder.WorldShown = _panel.OpenTab == "World";
                if (opened || Panel.TakeLookEnded()) _recorder.PanelOpened();
                _recorder.PollServer(_hud.Shown || network || _recorder.WantsPings, network ? 2 : 5);
                _hud.Update(_recorder.HudValues, _recorder.Limits);
            }
        }
        catch (Exception e)
        {
            if (!_uiFailed)
            {
                _uiFailed = true;
                Logger.LogError("Vigil: the panel or HUD line failed (logged once): " + e);
            }
        }

        Timed("Panel and HUD", "Update", panelAt, panelAlloc, 0);
    }

    private void LateUpdate()
    {
        if (_panel == null)
        {
            return;
        }

        long t = Ticks, a = PhaseTimer.AllocNow();
        _panel.LateUpdate();
        Timed("Panel", "LateUpdate", t, a, 0);
    }

    private void OnGUI()
    {
        if (_panel == null)
        {
            return;
        }

        long t = Ticks, a = PhaseTimer.AllocNow();
        _panel.OnGUI();
        Timed("Panel", "OnGUI", t, a, 0);
    }

    private void FixedUpdate()
    {
        foreach (var m in _running)
        {
            long t = Ticks, a = PhaseTimer.AllocNow();
            Guarded(m, m.FixedUpdate);
            Timed(m.Area, "FixedUpdate", t, a, 0);
        }
    }

    private static long Ticks => System.Diagnostics.Stopwatch.GetTimestamp();

    private readonly Dictionary<(string, string), string> _ownLabels = new();

    /// <summary>Adds one call of Vigil's own work to the per-mod timing, under a label made once per area and call.</summary>
    private void Timed(string area, string call, long start, long alloc, long timedElsewhere)
    {
        if (!_ownLabels.TryGetValue((area, call), out string label))
        {
            _ownLabels[(area, call)] = label = OwnTime.Label(area, call);
        }

        PhaseTimer.OwnCall(label, OwnTime.Counted(start, Ticks, timedElsewhere), PhaseTimer.AllocNow() - alloc);
    }

    private void OnApplicationQuit()
    {
        foreach (var m in _running)
        {
            Guarded(m, m.Quit);
        }
    }

    private void OnDestroy()
    {
        foreach (var m in _running)
        {
            Guarded(m, m.Destroy);
        }

        _hud?.Destroy();
    }

    /// <summary>Runs one of a module's calls; its first failure is logged once, never every frame.</summary>
    private void Guarded(Module module, Action call)
    {
        try
        {
            call();
        }
        catch (Exception e)
        {
            if (_failed.Add(module))
            {
                Logger.LogError("Vigil: " + module.Area + " failed (logged once): " + e);
            }
        }
    }

    private static string Safe(Module module, Func<string> answer)
    {
        try
        {
            return answer();
        }
        catch (Exception e)
        {
            return "Vigil: " + module.Area + " failed: " + e.GetType().Name + " " + e.Message;
        }
    }
}
