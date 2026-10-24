using System;
using System.Collections.Generic;
using System.Reflection;

namespace Vigil.Core
{
    /// <summary>
    /// Finds a type by its full name among the loaded assemblies. Harmony's <c>AccessTools.TypeByName</c>
    /// lists every type of every assembly on each call (twice over for a name it cannot find), which
    /// with a modded game's hundred and more assemblies costs seconds, and logs an error for every
    /// assembly holding a type that cannot load (a mod's support for a mod that is not installed). Here
    /// each assembly is asked for the one name, which it answers from its own table, the answer is kept,
    /// and an assembly loaded later is asked the next time the name comes up.
    /// </summary>
    internal sealed class TypeLookup
    {
        private readonly Func<Assembly[]> _assemblies;
        private readonly Dictionary<string, Type> _found = new Dictionary<string, Type>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<Assembly>> _asked = new Dictionary<string, HashSet<Assembly>>(StringComparer.Ordinal);

        /// <param name="assemblies">The assemblies to look in; asked again for a name not found yet.</param>
        public TypeLookup(Func<Assembly[]> assemblies)
        {
            _assemblies = assemblies ?? (() => Array.Empty<Assembly>());
        }

        /// <summary>The one for the running game: every assembly loaded in it.</summary>
        public static TypeLookup Loaded { get; } = new TypeLookup(() => AppDomain.CurrentDomain.GetAssemblies());

        /// <summary>How many times one assembly was asked for one name.</summary>
        public int Lookups { get; private set; }

        /// <summary>The type with this full name (namespace, a "+" before a nested type's name); null when no loaded assembly has it.</summary>
        public Type Find(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            lock (_found)
            {
                if (_found.TryGetValue(fullName, out var known)) return known;
                if (!_asked.TryGetValue(fullName, out var asked)) _asked[fullName] = asked = new HashSet<Assembly>();
                Assembly[] all;
                try { all = _assemblies() ?? Array.Empty<Assembly>(); }
                catch (Exception) { return null; }
                foreach (var a in all)
                {
                    if (a == null || !asked.Add(a)) continue;
                    Lookups++;
                    Type t;
                    try { t = a.GetType(fullName, false); }
                    catch (Exception) { continue; }
                    if (t == null) continue;
                    _found[fullName] = t;
                    _asked.Remove(fullName);
                    return t;
                }
                return null;
            }
        }
    }
}
