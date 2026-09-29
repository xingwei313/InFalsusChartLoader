using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MelonLoader.NativeUtils;

namespace InFalsusChartLoader
{
    internal static unsafe partial class Hooks
    {
        /// <summary>public static _R _VA(string) — the chart loader, by name.</summary>
        private const long RvaVa = 0x53DB30;

        /// <summary>
        /// `_s._VA(string name) -> _R`
        ///
        /// `_R` is 40 bytes, so it comes back through a hidden return buffer: rcx on the way in, rax
        /// on the way out. The caller reads rax, which is why <see cref="VaDetour"/> returns the same
        /// pointer it was given rather than returning void.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr VaFn(IntPtr result, IntPtr name, IntPtr methodInfo);

        private static NativeHook<VaFn> _va;
        private static VaFn _vaTramp;

        private static bool InstallChartLoad()
        {
            IntPtr target = MethodResolver.ByName("_s", "_VA", RvaVa);
            if (target == IntPtr.Zero) return false;

            byte[] prologue = Prologue(target);
            _va = new NativeHook<VaFn>
            {
                Target = target,
                Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr>)&VaDetour,
            };
            _va.Attach();
            _vaTramp = _va.Trampoline;
            Diagnostics.Info("_s._VA hooked");
            return Landed(Hook.ChartLoader, target, prologue);
        }

        private static void DetachChartLoad()
        {
            _va?.Detach();
            _va = null;
            _vaTramp = null;
        }

        /// <summary>
        /// Serves this mod's charts and passes everything else through untouched.
        ///
        /// The pass-through is the ordinary case and has to stay exactly that: `_VA` is the only way
        /// any chart is loaded, and its own not-found path throws inside a coroutine. Anything this
        /// hook changes about a call that is not its own changes where the game can load a chart from.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr VaDetour(IntPtr result, IntPtr name, IntPtr methodInfo)
        {
            VaCalls++;

            if (!Faulted)
            {
                try
                {
                    if (ChartCatalog.TryServe(name, result)) { VaServed++; return result; }
                }
                catch (Exception e)
                {
                    Fault(Hook.ChartLoader, e);
                }
            }

            return _vaTramp(result, name, methodInfo);
        }
    }
}
