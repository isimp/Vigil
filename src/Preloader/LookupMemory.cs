using System;
using System.Collections.Generic;
using System.Linq;
using Vigil.Core;

namespace Vigil.Startup
{
    /// <summary>
    /// Failed assembly searches remembered during the start. When a mod asks for an assembly that is
    /// not installed (a check for an optional mod), BepInEx searches every folder under BepInEx's own
    /// folder, the patchers and the plugins for its DLL, on every ask. While the start is on, a search
    /// that found nothing in a folder is answered from memory the next time the same assembly is asked
    /// for there; from the menu on, every search runs again.
    /// </summary>
    internal sealed class LookupMemory
    {
        private const int MostShown = 5;

        private readonly object _gate = new object();
        private readonly HashSet<string> _missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _answered = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public bool On { get; private set; }

        /// <summary>What the memory answered, once the start is over; null before.</summary>
        public LookupResult Result { get; private set; }

        public void Begin()
        {
            lock (_gate)
            {
                if (Result == null) On = true;
            }
        }

        /// <summary>Whether a search for this assembly in this folder already found nothing this start: then it is not searched again.</summary>
        public bool KnownMissing(string name, string directory)
        {
            if (name == null || directory == null) return false;
            lock (_gate)
            {
                if (!On || !_missing.Contains(Key(name, directory))) return false;
                string shown = _names.TryGetValue(name, out var first) ? first : name;
                _answered[shown] = (_answered.TryGetValue(shown, out int n) ? n : 0) + 1;
                return true;
            }
        }

        /// <summary>A search for this assembly in this folder found nothing.</summary>
        public void Missing(string name, string directory)
        {
            if (name == null || directory == null) return;
            lock (_gate)
            {
                if (!On) return;
                _missing.Add(Key(name, directory));
                if (!_names.ContainsKey(name)) _names[name] = name;
            }
        }

        /// <summary>Ends the memory for the session; later calls give the first result.</summary>
        public LookupResult End(string why)
        {
            lock (_gate)
            {
                if (Result != null || !On) return Result;
                On = false;
                var r = new LookupResult(why, _answered.Values.Sum(), _answered.Count);
                r.Most.AddRange(_answered.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(MostShown));
                Result = r;
                return r;
            }
        }

        private static string Key(string name, string directory) => name + "\u0001" + directory;
    }
}
