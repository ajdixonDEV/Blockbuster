using Blockbuster;
using Serilog;

Log.Logger = DependencyInjection.CreateBootstrapLogger();

try
{
    var isOperatorCommand = args.Length > 0 && string.Equals(args[0], "operator", StringComparison.OrdinalIgnoreCase);
    var builder = WebApplication.CreateBuilder(isOperatorCommand ? [] : args);
    builder.AddBlockbusterWeb();

    var app = builder.Build();
    if (isOperatorCommand) return await app.RunBlockbusterOperatorAsync(args[1..]);

    app.UseBlockbusterWeb();
    Log.Information("Starting Blockbuster in {Environment}", app.Environment.EnvironmentName);
    await app.RunAsync();
    return 0;
}
catch (Exception exception)
{
    Log.Fatal(exception, "Blockbuster terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
