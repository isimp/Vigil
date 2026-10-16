using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using Vigil.Core;
using Check = Vigil.Core.SelfTestReport.Check;
using Outcome = Vigil.Core.SelfTestReport.Result;

namespace Vigil.Recorder
{
    /// <summary>
    /// Alerts for the server's admins. The server's Vigil turns what it sees into alerts
    /// (<see cref="AlertSources"/>): a player dropping without a goodbye, a connection struggling,
    /// the server falling behind or short of memory, its log flooding, spawns inside bases from the
    /// shared spawn logs, a report a player shared, and a start after a run that ended without a
    /// shutdown. It keeps a week of them in Vigil-state/alerts.tsv (<see cref="AlertBook"/>). An
    /// admin's game asks for the new ones every 10 s, shows the kinds set on screen as a line at the
    /// top left, counts the new ones in the HUD line, and sums them up in the Overview tab's Alerts
    /// card. What it opened and cleared there it keeps per world in Vigil-state/alerts-seen.tsv.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private const double AlertsEverySeconds = 10, HeartbeatSeconds = 60;
        private const int AlertEntriesShown = 20;
        private const float AlertSettingWidth = 230;
        private static readonly string[] AlertShows = { "On screen", "Count", "Off" };
        private static readonly Ui.Column[] AlertSettingsColumns = { new Ui.Column(null, 0, false) };

        // The server's side.
        private readonly AlertBook _alertBook = new AlertBook();
        private ConfigEntry<int> _alertLongFrameMs;
        private double _heartbeatAt;

        // An admin's side.
        private readonly Dictionary<AlertKind, ConfigEntry<AlertShow>> _alertShow = new Dictionary<AlertKind, ConfigEntry<AlertShow>>();
        private readonly List<Alert> _alerts = new List<Alert>();
        private long _alertsKnown;
        private string _alertWorld, _alertsError;
        private DateTime? _alertKeptFrom;
        private bool _alertsAsking, _alertsLinked, _alertsFirst = true, _alertsAnswered, _alertViewDirty = true, _alertSettingsOpen, _alertSettingsOpenShown;
        private double _alertsAskedAt = double.NegativeInfinity, _alertViewAt;
        private AlertMarks _alertMarks = new AlertMarks();
        private AlertView _alertView, _alertViewShown;
        private readonly HashSet<AlertKind> _alertOpen = new HashSet<AlertKind>();
        private HashSet<AlertKind> _alertOpenShown = new HashSet<AlertKind>();

        private string AlertsFile => Path.Combine(Path.Combine(_dir, "Vigil-state"), "alerts.tsv");

        private string AlertMarksFile => Path.Combine(Path.Combine(_dir, "Vigil-state"), "alerts-seen.tsv");

        private void BindAlerts()
        {
            foreach (var kind in AlertKinds.All)
            {
                var entry = Config.Bind("Alerts", AlertKinds.ConfigKey(kind), AlertKinds.Default(kind),
                    "For a server's admins: how this game tells of " + Lower(AlertKinds.Setting(kind)) + ". OnScreen: a line at the top left and the count " +
                    "in the HUD line. Count: the count only. Off: neither, and the Overview tab's Alerts card leaves it out.");
                entry.SettingChanged += (_, __) => _alertViewDirty = true;
                _alertShow[kind] = entry;
            }
            _alertLongFrameMs = Config.Bind("Alerts", "LongFrameMs", (int)AlertSources.LongFrameMs,
                "On a dedicated server: a frame at least this long tells its admins the server fell behind (at most once in ten minutes). " +
                "Saving the world stands still a few hundred milliseconds by design.");
            if (_server) StartAlertBook();
            else
            {
                try { if (File.Exists(AlertMarksFile)) _alertMarks = AlertMarks.Parse(File.ReadAllText(AlertMarksFile)); } catch (Exception) { }
            }
        }

        private static string Lower(string s) => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

        private AlertShow ShowOf(AlertKind kind) => _alertShow.TryGetValue(kind, out var e) ? e.Value : AlertKinds.Default(kind);

        // ------------------------------------------------------------------ the server's side

        private void StartAlertBook()
        {
            try { if (File.Exists(AlertsFile)) _alertBook.Load(File.ReadAllLines(AlertsFile), DateTime.UtcNow); } catch (Exception) { }
            if (!_journal.PreviousEndedCleanly) AddAlert(AlertSources.FromCrash(_journal.PreviousStarted, _journal.PreviousLastSeen, DateTime.Now, DateTime.UtcNow));
            _recorder.IncidentSeen = (kind, detail, file, who) =>
            {
                // A load test's own ping and backlog are made on purpose.
                if (!_load.Running) AddAlert(AlertSources.FromIncident(kind, detail, file, who, DateTime.UtcNow, _alertLongFrameMs.Value));
            };
        }

        /// <summary>Keeps an alert the book takes, writes the book's file and names it in the server's log.</summary>
        private void AddAlert(Alert alert)
        {
            if (!_server || alert == null || !_alertBook.Add(alert)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AlertsFile));
                File.WriteAllLines(AlertsFile, _alertBook.All.Select(AlertBook.Line).ToArray());
            }
            catch (Exception) { }
            Logger.LogInfo("Vigil alert: " + alert.Text);
        }

        private void AddAlerts(IEnumerable<Alert> alerts)
        {
            foreach (var a in alerts) AddAlert(a);
        }

        private Dictionary<string, object> AlertsReply(Dictionary<string, string> args)
        {
            double since = KeyValues.Number(args, "since");
            string runsDir = Path.Combine(_dir, RunArchive.FolderName);
            string[] runs;
            try { runs = Directory.Exists(runsDir) ? Directory.GetDirectories(runsDir).Select(Path.GetFileName).OrderByDescending(n => n, StringComparer.Ordinal).ToArray() : new string[0]; }
            catch (Exception) { runs = new string[0]; }
            bool Exists(string path) => File.Exists(Path.Combine(_dir, path.Replace('/', Path.DirectorySeparatorChar)));
            var sent = _alertBook.Since(double.IsNaN(since) ? 0 : (long)since).Select(a =>
            {
                var copy = a.Copy();
                copy.File = AlertSources.ReportPath(a.File, Exists, runs);
                return copy;
            });
            var reply = AlertBook.Encode(sent);
            reply["world"] = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
            if (_alertBook.OldestUtc.HasValue) reply["kept_from"] = new DateTimeOffset(DateTime.SpecifyKind(_alertBook.OldestUtc.Value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            return reply;
        }

        // ------------------------------------------------------------------ an admin's side

        /// <summary>Every frame: the running note's heartbeat, and on an admin's game the question for new alerts every 10 s.</summary>
        private void UpdateAlerts()
        {
            if (Now >= _heartbeatAt)
            {
                _heartbeatAt = Now + HeartbeatSeconds;
                _journal.Heartbeat();
            }
            if (_server) return;
            bool linked = _host.Link.Server.Lacking(Greeting.Alerts, "tell its admins of alerts", adminOnly: true) == null;
            if (linked != _alertsLinked)
            {
                // A new connection reads the server's week again, so another world never mixes in.
                _alertsLinked = linked;
                _alertsFirst = true;
                _alertsAnswered = false;
                _alertsKnown = 0;
                _alerts.Clear();
                _alertsAskedAt = double.NegativeInfinity;
                _alertViewDirty = true;
            }
            if (!linked || _alertsAsking || Now - _alertsAskedAt < AlertsEverySeconds) return;
            _alertsAsking = true;
            _alertsAskedAt = Now;
            _host.Link.Ask(Greeting.Alerts, new Dictionary<string, object> { { "since", _alertsKnown } }, (ok, reply) =>
            {
                _alertsAsking = false;
                if (!ok)
                {
                    _alertsError = reply != null && reply.TryGetValue("text", out string t) ? t : "the server did not answer";
                    return;
                }
                _alertsError = null;
                OnAlerts(reply);
            });
        }

        private void OnAlerts(Dictionary<string, string> reply)
        {
            _alertWorld = reply.TryGetValue("world", out string w) && !string.IsNullOrEmpty(w) ? w : "server";
            double kept = KeyValues.Number(reply, "kept_from");
            _alertKeptFrom = double.IsNaN(kept) ? (DateTime?)null : AlertBook.TimeOf((long)kept);
            bool first = _alertsFirst;
            _alertsFirst = false;
            _alertsAnswered = true;
            _alertViewDirty = true;
            var arrived = AlertBook.Decode(reply).Where(a => a.Id > _alertsKnown).OrderBy(a => a.Id).ToList();
            if (arrived.Count == 0) return;
            _alerts.AddRange(arrived);
            if (_alerts.Count > AlertBook.Kept) _alerts.RemoveRange(0, _alerts.Count - AlertBook.Kept);
            _alertsKnown = arrived[arrived.Count - 1].Id;
            long opened = _alertMarks.Opened(_alertWorld), cleared = _alertMarks.Cleared(_alertWorld);
            var fresh = arrived.Where(a => a.Id > opened && a.Id > cleared).ToList();
            foreach (var line in AlertNotice.Lines(fresh, first, ShowOf))
            {
                try { MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, line); } catch (Exception) { }
            }
        }

        /// <summary>The card's summing up, built again when something changed and at least every 30 s for its times.</summary>
        private AlertView AlertViewNow()
        {
            if (_alertView == null || _alertViewDirty || Now - _alertViewAt > 30)
            {
                _alertViewDirty = false;
                _alertViewAt = Now;
                _alertView = AlertView.Build(_alerts, _alertMarks.Cleared(_alertWorld), _alertMarks.Opened(_alertWorld), _alertKeptFrom, ShowOf,
                    DateTime.UtcNow, TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow));
            }
            return _alertView;
        }

        /// <summary>The HUD line's count: the new alerts and the worst of them.</summary>
        private void AlertHud(HudLine.Values v)
        {
            if (_server || !_alertsLinked || !_alertsAnswered) return;
            var view = AlertViewNow();
            v.Alerts = view.New;
            v.AlertWorst = view.NewLevel;
        }

        private void SaveAlertMarks()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AlertMarksFile));
                File.WriteAllText(AlertMarksFile, _alertMarks.Format());
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ the card

        private PanelBadge AlertsBadge()
        {
            if (!_alertsLinked || !_alertsAnswered) return null;
            var v = AlertViewNow();
            if (v.New > 0) return new PanelBadge(v.New + " new", v.NewLevel == AlertLevel.Bad ? Skin.Tone.Bad : v.NewLevel == AlertLevel.Warn ? Skin.Tone.Caution : Skin.Tone.Accent);
            return v.Calm ? new PanelBadge("calm", Skin.Tone.Good) : null;
        }

        private static ResultTone ToneOf(AlertLevel level) => level == AlertLevel.Bad ? ResultTone.Bad : level == AlertLevel.Warn ? ResultTone.Caution : ResultTone.Quiet;

        private static Color DotOf(AlertLevel level) => level == AlertLevel.Bad ? Skin.Bad : level == AlertLevel.Warn ? Skin.Caution : Skin.Accent;

        private void DrawAlerts()
        {
            string lacking = _host.Link.Server.Lacking(Greeting.Alerts, "tell its admins of alerts", adminOnly: true);
            if (lacking != null)
            {
                Ui.Note(Sentence(lacking) + " The server's admins hear here of drops, the server falling behind or short of memory, crashes and more, when it runs Vigil 0.20 or later.");
                return;
            }

            // Read on a layout pass only, so the pass that draws the card draws what was laid out.
            if (Event.current.type == EventType.Layout)
            {
                if (_alertsAnswered && _alertsKnown > _alertMarks.Opened(_alertWorld))
                {
                    // The card is open: what it shows now is no longer new.
                    _alertMarks.SetOpened(_alertWorld, _alertsKnown);
                    SaveAlertMarks();
                    _alertViewDirty = true;
                }
                _alertViewShown = _alertsAnswered ? AlertViewNow() : null;
                _alertOpenShown = new HashSet<AlertKind>(_alertOpen);
                _alertSettingsOpenShown = _alertSettingsOpen;
            }

            var v = _alertViewShown;
            if (v == null)
            {
                Ui.Note(_alertsError != null ? Sentence(_alertsError) : "Asking the server ...");
                return;
            }

            Ui.Verdict(v.Calm ? ResultTone.Good : ToneOf(v.VerdictLevel), v.Verdict);
            foreach (var g in v.Groups)
            {
                bool open = _alertOpenShown.Contains(g.Kind);
                if (Ui.ExpandLine(DotOf(g.Level), g.Name, g.Count == 1 ? "1 alert" : g.Count + " alerts", "last " + g.Last, open))
                {
                    var kind = g.Kind;
                    Ui.Later(() => { if (!_alertOpen.Remove(kind)) _alertOpen.Add(kind); });
                }
                if (!open) continue;
                foreach (var a in g.Entries.Take(AlertEntriesShown)) DrawAlert(a);
                if (g.Count > AlertEntriesShown) Ui.Note("And " + (g.Count - AlertEntriesShown) + " older ones.");
            }
            if (v.Quiet != null) Ui.Verdict(ResultTone.Good, v.Quiet);

            if (Ui.ExpandRow(AlertSettingsColumns, new[] { "What to tell me" }, _alertSettingsOpenShown)) Ui.Later(() => _alertSettingsOpen = !_alertSettingsOpen);
            if (_alertSettingsOpenShown)
            {
                foreach (var kind in AlertKinds.All)
                {
                    int now = (int)ShowOf(kind);
                    Ui.BeginFormLine(AlertKinds.Setting(kind), AlertSettingWidth);
                    int picked = Ui.Choice(now, AlertShows);
                    Ui.EndLine();
                    if (picked >= 0 && picked != now)
                    {
                        // Off takes a kind's line off the card, above this row.
                        var k = kind;
                        var to = (AlertShow)picked;
                        Ui.Later(() => { _alertShow[k].Value = to; _alertViewDirty = true; });
                    }
                }
            }

            Ui.BeginLine();
            Ui.Note(Upper(v.Since) + "; asked every 10 s.");
            GUILayout.FlexibleSpace();
            if (Ui.Quiet("Seen"))
            {
                Ui.Later(() =>
                {
                    _alertMarks.SetCleared(_alertWorld, _alertsKnown);
                    SaveAlertMarks();
                    _alertViewDirty = true;
                });
            }
            Ui.EndLine();
        }

        /// <summary>One alert under its kind's line: its report to get and open, the card it belongs to, or only its words.</summary>
        private void DrawAlert(Alert a)
        {
            string when = AlertView.When(a.Utc, DateTime.UtcNow, TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow));
            if (a.File != null)
            {
                bool here = _fetchedTo.TryGetValue(a.File, out string local) && File.Exists(local);
                bool getting = _fetching != null && _fetching.Path == a.File;
                if (getting) Ui.Tool(when, a.Text, "Getting", enabled: false);
                else if (Ui.Tool(when, a.Text, here ? PathClipboard.Label(local) : "Get", enabled: here || _fetching == null))
                {
                    if (here) PathClipboard.Copy(local);
                    else StartFetch(new ReportCatalog.Entry { Path = a.File });
                }
                return;
            }
            string tab = a.Kind == AlertKind.BaseSpawns ? "World" : "Network";
            string card = a.Kind == AlertKind.BaseSpawns ? "Spawns across the server" : a.Kind == AlertKind.Drop || a.Kind == AlertKind.Connection ? "The server" : null;
            if (card == null) Ui.Row(when, a.Text);
            else if (Ui.Tool(when, a.Text, "Show")) Ui.Later(() => _host.Reveal(tab, card));
        }

        // ------------------------------------------------------------------ self-test

        private IEnumerator AlertsOnServer(List<Check> checks)
        {
            const string what = "the server's alerts";
            if (!CanAsk(checks, what, Greeting.Alerts, adminOnly: true)) yield break;
            var a = Ask(Greeting.Alerts, new Dictionary<string, object> { { "since", 0 } });
            while (!a.Done) yield return null;
            if (!a.Ok)
            {
                checks.Add(new Check(Here, what, Outcome.Fail, Why(a)));
                yield break;
            }
            var alerts = AlertBook.Decode(a.Reply);
            string newest = alerts.Count > 0 ? "; the newest: " + alerts[alerts.Count - 1].Text : "";
            checks.Add(new Check(Here, what, SelfTestRoutines.Judge(true, a.Seconds),
                alerts.Count + (alerts.Count == 1 ? " alert" : " alerts") + " kept for the last week" + newest));
        }
    }
}
