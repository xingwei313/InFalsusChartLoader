extern alias UnityEngineCore;

using System;
using System.Collections.Generic;
using MelonLoader;

[assembly: MelonInfo(typeof(InFalsusChartLoader.ChartLoaderMod), "InFalsusChartLoader", "1.0.0", "infalsus", null)]
[assembly: MelonGame("lowiro", "infalsus")]

namespace InFalsusChartLoader
{
    /// <summary>
    /// The mod: read the chart folders, put them into the game's song list, and answer for them when
    /// the game asks.
    ///
    /// <para>
    /// Three stages, in this order and no other:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>Read and vet.</b> Every folder under <c>Charts/</c> is parsed and its four charts are each
    /// decoded once. A folder that fails anything is left out entirely — the song list simply does
    /// not contain it, which is the visible form of "非法则不导入".
    /// </description></item>
    /// <item><description>
    /// <b>Hook.</b> Seventeen detours, installed by eight functions before the game can ask for
    /// anything. Eight of them answer for something — the chart, the audio (a name and a read), and
    /// the pictures (three reference getters and two loaders, all sharing one handshake). One more
    /// answers for this mod's pack at the single place the game reads a pack's row of visuals, giving
    /// it the In Falsus pack's row. Two only
    /// count: the play gate and the scene switch say whether the game took each step, which is not
    /// something the decompilation can be read for. Two change what the game does around a call
    /// it makes, so that a difficulty change reaches the pictures — one on the song select, one on
    /// the pack screen. And four keep a custom song's results out of the game's own save: three give
    /// them a table of their own, and one drops the encounter result the settlement would otherwise
    /// write. See <see cref="Hooks"/>.
    /// </description></item>
    /// <item><description>
    /// <b>Register.</b> The songs and the pack go into the game's own tables. This waits: both are
    /// Addressables assets that are not loaded when the mod starts, and writing to them before the
    /// game has them would be writing to nothing. The custom results file (`IFCL.sav`) is opened at
    /// the same moment — its folder is taken off the game's own save file, so the two sit beside
    /// each other — and its table is prepared for the hooks above to answer from. See
    /// <see cref="CustomResults"/>.
    /// </description></item>
    /// </list>
    /// </summary>
    public class ChartLoaderMod : MelonMod
    {
        /// <summary>What was imported, waiting for the game's data to exist so it can be registered.</summary>
        private static List<ChartInfo> _imported = new List<ChartInfo>();

        private static bool _registered;

#if DEBUG
        /// <summary>Frames seen, for the probe's timer. Debug only; nothing else counts frames here.</summary>
        private static int _frames;
#endif

        public override void OnInitializeMelon()
        {
            Diagnostics.Load("InFalsusChartLoader loading...");
        }

        /// <summary>
        /// Where the real work starts. Not <see cref="OnInitializeMelon"/>: the interop assemblies
        /// are only wired up after MelonLoader has generated them, and until they are there is no
        /// game to ask about anything.
        /// </summary>
        public override void OnLateInitializeMelon()
        {
            try
            {
                // The decoder comes first: it is what decides whether a chart folder is any good.
                bool codecReady = ChartCodec.Resolve();
                if (!codecReady)
                {
                    Diagnostics.Error("the game's chart decoder could not be reached; no charts will load");
                    return;
                }

                // Asked once here for the log only. It is asked for real at registration, which is
                // the first moment the answer can be yes -- see OnUpdate.
                bool assetsReady = StreamingAssets.Resolve();

                string root = ChartLibrary.Locate();
                Diagnostics.Info($"charts folder: {root}");

                _imported = ChartLibrary.Import(root, Validate);
                Diagnostics.Load($"charts: {_imported.Count} imported");

                // Nothing to serve, so nothing is put on the game's hot paths. Five detours cost a
                // couple of seconds to install and would then sit on every chart request, sound read
                // and jacket load answering nothing but pass-through.
                bool worthHooking = _imported.Count > 0;
                if (worthHooking) Hooks.Install();
                else Diagnostics.Info("no charts were imported, so no hooks are being installed");

#if DEBUG
                // After the decision, not before it: with nothing imported the hook count is zero, and
                // that is a fact worth printing rather than a reason not to print.
                Probe.Startup(root, codecReady, assetsReady, Hooks.Installed, Hooks.Attempted);
#endif
            }
            catch (Exception e)
            {
                Diagnostics.Error($"chart loading failed: {Diagnostics.Describe(e)}");
            }
        }

        /// <summary>
        /// Registers the songs once the game has loaded the tables they go in.
        ///
        /// Polled rather than hooked: the two assets arrive from an Addressables load during startup,
        /// there is no one place they become available, and the cost of asking is two pointer reads
        /// and a null check per frame until they do.
        /// </summary>
        public override void OnUpdate()
        {
#if DEBUG
            // About every ten seconds. A run that never gets past the menu still says, from here,
            // whether each hook was called and what it answered.
            if (++_frames % 600 == 0) Diagnostics.Info("probe: counters       " + Probe.Status());
#endif

            if (_registered || _imported.Count == 0) return;

            try
            {
                if (!SongCatalog.TryGetAssets(out IntPtr songData, out IntPtr packData))
                {
                    // A name the mod reads the tables by is not in this build: reported once where it
                    // was asked, and nothing this run can come of asking again. Stop, rather than
                    // poll for the rest of the session.
                    if (SongCatalog.Unusable) _registered = true;
                    return;
                }

#if DEBUG
                PackSetup.TryGetVisuals(out IntPtr packAssetsForProbe);
                Probe.Before(songData, packData, packAssetsForProbe);
#endif

                // The offsets are only asked for once the game has the objects they belong to, so
                // this is the first moment there is an answer -- and the only line that says whether
                // a game update moved something this mod reads.
                Diagnostics.Load(FieldResolver.Stats());

                // Names first, and before the audio, because a slot in the asset manager cannot be
                // given back: a song whose name the game already has is refused here and never takes
                // one. See SongCatalog.SettleNames.
                List<ChartInfo> named = SongCatalog.SettleNames(_imported, songData);
                if (named.Count == 0)
                {
                    _registered = true;
                    Diagnostics.Error("no custom song could be filed under its name");
                    return;
                }

                // Audio next, because this is the first moment the asset manager exists. A song whose
                // audio cannot be registered is left out here rather than imported silent: the song
                // list is built from what this returns, so a folder dropped now never appears at all.
                List<ChartInfo> ready = RegisterAudio(named);
                if (ready.Count == 0)
                {
                    _registered = true;
                    Diagnostics.Error("no custom song could have its audio registered");
                    return;
                }

                int added = SongCatalog.Inject(songData, packData, ready);
                _registered = true;

                if (added == 0)
                {
                    Diagnostics.Error("the custom songs could not be added to the game's song list");
                    return;
                }

                // Two follow-ups the pack needs, and only when there is a pack to follow up on: an id
                // still at -1 means `AddPack` refused, and both of these are keyed by that id — an
                // unset one is zero, which is the reserved pack the game never draws. Writing a name
                // or a row of visuals against it would put this mod's text on a pack that is not its
                // own, which is the same class of mistake as taking the DLC pack's id was.
                if (SongCatalog.CustomPackId < 0)
                {
                    Diagnostics.Error("the custom pack could not be added; the custom songs are in the " +
                                      "song list but have no pack of their own");
                }
                else
                {
                    // Both are tolerant of failure -- a pack with no row of its own still draws,
                    // wearing the first pack's look, and a stale lookup only makes reverse lookups
                    // about custom songs answer nothing -- so neither is fatal.
                    if (PackSetup.TryGetVisuals(out IntPtr packAssets))
                        PackSetup.ExtendVisuals(packAssets, SongCatalog.CustomPackId);
                    else
                        Diagnostics.Warn("the pack visual table could not be reached");

                    PackSetup.RefreshLookup(packData);
                    PackSetup.SetPackName(SongCatalog.CustomPackId, SongCatalog.PackName);
                }

                // The custom songs' results go to a save file of their own, and this is the first
                // moment both halves of that exist: the game's save (for the folder it lives in)
                // and this mod's songs (for the ids the hooks answer by). See CustomResults.
                CustomResults.Prepare();

#if DEBUG
                PackSetup.TryGetVisuals(out IntPtr packAssetsAfter);
                Probe.After(songData, packData, packAssetsAfter, ready);
#endif

                Diagnostics.Load($"charts: {added} registered");
            }
            catch (Exception e)
            {
                // Once: a failure here would otherwise repeat every frame for the rest of the session.
                _registered = true;
                Diagnostics.Error($"registering the custom songs failed: {Diagnostics.Describe(e)}");
            }
        }

        public override void OnDeinitializeMelon()
        {
            Hooks.Uninstall();
        }

        /// <summary>
        /// Gives every song its audio, and returns the ones that got it.
        ///
        /// The asset manager is asked for room here rather than at import, because at import it does
        /// not exist yet and a failure then would have looked exactly like a bad chart folder. It is
        /// asked for once: <see cref="OnUpdate"/> does not call this again after a failure.
        /// </summary>
        private static List<ChartInfo> RegisterAudio(List<ChartInfo> charts)
        {
            if (!StreamingAssets.Resolve())
            {
                Diagnostics.Error("the game's asset manager could not be reached; no custom audio can " +
                                  "be registered, so no custom song can be played");
                return new List<ChartInfo>();
            }

            var ready = new List<ChartInfo>(charts.Count);
            foreach (ChartInfo info in charts)
            {
                if (AudioCatalog.Add(info.BaseName, info.SongPath, out string reason)) ready.Add(info);
                else Diagnostics.Error($"'{info.Name}' was left out: {reason}");
            }

            Diagnostics.Info($"audio registered for {ready.Count} of {charts.Count} songs");
            return ready;
        }

        /// <summary>
        /// The import's second half: everything <see cref="ChartInfoReader"/> could not check,
        /// because it needs the game's decoder.
        ///
        /// A chart is accepted when the game's own loader accepts it — the container check is the
        /// game's, and the note-by-note inspection in <see cref="ChartCodec"/> is what catches a file
        /// that is well formed but was encoded under a different name, which otherwise decodes to
        /// nonsense silently.
        /// </summary>
        private static string Validate(ChartInfo info)
        {
            byte[] scratch = new byte[32];
            for (int d = 0; d < ChartInfo.Difficulties; d++)
            {
                string path = info.ChartPaths[d];

                // What the chart was encoded under, which is what it can be decoded under. It is
                // also the file's name — the path above was built from this string — so an author
                // who renames a chart file after encoding it breaks it, and the container check will
                // not say so: the container passes and the notes come out as nonsense.
                string seed = info.ChartNames[d];

                byte[] bytes;
                try
                {
                    bytes = System.IO.File.ReadAllBytes(path);
                }
                catch (Exception e)
                {
                    return $"difficulty {d} could not be read: {Diagnostics.Describe(e)}";
                }

                unsafe
                {
                    fixed (byte* tuple = scratch)
                    {
                        if (!ChartCodec.TryDecode(bytes, seed, (IntPtr)tuple, out string reason))
                            return $"difficulty {d}: {reason}";
                    }
                }
            }

            // The jacket is built here, at import, rather than when the song is first shown, so that a
            // picture that cannot be decoded is decided before the song appears in the list. After the
            // charts, not before: a jacket built for a folder whose charts then fail is a picture for
            // a song that is not going to exist, left in the catalogue answering for a name nothing
            // will ever ask about.
            //
            // It does not reject the song, though. A jacket is one field of the song's data, and the
            // game draws a song without one -- the material this would have supplied is what a fallback
            // is for. Rejecting here made a rendering problem look like a chart problem: every folder
            // was dropped for a reason that had nothing to do with the folder, and because an import
            // with no songs registers nothing, the pack the songs belong to never appeared either.
            //
            // Audio is not checked here either. Registering it needs the game's asset manager, which is
            // not up yet at this point -- and a check that failed this early would reject every folder
            // for a reason that has nothing to do with the folder. It is registered at registration
            // time instead, and a song whose audio cannot be registered is dropped there.
            if (!JacketCatalog.Add(info, out string jacketReason))
                Diagnostics.Warn($"'{info.Name}' has no jacket of its own: {jacketReason}");

#if DEBUG
            // Only once every check above has passed, so the line means "this folder is in" rather
            // than "this folder got this far".
            Probe.Imported(info, AudioCatalog.IndexOf(info.BaseName), AudioCatalog.BytesOf(info.BaseName),
                           JacketCatalog.Count);
#endif

            return null;
        }
    }
}
