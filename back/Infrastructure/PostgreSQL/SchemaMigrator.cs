using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace DocParse.Infrastructure.PostgreSQL;

public static class SchemaMigrator
{
    public static async Task ApplyAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var migrationLock = new NpgsqlCommand("SELECT pg_advisory_lock(693865320);", connection);
        await migrationLock.ExecuteNonQueryAsync(cancellationToken);
        try
        {
            await using var setup = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS public.schema_migrations (name text PRIMARY KEY, checksum text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now());", connection);
            await setup.ExecuteNonQueryAsync(cancellationToken);
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var resource in assembly.GetManifestResourceNames().Where(x => x.Contains(".Migrations.") && x.EndsWith(".sql")).Order())
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
                var sql = (await reader.ReadToEndAsync(cancellationToken)).Replace("\r\n", "\n");
                var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
                await using var lookup = new NpgsqlCommand("SELECT checksum FROM public.schema_migrations WHERE name = @name", connection);
                lookup.Parameters.AddWithValue("name", resource);
                var recorded = await lookup.ExecuteScalarAsync(cancellationToken) as string;
                if (recorded is not null)
                {
                    if (recorded != checksum) throw new InvalidOperationException($"Applied migration has changed: {resource}");
                    continue;
                }
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await using var apply = new NpgsqlCommand(sql, connection, transaction);
                await apply.ExecuteNonQueryAsync(cancellationToken);
                await using var record = new NpgsqlCommand("INSERT INTO public.schema_migrations (name, checksum) VALUES (@name, @checksum)", connection, transaction);
                record.Parameters.AddWithValue("name", resource);
                record.Parameters.AddWithValue("checksum", checksum);
                await record.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                Console.WriteLine($"Applied migration: {resource}");
            }
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(693865320)", connection);
            await unlock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
