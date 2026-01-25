namespace StreamServer.Options;

public static class OptionsIoC
{
    public static void AddOptionsIoC(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StorageOptions>()
            .BindConfiguration(StorageOptions.sectionKey)
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }
}