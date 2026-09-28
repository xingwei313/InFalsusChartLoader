using System.Diagnostics;

namespace InFalsusChartLoader
{
    /// <summary>
    /// Logging. Every line goes out through MelonLoader's logger, which prefixes it with this mod's
    /// assembly name — so this mod's lines are already findable in a log that also carries
    /// MelonLoader's and every other mod's output, and adding a tag of its own would print the mod's
    /// name twice.
    ///
    /// <para>
    /// Five levels, and which of them a Release build carries is the whole of the design:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="Load"/> is compiled into every build and is deliberately a handful of lines: that
    /// the mod loaded, the offset summary, how many hooks went in, and what the chart folder yielded.
    /// A release is the build users run, so it has to be able to answer "did my chart get picked
    /// up?" without a debug build — and the answer is a count per folder, not a narration. Which
    /// folder was skipped and why is the debug build's job.
    /// </description></item>
    /// <item><description>
    /// <see cref="Error"/> is compiled into every build. It is the one thing a release must never
    /// swallow: the mod has stopped doing its work, and nobody is going to look for that in a build
    /// they were told to reinstall.
    /// </description></item>
    /// <item><description>
    /// <see cref="Info"/> and <see cref="Warn"/> are Debug-only, and by a compile-time switch rather
    /// than a runtime one: `[Conditional("DEBUG")]` removes the call <b>and the string its argument
    /// builds</b>, so a Release assembly carries neither the per-chart walkthrough of the import nor
    /// the per-file detail of a rejection. That is the bulk of what this class emits, and keeping it
    /// out is what makes a release small.
    /// </description></item>
    /// <item><description>
    /// <see cref="WarnRelease"/> is the exception to that pair, and it is one line wide on purpose —
    /// see its own summary for where the line is drawn.
    /// </description></item>
    /// </list>
    /// <para>
    /// The folder path is deliberately not in any of them: `charts folder: &lt;path&gt;` is an
    /// <see cref="Info"/> line, so a release does not print where the user keeps their files.
    /// </para>
    /// <para>
    /// One consequence worth knowing, because it is easy to get backwards: `[Conditional]` removes the
    /// call and the argument's evaluation, but the argument still has to compile. So a method named
    /// inside a log argument (<see cref="Describe"/>) must exist in Release as well, however little of
    /// it is left.
    /// </para>
    /// </summary>
    internal static class Diagnostics
    {
        /// <summary>Narration about a run — Debug builds only. See the class summary.</summary>
        [Conditional("DEBUG")]
        internal static void Info(string message)
        {
#if DEBUG
            MelonLoader.MelonLogger.Msg(message);
#endif
        }

        /// <summary>
        /// The lines that say the mod is here and healthy, in every build: that it loaded, what the
        /// game answered when asked where its fields are, how many hooks went in, and what the chart
        /// folder yielded.
        ///
        /// Deliberately few. A release is the build users run and it has to be able to answer "is this
        /// working, did a game update move something, and did my chart load" without a debug build —
        /// but the answer to all three is a summary: `moved=3` and `1 skipped` are the signals, and
        /// which three, and why that one, is what the debug build is for. Every line here is a string
        /// in the shipped assembly.
        /// </summary>
        internal static void Load(string message) => MelonLoader.MelonLogger.Msg(message);

        /// <summary>Something is wrong and the mod has stopped doing it: a detour faulted.</summary>
        internal static void Error(string message) => MelonLoader.MelonLogger.Error(message);

        /// <summary>
        /// A warning a Release build keeps — for the one thing the person running that build has to
        /// be told.
        ///
        /// The line between this and <see cref="Warn"/> is the <b>reader</b>, not the severity.
        /// `Warn` is the mod talking to itself: which rule refused which folder, and why, is what an
        /// author reads in a Debug build, and a shipped build has no business carrying a sentence
        /// about a mistake somebody already knows about. This is the other kind — the mod has left a
        /// song out of the game in front of the user, and the alternative to one line is a song that
        /// is simply not there.
        ///
        /// It is a warning and not an <see cref="Error"/> because the mod has not stopped working:
        /// everything else loaded. Every string passed here ships, so there should be very few of
        /// them — which is also why this is a separate method rather than a flag on `Warn`: a
        /// `[Conditional]` call cannot be conditional per call site, so the choice has to be visible
        /// at the call site, and it is.
        /// </summary>
        internal static void WarnRelease(string message) => MelonLoader.MelonLogger.Warning(message);

        /// <summary>
        /// Something is wrong and the mod works around it: a missing hook, an offset that would not
        /// resolve, a chart folder that was rejected. Debug builds only, like <see cref="Info"/> —
        /// see the class summary — and the summary lines above are what a release has instead.
        /// </summary>
        [Conditional("DEBUG")]
        internal static void Warn(string message)
        {
#if DEBUG
            MelonLoader.MelonLogger.Warning(message);
#endif
        }

        /// <summary>
        /// Flattens an exception chain into one line, outermost cause first.
        ///
        /// This is the one thing every failure in this mod needs: reflection and interop wrap the
        /// real cause in an InnerException, so the top-level message on its own rarely says what is
        /// wrong.
        ///
        /// The walk is Debug-only; Release keeps the type name, because the method has to exist for
        /// the log calls that name it (see the class summary) and those calls are gone anyway.
        /// </summary>
        internal static string Describe(System.Exception e)
        {
            if (e == null) return "<null>";

#if DEBUG
            var sb = new System.Text.StringBuilder();
            for (System.Exception x = e; x != null; x = x.InnerException)
            {
                if (sb.Length > 0) sb.Append(" <- ");
                sb.Append(x.GetType().Name).Append(": ").Append(x.Message);
            }
            return sb.ToString();
#else
            return e.GetType().Name;
#endif
        }
    }
}
