using System;

namespace InFalsusChartLoader
{
    /// <summary>
    /// The song and difficulty the game is currently on.
    ///
    /// <para>
    /// Two places need this and neither is handed it. `SongData._apA` takes a song and returns a
    /// jacket, so a song with a different picture per difficulty has to be told which one to show;
    /// and `GameplayBackgrounds._UmA` takes only the player's background choice, so a mod picture
    /// has to be matched to a song by something else entirely.
    /// </para>
    /// <para>
    /// The payload that carries both is the one the song-select writes when the player picks a song
    /// and a difficulty. In the demo build's source it is a static on <c>SongSelectScene</c> that the
    /// play handler fills as its first act, and in the shipped build it is the same thing: a
    /// <c>private static _nG</c>, whose first field is the song record and whose difficulty byte sits
    /// right after it. Everything about that shape is asked by name (<see cref="Fields"/>) — no
    /// offset is written in this file, so nothing in it can go stale.
    /// </para>
    /// <para>
    /// The record is a struct held in the static field, so its address is the field's address — not a
    /// pointer to dereference. That is what makes this cheap enough to call from the jacket path: no
    /// allocation, no lookup, two reads.
    /// </para>
    /// </summary>
    internal static class Selection
    {
        // No offsets are written here: all five are asked by name (`Fields`), and each stays at
        // `Offsets.Unresolved` (-1) until it answers. See `Offsets` for why.

        /// <summary>Where the payload sits inside `SongSelectScene`'s statics.</summary>
        private static int PayloadField = Offsets.Unresolved;

        /// <summary>
        /// `_nG._LYA` (the selected song, embedded — this is its address, not a pointer to it) and
        /// `_nG._mYA` (its difficulty, one byte). Resolved by name where the payload is reached.
        /// </summary>
        private static int PayloadSong = Offsets.Unresolved;
        private static int PayloadDifficulty = Offsets.Unresolved;

        /// <summary>`SongInfo.ChartInfos` — read only to tell a filled payload from a zeroed one.</summary>
        private static int SongInfoCharts => Offsets.Song.Charts;

        /// <summary>The four flags, in difficulty order. The same set the chart records are written with.</summary>
        private static readonly byte[] DifficultyFlags = { 1, 2, 4, 8 };

        private static IntPtr _payload;

        private static bool _fieldsAsked;

        private static bool _fieldsMissing;

        /// <summary>
        /// The address of the selected song record and its difficulty as 0..3, or false when the game
        /// has not filled the payload in yet.
        ///
        /// <paramref name="songInfo"/> is an address into the game's statics block, not a heap
        /// object: the record is embedded in the payload, so there is no header to check. Whether it
        /// names a song of this mod's is the caller's question and the caller's check — this answers
        /// only where the record is, and it deliberately does not read the name to decide that,
        /// because every caller reads the name anyway and this runs on a path that repaints the whole
        /// song list.
        /// </summary>
        internal static bool TryRead(out IntPtr songInfo, out int difficulty)
        {
            songInfo = IntPtr.Zero;
            difficulty = -1;

            // The record's own shape is part of the same answer: `Offsets` was asked for it, and a
            // name that did not resolve leaves it at -1 — which is the record's header, not a field.
            if (!Offsets.Ready) return false;

            IntPtr payload = Payload();
            if (payload == IntPtr.Zero) return false;

            // A difficulty that is not one of the four flags means the payload has not been written
            // yet, which is the ordinary state for the first frames and not an error.
            int index = IndexOf(Memory.U8(payload + PayloadDifficulty));
            if (index < 0)
            {
                Refused(payload, $"the difficulty byte is not one of the four flags");
                return false;
            }

            IntPtr song = payload + PayloadSong;

            // One pointer read as a second opinion, on a field that is never null for a real song and
            // is zero in a zeroed payload. A name that cannot be read is caught by the caller.
            if (!Memory.LooksLikeObject(Memory.Ptr(song + SongInfoCharts)))
            {
                Refused(payload, "the selected song has no chart array");
                return false;
            }

            songInfo = song;
            difficulty = index;
            return true;
        }

        /// <summary>
        /// The song the payload names, without asking for the difficulty.
        ///
        /// <see cref="TryRead"/> turns the read down unless the difficulty byte is one of the four
        /// flags, which is right for its callers — they all want the difficulty. This one is for the
        /// callers that want the song alone, and the gate has to come off for them: measured on this
        /// build, the play handler writes the selected song into the payload and leaves the
        /// difficulty byte at zero (the difficulty actually in force lives on the scene — see
        /// <see cref="FromScene"/>), so a caller that required it would answer "no" for every real
        /// play. The song itself is the part that is always there.
        /// </summary>
        internal static bool TryReadSong(out IntPtr songInfo)
        {
            songInfo = IntPtr.Zero;

            if (!Offsets.Ready) return false;

            IntPtr payload = Payload();
            if (payload == IntPtr.Zero) return false;

            IntPtr song = payload + PayloadSong;

            // A zeroed payload names no song — id 0 is the empty slot, the same invariant the song
            // table itself uses — and a song's record always carries its chart array. The id's
            // offset is asked (`Offsets.Song.Id`), not assumed to be zero.
            if (Memory.U16(song + Offsets.Song.Id) == 0) return false;
            if (!Memory.LooksLikeObject(Memory.Ptr(song + SongInfoCharts))) return false;

            songInfo = song;
            return true;
        }

        /// <summary>
        /// Debug-only: what the payload actually held when this was turned down, once.
        ///
        /// Every claim in this file is about an offset in a structure nothing else in the mod reads,
        /// and a wrong one has no symptom of its own — the three checks above would simply keep
        /// answering "not yet" for the rest of the session. So the first refusal prints the bytes, and
        /// which of the checks it was, rather than leaving the next reader to infer it from counters.
        ///
        /// Reported on the first <b>refusal</b> rather than the first use: the payload is the game's
        /// to fill, and reading it before it has been written would show an empty one and say nothing.
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void Refused(IntPtr payload, string why)
        {
#if DEBUG
            if (_refused) return;
            _refused = true;

            // The song record and the words after it, so a payload that is not shaped the way the
            // names say is visible as bytes instead of inferred from a failed check. The window
            // starts where the record does — a resolved offset, not a number.
            var words = new System.Text.StringBuilder();
            for (int i = 0; i < 12; i++)
            {
                if (i > 0) words.Append(' ');
                words.Append(Memory.I64(payload + PayloadSong + i * 8).ToString("X16"));
            }

            Diagnostics.Info($"probe: selection payload 0x{payload.ToInt64():X} turned the read down: {why}");
            Diagnostics.Info($"probe:   from +0x{PayloadSong:X}  {words}");
            Diagnostics.Info($"probe:   +0x{PayloadSong:X} id={Memory.I64(payload + PayloadSong)} " +
                             $"name='{Memory.Text(Memory.Ptr(payload + PayloadSong + Offsets.Song.BaseName), 128) ?? "(not a string)"}' " +
                             $"charts=0x{Memory.Ptr(payload + PayloadSong + SongInfoCharts).ToInt64():X} " +
                             $"diff-bytes={Memory.U8(payload + PayloadDifficulty):X2} " +
                             $"{Memory.U8(payload + PayloadDifficulty + 1):X2} " +
                             $"{Memory.U8(payload + PayloadDifficulty + 2):X2} " +
                             $"{Memory.U8(payload + PayloadDifficulty + 3):X2} (as a byte, then as an int)");
#endif
        }

#if DEBUG
        private static bool _refused;
#endif

        /// <summary>`SongSelectScene._sr` — resolved with the payload, see the resolve site.</summary>
        private static int SelectedSong = Offsets.Unresolved;

        /// <summary>`SongSelectScene._Sr` — the difficulty the player has selected, on the scene itself.</summary>
        private static int SceneDifficulty = Offsets.Unresolved;

        // ---- the difficulty the game itself is on, off the save ----
        //
        // Four offsets, all asked by name: `_NH._DEb` (the container), the container's own `_iEb`
        // (its object field — the class is generic, so only the instance's class has a layout), then
        // `SingleFileSaveDataV2.GeneralSaveState` and `GeneralSaveStateV5.LastSelectedDifficulty`.
        // The route is the game's own getter for that field, `_NH._gOA`, which is
        // `[container + 0x10] + 0x38`; see `FromSave` for what it is for.

        private static int SaveContainer = Offsets.Unresolved;
        private static int SaveObject = Offsets.Unresolved;
        private static int SaveState = Offsets.Unresolved;
        private static int SaveDifficulty = Offsets.Unresolved;
        private static IntPtr _saveClass;
        private static bool _saveMissing;

        private static IntPtr _songSelectClass;

        // ---- the selection the game is applying, taken from the call that applies it ----

        private static int _applied = -1;

        private static ushort _appliedSong;

        /// <summary>
        /// The difficulty the game last applied, 0..3, or -1 before the first one.
        ///
        /// <para>
        /// This is the argument of the call that leads to a picture being asked for, so it is both
        /// the freshest answer and the only one available that early. It ranks above the two older
        /// sources because of what each is: the payload is the play handler's record of "what is
        /// about to be played" — correct when entering a chart, and on this screen the last thing
        /// that was played, which is a different difficulty the moment the player switches; and the
        /// scene field has the right value but sits behind a live scene pointer, which is the thing
        /// that is not yet available on the screen's first draw.
        /// </para>
        /// <para>
        /// Written by both screens that apply one — the song select through `_MN`, which also names
        /// the song, and the pack screen through `PackVisualMemberLarge._vc`, where there is no song
        /// and each card carries its own. Whichever applied last is the answer, which is what "the
        /// difficulty the game is on" means when the two screens disagree for a moment.
        /// </para>
        /// <para>
        /// Called before the game's own body runs, which is what makes it usable by the pictures
        /// that body asks for.
        /// </para>
        /// </summary>
        /// <summary>
        /// Records the selection, and answers whether its difficulty is one the last call did not
        /// have.
        ///
        /// The answer is computed <b>here</b> rather than by a caller reading the field afterwards,
        /// and that is not a style choice: the field is written on the line below the comparison, so
        /// anything asking "did this change?" after the call is comparing the new value with itself.
        /// The first version of this did exactly that, the counter that reports how often the reload
        /// fires stayed at zero for a whole run, and one run was enough to say which of the two it
        /// was — the same shape as reading a loop's exit value after the loop.
        /// </summary>
        internal static bool Applying(IntPtr scene, ushort songId, byte difficultyFlag)
        {
            if (IndexOf(difficultyFlag) < 0) return false;   // not one of the four: whatever was applied last stands

            bool changed = ApplyingDifficulty(difficultyFlag);
            _appliedSong = songId;

            // The scene as well, so the fallback below has an instance to check against: the other
            // hook that records it is asked later in this same call, and a picture asked for before
            // that had nothing to read.
            if (Memory.LooksLikeObject(scene)) Hooks.SongSelect = scene;

            return changed;
        }

        /// <summary>
        /// Records a difficulty the game is applying, wherever it is applying one, and answers whether
        /// it is a different one from the last.
        ///
        /// The pack screen goes through here too, and it has no song to record beside it: its cards
        /// each carry their own difficulty, so the only thing the pictures need from that screen is
        /// which one is in force. Both callers get the same answer to "did this change?", which each
        /// of them uses for its own reload — the song select's picture gate, and the pack screen's
        /// cards, which the game does not rebuild by itself.
        /// </summary>
        internal static bool ApplyingDifficulty(byte difficultyFlag)
        {
            int index = IndexOf(difficultyFlag);
            if (index < 0) return false;   // not one of the four: whatever was applied last stands

            bool changed = index != _applied;
            _applied = index;
            return changed;
        }

        /// <summary>
        /// The value the selected-song field is given to make the game reload the pictures.
        ///
        /// A song id is an index into the song array — tens, not thousands — so anything above the
        /// array can never be one, and the comparison the game makes cannot be satisfied by accident.
        /// </summary>
        private static ushort Shadow(ushort songId) => (ushort)(songId ^ 0xFFFF);

        /// <summary>
        /// Leaves the selected-song field reading as changed, when that is the only way the picture
        /// this selection needs will be asked for again. True when it wrote.
        ///
        /// <para>
        /// The game reloads the selection's pictures only when the <b>song</b> changed — the gate is
        /// a comparison of the selected-song field against the one being applied — so a difficulty
        /// change alone leaves the song-select background showing the difficulty the song was first
        /// drawn at. Writing a value no song can hold makes that comparison true for the length of
        /// the call, so the game takes its own reload path with the arguments it was already given.
        /// </para>
        /// <para>
        /// Three cases are left alone on purpose. A difficulty that did not change needs no reload.
        /// A song that did change already takes the reload path. And a field that does not match the
        /// song being applied is a call this mod does not understand well enough to touch — the
        /// scene is mid-something, and the game's own path will run regardless.
        /// </para>
        /// </summary>
        internal static bool ShadowUnlessSongChanged(IntPtr scene, ushort songId, bool difficultyChanged)
        {
            if (!difficultyChanged) return false;
            if (!Memory.LooksLikeObject(scene)) return false;
            if (!Fields()) return false;
            if (Memory.U16(scene + SelectedSong) != songId) return false;

            Memory.WriteU16(scene + SelectedSong, Shadow(songId));
            return true;
        }

        /// <summary>
        /// Puts the selected-song field back, if the game did not.
        ///
        /// The game writes it itself on the path this is aiming at, so the usual case here is a
        /// no-op. It does not on the path where the song could not be found, and that path leaves the
        /// shadow behind — which would leave the field naming a song that does not exist.
        /// </summary>
        internal static void Unshadow(IntPtr scene, ushort songId)
        {
            if (!Memory.LooksLikeObject(scene)) return;
            if (!Fields()) return;
            if (Memory.U16(scene + SelectedSong) != Shadow(songId)) return;

            Memory.WriteU16(scene + SelectedSong, songId);
        }

        /// <summary>The song the last applied selection named. Reported by the probe.</summary>
        internal static int AppliedSong => _appliedSong;

        /// <summary>The difficulty it named, 0..3, or -1 before the first one. Reported by the probe.</summary>
        internal static int AppliedDifficulty => _applied;

        /// <summary>
        /// The selected difficulty as 0..3, or -1 when there is not one.
        ///
        /// Four sources, in order, and the order is the whole of it: the selection the game last
        /// applied (above), the save's own current difficulty, the payload, and the scene.
        /// <see cref="Applying"/> says why the first one wins; <see cref="FromSave"/> says what the
        /// second one is for — it is the answer the game's own screens use, and the only one that
        /// exists on the hub before anything has been applied.
        /// </summary>
        internal static int Difficulty()
        {
            if (_applied >= 0) return _applied;

            int saved = FromSave();
            if (saved >= 0) return saved;

            if (TryRead(out IntPtr _, out int difficulty)) return difficulty;

            return FromScene();
        }

        /// <summary>
        /// The difficulty the game itself is on, read off the save.
        ///
        /// <para>
        /// The value lives in `GeneralSaveStateV5.LastSelectedDifficulty`, and the game's own screens
        /// read it through <c>_NH._CoA()</c>: the song select takes its difficulty from there when it
        /// opens, the pack screen applies it on entry, and — the reason this exists — the hub filters
        /// the songs its random jacket is drawn from by it before asking for a picture. That last one
        /// is the hole this fills: on the hub, in a run where nothing has been applied yet, the other
        /// three sources have no answer (the selection record starts empty, the payload is written by
        /// the play handler, and the scene is not up), so a song with a picture per difficulty showed
        /// none. The save is loaded from the first frame, so it answers there.
        /// </para>
        /// <para>
        /// It ranks below the applied selection on purpose: a difficulty the player just switched to
        /// is applied before the game writes it back, so the record is the fresher of the two.
        /// </para>
        /// </summary>
        private static int FromSave()
        {
            if (_saveMissing) return -1;

            // The class is remembered, not asked again: this runs from the jacket path while nothing
            // has been applied yet — the hub's first draws — and the lookup behind it allocates.
            if (_saveClass == IntPtr.Zero) _saveClass = FieldResolver.ClassPointer("_NH");
            if (_saveClass == IntPtr.Zero) return -1;   // not yet, the same as everywhere else

            IntPtr statics = FieldResolver.Statics(_saveClass);
            if (statics == IntPtr.Zero) return -1;

            IntPtr save = Memory.Ptr(statics);
            if (!Memory.LooksLikeObject(save)) return -1;

            if (SaveContainer == Offsets.Unresolved)
            {
                SaveState = FieldResolver.Field("SingleFileSaveDataV2", "GeneralSaveState");
                SaveDifficulty = FieldResolver.Field("GeneralSaveStateV5", "LastSelectedDifficulty");
                SaveContainer = FieldResolver.Field("_NH", "_DEb");

                if (SaveState < 0 || SaveDifficulty < 0 || SaveContainer < 0)
                {
                    _saveMissing = true;
                    Diagnostics.Error("the save's difficulty could not be read by name in this build; " +
                                      "which difficulty is selected will not be known before the " +
                                      "song select has been visited");
                    return -1;
                }
            }

            IntPtr container = Memory.Ptr(save + SaveContainer);
            if (!Memory.LooksLikeObject(container)) return -1;

            if (SaveObject == Offsets.Unresolved)
            {
                // `_iEb` is on `_NH._OH<T>` — a generic, whose fields the dump renders at 0x0 and
                // whose definition has no layout to ask about. Asked of the instance's own class,
                // the same route `CustomResults` takes for the same reason.
                SaveObject = FieldOn(container, "_iEb");
                if (SaveObject < 0) { _saveMissing = true; return -1; }
            }

            IntPtr data = Memory.Ptr(container + SaveObject);
            if (!Memory.LooksLikeObject(data)) return -1;

            return IndexOf(Memory.U8(data + SaveState + SaveDifficulty));
        }

        /// <summary>
        /// A field's offset on one instance's own class, asked by name, or -1 — the shape
        /// `CustomResults.FieldOn` uses, for the same reason: a generic's field has no offset
        /// anywhere the dump or a definition can answer.
        /// </summary>
        private static int FieldOn(IntPtr instance, string name)
        {
            IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(instance);
            if (klass == IntPtr.Zero) { Diagnostics.Error($"{name}: no class to ask in this build"); return -1; }

            IntPtr field = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(klass, name);
            if (field == IntPtr.Zero) { Diagnostics.Error($"{name}: no field by that name in this build"); return -1; }

            int offset = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(field);
            if (offset <= 0) { Diagnostics.Error($"{name}: no field by that name in this build"); return -1; }

            return offset;
        }

        /// <summary>
        /// The difficulty off the song-select scene.
        ///
        /// <see cref="Hooks.SongSelect"/> is remembered by a hook, so it is only a valid object while
        /// the screen it belongs to is the one on show. The class pointer is the cheap check for
        /// that; a byte that is not one of the four flags reads as "unknown" anyway, which is what
        /// the caller already knows how to handle.
        /// </summary>
        private static int FromScene()
        {
            IntPtr scene = Hooks.SongSelect;
            if (scene == IntPtr.Zero) return -1;

            // Not remembered on failure: the answer is re-asked while it is zero, the same as the
            // payload above, so a call before the interop is ready does not settle anything.
            if (_songSelectClass == IntPtr.Zero)
                _songSelectClass = FieldResolver.ClassPointer("SongSelectScene");
            if (_songSelectClass == IntPtr.Zero) return -1;

            if (Memory.Ptr(scene) != _songSelectClass) return -1;

            // The field read below is resolved with the payload's own shape (`Fields`), and not
            // every way in here goes through it: `Difficulty` reaches this after `TryRead` may have
            // turned the read down at `Offsets.Ready` — before the payload address was ever asked
            // for — and after `Fields` itself may have failed. Either way the field is still -1,
            // which would aim the read one byte before the scene object. Asked here; a miss is
            // reported where it is asked, and "no difficulty" is the answer every caller handles.
            if (!Fields()) return -1;

            return IndexOf(Memory.U8(scene + SceneDifficulty));
        }

        /// <summary>
        /// The five offsets this file reads, asked of the running game once, and whether all of them
        /// answered.
        ///
        /// A miss is reported by <see cref="FieldResolver.Field"/> and leaves its offset at -1; this
        /// then answers false and latches, and every reader and writer here refuses. Refusing is the
        /// answer the ruling on this feature already gives: a difficulty that cannot be read means
        /// the game draws its own picture — never a read, and never a write, at an offset that is
        /// not there.
        /// </summary>
        private static bool Fields()
        {
            if (_fieldsMissing) return false;
            if (_fieldsAsked) return true;

            // Not a failure: the interop may not be up yet, and the answer is re-asked while it is
            // not — the same rule the payload address used to follow on its own.
            IntPtr klass = FieldResolver.ClassPointer("SongSelectScene");
            if (klass == IntPtr.Zero) return false;

            IntPtr statics = FieldResolver.Statics(klass);
            if (statics == IntPtr.Zero) return false;

            // The payload's own shape, asked of the game once, alongside the static it lives in.
            PayloadSong = FieldResolver.Field("_nG", "_LYA");
            PayloadDifficulty = FieldResolver.Field("_nG", "_mYA");
            SelectedSong = FieldResolver.Field("SongSelectScene", "_sr");
            SceneDifficulty = FieldResolver.Field("SongSelectScene", "_Sr");
            PayloadField = FieldResolver.Field("SongSelectScene", "_xr");

            if (PayloadSong < 0 || PayloadDifficulty < 0 || SelectedSong < 0 ||
                SceneDifficulty < 0 || PayloadField < 0)
            {
                _fieldsMissing = true;
                Diagnostics.Error("SongSelectScene's payload could not be read by name in this build; " +
                                  "which difficulty is selected will not be known, so a song with a " +
                                  "picture per difficulty will not show one");
                return false;
            }

            _fieldsAsked = true;
            _payload = statics + PayloadField;
            return true;
        }

        /// <summary>The address of the payload, or zero — see <see cref="Fields"/>.</summary>
        private static IntPtr Payload() => Fields() ? _payload : IntPtr.Zero;

        /// <summary>A `ChartDifficultyFlag` as an index into the four difficulty slots, or -1.</summary>
        private static int IndexOf(byte flag)
        {
            for (int i = 0; i < DifficultyFlags.Length; i++)
                if (DifficultyFlags[i] == flag) return i;
            return -1;
        }
    }
}
