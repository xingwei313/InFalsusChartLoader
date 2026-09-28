using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace InFalsusChartLoader
{
    /// <summary>
    /// The custom songs' audio: how the game finds it, and what it gets when it reads it.
    ///
    /// <para>
    /// Two halves, matching the two places the game touches a song's audio. The name
    /// <c>{BaseName}.wav</c> is resolved to an index, and the index is resolved to bytes. This mod
    /// answers both, for its own songs only, and every other name and index goes to the game.
    /// </para>
    /// <para>
    /// The bytes handed over are the file with the keystream already applied — see
    /// <see cref="AudioCodec"/>. That is what lets the game's own completion path run unchanged: it
    /// XORs the keystream in, which cancels, and the format reaches FMOD intact.
    /// </para>
    /// </summary>
    internal static unsafe class AudioCatalog
    {
        private const string Extension = ".wav";

        /// <summary>One song's audio, and the index it answers to.</summary>
        private sealed class Entry
        {
            internal int Index;
            internal AudioCodec.Prepared Audio;
        }

        private static readonly Dictionary<string, Entry> ByBase =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        private static readonly Dictionary<int, Entry> ByIndex = new Dictionary<int, Entry>();

        /// <summary>Reads served and bytes handed over. Counted rather than logged — see Serve.</summary>
        internal static long Served, ServedBytes, Resolved;

        /// <summary>The index a song's audio answers to, or -1. For reporting only.</summary>
        internal static int IndexOf(string baseName) =>
            ByBase.TryGetValue(baseName, out Entry entry) ? entry.Index : -1;

        /// <summary>The byte length of a song's prepared audio, or -1. For reporting only.</summary>
        internal static int BytesOf(string baseName) =>
            ByBase.TryGetValue(baseName, out Entry entry) ? entry.Audio.Length : -1;

        /// <summary>
        /// Prepares a song's audio and gives it an index. False, with a reason, when the file cannot
        /// be read or the manager has no room — in which case the song is not imported at all, since
        /// a song that cannot make a sound is not a song.
        /// </summary>
        internal static bool Add(string baseName, string path, out string reason)
        {
            reason = null;

            AudioCodec.Prepared audio = AudioCodec.Load(path, out reason);
            if (audio == null) return false;

            int index = StreamingAssets.Add(audio.Length);
            if (index < 0)
            {
                reason = "the game's asset manager would not take another file";
                return false;
            }

            var entry = new Entry { Index = index, Audio = audio };
            ByBase[baseName] = entry;
            ByIndex[index] = entry;
            Diagnostics.Info($"audio '{baseName}' -> index {index}, {audio.Length} bytes");
            return true;
        }

        /// <summary>
        /// The `_BG._DJA` body: answers for <c>{BaseName}.wav</c> when the base is one of ours.
        /// </summary>
        internal static bool TryResolve(IntPtr namePtr, IntPtr indexPtr)
        {
            // This runs for every asset the game looks up, not just audio, so the first test has to
            // be one that does not build a string: a name that cannot end in ".wav" is answered
            // without allocating anything to find that out.
            if (!CouldBeAudio(namePtr)) return false;

            string name = Memory.Text(namePtr, 128);
            if (name == null) return false;
            if (!name.EndsWith(Extension, StringComparison.Ordinal)) return false;

            string baseName = name.Substring(0, name.Length - Extension.Length);
            if (baseName.Length == 0) return false;
            if (!ByBase.TryGetValue(baseName, out Entry entry)) return false;

            Memory.WriteI32(indexPtr, entry.Index);
            Resolved++;
            return true;
        }

        /// <summary>
        /// Whether a name is even a candidate, decided by reading its length and last character
        /// rather than by materialising it.
        /// </summary>
        private static unsafe bool CouldBeAudio(IntPtr s)
        {
            if (!Memory.LooksLikeObject(s)) return false;

            try
            {
                int length = Il2CppInterop.Runtime.IL2CPP.il2cpp_string_length(s);
                if (length < Extension.Length + 1 || length > 128) return false;

                char* chars = Il2CppInterop.Runtime.IL2CPP.il2cpp_string_chars(s);
                if (chars == null) return false;

                return chars[length - 1] == 'v';
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The `_ZgA` body: fills the read buffer for one of this mod's files and finishes the read.
        ///
        /// Returns false for any other file, which leaves the caller to run the game's own path.
        /// </summary>
        internal static bool TryServe(IntPtr record)
        {
            if (Hooks.CompleteRead == null) return false;

            // The handle carries the index in its low 24 bits. The length and kind packed above it
            // are the game's business; this mod only has to recognise its own index.
            long handle = Memory.I64(record + 8);
            int index = (int)(handle & 0xFFFFFF);
            if (!ByIndex.TryGetValue(index, out Entry entry)) return false;

            IntPtr info = Memory.Ptr(record);
            if (info == IntPtr.Zero) return false;

            int offset = Memory.I32(info + 8);
            int size = Memory.I32(info + 12);
            IntPtr buffer = Memory.Ptr(info + 32);
            if (buffer == IntPtr.Zero || size <= 0) return false;

            byte[] bytes = entry.Audio.Bytes;
            int available = offset >= 0 && offset < bytes.Length ? bytes.Length - offset : 0;
            int count = Math.Min(size, available);

            if (count > 0) Marshal.Copy(bytes, offset, buffer, count);

            // Counted, not logged. This is the hottest path in the mod — every chunk of every
            // playback goes through it, hundreds of lines a second — and MelonLoader's logger writes
            // each one to a file and to the console. A mod that logs per audio read costs frames,
            // and the game's scene transitions are timing-sensitive enough to notice. The totals are
            // in the probe.
            Served++;
            ServedBytes += count;

            // A read past the end is answered with nothing rather than refused, and the buffer is
            // left as it was: the game sizes its reads from the length this mod registered, so this
            // is the tail of the last chunk, not a gap.
            Memory.WriteI32(info + 40, count);

            // The game's own handler takes the block out of its two outstanding-read sets at this
            // point, and this mod is standing in for that handler. Leaving those two calls out is
            // not harmless: the dispatcher reads one of the sets to decide what is a duplicate, and
            // what it calls a duplicate is cancelled -- failed without a byte being read, for
            // whoever's read lands on that block next. See PendingReads.
            PendingReads.Finished(record);

            // Finish through the game's own completion, with the record copied exactly as its own
            // handler copies it. Result 0 -- a success -- because the keystream already applied to
            // these bytes is cancelled by the transform that runs next.
            byte* copy = stackalloc byte[40];
            Buffer.MemoryCopy((void*)record, copy, 40, 40);
            Hooks.CompleteRead((IntPtr)copy, 0);
            return true;
        }
    }
}
