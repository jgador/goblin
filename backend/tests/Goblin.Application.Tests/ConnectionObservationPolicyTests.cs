using Goblin.Application.Connections;
using Goblin.Contracts;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class ConnectionObservationPolicyTests
{
    [Theory]
    [InlineData(ConnectionAvailability.Changing)]
    [InlineData(ConnectionAvailability.Verifying)]
    public void ReservationsRejectBackgroundObservations(ConnectionAvailability availability)
    {
        Assert.Equal(default, Decide(availability, "current", ConnectionObservationKind.Availability, null));
        Assert.Equal(default, Decide(availability, "current", ConnectionObservationKind.Account, "observed"));
    }

    [Theory]
    [InlineData(ConnectionAvailability.Disconnected)]
    [InlineData(ConnectionAvailability.Available)]
    [InlineData(ConnectionAvailability.Unavailable)]
    public void AccountObservationInvalidatesCredentialsOnlyWhenIdentityChanges(ConnectionAvailability availability)
    {
        Assert.Equal(new(true, false), Decide(availability, "same", ConnectionObservationKind.Account, "same"));
        Assert.Equal(new(true, true), Decide(availability, "current", ConnectionObservationKind.Account, "observed"));
    }

    [Theory]
    [InlineData(ConnectionAvailability.Changing)]
    [InlineData(ConnectionAvailability.Verifying)]
    [InlineData(ConnectionAvailability.Available)]
    public void ExplicitChangeCompletionAlwaysInvalidatesCredentials(ConnectionAvailability availability)
    {
        Assert.Equal(new(true, true), Decide(availability, "same", ConnectionObservationKind.ChangeCompletion, "same"));
    }

    [Fact]
    public void AvailabilityObservationDoesNotInvalidateCredentials()
    {
        Assert.Equal(new(true, false), Decide(ConnectionAvailability.Available, "current",
            ConnectionObservationKind.Availability, "observed"));
    }

    private static ConnectionObservationDecision Decide(ConnectionAvailability availability, string? currentSignature,
        ConnectionObservationKind kind, string? observedSignature) =>
        ConnectionObservationPolicy.Decide(availability, currentSignature, kind, observedSignature);
}
