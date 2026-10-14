using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Vigil.Core
{
    /// <summary>
    /// Loaded objects around the player per object type, with what they ask of the renderer:
    /// active renderers, shadow-casting renderers, lights, particle systems, playing sounds, and the
    /// renderers that draw with a material copy of their own (they cannot be drawn together with
    /// others of their kind), plus the mod the type comes from when it is known.
    /// The first row sums every type; the rest are sorted by renderers, most first. The same can
    /// be added up per mod.
    /// </summary>
    public sealed class Census
    {
        public const string Header = "prefab\tobjects\trenderers\tshadow_casters\tlights\tparticle_systems\tsounds\town_materials\tmod";
        public const string OwnerHeader = "mod\tobjects\trenderers\tshadow_casters\tlights\tparticle_systems\tsounds\town_materials";

        private sealed class Row { public int Objects, Renderers, Shadows, Lights, Particles, Audio, Copied; public string Owner; }

        private readonly Dictionary<string, Row> _rows = new Dictionary<string, Row>(StringComparer.Ordinal);

        public void Add(string prefab, int renderers, int shadowCasters, int lights, int particles, int audio, int copied = 0, string owner = null)
        {
            if (!_rows.TryGetValue(prefab ?? "?", out var r)) _rows[prefab ?? "?"] = r = new Row();
            r.Objects++; r.Renderers += renderers; r.Shadows += shadowCasters; r.Lights += lights; r.Particles += particles; r.Audio += audio; r.Copied += copied;
            if (r.Owner == null) r.Owner = owner;
        }

        private static Row Sum(IEnumerable<Row> rows)
        {
            var list = rows.ToList();
            return new Row
            {
                Objects = list.Sum(r => r.Objects), Renderers = list.Sum(r => r.Renderers), Shadows = list.Sum(r => r.Shadows),
                Lights = list.Sum(r => r.Lights), Particles = list.Sum(r => r.Particles), Audio = list.Sum(r => r.Audio), Copied = list.Sum(r => r.Copied),
            };
        }

        /// <summary>Every type with its counts and mod, most renderers first, for the panel.</summary>
        public List<(string Prefab, string Owner, int Objects, int Renderers, int Shadows, int Lights, int Particles, int Audio, int Copied)> Rows() =>
            _rows.OrderByDescending(k => k.Value.Renderers).ThenByDescending(k => k.Value.Objects).ThenBy(k => k.Key, StringComparer.Ordinal)
                .Select(k => (k.Key, k.Value.Owner, k.Value.Objects, k.Value.Renderers, k.Value.Shadows, k.Value.Lights, k.Value.Particles, k.Value.Audio, k.Value.Copied)).ToList();

        public string Format()
        {
            var sb = new StringBuilder();
            sb.Append(Header).Append("\r\n");
            Line(sb, "(all)", Sum(_rows.Values), "");
            foreach (var kv in _rows.OrderByDescending(k => k.Value.Renderers).ThenByDescending(k => k.Value.Objects).ThenBy(k => k.Key, StringComparer.Ordinal))
                Line(sb, kv.Key.Replace('\t', ' '), kv.Value, kv.Value.Owner ?? "");
            return sb.ToString();
        }

        /// <summary>The same per mod, most renderers first; types of no known mod in one row at the end.</summary>
        public string FormatByOwner()
        {
            var sb = new StringBuilder();
            sb.Append(OwnerHeader).Append("\r\n");
            foreach (var g in _rows.Values.Where(r => r.Owner != null).GroupBy(r => r.Owner).Select(g => new { g.Key, Row = Sum(g) })
                .OrderByDescending(g => g.Row.Renderers).ThenBy(g => g.Key, StringComparer.Ordinal))
                Line(sb, g.Key.Replace('\t', ' '), g.Row, null);
            var unmatched = _rows.Values.Where(r => r.Owner == null).ToList();
            if (unmatched.Count > 0) Line(sb, ModMemory.Unmatched, Sum(unmatched), null);
            return sb.ToString();
        }

        private static void Line(StringBuilder sb, string name, Row r, string owner)
        {
            var ci = CultureInfo.InvariantCulture;
            sb.Append(name).Append('\t').Append(r.Objects.ToString(ci)).Append('\t').Append(r.Renderers.ToString(ci)).Append('\t')
              .Append(r.Shadows.ToString(ci)).Append('\t').Append(r.Lights.ToString(ci)).Append('\t').Append(r.Particles.ToString(ci)).Append('\t')
              .Append(r.Audio.ToString(ci)).Append('\t').Append(r.Copied.ToString(ci));
            if (owner != null) sb.Append('\t').Append(owner.Replace('\t', ' '));
            sb.Append("\r\n");
        }
    }
}
