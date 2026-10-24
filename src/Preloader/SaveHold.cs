using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Vigil.Core;

namespace Vigil.Startup
{
    /// <summary>
    /// Holds BepInEx config saves during a start. BepInEx rewrites a mod's whole settings file each time
    /// the mod binds a setting, so a mod with a thousand settings writes its file a thousand times while
    /// it loads. While the hold is on, a save only marks its file; <see cref="Release"/> writes each file
    /// once. The files end up as they would have without the hold: of the settings objects on one path,
    /// the last to save is written; a path about to be read back is written first; and a file written
    /// past BepInEx after its last held save (another mod writing it itself) stays as it was written.
    /// </summary>
    internal sealed class SaveHold
    {
        /// <summary>A hold still on this long after it began ends at the next save.</summary>
        public static readonly TimeSpan Cap = TimeSpan.FromMinutes(10);

        public const string CapWhy = "10 min passed";

        private const int MostShown = 3;

        private readonly Func<string, string> _stamp;
        private readonly Action<object> _write;
        private readonly Func<DateTime> _clock;
        private readonly object _gate = new object(), _releaseGate = new object();
        private readonly Dictionary<string, Waiting> _waiting = new Dictionary<string, Waiting>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _files = new HashSet<string>(StringComparer.OrdinalIgnoreCase), _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _readPast = new List<string>();
        private readonly Dictionary<string, int> _counts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> _kept = new List<string>(), _failed = new List<string>();
        private readonly List<object> _written = new List<object>();
        private DateTime _began;
        private long _seq;
        private int _held, _flushed;
        private double _writeMs;

        /// <param name="stamp">A file's size and write time as text, "" when it has none: shows whether someone wrote it meanwhile.</param>
        /// <param name="write">BepInEx's own save of a settings object, past the hold.</param>
        /// <param name="clock">The time now, for the cap.</param>
        public SaveHold(Func<string, string> stamp, Action<object> write, Func<DateTime> clock)
        {
            _stamp = stamp;
            _write = write;
            _clock = clock;
        }

        public bool Holding { get; private set; }

        /// <summary>What the hold did, once it has ended; null before.</summary>
        public SaveHoldResult Result { get; private set; }

        /// <summary>The settings objects written when the hold ended, for the check that each file holds their values.</summary>
        public List<object> Written
        {
            get { lock (_releaseGate) return _written.ToList(); }
        }

        public void Begin()
        {
            lock (_gate)
            {
                if (Holding || Result != null) return;
                Holding = true;
                _began = _clock();
            }
        }

        /// <summary>A save asked for: true when the hold takes it, so the file is not written now.</summary>
        public bool Save(object file, string path, string label)
        {
            if (file == null || path == null) return false;
            lock (_gate)
            {
                if (!Holding) return false;
                if (_clock() - _began <= Cap)
                {
                    string name = string.IsNullOrEmpty(label) ? System.IO.Path.GetFileNameWithoutExtension(path) : label;
                    _waiting[path] = new Waiting(file, path, name, ++_seq, _stamp(path));
                    _files.Add(path);
                    _names.Add(System.IO.Path.GetFileName(path));
                    _held++;
                    _counts[name] = (_counts.TryGetValue(name, out int n) ? n : 0) + 1;
                    return true;
                }
            }

            Release(CapWhy);
            return false;
        }

        /// <summary>
        /// Before a file is read back from disk: its held save is written first, as it would be there
        /// already, unless someone wrote the file after that save, whose write then stands.
        /// </summary>
        public void BeforeRead(string path) => WriteBeforeRead(path);

        /// <summary>
        /// A mod about to open, check or copy a file itself, past BepInEx: as <see cref="BeforeRead"/>,
        /// and the mod is named once for the file when its held save had to be written for it. With
        /// no mod asking (<paramref name="who"/> null: the runtime's own file watcher looking for the
        /// file), the file stays held; the watcher sees it change when it is written.
        /// </summary>
        public void ReadPast(string path, string who)
        {
            if (string.IsNullOrEmpty(who) || !WriteBeforeRead(path)) return;
            string entry = who + ": " + System.IO.Path.GetFileName(path);
            lock (_gate)
            {
                if (!_readPast.Contains(entry)) _readPast.Add(entry);
            }
        }

        /// <summary>Whether a file of this name has a held save: the quick test before a file a mod opens is looked at closer.</summary>
        public bool Holds(string fileName)
        {
            lock (_gate) return Holding && fileName != null && _names.Contains(fileName);
        }

        public bool IsWaiting(string path)
        {
            lock (_gate) return Holding && path != null && _waiting.ContainsKey(path);
        }

        // True when the path's held save was written now.
        private bool WriteBeforeRead(string path)
        {
            Waiting w;
            lock (_gate)
            {
                if (!Holding || path == null || !_waiting.TryGetValue(path, out w)) return false;
                _waiting.Remove(path);
                if (_stamp(path) != w.Stamp)
                {
                    _kept.Add(w.Label);
                    return false;
                }
            }

            if (!Write(w)) return false;
            lock (_gate) _flushed++;
            return true;
        }

        /// <summary>Ends the hold and writes each file with a held save once, in the order of their last saves. A second call gives the first result.</summary>
        public SaveHoldResult Release(string why)
        {
            lock (_releaseGate)
            {
                List<Waiting> pending;
                lock (_gate)
                {
                    if (Result != null || !Holding) return Result;
                    Holding = false;
                    pending = _waiting.Values.OrderBy(w => w.Seq).ToList();
                    _waiting.Clear();
                }

                int written = 0;
                foreach (var w in pending)
                {
                    if (_stamp(w.Path) != w.Stamp)
                    {
                        _kept.Add(w.Label);
                        continue;
                    }

                    if (Write(w))
                    {
                        written++;
                        _written.Add(w.File);
                    }
                }

                var r = new SaveHoldResult(why, _held, _files.Count, written, _flushed, _writeMs);
                r.Most.AddRange(_counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(MostShown));
                r.Kept.AddRange(_kept);
                r.Failed.AddRange(_failed);
                r.ReadPast.AddRange(_readPast);
                Result = r;
                return r;
            }
        }

        private bool Write(Waiting w)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                _write(w.File);
                return true;
            }
            catch (Exception e)
            {
                lock (_gate) _failed.Add(w.Label + ": " + e.Message);
                return false;
            }
            finally
            {
                lock (_gate) _writeMs += watch.Elapsed.TotalMilliseconds;
            }
        }

        private sealed class Waiting
        {
            public Waiting(object file, string path, string label, long seq, string stamp)
            {
                File = file;
                Path = path;
                Label = label;
                Seq = seq;
                Stamp = stamp;
            }

            public object File { get; }

            public string Path { get; }

            public string Label { get; }

            public long Seq { get; }

            /// <summary>The file on disk as it was at this save.</summary>
            public string Stamp { get; }
        }
    }
}
