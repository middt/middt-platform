using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Middt.Framework.Ui.Model;
using Middt.Framework.Ui.Server.Persistence;

namespace Middt.Framework.Ui.Server.Runtime;

/// <summary>Creates the layout tables (optional) and seeds layouts from the configured directories; invalid seeds fail the start.</summary>
internal sealed partial class UiStartup(IServiceScopeFactory scopeFactory, IHostEnvironment environment, IOptions<MiddtUiOptions> options, ILogger<UiStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Parse and validate the seed files first: a broken file fails fast even when the database is unavailable.
        var seeds = LoadSeeds();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UiDbContext>();
        var provider = db.Database.ProviderName ?? string.Empty;
        var lockKey = $"middt_ui_startup_{options.Value.Schema ?? "ui"}";
        var lockAcquired = false;
        if (options.Value.AutoCreateSchema)
        {
            var creator = db.GetService<IRelationalDatabaseCreator>();
            if (!await creator.ExistsAsync(cancellationToken))
            {
                try
                {
                    await creator.CreateAsync(cancellationToken);
                }
                catch
                {
                    if (!await creator.ExistsAsync(cancellationToken))
                    {
                        throw;
                    }
                }
            }
        }

        if (options.Value.AutoCreateSchema || seeds.Count > 0)
        {
            lockAcquired = await AcquireStartupLockAsync(db, provider, lockKey, cancellationToken);
        }

        try
        {
            if (options.Value.AutoCreateSchema)
            {
                await EnsureTablesAsync(db, cancellationToken);
            }

            var store = scope.ServiceProvider.GetRequiredService<UiLayoutStore>();
            foreach (var (file, layout) in seeds)
            {
                try
                {
                    if (await store.SeedAsync(layout, cancellationToken))
                    {
                        logger.LogInformation("Seeded layout {Key} v{Version} from {File}", layout.Key, layout.Version, Path.GetFileName(file));
                    }
                }
                catch (UiLayoutConflictException ex)
                {
                    logger.LogInformation(ex, "Layout {Key} v{Version} was concurrently seeded by another instance.", layout.Key, layout.Version);
                }
            }
        }
        finally
        {
            if (lockAcquired)
            {
                await ReleaseStartupLockAsync(db, provider, lockKey);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private List<(string File, UiLayoutDocument Layout)> LoadSeeds()
    {
        var seeds = new List<(string, UiLayoutDocument)>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<UiLayoutStore>();

        foreach (var directory in options.Value.SeedDirectories)
        {
            var root = Path.IsPathRooted(directory) ? directory : Path.Combine(environment.ContentRootPath, directory);
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"Layout directory '{root}' does not exist.");
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                UiLayoutDocument layout;
                try
                {
                    layout = UiJson.Parse(File.ReadAllText(file));
                }
                catch (System.Text.Json.JsonException ex)
                {
                    throw new InvalidOperationException($"Layout file '{file}' is not valid JSON: {ex.Message}", ex);
                }

                if (layout.Version == 0)
                {
                    layout.Version = 1;
                }

                var validation = store.Validate(layout);
                if (!validation.IsValid)
                {
                    throw new InvalidOperationException($"Layout file '{file}' is invalid:{Environment.NewLine}{string.Join(Environment.NewLine, validation.Errors)}");
                }

                if (!keys.Add(layout.Key))
                {
                    throw new InvalidOperationException($"Layout key '{layout.Key}' is defined more than once ({file}).");
                }

                seeds.Add((file, layout));
            }
        }

        var graphErrors = Validation.UiLayoutValidator.ValidateGraph(seeds.ToDictionary(s => s.Item2.Key, s => s.Item2, StringComparer.Ordinal));
        if (graphErrors.Count > 0)
        {
            throw new InvalidOperationException($"Layout seed graph is invalid:{Environment.NewLine}{string.Join(Environment.NewLine, graphErrors)}");
        }

        return seeds;
    }

    private async Task EnsureTablesAsync(UiDbContext db, CancellationToken cancellationToken)
    {
        var creator = db.GetService<IRelationalDatabaseCreator>();
        var provider = db.Database.ProviderName ?? string.Empty;
        bool exists;
        if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            exists = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = 'UiLayouts'").SingleAsync(cancellationToken) > 0;
        }
        else if (provider.Contains("MySql", StringComparison.OrdinalIgnoreCase))
        {
            exists = await db.Database.SqlQuery<int>($"SELECT CAST(COUNT(*) AS SIGNED) AS `Value` FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'UiLayouts'").SingleAsync(cancellationToken) > 0;
        }
        else if (provider.Contains("Oracle", StringComparison.OrdinalIgnoreCase))
        {
            exists = await db.Database.SqlQuery<int>($"SELECT CAST(COUNT(*) AS NUMBER(10)) AS \"Value\" FROM USER_TABLES WHERE UPPER(TABLE_NAME) = 'UILAYOUTS'").SingleAsync(cancellationToken) > 0;
        }
        else
        {
            var defaultSchema = provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) ? "dbo" : "public";
            var schema = string.IsNullOrWhiteSpace(options.Value.Schema) ? defaultSchema : options.Value.Schema;
            exists = await db.Database.SqlQuery<int>($"SELECT CAST(COUNT(*) AS int) AS \"Value\" FROM information_schema.tables WHERE table_schema = {schema} AND table_name = 'UiLayouts'").SingleAsync(cancellationToken) > 0;
        }

        if (exists)
        {
            return;
        }

        if (UiDbContext.SupportsDatabaseSchemas(provider) && options.Value.Schema is { Length: > 0 } name)
        {
            // Identifiers cannot be parameterised: only plain identifiers are accepted.
            if (!IdentifierRegex().IsMatch(name))
            {
                throw new InvalidOperationException($"Invalid UI schema name '{name}'.");
            }

#pragma warning disable EF1002 // name is a validated identifier
            if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA IF NOT EXISTS \"{name}\"", cancellationToken);
            }
            else if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                await db.Database.ExecuteSqlRawAsync($"IF SCHEMA_ID(N'{name}') IS NULL EXEC(N'CREATE SCHEMA [{name}]');", cancellationToken);
            }
#pragma warning restore EF1002
        }

        await creator.CreateTablesAsync(cancellationToken);
    }

    private static async Task<bool> AcquireStartupLockAsync(DbContext db, string provider, string lockKey, CancellationToken cancellationToken)
    {
        if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock(hashtext({lockKey}))", cancellationToken);
            return true;
        }

        if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"EXEC sp_getapplock @Resource = {lockKey}, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 30000", cancellationToken);
            return true;
        }

        if (provider.Contains("MySql", StringComparison.OrdinalIgnoreCase))
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT GET_LOCK({lockKey}, 30)", cancellationToken);
            return true;
        }

        return false;
    }

    private static async Task ReleaseStartupLockAsync(DbContext db, string provider, string lockKey)
    {
        try
        {
            if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock(hashtext({lockKey}))", CancellationToken.None);
            }
            else if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"EXEC sp_releaseapplock @Resource = {lockKey}, @LockOwner = 'Session'", CancellationToken.None);
            }
            else if (provider.Contains("MySql", StringComparison.OrdinalIgnoreCase))
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT RELEASE_LOCK({lockKey})", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
}
