using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// With the panel open the player can still walk, as with the game's own inventory open: the
    /// game's movement asks past the panel, everything else (attacks, use, the hotbar, the menus) sees
    /// it as a text box and stays held back, and while a field of the panel has the keyboard the keys
    /// go to the field. After a look with the right mouse the cursor goes back where the look began.
    /// </summary>
    public sealed class PanelControlsTests
    {
        [Fact]
        public void With_the_panel_open_the_game_walks_but_sees_a_text_box_for_everything_else()
        {
            Assert.False(PanelControls.SeesTextBox(panelOpen: true, movementAsks: true, typing: false));
            Assert.True(PanelControls.SeesTextBox(panelOpen: true, movementAsks: false, typing: false));
            Assert.False(PanelControls.SeesTextBox(panelOpen: false, movementAsks: false, typing: false));
        }

        [Fact]
        public void While_a_panel_field_has_the_keyboard_the_keys_go_to_the_field()
        {
            Assert.True(PanelControls.SeesTextBox(panelOpen: true, movementAsks: true, typing: true));
        }

        [Fact]
        public void Attacks_and_blocking_stay_held_back_while_the_panel_is_open()
        {
            Assert.True(PanelControls.HoldsAttacks(panelOpen: true));
            Assert.False(PanelControls.HoldsAttacks(panelOpen: false));
        }

        [Fact]
        public void After_a_look_the_cursor_goes_back_once_it_is_free_for_a_few_frames()
        {
            Assert.False(PanelControls.PutCursorBack(framesLeft: 2, cursorLocked: true));
            Assert.True(PanelControls.PutCursorBack(framesLeft: 2, cursorLocked: false));
            Assert.True(PanelControls.PutCursorBack(framesLeft: 1, cursorLocked: false));
            Assert.False(PanelControls.PutCursorBack(framesLeft: 0, cursorLocked: false));
            Assert.Equal(2, PanelControls.CursorBackFrames);
        }
    }
}
