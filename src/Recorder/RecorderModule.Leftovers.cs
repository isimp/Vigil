using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;
using Check = Vigil.Core.SelfTestReport.Check;
using Outcome = Vigil.Core.SelfTestReport.Result;

namespace Vigil.Recorder
{
    /// <summary>
    /// "Leftovers in the world", for the server's admins. The server reads every object the world
    /// keeps, a share a frame (<see cref="WorldScan"/>), and lists those whose prefab no installed mod
    /// provides; a removal saves the world first and then takes over and destroys each object of that
    /// kind the way the game destroys any object, a share a frame, and reads the world again. It also
    /// reads the world's player history (<see cref="PlayerHistory"/>) and removes the entries load tests
    /// left; a load test removes its own when it stops (<see cref="RemoveBotHistory"/>).
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private const int ScanShare = 20000, RemoveShare = 1000;
        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> s_allObjects =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");
        private static readonly MethodInfo s_sendHistory = AccessTools.Method(typeof(ZNet), "SendHistoricalPlayerList");

        // The server's side.
        private string _leftState = LeftoverReport.None;
        private WorldScan _leftScan;
        private List<ZDO> _leftObjects;
        private List<ZDOID> _leftRemoving;
        private int _leftNext, _leftRemoved;
        private double _leftStarted, _leftSeconds, _leftDoneAt;
        private string _leftNote;

        // An admin's card.
        private LeftoverReport _leftReport, _leftShown;
        private string _leftError;
        private double _leftAskedAt = double.NegativeInfinity;
        private bool _leftAsking;
        private readonly Confirm _leftConfirm = new Confirm(5);

        // ------------------------------------------------------------------ the server's side

        /// <summary>Every frame on the server: the next share of a scan or a removal.</summary>
        private void UpdateLeftovers()
        {
            if (!_server || _leftState == LeftoverReport.None || _leftState == LeftoverReport.Done) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (_leftState == LeftoverReport.Scanning) ScanShareNow();
                else if (_leftState == LeftoverReport.Removing && ZNet.instance != null && !ZNet.instance.IsSaving()) RemoveShareNow();
            }
            finally { _recorder?.AddOwnTime(watch.Elapsed.TotalMilliseconds); }
        }

        private void StartWorldScan()
        {
            var all = ZDOMan.instance != null ? s_allObjects(ZDOMan.instance) : null;
            if (all == null || ZNetScene.instance == null) return;
            _leftObjects = all.Values.ToList();
            _leftScan = new WorldScan();
            _leftNext = 0;
            _leftStarted = Now;
            _leftState = LeftoverReport.Scanning;
        }

        private void ScanShareNow()
        {
            int end = Math.Min(_leftObjects.Count, _leftNext + ScanShare);
            for (; _leftNext < end; _leftNext++)
            {
                var zdo = _leftObjects[_leftNext];
                if (zdo == null) continue;
                int hash = zdo.GetPrefab();
                var p = zdo.GetPosition();
                _leftScan.Read(hash, p.x, p.z, ZNetScene.instance.HasPrefab(hash), zdo.m_uid);
            }
            if (_leftNext < _leftObjects.Count) return;
            _leftSeconds = Now - _leftStarted;
            _leftDoneAt = Now;
            _leftObjects = null;
            _leftState = LeftoverReport.Done;
        }

        private void RemoveShareNow()
        {
            long session = ZDOMan.GetSessionID();
            int end = Math.Min(_leftRemoving.Count, _leftNext + RemoveShare);
            for (; _leftNext < end; _leftNext++)
            {
                var zdo = ZDOMan.instance.GetZDO(_leftRemoving[_leftNext]);
                if (zdo == null || ZNetScene.instance.HasPrefab(zdo.GetPrefab())) continue;
                zdo.SetOwner(session);
                ZDOMan.instance.DestroyZDO(zdo);
                _leftRemoved++;
            }
            if (_leftNext < _leftRemoving.Count) return;
            _leftNote = "saved the world, then removed " + _leftRemoved.ToString("N0", CultureInfo.InvariantCulture) + (_leftRemoved == 1 ? " object" : " objects");
            _host.Log.LogInfo("Vigil: " + _leftNote);
            _leftRemoving = null;
            StartWorldScan();
        }

        private static List<ZNet.CrossNetworkUserInfo> History() => ZNet.World?.m_playerHistory;

        /// <summary>Takes the entries load tests left out of the world's player history and tells the players; the next world save keeps it.</summary>
        internal static int RemoveBotHistory()
        {
            var history = History();
            if (history == null) return 0;
            int removed = history.RemoveAll(h => PlayerHistory.IsBot(h.m_id.ToString()));
            if (removed > 0)
            {
                try { s_sendHistory?.Invoke(ZNet.instance, null); } catch (Exception) { }
            }
            return removed;
        }

        private Dictionary<string, object> LeftoversReply(Dictionary<string, string> args, ZNetPeer from)
        {
            args.TryGetValue("do", out string what);
            string who = from?.m_playerName ?? "an admin";
            if (what == "scan" && _leftState != LeftoverReport.Scanning && _leftState != LeftoverReport.Removing)
            {
                StartWorldScan();
                _leftNote = null;
                _host.Log.LogInfo("Vigil: " + who + " asked to read the whole world for objects of removed mods");
            }
            else if (what == "remove")
            {
                int hash = (int)KeyValues.Number(args, "hash");
                if (_leftState != LeftoverReport.Done || _leftScan == null) return No("read the world first");
                var ids = _leftScan.IdsOf(hash).OfType<ZDOID>().ToList();
                if (ids.Count == 0) return No("no objects of that kind in the last reading");
                ZNet.instance.Save(false);
                _leftRemoving = ids;
                _leftNext = 0;
                _leftRemoved = 0;
                _leftState = LeftoverReport.Removing;
                _host.Log.LogInfo("Vigil: " + who + " asked to remove " + ids.Count + " objects of prefab hash " + hash + "; the world is saved first");
            }
            else if (what == "bots")
            {
                int removed = RemoveBotHistory();
                _leftNote = "took " + removed + (removed == 1 ? " load-test entry" : " load-test entries") + " out of the player history; the next world save keeps that";
                _host.Log.LogInfo("Vigil: " + who + " asked to remove the load-test entries of the player history: " + removed);
            }

            var history = History();
            var ids2 = history != null ? history.Select(h => h.m_id.ToString()).ToList() : new List<string>();
            var report = new LeftoverReport
            {
                State = _leftState, Note = _leftNote,
                History = history != null ? PlayerHistory.Summary(ids2) : "the world's player history cannot be read", Bots = PlayerHistory.Bots(ids2),
            };
            if (_leftState == LeftoverReport.Scanning)
            {
                report.Read = _leftNext;
                report.Total = _leftObjects?.Count ?? 0;
            }
            else if (_leftState == LeftoverReport.Removing)
            {
                report.Read = _leftNext;
                report.Total = _leftRemoving?.Count ?? 0;
            }
            else if (_leftState == LeftoverReport.Done)
            {
                report.Read = report.Total = _leftScan.Objects;
                report.Seconds = _leftSeconds;
                report.Ago = Now - _leftDoneAt;
                report.Verdict = _leftScan.Verdict;
                report.Kinds = _leftScan.Kinds();
            }
            return report.Encode();

            Dictionary<string, object> No(string why) => new Dictionary<string, object> { { "ok", 0 }, { "text", why } };
        }

        // ------------------------------------------------------------------ an admin's card

        private PanelBadge LeftoversBadge()
        {
            var r = _leftShown;
            if (r == null) return null;
            int kinds = r.State == LeftoverReport.Done ? r.Kinds.Count : 0;
            if (kinds == 0 && r.Bots == 0) return r.State == LeftoverReport.Done ? new PanelBadge("clean", Skin.Tone.Quiet) : null;
            var parts = new List<string>();
            if (kinds > 0) parts.Add(kinds + (kinds == 1 ? " kind" : " kinds"));
            if (r.Bots > 0) parts.Add(r.Bots + (r.Bots == 1 ? " bot" : " bots"));
            return new PanelBadge(string.Join(" · ", parts.ToArray()), Skin.Tone.Accent);
        }

        private void DrawLeftovers()
        {
            string lacking = _host.Link.Server.Lacking(Greeting.Leftovers, "read the whole world", adminOnly: true);
            if (lacking != null)
            {
                Ui.Note(Sentence(lacking) + " The server's admins read the whole world here, on the server, for objects of removed mods and load-test entries.");
                return;
            }

            // Asked and read on a layout pass only, so the pass that draws the card draws what was laid out.
            if (Event.current.type == EventType.Layout)
            {
                bool busy = _leftReport != null && (_leftReport.State == LeftoverReport.Scanning || _leftReport.State == LeftoverReport.Removing);
                if (!_leftAsking && Now - _leftAskedAt >= (busy ? 1 : _leftReport == null ? 0 : 30)) AskLeftovers(null, 0);
                _leftShown = _leftReport;
            }

            var r = _leftShown;
            if (r == null)
            {
                Ui.Note(_leftError != null ? Sentence(_leftError) : "Asking the server ...");
                return;
            }

            bool working = r.State == LeftoverReport.Scanning || r.State == LeftoverReport.Removing;
            string action = working ? "Running" : r.State == LeftoverReport.Done ? "Again" : "Scan";
            if (Ui.Tool("Read the whole world", r.StateLine(), action, enabled: !working)) Ui.Later(() => AskLeftovers("scan", 0));
            if (r.Note != null) Ui.Note(Sentence(r.Note));
            if (_leftError != null) Ui.Note(Sentence(_leftError));

            if (r.State == LeftoverReport.Done)
            {
                Ui.Verdict(r.Kinds.Count > 0 ? ResultTone.Caution : ResultTone.Good, r.Verdict);
                if (r.Kinds.Count > 0)
                {
                    Ui.Heading("Objects of removed mods");
                    foreach (var k in r.Kinds)
                    {
                        string what = k.Hash.ToString(CultureInfo.InvariantCulture);
                        bool armed = _leftConfirm.Armed(what, Now);
                        string words = armed
                            ? "Press Confirm within 5 s: the server saves the world, then removes these " + k.Count.ToString("N0", CultureInfo.InvariantCulture) + " for good"
                            : k.Count.ToString("N0", CultureInfo.InvariantCulture) + (k.Count == 1 ? " object · " : " objects · ") + k.Where;
                        if (Ui.Tool(k.Name, words, armed ? "Confirm" : "Remove", danger: armed) && _leftConfirm.Press(what, Now))
                        {
                            int hash = k.Hash;
                            Ui.Later(() => AskLeftovers("remove", hash));
                        }
                    }
                    Ui.Note("Removing takes over each object and destroys it the way the game destroys any object, after a world save; the world is read again after it.");
                }
            }

            Ui.Heading("The world's player history");
            if (r.Bots > 0)
            {
                bool armed = _leftConfirm.Armed("bots", Now);
                if (Ui.Tool("Load-test entries", armed ? "Press Confirm within 5 s to take them out of the history" : r.History, armed ? "Confirm" : "Remove", danger: armed)
                    && _leftConfirm.Press("bots", Now))
                    Ui.Later(() => AskLeftovers("bots", 0));
            }
            else Ui.Row("Entries", r.History ?? "");
            Ui.Note("The game keeps everyone who ever joined in the world file and sends the list to every player. Load tests no longer leave entries: each takes its own out when it stops.");
        }

        private void AskLeftovers(string what, int hash)
        {
            _leftAsking = true;
            _leftAskedAt = Now;
            var args = new Dictionary<string, object>();
            if (what != null) args["do"] = what;
            if (what == "remove") args["hash"] = hash;
            _host.Link.Ask(Greeting.Leftovers, args, (ok, reply) =>
            {
                _leftAsking = false;
                if (!ok)
                {
                    _leftError = reply.TryGetValue("text", out string why) ? why : "no answer";
                    return;
                }
                _leftError = null;
                _leftReport = LeftoverReport.Decode(reply);
            });
        }

        // ------------------------------------------------------------------ console

        /// <summary>"vigil leftovers [scan|remove &lt;hash&gt;|bots]": the server's reading of the whole world, for its admins.</summary>
        private string LeftoversCommand(string rest)
        {
            if (_server) return "this is the server; its admins ask from their game";
            string lacking = _host.Link.Server.Lacking(Greeting.Leftovers, "read the whole world", adminOnly: true);
            if (lacking != null) return lacking;
            var words = (rest ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string what = words.Length > 0 ? words[0].ToLowerInvariant() : null;
            int hash = 0;
            if (what == "remove" && (words.Length < 2 || !int.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out hash)))
                return "use: vigil leftovers remove <hash>, with the hash from the list";
            if (what != null && what != "scan" && what != "remove" && what != "bots") return "use: vigil leftovers [scan|remove <hash>|bots]";
            var args = new Dictionary<string, object>();
            if (what != null) args["do"] = what;
            if (what == "remove") args["hash"] = hash;
            _host.Link.Ask(Greeting.Leftovers, args, (ok, reply) =>
            {
                if (!ok)
                {
                    _host.Reply("Vigil on the server: " + (reply.TryGetValue("text", out string why) ? why : "no answer"));
                    return;
                }
                var r = LeftoverReport.Decode(reply);
                _leftReport = r;
                var lines = new List<string> { "Vigil on the server: " + r.StateLine() };
                if (r.Note != null) lines.Add("  " + r.Note);
                if (r.Verdict != null) lines.Add("  " + r.Verdict);
                lines.AddRange(r.Kinds.Select(k => "  " + k.Hash.ToString(CultureInfo.InvariantCulture).PadRight(13) + k.Count.ToString("N0", CultureInfo.InvariantCulture).PadLeft(7) + "  " + k.Name + ", " + k.Where));
                lines.Add("  player history: " + r.History);
                _host.Reply(string.Join("\n", lines));
            });
            return what == "scan" ? "the server reads the whole world; vigil leftovers shows how far it got" : "asking the server's Vigil";
        }

        // ------------------------------------------------------------------ self-test (full)

        private IEnumerator LeftoversOnServer(List<Check> checks)
        {
            const string what = "the server's reading of the whole world";
            if (!CanAsk(checks, what, Greeting.Leftovers, adminOnly: true)) yield break;
            var a = Ask(Greeting.Leftovers, null);
            while (!a.Done) yield return null;
            if (!a.Ok)
            {
                checks.Add(new Check(Here, what, Outcome.Fail, Why(a)));
                yield break;
            }
            var r = LeftoverReport.Decode(a.Reply);
            checks.Add(new Check(Here, what, SelfTestRoutines.Judge(true, a.Seconds), r.StateLine() + "; history: " + r.History));
        }
    }
}
