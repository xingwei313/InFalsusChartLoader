using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MelonLoader.NativeUtils;

namespace InFalsusChartLoader
{
    internal static unsafe partial class Hooks
    {
        /// <summary>
        /// `SongSelectScene._MN(SongId, ChartDifficultyFlag, _VA) -> bool` — apply a selection.
        ///
        /// The song select's one funnel: every path that changes what is selected — a card click, the
        /// left/right arrows, a difficulty button, the scene's own first draw — ends up here with the
        /// song and the difficulty it wants shown. That makes it the only place where the two are
        /// still known <i>at the moment they are applied</i>, which is what the pictures need.
        /// </summary>
        // Prefixed because `Hooks` is a partial class and the jacket's file already has an `Image`
        // and a `Namespace` — a different image, so the names have to say which is which.
        private const string SceneImage = "Game.dll";

        private const string SceneNamespace = "ifapp.Game.Scenes";

        /// <summary>Selections applied. Counted rather than logged: this runs on every repaint.</summary>
        internal static long ApplyCalls;

        /// <summary>
        /// Times the difficulty changed without the song changing, and the picture was reloaded
        /// anyway. The count that says the fix is doing something.
        /// </summary>
        internal static long ApplyReloaded;

        /// <summary>
        /// `SongSelectScene._MN(SongId songId, ChartDifficultyFlag difficulty, _VA transition)`.
        ///
        /// The third parameter is the transition the caller wants: it reaches
        /// `LargeSongCard._OS` unchanged, where 0 swaps the material with no animation and 1 and 2
        /// run one of the two slide tweens. It is left as an `int` and forwarded untouched — it is
        /// the tempting lever, because the same gate that decides whether the pictures are reloaded
        /// also tests it, and pulling it would slide the card in on every difficulty change. See
        /// <see cref="MnDetour"/> for what is done instead.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte ApplyFn(IntPtr self, ushort songId, byte difficulty, int transition,
                                      IntPtr methodInfo);

        private static NativeHook<ApplyFn> _apply;
        private static ApplyFn _applyTramp;

        private static bool InstallSongSelect()
        {
            // By runtime walk, like `_sN` on the same class: the store route answers zero for some of
            // this class's methods, and a zero there falls back to a pinned address silently.
            IntPtr target = MethodResolver.ByRuntime("SongSelectScene", "_MN", SceneImage, SceneNamespace);
            if (target == IntPtr.Zero)
            {
                Diagnostics.Warn("SongSelectScene._MN could not be resolved; a song with a picture per " +
                                 "difficulty will keep showing the one it was first drawn with");
                return false;
            }

            byte[] prologue = Prologue(target);
            _apply = new NativeHook<ApplyFn>
            {
                Target = target,
                Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ushort, byte, int, IntPtr, byte>)&MnDetour,
            };
            _apply.Attach();
            _applyTramp = _apply.Trampoline;
            Diagnostics.Info("SongSelectScene._MN hooked");
            return Landed(Hook.SongSelectApply, target, prologue);
        }

        private static void DetachSongSelect()
        {
            _apply?.Detach();
            _apply = null;
            _applyTramp = null;
        }

        /// <summary>
        /// Records the selection the game is about to apply, and — when only the difficulty changed —
        /// leaves the selected song reading as changed for the length of the call.
        ///
        /// <para>
        /// Two problems, one call. The first is that the pictures this leads to are asked for by
        /// <b>difficulty</b>, and the two places this mod could read one — the payload the play
        /// handler writes, and the scene's own difficulty field — are either not written yet or not
        /// reachable yet at that moment. This detour is handed the difficulty, so it takes it from
        /// there; see <see cref="Selection"/>.
        /// </para>
        /// <para>
        /// The second is that the game reloads the selection's pictures only when the <b>song</b>
        /// changed — a difficulty change does not reload them, so the song-select background keeps the
        /// picture of the difficulty the song was first drawn at. What follows fixes that from the
        /// game's own side rather than by reimplementing any part of it: the gate is a comparison
        /// against the selected-song field, so that field is set to a value no song can have for the
        /// duration of the call. The game then takes its reload path with the arguments it was
        /// already given, and writes the field back itself.
        /// </para>
        /// <para>
        /// What is deliberately <i>not</i> touched is the fourth argument. It looks like the obvious
        /// lever — the same gate tests it — but it also selects a transition animation on the large
        /// card, so forcing it would make every difficulty change slide the card in. Nothing else in
        /// the body reads either, which is why the shadowed field gives the reload alone.
        /// </para>
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte MnDetour(IntPtr self, ushort songId, byte difficulty, int transition,
                                     IntPtr methodInfo)
        {
            ApplyCalls++;

            bool shadowed = false;
            if (!Faulted)
            {
                try
                {
                    // Before the game's body runs, because the jacket request this leads to happens
                    // inside it and has to see the new answer. The answer to "is this a difficulty
                    // the last call did not have" comes back from the same call: it is the field
                    // the recording writes, and reading it afterwards compares it with itself.
                    bool changed = Selection.Applying(self, songId, difficulty);

                    shadowed = Selection.ShadowUnlessSongChanged(self, songId, changed);
                    if (shadowed) ApplyReloaded++;
                }
                catch (Exception e)
                {
                    Fault(Hook.SongSelectApply, e);
                }
            }

            byte applied = _applyTramp(self, songId, difficulty, transition, methodInfo);

            // Outside the try, like every other detour here: the game's call is made whatever
            // happened above. The restore is after it because the field has to stay shadowed for the
            // whole call.
            if (shadowed) Selection.Unshadow(self, songId);

            return applied;
        }
    }
}
