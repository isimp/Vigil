using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// Questions sent to the server's Vigil and not yet answered. Each answer reaches the one who
    /// asked, once, and says whether the server did what was asked ("ok" other than 0). A question
    /// left unanswered ends after the timeout, and all of them when the connection ends, each with
    /// a message instead of an answer; an answer arriving after that is ignored.
    /// </summary>
    internal sealed class PendingRequests
    {
        private sealed class Request
        {
            public string Command;
            public double Sent;
            public Action<bool, Dictionary<string, string>> Answer;
        }

        private readonly double _timeout;
        private readonly Dictionary<int, Request> _open = new Dictionary<int, Request>();
        private int _next = 1;

        public PendingRequests(double timeoutSeconds) => _timeout = timeoutSeconds;

        public int Count => _open.Count;

        /// <summary>Remembers a question; the returned id goes out with it and comes back with the answer.</summary>
        public int Add(string command, double now, Action<bool, Dictionary<string, string>> answer)
        {
            int id = _next++;
            _open[id] = new Request { Command = command, Sent = now, Answer = answer };
            return id;
        }

        /// <summary>Hands an answer to its question; false when nothing with that id is open.</summary>
        public bool Complete(int id, Dictionary<string, string> reply, double now)
        {
            if (!_open.TryGetValue(id, out var r)) return false;
            _open.Remove(id);
            reply ??= new Dictionary<string, string>();
            bool ok = !reply.TryGetValue("ok", out string flag) || flag != "0";
            r.Answer?.Invoke(ok, reply);
            return true;
        }

        /// <summary>Ends every question older than the timeout with a message.</summary>
        public void Expire(double now)
        {
            foreach (var kv in _open.Where(kv => now - kv.Value.Sent > _timeout).ToList())
            {
                _open.Remove(kv.Key);
                string text = string.Format(CultureInfo.InvariantCulture, "the server did not answer \"{0}\" within {1:0} s", kv.Value.Command, _timeout);
                kv.Value.Answer?.Invoke(false, new Dictionary<string, string> { { "text", text } });
            }
        }

        /// <summary>Ends every open question with the same message, as when the connection ends.</summary>
        public void Drop(string why)
        {
            var all = _open.Values.ToList();
            _open.Clear();
            foreach (var r in all) r.Answer?.Invoke(false, new Dictionary<string, string> { { "text", why } });
        }
    }
}
