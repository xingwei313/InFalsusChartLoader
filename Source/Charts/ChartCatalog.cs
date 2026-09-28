using System;
using System.Collections.Generic;
using System.IO;

namespace InFalsusChartLoader
{
    /// <summary>
    /// The custom charts, keyed by the name the game will ask for them by.
    ///
    /// <para>
    /// The game builds a chart's name as <c>{BaseName}{difficulty}.spc</c> and hands it to
    /// <c>_s._VA</c>, which looks it up in StreamingAssets and throws when it is not there. This mod
    /// answers that call for its own names, so the game's loader never sees them and its not-found
    /// path is never reached.
    /// </para>
    /// <para>
    /// The name is matched whole: the base has to be a folder this mod imported, and the character
    /// before <c>.spc</c> has to be a difficulty digit. Anything else is not this mod's and is
    /// passed straight through.
    /// </para>
    /// </summary>
    internal static unsafe class ChartCatalog
    {
        private const string Extension = ".spc";

        /// <summary>Folders that imported, by base name. Ordinal: these are file names.</summary>
        private static readonly Dictionary<string, ChartInfo> ByBase =
            new Dictionary<string, ChartInfo>(StringComparer.Ordinal);

        /// <summary>
        /// Registers a chart under the name the game will ask for it with — the `if` file's `id`.
        ///
        /// The game builds `{name}{difficulty}.spc` from that string, so it is the name this has to
        /// answer to, and nothing in a run changes it: a name already in use is refused at
        /// registration rather than renamed, which is why there is no un-registration beside this.
        /// </summary>
        internal static void Adopt(ChartInfo info) => ByBase[info.BaseName] = info;

        /// <summary>
        /// Answers a chart request. Returns false when the name is not one of ours, in which case the
        /// caller must pass the call through untouched.
        ///
        /// On a name that is ours, the 32-byte tuple at <paramref name="result"/> is always written —
        /// with the decoded chart if it decodes, and with the game's empty chart if it does not — and
        /// the 40-byte <c>_R</c> is finished by clearing its source-text field, which is what the
        /// game's own loader does.
        /// </summary>
        internal static bool TryServe(IntPtr namePtr, IntPtr result)
        {
            string name = Memory.Text(namePtr, 128);
            if (name == null) return false;
            if (!Split(name, out string baseName, out int difficulty)) return false;

            if (!ByBase.TryGetValue(baseName, out ChartInfo info))
            {
                // A chart name this mod does not own is worth a line: it is the game asking for a
                // chart, and if it is one that should have been imported then the folder was dropped
                // for a reason that is already in the log above.
                Diagnostics.Info($"chart '{name}' is not ours; passing through");
                return false;
            }

            string path = info.ChartPaths[difficulty];
            // What the bytes were encoded under, and therefore what they can be decoded under. The
            // file has the same name — its path was built from this string — so this is a statement
            // about the encoding rather than a second name for the file.
            string seed = info.ChartNames[difficulty];

            bool decoded = false;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                decoded = ChartCodec.TryDecode(bytes, seed, result, out string reason);
                if (!decoded) Diagnostics.Error($"'{info.Name}' difficulty {difficulty}: {reason}");
            }
            catch (Exception e)
            {
                Diagnostics.Error($"'{info.Name}' difficulty {difficulty} could not be read: " +
                                  Diagnostics.Describe(e));
            }

            // A chart the import accepted and the play-through cannot decode is a file that changed
            // underneath us. Empty is the safe answer: the song plays, silently and with no notes,
            // instead of the scene hanging on a thrown lookup.
            if (!decoded) ChartCodec.Empty(result);

            // `_R._RC`. The game's loader always clears it, and the note loop that follows compares
            // it against the previous load's value.
            *(IntPtr*)(result + 0x20) = IntPtr.Zero;
            return true;
        }

        /// <summary>
        /// Splits <c>{base}{digit}.spc</c>. The digit is the difficulty, so the base is everything
        /// before it — which is how a folder named <c>song1</c> and a folder named <c>song</c> stay
        /// distinct rather than one being read as the other's difficulty.
        /// </summary>
        private static bool Split(string name, out string baseName, out int difficulty)
        {
            baseName = null;
            difficulty = 0;

            if (!name.EndsWith(Extension, StringComparison.Ordinal)) return false;

            int end = name.Length - Extension.Length;
            if (end < 2) return false;

            char last = name[end - 1];
            if (last < '0' || last > '3') return false;

            difficulty = last - '0';
            baseName = name.Substring(0, end - 1);
            return true;
        }
    }
}
