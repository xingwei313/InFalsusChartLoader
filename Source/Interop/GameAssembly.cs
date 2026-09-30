using System;
using System.Runtime.InteropServices;

namespace InFalsusChartLoader
{
    /// <summary>
    /// Where the game's native code lives — asked once, and reported by the probe.
    ///
    /// There is deliberately no `FromRva` beside this. This mod used to relocate a handful of pinned
    /// dump.cs addresses against this base; the last one was the song-select card builder, and it
    /// failed the way the others were removed for — the address belonged to a build the game no
    /// longer runs, so the patch landed on whatever now occupied it (a parameterless `_rN()`,
    /// `MISSING.md` D5) and the log still said "hooked". Every target is resolved by name now, and a
    /// name that does not resolve leaves its hook uninstalled instead of patching a guess.
    ///
    /// What is still wanted from this base is the one line the probe prints: whether the module was
    /// found at all. A zero there means nothing this mod does can land, and it is worth knowing that
    /// before a hook count is read as good news.
    ///
    /// The handle comes from `GetModuleHandleW` — "the handle of this module, if it is already
    /// loaded", which is exactly the question, and MelonLoader injects into a running game, so by
    /// the time anything here runs the module is there.
    ///
    /// Nothing is remembered on failure: the answer is re-asked while it is zero, so no caller
    /// depends on being the first one to run.
    ///
    /// `kernel32` is the one thing in this mod that names a platform outright — everything else is
    /// bound to this build of the game rather than to an OS. Do not replace it with
    /// `Process.GetCurrentProcess().Modules`: that enumerates and allocates a `ProcessModule` for
    /// every module in the process to find one, and drags `System.Diagnostics.Process` and
    /// `System.Collections.NonGeneric` in behind it.
    /// </summary>
    internal static class GameAssembly
    {
        /// <summary>The module the game's native code is in.</summary>
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
    }
}
