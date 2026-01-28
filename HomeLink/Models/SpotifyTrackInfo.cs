namespace HomeLink.Models;

public class SpotifyTrackInfo
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string AlbumCoverUrl { get; set; } = string.Empty;
    public long ProgressMs { get; set; }
    public long DurationMs { get; set; }
    public string SpotifyUri { get; set; } = string.Empty;
    public string ScannableCodeUrl { get; set; } = string.Empty;
    public bool IsPlaying { get; set; }
}