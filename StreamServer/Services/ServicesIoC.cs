using StreamServer.Interfaces.Services;

namespace StreamServer.Services;

public static class ServicesIoC
{
    public static void AddServices(this IServiceCollection service, IConfiguration configuration)
    {
        service.AddSingleton<IFFmpegService, FFmpegService>();
    }
}