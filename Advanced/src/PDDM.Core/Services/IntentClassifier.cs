using System.Text.RegularExpressions;
using PDDM.Core.Abstractions;
using PDDM.Core.Constants;
using PDDM.Shared;

namespace PDDM.Core.Services;

/// <summary>Heuristic intent classifier (Jira key + phrase lists). Used as the hybrid fast path.</summary>
public sealed partial class IntentClassifier : IIntentClassifier
{
    /// <inheritdoc />
    public QueryIntent Classify(string userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput))
            return QueryIntent.GeneralQuestion;

        var keyMatch = IssueKeyRegex().Match(userInput);
        if (keyMatch.Success)
        {
            var keyPosition = keyMatch.Index;
            if (keyPosition < userInput.Length / 2 || userInput.Length < 30)
                return QueryIntent.AssignedIssue;
        }

        var lower = userInput.ToLowerInvariant();
        if (IntentPhrases.Decision.Any(p => lower.Contains(p, StringComparison.Ordinal)))
            return QueryIntent.DecisionRationale;

        if (IntentPhrases.Requirement.Any(p => lower.Contains(p, StringComparison.Ordinal)))
            return QueryIntent.NewRequirement;

        return QueryIntent.GeneralQuestion;
    }

    /// <inheritdoc />
    public Task<QueryIntent> ClassifyAsync(string userInput, CancellationToken cancellationToken = default)
        => Task.FromResult(Classify(userInput));

    /// <inheritdoc />
    public string? ExtractIssueKey(string userInput)
    {
        var match = IssueKeyRegex().Match(userInput);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex(@"[A-Z]+-\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IssueKeyRegex();
}
