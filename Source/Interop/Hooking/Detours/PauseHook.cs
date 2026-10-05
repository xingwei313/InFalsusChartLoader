using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MelonLoader.NativeUtils;

namespace InFalsusChartLoader
{
    internal static unsafe partial class Hooks
    {
        /// <summary>
        /// `_Dg._hiA(double at)` and `_Dg._KiA(float volume, double time)` — the scene's own music
        /// being paused and taken up again.
        ///
        /// `_Dg` wraps the current scene's music: `_miA()` is the clock the gameplay reads every
        /// frame, and these are the two calls a pause is made of. `_hiA` pauses it — storing the
        /// position it was held at — and its only caller in the build is `GameScene._Vk`, the pause
        /// entry the Escape handler runs.
        ///
        /// The resume is not one call but a scheduled one: pressing resume goes through
        /// `GameScene._Bl` → `_Dg._HiA(now + 3 s)` — a *timestamp* three seconds out, not a start —
        /// and the countdown coroutine `GameScene._tB` polls until that moment and then fades the
        /// music back in through `_KiA`, the call the music actually comes back on. So the clip
        /// follows `_KiA`, not `_HiA`: hooking the schedule would have run the picture through the
        /// whole countdown while the song was still held.
        ///
        /// `_KiA` is a general fade — the settlement fades the music out with it too — so the detour
        /// reports every call while the clip's own half acts only on the one that answers a hold:
        /// <see cref="VideoBackground.Resume"/> does nothing unless the clip was actually held by
        /// <see cref="VideoBackground.Hold"/>, which is what keeps a song-end fade from being read
        /// as a resume.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void MusicPauseFn(IntPtr self, double at, IntPtr methodInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void MusicResumeFn(IntPtr self, float volume, double time, IntPtr methodInfo);

        private static NativeHook<MusicPauseFn> _musicPause;
        private static MusicPauseFn _musicPauseTramp;
        private static NativeHook<MusicResumeFn> _musicResume;
        private static MusicResumeFn _musicResumeTramp;

        private static bool InstallPause()
        {
            IntPtr pause = MethodResolver.ByName("_Dg", "_hiA");
            if (pause == IntPtr.Zero) return false;

            IntPtr resume = MethodResolver.ByName("_Dg", "_KiA");
            if (resume == IntPtr.Zero) return false;

            byte[] pausePrologue = Prologue(pause);
            _musicPause = new NativeHook<MusicPauseFn>
            {
                Target = pause,
                Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, double, IntPtr, void>)&MusicPauseDetour,
            };
            _musicPause.Attach();
            _musicPauseTramp = _musicPause.Trampoline;
            Diagnostics.Info("_Dg._hiA hooked");

            byte[] resumePrologue = Prologue(resume);
            _musicResume = new NativeHook<MusicResumeFn>
            {
                Target = resume,
                Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, float, double, IntPtr, void>)&MusicResumeDetour,
            };
            _musicResume.Attach();
            _musicResumeTramp = _musicResume.Trampoline;
            Diagnostics.Info("_Dg._KiA hooked");
            return Landed(Hook.MusicPause, pause, pausePrologue) & Landed(Hook.MusicResume, resume, resumePrologue);
        }

        private static void DetachPause()
        {
            _musicResume?.Detach();
            _musicResume = null;
            _musicResumeTramp = null;

            _musicPause?.Detach();
            _musicPause = null;
            _musicPauseTramp = null;
        }

        /// <summary>
        /// The game paused its music: hold this mod's clip with it.
        ///
        /// Nothing about the call is changed — the music is paused exactly as it was going to be —
        /// and the clip's own half does nothing unless a chart of this mod's is the one running:
        /// <see cref="VideoBackground.Hold"/> no-ops for a shipped song or a custom one that named
        /// no `background`. `at` — the position the music is held at — goes with the call, and is
        /// what the resume's reading compares the clip against.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void MusicPauseDetour(IntPtr self, double at, IntPtr methodInfo)
        {
            MusicPauses++;
            _musicPauseTramp(self, at, methodInfo);

            if (!Faulted)
            {
                try
                {
                    VideoBackground.Hold(at);
                }
                catch (Exception e)
                {
                    Fault(Hook.MusicPause, e);
                }
            }
        }

        /// <summary>
        /// The game took its music up again: set this mod's clip running again — unless the frame can
        /// do it better, which it usually can.
        ///
        /// The one rule is that a fade which is not answered by a hold does nothing — a fade before
        /// any chart of this mod's has started must not start one, a song-end fade must not restart
        /// a running clip, and one after `Restart` (which clears the hold) is not a resume; see
        /// <see cref="VideoBackground.Resume"/>, whose `atMusicReturn` says this road is the fallback
        /// one: `time` — the seconds the music has still to go before it is scheduled back — is a
        /// second or less, and the picture is meant to wait that second out (the chart clock, which
        /// the frame reads, is what tells it the song is back level with it).
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void MusicResumeDetour(IntPtr self, float volume, double time, IntPtr methodInfo)
        {
            MusicResumes++;
            _musicResumeTramp(self, volume, time, methodInfo);

            if (!Faulted)
            {
                try
                {
                    VideoBackground.Resume(atMusicReturn: true);
                }
                catch (Exception e)
                {
                    Fault(Hook.MusicResume, e);
                }
            }
        }
    }
}
