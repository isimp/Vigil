using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patch "OdinOnDemand's stream cannot crash the game" (<see cref="OdinAudioClock"/>).
    /// OdinOnDemand 1.3.0's <c>YoutubeDecoder.PcmRingBuffer.Read</c> answers Unity's sound thread
    /// with the stream's samples and reads <c>output.timeSamples</c> there; the game crashed inside
    /// that read while it was running slowly. A prefix and finalizer on <c>Read</c> mark the sound
    /// thread as inside it, and a prefix on <c>AudioSource.timeSamples</c> answers such a marked read
    /// from a note the main thread takes after every <c>YoutubeDecoder.Update</c>, without running
    /// Unity's getter. Any other read of the property, on any thread, runs as it would. Prefixes and
    /// finalizers only: Vigil adds no transpilers (see HookShapeTests). OdinOnDemand's code is
    /// compared with 1.3.0's, and any change makes the patch step aside. The switch is this game's own.
    /// </summary>
    internal static class OdinSafeStreamPatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.odinsafestream";
        private const string OodGuid = "com.valmedia.odinondemand";

        /// <summary>OdinOnDemand 1.3.0's YoutubeDecoder.PcmRingBuffer.Read.</summary>
        private const ulong KnownCode = 0xD1D6B7C4BEBCF7C0UL;

        /// <summary>This game's switch, kept current by the config entry.</summary>
        public static volatile bool On;

        public static bool Found { get; private set; }
        public static string OodVersion { get; private set; }

        /// <summary>Why the patch stepped aside; null while it is in (or OdinOnDemand is absent).</summary>
        public static string Problem { get; private set; }

        public static string State => OdinAudioClock.StateLine(On, Found, OodVersion, Problem);

        /// <summary>The newest note for one stream; replaced whole, so the sound thread never sees half of one.</summary>
        private sealed class NoteBox
        {
            public volatile PlayNote Note;
        }

        // Keyed by the stream's ring buffer object itself; the table compares references and is safe across threads.
        private static readonly ConditionalWeakTable<object, NoteBox> s_notes = new ConditionalWeakTable<object, NoteBox>();
        private static readonly ConditionalWeakTable<object, NoteBox>.CreateValueCallback s_newBox = _ => new NoteBox();

        /// <summary>The ring buffer whose Read this thread is in, set only off the main thread with the switch on.</summary>
        [ThreadStatic] private static object t_reading;

        private static Action<string> s_log;
        private static int s_mainThread;
        private static FieldInfo s_session, s_clip, s_output;
        private static PropertyInfo s_audio;

        public static void Hook(Action<string> log)
        {
            s_log = log;
            s_mainThread = Thread.CurrentThread.ManagedThreadId;
            var ood = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == OodGuid);
            Found = ood?.Instance != null;
            OodVersion = ood?.Metadata.Version.ToString();
            if (!Found) return;
            try
            {
                var asm = ood.Instance.GetType().Assembly;
                var decoder = asm.GetType("OdinOnDemand.MPlayer.YoutubeDecoder");
                var ring = decoder?.GetNestedType("PcmRingBuffer", BindingFlags.NonPublic);
                var session = decoder?.GetNestedType("PlaybackSession", BindingFlags.NonPublic);
                var read = ring != null ? AccessTools.DeclaredMethod(ring, "Read") : null;
                if (read == null || CodeShape.Hash(new MethodBase[] { read }) != KnownCode)
                {
                    Problem = "OdinOnDemand's stream code changed (an update)";
                    return;
                }
                var update = AccessTools.DeclaredMethod(decoder, "Update");
                var timeSamples = AccessTools.PropertyGetter(typeof(AudioSource), nameof(AudioSource.timeSamples));
                s_session = AccessTools.DeclaredField(decoder, "session");
                s_clip = AccessTools.DeclaredField(decoder, "streamingClip");
                s_output = AccessTools.DeclaredField(ring, "output");
                s_audio = session != null ? AccessTools.DeclaredProperty(session, "Audio") : null;
                if (update == null || timeSamples == null || s_session == null || s_clip == null || s_output == null || s_audio == null)
                {
                    Problem = "OdinOnDemand's stream parts were not found (an update changed them)";
                    return;
                }
                var harmony = new Harmony(HarmonyId);
                harmony.Patch(update, postfix: new HarmonyMethod(typeof(OdinSafeStreamPatch), nameof(AfterUpdate)));
                harmony.Patch(timeSamples, prefix: new HarmonyMethod(typeof(OdinSafeStreamPatch), nameof(BeforeTimeSamples)));
                harmony.Patch(read, prefix: new HarmonyMethod(typeof(OdinSafeStreamPatch), nameof(BeforeRead)),
                    finalizer: new HarmonyMethod(typeof(OdinSafeStreamPatch), nameof(AfterRead)));
            }
            catch (Exception e) { Problem = "OdinOnDemand could not be hooked: " + e.GetType().Name + " " + e.Message; }
        }

        private static void BeforeRead(object __instance)
        {
            t_reading = On && Problem == null && Thread.CurrentThread.ManagedThreadId != s_mainThread ? __instance : null;
        }

        /// <summary>A finalizer that returns nothing lets any exception go on as before.</summary>
        private static void AfterRead() => t_reading = null;

        /// <summary>Inside a marked Read: the noted play position, carried forward; Unity's getter does not run.</summary>
        private static bool BeforeTimeSamples(ref int __result)
        {
            var ring = t_reading;
            if (ring == null) return true;
            s_notes.TryGetValue(ring, out var box);
            __result = OdinAudioClock.Position(box?.Note, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
            return false;
        }

        /// <summary>Notes the current stream's play position on the main thread, after the decoder's own frame work.</summary>
        private static void AfterUpdate(object __instance)
        {
            if (!On || Problem != null) return;
            try
            {
                var session = s_session.GetValue(__instance);
                var ring = session != null ? s_audio.GetValue(session, null) : null;
                if (ring == null) return;
                var box = s_notes.GetValue(ring, s_newBox);
                var output = s_output.GetValue(ring) as AudioSource;
                var clip = s_clip.GetValue(__instance) as AudioClip;
                // Only the decoder's own stream clip on the source is this ring buffer's; anything else plays from a fresh start.
                if (output == null || clip == null || output.clip != clip)
                {
                    box.Note = null;
                    return;
                }
                box.Note = new PlayNote(output.timeSamples, Stopwatch.GetTimestamp(), output.isPlaying, output.pitch, clip.frequency, clip.samples);
            }
            catch (Exception e)
            {
                Problem = "its note of the play position failed: " + e.GetType().Name + " " + e.Message;
                s_log?.Invoke("Vigil: the patch keeping OdinOnDemand's stream from crashing the game stepped aside: " + Problem);
            }
        }
    }
}
