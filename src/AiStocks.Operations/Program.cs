using AiStocks.Observability;
using AiStocks.Operations;
using AiStocks.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Serilog;

var loggingConfiguration = new ConfigurationManager();
loggingConfiguration.AddEnvironmentVariables();
Log.Logger = AiStocksLogging.CreateLogger(loggingConfiguration, "AiStocks.Operations");

try
{
    if (args.SequenceEqual(["runtime"], StringComparer.Ordinal))
    {
        var environment = PostgresConfiguration.Environment();
        var runtimeConnection = PostgresConfiguration.Require(environment, "DATABASE_URL");
        var target = environment.GetValueOrDefault("DISCORD_REPORT_TARGET")
            ?? throw new RuntimeConfigurationException("Required configuration is missing.");
        var executable = environment.GetValueOrDefault("HERMES_EXECUTABLE") ?? "/opt/hermes/bin/hermes";
        var pollSeconds = int.TryParse(environment.GetValueOrDefault("OPERATIONS_POLL_SECONDS"), out var configuredPoll)
            ? configuredPoll : 30;
        await using var runtimeSource = NpgsqlDataSource.Create(runtimeConnection);
        var clock = new SystemOperationsClock(TimeProvider.System);
        var delivery = new AuditedDiscordDelivery(new PostgresDeliveryAuditPort(runtimeSource),
            new HermesDiscordPort(executable, target), clock);
        var publisher = new PostgresDailyReportPublisher(runtimeSource, new DailyReportService(), delivery);
        var alerts = new PostgresImmediateAlertPublisher(runtimeSource, delivery);
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
        Log.Information("Operations reporter runtime started with a {PollSeconds}-second poll interval", pollSeconds);
        await new OperationsRuntimeService(new PostgresContestOperations(runtimeSource), publisher, alerts,
            TimeProvider.System, TimeSpan.FromSeconds(pollSeconds)).RunAsync(shutdown.Token);
        return;
    }

    var command = OperationsCommandParser.Parse(args);
    var operationEnvironment = PostgresConfiguration.Environment();
    var migratorKey = command == OperationsCommand.Migrate ? "MIGRATOR_DATABASE_URL" : "DATABASE_URL";
    var migratorConnection = PostgresConfiguration.Require(operationEnvironment, migratorKey);
    var runtimeConnectionForCommand = PostgresConfiguration.Require(operationEnvironment, "DATABASE_URL");
    await using var migrator = NpgsqlDataSource.Create(migratorConnection);
    await using var runtime = NpgsqlDataSource.Create(runtimeConnectionForCommand);
    Environment.ExitCode = await OperationsApplication.RunAsync(
        args,
        new PostgresOperationsPorts(migrator, runtime),
        Console.Error,
        CancellationToken.None);
}
catch (OperationCanceledException) { }
catch (Exception exception) when (exception is OperationsException or RuntimeConfigurationException or NpgsqlException or InvalidOperationException)
{
    Log.Error(exception, "Operations command failed safely");
    Console.Error.WriteLine("operations failed safely: invalid command or configuration");
    Environment.ExitCode = 2;
}
finally
{
    await Log.CloseAndFlushAsync();
}

public sealed class SystemOperationsClock(TimeProvider timeProvider) : AiStocks.Core.IClock
{
    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
}
