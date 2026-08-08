using Npgsql;

namespace AiStocks.Persistence;

public sealed class RuntimeConfigurationException(string message, Exception? inner = null) : Exception(message, inner);

public static class PostgresConfiguration
{
    public static string Require(IReadOnlyDictionary<string, string?> environment, string name)
    {
        if (!environment.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            throw new RuntimeConfigurationException($"{name} is required.");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(value);
            if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database) ||
                string.IsNullOrWhiteSpace(builder.Username) || string.IsNullOrEmpty(builder.Password))
                throw new ArgumentException();
            return builder.ConnectionString;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new RuntimeConfigurationException($"{name} must be a PostgreSQL connection string with host, database, username, and password.", exception);
        }
    }

    public static IReadOnlyDictionary<string, string?> Environment() =>
        System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(entry => entry.Key is string)
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString(), StringComparer.Ordinal);
}
