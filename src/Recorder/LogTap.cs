using System;
using BepInEx.Logging;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// Listens to every BepInEx log line (mods and, through BepInEx, Unity's own log): counts lines,
    /// warnings and errors per second, groups repeats of the same message (numbers ignored) to
    /// find floods, and keeps the most recent lines for incident reports. Log events can arrive
    /// on any thread, hence the lock.
    /// </summary>
    internal sealed class LogTap : ILogListener
    {
        private readonly object _lock = new object();
        private readonly RingBuffer<string> _recent = new RingBuffer<string>(400);
        private readonly MessageTally _spam = new MessageTally();
        private readonly System.Collections.Generic.Dictionary<string, int> _second = new System.Collections.Generic.Dictionary<string, int>();
        private int _lines, _warnings, _errors;
        private long _runLines, _runWarnings, _runErrors;

        public void LogEvent(object sender, LogEventArgs e)
        {
            if (!ProbeState.Active) return;
            try
            {
                string source = e.Source != null ? e.Source.SourceName : "?";
                string text = e.Data != null ? e.Data.ToString() : "";
                string key = source + ": " + LogText.Normalize(text);
                lock (_lock)
                {
                    _lines++; _runLines++;
                    if ((e.Level & (LogLevel.Error | LogLevel.Fatal)) != 0) { _errors++; _runErrors++; }
                    else if ((e.Level & LogLevel.Warning) != 0) { _warnings++; _runWarnings++; }
                    _spam.Add(key, 0);
                    _second.TryGetValue(key, out int n);
                    _second[key] = n + 1;
                    string line = text.Length > 600 ? text.Substring(0, 600) + "..." : text;
                    _recent.Add(DateTime.Now.ToString("HH:mm:ss.fff") + " [" + e.Level + ": " + source + "] " + line.Replace("\r", "").Replace("\n", " | "));
                }
            }
            catch (Exception) { }
        }

        /// <summary>Counts for the second that just ended, and its most repeated message.</summary>
        public void EndSecond(out int lines, out int warnings, out int errors, out string topMessage, out int topCount)
        {
            lock (_lock)
            {
                lines = _lines; warnings = _warnings; errors = _errors;
                topMessage = ""; topCount = 0;
                foreach (var kv in _second)
                    if (kv.Value > topCount) { topCount = kv.Value; topMessage = kv.Key; }
                _lines = _warnings = _errors = 0;
                _second.Clear();
            }
        }

        public string[] Recent(int n) { lock (_lock) return _recent.Last(n); }
        public string SpamWindow(float span) { lock (_lock) return _spam.EndWindow(span, 15); }
        public string SpamWindowPeek() { lock (_lock) return _spam.Peek(10f, 15); }
        public string SpamSummary(float seconds) { lock (_lock) return _spam.Summary(seconds, 20); }

        public string Totals()
        {
            lock (_lock) return _runLines + " lines, " + _runWarnings + " warnings, " + _runErrors + " errors";
        }

        public void Dispose() { }
    }
}
