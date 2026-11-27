using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using OpenIddict.EntityFrameworkCore.Models;

namespace Goblin.Persistence;

// UseOpenIddict<long>() in PersistenceServices adds the package's entities and
// relationships to the EF model before OnModelCreating runs. This context uses
// the table and column names in 0001_initial.sql. The migration also declares
// the foreign-key constraints PostgreSQL enforces.
public sealed class ChatGptOAuthDbContext : DbContext
{
    public ChatGptOAuthDbContext(DbContextOptions<ChatGptOAuthDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // OpenIddict's default table names are plural. Map each entity explicitly
        // to its singular SQL name, such as public.openiddict_application for
        // the entity that defaults to OpenIddictApplications.
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreApplication<long>>().ToTable("openiddict_application", "public");
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreAuthorization<long>>().ToTable("openiddict_authorization", "public");
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreScope<long>>().ToTable("openiddict_scope", "public");
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreToken<long>>().ToTable("openiddict_token", "public");

        // GetProperties() reads EF model metadata. It includes CLR properties such
        // as ClientId, which maps to openiddict_application.client_id, and shadow
        // properties, which exist only in the model.
        //
        // The token's Application navigation uses a shadow property named ApplicationId.
        // After mapping it to application_id, EF joins openiddict_token.application_id
        // to openiddict_application.id. The Authorization navigation likewise uses
        // AuthorizationId, which maps to authorization_id.
        //
        // SnakeCaseLower converts the name string. SetColumnName changes the SQL
        // column identifier while EF keeps the existing property and relationships.
        foreach (IMutableEntityType entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (IMutableProperty property in entity.GetProperties())
                property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
        }
    }
}
