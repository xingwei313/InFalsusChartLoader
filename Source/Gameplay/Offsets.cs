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
            /// <summary>Stride. Measured off a live array at startup rather than trusted.</summary>
            internal static int Size = 0x80;

            /// <summary>`_Ae` — which plane the note is on: 1 main, 2 shift, 3 space, 4 sky.</summary>
            internal static int Side = 0x10;

            /// <summary>`_be` — 1 tap, 2 hold, 4 flick, 5 sky area.</summary>
            internal static int Type = 0x14;

            /// <summary>`_Be` — judgement start, absolute milliseconds.</summary>
            internal static int StartMs = 0x18;

            /// <summary>`_ce` — judgement end, absolute milliseconds. Equal to the start for a tap.</summary>
            internal static int EndMs = 0x1C;
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // The song tables. Everything below is a property of *this build of the game*, so none of it
        // is a constant: the numbers are only what this build was reversed with, and `Resolve` asks
        // the running game for the real ones as soon as the data objects exist.
        //
        // The two **sizes** have no name to look up at all — a struct's size is not a field — so they
        // are *measured* off the live arrays (`FieldResolver.ElementSize`). Those arrays are the only
        // place a patch that changed a struct's shape can be caught; without them a stale stride
        // reads the middle of one element as the start of the next, which is a pointer that passes
        // every "does this look like an object" test and then faults inside the runtime.
        //
        // A single home for all of it, because three copies of the same offset is how "fixed one,
        // forgot the other two" happens.
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>`SongData` — the class holding every song.</summary>
        internal static class SongData
        {
            /// <summary>`allSongInfo`, `SongInfo[]`. The game's own song table.</summary>
            internal static int AllSongs = 0x20;

            /// <summary>`_Efb`, `Dictionary&lt;string, SongId&gt;` — the name index.</summary>
            internal static int NameIndex = 0x58;

            /// <summary>`_Ffb`, the `BitArray` with one bit per song index.</summary>
            internal static int Availability = 0x68;
        }

        /// <summary>`PackData` — the class holding every pack.</summary>
        internal static class PackData
        {
            /// <summary>`PackInfo[]`; the array order is the order the UI shows them in.</summary>
            internal static int Packs = 0x20;
        }

        /// <summary>`SongInfo`, one song.</summary>
        internal static class Song
        {
            /// <summary>Stride. Measured off `allSongInfo` — a patch that widens the struct lands here.</summary>
            internal static int Size = 0x40;

            /// <summary>`Id`, `SongId` (a ushort). Slot-valid ⟺ `Id == index`; the game's own `_CpA` tests exactly that.</summary>
            internal static int Id = 0x00;

            /// <summary>`BaseName`, the string the game builds `{name}{difficulty}.spc` from.</summary>
            internal static int BaseName = 0x08;

            /// <summary>`ChartInfos`, `SongChartInfo[]` — the four difficulties.</summary>
            internal static int Charts = 0x18;

            /// <summary>`PreviewStartSeconds` / `PreviewEndSeconds`, floats in seconds.</summary>
            internal static int PreviewStart = 0x20;
            internal static int PreviewEnd = 0x24;

            /// <summary>`LocalizationToTitleReadingOverride` / `ArtistReadingOverride` — per-locale readings.</summary>
            internal static int TitleReading = 0x28;
            internal static int ArtistReading = 0x30;

            /// <summary>`RewardStyle`.</summary>
            internal static int RewardStyle = 0x3C;
        }

        /// <summary>`SongChartInfo`, one difficulty of one song.</summary>
        internal static class Chart
        {
            /// <summary>Stride. Measured off a song's own `ChartInfos`.</summary>
            internal static int Size = 0x30;

            internal static int Id = 0x00;              // string — the chart's file name
            internal static int Available = 0x08;       // bool
            internal static int Difficulty = 0x09;      // ChartDifficultyFlag
            internal static int Designer = 0x10;        // string
            internal static int JacketDesigner = 0x18;  // string
            internal static int Rating = 0x20;          // int
            internal static int Section = 0x28;         // string, LevelSectionIndicator
        }

        /// <summary>`PackInfo`, one pack (a row of the pack select screen).</summary>
        internal static class Pack
        {
            /// <summary>Stride. Measured off `PackData.PackInfo`.</summary>
            internal static int Size = 0x18;

            internal static int Id = 0x00;     // PackId (ushort)
            internal static int Slug = 0x08;   // string
            internal static int Songs = 0x10;  // SongId[]
        }

        private static bool _resolved;

        /// <summary>
        /// Asks the running game where its fields are, once, as soon as the data objects exist.
        ///
        /// Called every frame until it can run (see <c>SongCatalog.TryGetAssets</c>); after the first
        /// success it is a single boolean. Every name here is one this mod's own dump shows, and every
        /// answer is adopted — the game is the authority on its own layout. A name that does not
        /// resolve keeps its constant and says so, which is the same shape as any other failure here:
        /// a line in the log, not a value read from the wrong place.
        /// </summary>
        internal static void Resolve(IntPtr songData, IntPtr packData)
        {
            if (_resolved) return;

            SongData.AllSongs = FieldResolver.Field("SongData", "allSongInfo", SongData.AllSongs);
            SongData.NameIndex = FieldResolver.Field("SongData", "_Efb", SongData.NameIndex);
            SongData.Availability = FieldResolver.Field("SongData", "_Ffb", SongData.Availability);
            PackData.Packs = FieldResolver.Field("PackData", "PackInfo", PackData.Packs);

            Song.Id = FieldResolver.Field("SongInfo", "Id", Song.Id);
            Song.BaseName = FieldResolver.Field("SongInfo", "BaseName", Song.BaseName);
            Song.Charts = FieldResolver.Field("SongInfo", "ChartInfos", Song.Charts);
            Song.PreviewStart = FieldResolver.Field("SongInfo", "PreviewStartSeconds", Song.PreviewStart);
            Song.PreviewEnd = FieldResolver.Field("SongInfo", "PreviewEndSeconds", Song.PreviewEnd);
            Song.TitleReading = FieldResolver.Field("SongInfo", "LocalizationToTitleReadingOverride", Song.TitleReading);
            Song.ArtistReading = FieldResolver.Field("SongInfo", "ArtistReadingOverride", Song.ArtistReading);
            Song.RewardStyle = FieldResolver.Field("SongInfo", "RewardStyle", Song.RewardStyle);

            Chart.Id = FieldResolver.Field("SongChartInfo", "Id", Chart.Id);
            Chart.Available = FieldResolver.Field("SongChartInfo", "Available", Chart.Available);
            Chart.Difficulty = FieldResolver.Field("SongChartInfo", "Difficulty", Chart.Difficulty);
            Chart.Designer = FieldResolver.Field("SongChartInfo", "DisplayChartDesigner", Chart.Designer);
            Chart.JacketDesigner = FieldResolver.Field("SongChartInfo", "DisplayJacketDesigner", Chart.JacketDesigner);
            Chart.Rating = FieldResolver.Field("SongChartInfo", "Rating", Chart.Rating);
            Chart.Section = FieldResolver.Field("SongChartInfo", "LevelSectionIndicator", Chart.Section);

            Pack.Id = FieldResolver.Field("PackInfo", "Id", Pack.Id);
            Pack.Slug = FieldResolver.Field("PackInfo", "Slug", Pack.Slug);
            Pack.Songs = FieldResolver.Field("PackInfo", "SongIds", Pack.Songs);

            // The three strides, measured rather than assumed. Each needs the array it is a stride of,
            // so they are taken here — the first moment the game's own data is in hand.
            IntPtr songs = Memory.Ptr(songData + SongData.AllSongs);
            Song.Size = FieldResolver.ElementSize(songs, "SongInfo.Size", Song.Size);

            if (Memory.LooksLikeObject(songs) && Memory.I32(songs + Runtime.ArrayLength) > 0)
            {
                IntPtr first = songs + Runtime.ArrayDataOffset;
                Chart.Size = FieldResolver.ElementSize(Memory.Ptr(first + Song.Charts),
                                                       "SongChartInfo.Size", Chart.Size);
            }

            Pack.Size = FieldResolver.ElementSize(Memory.Ptr(packData + PackData.Packs),
                                                  "PackInfo.Size", Pack.Size);

            _resolved = true;
        }
    }
}
