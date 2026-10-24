using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Vigil.Core
{
    /// <summary>
    /// A file's keys copied once, for a run of checks: read-only like the array BepInEx's
    /// <c>ConfigFile.Keys</c> hands out, listing the keys in the order copied, and answering
    /// <see cref="Contains"/> from a set made with the same equality the array's search uses
    /// (<see cref="EqualityComparer{T}.Default"/>). With an agreement, its first answers are asked of the
    /// copy too, and after a disagreement only of the copy.
    /// </summary>
    internal sealed class KeySnapshot<T> : ICollection<T>
    {
        private readonly T[] _keys;
        private readonly HashSet<T> _set;
        private readonly KeyCheckAgreement _agreement;

        public KeySnapshot(T[] keys, KeyCheckAgreement agreement = null)
        {
            _keys = (T[])keys.Clone();
            _set = new HashSet<T>(_keys, EqualityComparer<T>.Default);
            _agreement = agreement;
        }

        public int Count => _keys.Length;

        public bool IsReadOnly => true;

        public bool Contains(T item)
        {
            if (_agreement == null) return _set.Contains(item);
            if (!_agreement.UseKey) return Array.IndexOf(_keys, item) >= 0;
            bool bySet = _set.Contains(item);
            if (_agreement.Comparing)
            {
                bool byCopy = Array.IndexOf(_keys, item) >= 0;
                _agreement.Compared(bySet, byCopy);
                if (!_agreement.UseKey) return byCopy;
            }

            _agreement.Answer();
            return bySet;
        }

        public void CopyTo(T[] array, int arrayIndex) => _keys.CopyTo(array, arrayIndex);

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_keys).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Add(T item) => throw new NotSupportedException("Collection was of a fixed size.");

        public bool Remove(T item) => throw new NotSupportedException("Collection was of a fixed size.");

        public void Clear() => throw new NotSupportedException("Collection was of a fixed size.");
    }

    /// <summary>The snapshots of one sync: one per file, made again when the file's count of settings changed.</summary>
    internal sealed class KeySnapshots<TFile, T> where TFile : class
    {
        private readonly Dictionary<TFile, KeyValuePair<int, KeySnapshot<T>>> _files = new Dictionary<TFile, KeyValuePair<int, KeySnapshot<T>>>(new SameObject());
        private readonly KeyCheckAgreement _agreement;

        public KeySnapshots(KeyCheckAgreement agreement = null)
        {
            _agreement = agreement;
        }

        public KeySnapshot<T> Get(TFile file, int count, Func<T[]> copy)
        {
            if (_files.TryGetValue(file, out var kept) && kept.Key == count) return kept.Value;
            var snapshot = new KeySnapshot<T>(copy(), _agreement);
            _files[file] = new KeyValuePair<int, KeySnapshot<T>>(count, snapshot);
            return snapshot;
        }

        public void Clear() => _files.Clear();

        private sealed class SameObject : IEqualityComparer<TFile>
        {
            public bool Equals(TFile x, TFile y) => ReferenceEquals(x, y);

            public int GetHashCode(TFile obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }

    /// <summary>
    /// The live check of the snapshots: their first answers are asked both ways, of the set and of the
    /// copy of the keys, and at the first disagreement only the copy is asked, for good.
    /// </summary>
    internal sealed class KeyCheckAgreement
    {
        private readonly object _gate = new object();
        private int _left;

        public KeyCheckAgreement(int compares)
        {
            _left = compares;
        }

        /// <summary>Whether the snapshot's set answers (true) or the copy of the keys, as Jotunn does.</summary>
        public bool UseKey { get; private set; } = true;

        /// <summary>Whether the next answer is to be asked both ways.</summary>
        public bool Comparing
        {
            get { lock (_gate) return UseKey && _left > 0; }
        }

        /// <summary>Answers asked both ways that were the same.</summary>
        public int Agreed { get; private set; }

        /// <summary>Checks answered by a snapshot's set.</summary>
        public long Answered { get; private set; }

        /// <summary>Why the copy of the keys is asked again; null while the two ways agree.</summary>
        public string Problem { get; private set; }

        public void Compared(bool bySet, bool byCopy)
        {
            lock (_gate)
            {
                if (bySet != byCopy)
                {
                    UseKey = false;
                    _left = 0;
                    Problem = "the snapshot of the keys answered a check differently from the copy, so Jotunn's own way is back";
                    return;
                }

                Agreed++;
                if (_left > 0) _left--;
            }
        }

        public void Answer()
        {
            lock (_gate) Answered++;
        }

        /// <summary>What the patch on Jotunn's config sync does on this game, in words.</summary>
        public static string StateLine(bool on, bool found, string version, string problem, long answered, int agreed)
        {
            if (!on) return "off: this game's switch (Patches.JotunnConfigSync)";
            if (!found) return "not needed: Jotunn is not installed";
            if (problem != null) return "stepped aside: " + problem;
            if (answered == 0) return "in · Jotunn " + version + ": waiting for synced settings to arrive";
            var ci = CultureInfo.InvariantCulture;
            return "working · Jotunn " + version + ": " + answered.ToString("N0", ci) + " checks answered from one snapshot of the keys per sync, the first "
                + agreed.ToString(ci) + " asked both ways with the same answer";
        }
    }
}
