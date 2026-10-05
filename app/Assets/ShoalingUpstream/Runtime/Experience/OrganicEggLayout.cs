using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShoalingUpstream.Experience
{
    public static class OrganicEggLayout
    {
        // Circular, irregular, non-overlapping clutch. Surface gaps: zero to one radius.
        public static List<Vector2> Create(int count, float diameter, int seed)
        {
            var result = new List<Vector2>();
            if (count <= 0) return result;
            var random = new System.Random(seed);
            diameter = Mathf.Max(.001f, diameter);
            float radius = Mathf.Sqrt(count) * diameter * .68f;
            result.Add(Vector2.zero);
            for (int i = 1; i < count; i++)
            {
                Vector2 candidate = Vector2.zero;
                bool found = false;
                for (int attempt = 0; attempt < 10000; attempt++)
                {
                    float angle = (float)random.NextDouble() * Mathf.PI * 2f;
                    float distance = Mathf.Sqrt((float)random.NextDouble()) * radius;
                    candidate = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
                    float nearest = float.MaxValue;
                    foreach (var other in result) nearest = Mathf.Min(nearest, Vector2.Distance(candidate, other));
                    if (nearest >= diameter && nearest <= diameter * 1.5f) { found = true; break; }
                }
                if (!found) throw new InvalidOperationException("Could not pack circular egg clutch");
                result.Add(candidate);
            }
            return result;
        }
    }
}
