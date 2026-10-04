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
        // No offsets are written in this file: every one is asked by name (`ResolveFields`), and each
        // stays at `Offsets.Unresolved` (-1) until it answers. See `Offsets` for why a game offset
        // is never a number here.

        /// <summary>`PackSelectSceneAssets.packToAssets` — resolved by name, see `ResolveFields`.</summary>
        private static int PackAssetsTable = Offsets.Unresolved;

        /// <summary>`DataAccess._NAb` — resolved with the rest, see `ResolveFields`.</summary>
        private static int PackAssetsField = Offsets.Unresolved;

        /// <summary>`DataAccess._mAb` — the localisation table, resolved with the rest.</summary>
        private static int StringMappingField = Offsets.Unresolved;

        /// <summary>`PackData._SkA()`, which rebuilds the song-to-pack lookup.</summary>
        // (`PackData._SkA` — the lookup rebuild — is called by name; no RVA is kept for it.)

        /// <summary>
        /// The pack visuals, or false while the game has not loaded them — or when a name this file
        /// reads by is not in this build, in which case <see cref="ResolveFields"/> has already said
        /// which one and nothing here may be touched.
        /// </summary>
        internal static bool TryGetVisuals(out IntPtr packAssets)
        {
            packAssets = IntPtr.Zero;

            if (!ResolveFields()) return false;

            IntPtr klass = FieldResolver.ClassPointer("DataAccess");
            if (klass == IntPtr.Zero) return false;

            IntPtr statics = FieldResolver.Statics(klass);
            if (statics == IntPtr.Zero) return false;

            packAssets = Memory.Ptr(statics + PackAssetsField);
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

            // Which row it wears: the In Falsus pack's — as long as that row has something to draw
            // with. `Hooks.SourceRow` owns that question, because the same choice is made on every
            // frame afterwards: this mod's row does not stay as written (something in this build puts
            // a material into it during play), so the row is kept equal to its source rather than
            // written once. See `Hooks.Keep`.
            int from = Hooks.SourceRow(table, count, stride);
            if (from < 0)
            {
                Diagnostics.Warn("no shipped pack row has a material to copy; the custom pack will " +
                                 "wear the game's fallback");
                from = 0;
            }

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
        /// `PackSelectSceneAssets.packToAssets`'s offset, resolved by name — asked for by the row keep,
        /// which runs every frame and must not re-ask the field resolver for it.
        /// </summary>
        internal static int VisualsTableOffset => PackAssetsTable;

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

        /// <summary>`DynamicStringMapping.packIdTypeMapping`.</summary>
        private static int PackIdTypeMapping = Offsets.Unresolved;

        /// <summary>
        /// The three mappings a song's own card text comes from.
        ///
        /// The third is the jacket's illustrator. It is a different mapping from the artist because the
        /// game shows them in different places, and it is keyed by the same `SongId` — so the write is
        /// the same call, against the field the dump lists as `jacketIllustratorNameTypeMapping`.
        /// </summary>
        internal static int SongTitleTypeMapping = Offsets.Unresolved;
        internal static int SongArtistTypeMapping = Offsets.Unresolved;
        internal static int SongIllustratorTypeMapping = Offsets.Unresolved;

        private static bool _fieldsResolved;

        private static bool _fieldsMissing;

        /// <summary>
        /// Every field offset this file uses, asked of the running game by name, once, and whether
        /// all of them answered.
        ///
        /// None of these is a property of the mod — they are properties of *this build of the game*,
        /// and a patch moves them without anything here failing loudly (a stale offset reads a
        /// plausible-looking value from the wrong place). So a name that cannot be resolved is not
        /// papered over with the constant this build was reversed with: it is reported by
        /// <see cref="FieldResolver.Field"/>, this answers false and latches, and every write that
        /// needs one of them is skipped — the pack keeps the game's own visuals and text, which is
        /// visible, where a write through a wrong offset would not be.
        /// </summary>
        private static bool ResolveFields()
        {
            if (_fieldsResolved) return true;
            if (_fieldsMissing) return false;

            PackAssetsTable = FieldResolver.Field("PackSelectSceneAssets", "packToAssets");

            PackIdTypeMapping = FieldResolver.Field("DynamicStringMapping", "packIdTypeMapping");
            SongTitleTypeMapping = FieldResolver.Field("DynamicStringMapping", "songIdTitleTypeMapping");
            SongArtistTypeMapping = FieldResolver.Field("DynamicStringMapping", "songIdArtistTypeMapping");
            SongIllustratorTypeMapping = FieldResolver.Field("DynamicStringMapping",
                                                             "jacketIllustratorNameTypeMapping");

            // The two the pack's own objects carry, asked here rather than at each use: this is the
            // only place either is wanted, and asking inside a loop would report a miss inside a loop.
            PackAssetsField = FieldResolver.Field("DataAccess", "_NAb");
            StringMappingField = FieldResolver.Field("DataAccess", "_mAb");

            if (PackAssetsTable < 0 || PackIdTypeMapping < 0 || SongTitleTypeMapping < 0 ||
                SongArtistTypeMapping < 0 || SongIllustratorTypeMapping < 0 ||
                PackAssetsField < 0 || StringMappingField < 0)
            {
                _fieldsMissing = true;
                Diagnostics.Error("the pack's fields could not be read by name in this build; the custom " +
                                  "pack keeps the game's own visuals and text");
                return false;
            }

            _fieldsResolved = true;
            return true;
        }

        /// <summary>
        /// `DynamicStringMapping.TextMappingValues` — **five string pointers in a row**, one per
        /// language, so every slot is `index × pointer size` and the struct is five of them.
        ///
        /// Written this way rather than as literals because what fixes the layout is the pointer size,
        /// which belongs to the runtime and not to this game: on x64 these come out as
        /// 0x28 / 0x00 / 0x08 / 0x18 / 0x20 — the numbers this file used to carry — and the third slot
        /// is the one language this mod does not write. (The type has no declaration of its own to
        /// look up in the dump, which is why it is not resolved by name like the fields above.)
        /// </summary>
        private static readonly int TextMappingValuesSize = 5 * IntPtr.Size;

        private static readonly int ValueEnglish = 0 * IntPtr.Size;
        private static readonly int ValueJapanese = 1 * IntPtr.Size;
        private static readonly int ValueTraditionalChinese = 3 * IntPtr.Size;
        private static readonly int ValueSimplifiedChinese = 4 * IntPtr.Size;

        /// <summary>
        /// `Mapping`'s offset once asked, `int.MinValue` before, -1 when it could not be asked.
        /// See <see cref="MappingOffset"/>.
        /// </summary>
        private static int _mappingOffset = int.MinValue;

        /// <summary>
        /// Gives the pack a name.
        ///
        /// There is no field for it. A pack's title comes out of the same localisation table every
        /// other string in the game comes from, looked up by the pack's id — `PackInfo` itself has
        /// only an id, a slug and a song list. Without an entry the card reads the table's own
        /// miss value, which is the literal text "Missing String Mapping": there is no fallback to
        /// the slug or to anything else.
        /// </summary>
        /// <summary>
        /// Which write it is, for the one place the distinction is needed: one that could not be made.
        ///
        /// An enum rather than the sentence it replaced, for the reason <see cref="Hooks.Hook"/> gives:
        /// the value travels through <see cref="SetText"/>, which does real work and therefore cannot
        /// be `[Conditional("DEBUG")]`, so a string argument to it is a string in every Release build.
        /// The two sentences now live in <see cref="Name"/>, which that build does not carry.
        /// </summary>
        private enum TextWhat { PackName, SongText }

        internal static bool SetPackName(int packId, string name) =>
            SetText(PackIdTypeMapping, packId, name, TextWhat.PackName);

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
            SetText(mappingField, songId, text, TextWhat.SongText);

        /// <summary>The one write, shared by the pack's name and a song's title and artist.</summary>
        private static bool SetText(int mappingField, int id, string text, TextWhat what)
        {
            // Asked here, before this method reads any of them: this is the funnel every write goes
            // through, and `Inject` reaches it before anything asks for the pack's visuals — so
            // without this line the three song-text writes run before the offsets have been asked
            // for at all. False means a name is missing and has been reported; nothing is written.
            if (!ResolveFields()) return false;

            IntPtr klass = FieldResolver.ClassPointer("DataAccess");
            if (klass == IntPtr.Zero) return false;

            IntPtr statics = FieldResolver.Statics(klass);
            if (statics == IntPtr.Zero) return false;

            IntPtr mapping = Memory.Ptr(statics + StringMappingField);
            if (!Memory.LooksLikeObject(mapping))
            {
                NotWritten(what, "the localisation table is missing");
                return false;
            }

            IntPtr typeMapping = Memory.Ptr(mapping + mappingField);
            if (!Memory.LooksLikeObject(typeMapping))
            {
                NotWritten(what, $"the localisation mapping at +0x{mappingField:X} is missing");
                return false;
            }

            int at = MappingOffset(typeMapping);
            if (at < 0) return false;      // asked and reported where it was asked

            IntPtr table = Memory.Ptr(typeMapping + at);
            if (!Memory.LooksLikeObject(table))
            {
                NotWritten(what, $"the localisation table at +0x{mappingField:X} is missing");
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
        /// Says which write could not be made, and where the lookup stopped, in a Debug build only.
        ///
        /// This one <b>can</b> be `[Conditional("DEBUG")]` — it only reports — and that is what keeps
        /// the message at the call site from shipping: a conditional call takes its arguments with it.
        /// The name still has to come from <see cref="Name"/>, because this body is compiled either
        /// way and a literal in it would ship where the message does not.
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void NotWritten(TextWhat what, string where) =>
            Diagnostics.Warn($"{where}; {Name(what)}");

        /// <summary>The two writes by name. Null in a Release build — see <see cref="TextWhat"/>.</summary>
        private static string Name(TextWhat what)
        {
#if DEBUG
            return what == TextWhat.PackName ? "the custom pack will have no name"
                                             : "the song will have no text of its own";
#else
            return null;
#endif
        }

        /// <summary>
        /// Where `StringTypeMapping&lt;T&gt;.Mapping` sits inside the instance being read — asked of
        /// that instance's <b>own class</b>, which is the inflated generic the game is actually using
        /// and therefore the only place the field has a real offset.
        ///
        /// The dump cannot answer this one: `StringTypeMapping&lt;T&gt;` is generic, so its fields are
        /// rendered at `0x0` there, and every candidate this mod can look up by name is a generic
        /// definition whose fields have no layout. Asking the instance's class is the same route
        /// `CustomResults` takes for the save container, which is generic for the same reason. The
        /// constant this used to fall back to is gone: a name that cannot be asked leaves this at -1
        /// and the write is not made (see <see cref="ResolveFields"/>).
        /// </summary>
        private static int MappingOffset(IntPtr typeMapping)
        {
            if (_mappingOffset != int.MinValue) return _mappingOffset;

            IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(typeMapping);
            if (klass != IntPtr.Zero)
            {
                IntPtr field = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(klass, "Mapping");
                if (field != IntPtr.Zero)
                {
                    int offset = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(field);
                    if (offset > 0)
                    {
                        Diagnostics.Info($"StringTypeMapping.Mapping is at 0x{offset:X}");
                        return _mappingOffset = offset;
                    }
                }
            }

            // The old route, kept as a second chance for a build that declares a non-generic subclass
            // somewhere in the index: a name found there is still a name, and still not a number
            // typed into this file. A generic definition is skipped — its fields have no layout, and
            // reflection refuses to read them (`ContainsGenericParameters`), which is why this walk
            // never answered on the build it was written for.
            foreach (var entry in InteropTypeIndex.ByName())
            {
                if (!entry.Key.Contains("StringTypeMapping")) continue;
                if (entry.Key.IndexOf('`') >= 0) continue;

                int offset = FieldResolver.Lookup(entry.Key, "Mapping");
                if (offset > 0)
                {
                    Diagnostics.Info($"StringTypeMapping.Mapping is at 0x{offset:X} (via {entry.Key})");
                    return _mappingOffset = offset;
                }
            }

            Diagnostics.Error("StringTypeMapping.Mapping could not be found on its own class in this " +
                              "build; the custom pack's texts will not be written");
            return _mappingOffset = -1;
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
