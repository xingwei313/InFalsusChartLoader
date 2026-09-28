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
    /// exactly where the record's size says it should (0x168 + 0x40 = 0x1A8, with the next field at
    /// 0x1A9 — the three offsets check each other).
    /// </para>
    /// <para>
    /// The record is a struct held in the static field, so its address is the field's address — not a
    /// pointer to dereference. That is what makes this cheap enough to call from the jacket path: no
    /// allocation, no lookup, two reads.
    /// </para>
    /// </summary>
    internal static class Selection
    {
        /// <summary>Where the payload sits inside `SongSelectScene`'s statics.</summary>
        private const int StaticPayload = 0x0;

        /// <summary>`_nG._LYA` — the selected song, embedded, so this is its address rather than a pointer to it.</summary>
        private const int PayloadSong = 0x168;

        /// <summary>`_nG._mYA` — the selected difficulty, a one-byte `ChartDifficultyFlag`.</summary>
        private const int PayloadDifficulty = 0x1A8;

        /// <summary>`SongInfo.ChartInfos` — read only to tell a filled payload from a zeroed one.</summary>
        private const int SongInfoCharts = 0x18;

        /// <summary>The four flags, in difficulty order. The same set the chart records are written with.</summary>
        private static readonly byte[] DifficultyFlags = { 1, 2, 4, 8 };

        private static IntPtr _payload;

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

            // The window both offsets sit in, so a payload that is not shaped the way the dump says
            // is visible as bytes instead of inferred from a failed check.
            var words = new System.Text.StringBuilder();
            for (int i = 0; i < 12; i++)
            {
                if (i > 0) words.Append(' ');
                words.Append(Memory.I64(payload + 0x150 + i * 8).ToString("X16"));
            }

            Diagnostics.Info($"probe: selection payload 0x{payload.ToInt64():X} turned the read down: {why}");
            Diagnostics.Info($"probe:   +0x150..  {words}");
            Diagnostics.Info($"probe:   +0x{PayloadSong:X} id={Memory.I64(payload + PayloadSong)} " +
                             $"name='{Memory.Text(Memory.Ptr(payload + PayloadSong + 8), 128) ?? "(not a string)"}' " +
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

        /// <summary>`SongSelectScene._sr` — the selected song, beside the difficulty on the scene.</summary>
        private const int SelectedSong = 0x194;

        /// <summary>`SongSelectScene._Sr` — the difficulty the player has selected, on the scene itself.</summary>
        private const int SceneDifficulty = 0x196;

        private static IntPtr _songSelectClass;

        // ---- the selection the game is applying, taken from the call that applies it ----

        private static int _applied = -1;

        private static ushort _appliedSong;

        /// <summary>
        /// The selection `_MN` was last called with, 0..3, or -1 before the first one.
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
            int index = IndexOf(difficultyFlag);
            if (index < 0) return false;   // not one of the four: whatever was applied last stands

            bool changed = index != _applied;

            _applied = index;
            _appliedSong = songId;

            // The scene as well, so the fallback below has an instance to check against: the other
            // hook that records it is asked later in this same call, and a picture asked for before
            // that had nothing to read.
            if (Memory.LooksLikeObject(scene)) Hooks.SongSelect = scene;

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
        /// Three sources, in order, and the order is the whole of it: the selection the game last
        /// applied (above), the payload, and the scene. See <see cref="Applying"/> for why the first
        /// one wins — the other two are the older answers to the same question, kept because the
        /// selection record is only written once the song select has run.
        /// </summary>
        internal static int Difficulty()
        {
            if (_applied >= 0) return _applied;

            if (TryRead(out IntPtr _, out int difficulty)) return difficulty;

            return FromScene();
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

            return IndexOf(Memory.U8(scene + SceneDifficulty));
        }

        /// <summary>
        /// The address of the payload, or zero.
        ///
        /// Resolved once — the class and its statics block do not move, and this is read from a path
        /// that runs dozens of times whenever the song list repaints. A <b>failure is not
        /// remembered</b>, though: the answer is re-asked while it is zero, so an early call before
        /// the interop has the class does not disable this for the rest of the session.
        /// </summary>
        private static IntPtr Payload()
        {
            if (_payload != IntPtr.Zero) return _payload;

            IntPtr klass = FieldResolver.ClassPointer("SongSelectScene");
            if (klass == IntPtr.Zero)
            {
                Diagnostics.Warn("SongSelectScene could not be found; which difficulty is selected will " +
                                 "not be known, and a song with a picture per difficulty will not show one");
                return IntPtr.Zero;
            }

            IntPtr statics = FieldResolver.Statics(klass);
            if (statics == IntPtr.Zero)
            {
                Diagnostics.Warn("SongSelectScene has no statics block; which difficulty is selected " +
                                 "will not be known");
                return IntPtr.Zero;
            }

            return _payload = statics + FieldResolver.Field("SongSelectScene", "_xr", StaticPayload);
        }

        /// <summary>A `ChartDifficultyFlag` as an index into the four difficulty slots, or -1.</summary>
        private static int IndexOf(byte flag)
        {
            for (int i = 0; i < DifficultyFlags.Length; i++)
                if (DifficultyFlags[i] == flag) return i;
            return -1;
        }
    }
}
