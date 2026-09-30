using System;
using System.Runtime.InteropServices;

namespace InFalsusChartLoader
{
    /// <summary>
    /// Reading and writing the game's memory, and the checks that have to come first.
    ///
    /// Every accessor here is unchecked by design — they are the primitive the callers validate
    /// around, not a validating layer of their own. What is centralised is the part that is easy to
    /// forget: <see cref="LooksLikeObject"/>, which every pointer that came out of the game has to
    /// pass before anything is read through it.
    /// </summary>
    internal static unsafe class Memory
    {
        internal static int I32(IntPtr p) => *(int*)p;
        internal static long I64(IntPtr p) => *(long*)p;
        internal static ushort U16(IntPtr p) => *(ushort*)p;
        internal static float F32(IntPtr p) => *(float*)p;
        internal static byte U8(IntPtr p) => *(byte*)p;
        internal static IntPtr Ptr(IntPtr p) => *(IntPtr*)p;
        internal static void WriteI32(IntPtr p, int v) => *(int*)p = v;
        internal static void WriteF32(IntPtr p, float v) => *(float*)p = v;
        internal static void WriteI64(IntPtr p, long v) => *(long*)p = v;
        internal static void WritePtr(IntPtr p, IntPtr v) => *(IntPtr*)p = v;
        internal static void WriteU8(IntPtr p, byte v) => *(byte*)p = v;
        internal static void WriteU16(IntPtr p, ushort v) => *(ushort*)p = v;

        /// <summary>
        /// Whether a pointer can be dereferenced as an IL2CPP object.
        ///
        /// The test is the object header: a managed object starts with a class pointer, and a class
        /// pointer is an aligned address in the game's own image. Garbage from a stale field or a
        /// stale index fails this far more often than it passes, which is the point — the failure it
        /// prevents is an access violation, and that is not catchable.
        /// </summary>
        internal static bool LooksLikeObject(IntPtr p)
        {
            if (p == IntPtr.Zero) return false;
            long v = p.ToInt64();
            if ((v & 7) != 0) return false;

            // A real class pointer is inside the game's image, well above the low address range and
            // well below the canonical upper half.
            return v > 0x10000 && v < 0x7FFFFFFFFFFF;
        }

        /// <summary>
        /// The text of an IL2CPP string.
        ///
        /// The length and the character data are read through interop's own accessors rather than
        /// through offsets this mod would have to carry: where a `System.String` keeps its length is
        /// an ABI fact that belongs to Il2CppInterop, which already knows it, and copying it here
        /// would turn one maintained constant into two. That is also why nothing in this mod adds
        /// `StringLength` / `StringChars` to its offsets.
        ///
        /// Returns null for anything that is not a string — a null pointer, or a field that has been
        /// left holding something else.
        /// </summary>
        internal static string Text(IntPtr s, int limit = 256)
        {
            if (!LooksLikeObject(s)) return null;

            try
            {
                int length = Il2CppInterop.Runtime.IL2CPP.il2cpp_string_length(s);
                if (length <= 0 || length > limit) return null;
                return Il2CppInterop.Runtime.IL2CPP.Il2CppStringToManaged(s);
            }
            catch (Exception e)
            {
                Diagnostics.Warn($"string at 0x{s.ToInt64():X} could not be read: {Diagnostics.Describe(e)}");
                return null;
            }
        }

        /// <summary>
        /// An IL2CPP array's total byte length, or 0 when it cannot be had.
        ///
        /// This is what bounds a walk over an array of *structs*: the element stride is measured
        /// (<see cref="FieldResolver.ElementSize"/>) rather than assumed, and a walk bounded by the
        /// allocation cannot run past it even when that measurement fell back to a constant.
        /// </summary>
        internal static long ArrayBytes(IntPtr array)
        {
            if (!LooksLikeObject(array)) return 0;

            try
            {
                return Il2CppInterop.Runtime.IL2CPP.il2cpp_array_get_byte_length(array);
            }
            catch (Exception e)
            {
                Diagnostics.Warn($"an array's byte length could not be read: {Diagnostics.Describe(e)}");
                return 0;
            }
        }

        /// <summary>
        /// A `List&lt;T&gt;`'s backing array and count.
        ///
        /// The list is read through its own layout rather than through an indexer: this mod runs
        /// before anything has had a chance to check its own arguments, and a direct read of `_items`
        /// and `_size` says exactly what is being taken. A list whose fields do not hold an array is
        /// reported as empty rather than followed.
        /// </summary>
        internal static bool TryList(IntPtr list, out IntPtr items, out int count)
        {
            items = IntPtr.Zero;
            count = 0;

            if (!LooksLikeObject(list)) return false;

            IntPtr array = Ptr(list + Offsets.Runtime.ListItems);
            int size = I32(list + Offsets.Runtime.ListSize);
            if (size < 0 || !LooksLikeObject(array)) return false;

            // The array's own length bounds the count: a size past it is a list mid-write, and
            // following it would read past the allocation.
            int length = I32(array + Offsets.Runtime.ArrayLength);
            if (size > length) return false;

            items = array + Offsets.Runtime.ArrayDataOffset;
            count = size;
            return true;
        }
    }
}
