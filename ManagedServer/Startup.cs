using Yarp.ReverseProxy.Configuration;

namespace ManagedServer;

public class Startup
{
    public IConfiguration Configuration { get; }

    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddLettuceEncrypt();

        services.AddReverseProxy().LoadFromMemory(Array.Empty<RouteConfig>(), Array.Empty<ClusterConfig>());

        services.AddSingleton<DynamicMiddlewareConfig>();

        services.AddHostedService<ApplicationHostService>();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env, DynamicMiddlewareConfig dynamicMiddleware)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            await dynamicMiddleware.RequestDelegate(context);
            if (context.Items.TryGetValue("DynamicMiddlewareContinuePipeline", out var cont) && cont is "true")
            {
                await next(context);
            }
        });

        app.UseRouting();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapReverseProxy();
        });
    }
}
