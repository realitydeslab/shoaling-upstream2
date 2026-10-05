namespace ShoalingUpstream.Experience
{
    public enum JourneyChoice { None, FirstFound, SecondFound, Repeat, Receive, Offer, Ignore, Jump, Locate, Home, Spawn }
    public enum JourneyGate { Hidden, Shelter, Strider, Heron, Return, Home, Spawn }

    // Choices only exist while their gate is visible. Repeat preserves shelter confirmations.
    public sealed class GuidedChoiceGate
    {
        public JourneyGate Gate { get; private set; }
        public JourneyChoice Choice { get; private set; }
        public bool FirstFound { get; private set; }
        public bool SecondFound { get; private set; }
        public void Open(JourneyGate gate, bool resetShelter = false)
        {
            Gate = gate; Choice = JourneyChoice.None;
            if (resetShelter) FirstFound = SecondFound = false;
        }
        public void Hide() { Gate = JourneyGate.Hidden; }
        public bool Choose(JourneyChoice choice)
        {
            if (Gate == JourneyGate.Hidden || Choice != JourneyChoice.None) return false;
            bool allowed = choice == JourneyChoice.Repeat && (Gate == JourneyGate.Shelter || Gate == JourneyGate.Heron || Gate == JourneyGate.Return || Gate == JourneyGate.Home)
                || Gate == JourneyGate.Strider && choice == JourneyChoice.Receive
                || Gate == JourneyGate.Heron && (choice == JourneyChoice.Offer || choice == JourneyChoice.Ignore)
                || Gate == JourneyGate.Return && (choice == JourneyChoice.Jump || choice == JourneyChoice.Locate)
                || Gate == JourneyGate.Home && choice == JourneyChoice.Home
                || Gate == JourneyGate.Spawn && choice == JourneyChoice.Spawn;
            if (Gate == JourneyGate.Shelter && choice == JourneyChoice.FirstFound && !FirstFound) { FirstFound = true; allowed = true; }
            if (Gate == JourneyGate.Shelter && choice == JourneyChoice.SecondFound && !SecondFound) { SecondFound = true; allowed = true; }
            if (!allowed) return false;
            // One shelter selection keeps the gate open; both selections finish it.
            if (Gate == JourneyGate.Shelter && (choice == JourneyChoice.FirstFound || choice == JourneyChoice.SecondFound))
            {
                if (FirstFound && SecondFound) { Choice = JourneyChoice.SecondFound; Hide(); }
                return true;
            }
            Choice = choice; Hide(); return true;
        }
    }
}
