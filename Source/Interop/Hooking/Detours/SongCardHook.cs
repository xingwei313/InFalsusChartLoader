using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MelonLoader.NativeUtils;

namespace InFalsusChartLoader
{
    internal static unsafe partial class Hooks
    {
        /// <summary>
        /// `SongSelectScene._Jc._Lo`'s sibling: the one that builds a song card and appends it to the
        /// song-select's display list.
        ///
        /// Not a hook this mod needs to *do* anything with — nothing is substituted here. It is the
        /// only place that can answer, from a run, whether a custom song reached the song list: the
        /// list is built by a filter that is several optimised branches deep, and whether a song of
        /// this mod's survives it cannot be read off the decompilation with confidence. Counting the
        /// cards built for it says the same thing without the ambiguity.
        /// </summary>
        private const long RvaSongCard = 0x73EA50;

        /// <summary>
        /// `void _Go(SongSelectScene self, Il2CppObject* a, SongInfo* b, _Kc* c, MethodInfo*)`.
        ///
        /// The second argument is left as a pointer rather than the `int` its first four bytes look
        /// like, so that this hook can say what it actually received when the third one turns out not
        /// to be the song record after all.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr SongCardFn(IntPtr self, IntPtr second, IntPtr songInfo, IntPtr extra);

        private static NativeHook<SongCardFn> _card;
        private static SongCardFn _cardTramp;

        /// <summary>Cards appended to the song-select list, and how many of them are this mod's.</summary>
        internal static long CardsBuilt, CardsOurs;

        private static bool InstallSongCard()
        {
            // By RVA only: the method is reached through a nested closure class, whose name is not
            // something to look up, and the address is pinned to this build either way.
            IntPtr target = GameAssembly.FromRva(RvaSongCard);
            if (target == IntPtr.Zero)
            {
                Diagnostics.Warn($"could not resolve the song card builder (RVA 0x{RvaSongCard:X}); " +
                                 "is GameAssembly.dll loaded?");
                return false;
            }

            byte[] prologue = Prologue(target);
            _card = new NativeHook<SongCardFn>
            {
                Target = target,
                Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&SongCardDetour,
            };
            _card.Attach();
            _cardTramp = _card.Trampoline;
            Diagnostics.Info("song card builder hooked");
            return Landed("the song card builder", target, prologue);
        }

        private static void DetachSongCard()
        {
            _card?.Detach();
            _card = null;
            _cardTramp = null;
        }

        /// <summary>
        /// `SongSelectScene._sN()` — the song-select's "may this be started" predicate.
        ///
        /// The play handler (`_TN`) reads it and only calls `StartCoroutine` when it is true; when it
        /// is false it just repaints the button. So its answer is the whole difference between "the
        /// button does nothing" and "the game starts something and stalls", and it is the one thing
        /// about the entry path that cannot be read off the decompilation — it depends on the player's
        /// save.
        ///
        /// Reported rather than acted on: this mod changes nothing here. `SongSelectScene` and `_sN`
        /// are both real names, so the runtime walk reaches it on any build.
        /// </summary>
        internal static long PlayGateCalls, PlayGateYes;

        /// <summary>
        /// The song-select scene the play gate was last called on.
        ///
        /// Kept because the gate is an <b>instance</b> method, so its first argument is the scene
        /// itself — and the difficulty the player has selected lives in an instance field of that
        /// scene, at `_Sr`, which nothing static reaches. The payload the rest of the mod prefers
        /// does not carry it: read out of a running game, that byte is zero while the song beside it
        /// is correct.
        ///
        /// The gate is not called every frame, but it is called in bursts whenever the screen
        /// repaints, so by the time a card asks for its picture this has been refreshed.
        /// </summary>
        internal static IntPtr SongSelect;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte PlayGateFn(IntPtr self, IntPtr methodInfo);

        private static NativeHook<PlayGateFn> _gate;
        private static PlayGateFn _gateTramp;

        private static bool InstallPlayGate()
        {
            IntPtr target = MethodResolver.ByRuntime("SongSelectScene", "_sN", "Game.dll", "ifapp.Game.Scenes");
            if (target == IntPtr.Zero)
            {
                Diagnostics.Warn("SongSelectScene._sN could not be resolved; whether a song may be " +
                                 "started will not be reported");
                return false;
            }

            byte[] prologue = Prologue(target);
            _gate = new NativeHook<PlayGateFn>
            {
                Target = target,
                Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&PlayGateDetour,
            };
            _gate.Attach();
            _gateTramp = _gate.Trampoline;
            Diagnostics.Info("SongSelectScene._sN hooked");
            return Landed("SongSelectScene._sN", target, prologue);
        }

        private static void DetachPlayGate()
        {
            _gate?.Detach();
            _gate = null;
            _gateTramp = null;
        }

        /// <summary>Answers exactly as the game would, and says what the answer was.</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte PlayGateDetour(IntPtr self, IntPtr methodInfo)
        {
            byte answer = _gateTramp(self, methodInfo);

            PlayGateCalls++;
            if (answer != 0) PlayGateYes++;
            if (Memory.LooksLikeObject(self)) SongSelect = self;

            Diagnostics.Info($"play gate: _sN -> {answer != 0}");

            return answer;
        }

        /// <summary>
        /// `CoreScene._CB.MoveNext` — the scene switch, which is where the game is actually stuck.
        ///
        /// The demo build's source is unambiguous about its shape (`CoreScene.$NH`): it takes the
        /// input away on its first line (`TakeInputControl` + `DisableInputEvents = true`) and gives
        /// it back on its last, and in between it waits on an Addressables
        /// `AsyncOperationHandle&lt;SceneInstance&gt;` for the scene load and on a flag a callback sets
        /// once the scene is live. Stopping inside it is therefore *exactly* "the game stays on the
        /// song-select screen and no UI responds" — which is what is observed — and it also explains
        /// `va=0`, because the chart is only asked for after the switch completes.
        ///
        /// The state field is the `&lt;&gt;1__state` of the generated coroutine at `+0x10`; the value
        /// says which `yield` it is parked at. Reported only — nothing is changed.
        /// </summary>
        internal static long SceneSwitchCalls;
        internal static int SceneSwitchLastState = int.MinValue;

        /// <summary>
        /// The object the coroutine is parked on, and its class's name.
        ///
        /// The state number alone says *where* it stopped and nothing about *why*. Unity only calls
        /// `MoveNext` again when the object a coroutine yielded stops waiting, so which object that is
        /// is the whole of the question — and it is the one fact that changes between a transition
        /// that finishes and one that does not.
        ///
        /// The object is `&lt;&gt;2__current` at `+0x18`, and it is null for a `yield return null` (the
        /// frame loops, which are the transitions that do progress). Only a non-null one is kept, so
        /// what is left is exactly "the thing being waited on".
        /// </summary>
        internal static IntPtr SceneSwitchWait;
        internal static string SceneSwitchWaitName;

        /// <summary>The class of an object, by name, or null.</summary>
        private static string ClassNameOf(IntPtr instance)
        {
            try
            {
                IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(instance);
                if (klass == IntPtr.Zero) return null;
                return Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name_(klass);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// How far along a `_J._Ig` is, when that is what is being waited on.
        ///
        /// `_Ig` is a pooled `CustomYieldInstruction` whose `keepWaiting` is one byte at `+0x10`:
        /// zero until whoever owns it says it is done. Printed only for that class, and only as a
        /// number, because it is the difference between "the game is still working on it" and "it has
        /// been finished and this is not resuming anyway".
        /// </summary>
        internal static string WaitProgress()
        {
            if (SceneSwitchWait == IntPtr.Zero) return null;
            if (SceneSwitchWaitName != "_Ig") return SceneSwitchWaitName;

            return $"_Ig/+0x10={Memory.U8(SceneSwitchWait + 0x10)}";
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte MoveNextFn(IntPtr self, IntPtr methodInfo);

        private static NativeHook<MoveNextFn> _moveNext;
        private static MoveNextFn _moveNextTramp;

        private static bool InstallSceneSwitch()
        {
            // `_CB` is a nested state machine, and the index answers to both `_CB` and `CoreScene._CB`.
            IntPtr target = MethodResolver.ByRuntime("_CB", "MoveNext", "Game.dll", "ifapp.Game.Scenes");
            if (target == IntPtr.Zero)
            {
                Diagnostics.Warn("CoreScene._CB.MoveNext could not be resolved; where the scene switch " +
                                 "stops will not be reported");
                return false;
            }

            byte[] prologue = Prologue(target);
            _moveNext = new NativeHook<MoveNextFn>
            {
                Target = target,
                Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)&MoveNextDetour,
            };
            _moveNext.Attach();
            _moveNextTramp = _moveNext.Trampoline;
            Diagnostics.Info("CoreScene._CB.MoveNext hooked");
            return Landed("CoreScene._CB.MoveNext", target, prologue);
        }

        private static void DetachSceneSwitch()
        {
            _moveNext?.Detach();
            _moveNext = null;
            _moveNextTramp = null;
        }

        /// <summary>Runs the step, then says which state it is now parked in.</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte MoveNextDetour(IntPtr self, IntPtr methodInfo)
        {
            byte more = _moveNextTramp(self, methodInfo);
            SceneSwitchCalls++;

            if (!Memory.LooksLikeObject(self)) return more;

            int state = Memory.I32(self + StateField);
            if (state != SceneSwitchLastState)
            {
                SceneSwitchLastState = state;
                Diagnostics.Info($"scene switch: parked at state {state}{(more == 0 ? " (finished)" : "")}");
            }

            // What it yielded, when it yielded something. A `yield return null` clears this: those are
            // the waits that get resumed every frame, and keeping one would make the probe report a
            // frame loop as though it were a stall.
            //
            // A finished coroutine clears it too. `<>2__current` is not reset when `MoveNext` reports
            // done, so the last object stayed recorded and the probe went on printing it during the
            // minutes the game then sat on a screen doing nothing — which reads as a stall and is not
            // one. Measured: a run that was not stuck printed `wait=_Ig/+0x10=0` for forty seconds at
            // `@-1`, which is exactly the line a real stall produces.
            IntPtr current = more == 0 ? IntPtr.Zero : Memory.Ptr(self + CurrentField);
            if (current == IntPtr.Zero)
            {
                SceneSwitchWait = IntPtr.Zero;
                SceneSwitchWaitName = null;
            }
            else if (current != SceneSwitchWait)
            {
                SceneSwitchWait = current;
                SceneSwitchWaitName = ClassNameOf(current);
                Diagnostics.Info($"scene switch: waiting on {SceneSwitchWaitName ?? "(unnamed)"} " +
                                 $"at 0x{current.ToInt64():X}");
            }

            return more;
        }

        /// <summary>`&lt;&gt;2__current` — the object a generated coroutine is yielding.</summary>
        private const int CurrentField = 0x18;

        /// <summary>`&lt;&gt;1__state` — a generated coroutine's state, at the same offset in all of them.</summary>
        private const int StateField = 0x10;

        /// <summary>Counts the card, then builds it exactly as the game would have.</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr SongCardDetour(IntPtr self, IntPtr second, IntPtr songInfo, IntPtr extra)
        {
            CardsBuilt++;

            if (!Faulted)
            {
                // Deliberately not `Fault`: this is a diagnostic on a hot path, and faulting stops
                // every detour in the mod — which is what happened the first time this ran, halfway
                // through a run. A diagnostic that can disable the mod is worse than no diagnostic.
                //
                // Named per card it was not: the argument that should be the song record reads as
                // zero here, for both the second and the third slot, while the card is built from a
                // real one. So this counts and does not name — and the argument layout wants a proper
                // look at the call site before anything is built on it again.
                try
                {
                    if (JacketCatalog.IsOurs(songInfo)) CardsOurs++;
                }
                catch (Exception e)
                {
                    Diagnostics.Warn($"the song card diagnostic failed: {Diagnostics.Describe(e)}");
                }
            }

            return _cardTramp(self, second, songInfo, extra);
        }
    }
}
