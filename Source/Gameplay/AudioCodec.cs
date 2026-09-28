using System;
using System.IO;

namespace InFalsusChartLoader
{
    /// <summary>
    /// Undoing the game's audio obfuscation — in the opposite direction, on purpose.
    ///
    /// <para>
    /// A song's audio on disk is <c>keystream(_MJA(plaintext))</c>. <c>_MJA</c> (an 8-byte fixed-key
    /// XOR) is removed by managed code inside the reader; the keystream is removed afterwards by
    /// <c>ifapp_fmod_native_async_complete_transform</c>. A custom file has neither layer — it is a
    /// plain Ogg — so the bytes this mod hands over have to come out of that pipeline equal to the
    /// file, and the way to arrange that is to apply the keystream <b>to the file, once, up
    /// front</b>: it is an XOR, so the game's own pass cancels it exactly.
    /// </para>
    /// <para>
    /// Only the keystream is applied here, not <c>_MJA</c>: this mod serves its bytes by filling the
    /// buffer the reader would have filled, which skips the reader and with it that layer. Applying
    /// it too would leave the audio XOR-ed once.
    /// </para>
    /// <para>
    /// The keystream is a function of file position, and its seed is the file's own length — both
    /// known here — so the transform is computable at load time and the read path is a copy.
    /// </para>
    /// </summary>
    internal static class AudioCodec
    {
        private const ulong Gold = 0x9E3779B97F4A7C15;
        private const ulong Mul1 = 0xD6E8FEB86659FD93;
        private const ulong Mul2 = 0xA24BAED4963EE407;

        /// <summary>
        /// A song's audio, already carrying the keystream, ready to be copied into whatever buffer
        /// the game asks for. <see cref="Bytes"/> is what goes on the wire; it is the same length as
        /// the file, because an XOR does not change length.
        /// </summary>
        internal sealed class Prepared
        {
            internal byte[] Bytes;

            /// <summary>The length the game must be told the file is, so its read window matches.</summary>
            internal int Length => Bytes.Length;
        }

        /// <summary>Reads <paramref name="path"/> and returns it transformed, or null with a reason.</summary>
        internal static Prepared Load(string path, out string reason)
        {
            reason = null;

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception e)
            {
#if DEBUG
                reason = $"{Path.GetFileName(path)} could not be read: {Diagnostics.Describe(e)}";
#else
                _ = e;
#endif
                return null;
            }

            if (bytes.Length == 0)
            {
#if DEBUG
                reason = $"{Path.GetFileName(path)} is empty";
#endif
                return null;
            }

            ulong seed = unchecked(Gold * (uint)bytes.Length);

            // A word covers eight positions, so it is computed once per aligned group rather than
            // once per byte; the first and last groups are partial because the file need not start
            // or end on an eight-byte boundary relative to what is being read.
            int i = 0;
            while (i < bytes.Length)
            {
                long position = i;
                ulong word = Word(position >> 3, seed);

                int groupEnd = (int)Math.Min(((position >> 3) + 1) << 3, bytes.Length);
                for (; i < groupEnd; i++)
                    bytes[i] ^= (byte)(word >> (int)(8 * ((long)i & 7)));
            }

            return new Prepared { Bytes = bytes };
        }

        /// <summary>One 64-bit keystream word, for the eight file positions starting at <c>n * 8</c>.</summary>
        private static ulong Word(long n, ulong seed)
        {
            ulong t = unchecked(Mul1 * (ulong)n + seed);
            ulong u = t ^ Ror(t, 47);
            ulong v = unchecked(Mul2 * u);
            return v ^ Ror(v, 23);
        }

        private static ulong Ror(ulong x, int r) => (x >> r) | (x << (64 - r));
    }
}
