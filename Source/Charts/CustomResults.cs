using System;
using System.IO;

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
    /// A raise is therefore not evidence of a failure; the file's stamp not moving is, and that is
    /// the one case the error line is for. The raises that did land are counted apart so the
    /// behaviour stays visible rather than being swallowed.
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
        /// <summary>`_NH._DEb`, the container holding the game's own save.</summary>
        private const int SaveContainer = 0x10;

        // ---- Fields of the two classes reached into. They are properties of *this build of the
        // game*, so the name is what is used and the number is only what the build was reversed
        // with: `Resolve` asks the running game and adopts its answer (see `FieldOn`).
        private static int FieldFullPath = 0x28;      // `_NH._OH<T>._JEb` — where the container's file is
        private static int FieldGameResults = 0x20;   // `SingleFileSaveDataV2.GameResults`

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
        /// <see cref="Saves"/>'s total only for visibility — see <see cref="Save"/> — and expected to
        /// equal it on a build whose save behaves the way this one was measured to.
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
            return SongCatalog.CustomSongIds.Contains(Memory.U16(songInfo));
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
                IntPtr official = save == IntPtr.Zero ? IntPtr.Zero : Memory.Ptr(save + SaveContainer);
                if (!Memory.LooksLikeObject(official))
                {
                    Diagnostics.Error("the game's save could not be reached; custom scores will go into " +
                                      "the game's own save file");
                    return;
                }

                IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(official);
                Resolve(klass);

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
                IntPtr obj = exists ? Load(path) : IntPtr.Zero;
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

                IntPtr container = NewContainer(klass, obj, path, tempPath);
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
                byte* song = stackalloc byte[0x48];
                for (int i = 0; i < 0x48; i++) song[i] = 0;
                *(IntPtr*)(song + 8) = Str("IFCL self test");

                byte difficulty = 1;
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
        /// measurement this rests on. A raise whose file moved is counted as a save that landed
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

        /// <summary>The save object read out of `IFCL.sav`, or zero when it could not be read.</summary>
        private static IntPtr Load(string path)
        {
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
                // ReadOnlySpan<byte> is 16 bytes and goes by address, the same shape the game's own
                // loader passes. The reference being written goes by its own address; the options
                // are a reference, so their value is the pointer.
                byte* span = stackalloc byte[16];
                *(IntPtr*)span = (IntPtr)data;
                *(int*)(span + 8) = bytes.Length;

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

            return Memory.Ptr(statics + FieldResolver.Field("_NH", "_dEb", 0x08));
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
        /// </summary>
        private static IntPtr NewContainer(IntPtr klass, IntPtr saveObject, string path, string tempPath)
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
            return instance;
        }

        // ---------------------------------------------------------------- resolution

        /// <summary>The two field offsets, asked of the running game once.</summary>
        private static void Resolve(IntPtr containerClass)
        {
            if (_resolved) return;
            _resolved = true;

            FieldFullPath = FieldOn(containerClass, "_JEb", FieldFullPath);
            FieldGameResults = FieldOn(FieldResolver.ClassPointer("SingleFileSaveDataV2"),
                                       "GameResults", FieldGameResults);
        }

        /// <summary>
        /// A field's offset on one class, by name; the fallback when it cannot be asked.
        ///
        /// These two classes are not in <see cref="FieldResolver"/>'s index — one is a nested
        /// generic, the other is reached as an instance's own class — so the offset is taken off
        /// the class pointer itself. An offset that disagrees with the constant is adopted and
        /// reported, the same rule the rest of this mod resolves fields by.
        /// </summary>
        private static int FieldOn(IntPtr klass, string name, int fallback)
        {
            if (klass == IntPtr.Zero) return fallback;

            try
            {
                IntPtr field = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(klass, name);
                if (field == IntPtr.Zero)
                {
                    Diagnostics.Warn($"{name} is not on its class; keeping 0x{fallback:X}");
                    return fallback;
                }

                int offset = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(field);
                if (offset <= 0)
                {
                    Diagnostics.Warn($"{name} reports offset 0x{offset:X}, which cannot be one; keeping 0x{fallback:X}");
                    return fallback;
                }

                if (offset != fallback)
                    Diagnostics.Warn($"{name} has moved to 0x{offset:X} (this build was reversed with " +
                                     $"0x{fallback:X}); using the game's");
                return offset;
            }
            catch (Exception e)
            {
                Diagnostics.Warn($"{name} could not be resolved: {Diagnostics.Describe(e)}");
                return fallback;
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
