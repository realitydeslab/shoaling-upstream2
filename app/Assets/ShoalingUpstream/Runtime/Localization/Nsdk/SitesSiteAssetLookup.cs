#if NSDK_PRESENT
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NianticSpatial.NSDK.AR.Sites;
using NianticSpatial.NSDK.AR.VPS2;
using NianticSpatial.NSDK.AR.XRSubsystems;

namespace ShoalingUpstream.Localization.Nsdk
{
    /// <summary>
    /// The runtime anchor route, over <c>SitesClient.RequestSiteAssetsByLocationAsync</c>.
    ///
    /// Flattening NSDK's nested result into <see cref="SiteAssetCandidate"/> is the whole job.
    /// Which candidate to take, and how loudly to complain about it, is decided in
    /// <see cref="AnchorResolver"/> where it can be tested against a fixed list rather than
    /// against a network, a token and a park.
    /// </summary>
    public class SitesSiteAssetLookup : ISiteAssetLookup
    {
        private readonly SitesClient _client;
        private readonly ARVps2Manager _manager;

        public SitesSiteAssetLookup(SitesClient client, ARVps2Manager manager)
        {
            _client = client;
            _manager = manager;
        }

        public bool TryGetDeviceLocation(out double latitude, out double longitude)
        {
            latitude = 0; longitude = 0;
            if (_manager == null) return false;
            if (!_manager.TryGetDeviceGeolocation(out var geolocation)) return false;

            // Unavailable means the device does not know where it is at all. Coarse is fine here
            // and is exactly what this route needs: a query radius of a couple of hundred metres
            // does not care about the difference between Coarse and Precise, and demanding
            // Precise would make the fallback route depend on the localization it exists to
            // replace.
            if (geolocation.TrackingState == Vps2TrackingState.Unavailable) return false;

            latitude = geolocation.Geolocation.Latitude;
            longitude = geolocation.Geolocation.Longitude;
            return true;
        }

        public async Task<SiteAssetCandidate[]> FindNearbyAsync(
            double latitude, double longitude, double radiusMetres, CancellationToken ct)
        {
            if (_client == null) return Array.Empty<SiteAssetCandidate>();

            var result = await _client.RequestSiteAssetsByLocationAsync(
                latitude, longitude, radiusMetres, AssetType.VpsInfo, cancellationToken: ct);

            if (result.Status != SitesRequestStatus.Success) return Array.Empty<SiteAssetCandidate>();

            var candidates = new List<SiteAssetCandidate>();
            foreach (var entry in result.Entries)
            {
                if (entry.Assets == null) continue;
                foreach (var asset in entry.Assets)
                {
                    if (!asset.VpsData.HasValue) continue;

                    // Deployment, not AssetStatus. "Set to production" in the portal is the flag
                    // that says an asset is finished and safe to localize against; the official
                    // sample filters on exactly this, and the UBC garden asset currently fails it
                    // (docs/devlog.md), which is why an empty result here is reported as an
                    // un-promoted asset rather than as a missing site.
                    candidates.Add(new SiteAssetCandidate(
                        entry.Site.Id,
                        asset.Id,
                        asset.VpsData.Value.AnchorPayload,
                        asset.Deployment == AssetDeploymentType.Production,
                        entry.Distance));
                }
            }
            return candidates.ToArray();
        }
    }
}
#endif
