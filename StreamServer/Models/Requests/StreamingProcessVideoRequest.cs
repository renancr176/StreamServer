using System.ComponentModel.DataAnnotations;

namespace StreamServer.Models.Requests;

public class StreamingProcessVideoRequest
{
    [Required]
    [CustomValidation(typeof(StreamingProcessVideoRequest), "ValidateFileExists")]
    public string FilePath { get; set; }
    public bool ExtractAudioTracks { get; set; } = false;

    public static ValidationResult ValidateFileExists(string? filePath)
    {
        return File.Exists(filePath)
            ? ValidationResult.Success
            : new ValidationResult("File not exists.");
    }
}