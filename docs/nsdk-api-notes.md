# Niantic Spatial SDK (NSDK) 4.1.0 — verified API notes

Compiled 2026-08-13 by reading the actual package source, not the docs.
Package: `com.nianticspatial.nsdk` from `https://github.com/nianticspatial/nsdk-library-upm.git#4.1.0-26051913`
Samples: `https://github.com/nianticspatial/nsdk-samples-csharp` (public, ~305 MB)

Everything below is VERIFIED by reading source unless marked otherwise.

## Package facts

| | |
|---|---|
| Latest **tag** | `4.1.0-26051913` (= `main`) |
| Unreleased branch | `nsdk-upm-update-20260603-2574577096` carries `4.2.0-c.319965` — untagged pre-release, **do not pin** |
| Minimum Unity | `2021.3` (declared). Runs on 6000.5.7f1. |
| Declared AR Foundation dep | **6.4.2** (also arcore/arkit 6.4.2) |
| Other deps | `com.unity.xr.openxr` 1.6.0, `xr.core-utils` 2.1.0, `xr.management` 4.0.1, `nuget.newtonsoft-json` 3.2.1, `burst` 1.8.17, `editorcoroutines` 1.0.0 |

Note the AR Foundation pin is **6.4.2**, not 6.5.0. Taking AR Foundation 6.5.0 is an
override of the package's declared dependency. Prefer 6.4.2 unless something forces otherwise.

## Namespaces

- `NianticSpatial.NSDK.AR.VPS2` — managers, anchors, payloads
- `NianticSpatial.NSDK.AR.XRSubsystems` — tracking-state enums, localization structs
- `NianticSpatial.NSDK.AR.Sites` — the runtime Sites/Orgs/Assets REST client
- `NianticSpatial.NSDK.AR.Subsystems` — `LocationMeshManager` and mesh download

## Localization: `ARVps2Manager`

`public class ARVps2Manager : ARTrackableManager<...>` — an AR Foundation trackable manager,
so it goes on the XR Origin like any other AR manager.

Core methods:

```csharp
bool TryTrackAnchor(string anchorPayload, out ARVps2Anchor anchorOut);  // <- the main entry point
bool TryCreateAnchor(Pose localPose, out ARVps2Anchor anchorOut);
bool TryGetAnchorPayload(ARVps2Anchor anchor, out string payload);
void RemoveAnchor(ARVps2Anchor anchor);
bool TryGetLatestLocalization(out XRVps2Localization localizationOut);
bool TryGetDeviceGeolocation(out XRVps2Geolocation geolocationOut, ...);
bool TryGetPose(...);
bool GetSessionId(out string sessionId);
```

Events: `LocalizationRequestRecordAdded` (`Action<XRVps2LocalizationRequestRecord>`),
`DebuggerDataReceived` (`Action<VpsDebuggerDataEvent>`).

Tunables that matter for a creek under canopy:

| Property | Why we care |
|---|---|
| `VpsMapLocalizationEnabled` | localize against our private scanned map |
| `InitialVpsRequestsPerSecond` | how aggressively we try for first lock |
| `ContinuousVpsRequestsPerSecond` | re-localization rate while walking |
| `AnchorDistanceGateMeters` | gates anchors by distance — relevant to a 100 m linear route |
| `UniversalLocalizationEnabled` | Niantic's global coverage, not our scan |
| `DeviceMapLocalizationEnabled` / `DeviceMapLocalizationFramerate` | on-device mapping |
| `GeolocationSmoothingEnabled` | smoothing of the geolocation estimate |
| `MaxRequestsInTransitPerTarget` | network backpressure |
| `VpsDebuggerEnabled` | field diagnosis |

## The anchor: `ARVps2Anchor`

`public sealed class ARVps2Anchor : ARTrackable<XRVps2Anchor, ARVps2Anchor>`

```csharp
TrackingState trackingState;                          // Unity's None/Limited/Tracking
Vps2AnchorTrackingStateReason trackingStateReason;
float trackingConfidence;                             // <- degradation-ladder signal
Pose PredictedPose { get; }
UInt64 TimestampMs { get; }
XRGeolocation? geolocation { get; }
byte[] GetDataAsBytes();
```

The anchor is a `Transform` in the scene. **Authored content is parented to it**, so waypoints
are authored as offsets in the anchor's local frame. This is the sanctioned pattern and it is
what the sample does.

`ARVps2AnchorPayload` wraps the payload: constructors from `byte[]` and from `string`, plus
`ToBase64()`. The portal's base64 payload string goes straight into `TryTrackAnchor`.

## Tracking state enums

```csharp
enum Vps2TrackingState { Unavailable, Coarse, Precise }
```

Three states, not two — so the degradation ladder has a real middle rung. `Coarse` presumably
means geolocation-grade, `Precise` means visually localized.

```csharp
enum Vps2AnchorTrackingStateReason : uint {
    None = 0, Removed = 1, PermissionDenied = 3,
    Initializing, InternalError, FatalNetworkError, NoVisualLocalization
}
```

`PermissionDenied` is documented in-source as "this anchor is part of a private VPS map that
this application does not have permission to localize to" — the failure mode if auth or org
membership is wrong. `NoVisualLocalization` is the expected canopy/low-light failure.

## Runtime Sites API — `SitesClient`

This mirrors the portal REST API and removes any need to hardcode site IDs or anchor payloads.

```csharp
Task<UserResult>       RequestSelfUserInfoAsync(...);
Task<OrganizationResult> RequestSelfOrganizationInfoAsync(...);
Task<OrganizationResult> RequestOrganizationsForUserAsync(...);
Task<SiteResult>       RequestSitesForOrganizationAsync(...);
Task<SiteResult>       RequestSiteInfoAsync(...);
Task<AssetResult>      RequestAssetsForSiteAsync(...);
Task<AssetResult>      RequestAssetInfoAsync(...);
Task<SiteAssetsResult> RequestSiteAssetsByLocationAsync(...);   // <- "what sites are near me"
```

Data shapes:

```csharp
struct SiteInfo  { string Id, Name, Status, OrganizationId, ParentSiteId;
                   double Latitude, Longitude; bool HasLocation; }

struct AssetInfo { string Id, SiteId, Name, Description;
                   AssetType AssetType;               // Unspecified|Mesh|Splat|VpsInfo
                   AssetStatusType AssetStatus;       // Unspecified|Active|Inactive|Pending
                   AssetDeploymentType Deployment;    // Unspecified|Production
                   AssetMeshData?  MeshData;          // RootNodeId, NodeIds[], MeshCoverage (m²)
                   AssetSplatData? SplatData;         // RootNodeId
                   AssetVpsData?   VpsData;           // AnchorPayload  <- !!
                   string PipelineJobId;
                   AssetPipelineJobStatus PipelineJobStatus;
                   IReadOnlyList<string> SourceScanIds; }
```

**`AssetVpsData.AnchorPayload` is the anchor payload, fetchable at runtime.** Combined with
`RequestSiteAssetsByLocationAsync`, the app can discover which site the participant is standing
in and obtain its payload without any baked-in configuration. This is the clean basis for
multi-site switching.

`AssetDeploymentType.Production` confirms the portal's "Set to production" flag is exposed at
runtime; the official sample has a `_filterProductionAssetsToggle` that filters on exactly this.

## Auth

Not an API key. `Runtime/Auth/AuthBuildSettings.cs` is an OAuth-style token store:

```
RefreshToken / RefreshExpiresAt
AccessToken  / AccessExpiresAt
AccessTokenOverride
UseDeveloperAuthentication
AuthEnvironmentType
```

with a parallel editor-side `AuthEditorSettings` holding `EditorAccessToken` etc.
`NsdkUnityContext` still carries an `ApiKey` field but it is marked
`// TODO: ARDK-7769 Remove ApiKey when it is removed from underlying C API` and is set to empty.

**Operational consequence: tokens expire.** A field session needs a freshly minted token, and
the build must be able to take one without a source edit. The portal exposes both
"Service accounts" and "Developer tokens" under Credentials.

## Mesh download at runtime

`LocationMeshManager` + `MeshDownloadClient` (`Runtime/ARFoundation/Overrides/PersistentAnchors/MeshDownload/`)
download the site mesh on device, with `GlobalPoseData` and `NodeToLoad` types. The VPS2 sample
has a "download mesh" toggle. Useful for occlusion, and for showing the participant where the
scan actually covers.

## Canonical localization pattern (from `VPS2LocalizeDemo.cs`)

1. `SitesClientManager` → list orgs → sites → assets, filtered to Production.
2. User (or auto-detect) selects a site; take `AssetInfo.VpsData.AnchorPayload`.
3. `_arVps2Manager.TryTrackAnchor(payload, out _anchor)`.
4. Each frame, `TryGetLatestLocalization(out var localization)` → switch on
   `localization.TrackingState` for `Unavailable` / `Coarse` / `Precise`.
5. Watch `_anchor.trackingState` and `trackingStateReason` separately from the session state.
6. Parent content to the anchor transform.

## Open questions this did not answer

- Whether re-generating or re-promoting a site asset **invalidates a previously recorded anchor
  payload**. This matters: Strawberry Creek South has a newer non-production asset
  ("Strawberry Creek South 2") and promoting it could move the origin frame under authored
  waypoints. Treat the payload as versioned data tied to a specific asset ID.
- Real-world time-to-lock and success rate under tree canopy over moving water. Unknowable
  without a field test; this is the project's dominant risk.
- What `Coarse` actually corresponds to in metres of error.
