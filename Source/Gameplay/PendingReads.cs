using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace InFalsusChartLoader
{
    /// <summary>
    /// The game's own record of which file reads are outstanding, and the calls that keep it honest
    /// after this mod has served a read itself.
    ///
    /// <para>
    /// `_hF._ZgA` — the handler the FMOD servicing thread runs for one read — does three things to
    /// two sets. It <b>opens</b> with <c>_hF._VSA.Remove(info)</c>, and a successful read
    /// <b>closes</b> with <c>_hF._vSA.Remove(info)</c> followed by <c>_hF._VSA.Remove(info)</c>.
    /// This mod answers that handler itself for its own files, so none of the three ran, and every
    /// read it served left its info block behind in <c>_vSA</c>.
    /// </para>
    /// <para>
    /// Neither set is scratch. `_hF._zgA`, the loop the native side calls when it has requests,
    /// adds every block it dispatches to <c>_vSA</c> — and treats a block that is <b>already</b>
    /// there as a duplicate, putting it into <c>_VSA</c> instead of dispatching it. `_ZgA` then
    /// opens by removing the block from <c>_VSA</c>, and when that removal finds something the read
    /// is <b>not performed at all</b>: the game completes it as a failure (result 15) and returns.
    /// </para>
    /// <para>
    /// So a block left in <c>_vSA</c> is a block the dispatcher can later mark cancelled, and the
    /// next read that lands on that block — anyone's read; the info blocks belong to the native
    /// side and are reused — is failed without a byte being read. A streamed sound whose read is
    /// failed that way never leaves FMOD's loading state, and its operation never completes, which
    /// is what a scene transition parked on an audio operation looks like. That consequence is a
    /// hypothesis; what is not a hypothesis is the leak, which is three missing calls away from the
    /// game's own path. <see cref="Observe"/> measures the consequence so the next run can say
    /// whether it happens at all.
    /// </para>
    /// <para>
    /// The calls go to the runtime's own <c>HashSet&lt;IntPtr&gt;</c> methods rather than to
    /// anything this mod writes itself, because the sets are the game's and other code reads them
    /// between these calls: the methods are walked off the instance's own class, which is the
    /// inflated generic the game calls, so no image name or instantiation has to be assumed.
    /// </para>
    /// </summary>
    internal static unsafe class PendingReads
    {
        /// <summary>`_hF._vSA` — reads handed to the native side and not finished yet.</summary>
        private static int FieldDispatched = Offsets.Unresolved;

        /// <summary>`_hF._VSA` — reads the dispatcher has decided to skip.</summary>
        private static int FieldCancelled = Offsets.Unresolved;

        /// <summary>`_hF._tSA`, the manager's state word. Inline, not a pointer: it reads 1.</summary>
        private static int FieldState = Offsets.Unresolved;

        private static bool _fieldsResolved;

        private static bool _fieldsMissing;

        /// <summary>
        /// `_hF`'s three fields, asked of the running game by name, once, and whether all answered.
        ///
        /// They are offsets into the class's statics block, and they are properties of *this build of
        /// the game* rather than of this mod — the same reason every other offset here is looked up
        /// (see <see cref="Offsets"/>). A patch that inserts a field ahead of these moves them, and a
        /// stale one would have this mod removing from the wrong sets without saying so — which is
        /// why a miss is reported by <see cref="FieldResolver.Field"/>, answered false here, and
        /// hinted at once in the log: the sets are then left exactly as the game leaves them, which
        /// is the behaviour this class was written to correct, and the run says so rather than
        /// reaching into whatever sits at the old offset.
        /// </summary>
        private static bool ResolveFields()
        {
            if (_fieldsResolved) return true;
            if (_fieldsMissing) return false;

            FieldDispatched = FieldResolver.Field("_hF", "_vSA");
            FieldCancelled = FieldResolver.Field("_hF", "_VSA");
            FieldState = FieldResolver.Field("_hF", "_tSA");

            if (FieldDispatched < 0 || FieldCancelled < 0 || FieldState < 0)
            {
                _fieldsMissing = true;
                Diagnostics.Error("the read bookkeeping's fields could not be read by name in this build; " +
                                  "a served read will not release its block");
                return false;
            }

            _fieldsResolved = true;
            return true;
        }

        /// <summary>The value of that word while the manager is not taking reads.</summary>
        private const int NotReading = 2;

        private static IntPtr _statics;
        private static IntPtr _remove, _removeInfo;
        private static IntPtr _contains, _containsInfo;
        private static bool _ready;
        private static bool _warned;

        /// <summary>Reads the game had already marked to be skipped. Should stay at zero.</summary>
        internal static long Skipped;

        /// <summary>Reads that arrived while the manager was not taking reads.</summary>
        internal static long NotTaken;

        /// <summary>Reads this mod served and then released, the way the game releases them.</summary>
        internal static long Released;

        /// <summary>
        /// The `_hF._ZgA` body's closing words for a read that was performed: out of the
        /// outstanding set, and out of the cancelled set.
        ///
        /// Called from the detour in place of the two statements the game would have run there.
        /// Once the sets are reached this is two calls and no allocation; before that — the window
        /// before the game has served any file at all — it does nothing, which only means the first
        /// few reads leave the sets as the game left them anyway, since the dispatcher has not
        /// filled them yet either.
        /// </summary>
        internal static void Finished(IntPtr record)
        {
            if (!Ready()) return;

            IntPtr info = Memory.Ptr(record);
            if (info == IntPtr.Zero) return;

            Call(_remove, _removeInfo, Memory.Ptr(_statics + FieldDispatched), info);
            Call(_remove, _removeInfo, Memory.Ptr(_statics + FieldCancelled), info);

            Released++;
        }

        /// <summary>
        /// Reads the sets without changing them, for the two questions a run has to answer: is a
        /// read about to be skipped, and is the manager still taking reads at all.
        ///
        /// Read-only on purpose. Taking the block out of the cancelled set here would stop the skip
        /// from happening, and a run that then hangs would have had the evidence removed from under
        /// it. This counts the event and lets it happen.
        /// </summary>
        internal static void Observe(IntPtr record)
        {
            if (!Ready()) return;

            if (Memory.I32(_statics + FieldState) == NotReading) { NotTaken++; return; }

            IntPtr info = Memory.Ptr(record);
            if (info == IntPtr.Zero) return;

            if (Call(_contains, _containsInfo, Memory.Ptr(_statics + FieldCancelled), info) != 0)
                Skipped++;
        }

        /// <summary>How many reads are outstanding, for the probe. -1 when the sets are not reached.</summary>
        internal static int Outstanding()
        {
            if (!Ready()) return -1;
            IntPtr dispatched = Memory.Ptr(_statics + FieldDispatched);
            return Memory.LooksLikeObject(dispatched)
                ? Memory.I32(dispatched + Offsets.Runtime.HashSetCount)
                : -1;
        }

        /// <summary>
        /// Reaches `_hF`'s statics and the two `HashSet&lt;IntPtr&gt;` methods, once.
        ///
        /// The class is found by name like every other type this mod reaches. The two methods are
        /// taken off the <b>instance's</b> class rather than off a class looked up by name: the sets
        /// are an inflated generic, and the instance is the instantiation the game itself calls.
        /// </summary>
        private static bool Ready()
        {
            if (_ready) return true;

            try
            {
                if (_statics == IntPtr.Zero)
                {
                    IntPtr klass = FieldResolver.ClassPointer("_hF");
                    if (klass == IntPtr.Zero) return false;

                    _statics = FieldResolver.Statics(klass);
                    if (_statics == IntPtr.Zero) return false;
                }

                // The offsets come first: everything below reads through one of them, and a name
                // that did not answer leaves its offset at -1 — the class, not the field.
                if (!ResolveFields()) return false;

                IntPtr sample = Memory.Ptr(_statics + FieldDispatched);
                if (!Memory.LooksLikeObject(sample)) return false;

                IntPtr setClass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(sample);
                if (setClass == IntPtr.Zero) return false;

                // Everywhere else this mod walks a class by name it initialises the class first. Here
                // the class is one an instance already exists for, so it has certainly been
                // initialised — but the walk is the same walk and the reason for the call is the
                // same, so it is made rather than argued about.
                Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_class_init(setClass);

                _remove = MethodOn(setClass, "Remove", 1, out _removeInfo);
                _contains = MethodOn(setClass, "Contains", 1, out _containsInfo);
                if (_remove == IntPtr.Zero || _contains == IntPtr.Zero)
                {
                    Report("the outstanding-read sets could not be read; a served read will not " +
                           "release its block");
                    return false;
                }

                _ready = true;
                Diagnostics.Info($"read bookkeeping reached: Remove 0x{_remove.ToInt64():X}, " +
                                 $"Contains 0x{_contains.ToInt64():X}, " +
                                 $"outstanding now {Outstanding()}");
                return true;
            }
            catch (Exception e)
            {
                Report($"the outstanding-read sets could not be reached: {Diagnostics.Describe(e)}");
                return false;
            }
        }

        /// <summary>
        /// Says why the sets could not be reached, once.
        ///
        /// <see cref="Ready"/> is retried on every call rather than given up on — the window where
        /// it cannot succeed is the window before the game has served any file at all, and it closes
        /// on its own. But it is called from the read path, which is the hottest one this mod has,
        /// so a line per attempt would be its own defect: the first read of a run arriving before
        /// the sets exist would print once per chunk for the rest of the session.
        ///
        /// `[Conditional]` for the reason the log class gives: without it the strings below are
        /// arguments to an ordinary method, they are evaluated in Release, and the shipped assembly
        /// carries a diagnostic a release has no business carrying. The artifact check catches this,
        /// which is how the first version of this method was found.
        /// </summary>
        [Conditional("DEBUG")]
        private static void Report(string why)
        {
            if (_warned) return;
            _warned = true;
            Diagnostics.Warn(why);
        }

        /// <summary>
        /// One of a class's methods, by name and parameter count, with its `MethodInfo`.
        ///
        /// The `MethodInfo` is handed back because a generic instantiation's body can still reach
        /// for it — the runtime passes it as the call's last argument and this mod is standing in
        /// for a call site, so it passes what the game passes. Zero parameters-matched is kept out
        /// of the answer entirely: `HashSet&lt;T&gt;` carries overloads, and a wrong pick would be a
        /// call into a different method with a different meaning.
        /// </summary>
        private static IntPtr MethodOn(IntPtr klass, string name, int parameters, out IntPtr methodInfo)
        {
            methodInfo = IntPtr.Zero;

            IntPtr iter = IntPtr.Zero;
            IntPtr method;
            while ((method = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
            {
                if (Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_name_(method) != name) continue;
                if ((int)Il2CppInterop.Runtime.IL2CPP.il2cpp_method_get_param_count(method) != parameters) continue;

                IntPtr code = *(IntPtr*)method;
                if (code == IntPtr.Zero) continue;

                methodInfo = method;
                return code;
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// `HashSet&lt;IntPtr&gt;.Remove`/`.Contains`, called the way the game's own call sites call
        /// them: the set, the value by value (the element is one word), and the `MethodInfo`.
        /// </summary>
        private static byte Call(IntPtr code, IntPtr methodInfo, IntPtr set, IntPtr value) =>
            ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte>)code)(set, value, methodInfo);
    }
}
