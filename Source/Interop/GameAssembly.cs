using System;
using System.Runtime.InteropServices;

namespace InFalsusChartLoader
{
    /// <summary>
    /// Where the game's native code lives.
    ///
    /// GameAssembly.dll is not relocated (preferred base 0x180000000), so moduleBase + RVA lands on
    /// the function. RVAs come from Il2CppDumper's dump.cs and are pinned to this build; nothing
    /// here can tell that they have gone stale, so whoever uses one falls back to it knowingly.
    ///
    /// The handle comes from `GetModuleHandleW` — "the handle of this module, if it is already
    /// loaded", which is exactly the question, and MelonLoader injects into a running game, so by
    /// the time anything here runs the module is there.
    ///
    /// Nothing is remembered on failure: the answer is re-asked while it is zero, so no caller
    /// depends on being the first one to run. A zero is reported by whoever wanted an address.
    ///
    /// `kernel32` is the one thing in this mod that names a platform outright — everything else is
    /// bound to this build of the game rather than to an OS. Do not replace it with
    /// `Process.GetCurrentProcess().Modules`: that enumerates and allocates a `ProcessModule` for
    /// every module in the process to find one, and drags `System.Diagnostics.Process` and
    /// `System.Collections.NonGeneric` in behind it.
    /// </summary>
    internal static class GameAssembly
    {
        /// <summary>The module the RVAs are relative to.</summary>
        private const string Module = "GameAssembly.dll";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern IntPtr GetModuleHandleW(string moduleName);

        private static IntPtr _base;

        /// <summary>Base address GameAssembly.dll is loaded at, or Zero if it is not loaded.</summary>
        internal static IntPtr Base
        {
            get
            {
                if (_base == IntPtr.Zero) _base = GetModuleHandleW(Module);
                return _base;
            }
        }

        /// <summary>Native address from a dump.cs RVA, relocated by the real module base.</summary>
        internal static IntPtr FromRva(long rva)
        {
            IntPtr b = Base;
            return b == IntPtr.Zero ? IntPtr.Zero : (IntPtr)(b.ToInt64() + rva);
        }
    }
}
