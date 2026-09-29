using System;

namespace InFalsusChartLoader
{
    /// <summary>
    /// The native hooks: which game functions are patched, and what happens when one faults.
    ///
    /// All of them are thin. Each hook is a file of its own under Detours/ — its RVA, its delegate
    /// shape, its trampoline and its detour body — and the decisions live in <see cref="ChartCatalog"/>
    /// and <see cref="AudioCatalog"/>, so a mistake here is a routing mistake rather than a data one.
    ///
    /// RVAs are from Il2CppDumper's dump.cs; <see cref="MethodResolver"/> prefers the runtime
    /// MethodInfo and only falls back to them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Thirteen detours, installed by eight functions. Eight of them exist because a custom song needs
    /// the game to accept something it has no path for — a name it will look up, a byte range it
    /// will read, a picture it cannot reach. Each of those answers, and each is on a hot path, so
    /// each stays thin. Three more answer nothing: they count and report, because whether the game
    /// took each step cannot be read off the decompilation with confidence and one number per step
    /// settles it. The last two do neither — each changes what the game does around a call it is
    /// making, so that a difficulty change reaches the pictures. One widens the song-select's own
    /// reload to include a difficulty change; the other rebuilds the pack screen's cards, which that
    /// screen never asks the game to rebuild.
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>`_s._VA`</b> — the chart loader. The game builds `{BaseName}{difficulty}.spc` and asks for
    /// it by name; a custom chart has no StreamingAssets entry, so without this the lookup throws
    /// inside a coroutine and the scene hangs.
    /// </description></item>
    /// <item><description>
    /// <b>`_BG._DJA`</b> — the name lookup, answered so the game can find a custom song's audio at
    /// all — and <b>`_J._hF._ZgA`</b>, the audio read itself. The game's own files are obfuscated and
    /// this one's are not, so the bytes have to be substituted rather than decoded; `_ZgA` is the one
    /// place where the bytes are still the mod's to choose.
    ///
    /// <para>
    /// `_ZgA` is also the one detour that stands in for a body with side effects on the game's own
    /// state rather than only on its return: the body opens by taking the read's block out of a set
    /// the dispatcher reads, and closes by taking it out of two. Answering the read without those
    /// three calls leaves the block where the dispatcher will later mistake it for a duplicate and
    /// cancel the next read that lands on it. See <see cref="PendingReads"/>.
    /// </para>
    /// </description></item>
    /// <item><description>
    /// <b>`SongData._apA`</b> and <b>`AddressableHandleAutoReleaser._MIA`</b> — the two halves of
    /// showing a jacket, which the game can only do for something in its own Addressables catalogue.
    /// A jacket is not one entry but a pair of axes, so there are three more of these:
    /// <b>`SongData._ZOA`</b> and <b>`AddressableHandleAutoReleaser._LIA`</b> for the callers that
    /// hold a difficulty record instead of a song, and <b>`GameplayBackgrounds._UmA`</b> for the
    /// background behind the chart, which a custom song's picture also stands in for. All five share
    /// one handshake and one material catalogue.
    /// </description></item>
    /// <item><description>
    /// <b>`SongSelectScene._sN`</b> and <b>`CoreScene._CB.MoveNext`</b> — the play gate and the
    /// scene switch. Both are reported and neither is changed; they are the two halves of "the
    /// button does nothing" against "the game started something and stalled", and telling those
    /// apart from the outside was the whole of a round of work.
    /// </description></item>
    /// <item><description>
    /// <b>`SongSelectScene._MN`</b> — the selection being applied, on the screen where a picture is
    /// asked for by song. Two things are wrong with the pictures that a difficulty change has to
    /// reach, and both are answered from here: it is the only place where the difficulty is known
    /// while it is being applied (the song-select background asks for its picture <i>before</i> the
    /// hook that records the scene has run), and the game reloads those pictures only on a song
    /// change, which this widens to include a difficulty change. See <see cref="Selection"/> and
    /// <c>SongSelectHook</c>.
    /// </description></item>
    /// <item><description>
    /// <b>`PackVisualMemberLarge._vc`</b> — the same problem on the pack screen, where a picture is
    /// asked for by song but the screen has no difficulty of its own to answer with, and where the
    /// game does not re-ask at all when the difficulty changes. The difficulty is taken from this
    /// call, and the cards are rebuilt through the game's own card builder when it moves. See
    /// <c>PackVisualHook</c>.
    /// </description></item>
    /// <item><description>
    /// <b>The song-select's card builder</b> — the odd one out: it counts the cards the list is
    /// built from, because whether a custom song survives the filter that decides the list cannot be
    /// read off the decompilation with confidence, and one counter settles it. See
    /// <c>SongCardHook</c>.
    /// </description></item>
    /// </list>
    /// <para>
    /// Attaching a detour costs about 400 ms. Measured in the sibling mod in this repository: every
    /// hook 395-418 ms, independent of the size of the function being hooked — the shape of a fixed
    /// wait, not of work. Where it goes is not known; it is inside MelonLoader's own native bootstrap,
    /// and it is not MonoMod, which is the Mono path.
    /// </para>
    /// <para>
    /// `NativeHook&lt;T&gt;.Attach()` does not report failure — `CoreClrDelegateFixer.SanityCheckDetour`
    /// returns silently when it is not satisfied — so a "hooked" line in the log means the call was
    /// made, not that the hook is in place.
    /// </para>
    /// </remarks>
    internal static unsafe partial class Hooks
    {
        /// <summary>Set when a detour throws. Every detour stops calling into the mod after that.</summary>
        internal static bool Faulted { get; private set; }

        internal static int Faults { get; private set; }

        // What each detour has seen. Plain fields incremented unconditionally, read only by the
        // probes: a long added to a hot path is a few bytes, and the alternative — counting behind
        // [Conditional] — costs more than it saves, because a counter method is two metadata rows
        // where a field is one.
        internal static long VaCalls, VaServed;
        internal static long DjaCalls, DjaHits;
        internal static long ZgaCalls, ZgaServed;
        internal static long ApACalls, ApAArmed;

        /// <summary>
        /// Jacket requests for a song of this mod's that had no picture to hand back.
        ///
        /// Beside the armed count rather than folded into it: "the game never asked about the song"
        /// and "it asked and the picture was missing" are different problems, and both leave
        /// <see cref="ApAArmed"/> at zero.
        /// </summary>
        internal static long ApaAsked;

        internal static long MiaCalls, MiaClaimed;

        // 曲绘的另一半：按难度取引用（`_ZOA`）与阻塞加载（`_LIA`）。分开计数，因为
        // "卡片有图、加载界面没有"正是这两条中某一条没被走到时的形状。
        internal static long ZoaCalls, ZoaArmed, LiaCalls, LiaClaimed;

        /// <summary>谱面内背景（`_UmA`）。与曲绘分开计数，因为"背景换了、卡片没换"是它独有的形状。</summary>
        internal static long UmaCalls, UmaArmed;

        /// <summary>How many went in, and how many were tried. Reported by the probe.</summary>
        internal static int Installed { get; private set; }
        internal static int Attempted { get; private set; }

        internal static void Install()
        {
            int installed = 0;
            int attempted = 0;

            // Attempts are counted beside the calls rather than written down as a total, so the
            // expected number cannot drift when one is added or removed.
            void Count(bool ok) { attempted++; if (ok) installed++; }

            // The chart hook goes first: without it nothing the mod contributes can be loaded at all.
            Count(InstallChartLoad());
            Count(InstallAudioLoad());
            Count(InstallJacket());
            Count(InstallSongCard());
            Count(InstallSongSelect());
            Count(InstallPackVisual());
            Count(InstallPlayGate());
            Count(InstallSceneSwitch());

            Installed = installed;
            Attempted = attempted;

            Diagnostics.Load($"{installed}/{attempted} hooks installed");
            if (installed < attempted)
                Diagnostics.Warn("a hook is missing; whatever it drives will silently do nothing");
        }

        internal static void Uninstall()
        {
            DetachSceneSwitch();
            DetachPlayGate();
            DetachPackVisual();
            DetachSongSelect();
            DetachSongCard();
            DetachJacket();
            DetachAudioLoad();
            DetachChartLoad();
        }

        /// <summary>
        /// The first bytes at a hook target, taken before it is attached, so the attach can be
        /// checked against them afterwards.
        /// </summary>
        internal static unsafe byte[] Prologue(IntPtr target, int count = 8)
        {
            if (target == IntPtr.Zero) return null;

            var bytes = new byte[count];
            for (int i = 0; i < count; i++) bytes[i] = *(byte*)(target + i);
            return bytes;
        }

        /// <summary>
        /// Whether an attach changed the target at all.
        ///
        /// The only way to tell: `Attach()` returns nothing and says nothing when it fails, and a
        /// detour that is not in place is indistinguishable from one that is never reached — both
        /// leave the counter at zero for the rest of the run. A hook engine that has taken the target
        /// has rewritten its first bytes, so comparing them is the whole test.
        /// </summary>
        private static unsafe bool Patched(IntPtr target, byte[] prologue)
        {
            if (target == IntPtr.Zero || prologue == null) return false;

            for (int i = 0; i < prologue.Length; i++)
                if (*(byte*)(target + i) != prologue[i]) return true;
            return false;
        }

        /// <summary>
        /// A hook, named for the one place a name is needed: a detour that did not land.
        ///
        /// An enum rather than the string it replaced, and that is about what a Release build carries.
        /// The name is an argument to <see cref="Landed"/>, which answers with the installer's return
        /// value and therefore cannot be `[Conditional("DEBUG")]` — and an argument to a method that
        /// ships is a string that ships. Thirteen of these names were in every Release build, readable
        /// by nothing in it. They now live in <see cref="Name"/>, which that build does not compile.
        /// </summary>
        internal enum Hook
        {
            ChartLoader,
            AudioRead,
            AudioName,
            JacketReference,
            JacketMaterial,
            ChartReference,
            ChartMaterial,
            Background,
            SongCard,
            SongSelectApply,
            PackVisual,
            PlayGate,
            SceneSwitch,
        }

        /// <summary>
        /// Whether a detour landed, reported by name when it did not.
        ///
        /// The answer becomes the installer's return value, which is what the `n/m hooks installed`
        /// line is built from — and that line is in every build, so a hook that was attached and did
        /// not take reads as a count, not as an absence nobody can see.
        /// </summary>
        private static bool Landed(Hook hook, IntPtr target, byte[] prologue)
        {
            if (Patched(target, prologue)) return true;

            NotLanded(hook);
            return false;
        }

        /// <summary>
        /// Says which hook did not take.
        ///
        /// The body is guarded, not just the call site: a `[Conditional]` method is still compiled in
        /// a build where its calls are removed, so a name rendered outside `#if DEBUG` would be a
        /// string in the Release build in exactly the way <see cref="Hook"/> exists to avoid.
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void NotLanded(Hook hook)
        {
#if DEBUG
            Diagnostics.Warn($"{Name(hook)} was attached but its first bytes are unchanged; the detour " +
                             "is not in place, and whatever it answers for will silently do nothing");
#endif
        }

#if DEBUG
        /// <summary>The hooks by name, for the log. Debug only — see <see cref="Hook"/>.</summary>
        private static string Name(Hook hook) => hook switch
        {
            Hook.ChartLoader => "_s._VA",
            Hook.AudioRead => "_J._hF._ZgA",
            Hook.AudioName => "_BG._DJA",
            Hook.JacketReference => "_apA",
            Hook.JacketMaterial => "_MIA",
            Hook.ChartReference => "SongData._ZOA",
            Hook.ChartMaterial => "AddressableHandleAutoReleaser._LIA",
            Hook.Background => "GameplayBackgrounds._UmA",
            Hook.SongCard => "the song card builder",
            Hook.SongSelectApply => "SongSelectScene._MN",
            Hook.PackVisual => "PackVisualMemberLarge._vc",
            Hook.PlayGate => "SongSelectScene._sN",
            Hook.SceneSwitch => "CoreScene._CB.MoveNext",
            _ => "(unknown hook)",
        };
#endif

        /// <summary>
        /// Reports a detour fault once, then stops calling into the mod.
        ///
        /// The hook is named on the line only in a Debug build, and the article standing in for the
        /// name in a Release one is why the sentence still reads: a release says <i>a</i> detour
        /// faulted, which is what the person reading it has to act on, and which one it was is a
        /// question for a build that can answer it. A <see cref="Hook"/> rather than the string this
        /// took, for the reason the enum exists — this was the third member of that family, and the
        /// one the first pass over it missed.
        /// </summary>
        private static void Fault(Hook hook, Exception e)
        {
            Faulted = true;
            Faults++;

            if (Faults > 1) return;
            Diagnostics.Error($"{Which(hook)}detour faulted, custom charts disabled: {Diagnostics.Describe(e)}");
        }

        /// <summary>
        /// What the fault line calls the hook: its name in a Debug build, and the article the sentence
        /// needs in a Release one. See <see cref="Fault"/> and <see cref="Name"/>.
        /// </summary>
        private static string Which(Hook hook)
        {
#if DEBUG
            return Name(hook) + " ";
#else
            return "a ";
#endif
        }
    }
}
