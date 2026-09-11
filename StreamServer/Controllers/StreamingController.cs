using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StreamServer.Extensions;
using StreamServer.Models.Requests;
using StreamServer.Models.Responses;
using StreamServer.Options;
using Swashbuckle.AspNetCore.Annotations;
using System.Text;
using System.Text.Json;
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

        private async Task<List<VideoReponse>> GetVideosAsync(string folder)
        {
            var videos = new List<VideoReponse>();

            try
            {
                var jsonPath = new FileInfo(Path.Combine(folder, "videos.json"));
                

                if (System.IO.File.Exists(jsonPath.FullName))
                {
                    try
                    {
                        var json = await System.IO.File.ReadAllTextAsync(jsonPath.FullName);
                        if (!string.IsNullOrEmpty(json))
                            videos = JsonSerializer.Deserialize<List<VideoReponse>>(json) ?? new List<VideoReponse>();
                    }
                    catch (Exception e)
                    {
                    }
                }
            }
            catch (Exception e)
            {
            }

            if (!videos.Any())
            {
                var directories = Directory.GetDirectories(folder)
                    .Where(d => Directory.GetFiles(d).Any(file => file.EndsWith(".m3u8")));

                foreach (var directory in directories)
                {
                    var tracks = Directory.GetDirectories(directory)
                        .Where(file => Regex.IsMatch(file, @"audio_track_\d+$"))
                        .SelectMany(d => Directory.GetFiles(d))
                        .Where(file => file.EndsWith(".m3u8"))
                        .Select(file => Regex.Replace(file, @"^.*.hls", "/Streaming/Hls", RegexOptions.IgnoreCase).Replace("\\", "/"))
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

                await SaveVideosAsync(folder, videos);
            }

            return videos;
        }

        private async Task SaveVideosAsync(string folder, List<VideoReponse> videos)
        {
            try
            {
                var jsonPath = new FileInfo(Path.Combine(folder, "videos.json"));
                if (System.IO.File.Exists(jsonPath.FullName))
                {
                    System.IO.File.Delete(jsonPath.FullName);
                }
                var json = JsonSerializer.Serialize(videos);
                await System.IO.File.WriteAllTextAsync(jsonPath.FullName, json);
            }
            catch (Exception e)
            {
            }
        }

        private async Task SplitVideoAsync(
            string inputPath,
            string outputPart1,
            string outputPart2,
            TimeSpan splitAt)
        {
            // Obtém a duração total do arquivo
            IMediaInfo mediaInfo = await FFmpeg.GetMediaInfo(inputPath);

            TimeSpan duracaoTotal = mediaInfo.Duration;

            if (splitAt >= duracaoTotal)
                throw new ArgumentException(
                    $"The video has only {duracaoTotal} of duration.");

            // Parte 1: 00:00:00 -> 21:46
            IConversion conversion1 =
                await FFmpeg.Conversions.FromSnippet.Split(
                    inputPath,
                    outputPart1,
                    TimeSpan.Zero,
                    splitAt);

            await conversion1.Start();

            // Parte 2: 21:46 -> final
            TimeSpan duracaoParte2 = duracaoTotal - splitAt;

            IConversion conversion2 =
                await FFmpeg.Conversions.FromSnippet.Split(
                    inputPath,
                    outputPart2,
                    splitAt,
                    duracaoParte2);

            await conversion2.Start();
        }

        private static List<string> VideoFolders { get; set; } = new List<string>();

        private async Task<string> GetVideoPathAsync(string videoDirName)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));

            var videoPath =
                VideoFolders.FirstOrDefault(storageFolder => 
                    storageFolder.EndsWith(videoDirName));

            if (string.IsNullOrEmpty(videoPath))
            {
                var list = new List<string>();
                foreach (var storageOption in StorageOptions.Folders)
                {
                    list.AddRange(Directory.GetDirectories(storageOption.Folder));
                }

                VideoFolders = list;

                videoPath =
                    VideoFolders.FirstOrDefault(storageFolder =>
                    storageFolder.EndsWith(videoDirName));
            }

            return videoPath;
        }

        /// <summary>
        /// Split video
        /// </summary>
        [HttpPatch("Split")]
        [SwaggerResponse(200, Type = typeof(BaseResponse<BaseResponse>))]
        [SwaggerResponse(400, Type = typeof(BaseResponse<BaseResponse>))]
        public async Task<IActionResult> SplitVideoAsync([FromBody] SplitVideoRequest request)
        {
            var response = new BaseResponse<object>();
            try
            {
                var file = new FileInfo(request.FilePath);
                var part1 = new FileInfo(Path.Combine(file.Directory.FullName, $"{file.Name.Replace(file.Extension, "")}_part1{file.Extension}"));
                var part2 = new FileInfo(Path.Combine(file.Directory.FullName, $"{file.Name.Replace(file.Extension, "")}_part2{file.Extension}"));
                await SplitVideoAsync(
                file.FullName,
                part1.FullName,
                part2.FullName,
                request.SplitAt);

                response.Data = new
                {
                    Part1 = part1.FullName,
                    Part2 = part2.FullName
                };
                return Ok(response);
            }
            catch (Exception e)
            {
                response.Errors.Add(new BaseResponseError()
                {
                    ErrorCode = "InternalServerError",
                    Message = e.Message
                });
                return BadRequest(response);
            }
        }

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

            var filesPath = request.FilesPath.ToList().OrderBy(name => name).ToList();

            foreach (var filePath in filesPath)
            {
                var video = new FileInfo(filePath);
                var baseResponse = new BaseResponse();

                try
                {
                    var folder = StorageOptions.Folders
                        .Where(x => x.Store)
                        .Select(x => new DirectoryInfo(x.Folder))
                        .FirstOrDefault(dir => new DriveInfo(dir.Root.FullName).AvailableFreeSpace > (video.Length * 2));

                    if (folder == null)
                    {
                        throw new Exception("No drive with sufficient free space.");
                    }

                    if (validExtensions.All(extension => extension != video.Extension))
                    {
                        throw new Exception("Invalid file type.");
                    }

                    var folderName = Path.Combine(folder.FullName, video.Name.Replace(video.Extension, "").SanitizeFolderName());
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

                    var videoData = new VideoReponse(
                        Path.GetFileName(folderName),
                        $"/Streaming/Hls/{Path.GetFileName(folderName)}/playlist.m3u8"
                        )
                    {
                        Tracks = Directory.GetDirectories(folderName)
                                .Where(file => Regex.IsMatch(file, @"audio_track_\d+$"))
                                .SelectMany(d => Directory.GetFiles(d))
                                .Where(file => file.EndsWith(".m3u8"))
                                .Select(file => Regex.Replace(file, @"^.*.hls", "/Streaming/Hls", RegexOptions.IgnoreCase).Replace("\\", "/"))
                                .ToList(),
                        Legends = Directory.GetDirectories(folderName)
                            .Where(file => Regex.IsMatch(file, @"subtitles"))
                            .SelectMany(d => Directory.GetFiles(d))
                            .Where(file => file.EndsWith(".srt"))
                            .Select(file => Regex.Replace(file, @"^.*.hls", "/Streaming/Hls", RegexOptions.IgnoreCase).Replace("\\", "/"))
                            .ToList()
                    };

                    if (request.RegiterInJson)
                    {
                        var videos = await GetVideosAsync(folder.FullName);
                        videos.Add(videoData);
                        videos = videos.OrderBy(x => x.Name).ToList();
                        await SaveVideosAsync(folder.FullName, videos);
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
            var videos = new List<VideoReponse>();
            foreach (var storageFolder in StorageOptions.Folders)
            {
                videos.AddRange(await GetVideosAsync(storageFolder.Folder));    
            }
            return Ok(videos.OrderBy(x => x.Name).ToList());
        }

        [HttpGet("Hls/{folder}/{fileName}")]
        [SwaggerResponse(200)]
        [SwaggerResponse(400)]
        public async Task<IActionResult> HlsAsync([FromRoute] string folder, [FromRoute] string fileName)
        {
            var videoPath = await GetVideoPathAsync(folder);
            var file = new FileInfo(Path.Combine(videoPath, fileName));

            if (!System.IO.File.Exists(file.FullName))
                return BadRequest();

            return File(System.IO.File.ReadAllBytes(file.FullName), file.GetContentType(), file.Name);
        }
        
        [HttpGet("Hls/{folder}/{subFolder}/{fileName}")]
        [SwaggerResponse(200)]
        [SwaggerResponse(400)]
        public async Task<IActionResult> HlsAsync([FromRoute] string folder, [FromRoute] string subFolder, [FromRoute] string fileName)
        {
            var videoPath = await GetVideoPathAsync(folder);
            var file = new FileInfo(Path.Combine(videoPath, subFolder, fileName));

            if (!System.IO.File.Exists(file.FullName))
                return BadRequest();

            return File(System.IO.File.ReadAllBytes(file.FullName), file.GetContentType(), file.Name);
        }
    }
}
