using System;
using System.Collections.Generic;

namespace Vigil.Core
{
    /// <summary>
    /// The icon a button's words call for, by its first word, so every button shows what it does at
    /// a glance: Run and Start a triangle, Stop a square, Write a page, Open an arrow out of a box,
    /// a folder for anything that opens one, Refresh, Again and Read a circling arrow, Find a lens,
    /// Get an arrow down onto a line, Send an arrow up from one, Show and Mark an eye, Hide and Unmark
    /// an eye struck through, Clear and Close a cross, Set and Seen a tick, Compare two columns, Reset an arrow
    /// turning back, Remove (and its Press again to remove) a bin, Pin a map pin and Unpin one struck through, Scan a lens, Confirm (the second press of a removal) a bin, Copy two pages and Copied a tick, Freeze two bars, Back the arrow turning back, and a clock while something is under way. The panel draws them
    /// (<c>Skin.IconFor</c>); a test checks every button in the panel has one.
    /// </summary>
    internal static class ButtonIcon
    {
        public static readonly string[] Names =
        {
            "play", "stop", "doc", "external", "folder", "refresh", "search", "download", "upload", "eye", "eye-off", "close",
            "check", "compare", "reset", "trash", "wait", "pin", "pin-off", "copy", "pause",
        };

        private static readonly Dictionary<string, string> ByWord = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run"] = "play", ["start"] = "play", ["stop"] = "stop", ["write"] = "doc", ["open"] = "external",
            ["refresh"] = "refresh", ["again"] = "refresh", ["read"] = "refresh", ["find"] = "search", ["get"] = "download",
            ["send"] = "upload", ["show"] = "eye", ["mark"] = "eye", ["hide"] = "eye-off", ["unmark"] = "eye-off",
            ["clear"] = "close", ["close"] = "close", ["set"] = "check", ["seen"] = "check", ["compare"] = "compare", ["reset"] = "reset",
            ["remove"] = "trash", ["press"] = "trash", ["running"] = "wait", ["watching"] = "wait", ["getting"] = "wait", ["comparing"] = "wait",
            ["how"] = "wait", ["pin"] = "pin", ["unpin"] = "pin-off", ["scan"] = "search", ["confirm"] = "trash",
            ["copy"] = "copy", ["copied"] = "check", ["freeze"] = "pause", ["back"] = "reset",
        };

        /// <summary>The icon's name for a button's words; null for words with none.</summary>
        public static string For(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;
            if (label.IndexOf("folder", StringComparison.OrdinalIgnoreCase) >= 0) return "folder";
            string first = label.Trim().Split(' ')[0].ToLowerInvariant();
            return ByWord.TryGetValue(first, out string name) ? name : null;
        }
    }
}
