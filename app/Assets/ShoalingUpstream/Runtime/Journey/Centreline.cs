using System.Collections.Generic;
using UnityEngine;

namespace ShoalingUpstream.Journey
{
    /// <summary>
    /// Reduces the reach to one dimension.
    ///
    /// The site is a line, not a field: a ~34-61 m creek run with beats 6-10 m apart. Working
    /// in a single along-stream coordinate throws away all cross-stream pose noise before it
    /// can affect a trigger, and makes "has the visitor moved upstream" a scalar comparison
    /// rather than a geometry problem.
    /// </summary>
    public static class Centreline
    {
        public readonly struct Projection
        {
            public readonly float S;
            public readonly float Lateral;
            public readonly Vector3 Closest;

            public Projection(float s, float lateral, Vector3 closest)
            {
                S = s; Lateral = lateral; Closest = closest;
            }
        }

        public static Projection Project(Vector3 point, IReadOnlyList<Vec3> centreline)
        {
            var best = new Projection(0f, float.MaxValue, point);
            float travelled = 0f;

            for (int i = 0; i < centreline.Count - 1; i++)
            {
                Vector3 a = centreline[i].ToVector3();
                Vector3 b = centreline[i + 1].ToVector3();
                Vector3 ab = b - a;
                float segLen = ab.magnitude;
                if (segLen < 1e-6f) continue;

                float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / (segLen * segLen));
                Vector3 closest = a + ab * t;
                float lateral = Vector3.Distance(point, closest);

                if (lateral < best.Lateral)
                {
                    best = new Projection(travelled + segLen * t, lateral, closest);
                }
                travelled += segLen;
            }
            return best;
        }

        public static Vector3 PointAt(float s, IReadOnlyList<Vec3> centreline)
        {
            float travelled = 0f;
            for (int i = 0; i < centreline.Count - 1; i++)
            {
                Vector3 a = centreline[i].ToVector3();
                Vector3 b = centreline[i + 1].ToVector3();
                float segLen = Vector3.Distance(a, b);
                if (segLen < 1e-6f) continue;

                if (travelled + segLen >= s || i == centreline.Count - 2)
                {
                    float t = Mathf.Clamp01((s - travelled) / segLen);
                    return Vector3.Lerp(a, b, t);
                }
                travelled += segLen;
            }
            return centreline.Count > 0 ? centreline[^1].ToVector3() : Vector3.zero;
        }

        public static float Length(IReadOnlyList<Vec3> centreline)
        {
            float total = 0f;
            for (int i = 0; i < centreline.Count - 1; i++)
            {
                total += Vector3.Distance(centreline[i].ToVector3(), centreline[i + 1].ToVector3());
            }
            return total;
        }
    }
}
