#nullable enable
namespace SVS_ADVScenarioSwapper
{
    /// <summary>
    /// Cross-plugin answer marker. The judge owner (currently SVS_MAHelpers)
    /// records whether the answer it just produced was fake or forced for a
    /// specific NPC; the swapper consumes that marker when the same NPC's
    /// answer scenario loads. Nothing here is trait-based: a character can
    /// fake an answer only when the active judge actually did it.
    /// </summary>
    public static class FakeAnswer
    {
        private static int _npcIdx = -1;
        private static bool _fakeYes;
        private static bool _fakeNo;

        /// <summary>Set the marker after a judge decision; call once per answer.</summary>
        public static void Set(int npcIdx, bool fakeYes, bool fakeNo)
        {
            _npcIdx = npcIdx;
            _fakeYes = fakeYes;
            _fakeNo = fakeNo;
        }

        /// <summary>Clear any pending marker (called at the start of a new judge).</summary>
        public static void Reset() => Set(-1, false, false);

        /// <summary>
        /// Take the marker for this NPC, if one was set. A marker for another
        /// NPC stays pending. Returns true when a fake answer was recorded.
        /// </summary>
        internal static bool TryTake(int npcIdx, out bool fakeYes, out bool fakeNo)
        {
            fakeYes = _fakeYes;
            fakeNo = _fakeNo;
            if (npcIdx < 0 || npcIdx != _npcIdx) return false;
            Reset();
            return fakeYes || fakeNo;
        }
    }
}
