using System;
using System.Collections.Generic;

namespace InFalsusChartLoader
{
    /// <summary>
    /// Everything a Debug build can report about one run, in the order the run happens.
    ///
    /// <para>
    /// This file is not compiled into a Release build at all — the project excludes this folder — and
    /// neither are its call sites, which are wrapped in `#if DEBUG`. That is the point: a probe may
    /// read whatever it likes, because none of it ships.
    /// </para>
    /// <para>
    /// It is written to answer the questions a single run has to settle, in the order they stop
    /// mattering:
    /// </para>
    /// <list type="number">
    /// <item><description>Did the mod reach the game at all — the codec, the asset manager, the hooks.</description></item>
    /// <item><description>What did the chart folders yield, and what was each rejection.</description></item>
    /// <item><description><b>Did the tables grow.</b> A song only appears if <c>_ffb</c> grew, an
    /// aggregate of <c>allSongInfo</c> the game rebuilds once and will not rebuild again. This is the
    /// line to read first when nothing shows up.</description></item>
    /// <item><description>Is what was written back readable — the ids, the names, the chart counts.</description></item>
    /// <item><description>Are the hooks alive, and are they firing on the right things.</description></item>
    /// </list>
    /// <para>
    /// Field offsets are asked for by name rather than written down, which makes this file a second
    /// opinion on <see cref="Offsets"/> rather than a copy of it: a disagreement shows up here.
    /// </para>
    /// </summary>
    internal static class Probe
    {
        private const int SongInfoSize = 0x40;
        private const int SongInfoId = 0x00;
        private const int SongInfoBaseName = 0x08;
        private const int SongInfoCharts = 0x18;
        private const int SongInfoTitleReading = 0x28;
        private const int SongInfoArtistReading = 0x30;

        // ---------------------------------------------------------------- one: reaching the game

        /// <summary>What the mod found when it went looking for the game.</summary>
        internal static void Startup(string chartsFolder, bool codecReady, bool assetsReady,
                                     int installed, int attempted)
        {
            line($"charts folder   {chartsFolder ?? "<none>"}");
            line($"charts folder exists? {chartsFolder != null && System.IO.Directory.Exists(chartsFolder)}");
            line($"chart decoder   {(codecReady ? "reached" : "NOT REACHED")}");
            line($"asset manager   {(assetsReady ? "reached" : "NOT REACHED")}");
            line($"hooks           {installed}/{attempted}");
            line($"offsets         {FieldResolver.Stats()}");

            // Every RVA fallback in this mod is relocated against this. A wrong base turns those
            // into silent misses, and a zero one means the module was not found at all — both worth
            // knowing before a hook is reported as installed.
            long baseAddress = GameAssembly.Base.ToInt64();
            line($"game assembly   0x{baseAddress:X}" +
                 (baseAddress == 0 ? "  <-- NOT FOUND; every RVA fallback is dead" : ""));
        }

        // ---------------------------------------------------------------- two: what was imported

        /// <summary>What each folder decided, and what the ones that were kept produced.</summary>
        internal static void Imported(ChartInfo info, int audioIndex, int audioBytes, int jacketCount)
        {
            // The name here is the one the folder imported under, which is the `if` file's id -- not
            // the name it will be filed under, which is not settled until registration and may differ
            // when a shipped song already uses it. Printing this one and not the other is deliberate:
            // if the two disagree at registration, "song[N] base=" in the after-block says so.
            line($"kept '{Path(info)}' as '{info.BaseName}', audio index {audioIndex} ({audioBytes} bytes), " +
                 $"jackets built {jacketCount}");
            line($"  name='{info.Name}' composer='{info.Composer}' illust='{info.Illust}'");
            line($"  levels={string.Join("/", info.Level)} charters={string.Join("/", info.Charter)}");
            line($"  preview={info.PreviewStart}..{info.PreviewEnd}s");
            line($"  audio={System.IO.Path.GetFileName(info.SongPath)} pictures={Pictures(info)}");
        }

        /// <summary>
        /// The four jacket file names, and whether they are one file or several — the distinction
        /// that decides whether four materials were built or one.
        /// </summary>
        private static string Pictures(ChartInfo info)
        {
            var names = new List<string>(ChartInfo.Difficulties);
            foreach (string path in info.PicturePaths) names.Add(System.IO.Path.GetFileName(path));

            int distinct = new HashSet<string>(names, StringComparer.Ordinal).Count;
            return string.Join(", ", names) + (distinct == 1 ? "  (one file)" : $"  ({distinct} files)");
        }

        // ---------------------------------------------------------------- three: the tables

        /// <summary>The sizes everything is about to be written into.</summary>
        internal static void Before(IntPtr songData, IntPtr packData, IntPtr packAssets)
        {
            line("--- before ---");
            line($"resolve         DataAccess ok, SongData=0x{songData.ToInt64():X} " +
                 $"PackData=0x{packData.ToInt64():X} PackAssets=0x{packAssets.ToInt64():X}");
            line($"sizes           {Sizes(songData, packData, packAssets)}");
        }

        /// <summary>
        /// The sizes afterwards, and what was actually written.
        ///
        /// The first thing to read is whether <c>_ffb</c> moved: it is the list song select reads,
        /// and the game builds it once. If it did not grow, no custom song can appear no matter how
        /// correct everything else is.
        /// </summary>
        internal static void After(IntPtr songData, IntPtr packData, IntPtr packAssets,
                                   List<ChartInfo> charts)
        {
            line("--- after ---");
            line($"sizes           {Sizes(songData, packData, packAssets)}");

            IntPtr songs = ArrayAt(songData, "SongData", "allSongInfo");
            int count = Length(songs);
            line($"allSongInfo     {count} entries");

            for (int i = 0; i < charts.Count; i++)
            {
                int index = count - charts.Count + i;
                if (index < 0) break;
                DumpSong(songs, index, charts[i]);
            }

            DumpPack(packData);
            DumpPackVisuals(packAssets);
        }

        /// <summary>
        /// The counters. Printed on a timer, so a run that never reaches a screen still says whether
        /// the hooks were called and what they answered.
        /// </summary>
        internal static string Status() =>
            $"va={Hooks.VaCalls}/{Hooks.VaServed} dja={Hooks.DjaCalls}/{Hooks.DjaHits} " +
            $"zga={Hooks.ZgaCalls}/{Hooks.ZgaServed} apa={Hooks.ApACalls}/{Hooks.ApAArmed} " +
            $"askedNoPicture={Hooks.ApaAsked} mia={Hooks.MiaCalls}/{Hooks.MiaClaimed} " +
            $"zoa={Hooks.ZoaCalls}/{Hooks.ZoaArmed} lia={Hooks.LiaCalls}/{Hooks.LiaClaimed} " +
            $"uma={Hooks.UmaCalls}/{Hooks.UmaArmed} " +
            $"cards={Hooks.CardsBuilt}/{Hooks.CardsOurs} gate={Hooks.PlayGateCalls}/{Hooks.PlayGateYes} " +
            // The selection the game last applied, and how often a difficulty change was given the
            // reload the game would not have done by itself. `askedNoPicture` above is the other
            // half: it counts the times a jacket was asked for and the difficulty could not be told,
            // which is what an empty background on a song with one picture per difficulty looks like.
            //
            // `pack` is the same pair for the pack screen: how often a difficulty was applied there,
            // and how many cards were rebuilt because of one. The second number stays at zero while
            // no difficulty changes on that screen, which is what says the rebuild is not running
            // when it has nothing to do.
            $"apply={Hooks.ApplyCalls}/{Hooks.ApplyReloaded} " +
            $"diff={Selection.AppliedDifficulty} song={Selection.AppliedSong} " +
            $"pack={Hooks.PackDifficultyCalls}/{Hooks.PackCardsRebuilt} " +
            $"switch={Hooks.SceneSwitchCalls}@{Hooks.SceneSwitchLastState}" +
            // What the scene switch is parked on, when it is parked on something. This is the field
            // that separates a stall from a frame loop in one glance.
            (Hooks.WaitProgress() is string waiting ? $" wait={waiting}" : "") + " " +
            $"jackets={JacketCatalog.Asked}/{JacketCatalog.Claimed} " +
            $"reads={AudioCatalog.Served}/{AudioCatalog.ServedBytes} " +
            // The read bookkeeping. `outstanding` is the game's own count of reads handed to the
            // native side and not yet finished: it is the number this mod's leak used to inflate,
            // and it should hover around the few reads actually in flight, not grow with every
            // chunk served. `skip` is the thing that leak is suspected of causing — a read the
            // dispatcher decided to drop, which the game fails without reading. Zero is the answer
            // that says the mechanism never fires.
            $"outstanding={PendingReads.Outstanding()} skip={PendingReads.Skipped} " +
            $"released={PendingReads.Released} noread={PendingReads.NotTaken}";

        // ---------------------------------------------------------------- reading it back

        private static string Sizes(IntPtr songData, IntPtr packData, IntPtr packAssets)
        {
            return $"allSongInfo={Length(ArrayAt(songData, "SongData", "allSongInfo"))} " +
                   $"_ffb={Length(ArrayAt(songData, "SongData", "_ffb"))} " +
                   $"_Efb={Count(DictAt(songData, "SongData", "_Efb"))} " +
                   $"packInfo={Length(ArrayAt(packData, "PackData", "PackInfo"))} " +
                   $"_fYA={Count(DictAt(packData, "PackData", "_fYA"))} " +
                   $"packToAssets={Length(ArrayAt(packAssets, "PackSelectSceneAssets", "packToAssets"))}";
        }

        private static void DumpSong(IntPtr songs, int index, ChartInfo expected)
        {
            IntPtr song = songs + Offsets.Runtime.ArrayDataOffset + index * SongInfoSize;

            int id = Memory.I32(song + SongInfoId) & 0xFFFF;
            string baseName = Memory.Text(Memory.Ptr(song + SongInfoBaseName), 128);
            IntPtr chartArray = Memory.Ptr(song + SongInfoCharts);
            IntPtr title = Memory.Ptr(song + SongInfoTitleReading);
            IntPtr artist = Memory.Ptr(song + SongInfoArtistReading);

            line($"song[{index}]       id={id} base='{baseName}' charts={Length(chartArray)} " +
                 $"readings={(title == IntPtr.Zero ? "null" : "set")} " +
                 $"artist(ptr)={(artist == IntPtr.Zero ? "null" : "set")} " +
                 $"preview={Memory.F32(song + 0x20)}..{Memory.F32(song + 0x24)}s " +
                 $"reward={Memory.I32(song + 0x3C)}");

            if (baseName != expected.BaseName)
                line($"  !! base name is '{baseName}', expected '{expected.BaseName}'");

            // One difficulty is enough to prove the shape; the other three are copies of it.
            if (chartArray != IntPtr.Zero && Length(chartArray) > 0)
            {
                IntPtr chart = chartArray + Offsets.Runtime.ArrayDataOffset;
                line($"  chart[0]      id='{Memory.Text(Memory.Ptr(chart), 128)}' " +
                     $"available={Memory.U8(chart + 0x08)} difficulty={Memory.U8(chart + 0x09)} " +
                     $"designer='{Memory.Text(Memory.Ptr(chart + 0x10), 128)}' " +
                     $"rating={Memory.I32(chart + 0x20)} " +
                     $"section='{Memory.Text(Memory.Ptr(chart + 0x28), 16)}'");
            }
        }

        private static void DumpPack(IntPtr packData)
        {
            IntPtr packs = ArrayAt(packData, "PackData", "PackInfo");
            int count = Length(packs);

            for (int i = 0; i < count && i < 4; i++)
            {
                IntPtr pack = packs + Offsets.Runtime.ArrayDataOffset + i * 0x18;
                string slug = Memory.Text(Memory.Ptr(pack + 0x08), 64);
                line($"pack[{i}]         id={Memory.I32(pack) & 0xFFFF} slug='{slug}' " +
                     $"songs={Length(Memory.Ptr(pack + 0x10))}");
            }
        }

        private static void DumpPackVisuals(IntPtr packAssets)
        {
            if (!Memory.LooksLikeObject(packAssets)) { line("pack visuals    NOT REACHED"); return; }

            IntPtr table = Memory.Ptr(packAssets + 0x18);
            int count = Length(table);
            line($"packToAssets    {count} rows");

            // Only the first slot of a row is checked: if the row exists at all, the rest of it is a
            // copy of row zero and the read that matters is whether the index is in range.
            for (int i = 0; i < count; i++)
            {
                IntPtr row = table + Offsets.Runtime.ArrayDataOffset + i * 0x80;
                IntPtr material = Memory.Ptr(row);
                line($"  row[{i}]        first material {(Memory.LooksLikeObject(material) ? "set" : "NULL")}");
            }
        }

        private static IntPtr ArrayAt(IntPtr obj, string type, string field)
        {
            if (!Memory.LooksLikeObject(obj)) return IntPtr.Zero;
            int offset = FieldResolver.Lookup(type, field);
            if (offset < 0) { line($"  !! {type}.{field} was not found by name"); return IntPtr.Zero; }
            return Memory.Ptr(obj + offset);
        }

        private static IntPtr DictAt(IntPtr obj, string type, string field) => ArrayAt(obj, type, field);

        private static int Length(IntPtr array) =>
            Memory.LooksLikeObject(array) ? Memory.I32(array + Offsets.Runtime.ArrayLength) : -1;

        private static int Count(IntPtr dictionary) =>
            Memory.LooksLikeObject(dictionary) ? Memory.I32(dictionary + Offsets.Runtime.DictCount) : -1;

        private static string Path(ChartInfo info) => System.IO.Path.GetFileName(info.Folder);

        private static void line(string text) => Diagnostics.Info("probe: " + text);
    }
}
