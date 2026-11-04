using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Middt.Framework.Workflow.Definitions;
using Middt.Framework.Workflow.Persistence;

namespace Middt.Framework.Workflow.Runtime;

/// <summary>Validates all definitions at startup (fail fast) and optionally creates the workflow tables.</summary>
internal sealed class WorkflowStartup(
    IServiceScopeFactory scopeFactory,
    WorkflowRegistry registry,
    IOptions<MiddtWorkflowOptions> options,
    ILogger<WorkflowStartup>? logger = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        registry.EnsureLoaded();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var provider = db.Database.ProviderName ?? string.Empty;
        var isPostgres = provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        var lockKey = $"middt_wf_startup_{options.Value.Schema ?? "public"}";
        var lockAcquired = false;
        if (options.Value.AutoCreateSchema)
        {
            lockAcquired = await AcquireStartupLockAsync(db, provider, lockKey, cancellationToken);
        }

        try
        {
            if (options.Value.AutoCreateSchema)
            {
                var creator = db.GetService<IRelationalDatabaseCreator>();

                if (!await creator.ExistsAsync(cancellationToken))
                {
                    await creator.CreateAsync(cancellationToken);
                }

                if (!await TablesExistAsync(db, cancellationToken))
                {
                    // Tables of this context only (the database may contain other application tables).
                    if (options.Value.Schema is { Length: > 0 } schema && WorkflowDbContext.SupportsDatabaseSchemas(provider))
                    {
                        await EnsureSchemaAsync(db, schema, cancellationToken);
                    }

                    if (isPostgres)
                    {
                        await WorkflowPartitionManager.CreatePartitionedTablesAsync(db, options.Value.Schema, cancellationToken);
                    }
                    else
                    {
                        await creator.CreateTablesAsync(cancellationToken);
                    }
                }
                else
                {
                    // CreateTables only runs for a new database: ensure any newly added partitioned tables and nullable columns exist.
                    if (isPostgres)
                    {
                        await WorkflowPartitionManager.CreatePartitionedTablesAsync(db, options.Value.Schema, cancellationToken);
                    }

                    await EnsureWorkflowKeyMigrationAsync(db, options.Value.Schema, cancellationToken);
                    await EnsureColumnsAsync(db, cancellationToken);
                }
            }

            await RestorePersistedDefinitionsAsync(db, registry, options.Value.Schema, cancellationToken);

            if (options.Value.AutoCreateSchema)
            {
                await WorkflowPartitionManager.EnsurePartitionsAndIndexesAsync(db, registry, options.Value.Schema, logger, cancellationToken);
                await EnsureIndexesAsync(db, cancellationToken);
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

    private static async Task RestorePersistedDefinitionsAsync(WorkflowDbContext db, WorkflowRegistry registry, string? schema, CancellationToken cancellationToken)
    {
        try
        {
            if (!await TableExistsAsync(db, schema, "WorkflowDefinitions", cancellationToken))
            {
                return;
            }

            var rows = await db.Definitions
                .AsNoTracking()
                .Where(d => !d.Key.StartsWith("__"))
                .OrderBy(d => d.Version)
                .ToListAsync(cancellationToken);

            registry.RestorePersistedDefinitions(rows.Select(r => r.Content));
        }
        catch
        {
            // Database may not be created yet when AutoCreateSchema = false on an uninitialized store.
        }
    }

    private static async Task EnsureWorkflowKeyMigrationAsync(WorkflowDbContext db, string? schemaName, CancellationToken cancellationToken)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        if (!provider.Contains("Npgsql", StringComparison.Ordinal))
        {
            return;
        }

        var schema = string.IsNullOrWhiteSpace(schemaName) ? "public" : schemaName;
        var statements = new[]
        {
            $"ALTER TABLE \"{schema}\".\"WorkflowInstanceData\" ADD COLUMN IF NOT EXISTS \"WorkflowKey\" varchar(128) NULL;",
            $"UPDATE \"{schema}\".\"WorkflowInstanceData\" d SET \"WorkflowKey\" = COALESCE(i.\"WorkflowKey\", 'Default') FROM \"{schema}\".\"WorkflowInstances\" i WHERE d.\"InstanceId\" = i.\"Id\" AND d.\"WorkflowKey\" IS NULL;",
            $"UPDATE \"{schema}\".\"WorkflowInstanceData\" SET \"WorkflowKey\" = 'Default' WHERE \"WorkflowKey\" IS NULL;",

            $"ALTER TABLE \"{schema}\".\"WorkflowJobs\" ADD COLUMN IF NOT EXISTS \"WorkflowKey\" varchar(128) NULL;",
            $"UPDATE \"{schema}\".\"WorkflowJobs\" j SET \"WorkflowKey\" = COALESCE(i.\"WorkflowKey\", 'Default') FROM \"{schema}\".\"WorkflowInstances\" i WHERE j.\"InstanceId\" = i.\"Id\" AND j.\"WorkflowKey\" IS NULL;",
            $"UPDATE \"{schema}\".\"WorkflowJobs\" SET \"WorkflowKey\" = 'Default' WHERE \"WorkflowKey\" IS NULL;",

            $"ALTER TABLE \"{schema}\".\"WorkflowHistory\" ADD COLUMN IF NOT EXISTS \"WorkflowKey\" varchar(128) NULL;",
            $"UPDATE \"{schema}\".\"WorkflowHistory\" h SET \"WorkflowKey\" = COALESCE(i.\"WorkflowKey\", 'Default') FROM \"{schema}\".\"WorkflowInstances\" i WHERE h.\"InstanceId\" = i.\"Id\" AND h.\"WorkflowKey\" IS NULL;",
            $"UPDATE \"{schema}\".\"WorkflowHistory\" SET \"WorkflowKey\" = 'Default' WHERE \"WorkflowKey\" IS NULL;",

            $"ALTER TABLE \"{schema}\".\"WorkflowIncidents\" ADD COLUMN IF NOT EXISTS \"WorkflowKey\" varchar(128) NULL;",
            $"UPDATE \"{schema}\".\"WorkflowIncidents\" inc SET \"WorkflowKey\" = COALESCE(i.\"WorkflowKey\", 'Default') FROM \"{schema}\".\"WorkflowInstances\" i WHERE inc.\"InstanceId\" = i.\"Id\" AND inc.\"WorkflowKey\" IS NULL;",
            $"UPDATE \"{schema}\".\"WorkflowIncidents\" SET \"WorkflowKey\" = 'Default' WHERE \"WorkflowKey\" IS NULL;",

            // history: delta rows keep a patch instead of a snapshot.
            $"ALTER TABLE \"{schema}\".\"WorkflowHistory\" ALTER COLUMN \"Data\" DROP NOT NULL;",

            // WorkflowDefinitions column alignment across versions.
            $"ALTER TABLE \"{schema}\".\"WorkflowDefinitions\" RENAME COLUMN \"Json\" TO \"Content\";",
            $"ALTER TABLE \"{schema}\".\"WorkflowDefinitions\" DROP COLUMN IF EXISTS \"Checksum\";",
            $"ALTER TABLE \"{schema}\".\"WorkflowDefinitions\" DROP COLUMN IF EXISTS \"Source\";",
        };

        foreach (var sql in statements)
        {
            try
            {
#pragma warning disable EF1002
                await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
#pragma warning restore EF1002
            }
            catch
            {
                // Ignored if table does not exist
            }
        }
    }

    /// <summary>Adds every nullable column of the model that does not exist yet (idempotent; PostgreSQL).</summary>
    internal static async Task EnsureColumnsAsync(WorkflowDbContext db, CancellationToken cancellationToken)
    {
        foreach (var statement in EnsureColumnStatements(db))
        {
#pragma warning disable EF1002 // identifiers and column types come from the EF model, not from user input
            await db.Database.ExecuteSqlRawAsync(statement, cancellationToken);
#pragma warning restore EF1002
        }
    }

    internal static IEnumerable<string> EnsureColumnStatements(WorkflowDbContext db)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        if (!provider.Contains("Npgsql", StringComparison.Ordinal))
        {
            yield break;
        }

        var sql = db.GetService<ISqlGenerationHelper>();
        foreach (var entityType in db.GetService<IDesignTimeModel>().Model.GetEntityTypes())
        {
            var table = entityType.GetTableName();
            if (table is null)
            {
                continue;
            }

            var schema = entityType.GetSchema();
            var tableIdentifier = StoreObjectIdentifier.Table(table, schema);
            var qualifiedTable = sql.DelimitIdentifier(table, schema);
            foreach (var property in entityType.GetProperties())
            {
                if (property.IsPrimaryKey() || !property.IsColumnNullable(tableIdentifier))
                {
                    continue;
                }

                var columnName = property.GetColumnName(tableIdentifier);
                var columnType = property.GetColumnType(tableIdentifier);
                if (columnName is null || columnType is null)
                {
                    continue;
                }

                yield return $"ALTER TABLE {qualifiedTable} ADD COLUMN IF NOT EXISTS {sql.DelimitIdentifier(columnName)} {columnType} NULL";
            }
        }
    }

    /// <summary>Creates every index of the model that does not exist yet (idempotent; PostgreSQL and SQLite).</summary>
    internal static async Task EnsureIndexesAsync(WorkflowDbContext db, CancellationToken cancellationToken)
    {
        foreach (var statement in EnsureIndexStatements(db))
        {
#pragma warning disable EF1002 // identifiers come from the EF model (delimited), not from user input
            await db.Database.ExecuteSqlRawAsync(statement, cancellationToken);
#pragma warning restore EF1002
        }

        if ((db.Database.ProviderName ?? string.Empty).Contains("Oracle", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX \"IX_WorkflowInstances_WorkflowKey_BusinessKey_UQ\" ON \"WorkflowInstances\" (CASE WHEN \"BusinessKey\" IS NOT NULL THEN \"WorkflowKey\" END, \"BusinessKey\")",
                    cancellationToken);
            }
            catch
            {
                // Ignored when the index already exists (ORA-00955 / ORA-01408).
            }
        }
    }

    internal static IEnumerable<string> EnsureIndexStatements(WorkflowDbContext db)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        var isPostgres = provider.Contains("Npgsql", StringComparison.Ordinal);
        if (!isPostgres && !provider.Contains("Sqlite", StringComparison.Ordinal))
        {
            yield break;
        }

        var sql = db.GetService<ISqlGenerationHelper>();
        // Index metadata such as filters and sort order is only kept in the design-time model.
        foreach (var entityType in db.GetService<IDesignTimeModel>().Model.GetEntityTypes())
        {
            var table = entityType.GetTableName();
            if (table is null)
            {
                continue;
            }

            var schema = entityType.GetSchema();
            var tableIdentifier = StoreObjectIdentifier.Table(table, schema);
            var qualifiedTable = sql.DelimitIdentifier(table, schema);
            foreach (var index in entityType.GetIndexes())
            {
                var name = index.GetDatabaseName(tableIdentifier);
                if (name is null)
                {
                    continue;
                }

                // PostgreSQL partitioned tables require all partition key columns ("WorkflowKey") in any UNIQUE index.
                var isUnique = index.IsUnique && (!isPostgres || index.Properties.Any(p => p.Name == "WorkflowKey"));
                var columns = string.Join(", ", index.Properties.Select((p, i) =>
                    sql.DelimitIdentifier(p.GetColumnName(tableIdentifier)!) + (index.IsDescending is { Count: > 0 } desc && desc[i] ? " DESC" : string.Empty)));
                yield return $"CREATE {(isUnique ? "UNIQUE " : string.Empty)}INDEX IF NOT EXISTS "
                    + $"{sql.DelimitIdentifier(name)} ON {qualifiedTable} ({columns})"
                    + (index.GetFilter() is { Length: > 0 } filter ? $" WHERE {filter}" : string.Empty);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private Task<bool> TablesExistAsync(WorkflowDbContext db, CancellationToken cancellationToken) =>
        TableExistsAsync(db, options.Value.Schema, "WorkflowInstances", cancellationToken);

    internal static async Task<bool> TableExistsAsync(WorkflowDbContext db, string? schemaName, string tableName, CancellationToken cancellationToken)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = {tableName}").SingleAsync(cancellationToken) > 0;
        }

        if (provider.Contains("MySql", StringComparison.OrdinalIgnoreCase))
        {
            return await db.Database.SqlQuery<int>($"SELECT CAST(COUNT(*) AS SIGNED) AS `Value` FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = {tableName}").SingleAsync(cancellationToken) > 0;
        }

        if (provider.Contains("Oracle", StringComparison.OrdinalIgnoreCase))
        {
            return await db.Database.SqlQuery<int>($"SELECT CAST(COUNT(*) AS NUMBER(10)) AS \"Value\" FROM USER_TABLES WHERE UPPER(TABLE_NAME) = UPPER({tableName})").SingleAsync(cancellationToken) > 0;
        }

        var defaultSchema = provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) ? "dbo" : "public";
        var schema = string.IsNullOrWhiteSpace(schemaName) ? defaultSchema : schemaName;
        return await db.Database.SqlQuery<int>($"SELECT CAST(COUNT(*) AS int) AS \"Value\" FROM information_schema.tables WHERE table_schema = {schema} AND table_name = {tableName}").SingleAsync(cancellationToken) > 0;
    }

    private static async Task EnsureSchemaAsync(WorkflowDbContext db, string schema, CancellationToken cancellationToken)
    {
        // Identifiers cannot be parameterised: only plain identifiers are accepted.
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[A-Za-z_][A-Za-z0-9_]{0,63}$"))
        {
            throw new InvalidOperationException($"Invalid workflow schema name '{schema}'.");
        }

        var provider = db.Database.ProviderName ?? string.Empty;
#pragma warning disable EF1002 // schema is a validated identifier
        if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            await db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA IF NOT EXISTS \"{schema}\"", cancellationToken);
        }
        else if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            await db.Database.ExecuteSqlRawAsync($"IF SCHEMA_ID(N'{schema}') IS NULL EXEC(N'CREATE SCHEMA [{schema}]');", cancellationToken);
        }
#pragma warning restore EF1002
    }

    internal static async Task<bool> AcquireStartupLockAsync(DbContext db, string provider, string lockKey, CancellationToken cancellationToken)
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

    internal static async Task ReleaseStartupLockAsync(DbContext db, string provider, string lockKey)
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
}
