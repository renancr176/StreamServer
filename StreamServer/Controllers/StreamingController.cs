using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StreamServer.Extensions;
using StreamServer.Models.Requests;
using StreamServer.Models.Responses;
using StreamServer.Options;
using Swashbuckle.AspNetCore.Annotations;
using System.Text;
using System.Text.RegularExpressions;
using Xabe.FFmpeg;

namespace StreamServer.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class StreamingController : Controller
    {
        private readonly IOptions<StorageOptions> _storageOptions;

        public StreamingController(IOptions<StorageOptions> storageOptions)
        {
            _storageOptions = storageOptions;
        }

        private StorageOptions StorageOptions => _storageOptions.Value;

        /// <summary>
        /// Process video
        /// </summary>
        [HttpPatch("ProcessVideo")]
        [SwaggerResponse(200, Type = typeof(BaseResponse<IEnumerable<BaseResponse>>))]
        [SwaggerResponse(400, Type = typeof(BaseResponse<IEnumerable<BaseResponse>>))]
        public async Task<IActionResult> ProcessVideoAsync([FromBody] StreamingProcessVideoRequest request)
        {
            var responses = new List<BaseResponse>();

            if (!ModelState.IsValid)
            {
                var baseResponse = new BaseResponse();
                foreach (var modelError in ModelState.Values.SelectMany(x => x.Errors)) 
                {
                    baseResponse.Errors.Add(new BaseResponseError()
                    {
                        ErrorCode = "ModelError",
                        Message = modelError.ErrorMessage
                    });
                    
                }
                responses.Add(baseResponse);

                return BadRequest(responses);
            }

            var validExtensions = new List<string>()
            {
                ".mpeg", ".mp4", ".mkv", ".avi"
            };

            foreach (var filePath in request.FilesPath)
            {
                var baseResponse = new BaseResponse();
                var video = new FileInfo(filePath);
                try
                {
                    if (validExtensions.All(extension => extension != video.Extension))
                    {
                        throw new Exception("Invalid file type.");
                    }

                    var folderName = Path.Combine(StorageOptions.Folder, video.Name.Replace(video.Extension, "").SanitizeFolderName());
                    //Path.Combine(Directory.GetCurrentDirectory(), "hls", video.Name.Replace(video.Extension, "").SanitizeFolderName());

                    if (Directory.Exists(folderName) && Directory.GetFiles(folderName).Any())
                        Directory.Delete(folderName, true);

                    if (!Directory.Exists(folderName))
                        Directory.CreateDirectory(folderName);

                    var mediaInfo = await FFmpeg.GetMediaInfo(video.FullName);

                    var processVideoArguments = new StringBuilder();

                    if (request.ExtractAudioTracks)
                    {
                        processVideoArguments.Append(
                            $"-map 0:v:0 -codec: copy -an -sn -hls_time 10 -hls_playlist_type vod \"{Path.Combine(folderName, "playlist.m3u8")}\"");

                        var trackIndex = 0;
                        foreach (var mediaInfoAudioStream in mediaInfo.AudioStreams)
                        {
                            processVideoArguments.Append(
                                $" -map 0:a:{trackIndex} -vn -q:a 0 \"{Path.Combine(folderName, $"audio_track_{trackIndex + 1}.mp3")}\"");
                            trackIndex++;
                        }
                    }
                    else
                    {
                        processVideoArguments.Append($"-codec: copy -sn -hls_time 10 -hls_playlist_type vod \"{Path.Combine(folderName, "playlist.m3u8")}\"");
                    }

                    var args = processVideoArguments.ToString();
                    await FFmpeg.Conversions.New()
                        .AddStream(mediaInfo.Streams)
                        //.AddParameter($"-codec: copy -hls_time 10 -hls_playlist_type vod \"{Path.Combine(folderName, "playlist.m3u8")}\"")
                        //.AddParameter($"-map 0:v:0 -codec: copy -an -hls_time 10 -hls_playlist_type vod \"{Path.Combine(folderName, "playlist.m3u8")}\" -map 0:a:0 -vn -q:a 0 \"{Path.Combine(folderName, "audio_track_1.mp3")}\" -map 0:a:1 -vn -q:a 0 \"{Path.Combine(folderName, "audio_track_2.mp3")}\"")
                        .AddParameter(args)
                        .Start();

                    var tracks = Directory.GetFiles(folderName)
                        .Where(file => Regex.IsMatch(file, @"audio_track_\d+\.mp3$"))
                        .Select(file => new FileInfo(file))
                        .ToList();

                    foreach (var track in tracks)
                    {
                        var trackInfo = await FFmpeg.GetMediaInfo(track.FullName);
                        var trackFolder = Path.Combine(folderName, track.Name.Replace(track.Extension, ""));

                        if (!Directory.Exists(trackFolder))
                            Directory.CreateDirectory(trackFolder);

                        await FFmpeg.Conversions.New()
                            .AddStream(trackInfo.Streams)
                            .AddParameter($"-c:a aac -b:a 128k -f hls -hls_time 10 -hls_list_size 0 \"{Path.Combine(trackFolder, "playlist.m3u8")}\"")
                            .Start();

                        System.IO.File.Delete(track.FullName);
                    }

                    if (request.DeletedFileAfterProcess)
                    {
                        System.IO.File.Delete(video.FullName);
                    }
                }
                catch (Exception e)
                {
                    baseResponse.Errors.Add(new BaseResponseError()
                    {
                        ErrorCode = "InternalServerError",
                        Message = e.Message
                    });
                }

                responses.Add(baseResponse);
            }

            return responses.All(x => x.Success) 
                ? Ok(responses)
                : BadRequest(responses);
        }

        [HttpGet("Videos")]
        [SwaggerResponse(200, Type = typeof(VideoReponse))]
        public async Task<IActionResult> ListVideosAsync()
        {
            var hlsPath = StorageOptions.Folder; //Path.Combine(Directory.GetCurrentDirectory(), "hls");

            var directories = Directory.GetDirectories(hlsPath)
                .Where(d => Directory.GetFiles(d).Any(file => file.EndsWith(".m3u8")));

            var videos = new List<VideoReponse>();

            foreach (var directory in directories)
            {
                var tracks = Directory.GetDirectories(directory)
                    .Where(file => Regex.IsMatch(file, @"audio_track_\d+$"))
                    .SelectMany(d => Directory.GetFiles(d))
                    .Where(file => file.EndsWith(".m3u8"))
                    .Select(file => Regex.Replace(file, @"^.*.hls", "/Streaming/Hls", RegexOptions.IgnoreCase).Replace("\\","/"))
                    .ToList();
                var video = new VideoReponse(
                    Path.GetFileName(directory),
                    $"/Streaming/Hls/{Path.GetFileName(directory)}/playlist.m3u8"
                    )
                {
                    Tracks = tracks,
                    Legends = Directory.GetDirectories(directory)
                        .Where(file => Regex.IsMatch(file, @"subtitles"))
                        .SelectMany(d => Directory.GetFiles(d))
                        .Where(file => file.EndsWith(".srt"))
                        .Select(file => Regex.Replace(file, @"^.*.hls", "/Streaming/Hls", RegexOptions.IgnoreCase).Replace("\\", "/"))
                        .ToList()
                };
                videos.Add(video);
            }

            return Ok(videos);
        }

        [HttpGet("Hls/{folder}/{fileName}")]
        [SwaggerResponse(200)]
        [SwaggerResponse(400)]
        public async Task<IActionResult> HlsAsync([FromRoute] string folder, [FromRoute] string fileName)
        {
            var file = new FileInfo(Path.Combine(StorageOptions.Folder, folder, fileName)); //new FileInfo(Path.Combine(Directory.GetCurrentDirectory(), "hls", folder, fileName));

            if (!System.IO.File.Exists(file.FullName))
                return BadRequest();

            return File(System.IO.File.ReadAllBytes(file.FullName), file.GetContentType(), file.Name);
        }
        
        [HttpGet("Hls/{folder}/{subFolder}/{fileName}")]
        [SwaggerResponse(200)]
        [SwaggerResponse(400)]
        public async Task<IActionResult> HlsAsync([FromRoute] string folder, [FromRoute] string subFolder, [FromRoute] string fileName)
        {
            var file = new FileInfo(Path.Combine(StorageOptions.Folder, folder, subFolder, fileName)); //new FileInfo(Path.Combine(Directory.GetCurrentDirectory(), "hls", folder, subFolder, fileName));

            if (!System.IO.File.Exists(file.FullName))
                return BadRequest();

            return File(System.IO.File.ReadAllBytes(file.FullName), file.GetContentType(), file.Name);
        }
    }
}
