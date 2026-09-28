using System;
using System.Reflection;

namespace InFalsusChartLoader
{
    /// <summary>
    /// Debug-only: what the running game actually exposes for the types a jacket is built from.
    ///
    /// <para>
    /// This exists because reflection and the file on disk disagree about these types, and the
    /// answer is worth having in the log rather than in a note. `UnityEngine.CoreModule` is one of
    /// the assemblies the interop layer substitutes. For those, the CLR reports every field and no
    /// method: `Texture2D` in the assembly declares 187 methods, `Type.GetMethods` answers with
    /// none, and `GetField` fails by name on a field that is plainly present — though the same
    /// field is found when the list is walked by hand. Fields survive the substitution; methods do
    /// not, and a name lookup does not.
    /// </para>
    /// <para>
    /// Nothing should be resolved through reflection on a type in this assembly. The game's own
    /// `il2cpp_class_get_methods` sees the methods, and that is what the jacket uses.
    /// </para>
    /// </summary>
    internal static class JacketProbe
    {
        private const BindingFlags AnyField =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance |
            BindingFlags.DeclaredOnly;

        private const string CtorField =
            "NativeMethodInfoPtr__ctor_Public_Void_Int32_Int32_TextureFormat_Boolean_Boolean_0";

        internal static void Report()
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = asm.GetName().Name;
                if (name == null || name.IndexOf("UnityEngine.CoreModule", StringComparison.Ordinal) < 0) continue;

                Diagnostics.Info($"probe: assembly '{name}' location='{Location(asm)}'");
                Diagnostics.Info($"probe:   identity '{asm.FullName}'");

                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types;
                    Diagnostics.Info($"probe:   GetTypes threw, {e.LoaderExceptions?.Length ?? 0} loader exception(s)");
                }
                catch (Exception e)
                {
                    Diagnostics.Info($"probe:   GetTypes threw {e.GetType().Name}: {e.Message}");
                    continue;
                }

                int found = 0;
                foreach (Type t in types)
                {
                    if (t == null || t.Name != "Texture2D") continue;
                    found++;
                    Report(t);
                }
                if (found == 0) Diagnostics.Info("probe:   no type named Texture2D in this assembly");
            }
        }

        private static void Report(Type t)
        {
            FieldInfo[] fields;
            try
            {
                fields = t.GetFields(AnyField);
            }
            catch (Exception e)
            {
                Diagnostics.Info($"probe:   '{t.FullName}' GetFields threw {e.GetType().Name}");
                return;
            }

            Diagnostics.Info($"probe:   type '{t.FullName}' in '{t.Assembly.GetName().Name}' " +
                             $"declares {fields.Length} field(s), assembly-qualified '{t.AssemblyQualifiedName}'");

            int native = 0;
            foreach (FieldInfo f in fields)
            {
                if (f.Name.IndexOf("NativeMethodInfoPtr", StringComparison.Ordinal) < 0) continue;
                if (native < 5) Diagnostics.Info($"probe:     {f.Name}");
                native++;
            }
            Diagnostics.Info($"probe:     ({native} field(s) named NativeMethodInfoPtr*, showing up to 5)");

            // The field Cecil says is there. GetField is asked for it by name, and then the whole
            // list is searched by hand, so a null answer says whether the field is absent or the
            // lookup is.
            FieldInfo byName = null;
            try { byName = t.GetField(CtorField, BindingFlags.Public | BindingFlags.Static); }
            catch (Exception e) { Diagnostics.Info($"probe:     GetField threw {e.GetType().Name}"); }

            FieldInfo byHand = null;
            foreach (FieldInfo f in fields) { if (f.Name == CtorField) { byHand = f; break; } }

            Diagnostics.Info($"probe:     GetField('{CtorField}') -> " +
                             (byName == null ? "null" : "found") +
                             ", searched by hand -> " + (byHand == null ? "null" : "found"));
        }

        private static string Location(Assembly asm)
        {
            try { return asm.Location; }
            catch (Exception) { return "(dynamic)"; }
        }
    }
}
