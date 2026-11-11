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

        /// <summary>
        /// false (default): copies the streams without re-encoding (takes seconds, no quality loss);
        /// the cut moves back to the last keyframe before SplitAt.
        /// true: cuts exactly at SplitAt re-encoding the video (NVIDIA GPU when available, otherwise CPU).
        /// </summary>
        /// <example>false</example>
        public bool Exact { get; set; }

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
