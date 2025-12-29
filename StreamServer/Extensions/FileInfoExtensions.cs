using Microsoft.AspNetCore.StaticFiles;

namespace StreamServer.Extensions;

public static class FileInfoExtensions
{
    public static string GetContentType(this FileInfo file)
    {
        switch (file.Extension.ToUpper())
        {
            case ".M3U8":
                return "application/x-mpegURL";
            case ".TS":
                return "application/x-typescript";
            case ".SRT":
                return "application/x-subrip";
            default:
                if (!new FileExtensionContentTypeProvider().TryGetContentType(file.Name, out var mediaType))
                    throw new NotImplementedException("Unrecognized content type.");
                return mediaType;
        }
    }
}