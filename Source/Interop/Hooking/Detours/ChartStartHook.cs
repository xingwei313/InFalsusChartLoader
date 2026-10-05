using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MelonLoader.NativeUtils;

namespace InFalsusChartLoader
{
    internal static unsafe partial class Hooks
    {
        /// <summary>
        /// `GameScene._rk() -> IEnumerator`
        ///
        /// The game's starting sequence handing over to the running chart: every path through
        /// `GameScene._UB.MoveNext` — the one that plays the battle's starting overlay, the one that
        /// plays the meditation one, and the one that plays neither — ends by starting this watcher,
        /// after its waits on that overlay and on the song's own playback have both been satisfied.
        /// Those three call sites are the only ones in the build. So this call <i>is</i> "the chart
        /// starts", once per play, retries included.
        ///
        /// It replaces the anchor this hook used to sit on, `LogicalNotePlayer._Ib` — the chart
        /// reaching the note player. That call happens while the gameplay scene is being put
        /// together, seconds before the game hands play over: the chart is read, the song is started
        /// and the chart is handed on, all before the starting sequence runs its overlay. A clip
        /// started there ran through the whole intro, which is the shape "the video plays the moment
        /// the scene opens, not when the chart does" has. (The round that measured all of this is
        /// `InFalsusChartLoader`'s V22 handoff; this call site is the same seam the game's own
        /// scene-entry sequence ends on.)
        ///
        /// The body is a factory whose result the caller hands straight to `StartCoroutine`, so the
        /// detour returns that result unchanged and the clip is set running just before the watcher
        /// it built begins its first frame.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr RkFn(IntPtr self, IntPtr methodInfo);

        private static NativeHook<RkFn> _rk;
        private static RkFn _rkTramp;

        private static bool InstallChartStart()
        {
            IntPtr target = MethodResolver.ByName("GameScene", "_rk");
            if (target == IntPtr.Zero) return false;

            byte[] prologue = Prologue(target);
            _rk = new NativeHook<RkFn>
            {
                Target = target,
                Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&RkDetour,
            };
            _rk.Attach();
            _rkTramp = _rk.Trampoline;
            Diagnostics.Info("GameScene._rk hooked");
            return Landed(Hook.ChartStart, target, prologue);
        }

        private static void DetachChartStart()
        {
            _rk?.Detach();
            _rk = null;
            _rkTramp = null;
        }

        /// <summary>
        /// Sets this mod's background clip running with the chart.
        ///
        /// Nothing about the call is changed — the watcher is made and handed back exactly as it was
        /// — and the only work is this mod's own video player: <see cref="VideoBackground.Restart"/>
        /// does nothing when no clip of this mod's is loaded, which is every shipped song and every
        /// custom one that named no `background`.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr RkDetour(IntPtr self, IntPtr methodInfo)
        {
            ChartStarts++;

            if (!Faulted)
            {
                try
                {
                    VideoBackground.Restart();
                }
                catch (Exception e)
                {
                    Fault(Hook.ChartStart, e);
                }
            }

            return _rkTramp(self, methodInfo);
        }
    }
}
