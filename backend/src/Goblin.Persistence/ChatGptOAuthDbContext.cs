using Microsoft.EntityFrameworkCore;

namespace Goblin.Persistence;

// OpenIddict owns these mappings. Its native tables are applied by the SQL
// administrator and excluded from Goblin's database-first product scaffolding.
public sealed class ChatGptOAuthDbContext : DbContext
{
    public ChatGptOAuthDbContext(DbContextOptions<ChatGptOAuthDbContext> options) : base(options) { }
}
