using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ShoalingUpstream.Config
{
    /// <summary>How to reach the three places a journey can come from.</summary>
    public sealed class JourneyProviderSettings
    {
        /// <summary>Which site this build is walking. Beats and revision numbers are per site,
        /// so a document for any other one is refused rather than merged.</summary>
        public string Slug;

        /// <summary>e.g. "http://192.168.1.24:8710". Empty means "no laptop today", which is the
        /// normal case for the standalone TestFlight walk and costs nothing at launch.</summary>
        public string ServiceHost;

        /// <summary>
        /// How long the launch will wait for the laptop before going with what it already has.
        ///
        /// Two and a half seconds because the piece opens with sound and a silent start reads as
        /// a crash within about three. A phone that is on the wrong wifi does not refuse the
        /// connection — it hangs until the OS gives up, tens of seconds later — so this deadline,
        /// not the socket, is what makes a dead network cost a pause instead of a session.
        /// </summary>
        public float ServiceTimeoutSeconds = 2.5f;

        public JourneyEnvironment Environment = JourneyResolver.DetectEnvironment();
    }

    /// <summary>
    /// Resolves the journey the app will run, at launch, from the build, the last good fetch and
    /// the service — in that order of consultation, though not of preference.
    ///
    /// The precedence itself, and the reasoning behind it, lives in <see cref="JourneyResolver"/>.
    /// What this class adds is everything that can go wrong while gathering the candidates:
    ///
    /// * The bundled journey is read first and always, so the candidate list is never empty
    ///   through anyone else's fault. It is also offered to the resolver first, which is how
    ///   equal revisions end up resolving to the copy that has its audio beside it.
    ///
    /// * The service is given a deadline of its own rather than being trusted to fail. A network
    ///   that is merely absent fails fast; a network that is present and cannot route to the
    ///   laptop — a phone on cellular, a park with municipal wifi, the wrong SSID — hangs for as
    ///   long as the OS allows, and that is a launch nobody waits through.
    ///
    /// * A fetch is written to the cache only after it has parsed. A document that is not good
    ///   enough to run is not good enough to keep, and this is the single guard that stops a bad
    ///   afternoon at the creek from becoming a bad launch in a park with no wifi at all.
    ///
    /// * A cached document that fails to parse is deleted rather than left to fail again.
    /// </summary>
    public sealed class JourneyProvider
    {
        private readonly JourneyProviderSettings _settings;
        private readonly IJourneyReader _bundled;
        private readonly IJourneyCache _cache;
        private readonly IJourneyReader _service;

        public JourneyProvider(
            JourneyProviderSettings settings,
            IJourneyReader bundled,
            IJourneyCache cache = null,
            IJourneyReader service = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _bundled = bundled;
            _cache = cache;
            _service = service;
        }

        /// <summary>The wiring a real launch uses. A service host is optional; without one this
        /// is exactly the standalone configuration the Berkeley walk runs on.</summary>
        public static JourneyProvider ForLaunch(JourneyProviderSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(settings.Slug))
            {
                throw new ArgumentException("a slug is required", nameof(settings));
            }

            IJourneyReader service = string.IsNullOrWhiteSpace(settings.ServiceHost)
                ? null
                : new ServiceJourneyReader(
                    settings.ServiceHost, settings.Slug, settings.ServiceTimeoutSeconds);

            return new JourneyProvider(
                settings,
                FileJourneyReader.Bundled(settings.Slug),
                PersistentJourneyCache.ForSite(settings.Slug),
                service);
        }

        /// <summary>
        /// Gather every candidate and decide. Never throws for an unreachable service, an
        /// unreadable cache or an unpublished site; the reasons come back in
        /// <see cref="JourneyResolution.Notes"/> so a failed field test leaves a record.
        /// </summary>
        public async Task<JourneyResolution> ResolveAsync(
            CancellationToken cancellationToken = default)
        {
            var candidates = new List<JourneyCandidate>();
            var failures = new List<string>();

            string bundled = await ReadAsync(_bundled, JourneySourceKind.Bundled, failures);
            if (bundled != null) candidates.Add(new JourneyCandidate(JourneySourceKind.Bundled, bundled));

            string cached = await ReadAsync(_cache, JourneySourceKind.Cache, failures);
            if (cached != null) candidates.Add(new JourneyCandidate(JourneySourceKind.Cache, cached));

            string fetched = await FetchAsync(failures, cancellationToken);
            if (fetched != null) candidates.Add(new JourneyCandidate(JourneySourceKind.Service, fetched));

            var resolution = JourneyResolver.Resolve(
                candidates, _settings.Environment, _settings.Slug);
            resolution.Notes.InsertRange(0, failures);

            if (fetched != null)
            {
                await UpdateCacheAsync(fetched, resolution, cancellationToken);
            }
            else if (resolution.VerdictFor(JourneySourceKind.Cache) == CandidateVerdict.Unreadable)
            {
                // A cache that cannot be parsed will not become parseable by being read again,
                // and the launch it ruins is the one with no other source available. Only when
                // no fetch replaced it this launch, or the write below has already dealt with it.
                _cache?.Discard();
                resolution.Notes.Add("Cache: discarded, it could not be read");
            }

            return resolution;
        }

        private async Task<string> ReadAsync(
            IJourneyReader reader, JourneySourceKind kind, List<string> failures)
        {
            if (reader == null) return null;
            try
            {
                return await reader.ReadAsync(CancellationToken.None);
            }
            catch (Exception e)
            {
                failures.Add($"{kind}: unreadable — {e.Message}");
                return null;
            }
        }

        private async Task<string> FetchAsync(
            List<string> failures, CancellationToken cancellationToken)
        {
            if (_service == null) return null;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var read = _service.ReadAsync(deadline.Token);
            var expiry = Task.Delay(
                TimeSpan.FromSeconds(_settings.ServiceTimeoutSeconds), cancellationToken);

            // Racing the deadline rather than relying on the read to honour the token: the point
            // of the timeout is that the launch continues even when the far end never answers,
            // and a source that swallows cancellation must not be able to defeat that.
            if (await Task.WhenAny(read, expiry) != read)
            {
                deadline.Cancel();
                failures.Add(
                    $"Service: no answer within {_settings.ServiceTimeoutSeconds:0.#} s — "
                    + "carrying on with what is already on the device");
                return null;
            }

            try
            {
                string json = await read;
                if (json == null) failures.Add("Service: nothing published for this site");
                return json;
            }
            catch (Exception e)
            {
                failures.Add($"Service: unreachable — {e.Message}");
                return null;
            }
        }

        private async Task UpdateCacheAsync(
            string fetched, JourneyResolution resolution, CancellationToken cancellationToken)
        {
            if (_cache == null) return;

            // Keep a fetch that parsed, even one this device then refuses to run. An
            // uncalibrated journey is still the right thing to have on hand for the next
            // simulation, and holding it costs nothing: the resolver refuses it again every
            // launch, from the cache exactly as it did from the wire. What must never be
            // written is a document that did not parse — that is the file that ruins the next
            // launch in a park with no wifi.
            // A document for another site is not kept either: the cache is keyed by slug, and
            // filing Berkeley's journey under UBC's name would make a mistyped host survive the
            // launch that produced it.
            var verdict = resolution.VerdictFor(JourneySourceKind.Service);
            if (verdict == null
                || verdict == CandidateVerdict.Unreadable
                || verdict == CandidateVerdict.WrongSite)
            {
                return;
            }

            try
            {
                await _cache.WriteAsync(fetched, cancellationToken);
            }
            catch (Exception e)
            {
                resolution.Notes.Add($"Cache: could not be written — {e.Message}");
            }
        }

        /// <summary>Write the resolution to the log. Called once at launch — when a field test
        /// turns out to have been running the wrong revision, this is the record of why.</summary>
        public static void Log(JourneyResolution resolution)
        {
            var text = new System.Text.StringBuilder();
            text.AppendLine($"[Config] {resolution}");
            foreach (string note in resolution.Notes) text.AppendLine($"         {note}");
            foreach (var outcome in resolution.Outcomes) text.AppendLine($"         {outcome}");

            if (resolution.HasJourney) Debug.Log(text.ToString().TrimEnd());
            else Debug.LogError(text.ToString().TrimEnd());
        }
    }
}
