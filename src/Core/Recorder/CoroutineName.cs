namespace Vigil.Core
{
    /// <summary>Readable names for coroutines: the compiler names an iterator "&lt;Method&gt;d__N" inside the class that wrote it.</summary>
    public static class CoroutineName
    {
        /// <summary>"Class.Method" for a compiler-made iterator, otherwise "Class.TypeName" (or just the type name).</summary>
        public static string Describe(string typeName, string declaringTypeName)
        {
            string name = typeName ?? "?";
            int close = name.IndexOf('>');
            if (name.StartsWith("<") && close > 1) name = name.Substring(1, close - 1);
            return string.IsNullOrEmpty(declaringTypeName) ? name : declaringTypeName + "." + name;
        }
    }
}
