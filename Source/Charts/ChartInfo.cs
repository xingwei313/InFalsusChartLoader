using System;

namespace InFalsusChartLoader
{
    /// <summary>
    /// One song folder under <c>Charts/</c>, as the <c>if</c> file inside it describes it.
    ///
    /// Every path in here is absolute and resolved against the folder the <c>if</c> was found in.
    /// The reader refuses names that escape that folder, so nothing downstream has to re-check where
    /// a path points.
    /// </summary>
    /// <remarks>
    /// The four difficulties are positional, matching the game's own difficulty slots:
    /// index 0 = Minimal, 1 = Evolved, 2 = Ultimate, 3 = Forbidden. That is why <c>chart</c>,
    /// <c>Charter</c> and <c>lv</c> must all be exactly four long — a folder cannot describe three
    /// difficulties, and a gap in the middle has nowhere to be written.
    /// </remarks>
    internal sealed class ChartInfo
    {
        /// <summary>How many difficulties a chart folder must describe. See the remarks.</summary>
        internal const int Difficulties = 4;

        /// <summary>The folder this came from. Used as the song's identity, not just its location.</summary>
        internal string Folder;

        /// <summary>Display name of the song.</summary>
        internal string Name;

        /// <summary>Composer, shown as the song's artist.</summary>
        internal string Composer;

        /// <summary>
        /// Illustrator. Goes to two places, and neither of them is `SongInfo`'s own override fields:
        /// the per-difficulty `SongChartInfo.DisplayJacketDesigner` (what the record carries) and
        /// `DynamicStringMapping.jacketIllustratorNameTypeMapping` at `+0x78` (what the card reads).
        /// It is a separate table from the artist's, keyed by the same `SongId`.
        /// </summary>
        internal string Illust;

        /// <summary>Absolute path of the audio file.</summary>
        internal string SongPath;

        /// <summary>
        /// Absolute paths of the four jacket images, in difficulty order.
        ///
        /// Four rather than one because a difficulty is allowed to look different from its
        /// neighbours. They are frequently the same file listed four times, which costs nothing: the
        /// catalogue builds one material per distinct path, so four entries pointing at one picture
        /// are four references to one texture.
        /// </summary>
        internal string[] PicturePaths;

        /// <summary>
        /// The window the song-select previews, in seconds.
        ///
        /// Always set: the `if` file has to carry it, so there is no "the author did not say" state
        /// left to represent. It used to be left at -1 when the field was absent, which handed the
        /// question to the game — and the game's own window (20 to 30 seconds, taken when
        /// <c>start &gt;= 0 &amp;&amp; end &gt;= 1</c> does not hold) is a different part of the song
        /// from the one the author meant.
        /// </summary>
        internal float PreviewStart;

        internal float PreviewEnd;

        /// <summary>Absolute paths of the four chart files, in difficulty order.</summary>
        internal string[] ChartPaths;

        /// <summary>
        /// The names the `if` file lists the four charts under, in difficulty order.
        ///
        /// These are also the files' names, because that is how their paths are built — and they are
        /// the decoder's seed, because a chart's bytes are encoded under a name and decoded under it
        /// again. One string, three jobs, and that is the point: a chart file renamed after it was
        /// encoded decodes to nonsense that no container check catches, so the name it carries has to
        /// be the name it was made with.
        /// </summary>
        internal string[] ChartNames;

        /// <summary>Chart designer per difficulty.</summary>
        internal string[] Charter;

        /// <summary>Difficulty rating per difficulty, as the game's own ratings are: small integers.</summary>
        internal int[] Level;

        /// <summary>
        /// The folder's own name. It is a label for the folder and nothing else: not a name for the
        /// song — an `if` file that names no `id` is a rejection, so this never stands in for one —
        /// and not a tie-breaker either, now that a name already in use is refused rather than
        /// suffixed. What it is used for is the log: it names the folder in the message that says
        /// which song a bad `if` file cost.
        /// </summary>
        internal string FolderName;

        /// <summary>
        /// The name the game asks for this song's charts under — <c>{BaseName}{difficulty}.spc</c> —
        /// and the key it files the song under. Neither is a file: the game never reads this as a
        /// path, and which file backs a chart is decided by <c>ChartCatalog</c> from the `if` file.
        ///
        /// One value, set once by the reader from the `if` file's <c>id</c>, and every catalogue that
        /// answers by name is keyed by it: audio, jackets and the charts themselves. It used to have
        /// two — the reader set it, then <c>SongCatalog</c> replaced it with a name made unique, and
        /// the catalogues were re-keyed to match. That is gone with the renaming: a name already in
        /// use is refused, so this is the author's string for the whole run, from the moment the
        /// folder is read to the moment the game asks for a chart.
        ///
        /// The uniqueness is not a nicety. The game keys its own indexes by this string and raises on
        /// a second entry with it, partway through the rebuild, which leaves every index it was
        /// building empty — which is why a collision cannot be passed through, and why it is refused
        /// where the author will see it rather than worked around. Nothing on disk is involved: the
        /// name is only ever what this mod is asked for, and what the game is told to ask.
        /// </summary>
        internal string BaseName;
    }
}
