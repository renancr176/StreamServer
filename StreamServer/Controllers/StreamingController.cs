using Microsoft.AspNetCore.Mvc;
using StreamServer.Extensions;
using StreamServer.Models.Requests;
using StreamServer.Models.Responses;
using Swashbuckle.AspNetCore.Annotations;
using System.IO;
using System.Text.RegularExpressions;
using Xabe.FFmpeg;

namespace StreamServer.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class StreamingController : Controller
    {
        /// <summary>
        /// Upload video
        /// </summary>
        [HttpPost("UploadVideo")]
        [RequestSizeLimit(2147483648)] // 2 GB
        [RequestFormLimits(MultipartBodyLengthLimit = 524288000)] // 2 GB
        [SwaggerResponse(200, Type = typeof(BaseResponse))]
        [SwaggerResponse(400, Type = typeof(BaseResponse))]
        public async Task<IActionResult> UploadVideoAsync([FromForm] StreamingUploadVideoRequest request)
        {
            var baseResponse = new BaseResponse();
            var validExtensions = new List<string>()
            {
                ".mpeg", ".mp4", ".mkv"
            };

            var fileExtension = request?.Video != null ? Path.GetExtension(request.Video.FileName) : "";
            var tempFile = new FileInfo(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{fileExtension}"));
            try
            {
                if (request?.Video == null || request.Video.Length == 0)
                {
                    throw new Exception("No file not received");
                }

                if (validExtensions.All(extension => extension != fileExtension))
                {
                    throw new Exception("Invalid file type.");
                }

                var folderName = Path.Combine(Directory.GetCurrentDirectory(), "hls", request.Video.FileName.Replace(fileExtension, "").SanitizeFolderName());

                if (Directory.Exists(folderName) && Directory.GetFiles(folderName).Length > 0)
                {
                    throw new Exception("File already uploaded.");
                }

                using (var memoryStream = new MemoryStream())
                {
                    await request.Video.CopyToAsync(memoryStream);
                    memoryStream.Position = 0;
                    await System.IO.File.WriteAllBytesAsync(tempFile.FullName, memoryStream.ToArray());
                }

                var mediaInfo = await FFmpeg.GetMediaInfo(tempFile.FullName);

                if (!Directory.Exists(folderName))
                    Directory.CreateDirectory(folderName);

                await FFmpeg.Conversions.New()
                    .AddStream(mediaInfo.Streams)
                    // -codec (Copy the codec)
                    // -start_number (Start at second)
                    // -hls_time (Define duration of each segment in seconds)
                    // -hls_list_size (Keep all segments in the playlist)
                    // -f hls (Sets the output format to HLS)
                    .AddParameter("-codec: copy -start_number 0 -hls_time 10 -hls_list_size 0 -f hls")
                    .SetOutput(Path.Combine(folderName, "play.m3u8"))
                    .Start();
            }
            catch (Exception e)
            {
                baseResponse.Errors.Add(new BaseResponseError()
                {
                    ErrorCode = "InternalServerError",
                    Message = e.Message
                });
            }
            finally
            {
                if (System.IO.File.Exists(tempFile.FullName))
                    System.IO.File.Delete(tempFile.FullName);
            }
            return BadRequest(baseResponse);
        }

        /// <summary>
        /// Process video
        /// </summary>
        [HttpPatch("ProcessVideo")]
        [SwaggerResponse(200, Type = typeof(BaseResponse))]
        [SwaggerResponse(400, Type = typeof(BaseResponse))]
        public async Task<IActionResult> ProcessVideoAsync([FromBody] StreamingProcessVideoRequest request)
        {
            var baseResponse = new BaseResponse();

            if (!ModelState.IsValid)
            {
                foreach (var modelError in ModelState.Values.SelectMany(x => x.Errors)) 
                {
                    baseResponse.Errors.Add(new BaseResponseError()
                    {
                        ErrorCode = "ModelError",
                        Message = modelError.ErrorMessage
                    });
                }

                return BadRequest(baseResponse);
            }

            
            var validExtensions = new List<string>()
            {
                ".mpeg", ".mp4", ".mkv"
            };

            var video = new FileInfo(request.FilePath);
            try
            {
                if (validExtensions.All(extension => extension != video.Extension))
                {
                    throw new Exception("Invalid file type.");
                }

                var folderName = Path.Combine(Directory.GetCurrentDirectory(), "hls", video.Name.Replace(video.Extension, "").SanitizeFolderName());

                if (Directory.Exists(folderName) && Directory.GetFiles(folderName).Any())
                    Directory.Delete(folderName, true);

                if (!Directory.Exists(folderName))
                    Directory.CreateDirectory(folderName);

                var mediaInfo = await FFmpeg.GetMediaInfo(video.FullName);

                await FFmpeg.Conversions.New()
                    .AddStream(mediaInfo.Streams)
                    //.AddParameter($"-codec: copy -hls_time 10 -hls_playlist_type vod \"{Path.Combine(folderName, "playlist.m3u8")}\"")
                    .AddParameter($"-map 0:v:0 -codec: copy -an -hls_time 10 -hls_playlist_type vod \"{Path.Combine(folderName, "playlist.m3u8")}\" -map 0:a:0 -vn -q:a 0 \"{Path.Combine(folderName, "audio_track_1.mp3")}\" -map 0:a:1 -vn -q:a 0 \"{Path.Combine(folderName, "audio_track_2.mp3")}\"")
                    .Start();

                var tracks = Directory.GetFiles(folderName)
                    .Where(file => Regex.IsMatch(file, @"audio_track_\d+\.mp3$"))
                    .Select(file => new FileInfo(file))
                    .ToList();

                foreach (var track in tracks)
                {
                    mediaInfo = await FFmpeg.GetMediaInfo(track.FullName);

                    await FFmpeg.Conversions.New()
                        .AddStream(mediaInfo.Streams)
                        .AddParameter($"-c:a aac -b:a 128k -f hls -hls_time 10 -hls_list_size 0 \"{Path.Combine(folderName, $"{track.Name.Replace(track.Extension, "")}.m3u8")}\"")
                        .Start();

                    System.IO.File.Delete(track.FullName);
                }

                return Ok(baseResponse);
            }
            catch (Exception e)
            {
                baseResponse.Errors.Add(new BaseResponseError()
                {
                    ErrorCode = "InternalServerError",
                    Message = e.Message
                });
            }

            return BadRequest(baseResponse);
        }

        [HttpGet("Videos")]
        [SwaggerResponse(200, Type = typeof(VideoReponse))]
        public async Task<IActionResult> ListVideosAsync()
        {
            var hlsPath = Path.Combine(Directory.GetCurrentDirectory(), "hls");

            var directories = Directory.GetDirectories(hlsPath);

            var videos = new List<VideoReponse>();

            foreach (var directory in directories)
            {
                var tracks = Directory.GetFiles(directory)
                    .Where(file => Regex.IsMatch(file, @"audio_track_\d+\.m3u8$"))
                    .Select(file => $"/hls/{Path.GetFileName(directory)}/{Path.GetFileName(file)}")
                    .ToList();
                var video = new VideoReponse(
                    Path.GetFileName(directory),
                    $"/hls/{Path.GetFileName(directory)}/playlist.m3u8"
                    )
                {
                    Tracks = tracks
                };
                videos.Add(video);
            }

            return Ok(videos);
        }
    }
}
