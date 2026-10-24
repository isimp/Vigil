using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;

// Runs the same config scenarios with BepInEx's own ConfigFile twice, without Vigil's hold and with it,
// in Valheim's Mono, and compares every file the two runs leave byte for byte.
public static class Probe
{
    enum Mode { Low, High, Off }

    static string root;

    public static int Main()
    {
        root = Environment.GetEnvironmentVariable("PROBE_DIR");
        // ConfigFile's static setup opens BepInEx's own config, so BepInEx's paths come first (a folder of the probe's).
        var setPath = typeof(Paths).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).First(m => m.Name == "SetExecutablePath");
        var args = setPath.GetParameters().Select(p => p.Position == 0 ? (object)Path.Combine(root, "Probe.exe") : p.Position == 1 ? (object)Path.Combine(root, "BepInEx") : null).ToArray();
        setPath.Invoke(null, args);
        string plain = Path.Combine(root, "plain"), held = Path.Combine(root, "held");
        foreach (var d in new[] { plain, held }) { if (Directory.Exists(d)) Directory.Delete(d, true); Directory.CreateDirectory(d); }

        var sw = Stopwatch.StartNew();
        Scenarios(plain);
        double plainMs = sw.Elapsed.TotalMilliseconds;

        var pre = Assembly.LoadFrom(Path.Combine(root, "Vigil.Preloader.dll"));
        var hookType = pre.GetType("Vigil.Startup.ConfigSaveHook", true);
        hookType.GetMethod("Install", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
        object problem = hookType.GetProperty("Problem", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
        object hold = hookType.GetProperty("Hold", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
        Console.WriteLine("hook: " + (hold != null ? "in" : "NOT IN: " + problem));
        if (hold == null) return 1;

        sw = Stopwatch.StartNew();
        Scenarios(held);
        double heldMs = sw.Elapsed.TotalMilliseconds;
        bool holding = (bool)hold.GetType().GetProperty("Holding").GetValue(hold, null);
        Console.WriteLine("still holding before the end: " + holding);
        string bigBefore = File.Exists(Path.Combine(held, "big.cfg")) ? "exists" : "not written yet";
        Console.WriteLine("big.cfg before the end: " + bigBefore);

        sw = Stopwatch.StartNew();
        object result = hookType.GetMethod("End", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { "the probe ended it" });
        double endMs = sw.Elapsed.TotalMilliseconds;
        foreach (var line in (List<string>)result.GetType().GetMethod("Lines").Invoke(result, null)) Console.WriteLine(line);
        var check = (string[])hookType.GetProperty("Check", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
        Console.WriteLine("read back: " + string.Join(" | ", check));

        Console.WriteLine(string.Format("time: without the hold {0:0} ms, with it {1:0} ms plus {2:0} ms writing at the end", plainMs, heldMs, endMs));

        int same = 0, differ = 0;
        foreach (var f in Directory.GetFiles(plain).Select(Path.GetFileName).Union(Directory.GetFiles(held).Select(Path.GetFileName)).OrderBy(n => n))
        {
            string a = Path.Combine(plain, f), b = Path.Combine(held, f);
            bool equal = File.Exists(a) && File.Exists(b) && File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
            if (equal) same++; else differ++;
            Console.WriteLine((equal ? "same    " : "DIFFERS ") + f + (File.Exists(a) ? "" : " (missing without the hold)") + (File.Exists(b) ? "" : " (missing with the hold)"));
        }

        Console.WriteLine(string.Format("files: {0} the same, {1} different", same, differ));
        return differ == 0 ? 0 : 2;
    }

    static ConfigFile File_(string dir, string name, string owner)
    {
        return new ConfigFile(Path.Combine(dir, name), true, owner == null ? null : new BepInPlugin("probe." + owner, owner, "1.2.3"));
    }

    static void Scenarios(string dir)
    {
        // A file the size of OdinArchitect's: many sections, many settings, each with a description.
        var big = File_(dir, "big.cfg", "Big");
        for (int s = 0; s < 330; s++)
        {
            string section = "Piece " + s.ToString("000") + ": Wall, Stone";
            big.Bind(section, "Enabled", s % 3 != 0, new ConfigDescription("Whether piece " + s + " can be built."));
            big.Bind(section, "Recipe", "Stone:" + s + ",Wood:2", new ConfigDescription("What it costs, \"quoted\" and = signs."));
            big.Bind(section, "Comfort", s % 7, new ConfigDescription("Comfort it gives.", new AcceptableValueRange<int>(0, 10)));
            big.Bind(section, "Mode", (Mode)(s % 3), new ConfigDescription("A list of values."));
        }

        var enabled = big.Bind("Piece 005: Wall, Stone", "Enabled", true, "already bound");
        enabled.Value = !enabled.Value;

        // A value set during the start, then read back from disk: it stays.
        var back = File_(dir, "readback.cfg", "ReadBack");
        var level = back.Bind("General", "Level", 1, "a level");
        level.Value = 5;
        back.Reload();
        back.Bind("General", "After", "x", "bound after the read-back");

        // Written by its mod itself, then read back: the mod's write stands.
        var own = File_(dir, "ownwrite.cfg", "OwnWrite");
        own.Bind("S", "k", 1, "a key");
        own.Bind("S", "other", "a", "another key");
        File.WriteAllText(Path.Combine(dir, "ownwrite.cfg"), "[S]\nk = 7\n");
        own.Reload();

        // Written from outside after its last save, never read back: the outside write stands.
        var late = File_(dir, "latewrite.cfg", null);
        late.Bind("S", "k", 1, "a key");
        File.WriteAllText(Path.Combine(dir, "latewrite.cfg"), "[S]\nk = 9\n");

        // Two settings objects on one file: the last to save wins.
        var one = File_(dir, "shared.cfg", "One");
        var two = File_(dir, "shared.cfg", "Two");
        one.Bind("A", "a", 1, "from one");
        two.Bind("B", "b", 2, "from two");
        one.Bind("A", "c", 3, "from one again");

        // A second settings object opened on a new file the first has saved: it reads the first one's
        // values as it opens, and its own save keeps them.
        var x = File_(dir, "opened.cfg", "X");
        x.Bind("X", "x1", 1, "from x");
        var y = File_(dir, "opened.cfg", "Y");
        y.Bind("Y", "y1", 2, "from y");

        // StartupAccelerator's way: saving switched off, values set, one Save asked for.
        var quiet = File_(dir, "quiet.cfg", "Quiet");
        quiet.SaveOnConfigSet = false;
        var q = quiet.Bind("Q", "q", 1, "quiet");
        q.Value = 4;
        quiet.Save();
        quiet.SaveOnConfigSet = true;
        quiet.Bind("Q", "r", 2, "bound with saving on again");
    }
}
