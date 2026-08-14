using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShoalingUpstream.Journey
{
    public enum LocalizationQuality { Unavailable, Coarse, Precise }

    public enum BeatState { Idle, Approaching, Committed, AwaitingAction, Complete }

    /// <summary>
    /// The trigger state machine.
    ///
    /// Deliberately one state variable rather than six independent zone monitors. Six monitors
    /// interleave and double-fire near boundaries when zones overlap — and at 6-10 m spacing
    /// with hysteresis they overlap almost everywhere. A single winner-take-all machine cannot.
    ///
    /// No Unity dependencies beyond Vector3/Mathf, so the whole thing is testable in EditMode
    /// by feeding it a synthetic walk. That matters: hysteresis, minimum hold, backtracking and
    /// standing still are exactly the behaviours a field test is worst at exercising repeatably.
    /// </summary>
    public class JourneyProgression
    {
        public readonly struct Event
        {
            public enum Type { Armed, Committed, ActionSatisfied, Completed, Released, Blocked }
            public readonly Type Kind;
            public readonly Beat Beat;
            public readonly string Note;

            public Event(Type kind, Beat beat, string note = null)
            {
                Kind = kind; Beat = beat; Note = note;
            }
            public override string ToString() => $"{Kind} {Beat?.id}{(Note is null ? "" : $" ({Note})")}";
        }

        private readonly JourneyDocument _journey;
        private readonly Dictionary<string, BeatState> _states = new();
        private readonly List<string> _completed = new();
        private readonly List<Event> _pending = new();

        private Beat _current;
        private string _dwellBeatId;
        private float _dwellAccumulated;
        private float _heldFor;
        private int _highWaterMark = -1;

        public float S { get; private set; }
        public float Lateral { get; private set; }
        public LocalizationQuality Quality { get; private set; } = LocalizationQuality.Unavailable;
        public Beat Current => _current;
        public int HighWaterMark => _highWaterMark;
        public IReadOnlyList<string> Completed => _completed;
        public int ShoalCount { get; private set; }

        /// <summary>True while a committed beat is waiting on a gesture the visitor has not made.</summary>
        public bool AwaitingAction =>
            _current != null && _states.TryGetValue(_current.id, out var st) && st == BeatState.AwaitingAction;

        public JourneyProgression(JourneyDocument journey)
        {
            _journey = journey ?? throw new ArgumentNullException(nameof(journey));
            ShoalCount = journey.shoal?.startingCount ?? 40;
            foreach (var beat in journey.beats) _states[beat.id] = BeatState.Idle;
        }

        public BeatState StateOf(string beatId) =>
            _states.TryGetValue(beatId, out var st) ? st : BeatState.Idle;

        /// <summary>Drain events raised since the last call. Audio and telemetry read these.</summary>
        public IReadOnlyList<Event> DrainEvents()
        {
            var copy = new List<Event>(_pending);
            _pending.Clear();
            return copy;
        }

        /// <summary>
        /// Advance the machine.
        /// </summary>
        /// <param name="anchorLocalPosition">Camera position in the VPS anchor's local frame.</param>
        public void Tick(Vector3 anchorLocalPosition, LocalizationQuality quality, float deltaTime)
        {
            Quality = quality;

            // Gate on localization quality, not distance. A distance test computed against an
            // untrusted pose is meaningless, and Niantic's own sample does the same: it points
            // an arrow while Coarse and treats nothing as placed until Precise.
            if (quality != LocalizationQuality.Precise)
            {
                if (_current != null)
                {
                    // Do not tear the beat down — a brief loss mid-beat should not sound like a
                    // bug. Hold it and stop accumulating.
                    _heldFor += deltaTime;
                }
                return;
            }

            var projection = Centreline.Project(anchorLocalPosition, _journey.site.centreline);
            S = projection.S;
            Lateral = projection.Lateral;

            if (_current != null) { _heldFor += deltaTime; }

            // --- can the current beat release? --------------------------------
            if (_current != null)
            {
                var trigger = _current.trigger;
                float distance = Mathf.Abs(S - _current.s);
                bool outsideExit = distance > trigger.exitRadiusM;
                bool heldLongEnough = _heldFor >= trigger.minimumHoldSeconds;
                var state = _states[_current.id];

                if (state == BeatState.Complete)
                {
                    // A finished beat holds for its full minimum regardless of where the
                    // visitor has walked to. Position must not cut it short: the hold is what
                    // guarantees the beat's sound is actually heard before the next one arms,
                    // and a brisk walker covers the gap between beats in a few seconds.
                    if (!heldLongEnough) return;

                    _pending.Add(new Event(Event.Type.Released, _current));
                }
                else
                {
                    // Left before performing the gesture. Release only once they are genuinely
                    // outside the exit band and the hold has expired, so a step backwards does
                    // not abandon a beat they are still standing in.
                    if (!outsideExit || !heldLongEnough) return;

                    _states[_current.id] = BeatState.Idle;
                    _pending.Add(new Event(Event.Type.Released, _current, "left before completing"));
                }

                _current = null;
                _dwellAccumulated = 0f;
                _dwellBeatId = null;
                _heldFor = 0f;
            }

            // --- choose a winner ----------------------------------------------
            Beat winner = null;
            float bestDistance = float.MaxValue;
            int winnerIndex = -1;

            for (int i = 0; i < _journey.beats.Count; i++)
            {
                var beat = _journey.beats[i];
                if (_states[beat.id] == BeatState.Complete) continue;

                float distance = Mathf.Abs(S - beat.s);

                // Hysteresis, and the reason it exists. The enter radius *arms* a beat; the
                // wider exit radius *sustains* it. Testing against the enter radius on every
                // frame would let a visitor loitering on the boundary lose their accumulated
                // dwell each time pose noise nudged them a few centimetres out — so the beat
                // would arm and disarm forever and never actually fire.
                bool armed = _states[beat.id] == BeatState.Approaching;
                float threshold = armed ? beat.trigger.exitRadiusM : beat.trigger.enterRadiusM;
                if (distance > threshold) continue;

                // Order is soft-gated: a beat that needs its predecessor is skipped rather than
                // blocking the machine, so a visitor who wanders ahead is not stuck in silence.
                if (beat.trigger.requiresPreviousComplete && i > _highWaterMark + 1)
                {
                    _pending.Add(new Event(Event.Type.Blocked, beat, "earlier beat not finished"));
                    continue;
                }

                if (distance < bestDistance) { bestDistance = distance; winner = beat; winnerIndex = i; }
            }

            if (winner == null)
            {
                _dwellAccumulated = 0f;
                _dwellBeatId = null;
                return;
            }

            // Dwell belongs to one beat. Drifting into a different one starts its clock fresh
            // rather than inheriting credit earned somewhere else.
            if (_dwellBeatId != winner.id)
            {
                _dwellBeatId = winner.id;
                _dwellAccumulated = 0f;
            }

            // --- dwell then commit --------------------------------------------
            if (_states[winner.id] == BeatState.Idle)
            {
                _states[winner.id] = BeatState.Approaching;
                _pending.Add(new Event(Event.Type.Armed, winner));
            }

            _dwellAccumulated += deltaTime;
            if (_dwellAccumulated < winner.trigger.dwellSeconds) return;

            _current = winner;
            _heldFor = 0f;
            _dwellAccumulated = 0f;
            _dwellBeatId = null;
            _states[winner.id] = winner.Kind == InteractionKind.Proximity
                ? BeatState.Committed
                : BeatState.AwaitingAction;
            _pending.Add(new Event(Event.Type.Committed, winner));

            if (winner.Kind == InteractionKind.Proximity)
            {
                CompleteBeat(winner, winnerIndex);
            }
        }

        /// <summary>
        /// Report that the visitor performed the gesture a committed beat was waiting for.
        /// Returns false if nothing was waiting, which is how a stray gesture stays silent.
        /// </summary>
        public bool SatisfyAction(InteractionKind kind)
        {
            if (_current == null) return false;
            if (_states[_current.id] != BeatState.AwaitingAction) return false;
            if (_current.Kind != kind) return false;

            _pending.Add(new Event(Event.Type.ActionSatisfied, _current));
            CompleteBeat(_current, _journey.beats.IndexOf(_current));
            return true;
        }

        /// <summary>Operator override from the controller. Completes whatever beat is named,
        /// whether or not the visitor is anywhere near it.</summary>
        public bool ForceBeat(string beatId)
        {
            int index = _journey.beats.FindIndex(b => b.id == beatId);
            if (index < 0) return false;
            var beat = _journey.beats[index];

            _current = beat;
            _heldFor = 0f;
            _states[beat.id] = BeatState.Committed;
            _pending.Add(new Event(Event.Type.Committed, beat, "forced by operator"));
            CompleteBeat(beat, index);
            return true;
        }

        private void CompleteBeat(Beat beat, int index)
        {
            _states[beat.id] = BeatState.Complete;
            if (!_completed.Contains(beat.id)) _completed.Add(beat.id);
            if (index > _highWaterMark) _highWaterMark = index;

            // The only interaction that changes persistent state. Giving fish to the heron is
            // only meaningful if the shoal is audibly thinner afterwards, so this number is
            // read continuously by the audio rather than displayed anywhere.
            if (beat.Kind == InteractionKind.Give && beat.givesFish > 0)
            {
                int floor = _journey.shoal?.minimumCount ?? 0;
                ShoalCount = Mathf.Max(floor, ShoalCount - beat.givesFish);
            }

            _pending.Add(new Event(Event.Type.Completed, beat));
        }
    }
}
