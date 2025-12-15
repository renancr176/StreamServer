using StreamServer.Interfaces.Services;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace StreamServer.Services;

public class FFmpegService : IFFmpegService
{
    public static string FFmpegPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FFmpeg");

    private async Task GetLatestVersionAsync()
    {
        if (Directory.Exists(FFmpegPath)
            && Directory.GetFiles(FFmpegPath).Any(file => file.EndsWith(".exe")))
            return;

        var retries = 3;
        var sucess = false;
        do
        {
            try
            {
                if (!Directory.Exists(FFmpegPath))
                    Directory.CreateDirectory(FFmpegPath);
                await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, FFmpegPath);
                sucess = true;
            }
            catch (Exception e)
            {
                retries--;
            }
        } while (!sucess && retries > 0);

        if (!sucess)
            throw new Exception("Error when trying to download FFmpeg. Download attempts exceeded.");
    }

    public async Task SetExecutablesPathAsync()
    {
        await GetLatestVersionAsync();
        FFmpeg.SetExecutablesPath(FFmpegPath);
    }
}