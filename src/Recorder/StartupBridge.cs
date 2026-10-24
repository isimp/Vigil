using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The patcher's <c>Vigil.Startup.Bridge</c>, found by name among the loaded assemblies, so that
    /// Vigil.dll does not need the patcher to load. Without it (Vigil.dll installed alone) the start is
    /// not timed and bundles load the usual way; the steps Vigil sees itself (the menu, a join) are
    /// then kept here on Vigil's own clock.
    /// </summary>
    internal sealed class StartupBridge
    {
        private const string AssemblyName = "Vigil.Preloader", TypeName = "Vigil.Startup.Bridge";
        private readonly Type _bridge;
        private readonly List<string> _local = new List<string>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private StartupBridge(Type bridge)
        {
            _bridge = bridge;
        }

        public static StartupBridge Find()
        {
            Type t = null;
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);
                t = assembly?.GetType(TypeName, false);
            }
            catch (Exception) { }
            return new StartupBridge(t);
        }

        /// <summary>Whether Vigil's patcher is installed and loaded.</summary>
        public bool Installed => _bridge != null;

        private object Call(string name, params object[] args)
        {
            if (_bridge == null) return null;
            try { return _bridge.GetMethod(name, BindingFlags.Public | BindingFlags.Static)?.Invoke(null, args); }
            catch (TargetInvocationException) { return null; }
            catch (ArgumentException) { return null; }
        }

        public List<StartupEvent> Events()
        {
            var lines = _bridge != null ? Call("Events") as string[] ?? Array.Empty<string>() : _local.ToArray();
            return lines.Select(StartupEvent.Parse).Where(e => e != null).ToList();
        }

        public void Mark(string kind, string text = "")
        {
            if (_bridge != null) Call("Mark", kind, text ?? "");
            else _local.Add(new StartupEvent(_clock.Elapsed.TotalMilliseconds, kind, text).Line());
        }

        public void StopTimeline() => Call("StopTimeline");

        public bool TimelineOn => Call("TimelineOn") as bool? ?? false;

        public bool CacheOn => Call("CacheOn") as bool? ?? false;

        public string EditResult => Call("EditResult") as string ?? "the patcher is not installed";

        public string HousekeepingResult => Call("HousekeepingResult") as string ?? "";

        public string CacheDir => Call("CacheDir") as string ?? "";

        public string Status => Call("Status") as string ?? "Vigil's patcher is not installed";

        /// <summary>Loads asked, copies started, made, failed, running, loaded and refused.</summary>
        public int[] Counts => Call("Counts") as int[] ?? new int[7];

        public List<BundleEntry> Entries() => BundleCacheIndex.Parse(Call("IndexText") as string ?? "");

        public void SaveIndex() => Call("SaveIndex");

        public string RequestReset() => Call("RequestReset") as string ?? "Vigil's patcher is not installed, so there is no bundle cache to empty";

        public bool ResetRequested => Call("ResetRequested") as bool? ?? false;

        public bool ResetDone => Call("ResetDone") as bool? ?? false;
    }
}
