namespace Vigil.Core
{
    /// <summary>
    /// The panel's buttons for a file or folder copy its path to the clipboard, to paste into Explorer
    /// or an editor, rather than open it: a program the game starts counts as part of the game for
    /// Steam, and Explorer handed a path it cannot read opens a folder of its own choosing. The button
    /// reads "Copied" for a moment after a press.
    /// </summary>
    internal static class PathCopy
    {
        public const string Idle = "Copy path", Done = "Copied";

        /// <summary>How long a button reads <see cref="Done"/> after its path was copied.</summary>
        public const float ShowSeconds = 2f;

        /// <summary>The words of the button for <paramref name="path"/>, given the path copied last and when.</summary>
        public static string Label(string copied, float copiedAt, string path, float now, string idle = Idle) =>
            copied != null && copied == path && now >= copiedAt && now - copiedAt < ShowSeconds ? Done : idle;
    }
}
