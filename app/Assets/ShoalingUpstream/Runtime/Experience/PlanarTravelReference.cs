using UnityEngine;

namespace ShoalingUpstream.Experience
{
    // A shared travel direction, never a different bearing from each fish to the device.
    public sealed class PlanarTravelReference
    {
        public Vector3 Forward { get; private set; } = Vector3.forward;
        public bool Turning { get; private set; }
        public bool AwaitingParticipantTurn { get; private set; }
        private Vector3 _upstream;
        public void Observe(Vector3 deviceForward)
        {
            deviceForward.y = 0f;
            if (deviceForward.sqrMagnitude < .04f || Turning) return;
            deviceForward.Normalize();
            if (AwaitingParticipantTurn)
            {
                if (Vector3.Dot(deviceForward, _upstream) < .6f) return;
                AwaitingParticipantTurn = false;
            }
            Forward = deviceForward;
        }
        public void ObserveTravel(Vector3 deviceForward, Vector3 movement, bool walking)
        {
            movement.y = 0f;
            Observe(walking && movement.sqrMagnitude > .001f ? movement.normalized : deviceForward);
        }
        public void BeginReturn() { _upstream = -Forward; Turning = true; }
        public void CompleteReturn()
        { Forward = _upstream; Turning = false; AwaitingParticipantTurn = true; }
    }
}
