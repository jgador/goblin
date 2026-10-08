using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Goblin.Contracts.Conversations;
using Goblin.Core.Work;

namespace Goblin.Application.Work;

internal readonly record struct ExternalLinkAttempt(bool IsLinking, string? CodeHash);

internal readonly record struct ExternalMessageAcceptance(string? Body, ExternalMessageState State);

// Keeps external transport text outside durable Work until local identity policy
// authorizes it, and limits conversational replies to explicit input decisions.
internal static class ExternalConversationPolicy
{
    internal static (string Code, string CodeHash, DateTime ExpiresAt) NewLink(DateTime now)
    {
        string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return (code, Hash(code), now.AddMinutes(5));
    }

    internal static ExternalLinkAttempt LinkAttempt(ExternalMessage message)
    {
        bool linking = message.Direct && message.Text.StartsWith("link ", StringComparison.Ordinal);
        return new(linking, linking ? Hash(message.Text[5..].Trim()) : null);
    }

    internal static ExternalMessageAcceptance Accept(string text, bool linking, bool validProof, bool authorized) =>
        new(!linking && authorized ? text : null,
            linking && validProof ? ExternalMessageState.PendingLink : ExternalMessageState.Pending);

    internal static WorkCommand Continue(WorkSnapshot snapshot, long commandId, long workId,
        long version, string text)
    {
        WorkDecision? decision = snapshot.Decisions.LastOrDefault(value => value.AnsweredAt is null);
        // External conversations may answer an explicit question, but cannot
        // approve a repository, retry, complete, or cancel Work.
        return snapshot.Attention?.Reason == AttentionReason.InputRequired && decision is not null
            ? WorkCommands.Answer(commandId, workId, version, decision.Id, text)
            : WorkCommands.AddContext(commandId, workId, version, text);
    }

    internal static WorkDecision? PendingQuestion(WorkSnapshot snapshot, long? notifiedDecisionId)
    {
        WorkDecision? decision = snapshot.Decisions.LastOrDefault(value => value.AnsweredAt is null);
        return snapshot.Attention?.Reason == AttentionReason.InputRequired && decision?.Id != notifiedDecisionId ? decision : null;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
