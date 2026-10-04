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
        /// The number lives in `Offsets.Runtime` with the rest of the runtime's layout.
        /// </summary>
        private const int ObjectHeader = Offsets.Runtime.ObjectHeader;

        private static int _asked;
        private static int _unresolved;

        /// <summary>The line a Release build carries: how many names this mod reads by were answered.</summary>
        internal static string Stats() =>
            $"offsets asked={_asked} unresolved={_unresolved}";

        /// <summary>
        /// A field's offset, asked of the running game by name, or <b>-1</b> when the name cannot be
        /// answered.
        ///
        /// <para>
        /// -1 is not a value: every field sits at or after the object's header, so a caller that
        /// fails to check reads the header — a plausible-looking number from the wrong place, which
        /// is exactly how a moved field used to fail.
        /// </para>
        /// <para>
        /// There is <b>no number in this signature</b>, and that is deliberate. It used to take the
        /// constant this build was reversed with and return it when the name lookup failed — not a
        /// safety net: the failure of the lookup is the only signal that the game has renamed
        /// something, and the stale number destroyed it, so the run carried on and the symptom
        /// surfaced somewhere else. A number in the source is also a game offset that nothing keeps
        /// true: it cannot be resolved (it is not a name) and it is not measured, so it is exactly
        /// the thing a mod should not contain. A miss is reported here, by name, in every build, and
        /// every caller must treat it as "this chain is off" the way a hook that does not resolve is
        /// simply not installed.
        /// </para>
        /// </summary>
        internal static int Field(string typeName, string fieldName)
        {
            if (!TryField(typeName, fieldName, out int offset))
            {
                _unresolved++;
                Diagnostics.Error($"{typeName}.{fieldName}: no field by that name in this build");
                return -1;
            }

            _asked++;
            return offset;
        }

        /// <summary>
        /// A field's offset, or -1 when the name cannot be resolved.
        ///
        /// For trying a name that may not exist — a generic type's simple name is generated and not
        /// something to assume — where the caller is asking a question and can take "no" for an
        /// answer, rather than the "this chain is off" that <see cref="Field"/> reports.
        /// </summary>
        internal static int Lookup(string typeName, string fieldName) =>
            TryField(typeName, fieldName, out int offset) ? offset : -1;

        // ---------------------------------------------------------------- sizes

        private static readonly Dictionary<string, int> Measured = new Dictionary<string, int>();

        /// <summary>
        /// The size of an IL2CPP array's elements, taken from the array itself, or <b>-1</b> when it
        /// cannot be measured — the same "no answer" as <see cref="Field"/>, and for the same
        /// reason: a stride that was not measured is a stride nothing may walk by.
        ///
        /// A struct's size is not a field, so it has no name to look up — but an array of them
        /// carries it: its byte length is its element count times the element size. This is the
        /// one number a game update that changes a struct's shape cannot hide, which is why no
        /// stride in this mod is trusted to a constant — and why this takes no expected value
        /// either: a measurement is the only thing that may answer here.
        ///
        /// Only a measurement is remembered; a miss is not cached here because the array it was
        /// asked about may not be the one a later caller asks about — the callers that cannot go on
        /// without an answer latch their own failure instead.
        /// </summary>
        internal static int ElementSize(IntPtr array, string label)
        {
            if (Measured.TryGetValue(label, out int known)) return known;

            string why = null;
            try
            {
                if (Memory.LooksLikeObject(array))
                {
                    uint length = Il2CppInterop.Runtime.IL2CPP.il2cpp_array_length(array);
                    uint bytes = length == 0
                        ? 0
                        : Il2CppInterop.Runtime.IL2CPP.il2cpp_array_get_byte_length(array);

                    if (length > 0 && length <= 1_000_000 && bytes > 0 && bytes % length == 0)
                    {
                        int size = (int)(bytes / length);
                        Measured[label] = size;
                        _asked++;
                        return size;
                    }

                    why = $"the array holds {length} element(s) in {bytes} byte(s)";
                }
                else
                {
                    why = "there is no array to measure";
                }
            }
            catch (Exception e)
            {
                why = Diagnostics.Describe(e);
            }

            _unresolved++;
            Diagnostics.Error($"{label} could not be measured off the live arrays in this build: {why}");
            return -1;
        }

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

                    // Whether to correct for the object header is asked of **IL2CPP**, not of the
                    // generated `System.Type`: Cpp2IL emits the game's structs as classes, so
                    // `Type.IsValueType` is false for exactly the types that need the correction — and
                    // the failure is silent and uniform (every field of every struct comes back
                    // 0x10 too high), which reads like "the game moved everything".
                    offset = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(info) -
                             (IsValueType(ClassOf(type)) ? ObjectHeader : 0);
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
        /// The IL2CPP class of a generated `System.Type`, or zero.
        /// </summary>
        private static IntPtr ClassOf(Type type)
        {
            try
            {
                FieldInfo pointer = typeof(Il2CppInterop.Runtime.Il2CppClassPointerStore<>)
                    .MakeGenericType(type)
                    .GetField("NativeClassPtr", BindingFlags.Public | BindingFlags.Static);

                return pointer == null ? IntPtr.Zero : (IntPtr)pointer.GetValue(null);
            }
            catch (Exception)
            {
                return IntPtr.Zero;      // a generic definition has no class; nothing to correct for
            }
        }

        /// <summary>
        /// Whether an IL2CPP class is a value type — a struct's field offsets are reported against the
        /// object header, because that is where they sit once the struct is boxed, while every offset
        /// this mod uses is relative to the struct itself.
        ///
        /// Asked the way IL2CPP defines it rather than through the generated type: a value type's
        /// parent is <c>System.ValueType</c>.
        /// </summary>
        private static bool IsValueType(IntPtr klass)
        {
            IntPtr parent = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_parent(klass);
            if (parent == IntPtr.Zero) return false;

            return Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name_(parent) == "ValueType";
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
