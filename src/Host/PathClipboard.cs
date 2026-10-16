using System;
using System.IO;
using UnityEngine;
using Vigil.Core;

namespace Vigil;

/// <summary>
/// Puts a file's or folder's path on the clipboard for the panel's copy buttons (<see cref="PathCopy"/>),
/// written the way the system writes paths, and remembers which path was copied when, for the
/// button's "Copied".
/// </summary>
internal static class PathClipboard
{
    private static string s_copied;
    private static float s_copiedAt;

    public static void Copy(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        string text = path;
        try
        {
            text = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            // the path as Vigil holds it is still one to paste
        }

        GUIUtility.systemCopyBuffer = text;
        s_copied = path;
        s_copiedAt = Time.unscaledTime;
    }

    /// <summary>No button reads "Copied" any more (the self-test's copy is not the player's).</summary>
    public static void Forget() => s_copied = null;

    /// <summary>The words of the copy button for <paramref name="path"/>.</summary>
    public static string Label(string path, string idle = PathCopy.Idle) =>
        PathCopy.Label(s_copied, s_copiedAt, path, Time.unscaledTime, idle);
}
