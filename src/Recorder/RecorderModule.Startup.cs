using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;
using Check = Vigil.Core.SelfTestReport.Check;
using Outcome = Vigil.Core.SelfTestReport.Result;

namespace Vigil.Recorder
{
    /// <summary>
    /// The Health tab's <em>Startup</em>, the startup report and "vigil startup". The patcher (Vigil.Preloader)
    /// times the start from before the first plugin, and keeps the bundle cache; Vigil adds the steps it
    /// sees itself, the menu and a join, and writes Vigil-startup.txt once the menu shows (on a dedicated
    /// server once the world is up), and again after each join.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private const string StartupReportName = "Vigil-startup.txt", StartupStepsName = "Vigil-startup.tsv";
        private static RecorderModule s_startupOwner;
        private static readonly Ui.Column[] PluginColumns = { new Ui.Column(null, 70, true), new Ui.Column(null, 0, false) };

        private StartupBridge _startup;
        private StartupReport _startupReport;
        private ConfigEntry<bool> _startupTimeline;
        private DateTime _processStart;
        private int _menuStartFrame = -1;
        private bool _menuSceneMarked, _startFinished, _joining, _startupShowAll;

        private void BindStartup(Harmony harmony)
        {
            _startupTimeline = Config.Bind(StartupSettings.Section, StartupSettings.Timeline, true, StartupSettings.TimelineText);
            Config.Bind(StartupSettings.Section, StartupSettings.Cache, true, StartupSettings.CacheText);
            Config.Bind(StartupSettings.Section, StartupSettings.CacheOnServer, false, StartupSettings.CacheOnServerText);
            Config.Bind(StartupSettings.Section, StartupSettings.KeepDays, BundleCache.DefaultKeepDays,
                new ConfigDescription(StartupSettings.KeepDaysText, new AcceptableValueRange<int>(1, 365)));
            Config.Bind(StartupSettings.Section, StartupSettings.Hold, true, StartupSettings.HoldText);
            Config.Bind(StartupSettings.Section, StartupSettings.HoldOnServer, false, StartupSettings.HoldOnServerText);
            _startup = StartupBridge.Find();
            s_startupOwner = this;
            try { _processStart = Process.GetCurrentProcess().StartTime; }
            catch (InvalidOperationException) { _processStart = DateTime.Now; }
            catch (System.ComponentModel.Win32Exception) { _processStart = DateTime.Now; }

            Patching.Apply(harmony, AccessTools.Method(typeof(FejdStartup), "Awake"), "the start timed to the menu",
                postfix: Patching.M(typeof(RecorderModule), nameof(MenuScenePostfix)));
            if (!_server)
            {
                Patching.Apply(harmony, AccessTools.Method(typeof(FejdStartup), "Start"), "the start timed to the menu",
                    postfix: Patching.M(typeof(RecorderModule), nameof(MenuStartPostfix)));
                EventLog.StepTimed = JoinStepTimed;
            }
            if (!_startup.Installed) _sessionNotes.Add("Vigil's patcher is not installed (BepInEx/patchers), so the start is not timed and bundles load the usual way.");
        }

        private static void MenuScenePostfix()
        {
            var m = s_startupOwner;
            if (m == null || m._menuSceneMarked) return;
            m._menuSceneMarked = true;
            m._startup.Mark(StartupLines.MenuScene);
        }

        private static void MenuStartPostfix()
        {
            var m = s_startupOwner;
            if (m == null || m._startFinished || m._menuStartFrame >= 0) return;
            m._startup.Mark(StartupLines.MenuStart);
            m._menuStartFrame = Time.frameCount;
        }

        // Called from the connection hook: on a player's game a new connection is the join to a server.
        private void JoinBegins()
        {
            if (_server || ZNet.instance == null || ZNet.instance.IsServer()) return;
            _joining = true;
            _startup.Mark(StartupLines.JoinStart);
            HandlerTime.Timed = JoinMessageTimed;
        }

        private static void JoinStepTimed(string step, long ms)
        {
            var m = s_startupOwner;
            if (m == null || !m._joining || step == null || !step.EndsWith("RPC_PeerInfo", StringComparison.Ordinal)) return;
            m._startup.Mark(StartupLines.JoinStep, step + "|" + ms.ToString(Ic));
        }

        // Only the slow ones are kept: a join handles thousands of small messages.
        private static void JoinMessageTimed(string label, double ms)
        {
            var m = s_startupOwner;
            if (m == null || !m._joining || ms < 100) return;
            m._startup.Mark(StartupLines.JoinMessage, label + "|" + ms.ToString("0", Ic));
        }

        /// <summary>Once a frame: the menu shown (the frame after FejdStartup.Start), a dedicated server's world up, a join arrived.</summary>
        private void UpdateStartup()
        {
            if (!_startFinished)
            {
                // Should the menu's hooks have gone in after the menu began (another mod holding patches back), the menu is
                // seen from its instance instead, at most a frame late.
                if (!_server && _menuStartFrame < 0 && FejdStartup.instance != null)
                {
                    if (!_menuSceneMarked) MenuScenePostfix();
                    MenuStartPostfix();
                }
                bool shown = !_server && _menuStartFrame >= 0 && Time.frameCount > _menuStartFrame;
                bool worldUp = _server && ZNet.instance != null && ZoneSystem.instance != null;
                if (shown || worldUp)
                {
                    _startup.Mark(shown ? StartupLines.MenuShown : StartupLines.WorldUp);
                    EndHeldSaves(shown ? "the menu showed" : "the world was up");
                    _startFinished = true;
                    _startup.StopTimeline();
                    _startup.SaveIndex();
                    WriteStartupReport();
                    if (_startup.Installed) _sessionNotes.Add("Startup: " + StartupTimeline.Summary(_startupReport));
                }
            }
            if (_joining && Player.m_localPlayer != null && WorldContext.Situation() == "in world")
            {
                _joining = false;
                HandlerTime.Timed = null;
                _startup.Mark(StartupLines.JoinWorld);
                _startup.SaveIndex();
                WriteStartupReport();
                _sessionNotes.Add("Join: " + StartupTimeline.JoinLine(_startupReport));
            }
        }

        /// <summary>
        /// Ends the patcher's hold of config saves, which writes each held file once. The config files
        /// were left out of the first frame's list (Vigil-configs.tsv) while held, so they are listed now.
        /// </summary>
        private void EndHeldSaves(string why)
        {
            if (_startup.ReleaseSaves(why) == null) return;
            try { SessionInfo.Write(_dir); }
            catch (Exception) { }
        }

        private void WriteStartupReport()
        {
            var events = _startup.Events();
            events.AddRange(StartSteps.Events());
            _startupReport = StartupTimeline.Build(events);
            try
            {
                var lines = new List<string>
                {
                    "Vigil startup report - game started " + _processStart.ToString("yyyy-MM-dd HH:mm:ss", Ic) + ", written " + DateTime.Now.ToString("HH:mm:ss", Ic),
                    _startup.Status + "; Unity's bundle loading: " + _startup.EditResult,
                    "",
                };
                if (_startup.Installed) lines.AddRange(StartupTimeline.Lines(_startupReport, 500));
                else lines.Add("Vigil's patcher is not installed, so the start is not timed. Join: " + StartupTimeline.JoinLine(_startupReport));
                File.WriteAllLines(Path.Combine(_dir, StartupReportName), lines.ToArray());
                File.WriteAllLines(Path.Combine(_dir, StartupStepsName), new[] { "ms\tkind\ttext" }.Concat(events.Select(e => e.Line())).ToArray());
            }
            catch (IOException e) { Logger.LogWarning("The startup report could not be written: " + e.Message); }
            catch (UnauthorizedAccessException e) { Logger.LogWarning("The startup report could not be written: " + e.Message); }
        }

        // ------------------------------------------------------------------ panel

        private PanelBadge StartupBadge()
        {
            string text = _startup != null && _startup.Installed ? StartupTimeline.Badge(_startupReport) : null;
            return text == null ? null : new PanelBadge(text, Skin.Tone.Quiet);
        }

        private void DrawStartup()
        {
            var r = _startupReport;
            if (!_startup.Installed) Ui.Row("Last start", "not timed: Vigil's patcher is not installed");
            else if (r == null) Ui.Row("Last start", "the start is not over yet");
            else
            {
                Ui.Row("Last start", StartupTimeline.Summary(r) + ", started " + _processStart.ToString("HH:mm:ss", Ic));
                Ui.BeginFormLine("Slowest plugins");
                Ui.Text(StartupTimeline.Slowest(r, 3));
                if (Ui.Quiet(_startupShowAll ? "Show fewer" : "Show all")) Ui.Later(() => _startupShowAll = !_startupShowAll);
                Ui.EndLine();
                if (_startupShowAll) Ui.Table(PluginColumns, StartupPluginRows(r));
            }
            Ui.Row("Last join", StartupTimeline.JoinLine(r));
            Ui.Row("Bundle cache", StartupCacheLine());
            if (Ui.Tool("Reset the cache", "Empties the bundle cache at the next start, before any bundle loads", "Reset", enabled: _startup.Installed && _startup.CacheOn))
            {
                _host.RunShown("startup reset");
            }
            if (_startup.Installed && _startup.CacheDir.Length > 0 && Ui.Quiet(PathClipboard.Label(_startup.CacheDir, "Copy the bundle cache's path"))) PathClipboard.Copy(_startup.CacheDir);
            Ui.Row("Config saves", StartupSavesLine());
            Ui.Note("A plugin's time runs from BepInEx's line that it is loading to the next plugin's: its assembly, its Awake and the bundles it loads. " +
                "An LZMA bundle has to be unpacked whole on every start; the cache keeps an LZ4 copy that loads by reading only what is used. " +
                "BepInEx rewrites a mod's whole settings file for each setting the mod binds; while the game starts, those saves are held and each file is written once when the menu shows. " +
                "Each start writes Vigil-startup.txt into the recordings folder. Switches: Startup in Vigil's config, read at the next start.");
        }

        private string StartupSavesLine()
        {
            if (!_startup.Installed) return "not held: Vigil's patcher is not installed";
            if (!_startup.HoldAsked) return _server ? "not held on this dedicated server (Startup.HoldConfigSavesOnServer)" : "not held (Startup.HoldConfigSaves)";
            var saves = _startupReport?.Saves;
            if (saves == null) return _startup.HoldStatus;
            return string.Join("; ", saves.Lines().Select(l => l.StartsWith(SavesPrefix, StringComparison.Ordinal) ? l.Substring(SavesPrefix.Length) : l).ToArray());
        }

        private const string SavesPrefix = "Config saves: ";

        private static IEnumerable<string[]> StartupPluginRows(StartupReport r) =>
            r.Plugins.OrderByDescending(p => p.Ms).Select(p => new[]
            {
                StartupTimeline.Duration(p.Ms),
                p.Name + (p.Bundles == 0 ? "" : string.Format(CultureInfo.InvariantCulture, "   {0} bundle{1}, {2:0.0} MB, {3}", p.Bundles, p.Bundles == 1 ? "" : "s", p.BundleBytes / 1e6, StartupTimeline.Duration(p.BundleMs))),
            });

        private string StartupCacheLine()
        {
            if (!_startup.Installed) return "off: Vigil's patcher is not installed";
            if (!_startup.CacheOn) return _server ? "off on this dedicated server (Startup.BundleCacheOnServer)" : "off (Startup.BundleCache)";
            if (_startup.EditResult != "edited") return "not working, bundles load the usual way: " + _startup.EditResult;
            var c = _startup.Counts;
            string line = _startupReport != null ? StartupTimeline.BundleLine(_startupReport) : "";
            if (c[4] > 0) line += string.Format(Ic, ", {0} cop{1} being made", c[4], c[4] == 1 ? "y" : "ies");
            line += (line.Length > 0 ? "; " : "") + BundleCache.DiskLine(_startup.Entries());
            if (_startup.ResetRequested) line += "; reset asked for, done at the next start";
            return line;
        }

        // ------------------------------------------------------------------ console

        private string StartupCommand(string rest)
        {
            if (rest.Trim().Equals("reset", StringComparison.OrdinalIgnoreCase)) return _startup.RequestReset();
            var r = _startupReport ?? StartupTimeline.Build(_startup.Events());
            var lines = new List<string>
            {
                _startup.Installed ? StartupTimeline.Summary(r) : "the start is not timed: Vigil's patcher is not installed",
            };
            if (_startup.Installed && r.Plugins.Count > 0) lines.Add("slowest plugins: " + StartupTimeline.Slowest(r, 5));
            lines.Add("join: " + StartupTimeline.JoinLine(r));
            lines.Add("bundle cache: " + StartupCacheLine());
            lines.Add("config saves: " + StartupSavesLine());
            lines.Add("report: " + Path.Combine(_dir, StartupReportName));
            return string.Join("\n", lines.ToArray());
        }

        // ------------------------------------------------------------------ self-test

        private void StartupChecks(List<Check> checks)
        {
            if (!_startup.Installed)
            {
                checks.Add(new Check(Here, "Vigil's patcher timed this start", Outcome.Warn, "not installed: BepInEx/patchers/isimp-Vigil/Vigil.Preloader.dll is missing"));
                return;
            }

            var r = _startupReport ?? StartupTimeline.Build(_startup.Events());
            checks.Add(!_startup.TimelineOn ? new Check(Here, "Vigil's patcher timed this start", Outcome.Info, "the timeline is off (Startup.Timeline)")
                : r.Plugins.Count == 0 ? new Check(Here, "Vigil's patcher timed this start", Outcome.Fail, "no plugin's Loading line was heard")
                : new Check(Here, "Vigil's patcher timed this start", Outcome.Pass, r.Plugins.Count.ToString(Ic) + " plugins timed"));
            checks.Add(!r.Finished ? new Check(Here, "the startup report covers the whole start", Outcome.Info, "the start is not over yet")
                : r.EngineMs <= 0 || r.Plugins.Count == 0 ? new Check(Here, "the startup report covers the whole start", Outcome.Warn, StartupTimeline.Summary(r))
                : new Check(Here, "the startup report covers the whole start", Outcome.Pass, StartupTimeline.Summary(r)));

            const string hook = "the bundle cache's hook in Unity's bundle loading";
            var c = _startup.Counts;
            if (!_startup.CacheOn) checks.Add(new Check(Here, hook, Outcome.Info, StartupCacheLine()));
            else if (_startup.EditResult != "edited") checks.Add(new Check(Here, hook, Outcome.Fail, _startup.EditResult));
            else checks.Add(c[0] > 0 ? new Check(Here, hook, Outcome.Pass, c[0].ToString(Ic) + " bundle loads asked the cache this start")
                : new Check(Here, hook, Outcome.Warn, "in place, but no bundle load asked the cache this start"));

            if (_startup.CacheOn && _startup.EditResult == "edited")
            {
                var entries = _startup.Entries().ToDictionary(e => e.Key, StringComparer.Ordinal);
                var usual = _startup.Events().Where(e => e.Kind == StartupLines.Bundle).Select(e => e.Text.Split('|'))
                    .Where(p => p.Length >= 4 && p[0] == BundleOutcome.Usual && p[3].Length > 0).Select(p => p[3]).Distinct().ToList();
                int running = c[4];
                var left = usual.Where(k => !entries.ContainsKey(k)).ToList();
                const string made = "every LZMA bundle of this start loaded from a fast copy or got one made";
                checks.Add(left.Count == 0 ? new Check(Here, made, Outcome.Pass, string.Format(Ic, "{0} from copies, {1} copies made this start", r.FromCopies, usual.Count))
                    : left.Count <= running ? new Check(Here, made, Outcome.Info, string.Format(Ic, "{0} cop{1} still being made", left.Count, left.Count == 1 ? "y" : "ies"))
                    : new Check(Here, made, Outcome.Warn, string.Format(Ic, "{0} without a copy or a reason (see the log's Bundle cache lines)", left.Count - running)));
                const string same = "each fast copy loaded this start matched its original's assets";
                checks.Add(c[6] == 0 ? new Check(Here, same, Outcome.Pass, c[5].ToString(Ic) + " copies loaded, none refused")
                    : new Check(Here, same, Outcome.Fail, c[6].ToString(Ic) + " refused (they load the usual way from now on)"));
            }

            const string reset = "a cache reset asked for is done at the next start";
            checks.Add(_startup.ResetRequested ? new Check(Here, reset, Outcome.Info, "asked for; done at the next start")
                : _startup.ResetDone ? new Check(Here, reset, Outcome.Pass, "this start emptied the cache as asked (" + _startup.HousekeepingResult + ")")
                : new Check(Here, reset, Outcome.Info, "no reset asked for"));

            checks.Add(SavesCheck(r.Saves));
        }

        // The held config saves of this start: each file written once, and right after, every written file read back
        // the way BepInEx reads it held the value of each of its mod's settings.
        private Check SavesCheck(SaveHoldResult saves)
        {
            const string what = "config saves held this start were written, each file holding its settings";
            if (!_startup.HoldAsked) return new Check(Here, what, Outcome.Info, StartupSavesLine());
            if (saves == null)
            {
                // Asked for but never put in (the hook failed), or still on: the start is not over.
                bool holding = _startup.HoldStatus.StartsWith("config saves held", StringComparison.Ordinal);
                return new Check(Here, what, holding ? Outcome.Info : Outcome.Fail, _startup.HoldStatus);
            }

            var check = _startup.SaveCheck;
            if (saves.Failed.Count > 0) return new Check(Here, what, Outcome.Fail, saves.Failed.Count.ToString(Ic) + " not written: " + string.Join(", ", saves.Failed));
            if (check.Length > 2) return new Check(Here, what, Outcome.Fail, "read back, not as set: " + string.Join(", ", check.Skip(2)));
            string read = check.Length == 2 ? string.Format(Ic, "; read back: {0} files, {1} settings, each as set", check[0], check[1]) : "";
            string kept = saves.Kept.Count > 0 ? "; kept as another mod wrote them: " + string.Join(", ", saves.Kept) : "";
            string past = saves.ReadPast.Count > 0 ? "; read past BepInEx, so written first: " + string.Join(", ", saves.ReadPast) : "";
            string summary = string.Format(Ic, "{0:N0} held for {1:N0} files, written when {2}", saves.Held, saves.Files, saves.Why) + read + kept + past;
            string guard = _startup.GuardProblem;
            return guard.Length > 0
                ? new Check(Here, what, Outcome.Warn, summary + "; mods' own reads of held files were not watched: " + guard)
                : new Check(Here, what, Outcome.Pass, summary);
        }
    }
}
