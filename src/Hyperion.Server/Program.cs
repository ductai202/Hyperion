using System;
using System.Threading;
using System.Threading.Tasks;
using Hyperion.Persistence;
using Hyperion.Cluster;
using Hyperion.Server;
using Microsoft.Extensions.Logging;

namespace Hyperion;

class Program
{
    static async Task<int> Main(string[] args)
    {
        int port       = 3000;
        string mode    = "multi";
        int workers    = Environment.ProcessorCount;
        int ioHandlers = Math.Max(1, Environment.ProcessorCount / 2);
        LogLevel minLog = LogLevel.Warning;
        int delayUs    = 0;
        bool noSave    = false;
        
        bool clusterEnabled = false;
        string clusterConfigFile = "nodes.conf";

        // Persistence defaults
        string dbFilename = "dump.rdb";
        string dbDir      = Directory.GetCurrentDirectory();

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--port"       && int.TryParse(args[i + 1], out int argPort))   port       = argPort;
            if (args[i] == "--mode")                                                         mode       = args[i + 1].ToLowerInvariant();
            if (args[i] == "--workers"    && int.TryParse(args[i + 1], out int argWorkers)) workers    = argWorkers;
            if (args[i] == "--io"         && int.TryParse(args[i + 1], out int argIo))      ioHandlers = argIo;
            if (args[i] == "--delay-us"   && int.TryParse(args[i + 1], out int argDelay))   delayUs    = argDelay;
            if (args[i] == "--log"        && Enum.TryParse(args[i + 1], true, out LogLevel level)) minLog = level;
            if (args[i] == "--dbfilename")                                                   dbFilename = args[i + 1];
            if (args[i] == "--dir")                                                          dbDir      = args[i + 1];
            if (args[i] == "--cluster-enabled" && args[i + 1].ToLower() == "yes")            clusterEnabled = true;
            if (args[i] == "--cluster-config-file")                                          clusterConfigFile = args[i + 1];
        }
        if (Array.Exists(args, a => a == "--no-save")) noSave = true;

        var persistenceConfig = noSave
            ? PersistenceConfig.Disabled
            : new PersistenceConfig
            {
                RdbFilePath = Path.Combine(dbDir, dbFilename)
            };

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(minLog);
        });

        var logger = loggerFactory.CreateLogger<Program>();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            logger.LogInformation("Shutdown signal received, stopping...");
            cts.Cancel();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        Console.WriteLine($"Starting Hyperion in [{mode}] mode on port {port} " +
                          $"(Workers: {workers}, IO: {ioHandlers}, RDB: {persistenceConfig.RdbFilePath})");

        try
        {
            if (mode == "single")
            {
                if (clusterEnabled)
                {
                    logger.LogCritical("Cluster mode is not supported in single-thread mode.");
                    return 1;
                }
                
                var server = new SingleThreadServer(
                    loggerFactory.CreateLogger<SingleThreadServer>(),
                    port,
                    persistenceConfig,
                    delayUs);
                await server.RunAsync(cts.Token);
            }
            else
            {
                ClusterState? clusterState = null;
                ClusterBus? clusterBus = null;
                GossipEngine? gossipEngine = null;

                if (clusterEnabled)
                {
                    clusterState = ClusterState.LoadConfig(clusterConfigFile);
                    if (clusterState == null)
                    {
                        string myId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N").Substring(0, 8); // 40 chars
                        clusterState = new ClusterState(myId);
                        clusterState.Myself.Ip = "127.0.0.1";
                        clusterState.Myself.Port = port;
                        clusterState.Myself.ClusterBusPort = port + 10000;
                        clusterState.SaveConfig(clusterConfigFile);
                    }
                    else
                    {
                        // Ensure IP/Ports are updated
                        clusterState.Myself.Ip = "127.0.0.1";
                        clusterState.Myself.Port = port;
                        clusterState.Myself.ClusterBusPort = port + 10000;
                    }

                    clusterState.SaveConfigCallback = () => clusterState.SaveConfig(clusterConfigFile);
                    
                    gossipEngine = new GossipEngine(clusterState, loggerFactory.CreateLogger<GossipEngine>());
                    clusterBus = new ClusterBus(clusterState, gossipEngine, loggerFactory.CreateLogger<ClusterBus>());
                    gossipEngine.SetBus(clusterBus);
                    
                    clusterBus.Start();
                    gossipEngine.Start();
                }

                var server = new HyperionServer(
                    loggerFactory, port, workers, ioHandlers, delayUs, persistenceConfig, clusterState);
                
                try
                {
                    await server.RunAsync(cts.Token);
                }
                finally
                {
                    gossipEngine?.Stop();
                    clusterBus?.Stop();
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (minLog <= LogLevel.Information)
                logger.LogInformation("Server shut down completely.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Server crashed.");
            return 1;
        }

        return 0;
    }
}
