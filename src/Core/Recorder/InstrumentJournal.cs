using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// Crash safety for installing hooks. Before each hook is installed its id is written to a
    /// journal, and again once it is in place. If the game dies in between, the next launch finds
    /// the unfinished entry and adds that id to a skip list that later launches honour. A marker
    /// file tells whether the previous session quit normally. Nothing here ever throws: without a
    /// writable folder every hook is simply allowed.
    /// </summary>
    public sealed class InstrumentJournal : IDisposable
    {
        private const string JournalFile = "hooks.journal";
        private const string SkipFile = "hooks.skip";
        private const string RunningFile = "session.running";

        private readonly string _dir;
        private readonly HashSet<string> _skip = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _newlySkipped = new List<string>();
        private StreamWriter _journal;

        public InstrumentJournal(string stateDir)
        {
            _dir = stateDir;
            PreviousEndedCleanly = true;
            try
            {
                Directory.CreateDirectory(_dir);
                string skipPath = Path.Combine(_dir, SkipFile);
                if (File.Exists(skipPath))
                    foreach (var l in File.ReadAllLines(skipPath)) if (l.Trim().Length > 0) _skip.Add(l.Trim());

                string journalPath = Path.Combine(_dir, JournalFile);
                if (File.Exists(journalPath))
                {
                    var open = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var l in File.ReadAllLines(journalPath))
                    {
                        if (l.StartsWith("begin ", StringComparison.Ordinal)) open.Add(l.Substring(6));
                        else if (l.StartsWith("done ", StringComparison.Ordinal)) open.Remove(l.Substring(5));
                    }
                    foreach (var id in open.Where(id => _skip.Add(id)))
                    {
                        _newlySkipped.Add(id);
                        File.AppendAllText(skipPath, id + Environment.NewLine);
                    }
                }

                string running = Path.Combine(_dir, RunningFile);
                PreviousEndedCleanly = !File.Exists(running);
                if (!PreviousEndedCleanly)
                {
                    PreviousStarted = File.ReadAllText(running).Trim();
                    PreviousLastSeen = File.GetLastWriteTime(running);
                }
                File.WriteAllText(running, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                _journal = new StreamWriter(new FileStream(journalPath, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            }
            catch (Exception) { _journal = null; }
        }

        /// <summary>False when the previous session neither quit normally nor was the first one.</summary>
        public bool PreviousEndedCleanly { get; }

        /// <summary>When a previous session that did not quit normally began (as its marker says); null otherwise.</summary>
        public string PreviousStarted { get; }

        /// <summary>When a previous session that did not quit normally last touched its marker (<see cref="Heartbeat"/>); null otherwise.</summary>
        public DateTime? PreviousLastSeen { get; }

        /// <summary>Touches the marker, so a session that dies later leaves the time it was last alive.</summary>
        public void Heartbeat()
        {
            try { File.SetLastWriteTime(Path.Combine(_dir, RunningFile), DateTime.Now); } catch (Exception) { }
        }

        /// <summary>Hooks put on the skip list at this launch because the last session died installing them.</summary>
        public IReadOnlyCollection<string> NewlySkipped => _newlySkipped;

        public IReadOnlyCollection<string> AllSkipped => _skip;

        public bool Allowed(string id) => !_skip.Contains(id);

        public void Begin(string id) => Write("begin " + id);

        public void Done(string id) => Write("done " + id);

        public void MarkCleanExit()
        {
            try { File.Delete(Path.Combine(_dir, RunningFile)); } catch (Exception) { }
        }

        private void Write(string line)
        {
            try { _journal?.WriteLine(line); } catch (Exception) { }
        }

        public void Dispose()
        {
            try { _journal?.Dispose(); } catch (Exception) { }
            _journal = null;
        }
    }
}
