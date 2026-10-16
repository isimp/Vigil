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
    /// The server's reports in an admin's panel. The server lists its report files (this session's
    /// and the two sessions before it in Vigil-runs) with their kind and first words, and hands one
    /// out in pieces; the player's game asks for the pieces one after another and writes the report
    /// into its own recordings folder under "server", keeping the server's folders, where Open
    /// shows it. Only an admin gets either answer, and only report files are handed out
    /// (<see cref="ReportCatalog.Servable"/>).
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private const int RunsListed = 2, HeadLines = 40;

        private List<ReportCatalog.Entry> _reports;
        private double _reportsAt = double.NegativeInfinity;
        private bool _reportsAsking;
        private string _reportsError;

        // The report being fetched: its entry, the pieces so far and the piece asked for.
        private ReportCatalog.Entry _fetching;
        private MemoryStream _fetched;
        private int _fetchPart, _fetchParts;
        private readonly Dictionary<string, string> _fetchedTo = new Dictionary<string, string>(StringComparer.Ordinal);

        // ------------------------------------------------------------------ the server's side

        private Dictionary<string, object> ReportsReply()
        {
            var entries = new List<ReportCatalog.Entry>();
            void Add(string folder, string prefix)
            {
                foreach (var file in Directory.GetFiles(folder, "Vigil-*.txt"))
                {
                    string name = Path.GetFileName(file), kind = ReportCatalog.KindOf(name);
                    if (kind == null) continue;
                    var info = new FileInfo(file);
                    entries.Add(new ReportCatalog.Entry { Path = prefix + name, Size = info.Length, Modified = info.LastWriteTime, Kind = kind, Headline = ReportCatalog.Headline(kind, Head(file)) });
                }
            }
            try
            {
                Add(_dir, "");
                string runs = Path.Combine(_dir, "Vigil-runs");
                if (Directory.Exists(runs))
                    foreach (var run in Directory.GetDirectories(runs).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).Take(RunsListed))
                        Add(run, "Vigil-runs/" + Path.GetFileName(run) + "/");
                string shared = Path.Combine(_dir, ReportCatalog.SharedFolder);
                if (Directory.Exists(shared))
                    foreach (var player in Directory.GetDirectories(shared))
                        Add(player, ReportCatalog.SharedFolder + "/" + Path.GetFileName(player) + "/");
            }
            catch (Exception e) { return new Dictionary<string, object> { { "text", "the reports could not be listed: " + e.Message }, { "ok", 0 } }; }
            var reply = new Dictionary<string, object>();
            foreach (var kv in ReportCatalog.FormatList(entries.OrderByDescending(e => e.Modified).Take(ReportCatalog.MaxListed).ToList())) reply[kv.Key] = kv.Value;
            return reply;
        }

        private static IEnumerable<string> Head(string file)
        {
            var lines = new List<string>();
            try
            {
                using (var r = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
                    for (string l; lines.Count < HeadLines && (l = r.ReadLine()) != null;) lines.Add(l);
            }
            catch (Exception) { }
            return lines;
        }

        private Dictionary<string, object> ReportPartReply(Dictionary<string, string> args)
        {
            args.TryGetValue("path", out string path);
            if (!ReportCatalog.Servable(path)) return new Dictionary<string, object> { { "text", "not a report: " + path }, { "ok", 0 } };
            int.TryParse(args.TryGetValue("part", out string p) ? p : "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out int part);
            string file = Path.Combine(_dir, path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                using (var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long size = s.Length;
                    int parts = ReportCatalog.Parts(size);
                    if (part < 0 || part >= parts) return new Dictionary<string, object> { { "text", "no such part" }, { "ok", 0 } };
                    s.Seek((long)part * ReportCatalog.PartBytes, SeekOrigin.Begin);
                    var buffer = new byte[(int)Math.Min(ReportCatalog.PartBytes, size - (long)part * ReportCatalog.PartBytes)];
                    int read = 0;
                    while (read < buffer.Length) { int n = s.Read(buffer, read, buffer.Length - read); if (n <= 0) break; read += n; }
                    return new Dictionary<string, object> { { "data", ReportCatalog.EncodePart(buffer.Take(read).ToArray(), 0) }, { "part", part }, { "parts", parts }, { "size", size } };
                }
            }
            catch (Exception e) { return new Dictionary<string, object> { { "text", "the report could not be read: " + e.Message }, { "ok", 0 } }; }
        }

        // ------------------------------------------------------------------ the admin's panel

        private PanelBadge ReportsBadge() =>
            _fetching != null ? new PanelBadge("getting " + (_fetchPart + 1) + " of " + _fetchParts, Skin.Tone.Accent)
            : _reports != null ? new PanelBadge(_reports.Count + (_reports.Count == 1 ? " report" : " reports"), Skin.Tone.Quiet) : null;

        private void DrawReports()
        {
            string lacking = _host.Link.Server.Lacking(Greeting.Reports, "hand out its reports", adminOnly: true);
            if (lacking != null)
            {
                Ui.Note(Sentence(lacking) + " The server's reports show here for its admins, when it runs Vigil 0.5 or later.");
                return;
            }
            if (!_reportsAsking && Now - _reportsAt >= 60) AskReports();
            if (_reports == null)
            {
                Ui.Note(_reportsError != null ? Sentence(_reportsError) : "Asking the server ...");
                return;
            }
            if (_reports.Count == 0) Ui.Note("The server has written no reports this session or the two before it.");
            foreach (var e in _reports)
            {
                bool here = _fetchedTo.TryGetValue(e.Path, out string local) && File.Exists(local);
                bool getting = _fetching != null && _fetching.Path == e.Path;
                string when = e.Modified.Date == DateTime.Today ? e.Modified.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : e.Modified.ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture);
                string from = ReportCatalog.From(e.Path);
                string name = Upper(e.Kind) + "   " + when + (from != null ? "   from " + from : e.Path.StartsWith("Vigil-runs/", StringComparison.Ordinal) ? "   (before a restart)" : "");
                if (getting) Ui.Tool(name, e.Headline, "Getting", enabled: false);
                else if (Ui.Tool(name, e.Headline, here ? PathClipboard.Label(local) : "Get", enabled: here || _fetching == null))
                {
                    if (here) PathClipboard.Copy(local);
                    else StartFetch(e);
                }
            }
            Ui.BeginLine();
            Ui.Note(string.Format(CultureInfo.InvariantCulture, "As of {0:0} s ago. Fetched reports go into the recordings folder under server.", Now - _reportsAt));
            GUILayout.FlexibleSpace();
            if (Ui.Quiet("Refresh")) AskReports();
            Ui.EndLine();
        }

        private static string Upper(string s) => string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private void AskReports()
        {
            _reportsAsking = true;
            _reportsAt = Now;
            _host.Link.Ask("reports", null, (ok, reply) =>
            {
                _reportsAsking = false;
                if (ok) { _reports = ReportCatalog.ParseList(reply); _reportsError = null; }
                else _reportsError = reply.TryGetValue("text", out string t) ? t : "the server did not answer";
            });
        }

        private void StartFetch(ReportCatalog.Entry e)
        {
            _fetching = e;
            _fetched = new MemoryStream();
            _fetchPart = 0;
            _fetchParts = ReportCatalog.Parts(e.Size);
            AskPart();
        }

        private void AskPart()
        {
            var e = _fetching;
            _host.Link.Ask("report", new Dictionary<string, object> { { "path", e.Path }, { "part", _fetchPart } }, (ok, reply) =>
            {
                if (_fetching != e) return;
                if (!ok || !reply.TryGetValue("data", out string data))
                {
                    _fetching = null;
                    _host.Show("Vigil: " + e.Path + " could not be fetched: " + (reply.TryGetValue("text", out string t) ? t : "no answer"));
                    return;
                }
                try
                {
                    var bytes = ReportCatalog.DecodePart(data);
                    _fetched.Write(bytes, 0, bytes.Length);
                    double parts = KeyValues.Number(reply, "parts");
                    if (!double.IsNaN(parts) && parts > 0) _fetchParts = (int)parts;
                    if (++_fetchPart < _fetchParts) { AskPart(); return; }
                    string local = Path.Combine(_dir, "server", e.Path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(local));
                    File.WriteAllBytes(local, _fetched.ToArray());
                    _fetchedTo[e.Path] = local;
                }
                catch (Exception x) { _host.Show("Vigil: " + e.Path + " could not be written: " + x.Message); }
                _fetching = null;
                _fetched = null;
            });
        }
    }
}
