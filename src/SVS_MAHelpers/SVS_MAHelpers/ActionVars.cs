// ActionVars.cs
// Essential variables for helper plugin, which is need for proper work of action.
#nullable enable

namespace SVS_MAHelpers
{
    internal static class ActionVars
    {
        internal const string MoreActionsGuid = "DS27.SVS.MoreActions";

        // "Have 3P"
        // currently reserved for future, logic is not available yet
        internal const int Have3PActionNo = 502;
        internal const string Have3PAdvName = "a_502";

        // "Move Away"
        internal const int MoveAwayActionNo = 503;
        internal const string MoveAwayAdvName = "a_503";

        // " Ignore me"
        internal const int IgnoreActionNo = 504;
        internal const string IgnoreAdvName = "a_504";

        // "Stop harassing..."
        internal const int HarassActionNo = 505;
        internal const string HarassAdvName = "a_505";

        // "Give me your underwear"
        internal const int BlackmailUnderwearActionNo = 506;
        internal const string BlackmailUnderwearAdvName = "a_506";

        // "Take off clothes"
        internal const int NakedActionNo = 507;
        internal const string NakedAdvName = "a_507";

        // "I had fun with..."
        internal const int GossipActionNo = 508;
        internal const string GossipAdvName = "a_508";

        // "Ask for panties"
        internal const int LoveUnderwearActionNo = 509;
        internal const string LoveUnderwearAdvName = "a_509";

        // Fallback map pool for MoveAway when the game's own adjacency data
        // yields nothing usable.
        internal static readonly int[] CandidateMaps =
            { 0, 1, 2, 3, 4, 5, 6, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };
    }
}
