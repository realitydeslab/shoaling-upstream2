using UnityEngine;
using ShoalingUpstream.Journey;

namespace ShoalingUpstream.Localization
{
    /// <summary>
    /// The transform between the anchor's frame and the journey's own coordinates.
    ///
    /// <c>editorFrame</c> is documented as <b>splat space -&gt; anchor space</b>
    /// (docs/journey-schema-draft.md). The journey is authored against the splat, so beats and
    /// the centreline are in splat space; the device tracks an anchor, so a pose arrives in
    /// anchor space. Every position we read therefore has to be pulled the *other* way through
    /// this transform before it can be projected onto the centreline.
    ///
    /// Getting the direction wrong is the single most dangerous mistake available here — it
    /// produces a soundscape that is confidently, silently in the wrong place. Both directions
    /// are named explicitly for that reason, and the calibration gate exists because the
    /// transform is identity until somebody has matched three physical points on site.
    /// </summary>
    public readonly struct JourneyFrame
    {
        public readonly Quaternion Rotation;
        public readonly Vector3 Translation;
        public readonly float Scale;

        /// <summary>True when the document's editorFrame was structurally usable. A corrupt
        /// scale or a degenerate quaternion falls back to identity rather than to NaN, and says
        /// so, because NaN would propagate into every s the piece ever computes.</summary>
        public readonly bool WellFormed;

        private JourneyFrame(Quaternion rotation, Vector3 translation, float scale, bool wellFormed)
        {
            Rotation = rotation; Translation = translation; Scale = scale; WellFormed = wellFormed;
        }

        public static JourneyFrame Identity =>
            new(Quaternion.identity, Vector3.zero, 1f, true);

        public static JourneyFrame From(EditorFrame frame)
        {
            if (frame == null) return Identity;

            bool wellFormed = true;

            var q = Quaternion.identity;
            if (frame.rotation != null && frame.rotation.Length == 4)
            {
                q = new Quaternion(frame.rotation[0], frame.rotation[1], frame.rotation[2], frame.rotation[3]);
                // An unnormalised quaternion here is almost always a sign the array was written
                // in [w,x,y,z] order somewhere upstream. Normalising rescues the common case
                // where it is merely drifted; the length check catches the genuinely broken one.
                float length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                if (length < 1e-4f) { q = Quaternion.identity; wellFormed = false; }
                else q = new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length);
            }
            else if (frame.rotation != null && frame.rotation.Length != 0)
            {
                wellFormed = false;
            }

            float scale = frame.scale;
            if (!(scale > 1e-4f) || float.IsNaN(scale) || float.IsInfinity(scale))
            {
                scale = 1f;
                wellFormed = false;
            }

            return new JourneyFrame(q, frame.translation.ToVector3(), scale, wellFormed);
        }

        /// <summary>Anchor space -&gt; journey space. This is the direction the runtime uses.</summary>
        public Vector3 ToJourney(Vector3 anchorPoint) =>
            (Quaternion.Inverse(Rotation) * (anchorPoint - Translation)) / Scale;

        public Pose ToJourney(Pose anchorPose) => new(
            ToJourney(anchorPose.position),
            Quaternion.Inverse(Rotation) * anchorPose.rotation);

        /// <summary>Journey space -&gt; anchor space. Used to place authored content against a
        /// tracked anchor, which is the direction editorFrame is written in.</summary>
        public Vector3 ToAnchor(Vector3 journeyPoint) =>
            Rotation * (journeyPoint * Scale) + Translation;
    }

    /// <summary>
    /// A rigid transform from the AR session's own frame into journey coordinates, captured at
    /// the moment of a precise fix.
    ///
    /// This is what makes dropouts survivable. VPS and ARKit fail independently: the phone can
    /// lose visual localization against the site map while still tracking its own motion
    /// perfectly well. Caching the correspondence at the last good fix lets a dropout be carried
    /// by odometry at VIO accuracy rather than by a guess at walking speed.
    /// </summary>
    public readonly struct SessionToJourney
    {
        public readonly Quaternion Rotation;
        public readonly Vector3 Translation;
        public readonly bool Valid;

        private SessionToJourney(Quaternion rotation, Vector3 translation)
        {
            Rotation = rotation; Translation = translation; Valid = true;
        }

        public static SessionToJourney Capture(Pose sessionPose, Pose journeyPose)
        {
            var q = journeyPose.rotation * Quaternion.Inverse(sessionPose.rotation);
            return new SessionToJourney(q, journeyPose.position - q * sessionPose.position);
        }

        public Vector3 Apply(Vector3 sessionPoint) => Rotation * sessionPoint + Translation;
    }
}
