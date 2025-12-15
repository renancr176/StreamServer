using System.ComponentModel.DataAnnotations;

namespace StreamServer.Models.Requests;

public class StreamingUploadVideoRequest
{
    [Required]
    public IFormFile Video { get; set; }
}