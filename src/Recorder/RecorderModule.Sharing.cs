using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// Shared reports. A player who switches sharing on sends the incident reports of the kinds they
    /// chose to the server's Vigil on their own, and any incident report with a button; the reports
    /// wait in Vigil-state/share-queue.txt until the server has them, so one written while the
    /// connection was down (a drop) goes at the next join. They travel one 32 kB piece at a time,
    /// each answered before the next, at most four a second, and not during a load test. A server
    /// takes them only when its owner switched that on: it then says so in its greeting
    /// (capability "share"), keeps each player's in BepInEx/Vigil/shared/&lt;player&gt;/, the oldest
    /// going first above its limit, and lists them with its own reports.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private ConfigEntry<bool> _share, _accept;
        private ConfigEntry<ShareKinds> _shareKinds;
        private ConfigEntry<int> _keepPerPlayerMb;

        private ShareQueue _shareQueue = new ShareQueue();
        private readonly List<string> _written = new List<string>();
        private string _shareFile, _shareError;
        private byte[] _shareBytes;
        private int _sharePart, _shareParts, _shareSent;
        private bool _shareAsking;
        private double _shareNextAt;

        // On the server: each player's report on its way, by folder: its name and the piece expected next.
        private readonly Dictionary<string, KeyValuePair<string, int>> _arriving = new Dictionary<string, KeyValuePair<string, int>>(StringComparer.Ordinal);

        private string ShareQueueFile => Path.Combine(_dir, "Vigil-state", "share-queue.txt");

        private long KeepPerPlayerBytes => Math.Max(1, _keepPerPlayerMb.Value) * 1024L * 1024L;

        private void BindSharing()
        {
            _share = Config.Bind("Sharing", "Share", false,
                "On a player's game: send incident reports of the kinds below to the server's Vigil on their own, so they need not be passed around by hand. " +
                "Reports hold this machine's frame times, network numbers, system details and recent log lines. Any incident report can also be sent with its button in the panel.");
            _shareKinds = Config.Bind("Sharing", "Kinds", ShareKinds.LagReports | ShareKinds.Drops | ShareKinds.OtherIncidents | ShareKinds.EnvironmentReports | ShareKinds.SpawnLogs,
                "On a player's game: which reports Share sends: LagReports (F9), Drops (a connection that died, sent at the next join), OtherIncidents (long frames, slow stretches, ping, piled-up data, memory), " +
                "EnvironmentReports (each one written with the World tab's Write button or vigil weather report), SpawnLogs (what this game spawned while on the server and what mods cancelled, once a minute).");
            _accept = Config.Bind("Sharing", "Accept", false,
                "On a dedicated server: take the reports players share, into BepInEx/Vigil/shared/<player>/, listed with the server's own reports for its admins, " +
                "and the spawn logs they send, shown merged to its admins in the World tab's Spawns across the server.");
            _keepPerPlayerMb = Config.Bind("Sharing", "KeepPerPlayerMB", 50,
                "On a dedicated server: how much of each player's shared reports is kept; their oldest go first.");
            Link.MoreCaps = () => _server && _accept.Value ? "," + Greeting.Share + "," + Greeting.SpawnLog : "";
            try { if (File.Exists(ShareQueueFile)) _shareQueue = ShareQueue.Parse(File.ReadAllText(ShareQueueFile)); } catch (Exception) { }
            _share.SettingChanged += (s, e) => { if (!_share.Value) { _shareQueue = new ShareQueue(); _shareFile = null; SaveShareQueue(); } };
        }

        /// <summary>From the thread that wrote a report: it is on disk now.</summary>
        private void ReportWritten(string path)
        {
            lock (_written) _written.Add(path);
        }

        private void SaveShareQueue()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ShareQueueFile));
                File.WriteAllText(ShareQueueFile, _shareQueue.Format());
            }
            catch (Exception) { }
        }

        /// <summary>Queues one report (an incident or an environment report) to be sent, whatever the switch says (the panel's button).</summary>
        private void ShareOne(string path)
        {
            if (!ShareQueue.Shareable(path)) return;
            if (_shareQueue.Add(path)) SaveShareQueue();
            _shareError = null;
        }

        // ------------------------------------------------------------------ the player's side

        /// <summary>Every frame on a player's game: queues new reports the switch sends, and sends the next piece when it may.</summary>
        private void UpdateSharing()
        {
            if (_server) return;
            List<string> written = null;
            lock (_written) { if (_written.Count > 0) { written = new List<string>(_written); _written.Clear(); } }
            if (written != null && _share.Value)
            {
                bool added = false;
                foreach (var w in written) if (ShareQueue.Wanted(_shareKinds.Value, w)) added |= _shareQueue.Add(w);
                if (added) SaveShareQueue();
            }
            if (_shareAsking || Now < _shareNextAt || _load.Running || _shareQueue.Files.Count == 0) return;
            if (_host.Link.Server.Lacking(Greeting.Share, "take shared reports", adminOnly: false) != null) return;
            string next = _shareQueue.Next(File.Exists);
            if (next == null) { SaveShareQueue(); return; }
            if (next != _shareFile)
            {
                try { _shareBytes = File.ReadAllBytes(next); }
                catch (Exception) { _shareNextAt = Now + 5; return; }
                _shareFile = next;
                _sharePart = 0;
                _shareParts = ReportCatalog.Parts(_shareBytes.Length);
            }
            SendPiece(next);
        }

        private void SendPiece(string file)
        {
            _shareAsking = true;
            var args = new Dictionary<string, object>
            {
                { "name", Path.GetFileName(file) }, { "size", _shareBytes.Length }, { "part", _sharePart }, { "parts", _shareParts },
                { "data", ReportCatalog.EncodePart(_shareBytes, _sharePart) },
            };
            _host.Link.Ask("share", args, (ok, reply) =>
            {
                _shareAsking = false;
                _shareNextAt = Now + 0.25;
                if (_shareFile != file) return;
                if (!ok)
                {
                    reply.TryGetValue("text", out string why);
                    if (reply.TryGetValue("ok", out string flag) && flag == "0")
                    {
                        // Refused: this report will not be taken; the next one may be.
                        _shareError = Path.GetFileName(file) + ": " + why;
                        _shareQueue.Remove(file);
                        _shareFile = null;
                        SaveShareQueue();
                    }
                    else _shareNextAt = Now + 10; // no answer: the same piece again later
                    return;
                }
                if (++_sharePart < _shareParts) return;
                _shareQueue.Remove(file);
                _shareFile = null;
                _shareBytes = null;
                _shareSent++;
                SaveShareQueue();
            });
        }

        // ------------------------------------------------------------------ the server's side

        private Dictionary<string, object> ShareReply(Dictionary<string, string> args, ZNetPeer from)
        {
            Dictionary<string, object> No(string why) => new Dictionary<string, object> { { "ok", 0 }, { "text", why } };
            if (!_accept.Value) return No("this server does not take shared reports");
            args.TryGetValue("name", out string name);
            long size = (long)KeyValues.Number(args, "size");
            int part = (int)KeyValues.Number(args, "part"), parts = (int)KeyValues.Number(args, "parts");
            string why = SharedStore.Refuse(name, size, KeepPerPlayerBytes);
            if (why != null) return No(why);
            if (parts != ReportCatalog.Parts(size) || part < 0 || part >= parts) return No("the report's pieces do not add up");
            string folder = SharedStore.Folder(from?.m_playerName);
            string dir = Path.Combine(Path.Combine(_dir, ReportCatalog.SharedFolder), folder);
            string partial = Path.Combine(dir, name + ".part");
            try
            {
                if (part == 0)
                {
                    Directory.CreateDirectory(dir);
                    foreach (var stale in Directory.GetFiles(dir, "*.part")) File.Delete(stale);
                    var kept = Directory.GetFiles(dir, "Vigil-*.txt").Where(f => Path.GetFileName(f) != name).Select(f => new FileInfo(f)).Select(i => (i.Name, i.Length, i.LastWriteTime));
                    foreach (var old in SharedStore.Prune(kept, size, KeepPerPlayerBytes)) File.Delete(Path.Combine(dir, old));
                    File.WriteAllBytes(partial, new byte[0]);
                }
                else if (!_arriving.TryGetValue(folder, out var expected) || expected.Key != name || expected.Value != part)
                    return No("a piece out of order; the report starts again");
                var bytes = ReportCatalog.DecodePart(args.TryGetValue("data", out string data) ? data : "");
                using (var s = new FileStream(partial, FileMode.Append, FileAccess.Write))
                {
                    if (s.Length + bytes.Length > size) return No("more data than the report's size");
                    s.Write(bytes, 0, bytes.Length);
                }
                if (part < parts - 1)
                {
                    _arriving[folder] = new KeyValuePair<string, int>(name, part + 1);
                    return new Dictionary<string, object> { { "part", part } };
                }
                _arriving.Remove(folder);
                string final = Path.Combine(dir, name);
                if (File.Exists(final)) File.Delete(final);
                File.Move(partial, final);
                _host.Log.LogInfo("Vigil: " + (from?.m_playerName ?? "a player") + " shared " + name);
                AddAlert(AlertSources.FromShared(ReportCatalog.SharedFolder + "/" + folder + "/" + name, DateTime.UtcNow));
                return new Dictionary<string, object> { { "part", part }, { "text", "kept" } };
            }
            catch (Exception e) { return No("the server could not keep it: " + e.Message); }
        }

        // ------------------------------------------------------------------ the panel

        private PanelBadge SharingBadge() =>
            _shareQueue.Files.Count > 0 ? new PanelBadge(_shareQueue.Files.Count + " waiting", Skin.Tone.Accent)
            : _share.Value ? new PanelBadge("on", Skin.Tone.Good) : new PanelBadge("off", Skin.Tone.Quiet);

        private static readonly string[] OnOff = { "On", "Off" };

        private void DrawSharing()
        {
            Ui.BeginFormLine("Send my reports");
            int picked = Ui.Choice(_share.Value ? 0 : 1, OnOff);
            if (picked >= 0 && (picked == 0) != _share.Value) _share.Value = picked == 0;
            Ui.EndLine();
            if (_share.Value)
            {
                Ui.BeginFormLine("Which");
                var k = _shareKinds.Value;
                bool lag = Ui.Toggle((k & ShareKinds.LagReports) != 0, "Lag reports (F9)");
                bool drops = Ui.Toggle((k & ShareKinds.Drops) != 0, "Drops");
                bool other = Ui.Toggle((k & ShareKinds.OtherIncidents) != 0, "Other incidents");
                Ui.EndLine();
                Ui.BeginFormLine("");
                bool environment = Ui.Toggle((k & ShareKinds.EnvironmentReports) != 0, "Environment reports, as each is written");
                Ui.EndLine();
                Ui.BeginFormLine("");
                bool spawns = Ui.Toggle((k & ShareKinds.SpawnLogs) != 0, "Spawn log, every minute");
                Ui.EndLine();
                var now = (lag ? ShareKinds.LagReports : 0) | (drops ? ShareKinds.Drops : 0) | (other ? ShareKinds.OtherIncidents : 0)
                    | (environment ? ShareKinds.EnvironmentReports : 0) | (spawns ? ShareKinds.SpawnLogs : 0);
                if (now != k) _shareKinds.Value = now;
            }
            string lacking = _host.Link.Server.Lacking(Greeting.Share, "take shared reports", adminOnly: false);
            Ui.Row("This server", lacking == null ? "takes shared reports" : Sentence(lacking));
            string state = _shareFile != null ? string.Format(CultureInfo.InvariantCulture, "sending {0}, piece {1} of {2}", Path.GetFileName(_shareFile), _sharePart + 1, _shareParts)
                : _shareQueue.Files.Count > 0 ? _shareQueue.Files.Count + " waiting for the server"
                : _shareSent > 0 ? _shareSent + " sent this session" : "nothing waiting";
            Ui.Row("Reports", state);
            if (_shareError != null) Ui.Row("Not taken", _shareError);
            if (_share.Value && (_shareKinds.Value & ShareKinds.SpawnLogs) != 0) Ui.Row("Spawn log", SpawnLogState());
            Ui.Note("Reports hold this machine's frame times, network numbers, system details and recent log lines; environment reports the weather, light and graphics settings; the spawn log what your game spawned, where, and what mods cancelled. Each incident below, and each environment report in the World tab, can also be sent on its own.");
        }

        /// <summary>Under an open incident: a button that sends it to the server, or where it stands.</summary>
        private void DrawSendIncident(Result incident)
        {
            if (incident?.Path == null || !ShareQueue.Shareable(incident.Path)) return;
            Ui.BeginLine();
            GUILayout.FlexibleSpace();
            bool waiting = _shareQueue.Files.Contains(incident.Path, StringComparer.OrdinalIgnoreCase);
            if (waiting) Ui.Note(_shareFile == incident.Path ? "Sending to the server ..." : "Waiting for the server");
            else if (Ui.Button("Send to the server")) ShareOne(incident.Path);
            Ui.EndLine();
        }
    }
}
