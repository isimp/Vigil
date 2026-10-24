using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>One plugin's share of a start: from its "Loading" line to the next plugin's, and the bundles it loaded meanwhile.</summary>
    internal sealed class PluginTime
    {
        public PluginTime(string name, double startMs) { Name = name; StartMs = startMs; }

        public string Name { get; }

        public double StartMs { get; }

        public double Ms { get; set; }

        public int Bundles { get; set; }

        public long BundleBytes { get; set; }

        public double BundleMs { get; set; }
    }

    /// <summary>A start read from its steps: how long each part took, from the process starting to the main menu.</summary>
    internal sealed class StartupReport
    {
        public double EngineMs { get; set; }

        public double BepInExMs { get; set; }

        public double PluginsMs { get; set; }

        public double PatchingMs { get; set; }

        public int PatchedMethods { get; set; }

        public double MenuMs { get; set; }

        public double TotalMs { get; set; }

        public bool Finished { get; set; }

        /// <summary>A dedicated server's start, which ends when its world is up rather than at a menu.</summary>
        public bool Server { get; set; }

        public List<PluginTime> Plugins { get; } = new List<PluginTime>();

        public int FromCopies { get; set; }

        public int LoadedUsual { get; set; }

        public int NotLzma { get; set; }

        public int Failed { get; set; }

        public double BundleMs { get; set; }

        public bool Joined { get; set; }

        public bool Joining { get; set; }

        public double JoinMs { get; set; }

        public double HandshakeMs { get; set; }

        public List<KeyValuePair<string, double>> JoinMessages { get; } = new List<KeyValuePair<string, double>>();
    }

    /// <summary>
    /// Builds a <see cref="StartupReport"/> from the steps the patcher and Vigil recorded, and writes it
    /// as the startup report's text, the panel's rows and the session summary's line.
    /// </summary>
    internal static class StartupTimeline
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public static StartupReport Build(IList<StartupEvent> events)
        {
            var r = new StartupReport();
            if (events == null || events.Count == 0) return r;
            var all = events.Where(e => e != null).OrderBy(e => e.Ms).ToList();
            double last = all[all.Count - 1].Ms;

            var started = all.FirstOrDefault(e => e.Kind == StartupLines.Started);
            if (started != null && double.TryParse(started.Text, NumberStyles.Float, Ci, out double engine)) r.EngineMs = Math.Max(0, engine);

            double? done = First(all, StartupLines.PluginsDone), scene = First(all, StartupLines.MenuScene), world = First(all, StartupLines.WorldUp);
            double? shown = First(all, StartupLines.MenuShown) ?? world;
            r.Server = world.HasValue && !First(all, StartupLines.MenuShown).HasValue;
            double pluginsEnd = done ?? last;

            foreach (var e in all.Where(e => e.Kind == StartupLines.Plugin)) r.Plugins.Add(new PluginTime(StartupLines.PluginName(e.Text), e.Ms));
            for (int i = 0; i < r.Plugins.Count; i++)
            {
                double end = i + 1 < r.Plugins.Count ? r.Plugins[i + 1].StartMs : pluginsEnd;
                r.Plugins[i].Ms = Math.Max(0, end - r.Plugins[i].StartMs);
            }

            r.BepInExMs = r.Plugins.Count > 0 ? r.Plugins[0].StartMs : pluginsEnd;
            r.PluginsMs = r.Plugins.Count > 0 ? Math.Max(0, pluginsEnd - r.Plugins[0].StartMs) : 0;

            var batch = all.FirstOrDefault(e => e.Kind == StartupLines.Batch && (!done.HasValue || e.Ms >= done.Value) && (!scene.HasValue || e.Ms <= scene.Value));
            if (batch != null && StartupLines.ParseBatch(batch.Text, out int methods, out double batchMs))
            {
                r.PatchingMs = batchMs;
                r.PatchedMethods = methods;
            }

            r.Finished = shown.HasValue;
            double end0 = shown ?? last;
            r.TotalMs = r.EngineMs + end0;
            r.MenuMs = Math.Max(0, r.TotalMs - r.EngineMs - r.BepInExMs - r.PluginsMs - r.PatchingMs);

            foreach (var e in all.Where(e => e.Kind == StartupLines.Bundle)) CountBundle(r, e, pluginsEnd);
            ReadJoin(r, all);
            return r;
        }

        private static double? First(List<StartupEvent> all, string kind)
        {
            var e = all.FirstOrDefault(x => x.Kind == kind);
            return e == null ? (double?)null : e.Ms;
        }

        // "outcome|bytes|ms|key"
        private static void CountBundle(StartupReport r, StartupEvent e, double pluginsEnd)
        {
            var parts = e.Text.Split('|');
            if (parts.Length < 3) return;
            long.TryParse(parts[1], NumberStyles.Integer, Ci, out long bytes);
            double.TryParse(parts[2], NumberStyles.Float, Ci, out double ms);
            switch (parts[0])
            {
                case BundleOutcome.Copy: r.FromCopies++; break;
                case BundleOutcome.Plain: r.NotLzma++; break;
                case BundleOutcome.Failed: r.Failed++; r.LoadedUsual++; break;
                default: r.LoadedUsual++; break;
            }
            r.BundleMs += ms;
            if (e.Ms >= pluginsEnd) return;
            var owner = r.Plugins.LastOrDefault(p => p.StartMs <= e.Ms);
            if (owner == null) return;
            owner.Bundles++;
            owner.BundleBytes += bytes;
            owner.BundleMs += ms;
        }

        private static void ReadJoin(StartupReport r, List<StartupEvent> all)
        {
            int start = all.FindLastIndex(e => e.Kind == StartupLines.JoinStart);
            if (start < 0) return;
            var join = all.Skip(start).ToList();
            var world = join.FirstOrDefault(e => e.Kind == StartupLines.JoinWorld);
            r.Joined = world != null;
            r.Joining = world == null;
            if (world != null) r.JoinMs = world.Ms - join[0].Ms;
            foreach (var e in join)
            {
                var parts = e.Text.Split('|');
                if (parts.Length < 2 || !double.TryParse(parts[parts.Length - 1], NumberStyles.Float, Ci, out double ms)) continue;
                string label = string.Join("|", parts, 0, parts.Length - 1);
                if (e.Kind == StartupLines.JoinStep && label.EndsWith("RPC_PeerInfo", StringComparison.Ordinal)) r.HandshakeMs = Math.Max(r.HandshakeMs, ms);
                if (e.Kind == StartupLines.JoinMessage && !label.EndsWith(": PeerInfo", StringComparison.Ordinal)) r.JoinMessages.Add(new KeyValuePair<string, double>(label, ms));
            }
            r.JoinMessages.Sort((a, b) => b.Value.CompareTo(a.Value));
        }

        // ------------------------------------------------------------------ text

        /// <summary>Tenths of a second below ten seconds, whole seconds below a minute, then minutes and seconds.</summary>
        public static string Duration(double ms)
        {
            if (ms < 10000) return (ms / 1000).ToString("0.0", Ci) + " s";
            long secs = (long)Math.Round(ms / 1000, MidpointRounding.AwayFromZero);
            if (secs < 60) return secs.ToString(Ci) + " s";
            return (secs / 60).ToString(Ci) + " min " + (secs % 60).ToString(Ci) + " s";
        }

        public static string Badge(StartupReport r) => r == null || r.TotalMs <= 0 ? null : Duration(r.TotalMs) + (r.Finished ? "" : " so far");

        public static string Summary(StartupReport r)
        {
            if (r == null || r.TotalMs <= 0) return "no start recorded";
            var parts = new List<string> { "Unity " + Duration(r.EngineMs), "BepInEx " + Duration(r.BepInExMs), "plugins " + Duration(r.PluginsMs) };
            if (r.PatchingMs > 0) parts.Add("patching " + Duration(r.PatchingMs));
            string end = r.Server ? "world" : "menu";
            if (r.Finished) parts.Add(end + " " + Duration(r.MenuMs));
            string head = !r.Finished ? Duration(r.TotalMs) + " so far: the start did not reach the menu"
                : Duration(r.TotalMs) + (r.Server ? " to the running world" : " to the menu");
            return head + " (" + string.Join(", ", parts) + ")";
        }

        public static string Slowest(StartupReport r, int count) =>
            string.Join(", ", SlowestFirst(r).Take(count).Select(p => p.Name + " " + Duration(p.Ms)));

        private static List<PluginTime> SlowestFirst(StartupReport r) =>
            r == null ? new List<PluginTime>() : r.Plugins.OrderByDescending(p => p.Ms).ToList();

        public static string JoinLine(StartupReport r)
        {
            if (r == null || (!r.Joined && !r.Joining)) return "not joined a world yet this session";
            if (r.Joining) return "joining now";
            var parts = new List<string>();
            if (r.HandshakeMs > 0) parts.Add("handshake " + Duration(r.HandshakeMs));
            parts.AddRange(r.JoinMessages.Take(2).Select(m => m.Key + " " + Duration(m.Value)));
            return Duration(r.JoinMs) + (parts.Count > 0 ? " (" + string.Join(", ", parts) + ")" : "");
        }

        public static string BundleLine(StartupReport r)
        {
            if (r == null) return "";
            int lzma = r.FromCopies + r.LoadedUsual;
            if (lzma == 0 && r.NotLzma == 0) return "no bundles loaded through the cache";
            string line = string.Format(Ci, "{0} of {1} LZMA bundles from fast copies", r.FromCopies, lzma);
            if (r.LoadedUsual > 0) line += string.Format(Ci, ", {0} loaded the usual way (copies are made for the next start)", r.LoadedUsual - r.Failed);
            if (r.Failed > 0) line += string.Format(Ci, ", {0} whose copy failed", r.Failed);
            if (r.NotLzma > 0) line += string.Format(Ci, "; {0} left alone (not LZMA, or under 1 MB)", r.NotLzma);
            return line;
        }

        /// <summary>The startup report's text: the summary, every plugin slowest first (up to <paramref name="top"/>), bundles, the join.</summary>
        public static List<string> Lines(StartupReport r, int top)
        {
            var lines = new List<string> { Summary(r), "" };
            if (r == null) return lines;
            lines.Add("Bundles: " + BundleLine(r) + string.Format(Ci, ", {0} in all", Duration(r.BundleMs)));
            lines.Add("Join: " + JoinLine(r));
            lines.Add("");
            lines.Add(string.Format(Ci, "Slowest plugins, of {0} (from its Loading line to the next plugin's: its assembly, Awake and bundles):", r.Plugins.Count));
            foreach (var p in SlowestFirst(r).Take(top))
            {
                string bundles = p.Bundles == 0 ? "" : string.Format(Ci, "   {0} bundle{1}, {2:0.0} MB, {3}", p.Bundles, p.Bundles == 1 ? "" : "s", p.BundleBytes / 1e6, Duration(p.BundleMs));
                lines.Add(string.Format(Ci, "  {0,10}  {1}{2}", Duration(p.Ms), p.Name, bundles));
            }
            return lines;
        }
    }
}
