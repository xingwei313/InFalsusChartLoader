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
            /// The two pointers every IL2CPP object starts with — its class and its monitor. A
            /// value type's field offsets are reported relative to them (a boxed struct's fields
            /// start here), which is the correction `FieldResolver` applies.
            /// </summary>
            internal const int ObjectHeader = 0x10;

            /// <summary>
            /// Where a boxed value type's payload starts (klass + monitor). What
            /// `il2cpp_value_box` hands back points at a box, not at the value, and what
            /// `il2cpp_runtime_invoke` hands back for a value-type return is one of these.
            /// </summary>
            internal const int BoxedData = 0x10;

            /// <summary>
            /// `ReadOnlySpan&lt;byte&gt;` / `Span&lt;byte&gt;`: `{ byte* reference; int length }` — 16 bytes,
            /// handed over by address because it is wider than one register. A runtime ABI, like
            /// everything else in this class.
            /// </summary>
            internal const int SpanSize = 0x10;

            /// <summary>A span's length field, right after its reference (`{ T*; int }`).</summary>
            internal const int SpanLength = 8;

            /// <summary>
            /// The note list inside `_R._rC`'s `ValueTuple&lt;_t, List&lt;_fA&gt;, List&lt;_eA&gt;&gt;`:
            /// the tuple's second element, so it sits after the 16-byte `_t`. A tuple has no field
            /// names to ask for and the size of `_t` is not measurable — a container layout, not a
            /// game field, so it lives here with the rest.
            /// </summary>
            internal const int ChartTupleNotes = 0x10;

            /// <summary>
            /// The read record the FMOD servicing thread is handed, and what its info block holds.
            ///
            /// Neither has a name to ask for: both are built by the **native** side — the plugin's
            /// file callbacks — and no game class declares them. They are the only layout left in
            /// this mod that is neither a game field (asked by name) nor a measurement, so they are
            /// named once, here, with the reason.
            /// </summary>
            internal static class ReadRecord
            {
                internal const int Info = 0;          // record[0]: the info block the read works on
                internal const int Handle = 8;        // record[1]: index | length << 24 | kind << 56
                internal const int Size = 40;         // five qwords, copied whole into `_bGA`
                internal const int IndexMask = 0xFFFFFF;

                internal const int Offset = 8;        // info+8: where in the file this read starts
                internal const int Length = 12;       // info+12: how many bytes it asks for
                internal const int Buffer = 32;       // info+32: where those bytes go
                internal const int BytesRead = 40;    // info+40: how many were put there
            }

            /// <summary>
            /// The two fields the C# compiler puts at the head of every generated coroutine class:
            /// `&lt;&gt;1__state` and `&lt;&gt;2__current`.
            ///
            /// These sit with the container layout rather than with the game's own fields because
            /// nothing in the game declares them — the compiler does, in this order, for every
            /// iterator it emits. A patch to the game cannot move them; a new compiler could.
            /// </summary>
            internal static class Coroutine
            {
                internal const int State = 0x10;
                internal const int Current = 0x18;
            }

            /// <summary>
            /// `System.Collections.BitArray.m_array` and `m_length`.
            ///
            /// A BCL layout, so it sits here rather than with the game's fields: taken from the game's
            /// own `BitArray.Get`, which bounds-checks against `+0x18` and fetches the word from
            /// `+0x10`. (Every IL2CPP object starts with eight bytes of class and eight of monitor, so
            /// the first field is at `0x10` — and `0x08` is the monitor, which reads as zero, which is
            /// how a wrong value here fails quietly.)
            /// </summary>
            internal const int BitArrayArray = 0x10;
            internal const int BitArrayLength = 0x18;
        }

        /// <summary>`_fA`, the note record the decoder produces. 128 bytes each.</summary>
        internal static class Note
        {
            // -1 until `Resolve` asks. There is no size here: a stride is a measurement, taken off
            // the live array (`ChartCodec`, which owns that question) — never a number to fall back
            // to. The four below are field offsets, asked by name and kept at -1 when they cannot be.

            /// <summary>`_Ae` — which plane the note is on: 1 main, 2 shift, 3 space, 4 sky.</summary>
            internal static int Side = Unresolved;

            /// <summary>`_be` — 1 tap, 2 hold, 4 flick, 5 sky area.</summary>
            internal static int Type = Unresolved;

            /// <summary>`_Be` — judgement start, absolute milliseconds.</summary>
            internal static int StartMs = Unresolved;

            /// <summary>`_ce` — judgement end, absolute milliseconds. Equal to the start for a tap.</summary>
            internal static int EndMs = Unresolved;
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // The song tables. Everything below is a property of *this build of the game*, so nothing
        // here is a number: every field offset is asked by name (`Resolve`), and every stride is
        // **measured** off the live arrays (`FieldResolver.ElementSize`). A number written here
        // would be a game offset nothing keeps true — not a name, not a measurement.
        //
        // Each one reads `Unresolved` (-1) until it has been asked, which is not an offset at all:
        // a premature read is a read of the object's header, so nothing may read one before
        // `Ready`. The sizes have no name to look up — a struct's size is not a field — and the
        // arrays are the only place a patch that changed a struct's shape can be caught; without
        // them a stale stride reads the middle of one element as the start of the next, which is a
        // pointer that passes every "does this look like an object" test and then faults inside the
        // runtime.
        //
        // A single home for all of it, because three copies of the same offset is how "fixed one,
        // forgot the other two" happens.
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>-1: a field that has not been asked yet is not a field.</summary>
        internal const int Unresolved = -1;

        /// <summary>`SongData` — the class holding every song.</summary>
        internal static class SongData
        {
            /// <summary>`allSongInfo`, `SongInfo[]`. The game's own song table.</summary>
            internal static int AllSongs = Unresolved;

            /// <summary>`_Efb`, `Dictionary&lt;string, SongId&gt;` — the name index.</summary>
            internal static int NameIndex = Unresolved;

            /// <summary>`_Ffb`, the `BitArray` with one bit per song index.</summary>
            internal static int Availability = Unresolved;
        }

        /// <summary>`PackData` — the class holding every pack.</summary>
        internal static class PackData
        {
            /// <summary>`PackInfo[]`; the array order is the order the UI shows them in.</summary>
            internal static int Packs = Unresolved;
        }

        /// <summary>`SongInfo`, one song.</summary>
        internal static class Song
        {
            /// <summary>Stride, measured off `allSongInfo` — a patch that widens the struct lands here.</summary>
            internal static int Size = Unresolved;

            /// <summary>`Id`, `SongId` (a ushort). Slot-valid ⟺ `Id == index`; the game's own `_CpA` tests exactly that.</summary>
            internal static int Id = Unresolved;

            /// <summary>`BaseName`, the string the game builds `{name}{difficulty}.spc` from.</summary>
            internal static int BaseName = Unresolved;

            /// <summary>`ChartInfos`, `SongChartInfo[]` — the four difficulties.</summary>
            internal static int Charts = Unresolved;

            /// <summary>`PreviewStartSeconds` / `PreviewEndSeconds`, floats in seconds.</summary>
            internal static int PreviewStart = Unresolved;
            internal static int PreviewEnd = Unresolved;

            /// <summary>`LocalizationToTitleReadingOverride` / `ArtistReadingOverride` — per-locale readings.</summary>
            internal static int TitleReading = Unresolved;
            internal static int ArtistReading = Unresolved;

            /// <summary>`RewardStyle`.</summary>
            internal static int RewardStyle = Unresolved;
        }

        /// <summary>`SongChartInfo`, one difficulty of one song.</summary>
        internal static class Chart
        {
            /// <summary>Stride, measured off a song's own `ChartInfos`.</summary>
            internal static int Size = Unresolved;

            internal static int Id = Unresolved;              // string — the chart's file name
            internal static int Available = Unresolved;       // bool
            internal static int Difficulty = Unresolved;      // ChartDifficultyFlag
            internal static int Designer = Unresolved;        // string
            internal static int JacketDesigner = Unresolved;  // string
            internal static int Rating = Unresolved;          // int
            internal static int Section = Unresolved;         // string, LevelSectionIndicator
        }

        /// <summary>`PackInfo`, one pack (a row of the pack select screen).</summary>
        internal static class Pack
        {
            /// <summary>Stride, measured off `PackData.PackInfo`.</summary>
            internal static int Size = Unresolved;

            internal static int Id = Unresolved;     // PackId (ushort)
            internal static int Slug = Unresolved;   // string
            internal static int Songs = Unresolved;  // SongId[]
        }

        private static bool _resolved;

        private static bool _failed;

        /// <summary>
        /// Whether every offset here was asked of the running game and answered.
        ///
        /// False until then, and false for the rest of the run after a name has come back missing.
        /// A caller must not read any of these numbers unless this is true: an unresolved one is
        /// -1, and -1 is not an offset — it is the object's own header.
        /// </summary>
        internal static bool Ready => _resolved;

        /// <summary>
        /// Asks the running game where its fields are, once, as soon as the data objects exist, and
        /// answers whether every name resolved.
        ///
        /// Called every frame until it can run (see <c>SongCatalog.TryGetAssets</c>); after the first
        /// answer it is a single boolean. Every name here is one this mod's own dump shows, and every
        /// answer is adopted — the game is the authority on its own layout.
        ///
        /// <para>
        /// A name that does not resolve is reported by <see cref="FieldResolver.Field"/> and leaves
        /// its offset at -1; this then answers false, <b>latches</b>, and says what it costs: the
        /// tables cannot be walked, so nothing that walks them runs. There is deliberately no retry
        /// (a name does not appear at frame 900) and no fallback to the number this build was
        /// reversed with (that number is exactly what a moved field makes wrong).
        /// </para>
        /// </summary>
        internal static bool Resolve(IntPtr songData, IntPtr packData)
        {
            if (_failed) return false;
            if (_resolved) return true;

            // Every name is asked even after one has failed — the `&=` does not short-circuit — so
            // that one run says all of what is missing rather than only the first of it.
            bool ok = true;

            ok &= (SongData.AllSongs = FieldResolver.Field("SongData", "allSongInfo")) >= 0;
            ok &= (SongData.NameIndex = FieldResolver.Field("SongData", "_Efb")) >= 0;
            ok &= (SongData.Availability = FieldResolver.Field("SongData", "_Ffb")) >= 0;
            ok &= (PackData.Packs = FieldResolver.Field("PackData", "PackInfo")) >= 0;

            ok &= (Song.Id = FieldResolver.Field("SongInfo", "Id")) >= 0;
            ok &= (Song.BaseName = FieldResolver.Field("SongInfo", "BaseName")) >= 0;
            ok &= (Song.Charts = FieldResolver.Field("SongInfo", "ChartInfos")) >= 0;
            ok &= (Song.PreviewStart = FieldResolver.Field("SongInfo", "PreviewStartSeconds")) >= 0;
            ok &= (Song.PreviewEnd = FieldResolver.Field("SongInfo", "PreviewEndSeconds")) >= 0;
            ok &= (Song.TitleReading = FieldResolver.Field("SongInfo", "LocalizationToTitleReadingOverride")) >= 0;
            ok &= (Song.ArtistReading = FieldResolver.Field("SongInfo", "ArtistReadingOverride")) >= 0;
            ok &= (Song.RewardStyle = FieldResolver.Field("SongInfo", "RewardStyle")) >= 0;

            ok &= (Chart.Id = FieldResolver.Field("SongChartInfo", "Id")) >= 0;
            ok &= (Chart.Available = FieldResolver.Field("SongChartInfo", "Available")) >= 0;
            ok &= (Chart.Difficulty = FieldResolver.Field("SongChartInfo", "Difficulty")) >= 0;
            ok &= (Chart.Designer = FieldResolver.Field("SongChartInfo", "DisplayChartDesigner")) >= 0;
            ok &= (Chart.JacketDesigner = FieldResolver.Field("SongChartInfo", "DisplayJacketDesigner")) >= 0;
            ok &= (Chart.Rating = FieldResolver.Field("SongChartInfo", "Rating")) >= 0;
            ok &= (Chart.Section = FieldResolver.Field("SongChartInfo", "LevelSectionIndicator")) >= 0;

            ok &= (Pack.Id = FieldResolver.Field("PackInfo", "Id")) >= 0;
            ok &= (Pack.Slug = FieldResolver.Field("PackInfo", "Slug")) >= 0;
            ok &= (Pack.Songs = FieldResolver.Field("PackInfo", "SongIds")) >= 0;

            // The three strides, measured rather than assumed — and a stride that cannot be measured
            // is a failure like any other, because a stride is what every walk over these arrays
            // uses. Skipped entirely when a name has already failed: the reads below would be aimed
            // with an offset that is -1.
            if (ok)
            {
                IntPtr songs = Memory.Ptr(songData + SongData.AllSongs);

                Song.Size = FieldResolver.ElementSize(songs, "SongInfo.Size");
                ok &= Song.Size > 0;

                Chart.Size = FieldResolver.ElementSize(ChartTemplate(songs), "SongChartInfo.Size");
                ok &= Chart.Size > 0;

                Pack.Size = FieldResolver.ElementSize(Memory.Ptr(packData + PackData.Packs), "PackInfo.Size");
                ok &= Pack.Size > 0;
            }

            if (!ok)
            {
                _failed = true;
                Diagnostics.Error("the game's song and pack tables could not be read by name in this build; " +
                                  "no custom song will be registered");
                return false;
            }

            _resolved = true;
            return true;
        }

        /// <summary>
        /// The first song element whose `ChartInfos` is a live array — the one the chart stride is
        /// measured off, and not necessarily element zero: the table has holes by design (a hole
        /// carries id zero and no chart array), and a measurement asked of a hole measures nothing.
        /// The walk is bounded by the array's own byte length, like every other walk here.
        /// </summary>
        private static IntPtr ChartTemplate(IntPtr songs)
        {
            if (!Memory.LooksLikeObject(songs)) return IntPtr.Zero;

            int count = Memory.I32(songs + Runtime.ArrayLength);
            long bytes = Memory.ArrayBytes(songs);
            int stride = Song.Size;

            for (int i = 0; i < count && stride > 0 && (long)(i + 1) * stride <= bytes; i++)
            {
                IntPtr charts = Memory.Ptr(songs + Runtime.ArrayDataOffset + i * stride + Song.Charts);
                if (Memory.LooksLikeObject(charts) && Memory.I32(charts + Runtime.ArrayLength) > 0)
                    return charts;
            }

            return IntPtr.Zero;
        }
    }
}
