using UnityEngine;

namespace ShoalingUpstream.Experience
{
    public static class NaturalFishMotion
    {
        // Natural vertical buoyancy is not a failed arrival at a horizontal swim target.
        public static bool Arrived(Vector3 position, Vector3 target)
        {
            Vector3 delta = target - position;
            return delta.x * delta.x + delta.z * delta.z <= .025f * .025f && Mathf.Abs(delta.y) <= .04f;
        }
        public static float CruiseSpeed(float current, float requested, float deltaTime)
            => Mathf.MoveTowards(current, Mathf.Clamp(requested, 0f, 1.44f), .36f * Mathf.Max(0f, deltaTime));

        // atan2 preserves tiny frame-by-frame angles; acos-based SignedAngle can round
        // them to zero at the high frame rates used by batch-mode verification.
        public static float YawDelta(Vector3 from, Vector3 to)
            => Mathf.Atan2(Vector3.Cross(from, to).y, Vector3.Dot(from, to)) * Mathf.Rad2Deg;
        public static Vector3 TurnHeading(Vector3 heading, Vector3 desired, float maximumDegrees)
        {
            heading.y = desired.y = 0f;
            if (heading.sqrMagnitude < .001f) heading = Vector3.forward;
            if (desired.sqrMagnitude < .001f) return heading.normalized;
            float angle = YawDelta(heading, desired);
            return (Quaternion.AngleAxis(Mathf.Clamp(angle, -maximumDegrees, maximumDegrees), Vector3.up) * heading.normalized).normalized;
        }
        // No translation until the head is aligned. A reversal is a turn, then a swim.
        public static Vector3 Step(Vector3 position, ref Vector3 heading, Vector3 target,
            float speed, float turnDegreesPerSecond, float deltaTime)
        {
            Vector3 direction = target - position; direction.y = 0f;
            Vector3 shift = Vector3.zero;
            if (direction.sqrMagnitude > .0001f)
            {
                heading = TurnHeading(heading, direction, turnDegreesPerSecond * deltaTime);
                if (Vector3.Dot(heading, direction.normalized) > .995f)
                    shift = heading * Mathf.Min(speed * deltaTime, direction.magnitude);
            }
            // Buoyancy/depth adjustment is independent of horizontal swimming.
            shift.y = Mathf.MoveTowards(position.y, target.y, speed * deltaTime) - position.y;
            return shift;
        }
    }
}
