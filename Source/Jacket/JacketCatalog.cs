using System;
using System.Collections.Generic;

namespace InFalsusChartLoader
{
    /// <summary>
    /// The custom jackets, and the handshake that puts one on screen.
    ///
    /// <para>
    /// Showing a jacket is two steps in the game and neither of them takes a picture. The song-select
    /// asks for an <c>AssetReferenceT&lt;Material&gt;</c> and then loads it through Addressables —
    /// which only resolves things that are in the game's own catalogue, and a file on disk is not.
    /// So this mod answers both halves: the first hook hands back a reference the game can load, and
    /// the second returns the material this mod built in its place.
    /// </para>
    /// <para>
    /// The pair is matched by pointer identity: the reference the first hook returned is remembered,
    /// and the second hook only intervenes for that exact object. Without it, a pending material
    /// could be handed to an unrelated image load — the material hook serves every asset the game
    /// loads, not just jackets, and a mismatch there would be a wrong picture rather than an error.
    /// </para>
    /// <para>
    /// Jackets are keyed by base name, not by song id, because the hook that has to recognise the
    /// song is handed the song record itself — which carries its base name at a known offset and its
    /// id only after this mod has assigned one.
    /// </para>
    /// <para>
    /// A song holds four of them, one per difficulty, because a difficulty is allowed to look unlike
    /// its neighbours. Which one is used depends on who is asking: the per-difficulty reference
    /// getter knows the difficulty from the record it was handed, while the per-song one does not and
    /// takes it from the game's own selection — see <see cref="Selection"/>. Four entries naming one
    /// file cost one texture: materials are built per distinct path, so the common case is one build
    /// and four references to it.
    /// </para>
    /// </summary>
    internal static class JacketCatalog
    {
        /// <summary>`SongInfo.BaseName` — the one resolved set is <see cref="Offsets.Song"/>.</summary>
        private static int SongInfoBaseName => Offsets.Song.BaseName;

        /// <summary>Per song, one material per difficulty. Zero where that difficulty has none.</summary>
        private static readonly Dictionary<string, IntPtr[]> ByBase =
            new Dictionary<string, IntPtr[]>(StringComparer.Ordinal);

        /// <summary>
        /// The songs this mod built a jacket *for*, whether or not one came out.
        ///
        /// Kept apart from <see cref="ByBase"/> so that a song whose picture failed can still be
        /// recognised when the game asks for it. Without this the two cases — the song never reached
        /// the screen, and it reached the screen with a missing picture — produce exactly the same
        /// log, which is a question a single run has to be able to settle.
        /// </summary>
        private static readonly HashSet<string> Imported = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The reference the song was just told to load, and what to load in its place.</summary>
        private static IntPtr _armed;
        private static IntPtr _armedMaterial;

        internal static int Count => ByBase.Count;

        /// <summary>Handshakes seen. Counted rather than logged — see Find.</summary>
        internal static long Asked, Claimed;

        /// <summary>`SongChartInfo` 的步长 —— 量出来的（`Offsets.Resolve` → `Offsets.Chart.Size`）。</summary>
        private static int ChartInfoSize => Offsets.Chart.Size;

        /// <summary>
        /// 每首自制歌的 `ChartInfos` **数组对象**，用来把 `_ZOA` 收到的那一个元素指针认回来。
        ///
        /// 曲绘有两条路，各自有"取引用"和"加载"两半：
        ///
        /// <code>
        ///   _apA(in SongInfo, bool)      + _MIA&lt;T&gt;(…)      ← 卡片走这条
        ///   _ZOA(in SongChartInfo, bool) + _LIA&lt;T&gt;(…)      ← 加载/转场界面走这条
        /// </code>
        ///
        /// 后一条里 `_ZOA` 只拿到一个 `SongChartInfo` —— **里面没有歌，只有谱面 id**，
        /// 而两首歌完全可以用同一个谱面文件名，所以按 id 认会撞。按**地址**认不会：
        /// 那个元素一定落在某首自制歌的 `ChartInfos` 数组里，逐一比四个槽位即可。
        /// </summary>
        private static readonly List<(IntPtr Charts, ChartInfo Info)> ChartArrays =
            new List<(IntPtr, ChartInfo)>();

        /// <summary>登记一首自制歌的 `ChartInfos` 数组，供 `_ZOA` 那条路认歌。</summary>
        internal static void RegisterCharts(IntPtr charts, ChartInfo info)
        {
            if (info == null || !Memory.LooksLikeObject(charts)) return;
            ChartArrays.Add((charts, info));
        }

        /// <summary>
        /// `_ZOA` 那条路的认歌 + 取材质。`chartInfo` 是它收到的那个 `SongChartInfo` 的地址。
        ///
        /// <para>
        /// 两条判据，因为调用者有**两种传法**，而它们给出的是两种不同的指针：
        /// </para>
        /// <list type="number">
        /// <item><description>
        /// <b>地址</b> —— 传的是数组元素本身（转场界面就是：`ref …ChartInfos[i]`）。那个地址
        /// 一定落在某首自制歌的 `ChartInfos` 数组里，逐一比四个槽位即可，精确且不怕重名。
        /// </description></item>
        /// <item><description>
        /// <b>名字</b> —— 传的是**结构体副本**的地址（`SongInfoContainer` 把记录拷进局部变量
        /// 再取地址，而 `SongChartInfo` 是 0x30 字节的 struct）。副本落在栈上，地址认不出来，
        /// 但副本里唯一的引用字段 `Id`（+0x00）仍指着同一个字符串 —— 谱面的文件名。
        /// **重名时放弃**：两首歌用同一个文件名的话，这个名字说明不了是哪一首，宁可退回兜底
        /// 也不能把别人的曲绘贴上去。
        /// </description></item>
        /// </list>
        /// </summary>
        internal static bool FindByChart(IntPtr chartInfo, out IntPtr material)
        {
            material = IntPtr.Zero;
            if (!Memory.LooksLikeObject(chartInfo)) return false;

            foreach ((IntPtr charts, ChartInfo info) in ChartArrays)
            {
                IntPtr first = charts + Offsets.Runtime.ArrayDataOffset;
                for (int d = 0; d < ChartInfo.Difficulties; d++)
                {
                    if (first + d * ChartInfoSize != chartInfo) continue;

                    // The slot says which difficulty this is, so the picture follows from the address
                    // alone — which is the reason this criterion is the first one tried.
                    if (!ByBase.TryGetValue(info.BaseName, out IntPtr[] byDifficulty)) return false;

                    material = At(byDifficulty, d);
                    return material != IntPtr.Zero;
                }
            }

            string id = Memory.Text(Memory.Ptr(chartInfo), 128);
            if (id == null) return false;

            // The copy carries the chart's file name, and that name is one of the four the `if` file
            // listed — so the index it matched at is the difficulty, recovered the same way the
            // address criterion recovers it.
            ChartInfo owner = null;
            int at = -1;
            foreach ((IntPtr _, ChartInfo info) in ChartArrays)
            {
                for (int d = 0; d < ChartInfo.Difficulties; d++)
                {
                    if (info.ChartNames[d] != id) continue;
                    if (owner != null && owner != info) return false;
                    owner = info;
                    at = d;
                }
            }

            if (owner == null) return false;
            if (!ByBase.TryGetValue(owner.BaseName, out IntPtr[] ownerMaterials)) return false;

            material = At(ownerMaterials, at);
            return material != IntPtr.Zero;
        }

        /// <summary>
        /// One of a song's four materials, or zero — and zero means the caller shows the game's own
        /// fallback instead.
        ///
        /// That is what an unknown difficulty gets. Showing the first slot's picture instead would be
        /// a picture belonging to another difficulty whenever a folder gives each one its own, and a
        /// wrong picture is worse than none: there is nothing to be gained by covering for a
        /// difficulty that could not be read, because the reader is what should be fixed.
        ///
        /// The one case that is not a guess is a folder naming the same file for all four — every
        /// choice gives the same material, so there is no wrong one to give. That is the loop below,
        /// and it is a fact about the folder rather than a fallback.
        /// </summary>
        private static IntPtr At(IntPtr[] materials, int difficulty)
        {
            if (difficulty >= 0 && difficulty < materials.Length) return materials[difficulty];

            IntPtr only = IntPtr.Zero;
            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] == IntPtr.Zero) return IntPtr.Zero;
                if (i == 0) only = materials[i];
                else if (materials[i] != only) return IntPtr.Zero;
            }

            return only;
        }

        /// <summary>
        /// Builds and keeps the jackets for one chart folder. False when any of the four failed, with
        /// that failure as the reason — but whatever did build is kept either way, because a song with
        /// three pictures is drawn correctly on three difficulties and a missing one is a case the
        /// game already handles.
        ///
        /// One build per distinct file: a folder naming the same picture four times — the common case
        /// — makes one texture and four references to it.
        /// </summary>
        internal static bool Add(ChartInfo info, out string reason)
        {
            Imported.Add(info.BaseName);
            reason = null;

            var materials = new IntPtr[ChartInfo.Difficulties];
            var built = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            bool all = true;

            for (int d = 0; d < materials.Length; d++)
            {
                string path = info.PicturePaths[d];

                if (!built.TryGetValue(path, out IntPtr material))
                {
                    material = JacketFactory.Build(path, out string why);
                    built[path] = material;

                    if (material == IntPtr.Zero)
                    {
                        all = false;
#if DEBUG
                        if (reason == null) reason = $"difficulty {d}: {why}";
#else
                        _ = why;
#endif
                    }
                }

                materials[d] = material;
            }

            ByBase[info.BaseName] = materials;
            return all;
        }

        /// <summary>
        /// Whether a song record names one of this mod's songs.
        ///
        /// Separate from <see cref="Find"/>, which also answers *what to show*: this one is the
        /// question "is this one of ours" on its own, so a caller that only counts can ask it without
        /// arming anything.
        /// </summary>
        internal static bool IsOurs(IntPtr songInfo)
        {
            string baseName = NameOf(songInfo);
            return baseName != null && Imported.Contains(baseName);
        }

        /// <summary>
        /// The name a song record carries, or null when it cannot be read.
        ///
        /// The pointer is checked before it is dereferenced, like every other pointer this mod takes
        /// from the game. It was not, once: a caller that passed something which was not a song record
        /// took the process's memory with it, and because that caller was a diagnostic the failure
        /// landed as a fault on a hot path — every detour in the mod switched off mid-run.
        /// </summary>
        internal static string NameOf(IntPtr songInfo)
        {
            // The offset this reads belongs to the song tables (`Offsets.Resolve`), and this is
            // reached from the jacket detours — which are live from startup and stay live for the
            // whole of a run whose registration failed. Before the tables are resolved that offset
            // is -1, and -1 is not an offset: the read would be aimed one byte before the record.
            // Nothing can be one of this mod's songs before the songs are registered anyway, so
            // "not ours" is the honest answer while the tables are not up.
            if (!Offsets.Ready) return null;
            if (!Memory.LooksLikeObject(songInfo)) return null;

            return Memory.Text(Memory.Ptr(songInfo + SongInfoBaseName), 128);
        }

        /// <summary>
        /// Whether the song being asked about is one of this mod's, and if so arms the handshake.
        ///
        /// The caller must have already obtained the reference the game will load and pass it to
        /// <see cref="Arm"/>.
        /// </summary>
        internal static bool Find(IntPtr songInfo, out IntPtr material)
        {
            material = IntPtr.Zero;

            string baseName = NameOf(songInfo);
            if (baseName == null) return false;

            if (ByBase.TryGetValue(baseName, out IntPtr[] materials))
            {
                // The card asks for a song, not a difficulty, so which picture it shows is the
                // difficulty the game is currently on -- and the second of Selection's two sources
                // is where that comes from, off the song-select scene itself.
                material = At(materials, Selection.Difficulty());
                if (material != IntPtr.Zero)
                {
                    // Counted, not logged: this runs dozens of times in a few milliseconds whenever
                    // the song list repaints, and a log line each is a frame-time cost on a path the
                    // game's transitions are timing-sensitive about.
                    Asked++;
                    return true;
                }
            }

            if (Imported.Contains(baseName))
            {
                // Two ways to reach here and the line has to cover both: none of the four pictures was
                // built, or they were but which difficulty is selected could not be told. Naming only
                // the first would send the next reader looking for a build failure that is not there.
                Hooks.ApaAsked++;
                Diagnostics.Warn($"jacket asked for '{baseName}' - ours, but no picture was available " +
                                 "for the selected difficulty");
            }

            return false;
        }

        internal static void Arm(IntPtr reference, IntPtr material)
        {
            _armed = reference;
            _armedMaterial = material;
        }

        internal static void Disarm()
        {
            _armed = IntPtr.Zero;
            _armedMaterial = IntPtr.Zero;
        }

        /// <summary>
        /// The material to use for a load, when the load is the one just armed.
        ///
        /// Consumes the arming whatever the answer: a pending pair that outlived its load would be
        /// waiting for the next one.
        /// </summary>
        internal static bool TryClaim(IntPtr reference, out IntPtr material)
        {
            material = IntPtr.Zero;
            if (_armed == IntPtr.Zero || reference != _armed) return false;

            material = _armedMaterial;
            Disarm();
            Claimed++;
            return true;
        }
    }
}
