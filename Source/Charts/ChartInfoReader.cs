using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

namespace InFalsusChartLoader
{
    /// <summary>
    /// Reads and vets one chart folder's <c>if</c> file.
    ///
    /// <para>
    /// Everything here is a rejection rule. A folder either produces a complete <see cref="ChartInfo"/>
    /// or it produces nothing at all plus a reason — there is no half-imported song, because a song
    /// missing one of its four difficulties has nowhere to be shown and a song whose audio is absent
    /// cannot be played.
    /// </para>
    /// <para>
    /// The rules come from <c>Charts.EXAMPLE/READ</c>: the three arrays must be exactly four long,
    /// no field may be missing, and the audio and image formats are restricted to what the game
    /// itself can decode. On top of those, a referenced file that is not there is a rejection — a
    /// name in the file is a claim about the disk, and the disk is what decides.
    /// </para>
    /// <para>
    /// Two fields are newer than that document and are described in <c>IF_FORMAT_V2.md</c>:
    /// <c>id</c>, which is the name the song is filed under, and <c>preview_seconds</c>, the window
    /// the song-select previews. Both are <b>required</b>, like every other field. They used to be
    /// optional, defaulting to the folder's name and to the game's own 20-to-30-second window; that
    /// is gone. A song silently filed under a name its author did not choose, previewing a window
    /// its author did not set, is a defect the author cannot see — and neither default was ever a
    /// decision anybody made, only one nobody noticed. Leaving either out is a rejection, and the
    /// reason names the field.
    /// <c>picture</c> additionally accepts the older single-name form.
    /// </para>
    /// <para>
    /// Every reason assignment is inside <c>#if DEBUG</c>. That looks like clutter and is not: the
    /// only consumer is a <c>[Conditional("DEBUG")]</c> log call, so in a Release build these
    /// messages can never be printed — and a string built in a normal method ships whether or not
    /// anything reads it. Leaving them unguarded costs the release about a kilobyte of text that
    /// does nothing, which is the exact thing build.bat's artifact check exists to catch. The
    /// <c>#if</c> cannot be moved onto a helper either: a conditional method may not have an
    /// <c>out</c> parameter, and every one of these sets one.
    /// </para>
    /// </summary>
    internal static class ChartInfoReader
    {
        /// <summary>
        /// Audio containers this mod will hand to the game.
        ///
        /// The game's own audio is Ogg Vorbis — its <c>.wav</c> files are Ogg streams under a
        /// mislabelled extension — and FMOD, which is what plays it, also has MP3 and RIFF/WAVE
        /// compiled in.
        /// </summary>
        private static readonly string[] AudioExtensions = { ".wav", ".mp3", ".ogg" };

        /// <summary>
        /// Image formats a jacket may be in — PNG only.
        ///
        /// Not a preference. The game reaches every image through Addressables and has no
        /// decode-from-bytes entry point in its metadata, so this mod has to decode the file itself,
        /// and a PNG decoder is one this mod can carry while a JPEG decoder is not. `READ` makes the
        /// format conditional on exactly this ("若并不支持 jpg 则合法图片格式为 png"), so PNG-only is
        /// the branch that applies. Accepting `.jpg` here would be accepting a file nothing can draw.
        /// </summary>
        private static readonly string[] ImageExtensions = { ".png" };

        /// <summary>The file that carries a folder's metadata. Lower case, and matched as written.</summary>
        internal const string InfoFileName = "if";

        /// <summary>
        /// Reads <paramref name="folder"/>. On success <paramref name="info"/> is complete and every
        /// path in it exists. On failure it is null and <paramref name="reason"/> says which rule
        /// broke — in a Debug build, where the value is actually filled in.
        /// </summary>
        internal static bool TryRead(string folder, out ChartInfo info, out string reason)
        {
            info = null;
            reason = null;

            string ifPath = Path.Combine(folder, InfoFileName);
            if (!File.Exists(ifPath))
            {
#if DEBUG
                reason = $"no {InfoFileName} file";
#endif
                return false;
            }

            string text;
            try
            {
                text = File.ReadAllText(ifPath);
            }
            catch (Exception e)
            {
#if DEBUG
                reason = $"{InfoFileName} could not be read: {Diagnostics.Describe(e)}";
#else
                _ = e;
#endif
                return false;
            }

            JObject root;
            try
            {
                root = JObject.Parse(text);
            }
            catch (Exception e)
            {
#if DEBUG
                reason = $"{InfoFileName} is not valid JSON: {Diagnostics.Describe(e)}";
#else
                _ = e;
#endif
                return false;
            }

            string folderName = Path.GetFileName(folder);
            var built = new ChartInfo { Folder = folder, FolderName = folderName };

            // `id` names the chart: the game files the song under it and builds `{id}{difficulty}.spc`
            // from it, so it is the string everything downstream answers to. Required, and
            // deliberately not defaulted to the folder's name — that default made a missing `id` look
            // like a working song under a name its author never chose.
            if (!Text(root, "id", out string id, out reason)) return false;
            built.BaseName = id;

            if (!Text(root, "name", out built.Name, out reason)) return false;
            if (!Text(root, "composer", out built.Composer, out reason)) return false;
            if (!Text(root, "illust", out built.Illust, out reason)) return false;

            // `song` names a file in this folder and it must be there: an `if` that points at nothing
            // describes a song that cannot be played.
            if (!LocalFile(root, folder, "song", AudioExtensions, out built.SongPath, out reason)) return false;

            if (!Pictures(root, folder, out built.PicturePaths, out reason)) return false;
            if (!Preview(root, out built.PreviewStart, out built.PreviewEnd, out reason)) return false;

            if (!Texts(root, "Charter", out built.Charter, out reason)) return false;
            if (!Levels(root, "lv", out built.Level, out reason)) return false;
            if (!ChartFiles(root, folder, out built.ChartPaths, out built.ChartNames, out reason)) return false;

            info = built;
            return true;
        }

        /// <summary>A required, non-empty string field.</summary>
        private static bool Text(JObject root, string key, out string value, out string reason)
        {
            value = null;
            reason = null;

            JToken token = root[key];
            if (token == null || token.Type == JTokenType.Null)
            {
#if DEBUG
                reason = $"\"{key}\" is missing";
#endif
                return false;
            }

            if (token.Type != JTokenType.String)
            {
#if DEBUG
                reason = $"\"{key}\" is not a string";
#endif
                return false;
            }

            value = token.Value<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
#if DEBUG
                reason = $"\"{key}\" is empty";
#endif
                return false;
            }

            return true;
        }

        /// <summary>
        /// A string field that names a file in the folder, with an extension from
        /// <paramref name="allowed"/>. The file has to exist, and the name has to stay inside the
        /// folder — a name with a separator or a <c>..</c> in it is refused rather than resolved.
        /// </summary>
        private static bool LocalFile(JObject root, string folder, string key, string[] allowed,
                                      out string path, out string reason)
        {
            path = null;
            if (!Text(root, key, out string name, out reason)) return false;

            return LocalFileAt(folder, key, name, allowed, out path, out reason);
        }

        /// <summary>
        /// The rules for one file name from an `if` file, wherever it appeared.
        ///
        /// Split out from <see cref="LocalFile"/> because the jacket list names four files under one
        /// key, and four copies of a rule that lives in two places is how one of them ends up
        /// checking less than the other.
        /// </summary>
        private static bool LocalFileAt(string folder, string key, string name, string[] allowed,
                                        out string path, out string reason)
        {
            path = null;
            reason = null;

            if (Path.GetFileName(name) != name)
            {
#if DEBUG
                reason = $"\"{key}\" names something outside the folder: {name}";
#endif
                return false;
            }

            string extension = Path.GetExtension(name).ToLowerInvariant();
            if (Array.IndexOf(allowed, extension) < 0)
            {
#if DEBUG
                reason = $"\"{key}\" is {extension}, which the game cannot decode " +
                         $"(accepted: {string.Join(", ", allowed)})";
#endif
                return false;
            }

            path = Path.Combine(folder, name);
            if (!File.Exists(path))
            {
#if DEBUG
                reason = $"\"{key}\" points at {name}, which is not in the folder";
#endif
                path = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// The jacket images: one file per difficulty.
        ///
        /// A single string counts as four copies of itself. That is the shape the previous format
        /// had, and it is still what most folders want — one picture for a whole song — so refusing
        /// it would break every `if` file written before the list existed for no gain. Four entries
        /// is the general case, and lets a difficulty look unlike its neighbours.
        /// </summary>
        private static bool Pictures(JObject root, string folder, out string[] paths, out string reason)
        {
            paths = null;
            reason = null;

            JToken token = root["picture"];
            if (token == null || token.Type == JTokenType.Null)
            {
#if DEBUG
                reason = "\"picture\" is missing";
#endif
                return false;
            }

            if (token.Type == JTokenType.String)
            {
                if (!LocalFile(root, folder, "picture", ImageExtensions, out string single, out reason)) return false;

                var same = new string[ChartInfo.Difficulties];
                for (int i = 0; i < same.Length; i++) same[i] = single;
                paths = same;
                return true;
            }

            if (token.Type != JTokenType.Array)
            {
#if DEBUG
                reason = "\"picture\" is neither a name nor a list of names";
#endif
                return false;
            }

            var array = (JArray)token;
            if (array.Count != ChartInfo.Difficulties)
            {
#if DEBUG
                reason = $"\"picture\" has {array.Count} entries, not {ChartInfo.Difficulties}";
#endif
                return false;
            }

            var result = new string[ChartInfo.Difficulties];
            for (int i = 0; i < result.Length; i++)
            {
                JToken item = array[i];
                if (item.Type != JTokenType.String || string.IsNullOrWhiteSpace(item.Value<string>()))
                {
#if DEBUG
                    reason = $"\"picture\"[{i}] is not a name";
#endif
                    return false;
                }

                if (!LocalFileAt(folder, "picture", item.Value<string>(), ImageExtensions,
                                 out result[i], out reason)) return false;
            }

            paths = result;
            return true;
        }

        /// <summary>
        /// The preview window, <c>[start, end]</c> in seconds.
        ///
        /// Required, like the rest of the file. It used to be optional, with the two values written
        /// as -1 so the game's own window applied (20 to 30 seconds) — which is a different part of
        /// the song from the one the author meant, and neither the game nor the log said so. A window
        /// of zeros is a real window at the very start of a song, so there is no value that could
        /// have stood for "not said".
        /// </summary>
        private static bool Preview(JObject root, out float start, out float end, out string reason)
        {
            start = 0f;
            end = 0f;
            reason = null;

            JToken token = root["preview_seconds"];
            if (token == null || token.Type == JTokenType.Null)
            {
#if DEBUG
                reason = "\"preview_seconds\" is missing";
#endif
                return false;
            }

            if (token.Type != JTokenType.Array)
            {
#if DEBUG
                reason = "\"preview_seconds\" is not a list";
#endif
                return false;
            }

            var array = (JArray)token;
            if (array.Count != 2)
            {
#if DEBUG
                reason = $"\"preview_seconds\" has {array.Count} entries, not 2";
#endif
                return false;
            }

            if (!Seconds(array[0], 0, out start, out reason)) return false;
            if (!Seconds(array[1], 1, out end, out reason)) return false;
            return true;
        }

        /// <summary>
        /// One of the two preview values.
        ///
        /// A number is taken as one, and so is a number written as a string — the field was
        /// specified with its values quoted, so a reader that refused that would be refusing the
        /// format as written. Parsed invariantly: a locale that reads <c>0,5</c> as a half would
        /// otherwise turn a window into something else entirely.
        /// </summary>
        private static bool Seconds(JToken item, int index, out float value, out string reason)
        {
            value = 0f;
            reason = null;

            switch (item.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    value = item.Value<float>();
                    return true;

                case JTokenType.String:
                    if (float.TryParse(item.Value<string>(), NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out value)) return true;
                    break;
            }

#if DEBUG
            reason = $"\"preview_seconds\"[{index}] is not a number of seconds";
#endif
            return false;
        }

        /// <summary>One of the three arrays that must be exactly <see cref="ChartInfo.Difficulties"/> long.</summary>
        private static bool RequiredArray(JObject root, string key, out JArray array, out string reason)
        {
            array = null;
            reason = null;

            JToken token = root[key];
            if (token == null || token.Type == JTokenType.Null)
            {
#if DEBUG
                reason = $"\"{key}\" is missing";
#endif
                return false;
            }

            if (token.Type != JTokenType.Array)
            {
#if DEBUG
                reason = $"\"{key}\" is not an array";
#endif
                return false;
            }

            array = (JArray)token;
            if (array.Count != ChartInfo.Difficulties)
            {
#if DEBUG
                reason = $"\"{key}\" has {array.Count} entries, not {ChartInfo.Difficulties}";
#endif
                return false;
            }

            return true;
        }

        /// <summary>The four chart designers.</summary>
        private static bool Texts(JObject root, string key, out string[] values, out string reason)
        {
            values = null;
            reason = null;
            if (!RequiredArray(root, key, out JArray array, out reason)) return false;

            var result = new string[ChartInfo.Difficulties];
            for (int i = 0; i < result.Length; i++)
            {
                JToken item = array[i];
                if (item.Type != JTokenType.String || string.IsNullOrWhiteSpace(item.Value<string>()))
                {
#if DEBUG
                    reason = $"\"{key}\"[{i}] is not a name";
#endif
                    return false;
                }
                result[i] = item.Value<string>();
            }

            values = result;
            return true;
        }

        /// <summary>The four difficulty ratings. The game stores these as small integers.</summary>
        private static bool Levels(JObject root, string key, out int[] values, out string reason)
        {
            values = null;
            reason = null;
            if (!RequiredArray(root, key, out JArray array, out reason)) return false;

            var result = new int[ChartInfo.Difficulties];
            for (int i = 0; i < result.Length; i++)
            {
                JToken item = array[i];
                if (item.Type != JTokenType.Integer)
                {
#if DEBUG
                    reason = $"\"{key}\"[{i}] is not a whole number";
#endif
                    return false;
                }

                int level = item.Value<int>();
                if (level <= 0)
                {
#if DEBUG
                    reason = $"\"{key}\"[{i}] is {level}";
#endif
                    return false;
                }
                result[i] = level;
            }

            values = result;
            return true;
        }

        /// <summary>
        /// The four chart files. Each has to be in the folder, and each has to be a chart the game's
        /// own decoder will accept — that second half is checked in <see cref="ChartLoaderMod"/>,
        /// which is where the decoder is reachable.
        /// </summary>
        private static bool ChartFiles(JObject root, string folder, out string[] paths,
                                       out string[] chartNames, out string reason)
        {
            paths = null;
            chartNames = null;
            if (!Texts(root, "chart", out string[] names, out reason)) return false;

            var result = new string[ChartInfo.Difficulties];
            for (int i = 0; i < result.Length; i++)
            {
                string name = names[i];
                if (Path.GetFileName(name) != name)
                {
#if DEBUG
                    reason = $"\"chart\"[{i}] names something outside the folder: {name}";
#endif
                    return false;
                }

                string path = Path.Combine(folder, name);
                if (!File.Exists(path))
                {
#if DEBUG
                    reason = $"\"chart\"[{i}] points at {name}, which is not in the folder";
#endif
                    return false;
                }
                result[i] = path;
            }

            paths = result;
            chartNames = names;
            return true;
        }
    }
}
