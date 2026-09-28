using System;

namespace InFalsusChartLoader
{
    /// <summary>
    /// Room for files the game did not ship with, inside the manager that looks files up by name.
    ///
    /// <para>
    /// A song's audio is asked for as <c>{BaseName}.wav</c> and resolved through `_BG`, which keeps
    /// four parallel arrays indexed by an integer. The name is turned into that integer by `_DJA`,
    /// which is hooked, so the name table never has to be touched — but the three arrays the rest of
    /// the path reads by index do: the length the game reports to FMOD, the `FileInfo` `_jJA`
    /// dereferences, and the chunk table the reader would consult.
    /// </para>
    /// <para>
    /// Each is extended by building a larger array, copying the old contents, and pointing `_BG` at
    /// the new one — indices the game already uses keep their values. The new slots at the end are
    /// the only ones this mod ever writes.
    /// </para>
    /// <para>
    /// Two of those need care, because the manager dereferences one of their words without a null
    /// check: the `FileInfo` array at the element's first word, and the chunk table at its second.
    /// A new slot in either has to hold a live object at that word — leaving it zero is a null
    /// reference, and in the chunk table's case it is thrown from inside the coroutine that finishes
    /// loading a scene, which is the white screen. They are reached differently, and that is worth
    /// knowing before reading the code below: the chunk table is walked in full (`_IJA`, once per
    /// scene change, over every entry), while the `FileInfo` array is only ever read by index — the
    /// accessors that turn an index into a name or a reader (`_jJA`, `_eJA`), each of which throws on
    /// a null element of its own. A walk and an indexed read have different reach, so the two are
    /// filled differently, and why is on <see cref="Grow"/>.
    /// </para>
    /// </summary>
    internal static unsafe class StreamingAssets
    {
        /// <summary>`_BG._RxA`, the singleton, at that offset inside the statics block.</summary>
        private const int StaticSingleton = 0x10;

        private const int FieldFiles = 0x28;    // FileInfo[] _TxA
        private const int FieldLengths = 0x30;  // long[]    _uxA
        private const int FieldFlags = 0x38;    // bool[]    _UxA
        private const int FieldChunks = 0x40;   // _DG[]     _vxA, 40 bytes per entry

        /// <summary>No stand-in: the array holds values, so a zeroed slot is a complete one.</summary>
        private const int None = -1;

        private static IntPtr _bg;

        /// <summary>Where the next added file goes. Starts past everything the game shipped.</summary>
        private static int _next;

        internal static bool Resolved => _bg != IntPtr.Zero;

        /// <summary>Finds the manager and the first free index. False when it cannot be reached.</summary>
        internal static bool Resolve()
        {
            try
            {
                // The class by name, like every other type this mod reaches. An earlier version read
                // the global slot the game's own code reads, which is a metadata usage slot: what sits
                // there before the runtime initialises it is an encoded index, not a class pointer, and
                // dereferencing it takes the process down. By name there is nothing to be early for.
                IntPtr klass = FieldResolver.ClassPointer("_BG");
                if (klass == IntPtr.Zero)
                {
                    Diagnostics.Warn("_BG could not be found by name; custom audio will not work");
                    return false;
                }

                IntPtr statics = FieldResolver.Statics(klass);
                if (statics == IntPtr.Zero)
                {
                    Diagnostics.Warn("_BG has no statics block; custom audio will not work");
                    return false;
                }

                IntPtr bg = Memory.Ptr(statics + FieldResolver.Field("_BG", "_RxA", StaticSingleton));
                if (!Memory.LooksLikeObject(bg))
                {
                    Diagnostics.Warn("the streaming asset manager singleton is not there yet; " +
                                     "custom audio will not work");
                    return false;
                }

                IntPtr lengths = Memory.Ptr(bg + FieldLengths);
                if (!Memory.LooksLikeObject(lengths))
                {
                    Diagnostics.Warn("the streaming asset manager has no length table; custom audio " +
                                     "will not work");
                    return false;
                }

                int count = Memory.I32(lengths + Offsets.Runtime.ArrayLength);
                if (count <= 0)
                {
                    Diagnostics.Warn($"the streaming asset manager lists {count} files; custom audio " +
                                     "will not work");
                    return false;
                }

                _bg = bg;
                _next = count;

                Diagnostics.Info($"streaming assets: {count} files, custom files start at {_next}");
                return true;
            }
            catch (Exception e)
            {
                Diagnostics.Warn($"the streaming asset manager could not be reached: {Diagnostics.Describe(e)}");
                return false;
            }
        }

        /// <summary>
        /// Makes room for one more file and records its length. Returns its index, or -1 if any of
        /// the arrays could not be grown — in which case nothing is left half-written: a file that
        /// cannot be indexed is not registered at all.
        /// </summary>
        internal static int Add(int length)
        {
            if (!Resolved) return -1;

            int index = _next;
            int size = index + 1;

            if (!Grow(FieldFiles, size, standIn: 0, own: false)) return -1;
            if (!Grow(FieldFlags, size, standIn: None, own: false)) return -1;
            if (!Grow(FieldChunks, size, standIn: 8, own: true)) return -1;
            if (!Grow(FieldLengths, size, standIn: None, own: false)) return -1;

            IntPtr lengths = Memory.Ptr(_bg + FieldLengths);
            Memory.WriteI64(lengths + Offsets.Runtime.ArrayDataOffset + index * sizeof(long), length);

            _next = size;
            return index;
        }

        /// <summary>
        /// Replaces one of the manager's arrays with one that has room for <paramref name="size"/>
        /// entries, keeping every existing value.
        ///
        /// The element size is taken from the array itself rather than assumed: a reference array and
        /// a `long` array are both eight bytes here, but the `bool` one is not, and the array knows
        /// its own byte length and count.
        /// </summary>
        /// <param name="standIn">
        /// Offset inside an element of a reference the manager dereferences without checking it, or
        /// <see cref="None"/> for an array it only reads values out of. A newly allocated array is
        /// zeroed, and two of these tables hand that word to code which assumes it holds a live
        /// record: the `FileInfo` table at its first word, read by index, and the chunk table at its
        /// second, walked in full.
        /// </param>
        /// <param name="own">
        /// Whether that slot needs an object of its own rather than a copy of another entry's pointer.
        /// The walk that reads the chunk table also *replaces* what it finds at that word, which
        /// leaves a copied pointer aimed at whatever the collector takes next -- so that slot is given
        /// a fresh empty instance. Nothing replaces a `FileInfo`, so the copy there is stable.
        /// </param>
        private static bool Grow(int field, int size, int standIn, bool own)
        {
            IntPtr old = Memory.Ptr(_bg + field);
            if (!Memory.LooksLikeObject(old)) { Diagnostics.Warn($"field +0x{field:X} is not an array"); return false; }

            int oldCount = Memory.I32(old + Offsets.Runtime.ArrayLength);
            if (size <= oldCount) return true;

            int stride = oldCount > 0
                ? (int)(Il2CppInterop.Runtime.IL2CPP.il2cpp_array_get_byte_length(old) / oldCount)
                : sizeof(long);
            if (stride <= 0) return false;
            if (standIn != None && standIn + IntPtr.Size > stride)
            {
                Diagnostics.Warn($"field +0x{field:X} has {stride}-byte elements with no room at +0x{standIn:X}");
                return false;
            }

            IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(old);
            IntPtr element = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_element_class(klass);
            if (element == IntPtr.Zero) { Diagnostics.Warn($"field +0x{field:X} has no element class"); return false; }

            IntPtr grown = Il2CppInterop.Runtime.IL2CPP.il2cpp_array_new(element, (ulong)size);
            if (grown == IntPtr.Zero) return false;

            IntPtr source = old + Offsets.Runtime.ArrayDataOffset;
            IntPtr target = grown + Offsets.Runtime.ArrayDataOffset;
            Buffer.MemoryCopy((void*)source, (void*)target, (long)size * stride, (long)oldCount * stride);

            if (standIn != None && size > oldCount)
            {
                // The first entry that actually has one, rather than element zero: element zero of
                // the `FileInfo` table is null — measured, after a version that took its word and
                // copied a null into every new slot. A null there is not harmless either: the name
                // lookup reads it and throws, which is the shape of failure that reaches a coroutine.
                IntPtr stand = IntPtr.Zero;
                for (int i = 0; i < oldCount && stand == IntPtr.Zero; i++)
                {
                    IntPtr value = *(IntPtr*)(source + i * stride + standIn);
                    if (!Memory.LooksLikeObject(value)) continue;
                    stand = own ? EmptyLike(value) : value;
                }

                if (stand == IntPtr.Zero)
                {
                    Diagnostics.Warn($"no entry of +0x{field:X} has anything at +0x{standIn:X} to go " +
                                     "by; the custom files are not registered");
                    return false;
                }

                for (int i = oldCount; i < size; i++)
                    *(IntPtr*)(target + i * stride + standIn) = stand;
            }

            Memory.WritePtr(_bg + field, grown);
            return true;
        }

        /// <summary>
        /// A fresh, empty instance of whatever `like` is: the same class, with nothing in it. An
        /// array becomes a zero-length array of the same element class, anything else a new object
        /// with every field zero -- for a collection that reads as empty, which is what the walk
        /// wants to see: an entry with nothing to release and nothing to dereference.
        /// </summary>
        private static IntPtr EmptyLike(IntPtr like)
        {
            if (!Memory.LooksLikeObject(like)) return IntPtr.Zero;

            IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(like);
            if (klass == IntPtr.Zero) return IntPtr.Zero;

            // An array class has an element class; a plain one does not. `il2cpp_object_new` on an
            // array class would make something with no bounds at all.
            IntPtr element = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_element_class(klass);
            if (element != IntPtr.Zero)
                return Il2CppInterop.Runtime.IL2CPP.il2cpp_array_new(element, 0);

            // The class has to be initialized first, the same as anywhere else this mod reaches a
            // type by name: allocating from a class whose static constructor has not run gives
            // nothing back.
            Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_class_init(klass);
            return Il2CppInterop.Runtime.IL2CPP.il2cpp_object_new(klass);
        }
    }
}
