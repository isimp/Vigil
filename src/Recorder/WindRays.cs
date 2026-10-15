using System.Collections.Generic;
using UnityEngine;
using Vigil.Spawns;

namespace Vigil.Recorder
{
    /// <summary>
    /// The 17 rays of a windmill's wind check drawn in the world, from the hub to where each ends or
    /// hits, green open and amber blocked, until hidden or the world unloads.
    /// </summary>
    internal sealed class WindRays
    {
        private static readonly Color Open = new Color(0.61f, 0.84f, 0.49f, 0.9f), Blocked = new Color(0.91f, 0.69f, 0.29f, 0.95f);
        private GameObject _root;
        private LineRenderer[] _lines;
        private Material _material;

        public bool Shown => _root != null;

        public void Show(Vector3 hub, IList<Vector3> ends, IList<bool> hit)
        {
            if (_root == null && !Make(ends.Count)) return;
            for (int i = 0; i < _lines.Length && i < ends.Count; i++)
            {
                var line = _lines[i];
                line.SetPosition(0, hub);
                line.SetPosition(1, ends[i]);
                line.startColor = line.endColor = hit[i] ? Blocked : Open;
            }
        }

        public void Hide()
        {
            if (_root != null) Object.Destroy(_root);
            _root = null;
            _lines = null;
        }

        private bool Make(int count)
        {
            string shader = MarkMesh.FirstShader();
            if (shader == null) return false;
            if (_material == null) _material = new Material(Shader.Find(shader)) { name = "Vigil wind rays" };
            _root = new GameObject("Vigil wind rays");
            _lines = new LineRenderer[count];
            for (int i = 0; i < count; i++)
            {
                var child = new GameObject("ray " + i);
                child.transform.SetParent(_root.transform, false);
                var line = child.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.positionCount = 2;
                line.widthMultiplier = 0.06f;
                line.sharedMaterial = _material;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                _lines[i] = line;
            }
            return true;
        }
    }
}
