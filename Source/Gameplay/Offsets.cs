using System;

namespace InFalsusChartLoader
{
    /// <summary>
    /// Where things are inside the game's objects.
    ///
    /// <para>
    /// Two kinds of constant live here, and they age differently:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Game fields</b> are a property of this build of the game. They move when it is patched,
    /// nothing in the build can tell that they have, and the symptom is a value that reads as
    /// garbage rather than an error. Each one carries the name it had in the metadata so it can be
    /// re-resolved by name at startup — see <c>Resolve</c>.
    /// </description></item>
    /// <item><description>
    /// <b>IL2CPP container layout</b> is a property of the IL2CPP runtime, not of the game. A game
    /// patch does not move it; a Unity or IL2CPP version change does. These are the ones with no
    /// name to look up.
    /// </description></item>
    /// </list>
    /// </summary>
    internal static class Offsets
    {
        /// <summary>Layout of the IL2CPP runtime's own containers. Follows the IL2CPP version.</summary>
        internal static class Runtime
        {
            /// <summary>IL2CPP arrays start their elements here (klass+monitor+bounds+length).</summary>
            internal const int ArrayDataOffset = 0x20;

            /// <summary>An IL2CPP array's element count.</summary>
            internal const int ArrayLength = 0x18;

            /// <summary>List&lt;T&gt;._items.</summary>
            internal const int ListItems = 0x10;

            /// <summary>List&lt;T&gt;._size — the same slot as an array's length.</summary>
            internal const int ListSize = 0x18;

            /// <summary>Dictionary&lt;K,V&gt;._count.</summary>
            internal const int DictCount = 0x20;

            /// <summary>
            /// HashSet&lt;T&gt;._count — the same slot as a dictionary's, for the same reason: both
            /// start with the bucket array and the entry array, so the count lands in the third word
            /// either way. Measured on a live `HashSet&lt;IntPtr&gt;`.
            /// </summary>
            internal const int HashSetCount = 0x20;

            /// <summary>
            /// `Il2CppClass.static_fields`. Where a class's static fields start, which is what
            /// `il2cpp_field_get_offset` reports offsets against.
            /// </summary>
            internal const int ClassStatics = 0xB8;

            /// <summary>
            /// Where a boxed value type's payload starts (klass + monitor). What
            /// `il2cpp_value_box` hands back points at a box, not at the value, and what
            /// `il2cpp_runtime_invoke` hands back for a value-type return is one of these.
            /// </summary>
            internal const int BoxedData = 0x10;
        }

        /// <summary>`_fA`, the note record the decoder produces. 128 bytes each.</summary>
        internal static class Note
        {
            /// <summary>Stride. Measured off a live array at startup rather than trusted.</summary>
            internal static int Size = 0x80;

            /// <summary>`_Ae` — which plane the note is on: 1 main, 2 shift, 3 space, 4 sky.</summary>
            internal const int Side = 0x10;

            /// <summary>`_be` — 1 tap, 2 hold, 4 flick, 5 sky area.</summary>
            internal const int Type = 0x14;

            /// <summary>`_Be` — judgement start, absolute milliseconds.</summary>
            internal const int StartMs = 0x18;

            /// <summary>`_ce` — judgement end, absolute milliseconds. Equal to the start for a tap.</summary>
            internal const int EndMs = 0x1C;
        }
    }
}
