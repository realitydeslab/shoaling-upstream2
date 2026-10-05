namespace ShoalingUpstream.Experience
{
    public enum OpeningChapterOneState
    {
        Start, OpeningAudio, OpeningChoice, ChapterOneSearchAudio, FindShelter,
        SearchRepeatAudio, ChapterOneSpawnAudio, SpawnChoice, EggsDropping,
        EggWait, Hatching, ChapterOneEndingAudio, Complete
    }

    // Semantic events are independent of the audio player, touch input and AR tracking.
    public sealed class OpeningChapterOneSequence
    {
        public OpeningChapterOneState State { get; private set; } = OpeningChapterOneState.Start;
        public bool TreeFound { get; private set; }
        public bool GravelFound { get; private set; }
        public bool Start()
        {
            if (State != OpeningChapterOneState.Start && State != OpeningChapterOneState.OpeningChoice) return false;
            State = OpeningChapterOneState.OpeningAudio; return true;
        }
        public bool OpeningEnded()
        {
            if (State != OpeningChapterOneState.OpeningAudio) return false;
            State = OpeningChapterOneState.OpeningChoice; return true;
        }
        public bool Continue()
        {
            if (State != OpeningChapterOneState.OpeningChoice) return false;
            State = OpeningChapterOneState.ChapterOneSearchAudio; return true;
        }
        public bool SearchEnded()
        {
            if (State != OpeningChapterOneState.ChapterOneSearchAudio && State != OpeningChapterOneState.SearchRepeatAudio) return false;
            State = OpeningChapterOneState.FindShelter; return true;
        }
        public bool RepeatSearch()
        {
            if (State != OpeningChapterOneState.FindShelter) return false;
            State = OpeningChapterOneState.SearchRepeatAudio; return true;
        }
        public bool Found(bool tree)
        {
            if (State != OpeningChapterOneState.FindShelter || (tree ? TreeFound : GravelFound)) return false;
            if (tree) TreeFound = true; else GravelFound = true;
            if (TreeFound && GravelFound) State = OpeningChapterOneState.ChapterOneSpawnAudio;
            return true;
        }
        public bool SpawnPromptEnded()
        {
            if (State != OpeningChapterOneState.ChapterOneSpawnAudio) return false;
            State = OpeningChapterOneState.SpawnChoice; return true;
        }
        public bool Spawn()
        {
            if (State != OpeningChapterOneState.SpawnChoice) return false;
            State = OpeningChapterOneState.EggsDropping; return true;
        }
        public bool EggsLanded()
        {
            if (State != OpeningChapterOneState.EggsDropping) return false;
            State = OpeningChapterOneState.EggWait; return true;
        }
        public bool WaitEnded()
        {
            if (State != OpeningChapterOneState.EggWait) return false;
            State = OpeningChapterOneState.Hatching; return true;
        }
        public bool HatchEnded()
        {
            if (State != OpeningChapterOneState.Hatching) return false;
            State = OpeningChapterOneState.ChapterOneEndingAudio; return true;
        }
        public bool EndingEnded()
        {
            if (State != OpeningChapterOneState.ChapterOneEndingAudio) return false;
            State = OpeningChapterOneState.Complete; return true;
        }
    }
}
