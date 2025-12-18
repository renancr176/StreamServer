namespace StreamServer.Models.Responses;

public class VideoReponse
{
    public string Name { get; set; }
    public string Playlist { get; set; }
    public IEnumerable<string> Tracks { get; set; } = new List<string>();
    public IEnumerable<string> Legends { get; set; } = new List<string>();

    public VideoReponse(string name, string playlist)
    {
        Name = name;
        Playlist = playlist;
    }
}