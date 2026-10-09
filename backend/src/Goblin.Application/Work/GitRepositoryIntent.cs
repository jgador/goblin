using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Goblin.Core.Work;

namespace Goblin.Application.Work;

public static class GitRepositoryIntent
{
    private const string BasePattern = @"\b(?:base\s+branch\s*(?:is\s+|[:=]\s*)?|(?:branch|branching)\s+(?:from|off)\s+|based\s+on\s+)[`""']?([a-zA-Z0-9][a-zA-Z0-9._/-]*)|\buse\s+[`""']?([a-zA-Z0-9][a-zA-Z0-9._/-]*)[`""']?\s+as\s+(?:the\s+)?base\b";

    public static string WithoutBranchNames(string text) => Regex.Replace(text, BasePattern, " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IEnumerable<string> Inputs(WorkSnapshot work) => work.History
        .Where(x => x.Kind is WorkEventKind.Created or WorkEventKind.ContextAdded or WorkEventKind.InputProvided or WorkEventKind.ChangesRequested)
        .Select(x => x.Text ?? "");

    public static GitDeliveryIntent Delivery(WorkSnapshot work, string? answer = null)
    {
        GitRepositoryGrant? prior = work.Attempts.LastOrDefault()?.Target.GitRepository?.Grant;
        var intent = new GitDeliveryIntent(prior?.BaseBranch, prior?.AllowPush ?? false, prior?.AllowPullRequest ?? false);
        long authorizedAt = work.History.LastOrDefault(x => x.Kind == WorkEventKind.GitRepositoryAuthorized)?.Sequence ?? 0;
        IEnumerable<string> inputs = work.History.Where(x => x.Sequence > authorizedAt && x.Kind is WorkEventKind.Created or WorkEventKind.ContextAdded or WorkEventKind.InputProvided or WorkEventKind.ChangesRequested)
            .Select(x => x.Text ?? "");
        foreach (string input in inputs.Append(answer ?? "")) intent = Parse(input, intent);
        intent.Validate();
        return intent;
    }

    public static GitDeliveryIntent Parse(string text, GitDeliveryIntent? previous = null)
    {
        GitDeliveryIntent intent = previous ?? new();
        const RegexOptions flags = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        bool explanation = Regex.IsMatch(text, @"^\s*(?:explain|describe|show\s+me\s+how|how\s+(?:do|can|should|to)|(?:should|could|can|would|do|did)\s+(?:i|we)|what|why|when|where)\b", flags);
        const string instruction = @"(?:^\s*|[.;,]\s*|\b(?:and|then|also|please|but)\s+|\b(?:can|could|would)\s+you\s+|\bi\s+want\s+you\s+to\s+)";
        bool push = !explanation && Regex.IsMatch(text, instruction + @"(push|publish)(?:\s+(?:(?:the|these|your|my|our|all)\s+)?(?:changes?\b|commits?\b|branch\b|repository\b|code\b|it\b|them\b|to\b|[a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+\b)|\s*[.!]?\s*$)", flags);
        bool pr = !explanation && Regex.IsMatch(text, instruction + @"(open|create|submit|raise|make)\s+(?:a\s+|the\s+)?(?:draft\s+)?(?:pull\s+request|pr)\b", flags);
        bool noPush = Regex.IsMatch(text, @"\b(?:do\s+not|don't|don’t|never|without|no|not)\s+(?:\w+\s+){0,3}(?:push(?:ing)?|publish(?:ing)?)\b", flags);
        bool noPr = Regex.IsMatch(text, @"\b(?:do\s+not|don't|don’t|never|without|no|not)\s+(?:(?:push|publish)(?:\s+changes)?\s+or\s+)?(?:(?:open|create|submit|raise|make)\s+)?(?:a\s+|the\s+)?(?:draft\s+)?(?:pull\s+request|pr)\b", flags);
        if (noPush && pr && !noPr) throw new ApplicationFailure("repository_intent_conflict");
        if (push) intent = intent with { Push = !noPush };
        if (noPush) intent = intent with { Push = false, OpenPullRequest = false };
        if (pr || noPr) intent = intent with { OpenPullRequest = pr && !noPr };
        if (intent.OpenPullRequest) intent = intent with { Push = true };
        MatchCollection bases = Regex.Matches(text, BasePattern, flags);
        string[] branches = [.. bases.Select(x => (x.Groups[1].Success ? x.Groups[1].Value : x.Groups[2].Value).TrimEnd('.')).Distinct(StringComparer.Ordinal)];
        if (branches.Length > 1) throw new ApplicationFailure("repository_intent_conflict");
        if (branches.Length == 1) intent = intent with { BaseBranch = branches[0] };
        intent.Validate();
        return intent;
    }
}
