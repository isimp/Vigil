namespace Vigil.Core
{
    /// <summary>
    /// What the game may do while the panel is open. The game holds back its keys and frees the cursor
    /// while one of its text boxes is up, and lets the player walk, run, jump and crouch, but not
    /// attack, block or dodge, while its inventory is open. The panel takes both: the game's movement
    /// (<c>PlayerController</c>'s own input check) asks past the panel unless a field of the panel has
    /// the keyboard, everything else sees a text box, and attacks are held back as with the inventory.
    /// A look with the right mouse locks the cursor in the middle of the screen; once it is free again,
    /// the cursor is put back where the look began, for a few frames, as the game may centre it again
    /// on the frame it frees it.
    /// </summary>
    internal static class PanelControls
    {
        public const int CursorBackFrames = 2;

        /// <summary>Whether the game is told a text box is up: always with the panel open, except when its movement asks and no panel field has the keyboard.</summary>
        public static bool SeesTextBox(bool panelOpen, bool movementAsks, bool typing) => panelOpen && (!movementAsks || typing);

        public static bool HoldsAttacks(bool panelOpen) => panelOpen;

        /// <summary>Whether the cursor is put where the look began this frame.</summary>
        public static bool PutCursorBack(int framesLeft, bool cursorLocked) => framesLeft > 0 && !cursorLocked;
    }
}
