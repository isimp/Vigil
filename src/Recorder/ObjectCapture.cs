using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// A capture of what this game sends (<see cref="ObjectDataTally"/>): for every object sent, its
    /// size and the bytes of its values that are not the game's, by whose code holds each value's name;
    /// and every value that changed on an object this game runs, which is what makes it send again.
    /// The hooks stay in all the time (putting hooks on the value setters in and out would rebuild hot
    /// methods while playing) and count only while a capture runs: on request for
    /// <see cref="CaptureSeconds"/>, for a self-test, or for as long as a deep capture runs. Postfixes on
    /// the value setters of <c>ZDOExtraData</c> (they say whether the value changed), and a prefix and
    /// postfix on <c>ZDO.SetPosition</c> and <c>ZDO.SetRotation</c> (comparing the object's change
    /// counter); the sizes come from the object's values as the game writes them (<see cref="ValueSize"/>).
    /// </summary>
    internal static class ObjectCapture
    {
        public const float CaptureSeconds = 60f;
        public const string DuringDeep = "during a deep capture";

        private static ObjectDataTally s_tally;
        private static float s_startedAt, s_until;

        public static bool On { get; private set; }

        /// <summary>Why the running capture runs: on request, a self-test, or <see cref="DuringDeep"/>.</summary>
        public static string Reason { get; private set; }

        public static ObjectDataView Last { get; private set; }

        public static DateTime LastAt { get; private set; }

        public static string LastReason { get; private set; }

        public static void Patch(Harmony harmony)
        {
            var extra = typeof(ZDOExtraData);
            var after = Patching.M(typeof(ObjectCapture), nameof(AfterSet));
            foreach (var t in new[] { typeof(float), typeof(string), typeof(Vector3), typeof(Quaternion), typeof(int), typeof(long), typeof(byte[]) })
                Patching.Apply(harmony, AccessTools.Method(extra, "Set", new[] { typeof(ZDOID), typeof(int), t }), "object values changed (" + t.Name + ")", postfix: after);
            Patching.Apply(harmony, AccessTools.Method(extra, "Update", new[] { typeof(ZDOID), typeof(int), typeof(Vector3) }), "object values changed (Vector3 update)", postfix: after);
            Patching.Apply(harmony, AccessTools.Method(typeof(ZDO), nameof(ZDO.SetPosition)), "objects moved",
                prefix: Patching.M(typeof(ObjectCapture), nameof(BeforeChange)), postfix: Patching.M(typeof(ObjectCapture), nameof(AfterPosition)));
            Patching.Apply(harmony, AccessTools.Method(typeof(ZDO), nameof(ZDO.SetRotation)), "objects turned",
                prefix: Patching.M(typeof(ObjectCapture), nameof(BeforeChange)), postfix: Patching.M(typeof(ObjectCapture), nameof(AfterRotation)));
        }

        /// <summary>Starts a capture for <paramref name="seconds"/> (infinity until <see cref="Stop"/>); false when one runs.</summary>
        public static bool Start(string reason, float seconds)
        {
            if (On) return false;
            s_tally = new ObjectDataTally();
            s_startedAt = Time.realtimeSinceStartup;
            s_until = s_startedAt + seconds;
            Reason = reason;
            On = true;
            return true;
        }

        public static float SecondsLeft => On ? Math.Max(0f, s_until - Time.realtimeSinceStartup) : 0f;

        /// <summary>Every frame: a capture whose time is up ends.</summary>
        public static void Tick()
        {
            if (On && Time.realtimeSinceStartup >= s_until) Stop();
        }

        public static void Stop()
        {
            if (!On) return;
            On = false;
            Last = s_tally.View(Time.realtimeSinceStartup - s_startedAt);
            LastAt = DateTime.Now;
            LastReason = Reason;
            s_tally = null;
        }

        /// <summary>An object this game sends, while sending (<c>ZDO.Serialize</c> into the upload).</summary>
        public static void Sent(ZDO zdo, int size)
        {
            if (!On) return;
            ZDOExtraData.GetData(zdo.m_uid, out var floats, out var vec3s, out var quats, out var ints, out var longs, out var strings, out var bytes, out _);
            var owners = new Dictionary<string, int>(StringComparer.Ordinal);
            void Add(int hash, int b)
            {
                string owner = Named(hash).Owner;
                if (owner == ObjectDataTally.Game) return;
                owners.TryGetValue(owner, out int had);
                owners[owner] = had + b;
            }
            foreach (var kv in floats) Add(kv.Key, ValueSize.Float);
            foreach (var kv in vec3s) Add(kv.Key, ValueSize.Vec3);
            foreach (var kv in quats) Add(kv.Key, ValueSize.Quat);
            foreach (var kv in ints) Add(kv.Key, ValueSize.Int);
            foreach (var kv in longs) Add(kv.Key, ValueSize.Long);
            foreach (var kv in strings) Add(kv.Key, ValueSize.String(kv.Value));
            foreach (var kv in bytes) Add(kv.Key, ValueSize.Bytes(kv.Value));
            s_tally.Add(RecorderModule.NameOf(zdo.GetPrefab()), Id(zdo.m_uid), size, owners);
        }

        private static long Id(ZDOID id) => (id.UserID * 397) ^ id.ID;

        // A value's name and owner: from the files (the game first), else as learned while playing, with the mod that hashed it.
        private static (string Text, string Owner) Named(int hash)
        {
            var name = ObjectInspector.NameOf(hash);
            if (name.Found) return (name.Text, name.NamedIn == ValueNames.Game ? ObjectDataTally.Game : name.NamedIn);
            var book = NetAttribution.Book;
            string text = book.LearnedText(hash);
            if (text == null) return ("#" + hash, ObjectDataTally.Unknown);
            string owner = book.LearnedOwner(hash);
            return (text, owner == null ? ObjectDataTally.Unknown : owner == ValueNames.Game ? ObjectDataTally.Game : owner);
        }

        private static void AfterSet(ZDOID zid, int hash, bool __result)
        {
            if (!On || !__result) return;
            try
            {
                var zdo = ZDOMan.instance?.GetZDO(zid);
                if (zdo == null || !zdo.IsOwner()) return;
                var (text, owner) = Named(hash);
                s_tally?.Changed(RecorderModule.NameOf(zdo.GetPrefab()), text, owner);
            }
            catch (Exception) { }
        }

        private static void BeforeChange(ZDO __instance, out uint __state) => __state = __instance.DataRevision;

        private static void AfterPosition(ZDO __instance, uint __state) => Moved(__instance, __state, "position");

        private static void AfterRotation(ZDO __instance, uint __state) => Moved(__instance, __state, "rotation");

        private static void Moved(ZDO zdo, uint before, string what)
        {
            if (!On || zdo.DataRevision == before || !zdo.IsOwner()) return;
            s_tally?.Changed(RecorderModule.NameOf(zdo.GetPrefab()), what, ObjectDataTally.Game);
        }
    }
}
