using System;
using System.Collections.Generic;

namespace Vigil.Core
{
    /// <summary>
    /// The last result of each tool and the session's last incidents, as the panel shows them: a
    /// tool's result under the tool, an incident's when its row in the list is opened. Tools put
    /// their results in on the game's main thread, where the panel reads them.
    /// </summary>
    internal sealed class ResultStore
    {
        public const int IncidentsKept = 8;

        public const string Census = "census", Memory = "memory", Patches = "patches", Trace = "trace", Deep = "deep", Load = "load", Parity = "parity", Busiest = "busiest";

        private readonly Dictionary<string, Result> _last = new Dictionary<string, Result>(StringComparer.Ordinal);
        private readonly List<Result> _incidents = new List<Result>();

        /// <summary>The one the plugin's tools and its panel share.</summary>
        public static ResultStore Shared { get; } = new ResultStore();

        /// <summary>The session's last incidents, newest first.</summary>
        public IReadOnlyList<Result> Incidents => _incidents;

        /// <summary>False on a dedicated server, which has no panel: tools and incidents then build no result, and the store keeps none.</summary>
        public bool Shown { get; set; } = true;

        public void Put(string tool, Result result)
        {
            if (Shown) _last[tool] = result;
        }

        /// <summary>The tool's last result; null when it brought none, or it was closed.</summary>
        public Result Last(string tool) => _last.TryGetValue(tool, out var r) ? r : null;

        /// <summary>Closes the tool's result: the panel shows none until the tool runs again.</summary>
        public void Forget(string tool) => _last.Remove(tool);

        /// <summary>Whether the tool brought a result since its last one was <paramref name="before"/> (null for none).</summary>
        public bool CameSince(string tool, Result before)
        {
            var now = Last(tool);
            return now != null && !ReferenceEquals(now, before);
        }

        public void AddIncident(Result result)
        {
            if (!Shown) return;
            _incidents.Insert(0, result);
            if (_incidents.Count > IncidentsKept) _incidents.RemoveAt(_incidents.Count - 1);
        }
    }
}
