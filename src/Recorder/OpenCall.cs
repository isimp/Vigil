using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Vigil.Recorder
{
    /// <summary>
    /// A delegate that calls another mod's method, private or not, when that method's own types
    /// cannot be named here: a delegate parameter of type object is cast to the method's type.
    /// For an instance method the delegate's first parameter is the instance.
    /// </summary>
    internal static class OpenCall
    {
        public static T Make<T>(MethodInfo target) where T : Delegate
        {
            var invoke = typeof(T).GetMethod("Invoke");
            var from = invoke.GetParameters().Select(p => p.ParameterType).ToArray();
            var to = target.GetParameters().Select(p => p.ParameterType).ToList();
            if (!target.IsStatic) to.Insert(0, target.DeclaringType);
            if (from.Length != to.Count) throw new ArgumentException(target.Name + " takes " + to.Count + " values, the delegate " + from.Length);
            var dm = new DynamicMethod("Vigil_call_" + target.Name, invoke.ReturnType, from, typeof(OpenCall).Module, true);
            var il = dm.GetILGenerator();
            for (int i = 0; i < from.Length; i++)
            {
                il.Emit(OpCodes.Ldarg, i);
                if (from[i] != to[i])
                {
                    if (to[i].IsValueType) il.Emit(OpCodes.Unbox_Any, to[i]);
                    else il.Emit(OpCodes.Castclass, to[i]);
                }
            }
            il.Emit(target.IsStatic || target.DeclaringType.IsValueType ? OpCodes.Call : OpCodes.Callvirt, target);
            if (target.ReturnType != invoke.ReturnType && invoke.ReturnType == typeof(object) && target.ReturnType.IsValueType) il.Emit(OpCodes.Box, target.ReturnType);
            il.Emit(OpCodes.Ret);
            return (T)dm.CreateDelegate(typeof(T));
        }
    }
}
