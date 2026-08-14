using System.Collections.Generic;

namespace ShoalingUpstream.Control
{
    /// <summary>
    /// "Play this beat" — the whole of what the control bus is allowed to do to the piece.
    ///
    /// Narrow on purpose, and narrow in a particular direction: the controller is a safety net
    /// for a trigger that did not fire, not a second authoring surface. Moving a beat or
    /// swapping a clip means editing the draft and publishing it, so none of that appears here.
    ///
    /// Every method returns whether it actually did anything, because that answer is what goes
    /// back to the operator in the ack. An operator pressing a button and seeing it confirmed
    /// when nothing happened is worse than seeing it refused.
    /// </summary>
    public interface IControlEffects
    {
        bool FireBeat(string beatId);
        bool ReplayCurrent();

        /// <summary>Give up on the current beat and move to the next.</summary>
        bool Advance();

        /// <summary>Stop everything sounding. The button an operator reaches for when a visitor
        /// is being spoken to, or when something has gone wrong.</summary>
        bool Silence();

        bool Resume();
    }

    /// <summary>
    /// What the device reports upward, from whoever owns the trigger machine.
    ///
    /// Separate from <see cref="IControlEffects"/> because the two have different owners: the
    /// audio engine performs, the journey runtime knows. Fields left null are filled in by the
    /// client from the live pose, so an implementation only has to answer for what it knows.
    /// </summary>
    public interface IControlStatusSource
    {
        ControlStatus Snapshot();
    }

    /// <summary>A status source that knows nothing, for a scene with no journey loaded yet.</summary>
    public sealed class EmptyStatusSource : IControlStatusSource
    {
        public static readonly EmptyStatusSource Instance = new();

        // Localization is left null rather than asserted "unavailable": the client fills it from
        // whichever pose source is live, and a scene with no journey still has a real answer.
        public ControlStatus Snapshot() => new()
        {
            HighWaterMark = -1,
            Completed = System.Array.Empty<string>(),
        };
    }

    /// <summary>Records what was asked of it and refuses nothing. Useful before the audio engine
    /// is wired in, and as the seam a test drives.</summary>
    public sealed class RecordingEffects : IControlEffects
    {
        public readonly List<string> Calls = new();

        public bool FireBeat(string beatId) { Calls.Add($"fireBeat:{beatId}"); return true; }
        public bool ReplayCurrent() { Calls.Add("replayCurrent"); return true; }
        public bool Advance() { Calls.Add("advance"); return true; }
        public bool Silence() { Calls.Add("silence"); return true; }
        public bool Resume() { Calls.Add("resume"); return true; }
    }
}
