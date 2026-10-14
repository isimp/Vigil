using System.Collections.Generic;
using Vigil.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Vigil.Spawns;

/// <summary>
/// The marks as one mesh, shaped by <see cref="MarkShape"/>, drawn each frame with
/// <c>Graphics.DrawMesh</c>, so they need no game object and cost one draw call.
/// </summary>
internal sealed class MarkMesh
{
    private Mesh _mesh;
    private Material _material;
    private Texture2D _texture;
    private bool _noShader;

    public bool HasMarks => _mesh && _mesh.vertexCount > 0;

    public void Build(IReadOnlyList<Mark> marks, MarkStyle style, float spacing, float pillarHeight, Color color, float waterLevel)
    {
        if (!_mesh)
        {
            _mesh = new Mesh { name = "Vigil spawn marks", indexFormat = IndexFormat.UInt32 };
        }

        _mesh.Clear();
        var shape = MarkShape.Build(marks, style, spacing, pillarHeight, waterLevel);
        int count = shape.Positions.Length / 3;
        var vertices = new Vector3[count];
        var uvs = new Vector2[count];
        var colors = new Color[count];
        for (int i = 0; i < count; i++)
        {
            vertices[i] = new Vector3(shape.Positions[i * 3], shape.Positions[(i * 3) + 1], shape.Positions[(i * 3) + 2]);
            uvs[i] = new Vector2(shape.Uvs[i * 2], shape.Uvs[(i * 2) + 1]);
            colors[i] = color;
        }

        _mesh.vertices = vertices;
        _mesh.uv = uvs;
        _mesh.colors = colors;
        _mesh.triangles = shape.Triangles;
        _mesh.RecalculateBounds();
    }

    public void Draw()
    {
        if (!HasMarks)
        {
            return;
        }

        var material = Material();
        if (material)
        {
            Graphics.DrawMesh(_mesh, Matrix4x4.identity, material, 0, null, 0, null, ShadowCastingMode.Off, false);
        }
    }

    public void Clear()
    {
        if (_mesh)
        {
            _mesh.Clear();
        }
    }

    public void Destroy()
    {
        if (_mesh)
        {
            Object.Destroy(_mesh);
        }

        if (_material)
        {
            Object.Destroy(_material);
        }

        if (_texture)
        {
            Object.Destroy(_texture);
        }
    }

    /// <summary>
    /// An unlit, see-through material that takes the dot's colour from the mesh. Shaders are
    /// looked up by name and a game build may leave one out, so the first one present is used.
    /// </summary>
    private static readonly string[] Shaders = { "Sprites/Default", "Particles/Standard Unlit", "Legacy Shaders/Particles/Alpha Blended", "Unlit/Transparent" };

    /// <summary>The first of the shaders the marks can use that this game has, or null.</summary>
    public static string FirstShader()
    {
        foreach (var name in Shaders)
        {
            if (Shader.Find(name))
            {
                return name;
            }
        }

        return null;
    }

    private Material Material()
    {
        if (_material || _noShader)
        {
            return _material;
        }

        foreach (var name in Shaders)
        {
            var shader = Shader.Find(name);
            if (!shader)
            {
                continue;
            }

            _material = new Material(shader) { name = "Vigil spawn marks", mainTexture = Texture(), renderQueue = 3000 };
            return _material;
        }

        _noShader = true;
        SpawnsModule.Log.LogWarning("No shader for the marks was found, so they cannot be drawn.");
        return null;
    }

    /// <summary>
    /// White shapes the colour comes onto from the mesh: in the left half a disc with a soft edge
    /// for the dot, in the right half a beam with soft sides that fades towards its top for the
    /// pillar, so the foot of a pillar is its clearest part.
    /// </summary>
    private Texture2D Texture()
    {
        const int size = 32;
        _texture = new Texture2D(size * 2, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "Vigil spawn marks" };
        var pixels = new Color[size * 2 * size];
        for (int y = 0; y < size; y++)
        {
            float v = (y + 0.5f) / size;
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;
                float dx = u - 0.5f, dy = v - 0.5f;
                float r = Mathf.Sqrt((dx * dx) + (dy * dy)) * 2f;
                pixels[(y * size * 2) + x] = new Color(1f, 1f, 1f, Mathf.Clamp01((1f - r) / 0.25f));

                float side = Mathf.Clamp01((1f - (Mathf.Abs(dx) * 2f)) / 0.4f);
                float fade = Mathf.Lerp(0.95f, 0.15f, v);
                pixels[(y * size * 2) + size + x] = new Color(1f, 1f, 1f, side * fade);
            }
        }

        _texture.SetPixels(pixels);
        _texture.Apply();
        return _texture;
    }
}
