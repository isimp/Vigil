using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using Vigil.Core;
using Check = Vigil.Core.SelfTestReport.Check;
using Outcome = Vigil.Core.SelfTestReport.Result;

namespace Vigil.Recorder
{
    /// <summary>
    /// Server fixes > JoinPackages: Drop That's and Spawn That's join packages built once and sent
    /// to every later join (<see cref="JoinPackagesPatch"/>), built ahead on a dedicated server; the
    /// summary's lines, and the self-test's check that two builds give the same bytes.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private ConfigEntry<bool> _joinPackages;

        private void BindJoinPackages()
        {
            _joinPackages = Config.Bind("Server fixes", "JoinPackages", true,
                "On a server: Drop That and Spawn That build the config packages they send each joining player from scratch, on the server's main loop (0.7 to 0.9 s each, everyone else standing still meanwhile). " +
                "On, the first build is kept and every later join gets the same bytes; a dedicated server builds them a minute after its world is up, while nobody is connected. " +
                "They are built again after dropthat:reload or when the world's location count changes. Off leaves both mods to build their own.");
            JoinPackagesPatch.Enabled = _joinPackages.Value;
            _joinPackages.SettingChanged += (_, __) => JoinPackagesPatch.Enabled = _joinPackages.Value;
        }

        // On the first frame, when every plugin is loaded (Drop That and Spawn That may load after Vigil).
        private void HookJoinPackages()
        {
            JoinPackagesPatch.Hook();
            foreach (var p in new[] { JoinPackagesPatch.DropThatProblem, JoinPackagesPatch.SpawnThatProblem })
                if (p != null && p != "not installed") _host.Log.LogWarning("Vigil: " + p);
        }

        private void UpdateJoinPackages()
        {
            double ms = JoinPackagesPatch.Tick();
            if (ms <= 0) return;
            // Built by Vigil's choice, ahead of a join: a tool of its own, and the frame it lengthened is no hitch.
            OwnTools.JoinAheadMs += ms;
            OwnTools.JoinAheadRuns++;
            _recorder?.AddOwnTime(ms);
        }

        private IEnumerable<string> JoinPackagesLines() =>
            _server ? JoinPackagesPatch.Lines().Select(l => "Join packages: " + l) : Enumerable.Empty<string>();

        // Two builds of each mod's packages on this game, compared: the cache is right only if they come out the same.
        private IEnumerator JoinPackagesTwice(List<Check> checks)
        {
            const string what = "join packages built twice come out the same";
            var parts = new List<string>();
            var outcome = Outcome.Pass;
            foreach (bool dropThat in new[] { true, false })
            {
                string mod = dropThat ? "Drop That" : "Spawn That";
                string problem = JoinPackagesPatch.SameTwice(dropThat, out double ms);
                if (problem == "not installed") { parts.Add(mod + " not installed"); continue; }
                if (problem != null) { outcome = Outcome.Fail; parts.Add(mod + ": " + problem); continue; }
                parts.Add(mod + " the same (" + ms.ToString("0", CultureInfo.InvariantCulture) + " ms a build here)");
                yield return null;
            }
            bool none = parts.All(p => p.EndsWith("not installed", System.StringComparison.Ordinal));
            checks.Add(new Check(Here, what, none ? Outcome.Info : outcome, string.Join("; ", parts.ToArray())));
        }
    }
}
