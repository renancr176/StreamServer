using System.ComponentModel.DataAnnotations;

namespace StreamServer.Options;

public class StorageOptions
{
    public static string sectionKey = "Storage";

    [CustomValidation(typeof(StorageOptions), "DirectoryExists")]
    public IEnumerable<StorageFolder> Folders { get; set; } = new List<StorageFolder>();

    public static ValidationResult DirectoryExists(IEnumerable<StorageFolder> folders)
    {
        foreach (var storageFolder in folders)
        {
            try
            {
                if (!Directory.Exists(storageFolder.Folder))
                    Directory.CreateDirectory(storageFolder.Folder);
            }
            catch (Exception e)
            {
                return new ValidationResult($"The folder \"{storageFolder.Folder}\" is invalid.");
            }
        }

        if (!folders.Any(x => x.Store))
            return new ValidationResult("No one folder was defined to store streaming.");

        return ValidationResult.Success;
    }
}

public class StorageFolder
{
    public string Folder { get; set; }
    public bool Store { get; set; }
}