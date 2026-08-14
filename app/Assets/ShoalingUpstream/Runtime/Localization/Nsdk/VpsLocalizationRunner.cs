#if NSDK_PRESENT
using System;
using System.Threading;
using UnityEngine;
using Unity.XR.CoreUtils;
using NianticSpatial.NSDK.AR.Sites;
using NianticSpatial.NSDK.AR.VPS2;
using ShoalingUpstream.Journey;

namespace ShoalingUpstream.Localization.Nsdk
{
    /// <summary>
    /// Drives the localizer from Unity's update loop: resolve an anchor, hand it to VPS, then
    /// sample once a frame.
    ///
    /// This is the only MonoBehaviour in the localization stack, and it holds no logic worth
    /// testing — that is the point. Everything that could be wrong at a creek lives in
    /// <see cref="VpsLocalizer"/> and <see cref="AnchorResolver"/>, which run in EditMode.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class VpsLocalizationRunner : MonoBehaviour
    {
        [SerializeField] private ARVps2Manager _vps2Manager;
        [SerializeField] private XROrigin _origin;
        [SerializeField] private SitesClientManager _sitesClientManager;

        /// <summary>
        /// Whether this build is allowed to run an uncalibrated journey.
        ///
        /// Serialized rather than derived from a platform define, because the gate it feeds is
        /// the one guard against shipping provisional coordinates to a person standing in a
        /// creek. Somebody has to have set it deliberately.
        /// </summary>
        [SerializeField] private RuntimeSurface _surface = RuntimeSurface.Device;

        private VpsLocalizer _localizer;
        private Vps2VpsSource _source;
        private CancellationTokenSource _resolving;

        public VpsLocalizer Localizer => _localizer;

        /// <summary>Raised whenever the report changes materially — mode, source or a snap.
        /// The WebSocket client and the audio layer subscribe; neither polls at frame rate.</summary>
        public event Action<LocalizationReport> ReportChanged;

        private LocalizationMode _lastMode = LocalizationMode.Idle;
        private FixSource _lastSource = FixSource.None;

        /// <summary>
        /// Start a journey. Returns false when the calibration gate refused it, in which case
        /// <see cref="VpsLocalizer.RefusalReason"/> says why and the caller must not proceed —
        /// the whole point of the gate is that this failure is loud.
        /// </summary>
        public bool Run(JourneyDocument journey, LocalizationPolicy policy = null)
        {
            Stop();

            _localizer = new VpsLocalizer(journey, policy, _surface);
            if (_localizer.Mode == LocalizationMode.Refused)
            {
                Debug.LogError($"[localization] {_localizer.Report().Detail}");
                ReportChanged?.Invoke(_localizer.Report());
                return false;
            }

            _source = new Vps2VpsSource(_vps2Manager, _origin);
            _resolving = new CancellationTokenSource();
            ResolveThenBegin(journey.site, _resolving.Token);
            return true;
        }

        private async void ResolveThenBegin(SiteRef site, CancellationToken ct)
        {
            AnchorResolution resolution;
            try
            {
                var lookup = new SitesSiteAssetLookup(_sitesClientManager != null
                    ? _sitesClientManager.Client
                    : null, _vps2Manager);
                resolution = await new AnchorResolver(lookup).ResolveAsync(site, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                resolution = AnchorResolution.Failure($"anchor resolution threw: {e.Message}");
            }

            if (ct.IsCancellationRequested || _localizer == null) return;

            // A payload the SDK will not even accept is not a resolution. Demote it here rather
            // than letting the localizer spend its whole first-fix timeout waiting for an anchor
            // that was never tracked.
            if (resolution.Success && !_source.TryTrackAnchor(resolution.Payload))
            {
                resolution = AnchorResolution.Failure(
                    $"the SDK refused the payload from the {resolution.Route} route "
                    + "(malformed, or a private map this build has no permission for)");
            }

            _localizer.Begin(resolution);
            Debug.Log($"[localization] {resolution.Detail}");
            if (_localizer.AnchorFrameSuspect)
            {
                Debug.LogWarning($"[localization] {resolution.Detail}");
            }
            ReportChanged?.Invoke(_localizer.Report());
        }

        private void Update()
        {
            if (_localizer == null || _localizer.Mode == LocalizationMode.Refused) return;

            var sample = _source != null ? _source.Sample() : VpsSample.Nothing;
            _localizer.Tick(sample, Time.deltaTime);

            var report = _localizer.Report();
            if (report.Mode != _lastMode || report.Fix.Source != _lastSource || report.SnapMetres > 0.25f)
            {
                _lastMode = report.Mode;
                _lastSource = report.Fix.Source;
                ReportChanged?.Invoke(report);
            }
        }

        public void Stop()
        {
            _resolving?.Cancel();
            _resolving?.Dispose();
            _resolving = null;
            _source?.Stop();
            _source = null;
            _localizer = null;
            _lastMode = LocalizationMode.Idle;
            _lastSource = FixSource.None;
        }

        private void OnDestroy() => Stop();
    }
}
#endif
