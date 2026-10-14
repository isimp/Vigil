using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Vigil.Core
{
    /// <summary>
    /// The real connection under wrappers that other mods put around a player's socket. ServerSync,
    /// bundled in many mods, wraps a joining player's socket in its own BufferingSocket while it sends
    /// its configs; every bundled copy wraps the one before it and takes off only a wrapper of its own
    /// type, so wrappers can stay on for the whole session. A wrapper keeps the socket it wraps in an
    /// instance field typed as the socket interface; static fields are never followed (the game's
    /// PlayFab socket, which ServerSync's wrapper derives from, keeps its listen socket in one).
    /// </summary>
    public static class SocketChain
    {
        /// <summary>More wrappers than this are given up on; 41 mods in the pack bundle ServerSync.</summary>
        public const int MaxWrappers = 256;

        public enum Stop { Found, Missing, NotTarget, Loop, TooDeep }

        public sealed class Result
        {
            /// <summary>The connection that was looked for, or null.</summary>
            public object Found;
            /// <summary>The last object reached: the connection found, or where the search ended.</summary>
            public object Last;
            public Stop Stopped;
            /// <summary>The wrappers passed on the way, outermost first.</summary>
            public readonly List<Type> Wrappers = new List<Type>();

            /// <summary>Why nothing was found, for the game log; <paramref name="target"/> names what was looked for.</summary>
            public string WhyNot(string target)
            {
                switch (Stopped)
                {
                    case Stop.Missing: return "the connection has no socket";
                    case Stop.Loop: return "the wrappers around the connection lead back to themselves";
                    case Stop.TooDeep: return "more than " + MaxWrappers + " wrappers around the connection";
                    case Stop.NotTarget:
                        string under = "";
                        if (Wrappers.Count > 0)
                        {
                            var names = new List<string>();
                            foreach (var t in Wrappers) if (!names.Contains(t.Name)) names.Add(t.Name);
                            under = " under " + Count(Wrappers.Count) + " (" + string.Join(", ", names.ToArray()) + ")";
                        }
                        return "the connection is a " + Last.GetType().Name + under + ", not " + target;
                    default: return null;
                }
            }
        }

        /// <summary>
        /// Follows <paramref name="socket"/> through its wrappers until <paramref name="isTarget"/>
        /// accepts one. A wrapper is anything with a non-null instance field of type
        /// <paramref name="socketType"/> (its own fields first, then its base classes'); the first such
        /// field is followed.
        /// </summary>
        public static Result Find(object socket, Type socketType, Func<object, bool> isTarget)
        {
            var r = new Result();
            if (socket == null) { r.Stopped = Stop.Missing; return r; }
            var seen = new HashSet<object>(SameObject.Instance);
            object cur = socket;
            while (true)
            {
                r.Last = cur;
                if (isTarget(cur)) { r.Found = cur; r.Stopped = Stop.Found; return r; }
                if (!seen.Add(cur)) { r.Stopped = Stop.Loop; return r; }
                object inner = Inner(cur, socketType);
                if (inner == null) { r.Stopped = Stop.NotTarget; return r; }
                if (r.Wrappers.Count == MaxWrappers) { r.Stopped = Stop.TooDeep; return r; }
                r.Wrappers.Add(cur.GetType());
                cur = inner;
            }
        }

        private static object Inner(object wrapper, Type socketType)
        {
            const BindingFlags own = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type t = wrapper.GetType(); t != null; t = t.BaseType)
                foreach (var f in t.GetFields(own))
                {
                    if (!socketType.IsAssignableFrom(f.FieldType)) continue;
                    object v = f.GetValue(wrapper);
                    if (v != null) return v;
                }
            return null;
        }

        /// <summary>
        /// "read through 3 wrappers: BufferingSocket from Seasonality, TargetPortal; OtherWrapper from
        /// SomeMod": each wrapper as (type name, assembly name), outermost first, grouped by type name.
        /// </summary>
        public static string Describe(IReadOnlyList<KeyValuePair<string, string>> wrappers)
        {
            if (wrappers.Count == 0) return "read directly, no wrappers";
            var order = new List<string>();
            var from = new Dictionary<string, List<string>>();
            foreach (var w in wrappers)
            {
                if (!from.TryGetValue(w.Key, out var list)) { from[w.Key] = list = new List<string>(); order.Add(w.Key); }
                list.Add(w.Value);
            }
            var sb = new StringBuilder("read through " + Count(wrappers.Count) + ":");
            for (int i = 0; i < order.Count; i++)
                sb.Append(i == 0 ? " " : "; ").Append(order[i]).Append(" from ").Append(string.Join(", ", from[order[i]].ToArray()));
            return sb.ToString();
        }

        private static string Count(int n) => n + (n == 1 ? " wrapper" : " wrappers");

        /// <summary>Compares by identity: a wrapper may override Equals.</summary>
        private sealed class SameObject : IEqualityComparer<object>
        {
            public static readonly SameObject Instance = new SameObject();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
        }
    }
}
