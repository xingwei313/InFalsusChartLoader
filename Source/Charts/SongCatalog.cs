using System;
using System.Collections.Generic;

namespace InFalsusChartLoader
{
    /// <summary>
    /// The custom songs, put into the game's own song and pack tables.
    ///
    /// <para>
    /// Nothing here is a hook. Both tables are plain arrays of plain structs and the game reads them
    /// when it builds the song list, so a custom song is a custom entry: append to
    /// <c>SongData.allSongInfo</c> and prepend a pack to <c>PackData.PackInfo</c>. From there the
    /// song-select, the pack filter, the difficulty selector and the chart loader all see a song that
    /// looks exactly like a shipped one.
    /// </para>
    /// <para>
    /// The pack goes first, which is the whole of "above In Falsus": the pack list is the array in
    /// order, and the game's own first pack is at index one afterwards.
    /// </para>
    /// <para>
    /// A new entry starts as a copy of an existing one, not as zeroed memory. Almost every field of
    /// a <c>SongInfo</c> is an enum, a flag or a tuning value this mod has no opinion about, and a
    /// copy of a real song gives all of them values the game already understands; only the handful
    /// this mod actually knows — the id, the name, the charts — are written over.
    /// </para>
    /// </summary>
    internal static unsafe class SongCatalog
    {
        // ---- DataAccess: the two asset fields, both static. ----
        private const int StaticSongData = 0x48;
        private const int StaticPackData = 0x60;

        // ---- SongData ----
        private const int SongDataAllSongs = 0x20;

        /// <summary>`SongData._Ffb`, a `BitArray` with one bit per song index.</summary>
        private const int SongDataAvailability = 0x68;

        /// <summary>
        /// `BitArray.m_array` and `m_length` — the backing ints and the bit count.
        ///
        /// Taken from the game's own `BitArray.Get` rather than from the type's layout on paper: it
        /// bounds-checks against `+0x18` and fetches the word from `+0x10`. Every IL2CPP object
        /// starts with eight bytes of class and eight of monitor, so the first field is at `0x10`;
        /// `0x08` is the monitor and reads as zero, which is how a wrong value here fails quietly.
        /// </summary>
        private const int BitArrayArray = 0x10;
        private const int BitArrayLength = 0x18;

        /// <summary>
        /// Grows the game's per-song `BitArray` to cover <paramref name="count"/> songs, with the new
        /// bits set.
        ///
        /// It is indexed by song position, so appending songs runs off its end — the game reads index
        /// 83 of an 83-bit array and throws `ArgumentOutOfRangeException` from inside its own index
        /// rebuild, which then leaves every index it was building empty. The game never grows it for
        /// songs it did not ship, so this does.
        ///
        /// Length is the part that matters. The rebuild that follows clears every bit and re-derives
        /// them from the song names -- a bit is set for the songs the song list leaves out -- so the
        /// values written here are only what the array holds in between. What the rebuild cannot do
        /// for itself is read index 83 of an 83-bit array, and that read is what it throws on.
        /// </summary>
        private static void GrowAvailability(IntPtr songData, int count)
        {
            IntPtr bits = Memory.Ptr(songData + SongDataAvailability);
            if (!Memory.LooksLikeObject(bits))
            {
                Diagnostics.Warn("SongData has no availability bits; the new songs may not be selectable");
                return;
            }

            int oldLength = Memory.I32(bits + BitArrayLength);
            if (oldLength >= count) return;

            int oldWords = (oldLength + 31) / 32;
            int newWords = (count + 31) / 32;

            IntPtr words = Memory.Ptr(bits + BitArrayArray);
            if (!Memory.LooksLikeObject(words))
            {
                Diagnostics.Warn($"the availability bits have no backing array (0x{words.ToInt64():x}); " +
                                 "the appending songs will not be selectable");
                return;
            }

            // The backing int array is replaced, not resized: IL2CPP arrays cannot grow, and a
            // larger one is what the game's own constructor would have made.
            IntPtr grown = NewArray(words, newWords);
            if (grown == IntPtr.Zero)
            {
                Diagnostics.Warn($"no room for {newWords} availability word(s); the new songs will not be selectable");
                return;
            }

            long bytes = (long)Memory.I32(words + Offsets.Runtime.ArrayLength) * sizeof(int);
            Buffer.MemoryCopy((void*)(words + Offsets.Runtime.ArrayDataOffset),
                              (void*)(grown + Offsets.Runtime.ArrayDataOffset), bytes, bytes);

            // Every new bit set, so the appended songs read as available.
            for (int i = 0; i < newWords; i++)
            {
                int mask = i < oldWords ? Memory.I32(words + Offsets.Runtime.ArrayDataOffset + i * sizeof(int))
                                        : 0;
                for (int b = 0; b < 32; b++)
                {
                    int index = i * 32 + b;
                    if (index < oldLength || index >= count) continue;
                    mask |= 1 << b;
                }
                Memory.WriteI32(grown + Offsets.Runtime.ArrayDataOffset + i * sizeof(int), mask);
            }

            Memory.WritePtr(bits + BitArrayArray, grown);
            Memory.WriteI32(bits + BitArrayLength, count);
            Diagnostics.Info($"availability bits grown from {oldLength} to {count}");
        }
        private const int SongDataNameIndex = 0x58;   // Dictionary<string, SongId> _Efb
        private const long RvaSongDataRefresh = 0x4C54F0;  // private void _yOA()

        /// <summary>
        /// `tutorial` is the one prefix the game's own rebuild leaves out of `_ffb`.
        ///
        /// Kept as a fact rather than as code: a diagnostic switch that named every imported song
        /// into that prefix lived here, and it was removed once the run it was for was read (its
        /// conclusion had already been withdrawn — the run it was read from had stopped before the
        /// step it was testing). Nothing reads this constant; it is here so the next reader of
        /// <see cref="Refresh"/> does not have to re-derive what the rebuild filters on.
        /// </summary>
        private const string FilteredPrefix = "tutorial";

        /// <summary>How many locales the title-reading array is indexed by. `Localization` goes to 5.</summary>
        private const int LocaleCount = 6;

        // ---- PackData ----
        private const int PackDataPacks = 0x20;

        // ---- SongInfo, 0x40 bytes ----
        private const int SongInfoSize = 0x40;
        private const int SongInfoId = 0x00;          // SongId (ushort)
        private const int SongInfoBaseName = 0x08;    // string
        private const int SongInfoCharts = 0x18;      // SongChartInfo[]
        private const int SongInfoPreviewStart = 0x20;  // float, seconds
        private const int SongInfoPreviewEnd = 0x24;    // float, seconds
        private const int SongInfoTitleReading = 0x28;  // string[]
        private const int SongInfoArtistReading = 0x30; // string
        private const int SongInfoRewardStyle = 0x3C;   // RewardStyle

        /// <summary>
        /// `RewardStyle.Supress` — do not play the unlock reveal.
        ///
        /// The field says whether a song plays a banner and a jacket-reveal animation when it is
        /// handed out by a recipe or a reward. A custom song is never handed out that way, so
        /// nothing reads this; it is written so that it is not the copied template's answer, which
        /// for the wrong song would be `Banner`.
        /// </summary>
        private const int RewardStyleSupress = 0;

        // ---- SongChartInfo, 0x30 bytes ----
        private const int ChartInfoSize = 0x30;
        private const int ChartInfoId = 0x00;         // string
        private const int ChartInfoAvailable = 0x08;  // bool
        private const int ChartInfoDifficulty = 0x09; // ChartDifficultyFlag
        private const int ChartInfoDesigner = 0x10;   // string
        private const int ChartInfoJacketDesigner = 0x18; // string
        private const int ChartInfoRating = 0x20;     // int
        private const int ChartInfoSection = 0x28;    // string, LevelSectionIndicator

        /// <summary>
        /// Where the song list's level grouping changes over.
        ///
        /// `LevelSectionIndicator` is the heading a song sits under when the list is sorted by level.
        /// Across all 281 shipped charts it takes exactly two values, and the boundary is here:
        /// everything up to 9 is `"1"`, everything from 10 up is `"2"`. The string is not decoration
        /// the game invents — it is shown as written — so it is copied from the game's own data
        /// rather than composed, and there is nothing else in that data to copy.
        /// </summary>
        private const int SectionBoundary = 9;

        // ---- PackInfo, 0x18 bytes ----
        private const int PackInfoSize = 0x18;
        private const int PackInfoId = 0x00;          // PackId (ushort)
        private const int PackInfoSlug = 0x08;        // string
        private const int PackInfoSongs = 0x10;       // SongId[]

        /// <summary>
        /// This mod's pack id, and its index into the pack visual table.
        ///
        /// `PackId.Value` is an index into <c>PackSelectSceneAssets.packToAssets</c>, and a value
        /// past the end does not fail — the game quietly falls back to the first entry's materials.
        /// So the pack takes the slot after the shipped ones, and that table gains an entry to match.
        /// </summary>
        internal const int CustomPackId = 7;

        /// <summary>Where in the pack array this mod's pack is inserted. See <see cref="AddPack"/>.</summary>
        private const int CustomPackIndex = 1;

        /// <summary>The pack's name in the pack list. The game shows this text.</summary>
        internal const string PackName = "IFCL";

        /// <summary>
        /// The difficulty flags, in the order <c>chart[0..3]</c> is written: Minimal, Evolved,
        /// Ultimate, Forbidden.
        /// </summary>
        private static readonly byte[] DifficultyFlags = { 1, 2, 4, 8 };

        /// <summary>
        /// The two asset objects, or false while the game has not finished loading them.
        ///
        /// They are static fields on <c>DataAccess</c> and are filled by an Addressables load during
        /// startup, so "not yet" is the ordinary state for the first frames and not an error.
        /// </summary>
        internal static bool TryGetAssets(out IntPtr songData, out IntPtr packData)
        {
            songData = IntPtr.Zero;
            packData = IntPtr.Zero;

            IntPtr klass = FieldResolver.ClassPointer("DataAccess");
            if (klass == IntPtr.Zero) return false;

            IntPtr statics = FieldResolver.Statics(klass);
            if (statics == IntPtr.Zero) return false;

            songData = Memory.Ptr(statics + FieldResolver.Field("DataAccess", "_JAb", StaticSongData));
            packData = Memory.Ptr(statics + FieldResolver.Field("DataAccess", "_lAb", StaticPackData));

            return Memory.LooksLikeObject(songData) && Memory.LooksLikeObject(packData);
        }

        /// <summary>
        /// Settles every song's name and refuses the ones whose name the game already has, returning
        /// the ones that may carry on.
        ///
        /// <para>
        /// Called before the audio is registered, and that ordering is the point of it being a step
        /// of its own: a slot in the asset manager cannot be given back, so a song that is going to
        /// be refused must be refused while it still has none.
        /// </para>
        /// <para>
        /// <b>A name already in use is a refusal, not a rename.</b> The `id` is what the game files
        /// this song under and builds `{name}{difficulty}.spc` from, so a second song under a name
        /// the game already has would have to be answered under something its author did not write.
        /// This used to be renamed instead — `{id}_{folder}`, then `_2`, and so on — which kept the
        /// song and lost the one thing the author needed to hear. The rest of the folders load
        /// without it: a taken name costs one song, the same as an `if` file that breaks a rule;
        /// what it must not cost is the whole import, which is what the renaming's last resort did.
        /// </para>
        /// <para>
        /// The names are checked against the live song table plus this run's own, which is where
        /// they have to be checked at all: the game keys its own indexes by this string and raises on
        /// a second entry with it, partway through the rebuild, which leaves every index it was
        /// building empty.
        /// </para>
        /// </summary>
        internal static List<ChartInfo> SettleNames(List<ChartInfo> charts, IntPtr songData)
        {
            var admitted = new List<ChartInfo>(charts.Count);

            IntPtr songs = Memory.Ptr(songData + SongDataAllSongs);
            int count = Memory.LooksLikeObject(songs) ? Memory.I32(songs + Offsets.Runtime.ArrayLength) : 0;

            var taken = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                IntPtr name = Memory.Ptr(songs + Offsets.Runtime.ArrayDataOffset + i * SongInfoSize + SongInfoBaseName);
                string text = Memory.Text(name);
                if (text != null) taken.Add(text);
            }

            foreach (ChartInfo chart in charts)
            {
                if (taken.Contains(chart.BaseName))
                {
                    Diagnostics.WarnRelease($"'{chart.BaseName}' is already the name of a song this game " +
                                            $"has, so '{chart.FolderName}' was not loaded. Change the id " +
                                            "in its if file.");
                    continue;
                }

                taken.Add(chart.BaseName);
                admitted.Add(chart);

                // Registered under this name, which is the name the game will ask with: it builds
                // `{name}{difficulty}.spc` for a chart and `{name}.wav` for the audio.
                ChartCatalog.Adopt(chart);
            }

            return admitted;
        }

        /// <summary>
        /// Adds every chart folder to the song and pack tables. Returns how many went in.
        ///
        /// The two arrays are extended first and the pack last: a song with no pack would be
        /// unreachable, and a pack naming a song that is not there would be worse than not appearing.
        /// </summary>
        internal static int Inject(IntPtr songData, IntPtr packData, List<ChartInfo> charts)
        {
            if (charts.Count == 0) return 0;

            IntPtr oldSongs = Memory.Ptr(songData + SongDataAllSongs);
            if (!Memory.LooksLikeObject(oldSongs)) { Diagnostics.Warn("SongData has no song array"); return 0; }

            int oldCount = Memory.I32(oldSongs + Offsets.Runtime.ArrayLength);
            if (oldCount <= 0) { Diagnostics.Warn("SongData has no songs to take a template from"); return 0; }

            int newCount = oldCount + charts.Count;
            IntPtr songs = GrowArray(oldSongs, newCount);
            if (songs == IntPtr.Zero) return 0;

            // Every chart here has already had its name settled, and the ones whose name the game
            // already has were refused — see SettleNames, which runs before the audio is registered
            // so that a song that will not be loaded never takes a slot in the asset manager.

            IntPtr chartTemplate = FirstCharts(oldSongs, oldCount);
            if (chartTemplate == IntPtr.Zero) return 0;

            IntPtr songIds = AllocateSongIds(packData, charts.Count);
            if (songIds == IntPtr.Zero) return 0;

            // The shipped songs carry a title-reading array, and its shape (one entry per locale) is
            // what a new song's has to have. Taken from a real one rather than guessed at.
            IntPtr titleTemplate = Memory.Ptr(oldSongs + Offsets.Runtime.ArrayDataOffset + SongInfoTitleReading);

            for (int i = 0; i < charts.Count; i++)
            {
                // A SongId is not a name, it is an index: `SongData._BpA` is `&allSongInfo[Value]`,
                // with no lookup in between. So the id of an appended song is the slot it landed in,
                // and any other numbering would have every song resolve to a different song.
                int id = oldCount + i;
                IntPtr slot = songs + Offsets.Runtime.ArrayDataOffset + (oldCount + i) * SongInfoSize;

                // Start from a real song so every field this mod does not set keeps a value the game
                // already accepts, then write over the ones it does.
                Buffer.MemoryCopy((void*)(oldSongs + Offsets.Runtime.ArrayDataOffset), (void*)slot,
                                  SongInfoSize, SongInfoSize);

                // The display name and artist, which the song list reads straight off the song.
                //
                // These two fields are the whole of how a new song gets a title: the list's
                // constructor prefers them over the localisation table, so writing them costs a
                // string copy where the table would cost a dictionary insert per locale. They also
                // have to be *written* rather than left from the template -- inherited, a custom
                // song would show the shipped song's title as its own.
                //
                // One trap from the same constructor: it ignores an override shorter than two
                // characters. A one-character title therefore falls through to the localisation
                // table, which has no entry for it. That is a pathological name, not an error.
                Memory.WritePtr(slot + SongInfoTitleReading, BuildReadings(titleTemplate, charts[i].Name));
                Memory.WritePtr(slot + SongInfoArtistReading, Str(charts[i].Composer));

                Memory.WriteU16(slot + SongInfoId, (ushort)id);
                Memory.WritePtr(slot + SongInfoBaseName, Str(charts[i].BaseName));

                // The preview window, always the `if` file's own. Written rather than left as the
                // template's, which would preview a window belonging to the song this was copied
                // from -- and the field is required precisely so that there is nothing to guess at.
                Memory.WriteF32(slot + SongInfoPreviewStart, charts[i].PreviewStart);
                Memory.WriteF32(slot + SongInfoPreviewEnd, charts[i].PreviewEnd);

                // Whether handing the song out plays the reveal. Nothing reads it for a custom song,
                // but the template's answer belongs to another song.
                Memory.WriteI32(slot + SongInfoRewardStyle, RewardStyleSupress);

                // The per-difficulty records, registered with the jacket catalogue as they go: the
                // loading screen asks for a jacket by `SongChartInfo` rather than by song, and an
                // element's address is the only thing that tells the two apart.
                IntPtr chartInfos = BuildCharts(chartTemplate, charts[i]);
                Memory.WritePtr(slot + SongInfoCharts, chartInfos);
                JacketCatalog.RegisterCharts(chartInfos, charts[i]);

                WriteSongId(songIds, i, (ushort)id);
            }

            Memory.WritePtr(songData + SongDataAllSongs, songs);

            // The song list is filtered on this table, so the entries have to be there before the game
            // next builds that list — which is any time from now on, not only at the rebuild below.
            // The ids are the slots the songs landed in, which is why this runs after the loop.
            var ids = new List<int>(charts.Count);
            for (int i = 0; i < charts.Count; i++) ids.Add(oldCount + i);
            RewardEntries.Add(ids);

            // The song's title and artist as its card shows them: the localisation table, keyed by song
            // id — the same table the pack's name goes in, and the same write. See SetSongText.
            for (int i = 0; i < charts.Count; i++)
            {
                int id = oldCount + i;
                PackSetup.SetSongText(PackSetup.SongTitleTypeMapping, id, charts[i].Name);
                PackSetup.SetSongText(PackSetup.SongArtistTypeMapping, id, charts[i].Composer);
                PackSetup.SetSongText(PackSetup.SongIllustratorTypeMapping, id, charts[i].Illust);
            }

            // Before the rebuild, because that rebuild indexes these bits by song position.
            GrowAvailability(songData, newCount);

            Refresh(songData);
            AddPack(packData, songIds, charts.Count);
            return charts.Count;
        }

        /// <summary>
        /// Makes the game re-derive the lists it builds from <c>allSongInfo</c>.
        ///
        /// <para>
        /// This is not optional and it is not a plain call. <c>_yOA</c> rebuilds the name index, the
        /// tutorial bit array and the filtered song list — but it treats the name index as a
        /// one-shot guard: while that index is non-empty it returns early, and the game's own songs
        /// filled it at load. So a song appended to <c>allSongInfo</c> would never reach the
        /// filtered list, and the filtered list is the one song select reads. Nothing would appear.
        /// </para>
        /// <para>
        /// Clearing the index first is what makes the whole block run again. It is private, so it is
        /// reached by name through the runtime rather than by offset.
        /// </para>
        /// </summary>
        private static void Refresh(IntPtr songData)
        {
            Memory.WritePtr(songData + SongDataNameIndex, IntPtr.Zero);

            IntPtr klass = FieldResolver.ClassPointer("SongData");
            if (klass == IntPtr.Zero) { Diagnostics.Error("SongData could not be found; the new songs will not show"); return; }

            IntPtr method = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_method_from_name(klass, "_yOA", 0);
            if (method == IntPtr.Zero)
            {
                Diagnostics.Error("SongData._yOA could not be found; the new songs will not show");
                return;
            }

            IntPtr raised = IntPtr.Zero;
            Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(method, songData, null, ref raised);

            // A managed exception inside the game's own refresh would otherwise be invisible here:
            // the runtime hands it back rather than throwing.
            if (raised != IntPtr.Zero)
                Diagnostics.Error($"the game's song list refresh raised: {Raised.Text(raised)}");

            Diagnostics.Info("song list rebuilt");
        }

        /// <summary>
        /// A title-reading array — one entry per locale.
        ///
        /// The same text for every language: the `if` file carries one name, and inventing readings
        /// for languages the author did not write would be worse than repeating the one they did.
        /// The array's shape comes from a shipped song so its length matches what the game indexes
        /// with, rather than a length this mod assumes.
        /// </summary>
        private static IntPtr BuildReadings(IntPtr template, string text)
        {
            if (!Memory.LooksLikeObject(template)) return IntPtr.Zero;

            int locales = Memory.I32(template + Offsets.Runtime.ArrayLength);
            if (locales < LocaleCount) locales = LocaleCount;

            IntPtr array = NewArray(template, locales);
            if (array == IntPtr.Zero) return IntPtr.Zero;

            IntPtr value = Str(text);
            for (int i = 0; i < locales; i++)
                Memory.WritePtr(array + Offsets.Runtime.ArrayDataOffset + i * IntPtr.Size, value);

            return array;
        }

        /// <summary>
        /// The per-difficulty records for one song, built from a copy of a shipped song's first one.
        /// </summary>
        private static IntPtr BuildCharts(IntPtr template, ChartInfo info)
        {
            IntPtr array = NewArray(template, ChartInfo.Difficulties);
            if (array == IntPtr.Zero) return IntPtr.Zero;

            IntPtr source = template + Offsets.Runtime.ArrayDataOffset;

            for (int d = 0; d < ChartInfo.Difficulties; d++)
            {
                IntPtr slot = array + Offsets.Runtime.ArrayDataOffset + d * ChartInfoSize;
                Buffer.MemoryCopy((void*)source, (void*)slot, ChartInfoSize, ChartInfoSize);

                // The `if` file's own file name for this difficulty. The game carries this string
                // into the chart load, so it has to be what the folder actually holds.
                Memory.WritePtr(slot + ChartInfoId, Str(Path.GetFileName(info.ChartPaths[d])));
                Memory.WriteU8(slot + ChartInfoAvailable, 1);
                Memory.WriteU8(slot + ChartInfoDifficulty, DifficultyFlags[d]);
                Memory.WritePtr(slot + ChartInfoDesigner, Str(info.Charter[d]));

                // `illust` is the jacket artist. Written for every difficulty, matching how the
                // shipped charts carry the same name in all four -- though what they carry there is
                // not a name: it is a localisation key (`jacketDesigner1`..`4`), and no code in the
                // build reads this field at all. The card's illustrator is read from the localisation
                // table instead, which SongCatalog's caller writes. So this is a faithful copy of the
                // record's shape and nothing more; it is not what puts a name on screen.
                Memory.WritePtr(slot + ChartInfoJacketDesigner, Str(info.Illust));
                Memory.WriteI32(slot + ChartInfoRating, info.Level[d]);

                // The heading this difficulty sorts under. See SectionBoundary.
                Memory.WritePtr(slot + ChartInfoSection,
                                Str(info.Level[d] <= SectionBoundary ? "1" : "2"));
            }

            return array;
        }

        /// <summary>
        /// Puts this mod's pack at the front of the pack list, naming every custom song.
        ///
        /// The array is rebuilt rather than appended to: the pack list's order is what the player
        /// sees, and "above the game's first pack" is that order.
        /// </summary>
        private static bool AddPack(IntPtr packData, IntPtr songIds, int songCount)
        {
            IntPtr oldPacks = Memory.Ptr(packData + PackDataPacks);
            if (!Memory.LooksLikeObject(oldPacks)) { Diagnostics.Warn("PackData has no pack array"); return false; }

            int oldCount = Memory.I32(oldPacks + Offsets.Runtime.ArrayLength);
            if (oldCount <= CustomPackIndex)
            {
                Diagnostics.Warn($"the pack list has {oldCount} entries; there is no room before the first pack");
                return false;
            }

            IntPtr packs = NewArray(oldPacks, oldCount + 1);
            if (packs == IntPtr.Zero) return false;

            IntPtr source = oldPacks + Offsets.Runtime.ArrayDataOffset;
            IntPtr data = packs + Offsets.Runtime.ArrayDataOffset;

            // The custom pack goes at index one, not zero: element zero is a reserved, empty pack the
            // game skips, and the first pack it actually shows -- "In Falsus" -- is at index one.
            // Landing in front of that is the whole of "above In Falsus".
            long head = (long)CustomPackIndex * PackInfoSize;
            Buffer.MemoryCopy((void*)source, (void*)data, head, head);

            IntPtr mine = (IntPtr)(data.ToInt64() + head);
            Memory.WriteU16(mine + PackInfoId, CustomPackId);
            Memory.WritePtr(mine + PackInfoSlug, Str("custom"));
            Memory.WritePtr(mine + PackInfoSongs, songIds);

            long rest = (long)(oldCount - CustomPackIndex) * PackInfoSize;
            Buffer.MemoryCopy((void*)(source.ToInt64() + head), (void*)(mine.ToInt64() + PackInfoSize), rest, rest);

            Memory.WritePtr(packData + PackDataPacks, packs);
            Diagnostics.Info($"pack '{PackName}' added with {songCount} songs at index {CustomPackIndex}, " +
                             $"before the game's {oldCount - CustomPackIndex} shown packs");
            return true;
        }

        /// <summary>A `SongId[]` holding the ids this mod handed out.</summary>
        private static IntPtr AllocateSongIds(IntPtr packData, int count)
        {
            IntPtr packs = Memory.Ptr(packData + PackDataPacks);
            int packCount = Memory.I32(packs + Offsets.Runtime.ArrayLength);
            if (packCount <= 0) { Diagnostics.Warn("PackData has no packs to take a template from"); return IntPtr.Zero; }

            IntPtr firstPack = packs + Offsets.Runtime.ArrayDataOffset;
            IntPtr template = Memory.Ptr(firstPack + PackInfoSongs);
            if (!Memory.LooksLikeObject(template)) { Diagnostics.Warn("a shipped pack has no song ids"); return IntPtr.Zero; }

            return NewArray(template, count);
        }

        /// <summary>
        /// The first song's `ChartInfos` **array object**, used as the shape for new per-difficulty
        /// records. The caller adds the data offset to reach the elements.
        ///
        /// It returned `array + dataOffset` once, which reads as the first element and is not an
        /// object at all. `NewArray` takes a class from the pointer it is given, so it took one from
        /// a string field of the first element — a heap address, which passes every alignment and
        /// range check and is not a class. The array call then walked that as metadata and the
        /// process died, a long way from the line that was wrong.
        /// </summary>
        private static IntPtr FirstCharts(IntPtr songs, int count)
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr charts = Memory.Ptr(songs + Offsets.Runtime.ArrayDataOffset + i * SongInfoSize + SongInfoCharts);
                if (Memory.LooksLikeObject(charts) &&
                    Memory.I32(charts + Offsets.Runtime.ArrayLength) >= ChartInfo.Difficulties)
                    return charts;
            }

            Diagnostics.Warn("no shipped song has four difficulty records to take a shape from");
            return IntPtr.Zero;
        }

        /// <summary>
        /// An array of <paramref name="count"/> elements of the same element type as
        /// <paramref name="like"/>, holding a copy of everything <paramref name="like"/> had and
        /// zeros in the new slots.
        ///
        /// New slots are left zeroed, which is right for an array of values and wrong for one of
        /// references the game dereferences without a null check — so a caller that needs the new
        /// slots filled fills them itself, with what it needs and as many bytes as the element really
        /// is. That is deliberate: an earlier version offered to copy element zero in, and copied
        /// eight bytes of it, which for a 128-byte struct wrote one pointer into a row of sixteen.
        /// </summary>
        internal static IntPtr GrowArray(IntPtr like, int count)
        {
            int oldCount = Memory.I32(like + Offsets.Runtime.ArrayLength);
            IntPtr grown = NewArray(like, count);
            if (grown == IntPtr.Zero) return IntPtr.Zero;

            int stride = ElementStride(like, oldCount);
            if (stride <= 0) return IntPtr.Zero;

            long bytes = (long)oldCount * stride;
            if (bytes > 0)
            {
                Buffer.MemoryCopy((void*)(like + Offsets.Runtime.ArrayDataOffset),
                                  (void*)(grown + Offsets.Runtime.ArrayDataOffset), bytes, bytes);
            }

            return grown;
        }

        /// <summary>An array of the same element type as <paramref name="like"/>, empty.</summary>
        internal static IntPtr NewArray(IntPtr like, int count)
        {
            IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(like);
            IntPtr element = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_element_class(klass);
            if (element == IntPtr.Zero) { Diagnostics.Warn("an array has no element class"); return IntPtr.Zero; }

            IntPtr array = Il2CppInterop.Runtime.IL2CPP.il2cpp_array_new(element, (ulong)count);
            if (array == IntPtr.Zero) Diagnostics.Warn($"could not allocate an array of {count}");
            return array;
        }

        private static int ElementStride(IntPtr array, int count)
        {
            if (count <= 0) return 0;
            long bytes = Il2CppInterop.Runtime.IL2CPP.il2cpp_array_get_byte_length(array);
            return (int)(bytes / count);
        }

        private static void WriteSongId(IntPtr array, int index, ushort value) =>
            Memory.WriteU16(array + Offsets.Runtime.ArrayDataOffset + index * 2, value);

        /// <summary>
        /// A managed string as an IL2CPP one.
        ///
        /// Created and stored immediately: IL2CPP's collector is conservative and scans this thread's
        /// stack, so a pointer that only ever lives in a local is safe, and one parked in a managed
        /// field would not be. Nothing here keeps one.
        /// </summary>
        private static IntPtr Str(string text) =>
            Il2CppInterop.Runtime.IL2CPP.ManagedStringToIl2Cpp(text);
    }
}
