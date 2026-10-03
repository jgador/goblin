using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Scaffolding.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Goblin.Database;

// EF exposes candidate naming through its design-time internal API. Its version
// is pinned with the existing scaffolding dependency in this tooling project.
#pragma warning disable EF1001
// EF discovers this in the existing scaffolding startup project. Keep CLR names
// explicit when regenerating from the unchanged database table/column names.
public sealed class GitRepositoryDesignTimeServices : IDesignTimeServices
{
    public void ConfigureDesignTimeServices(IServiceCollection services) =>
        services.AddSingleton<ICandidateNamingService, GitRepositoryCandidateNamingService>();
}

internal sealed class GitRepositoryCandidateNamingService : CandidateNamingService
{
    public override string GenerateCandidateIdentifier(string originalIdentifier) =>
        base.GenerateCandidateIdentifier(originalIdentifier switch
        {
            "repository" => "git_repository",
            "repository_id" => "git_repository_id",
            "repository_operations" => "git_repository_operations",
            "repository_setup_memories" => "git_repository_setup_memories",
            _ => originalIdentifier
        });
}
#pragma warning restore EF1001
