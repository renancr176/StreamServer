using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StreamServer.Extensions;
using StreamServer.Models.Requests;
using StreamServer.Models.Responses;
using StreamServer.Options;
using StreamServer.Services;
using Swashbuckle.AspNetCore.Annotations;
using System.Diagnostics;
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

        /// <summary>
        /// Copy mode: repeats SPS/PPS (VPS for HEVC) before every keyframe, so each HLS segment
        /// can be decoded on its own (resume/seek). Without it, sources that carry them only at the
        /// start produce segments that never start playing.
        /// </summary>
        private static string CopyVideoBitstreamFilter(string? codec) => codec?.ToLowerInvariant() switch
        {
            "h264" => "-bsf:v h264_mp4toannexb,dump_extra=freq=keyframe",
            "hevc" or "h265" => "-bsf:v hevc_mp4toannexb,dump_extra=freq=keyframe",
            _ => ""
        };

        private static bool? _nvencAvailable;

        /// <summary>
        /// Tests once whether the NVIDIA encoder (NVENC) works on this machine.
        /// </summary>
        private static async Task<bool> NvencAvailableAsync()
        {
            if (_nvencAvailable.HasValue)
                return _nvencAvailable.Value;

            try
            {
                await FFmpeg.Conversions.New()
                    .AddParameter("-f lavfi -i color=c=black:s=1280x720:r=24:d=0.5 -c:v h264_nvenc -f null -")
                    .Start();
                _nvencAvailable = true;
            }
            catch (Exception e)
            {
                _nvencAvailable = false;
            }

            return _nvencAvailable.Value;
        }

        /// <summary>
        /// Re-encode mode (fallback when the copied segments can't be decoded on their own):
        /// H.264 with a keyframe every 2 s; the MPEG-TS output carries SPS/PPS on every keyframe.
        /// Uses the NVIDIA GPU (h264_nvenc) when available, otherwise the CPU (libx264).
        /// </summary>
        private static async Task<string> ReencodeVideoParametersAsync()
        {
            const string keyFrames = "-force_key_frames \"expr:gte(t,n_forced*2)\"";

            return await NvencAvailableAsync()
                ? $"-c:v h264_nvenc -preset p5 -tune hq -rc vbr -cq 21 -b:v 0 -maxrate 8M -bufsize 16M -spatial-aq 1 -profile:v high -pix_fmt yuv420p -forced-idr 1 {keyFrames}"
                : $"-c:v libx264 -preset medium -crf 20 -maxrate 8M -bufsize 16M -profile:v high -pix_fmt yuv420p -sc_threshold 0 {keyFrames}";
        }

        /// <summary>
        /// Checks that segments other than the first decode on their own (width found by ffprobe).
        /// </summary>
        private static async Task<bool> HlsSegmentsDecodableAsync(string folderName)
        {
            var playlist = Path.Combine(folderName, "playlist.m3u8");
            if (!System.IO.File.Exists(playlist))
                return false;

            var segments = (await System.IO.File.ReadAllLinesAsync(playlist))
                .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"))
                .ToList();

            if (segments.Count < 2)
                return true;

            var samples = new[] { 1, segments.Count / 2, segments.Count - 2 }
                .Where(index => index > 0)
                .Distinct();

            var ffprobe = Path.Combine(FFmpeg.ExecutablesPath ?? FFmpegService.FFmpegPath, "ffprobe.exe");

            foreach (var index in samples)
            {
                // Decodes the first 2 s: a broken segment reports "0,N/A", a good one "1920,48".
                using var process = Process.Start(new ProcessStartInfo(ffprobe,
                    $"-v error -select_streams v:0 -read_intervals %+2 -count_frames -show_entries stream=width,nb_read_frames -of csv=p=0 \"{Path.Combine(folderName, segments[index])}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process == null)
                    return false;

                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                var values = ((await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")
                    .Trim()
                    .Split(',');
                await stderr;

                if (values.Length < 2
                    || !int.TryParse(values[0], out var width) || width <= 0
                    || !int.TryParse(values[1], out var frames) || frames <= 0)
                    return false;
            }

            return true;
        }

        private static Dictionary<string, IEnumerable<string>> StorageFolders { get; set; } =
            new Dictionary<string, IEnumerable<string>>();

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

        [HttpPatch("Join")]
        [SwaggerResponse(200, Type = typeof(BaseResponse<BaseResponse>))]
        [SwaggerResponse(400, Type = typeof(BaseResponse<BaseResponse>))]
        public async Task<IActionResult> JoinVideoAsync([FromBody] JoinVideosRequest request)
        {
            var response = new BaseResponse<object>();

            if (request.FilesPath == null || request.FilesPath.Count() < 2)
            {
                response.Errors.Add(new BaseResponseError()
                {
                    ErrorCode = "InvalidRequest",
                    Message = "Informe pelo menos dois vídeos."
                });
                return BadRequest(response);
            }

            var files = request.FilesPath.ToList();

            if (files.Any(string.IsNullOrWhiteSpace))
            {
                response.Errors.Add(new BaseResponseError()
                {
                    ErrorCode = "InvalidRequest",
                    Message = "Os caminhos dos vídeos não podem ser vazios."
                });
                return BadRequest(response);
            }

            if (files.Any(path => !System.IO.File.Exists(path)))
            {
                response.Errors.Add(new BaseResponseError()
                {
                    ErrorCode = "InvalidRequest",
                    Message = "Um ou mais arquivos não foram encontrados."
                });
                return BadRequest(response);
            }

            if (files.Any(path => !System.IO.File.Exists(path)))
            {
                response.Errors.Add(new BaseResponseError()
                {
                    ErrorCode = "InvalidRequest",
                    Message = "Um ou mais arquivos não foram encontrados."
                });
                return BadRequest(response);
            }

            var tempDirectory = Path.Combine(
                Path.GetTempPath(),
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDirectory);

            try
            {
                var listPath = Path.Combine(tempDirectory, "videos.txt");
                var outputPath = Path.Combine(tempDirectory, "resultado.mp4");

                // Obtém informações sobre as trilhas de cada vídeo.
                var mediaInfos = new List<IMediaInfo>();

                foreach (var file in files)
                {
                    mediaInfos.Add(await FFmpeg.GetMediaInfo(file));
                }

                var audioTrackCounts = mediaInfos
                    .Select(info => info.AudioStreams.Count())
                    .ToList();

                // Verifica se todos os vídeos possuem uma trilha de vídeo.
                if (mediaInfos.Any(info => !info.VideoStreams.Any()))
                {
                    return BadRequest(new
                    {
                        erro = "Todos os arquivos devem conter uma trilha de vídeo."
                    });
                }

                // O concat demuxer exige estruturas de streams compatíveis.
                if (audioTrackCounts.Distinct().Count() > 1)
                {
                    return BadRequest(new
                    {
                        erro = "Os vídeos possuem quantidades diferentes de trilhas de áudio.",
                        trilhasPorVideo = audioTrackCounts
                    });
                }

                var audioTrackCount = audioTrackCounts.First();

                if (request.UseAudioTrack.HasValue &&
                    request.UseAudioTrack.Value >= audioTrackCount)
                {
                    return BadRequest(new
                    {
                        erro = "A trilha de áudio selecionada não existe em todos os vídeos.",
                        trilhasDisponiveis = audioTrackCount
                    });
                }

                // Cria o arquivo de lista utilizado pelo FFmpeg.
                var concatLines = files.Select(file =>
                {
                    var normalizedPath = Path.GetFullPath(file)
                        .Replace("\\", "/")
                        .Replace("'", "'\\''");

                    return $"file '{normalizedPath}'";
                });

                await System.IO.File.WriteAllLinesAsync(
                    listPath,
                    concatLines);

                var conversion = FFmpeg.Conversions.New()
                    .AddParameter(
                        $"-f concat -safe 0 -i \"{listPath}\"",
                        ParameterPosition.PreInput);

                // Seleciona a trilha de vídeo principal.
                conversion.AddParameter("-map 0:v:0");

                if (request.UseAudioTrack.HasValue)
                {
                    // Mantém somente a trilha de áudio selecionada.
                    conversion.AddParameter(
                        $"-map 0:a:{request.UseAudioTrack.Value}");
                }
                else
                {
                    // Mantém todas as trilhas de áudio.
                    conversion.AddParameter("-map 0:a?");
                }

                conversion
                    .AddParameter(
                        "-c:v libx264 -preset veryfast -crf 23 " +
                        "-c:a aac -b:a 192k -movflags +faststart")
                    .SetOutput(outputPath);

                await conversion.Start();

                // Limpa os arquivos temporários após o envio da resposta.
                HttpContext.Response.OnCompleted(() =>
                {
                    try
                    {
                        if (Directory.Exists(tempDirectory))
                            Directory.Delete(tempDirectory, true);
                    }
                    catch
                    {
                        // A limpeza poderá ser feita posteriormente.
                    }

                    return Task.CompletedTask;
                });

                return Ok(response);
            }
            catch (Exception e)
            {
                try
                {
                    if (Directory.Exists(tempDirectory))
                        Directory.Delete(tempDirectory, true);
                }
                catch
                {
                    // Ignora falhas na limpeza.
                }

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

                    string BuildArguments(string videoParameters)
                    {
                        var processVideoArguments = new StringBuilder();

                        if (request.ExtractAudioTracks)
                        {
                            processVideoArguments.Append(
                                $"-map 0:v:0 -codec: copy {videoParameters} -an -sn -hls_time 10 -hls_playlist_type vod \"{Path.Combine(folderName, "playlist.m3u8")}\"");

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
                            processVideoArguments.Append($"-codec: copy {videoParameters} -sn -hls_time 10 -hls_playlist_type vod \"{Path.Combine(folderName, "playlist.m3u8")}\"");
                        }

                        return processVideoArguments.ToString();
                    }

                    await FFmpeg.Conversions.New()
                        .AddStream(mediaInfo.Streams)
                        .AddParameter(BuildArguments(CopyVideoBitstreamFilter(mediaInfo.VideoStreams.FirstOrDefault()?.Codec)))
                        .Start();

                    // Copied segments that can't be decoded on their own hang the player on resume/seek:
                    // generate everything again re-encoding the video.
                    if (!await HlsSegmentsDecodableAsync(folderName))
                    {
                        foreach (var file in Directory.GetFiles(folderName))
                            System.IO.File.Delete(file);

                        var reencode = FFmpeg.Conversions.New();

                        // NVIDIA GPU detected: decode on the GPU too (FFmpeg falls back to the CPU for unsupported formats).
                        if (await NvencAvailableAsync())
                            reencode.AddParameter("-hwaccel cuda", ParameterPosition.PreInput);

                        await reencode
                            .AddStream(mediaInfo.Streams)
                            .AddParameter(BuildArguments(await ReencodeVideoParametersAsync()))
                            .Start();

                        if (!await HlsSegmentsDecodableAsync(folderName))
                            throw new Exception("HLS segments can't be decoded on their own, even after re-encoding the video.");
                    }

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
