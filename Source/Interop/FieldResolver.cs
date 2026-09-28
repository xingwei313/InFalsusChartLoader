using System;
using System.Collections.Generic;
using System.Reflection;

namespace InFalsusChartLoader
{
    /// <summary>
    /// Where a field sits, asked of the running game rather than copied from a dump.
    ///
    /// The Cpp2IL interop assembly keeps a hidden static field per field holding the runtime
    /// `FieldInfo*`, and `il2cpp_field_get_offset` turns that into the offset. The constants in
    /// <see cref="Offsets"/> are what the mod was written against; these are what the game says, and
    /// a disagreement is reported so that a game update shows up as a line in the log rather than as
    /// a value read from the wrong place.
    ///
    /// Fields only. The IL2CPP container layout this mod also relies on has no name to look up and
    /// is not resolved here.
    /// </summary>
    internal static class FieldResolver
    {
        private const BindingFlags AnyField =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        /// <summary>
        /// The two pointers every IL2CPP object starts with — its class and its monitor. A value
        /// type's field offsets are reported relative to that header, because that is where they sit
        /// once the struct is boxed; the offsets this mod uses are relative to the struct itself.
        /// </summary>
        private const int ObjectHeader = 0x10;

        private static int _agreed;
        private static int _moved;
        private static int _unresolved;

        /// <summary>The line a Release build carries: did anything this mod asked for move.</summary>
        internal static string Stats() =>
            $"offsets agreed={_agreed} moved={_moved} unresolved={_unresolved}";

        /// <summary>
        /// A field's offset, or <paramref name="fallback"/> when the name cannot be resolved.
        ///
        /// A resolved value that disagrees with the fallback is <b>adopted</b>: the game is the
        /// authority on its own layout, and the constant is only what the mod was written against.
        /// The disagreement is logged, because it means the game moved and the mod may be about to
        /// read something it never checked.
        /// </summary>
        internal static int Field(string typeName, string fieldName, int fallback)
        {
            if (!TryField(typeName, fieldName, out int offset))
            {
                _unresolved++;
                Diagnostics.Warn($"{typeName}.{fieldName} could not be resolved; keeping 0x{fallback:X}");
                return fallback;
            }

            if (offset == fallback)
            {
                _agreed++;
                return fallback;
            }

            _moved++;
            Diagnostics.Warn($"{typeName}.{fieldName} is at 0x{offset:X}, not 0x{fallback:X}; using the game's");
            return offset;
        }

        /// <summary>
        /// A field's offset, or -1 when the name cannot be resolved.
        ///
        /// For trying a name that may not exist — a generic type's simple name is generated and not
        /// something to assume — where <see cref="Field"/> would answer with its fallback and hide
        /// the miss.
        /// </summary>
        internal static int Lookup(string typeName, string fieldName) =>
            TryField(typeName, fieldName, out int offset) ? offset : -1;

        /// <summary>
        /// The class pointer for a generated type, so a class can be reached by name rather than
        /// through the address of the slot that happens to hold it.
        /// </summary>
        internal static IntPtr ClassPointer(string typeName)
        {
            if (!InteropTypeIndex.ByName().TryGetValue(typeName, out List<Type> types))
                return IntPtr.Zero;

            foreach (Type type in types)
            {
                try
                {
                    FieldInfo pointer = typeof(Il2CppInterop.Runtime.Il2CppClassPointerStore<>)
                        .MakeGenericType(type)
                        .GetField("NativeClassPtr", BindingFlags.Public | BindingFlags.Static);

                    if (pointer == null) continue;

                    IntPtr klass = (IntPtr)pointer.GetValue(null);
                    if (klass != IntPtr.Zero) return klass;
                }
                catch (Exception e)
                {
                    Diagnostics.Warn($"{typeName} class pointer could not be read: {Diagnostics.Describe(e)}");
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// A class's statics block — where its static fields live, at the offsets
        /// <see cref="Field"/> reports for them.
        /// </summary>
        internal static IntPtr Statics(IntPtr klass)
        {
            if (!Memory.LooksLikeObject(klass)) return IntPtr.Zero;
            return Memory.Ptr(klass + Offsets.Runtime.ClassStatics);
        }

        private static bool TryField(string typeName, string fieldName, out int offset)
        {
            offset = 0;

            try
            {
                if (!InteropTypeIndex.ByName().TryGetValue(typeName, out List<Type> types))
                    return false;

                foreach (Type type in types)
                {
                    FieldInfo pointer = PointerField(type, fieldName);
                    if (pointer == null) continue;

                    IntPtr info = (IntPtr)pointer.GetValue(null);
                    if (info == IntPtr.Zero) continue;

                    offset = type.IsValueType
                        ? (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(info) - ObjectHeader
                        : (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(info);
                    return true;
                }
            }
            catch (Exception e)
            {
                Diagnostics.Warn($"{typeName}.{fieldName} could not be resolved: {Diagnostics.Describe(e)}");
            }

            return false;
        }

        /// <summary>
        /// The generated `NativeFieldInfoPtr_&lt;name&gt;` static, which holds the `FieldInfo*`.
        /// </summary>
        private static FieldInfo PointerField(Type type, string fieldName)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField("NativeFieldInfoPtr_" + fieldName, AnyField | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }

            // A field the generator did not emit a pointer for still has one on some builds under the
            // declaring type's own name; the assembly-wide search above covers the common case.
            return null;
        }
    }
}
