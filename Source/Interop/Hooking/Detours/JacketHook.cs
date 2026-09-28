using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MelonLoader.NativeUtils;

namespace InFalsusChartLoader
{
    internal static unsafe partial class Hooks
    {
        /// <summary>`SongData._apA(in SongInfo, bool large)` — which reference holds a song's jacket.</summary>
        private const long RvaJacketReference = 0x4C52B0;

        /// <summary>Where `AddressableHandleAutoReleaser` lives, for the runtime's own lookup.</summary>
        private const string Image = "Game.Common.dll";

        private const string Namespace = "ifapp.Game.Common";

        /// <summary>
        /// Fallback address for `AddressableHandleAutoReleaser._MIA&lt;T&gt;` — which loads that reference.
        ///
        /// <para>
        /// ⚠ <b>This RVA is from a build the game no longer runs.</b> The address, and every other RVA in
        /// this mod, comes from `dump.cs`, which was dumped from the `GameAssembly.dll` in this
        /// repository (56 794 112 bytes, 2026-09-12). The installed game's copy is 56 758 272 bytes and
        /// dated 2026-09-20, and its `global-metadata.dat` differs too. Every hook in this mod therefore
        /// resolves <i>by name</i>, through the interop MethodInfo that Cpp2IL generates from the
        /// installed game at launch; the constant is only what to fall back to when that fails, and on
        /// a build it does not describe it lands on whatever now occupies the address.
        /// </para>
        /// <para>
        /// The metadata names the old build's address `_MIA&lt;object&gt;`: the shared body every
        /// reference-type instantiation goes through, and the one the card call sites
        /// (`SmallSongCard._tS`, `LargeSongCard._OS`) call.
        /// </para>
        /// </summary>
        private const long RvaJacketLoad = 0x75C770;

        /// <summary>`SongData._ZOA(in SongChartInfo, bool large)` — the *other* reference getter.</summary>
        private const long RvaJacketChartReference = 0x4BD9A0;

        /// <summary>
        /// `GameplayBackgrounds._UmA(GameplayBackground)` — the in-play background, which the `if`
        /// file's picture also stands in for.
        ///
        /// The name is `_UmA`, not the `_zDA` the demo build's source suggests: demo `$`-names and
        /// shipped `_`-names are not one mapping, and the class has exactly one method that answers
        /// with a reference. The dumper gives it at RVA 0x478680, in `GameplayBackgrounds`
        /// (`ifapp.Game.Data`, `Game.Data.dll`), instance, one `int` parameter.
        /// </summary>
        private const long RvaBackgroundLoad = 0x478680;

        /// <summary>
        /// `AssetReferenceT&lt;Material&gt; SongData._apA(in SongInfo songInfo, bool large)`
        ///
        /// Four arguments, and the two that are easy to get backwards are the first two: `rcx` is the
        /// `SongData` the method was called on, and the song record is `rdx` — a pointer to it,
        /// because a 64-byte struct goes by address. Reading the song out of `rcx` instead lands in
        /// `SongData`'s own header and dereferences whatever is there.
        ///
        /// The record's base name is what identifies the song, and this is the only place in the
        /// jacket path where the song is still known.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ApAFn(IntPtr songData, IntPtr songInfo, byte large, IntPtr methodInfo);

        /// <summary>
        /// `static T _MIA&lt;T&gt;(GameObject, ref AddressableHandleAutoReleaser, AssetReferenceT&lt;T&gt;, bool)`
        ///
        /// A shared-generic: one address serves every `T`, so the hook sees loads that have nothing to
        /// do with jackets and must let them through untouched. It is the reference argument that
        /// tells the two apart, which is why the pair is matched by identity and not by the fact that
        /// something is pending.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr MiaFn(IntPtr owner, IntPtr releaser, IntPtr reference, byte forceReload,
                                      IntPtr methodInfo);

        private static NativeHook<ApAFn> _apa;
        private static ApAFn _apaTramp;
        private static NativeHook<MiaFn> _mia;
        private static MiaFn _miaTramp;

        /// <summary>
        /// `AssetReferenceT&lt;Material&gt; SongData._ZOA(in SongChartInfo chartInfo, bool large)`
        ///
        /// The other half of "which reference holds a jacket", and the one the loading screen uses:
        /// it is keyed by the per-difficulty record rather than by the song. Same ABI as `_apA` —
        /// `rcx` the `SongData`, `rdx` a pointer to the record, `r8b` the size flag — because both
        /// take a record pointer and a bool.
        ///
        /// It looks its reference up in a different table (`SongData._efb`, keyed by the chart's own
        /// id) and falls back to the game's default jacket when the id is not there — which is
        /// exactly what a custom chart got before this hook existed: the loading screen drew the
        /// fallback while the song-select card, which goes through `_apA`, drew ours.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ZoaFn(IntPtr songData, IntPtr chartInfo, byte large, IntPtr methodInfo);

        /// <summary>
        /// `static T _LIA&lt;T&gt;(GameObject owner, AssetReferenceT&lt;T&gt; reference)`
        ///
        /// The second of the two loaders a jacket can go through. The game has exactly two, and both
        /// end in the same instance method that touches Addressables:
        ///
        ///   `_MIA&lt;T&gt;(GameObject, ref AddressableHandleAutoReleaser, AssetReferenceT&lt;T&gt;, bool)`
        ///   `_LIA&lt;T&gt;(GameObject, AssetReferenceT&lt;T&gt;)`
        ///
        /// `_LIA` is the blocking one, and it is what every jacket request outside the song-select
        /// cards uses — the loading/transition screen, the hub, the results screen. Hooked for the
        /// same reason as `_MIA`: it hands back the asset itself, so a material can be returned in
        /// its place.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr LiaFn(IntPtr owner, IntPtr reference, IntPtr methodInfo);

        private static NativeHook<ZoaFn> _zoa;
        private static ZoaFn _zoaTramp;
        private static NativeHook<LiaFn> _lia;
        private static LiaFn _liaTramp;

        /// <summary>
        /// `AssetReferenceT&lt;Material&gt; GameplayBackgrounds._UmA(GameplayBackground)`
        ///
        /// What the inside of a chart is drawn on. The player's own choice is a small enum — the
        /// background is one of a handful of shipped scenes — and the picture that goes with a
        /// custom song is neither in that set nor in the game's catalogue, so it takes the same
        /// route as a jacket: hand back a reference the game can load, and substitute the material
        /// when the load arrives.
        ///
        /// It is the third getter to arm the same handshake, and it is here rather than in a file of
        /// its own because what it arms with comes from the jacket catalogue -- a song's background
        /// is its picture.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr UmaFn(IntPtr backgrounds, int background, IntPtr methodInfo);

        private static NativeHook<UmaFn> _uma;
        private static UmaFn _umaTramp;

        private static bool InstallJacket()
        {
            IntPtr reference = MethodResolver.ByName("SongData", "_apA", RvaJacketReference);
            if (reference == IntPtr.Zero) return false;

            byte[] referencePrologue = Prologue(reference);
            _apa = new NativeHook<ApAFn>
            {
                Target = reference,
                Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte, IntPtr, IntPtr>)&ApADetour,
            };
            _apa.Attach();
            _apaTramp = _apa.Trampoline;
            Diagnostics.Info("SongData._apA hooked");

            // Through the runtime, not through reflection, and with no RVA to fall back to. Both
            // halves of that are deliberate for a generic method:
            //
            //  * Reflection cannot read it at all. The generated field's type contains a type
            //    parameter, `FieldInfo.GetValue` raises `InvalidOperationException: Late bound
            //    operations cannot be performed on fields with types for which
            //    Type.ContainsGenericParameters is true`, MethodResolver catches it and answers zero.
            //    Measured, in the run this was written from.
            //  * The RVA is a fixed address in one build. Falling back to it installs the hook
            //    somewhere plausible-looking that is never called, and `Attach()` reports nothing
            //    wrong — so it fails silently on every build but the one it was measured on, which is
            //    the opposite of what a mod meant to run across versions needs. Refusing is better:
            //    the count in "n/m hooks installed" then says the jacket will not be substituted.
            //
            // A third fact about this method, from `dump.cs`, because it decides what the walk below
            // can return: `_MIA<T>` is declared `// RVA: -1` — the generic definition has no code —
            // and its `GenericInstMethod` block lists exactly one instantiation, `_MIA<object>`, at
            // 0x75C770. That is the body every reference-type call goes to, including the cards'.
            // `script.json` agrees from the other side: the `_MIA<Material>` usage has
            // `MethodAddress: 0` while every entry around it has a real one.
            IntPtr load = MethodResolver.ByRuntime("AddressableHandleAutoReleaser", "_MIA",
                                                   Image, Namespace);
            if (load == IntPtr.Zero)
            {
                // "could not be found" rather than "could not be resolved": this line ships in Release,
                // and build.bat's artifact check keeps `could not be resolved` on its bad list because
                // that phrase belongs to MethodResolver's Debug-only RVA-fallback warning. This is the
                // other kind — a message a release user is meant to see — so it uses the wording the
                // rest of this mod's shipping errors use.
                Diagnostics.Error("AddressableHandleAutoReleaser._MIA could not be found; custom " +
                                  "jackets will not be substituted onto the song list");
                return false;
            }

#if DEBUG
            // What the runtime handed over, so that "resolved" and "resolved to something callable"
            // stop being the same word in the log.
            Diagnostics.Info($"  the method's first words: {JacketFactory.Words(load, 4)}");
#endif

            byte[] loadPrologue = Prologue(load);
            _mia = new NativeHook<MiaFn>
            {
                Target = load,
                Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte, IntPtr, IntPtr>)&MiaDetour,
            };
            _mia.Attach();
            _miaTramp = _mia.Trampoline;
            Diagnostics.Info("AddressableHandleAutoReleaser._MIA hooked");

            bool ok = Landed("_apA", reference, referencePrologue);
            ok &= Landed("_MIA", load, loadPrologue);

            // 另外两条。少了它们，只有选曲卡片有图、别的界面都退回游戏的兜底 —— 这正是被发现
            // 时的形状：选曲界面有曲绘，加载界面没有。曲绘不是一个入口，是 2×2：
            //
            //   取引用：  _apA(in SongInfo, bool)        / _ZOA(in SongChartInfo, bool)
            //   加载：    _MIA<T>(…, ref Releaser, …)    / _LIA<T>(…)
            ok &= InstallChartReference();
            ok &= InstallChartLoader();

            // 第三条取引用。它不是曲绘那一族的成员 —— 它给的是谱面内的背景 —— 但它用的是同一套
            // 握手、同一个材质目录（一首歌的背景就是它的曲绘），所以安装和卸载都挂在这一组上。
            ok &= InstallBackground();
            return ok;
        }

        /// <summary>
        /// `GameplayBackgrounds._UmA` —— 谱面内的背景。
        ///
        /// 与 `_apA` / `_ZOA` 同一套握手，区别只在"认歌"用的是什么：这里连歌都没有，参数只有一个
        /// 玩家选的背景枚举。所以身份只能从**游戏当前选中的那首歌**推（见 <see cref="Selection"/>），
        /// 而这也解释了它为什么必然是对的 —— 背景是进谱面时才要的，那一刻"当前选中"就是"这一局要放的"。
        /// </summary>
        private static bool InstallBackground()
        {
            IntPtr target = MethodResolver.ByName("GameplayBackgrounds", "_UmA", RvaBackgroundLoad);
            if (target == IntPtr.Zero)
            {
                Diagnostics.Warn("GameplayBackgrounds._UmA could not be found; a custom song will play " +
                                 "over the game's own background");
                return false;
            }

            byte[] prologue = Prologue(target);
            _uma = new NativeHook<UmaFn>
            {
                Target = target,
                Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, IntPtr>)&UmaDetour,
            };
            _uma.Attach();
            _umaTramp = _uma.Trampoline;
            Diagnostics.Info("GameplayBackgrounds._UmA hooked");
            return Landed("GameplayBackgrounds._UmA", target, prologue);
        }

        /// <summary>
        /// `SongData._ZOA` —— 调用者手上是**难度记录**而不是歌时，曲绘从这里取。
        /// </summary>
        private static bool InstallChartReference()
        {
            IntPtr target = MethodResolver.ByName("SongData", "_ZOA", RvaJacketChartReference);
            if (target == IntPtr.Zero)
            {
                Diagnostics.Warn("SongData._ZOA could not be found; the loading screen and the " +
                                 "results screen will keep the game's fallback jacket");
                return false;
            }

            byte[] prologue = Prologue(target);
            _zoa = new NativeHook<ZoaFn>
            {
                Target = target,
                Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte, IntPtr, IntPtr>)&ZoaDetour,
            };
            _zoa.Attach();
            _zoaTramp = _zoa.Trampoline;
            Diagnostics.Info("SongData._ZOA hooked");
            return Landed("SongData._ZOA", target, prologue);
        }

        /// <summary>`AddressableHandleAutoReleaser._LIA&lt;T&gt;` —— 两个加载器里的阻塞那个。</summary>
        private static bool InstallChartLoader()
        {
            IntPtr target = MethodResolver.ByRuntime("AddressableHandleAutoReleaser", "_LIA",
                                                     Image, Namespace);
            if (target == IntPtr.Zero)
            {
                Diagnostics.Warn("AddressableHandleAutoReleaser._LIA could not be found; anything " +
                                 "that loads a jacket through it will draw the game's fallback");
                return false;
            }

            byte[] prologue = Prologue(target);
            _lia = new NativeHook<LiaFn>
            {
                Target = target,
                Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr>)&LiaDetour,
            };
            _lia.Attach();
            _liaTramp = _lia.Trampoline;
            Diagnostics.Info("AddressableHandleAutoReleaser._LIA hooked");
            return Landed("AddressableHandleAutoReleaser._LIA", target, prologue);
        }

        private static void DetachJacket()
        {
            _uma?.Detach();
            _uma = null;
            _umaTramp = null;

            _lia?.Detach();
            _lia = null;
            _liaTramp = null;

            _zoa?.Detach();
            _zoa = null;
            _zoaTramp = null;

            _mia?.Detach();
            _mia = null;
            _miaTramp = null;

            _apa?.Detach();
            _apa = null;
            _apaTramp = null;
        }

        /// <summary>
        /// Answers a jacket request for one of this mod's songs with a reference the game can load,
        /// and remembers to substitute the real picture when that load arrives.
        ///
        /// Nothing about the returned reference is special: it is whatever the game would have used
        /// for a song with no jacket of its own, and it resolves normally. The substitution happens
        /// on the other side.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr ApADetour(IntPtr songData, IntPtr songInfo, byte large, IntPtr methodInfo)
        {
            IntPtr fallback = _apaTramp(songData, songInfo, large, methodInfo);
            ApACalls++;

            if (!Faulted)
            {
                try
                {
                    if (JacketCatalog.Find(songInfo, out IntPtr material))
                    {
                        JacketCatalog.Arm(fallback, material);
                        ApAArmed++;
                    }
                    else JacketCatalog.Disarm();
                }
                catch (Exception e)
                {
                    JacketCatalog.Disarm();
                    Fault("SongData._apA", e);
                }
            }

            return fallback;
        }

        /// <summary>
        /// Substitutes this mod's material for the one load that was just armed, and passes every
        /// other load — of any asset type — straight through.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr MiaDetour(IntPtr owner, IntPtr releaser, IntPtr reference, byte forceReload,
                                        IntPtr methodInfo)
        {
            MiaCalls++;

            if (!Faulted)
            {
                try
                {
                    if (JacketCatalog.TryClaim(reference, out IntPtr material)) { MiaClaimed++; return material; }
                }
                catch (Exception e)
                {
                    Fault("AddressableHandleAutoReleaser._MIA", e);
                }
            }

            return _miaTramp(owner, releaser, reference, forceReload, methodInfo);
        }

        /// <summary>
        /// 按难度取曲绘的那条路：是我们的谱面就把刚拿到的引用记下来，等它被加载时替换。
        ///
        /// 与 `ApADetour` 同一套握手，区别只在"认歌"用的是什么 —— 这里只有 `SongChartInfo`，
        /// 没有 `SongInfo`，所以按**地址**认（见 `JacketCatalog.FindByChart`）。
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr ZoaDetour(IntPtr songData, IntPtr chartInfo, byte large, IntPtr methodInfo)
        {
            IntPtr fallback = _zoaTramp(songData, chartInfo, large, methodInfo);
            ZoaCalls++;

            if (!Faulted)
            {
                try
                {
                    if (JacketCatalog.FindByChart(chartInfo, out IntPtr material))
                    {
                        JacketCatalog.Arm(fallback, material);
                        ZoaArmed++;
                    }
                    else JacketCatalog.Disarm();
                }
                catch (Exception e)
                {
                    JacketCatalog.Disarm();
                    Fault("SongData._ZOA", e);
                }
            }

            return fallback;
        }

        /// <summary>
        /// 阻塞那个加载器的另一半握手：替换逻辑与 `_MIA` 完全相同，靠同一个指针恒等分辨。
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr LiaDetour(IntPtr owner, IntPtr reference, IntPtr methodInfo)
        {
            LiaCalls++;

            if (!Faulted)
            {
                try
                {
                    if (JacketCatalog.TryClaim(reference, out IntPtr material)) { LiaClaimed++; return material; }
                }
                catch (Exception e)
                {
                    Fault("AddressableHandleAutoReleaser._LIA", e);
                }
            }

            return _liaTramp(owner, reference, methodInfo);
        }

        /// <summary>
        /// 谱面内背景的取引用那一半：是我们的歌就把刚拿到的引用记下来，等它被加载时替换成曲绘。
        ///
        /// 认歌用的是"游戏当前选中的歌"，因为参数里只有一个背景枚举。拿不到选中项（还没选到歌）时
        /// 什么都不做 —— 那种情况下这局也不是我们的歌。
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static IntPtr UmaDetour(IntPtr backgrounds, int background, IntPtr methodInfo)
        {
            IntPtr fallback = _umaTramp(backgrounds, background, methodInfo);
            UmaCalls++;

            if (!Faulted)
            {
                try
                {
                    if (Selection.TryRead(out IntPtr song, out int _) &&
                        JacketCatalog.Find(song, out IntPtr material))
                    {
                        JacketCatalog.Arm(fallback, material);
                        UmaArmed++;
                    }
                    else JacketCatalog.Disarm();
                }
                catch (Exception e)
                {
                    JacketCatalog.Disarm();
                    Fault("GameplayBackgrounds._UmA", e);
                }
            }

            return fallback;
        }
    }
}
