using System;
using System.IO;
using System.Runtime.InteropServices;

namespace InFalsusChartLoader
{
    /// <summary>
    /// The custom songs' results, kept in a save file of their own: `IFCL.sav`, beside the game's
    /// own `savestate_V3.sav`.
    ///
    /// <para>
    /// The game keeps every result in one object — `SingleFileSaveDataV2.GameResults`, a
    /// `GameResultsV4` holding a per-song-and-difficulty table of bests, a per-play history and the
    /// mapping between them — reads it from one file at startup, and writes the whole file back
    /// whenever anything changes. A custom song's score would therefore live in the game's own
    /// save: it would outlive the mod, and a player who removes the mod would keep rows about songs
    /// that no longer exist.
    /// </para>
    /// <para>
    /// This class gives the custom songs a table of their own. The game's table is never touched:
    /// the two readers of a song's record (`GameResultsV4.TryGetResult`, both overloads) and its
    /// only writer (`TryUpdate`, whose one caller is the results screen's commit) are answered from
    /// this table for songs this mod imported — see the detours in `CustomResultsHook`. Everything
    /// else about a play still goes through the game: the merge rules, the rank, the summary, the
    /// rewards, and the write of the game's own file, which keeps carrying the particles and the
    /// rest exactly as it did.
    /// </para>
    /// <para>
    /// The file is written with the game's own machinery rather than a copy of it. An instance of
    /// the game's own save container (`_NH._OH&lt;SingleFileSaveDataV2&gt;`, the class the
    /// `savestate` file lives in) is built with this file's paths, and the container's own save
    /// method serializes and writes it: the same MemoryPack bytes, the same temporary-file-and-
    /// replace dance, no encryption. Read back, it is a save file the game itself could load.
    /// </para>
    /// <para>
    /// Loading is this mod's own, because the container's factory builds its file name from a name
    /// and a version (`{name}_V{version}.sav`) and this file is called `IFCL.sav`. The bytes are
    /// deserialized through the game's own `MemoryPackSerializer`, so the payload's version ladder
    /// and its encoding are the game's, not a second implementation's.
    /// </para>
    /// <para>
    /// Whether a save landed is decided by the file, not by the call. This build's own save routine
    /// raises <b>after</b> it has already written the file — measured in both runs it was watched,
    /// with the file's timestamp moving about four tenths of a second before the exception arrived.
    /// The cause turned out to be the container's write cache, which the constructor this mod calls
    /// leaves empty and the game's own factory seeds; <see cref="SeedWrittenCache"/> seeds it here,
    /// which removes both the raise and the retry loop that came with it. Judging a save by the file
    /// stays, because a stamp is evidence that cannot be argued with — it is the fallback now rather
    /// than the rule, and a raise that still lands is counted apart so the behaviour stays visible
    /// rather than being swallowed.
    /// </para>
    /// <para>
    /// The objects here — the container, the save it holds, and the result table inside that — are
    /// this mod's own and nothing in the game refers to them, so each is kept alive with a handle
    /// of its own (<see cref="Root"/>). A raw address is not a reference: the game's collector
    /// cannot see one, and an unrooted object is one it is free to take — which it did, once, a
    /// couple of minutes into a session, and the next read crashed inside the game.
    /// </para>
    /// </summary>
    internal static unsafe class CustomResults
    {
        /// <summary>
        /// `_NH._DEb`, the container holding the game's own save — a field of a game class like the
        /// four below, so it is asked by name before its first use (`FieldResolver.Field`, the same
        /// call `_NH._dEb` is read with) and it stays at `Offsets.Unresolved` until it answers. A
        /// name that does not resolve is reported and stops the feature — see <see cref="Prepare"/>.
        /// </summary>
        private static int SaveContainer = Offsets.Unresolved;

        // ---- Fields of the two classes reached into. They are properties of *this build of the
        // game*, so the name is what is used and there is no number to write: `Resolve` asks the
        // running game (see `FieldOn`), and each stays at `Offsets.Unresolved` until it answers.
        private static int FieldFullPath = Offsets.Unresolved;      // `_NH._OH<T>._JEb` — the container's file
        private static int FieldGameResults = Offsets.Unresolved;   // `SingleFileSaveDataV2.GameResults`
        private static int FieldWrittenBytes = Offsets.Unresolved;  // `_NH._OH<T>._LEb` — bytes last written
        private static int FieldWrittenLength = Offsets.Unresolved; // `_NH._OH<T>._mEb` — and how many

        private const string FileName = "IFCL.sav";
        private const string TempFileName = "IFCL_temp.sav";

        /// <summary>The names the container calls its files by. Not paths: the paths are built here.</summary>
        private const string ShortName = "IFCL";
        private const string ShortTempName = "IFCL_temp";

        private static bool _resolved;

        /// <summary>The custom songs' results, a `GameResultsV4` of this mod's own.</summary>
        internal static IntPtr Table;

        /// <summary>Our save container — the object that owns `IFCL.sav`. Zero until prepared.</summary>
        internal static IntPtr Container;

        /// <summary>Where the file lives. For the log and the probes.</summary>
        internal static string FilePath;

        // Read by the probes: every record read, the ones answered from this table, every merge,
        // the merges that landed here, and the writes of the file that succeeded.
        internal static long Reads, Served, Updates, Merged, Saves;

        /// <summary>
        /// Writes that landed while the game's own save still raised. Counted apart from
        /// <see cref="Saves"/>'s total only for visibility — see <see cref="Save"/>. Zero is the
        /// healthy answer now that the container's write cache is seeded
        /// (<see cref="SeedWrittenCache"/>); a raise that still lands is worth seeing rather than
        /// swallowing, which is the whole reason this is counted rather than folded in.
        /// </summary>
        internal static long Late;

        // The encounter ("回想") results the settlement tried to write, and the ones dropped because
        // the play was one of this mod's. See `CustomResultsHook.UpdateEncounterDetour`.
        internal static long EncounterWrites, EncounterDropped;

        private static bool _writeFailed;

        // One gc handle per object this class keeps for the session — see Root. Held for the whole
        // session on purpose: the objects are wanted until the process ends, and a handle is a few
        // bytes, so there is nothing to be gained by ever freeing one.
        private static IntPtr _containerHandle;
        private static IntPtr _objectHandle;
        private static IntPtr _tableHandle;

        /// <summary>Whether the custom table exists and can be answered from.</summary>
        internal static bool Ready => Table != IntPtr.Zero;

        /// <summary>
        /// Whether a song record is one of this mod's. `SongId` is the record's first field and the
        /// slot the song was filed under, and the ids in <see cref="SongCatalog.CustomSongIds"/>
        /// are exactly the slots this run's import took, so the test is one read.
        /// </summary>
        internal static bool IsOurs(IntPtr songInfo)
        {
            if (songInfo == IntPtr.Zero) return false;

            // The id's offset is asked (`Offsets.Song.Id`), not assumed to be zero: a build that
            // puts anything ahead of `Id` moves it. Gated as well, because an unresolved offset is
            // -1 — one byte before the record — and this runs from the results hooks.
            if (!Offsets.Ready) return false;
            return SongCatalog.CustomSongIds.Contains(Memory.U16(songInfo + Offsets.Song.Id));
        }

        /// <summary>
        /// Opens `IFCL.sav` and puts a table in this class. Called once, at registration — the
        /// first moment both the game's save (for the folder) and this mod's songs (for the ids)
        /// exist. Any failure leaves <see cref="Ready"/> false and a line in the log, and the
        /// custom songs then keep their results in the game's own save, which is what happened
        /// before this class existed.
        /// </summary>
        internal static void Prepare()
        {
            if (Ready) return;

            try
            {
                IntPtr save = RewardEntries.PlayerSave();
                int containerField = FieldResolver.Field("_NH", "_DEb");
                if (containerField < 0) { PrepareFailed("no field"); return; }
                SaveContainer = containerField;
                IntPtr official = save == IntPtr.Zero ? IntPtr.Zero : Memory.Ptr(save + SaveContainer);
                if (!Memory.LooksLikeObject(official))
                {
                    Diagnostics.Error("the game's save could not be reached; custom scores will go into " +
                                      "the game's own save file");
                    return;
                }

                IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(official);
                if (!Resolve(klass)) { PrepareFailed("no field"); return; }

                // The folder comes off the game's own file, so the two files are beside each other
                // by construction and no path is guessed.
                string officialPath = Memory.Text(Memory.Ptr(official + FieldFullPath), 512);
                string folder = officialPath == null ? null : Path.GetDirectoryName(officialPath);
                if (string.IsNullOrEmpty(folder))
                {
                    Diagnostics.Error("the save folder could not be read; custom scores will go into " +
                                      "the game's own save file");
                    return;
                }

                string path = Path.Combine(folder, FileName);
                string tempPath = Path.Combine(folder, TempFileName);

                bool exists = File.Exists(path);
                byte[] known = null;
                IntPtr obj = exists ? Load(path, out known) : IntPtr.Zero;
                if (exists && obj == IntPtr.Zero)
                {
                    // A file that is there but could not be read is left alone: starting from an
                    // empty table would overwrite the scores it holds on the next save.
                    Diagnostics.Error("the custom results file exists but could not be read, so it was " +
                                      "left alone; custom scores will go into the game's own save file");
                    return;
                }

                if (obj == IntPtr.Zero)
                {
                    obj = CreateDefault();
                    if (obj == IntPtr.Zero) { PrepareFailed("no save object"); return; }
                }

                IntPtr table = Memory.Ptr(obj + FieldGameResults);
                if (!Memory.LooksLikeObject(table))
                {
                    table = NewResults();
                    if (table == IntPtr.Zero) { PrepareFailed("no table"); return; }
                    Memory.WritePtr(obj + FieldGameResults, table);
                }

                IntPtr container = NewContainer(klass, obj, path, tempPath, known);
                if (container == IntPtr.Zero) { PrepareFailed("no container"); return; }

                // All three are this mod's own objects and nothing in the game points at any of
                // them, so the only thing standing between them and the game's collector is a
                // handle of their own. See Root. (The container holds the save and the save holds
                // the table, so one handle would do; all three are rooted so that a build where
                // one of those fields sits somewhere unexpected still cannot lose a pointer.)
                Root(ref _containerHandle, container);
                Root(ref _objectHandle, obj);
                Root(ref _tableHandle, table);

                // And prove the table answers, once, before any screen asks it to. A table that
                // cannot would otherwise fail later as an exception out of the game's own code,
                // which is a crash rather than a line in the log.
                string failed = SelfCheck(table);
                if (failed != null) { PrepareFailed(failed); return; }

                Container = container;
                Table = table;
                FilePath = path;

                // "prepared", not "created": the file itself appears at the first save — nothing is
                // written until a custom song's result is merged into the table. Saying "created"
                // sent the last run looking for a file that was not there yet.
                Diagnostics.Load("custom results: " + (exists ? "opened " : "prepared ") + path);
            }
            catch (Exception e)
            {
                PrepareFailed(Diagnostics.Describe(e));
            }
        }

        /// <summary>
        /// Says the custom table could not be prepared, with the step that stopped it. Shipping —
        /// it is the line that explains why the custom songs' records are back in the game's own
        /// save — and the reasons are kept short because every word of this message ships.
        /// </summary>
        private static void PrepareFailed(string why) =>
            Diagnostics.Error("the custom results could not be prepared; custom scores will go into " +
                              "the game's own save file: " + why);

        /// <summary>
        /// Keeps one of this mod's objects alive for the session.
        ///
        /// The objects this class builds exist in the game's own heap but are referenced by nobody
        /// in the game — the only thing that knows about them is a field here, and a field here is
        /// a managed one, which the game's collector does not scan. Left alone, they are free to be
        /// collected and their memory reused, and every pointer in this class then points at
        /// whatever took their place. That is not a theory: it happened, and the read that came
        /// after the reuse threw out of the game's own result table.
        ///
        /// A handle is the runtime's own way of saying "this is a root", so the read-back is
        /// checked: a handle that does not answer with the object it was made from is reported
        /// rather than trusted.
        /// </summary>
        private static void Root(ref IntPtr handle, IntPtr obj)
        {
            if (handle != IntPtr.Zero || obj == IntPtr.Zero) return;

            try
            {
                handle = Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_new(obj, false);
                IntPtr back = Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_get_target(handle);
                if (back != obj)
                    Diagnostics.Warn($"a gc handle answered with 0x{back.ToInt64():X} for an object at " +
                                     $"0x{obj.ToInt64():X}; the custom results are not safe this session");
            }
            catch (Exception e)
            {
                Diagnostics.Warn("an object could not be rooted: " + Diagnostics.Describe(e));
            }
        }

        /// <summary>
        /// One record read against the custom table, before it is published: the table's highscore
        /// dictionary, answered through the game's own reader, with a key of this mod's own making.
        /// Null when the read came back the way it should — the reader returned, which for a key
        /// that is in no table means "no record" — and the reason when it did not.
        ///
        /// The song record it passes is a stack-built one shaped the way the reader reads it: the
        /// id at `+0`, the base name at `+8`, and nothing else touched. The base name is a real
        /// string because the key's own hash runs over it.
        /// </summary>
        private static string SelfCheck(IntPtr table)
        {
            IntPtr klass = FieldResolver.ClassPointer("GameResultsV4");
            IntPtr method = klass == IntPtr.Zero ? IntPtr.Zero : MethodOn(klass, "TryGetResult", 3);
            if (method == IntPtr.Zero) return "no self-test (the record reader could not be found)";

            try
            {
                // The record's own shape, from the one resolved set (`Offsets`, filled by
                // `Resolve` when the game's tables first appeared): the size is measured and the
                // two offsets are asked by name, so nothing here is a number of this file's.
                byte* song = stackalloc byte[Offsets.Song.Size];
                for (int i = 0; i < Offsets.Song.Size; i++) song[i] = 0;
                *(IntPtr*)(song + Offsets.Song.BaseName) = Str("IFCL self test");

                byte difficulty = 1;

                // The record the reader would write on a hit, at the size the game's own writer
                // asserts for it (96 — SAVE_RE §4.2). The key used below is in no table, so this
                // call is a miss and the buffer is a guard rather than a working area — but a
                // struct size has no name to look up and nothing here to measure it off, so it is
                // flagged rather than passed off as resolved (`CHARTLOADER_HANDOFF_V17` §8.2).
                byte* result = stackalloc byte[96];

                IntPtr* args = stackalloc IntPtr[3];
                args[0] = (IntPtr)song;
                args[1] = (IntPtr)(&difficulty);
                args[2] = (IntPtr)result;

                IntPtr raised = IntPtr.Zero;
                Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(method, table, (void**)args, ref raised);
                if (raised != IntPtr.Zero) return "the table failed its self-test: " + Raised.Text(raised);
                return null;
            }
            catch (Exception e)
            {
                return "the self-test could not be run: " + Diagnostics.Describe(e);
            }
        }

        /// <summary>
        /// Writes the table to `IFCL.sav` through the game's own save method — which serializes,
        /// and leaves the file alone when the bytes have not changed since the last write.
        /// Called after every merge into <see cref="Table"/>. A failure is reported once and is
        /// not fatal: the game keeps running, and the run's custom scores are simply not on disk.
        ///
        /// Success is read off the file, not off the call — see the class remarks for the
        /// measurement this rests on, and <see cref="SeedWrittenCache"/> for why a raise is no
        /// longer expected. A raise whose file moved is still counted as a save that landed
        /// (<see cref="Late"/>); only a raise that left the file untouched is a failure.
        /// </summary>
        internal static void Save()
        {
            if (Container == IntPtr.Zero) return;

            try
            {
                IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(Container);
                IntPtr method = klass == IntPtr.Zero ? IntPtr.Zero : MethodOn(klass, "_JOA", 0);
                if (method == IntPtr.Zero)
                {
                    SaveFailed("the save method is not on the container in this build");
                    return;
                }

                long before = Stamp(FilePath);

                IntPtr raised = IntPtr.Zero;
                Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(method, Container, null, ref raised);
                if (raised == IntPtr.Zero)
                {
                    Saves++;
                    return;
                }

                if (Stamp(FilePath) != before)
                {
                    Saves++;
                    Late++;
                    return;
                }

                SaveFailed(Raised.Text(raised));
            }
            catch (Exception e)
            {
                SaveFailed(Diagnostics.Describe(e));
            }
        }

        /// <summary>
        /// A cheap fingerprint of the save file — its write time and its length together, or -1 when
        /// there is no file to ask about. Enough to answer the one question a save has: was the file
        /// written just now, or left as it was?
        /// </summary>
        private static long Stamp(string path)
        {
            try
            {
                var file = new FileInfo(path);
                return file.LastWriteTimeUtc.Ticks ^ file.Length;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// Says the file could not be written, once. Not `[Conditional]`: this is the one thing a
        /// player has to hear when it happens (the session's custom scores are not being kept),
        /// so it is a shipping line, and one line only — it would otherwise repeat on every result.
        /// </summary>
        private static void SaveFailed(string why)
        {
            if (_writeFailed) return;
            _writeFailed = true;
            Diagnostics.Error("the custom results file could not be written; this session's custom " +
                              "scores are not saved: " + why);
        }

        // ---------------------------------------------------------------- the file

        /// <summary>
        /// The save object read out of `IFCL.sav`, or zero when it could not be read.
        ///
        /// <paramref name="raw"/> is the same bytes, handed on to the container's write cache —
        /// see <see cref="SeedWrittenCache"/>. Set whenever the file was read, even if turning it
        /// into a save object then failed.
        /// </summary>
        private static IntPtr Load(string path, out byte[] raw)
        {
            raw = null;

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception e)
            {
                Diagnostics.Warn($"reading {FileName} failed: {Diagnostics.Describe(e)}");
                return IntPtr.Zero;
            }

            if (bytes.Length == 0)
            {
                Diagnostics.Warn($"{FileName} is empty");
                return IntPtr.Zero;
            }

            raw = bytes;
            return Deserialize(bytes);
        }

        /// <summary>
        /// The bytes as a `SingleFileSaveDataV2`, through the game's own serializer.
        ///
        /// `MemoryPackSerializer.Deserialize&lt;T&gt;` is generic and its definition has no body —
        /// the body every reference-type instantiation runs is the shared one — so the method that
        /// can be called is the instantiation over this save type, which
        /// <see cref="MethodResolver.InflatedMethod"/> makes through the runtime's own reflection.
        /// The arguments are the shape the game's own loader passes: the buffer as a span by
        /// address, the destination by reference, and the options the game itself deserializes
        /// with.
        /// </summary>
        private static IntPtr Deserialize(byte[] bytes)
        {
            IntPtr saveClass = FieldResolver.ClassPointer("SingleFileSaveDataV2");
            if (saveClass == IntPtr.Zero)
            {
                Diagnostics.Warn("SingleFileSaveDataV2 could not be found; the custom results file " +
                                 "cannot be read");
                return IntPtr.Zero;
            }

            IntPtr serializer = FieldResolver.ClassPointer("MemoryPackSerializer");
            if (serializer == IntPtr.Zero)
                serializer = Il2CppInterop.Runtime.IL2CPP.GetIl2CppClass("MemoryPack.dll", "MemoryPack",
                                                                        "MemoryPackSerializer");
            if (serializer == IntPtr.Zero)
            {
                Diagnostics.Warn("MemoryPackSerializer could not be found; the custom results file " +
                                 "cannot be read");
                return IntPtr.Zero;
            }

            // The generic, ref-taking overload — three parameters, and generic: `Deserialize` has
            // siblings (the two-parameter one, and possibly a reflection-based one) and only a
            // generic definition can be instantiated over this save type.
            IntPtr definition = MethodOn(serializer, "Deserialize", 3, genericOnly: true);
            if (definition == IntPtr.Zero)
            {
                Diagnostics.Warn("MemoryPackSerializer.Deserialize could not be found; the custom " +
                                 "results file cannot be read");
                return IntPtr.Zero;
            }

            IntPtr method = MethodResolver.InflatedMethod(serializer, definition, "MemoryPackSerializer",
                                                          "Deserialize", saveClass);
            if (method == IntPtr.Zero)
            {
                Diagnostics.Warn("MemoryPackSerializer.Deserialize could not be instantiated for the " +
                                 "save type; the custom results file cannot be read");
                return IntPtr.Zero;
            }

            IntPtr value = IntPtr.Zero;
            fixed (byte* data = bytes)
            {
                // The span goes by address, the same shape the game's own loader passes — its
                // layout constant lives in `Offsets.Runtime` with the rest of the runtime's. The
                // reference being written goes by its own address; the options are a reference, so
                // their value is the pointer.
                byte* span = stackalloc byte[Offsets.Runtime.SpanSize];
                *(IntPtr*)span = (IntPtr)data;
                *(int*)(span + Offsets.Runtime.SpanLength) = bytes.Length;

                IntPtr* args = stackalloc IntPtr[3];
                args[0] = (IntPtr)span;
                args[1] = (IntPtr)(&value);
                args[2] = SerializerOptions();

                IntPtr raised = IntPtr.Zero;
                Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(method, IntPtr.Zero, (void**)args, ref raised);
                if (raised != IntPtr.Zero)
                {
                    Diagnostics.Warn($"reading {FileName} raised: {Raised.Text(raised)}");
                    return IntPtr.Zero;
                }
            }

            if (value == IntPtr.Zero) Diagnostics.Warn($"reading {FileName} answered with nothing");
            return value;
        }

        /// <summary>
        /// `_NH._dEb` — the options the game itself serializes and deserializes with. Beside the
        /// save singleton in `_NH`'s statics; zero (which MemoryPack reads as its default) when the
        /// field cannot be reached.
        /// </summary>
        private static IntPtr SerializerOptions()
        {
            IntPtr klass = FieldResolver.ClassPointer("_NH");
            if (klass == IntPtr.Zero) return IntPtr.Zero;

            IntPtr statics = FieldResolver.Statics(klass);
            if (statics == IntPtr.Zero) return IntPtr.Zero;

            int at = FieldResolver.Field("_NH", "_dEb");
            return at < 0 ? IntPtr.Zero : Memory.Ptr(statics + at);
        }

        /// <summary>`SingleFileSaveDataV2.CreateDefault()` — the game's own empty save.</summary>
        private static IntPtr CreateDefault()
        {
            IntPtr klass = FieldResolver.ClassPointer("SingleFileSaveDataV2");
            if (klass == IntPtr.Zero)
            {
                Diagnostics.Warn("SingleFileSaveDataV2 could not be found, so an empty save cannot be made");
                return IntPtr.Zero;
            }

            IntPtr method = MethodOn(klass, "CreateDefault", 0);
            if (method == IntPtr.Zero)
            {
                Diagnostics.Warn("SingleFileSaveDataV2.CreateDefault could not be found");
                return IntPtr.Zero;
            }

            IntPtr raised = IntPtr.Zero;
            IntPtr obj = Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(method, IntPtr.Zero, null, ref raised);
            if (raised != IntPtr.Zero)
            {
                Diagnostics.Warn($"making an empty save raised: {Raised.Text(raised)}");
                return IntPtr.Zero;
            }
            return obj;
        }

        /// <summary>`GameResultsV4.Create()` — an empty result table, for a save that has none.</summary>
        private static IntPtr NewResults()
        {
            IntPtr klass = FieldResolver.ClassPointer("GameResultsV4");
            if (klass == IntPtr.Zero)
            {
                Diagnostics.Warn("GameResultsV4 could not be found, so an empty table cannot be made");
                return IntPtr.Zero;
            }

            IntPtr method = MethodOn(klass, "Create", 0);
            if (method == IntPtr.Zero)
            {
                Diagnostics.Warn("GameResultsV4.Create could not be found");
                return IntPtr.Zero;
            }

            IntPtr raised = IntPtr.Zero;
            IntPtr table = Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(method, IntPtr.Zero, null, ref raised);
            if (raised != IntPtr.Zero)
            {
                Diagnostics.Warn($"making an empty result table raised: {Raised.Text(raised)}");
                return IntPtr.Zero;
            }
            return table;
        }

        /// <summary>
        /// The container: an instance of the game's own class, built with this file's two paths.
        ///
        /// The class is the one the game's own container has — `_NH._OH&lt;SingleFileSaveDataV2&gt;`
        /// — so the object is the game's, its save method is the game's, and the file it writes has
        /// the game's format by construction. The constructor takes the path parts explicitly
        /// (which is why this file can be called something the factory could not name), and the two
        /// arguments this mod has no use for — the queue of post-migration steps and the
        /// default-creation function, both consumed by the factory this class does not go through —
        /// are left null.
        ///
        /// What the factory also does, and the constructor does not, is seed the write cache; that
        /// is <see cref="SeedWrittenCache"/>, and skipping it is what made every save raise after
        /// writing its file. <paramref name="known"/> is the file's own bytes for that seed.
        /// </summary>
        private static IntPtr NewContainer(IntPtr klass, IntPtr saveObject, string path, string tempPath,
                                           byte[] known)
        {
            IntPtr ctor = MethodOn(klass, ".ctor", 7);
            if (ctor == IntPtr.Zero)
            {
                Diagnostics.Warn("the save container's constructor could not be found");
                return IntPtr.Zero;
            }

            IntPtr instance = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_new(klass);
            if (instance == IntPtr.Zero)
            {
                Diagnostics.Warn("the save container could not be allocated");
                return IntPtr.Zero;
            }

            IntPtr* args = stackalloc IntPtr[7];
            args[0] = saveObject;
            args[1] = Str(ShortName);
            args[2] = Str(ShortTempName);
            args[3] = Str(path);
            args[4] = Str(tempPath);
            args[5] = IntPtr.Zero;
            args[6] = IntPtr.Zero;

            IntPtr raised = IntPtr.Zero;
            Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(ctor, instance, (void**)args, ref raised);
            if (raised != IntPtr.Zero)
            {
                Diagnostics.Warn($"building the save container raised: {Raised.Text(raised)}");
                return IntPtr.Zero;
            }

            SeedWrittenCache(instance, known);
            return instance;
        }

        /// <summary>
        /// Gives the container the write cache its constructor does not.
        ///
        /// <para>
        /// `_JOA` writes the file and then updates a cache of the bytes it wrote — `_LEb` (a
        /// `byte[]`) and `_mEb` (how many of them are valid) — and that cache is what lets a later
        /// save whose bytes are unchanged skip the write. The closing step does not check the array
        /// for null: with `_LEb` zero it raises, after the file is already correct on disk.
        /// </para>
        /// <para>
        /// A container from the game's own factory is never in that state — the factory seeds both
        /// fields before returning, with a buffer holding the file's bytes (and an allocated, empty
        /// one when there is no file). This mod builds its container by calling the constructor
        /// directly, so that seeding is the step it was skipping. Measured without it, every save
        /// of a session: the file written correctly, then the raise, then the game's own retry loop
        /// — five more attempts at moving a temporary file the first attempt had already consumed,
        /// with sleeps of 25/50/75/100/125 ms (0.375 s, and the two runs it was watched in showed
        /// 0.387 and 0.389) — and the `FileNotFoundException` from the last attempt rethrown.
        /// Which is why `Save` used to have to judge a write by the file rather than by the call.
        /// </para>
        /// <para>
        /// Seeding the file's own bytes makes the cache start out true, so the first save that
        /// changes nothing writes nothing; with no file (or no array to be had) the cache starts
        /// empty and the closing step grows it. A seed that cannot be made is not fatal — the
        /// behaviour is then the one measured above, which `Save` already handles.
        /// </para>
        /// </summary>
        private static void SeedWrittenCache(IntPtr container, byte[] known)
        {
            if (container == IntPtr.Zero) return;

            try
            {
                int length = known == null ? 0 : known.Length;

                // Exactly the file's own bytes and nothing more. No size of this mod's choosing
                // belongs here: the closing step grows the buffer itself (to the next power of two)
                // whenever a write outgrows it, so an initial capacity would be a number with
                // nothing behind it — and for the first save of a session it would be outgrown
                // anyway. With no file this is a zero-length array, which is a complete, non-null
                // value; see `ByteArray`.
                IntPtr bytes = ByteArray(length);
                if (bytes == IntPtr.Zero)
                {
                    Diagnostics.Warn("the container's write cache could not be seeded; the first save " +
                                     "will take the game's retry path and raise an exception the file " +
                                     "will have to be judged against");
                    return;
                }

                if (length > 0) Marshal.Copy(known, 0, bytes + Offsets.Runtime.ArrayDataOffset, length);

                Memory.WritePtr(container + FieldWrittenBytes, bytes);
                Memory.WriteI32(container + FieldWrittenLength, length);
                Diagnostics.Info($"the container's write cache starts with {length} byte(s)");
            }
            catch (Exception e)
            {
                Diagnostics.Warn("the container's write cache could not be seeded: " + Diagnostics.Describe(e));
            }
        }

        /// <summary>
        /// A `byte[]` of this game's own heap — one dimension, `count` long — or zero.
        ///
        /// The element class is taken from an array that already is one: the game's own container
        /// keeps the bytes it wrote last as a `byte[]` (the factory seeds it — see
        /// <see cref="SeedWrittenCache"/>), and an array's own class is exactly the class this
        /// needs. A container that somehow has no such array falls back to asking the runtime for
        /// `System.Byte` by name, which is how every other class this mod reaches is found.
        /// (`il2cpp_object_new` is not an option: on an array class it would make something with no
        /// bounds at all.)
        /// </summary>
        private static IntPtr ByteArray(int count)
        {
            IntPtr arrayClass = BorrowedByteArrayClass();
            if (arrayClass == IntPtr.Zero)
            {
                IntPtr byteClass = Il2CppInterop.Runtime.IL2CPP.GetIl2CppClass("mscorlib.dll", "System", "Byte");
                if (byteClass == IntPtr.Zero) return IntPtr.Zero;

                arrayClass = Il2CppInterop.Runtime.IL2CPP.il2cpp_array_class_get(byteClass, 1);
            }
            if (arrayClass == IntPtr.Zero) return IntPtr.Zero;

            return Il2CppInterop.Runtime.IL2CPP.il2cpp_array_new(arrayClass, (ulong)count);
        }

        /// <summary>
        /// The class of the game container's own write-cache array, or zero when it has none.
        ///
        /// Reached the same way <see cref="Prepare"/> reaches that container — off the save
        /// singleton — rather than kept from there: this runs once, and two pointer reads are
        /// cheaper than another parameter threaded through the constructor call.
        /// </summary>
        private static IntPtr BorrowedByteArrayClass()
        {
            try
            {
                if (FieldWrittenBytes <= 0) return IntPtr.Zero;

                IntPtr save = RewardEntries.PlayerSave();
                IntPtr official = save == IntPtr.Zero ? IntPtr.Zero : Memory.Ptr(save + SaveContainer);
                if (!Memory.LooksLikeObject(official)) return IntPtr.Zero;

                IntPtr sample = Memory.Ptr(official + FieldWrittenBytes);
                if (!Memory.LooksLikeObject(sample)) return IntPtr.Zero;

                return Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(sample);
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        // ---------------------------------------------------------------- resolution

        private static bool _fieldsMissing;

        /// <summary>
        /// The four field offsets, asked of the running game once, and whether all of them answered.
        ///
        /// A miss leaves its field at -1 and is reported by <see cref="FieldOn"/>; this then answers
        /// false and latches, and <see cref="Prepare"/> gives up on the whole feature — the custom
        /// results stay in the game's own save, which is what happened before this class existed,
        /// rather than being read and written through an offset that is not there.
        /// </summary>
        private static bool Resolve(IntPtr containerClass)
        {
            if (_fieldsMissing) return false;
            if (_resolved) return true;
            _resolved = true;

            FieldFullPath = FieldOn(containerClass, "_JEb");
            FieldWrittenBytes = FieldOn(containerClass, "_LEb");
            FieldWrittenLength = FieldOn(containerClass, "_mEb");
            FieldGameResults = FieldOn(FieldResolver.ClassPointer("SingleFileSaveDataV2"), "GameResults");

            if (FieldFullPath < 0 || FieldWrittenBytes < 0 || FieldWrittenLength < 0 ||
                FieldGameResults < 0)
            {
                _fieldsMissing = true;
                return false;
            }

            return true;
        }

        /// <summary>
        /// A field's offset on one class, asked by name — or <b>-1</b> when it cannot be asked.
        ///
        /// These two classes are not in <see cref="FieldResolver"/>'s index — one is a nested
        /// generic, the other is reached as an instance's own class — so the offset is taken off the
        /// class pointer itself. There is no number in this signature, for the reason
        /// <see cref="FieldResolver.Field"/> has none: a miss is reported here, by name, in every
        /// build, and the caller has to stop — see <see cref="Resolve"/>, which does.
        /// </summary>
        private static int FieldOn(IntPtr klass, string name)
        {
            if (klass == IntPtr.Zero) return -1;

            try
            {
                IntPtr field = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(klass, name);
                if (field == IntPtr.Zero)
                {
                    Diagnostics.Error($"{name}: no usable field by that name in this build");
                    return -1;
                }

                int offset = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(field);
                if (offset <= 0)
                {
                    Diagnostics.Error($"{name}: no usable field by that name in this build");
                    return -1;
                }

                return offset;
            }
            catch (Exception)
            {
                Diagnostics.Error($"{name}: no usable field by that name in this build");
                return -1;
            }
        }

        /// <summary>
        /// One of a class's methods, by name and parameter count, as its `MethodInfo` — or zero.
        ///
        /// A walk rather than `il2cpp_class_get_method_from_name`, because what is wanted here is
        /// the method's <b>structure</b> (to invoke it) and this way the filters are explicit for
        /// every use: `TryGetResult` and `Deserialize` both have siblings with the same name, and
        /// the caller that means to inflate one has to ask for the generic definition.
        /// </summary>
        internal static IntPtr MethodOn(IntPtr klass, string name, int parameters, bool genericOnly = false)
        {
            if (klass == IntPtr.Zero) return IntPtr.Zero;

            IntPtr iter = IntPtr.Zero;
            IntPtr method;
            while ((method = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
            {
                if (Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_name_(method) != name) continue;
                if ((int)Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_param_count(method) != parameters) continue;
                if (genericOnly && !Il2CppInterop.Runtime.IL2CPP.il2cpp_method_is_generic(method)) continue;
                return method;
            }
            return IntPtr.Zero;
        }

        private static IntPtr Str(string text) =>
            Il2CppInterop.Runtime.IL2CPP.ManagedStringToIl2Cpp(text);
    }
}
