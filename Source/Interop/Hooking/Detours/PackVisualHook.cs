using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MelonLoader.NativeUtils;

namespace InFalsusChartLoader
{
    internal static unsafe partial class Hooks
    {
        /// <summary>
        /// `PackVisualMemberLarge._vc(ChartDifficultyFlag) -> void` — the pack screen's difficulty apply.
        ///
        /// <para>
        /// The category screen draws one small jacket per song of the selected pack, on
        /// `PackSongCardMember` cards, and it asks for each of them exactly once — when a pack is
        /// selected and the cards are built (`PackVisualMemberLarge._Sc`, which carries its own inlined
        /// copy of the card build). A difficulty change does not ask again: it runs this method, which
        /// walks the cards and updates their texts, their difficulty-based objects and their clear-type
        /// material, and leaves every jacket as it was. That is measured, not inferred — the body calls
        /// `_qc` once per card and never `_apA` or `_MIA`.
        /// </para>
        /// <para>
        /// So a song with a picture per difficulty keeps showing the one its card was built with, and on
        /// a fresh session it shows nothing at all: this screen has no source of its own for the
        /// difficulty, so `Selection` answers with the song-select's last value or with nothing. Both are
        /// answered from the argument already in this call:
        /// </para>
        /// <list type="number">
        /// <item><description>
        /// The difficulty is recorded <b>before</b> the game's body runs. On the entry path the apply
        /// comes first and the cards are built after it, so the jackets asked for during that build are
        /// answered with the difficulty actually in force rather than with the previous screen's.
        /// </description></item>
        /// <item><description>
        /// When the cards on screen were built for a different difficulty, they are rebuilt after the
        /// game's body runs — by calling the game's own card builder, `PackSongCardMember._pc`, once per
        /// card that is one of this mod's. That is the sequence `_Sc` performs inline when a pack is
        /// chosen, so it is the game's own code that re-asks `_apA` and substitutes through `_MIA`, and
        /// the picture follows the difficulty without a scene reload.
        /// </description></item>
        /// </list>
        /// <para>
        /// The RVA is measured on the installed build and is documentation: this resolves by name only.
        /// A wrong address here would be a patch into the middle of something else, and the failure
        /// would be silent — see <see cref="MethodResolver"/> and the note on `_MIA`.
        /// </para>
        /// </summary>
        private const long RvaPackDifficulty = 0x531190;

        /// <summary>
        /// `PackSongCardMember._pc(DataAccess, _SH) -> void` — build one card and its jacket.
        ///
        /// <b>Unreferenced in the installed build.</b> `_Sc` carries its own inlined copy of this body;
        /// the method itself has no code caller left (its only xrefs are the metadata tables). It is
        /// still the game's own card build, byte for byte the sequence `_Sc` performs — `_apA` for the
        /// reference, `_MIA` for the material with `forceReload`, the material onto the card, and `_qc`
        /// to re-apply the card's own difficulty — and calling it is one call instead of a copy of that
        /// sequence here. Nothing is invented: the alternative would be this mod re-implementing four
        /// of the game's steps and staying in step with them afterwards.
        ///
        /// Reached by name only, for the reason above: on a build where it is gone this degrades to
        /// "the picture updates when the screen is rebuilt", which is what happens now anyway.
        /// </summary>
        private const long RvaCardBuild = 0x52FA30;

        // Prefixed because `Hooks` is a partial class and other files already name this image and
        // namespace under their own class's terms.
        private const string PackImage = "Game.dll";

        private const string PackNamespace = "ifapp.Game";

        /// <summary>`PackVisualMemberLarge._Mf`, the cards of the pack on screen.</summary>
        private const int CardsField = 0x120;

        /// <summary>`PackSongCardMember._if`, the song view model the card was built from.</summary>
        private const int CardViewModel = 0x78;

        /// <summary>
        /// `PackVisualMemberLarge.dataAccess`, which the card's build has to be handed.
        ///
        /// It is the pack visual's own field rather than anything on the card: `_Sc` passes this one
        /// to the card build it performs inline, and the card's build only forwards it to `_qc`, which
        /// reads the song data and the localisation table out of it.
        /// </summary>
        private const int PackDataAccess = 0xF0;

        /// <summary>`_SH._WEb`, the song record — embedded, so this is its address and not a pointer.</summary>
        private const int ViewModelSong = 0x10;

        /// <summary>`_SH._xEb`, the difficulty the card is showing.</summary>
        private const int ViewModelDifficulty = 0x50;

        /// <summary>
        /// `void _vc(ChartDifficultyFlag)`.
        ///
        /// The difficulty is a one-byte enum in this build (`_SH._xEb`, `PackSelectScene._Gf` and this
        /// method's own parameter all read as bytes), which is the same shape the song-select's `_MN`
        /// is hooked with.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PackDifficultyFn(IntPtr self, byte difficulty, IntPtr methodInfo);

        private static NativeHook<PackDifficultyFn> _packDifficulty;
        private static PackDifficultyFn _packDifficultyTramp;

        /// <summary>`PackSongCardMember._pc`'s code pointer, or null when it could not be reached.</summary>
        private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void> _buildCard;

        /// <summary>
        /// Its `MethodInfo`, which the call passes as its last argument the way a call site does.
        /// A method's body can reach for it, and this mod is standing in for a call site.
        /// </summary>
        private static IntPtr _buildCardInfo;

        /// <summary>Difficulty applies seen on the pack screen, and cards rebuilt because of one.</summary>
        internal static long PackDifficultyCalls, PackCardsRebuilt;

        private static bool InstallPackVisual()
        {
            IntPtr info = MethodResolver.MethodInfoByRuntime("PackSongCardMember", "_pc", PackImage, PackNamespace);
            if (info == IntPtr.Zero)
            {
                // Not fatal, and not a hook: without it the difficulty is still recorded for the cards
                // the game builds later — it just does not reach the ones already on screen.
                Diagnostics.Warn("PackSongCardMember._pc could not be found; a difficulty change on the " +
                                 "pack screen will not reach the jackets until the screen is reloaded");
            }
            else
            {
                _buildCardInfo = info;
                _buildCard = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)(*(IntPtr*)info);
            }

            // By runtime walk, like the other methods on this screens' classes: the generated store
            // answers zero for methods nothing has touched, and a zero there falls back to a pinned
            // address silently. There is no RVA fallback here — a wrong address would be a patch into
            // the middle of whatever now occupies it, and `Attach()` reports nothing either way.
            IntPtr target = MethodResolver.ByRuntime("PackVisualMemberLarge", "_vc", PackImage, PackNamespace);
            if (target == IntPtr.Zero)
            {
                Diagnostics.Warn("PackVisualMemberLarge._vc could not be found; the jackets on the pack " +
                                 "screen will keep the difficulty they were first built with");
                return false;
            }

            byte[] prologue = Prologue(target);
            _packDifficulty = new NativeHook<PackDifficultyFn>
            {
                Target = target,
                Detour = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte, IntPtr, void>)&PackDifficultyDetour,
            };
            _packDifficulty.Attach();
            _packDifficultyTramp = _packDifficulty.Trampoline;
            Diagnostics.Info("PackVisualMemberLarge._vc hooked");
            return Landed(Hook.PackVisual, target, prologue);
        }

        private static void DetachPackVisual()
        {
            _packDifficulty?.Detach();
            _packDifficulty = null;
            _packDifficultyTramp = null;
            _buildCard = null;
            _buildCardInfo = IntPtr.Zero;
        }

        /// <summary>
        /// Records the difficulty being applied, and rebuilds the cards after the game's own body has
        /// moved them if they were showing another one.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void PackDifficultyDetour(IntPtr self, byte difficulty, IntPtr methodInfo)
        {
            PackDifficultyCalls++;

            bool rebuild = false;
            if (!Faulted)
            {
                try
                {
                    // Before the body, and both questions have to be asked here: the body is what moves
                    // the cards' own difficulty, so afterwards "were they showing another one" can only
                    // answer no.
                    Selection.ApplyingDifficulty(difficulty);
                    rebuild = CardsForAnotherDifficulty(self, difficulty);
                }
                catch (Exception e)
                {
                    Fault(Hook.PackVisual, e);
                }
            }

            _packDifficultyTramp(self, difficulty, methodInfo);

            // Outside the try, like every other detour here: the game's call is made whatever happened
            // above. The rebuild is after it, because what it re-runs reads the state the body just set.
            if (rebuild) RebuildCards(self);
        }

        /// <summary>
        /// Whether a card on screen was built for a difficulty other than the one being applied.
        ///
        /// Only this mod's songs count. A shipped pack's cards carry one picture for the whole song, so
        /// rebuilding them would be an Addressables reload with nothing to show for it. An empty list is
        /// not stale either: on the entry path the difficulty is applied before the pack is chosen, and
        /// there is then nothing on screen to be wrong.
        /// </summary>
        private static bool CardsForAnotherDifficulty(IntPtr packVisual, byte difficulty)
        {
            if (!Memory.TryList(Memory.Ptr(packVisual + CardsField), out IntPtr items, out int count))
                return false;

            for (int i = 0; i < count; i++)
            {
                if (!Ours(items, i, out IntPtr _, out IntPtr viewModel)) continue;
                if (Memory.U8(viewModel + ViewModelDifficulty) != difficulty) return true;
            }

            return false;
        }

        /// <summary>
        /// Re-runs the game's own card build for each of this mod's cards.
        ///
        /// One call per card, and it is the game's sequence rather than a copy of it: the reference from
        /// `_apA`, the substitution through `_MIA` with `forceReload`, the material onto the card, and
        /// the card's own difficulty re-applied. A failure here does <b>not</b> fault: this is an extra
        /// the mod performs on the player's behalf, and it must not be able to switch off the chart and
        /// audio hooks — the lesson the run with the card-counting diagnostic was written from.
        /// </summary>
        private static void RebuildCards(IntPtr packVisual)
        {
            if (_buildCard == null) return;

            IntPtr dataAccess = Memory.Ptr(packVisual + PackDataAccess);
            if (!Memory.LooksLikeObject(dataAccess)) return;

            if (!Memory.TryList(Memory.Ptr(packVisual + CardsField), out IntPtr items, out int count))
                return;

            for (int i = 0; i < count; i++)
            {
                if (!Ours(items, i, out IntPtr card, out IntPtr viewModel)) continue;

                try
                {
                    _buildCard(card, dataAccess, viewModel, _buildCardInfo);
                    PackCardsRebuilt++;
                }
                catch (Exception e)
                {
                    Reported("rebuilding a card on the pack screen raised: " + Diagnostics.Describe(e));
                }
            }
        }

        /// <summary>One card of the list, when it is one of this mod's songs'.</summary>
        private static bool Ours(IntPtr items, int index, out IntPtr card, out IntPtr viewModel)
        {
            card = Memory.Ptr(items + index * IntPtr.Size);
            viewModel = IntPtr.Zero;
            if (!Memory.LooksLikeObject(card)) return false;

            viewModel = Memory.Ptr(card + CardViewModel);
            if (!Memory.LooksLikeObject(viewModel)) return false;

            // The record is embedded in the view model, so its address is the view model plus the
            // field's offset — and the base name is inside it, which is what the catalogue matches on.
            return JacketCatalog.IsOurs(viewModel + ViewModelSong);
        }

        /// <summary>
        /// Says why the rebuild failed, once.
        ///
        /// <see cref="RebuildCards"/> runs on every difficulty change, and a card that cannot be
        /// rebuilt cannot be rebuilt on the next one either: a line per attempt would be its own defect
        /// on a path the player can drive. `[Conditional]` for the reason the log class gives — without
        /// it the argument's string is built in Release, where nothing reads it.
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void Reported(string why)
        {
            if (_rebuildReported) return;
            _rebuildReported = true;
            Diagnostics.Warn(why);
        }

        private static bool _rebuildReported;
    }
}
