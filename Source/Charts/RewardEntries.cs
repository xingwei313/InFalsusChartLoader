using System;
using System.Collections.Generic;

namespace InFalsusChartLoader
{
    /// <summary>
    /// One entry per custom song in `RewardData._Geb` — the table the song list is filtered by.
    ///
    /// <para>
    /// The song select builds its cards from two tests: the song belongs to the pack being shown, and
    /// the song is one that `_NH._yoA` derives from the song data. That derivation reads this table,
    /// and a song with no entry in it is left out — which is an empty list, a screen that never
    /// finishes loading, and a chart that is never asked for. The pack card's strip is built by a
    /// different caller of the same rule and shows such songs anyway, which is exactly the shape
    /// observed: the pack looks right, its contents do not exist.
    /// </para>
    /// <para>
    /// The value is a 4-byte `StoryIdentifier` — the story a song's unlock belongs to. This mod has no
    /// story, so the entry is a copy of a shipped song's, taken from the first song in the table that
    /// has one. Which song that was is logged: it is the one part of this that is a choice rather than
    /// a fact, and the next round's counters say whether the choice was right.
    /// </para>
    /// </summary>
    internal static unsafe class RewardEntries
    {
        /// <summary>How many shipped ids to try before giving up on finding a template.</summary>
        private const int SearchLimit = 512;

        /// <summary>
        /// Gives every id in <paramref name="songIds"/> an entry. Returns how many were written, which
        /// is all of them or none — nothing here is worth half-doing.
        /// </summary>
        internal static int Add(List<int> songIds)
        {
            if (songIds.Count == 0) return 0;

            try
            {
                IntPtr klass = FieldResolver.ClassPointer("DataAccess");
                if (klass == IntPtr.Zero) { Diagnostics.Warn("DataAccess could not be found; the custom songs will not be listed"); return 0; }

                IntPtr statics = FieldResolver.Statics(klass);
                if (statics == IntPtr.Zero) return 0;

                int rewardField = FieldResolver.Field("DataAccess", "_iAb");
                if (rewardField < 0) return 0;

                IntPtr reward = Memory.Ptr(statics + rewardField);
                if (!Memory.LooksLikeObject(reward))
                {
                    Diagnostics.Warn("RewardData is not loaded; the custom songs will not be listed");
                    return 0;
                }

                int storyField = FieldResolver.Field("RewardData", "_Geb");
                if (storyField < 0) return 0;

                IntPtr table = Memory.Ptr(reward + storyField);
                if (!Memory.LooksLikeObject(table))
                {
                    Diagnostics.Warn("RewardData has no song-to-story table; the custom songs will not be listed");
                    return 0;
                }

                IntPtr tableClass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(table);
                IntPtr tryGet = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_method_from_name(tableClass, "TryGetValue", 2);
                IntPtr setItem = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_method_from_name(tableClass, "set_Item", 2);
                if (tryGet == IntPtr.Zero || setItem == IntPtr.Zero)
                {
                    Diagnostics.Warn("the song-to-story table could not be read or written; the custom songs " +
                                     "will not be listed");
                    return 0;
                }

                // A template from a shipped song. `TryGetValue` is asked one id at a time rather than
                // the table being enumerated: the enumerator is a nested struct whose layout this mod
                // would be guessing at, and the ids are small integers.
                //
                // One argument array for both loops, allocated once: a `stackalloc` per iteration
                // grows the frame with the loop, which is a stack overflow waiting for a long enough
                // list — and the analyzer refuses to build it, on a clean build, at that.
                IntPtr* args = stackalloc IntPtr[2];

                // The play gate reads this table too, and an entry changes what it does. `_sN` — the
                // song-select's "can this be started" predicate — asks the player's save
                // `_K._NH._loA(story)` and `._toA(story)` for whichever identifier the song is filed
                // under, and refuses **silently** (returns 1, no log, no exception) when either says
                // no. That is what "the song is listed but will not open" is: filing a custom song
                // under a shipped song's identifier means it inherits that song's unlock state, and
                // an identifier belonging to something the player has not reached locks the custom
                // song too.
                //
                // So the identifier is chosen rather than copied: the candidates are walked as before,
                // but one is only taken once the game's own two checks both say yes. Same rule as
                // everywhere else in this mod -- ask the game instead of assuming -- and it is the
                // only way to pick, because which identifiers are unlocked is the player's progress
                // and nothing this mod can compute.
                IntPtr save = PlayerSave();
                IntPtr loA = MethodResolver.MethodInfoByRuntime("_NH", "_loA", "Game.dll", "ifapp.Game");
                IntPtr toA = MethodResolver.MethodInfoByRuntime("_NH", "_toA", "Game.dll", "ifapp.Game");
                bool canTest = save != IntPtr.Zero && loA != IntPtr.Zero && toA != IntPtr.Zero;
                if (!canTest)
                    Diagnostics.Warn("the song's unlock state could not be asked of the game; the " +
                                     "custom songs may be listed but not openable");

                uint story = 0;
                int from = -1;
                for (int id = 1; id < SearchLimit && from < 0; id++)
                {
                    ushort key = (ushort)id;
                    story = 0;

                    args[0] = (IntPtr)(&key);
                    args[1] = (IntPtr)(&story);

                    IntPtr raised = IntPtr.Zero;
                    IntPtr boxed = Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(
                        tryGet, table, (void**)args, ref raised);
                    if (raised != IntPtr.Zero) break;
                    if (boxed == IntPtr.Zero || *(byte*)(boxed + Offsets.Runtime.BoxedData) == 0) continue;

                    if (canTest && !Playable(save, loA, toA, story)) continue;

                    from = id;
                }

                if (from < 0)
                {
                    Diagnostics.Warn("no shipped song has a story identifier to copy; the custom songs will " +
                                     "not be listed");
                    return 0;
                }

                int added = 0;
                foreach (int id in songIds)
                {
                    ushort key = (ushort)id;

                    args[0] = (IntPtr)(&key);
                    args[1] = (IntPtr)(&story);

                    IntPtr raised = IntPtr.Zero;
                    Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(setItem, table, (void**)args, ref raised);
                    if (raised != IntPtr.Zero)
                    {
                        Diagnostics.Warn($"the story entry for song {id} raised: {Raised.Text(raised)}");
                        return added;
                    }
                    added++;
                }

                Diagnostics.Info($"story entries: {added} song(s) filed under story 0x{story:X}, " +
                                 $"copied from song {from}");
                return added;
            }
            catch (Exception e)
            {
                Diagnostics.Warn($"the story entries could not be written: {Diagnostics.Describe(e)}");
                return 0;
            }
        }

        /// <summary>
        /// The player's save — `_K._NH`'s singleton, which is the first word of its statics block.
        ///
        /// Read off the game's own use of it rather than from a field name: the play gate reaches it
        /// as `**(klass + 0xB8)`, and `0xB8` is `Il2CppClass.static_fields`. Shared with
        /// <see cref="CustomResults"/>, which needs the same singleton to reach the save container.
        /// </summary>
        internal static IntPtr PlayerSave()
        {
            IntPtr klass = FieldResolver.ClassPointer("_NH");
            if (klass == IntPtr.Zero) return IntPtr.Zero;

            IntPtr statics = FieldResolver.Statics(klass);
            if (statics == IntPtr.Zero) return IntPtr.Zero;

            IntPtr save = Memory.Ptr(statics);
            return Memory.LooksLikeObject(save) ? save : IntPtr.Zero;
        }

        /// <summary>
        /// Whether the game itself says a story identifier may be played.
        ///
        /// `_K._NH._loA` and `._toA`, both `bool(StoryIdentifier*)` — the same two the play gate calls,
        /// with the identifier passed by address because a four-byte value goes in a register's worth
        /// of memory rather than in a box. Both have to agree, which is what the gate requires.
        /// </summary>
        private static bool Playable(IntPtr save, IntPtr loA, IntPtr toA, uint story)
        {
            return Asks(save, loA, story) && Asks(save, toA, story);
        }

        private static unsafe bool Asks(IntPtr save, IntPtr method, uint story)
        {
            IntPtr* args = stackalloc IntPtr[1];
            args[0] = (IntPtr)(&story);

            IntPtr raised = IntPtr.Zero;
            IntPtr boxed = Il2CppInterop.Runtime.IL2CPP.il2cpp_runtime_invoke(method, save, (void**)args, ref raised);
            if (raised != IntPtr.Zero)
            {
                Diagnostics.Warn($"asking the game about story 0x{story:X} raised: {Raised.Text(raised)}");
                return false;
            }

            return boxed != IntPtr.Zero && *(byte*)(boxed + Offsets.Runtime.BoxedData) != 0;
        }
    }
}
