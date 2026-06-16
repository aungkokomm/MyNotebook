using MyNotebook.Core.Services;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MyNotebook.App.Services;

/// <summary>
/// OCR via Windows.Media.Ocr. The engine is created from the user's profile
/// languages; if no OCR language pack is installed, IsAvailable is false and
/// callers should surface a "install an OCR language pack" hint.
///
/// Images whose longest edge exceeds MaxEdge are downscaled first (OCR accuracy
/// does not benefit beyond this and very large bitmaps are slow / can fail).
/// </summary>
public sealed class WindowsOcrService : IOcrService
{
    private const uint MaxEdge = 4096;
    private readonly OcrEngine? _engine;

    public WindowsOcrService()
    {
        // Try the user's languages first, then fall back to any available OCR language.
        _engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (_engine is null)
        {
            foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
            {
                _engine = OcrEngine.TryCreateFromLanguage(lang);
                if (_engine is not null) break;
            }
        }
    }

    public bool IsAvailable => _engine is not null;

    public string? EngineLanguage => _engine?.RecognizerLanguage?.DisplayName;

    public async Task<string> RecognizeAsync(string imagePath, CancellationToken ct = default)
    {
        if (_engine is null) return "";

        var file = await StorageFile.GetFileFromPathAsync(imagePath);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);

        SoftwareBitmap bitmap = await DecodeMaybeResizedAsync(decoder);
        ct.ThrowIfCancellationRequested();

        var result = await _engine.RecognizeAsync(bitmap);
        bitmap.Dispose();
        return result?.Text ?? "";
    }

    /// <summary>Decode the bitmap, downscaling proportionally if the longest edge > MaxEdge.</summary>
    private static async Task<SoftwareBitmap> DecodeMaybeResizedAsync(BitmapDecoder decoder)
    {
        uint w = decoder.PixelWidth;
        uint h = decoder.PixelHeight;
        uint longest = Math.Max(w, h);

        if (longest <= MaxEdge)
            return await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        double scale = (double)MaxEdge / longest;
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Round(w * scale),
            ScaledHeight = (uint)Math.Round(h * scale),
            InterpolationMode = BitmapInterpolationMode.Linear, // bilinear
        };

        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);
    }
}
