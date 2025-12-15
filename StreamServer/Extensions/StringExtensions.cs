namespace StreamServer.Extensions;

public static class StringExtensions
{
    public static string SanitizeFolderName(this string folderName)
    {
        var invalidChars = Path.GetInvalidPathChars();
        return new string(folderName.Where(c => !invalidChars.Contains(c)).ToArray());
    }
}