using System;
using System.Collections.Generic;
using System.Reflection;

namespace InFalsusChartLoader
{
    /// <summary>
    /// The generated interop types, indexed by name.
    ///
    /// Types are looked up by name at runtime rather than with compile-time `typeof()`: the interop
    /// namespaces are generated (`Il2Cpp` + the obfuscated namespace) and a wrong guess would be a
    /// build error instead of a fallback.
    ///
    /// Walking the `Il2Cpp*` assemblies is slow — tens of thousands of generated types — and every
    /// method resolution wants the same walk. Indexing them by name once turns each lookup into a
    /// dictionary hit. The index is built on first use, which is during hook installation.
    ///
    /// A name can hit more than one type. That is not a defect to be fixed here: this game has
    /// types whose names differ only in the case of one letter, and both are real.
    /// </summary>
    internal static class InteropTypeIndex
    {
        private static Dictionary<string, List<Type>> _byName;

        /// <summary>Generated types by simple name. A name can hit more than one type.</summary>
        internal static Dictionary<string, List<Type>> ByName()
        {
            if (_byName != null) return _byName;

            var index = new Dictionary<string, List<Type>>(StringComparer.Ordinal);
            int scanned = 0;

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!IsGenerated(asm)) continue;

                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    // The usual shape: some of the assembly loads and some of it does not.
                    types = e.Types;
                }
                catch (Exception e)
                {
                    // The other shape, and the one that matters: an assembly whose metadata cannot be
                    // enumerated at all. The generated UIElements stubs are like this — their generic
                    // constraints are not satisfiable — and one assembly that cannot be walked must
                    // not take the whole index with it. Skipping it costs the types inside it, which
                    // nothing this mod asks for lives in.
                    Diagnostics.Warn($"{asm.GetName().Name} was skipped: {Diagnostics.Describe(e)}");
                    continue;
                }

                foreach (Type t in types)
                {
                    if (t == null) continue;

                    // One type at a time, because reading a type's own name and its parent's can
                    // throw on a type whose metadata the runtime cannot satisfy. This has been got
                    // wrong twice already: the throw is not confined to the assembly-level call, and
                    // a type nothing can describe is a type nothing can use.
                    try
                    {
                        scanned++;

                        Add(index, t.Name, t);

                        // A nested type's own name does not carry its parent, so the index would only
                        // answer to `_hF` while anybody reading the game's code writes `_J._hF` — and a
                        // lookup that finds nothing does not fail, it falls back to a pinned address.
                        // Indexing both shapes costs a few hundred entries and removes that trap.
                        if (t.DeclaringType != null) Add(index, t.DeclaringType.Name + "." + t.Name, t);
                    }
                    catch (Exception)
                    {
                        // Not indexed, and that is the whole of the consequence.
                    }
                }
            }

            Diagnostics.Info($"indexed {scanned} interop types by name");
            return _byName = index;
        }

        /// <summary>
        /// Whether an assembly is one of the game's own.
        ///
        /// The prefix, and only the prefix. It is narrower than "everything the generator produced",
        /// and that is on purpose: the UnityEngine assemblies keep their real names — which is what
        /// the game's own metadata calls them — and walking them means walking the generated
        /// UIElements stubs, whose generic constraints cannot be satisfied. Enumerating those throws,
        /// and a type whose metadata cannot be read is not a type this mod can use anyway, so the
        /// whole exercise buys nothing and can cost the entire index.
        ///
        /// Unity types are not lost by this: they are reached by assembly and name instead, which is
        /// what the generated code does for them and what one caller of this index needs.
        /// </summary>
        private static bool IsGenerated(Assembly assembly)
        {
            string name = assembly.GetName().Name;
            return name != null && name.StartsWith("Il2Cpp", StringComparison.Ordinal);
        }

        private static void Add(Dictionary<string, List<Type>> index, string key, Type type)
        {
            if (!index.TryGetValue(key, out List<Type> bucket))
                index[key] = bucket = new List<Type>(1);
            bucket.Add(type);
        }
    }
}
