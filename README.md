# Streaming Server

This is a pilot project for video streaming.

## Urls

[Api Endpoints](http://localhost:5133/swagger)

[Video Player Page](http://localhost:5133/index.html)

## Convert Videos to HLS format

Use the POST endpoint:
http://localhost:5133/Streaming/ProcessVideo
Request body:
```json
{
  "filePath": "Video path to be converted, example: C:\\VideosFolder\\Video.mkv",
  "extractAudioTracks": true, //For videos with more than one audio track.
  "deletedFileAfterProcess": true
}
```
