using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// A player's mods and configs against the server's (<see cref="Parity"/>), for any player: the
    /// server answers "parity" with its plugins and a fingerprint of each config file, worked out
    /// at most once a minute; the player's game works out its own the same way when asked, compares
    /// and shows the result on the Health tab.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private const long MaxConfigBytes = 2L * 1024 * 1024;
        private const int MaxConfigs = 600;
        private static readonly string[] ConfigKinds = { ".cfg", ".yml", ".yaml", ".json", ".txt", ".ini", ".xml" };

        private Dictionary<string, object> _parityCache;
        private double _parityCacheAt = double.NegativeInfinity;
        private bool _parityAsking;
        private string _parityError;

        private Dictionary<string, object> ParityReply()
        {
            if (_parityCache == null || Now - _parityCacheAt > 60)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                _parityCache = Parity.Format(OwnMods(), OwnConfigs()).ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                _parityCacheAt = Now;
                _recorder?.AddOwnTime(watch.Elapsed.TotalMilliseconds);
            }
            return _parityCache;
        }

        private static List<Parity.Mod> OwnMods() =>
            Chainloader.PluginInfos.Values.Select(p => new Parity.Mod(p.Metadata.GUID, p.Metadata.Name, p.Metadata.Version.ToString())).ToList();

        /// <summary>Every settings file under the config folder by its path there ("/" between folders), Vigil's own left out.</summary>
        private static Dictionary<string, string> OwnConfigs()
        {
            var configs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string root = Paths.ConfigPath;
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (configs.Count >= MaxConfigs) break;
                try
                {
                    var info = new FileInfo(f);
                    if (info.Length > MaxConfigBytes || !ConfigKinds.Contains(info.Extension.ToLowerInvariant())) continue;
                    string rel = f.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                    if (rel.Equals("isimp.Vigil.cfg", StringComparison.OrdinalIgnoreCase)) continue;
                    configs[rel] = Parity.Fingerprint(info.Name, File.ReadAllText(f));
                }
                catch (Exception) { }
            }
            return configs;
        }

        // ------------------------------------------------------------------ the panel

        private PanelBadge ParityBadge()
        {
            var last = ResultStore.Shared.Last(ResultStore.Parity);
            return _parityAsking ? new PanelBadge("comparing", Skin.Tone.Accent) : last != null ? new PanelBadge(last.Badge, Skin.Of(last.BadgeTone)) : null;
        }

        private void DrawParity()
        {
            string lacking = _host.Link.Server.Lacking(Greeting.Parity, "compare mods and configs", adminOnly: false);
            if (lacking != null)
            {
                Ui.Note(Sentence(lacking) + " The comparison needs the server to run Vigil 0.9 or later.");
                return;
            }
            if (_parityAsking) Ui.Tool("Compare with the server", "Asking the server for its mods and configs ...", "Comparing", enabled: false);
            else if (Ui.Tool("Compare with the server", "Mods whose versions differ, configs with other settings, mods only one side has", "Compare")) AskParity();
            if (_parityError != null) Ui.Note(Sentence(_parityError));
            ShowResult(ResultStore.Parity);
        }

        private void AskParity()
        {
            _parityAsking = true;
            _parityError = null;
            _host.Link.Ask("parity", null, (ok, reply) =>
            {
                _parityAsking = false;
                if (!ok)
                {
                    _parityError = reply.TryGetValue("text", out string t) ? t : "the server did not answer";
                    return;
                }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var (mods, configs) = Parity.Parse(reply);
                    var report = Parity.Compare(OwnMods(), mods, OwnConfigs(), configs);
                    ResultStore.Shared.Put(ResultStore.Parity, ParityResult.Build(report, DateTime.Now));
                }
                catch (Exception e) { _parityError = "the comparison failed: " + e.Message; }
                _recorder?.AddOwnTime(watch.Elapsed.TotalMilliseconds);
            });
        }
    }
}
