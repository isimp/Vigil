using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// The game patches on EasyRelocate (<see cref="Relocate"/>), each with this game's own switch.
    /// <para>Fixes: its look-up of a mod it works with is skipped when no loaded assembly has that
    /// type; the six hooks it puts in for a move go in once at the main menu and stay; while a piece
    /// moves, its own ghost update is left out when the game updated that ghost in <c>Player.LateUpdate</c>
    /// a frame ago, and the ghost's place and status are read from the game's update; the grab key
    /// (with its own modifiers, whatever else is held), Esc, or a text box taking the keys ends a move;
    /// an error out of its Update during a move ends the move as a relog would.</para>
    /// <para>Spirit: the moving ghost's materials are see-through ones on a white base (Unity's lit
    /// see-through diffuse where the game ships it, else the sprite shader), tinted pale blue, pale red
    /// where it cannot be placed, breathing slowly.</para>
    /// </summary>
    internal static class RelocatePatch
    {
        private const string HarmonyId = "isimp.Vigil.patches.relocate";

        public static bool FixesOn;

        public static bool SpiritOn;

        public static bool Found { get; private set; }

        public static string Version { get; private set; }

        /// <summary>Why the patches stepped aside; null while they are in (or EasyRelocate is absent).</summary>
        public static string Problem { get; private set; }

        /// <summary>Why the spirit look stepped aside; null while it works.</summary>
        public static string SpiritProblem { get; private set; }

        /// <summary>Moves Vigil ended: by a key EasyRelocate missed, a text box, or an error.</summary>
        public static long Ended { get; private set; }

        /// <summary>EasyRelocate ghost updates left out because the game had just made one.</summary>
        public static long Spared { get; private set; }

        public static string FixesState => Relocate.FixesLine(FixesOn, Found, Version, Problem, Ended, Spared);

        public static string SpiritState => Relocate.SpiritLine(SpiritOn, Found, Problem ?? SpiritProblem);

        private static readonly AccessTools.FieldRef<Player, GameObject> Ghost = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private static Relocate.Parts _parts;
        private static Func<bool> _moving;
        private static Func<bool> _textField;
        private static ConfigEntryBase _grabKey;
        private static Action<string> _log;
        private static GameObject _updatedGhost;
        private static int _updatedFrame = -10;
        private static Shader _spiritShader;
        private static bool _spiritLooked;
        private static bool _spiritTold;

        // Every spirit ghost made, with its own materials and whether it shows a blocked spot.
        private sealed class Spirit
        {
            public GameObject Ghost;
            public Material[] Materials;
            public bool Blocked;
        }

        private static readonly List<Spirit> Spirits = new List<Spirit>();

        public static void Hook(Action<string> log)
        {
            _log = log;
            var info = Chainloader.PluginInfos.Values.FirstOrDefault(p => p.Metadata.GUID == Relocate.Guid);
            Found = info != null;
            Version = info?.Metadata.Version.ToString();
            if (!Found) return;
            try
            {
                _parts = Relocate.FindParts(TypeLookup.Loaded.Find);
                if (_parts == null)
                {
                    Problem = "its parts changed (an update)";
                    return;
                }
                _moving = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), _parts.IsMoving);
                _textField = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), _parts.TextFieldFocused);
                _grabKey = _parts.GrabKey.GetValue(null) as ConfigEntryBase;
                var h = new Harmony(HarmonyId);
                h.Patch(_parts.TryPatch, prefix: Hm(nameof(BeforeTypeSearch)));
                h.Patch(_parts.RemoveHooks, prefix: Hm(nameof(BeforeRemoveHooks)));
                h.Patch(_parts.Update, prefix: Hm(nameof(BeforeUpdate)), finalizer: Hm(nameof(AfterUpdate)));
                h.Patch(_parts.UpdateGhost, prefix: Hm(nameof(BeforeGhostUpdate)));
                h.Patch(AccessTools.Method(typeof(Player), "LateUpdate"), postfix: Hm(nameof(AfterPlayerLateUpdate)));
                h.Patch(_parts.CreateGhost, postfix: Hm(nameof(AfterCreateGhost)));
                h.Patch(_parts.Tint, prefix: Hm(nameof(BeforeTint)));
                if (FixesOn) PutHooksIn();
            }
            catch (Exception e)
            {
                Problem = "EasyRelocate could not be hooked: " + e.GetType().Name + " " + e.Message;
            }
        }

        private static HarmonyMethod Hm(string name) => new HarmonyMethod(typeof(RelocatePatch), name);

        /// <summary>Puts EasyRelocate's move hooks in now, at the main menu, instead of at the first grab in the world.</summary>
        private static void PutHooksIn()
        {
            if (_parts.HarmonyInstance.Invoke(null, null) is Harmony harmony) _parts.ApplyHooks.Invoke(null, new object[] { harmony });
        }

        // TryPatch(Harmony harmony, string typeName, HarmonyMethod prefix): the type name is the second argument.
        private static bool BeforeTypeSearch(string __1) => !Relocate.SkipsTypeSearch(FixesOn, TypeLookup.Loaded.Find(__1) != null);

        private static bool BeforeRemoveHooks() => !Relocate.KeepsHooks(FixesOn);

        private static bool BeforeUpdate(object __instance)
        {
            if (_moving == null || !_moving()) return true;
            if (SpiritOn) Breathe();
            if (!FixesOn) return true;
            var key = _grabKey?.BoxedValue is KeyboardShortcut k ? k : KeyboardShortcut.Empty;
            bool grab = key.MainKey != KeyCode.None && Input.GetKeyDown(key.MainKey);
            bool modifiers = key.Modifiers.All(Input.GetKey);
            string reason = Relocate.CancelReason(true, true, grab, modifiers, Input.GetKeyDown(KeyCode.Escape), _textField());
            // EasyRelocate ends a move on its grab key itself when nothing else is held.
            if (reason == null || (grab && modifiers && key.IsDown())) return true;
            End(__instance, reason);
            return false;
        }

        private static Exception AfterUpdate(object __instance, Exception __exception)
        {
            if (__exception == null || _moving == null || !Relocate.EndsAfterError(FixesOn, _moving(), true)) return __exception;
            try { _parts.ForceReset.Invoke(__instance, null); }
            catch (Exception e) { _log?.Invoke("[Vigil] relocate: putting the move back failed too: " + e.GetBaseException().Message); }
            Ended++;
            _log?.Invoke("[Vigil] relocate: ended the move after an error in EasyRelocate: " + __exception.GetType().Name + " " + __exception.Message);
            return null;
        }

        /// <summary>Ends the move the way EasyRelocate's own cancel does; if that fails, the way a relog does.</summary>
        private static void End(object controller, string reason)
        {
            try { _parts.Cancel.Invoke(controller, null); }
            catch (Exception e)
            {
                _log?.Invoke("[Vigil] relocate: EasyRelocate's cancel failed (" + e.GetBaseException().Message + "), putting the move back as a relog would");
                try { _parts.ForceReset.Invoke(controller, null); }
                catch (Exception) { }
            }
            Ended++;
            _log?.Invoke("[Vigil] relocate: ended the move (" + reason + ")");
        }

        // UpdateGhostFromVanilla(Player player)
        private static bool BeforeGhostUpdate(object __instance, Player __0)
        {
            if (!FixesOn || __0 == null || _moving == null) return true;
            var ghost = Ghost(__0);
            if (!Relocate.ReusesGameUpdate(true, _moving(), ghost != null && ghost == _updatedGhost, _updatedFrame, Time.frameCount)) return true;
            if (!(_parts.SourceValid.Invoke(__instance, null) is bool valid) || !valid)
            {
                _parts.Abort.Invoke(__instance, null);
                return false;
            }
            _parts.GhostPosition.SetValue(__instance, ghost.transform.position);
            _parts.GhostRotation.SetValue(__instance, ghost.transform.rotation);
            _parts.PlacementValid.SetValue(__instance, __0.GetPlacementStatus() == Player.PlacementStatus.Valid);
            Spared++;
            return false;
        }

        private static void AfterPlayerLateUpdate(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            _updatedGhost = Ghost(__instance);
            _updatedFrame = Time.frameCount;
        }

        // GhostBuilder.Create(Piece sourcePiece, GameObject prefab)
        private static void AfterCreateGhost(GameObject __result)
        {
            if (!SpiritOn || __result == null) return;
            try { MakeSpirit(__result); }
            catch (Exception e)
            {
                SpiritProblem = "the spirit could not be made: " + e.GetType().Name + " " + e.Message;
            }
        }

        // GhostTint.Apply(GameObject ghost, bool invalid): the spirit takes the place of EasyRelocate's tint.
        private static bool BeforeTint(GameObject __0, bool __1)
        {
            var spirit = SpiritOf(__0);
            if (spirit == null) return true;
            spirit.Blocked = __1;
            Tint(spirit);
            return false;
        }

        private static void MakeSpirit(GameObject ghost)
        {
            Sweep();
            var shader = SpiritShader();
            if (shader == null)
            {
                SpiritProblem = "no see-through shader was found in this game";
                return;
            }
            var made = new List<Material>();
            foreach (var r in ghost.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    // A white base: these shaders take the see-through amount from the texture's alpha
                    // times the tint's, and a piece's texture mostly leaves its alpha empty.
                    var m = new Material(shader) { renderQueue = 3000, mainTexture = Texture2D.whiteTexture };
                    mats[i] = m;
                    made.Add(m);
                }
                r.sharedMaterials = mats;
            }
            var spirit = new Spirit { Ghost = ghost, Materials = made.ToArray() };
            Spirits.Add(spirit);
            Tint(spirit);
            SpiritProblem = null;
            if (!_spiritTold)
            {
                _spiritTold = true;
                _log?.Invoke("[Vigil] relocate: spirit ghost for " + ghost.name + ": " + made.Count + " materials, shader " + shader.name + ", " + VertexColours(ghost));
            }
        }

        /// <summary>Whether the ghost's first readable mesh has vertex colours, which the sprite shader multiplies in.</summary>
        private static string VertexColours(GameObject ghost)
        {
            var mesh = ghost.GetComponentsInChildren<MeshFilter>(true).Select(f => f.sharedMesh).FirstOrDefault(x => x != null);
            if (mesh == null) return "no mesh";
            if (!mesh.isReadable) return "mesh " + mesh.name + " not readable";
            var colours = mesh.colors;
            if (colours == null || colours.Length == 0) return "mesh " + mesh.name + " without vertex colours";
            return "mesh " + mesh.name + " with vertex colours, alpha " + colours.Min(c => c.a).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                + " to " + colours.Max(c => c.a).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The spirit's shader, looked up once: Unity's lit see-through diffuse where the game ships it,
        /// else the sprite shader EasyRelocate draws its own lines with.
        /// </summary>
        private static Shader SpiritShader()
        {
            if (_spiritLooked) return _spiritShader;
            _spiritLooked = true;
            _spiritShader = Shader.Find("Legacy Shaders/Transparent/Diffuse") ?? Shader.Find("Sprites/Default");
            return _spiritShader;
        }

        private static Spirit SpiritOf(GameObject ghost)
        {
            if (ghost == null) return null;
            for (int i = 0; i < Spirits.Count; i++)
                if (Spirits[i].Ghost == ghost) return Spirits[i];
            return null;
        }

        private static void Breathe()
        {
            var player = Player.m_localPlayer;
            var spirit = player != null ? SpiritOf(Ghost(player)) : null;
            if (spirit != null) Tint(spirit);
        }

        private static void Tint(Spirit spirit)
        {
            var t = Relocate.SpiritTint(spirit.Blocked, Time.time);
            var colour = new Color(t.R, t.G, t.B, t.A);
            foreach (var m in spirit.Materials)
            {
                if (m == null) continue;
                m.SetColor(ColorId, colour);
            }
        }

        /// <summary>Frees the materials of spirits whose ghost is gone.</summary>
        private static void Sweep()
        {
            for (int i = Spirits.Count - 1; i >= 0; i--)
            {
                if (Spirits[i].Ghost != null) continue;
                foreach (var m in Spirits[i].Materials) if (m != null) UnityEngine.Object.Destroy(m);
                Spirits.RemoveAt(i);
            }
        }
    }
}
