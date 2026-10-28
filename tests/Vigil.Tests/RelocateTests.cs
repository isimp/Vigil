using System;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The game patches on EasyRelocate: a grab that does not freeze the game, a move that costs what
    /// building with the hammer costs, a move that can always be ended, and a ghost that looks like a spirit.
    /// </summary>
    public sealed class RelocateTests
    {
        [Fact]
        public void The_first_grab_does_not_search_every_mod_for_a_mod_that_is_not_installed()
        {
            Assert.True(Relocate.SkipsTypeSearch(on: true, typeLoaded: false));
        }

        [Fact]
        public void A_mod_EasyRelocate_works_with_is_still_looked_up_when_it_is_installed_or_the_switch_is_off()
        {
            Assert.False(Relocate.SkipsTypeSearch(on: true, typeLoaded: true));
            Assert.False(Relocate.SkipsTypeSearch(on: false, typeLoaded: false));
        }

        [Fact]
        public void A_move_keeps_its_hooks_in_the_game_after_it_ends_so_the_next_grab_rebuilds_nothing()
        {
            Assert.True(Relocate.KeepsHooks(on: true));
            Assert.False(Relocate.KeepsHooks(on: false));
        }

        [Fact]
        public void The_grab_key_ends_a_move_even_while_another_key_is_held()
        {
            // BepInEx's shortcut check fails while any other key is held, so U alone did nothing while walking.
            Assert.Equal("the grab key", Relocate.CancelReason(on: true, moving: true, grabKeyDown: true, grabModifiersHeld: true, escapeDown: false, textFieldFocused: false, toolInHand: false));
        }

        [Fact]
        public void Escape_ends_a_move()
        {
            Assert.Equal("Esc", Relocate.CancelReason(on: true, moving: true, grabKeyDown: false, grabModifiersHeld: true, escapeDown: true, textFieldFocused: false, toolInHand: false));
        }

        [Fact]
        public void A_text_box_taking_the_keys_ends_a_move_instead_of_freezing_it()
        {
            Assert.Equal("a text box took the keys", Relocate.CancelReason(on: true, moving: true, grabKeyDown: false, grabModifiersHeld: true, escapeDown: false, textFieldFocused: true, toolInHand: false));
        }

        [Fact]
        public void Nothing_is_ended_outside_a_move_with_the_switch_off_or_without_the_grab_keys_own_modifiers()
        {
            Assert.Null(Relocate.CancelReason(on: true, moving: false, grabKeyDown: true, grabModifiersHeld: true, escapeDown: true, textFieldFocused: true, toolInHand: false));
            Assert.Null(Relocate.CancelReason(on: false, moving: true, grabKeyDown: true, grabModifiersHeld: true, escapeDown: true, textFieldFocused: true, toolInHand: false));
            Assert.Null(Relocate.CancelReason(on: true, moving: true, grabKeyDown: true, grabModifiersHeld: false, escapeDown: false, textFieldFocused: false, toolInHand: false));
            Assert.Null(Relocate.CancelReason(on: true, moving: true, grabKeyDown: false, grabModifiersHeld: true, escapeDown: false, textFieldFocused: false, toolInHand: false));
        }

        [Fact]
        public void Taking_a_build_tool_in_hand_ends_a_move()
        {
            // Equipping the hammer, hoe or cultivator hands the game its piece table in place of EasyRelocate's.
            Assert.Equal("a build tool was taken in hand", Relocate.CancelReason(on: true, moving: true, grabKeyDown: false, grabModifiersHeld: true, escapeDown: false, textFieldFocused: false, toolInHand: true));
            Assert.Null(Relocate.CancelReason(on: false, moving: true, grabKeyDown: false, grabModifiersHeld: true, escapeDown: false, textFieldFocused: false, toolInHand: true));
            Assert.Null(Relocate.CancelReason(on: true, moving: false, grabKeyDown: false, grabModifiersHeld: true, escapeDown: false, textFieldFocused: false, toolInHand: true));
        }

        [Fact]
        public void A_piece_cannot_be_grabbed_with_a_build_tool_in_hand_and_no_grab_is_offered()
        {
            Assert.True(Relocate.BlocksGrab(on: true, toolInHand: true));
        }

        [Fact]
        public void The_grab_key_with_a_build_tool_in_hand_says_to_put_that_tool_away()
        {
            Assert.Equal("Relocate: put the Hammer away first", Relocate.RefusedGrabLine(blocked: true, grabPressed: true, takesInput: true, tool: "Hammer"));
        }

        [Fact]
        public void No_refusal_is_shown_without_the_grab_key_in_a_menu_or_when_the_grab_is_allowed()
        {
            Assert.Null(Relocate.RefusedGrabLine(blocked: true, grabPressed: false, takesInput: true, tool: "Hammer"));
            Assert.Null(Relocate.RefusedGrabLine(blocked: true, grabPressed: true, takesInput: false, tool: "Hammer"));
            Assert.Null(Relocate.RefusedGrabLine(blocked: false, grabPressed: true, takesInput: true, tool: "Hammer"));
        }

        [Fact]
        public void A_tool_without_a_name_is_called_the_tool()
        {
            Assert.Equal("Relocate: put the tool away first", Relocate.RefusedGrabLine(blocked: true, grabPressed: true, takesInput: true, tool: ""));
            Assert.Equal("Relocate: put the tool away first", Relocate.RefusedGrabLine(blocked: true, grabPressed: true, takesInput: true, tool: null));
        }

        [Fact]
        public void A_piece_can_be_grabbed_with_no_build_tool_in_hand_or_the_switch_off()
        {
            Assert.False(Relocate.BlocksGrab(on: true, toolInHand: false));
            Assert.False(Relocate.BlocksGrab(on: false, toolInHand: true));
        }

        [Fact]
        public void An_error_during_a_move_ends_the_move_as_a_relog_would()
        {
            Assert.True(Relocate.EndsAfterError(on: true, moving: true, failed: true));
            Assert.False(Relocate.EndsAfterError(on: true, moving: false, failed: true));
            Assert.False(Relocate.EndsAfterError(on: true, moving: true, failed: false));
            Assert.False(Relocate.EndsAfterError(on: false, moving: true, failed: true));
        }

        [Fact]
        public void A_moving_ghost_the_game_updated_last_frame_is_not_updated_a_second_time()
        {
            // The game updates the placement ghost in Player.LateUpdate; EasyRelocate did it again in its own Update.
            Assert.True(Relocate.ReusesGameUpdate(on: true, moving: true, sameGhost: true, updatedFrame: 99, frame: 100));
            Assert.True(Relocate.ReusesGameUpdate(on: true, moving: true, sameGhost: true, updatedFrame: 100, frame: 100));
        }

        [Fact]
        public void A_new_ghost_or_one_the_game_has_not_updated_lately_gets_EasyRelocates_own_update()
        {
            Assert.False(Relocate.ReusesGameUpdate(on: true, moving: true, sameGhost: false, updatedFrame: 99, frame: 100));
            Assert.False(Relocate.ReusesGameUpdate(on: true, moving: true, sameGhost: true, updatedFrame: 98, frame: 100));
            Assert.False(Relocate.ReusesGameUpdate(on: true, moving: false, sameGhost: true, updatedFrame: 99, frame: 100));
            Assert.False(Relocate.ReusesGameUpdate(on: false, moving: true, sameGhost: true, updatedFrame: 99, frame: 100));
        }

        [Fact]
        public void The_spirit_is_see_through_pale_blue_and_pale_red_where_it_cannot_be_placed()
        {
            var free = Relocate.SpiritTint(blocked: false, seconds: 0f);
            var blocked = Relocate.SpiritTint(blocked: true, seconds: 0f);

            Assert.True(free.B > free.R && free.B >= free.G, "pale blue");
            Assert.True(blocked.R > blocked.B && blocked.R > blocked.G, "pale red");
            Assert.InRange(free.A, 0.05f, 0.6f);
            Assert.InRange(blocked.A, 0.05f, 0.6f);
        }

        [Fact]
        public void The_spirit_breathes_slowly_and_never_turns_solid_or_vanishes()
        {
            float low = float.MaxValue, high = float.MinValue;
            for (int i = 0; i < 400; i++)
            {
                float a = Relocate.SpiritTint(blocked: false, seconds: i * 0.01f).A;
                low = Math.Min(low, a);
                high = Math.Max(high, a);
            }

            Assert.True(high - low > 0.08f, "it pulses");
            Assert.True(low >= 0.15f && high <= 0.55f, "it stays a spirit: " + low + " to " + high);
            Assert.Equal(Relocate.SpiritTint(false, 0f).A, Relocate.SpiritTint(false, Relocate.PulseSeconds).A, 3);
        }

        [Fact]
        public void The_states_say_what_the_patches_do_on_this_game()
        {
            Assert.Equal("off: this game's switch (Patches.EasyRelocateFixes)", Relocate.FixesLine(on: false, found: true, version: "1.1.1", problem: null, ended: 0, spared: 0));
            Assert.Equal("not needed: EasyRelocate is not installed", Relocate.FixesLine(on: true, found: false, version: null, problem: null, ended: 0, spared: 0));
            Assert.Equal("stepped aside: its parts changed (an update)", Relocate.FixesLine(on: true, found: true, version: "1.1.1", problem: "its parts changed (an update)", ended: 0, spared: 0));
            Assert.Equal("working · EasyRelocate 1.1.1 · no freeze on a grab, one ghost update a frame, a move can always be ended",
                Relocate.FixesLine(on: true, found: true, version: "1.1.1", problem: null, ended: 0, spared: 0));
            Assert.Equal("working · EasyRelocate 1.1.1 · no freeze on a grab, one ghost update a frame, a move can always be ended · 2 moves ended by Vigil · 340 ghost updates spared",
                Relocate.FixesLine(on: true, found: true, version: "1.1.1", problem: null, ended: 2, spared: 340));
            Assert.Equal("off: this game's switch (Patches.EasyRelocateSpiritGhost)", Relocate.SpiritLine(on: false, found: true, problem: null));
            Assert.Equal("not needed: EasyRelocate is not installed", Relocate.SpiritLine(on: true, found: false, problem: null));
            Assert.Equal("stepped aside: no see-through shader was found in this game", Relocate.SpiritLine(on: true, found: true, problem: "no see-through shader was found in this game"));
            Assert.Equal("working · the moving ghost is a see-through spirit", Relocate.SpiritLine(on: true, found: true, problem: null));
        }

        [Fact]
        public void EasyRelocate_111s_parts_are_found()
        {
            var parts = Relocate.FindParts(Er111);

            Assert.NotNull(parts);
            Assert.Equal("Update", parts.Update.Name);
            Assert.Equal("UpdateGhostFromVanilla", parts.UpdateGhost.Name);
            Assert.Equal("TryPatch", parts.TryPatch.Name);
            Assert.Equal("Remove", parts.RemoveHooks.Name);
            Assert.Equal("Apply", parts.ApplyHooks.Name);
            Assert.Equal("Create", parts.CreateGhost.Name);
            Assert.Equal("Apply", parts.Tint.Name);
            Assert.Equal("_placementValid", parts.PlacementValid.Name);
            Assert.Equal("TryBeginRelocate", parts.TryBegin.Name);
            Assert.Equal("LateUpdate", parts.HoverHint.Name);
        }

        [Fact]
        public void Another_shape_or_no_EasyRelocate_is_not_taken()
        {
            Assert.Null(Relocate.FindParts(name => null));
            Assert.Null(Relocate.FindParts(name => name == Relocate.ControllerType ? typeof(Changed.RelocateController) : Er111(name)));
        }

        private static Type Er111(string name)
        {
            switch (name)
            {
                case Relocate.ControllerType: return typeof(RelocateController);
                case Relocate.CompatType: return typeof(FreePlacementRotationCompat);
                case Relocate.HooksType: return typeof(VanillaPlacementSessionPatches);
                case Relocate.PluginType: return typeof(RelocatePlugin);
                case Relocate.UtilsType: return typeof(RelocateUtils);
                case Relocate.GhostBuilderType: return typeof(GhostBuilder);
                case Relocate.GhostTintType: return typeof(GhostTint);
                case Relocate.HoverHintType: return typeof(RelocateHoverHint);
                default: return null;
            }
        }

        // Stand-ins with the shapes of EasyRelocate 1.1.1's members (the game's and Harmony's types as plain classes).
        private sealed class Player { }
        private sealed class Piece { }
        private sealed class GameObject { }
        private sealed class Harmony { }
        private sealed class HarmonyMethod { }
        private struct Vector3 { }
        private struct Quaternion { }
        private struct KeyboardShortcut { }
        private sealed class ConfigEntry<T> { }

#pragma warning disable CS0169, CS0649, IDE0051, IDE0044
        private sealed class RelocateController
        {
            private Vector3 _ghostPosition;
            private Quaternion _ghostRotation;
            private bool _placementValid;
            internal static bool IsMoving { get; private set; }
            private void Update() { }
            private void TryBeginRelocate(Player player) { }
            private void UpdateGhostFromVanilla(Player player) { }
            private void CancelRelocate() { }
            private void AbortRelocate() { }
            private void ForceReset() { }
            private bool IsSourceStillValid() => true;
        }

        private static class FreePlacementRotationCompat
        {
            private static void TryPatch(Harmony harmony, string typeName, HarmonyMethod prefix) { }
        }

        private static class VanillaPlacementSessionPatches
        {
            internal static void Apply(Harmony harmony) { }
            internal static void Remove(Harmony harmony) { }
        }

        private sealed class RelocatePlugin
        {
            internal static ConfigEntry<KeyboardShortcut> GrabKey;
            internal static Harmony HarmonyInstance => null;
        }

        private static class RelocateUtils
        {
            internal static bool IsTextFieldFocused() => false;
        }

        private static class GhostBuilder
        {
            internal static GameObject Create(Piece sourcePiece, GameObject prefab) => null;
        }

        private static class GhostTint
        {
            private static void Apply(GameObject ghost, bool invalid) { }
        }

        private sealed class RelocateHoverHint
        {
            private void LateUpdate() { }
        }

        // The controller with its ghost update renamed, as an update might.
        private static class Changed
        {
            internal sealed class RelocateController
            {
                private Vector3 _ghostPosition;
                private Quaternion _ghostRotation;
                private bool _placementValid;
                internal static bool IsMoving { get; private set; }
                private void Update() { }
                private void FollowGhost(Player player) { }
                private void CancelRelocate() { }
                private void AbortRelocate() { }
                private void ForceReset() { }
                private bool IsSourceStillValid() => true;
            }
        }
#pragma warning restore CS0169, CS0649, IDE0051, IDE0044
    }
}
