using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Profiling;
using UnityEngine;

namespace Vigil.Recorder
{
    /// <summary>
    /// What Unity itself reports per frame: the CPU time spent drawing (first camera cull to
    /// last camera render), Unity's frame timing (main thread, render thread, graphics card;
    /// only when the game build collects it) and its built-in counters (memory allocated per
    /// frame, draw calls, triangles, ...). Each source is probed and simply left out when this
    /// build of the game does not provide it; <see cref="Availability"/> says which ones work.
    /// </summary>
    internal sealed class EngineStats : IDisposable
    {
        internal sealed class Counter
        {
            public string Column;
            public string Name;
            public ProfilerRecorder Recorder;
            public double Scale;       // multiply LastValue by this
            public bool PerSecondSum;  // sum per frame over the second instead of averaging
            public double Sum; public int Frames; public double Max;
        }

        private readonly List<Counter> _counters = new List<Counter>();
        private long _renderStart = -1, _renderEnd = -1;
        private int _camerasThisFrame;
        private readonly FrameTiming[] _timing = new FrameTiming[1];
        private int _timingFramesTried, _timingFramesValid;

        public float LastRenderMs;
        public int LastCameras;
        public double LastMainMs = double.NaN, LastRenderThreadMs = double.NaN, LastGpuMs = double.NaN;
        public double SecondRenderMs, SecondMainMs, SecondRenderThreadMs, SecondGpuMs;
        public int SecondFrames, SecondTimingFrames;
        public double LastAllocBytes = double.NaN;

        public EngineStats()
        {
            Add("alloc_kb_s", ProfilerCategory.Memory, "GC Allocated In Frame", 1 / 1024.0, true);
            Add("allocs_s", ProfilerCategory.Memory, "GC Allocation In Frame Count", 1, true);
            Add("gc_used_mb", ProfilerCategory.Memory, "GC Used Memory", 1 / 1048576.0, false);
            Add("gc_reserved_mb", ProfilerCategory.Memory, "GC Reserved Memory", 1 / 1048576.0, false);
            Add("unity_used_mb", ProfilerCategory.Memory, "Total Used Memory", 1 / 1048576.0, false);
            Add("gfx_used_mb", ProfilerCategory.Memory, "Gfx Used Memory", 1 / 1048576.0, false);
            Add("draw_calls", ProfilerCategory.Render, "Draw Calls Count", 1, false);
            Add("batches", ProfilerCategory.Render, "Batches Count", 1, false);
            Add("setpass", ProfilerCategory.Render, "SetPass Calls Count", 1, false);
            Add("tris_k", ProfilerCategory.Render, "Triangles Count", 1 / 1000.0, false);
            Add("shadow_casters", ProfilerCategory.Render, "Shadow Casters Count", 1, false);
            Add("main_thread_ms", ProfilerCategory.Internal, "Main Thread", 1e-6, false);
            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
        }

        public IReadOnlyList<Counter> Counters => _counters;

        private void Add(string column, ProfilerCategory cat, string name, double scale, bool sum)
        {
            var c = new Counter { Column = column, Name = name, Scale = scale, PerSecondSum = sum };
            try { c.Recorder = ProfilerRecorder.StartNew(cat, name); } catch (Exception) { }
            _counters.Add(c);
        }

        private void OnPreCull(Camera cam)
        {
            if (_renderStart < 0) _renderStart = Stopwatch.GetTimestamp();
            _camerasThisFrame++;
        }

        private void OnPostRender(Camera cam) { _renderEnd = Stopwatch.GetTimestamp(); }

        /// <summary>Called at the start of each frame for the frame before.</summary>
        public void EndFrame()
        {
            LastRenderMs = _renderStart >= 0 && _renderEnd > _renderStart ? (float)((_renderEnd - _renderStart) * 1000.0 / Stopwatch.Frequency) : 0f;
            LastCameras = _camerasThisFrame;
            _renderStart = _renderEnd = -1; _camerasThisFrame = 0;
            SecondRenderMs += LastRenderMs; SecondFrames++;

            LastMainMs = LastRenderThreadMs = LastGpuMs = double.NaN;
            if (_timingFramesTried < 600 || _timingFramesValid > 0)
            {
                _timingFramesTried++;
                try
                {
                    FrameTimingManager.CaptureFrameTimings();
                    if (FrameTimingManager.GetLatestTimings(1, _timing) > 0 && _timing[0].cpuFrameTime > 0)
                    {
                        _timingFramesValid++;
                        LastMainMs = _timing[0].cpuMainThreadFrameTime;
                        LastRenderThreadMs = _timing[0].cpuRenderThreadFrameTime;
                        LastGpuMs = _timing[0].gpuFrameTime;
                        SecondMainMs += LastMainMs; SecondRenderThreadMs += LastRenderThreadMs; SecondGpuMs += LastGpuMs;
                        SecondTimingFrames++;
                    }
                }
                catch (Exception) { _timingFramesTried = 600; }
            }

            foreach (var c in _counters)
            {
                if (!c.Recorder.Valid) continue;
                double v = c.Recorder.LastValue * c.Scale;
                c.Sum += v; c.Frames++;
                if (v > c.Max) c.Max = v;
                if (c.Column == "alloc_kb_s") LastAllocBytes = c.Recorder.LastValue;
            }
        }

        /// <summary>Per-second value of a counter (sum or average over the second's frames), NaN if unavailable.</summary>
        /// <summary>Each working counter's value in the last frame, by its timeline column.</summary>
        public Dictionary<string, double> LastValues()
        {
            var d = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var c in _counters)
            {
                try { if (c.Recorder.Valid) d[c.Column] = c.Recorder.LastValue * c.Scale; } catch (Exception) { }
            }
            return d;
        }

        public double SecondValue(Counter c)
        {
            if (!c.Recorder.Valid || c.Frames == 0) return double.NaN;
            return c.PerSecondSum ? c.Sum : c.Sum / c.Frames;
        }

        public void EndSecond()
        {
            SecondRenderMs = SecondMainMs = SecondRenderThreadMs = SecondGpuMs = 0;
            SecondFrames = SecondTimingFrames = 0;
            foreach (var c in _counters) { c.Sum = 0; c.Frames = 0; c.Max = 0; }
        }

        public string Availability()
        {
            var parts = new List<string>();
            foreach (var c in _counters) parts.Add(c.Name + (c.Recorder.Valid ? " yes" : " no"));
            parts.Add("frame timing (CPU main/render thread, GPU) " + (_timingFramesValid > 0 ? "yes" : "no"));
            parts.Add("CPU render time via camera callbacks yes");
            return string.Join(", ", parts.ToArray());
        }

        /// <summary>Stops the engine counters and the camera callbacks while Vigil is not recording.</summary>
        public void Pause()
        {
            Camera.onPreCull -= OnPreCull;
            Camera.onPostRender -= OnPostRender;
            foreach (var c in _counters) { try { if (c.Recorder.Valid) c.Recorder.Stop(); } catch (Exception) { } }
        }

        public void Resume()
        {
            Camera.onPreCull -= OnPreCull;
            Camera.onPostRender -= OnPostRender;
            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
            foreach (var c in _counters) { try { if (c.Recorder.Valid) { c.Recorder.Reset(); c.Recorder.Start(); } } catch (Exception) { } }
        }

        public void Dispose()
        {
            Camera.onPreCull -= OnPreCull;
            Camera.onPostRender -= OnPostRender;
            foreach (var c in _counters) { try { c.Recorder.Dispose(); } catch (Exception) { } }
        }
    }
}
