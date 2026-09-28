using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace InFalsusChartLoader
{
    /// <summary>
    /// The <c>Charts/</c> folder: where it is, and what is in it.
    ///
    /// <para>
    /// The folder sits beside the <c>mod</c> folder the DLL was loaded from — the DLL in
    /// <c>&lt;root&gt;/mod/InFalsusChartLoader.dll</c> reads <c>&lt;root&gt;/Charts</c>. That layout is
    /// the one the caller asked for and is why this class uses the assembly's own location, which is
    /// the one thing the sibling mod in this repository deliberately never does.
    /// </para>
    /// <para>
    /// A folder is imported whole or not at all. Its four charts are decoded once, here, rather than
    /// when the song is played: a chart that the game's decoder rejects would otherwise be a silent
    /// empty play-through, and the point of reading them now is that a broken folder never reaches
    /// the song list in the first place.
    /// </para>
    /// </summary>
    internal static class ChartLibrary
    {
        /// <summary>The folder name to look for, and the name in the "beside the mod folder" rule.</summary>
        private const string FolderName = "Charts";

        /// <summary>The folder the DLL was loaded from.</summary>
        internal static string ModFolder()
        {
            try
            {
                string location = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(location)) return Path.GetDirectoryName(location);
            }
            catch (Exception e)
            {
                Diagnostics.Warn($"could not read this assembly's path: {Diagnostics.Describe(e)}");
            }
            return null;
        }

        /// <summary>
        /// Where the charts are, creating it if it is not there.
        ///
        /// Beside the mod folder first — <c>&lt;root&gt;/Charts</c>, where <c>&lt;root&gt;</c> is the
        /// parent of the folder the DLL was loaded from — because that is the layout this mod was
        /// asked for. Beside the DLL second, because "next to the mod folder" and "next to the DLL"
        /// are one sentence read two ways and only one of them can be right in a given install.
        ///
        /// If neither exists the first is created, empty, and returned: the answer to "where do I put
        /// charts" should be a folder someone can open, not a path in a log.
        /// </summary>
        internal static string Locate()
        {
            string mod = ModFolder();
            if (mod == null) return null;

            // Beside the mod folder first, which is the layout this mod was asked for. Beside the DLL
            // second, because "the folder next to the mod" and "the folder next to the DLL" are the
            // same sentence read two ways and only one of them can be right in a given install.
            DirectoryInfo parent = Directory.GetParent(mod);
            string[] candidates =
            {
                parent == null ? null : Path.GetFullPath(Path.Combine(parent.FullName, FolderName)),
                Path.GetFullPath(Path.Combine(mod, FolderName)),
            };

            foreach (string candidate in candidates)
            {
                if (candidate == null) continue;
                if (Directory.Exists(candidate))
                {
                    Diagnostics.Info($"{FolderName} folder: {candidate}");
                    return candidate;
                }
            }

            // Neither is there. Make the one the layout calls for, rather than only naming it: an
            // empty folder is a place to put something, and a path in a log is a scavenger hunt.
            string preferred = candidates[0] ?? candidates[1];
            if (preferred == null)
            {
                Diagnostics.Warn("there is nowhere to put a charts folder");
                return null;
            }

            try
            {
                Directory.CreateDirectory(preferred);
                Diagnostics.Info($"created the {FolderName} folder at {preferred}");
                Diagnostics.Info("drop one folder per song in there, each holding an `if` and its files");
                return preferred;
            }
            catch (Exception e)
            {
                Diagnostics.Warn($"no {FolderName} folder, and one could not be created at {preferred}: " +
                                 Diagnostics.Describe(e));
                return preferred;
            }
        }

        /// <summary>
        /// Every chart folder under <paramref name="root"/> that is complete, in a stable order.
        ///
        /// <paramref name="validateCharts"/> is asked about a folder whose files are all present and
        /// whose <c>if</c> is well formed; it returns null when the four charts decode, or a reason
        /// when they do not. Nothing is half-imported: a folder that fails any check is left out of
        /// the result and its reason is logged, and the song list simply does not contain it.
        /// </summary>
        internal static List<ChartInfo> Import(string root, Func<ChartInfo, string> validateCharts)
        {
            var imported = new List<ChartInfo>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                Diagnostics.Warn($"no {FolderName} folder at {root}; nothing to load");
                return imported;
            }

            string[] folders;
            try
            {
                folders = Directory.GetDirectories(root);
                Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e)
            {
                Diagnostics.Warn($"{root} could not be listed: {Diagnostics.Describe(e)}");
                return imported;
            }

            foreach (string folder in folders)
            {
                string name = Path.GetFileName(folder);

                if (!ChartInfoReader.TryRead(folder, out ChartInfo info, out string reason))
                {
                    Diagnostics.Warn($"chart folder '{name}' skipped: {reason}");
                    continue;
                }

                reason = validateCharts(info);
                if (reason != null)
                {
                    Diagnostics.Warn($"chart folder '{name}' skipped: {reason}");
                    continue;
                }

                Diagnostics.Info($"chart folder '{name}': '{info.Name}' by {info.Composer}, " +
                                 $"{info.Level[0]}/{info.Level[1]}/{info.Level[2]}/{info.Level[3]}");
                imported.Add(info);
            }

            return imported;
        }
    }
}
