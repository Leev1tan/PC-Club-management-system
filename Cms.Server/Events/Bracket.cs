namespace Cms.Server.Events;

/// <summary>A match of a bracket in memory (see EventMatchEntity).</summary>
public sealed record BracketMatch(int Round, int Slot, long? TeamA, long? TeamB, long? Winner = null, int? ScoreA = null, int? ScoreB = null)
{
    /// <summary>A first-round match with one team: the team goes on without playing.</summary>
    public bool IsBye => Round == 1 && (TeamA == null) != (TeamB == null);
}

/// <summary>
/// Single-elimination bracket (spec area 9). Size = the next power of two of the number of teams; the teams take
/// the standard seed positions (1 plays the last seed, 2 the second last, ...; 1 and 2 meet in the final at the
/// earliest), so the byes go to the best seeds and never two byes meet. A bye is decided at once. Staff enter
/// the winner of a match; the winner moves to the next round (round + 1, slot / 2; even slot = side A). A
/// result can be changed or cleared only while the next match has no winner.
/// </summary>
public static class Bracket
{
    public const int MaxScore = 999;

    /// <summary>Size of the bracket for this many teams (0 below two teams).</summary>
    public static int Size(int teams) => teams < 2 ? 0 : 1 << (int)Math.Ceiling(Math.Log2(teams));

    public static int Rounds(int teams) => teams < 2 ? 0 : (int)Math.Round(Math.Log2(Size(teams)));

    /// <summary>Seed numbers (1-based) in bracket order: 2 -> [1,2]; 4 -> [1,4,2,3]; 8 -> [1,8,4,5,2,7,3,6].</summary>
    public static IReadOnlyList<int> SeedOrder(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(size));
        var order = new List<int> { 1, 2 };
        while (order.Count < size)
        {
            var n = order.Count * 2;
            order = order.SelectMany(s => new[] { s, n + 1 - s }).ToList();
        }
        return order;
    }

    /// <summary>All matches of a new bracket for the teams in seed order; byes are decided and moved on.</summary>
    public static List<BracketMatch> Create(IReadOnlyList<long> teamsInSeedOrder)
    {
        var count = teamsInSeedOrder.Count;
        if (count < 2) throw new ArgumentException("A bracket needs at least two teams.", nameof(teamsInSeedOrder));
        var size = Size(count);
        var rounds = Rounds(count);
        var order = SeedOrder(size);
        long? Team(int seed) => seed <= count ? teamsInSeedOrder[seed - 1] : null;
        var matches = new List<BracketMatch>();
        for (var slot = 0; slot < size / 2; slot++)
        {
            var a = Team(order[2 * slot]);
            var b = Team(order[2 * slot + 1]);
            matches.Add(new BracketMatch(1, slot, a, b, a != null && b == null ? a : b != null && a == null ? b : null));
        }
        for (var round = 2; round <= rounds; round++)
            for (var slot = 0; slot < size >> round; slot++)
                matches.Add(new BracketMatch(round, slot, null, null));
        // Byes: their team waits in the next round
        foreach (var bye in matches.Where(m => m.Round == 1 && m.Winner != null).ToList())
            Place(matches, bye.Round + 1, bye.Slot / 2, bye.Slot % 2 == 0, bye.Winner);
        return matches;
    }

    private static void Place(List<BracketMatch> matches, int round, int slot, bool sideA, long? team)
    {
        var i = matches.FindIndex(m => m.Round == round && m.Slot == slot);
        if (i < 0) return; // after the final
        matches[i] = sideA ? matches[i] with { TeamA = team } : matches[i] with { TeamB = team };
    }

    /// <summary>
    /// Sets (winner = a team of the match) or clears (winner = null) the result of a match. Returns an error code or null;
    /// on success the list holds the new match and the winner in the next round.
    /// </summary>
    public static string? SetResult(List<BracketMatch> matches, int round, int slot, long? winner, int? scoreA, int? scoreB)
    {
        var i = matches.FindIndex(m => m.Round == round && m.Slot == slot);
        if (i < 0) return "match_not_found";
        var match = matches[i];
        if (match.IsBye) return "match_is_bye";
        if (match.TeamA == null || match.TeamB == null) return "match_not_ready";
        if (winner != null && winner != match.TeamA && winner != match.TeamB) return "bad_winner";
        if (scoreA is < 0 or > MaxScore || scoreB is < 0 or > MaxScore) return "bad_score";
        var next = matches.FirstOrDefault(m => m.Round == round + 1 && m.Slot == slot / 2);
        // Only the score of a decided next round may change; another winner would change the next match
        if (next?.Winner != null && match.Winner != winner) return "next_match_decided";
        matches[i] = match with { Winner = winner, ScoreA = winner == null ? null : scoreA, ScoreB = winner == null ? null : scoreB };
        Place(matches, round + 1, slot / 2, slot % 2 == 0, winner);
        return null;
    }

    /// <summary>The winner of the final, or null.</summary>
    public static long? Champion(IReadOnlyList<BracketMatch> matches)
    {
        if (matches.Count == 0) return null;
        var last = matches.Max(m => m.Round);
        return matches.Where(m => m.Round == last).Select(m => m.Winner).FirstOrDefault();
    }

    /// <summary>True when a match that was played (not a bye) has a result.</summary>
    public static bool HasResults(IEnumerable<BracketMatch> matches) => matches.Any(m => m.Winner != null && !m.IsBye);
}
