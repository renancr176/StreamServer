using System.ComponentModel.DataAnnotations;

namespace StreamServer.Options;

public class StorageOptions
{
    public static string sectionKey = "Storage";

    [CustomValidation(typeof(StorageOptions), "DirectoryExists")]
    public string Folder { get; set; }

    public static ValidationResult DirectoryExists(string? Folder)
    {
        try
        {
            if (!Directory.Exists(Folder))
                Directory.CreateDirectory(Folder);

            return ValidationResult.Success;
        }
        catch (Exception e)
        {
        }
        return new ValidationResult("The specified folder is invalid.");
    }
}