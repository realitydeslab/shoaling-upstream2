using UnityEngine;

namespace ShoalingUpstream.Simulation
{
    /// <summary>Keeps one point on a model — the middle of its feet — fixed on the ground, by
    /// moving the model's root by whatever the animation moved that point each frame.
    ///
    /// Needed because a clip's own baked turn (the heron's "Turn" rotates its Armature 180° about
    /// the model's origin) pivots on that origin, not on the feet: measured off HERON.glb, the
    /// origin sits 46% of the way up the body and the feet are about 4 cm of model space in front
    /// of it, so the feet swing round an arc and the whole bird visibly slides as it turns. Holding
    /// the feet in place turns that into spinning on the spot, whatever the clip's own pivot is.
    ///
    /// Runs in LateUpdate so it reads the pose the animation has just written this frame, and
    /// the correction is in the same frame it is drawn.</summary>
    public sealed class FeetLock : MonoBehaviour
    {
        private Transform _feet;
        private Vector3 _anchor;

        public static FeetLock Attach(Transform root, Transform feet)
        {
            var feetLock = root.gameObject.AddComponent<FeetLock>();
            feetLock._feet = feet;
            feetLock._anchor = feet.position;
            return feetLock;
        }

        private void LateUpdate()
        {
            if (_feet == null) return;
            Vector3 drift = _anchor - _feet.position;
            drift.y = 0f;
            transform.position += drift;
        }
    }
}
