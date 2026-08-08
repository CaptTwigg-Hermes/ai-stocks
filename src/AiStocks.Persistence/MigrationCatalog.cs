using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace AiStocks.Persistence;

public sealed record SqlMigration(string Id, string Sql, string Sha256);

public static class MigrationCatalog
{
    public static IReadOnlyList<SqlMigration> All { get; } = Load();

    public static string ComputeSha256(string sql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));

    private static IReadOnlyList<SqlMigration> Load()
    {
        var assembly = typeof(MigrationCatalog).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"Missing migration resource {name}.");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var sql = reader.ReadToEnd();
                var fileName = name[(name.LastIndexOf(".Migrations.", StringComparison.Ordinal) + 12)..^4];
                return new SqlMigration(fileName, sql, ComputeSha256(sql));
            }).ToArray();
    }
}