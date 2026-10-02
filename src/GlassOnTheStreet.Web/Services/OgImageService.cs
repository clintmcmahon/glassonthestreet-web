using SkiaSharp;

namespace GlassOnTheStreet.Web.Services;

/// <param name="Big">The headline number, drawn large in the site accent.</param>
public record OgCard(string Title, string Big, string BigLabel, string? Sub, string Footer);

/// <summary>
/// Draws the 1200x630 share image (the picture a link shows on Reddit, X, Slack and in news
/// feeds) with the page's own headline number on it. Manrope is bundled under the SIL Open
/// Font License. Callers fall back to the static image if drawing fails, for example if the
/// native Skia library can't load on the host.
/// </summary>
public class OgImageService
{
    public const int Width = 1200;
    public const int Height = 630;

    private static readonly SKColor Paper = SKColor.Parse("#fafaf9");
    private static readonly SKColor Ink = SKColor.Parse("#18181b");
    private static readonly SKColor Soft = SKColor.Parse("#71717a");
    private static readonly SKColor Accent = SKColor.Parse("#ea580c");

    // Static instances (Medium 500 and Bold 700) cut from Manrope's variable font; a variable
    // font would render at its lightest weight here.
    private static readonly Lazy<SKTypeface> Regular = new(() => Load("Manrope-Medium.ttf"));
    private static readonly Lazy<SKTypeface> BoldFace = new(() => Load("Manrope-Bold.ttf"));

    private static SKTypeface Load(string file) =>
        SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "Data", "fonts", file)) ?? SKTypeface.Default;

    public byte[] Render(OgCard card)
    {
        using var bitmap = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(Paper);

        using var bar = new SKPaint { Color = Accent, IsAntialias = true };
        canvas.DrawRect(0, 0, 18, Height, bar);

        const float left = 76;
        const float maxWidth = Width - left - 72;

        DrawText(canvas, "GLASS ON THE STREET", left, 84, 24, Soft, bold: true);

        var titleLines = Wrap(card.Title, 60, maxWidth, maxLines: 2);
        var y = 168f;
        foreach (var line in titleLines)
        {
            DrawText(canvas, line, left, y, 60, Ink, bold: true);
            y += 70;
        }

        // Shrink the big number if it would run off the card.
        var bigSize = 168f;
        while (bigSize > 90 && Measure(card.Big, bigSize, bold: true) > maxWidth)
        {
            bigSize -= 6;
        }

        DrawText(canvas, card.Big, left, 420, bigSize, Accent, bold: true);
        DrawText(canvas, card.BigLabel, left, 474, 36, Ink, bold: false);

        if (card.Sub is not null)
        {
            var subLines = Wrap(card.Sub, 30, maxWidth, maxLines: 2);
            var subY = 526f;
            foreach (var line in subLines)
            {
                DrawText(canvas, line, left, subY, 30, Soft, bold: false);
                subY += 38;
            }
        }

        DrawText(canvas, card.Footer, left, Height - 30, 22, Soft, bold: false);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static SKFont MakeFont(float size, bool bold) =>
        new(bold ? BoldFace.Value : Regular.Value, size) { Edging = SKFontEdging.SubpixelAntialias };

    private static float Measure(string text, float size, bool bold)
    {
        using var font = MakeFont(size, bold);
        return font.MeasureText(text);
    }

    private static void DrawText(SKCanvas canvas, string text, float x, float y, float size, SKColor color, bool bold)
    {
        using var font = MakeFont(size, bold);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);
    }

    private static List<string> Wrap(string text, float size, float maxWidth, int maxLines)
    {
        var lines = new List<string>();
        var current = "";
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var candidate = current.Length == 0 ? words[i] : current + " " + words[i];
            if (Measure(candidate, size, bold: size >= 50) <= maxWidth || current.Length == 0)
            {
                current = candidate;
                continue;
            }

            lines.Add(current);
            current = words[i];
            if (lines.Count == maxLines - 1)
            {
                // Last line takes everything left, trimmed with an ellipsis if it still doesn't fit.
                current = string.Join(' ', words.Skip(i));
                break;
            }
        }

        if (current.Length > 0)
        {
            var fit = current;
            var truncated = false;
            while (fit.Length > 3 && Measure(fit + (truncated ? "..." : ""), size, bold: size >= 50) > maxWidth)
            {
                fit = fit[..^1];
                truncated = true;
            }

            lines.Add(truncated ? fit.TrimEnd() + "..." : fit);
        }

        return lines;
    }
}
