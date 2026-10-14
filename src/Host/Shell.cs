using System;
using System.Diagnostics;
using System.IO;

namespace Vigil;

/// <summary>
/// Opens a file or a folder as a double-click in Explorer would, through Explorer: a short-lived
/// explorer.exe hands the path to the running desktop shell, which starts the program for it, so
/// that program (a text editor, a folder window) belongs to the shell, not to the game. Steam
/// counts every process the game starts as part of the game: an editor the game opened itself
/// (Unity's Application.OpenURL) kept Steam showing Valheim as running after it quit, and
/// refusing to start it again, until that editor was closed.
/// </summary>
internal static class Shell
{
    public static void Open(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using (Process.Start(new ProcessStartInfo(explorer, "\"" + path + "\"") { UseShellExecute = false }))
            {
            }
        }
        catch (Exception)
        {
            // nothing opened; the answer and the file name still say where it is
        }
    }
}
