using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ShoalingUpstream.Journey;
using ShoalingUpstream.Localization;

namespace ShoalingUpstream.Localization.Tests
{
    /// <summary>
    /// Which anchor payload gets used, and how loudly the doubtful cases complain.
    ///
    /// The route order is an operational judgement, not a preference: the stored payload works
    /// with no network and no token and is the only one that guarantees the authored coordinates
    /// and the anchor frame agree. Everything here pins that order and the warnings that come
    /// with departing from it.
    /// </summary>
    public class AnchorResolverTests
    {
        private const string StoredPayload = "c3RvcmVkLXBheWxvYWQ=";
        private const string SiteId = "187bbd62-4d21-480e-8f8d-e484754aa22b";
        private const string AuthoredAsset = "4767ffbb-0ba9-420a-967b-bcd677ab90a5";

        private static SiteRef Site(string payload = StoredPayload) => new()
        {
            slug = "ucb-strawberry-creek-south",
            nianticSiteId = SiteId,
            vpsAssetId = AuthoredAsset,
            anchorPayload = payload,
        };

        private class FakeLookup : ISiteAssetLookup
        {
            public bool HasLocation = true;
            public SiteAssetCandidate[] Candidates = Array.Empty<SiteAssetCandidate>();
            public Exception Throws;
            public int Calls;

            public bool TryGetDeviceLocation(out double latitude, out double longitude)
            {
                latitude = 37.870617; longitude = -122.266053;
                return HasLocation;
            }

            public Task<SiteAssetCandidate[]> FindNearbyAsync(
                double latitude, double longitude, double radiusMetres, CancellationToken ct)
            {
                Calls++;
                if (Throws != null) throw Throws;
                return Task.FromResult(Candidates);
            }
        }

        private static AnchorResolution Resolve(SiteRef site, FakeLookup lookup) =>
            new AnchorResolver(lookup).ResolveAsync(site).GetAwaiter().GetResult();

        [Test]
        public void TheStoredPayloadIsUsedAndTheNetworkIsNeverTouched()
        {
            var lookup = new FakeLookup();
            var resolution = Resolve(Site(), lookup);

            Assert.IsTrue(resolution.Success);
            Assert.AreEqual(AnchorRoute.StoredPayload, resolution.Route);
            Assert.AreEqual(StoredPayload, resolution.Payload);
            Assert.IsTrue(resolution.AssetMatchesJourney);
            Assert.AreEqual(0, lookup.Calls,
                "a working stored payload must not cost a round trip on park wifi");
        }

        [Test]
        public void TheRuntimeLookupPrefersTheAssetTheJourneyWasAuthoredAgainst()
        {
            var lookup = new FakeLookup
            {
                Candidates = new[]
                {
                    new SiteAssetCandidate(SiteId, "newer-asset", "bmV3ZXI=", true, 4),
                    new SiteAssetCandidate(SiteId, AuthoredAsset, "YXV0aG9yZWQ=", true, 12),
                },
            };

            var resolution = Resolve(Site(payload: ""), lookup);

            Assert.IsTrue(resolution.Success);
            Assert.AreEqual(AnchorRoute.RuntimeLookup, resolution.Route);
            Assert.AreEqual(AuthoredAsset, resolution.AssetId,
                "the authored asset wins even when a nearer one exists");
            Assert.IsTrue(resolution.AssetMatchesJourney);
        }

        [Test]
        public void ADifferentAssetOnTheRightSiteIsUsedButFlagged()
        {
            // Re-promoting an asset can move the anchor origin under authored beat positions.
            // Localization will succeed and the beats may still be wrong, which is the worst
            // combination available — so it is used, and it is announced.
            var lookup = new FakeLookup
            {
                Candidates = new[]
                {
                    new SiteAssetCandidate(SiteId, "strawberry-creek-south-2", "cmVwcm9tb3RlZA==", true, 6),
                },
            };

            var resolution = Resolve(Site(payload: ""), lookup);

            Assert.IsTrue(resolution.Success);
            Assert.IsFalse(resolution.AssetMatchesJourney);
            StringAssert.Contains("offset", resolution.Detail);
        }

        [Test]
        public void AssetsThatAreNotSetToProductionAreSkippedAndSaidSo()
        {
            // The known blocker at the UBC garden site. It has to read as "promote the asset",
            // not as "no site found", or somebody will spend an afternoon on the wrong problem.
            var lookup = new FakeLookup
            {
                Candidates = new[]
                {
                    new SiteAssetCandidate(SiteId, "draft-asset", "ZHJhZnQ=", false, 3),
                },
            };

            var resolution = Resolve(Site(payload: ""), lookup);

            Assert.IsFalse(resolution.Success);
            StringAssert.Contains("production", resolution.Detail);
        }

        [Test]
        public void ANearbySiteThatIsNotOursIsAFallbackOfLastResortAndSaysHowFar()
        {
            var lookup = new FakeLookup
            {
                Candidates = new[]
                {
                    new SiteAssetCandidate("some-other-site", "other-asset", "b3RoZXI=", true, 180),
                },
            };

            var resolution = Resolve(Site(payload: ""), lookup);

            Assert.IsTrue(resolution.Success);
            Assert.IsFalse(resolution.AssetMatchesJourney);
            StringAssert.Contains("180 m", resolution.Detail);
            StringAssert.Contains("not the authored site", resolution.Detail);
        }

        [Test]
        public void NoGeolocationIsReportedAsItsOwnFailureNotAsAnEmptyResult()
        {
            var lookup = new FakeLookup { HasLocation = false };
            var resolution = Resolve(Site(payload: ""), lookup);

            Assert.IsFalse(resolution.Success);
            Assert.AreEqual(0, lookup.Calls);
            StringAssert.Contains("geolocation", resolution.Detail);
        }

        [Test]
        public void ALookupThatThrowsFailsWithItsReasonRatherThanTakingTheAppDown()
        {
            var lookup = new FakeLookup { Throws = new InvalidOperationException("token expired") };
            var resolution = Resolve(Site(payload: ""), lookup);

            Assert.IsFalse(resolution.Success);
            StringAssert.Contains("token expired", resolution.Detail,
                "a stale OAuth token is the likeliest field failure and must name itself");
        }

        [Test]
        public void AJourneyWithNoPayloadAndNoLookupFailsCleanly()
        {
            var resolution = new AnchorResolver(null).ResolveAsync(Site(payload: ""))
                .GetAwaiter().GetResult();

            Assert.IsFalse(resolution.Success);
            Assert.AreEqual(AnchorRoute.None, resolution.Route);
        }

        [Test]
        public void CandidatesWithNoPayloadAreIgnored()
        {
            var lookup = new FakeLookup
            {
                Candidates = new[]
                {
                    new SiteAssetCandidate(SiteId, "mesh-only", "", true, 2),
                    new SiteAssetCandidate(SiteId, AuthoredAsset, "YXV0aG9yZWQ=", true, 9),
                },
            };

            var resolution = Resolve(Site(payload: ""), lookup);
            Assert.AreEqual(AuthoredAsset, resolution.AssetId);
        }
    }
}
