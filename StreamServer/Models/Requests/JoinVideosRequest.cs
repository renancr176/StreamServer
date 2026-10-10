using System.ComponentModel.DataAnnotations;

namespace StreamServer.Models.Requests
{
    public class JoinVideosRequest
    {
        [Required]
        [CustomValidation(typeof(JoinVideosRequest), "ValidateFilesExists")]
        public IEnumerable<string> FilesPath { get; set; } = new List<string>();
        [CustomValidation(typeof(JoinVideosRequest), "ValidateUseAudioTrack")]
        public int? UseAudioTrack { get; set; }

        public static ValidationResult ValidateFilesExists(IEnumerable<string> filesPath)
        {
            return filesPath.Any(filePath => !File.Exists(filePath))
                ? new ValidationResult("File not exists.")
                : ValidationResult.Success;
        }

        public static ValidationResult ValidateUseAudioTrack(int? useAudioTrack)
        {
            return useAudioTrack == null || useAudioTrack >= 0
                ? ValidationResult.Success 
                : new ValidationResult("File not exists.");
        }
    }
}
