using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// The names of the messages on the wire. The game names a message by the hash of its name, so a
    /// package or a routed message carries only the number. The book puts names to the numbers from
    /// three sources, best first: names hashed while the game runs (the game's hash function hands
    /// every name it hashes to <see cref="Learned"/>, names put together at run time included), each
    /// mod's config sync message ("id ConfigSync", as ServerSync names it), and the texts in the game's
    /// and the mods' files (<see cref="ValueNames"/>). A name learned while playing can carry the mod that
    /// hashed it (the game's for its own), which counts where no file ties the name to one mod. It also
    /// counts the kinds of message it was asked about, for how many it could name.
    /// </summary>
    internal sealed class MessageNames
    {
        public const int MostLearned = 200000;
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        private readonly int _most;
        private readonly ConcurrentDictionary<int, string> _learned = new ConcurrentDictionary<int, string>();
        private readonly ConcurrentDictionary<int, string> _owners = new ConcurrentDictionary<int, string>();
        private readonly ConcurrentDictionary<int, byte> _seen = new ConcurrentDictionary<int, byte>();
        private volatile Dictionary<int, KeyValuePair<string, string>> _sync = new Dictionary<int, KeyValuePair<string, string>>();
        private volatile ValueNames _files;

        public MessageNames(int most = MostLearned)
        {
            _most = most;
        }

        /// <summary>A name the game or a mod hashed, and who hashed it when that was looked at (<see cref="ValueNames.Game"/> for the game); any thread. The first name of a number stays, and the book stops at its limit.</summary>
        public void Learned(int hash, string text, string owner = null)
        {
            if (text == null || _learned.ContainsKey(hash) || _learned.Count >= _most) return;
            if (_learned.TryAdd(hash, text) && owner != null) _owners[hash] = owner;
        }

        /// <summary>Whether the book has learned a name for this number while the game ran.</summary>
        public bool Knows(int hash) => _learned.ContainsKey(hash);

        /// <summary>How many learned names have a mod noted as their owner, and how many the game.</summary>
        public (int Mods, int Game) OwnerCounts()
        {
            int game = _owners.Values.Count(o => o == ValueNames.Game);
            return (_owners.Count - game, game);
        }

        /// <summary>The name learned for this number while the game ran (not counted as a message seen); null when none.</summary>
        public string LearnedText(int hash) => _learned.TryGetValue(hash, out var text) ? text : null;

        /// <summary>Who hashed the name first, when that was looked at; null otherwise.</summary>
        public string LearnedOwner(int hash) => _owners.TryGetValue(hash, out var owner) ? owner : null;

        /// <summary>Whether the name of this number was hashed while the game ran (not counted as seen on the wire).</summary>
        public bool LearnedWhileRunning(int hash) => _learned.ContainsKey(hash);

        /// <summary>Each mod's id with its name: its config sync message is "id ConfigSync".</summary>
        public void Sync(IEnumerable<KeyValuePair<string, string>> idToMod)
        {
            var sync = new Dictionary<int, KeyValuePair<string, string>>();
            foreach (var kv in idToMod)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                string name = kv.Key + " ConfigSync";
                int hash = ValueNames.Hash(name);
                if (!sync.ContainsKey(hash)) sync[hash] = new KeyValuePair<string, string>(name, kv.Value);
            }
            _sync = sync;
        }

        /// <summary>The names read from the game's and the mods' files.</summary>
        public void Files(ValueNames names) => _files = names;

        /// <summary>The message's name, or "message N" when no source knows it.</summary>
        public string Label(int hash) => NameOf(hash) ?? "message " + hash.ToString(Ci);

        /// <summary>The message's name; null when no source knows it.</summary>
        public string NameOf(int hash)
        {
            _seen.TryAdd(hash, 0);
            return Text(hash, out _);
        }

        /// <summary>The mod a name belongs to: its config sync, the one mod whose code holds it, else the mod that hashed it while playing; null otherwise.</summary>
        public string ModOf(int hash)
        {
            if (_sync.TryGetValue(hash, out var sync)) return sync.Value;
            string files = _files?.SoleMod(hash);
            if (files != null) return files;
            string learned = LearnedOwner(hash);
            return learned == ValueNames.Game ? null : learned;
        }

        /// <summary>"3 of 4 kinds of message seen named: 1 learned while the game ran, 1 from the files, 1 config sync".</summary>
        public string CountLine()
        {
            int learned = 0, files = 0, sync = 0;
            foreach (int hash in _seen.Keys.ToList())
            {
                Text(hash, out var source);
                if (source == Source.Learned) learned++;
                else if (source == Source.Files) files++;
                else if (source == Source.Sync) sync++;
            }
            int named = learned + files + sync;
            return named.ToString(Ci) + " of " + _seen.Count.ToString(Ci) + " kinds of message seen named: " + learned.ToString(Ci) + " learned while the game ran, "
                + files.ToString(Ci) + " from the files, " + sync.ToString(Ci) + " config sync";
        }

        private enum Source { None, Learned, Sync, Files }

        private string Text(int hash, out Source source)
        {
            if (_learned.TryGetValue(hash, out var learned)) { source = Source.Learned; return learned; }
            if (_sync.TryGetValue(hash, out var sync)) { source = Source.Sync; return sync.Key; }
            var files = _files;
            if (files != null)
            {
                var name = files.Of(hash);
                if (name.Found) { source = Source.Files; return name.Text; }
            }
            source = Source.None;
            return null;
        }
    }

    /// <summary>
    /// A cap on how often something may be done in a window of time: at most <c>most</c> times in each
    /// <c>windowSeconds</c>, the window starting at the first time asked; the times refused are counted.
    /// For the look at who hashed a new name, so a mod making new names all the time costs little.
    /// </summary>
    internal sealed class OwnerLooks
    {
        private readonly int _most;
        private readonly double _window;
        private readonly object _gate = new object();
        private double _start = double.NegativeInfinity;
        private int _count;

        public OwnerLooks(int most, double windowSeconds)
        {
            _most = most;
            _window = windowSeconds;
        }

        public long Refused { get; private set; }

        public bool Allow(double now)
        {
            lock (_gate)
            {
                if (now - _start >= _window)
                {
                    _start = now;
                    _count = 0;
                }
                if (_count >= _most)
                {
                    Refused++;
                    return false;
                }
                _count++;
                return true;
            }
        }
    }
}
