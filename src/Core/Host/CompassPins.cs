using System;
using System.Collections.Generic;
using System.Globalization;

namespace Vigil.Core
{
    /// <summary>
    /// Where a HUDCompass pin icon belongs. HUDCompass 1.2.1 names each icon after its pin's position
    /// as Unity prints it, two decimals per axis, and finds the icon by that name, so pins on one spot
    /// share an icon. The key keeps that: each axis rounded to the centimetre, without any text.
    /// </summary>
    internal readonly struct PinKey : IEquatable<PinKey>
    {
        private readonly long _x, _y, _z;

        private PinKey(long x, long y, long z)
        {
            _x = x;
            _y = y;
            _z = z;
        }

        public static PinKey Of(float x, float y, float z) => new PinKey(Cm(x), Cm(y), Cm(z));

        private static long Cm(float v) => (long)Math.Round(v * 100.0, MidpointRounding.AwayFromZero);

        public bool Equals(PinKey other) => _x == other._x && _y == other._y && _z == other._z;

        public override bool Equals(object obj) => obj is PinKey k && Equals(k);

        public override int GetHashCode()
        {
            unchecked
            {
                long h = _x * 73856093L ^ _y * 19349663L ^ _z * 83492791L;
                return (int)h ^ (int)(h >> 32);
            }
        }
    }

    /// <summary>
    /// The compass's pin icons from frame to frame: an icon stays with its position while a pin is
    /// there, one no pin was seen at in a frame is handed back, and the next new position takes a
    /// handed-back icon before a new one is made. Up to <c>spareLimit</c> are kept aside; the rest
    /// are handed back to be destroyed.
    /// </summary>
    internal sealed class CompassPinBook<T> where T : class
    {
        private sealed class Slot
        {
            public T Item;
            public int Seen;
        }

        private readonly Dictionary<PinKey, Slot> _live = new Dictionary<PinKey, Slot>();
        private readonly Stack<Slot> _spare = new Stack<Slot>();
        private readonly List<PinKey> _gone = new List<PinKey>();
        private readonly int _spareLimit;
        private int _frame;

        public CompassPinBook(int spareLimit = 64)
        {
            _spareLimit = spareLimit;
        }

        public int Live => _live.Count;
        public int Spare => _spare.Count;

        public void BeginFrame() => _frame++;

        /// <summary>The icon at this position, if there is one; it stays for this frame.</summary>
        public bool TryGet(PinKey key, out T item)
        {
            if (_live.TryGetValue(key, out var slot))
            {
                slot.Seen = _frame;
                item = slot.Item;
                return true;
            }
            item = null;
            return false;
        }

        /// <summary>An icon for a position that has none: one kept aside, or else a new one.</summary>
        public T Place(PinKey key, Func<T> make, out bool reused)
        {
            if (_live.TryGetValue(key, out var slot))
            {
                slot.Seen = _frame;
                reused = false;
                return slot.Item;
            }
            reused = _spare.Count > 0;
            slot = reused ? _spare.Pop() : new Slot { Item = make() };
            slot.Seen = _frame;
            _live[key] = slot;
            return slot.Item;
        }

        /// <summary>Hands back every icon no pin was seen at this frame; those past the spare limit also go to <paramref name="destroy"/>.</summary>
        public void EndFrame(List<T> released, List<T> destroy = null)
        {
            _gone.Clear();
            foreach (var pair in _live)
                if (pair.Value.Seen != _frame) _gone.Add(pair.Key);
            foreach (var key in _gone)
            {
                var slot = _live[key];
                _live.Remove(key);
                released.Add(slot.Item);
                if (_spare.Count < _spareLimit) _spare.Push(slot);
                else destroy?.Add(slot.Item);
            }
        }

        /// <summary>Drops every icon, for a new HUD whose old icons went with the old one.</summary>
        public void Forget()
        {
            _live.Clear();
            _spare.Clear();
        }
    }

    /// <summary>HUDCompass 1.2.1's pin rules, given the same answers without making text for each pin.</summary>
    internal static class CompassPinRules
    {
        /// <summary>HUDCompass's word for a pin's type: the alias of its icon's name, or else the type's name.</summary>
        public static string TypeWord(string iconName, string typeName, Dictionary<string, string> alias)
        {
            if (iconName != null && alias.TryGetValue(iconName, out var word)) return word;
            return typeName;
        }

        public static bool TypeMatches(List<string> matches, string iconName, string typeName, Dictionary<string, string> alias)
        {
            if (matches.Count == 0) return false;
            return matches.Contains(TypeWord(iconName, typeName, alias));
        }

        public static bool TypeOrNameMatches(List<string> matches, string iconName, string typeName, string name, Dictionary<string, string> alias)
        {
            if (matches.Count == 0) return false;
            return TypeMatches(matches, iconName, typeName, alias) || matches.Contains(name);
        }

        /// <summary>Exact names, names starting with a prefix (an empty prefix matches none), names ending with a suffix (an empty one matches all).</summary>
        public static bool NameIgnored(List<string> exact, List<string> prefixes, List<string> suffixes, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (exact.Contains(name)) return true;
            foreach (var p in prefixes)
                if (p.Length > 0 && name.StartsWith(p, StringComparison.Ordinal)) return true;
            foreach (var s in suffixes)
                if (name.EndsWith(s, StringComparison.Ordinal)) return true;
            return false;
        }

        public static bool InBounds(float distance, float min, float max) => distance <= max && distance >= min;

        /// <summary>What the patch does on this game, in words.</summary>
        public static string StateLine(bool on, bool found, string version, string problem, long frames, double ms, int pins, long made, long reused, long theirFrames, double theirMs)
        {
            if (!on)
            {
                string off = "off: this game's switch (Patches.HudCompassPins)";
                if (theirFrames > 0) off += string.Format(CultureInfo.InvariantCulture, " · HUDCompass's own pins {0:0.00} ms a frame", theirMs / theirFrames);
                return off;
            }
            if (!found) return "not needed: HUDCompass is not installed";
            if (problem != null) return "stepped aside: " + problem;
            string line = "working · HUDCompass " + version;
            if (frames == 0) return line + " · no frame yet";
            return line + string.Format(CultureInfo.InvariantCulture, " · {0:0.00} ms a frame for {1} pins · {2} icons made, {3} reused", ms / frames, pins, made, reused);
        }
    }
}
