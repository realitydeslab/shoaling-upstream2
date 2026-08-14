using System;
using System.Threading;
using System.Threading.Tasks;
using ShoalingUpstream.Journey;

namespace ShoalingUpstream.Localization
{
    public enum AnchorRoute
    {
        None,

        /// <summary>The payload baked into the journey document at authoring time.</summary>
        StoredPayload,

        /// <summary>A payload fetched at runtime from the Sites API by device location.</summary>
        RuntimeLookup,
    }

    /// <summary>One candidate from a runtime lookup, flattened out of NSDK's SiteAssetsInfo /
    /// AssetInfo pair so the selection rules can be tested without the SDK.</summary>
    public readonly struct SiteAssetCandidate
    {
        public readonly string SiteId;
        public readonly string AssetId;
        public readonly string AnchorPayload;
        public readonly bool IsProduction;
        public readonly double DistanceMetres;

        public SiteAssetCandidate(string siteId, string assetId, string anchorPayload,
                                  bool isProduction, double distanceMetres)
        {
            SiteId = siteId; AssetId = assetId; AnchorPayload = anchorPayload;
            IsProduction = isProduction; DistanceMetres = distanceMetres;
        }
    }

    /// <summary>The seam over <c>SitesClient.RequestSiteAssetsByLocationAsync</c>.</summary>
    public interface ISiteAssetLookup
    {
        /// <summary>True if the device knows where it is well enough to ask. Without a
        /// geolocation the runtime route does not exist, which is a different failure from the
        /// lookup returning nothing and has to be reported differently.</summary>
        bool TryGetDeviceLocation(out double latitude, out double longitude);

        Task<SiteAssetCandidate[]> FindNearbyAsync(
            double latitude, double longitude, double radiusMetres, CancellationToken ct);
    }

    public readonly struct AnchorResolution
    {
        public readonly bool Success;
        public readonly AnchorRoute Route;
        public readonly string Payload;
        public readonly string AssetId;

        /// <summary>
        /// False when the payload came from an asset other than the one the journey was authored
        /// against.
        ///
        /// This matters more than it looks. Re-generating or re-promoting a site asset may move
        /// the anchor's origin frame, and the authored beat positions are metres relative to
        /// that origin. A mismatch means localization will succeed and the beats may still be in
        /// the wrong place — the worst combination available. It is used anyway, because a
        /// working fix in a possibly-shifted frame beats no fix, but it is flagged so the
        /// operator knows to watch for beats landing wrong and to fire them by hand.
        /// </summary>
        public readonly bool AssetMatchesJourney;

        public readonly string Detail;

        public AnchorResolution(bool success, AnchorRoute route, string payload, string assetId,
                                bool assetMatchesJourney, string detail)
        {
            Success = success; Route = route; Payload = payload; AssetId = assetId;
            AssetMatchesJourney = assetMatchesJourney; Detail = detail;
        }

        public static AnchorResolution Failure(string detail) =>
            new(false, AnchorRoute.None, null, null, false, detail);
    }

    /// <summary>
    /// Finds an anchor payload, by two routes, in the order that is right for a field session.
    ///
    /// <b>Stored payload first, always.</b> It is the payload the journey was authored against
    /// and recorded beside <c>vpsAssetId</c>, so it is the only route that guarantees the
    /// authored coordinates and the anchor frame agree. It needs no network and no auth token,
    /// and it is instant. In a park on a laptop hotspot those are not small advantages.
    ///
    /// <b>Runtime lookup second, when the first is absent or refused.</b> Appropriate when the
    /// payload was never baked, or when the asset has been re-promoted since and the baked
    /// payload is stale. It costs a round trip and a live OAuth token — and tokens expire
    /// (docs/nsdk-api-notes.md), so it cannot be the primary route for a session in the field.
    /// It is also the only route that can pick the site from where the visitor is standing,
    /// which is what multi-site switching will eventually be built on.
    /// </summary>
    public class AnchorResolver
    {
        private readonly ISiteAssetLookup _lookup;
        private readonly double _searchRadiusMetres;

        /// <summary>Wide enough to find a site from the far end of its own reach and from the
        /// car park, narrow enough not to return a neighbouring creek.</summary>
        public const double DefaultSearchRadiusMetres = 250;

        public AnchorResolver(ISiteAssetLookup lookup, double searchRadiusMetres = DefaultSearchRadiusMetres)
        {
            _lookup = lookup;
            _searchRadiusMetres = searchRadiusMetres;
        }

        /// <summary>The stored route on its own. Synchronous, offline, and the only one that can
        /// run before the AR session has any idea where it is.</summary>
        public static AnchorResolution FromStoredPayload(SiteRef site)
        {
            if (site == null || string.IsNullOrWhiteSpace(site.anchorPayload))
            {
                return AnchorResolution.Failure("journey carries no anchor payload");
            }

            return new AnchorResolution(
                true, AnchorRoute.StoredPayload, site.anchorPayload.Trim(), site.vpsAssetId, true,
                $"stored payload, authored against asset {Short(site.vpsAssetId)}");
        }

        public async Task<AnchorResolution> ResolveAsync(SiteRef site, CancellationToken ct = default)
        {
            var stored = FromStoredPayload(site);
            if (stored.Success) return stored;

            if (_lookup == null)
            {
                return AnchorResolution.Failure(
                    $"{stored.Detail}, and no runtime lookup is available");
            }

            if (!_lookup.TryGetDeviceLocation(out double lat, out double lng))
            {
                return AnchorResolution.Failure(
                    $"{stored.Detail}, and the device has no geolocation to look one up with");
            }

            SiteAssetCandidate[] candidates;
            try
            {
                candidates = await _lookup.FindNearbyAsync(lat, lng, _searchRadiusMetres, ct)
                             ?? Array.Empty<SiteAssetCandidate>();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                return AnchorResolution.Failure($"{stored.Detail}, and the runtime lookup failed: {e.Message}");
            }

            return Select(site, candidates, stored.Detail);
        }

        /// <summary>Candidate selection, separated so the preference order is testable against a
        /// fixed list rather than against a network.</summary>
        public static AnchorResolution Select(SiteRef site, SiteAssetCandidate[] candidates, string storedDetail)
        {
            if (candidates == null || candidates.Length == 0)
            {
                return AnchorResolution.Failure($"{storedDetail}, and no sites were found nearby");
            }

            bool sawUnpromoted = false;
            SiteAssetCandidate? exact = null, sameSite = null, nearest = null;

            foreach (var c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c.AnchorPayload)) continue;

                // The sample filters on Production, and so do we: a non-production asset is one
                // somebody is still working on, and localizing against it would put the visitor
                // in whatever frame that work-in-progress happens to have. It is also the known
                // blocker at UBC, which is why it is reported by name below rather than as a
                // generic empty result.
                if (!c.IsProduction) { sawUnpromoted = true; continue; }

                if (site != null && !string.IsNullOrEmpty(site.vpsAssetId) && c.AssetId == site.vpsAssetId)
                {
                    exact ??= c;
                }
                else if (site != null && !string.IsNullOrEmpty(site.nianticSiteId) && c.SiteId == site.nianticSiteId)
                {
                    if (sameSite == null || c.DistanceMetres < sameSite.Value.DistanceMetres) sameSite = c;
                }
                else if (nearest == null || c.DistanceMetres < nearest.Value.DistanceMetres)
                {
                    nearest = c;
                }
            }

            if (exact.HasValue)
            {
                return new AnchorResolution(
                    true, AnchorRoute.RuntimeLookup, exact.Value.AnchorPayload, exact.Value.AssetId, true,
                    $"runtime lookup found the authored asset {Short(exact.Value.AssetId)}");
            }

            if (sameSite.HasValue)
            {
                return new AnchorResolution(
                    true, AnchorRoute.RuntimeLookup, sameSite.Value.AnchorPayload, sameSite.Value.AssetId, false,
                    $"runtime lookup found the right site but asset {Short(sameSite.Value.AssetId)}, "
                    + $"not the authored {Short(site?.vpsAssetId)} — beat positions may be offset");
            }

            if (nearest.HasValue)
            {
                return new AnchorResolution(
                    true, AnchorRoute.RuntimeLookup, nearest.Value.AnchorPayload, nearest.Value.AssetId, false,
                    $"runtime lookup fell back to the nearest production asset {Short(nearest.Value.AssetId)} "
                    + $"at {nearest.Value.DistanceMetres:F0} m — this is not the authored site");
            }

            return AnchorResolution.Failure(sawUnpromoted
                ? $"{storedDetail}, and every nearby VPS asset is not Set to production in the portal"
                : $"{storedDetail}, and no nearby site carries a VPS anchor payload");
        }

        private static string Short(string id) =>
            string.IsNullOrEmpty(id) ? "(none)" : id.Length <= 8 ? id : id.Substring(0, 8);
    }
}
