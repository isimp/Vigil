using System;
using System.Collections.Generic;

namespace Vigil.Core
{
    /// <summary>Which mod a network message belongs to.</summary>
    public static class NetOwner
    {
        /// <summary>
        /// The mod whose id starts the name ("guid!name" from Jotunn, "guid ConfigSync" from
        /// ServerSync; the id must be followed by '!' or ' ' or end the name), else the mod whose
        /// assembly handles the message, else "Valheim" for the game's assemblies, else the mod whose
        /// code holds the name (<paramref name="namedIn"/>; the server has no handler for a message
        /// between players), else "unknown".
        /// </summary>
        public static string Resolve(string name, string handlerAssembly,
            IReadOnlyDictionary<string, string> guidToMod, IReadOnlyDictionary<string, string> assemblyToMod, string namedIn = null)
        {
            if (!string.IsNullOrEmpty(name))
            {
                // With the id required to end at '!', ' ' or the end of the name, at most one id can match.
                foreach (var kv in guidToMod)
                {
                    string g = kv.Key;
                    if (g.Length == 0 || !name.StartsWith(g, StringComparison.Ordinal)) continue;
                    if (name.Length > g.Length && name[g.Length] != '!' && name[g.Length] != ' ') continue;
                    return kv.Value;
                }
            }
            if (handlerAssembly != null && assemblyToMod.TryGetValue(handlerAssembly, out var mod)) return mod;
            if (handlerAssembly != null && handlerAssembly.StartsWith("assembly_", StringComparison.Ordinal)) return "Valheim";
            if (handlerAssembly == null && !string.IsNullOrEmpty(namedIn)) return namedIn;
            return handlerAssembly != null ? "unknown (" + handlerAssembly + ")" : "unknown";
        }
    }
}
