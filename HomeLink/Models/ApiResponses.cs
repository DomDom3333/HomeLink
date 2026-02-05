namespace HomeLink.Models;

public class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
}

public class BitmapResponse
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int BytesPerLine { get; set; }
    public string Data { get; set; } = string.Empty;
}

public class RenderBitmapResponse
{
    public bool Success { get; set; }
    public BitmapResponse Bitmap { get; set; } = new();
}

public class RenderBitmapWithDitherResponse
{
    public bool Success { get; set; }
    public bool Dithered { get; set; }
    public BitmapResponse Bitmap { get; set; } = new();
}
