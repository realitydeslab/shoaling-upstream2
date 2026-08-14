using System;
using System.Collections.Generic;
using UnityEngine;
using ShoalingUpstream.Journey;

namespace ShoalingUpstream.Config
{
    /// <summary>
    /// Turns journey JSON into a <see cref="JourneyDocument"/>, or says why it will not.
    ///
    /// Every byte that reaches this class came off a network, a cache file or a build, and any
    /// of the three can hand us something broken. The rule throughout is to refuse loudly with
    /// a reason rather than accept a document that is partly there: a half-parsed journey does
    /// not fail at launch, it fails at the fourth beat, at the creek, twenty minutes in.
    /// </summary>
    public static class JourneyParser
    {
        /// <summary>Parse and structurally check a journey. False means do not use it.</summary>
        public static bool TryParse(string json, out JourneyDocument document, out string error)
        {
            document = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "empty";
                return false;
            }

            JourneyDocument parsed;
            try
            {
                parsed = JsonUtility.FromJson<JourneyDocument>(json);
            }
            catch (Exception e)
            {
                // This is the truncated-download case. JsonUtility throws on unbalanced JSON,
                // which is exactly what a half-written cache file looks like.
                error = $"malformed JSON ({e.Message})";
                return false;
            }

            if (parsed == null)
            {
                error = "malformed JSON (not an object)";
                return false;
            }

            // Refuse an unrecognised schema outright rather than reading what we can of it.
            // JsonUtility ignores fields it does not know and default-constructs the ones it
            // cannot find, so a document from a different schema arrives looking entirely
            // plausible with whatever moved silently replaced by zeroes.
            if (parsed.schemaVersion != JourneyDocument.SupportedSchemaVersion)
            {
                error = $"schemaVersion \"{parsed.schemaVersion}\" is not "
                      + $"\"{JourneyDocument.SupportedSchemaVersion}\"";
                return false;
            }

            if (string.IsNullOrWhiteSpace(parsed.journeyId))
            {
                error = "journeyId is missing";
                return false;
            }

            if (parsed.site == null || string.IsNullOrWhiteSpace(parsed.site.slug))
            {
                error = "site.slug is missing";
                return false;
            }

            if (parsed.site.centreline == null || parsed.site.centreline.Count < 2)
            {
                error = "site.centreline needs at least 2 points — every trigger is a distance "
                      + "along it, so without it nothing can fire";
                return false;
            }

            if (parsed.beats == null || parsed.beats.Count == 0)
            {
                error = "no beats";
                return false;
            }

            var seen = new HashSet<string>();
            foreach (var beat in parsed.beats)
            {
                if (beat == null || string.IsNullOrWhiteSpace(beat.id))
                {
                    error = "a beat has no id";
                    return false;
                }
                if (!seen.Add(beat.id))
                {
                    error = $"duplicate beat id \"{beat.id}\"";
                    return false;
                }
                if (beat.trigger == null)
                {
                    error = $"beat \"{beat.id}\" has no trigger";
                    return false;
                }

                // Hysteresis is not optional, and the service refuses to publish without it.
                // Seeing it here means the document did not come from the service, or came from
                // a schema that means something different by these fields.
                if (beat.trigger.exitRadiusM <= beat.trigger.enterRadiusM)
                {
                    error = $"beat \"{beat.id}\" has exitRadiusM {beat.trigger.exitRadiusM} "
                          + $"<= enterRadiusM {beat.trigger.enterRadiusM}";
                    return false;
                }
            }

            document = parsed;
            error = null;
            return true;
        }
    }
}
