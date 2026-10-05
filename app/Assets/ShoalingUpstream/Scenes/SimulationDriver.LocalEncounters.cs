using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ShoalingUpstream.Simulation
{
    public sealed partial class SimulationDriver
    {
        public float LocalHeronContactTime { get; private set; } = -1f;
        public float LocalShoalGrowthStart { get; private set; } = -1f;
        public float LocalStriderReturnsTime { get; private set; } = -1f;

        private IEnumerator CrossfadeLifeStage(Transform previous, Transform next, bool growing)
        {
            float targetAlpha = next.name.StartsWith("ALEVIN FISH") ? .9f : 1f;
            var oldMaterials = new List<Material>();
            var newMaterials = new List<Material>();
            var originals = new List<Material>();
            foreach (var renderer in previous.GetComponentsInChildren<Renderer>())
                foreach (var material in renderer.materials)
                { PrepareMaterialForFade(material); oldMaterials.Add(material); }
            foreach (var renderer in next.GetComponentsInChildren<Renderer>())
                foreach (var material in renderer.materials)
                { originals.Add(new Material(material)); PrepareMaterialForFade(material); SetMaterialAlpha(material, 0f); newMaterials.Add(material); }
            Vector3 size = previous.localScale;
            float elapsed = 0f;
            while (previous != null && next != null && elapsed < 2f)
            {
                elapsed += Time.deltaTime;
                float blend = Mathf.Clamp01(elapsed / 2f);
                foreach (var material in oldMaterials) SetMaterialAlpha(material, 1f - blend);
                foreach (var material in newMaterials) SetMaterialAlpha(material, blend * targetAlpha);
                if (growing) previous.localScale = size * Mathf.Lerp(1f, 1.15f, blend);
                yield return null;
            }
            for (int i = 0; i < newMaterials.Count; i++)
            {
                if (newMaterials[i] != null)
                {
                    newMaterials[i].shader = originals[i].shader;
                    newMaterials[i].CopyPropertiesFromMaterial(originals[i]);
                    RestoreOpaqueMaterial(newMaterials[i]);
                    if (targetAlpha < 1f)
                    { PrepareMaterialForFade(newMaterials[i]); SetMaterialAlpha(newMaterials[i], targetAlpha); }
                }
                Destroy(originals[i]);
            }
            if (previous != null) Destroy(previous.gameObject);
        }

        public static void RestoreOpaqueMaterial(Material material)
        {
            SetMaterialAlpha(material, 1f);
            if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 0f);
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 0f);
            if (material.HasProperty("_SrcBlend")) material.SetInt("_SrcBlend", 1);
            if (material.HasProperty("_DstBlend")) material.SetInt("_DstBlend", 0);
            if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 1);
            material.DisableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.SetOverrideTag("RenderType", "Opaque");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
        }

        private IEnumerator GrowShoalMember(Transform fish, float multiplier)
        {
            if (LocalShoalGrowthStart < 0f) LocalShoalGrowthStart = Time.time;
            float delay = UnityEngine.Random.Range(0f, 1.5f);
            yield return WaitOrSkip(delay, _skipGeneration);
            if (fish != null) yield return GrowInPlace(fish, multiplier, 3f - delay);
        }

        public Transform LocalOfferedFish { get; private set; }
        private readonly Dictionary<Transform, Vector3> _mouthOffsets = new();

        // The silhouette's forwardmost point follows the live, scaled swim animation.
        public Vector3 LocalFishMouthPosition(Transform fish)
        {
            if (_mouthOffsets.TryGetValue(fish, out var offset)) return fish.TransformPoint(offset);
            Vector3 head = HeadDirection(fish);
            Vector3 result = fish.position;
            float foremost = float.NegativeInfinity;
            foreach (var skin in fish.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                var baked = new Mesh();
                skin.BakeMesh(baked);
                foreach (var vertex in baked.vertices)
                {
                    Vector3 world = skin.transform.TransformPoint(vertex);
                    float projection = Vector3.Dot(world - fish.position, head);
                    if (projection > foremost) { foremost = projection; result = world; }
                }
                Destroy(baked);
            }
            if (float.IsNegativeInfinity(foremost))
                foreach (var renderer in fish.GetComponentsInChildren<Renderer>())
                {
                    var bounds = renderer.bounds;
                    float extent = Vector3.Dot(new Vector3(Mathf.Abs(head.x), Mathf.Abs(head.y), Mathf.Abs(head.z)), bounds.extents);
                    float projection = Vector3.Dot(bounds.center - fish.position, head) + extent;
                    if (projection > foremost) { foremost = projection; result = bounds.center + head * extent; }
                }
            _mouthOffsets[fish] = fish.InverseTransformPoint(result);
            return result;
        }

        public bool FollowPoolFryLocally(string beatId)
        {
            if (!LocalOnly || !Ready || _eye == null || LocalSceneBusy || !_flocks.TryGetValue(beatId, out var fish) || fish.Count == 0) return false;
            if (!DevicePutDown) _travel.Observe(_eye.transform.forward);
            _formationHeading = _travel.Forward;
            _poolFish.Clear();
            _stableDevicePosition = _eye.transform.position;
            _followRequested = true;
            return true;
        }

        private Vector3 VisibleStriderPosition(int index, Vector3 fallback)
        {
            if (_eye == null) return fallback;
            // Ground placement is computed in the current view, beyond the entire shoal.
            float waterY = LocalGroundY + .05f;
            Vector3 horizontal = Vector3.ProjectOnPlane(_eye.transform.forward, Vector3.up);
            if (horizontal.sqrMagnitude < .001f) horizontal = _travel.Forward;
            float radius = Mathf.Max(.75f, (StriderForwardDepth() - (waterY - _eye.transform.position.y) * _eye.transform.forward.y)
                / Mathf.Max(.15f, Vector3.ProjectOnPlane(_eye.transform.forward, Vector3.up).magnitude));
            Vector3 middle = _eye.transform.position + horizontal.normalized * radius; middle.y = waterY;
            // When the iPad is held level, move the ground row farther out until it is in frame.
            for (int attempt = 0; attempt < 80 && _eye.WorldToViewportPoint(middle).y < .22f; attempt++)
            { radius += .25f; middle = _eye.transform.position + horizontal.normalized * radius; middle.y = waterY; }
            for (int attempt = 0; attempt < 80 && _eye.WorldToViewportPoint(middle).y > .78f; attempt++)
            { radius *= .92f; middle = _eye.transform.position + horizontal.normalized * radius; middle.y = waterY; }
            float row = _eye.WorldToViewportPoint(middle).y;
            float x = .24f + index * .26f + UnityEngine.Random.Range(-.025f, .025f);
            Ray ray = _eye.ViewportPointToRay(new Vector3(x, row, 0f));
            Vector3 point = ray.GetPoint(StriderForwardDepth());
            if (Mathf.Abs(ray.direction.y) > .001f)
            {
                float distance = (waterY - ray.origin.y) / ray.direction.y;
                if (distance > .1f) point = ray.GetPoint(distance);
            }
            Vector3 spread = Vector3.ProjectOnPlane(point - _eye.transform.position, Vector3.up);
            point += spread.normalized * ((index - 1) * .22f + UnityEngine.Random.Range(-.06f, .06f));
            point.y = waterY;
            return point;
        }

        private float StriderForwardDepth()
        {
            float depth = 2.2f;
            foreach (var fish in _swimOffsets.Keys)
                if (fish != null && !_localFacingExempt.Contains(fish) && !_localDetached.Contains(fish))
                    depth = Mathf.Max(depth, Vector3.Dot(LocalFishMouthPosition(fish) - _eye.transform.position, _eye.transform.forward) + .4f);
            return depth;
        }

        private static void EnlargeHeronVisuals(GameObject group)
        {
            // Keep every model's pivot/relative placement; enlarge its geometry only.
            foreach (Transform part in group.GetComponentsInChildren<Transform>(true))
            {
                if (part.name != "Mesh" && !part.name.StartsWith("Baby Bird")) continue;
                bool nested = false;
                for (var parent = part.parent; parent != null && parent != group.transform; parent = parent.parent)
                    if (parent.name == "Mesh" || parent.name.StartsWith("Baby Bird")) { nested = true; break; }
                if (!nested) part.localScale *= 1.2f;
            }
        }

        private bool HeronViewport(out Rect area, out float depth)
        {
            area = default; depth = float.MaxValue;
            if (_eye == null || !_flocks.TryGetValue("beat-6", out var group)) return false;
            Vector2 min = Vector2.one * float.MaxValue, max = Vector2.one * float.MinValue;
            bool found = false;
            foreach (var actor in group)
                if (actor != null)
                    foreach (var renderer in actor.GetComponentsInChildren<Renderer>())
                    {
                        var bounds = renderer.bounds;
                        for (int i = 0; i < 8; i++)
                        {
                            Vector3 corner = bounds.center + Vector3.Scale(bounds.extents,
                                new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                            Vector3 viewport = _eye.WorldToViewportPoint(corner);
                            if (viewport.z <= 0f) continue;
                            min = Vector2.Min(min, viewport); max = Vector2.Max(max, viewport);
                            depth = Mathf.Min(depth, viewport.z); found = true;
                        }
                    }
            if (!found) return false;
            area = Rect.MinMaxRect(min.x - .07f, min.y - .07f, max.x + .07f, max.y + .07f);
            return area.Overlaps(new Rect(0f, 0f, 1f, 1f));
        }

        private Vector3 ClearHeronSightline(Transform fish, Vector3 destination)
        {
            destination = ClearStriderSightline(fish, destination);
            if (_localFacingExempt.Contains(fish) || !HeronViewport(out var area, out float depth)) return destination;
            var current = _eye.WorldToViewportPoint(fish.position);
            var goal = _eye.WorldToViewportPoint(destination);
            if ((current.z <= 0f || current.z >= depth || !area.Contains(current))
                && (goal.z <= 0f || goal.z >= depth || !area.Contains(goal))) return destination;
            Vector3 clear = goal.z > 0f ? goal : current;
            if (clear.x < area.center.x) clear.x = area.xMin - .05f;
            else clear.x = area.xMax + .05f;
            if (clear.x < .06f || clear.x > .94f)
            {
                clear.y = area.yMin - .07f;
                clear.x = Mathf.Clamp(clear.x, .08f, .92f);
            }
            clear.z = Mathf.Clamp(current.z, .5f, 2f);
            Vector3 world = _eye.ViewportToWorldPoint(clear);
            world.y = Mathf.Max(LocalGroundY + .08f, world.y);
            return world;
        }

        private Vector3 ClearStriderSightline(Transform fish, Vector3 destination)
        {
            if (_eye == null || !_flocks.TryGetValue("beat-5", out var striders) || _localFacingExempt.Contains(fish)) return destination;
            Vector3 current = _eye.WorldToViewportPoint(fish.position);
            Vector3 goal = _eye.WorldToViewportPoint(destination);
            foreach (var strider in striders)
            {
                if (strider == null) continue;
                Vector3 offer = _eye.WorldToViewportPoint(strider.position);
                bool Blocks(Vector3 point) => point.z > 0f && point.z < offer.z + .1f
                    && Mathf.Abs(point.x - offer.x) < .12f && Mathf.Abs(point.y - offer.y) < .12f;
                bool bodyBlocks = false;
                float bodyHalfWidth = 0f;
                foreach (var renderer in fish.GetComponentsInChildren<Renderer>())
                {
                    Vector2 min = Vector2.one * float.MaxValue, max = Vector2.one * float.MinValue;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        Vector3 world = renderer.bounds.center + Vector3.Scale(renderer.bounds.extents,
                            new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f));
                        Vector3 view = _eye.WorldToViewportPoint(world);
                        if (view.z <= 0f) continue;
                        min = Vector2.Min(min, view); max = Vector2.Max(max, view);
                    }
                    bodyHalfWidth = Mathf.Max(bodyHalfWidth, (max.x - min.x) * .5f);
                    // Allow for the wider silhouette while a fish turns sideways to clear the view.
                    bodyHalfWidth = Mathf.Max(bodyHalfWidth, renderer.bounds.size.magnitude / Mathf.Max(.2f, current.z) * .7f);
                    var bounds = Rect.MinMaxRect(min.x - .06f, min.y - .06f, max.x + .06f, max.y + .06f);
                    if (current.z > 0f && current.z < offer.z && bounds.Contains(offer)) bodyBlocks = true;
                    bounds.position += (Vector2)(goal - current);
                    if (goal.z > 0f && goal.z < offer.z && bounds.Contains(offer)) bodyBlocks = true;
                }
                if (!bodyBlocks && !Blocks(current) && !Blocks(goal)) continue;
                goal = current;
                goal.x = current.x < .5f ? .1f - bodyHalfWidth : .9f + bodyHalfWidth;
                destination = _eye.ViewportToWorldPoint(goal);
                destination.y = Mathf.Max(LocalGroundY + .08f, destination.y);
            }
            return destination;
        }

        private IEnumerator RevealHeronLocally()
        {
            var moves = new List<Coroutine>();
            foreach (var fish in new List<Transform>(_swimOffsets.Keys))
            {
                if (fish == null || _localFacingExempt.Contains(fish) || _localDetached.Contains(fish) || _localMotionControlled.Contains(fish)) continue;
                Vector3 target = ClearHeronSightline(fish, fish.position);
                if (Vector3.Distance(target, fish.position) > .05f)
                    moves.Add(StartSceneAction(SwimLocalFish(fish, target, 3f)));
            }
            foreach (var move in moves) yield return move;
        }

        private static Transform FindEncounterBone(Transform root, string name)
        {
            foreach (var bone in root.GetComponentsInChildren<Transform>(true)) if (bone.name == name) return bone;
            return null;
        }

        private void HoldFishAtBeak(Transform fish, Transform beak, Vector3 offset)
        {
            if (fish == null || beak == null) return;
            Vector3 point = beak.TransformPoint(offset);
            _swimOffsets.TryGetValue(fish, out var current);
            _swimOffsets[fish] = current + point - fish.position;
            fish.position = point;
        }

        private static void SampleHeron(Animation animation, string clip, float time, Transform bird, Transform feet, Vector3 feetAnchor)
        {
            var state = animation[clip];
            state.time = time; state.speed = 0f; state.wrapMode = WrapMode.ClampForever;
            animation.Sample();
            if (feet != null)
            {
                Vector3 correction = feetAnchor - feet.position; correction.y = 0f;
                bird.position += correction;
            }
        }

        private IEnumerator FeedHeronLocally(SceneTrigger trigger, List<Transform> group)
        {
            Transform bird = null;
            Animation animation = null;
            foreach (var actor in group)
                if (actor != null && actor.GetComponentInChildren<Animation>() is Animation candidate)
                { bird = actor; animation = candidate; break; }
            if (bird == null || !_flocks.TryGetValue(trigger.FishSourceBeatId, out var flock) || flock.Count == 0) yield break;
            Transform beak = FindEncounterBone(bird, trigger.FishFollowBoneName);
            Transform feet = FindEncounterBone(bird, trigger.LockFeetTransformName);
            if (beak == null || animation.GetClip("Lower Head") == null) yield break;
            Vector3 feetAnchor = feet != null ? feet.position : bird.position;
            Transform fish = flock[0]; flock.RemoveAt(0);
            LocalOfferedFish = fish;
            _localFacingExempt.Add(fish); _poolFish.Remove(fish);
            animation.Stop(); animation.Play("Lower Head");
            float length = animation.GetClip("Lower Head").length;
            // Read the authored beak's minimum once. Hold that receiving pose while the fish
            // approaches, then resume the original raise/turn/feed curves from contact onward.
            float lowTime = 0f, lowY = float.MaxValue;
            for (int i = 0; i <= 100; i++)
            {
                float time = length * i / 100f;
                SampleHeron(animation, "Lower Head", time, bird, feet, feetAnchor);
                if (beak.position.y < lowY) { lowY = beak.position.y; lowTime = time; }
            }
            SampleHeron(animation, "Lower Head", 0f, bird, feet, feetAnchor);
            float lower = 0f;
            while (lower < lowTime)
            {
                lower += Time.deltaTime * .5f;
                SampleHeron(animation, "Lower Head", Mathf.Min(lower, lowTime), bird, feet, feetAnchor);
                yield return null;
            }
            yield return SwimLocalFish(fish, beak.TransformPoint(trigger.FishFollowLocalOffset), 3f, false, 1.5f);
            if (fish == null) yield break;
            LocalHeronContactTime = Time.time;
            HoldFishAtBeak(fish, beak, trigger.FishFollowLocalOffset);
            Quaternion caughtRotation = fish.rotation;
            Vector3 caughtAxis = Vector3.Cross(HeadDirection(fish), Vector3.up).normalized;
            float lift = 0f;
            while (lift < 1f)
            {
                lift += Time.deltaTime;
                fish.rotation = Quaternion.AngleAxis(90f * Mathf.Clamp01(lift), caughtAxis) * caughtRotation;
                HoldFishAtBeak(fish, beak, trigger.FishFollowLocalOffset);
                yield return null;
            }
            for (int stepIndex = 0; stepIndex < trigger.ClipSequence.Length; stepIndex++)
            {
                var step = trigger.ClipSequence[stepIndex];
                float pause = stepIndex > 0 ? step.DelaySeconds : 0f;
                while (pause > 0f) { pause -= Time.deltaTime; HoldFishAtBeak(fish, beak, trigger.FishFollowLocalOffset); yield return null; }
                // Turn already contains the slow authored half-turn. Apply this root
                // correction in the same frame as the next clip's baked-facing reset;
                // animating it separately would introduce an extra turn and a visible snap.
                if (step.RotateYDegrees != 0f)
                    bird.Rotate(0f, step.RotateYDegrees, 0f, Space.World);
                if (animation.GetClip(step.ClipName) == null) continue;
                animation.Stop(); animation.Play(step.ClipName);
                float elapsed = stepIndex == 0 ? lowTime : 0f;
                float clipLength = animation[step.ClipName].length;
                while (elapsed < clipLength)
                {
                    elapsed = Mathf.Min(clipLength, elapsed + Time.deltaTime * .5f);
                    SampleHeron(animation, step.ClipName, elapsed, bird, feet, feetAnchor);
                    HoldFishAtBeak(fish, beak, trigger.FishFollowLocalOffset);
                    if (stepIndex == trigger.ClipSequence.Length - 1 && elapsed >= lowTime && fish != null)
                    {
                        Destroy(fish.gameObject); fish = null;
                        foreach (var actor in group)
                            if (actor != null && actor != bird)
                                foreach (var baby in actor.GetComponentsInChildren<Animator>()) baby.Play("Clicked", 0, UnityEngine.Random.value);
                    }
                    yield return null;
                }
            }
            if (fish != null) Destroy(fish.gameObject);
        }
    }
}
