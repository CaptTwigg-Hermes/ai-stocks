using AiStocks.Operations;
using AiStocks.Persistence;
using Npgsql;

try
{
    var command = OperationsCommandParser.Parse(args);
    var environment = PostgresConfiguration.Environment();
    var migratorKey = command == OperationsCommand.Migrate ? "MIGRATOR_DATABASE_URL" : "DATABASE_URL";
    var migratorConnection = PostgresConfiguration.Require(environment, migratorKey);
    var runtimeConnection = PostgresConfiguration.Require(environment, "DATABASE_URL");
    await using var migrator = NpgsqlDataSource.Create(migratorConnection);
    await using var runtime = NpgsqlDataSource.Create(runtimeConnection);
    Environment.ExitCode = await OperationsApplication.RunAsync(
        args,
        new PostgresOperationsPorts(migrator, runtime),
        Console.Error,
        CancellationToken.None);
}
catch (Exception exception) when (exception is OperationsException or RuntimeConfigurationException or NpgsqlException or InvalidOperationException)
{
    Console.Error.WriteLine("operations failed safely: invalid command or configuration");
    Environment.ExitCode = 2;
}
