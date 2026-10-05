using NUnit.Framework;
using ShoalingUpstream.Experience;

namespace ShoalingUpstream.Tests
{
    public sealed class OpeningChapterOneSequenceTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void BothSheltersAreRequiredInEitherOrderAndRepeatPreservesSelection(bool treeFirst)
        {
            var s = new OpeningChapterOneSequence();
            Assert.That(s.Continue(), Is.False);
            Assert.That(s.Spawn(), Is.False);
            Assert.That(s.Start(), Is.True);
            Assert.That(s.Start(), Is.False);
            Assert.That(s.OpeningEnded(), Is.True);
            Assert.That(s.Start(), Is.True); // Opening Repeat
            Assert.That(s.OpeningEnded(), Is.True);
            Assert.That(s.Continue(), Is.True);
            Assert.That(s.Found(treeFirst), Is.False); // audio is still playing
            Assert.That(s.SearchEnded(), Is.True);
            Assert.That(s.Found(treeFirst), Is.True);
            Assert.That(s.Found(treeFirst), Is.False);
            Assert.That(s.Spawn(), Is.False);
            Assert.That(s.RepeatSearch(), Is.True);
            Assert.That(s.Found(!treeFirst), Is.False);
            Assert.That(s.SearchEnded(), Is.True);
            Assert.That(treeFirst ? s.TreeFound : s.GravelFound, Is.True);
            Assert.That(s.Found(!treeFirst), Is.True);
            Assert.That(s.State, Is.EqualTo(OpeningChapterOneState.ChapterOneSpawnAudio));
            Assert.That(s.Spawn(), Is.False);
            Assert.That(s.SpawnPromptEnded(), Is.True);
            Assert.That(s.Spawn(), Is.True);
            Assert.That(s.Spawn(), Is.False);
            Assert.That(s.WaitEnded(), Is.False);
            Assert.That(s.EggsLanded(), Is.True);
            Assert.That(s.WaitEnded(), Is.True);
            Assert.That(s.EndingEnded(), Is.False);
            Assert.That(s.HatchEnded(), Is.True);
            Assert.That(s.EndingEnded(), Is.True);
            Assert.That(s.State, Is.EqualTo(OpeningChapterOneState.Complete));
        }
    }
}
