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
        // PROBE_NO_GUARD=1 takes the guard of mods' own reads out again, to show the read cases tell the difference.
        if (Environment.GetEnvironmentVariable("PROBE_NO_GUARD") == "1")
        {
            hookType.GetMethod("RemoveGuard", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            Console.WriteLine("guard: taken out for this run");
        }

        sw = Stopwatch.StartNew();
        Scenarios(held);
        double heldMs = sw.Elapsed.TotalMilliseconds;
        bool holding = (bool)hold.GetType().GetProperty("Holding").GetValue(hold, null);
        Console.WriteLine("still holding before the end: " + holding);
        // FileInfo.Exists, which the guard leaves alone, so this look does not write the file.
        string bigBefore = new FileInfo(Path.Combine(held, "big.cfg")).Exists ? "exists" : "not written yet";
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
        bool lookups = Lookups(pre);
        bool jotunn = JotunnSync();
        return differ == 0 && lookups && jotunn ? 0 : 2;
    }

    // Vigil's patch on Jotunn's config sync, on Jotunn's own ApplyConfigZPackage: a package of 2,000
    // changed settings for a settings file, applied without the patch and with it; both must leave the
    // same values, and the second must be quicker.
    static bool JotunnSync()
    {
        string vigilPath = Path.Combine(root, "Vigil.dll"), jotunnPath = Path.Combine(root, "Jotunn.dll");
        if (!File.Exists(vigilPath) || !File.Exists(jotunnPath)) { Console.WriteLine("jotunn: Vigil.dll or Jotunn.dll not here, left out"); return true; }
        var patch = Assembly.LoadFrom(vigilPath).GetType("Vigil.Recorder.JotunnSyncPatch", true);
        var jotunn = Assembly.LoadFrom(jotunnPath);
        var syncType = jotunn.GetType("Jotunn.Managers.SynchronizationManager", true);
        var apply = syncType.GetMethod("ApplyConfigZPackage", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        var zpackage = Assembly.Load("assembly_valheim").GetType("ZPackage", true);

        // The manager without its Unity side: its collections made, as its field initializers would.
        object manager = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(syncType);
        foreach (var f in syncType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            if (f.FieldType.IsGenericType && f.FieldType.GetConstructor(Type.EmptyTypes) != null && f.GetValue(manager) == null) f.SetValue(manager, Activator.CreateInstance(f.FieldType));
        }

        var attrType = jotunn.GetType("ConfigurationManagerAttributes", true);
        var cfg = new ConfigFile(Path.Combine(root, "jotunnsync.cfg"), false);
        cfg.SaveOnConfigSet = false;
        var entries = new List<ConfigEntry<int>>();
        for (int s = 0; s < 40; s++) for (int k = 0; k < 50; k++)
        {
            object attr = Activator.CreateInstance(attrType);
            attrType.GetProperty("IsAdminOnly").SetValue(attr, true, null);
            entries.Add(cfg.Bind("Section " + s, "Key " + k, k, new ConfigDescription("a setting", null, attr)));
        }
        ((System.Collections.IDictionary)syncType.GetField("CustomConfigs", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(manager))["probe.sync"] = cfg;

        Func<object> package = () =>
        {
            object pkg = Activator.CreateInstance(zpackage);
            zpackage.GetMethod("Write", new[] { typeof(byte) }).Invoke(pkg, new object[] { (byte)0 });
            zpackage.GetMethod("Write", new[] { typeof(int) }).Invoke(pkg, new object[] { entries.Count });
            var write = zpackage.GetMethod("Write", new[] { typeof(string) });
            foreach (var e in entries)
            {
                write.Invoke(pkg, new object[] { "probe.sync" });
                write.Invoke(pkg, new object[] { e.Definition.Section });
                write.Invoke(pkg, new object[] { e.Definition.Key });
                write.Invoke(pkg, new object[] { ((int)e.DefaultValue + 1000).ToString() });
            }
            zpackage.GetMethod("SetPos").Invoke(pkg, new object[] { 0 });
            return pkg;
        };

        Func<double> run = () =>
        {
            foreach (var e in entries) e.Value = (int)e.DefaultValue;
            object pkg = package();
            var sw = Stopwatch.StartNew();
            apply.Invoke(manager, new object[] { pkg, null, null });
            return sw.Elapsed.TotalMilliseconds;
        };

        double without;
        try { without = run(); }
        catch (Exception e)
        {
            var inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
            Console.WriteLine("jotunn: Jotunn's ApplyConfigZPackage does not run outside the game here (" + inner.GetType().Name + ": " + inner.Message + ")");
            return false;
        }
        var first = entries.Select(e => e.Value).ToList();

        object problem = patch.GetMethod("Install", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { apply });
        Console.WriteLine("jotunn: hooks on " + jotunn.GetName().Version + "'s ApplyConfigZPackage and ConfigFile.Keys: " + (problem == null ? "in" : "NOT IN: " + problem));
        double with = run();
        var second = entries.Select(e => e.Value).ToList();
        int differ = first.Where((v, i) => v != second[i]).Count();
        long answered = (long)patch.GetProperty("Answered", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
        bool outside = cfg.Keys is ConfigDefinition[];
        Console.WriteLine(string.Format("jotunn: 2,000 synced settings applied: {0:0} ms without the patch, {1:0} ms with it; {2} values different; {3} checks answered from the snapshot; Keys outside the sync is BepInEx's own copy again: {4}",
            without, with, differ, answered, outside));
        Console.WriteLine("jotunn: " + patch.GetProperty("State", BindingFlags.Public | BindingFlags.Static).GetValue(null, null));
        return problem == null && differ == 0 && first.All(v => v >= 1000) && answered >= entries.Count && outside && with < without;
    }

    // BepInEx's own search for an assembly's DLL over a folder of 200 folders: a missing one asked
    // 40 times, without Vigil's memory of failed searches and with it, and one that is there.
    static bool Lookups(Assembly pre)
    {
        string dir = Path.Combine(root, "lookups");
        if (!Directory.Exists(dir))
        {
            for (int i = 0; i < 200; i++) Directory.CreateDirectory(Path.Combine(dir, "mod" + i.ToString("000")));
            File.Copy(Path.Combine(root, "Probe.exe"), Path.Combine(dir, "mod150", "Present.dll"), true);
        }

        var missing = new AssemblyName("EpicLoot");
        var present = new AssemblyName("Present");
        Assembly found;
        var sw = Stopwatch.StartNew();
        int hits = 0;
        for (int i = 0; i < 40; i++) if (Utility.TryResolveDllAssembly(missing, dir, out found)) hits++;
        double without = sw.Elapsed.TotalMilliseconds;
        bool presentBefore = Utility.TryResolveDllAssembly(present, dir, out found) && found != null;

        var hook = pre.GetType("Vigil.Startup.LookupHook", true);
        hook.GetMethod("Install", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
        object problem = hook.GetProperty("Problem", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
        Console.WriteLine("lookup memory: " + (problem == null ? "in" : "NOT IN: " + problem));
        sw = Stopwatch.StartNew();
        for (int i = 0; i < 40; i++) if (Utility.TryResolveDllAssembly(missing, dir, out found)) hits++;
        double with = sw.Elapsed.TotalMilliseconds;
        bool presentWith = Utility.TryResolveDllAssembly(present, dir, out found) && found != null;
        bool presentAgain = Utility.TryResolveDllAssembly(present, dir, out found) && found != null;

        object result = hook.GetMethod("End", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { "the probe ended it" });
        Console.WriteLine((string)result.GetType().GetMethod("Line").Invoke(result, null));
        sw = Stopwatch.StartNew();
        bool after = Utility.TryResolveDllAssembly(missing, dir, out found);
        double afterMs = sw.Elapsed.TotalMilliseconds;

        Console.WriteLine(string.Format("lookups: 40 searches for a missing assembly {0:0} ms without the memory, {1:0} ms with it; found {2} times; after the end one search {3:0.0} ms", without, with, hits, afterMs));
        Console.WriteLine(string.Format("lookups: an assembly that is there found before {0}, with the memory {1} and {2}", presentBefore, presentWith, presentAgain));
        return hits == 0 && !after && presentBefore && presentWith && presentAgain && with < without;
    }

    static ConfigFile File_(string dir, string name, string owner)
    {
        return new ConfigFile(Path.Combine(dir, name), true, owner == null ? null : new BepInPlugin("probe." + owner, owner, "1.2.3"));
    }

    // What a mod reading a file itself got: the text, or the exception it would have met.
    static void Seen(string dir, string name, Func<string> read)
    {
        string got;
        try { got = read(); }
        catch (Exception e) { got = "threw " + e.GetType().Name; }
        File.WriteAllText(Path.Combine(dir, name), got);
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

        // Mods reading a settings file themselves, past BepInEx, while the game starts: what each saw
        // is written beside the files, so the two runs compare it too.
        var reader = File_(dir, "readpast.cfg", "ReadPast");
        string readpast = Path.Combine(dir, "readpast.cfg");
        reader.Bind("R", "a", 1, "first");
        Seen(dir, "seen-readalltext.txt", () => File.ReadAllText(readpast));
        reader.Bind("R", "b", 2, "second");
        Seen(dir, "seen-streamreader.txt", () => { using (var sr = new StreamReader(readpast)) return sr.ReadToEnd(); });
        reader.Bind("R", "c", 3, "third");
        Seen(dir, "seen-readallbytes.txt", () => Convert.ToBase64String(File.ReadAllBytes(readpast)));

        // A brand-new settings file checked for: it is there, as it would be.
        var fresh = File_(dir, "fresh.cfg", "Fresh");
        fresh.Bind("F", "f", 1, "new");
        Seen(dir, "seen-exists.txt", () => File.Exists(Path.Combine(dir, "fresh.cfg")) ? "exists" : "missing");

        // A settings file copied, both ways: each copy holds what the file would.
        var copied = File_(dir, "copied.cfg", "Copied");
        copied.Bind("C", "c", 1, "copied");
        Seen(dir, "seen-copy-by-file.txt", () => { File.Copy(Path.Combine(dir, "copied.cfg"), Path.Combine(dir, "copy-by-file.txt"), true); return "copied"; });
        copied.Bind("C", "d", 2, "copied again");
        Seen(dir, "seen-copy-by-fileinfo.txt", () => { new FileInfo(Path.Combine(dir, "copied.cfg")).CopyTo(Path.Combine(dir, "copy-by-fileinfo.txt"), true); return "copied"; });

        // A mod watching its own settings file, as many do: the runtime's watcher looks for the file
        // every 750 ms on a thread of its own, which is no mod reading it, so the file stays held.
        var watched = File_(dir, "watched.cfg", "Watched");
        watched.Bind("W", "w", 1, "watched");
        var fsw = new FileSystemWatcher(dir, "watched.cfg");
        fsw.EnableRaisingEvents = true;
        watched.Bind("W", "x", 2, "watched again");
        System.Threading.Thread.Sleep(2000);
        fsw.EnableRaisingEvents = false;
        fsw.Dispose();
        if (dir.EndsWith("held", StringComparison.Ordinal))
        {
            Console.WriteLine("watched.cfg after 2 s of watching: " + (new FileInfo(Path.Combine(dir, "watched.cfg")).Exists ? "written" : "still held"));
        }

        // After the watcher, this thread is still guarded: a mod's own read here is written first.
        var after = File_(dir, "afterwatch.cfg", "AfterWatch");
        after.Bind("A", "a", 1, "after the watcher");
        Seen(dir, "seen-afterwatch.txt", () => File.ReadAllText(Path.Combine(dir, "afterwatch.cfg")));

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
