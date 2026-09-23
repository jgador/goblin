using Goblin.Contracts.Runtime;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class RepositorySetupTests
{
    [Theory]
    [InlineData("https://user:password@example.test/repo")]
    [InlineData("api_key=private-value")]
    [InlineData("cat /run/credentials/auth.json")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    public void SetupRejectsCredentialMaterial(string value) => Assert.False(RepositorySetupRules.SafeText(value, 2000));

    [Theory]
    [InlineData("../../outside")]
    [InlineData("/etc/passwd")]
    [InlineData(".git/config")]
    [InlineData(".env")]
    [InlineData("C:\\private")]
    public void SetupInputsStayInsideRepository(string value) => Assert.False(RepositorySetupRules.SafePath(value));
}
