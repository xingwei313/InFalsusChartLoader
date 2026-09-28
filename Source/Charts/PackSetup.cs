using System;

namespace InFalsusChartLoader
{
    /// <summary>
    /// The two things a new pack needs beyond its entry in the pack list.
    ///
    /// <para>
    /// A pack is a row of materials and a reverse lookup. The materials come from a table indexed by
    /// the pack's own id, so a pack whose id is past the end of that table does not fail — it quietly
    /// wears the first pack's clothes. The reverse lookup is what answers "which pack is this song
    /// in", and it is built from the pack list by a method the game calls when its data loads, which
    /// is before this mod has added anything.
    /// </para>
    /// <para>
    /// Both are the same shape of problem as the song list, and take the same kind of answer: make
    /// the game's own table big enough, and make the game's own derivation run again.
    /// </para>
    /// </summary>
    internal static unsafe class PackSetup
    {
        /// <summary>`DataAccess._NAb`, the pack visual table.</summary>
        private const int StaticPackAssets = 0x88;

        /// <summary>`PackSelectSceneAssets.packToAssets`.</summary>
        private const int PackAssetsTable = 0x18;

        /// <summary>
        /// Which row a new pack borrows its look from — the In Falsus pack.
        ///
        /// The table is indexed by a pack's own id, so this is the id as well as the position. It is
        /// one and not zero because zero is the reserved, empty entry at the head of the pack list:
        /// copying that would give a pack the look of the entry the game never draws.
        /// </summary>
        private const int InFalsusPackId = 1;

        /// <summary>`PackData._SkA()`, which rebuilds the song-to-pack lookup.</summary>
        private const long RvaRebuildLookup = 0x495C80;

        /// <summary>The pack visuals, or false while the game has not loaded them.</summary>
        internal static bool TryGetVisuals(out IntPtr packAssets)
        {
            packAssets = IntPtr.Zero;

            IntPtr klass = FieldResolver.ClassPointer("DataAccess");
            if (klass == IntPtr.Zero) return false;

            IntPtr statics = FieldResolver.Statics(klass);
            if (statics == IntPtr.Zero) return false;

            packAssets = Memory.Ptr(statics + FieldResolver.Field("DataAccess", "_NAb", StaticPackAssets));
            return Memory.LooksLikeObject(packAssets);
        }

        /// <summary>
        /// Gives the new pack a row of its own in the visual table, wearing the In Falsus pack's look.
        ///
        /// The `if` file says nothing about how a pack looks, so the row is a copy of a shipped one
        /// rather than an invention. The whole row is copied, not the first material: a
        /// `SongSelectPackAssets` is sixteen material references — the large and small cards, their
        /// hover and completed states, the backing image, the outline — and a row that carries one of
        /// them is a row of nulls, which draws as nothing rather than as the pack it was copied from.
        /// </summary>
        internal static bool ExtendVisuals(IntPtr packAssets, int packIndex)
        {
            IntPtr table = Memory.Ptr(packAssets + PackAssetsTable);
            if (!Memory.LooksLikeObject(table))
            {
                Diagnostics.Warn("the pack visual table is missing; the custom pack will wear the first pack's");
                return false;
            }

            int count = Memory.I32(table + Offsets.Runtime.ArrayLength);
            if (packIndex < count) return true;   // already room; nothing to do

            long bytes = Il2CppInterop.Runtime.IL2CPP.il2cpp_array_get_byte_length(table);
            int stride = count > 0 ? (int)(bytes / count) : 0;
            if (stride <= 0)
            {
                Diagnostics.Warn("the pack visual table has no element size; the custom pack will wear " +
                                 "the first pack's");
                return false;
            }

            IntPtr grown = SongCatalog.GrowArray(table, packIndex + 1);
            if (grown == IntPtr.Zero)
            {
                Diagnostics.Warn("the pack visual table could not be grown; the custom pack will wear " +
                                 "the first pack's");
                return false;
            }

            int from = count > InFalsusPackId ? InFalsusPackId : 0;
            unsafe
            {
                Buffer.MemoryCopy(
                    (void*)(table + Offsets.Runtime.ArrayDataOffset + from * stride),
                    (void*)(grown + Offsets.Runtime.ArrayDataOffset + packIndex * stride),
                    stride, stride);
            }

            Memory.WritePtr(packAssets + PackAssetsTable, grown);
            Diagnostics.Info($"pack visuals extended to {packIndex + 1} rows, " +
                             $"row {packIndex} copying row {from}, {stride} bytes");
            return true;
        }

        /// <summary>
        /// Rebuilds the game's song-to-pack lookup so it knows the new songs belong to the new pack.
        ///
        /// The game derives it from the pack list, and it did that before this mod appended anything.
        /// Its misses are tolerated — an unknown song reads as "in no pack" rather than as an error —
        /// so leaving it stale would not crash, it would just make every reverse lookup about a custom
        /// song answer nothing, which is the kind of wrong that is hard to notice and hard to trace.
        ///
        /// Reached by name because it is private: this is the game's own rebuild, not a second copy of
        /// its rules, and a second copy is exactly what would drift from the first.
        /// </summary>
        internal static void RefreshLookup(IntPtr packData)
        {
            IntPtr klass = FieldResolver.ClassPointer("PackData");
            if (klass == IntPtr.Zero) { Diagnostics.Warn("PackData could not be found; the pack lookup is stale"); return; }

            IntPtr method = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_method_from_name(klass, "_SkA", 0);
            if (method == IntPtr.Zero)
            {
                Diagnostics.Warn("PackData._SkA could not be found; the pack lookup is stale");
                return;
            }

            IntPtr raised = IntPtr.Zero;
            Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(method, packData, null, ref raised);
            if (raised != IntPtr.Zero)
                Diagnostics.Warn($"the game's pack lookup rebuild raised: {Raised.Text(raised)}");

            Diagnostics.Info("pack lookup rebuilt");
        }

        /// <summary>`DataAccess._mAb`, the localisation table.</summary>
        private const int StaticStringMapping = 0x70;

        /// <summary>`DynamicStringMapping.packIdTypeMapping`.</summary>
        private const int PackIdTypeMapping = 0x18;

        /// <summary>
        /// The three mappings a song's own card text comes from.
        ///
        /// The third is the jacket's illustrator. It is a different mapping from the artist because the
        /// game shows them in different places, and it is keyed by the same `SongId` — so the write is
        /// the same call, against the field the dump lists as `jacketIllustratorNameTypeMapping`.
        /// </summary>
        internal const int SongTitleTypeMapping = 0x30;
        internal const int SongArtistTypeMapping = 0x38;
        internal const int SongIllustratorTypeMapping = 0x78;

        /// <summary>`DynamicStringMapping.TextMappingValues` — five strings, one per language.</summary>
        private const int TextMappingValuesSize = 0x28;

        private const int ValueEnglish = 0x00;
        private const int ValueJapanese = 0x08;
        private const int ValueTraditionalChinese = 0x18;
        private const int ValueSimplifiedChinese = 0x20;

        private static int _mappingOffset;

        /// <summary>
        /// Gives the pack a name.
        ///
        /// There is no field for it. A pack's title comes out of the same localisation table every
        /// other string in the game comes from, looked up by the pack's id — `PackInfo` itself has
        /// only an id, a slug and a song list. Without an entry the card reads the table's own
        /// miss value, which is the literal text "Missing String Mapping": there is no fallback to
        /// the slug or to anything else.
        /// </summary>
        internal static bool SetPackName(int packId, string name) =>
            SetText(PackIdTypeMapping, packId, name, "the custom pack will have no name");

        /// <summary>
        /// Gives a song a title or an artist, in the table the song list reads them from.
        ///
        /// The cards do **not** read `SongInfo`'s own override fields — that was this mod's first
        /// guess and it is wrong for the card that is actually drawn. Read from the call site
        /// (`LargeSongCard._OS`): the title is `Strings.GetDynamic&lt;SongId&gt;(1, id)` and the artist
        /// is the same call with `2`, both of them entries in this table. So a song with no entry here
        /// has no title on its card, whichever fields the song record carries.
        /// </summary>
        internal static bool SetSongText(int mappingField, int songId, string text) =>
            SetText(mappingField, songId, text, "the song will have no text of its own");

        /// <summary>The one write, shared by the pack's name and a song's title and artist.</summary>
        private static bool SetText(int mappingField, int id, string text, string what)
        {
            IntPtr klass = FieldResolver.ClassPointer("DataAccess");
            if (klass == IntPtr.Zero) return false;

            IntPtr statics = FieldResolver.Statics(klass);
            if (statics == IntPtr.Zero) return false;

            IntPtr mapping = Memory.Ptr(statics + FieldResolver.Field("DataAccess", "_mAb", StaticStringMapping));
            if (!Memory.LooksLikeObject(mapping))
            {
                Diagnostics.Warn($"the localisation table is missing; {what}");
                return false;
            }

            IntPtr typeMapping = Memory.Ptr(mapping + mappingField);
            if (!Memory.LooksLikeObject(typeMapping))
            {
                Diagnostics.Warn($"the localisation mapping at +0x{mappingField:X} is missing; {what}");
                return false;
            }

            IntPtr table = Memory.Ptr(typeMapping + MappingOffset());
            if (!Memory.LooksLikeObject(table))
            {
                Diagnostics.Warn($"the localisation table at +0x{mappingField:X} is missing; {what}");
                return false;
            }

            if (!Insert(table, id, text)) return false;

            // Named by the table it went into, not by the first caller of this helper: the pack's name
            // and a song's title are written by the same three lines, and a log that calls both of them
            // "pack name" is how a run cannot tell whether a write landed where it was aimed.
            Diagnostics.Info($"localisation +0x{mappingField:X}: entry {id} set to '{text}'");
            return true;
        }

        /// <summary>
        /// Where `StringTypeMapping&lt;T&gt;.Mapping` sits, asked of the game rather than assumed.
        ///
        /// The type is generic and nested, so its generated simple name is not something to guess at:
        /// every type whose name mentions it is tried, and any of them answers the same way because
        /// they share a layout. The constant is the reversed answer, and it has to include the
        /// object header the dump does not print — the dump's offsets for this type are relative to
        /// the field area, so its 0x18 is this 0x28.
        /// </summary>
        private static int MappingOffset()
        {
            if (_mappingOffset != 0) return _mappingOffset;

            foreach (var entry in InteropTypeIndex.ByName())
            {
                if (!entry.Key.Contains("StringTypeMapping")) continue;

                // A generic definition is skipped rather than tried. Its field offsets only mean
                // something for an instantiation, reflection refuses to read a field whose type still
                // has parameters (`ContainsGenericParameters`), and the refusal arrives as an
                // InvalidOperationException per attempt. Every candidate this index has is one of
                // those — a generated name carries a backtick and a parameter count — so the attempt
                // was three warning lines a session for an answer that could only ever be the
                // fallback below.
                if (entry.Key.IndexOf('`') >= 0) continue;

                int offset = FieldResolver.Lookup(entry.Key, "Mapping");
                if (offset > 0)
                {
                    Diagnostics.Info($"StringTypeMapping.Mapping is at 0x{offset:X} (via {entry.Key})");
                    return _mappingOffset = offset;
                }
            }

            Diagnostics.Warn("StringTypeMapping.Mapping could not be resolved by name — every candidate " +
                             "is a generic definition; using the reversed 0x28");
            return _mappingOffset = 0x28;
        }

        /// <summary>
        /// One entry in a `Dictionary&lt;PackId, TextMappingValues&gt;`.
        ///
        /// Going through the runtime is the only way to reach a dictionary the game owns without
        /// reimplementing its hashing. Both arguments are value types, so both are passed as
        /// addresses rather than boxes — see <see cref="JacketFactory.Invoke"/> for why, and for what
        /// boxing one costs: an entry filed under the low bytes of a class pointer instead of its
        /// key. The entry is written with `set_Item`, not `Add`, so that running twice is the same as
        /// running once.
        ///
        /// The same text goes in every language the table can be read in. Korean is left empty on
        /// purpose: the table's own reader never consults it.
        /// </summary>
        private static unsafe bool Insert(IntPtr table, int packId, string name)
        {
            ushort id = (ushort)packId;

            byte[] raw = new byte[TextMappingValuesSize];
            fixed (byte* values = raw)
            {
                IntPtr text = Il2CppInterop.Runtime.IL2CPP.ManagedStringToIl2Cpp(name);
                *(IntPtr*)(values + ValueEnglish) = text;
                *(IntPtr*)(values + ValueJapanese) = text;
                *(IntPtr*)(values + ValueTraditionalChinese) = text;
                *(IntPtr*)(values + ValueSimplifiedChinese) = text;

                IntPtr* args = stackalloc IntPtr[2];
                args[0] = (IntPtr)(&id);
                args[1] = (IntPtr)values;

                IntPtr dictClass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(table);
                IntPtr setItem = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_method_from_name(
                    dictClass, "set_Item", 2);
                if (setItem == IntPtr.Zero)
                {
                    Diagnostics.Warn("the pack name table has no setter; the custom pack will have no name");
                    return false;
                }

                IntPtr raised = IntPtr.Zero;
                Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(setItem, table, (void**)args, ref raised);
                if (raised != IntPtr.Zero)
                {
                    Diagnostics.Warn($"writing the pack name raised: {Raised.Text(raised)}");
                    return false;
                }
            }

            return true;
        }
    }
}
