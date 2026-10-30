using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Vigil.Recorder
{
    /// <summary>
    /// A hash of methods' code by what it does, for Vigil's patches on other mods to tell an update
    /// that changes the code they rely on from one that leaves it alone. The code's raw bytes hold
    /// metadata tokens, numbers a mod's new parts renumber in unchanged code; this hash takes each
    /// instruction's operation and, for its operand, the name of the member or type it uses (with
    /// the declaring type's name), the text, the number, or the index of the instruction it jumps
    /// to. Read with Mono.Cecil, which BepInEx ships.
    /// </summary>
    internal static class CodeShape
    {
        private const ulong Offset = 14695981039346656037UL, Prime = 1099511628211UL;

        /// <summary>FNV-1a over each method's instructions in turn.</summary>
        public static ulong Hash(IEnumerable<MethodDefinition> methods)
        {
            ulong h = Offset;
            foreach (var method in methods)
            {
                if (method?.Body == null) throw new InvalidOperationException("a method without code: " + method?.FullName);
                var code = method.Body.Instructions;
                foreach (var ins in code)
                {
                    h = Mix(h, (ushort)ins.OpCode.Value);
                    h = Mix(h, Operand(ins.Operand, code));
                    h = Mix(h, (ushort)0);
                }
                h = Mix(h, (ushort)0xFFFF);
            }
            return h;
        }

        /// <summary>The loaded methods' code, read from the files their assemblies were loaded from.</summary>
        public static ulong Hash(IEnumerable<MethodBase> methods)
        {
            var modules = new Dictionary<string, ModuleDefinition>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var found = new List<MethodDefinition>();
                foreach (var method in methods)
                {
                    string path = method?.Module?.Assembly?.Location;
                    if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("no file for " + method?.Name);
                    if (!modules.TryGetValue(path, out var module))
                    {
                        // Read into memory so the mod's file is not held open.
                        module = ModuleDefinition.ReadModule(path, new ReaderParameters { ReadingMode = ReadingMode.Deferred, InMemory = true });
                        modules[path] = module;
                    }
                    var definition = module.LookupToken(method.MetadataToken) as MethodDefinition;
                    if (definition == null) throw new InvalidOperationException(method.Name + " is not in " + path);
                    found.Add(definition);
                }
                return Hash(found);
            }
            finally
            {
                foreach (var module in modules.Values) module.Dispose();
            }
        }

        private static string Operand(object operand, Mono.Collections.Generic.Collection<Instruction> code)
        {
            switch (operand)
            {
                case null: return "";
                case string s: return "s:" + s;
                case MethodReference m: return "m:" + Owner(m) + m.Name;
                case FieldReference f: return "f:" + Owner(f) + f.Name;
                case TypeReference t: return "t:" + t.Name;
                case Instruction target: return "i:" + code.IndexOf(target).ToString(CultureInfo.InvariantCulture);
                case Instruction[] targets:
                    var parts = new string[targets.Length];
                    for (int i = 0; i < targets.Length; i++) parts[i] = code.IndexOf(targets[i]).ToString(CultureInfo.InvariantCulture);
                    return "w:" + string.Join(",", parts);
                case VariableDefinition v: return "v:" + v.Index.ToString(CultureInfo.InvariantCulture);
                case ParameterDefinition p: return "p:" + p.Index.ToString(CultureInfo.InvariantCulture);
                case CallSite _: return "c:";
                default: return Number(operand);
            }
        }

        /// <summary>
        /// A number operand as text that reads the same on every runtime: a float or double by its
        /// bits (the text "R" gives differs between Mono or .NET Framework and later .NET), a whole
        /// number in invariant digits.
        /// </summary>
        internal static string Number(object operand)
        {
            switch (operand)
            {
                case float x: return "r:" + BitConverter.ToInt32(BitConverter.GetBytes(x), 0).ToString("X8", CultureInfo.InvariantCulture);
                case double x: return "d:" + BitConverter.DoubleToInt64Bits(x).ToString("X16", CultureInfo.InvariantCulture);
                case IFormattable n: return "n:" + n.ToString(null, CultureInfo.InvariantCulture);
                default: return "?:" + operand.GetType().Name;
            }
        }

        private static string Owner(MemberReference member) => member.DeclaringType != null ? member.DeclaringType.Name + "::" : "";

        private static ulong Mix(ulong h, ushort value)
        {
            h ^= (byte)value;
            h *= Prime;
            h ^= (byte)(value >> 8);
            h *= Prime;
            return h;
        }

        private static ulong Mix(ulong h, string text)
        {
            foreach (char c in text) h = Mix(h, (ushort)c);
            return h;
        }
    }
}
