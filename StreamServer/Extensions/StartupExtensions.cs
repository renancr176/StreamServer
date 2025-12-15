namespace StreamServer.Extensions;

public static class StartupExtensions
{
    public static WebApplicationBuilder UseStartup<TStartup>(this WebApplicationBuilder webApplicationBuilder)
        where TStartup : IStartup
    {
        var startup = Activator.CreateInstance(typeof(TStartup), webApplicationBuilder.Configuration,
            webApplicationBuilder.Environment) as IStartup;
        if (startup == null)
            throw new ArgumentNullException($"A classe {typeof(TStartup).Name} é inválida.");

        startup.ConfigureServices(webApplicationBuilder.Services);
        var app = webApplicationBuilder.Build();
        startup.Configure(app, app.Environment);
        app.Run();

        return webApplicationBuilder;
    }
}