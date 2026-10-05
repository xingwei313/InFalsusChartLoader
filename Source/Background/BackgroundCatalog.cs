using System;
using System.Collections.Generic;

namespace InFalsusChartLoader
{
    /// <summary>
    /// The in-play backgrounds this mod built for its songs, and the answer to "what should this song
    /// be drawn on".
    ///
    /// <para>
    /// A background is a still or a clip, one per difficulty, and both ends in a material:
    /// a `.png` is decoded into a texture exactly the way a jacket is, and a `.mp4` is played into a
    /// render texture the material shows (see <see cref="VideoBackground"/>). Which one an entry is
    /// is its extension — the reader has already refused anything else, so there is nothing to ask
    /// the disk again.
    /// </para>
    /// <para>
    /// A song with no <c>background</c> in its `if` file has no entry here, and that is an answer
    /// rather than a gap: the game keeps its own background, which is what the field being optional
    /// means. The mod's older behaviour — a jacket doubling as the in-play background — is gone with
    /// the format: an author who wants the jacket there names it again under <c>background</c>, and
    /// the two catalogs share the build when they do (see <c>JacketFactory.Build</c>'s cache).
    /// </para>
    /// </summary>
    internal static class BackgroundCatalog
    {
        /// <summary>One difficulty's background: a built material, or a clip still to be started.</summary>
        internal struct Entry
        {
            /// <summary>The material of a still, or of a clip that is already playing.</summary>
            internal IntPtr Material;

            /// <summary>The file of a clip — null for a still, and for "nothing was said".</summary>
            internal string Video;

            /// <summary>Whether the author named anything at all for this difficulty.</summary>
            internal bool Said => Material != IntPtr.Zero || Video != null;
        }

        /// <summary>Per song, one entry per difficulty. Absent songs have no row at all.</summary>
        private static readonly Dictionary<string, Entry[]> ByBase =
            new Dictionary<string, Entry[]>(StringComparer.Ordinal);

        internal static int Count => ByBase.Count;

        /// <summary>
        /// Builds whatever can be built for one chart folder: the four stills, decoded now, so that a
        /// picture that cannot be decoded is found before the song is ever played.
        ///
        /// A clip is not opened here. Opening one means creating a `VideoPlayer`, a render texture
        /// and a material, and a video is only ever wanted while its chart is on screen — while a
        /// still costs a texture nothing else would build. Whatever did build is kept either way:
        /// a failure costs that difficulty its background, not the song.
        /// </summary>
        internal static bool Add(ChartInfo info, out string reason)
        {
            reason = null;
            if (info.BackgroundPaths == null) return true;   // the game's own, by request

            var entries = new Entry[ChartInfo.Difficulties];
            bool all = true;

            for (int d = 0; d < entries.Length; d++)
            {
                string path = info.BackgroundPaths[d];
                if (path == null) continue;

                if (IsVideo(path))
                {
                    entries[d].Video = path;
                    continue;
                }

                IntPtr material = JacketFactory.Build(path, "background", out string why);
                if (material == IntPtr.Zero)
                {
                    all = false;
#if DEBUG
                    if (reason == null) reason = $"difficulty {d}: {why}";
#else
                    _ = why;
#endif
                    continue;
                }

                entries[d].Material = material;
            }

            ByBase[info.BaseName] = entries;
            return all;
        }

        /// <summary>
        /// What this song's background is, and whether there is one of this mod's at all.
        ///
        /// False with <paramref name="why"/> null is the ordinary "nothing was said" — the caller
        /// leaves the game's own background alone. False with a reason is a background that was named
        /// and could not be prepared, which the caller reports: a `.mp4` whose player could not be
        /// built shows the game's background, and the log is the only place that says why.
        ///
        /// The difficulty is picked the way the jackets pick it — an unknown one is not guessed at,
        /// because a folder may give each difficulty a different background and a wrong picture is
        /// worse than none. The one case that is not a guess is four entries that are all the same,
        /// which is a fact about the folder rather than a fallback.
        /// </summary>
        internal static bool Find(IntPtr songInfo, int difficulty, out Entry entry, out string why)
        {
            entry = default;
            why = null;

            string baseName = JacketCatalog.NameOf(songInfo);
            if (baseName == null) return false;
            if (!ByBase.TryGetValue(baseName, out Entry[] entries)) return false;

            if (difficulty >= 0 && difficulty < entries.Length)
            {
                entry = entries[difficulty];
            }
            else
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    if (i == 0) entry = entries[0];
                    else if (entries[i].Material != entry.Material || entries[i].Video != entry.Video)
                    {
                        // Named and different, but which one is unknown: said out loud once, because
                        // "the background did not show" and "there is no background" are otherwise
                        // the same sight. Built inside the guard like every other `reason` in this
                        // mod: it only ever travels to a Debug-only warning, and a literal in a
                        // normal method ships whether or not anything reads it.
#if DEBUG
                        why = "a background is set per difficulty and the selected one could not be read";
#endif
                        return false;
                    }
                }

                if (!entry.Said) return false;
            }

            if (!entry.Said) return false;

            if (entry.Video == null) return true;

            IntPtr material = VideoBackground.Material(entry.Video, out why);
            if (material == IntPtr.Zero) return false;

            entry.Material = material;
            return true;
        }

        /// <summary>
        /// Whether a background file is a clip. The reader has already restricted the extension to
        /// the two this mod knows, so this decides between them and nothing else.
        /// </summary>
        private static bool IsVideo(string path) =>
            string.Equals(System.IO.Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase);
    }
}
