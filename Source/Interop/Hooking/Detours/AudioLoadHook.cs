using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MelonLoader.NativeUtils;

namespace InFalsusChartLoader
{
    internal static unsafe partial class Hooks
    {
        // The three methods this file hooks, each resolved by name:
        //   _ZgA  private void _ZgA(record)                 — the FMOD servicing thread's per-read handler
        //   _bGA  private void _bGA(record, uint result)    — hands the filled buffer to the native transform
        //   _DJA  public bool _DJA(string, out int index)   — name to StreamingAssets index

        /// <summary>
        /// `_J._hF._ZgA(record, _)`
        ///
        /// The read record, as the decompilation lays it out: <c>record[0]</c> points at the info
        /// block and <c>record[1]</c> is the file handle; <c>info+8</c> is the read offset,
        /// <c>info+12</c> the byte count, <c>info+32</c> the destination buffer, and <c>info+40</c>
        /// where the number of bytes written is reported back.
        ///
        /// The handle is what makes this affordable: it is
        /// <c>index | length &lt;&lt; 24 | kind &lt;&lt; 56</c>, so the file being read is in the
        /// argument and this hook never has to recognise a reader or track which song is playing.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ZgAFn(IntPtr record, IntPtr unused);

        /// <summary>
        /// `_BG._DJA(string name, out int index) -> bool` — the name lookup every StreamingAssets
        /// read starts with, charts included, so the body is a compare and a pass-through.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte DjaFn(IntPtr self, IntPtr name, IntPtr index);

        /// <summary>`_J._hF._bGA(record, result)`. Called to finish a read this mod served itself.</summary>
        internal static delegate* unmanaged[Cdecl]<IntPtr, uint, void> CompleteRead;

        private static NativeHook<ZgAFn> _zga;
        private static ZgAFn _zgaTramp;
        private static NativeHook<DjaFn> _dja;
        private static DjaFn _djaTramp;

        private static bool InstallAudioLoad()
        {
            // `_hF`, not `_J._hF`: the interop index is keyed by the type's own name, and a nested
            // type's name does not carry its parent. Asking for the qualified name finds nothing —
            // and now that there is no RVA to fall back to, that is an Error naming the method and no
            // hook at all, instead of a patch at a pinned address that happened to work once.
            IntPtr zgA = MethodResolver.ByName("_hF", "_ZgA");
            if (zgA == IntPtr.Zero) return false;

            IntPtr bga = MethodResolver.ByName("_hF", "_bGA");
            if (bga == IntPtr.Zero) return false;
            CompleteRead = (delegate* unmanaged[Cdecl]<IntPtr, uint, void>)bga;

            byte[] zgAPrologue = Prologue(zgA);
            _zga = new NativeHook<ZgAFn>
            {
                Target = zgA,
                Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&ZgADetour,
            };
            _zga.Attach();
            _zgaTramp = _zga.Trampoline;
            Diagnostics.Info("_J._hF._ZgA hooked");

            IntPtr dja = MethodResolver.ByName("_BG", "_DJA");
            if (dja == IntPtr.Zero) return false;

            byte[] djaPrologue = Prologue(dja);
            _dja = new NativeHook<DjaFn>
            {
                Target = dja,
                Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte>)&DjaDetour,
            };
            _dja.Attach();
            _djaTramp = _dja.Trampoline;
            Diagnostics.Info("_BG._DJA hooked");
            return Landed(Hook.AudioRead, zgA, zgAPrologue) & Landed(Hook.AudioName, dja, djaPrologue);
        }

        private static void DetachAudioLoad()
        {
            _dja?.Detach();
            _dja = null;
            _djaTramp = null;

            _zga?.Detach();
            _zga = null;
            _zgaTramp = null;
            CompleteRead = null;
        }

        /// <summary>Serves this mod's audio reads and passes every other read through untouched.</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void ZgADetour(IntPtr record, IntPtr unused)
        {
            ZgaCalls++;

            if (!Faulted)
            {
                try
                {
#if DEBUG
                    // Before anything decides: the game is about to fail this read without reading
                    // it if its block is in the cancelled set. Counted, not acted on -- see
                    // PendingReads.Observe. This is the measurement of the whole mechanism, so it
                    // has to sit ahead of this mod's own handling and change nothing.
                    PendingReads.Observe(record);
#endif
                    if (AudioCatalog.TryServe(record)) { ZgaServed++; return; }
                }
                catch (Exception e)
                {
                    Fault(Hook.AudioRead, e);
                }
            }

            _zgaTramp(record, unused);
        }

        /// <summary>
        /// Answers for this mod's audio names and passes every other name through untouched.
        ///
        /// This is the hottest of the three hooks — every StreamingAssets lookup in the game goes
        /// through it — so the body stays a string compare and a dictionary miss.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte DjaDetour(IntPtr self, IntPtr name, IntPtr index)
        {
            DjaCalls++;

            if (!Faulted)
            {
                try
                {
                    if (AudioCatalog.TryResolve(name, index)) { DjaHits++; return 1; }
                }
                catch (Exception e)
                {
                    Fault(Hook.AudioName, e);
                }
            }

            return _djaTramp(self, name, index);
        }
    }
}
