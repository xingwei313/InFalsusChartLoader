using System;

namespace InFalsusChartLoader
{
    /// <summary>
    /// The game's own chart decoder, called from this mod.
    ///
    /// <para>
    /// Custom charts are read as <c>ICP1</c> — the shipping container — and handed to
    /// <c>_S._Gab</c>, the same function the game uses for its own. That is the whole design: the
    /// mod never decodes a chart itself, so anything it accepts is something the game accepts, and
    /// the container's own check (magic, five constants, count ceilings, and the identity
    /// <c>28 + 80·notes + 32·groups == length</c>) is what makes a malformed file a rejection rather
    /// than a crash.
    /// </para>
    /// <para>
    /// The decode seed is the chart's own file name. <c>_S._Ab(name, noteCount, 1)</c> is a hash of
    /// the name alone, so a chart is only readable under the name it was encoded with — which is why
    /// <c>tools/spc_convert.py</c> writes the output file name in as the seed, and why a chart that
    /// has been renamed decodes to nonsense instead of failing. <see cref="TryDecode"/> checks the
    /// result note by note and rejects it, because that failure is silent otherwise.
    /// </para>
    /// </summary>
    internal static unsafe class ChartCodec
    {
        /// <summary>
        /// `_S._Gab` (the chart decoder) and `_S._hab` (the empty chart), both resolved by name.
        /// </summary>
        /// <summary>
        /// `_S._Gab(ReadOnlySpan&lt;byte&gt;*, string, MethodInfo*) -> ValueTuple&lt;…&gt;*`
        ///
        /// The 32-byte tuple comes back through a hidden return buffer in rcx and the same pointer in
        /// rax, and the span is a 16-byte `{ byte* ptr; int length }` passed by address — a
        /// `ReadOnlySpan&lt;byte&gt;` is wider than one register, so the ABI hands over a pointer to it.
        /// </summary>
        private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> _gab;

        /// <summary>
        /// `_S._hab()`, the game's own "here is a chart with nothing in it" constructor. 32 bytes,
        /// two freshly allocated empty lists, bpm and beats zero.
        ///
        /// It is kept for the one case the import could not rule out: a chart that decoded when it
        /// was checked and does not now. Handing the game an empty chart costs a silent, empty
        /// play-through; the alternative is letting the name fall through to the game's own loader,
        /// which does not know the name and throws inside a coroutine — and that hangs the scene.
        /// </summary>
        private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr> _hab;

        /// <summary>
        /// Bytes of one `_fA`, measured off the live array before the first note is read. Zero until
        /// then: a stride is a measurement, and nothing may read a note before one has been taken.
        /// </summary>
        private static int _noteSize;

        /// <summary>
        /// How long a chart may claim to be, in milliseconds. Not a game rule — a bound that a
        /// mis-seeded decode cannot satisfy. Garbage note times are arbitrary 32-bit values; real
        /// ones are within a few minutes of the start of the song.
        /// </summary>
        private const int MaxChartMs = 3_600_000;

        /// <summary>Finds the decoder. False when it cannot be reached, in which case nothing imports.</summary>
        internal static bool Resolve()
        {
            IntPtr p = MethodResolver.ByName("_S", "_Gab");
            if (p == IntPtr.Zero) return false;

            _gab = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)p;

#if DEBUG
            // A MethodInfo that is known to work, for comparing the jacket's constructor against.
            // `_S._Gab` is resolved by name every run and is called for every chart, so if it is not
            // a valid structure nothing in this mod works.
            IntPtr reference = MethodResolver.MethodInfoOf("_S", "_Gab");
            if (reference != IntPtr.Zero)
                Diagnostics.Info($"reference MethodInfo _S._Gab at 0x{reference.ToInt64():X}");
            Diagnostics.Info($"reference _S._Gab words: {JacketFactory.Words(reference, 12)}");
#endif

            // Not fatal on its own — without it a chart that fails at play time falls through to the
            // game's loader instead of playing empty — so it is reported and the mod carries on.
            IntPtr hab = MethodResolver.ByName("_S", "_hab");
            _hab = hab == IntPtr.Zero
                ? null
                : (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)hab;

            return true;
        }

        /// <summary>Fills <paramref name="tuple32"/> with the game's empty chart.</summary>
        internal static void Empty(IntPtr tuple32)
        {
            if (_hab == null) return;
            _hab(tuple32, IntPtr.Zero);
        }

        /// <summary>The plane a note sits on, as `_fA._Ae`. 1 main, 2 shift, 3 space, 4 sky.</summary>
        private const int SideMin = 1;
        private const int SideMax = 4;

        /// <summary>
        /// Decodes <paramref name="bytes"/> under <paramref name="seed"/> into the 32-byte tuple at
        /// <paramref name="tuple32"/>.
        ///
        /// Returns false, with <paramref name="reason"/> filled in, for anything this mod will not
        /// put in the song list: a container the game's decoder rejects, a chart with no notes, or a
        /// chart whose notes do not look like notes — the last of which is what a wrong seed produces.
        /// </summary>
        internal static bool TryDecode(byte[] bytes, string seed, IntPtr tuple32, out string reason)
        {
            reason = null;

            if (_gab == null)
            {
#if DEBUG
                reason = "the chart decoder could not be reached";
#endif
                return false;
            }

            // Built here and used immediately: the only reference to this IL2CPP string is the local,
            // and a copy kept anywhere on this side would be invisible to IL2CPP's collector and
            // collected out from under the call. It is not cached.
            IntPtr name = Il2CppInterop.Runtime.IL2CPP.ManagedStringToIl2Cpp(seed);
            if (name == IntPtr.Zero)
            {
#if DEBUG
                reason = $"the name '{seed}' could not be passed to the decoder";
#endif
                return false;
            }

            byte* span = stackalloc byte[Offsets.Runtime.SpanSize];
            fixed (byte* data = bytes)
            {
                *(IntPtr*)span = (IntPtr)data;
                *(int*)(span + Offsets.Runtime.SpanLength) = bytes.Length;
                _gab(tuple32, (IntPtr)span, name, IntPtr.Zero);
            }

            return Inspect(tuple32, seed, out reason);
        }

        /// <summary>
        /// Checks what came back. The decoder answers a bad container with an empty chart rather than
        /// an error, so "no notes" is the first rejection; a wrong seed is the second, and it shows
        /// up as notes whose own fields are nonsense.
        /// </summary>
        private static bool Inspect(IntPtr tuple32, string seed, out string reason)
        {
            reason = null;

            IntPtr notes = Memory.Ptr(tuple32 + Offsets.Runtime.ChartTupleNotes);
            if (!Memory.TryList(notes, out IntPtr items, out int count))
            {
#if DEBUG
                reason = "the decoder returned no note list";
#endif
                return false;
            }
            if (count == 0)
            {
                // Either the container check failed or the file is an empty chart. Both are refusals.
#if DEBUG
                reason = "the decoder found no notes (bad container, or encoded under another name)";
#endif
                return false;
            }

            if (!MeasureNoteSize(items, count))
            {
#if DEBUG
                reason = "the note records could not be read this build";
#endif
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                IntPtr note = items + i * _noteSize;

                int side = Memory.U8(note + Offsets.Note.Side);
                int type = Memory.U8(note + Offsets.Note.Type);
                int start = Memory.I32(note + Offsets.Note.StartMs);
                int end = Memory.I32(note + Offsets.Note.EndMs);

                if (side < SideMin || side > SideMax)
                {
#if DEBUG
                    reason = $"note {i} is on plane {side} under the name '{seed}'; " +
                             "the file is probably not encoded under that name";
                    Dump(items, count);
#endif
                    return false;
                }
                if (!IsNoteType(type))
                {
#if DEBUG
                    reason = $"note {i} has type {type} under the name '{seed}'; " +
                             "the file is probably not encoded under that name";
                    Dump(items, count);
#endif
                    return false;
                }
                if (start < 0 || end < start || end > MaxChartMs)
                {
#if DEBUG
                    reason = $"note {i} runs {start}..{end}ms under the name '{seed}'; " +
                             "the file is probably not encoded under that name";
                    Dump(items, count);
#endif
                    return false;
                }
            }

            Diagnostics.Info($"decoded '{seed}': {count} notes, {_noteSize}-byte records");
            return true;
        }

        /// <summary>
        /// Debug-only: the first few note records **exactly as the game's decoder produced them**.
        ///
        /// Written when a chart is refused, because that is the only moment those bytes exist and
        /// nothing else will ever see them. The repository's own decoder and the game disagree about
        /// charts that differ only in the 32-byte tail — the repo says they decode identically, the
        /// game refuses one of them — and a disagreement about a decoder can only be settled by
        /// reading the decoder's output. This mod is running inside the game and is holding that
        /// output at the moment it decides. 128 bytes per note, first four notes, hex.
        /// </summary>
        private static void Dump(IntPtr items, int count)
        {
            int show = count < 4 ? count : 4;
            for (int i = 0; i < show; i++)
            {
                IntPtr note = items + i * _noteSize;

                var text = new System.Text.StringBuilder(_noteSize * 2);
                for (int b = 0; b < _noteSize; b++)
                    text.Append(Memory.U8(note + b).ToString("X2"));

                Diagnostics.Info($"  note[{i}] {text}");
            }
        }

        /// <summary>The four note types the shipped charts contain: tap, hold, flick, sky area.</summary>
        private static bool IsNoteType(int type) => type == 1 || type == 2 || type == 4 || type == 5;

        /// <summary>
        /// Takes the note stride off the live array instead of trusting the reversed constant: the
        /// array knows its own byte length and its own element count, and the quotient is the answer.
        /// False when it cannot be had — a stride nothing vouched for is a stride no note may be read
        /// at, so the chart is refused rather than checked against fields at unknown offsets.
        /// </summary>
        private static bool MeasureNoteSize(IntPtr items, int count)
        {
            if (!ResolveNoteFields()) return false;
            if (_strideMissing) return false;

            try
            {
                IntPtr array = items - Offsets.Runtime.ArrayDataOffset;
                long byteLength = Il2CppInterop.Runtime.IL2CPP.il2cpp_array_get_byte_length(array);
                int stride = (int)(byteLength / count);

                if (stride > 0)
                {
                    // The first measurement is the answer. A later one that disagrees is the thing
                    // worth saying: it means the array changed shape while the run was going.
                    if (_noteSize > 0 && stride != _noteSize)
                        Diagnostics.Warn($"note records measure {stride} bytes, not the {_noteSize} measured before");

                    _noteSize = stride;
                    return true;
                }
            }
            catch (Exception e)
            {
                Diagnostics.Warn($"could not measure the note stride: {Diagnostics.Describe(e)}");
            }

            _strideMissing = true;
            Diagnostics.Error("the note records' stride could not be measured in this build; " +
                              "no chart can be checked, so none will load");
            return false;
        }

        private static bool _offsetsResolved;

        private static bool _noteFieldsMissing;

        private static bool _strideMissing;

        /// <summary>
        /// The four `_fA` field offsets, asked of the running game by name, once, and whether all of
        /// them answered.
        ///
        /// A note record is a struct, so a patch that changes its shape moves its fields — and this is
        /// read for every note of every chart, which makes a stale offset here a wrong judgement
        /// rather than a visible failure. A name that cannot be resolved is reported by
        /// <see cref="FieldResolver.Field"/>, answered false here and latched: with no way to read a
        /// note, no chart can be vetted, and every chart is refused rather than accepted unchecked.
        /// </summary>
        private static bool ResolveNoteFields()
        {
            if (_offsetsResolved) return true;
            if (_noteFieldsMissing) return false;

            // Not a failure: this runs from the import, and the interop store may not be up yet —
            // the answer is re-asked while it is not. The latch below belongs after this line:
            // `FieldResolver.Field` cannot tell "not yet" from "renamed", so a name asked before
            // any class is in hand would turn a retryable state into a permanent miss.
            if (FieldResolver.ClassPointer("_fA") == IntPtr.Zero) return false;

            Offsets.Note.Side = FieldResolver.Field("_fA", "_Ae");
            Offsets.Note.Type = FieldResolver.Field("_fA", "_be");
            Offsets.Note.StartMs = FieldResolver.Field("_fA", "_Be");
            Offsets.Note.EndMs = FieldResolver.Field("_fA", "_ce");

            if (Offsets.Note.Side < 0 || Offsets.Note.Type < 0 ||
                Offsets.Note.StartMs < 0 || Offsets.Note.EndMs < 0)
            {
                _noteFieldsMissing = true;
                Diagnostics.Error("the note record's fields could not be read by name in this build; " +
                                  "no chart can be checked, so none will load");
                return false;
            }

            _offsetsResolved = true;
            return true;
        }
    }
}
