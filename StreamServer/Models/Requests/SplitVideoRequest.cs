using System.ComponentModel.DataAnnotations;

namespace StreamServer.Models.Requests
{
    public class SplitVideoRequest
    {
        [Required]
        [CustomValidation(typeof(SplitVideoRequest), "ValidateFileExists")]
        public string FilePath { get; set; }
        /// <example>00:00:00</example>
        [Required]
        [CustomValidation(typeof(SplitVideoRequest), "ValidateSplitAtMinval")]
        public TimeSpan SplitAt { get; set; }

        public static ValidationResult ValidateFileExists(string filePath)
        {
            return File.Exists(filePath)
                ? ValidationResult.Success
                : new ValidationResult("File not exists.");
        }

        public static ValidationResult ValidateSplitAtMinval(TimeSpan splitAt)
        {
            return splitAt > TimeSpan.Zero
                ? ValidationResult.Success
                : new ValidationResult("Split time must be greater than zero.");
        }
    }
}
