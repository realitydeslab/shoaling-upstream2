using System;
using System.Collections.Generic;

namespace ShoalingUpstream.Experience
{
    // Device inputs submit meanings here; touch, hand tracking and an operator can share this contract.
    public enum ConfirmationState { Ready, FindShade, GreetShade, FindPlants, GreetPlants, Playing, Finished }

    public sealed class ExperienceStep
    {
        public readonly string BeatId, Title;
        public readonly int NarrationIndex;
        public readonly bool RequiresTwoGreetings, AutoPlay;
        public ExperienceStep(string id, string title, int narration, bool greetings = false, bool autoPlay = false)
        {
            BeatId = id; Title = title; NarrationIndex = narration;
            RequiresTwoGreetings = greetings; AutoPlay = autoPlay;
        }
    }

    public interface IExperienceInput
    {
        bool ConfirmFound(string targetId);
        bool ConfirmGreeting(string targetId);
    }

    public sealed class StandaloneJourneySequence : IExperienceInput
    {
        public static readonly IReadOnlyList<ExperienceStep> DefaultSteps = Array.AsReadOnly(new[]
        {
            new ExperienceStep("beat-1", "New life · Eggs", 1),
            new ExperienceStep("beat-2", "New life · Hatch", 1),
            new ExperienceStep("beat-3", "Growing · Meet tree shade and water plants", 2, greetings: true),
            new ExperienceStep("beat-4", "Growing · Become fry", 2, autoPlay: true),
            new ExperienceStep("beat-5", "Journey · Water strider", 3),
            new ExperienceStep("beat-7", "Journey · Eat and grow", 3),
            new ExperienceStep("beat-6", "Journey · Heron", 3),
            new ExperienceStep("beat-8", "Journey · Feed", 3),
            new ExperienceStep("beat-11", "Journey · Heron leaves", 3),
            new ExperienceStep("beat-17", "Journey · Fry swim", 3),
            new ExperienceStep("beat-9", "Journey · Rainbow trout", 3),
            new ExperienceStep("beat-10", "Returning home · Turn", 4),
            new ExperienceStep("beat-16", "Returning home · Swim", 4),
            new ExperienceStep("beat-12", "Returning home · Leap", 4),
            new ExperienceStep("beat-13", "Rebirth · Locate", 5),
            new ExperienceStep("beat-14", "Rebirth · Spawn", 5),
            new ExperienceStep("beat-15", "Rebirth · Fade", 5),
        });

        public const string Shade = "tree-shade", Plants = "water-plants";
        public int Index { get; private set; }
        public ConfirmationState State { get; private set; }
        public ExperienceStep Current => Index < DefaultSteps.Count ? DefaultSteps[Index] : null;
        public string ExpectedTarget => State == ConfirmationState.FindShade || State == ConfirmationState.GreetShade
            ? Shade : State == ConfirmationState.FindPlants || State == ConfirmationState.GreetPlants ? Plants : null;

        public StandaloneJourneySequence() => Reset();
        public void Reset() { Index = 0; EnterStep(); }

        private void EnterStep() => State = Current == null ? ConfirmationState.Finished
            : Current.RequiresTwoGreetings ? ConfirmationState.FindShade : ConfirmationState.Ready;

        public bool ConfirmFound(string targetId)
        {
            if (targetId == Shade && State == ConfirmationState.FindShade) { State = ConfirmationState.GreetShade; return true; }
            if (targetId == Plants && State == ConfirmationState.FindPlants) { State = ConfirmationState.GreetPlants; return true; }
            return false;
        }

        public bool ConfirmGreeting(string targetId)
        {
            if (targetId == Shade && State == ConfirmationState.GreetShade) { State = ConfirmationState.FindPlants; return true; }
            if (targetId == Plants && State == ConfirmationState.GreetPlants) { State = ConfirmationState.Ready; return true; }
            return false;
        }

        public bool TryBegin()
        {
            if (State != ConfirmationState.Ready) return false;
            State = ConfirmationState.Playing;
            return true;
        }

        public bool CompletePlayback()
        {
            if (State != ConfirmationState.Playing) return false;
            Index++;
            EnterStep();
            return true;
        }

        public bool TryReplay()
        {
            if (Current == null || State == ConfirmationState.Playing) return false;
            // Replay is handled by the scene adapter; confirmations remain unchanged.
            return true;
        }
    }
}

