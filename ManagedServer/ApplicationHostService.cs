using Microsoft.Extensions.FileProviders;
using Serilog;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reactive.Linq;
using System.Xml.Linq;
using Yarp.ReverseProxy.Configuration;

namespace ManagedServer;

class WebApp : IAsyncDisposable
{
    public required string Name { get; set; }
    public string ContentRoot { get; set; } = null!;
    public string[] Hosts { get; set; } = null!;
    public string Kind { get; set; } = null!;
    public bool Disabled { get; set; }

    public Process? Process { get; set; }
    public FileSystemWatcher? Watcher { get; set; }
    public string? Port { get; set; }
    public string? Token { get; set; }

    public bool StartProcess()
    {
        if (Disabled)
        {
            return false;
        }

        if (Process?.HasExited is false)
        {
            return true;
        }

        string webConfig = Path.Combine(ContentRoot, "web.config");
        if (!File.Exists(webConfig))
        {
            return false;
        }

        var config = XDocument.Load(webConfig);
        var aspNetCore = config.Descendants().FirstOrDefault(x => x.Name.LocalName == "aspNetCore");
        if (aspNetCore == null)
        {
            return false;
        }

        if (aspNetCore.Attribute("processPath")?.Value != "dotnet")
        {
            return false;
        }

        string entryPointAssembly = aspNetCore.Attribute("arguments")!.Value.TrimStart('.', '\\');
        var startInfo = new ProcessStartInfo("dotnet", entryPointAssembly);
        startInfo.WorkingDirectory = ContentRoot;
        foreach (var key in startInfo.Environment.Where(x => x.Key.StartsWith("DOTNET") || x.Key.StartsWith("ASPNETCORE")).ToList())
        {
            startInfo.Environment.Remove(key);
        }

        Port = GetRandomPort().ToString();
        Token = Guid.NewGuid().ToString();
        startInfo.Environment["ASPNETCORE_PORT"] = Port;
        startInfo.Environment["ASPNETCORE_APPL_PATH"] = "/";
        startInfo.Environment["ASPNETCORE_TOKEN"] = Token;
        startInfo.Environment["ASPNETCORE_IIS_HTTPAUTH"] = "anonymous;";
        startInfo.Environment["ASPNETCORE_IIS_WEBSOCKETS_SUPPORTED"] = "true";
        Process = Process.Start(startInfo);

        Watcher = new FileSystemWatcher(ContentRoot)
        {
            Filters = { "*.dll", "*.exe" },
            IncludeSubdirectories = true
        };

        var changed = Observable.FromEventPattern<FileSystemEventHandler, EventArgs>(h => Watcher.Changed += h, h => Watcher.Changed -= h);
        var created = Observable.FromEventPattern<FileSystemEventHandler, EventArgs>(h => Watcher.Created += h, h => Watcher.Created -= h);
        var deleted = Observable.FromEventPattern<FileSystemEventHandler, EventArgs>(h => Watcher.Deleted += h, h => Watcher.Deleted -= h);
        var renamed = Observable.FromEventPattern<RenamedEventHandler, EventArgs>(h => Watcher.Renamed += h, h => Watcher.Renamed -= h);
        var error = Observable.FromEventPattern<ErrorEventHandler, EventArgs>(h => Watcher.Error += h, h => Watcher.Error -= h);

        var anyEvent = Observable.Merge(changed, created, deleted, renamed, error);

        anyEvent.Throttle(TimeSpan.FromSeconds(10))
                .Take(1)
                .Subscribe(async _ =>
                {
                    Log.Information("{Name} files have changed, restarting", Name);
                    await RestartAsync();
                });

        Watcher.EnableRaisingEvents = true;

        return true;
    }

    private async Task RestartAsync()
    {
        await StopAsync();
        StartProcess();
    }

    private static int GetRandomPort()
    {
        var usedPorts = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Select(tcpi => tcpi.LocalEndPoint.Port).ToHashSet();

        int port;
        do
        {
            port = Random.Shared.Next(1025, 48000);
        } while (usedPorts.Contains(port));

        return port;
    }

    public async Task StopAsync()
    {
        Watcher?.Dispose();
        Watcher = null;
        var client = new HttpClient();
        var content = new StringContent("");
        content.Headers.Add("MS-ASPNETCORE-TOKEN", Token);
        content.Headers.Add("MS-ASPNETCORE-EVENT", "shutdown");
        var resp = await client.PostAsync($"http://127.0.0.1:{Port}/iisintegration", content);
        if (Process is { } p)
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (resp.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                try
                {
                    await p.WaitForExitAsync(cts.Token);
                    Log.Information("{Name} has shutdown peacefully", Name);
                }
                catch (OperationCanceledException)
                {
                    Log.Information("{Name} did not shutdown in time, killing", Name);
                    p.Kill();
                }
            }
            else
            {
                Log.Information("{Name} did not accept shutdown, killing", Name);
                p.Kill();
            }
            p.Close();
            p.Dispose();
            Process = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}

internal class ApplicationHostService : BackgroundService
{
    public InMemoryConfigProvider YarpConfig { get; }
    public DynamicMiddlewareConfig DynamicMiddlewareConfig { get; }
    public IConfiguration AppSettings { get; }
    public IServiceProvider ServiceProvider { get; }
    public List<WebApp> Apps { get; set; } = new();

    public ApplicationHostService(InMemoryConfigProvider yarpConfig, DynamicMiddlewareConfig dynamicMiddlewareConfig, IConfiguration appSettings, IServiceProvider serviceProvider)
    {
        YarpConfig = yarpConfig;
        DynamicMiddlewareConfig = dynamicMiddlewareConfig;
        AppSettings = appSettings;
        ServiceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ProcessConfiguration();
    }

    private async Task ProcessConfiguration()
    {
        var managedServer = AppSettings.GetSection("ManagedServer");

        Observable.Create<object?>(observer =>
        {
            return managedServer.GetReloadToken().RegisterChangeCallback(x =>
            {
                observer.OnNext(null);
            }, null);
        }).Throttle(TimeSpan.FromSeconds(1)).Take(1).Subscribe(async x =>
        {
            Log.Information("Configuration changed, reloading");
            await ProcessConfiguration();
        });

        var children = managedServer.GetChildren();
        var newApps = children.Select(child => new WebApp
        {
            Name = child.Key,
            ContentRoot = child["ContentRoot"]!,
            Hosts = child.GetSection("Hosts").GetChildren().Select(x => x.Value!).ToArray(),
            Kind = child["Kind"]!,
            Disabled = child["Disabled"] == "true"
        }).ToList();

        foreach (var (newApp, existingApp, _) in newApps.FullOuterJoin(Apps, x => x.Name, x => x.Name).ToList())
        {
            if (newApp != null && existingApp != null)
            {
                existingApp.Hosts = newApp.Hosts;
                if (newApp.ContentRoot != existingApp.ContentRoot || newApp.Kind != existingApp.Kind || newApp.Disabled != existingApp.Disabled)
                {
                    Log.Information("{Name} configuration changed, restarting", existingApp.Name);
                    await existingApp.StopAsync();
                    existingApp.ContentRoot = newApp.ContentRoot;
                    existingApp.Kind = existingApp.Kind;
                    existingApp.Disabled = existingApp.Disabled;
                }
            }
            else if (newApp != null)
            {
                Log.Information("Starting new app {Name}", newApp.Name);
                Apps.Add(newApp);
            }
            else
            {
                Log.Information("Stopping removed app {Name}", existingApp!.Name);
                Apps.Remove(existingApp);
                await existingApp.DisposeAsync();
            }
        }

        var builder = new ApplicationBuilder(ServiceProvider);
        var routes = new List<RouteConfig>(Apps.Count);
        var clusters = new List<ClusterConfig>(Apps.Count);
        foreach (var app in Apps)
        {
            if (app.Kind == "Static")
            {
                builder.MapWhen(x => app.Hosts.Contains(x.Request.Host.Host, StringComparer.OrdinalIgnoreCase), x =>
                {
                    var fileProvider = new PhysicalFileProvider(app.ContentRoot);
                    x.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
                    x.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
                });
                continue;
            }

            if (app.StartProcess())
            {
                routes.Add(new RouteConfig
                {
                    RouteId = app.Name,
                    ClusterId = app.Name,
                    Match = new RouteMatch { Path = "{**catch-all}", Hosts = app.Hosts },
                    Transforms = new[]
                    {
                        new Dictionary<string, string>
                        {
                            { "RequestHeader", "MS-ASPNETCORE-TOKEN" },
                            { "Set", app.Token }
                        },
                    }
                });

                clusters.Add(new ClusterConfig
                {
                    ClusterId = app.Name,
                    Destinations = new Dictionary<string, DestinationConfig>
                    {
                        { "primary", new DestinationConfig { Address = "http://127.0.0.1:" + app.Port } }
                    },
                });
            }
        }

        builder.Use((HttpContext context, RequestDelegate next) =>
        {
            context.Items["DynamicMiddlewareContinuePipeline"] = "true";
            return Task.CompletedTask;
        });

        DynamicMiddlewareConfig.RequestDelegate = builder.Build();

        YarpConfig.Update(routes, clusters);
    }
}