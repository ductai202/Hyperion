using Hyperion.Config;
using Hyperion.Core;
using Hyperion.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Parse CLI args: --port 3000 --mode single|multi
int port = ServerConfig.Port.TrimStart(':') is string p && int.TryParse(p, out var parsedPort)
    ? parsedPort
    : 3000;
string mode = "single";

for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port" && int.TryParse(args[i + 1], out int argPort))
        port = argPort;
    if (args[i] == "--mode")
        mode = args[i + 1].ToLowerInvariant();
}

// Set up DI and logging
var services = new ServiceCollection()
    .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information))
    .AddSingleton<ICommandExecutor, CommandExecutor>()
    .BuildServiceProvider();

var logger = services.GetRequiredService<ILogger<SingleThreadServer>>();
var executor = services.GetRequiredService<ICommandExecutor>();

// Graceful shutdown on Ctrl+C / SIGTERM
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // Prevent hard exit, let us clean up
    logger.LogInformation("Shutdown signal received, stopping...");
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

logger.LogInformation("Starting Hyperion in [{Mode}] mode on port {Port}", mode, port);

try
{
    // Phase 3: single-threaded mode. Phase 6 will add multi-threaded mode here.
    if (mode == "single")
    {
        var server = new SingleThreadServer(executor, services.GetRequiredService<ILogger<SingleThreadServer>>(), port);
        await server.RunAsync(cts.Token);
    }
    else
    {
        logger.LogError("Mode '{Mode}' not yet implemented. Use --mode single.", mode);
        Environment.Exit(1);
    }
}
catch (OperationCanceledException)
{
    // Clean exit
}

logger.LogInformation("Hyperion shut down cleanly.");
