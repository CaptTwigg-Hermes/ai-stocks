using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;

namespace AiStocks.Observability;

public static class AiStocksLogging
{
    public static WebApplicationBuilder UseAiStocksSerilog(
        this WebApplicationBuilder builder,
        string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        builder.Host.UseSerilog((context, _, loggerConfiguration) => Configure(
            loggerConfiguration,
            context.Configuration,
            serviceName,
            context.HostingEnvironment.EnvironmentName));
        return builder;
    }

    public static WebApplication UseAiStocksRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate =
                "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
            options.GetLevel = (context, _, exception) =>
            {
                if (exception is not null || context.Response.StatusCode >= 500)
                    return LogEventLevel.Error;

                var path = context.Request.Path.Value;
                return path is "/healthz" or "/readyz"
                    ? LogEventLevel.Debug
                    : LogEventLevel.Information;
            };
        });
        return app;
    }

    public static Serilog.ILogger CreateLogger(
        IConfiguration configuration,
        string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        var environmentName = configuration["DOTNET_ENVIRONMENT"]
            ?? configuration["ASPNETCORE_ENVIRONMENT"]
            ?? "Production";
        return Configure(new LoggerConfiguration(), configuration, serviceName, environmentName)
            .CreateLogger();
    }

    public static LoggerConfiguration Configure(
        LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        string serviceName,
        string environmentName)
    {
        ArgumentNullException.ThrowIfNull(loggerConfiguration);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        loggerConfiguration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "AiStocks")
            .Enrich.WithProperty("Service", serviceName)
            .Enrich.WithProperty("Environment", environmentName)
            .WriteTo.Console()
            .ReadFrom.Configuration(configuration);

        var seqServerUrl = configuration["SEQ_SERVER_URL"];
        if (string.IsNullOrWhiteSpace(seqServerUrl))
            return loggerConfiguration;

        if (!Uri.TryCreate(seqServerUrl, UriKind.Absolute, out var seqUri)
            || (seqUri.Scheme != Uri.UriSchemeHttp && seqUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(seqUri.UserInfo))
            throw new InvalidOperationException(
                "SEQ_SERVER_URL must be an absolute HTTP(S) URL without embedded credentials.");

        var apiKey = configuration["SEQ_API_KEY"];
        loggerConfiguration.WriteTo.Seq(
            seqUri.ToString().TrimEnd('/'),
            apiKey: string.IsNullOrWhiteSpace(apiKey) ? null : apiKey);
        return loggerConfiguration;
    }
}
