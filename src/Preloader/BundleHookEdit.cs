using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Vigil.Startup
{
    /// <summary>
    /// The edit made to Unity's AssetBundleModule while BepInEx's preloader runs, before any plugin
    /// loads. Each of Unity's public ways to load a bundle from memory or a stream ends in one of four
    /// internal methods; each of those first asks <c>BundleHook</c> and returns its answer when it gives
    /// one, and otherwise goes on exactly as before:
    /// <code>if (BundleHook.TryMemory(binary, crc, out var result)) return result;</code>
    /// The hook is referred to by name only, so nothing here loads Unity's types. If any of the four is
    /// missing or has another shape (a game update changed Unity), nothing is edited.
    /// </summary>
    internal static class BundleHookEdit
    {
        public const string HookNamespace = "Vigil.Startup", HookName = "BundleHook";

        private sealed class Target
        {
            public Target(string method, string returns, string hook, params string[] parameters)
            {
                Method = method;
                Returns = returns;
                Hook = hook;
                Parameters = parameters;
            }

            public string Method { get; }

            public string Returns { get; }

            public string Hook { get; }

            public string[] Parameters { get; }
        }

        private static readonly Target[] Targets =
        {
            new Target("LoadFromMemory_Internal", "UnityEngine.AssetBundle", "TryMemory", "System.Byte[]", "System.UInt32"),
            new Target("LoadFromMemoryAsync_Internal", "UnityEngine.AssetBundleCreateRequest", "TryMemoryAsync", "System.Byte[]", "System.UInt32"),
            new Target("LoadFromStreamInternal", "UnityEngine.AssetBundle", "TryStream", "System.IO.Stream", "System.UInt32", "System.UInt32"),
            new Target("LoadFromStreamAsyncInternal", "UnityEngine.AssetBundleCreateRequest", "TryStreamAsync", "System.IO.Stream", "System.UInt32", "System.UInt32"),
        };

        /// <summary>A reference to the hook in Vigil's patcher assembly, added to the module's references.</summary>
        public static TypeReference HookType(ModuleDefinition module, string assemblyName, System.Version version)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            var scope = module.AssemblyReferences.FirstOrDefault(r => r.Name == assemblyName);
            if (scope == null)
            {
                scope = new AssemblyNameReference(assemblyName, version);
                module.AssemblyReferences.Add(scope);
            }
            return new TypeReference(HookNamespace, HookName, module, scope);
        }

        /// <summary>Why the module cannot be edited; null when all four methods are there with the expected shape.</summary>
        public static string FindProblem(ModuleDefinition module) => Find(module, out _);

        /// <summary>Edits the four methods to ask <paramref name="hook"/> first. Null when done; else why nothing was edited.</summary>
        public static string Apply(ModuleDefinition module, TypeReference hook)
        {
            if (hook == null) return "no hook given";
            string problem = Find(module, out var methods);
            if (problem != null) return problem;
            for (int i = 0; i < Targets.Length; i++) Insert(module, methods[i], hook, Targets[i]);
            return null;
        }

        private static string Find(ModuleDefinition module, out List<MethodDefinition> methods)
        {
            methods = new List<MethodDefinition>();
            var type = module?.GetType("UnityEngine", "AssetBundle");
            if (type == null) return "UnityEngine.AssetBundle not found";
            foreach (var t in Targets)
            {
                var m = type.Methods.FirstOrDefault(x => x.Name == t.Method && x.IsStatic && x.HasBody && x.ReturnType.FullName == t.Returns
                    && x.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(t.Parameters));
                if (m == null) return "AssetBundle." + t.Method + "(" + string.Join(", ", t.Parameters) + ") not found with the expected shape";
                methods.Add(m);
            }
            return null;
        }

        private static void Insert(ModuleDefinition module, MethodDefinition method, TypeReference hook, Target target)
        {
            var call = new MethodReference(target.Hook, module.TypeSystem.Boolean, hook) { HasThis = false };
            foreach (var p in method.Parameters) call.Parameters.Add(new ParameterDefinition(p.ParameterType));
            call.Parameters.Add(new ParameterDefinition(new ByReferenceType(method.ReturnType)) { IsOut = true });

            var result = new VariableDefinition(method.ReturnType);
            method.Body.Variables.Add(result);
            var il = method.Body.GetILProcessor();
            var original = method.Body.Instructions[0];
            var code = new List<Instruction>();
            foreach (var p in method.Parameters) code.Add(il.Create(OpCodes.Ldarg, p));
            code.Add(il.Create(OpCodes.Ldloca, result));
            code.Add(il.Create(OpCodes.Call, call));
            code.Add(il.Create(OpCodes.Brfalse, original));
            code.Add(il.Create(OpCodes.Ldloc, result));
            code.Add(il.Create(OpCodes.Ret));
            foreach (var instruction in code) il.InsertBefore(original, instruction);
        }
    }
}
