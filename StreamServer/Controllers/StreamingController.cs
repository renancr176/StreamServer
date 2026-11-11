using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StreamServer.Extensions;
using StreamServer.Models.Requests;
using StreamServer.Models.Responses;
using StreamServer.Options;
using StreamServer.Services;
using Swashbuckle.AspNetCore.Annotations;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Exceptions;

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

        private static List<string> VideoFolders { get; set; } = new List<string>();

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
            TimeSpan splitAt,
            bool exact)
        {
            // Obtém a duração total do arquivo
            IMediaInfo mediaInfo = await FFmpeg.GetMediaInfo(inputPath);

            TimeSpan duracaoTotal = mediaInfo.Duration;

            if (splitAt >= duracaoTotal)
                throw new ArgumentException(
                    $"The video has only {duracaoTotal} of duration.");

            var input = $"-i \"{inputPath}\"";

            if (!exact)
            {
                // Cópia (sem recodificar): leva segundos e não perde qualidade. A parte 2 precisa começar num
                // quadro-chave, então as duas partes são cortadas no último quadro-chave antes de "splitAt"
                // (sem repetir nem perder trechos).
                var cutAt = await LastKeyframeAtOrBeforeAsync(inputPath, splitAt) ?? splitAt;
                if (cutAt <= TimeSpan.Zero)
                    throw new ArgumentException($"There is no keyframe before {splitAt}; use \"exact\": true.");

                await FFmpeg.Conversions.New()
                    .AddParameter(input, ParameterPosition.PreInput)
                    .AddParameter($"-t {Seconds(cutAt)} -map 0 -c copy \"{outputPart1}\"")
                    .SetOverwriteOutput(true)
                    .Start();

                // "-ss" antes do "-i" posiciona direto no quadro-chave (+1 ms para não voltar ao anterior por arredondamento).
                await FFmpeg.Conversions.New()
                    .AddParameter($"-ss {Seconds(cutAt + TimeSpan.FromMilliseconds(1))}", ParameterPosition.PreInput)
                    .AddParameter(input, ParameterPosition.PreInput)
                    .AddParameter($"-map 0 -c copy -avoid_negative_ts make_zero \"{outputPart2}\"")
                    .SetOverwriteOutput(true)
                    .Start();

                return;
            }

            // Corte exato: recodifica o vídeo (GPU NVIDIA quando disponível); áudio e legendas são copiados.
            var gpu = await NvencAvailableAsync();
            var outputParameters = $"-map 0:v:0 -map 0:a? -map 0:s? {await ReencodeVideoParametersAsync()} -c:a copy -c:s copy";

            var part1 = FFmpeg.Conversions.New();
            if (gpu)
                part1.AddParameter("-hwaccel cuda", ParameterPosition.PreInput);
            await part1
                .AddParameter(input, ParameterPosition.PreInput)
                .AddParameter($"-t {Seconds(splitAt)} {outputParameters} \"{outputPart1}\"")
                .SetOverwriteOutput(true)
                .Start();

            var part2 = FFmpeg.Conversions.New();
            if (gpu)
                part2.AddParameter("-hwaccel cuda", ParameterPosition.PreInput);
            await part2
                .AddParameter($"-ss {Seconds(splitAt)}", ParameterPosition.PreInput)
                .AddParameter(input, ParameterPosition.PreInput)
                .AddParameter($"{outputParameters} \"{outputPart2}\"")
                .SetOverwriteOutput(true)
                .Start();
        }

        private static string Seconds(TimeSpan time) =>
            time.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);

        private static string FfprobePath =>
            Path.Combine(FFmpeg.ExecutablesPath ?? FFmpegService.FFmpegPath, "ffprobe.exe");

        /// <summary>
        /// Time of the last video keyframe at or before <paramref name="time"/> (looks at the previous 60 s).
        /// </summary>
        private static async Task<TimeSpan?> LastKeyframeAtOrBeforeAsync(string inputPath, TimeSpan time)
        {
            var from = time - TimeSpan.FromSeconds(60);
            if (from < TimeSpan.Zero)
                from = TimeSpan.Zero;

            using var process = Process.Start(new ProcessStartInfo(FfprobePath,
                $"-v error -select_streams v:0 -skip_frame nokey -read_intervals {Seconds(from)}%{Seconds(time + TimeSpan.FromMilliseconds(1))} " +
                $"-show_entries frame=best_effort_timestamp_time -of csv=p=0 \"{inputPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process == null)
                return null;

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await stderr;

            var keyframes = (await stdout)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => double.TryParse(line.TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    ? TimeSpan.FromSeconds(seconds)
                    : (TimeSpan?)null)
                .Where(t => t.HasValue && t.Value <= time)
                .ToList();

            return keyframes.Count == 0 ? null : keyframes.Max();
        }

        /// <summary>
        /// Videos can be joined without re-encoding when every file has the same video and audio formats.
        /// </summary>
        private static bool SameStreamLayout(IReadOnlyList<IMediaInfo> mediaInfos)
        {
            string Layout(IMediaInfo info)
            {
                var video = info.VideoStreams.First();
                var audios = info.AudioStreams.Select(a => $"{a.Codec}/{a.SampleRate}/{a.Channels}");
                return $"{video.Codec}/{video.Width}x{video.Height}/{video.PixelFormat}/{Math.Round(video.Framerate, 2)}|{string.Join("|", audios)}";
            }

            return mediaInfos.Select(Layout).Distinct().Count() == 1;
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
                request.SplitAt,
                request.Exact);

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

            if (request?.FilesPath == null || request.FilesPath.Count() < 2)
            {
                response.Errors.Add(new BaseResponseError()
                {
                    ErrorCode = "InvalidRequest",
                    Message = "Informe pelo menos dois vídeos."
                });
                return BadRequest(response);
            }

            if (request.FilesPath.Any(string.IsNullOrWhiteSpace))
            {
                response.Errors.Add(new BaseResponseError()
                {
                    ErrorCode = "InvalidRequest",
                    Message = "Os caminhos dos vídeos não podem ser vazios."
                });
                return BadRequest(response);
            }

            var files = request.FilesPath.Select(path => new FileInfo(path)).ToList();

            if (files.Any(file => !System.IO.File.Exists(file.FullName)))
            {
                response.Errors.Add(new BaseResponseError()
                {
                    ErrorCode = "InvalidRequest",
                    Message = "Um ou mais arquivos não foram encontrados."
                });
                return BadRequest(response);
            }

            if (files.Any(file => !System.IO.File.Exists(file.FullName)))
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
                var outputPath = Path.Combine(files.First().Directory.FullName, $"joined_video_{DateTime.Now.ToFileTime()}.mp4");

                // Obtém informações sobre as trilhas de cada vídeo.
                var mediaInfos = new List<IMediaInfo>();

                foreach (var file in files)
                {
                    mediaInfos.Add(await FFmpeg.GetMediaInfo(file.FullName));
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
                    var normalizedPath = Path.GetFullPath(file.FullName)
                        .Replace("\\", "/")
                        .Replace("'", "'\\''");

                    return $"file '{normalizedPath}'";
                });

                await System.IO.File.WriteAllLinesAsync(
                    listPath,
                    concatLines);

                // Mesmos formatos (ex.: partes geradas pelo Split): junta copiando, em segundos e sem perder qualidade,
                // no mesmo tipo de arquivo de origem. Formatos diferentes: recodifica (GPU NVIDIA quando disponível).
                var copy = SameStreamLayout(mediaInfos);
                var gpu = !copy && await NvencAvailableAsync();

                if (copy)
                    outputPath = Path.ChangeExtension(outputPath, files.First().Extension);

                var conversion = FFmpeg.Conversions.New();

                // Mantém somente a trilha de áudio selecionada ou todas.
                var audioTracks = request.UseAudioTrack.HasValue
                    ? new List<int> { request.UseAudioTrack.Value }
                    : Enumerable.Range(0, audioTrackCount).ToList();

                if (copy)
                {
                    // Concat demuxer: lê os arquivos em sequência, como se fossem um só.
                    conversion
                        .AddParameter($"-f concat -safe 0 -i \"{listPath}\"", ParameterPosition.PreInput)
                        .AddParameter($"-map 0:v:0 {string.Join(" ", audioTracks.Select(t => $"-map 0:a:{t}"))} -c copy");
                }
                else
                {
                    // Formatos diferentes (ex.: H.264 + XviD, outra resolução): o concat demuxer não troca de codec no
                    // meio do arquivo. O filtro concat decodifica cada vídeo e os iguala ao primeiro (resolução com
                    // barras pretas se preciso, fps, áudio) antes de juntar.
                    foreach (var file in files)
                    {
                        // Decodifica na GPU também (o FFmpeg volta para a CPU nos formatos que ela não suporta). Antes de cada "-i".
                        conversion.AddParameter($"{(gpu ? "-hwaccel cuda " : "")}-i \"{file.FullName}\"", ParameterPosition.PreInput);
                    }

                    var first = mediaInfos[0].VideoStreams.First();
                    var width = first.Width / 2 * 2;
                    var height = first.Height / 2 * 2;
                    var fps = (first.Framerate > 0 ? first.Framerate : 24).ToString("0.###", CultureInfo.InvariantCulture);
                    var channels = audioTracks
                        .Select(t => mediaInfos[0].AudioStreams.ElementAt(t).Channels >= 6 ? "5.1" : "stereo")
                        .ToList();

                    var filter = new StringBuilder();
                    var segments = new StringBuilder();
                    for (var i = 0; i < files.Count; i++)
                    {
                        filter.Append($"[{i}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease," +
                                      $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps},format=yuv420p[v{i}];");
                        segments.Append($"[v{i}]");

                        for (var a = 0; a < audioTracks.Count; a++)
                        {
                            filter.Append($"[{i}:a:{audioTracks[a]}]aresample=48000,aformat=channel_layouts={channels[a]}[a{i}_{a}];");
                            segments.Append($"[a{i}_{a}]");
                        }
                    }

                    var outputs = "[v]" + string.Concat(audioTracks.Select((_, a) => $"[a{a}]"));
                    filter.Append($"{segments}concat=n={files.Count}:v=1:a={audioTracks.Count}{outputs}");

                    var filterPath = Path.Combine(tempDirectory, "filter.txt");
                    await System.IO.File.WriteAllTextAsync(filterPath, filter.ToString());

                    conversion.AddParameter(
                        $"-filter_complex_script \"{filterPath}\" -map \"[v]\" " +
                        string.Concat(audioTracks.Select((_, a) => $"-map \"[a{a}]\" ")) +
                        $"{await ReencodeVideoParametersAsync()} -c:a aac -b:a 192k -movflags +faststart");
                }

                conversion.SetOutput(outputPath);

                await conversion.Start();

                response.Data = new
                {
                    Output = outputPath,
                    Mode = copy ? "copy" : gpu ? "gpu" : "cpu"
                };

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
                            processVideoArguments.Append($"-map 0:v:0 -map 0:a? -codec: copy {videoParameters} -sn -hls_time 10 -hls_playlist_type vod \"{Path.Combine(folderName, "playlist.m3u8")}\"");
                        }

                        return processVideoArguments.ToString();
                    }

                    // The input goes in as "-i" instead of AddStream(): AddStream also adds "-map" for every
                    // stream of the file, which put the cover image (mjpeg) and a second copy of the video in
                    // the HLS segments. The "-map" options in BuildArguments choose exactly what goes out.
                    var input = $"-i \"{video.FullName}\"";

                    // Only H.264/HEVC can be copied into HLS. Other codecs (e.g. XviD/mpeg4 in AVI) aren't played
                    // by the app, and AVI packets have no pts, so the copy fails with "first pts and dts value must be set".
                    var videoCodec = mediaInfo.VideoStreams.FirstOrDefault()?.Codec?.ToLowerInvariant();
                    var copied = false;

                    if (videoCodec is "h264" or "hevc" or "h265")
                    {
                        try
                        {
                            await FFmpeg.Conversions.New()
                                .AddParameter(input, ParameterPosition.PreInput)
                                .AddParameter(BuildArguments(CopyVideoBitstreamFilter(videoCodec)))
                                .Start();

                            // Copied segments that can't be decoded on their own hang the player on resume/seek.
                            copied = await HlsSegmentsDecodableAsync(folderName);
                        }
                        catch (ConversionException)
                        {
                            // Copy failed: falls back to re-encoding below.
                        }
                    }

                    // Generate everything again re-encoding the video.
                    if (!copied)
                    {
                        foreach (var file in Directory.GetFiles(folderName))
                            System.IO.File.Delete(file);

                        var reencode = FFmpeg.Conversions.New();

                        // Creates the timestamps missing from AVI packets.
                        reencode.AddParameter("-fflags +genpts", ParameterPosition.PreInput);

                        // NVIDIA GPU detected: decode on the GPU too (FFmpeg falls back to the CPU for unsupported formats).
                        // Must come before "-i".
                        if (await NvencAvailableAsync())
                            reencode.AddParameter("-hwaccel cuda", ParameterPosition.PreInput);

                        await reencode
                            .AddParameter(input, ParameterPosition.PreInput)
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
