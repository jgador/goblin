using Goblin.Contracts;

namespace Goblin.Application.Connections;

internal enum ConnectionObservationKind
{
    Availability,
    Account,
    ChangeCompletion
}

internal readonly record struct ConnectionObservationDecision(bool Apply, bool AccountChanged);

internal static class ConnectionObservationPolicy
{
    internal static ConnectionObservationDecision Decide(ConnectionAvailability currentAvailability, string? currentAccountSignature,
        ConnectionObservationKind kind, string? observedAccountSignature)
    {
        bool reserved = currentAvailability is ConnectionAvailability.Changing or ConnectionAvailability.Verifying;
        if (reserved && kind != ConnectionObservationKind.ChangeCompletion) return default;

        bool accountChanged = kind == ConnectionObservationKind.ChangeCompletion ||
            kind == ConnectionObservationKind.Account && currentAccountSignature != observedAccountSignature;
        return new(true, accountChanged);
    }
}
