using System.Collections.Generic;
using UnityEngine;

namespace ShoalingUpstream.Control
{
    /// <summary>
    /// The wire vocabulary of the control bus, mirroring service/src/control-bus.mjs and the
    /// cases in server.mjs's socket switch.
    ///
    /// Both sides of this protocol are pinned by test/control-bus.e2e.test.mjs, which is the
    /// precise specification — these names are copied from it rather than from prose.
    /// </summary>
    public static class ControlActions
    {
        public const string FireBeat = "fireBeat";
        public const string ReplayCurrent = "replayCurrent";
        public const string Advance = "advance";
        public const string Silence = "silence";
        public const string Resume = "resume";

        /// <summary>
        /// The editor's simulated walker, broadcast as an ordinary operator command.
        ///
        /// It is an action rather than a message type of its own because the bus already
        /// schedules, expires and session-scopes commands, and a pose that arrives late is
        /// exactly as wrong as a beat that arrives late. Riding the same envelope means the
        /// simulated walk and any beat fired against it share one time base.
        /// </summary>
        public const string SimulatePose = "simulatePose";
    }

    public enum ControlMessageKind { Unknown, Welcome, State, Presence, Command, Pong, CommandAck, CommandIssued, Error }

    /// <summary>A command as it arrived, with its schedule still in SERVER time.</summary>
    public sealed class ControlCommand
    {
        public string Id;
        public string Action;
        public string BeatId;
        public string SessionId;
        public double IssuedAtMs;
        public double FireAtMs;
        public double ExpiresAtMs;
        public JsonValue Value = JsonValue.Null;

        /// <summary>How long after issue the command wants to fire. Clock-independent, and so
        /// the only usable schedule while the server clock offset is still unknown.</summary>
        public double LeadMs => FireAtMs - IssuedAtMs;

        public double TtlMs => ExpiresAtMs - IssuedAtMs;
    }

    /// <summary>
    /// A pose broadcast by the editor's walk simulation.
    ///
    /// Either half may be absent: the editor knows <c>s</c> for certain and may or may not send
    /// a 3D point. Whichever is missing is reconstructed against the centreline, so downstream
    /// always receives a complete sample and cannot tell which half arrived over the wire.
    /// </summary>
    public readonly struct SimulatedPose
    {
        public readonly bool HasS;
        public readonly float S;
        public readonly bool HasPosition;
        public readonly Vector3 Position;
        public readonly float HeadingRad;

        public SimulatedPose(bool hasS, float s, bool hasPosition, Vector3 position, float headingRad)
        {
            HasS = hasS; S = s; HasPosition = hasPosition; Position = position; HeadingRad = headingRad;
        }
    }

    public sealed class ControlMessage
    {
        public ControlMessageKind Kind = ControlMessageKind.Unknown;
        public string RawType;

        // welcome
        public int ClientId;
        public string Role;
        public string SessionId;

        // pong / state
        public double ServerNowMs;

        // command
        public ControlCommand Command;

        // error
        public string Message;
    }

    public static class ControlProtocol
    {
        public static ControlMessage Parse(string json)
        {
            if (!JsonValue.TryParse(json, out var root) || root.Kind != JsonKind.Object) return null;

            string type = root["type"].AsString();
            var msg = new ControlMessage { RawType = type };

            switch (type)
            {
                case "welcome":
                    msg.Kind = ControlMessageKind.Welcome;
                    msg.ClientId = (int)root["clientId"].AsDouble();
                    msg.Role = root["role"].AsString();
                    msg.SessionId = root["sessionId"].AsString();
                    break;

                case "pong":
                    msg.Kind = ControlMessageKind.Pong;
                    msg.ServerNowMs = root["serverNowMs"].AsDouble();
                    break;

                case "state":
                    msg.Kind = ControlMessageKind.State;
                    msg.ServerNowMs = root["serverNowMs"].AsDouble();
                    msg.SessionId = root["state"]["sessionId"].AsString();
                    break;

                case "presence":
                    msg.Kind = ControlMessageKind.Presence;
                    break;

                case "commandAck":
                    msg.Kind = ControlMessageKind.CommandAck;
                    break;

                case "commandIssued":
                    msg.Kind = ControlMessageKind.CommandIssued;
                    break;

                case "error":
                    msg.Kind = ControlMessageKind.Error;
                    msg.Message = root["message"].AsString();
                    break;

                case "command":
                    msg.Kind = ControlMessageKind.Command;
                    msg.Command = new ControlCommand
                    {
                        Id = root["id"].AsString(),
                        Action = root["action"].AsString(),
                        BeatId = root["beatId"].AsString(),
                        SessionId = root["sessionId"].AsString(),
                        IssuedAtMs = root["issuedAtMs"].AsDouble(),
                        FireAtMs = root["fireAtMs"].AsDouble(),
                        ExpiresAtMs = root["expiresAtMs"].AsDouble(),
                        Value = root["value"],
                    };
                    msg.SessionId = msg.Command.SessionId;
                    break;

                default:
                    // A message type this build does not know is not an error. The bus is
                    // expected to grow, and an older phone in the field has to keep walking.
                    msg.Kind = ControlMessageKind.Unknown;
                    break;
            }
            return msg;
        }

        /// <summary>Read a simulatePose payload. Returns false for anything malformed, which
        /// then simply leaves the previous pose standing rather than teleporting the visitor.</summary>
        public static bool TryReadPose(ControlCommand command, out SimulatedPose pose)
        {
            pose = default;
            if (command is null || command.Action != ControlActions.SimulatePose) return false;

            var value = command.Value;
            if (value is null || value.Kind != JsonKind.Object) return false;

            bool hasS = value["s"].Kind == JsonKind.Number;
            bool hasPosition = value["x"].Kind == JsonKind.Number
                               && value["y"].Kind == JsonKind.Number
                               && value["z"].Kind == JsonKind.Number;
            if (!hasS && !hasPosition) return false;

            pose = new SimulatedPose(
                hasS, value["s"].AsFloat(),
                hasPosition, new Vector3(value["x"].AsFloat(), value["y"].AsFloat(), value["z"].AsFloat()),
                value["headingRad"].AsFloat());
            return true;
        }

        public static string Hello(string device, string os, string build) =>
            new JsonWriter()
                .Field("type", "hello")
                .Field("device", device)
                .Field("os", os)
                .Field("build", build)
                .Done();

        public static string Heartbeat() =>
            new JsonWriter().Field("type", "heartbeat").Done();

        /// <summary>
        /// The upward report. Flat, not nested: the server merges an allow-list of top-level
        /// keys and drops everything else, so a nested payload would arrive as silence in the
        /// operator's window with no error anywhere.
        /// </summary>
        public static string Status(ControlStatus status)
        {
            var w = new JsonWriter().Field("type", "status");
            if (status.Site != null) w.Field("site", status.Site);
            if (status.Revision.HasValue) w.Field("revision", status.Revision.Value);
            w.Field("localization", status.Localization);
            w.Field("trackingConfidence", status.TrackingConfidence);
            if (status.S.HasValue) w.Field("s", status.S.Value); else w.NullField("s");
            if (status.LateralM.HasValue) w.Field("lateralM", status.LateralM.Value); else w.NullField("lateralM");
            if (status.CurrentBeat != null) w.Field("currentBeat", status.CurrentBeat); else w.NullField("currentBeat");
            w.Field("highWaterMark", status.HighWaterMark);
            w.Field("completed", status.Completed ?? System.Array.Empty<string>());
            if (status.ShoalCount.HasValue) w.Field("shoalCount", status.ShoalCount.Value);
            return w.Done();
        }

        /// <summary>
        /// The sessionId is not ceremony. The server drops an ack carrying a session other than
        /// its own, because after a restart the command ids refer to nothing — so the id we
        /// echo must be the session the command itself was issued under, never a remembered one.
        /// </summary>
        public static string Ack(string commandId, bool applied, string note, string sessionId) =>
            new JsonWriter()
                .Field("type", "ack")
                .Field("commandId", commandId)
                .Field("applied", applied)
                .Field("note", note)
                .Field("sessionId", sessionId)
                .Done();
    }

    /// <summary>
    /// Exactly the fields the bus's allow-list accepts, and no others.
    /// </summary>
    public struct ControlStatus
    {
        public string Site;
        public double? Revision;
        public string Localization;      // unavailable | coarse | precise
        public double TrackingConfidence;
        public double? S;
        public double? LateralM;
        public string CurrentBeat;
        public double HighWaterMark;
        public IReadOnlyList<string> Completed;
        public double? ShoalCount;

        /// <summary>Cheap change test, so a stationary visitor does not fill the operator's
        /// socket with identical reports over park wifi.</summary>
        public bool MateriallyDiffers(ControlStatus other, double sEpsilon = 0.05)
        {
            if (Localization != other.Localization) return true;
            if (CurrentBeat != other.CurrentBeat) return true;
            if (HighWaterMark != other.HighWaterMark) return true;
            if (ShoalCount != other.ShoalCount) return true;
            if (S.HasValue != other.S.HasValue) return true;
            if (S.HasValue && System.Math.Abs(S.Value - other.S.Value) > sEpsilon) return true;
            if ((Completed?.Count ?? 0) != (other.Completed?.Count ?? 0)) return true;
            return System.Math.Abs(TrackingConfidence - other.TrackingConfidence) > 0.05;
        }
    }
}
