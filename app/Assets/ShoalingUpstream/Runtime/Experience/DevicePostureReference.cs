using UnityEngine;

namespace ShoalingUpstream.Experience
{
    // Raw device gravity is independent of autorotating the screen/camera image.
    public sealed class DevicePostureReference
    {
        private Vector3? _heldGravity;
        public bool Calibrated => _heldGravity.HasValue;
        public void Calibrate(Vector3 gravity)
        {
            if (gravity.sqrMagnitude > .1f) _heldGravity = gravity.normalized;
        }
        public bool IsPutDown(Vector3 gravity) => _heldGravity.HasValue && gravity.sqrMagnitude > .1f
            && Vector3.Dot(_heldGravity.Value, gravity.normalized) < .15f;
    }
}
