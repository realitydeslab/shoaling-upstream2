using System.Collections.Generic;

namespace ShoalingUpstream.Control
{
    public enum CommandDisposition { Queued, Fired, ExpiredOnArrival, ExpiredWaiting, StaleSession }

    /// <summary>A command with its schedule translated into the device's own clock.</summary>
    public sealed class ScheduledCommand
    {
        public ControlCommand Command;
        public double FireAtLocalMs;
        public double ExpiresAtLocalMs;

        /// <summary>True when the schedule was derived from the command's own lead rather than
        /// from its absolute timestamps, because the clock offset was unknown or untrusted.</summary>
        public bool Relative;
    }

    public sealed class CommandOutcome
    {
        public ControlCommand Command;
        public CommandDisposition Disposition;
        public string Note;
    }

    /// <summary>
    /// Holds commands until their moment, and throws away the ones whose moment has passed.
    ///
    /// The bus schedules rather than triggers, and the device has to keep its half of that
    /// bargain or the property is lost. Acting on arrival converts the server's fixed 400 ms
    /// lead back into variable network latency, which is the thing the lead exists to remove:
    /// steady latency is inaudible, jittery latency is not. Playing a late command is worse
    /// still — a sound belongs to a place on the creek, and by the time a delayed command
    /// arrives the visitor has walked out of that place.
    ///
    /// Everything here takes an explicit `nowLocalMs`, so the whole of it runs in EditMode
    /// against a clock the test moves by hand.
    /// </summary>
    public sealed class CommandScheduler
    {
        private readonly ServerClock _clock;
        private readonly List<ScheduledCommand> _queue = new();

        /// <summary>
        /// Actions whose newest instance replaces every queued older one.
        ///
        /// A simulated pose stream is a stream of positions, not a stream of events. After a
        /// wifi stall, ten seconds of queued poses would replay the walk at ten times speed and
        /// fire every beat along the way. What the operator wants is where the walker is NOW.
        /// Beats are the opposite — each one is a thing that happened — so this is a short list
        /// on purpose.
        /// </summary>
        private static readonly HashSet<string> SupersedingActions = new() { ControlActions.SimulatePose };

        public CommandScheduler(ServerClock clock) => _clock = clock;

        public int PendingCount => _queue.Count;

        /// <summary>How many queued commands a newer instance has replaced. A rising count on a
        /// steady connection means poses are arriving faster than the lead, which is fine; a
        /// jump means a stall was just absorbed rather than replayed.</summary>
        public int SupersededCount { get; private set; }

        /// <summary>
        /// Accept a command that has just arrived.
        ///
        /// <paramref name="sessionId"/> is the session of the connection it arrived on. A
        /// command from any other session is dropped without an ack: the server would discard
        /// the ack anyway, and acking would put a phantom success in the operator's log.
        /// </summary>
        public CommandOutcome Enqueue(ControlCommand command, double nowLocalMs, string sessionId)
        {
            if (command is null) return null;

            if (!string.IsNullOrEmpty(sessionId) && command.SessionId != sessionId)
            {
                return new CommandOutcome
                {
                    Command = command,
                    Disposition = CommandDisposition.StaleSession,
                    Note = "command from another service session",
                };
            }

            var scheduled = Translate(command, nowLocalMs);

            if (nowLocalMs >= scheduled.ExpiresAtLocalMs)
            {
                return new CommandOutcome
                {
                    Command = command,
                    Disposition = CommandDisposition.ExpiredOnArrival,
                    Note = "expired before it arrived",
                };
            }

            if (SupersedingActions.Contains(command.Action))
            {
                SupersededCount += _queue.RemoveAll(q => q.Command.Action == command.Action);
            }

            _queue.Add(scheduled);
            // Kept in fire order so Pump can stop at the first future entry rather than sweep.
            _queue.Sort((a, b) => a.FireAtLocalMs.CompareTo(b.FireAtLocalMs));

            return new CommandOutcome { Command = command, Disposition = CommandDisposition.Queued };
        }

        /// <summary>Everything due at <paramref name="nowLocalMs"/>, plus everything that went
        /// stale while waiting. The caller acks each outcome; nothing is acked twice.</summary>
        public void Pump(double nowLocalMs, List<CommandOutcome> into)
        {
            for (int i = 0; i < _queue.Count;)
            {
                var entry = _queue[i];
                if (nowLocalMs >= entry.ExpiresAtLocalMs)
                {
                    _queue.RemoveAt(i);
                    into.Add(new CommandOutcome
                    {
                        Command = entry.Command,
                        Disposition = CommandDisposition.ExpiredWaiting,
                        Note = "expired while queued",
                    });
                    continue;
                }
                if (nowLocalMs >= entry.FireAtLocalMs)
                {
                    _queue.RemoveAt(i);
                    into.Add(new CommandOutcome
                    {
                        Command = entry.Command,
                        Disposition = CommandDisposition.Fired,
                    });
                    continue;
                }
                i++;
            }
        }

        /// <summary>Abandon everything queued. The socket dropping means the operator no longer
        /// knows what the device is about to do, so firing on their behalf is guesswork.</summary>
        public void Clear() => _queue.Clear();

        /// <summary>
        /// Put a command's schedule into the device's own clock.
        ///
        /// With a trusted offset this is a subtraction. Without one — the first seconds after
        /// connecting, or a round trip too noisy to resolve — the absolute timestamps are
        /// unusable, but the DURATIONS in the command are not: a lead of 400 ms and a TTL of
        /// 10 s mean the same thing on any clock. So an untrusted estimate falls back to
        /// treating arrival as issue time. That loses the property that two devices fire
        /// together, and keeps the one that matters more: a command still fires once, near its
        /// intended moment, and still expires.
        /// </summary>
        private ScheduledCommand Translate(ControlCommand command, double nowLocalMs)
        {
            if (_clock.IsTrusted)
            {
                return new ScheduledCommand
                {
                    Command = command,
                    FireAtLocalMs = _clock.ToLocalMs(command.FireAtMs),
                    ExpiresAtLocalMs = _clock.ToLocalMs(command.ExpiresAtMs),
                    Relative = false,
                };
            }

            // The transit already spent is unknown, so the remaining TTL is over-estimated by
            // exactly that. Erring towards firing is right: the failure being avoided is a
            // silent creek, and the lead is short enough that the error cannot exceed it.
            double lead = command.LeadMs > 0 ? command.LeadMs : 0;
            double ttl = command.TtlMs > 0 ? command.TtlMs : lead;
            return new ScheduledCommand
            {
                Command = command,
                FireAtLocalMs = nowLocalMs + lead,
                ExpiresAtLocalMs = nowLocalMs + ttl,
                Relative = true,
            };
        }
    }
}
