using System.Collections.Generic;
using ShoalingUpstream.Journey;

namespace ShoalingUpstream.Config
{
    /// <summary>Where a candidate journey came from.</summary>
    public enum JourneySourceKind
    {
        /// <summary>Packaged into the build by tools/export-to-unity.mjs.</summary>
        Bundled,

        /// <summary>The last fetch that completed, kept in persistentDataPath.</summary>
        Cache,

        /// <summary>Fetched from the service on the laptop, this launch.</summary>
        Service,
    }

    /// <summary>
    /// Whether this run is a walk in a real creek or a simulation at a desk. It decides one
    /// thing only, and it is the most consequential decision in this file: see
    /// <see cref="JourneyResolver"/>.
    /// </summary>
    public enum JourneyEnvironment { Device, Simulation }

    /// <summary>What became of one candidate.</summary>
    public enum CandidateVerdict
    {
        /// <summary>Running this one.</summary>
        Accepted,

        /// <summary>Good, but another source offered a newer revision.</summary>
        Superseded,

        /// <summary>Did not parse: malformed, truncated, empty, or a schema we do not read.</summary>
        Unreadable,

        /// <summary>A journey for some other site.</summary>
        WrongSite,

        /// <summary>Uncalibrated, and this is a device rather than a simulation.</summary>
        Uncalibrated,
    }

    /// <summary>One source's answer, as raw text. Text, not a document, because refusing to
    /// parse is itself one of the outcomes the resolver has to report on.</summary>
    public readonly struct JourneyCandidate
    {
        public readonly JourneySourceKind Source;
        public readonly string Json;

        public JourneyCandidate(JourneySourceKind source, string json)
        {
            Source = source;
            Json = json;
        }
    }

    /// <summary>One line of the account of how the journey was chosen.</summary>
    public readonly struct CandidateOutcome
    {
        public readonly JourneySourceKind Source;
        public readonly CandidateVerdict Verdict;
        public readonly string Note;

        public CandidateOutcome(JourneySourceKind source, CandidateVerdict verdict, string note)
        {
            Source = source;
            Verdict = verdict;
            Note = note;
        }

        public override string ToString() => $"{Source}: {Verdict.ToString().ToLowerInvariant()} — {Note}";
    }

    /// <summary>What the app will run, and the full account of how that was decided.</summary>
    public sealed class JourneyResolution
    {
        public JourneyDocument Document { get; internal set; }
        public JourneySourceKind Source { get; internal set; }
        public bool HasJourney => Document != null;

        /// <summary>Every candidate the resolver saw, in the order it saw them.</summary>
        public List<CandidateOutcome> Outcomes { get; } = new();

        /// <summary>Free-text lines the provider adds for sources that never produced a
        /// candidate at all — an unreachable laptop, an empty cache. Logged with the outcomes:
        /// when a field test plays the wrong revision this is the only record of why.</summary>
        public List<string> Notes { get; } = new();

        public CandidateVerdict? VerdictFor(JourneySourceKind source)
        {
            foreach (var outcome in Outcomes)
            {
                if (outcome.Source == source) return outcome.Verdict;
            }
            return null;
        }

        public override string ToString() =>
            HasJourney
                ? $"{Document.journeyId} r{Document.revision} from {Source}"
                : "no journey";
    }

    /// <summary>
    /// Chooses which of the available journeys the app runs.
    ///
    /// PRECEDENCE — bundled is the floor, a newer revision is the override.
    ///
    /// The app has to work in three situations that pull in different directions. The Berkeley
    /// coauthor installs from TestFlight and walks a creek alone, so everything must already be
    /// in the build. The artist runs the service on a laptop on the same wifi and expects a
    /// freshly published revision to appear without a rebuild. And both of them sometimes sit
    /// at a desk with neither creek nor laptop.
    ///
    /// So the rule is not "prefer the network" and not "prefer the build". It is:
    ///
    ///   among the candidates that parse, are for this site, and are allowed to run here,
    ///   run the one with the highest revision number; ties go to the bundled copy.
    ///
    /// Revisions are immutable and monotonic per site — the service only ever appends — which
    /// is what makes them comparable across sources at all. Two consequences are the point of
    /// the design rather than side effects of it:
    ///
    /// * A STALE LAPTOP CANNOT UNDO A BUILD. The published revision on a laptop restored from
    ///   an old copy of data/journeys can easily be older than the one packaged into TestFlight.
    ///   "Newest wins" keeps the build; "network wins" would quietly walk the visitor through a
    ///   layout that was superseded weeks ago, and nobody would notice until the beats were in
    ///   the wrong places.
    ///
    /// * THE CACHE CANNOT PIN A SESSION TO THE PAST. It is only ever consulted as another
    ///   numbered candidate, so a fresher build or a reachable service overtakes it without any
    ///   invalidation step.
    ///
    /// Ties go to bundled because a bundled journey is the only one whose audio is guaranteed to
    /// be in the build beside it; the same revision fetched over the wire is at best equal. That
    /// is expressed by offering candidates bundled-first and requiring a challenger to be
    /// strictly newer.
    ///
    /// REFUSALS. Two conditions disqualify a candidate outright rather than degrading it:
    ///
    /// * An UNCALIBRATED journey (editorFrame.calibrated == false) is refused on device. Its
    ///   coordinates are authored in splat space and have never been matched to three physical
    ///   points, so against a real VPS anchor every beat is in the wrong place — the failure
    ///   this whole system is most able to hide. In simulation it runs, because that is exactly
    ///   where uncalibrated coordinates are supposed to be exercised.
    ///
    /// * A journey for a DIFFERENT SITE is refused. Revision numbers are per site; comparing
    ///   Berkeley's r7 against UBC's r3 is meaningless, and a mistyped host is a plausible way
    ///   to arrive at one.
    ///
    /// Refusing everything is a legitimate outcome and the caller must handle it — but it can
    /// only happen when the bundled journey is itself refused, which is a build error that
    /// tools/export-to-unity.mjs exists to prevent.
    /// </summary>
    public static class JourneyResolver
    {
        public static JourneyResolution Resolve(
            IEnumerable<JourneyCandidate> candidates,
            JourneyEnvironment environment,
            string expectedSlug = null)
        {
            var resolution = new JourneyResolution();

            foreach (var candidate in candidates)
            {
                if (!JourneyParser.TryParse(candidate.Json, out var document, out var error))
                {
                    Record(resolution, candidate.Source, CandidateVerdict.Unreadable, error);
                    continue;
                }

                if (!string.IsNullOrEmpty(expectedSlug) && document.site.slug != expectedSlug)
                {
                    Record(resolution, candidate.Source, CandidateVerdict.WrongSite,
                        $"is for \"{document.site.slug}\", this build walks \"{expectedSlug}\"");
                    continue;
                }

                bool calibrated = document.editorFrame != null && document.editorFrame.calibrated;
                if (!calibrated && environment == JourneyEnvironment.Device)
                {
                    Record(resolution, candidate.Source, CandidateVerdict.Uncalibrated,
                        $"r{document.revision} has never been matched to three physical points, "
                        + "so against a real anchor every beat would be in the wrong place. "
                        + "It runs in simulation only.");
                    continue;
                }

                if (resolution.HasJourney && document.revision <= resolution.Document.revision)
                {
                    Record(resolution, candidate.Source, CandidateVerdict.Superseded,
                        $"r{document.revision} is not newer than {resolution.Source} "
                        + $"r{resolution.Document.revision}");
                    continue;
                }

                Record(resolution, candidate.Source, CandidateVerdict.Accepted,
                    $"r{document.revision}{(calibrated ? "" : ", uncalibrated (simulation)")}");
                resolution.Document = document;
                resolution.Source = candidate.Source;
            }

            if (!resolution.HasJourney)
            {
                resolution.Notes.Add(resolution.Outcomes.Count == 0
                    ? "no journey was available from any source — this build was never packaged"
                    : "every journey available was refused");
            }

            // An earlier acceptance that a newer revision has since overtaken is reported as
            // superseded, not as the answer — otherwise the log claims two winners.
            for (int i = 0; i < resolution.Outcomes.Count; i++)
            {
                var outcome = resolution.Outcomes[i];
                if (outcome.Verdict != CandidateVerdict.Accepted) continue;
                if (resolution.HasJourney && outcome.Source == resolution.Source) continue;

                resolution.Outcomes[i] = new CandidateOutcome(
                    outcome.Source, CandidateVerdict.Superseded,
                    $"{outcome.Note}, overtaken by {resolution.Source}");
            }

            return resolution;
        }

        private static void Record(
            JourneyResolution resolution, JourneySourceKind source,
            CandidateVerdict verdict, string note) =>
            resolution.Outcomes.Add(new CandidateOutcome(source, verdict, note));

        /// <summary>Device on a phone, simulation everywhere else. The editor is a desk even
        /// when the build target is iOS, which is why this is not a platform test alone.</summary>
        public static JourneyEnvironment DetectEnvironment()
        {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
            return JourneyEnvironment.Device;
#else
            return JourneyEnvironment.Simulation;
#endif
        }
    }
}
