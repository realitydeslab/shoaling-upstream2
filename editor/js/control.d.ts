/**
 * The operator's controller.
 *
 * Carried alongside the visitor while the experience runs automatically. It exists because
 * every project that has built this kind of work has ended up needing it: Notre-Dame's
 * "Whispers" added manual waypoint unlocking so the narrative stays reachable when GPS drift
 * defeats the automatic triggers, and Hidden Florence shipped a Manual Mode for the same
 * reason. It is not a convenience.
 *
 * Two rules it follows:
 *   - Local feedback is immediate regardless of when the phone acts. The person pressing and
 *     the person hearing are different people, so nothing is fusing across the network anyway;
 *     what matters is that the operator is never left wondering whether the press registered.
 *   - Commands are scheduled, never fired. The server stamps a fire time a few hundred
 *     milliseconds ahead, which turns variable latency into fixed latency and makes a late
 *     packet get dropped rather than fire at the wrong moment.
 */
export {};
