using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Vigil.Core
{
    /// <summary>
    /// Counts network messages by kind (a free-text label such as "RPC_Damage -> Boar") for the
    /// current report window and for the whole session, and formats both as text.
    /// </summary>
    public sealed class MessageTally
    {
        private sealed class Agg { public long Count; public long Bytes; }

        private readonly Dictionary<string, Agg> _window = new Dictionary<string, Agg>();
        private readonly Dictionary<string, Agg> _run = new Dictionary<string, Agg>();
        private readonly bool _byBytes;

        /// <param name="rankByBytes">List the kinds with the most bytes first instead of the most messages.</param>
        public MessageTally(bool rankByBytes = false) { _byBytes = rankByBytes; }

        public void Add(string label, int bytes)
        {
            Bump(_window, label, bytes);
        }

        /// <summary>Formats the window that just ended, folds it into the session, starts a new one.</summary>
        public string EndWindow(float seconds, int top)
        {
            string text = Format(_window, seconds, top);
            Fold();
            return text;
        }

        /// <summary>One kind's bytes over the session, the window in progress included.</summary>
        public long BytesOf(string label) =>
            (_run.TryGetValue(label, out var run) ? run.Bytes : 0) + (_window.TryGetValue(label, out var window) ? window.Bytes : 0);

        /// <summary>The window in progress, formatted, without ending it.</summary>
        public string Peek(float seconds, int top) => Format(_window, seconds, top);

        /// <summary>The whole session so far, including the window still in progress, which goes on whole (a lag report reads it).</summary>
        public string Summary(float seconds, int top)
        {
            var all = new Dictionary<string, Agg>(_run.Count + _window.Count);
            foreach (var kv in _run) all[kv.Key] = new Agg { Count = kv.Value.Count, Bytes = kv.Value.Bytes };
            Add(all, _window);
            return Format(all, seconds, top);
        }

        private void Fold()
        {
            Add(_run, _window);
            _window.Clear();
        }

        private static void Add(Dictionary<string, Agg> into, Dictionary<string, Agg> from)
        {
            foreach (var kv in from)
            {
                if (!into.TryGetValue(kv.Key, out var a)) into[kv.Key] = a = new Agg();
                a.Count += kv.Value.Count; a.Bytes += kv.Value.Bytes;
            }
        }

        private static void Bump(Dictionary<string, Agg> d, string label, int bytes)
        {
            if (!d.TryGetValue(label, out var a)) d[label] = a = new Agg();
            a.Count++; a.Bytes += bytes;
        }

        private string Format(Dictionary<string, Agg> d, float seconds, int top)
        {
            var ci = CultureInfo.InvariantCulture;
            float s = Math.Max(seconds, 0.001f);
            long count = d.Values.Sum(a => a.Count);
            long bytes = d.Values.Sum(a => a.Bytes);
            var sb = new StringBuilder();
            sb.AppendFormat(ci, "{0} messages ({1:0.0}/s), {2:0.0} kB ({3:0.0} kB/s), {4} kinds\r\n",
                count, count / s, bytes / 1024.0, bytes / 1024.0 / s, d.Count);
            foreach (var kv in d.OrderByDescending(k => _byBytes ? k.Value.Bytes : k.Value.Count).ThenBy(k => k.Key, StringComparer.Ordinal).Take(top))
                sb.AppendFormat(ci, "   {0,-64} {1,7}  {2,7:0.0}/s  {3,8:0.0} kB\r\n",
                    kv.Key, kv.Value.Count, kv.Value.Count / s, kv.Value.Bytes / 1024.0);
            if (d.Count > top)
                sb.AppendFormat(ci, "   ... {0} more kinds\r\n", d.Count - top);
            return sb.ToString();
        }
    }
}
