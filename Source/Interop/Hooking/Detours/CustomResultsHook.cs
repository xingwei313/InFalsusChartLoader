using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MelonLoader.NativeUtils;

namespace InFalsusChartLoader
{
    internal static unsafe partial class Hooks
    {
        /// <summary>
        /// The four hooks that keep the custom songs' results out of the game's own save — see
        /// <see cref="CustomResults"/> for the whole of it.
        ///
        /// <para>
        /// `GameResultsV4` is the game's result table: one per save, holding the best score of every
        /// (song, difficulty), the per-play history, and the mapping between them. Two methods read
        /// a song's record out of it and exactly one writes one — `TryUpdate`, whose only caller is
        /// the results screen's commit — and all three take the table as their receiver. That is
        /// what these hooks stand on: for a song this mod imported, the receiver is swapped for the
        /// mod's own table and the game's method runs untouched against it. The merge rules, the
        /// rank, the history append, and the "was this a new record" answer the results screen uses
        /// are therefore still the game's, and the game's own table never sees a custom song's
        /// record at all — which is also what keeps the game's own save file clean, since that file
        /// is written from that table and nothing else.
        /// </para>
        /// <para>
        /// The write hook also persists: the moment a custom song's result has been merged, the
        /// mod's table is written to `IFCL.sav` through the game's own save method. The game's own
        /// save is written by the game itself a few frames later, as it always was, carrying the
        /// particles and everything else — and carrying no custom result, because none of them were
        /// ever put in it.
        /// </para>
        /// <para>
        /// The settlement writes two things and they are two members of the save, not one: the song's
        /// record, in `GameResultsV4`, and the encounter's result — the "回想" a play leaves behind —
        /// in `EncounterResults`. Both writes happen in the same `ResultsScene.Start`, the encounter's
        /// first, and both take the table the player's own save holds. The hooks above cover the
        /// record; the fourth covers the encounter, by not making the call at all for a custom play.
        /// </para>
        /// <para>
        /// Every body is a test and a forward: a song that is not this mod's goes to the trampoline
        /// with the receiver it came in with, byte for byte. Nothing is pinned — the class is asked
        /// for by name, and its methods by name and parameter count — so a miss leaves these hooks
        /// uninstalled and says so, rather than patching a guess.
        /// </para>
        /// </summary>
        /// <summary>
        /// Where `GameResultsV4` lives, for the runtime's own lookup when the generated store does
        /// not answer yet.
        /// </summary>
        private const string ResultsImage = "Game.Data.dll";

        private const string ResultsNamespace = "ifapp.Game.Data";

        /// <summary>
        /// `bool TryGetResult(in SongInfo, ChartDifficultyFlag, out GameResultV4)` — the read the
        /// song list and the results screen use.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte ReadResultFn(IntPtr results, IntPtr songInfo, byte difficulty,
                                           IntPtr result, IntPtr methodInfo);

        /// <summary>
        /// The wide overload of the same read: score, the four judgement counts, the lamp, the rank
        /// and the clear, for the screens that show all of them together.
        ///
        /// Every value after the difficulty is declared as a pointer because that is all this hook
        /// needs them to be: the receiver is the only argument it substitutes, and the rest are
        /// forwarded exactly as they arrived.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte ReadResultFullFn(IntPtr results, IntPtr songInfo, byte difficulty,
                                               IntPtr score, IntPtr shiny, IntPtr perfect, IntPtr far,
                                               IntPtr miss, IntPtr lamp, IntPtr rank, IntPtr clear,
                                               IntPtr methodInfo);

        /// <summary>
        /// `GameResultsV4.UpdateResult TryUpdate(in SongInfo, in GameResultV4, out long)` — the
        /// merge, and the one place a record is ever written.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int UpdateResultFn(IntPtr results, IntPtr songInfo, IntPtr newResult,
                                            IntPtr existingScore, IntPtr methodInfo);

        private static NativeHook<ReadResultFn> _readResult;
        private static ReadResultFn _readResultTramp;
        private static NativeHook<ReadResultFullFn> _readResultFull;
        private static ReadResultFullFn _readResultFullTramp;
        private static NativeHook<UpdateResultFn> _updateResult;
        private static UpdateResultFn _updateResultTramp;

        /// <summary>
        /// `bool UpdateEncounterResult(EncounterResult)` — the encounter ("回想") result's only writer.
        ///
        /// A class of its own, not a method on the result table: an encounter result is keyed by
        /// `EncounterId` and holds a `BattleScore`, so nothing in it names a song. Its only caller is
        /// the same settlement (`ResultsScene.Start`), a few hundred bytes ahead of `TryUpdate`'s.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte UpdateEncounterFn(IntPtr results, IntPtr newResult, IntPtr methodInfo);

        private static NativeHook<UpdateEncounterFn> _updateEncounter;
        private static UpdateEncounterFn _updateEncounterTramp;

        private static bool InstallCustomResults()
        {
            try
            {
                IntPtr klass = FieldResolver.ClassPointer("GameResultsV4");
                if (klass == IntPtr.Zero)
                    klass = Il2CppInterop.Runtime.IL2CPP.GetIl2CppClass(ResultsImage, ResultsNamespace,
                                                                        "GameResultsV4");

                // `TryGetResult` exists in two arities and this mod wants both; the walk picks each
                // by its parameter count, and the first word of what it answers with is the code.
                IntPtr read = CustomResults.MethodOn(klass, "TryGetResult", 3);
                IntPtr readFull = CustomResults.MethodOn(klass, "TryGetResult", 10);
                IntPtr update = CustomResults.MethodOn(klass, "TryUpdate", 3);

                IntPtr readCode = read == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)read;
                IntPtr readFullCode = readFull == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)readFull;
                IntPtr updateCode = update == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)update;

                if (readCode == IntPtr.Zero || readFullCode == IntPtr.Zero || updateCode == IntPtr.Zero)
                {
                    Diagnostics.Error("the game's result table could not be found; custom scores will go " +
                                      "into the game's own save file");
                    return false;
                }

                byte[] readPrologue = Prologue(readCode);
                _readResult = new NativeHook<ReadResultFn>
                {
                    Target = readCode,
                    Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte, IntPtr, IntPtr, byte>)
                        &ReadResultDetour,
                };
                _readResult.Attach();
                _readResultTramp = _readResult.Trampoline;
                Diagnostics.Info("GameResultsV4.TryGetResult(song) hooked");

                byte[] readFullPrologue = Prologue(readFullCode);
                _readResultFull = new NativeHook<ReadResultFullFn>
                {
                    Target = readFullCode,
                    Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte, IntPtr, IntPtr, IntPtr,
                        IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, byte>)&ReadResultFullDetour,
                };
                _readResultFull.Attach();
                _readResultFullTramp = _readResultFull.Trampoline;
                Diagnostics.Info("GameResultsV4.TryGetResult(song, full) hooked");

                byte[] updatePrologue = Prologue(updateCode);
                _updateResult = new NativeHook<UpdateResultFn>
                {
                    Target = updateCode,
                    Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int>)
                        &UpdateResultDetour,
                };
                _updateResult.Attach();
                _updateResultTramp = _updateResult.Trampoline;
                Diagnostics.Info("GameResultsV4.TryUpdate hooked");

                // The encounter result's writer, on the same settlement path. It is a separate class,
                // so it is reached separately; a miss is a shipping line rather than a silence, and it
                // leaves the game's behaviour exactly as it was before this hook existed.
                IntPtr encounterClass = FieldResolver.ClassPointer("EncounterResults");
                if (encounterClass == IntPtr.Zero)
                    encounterClass = Il2CppInterop.Runtime.IL2CPP.GetIl2CppClass(ResultsImage, ResultsNamespace,
                                                                                "EncounterResults");

                IntPtr encounter = CustomResults.MethodOn(encounterClass, "UpdateEncounterResult", 1);
                IntPtr encounterCode = encounter == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)encounter;
                bool encounterOk = false;

                if (encounterCode == IntPtr.Zero)
                {
                    Diagnostics.Error("the game's encounter results could not be found; a custom song's " +
                                      "encounter result will be written into the game's own save");
                }
                else
                {
                    byte[] encounterPrologue = Prologue(encounterCode);
                    _updateEncounter = new NativeHook<UpdateEncounterFn>
                    {
                        Target = encounterCode,
                        Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte>)
                            &UpdateEncounterDetour,
                    };
                    _updateEncounter.Attach();
                    _updateEncounterTramp = _updateEncounter.Trampoline;
                    Diagnostics.Info("EncounterResults.UpdateEncounterResult hooked");
                    encounterOk = Landed(Hook.EncounterResult, encounterCode, encounterPrologue);
                }

                return Landed(Hook.ResultRead, readCode, readPrologue)
                     & Landed(Hook.ResultReadFull, readFullCode, readFullPrologue)
                     & Landed(Hook.ResultUpdate, updateCode, updatePrologue)
                     & encounterOk;
            }
            catch (Exception e)
            {
                Diagnostics.Warn("the custom result hooks could not be installed: " + Diagnostics.Describe(e));
                return false;
            }
        }

        private static void DetachCustomResults()
        {
            _updateEncounter?.Detach();
            _updateEncounter = null;
            _updateEncounterTramp = null;

            _updateResult?.Detach();
            _updateResult = null;
            _updateResultTramp = null;

            _readResultFull?.Detach();
            _readResultFull = null;
            _readResultFullTramp = null;

            _readResult?.Detach();
            _readResult = null;
            _readResultTramp = null;
        }

        /// <summary>
        /// A song's record: answered from the custom table when the song is this mod's, and from
        /// the game's own when it is not. The receiver is the only thing that changes; the answer
        /// (including "there is no record", for a custom song never played) is the game's.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte ReadResultDetour(IntPtr results, IntPtr songInfo, byte difficulty,
                                             IntPtr result, IntPtr methodInfo)
        {
            CustomResults.Reads++;

            IntPtr table = results;
            if (!Faulted)
            {
#if DEBUG
                long t0 = Now;
#endif
                try
                {
                    if (CustomResults.Ready && CustomResults.IsOurs(songInfo))
                    {
                        CustomResults.Served++;
                        table = CustomResults.Table;
                    }
                }
                catch (Exception e)
                {
                    Fault(Hook.ResultRead, e);
                }
#if DEBUG
                TicksRead += Now - t0;
#endif
            }

            return _readResultTramp(table, songInfo, difficulty, result, methodInfo);
        }

        /// <summary>The wide read, substituted the same way.</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte ReadResultFullDetour(IntPtr results, IntPtr songInfo, byte difficulty,
                                                 IntPtr score, IntPtr shiny, IntPtr perfect, IntPtr far,
                                                 IntPtr miss, IntPtr lamp, IntPtr rank, IntPtr clear,
                                                 IntPtr methodInfo)
        {
            CustomResults.Reads++;

            IntPtr table = results;
            if (!Faulted)
            {
#if DEBUG
                long t0 = Now;
#endif
                try
                {
                    if (CustomResults.Ready && CustomResults.IsOurs(songInfo))
                    {
                        CustomResults.Served++;
                        table = CustomResults.Table;
                    }
                }
                catch (Exception e)
                {
                    Fault(Hook.ResultReadFull, e);
                }
#if DEBUG
                TicksRead += Now - t0;
#endif
            }

            return _readResultFullTramp(table, songInfo, difficulty, score, shiny, perfect, far, miss,
                                        lamp, rank, clear, methodInfo);
        }

        /// <summary>
        /// The merge: a custom song's result is taken into this mod's table — which is also where
        /// its history row goes, since the append is inside the method — and the file is written
        /// right afterwards. The answer (`New` / `Updated` / `NoChange`) goes back to the game
        /// unchanged, because the results screen acts on it.
        ///
        /// The game's own summary cache is not nudged. It recomputes when the *game's* table's
        /// update counter moves, and a custom play does not move it: the custom songs are no part
        /// of the game's own progress, so the numbers a summary would show are the ones it already
        /// has.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int UpdateResultDetour(IntPtr results, IntPtr songInfo, IntPtr newResult,
                                              IntPtr existingScore, IntPtr methodInfo)
        {
            CustomResults.Updates++;

            bool ours = false;
            IntPtr table = results;
            if (!Faulted)
            {
                try
                {
                    if (CustomResults.Ready && CustomResults.IsOurs(songInfo))
                    {
                        ours = true;
                        table = CustomResults.Table;
                    }
                }
                catch (Exception e)
                {
                    Fault(Hook.ResultUpdate, e);
                }
            }

            int outcome = _updateResultTramp(table, songInfo, newResult, existingScore, methodInfo);

            if (ours)
            {
                CustomResults.Merged++;
                CustomResults.Save();
            }

            return outcome;
        }

        /// <summary>
        /// Drops the encounter result of a play that was one of this mod's songs, and forwards every
        /// other one untouched.
        ///
        /// Write to nothing, literally: the game's own call is not made, so the result is written
        /// neither into the game's save nor into this mod's. The answer is `false` — what the call
        /// answers when nothing was recorded. The alternative, calling the real writer against a table
        /// of this mod's own, would answer "recorded" while nothing that persists had been, and the
        /// caller keeps that answer.
        ///
        /// Which play this is comes from the same static payload the rest of the mod reads the
        /// selection from — but the read asks only for the song (<see cref="Selection.TryReadSong"/>),
        /// not for the difficulty: measured on this build, the payload carries the played song and
        /// leaves the difficulty byte at zero, so the full read (which gates on that byte) turns every
        /// real play down. The call itself cannot say — its key is an `EncounterId`, and nothing in
        /// its arguments is a song.
        ///
        /// Gated on <see cref="CustomResults.Ready"/> like the three beside it: with no custom results
        /// table this mod is in its fallback, where a custom song's records go into the game's own
        /// save exactly as they did before it existed, and the encounter is part of that.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte UpdateEncounterDetour(IntPtr results, IntPtr newResult, IntPtr methodInfo)
        {
            CustomResults.EncounterWrites++;

            if (!Faulted)
            {
                try
                {
                    bool ours = false;
                    IntPtr playing = IntPtr.Zero;
#if DEBUG
                    // What the settlement's song looked like, so the next run answers "did the
                    // criterion fire, and if not, why" from the log instead of from another round.
                    string seen = "no payload";
#endif
                    if (CustomResults.Ready && Selection.TryReadSong(out playing))
                    {
                        ours = CustomResults.IsOurs(playing);
#if DEBUG
                        seen = $"song {Memory.U16(playing + Offsets.Song.Id)}, {(ours ? "ours" : "not ours")}";
#endif
                    }

                    if (ours)
                    {
                        CustomResults.EncounterDropped++;
#if DEBUG
                        Diagnostics.Info($"the encounter result of '{JacketCatalog.NameOf(playing) ?? "?"}' " +
                                         "was not written (a custom song keeps no encounter record)");
#endif
                        return 0;
                    }

#if DEBUG
                    // One line per settlement — a rare event — and the only place "the decision ran
                    // and answered no" is distinguishable from "the decision never ran".
                    Diagnostics.Info($"the encounter result was written; the settlement's song: {seen}");
#endif
                }
                catch (Exception e)
                {
                    Fault(Hook.EncounterResult, e);
                }
            }

            return _updateEncounterTramp(results, newResult, methodInfo);
        }
    }
}
