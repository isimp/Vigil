using System;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Vigil.Core
{
    /// <summary>
    /// The game patches on EasyRelocate 1.1.1. Its first grab of a session looks for two mods it works
    /// with through HarmonyX's <c>AccessTools.TypeByName</c>, which lists every type of every loaded
    /// assembly when the mod is missing (about a second in this pack); every grab and every end of a
    /// move put six hooks into the game and take them out again, each rebuilding a game method through
    /// every other mod's patches (about 50 ms each way); while a piece moves, the ghost is updated twice
    /// a frame, by the game's <c>Player.LateUpdate</c> and again by EasyRelocate's Update; and a move
    /// could be left with no way out short of a relog. The ghost itself is a solid copy of the piece;
    /// the spirit look draws it see-through.
    /// </summary>
    internal static class Relocate
    {
        public const string Guid = "ModdedWolf.EasyRelocate";

        public const string ControllerType = "EasyRelocate.RelocateController";
        public const string CompatType = "EasyRelocate.FreePlacementRotationCompat";
        public const string HooksType = "EasyRelocate.VanillaPlacementSessionPatches";
        public const string PluginType = "EasyRelocate.RelocatePlugin";
        public const string UtilsType = "EasyRelocate.RelocateUtils";
        public const string GhostBuilderType = "EasyRelocate.GhostBuilder";
        public const string GhostTintType = "EasyRelocate.GhostTint";
        public const string HoverHintType = "EasyRelocate.RelocateHoverHint";

        /// <summary>The seconds of one breath of the spirit ghost.</summary>
        public const float PulseSeconds = 2.4f;

        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        /// <summary>Whether EasyRelocate's look-up of a mod it works with is skipped: only when no loaded assembly has that type.</summary>
        public static bool SkipsTypeSearch(bool on, bool typeLoaded) => on && !typeLoaded;

        /// <summary>Whether the hooks EasyRelocate puts in for a move stay in after it; each of them does nothing outside a move.</summary>
        public static bool KeepsHooks(bool on) => on;

        /// <summary>
        /// Why a move ends this frame, or null. The grab key counts with its own modifiers held whatever
        /// else is held (BepInEx's shortcut check fails while any other key is down); Esc ends it too; a
        /// text box taking the keys ends it, where EasyRelocate would stop following input and freeze it;
        /// a build tool taken in hand ends it, since equipping one hands the game the tool's piece table in
        /// place of the one EasyRelocate moves with.
        /// </summary>
        public static string CancelReason(bool on, bool moving, bool grabKeyDown, bool grabModifiersHeld, bool escapeDown, bool textFieldFocused, bool toolInHand)
        {
            if (!on || !moving) return null;
            if (grabKeyDown && grabModifiersHeld) return "the grab key";
            if (escapeDown) return "Esc";
            if (textFieldFocused) return "a text box took the keys";
            if (toolInHand) return "a build tool was taken in hand";
            return null;
        }

        /// <summary>Whether a grab is refused, and its hover hint left out, because a build tool (hammer, hoe, cultivator) is in hand.</summary>
        public static bool BlocksGrab(bool on, bool toolInHand) => on && toolInHand;

        /// <summary>Whether an error out of EasyRelocate's Update ends the move, as a relog would.</summary>
        public static bool EndsAfterError(bool on, bool moving, bool failed) => on && moving && failed;

        /// <summary>
        /// Whether EasyRelocate reads the ghost the game updated instead of updating it a second time:
        /// only for the ghost the game updated in this frame or the one before.
        /// </summary>
        public static bool ReusesGameUpdate(bool on, bool moving, bool sameGhost, int updatedFrame, int frame) =>
            on && moving && sameGhost && frame - updatedFrame <= 1 && updatedFrame <= frame;

        /// <summary>A colour with alpha, kept free of the game's types.</summary>
        public struct Rgba
        {
            public float R, G, B, A;

            public Rgba(float r, float g, float b, float a)
            {
                R = r; G = g; B = b; A = a;
            }
        }

        /// <summary>The spirit's tint: pale blue where it can be placed, pale red where not, see-through and breathing.</summary>
        public static Rgba SpiritTint(bool blocked, float seconds)
        {
            float alpha = 0.33f + 0.12f * (float)Math.Sin(2.0 * Math.PI * seconds / PulseSeconds);
            return blocked ? new Rgba(1f, 0.45f, 0.4f, alpha) : new Rgba(0.55f, 0.8f, 1f, alpha);
        }

        /// <summary>What the fixes do on this game, in words.</summary>
        public static string FixesLine(bool on, bool found, string version, string problem, long ended, long spared)
        {
            if (!on) return "off: this game's switch (Patches.EasyRelocateFixes)";
            if (!found) return "not needed: EasyRelocate is not installed";
            if (problem != null) return "stepped aside: " + problem;
            string line = "working · EasyRelocate " + version + " · no freeze on a grab, one ghost update a frame, a move can always be ended";
            if (ended > 0) line += " · " + ended.ToString(CultureInfo.InvariantCulture) + " moves ended by Vigil";
            if (spared > 0) line += " · " + spared.ToString(CultureInfo.InvariantCulture) + " ghost updates spared";
            return line;
        }

        /// <summary>What the spirit look does on this game, in words.</summary>
        public static string SpiritLine(bool on, bool found, string problem)
        {
            if (!on) return "off: this game's switch (Patches.EasyRelocateSpiritGhost)";
            if (!found) return "not needed: EasyRelocate is not installed";
            if (problem != null) return "stepped aside: " + problem;
            return "working · the moving ghost is a see-through spirit";
        }

        /// <summary>EasyRelocate's members the patches use, found by name and shape.</summary>
        public sealed class Parts
        {
            public MethodInfo Update, TryBegin, UpdateGhost, Cancel, Abort, ForceReset, SourceValid, IsMoving, HoverHint;
            public FieldInfo GhostPosition, GhostRotation, PlacementValid, GrabKey;
            public MethodInfo TryPatch, ApplyHooks, RemoveHooks, HarmonyInstance, TextFieldFocused, CreateGhost, Tint;
        }

        /// <summary>EasyRelocate 1.1.1's members by name and shape; null when any is missing or differs.</summary>
        public static Parts FindParts(Func<string, Type> type)
        {
            var controller = type(ControllerType);
            var compat = type(CompatType);
            var hooks = type(HooksType);
            var plugin = type(PluginType);
            var utils = type(UtilsType);
            var builder = type(GhostBuilderType);
            var tint = type(GhostTintType);
            var hint = type(HoverHintType);
            if (controller == null || compat == null || hooks == null || plugin == null || utils == null || builder == null || tint == null || hint == null) return null;
            var p = new Parts
            {
                Update = Method(controller, "Update", 0, typeof(void)),
                TryBegin = Method(controller, "TryBeginRelocate", 1, typeof(void), "Player"),
                HoverHint = Method(hint, "LateUpdate", 0, typeof(void)),
                UpdateGhost = Method(controller, "UpdateGhostFromVanilla", 1, typeof(void), "Player"),
                Cancel = Method(controller, "CancelRelocate", 0, typeof(void)),
                Abort = Method(controller, "AbortRelocate", 0, typeof(void)),
                ForceReset = Method(controller, "ForceReset", 0, typeof(void)),
                SourceValid = Method(controller, "IsSourceStillValid", 0, typeof(bool)),
                IsMoving = controller.GetProperty("IsMoving", Any)?.GetGetMethod(true),
                GhostPosition = Field(controller, "_ghostPosition", "Vector3"),
                GhostRotation = Field(controller, "_ghostRotation", "Quaternion"),
                PlacementValid = Field(controller, "_placementValid", "Boolean"),
                GrabKey = Field(plugin, "GrabKey", "ConfigEntry`1"),
                TryPatch = Method(compat, "TryPatch", 3, typeof(void), "Harmony", "String", "HarmonyMethod"),
                ApplyHooks = Method(hooks, "Apply", 1, typeof(void), "Harmony"),
                RemoveHooks = Method(hooks, "Remove", 1, typeof(void), "Harmony"),
                HarmonyInstance = plugin.GetProperty("HarmonyInstance", Any)?.GetGetMethod(true),
                TextFieldFocused = Method(utils, "IsTextFieldFocused", 0, typeof(bool)),
                CreateGhost = Method(builder, "Create", 2, null, "Piece", "GameObject"),
                Tint = Method(tint, "Apply", 2, typeof(void), "GameObject", "Boolean"),
            };
            bool whole = p.Update != null && p.TryBegin != null && p.HoverHint != null && p.UpdateGhost != null && p.Cancel != null && p.Abort != null && p.ForceReset != null && p.SourceValid != null
                && p.IsMoving != null && p.IsMoving.ReturnType == typeof(bool) && p.GhostPosition != null && p.GhostRotation != null && p.PlacementValid != null
                && p.GrabKey != null && p.TryPatch != null && p.ApplyHooks != null && p.RemoveHooks != null && p.HarmonyInstance != null
                && p.TextFieldFocused != null && p.CreateGhost != null && p.CreateGhost.ReturnType.Name == "GameObject" && p.Tint != null;
            return whole ? p : null;
        }

        private static MethodInfo Method(Type t, string name, int count, Type returns, params string[] parameters) =>
            t.GetMethods(Any).FirstOrDefault(m => m.Name == name && (returns == null || m.ReturnType == returns)
                && m.GetParameters().Length == count
                && m.GetParameters().Select(x => x.ParameterType.Name).SequenceEqual(parameters));

        private static FieldInfo Field(Type t, string name, string typeName)
        {
            var f = t.GetField(name, Any);
            return f != null && f.FieldType.Name == typeName ? f : null;
        }
    }
}
