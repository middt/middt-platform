using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;

namespace Middt.Framework.Ui.Server.Persistence;

/// <summary>Current version of a layout.</summary>
public sealed class UiLayoutEntity
{
    public string Key { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string? Title { get; set; }

    /// <summary>Layout JSON.</summary>
    public string Content { get; set; } = string.Empty;

    public int Version { get; set; }

    /// <summary><c>seed</c> (file) or <c>api</c> (runtime edit).</summary>
    public string Source { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }
}

/// <summary>Replaced version of a layout (audit, rollback).</summary>
public sealed class UiLayoutRevisionEntity
{
    public long Id { get; set; }

    public string Key { get; set; } = string.Empty;

    public int Version { get; set; }

    public string Content { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public string? CreatedBy { get; set; }
}

/// <summary>Layout tables. The provider is chosen by the application.</summary>
public sealed class UiDbContext(DbContextOptions<UiDbContext> options, IOptions<MiddtUiOptions> uiOptions) : DbContext(options)
{
    public DbSet<UiLayoutEntity> Layouts => Set<UiLayoutEntity>();

    public DbSet<UiLayoutRevisionEntity> Revisions => Set<UiLayoutRevisionEntity>();

    internal string? ModelSchema => SupportsDatabaseSchemas(Database.ProviderName) ? uiOptions.Value.Schema : null;

    /// <summary>Returns <see langword="true"/> when the EF Core provider supports lightweight namespace schemas inside a database (PostgreSQL and SQL Server).</summary>
    internal static bool SupportsDatabaseSchemas(string? providerName)
    {
        if (string.IsNullOrEmpty(providerName))
        {
            return false;
        }

        return providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
            || providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var provider = Database.ProviderName ?? string.Empty;
        var schema = ModelSchema;
        var isPostgres = provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        var isOracle = provider.Contains("Oracle", StringComparison.OrdinalIgnoreCase);

        modelBuilder.Entity<UiLayoutEntity>(e =>
        {
            e.ToTable("UiLayouts", schema);
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            e.Property(x => x.Title).HasMaxLength(256);
            e.Property(x => x.Content).IsRequired();
            if (isPostgres)
            {
                e.Property(x => x.Content).HasColumnType("jsonb");
            }
            else if (isOracle)
            {
                e.Property(x => x.Content).HasColumnType("NCLOB");
            }

            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.Source).HasMaxLength(16);
            e.Property(x => x.UpdatedBy).HasMaxLength(256);
        });

        modelBuilder.Entity<UiLayoutRevisionEntity>(e =>
        {
            e.ToTable("UiLayoutRevisions", schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).HasMaxLength(100).IsRequired();
            e.Property(x => x.Content).IsRequired();
            if (isPostgres)
            {
                e.Property(x => x.Content).HasColumnType("jsonb");
            }
            else if (isOracle)
            {
                e.Property(x => x.Content).HasColumnType("NCLOB");
            }

            e.Property(x => x.CreatedBy).HasMaxLength(256);
            e.HasIndex(x => new { x.Key, x.Version });
        });
    }
}

/// <summary>Includes <see cref="UiDbContext.ModelSchema"/> in the EF Core model cache key.</summary>
internal sealed class UiModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is UiDbContext ui
            ? (context.GetType(), ui.ModelSchema, designTime)
            : (context.GetType(), designTime);
}
