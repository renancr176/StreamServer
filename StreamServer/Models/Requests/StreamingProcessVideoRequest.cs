using System.ComponentModel.DataAnnotations;

namespace StreamServer.Models.Requests;

public class StreamingProcessVideoRequest
{
    [Required]
    [CustomValidation(typeof(StreamingProcessVideoRequest), "ValidateFileExists")]
    public IEnumerable<string> FilesPath { get; set; }
    public bool ExtractAudioTracks { get; set; } = false;
    public bool DeletedFileAfterProcess { get; set; } = false;

    public static ValidationResult ValidateFileExists(IEnumerable<string> filesPath)
    {
        return filesPath.All(filePath => File.Exists(filePath))
            ? ValidationResult.Success
            : new ValidationResult("File not exists.");
    }
}