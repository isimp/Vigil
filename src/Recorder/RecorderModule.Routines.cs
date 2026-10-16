using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Vigil.Core;
using Check = Vigil.Core.SelfTestReport.Check;
using Outcome = Vigil.Core.SelfTestReport.Result;

namespace Vigil.Recorder
{
    /// <summary>
    /// The recorder's self-test routines. The quick tier runs each feature once on what is around
    /// the player and reads only. The full tier asks the server's Vigil: its status, the player's own
    /// last minute, its reports with one fetched back whole, its mods and configs, and, with sharing
    /// on, sends a small test report and fetches it back from the player's folder there.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private const string Here = "Recorder";
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        public override IEnumerable<SelfTestRoutine> Routines(bool full)
        {
            if (_server) yield break;
            yield return new SelfTestRoutine(Here, "the recorder's last second", 2f, LastSecond);
            yield return new SelfTestRoutine(Here, "the server connection's last seconds kept", 2f, ConnectionSeconds);
            yield return new SelfTestRoutine(Here, "whose game runs what, within 16 m", 3f, OwnershipOnce);
            yield return new SelfTestRoutine(Here, "what is under the crosshair, for the label", 1f, LookOnce);
            yield return new SelfTestRoutine(Here, "the inspector reads what is under the crosshair", 5f, InspectOnce);
            yield return new SelfTestRoutine(Here, "the value names from the game and the mods", 30f, NamesOnce);
            yield return new SelfTestRoutine(Here, "the busiest objects within 16 m, watched 2 s", 5f, BusiestOnce);
            yield return new SelfTestRoutine(Here, "the weather and why", 3f, WeatherOnce);
            yield return new SelfTestRoutine(Here, "what could spawn at the spot under the crosshair", 3f, SpawnableOnce);
            yield return new SelfTestRoutine(Here, "config fingerprints the same twice", 10f, FingerprintsTwice);
            yield return new SelfTestRoutine(Here, "the share queue's file", 1f, ShareQueueWritable);
            if (!full) yield break;
            yield return new SelfTestRoutine(Here, "the server's status", 12f, StatusAnswered);
            yield return new SelfTestRoutine(Here, "your last minute as the server keeps it", 12f, OwnLinkOnServer);
            yield return new SelfTestRoutine(Here, "a server report fetched back whole", 60f, ReportFetchedBack);
            yield return new SelfTestRoutine(Here, "mods and configs against the server", 20f, ParityOnce);
            yield return new SelfTestRoutine(Here, "everyone's spawn logs on the server", 12f, SpawnViewOnServer);
            yield return new SelfTestRoutine(Here, "the server's reading of the whole world", 12f, LeftoversOnServer);
            yield return new SelfTestRoutine(Here, "the server's alerts", 12f, AlertsOnServer);
            yield return new SelfTestRoutine(Here, "a shared report reaching the server", 90f, SharedRoundTrip);
        }

        // ------------------------------------------------------------------ quick

        private IEnumerator LastSecond(List<Check> checks)
        {
            const string what = "the recorder's last second";
            if (!ProbeState.Active) checks.Add(new Check(Here, what, Outcome.Info, "not recording now (" + RecordingStatus() + ")"));
            else
            {
                double age = UnityEngine.Time.realtimeSinceStartup - _recorder.LastAt;
                checks.Add(_recorder.Last != null && age < 3
                    ? new Check(Here, what, Outcome.Pass, age.ToString("0.0", Ic) + " s ago")
                    : new Check(Here, what, Outcome.Fail, _recorder.Last == null ? "no second recorded yet" : "the last one is " + age.ToString("0", Ic) + " s old"));
            }
            yield break;
        }

        private IEnumerator ConnectionSeconds(List<Check> checks)
        {
            const string what = "the server connection's last seconds kept";
            var server = ZNet.instance?.GetServerPeer();
            if (server == null) checks.Add(new Check(Here, what, Outcome.Info, "not connected to a server"));
            else if (!ProbeState.Active) checks.Add(new Check(Here, what, Outcome.Info, "kept only while recording"));
            else
            {
                int n = DropWatch.Recent(server.m_uid).Length;
                checks.Add(n >= 3 ? new Check(Here, what, Outcome.Pass, n + " s, for drops and the link view")
                    : new Check(Here, what, Outcome.Warn, n + " s so far; join for a few seconds and run again"));
            }
            yield break;
        }

        private IEnumerator OwnershipOnce(List<Check> checks)
        {
            const string what = "whose game runs what, within 16 m";
            if (Player.m_localPlayer == null)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "not in a world"));
                yield break;
            }
            _ownership.Refresh(16f, Now, _markKinds.Value);
            int objects = _ownership.Rows.Sum(r => r.Total);
            checks.Add(new Check(Here, what, Outcome.Pass, _ownership.Rows.Count + (_ownership.Rows.Count == 1 ? " game, " : " games, ") + objects + " objects"));
        }

        private IEnumerator InspectOnce(List<Check> checks)
        {
            const string what = "the inspector reads what is under the crosshair";
            if (Player.m_localPlayer == null)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "not in a world"));
                yield break;
            }
            var probe = new ObjectInspector();
            probe.Pick(_look.Pick(), Now);
            if (!probe.Picked)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "nothing with an owner there; look at a creature, cart or piece and run it again"));
                yield break;
            }
            var facts = probe.Read(_ownership, PingOf, Limits.PingMs);
            checks.Add(facts == null
                ? new Check(Here, what, Outcome.Warn, "it was gone before it could be read")
                : new Check(Here, what, Outcome.Pass, facts.Name + ": " + facts.Values.Count + " stored values, " + facts.SizeBytes + " bytes, scripts " + ScriptsByMod.Summary(facts.Scripts)));
        }

        private IEnumerator BusiestOnce(List<Check> checks)
        {
            const string what = "the busiest objects within 16 m, watched 2 s";
            if (Player.m_localPlayer == null)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "not in a world"));
                yield break;
            }
            var first = BusiestFirst(16f);
            double from = Now;
            yield return new UnityEngine.WaitForSeconds(2f);
            if (Player.m_localPlayer == null)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "left the world meanwhile"));
                yield break;
            }
            var report = BusiestObjects.Rank(first, BusiestSecond(16f, first), Now - from);
            checks.Add(new Check(Here, what, Outcome.Pass, string.Format(Ic, "{0} watched, {1} changed, about {2:0.0} kB/s at most", report.Watched, report.Changed, report.TotalBytesPerSecond / 1024.0)));
        }

        private IEnumerator WeatherOnce(List<Check> checks)
        {
            const string what = "the weather and why";
            checks.Add(WeatherWatch.HookProblem == null
                ? new Check(Here, "the fog hook", Outcome.Pass, "in place; limiter " + (WeatherWatch.Limit.HasValue ? WeatherWatch.Limit.Value.ToString("0.###", Ic) + (WeatherWatch.LimitFromServer ? " (the server's)" : "") : "off"))
                : new Check(Here, "the fog hook", Outcome.Fail, WeatherWatch.HookProblem + "; the fog limiter and setting the fog do nothing"));
            long dayLength = EnvMan.instance != null ? EnvMan.instance.m_dayLengthSec : 1200;
            checks.Add(ServerWeatherPatch.Problem == null
                ? new Check(Here, "the server's weather hooks", Outcome.Pass, "in place; now " + ServerWeatherPatch.State.NowLine(dayLength))
                : new Check(Here, "the server's weather hooks", Outcome.Fail, ServerWeatherPatch.Problem + "; this game rolls its own weather whatever the server holds"));
            checks.Add(new Check(Here, "the server's fog limit, through ServerSync", Outcome.Pass,
                (_limitFromServer ? "the server's " + _serverLimitText + " is in charge" : "the server sets none; your own limiter counts")
                + (_host.Sync.IsSourceOfTruth ? "; this game decides it (no server running Vigil)" : "; taken from the server")));
            var s = Player.m_localPlayer != null ? WeatherWatch.Read(withMods: true) : null;
            if (s == null)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "not in a world"));
                yield break;
            }
            checks.Add(new Check(Here, what, Outcome.Pass, s.Weather + " in " + s.Biome + ": " + s.Cause.Words + "; " + s.List.Count + " in the list, " + s.Mods.Count + " mods on weather and light"));
            var report = WeatherWatch.Report(s, Player.m_localPlayer.GetPlayerName());
            var back = EnvironmentReport.Parse(report.Lines());
            int differ = back == null ? -1 : EnvironmentReport.Compare(report, back).Count(r => r.Differs);
            checks.Add(differ == 0
                ? new Check(Here, "an environment report reads back", Outcome.Pass, report.Values.Count + " values")
                : new Check(Here, "an environment report reads back", Outcome.Fail, back == null ? "it was not read as one" : differ + " values came back different"));
        }

        private IEnumerator NamesOnce(List<Check> checks)
        {
            const string what = "the value names from the game and the mods";
            if (ObjectInspector.Loaded == null && !ObjectInspector.NamesReading) ObjectInspector.ReadNames();
            double until = Now + 25;
            while (ObjectInspector.NamesReading && Now < until) yield return null;
            var names = ObjectInspector.Loaded;
            if (names == null)
            {
                checks.Add(new Check(Here, what, Outcome.Warn, ObjectInspector.NamesReading ? "still reading after 25 s" : ObjectInspector.NamesNote));
                yield break;
            }
            // Vigil's own load test marks objects under "vigil_load": found by name, the mods' code is being read.
            var own = names.Of(ValueNames.Hash("vigil_load"));
            checks.Add(own.Found && own.NamedIn.Contains("Vigil")
                ? new Check(Here, what, Outcome.Pass, ObjectInspector.NamesNote + "; Vigil's own load-test mark found by name")
                : new Check(Here, what, Outcome.Fail, ObjectInspector.NamesNote + "; Vigil's own load-test mark was not found, so the mods' names are not read"));
        }

        private IEnumerator LookOnce(List<Check> checks)
        {
            const string what = "what is under the crosshair, for the label";
            if (Player.m_localPlayer == null)
            {
                checks.Add(new Check(Here, what, Outcome.Info, "not in a world"));
                yield break;
            }
            var target = _look.Pick();
            checks.Add(target == null
                ? new Check(Here, what, Outcome.Info, "nothing with an owner there; look at a creature, cart or piece and run it again to see the label's words")
                : new Check(Here, what, Outcome.Pass, OwnershipLook.Plain(OwnershipLook.Words(target, _ownership, PingOf, Limits.PingMs))));
        }

        private IEnumerator FingerprintsTwice(List<Check> checks)
        {
            const string what = "config fingerprints the same twice";
            var first = OwnConfigs();
            yield return null;
            var second = OwnConfigs();
            var differ = first.Where(kv => !second.TryGetValue(kv.Key, out string fp) || fp != kv.Value).Select(kv => kv.Key).ToList();
            checks.Add(first.Count == 0 ? new Check(Here, what, Outcome.Warn, "no config files found")
                : differ.Count == 0 ? new Check(Here, what, Outcome.Pass, first.Count + " files")
                : new Check(Here, what, Outcome.Warn, "changed between two reads (a mod writing them?): " + string.Join(", ", differ.Take(3).ToArray())));
        }

        private IEnumerator ShareQueueWritable(List<Check> checks)
        {
            const string what = "the share queue's file";
            try
            {
                string probe = ShareQueueFile + ".probe";
                Directory.CreateDirectory(Path.GetDirectoryName(probe));
                File.WriteAllText(probe, _shareQueue.Format());
                bool same = File.ReadAllText(probe) == _shareQueue.Format();
                File.Delete(probe);
                checks.Add(same ? new Check(Here, what, Outcome.Pass, _shareQueue.Files.Count + " waiting") : new Check(Here, what, Outcome.Fail, "read back other than written"));
            }
            catch (Exception e) { checks.Add(new Check(Here, what, Outcome.Fail, e.Message)); }
            yield break;
        }

        // ------------------------------------------------------------------ full: asking the server

        private sealed class Answer
        {
            public bool Done, Ok;
            public Dictionary<string, string> Reply = new Dictionary<string, string>();
            public double Seconds;
        }

        private Answer Ask(string command, Dictionary<string, object> args)
        {
            var a = new Answer();
            double t0 = Now;
            _host.Link.Ask(command, args, (ok, reply) => { a.Ok = ok; a.Reply = reply ?? a.Reply; a.Seconds = Now - t0; a.Done = true; });
            return a;
        }

        /// <summary>Whether the routine can ask the server this; adds the reason as a note when not.</summary>
        private bool CanAsk(List<Check> checks, string what, string capability, bool adminOnly)
        {
            string lacking = _host.Link.Server.Lacking(capability, "answer it", adminOnly);
            if (lacking == null) return true;
            checks.Add(new Check(Here, what, Outcome.Info, lacking));
            return false;
        }

        private static string Why(Answer a) => a.Reply.TryGetValue("text", out string t) ? t : "no answer";

        private IEnumerator StatusAnswered(List<Check> checks)
        {
            const string what = "the server's status";
            if (!CanAsk(checks, what, Greeting.Status, adminOnly: true)) yield break;
            var a = Ask("status", null);
            while (!a.Done) yield return null;
            var verdict = SelfTestRoutines.Judge(a.Ok, a.Seconds);
            checks.Add(new Check(Here, what, verdict, a.Ok ? "answered in " + a.Seconds.ToString("0.00", Ic) + " s" : Why(a)));
        }

        private IEnumerator OwnLinkOnServer(List<Check> checks)
        {
            const string what = "your last minute as the server keeps it";
            if (Player.m_localPlayer == null) { checks.Add(new Check(Here, what, Outcome.Info, "not in a world")); yield break; }
            if (!CanAsk(checks, what, Greeting.Link, adminOnly: true)) yield break;
            var a = Ask("link", new Dictionary<string, object> { { "who", Player.m_localPlayer.GetPlayerName() } });
            while (!a.Done) yield return null;
            if (!a.Ok) { checks.Add(new Check(Here, what, Outcome.Fail, Why(a))); yield break; }
            int n = LinkHistory.Parse(a.Reply).Count;
            checks.Add(n > 0 ? new Check(Here, what, SelfTestRoutines.Judge(true, a.Seconds), n + " s of your connection")
                : new Check(Here, what, Outcome.Warn, "the server keeps none (it records only while players are on)"));
        }

        private IEnumerator ReportFetchedBack(List<Check> checks)
        {
            const string what = "a server report fetched back whole";
            if (!CanAsk(checks, what, Greeting.Reports, adminOnly: true)) yield break;
            var list = Ask("reports", null);
            while (!list.Done) yield return null;
            if (!list.Ok) { checks.Add(new Check(Here, what, Outcome.Fail, Why(list))); yield break; }
            var entries = ReportCatalog.ParseList(list.Reply);
            var pick = SelfTestRoutines.SmallestFixed(entries);
            if (pick == null) { checks.Add(new Check(Here, what, Outcome.Info, entries.Count + " reports listed, none that stays the same while fetched")); yield break; }
            var got = new List<byte>();
            string failed = null;
            var fetch = Fetch(pick, got, e => failed = e);
            while (fetch.MoveNext()) yield return fetch.Current;
            checks.Add(failed != null ? new Check(Here, what, Outcome.Fail, pick.Path + ": " + failed)
                : got.Count == pick.Size ? new Check(Here, what, Outcome.Pass, entries.Count + " listed; " + pick.Path + ", " + got.Count + " bytes")
                : new Check(Here, what, Outcome.Fail, pick.Path + ": " + got.Count + " bytes came, " + pick.Size + " listed"));
        }

        /// <summary>A report's pieces one after another into <paramref name="into"/>.</summary>
        private IEnumerator Fetch(ReportCatalog.Entry e, List<byte> into, Action<string> fail)
        {
            int parts = ReportCatalog.Parts(e.Size);
            for (int part = 0; part < parts; part++)
            {
                var a = Ask("report", new Dictionary<string, object> { { "path", e.Path }, { "part", part } });
                while (!a.Done) yield return null;
                if (!a.Ok || !a.Reply.TryGetValue("data", out string data)) { fail(Why(a)); yield break; }
                into.AddRange(ReportCatalog.DecodePart(data));
            }
        }

        private IEnumerator ParityOnce(List<Check> checks)
        {
            const string what = "mods and configs against the server";
            if (!CanAsk(checks, what, Greeting.Parity, adminOnly: false)) yield break;
            var a = Ask("parity", null);
            while (!a.Done) yield return null;
            if (!a.Ok) { checks.Add(new Check(Here, what, Outcome.Fail, Why(a))); yield break; }
            var (mods, configs) = Parity.Parse(a.Reply);
            var report = Parity.Compare(OwnMods(), mods, OwnConfigs(), configs);
            ResultStore.Shared.Put(ResultStore.Parity, ParityResult.Build(report, DateTime.Now));
            checks.Add(new Check(Here, what, report.VersionsDiffer.Count > 0 ? Outcome.Warn : Outcome.Pass,
                mods.Count + " server mods, " + report.VersionsDiffer.Count + " differ in version, " + report.ConfigsDiffer.Count + " configs differ (the Health tab lists them)"));
        }

        private IEnumerator SharedRoundTrip(List<Check> checks)
        {
            const string what = "a shared report reaching the server";
            if (!_share.Value) { checks.Add(new Check(Here, what, Outcome.Info, "sharing is off (Overview, Sharing)")); yield break; }
            if (!CanAsk(checks, what, Greeting.Share, adminOnly: false)) yield break;
            var at = DateTime.Now;
            string name = SelfTestRoutines.TestIncidentName(at), path = Path.Combine(_dir, name);
            File.WriteAllText(path, SelfTestRoutines.TestIncidentText(at));
            var sent = File.ReadAllBytes(path);
            _shareError = null;
            ShareOne(path);
            while (_shareQueue.Files.Contains(path, StringComparer.OrdinalIgnoreCase)) yield return null;
            if (_shareError != null) { checks.Add(new Check(Here, what, Outcome.Fail, _shareError)); yield break; }
            if (!_host.Link.Server.IsAdmin) { checks.Add(new Check(Here, what, Outcome.Pass, "sent; only the server's admins can fetch it back")); yield break; }
            string shared = ReportCatalog.SharedFolder + "/" + SharedStore.Folder(Player.m_localPlayer?.GetPlayerName()) + "/" + name;
            var list = Ask("reports", null);
            while (!list.Done) yield return null;
            var entry = list.Ok ? ReportCatalog.ParseList(list.Reply).FirstOrDefault(x => x.Path == shared) : null;
            if (entry == null) { checks.Add(new Check(Here, what, Outcome.Fail, "sent, but not in the server's list as " + shared)); yield break; }
            var got = new List<byte>();
            string failed = null;
            var fetch = Fetch(entry, got, e => failed = e);
            while (fetch.MoveNext()) yield return fetch.Current;
            checks.Add(failed == null && got.SequenceEqual(sent) ? new Check(Here, what, Outcome.Pass, "sent and fetched back the same, as " + shared)
                : new Check(Here, what, Outcome.Fail, failed ?? "fetched back other than sent"));
        }
    }
}
