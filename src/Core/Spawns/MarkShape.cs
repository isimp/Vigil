using System;
using System.Collections.Generic;

namespace Vigil.Core;

/// <summary>How an open spot is shown.</summary>
internal enum MarkStyle
{
    /// <summary>A pillar standing straight up from the spot, with a dot at its foot. Easy to see on uneven ground.</summary>
    Pillars,

    /// <summary>A flat dot lying on the ground.</summary>
    Dots,
}

/// <summary>
/// The geometry of the marks: positions, texture coordinates and triangles of one mesh. A dot is
/// a small square lying on the ground's slope, lifted a little so the ground does not hide it. A
/// pillar is two upright faces crossing at a right angle, so from any side one of them faces the
/// viewer; it stands straight up whatever the slope. A spot below the water level is marked on
/// the surface instead, where it can be seen. The texture holds the dot in its left half and the
/// pillar in its right half.
/// </summary>
internal static class MarkShape
{
    internal const float Lift = 0.08f;

    /// <summary>The mesh data: three floats a vertex, two per texture coordinate, three indices a triangle.</summary>
    internal sealed class Data
    {
        public Data(float[] positions, float[] uvs, int[] triangles)
        {
            Positions = positions;
            Uvs = uvs;
            Triangles = triangles;
        }

        public float[] Positions { get; }

        public float[] Uvs { get; }

        public int[] Triangles { get; }
    }

    public static Data Build(IReadOnlyList<Mark> marks, MarkStyle style, float spacing, float pillarHeight, float waterLevel)
    {
        int quadsPerMark = style == MarkStyle.Pillars ? 3 : 1;
        int quads = marks.Count * quadsPerMark;
        var builder = new Builder(quads);
        float half = Clamp(spacing * 0.3f, 0.25f, 1.5f);
        float width = Clamp(spacing * 0.12f, 0.1f, 0.4f);

        foreach (var mark in marks)
        {
            float y = mark.Y, nx = mark.NormalX, ny = mark.NormalY, nz = mark.NormalZ;
            float length = (float)Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
            if (y < waterLevel || length < 0.1f)
            {
                nx = 0f;
                ny = 1f;
                nz = 0f;
            }
            else
            {
                nx /= length;
                ny /= length;
                nz /= length;
            }

            y = Math.Max(y, waterLevel);
            Dot(builder, mark.X, y, mark.Z, nx, ny, nz, half);
            if (style == MarkStyle.Pillars)
            {
                Pillar(builder, mark.X, y, mark.Z, width, pillarHeight);
            }
        }

        return builder.Done();
    }

    private static void Dot(Builder b, float x, float y, float z, float nx, float ny, float nz, float half)
    {
        // Two directions along the ground: t across the normal, s across both.
        float rx = 0f, ry = 0f, rz = 1f;
        if (Math.Abs(nz) > 0.99f)
        {
            rx = 1f;
            rz = 0f;
        }

        float tx = (ny * rz) - (nz * ry), ty = (nz * rx) - (nx * rz), tz = (nx * ry) - (ny * rx);
        float tl = (float)Math.Sqrt((tx * tx) + (ty * ty) + (tz * tz));
        tx /= tl;
        ty /= tl;
        tz /= tl;
        float sx = (ty * nz) - (tz * ny), sy = (tz * nx) - (tx * nz), sz = (tx * ny) - (ty * nx);

        float cx = x + (nx * Lift), cy = y + (ny * Lift), cz = z + (nz * Lift);
        b.Quad(
            cx - (half * tx) - (half * sx), cy - (half * ty) - (half * sy), cz - (half * tz) - (half * sz),
            cx - (half * tx) + (half * sx), cy - (half * ty) + (half * sy), cz - (half * tz) + (half * sz),
            cx + (half * tx) + (half * sx), cy + (half * ty) + (half * sy), cz + (half * tz) + (half * sz),
            cx + (half * tx) - (half * sx), cy + (half * ty) - (half * sy), cz + (half * tz) - (half * sz),
            0.03f, 0.47f);
    }

    private static void Pillar(Builder b, float x, float y, float z, float width, float height)
    {
        float top = y + height;
        b.Quad(x - width, y, z, x - width, top, z, x + width, top, z, x + width, y, z, 0.53f, 0.97f);
        b.Quad(x, y, z - width, x, top, z - width, x, top, z + width, x, y, z + width, 0.53f, 0.97f);
    }

    private static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;

    private sealed class Builder
    {
        private readonly float[] _positions;
        private readonly float[] _uvs;
        private readonly int[] _triangles;
        private int _quads;

        public Builder(int quads)
        {
            _positions = new float[quads * 12];
            _uvs = new float[quads * 8];
            _triangles = new int[quads * 6];
        }

        /// <summary>Corners in order around the quad: bottom left, top left, top right, bottom right of its texture part.</summary>
        public void Quad(
            float ax, float ay, float az, float bx, float by, float bz,
            float cx, float cy, float cz, float dx, float dy, float dz,
            float uFrom, float uTo)
        {
            int p = _quads * 12, u = _quads * 8, t = _quads * 6, v = _quads * 4;
            float[] corners = { ax, ay, az, bx, by, bz, cx, cy, cz, dx, dy, dz };
            Array.Copy(corners, 0, _positions, p, 12);
            float[] uv = { uFrom, 0.03f, uFrom, 0.97f, uTo, 0.97f, uTo, 0.03f };
            Array.Copy(uv, 0, _uvs, u, 8);
            _triangles[t] = v;
            _triangles[t + 1] = v + 1;
            _triangles[t + 2] = v + 2;
            _triangles[t + 3] = v;
            _triangles[t + 4] = v + 2;
            _triangles[t + 5] = v + 3;
            _quads++;
        }

        public Data Done() => new(_positions, _uvs, _triangles);
    }
}
