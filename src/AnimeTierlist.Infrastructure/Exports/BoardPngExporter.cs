using System.Collections.Immutable;
using System.Globalization;
using AnimeTierlist.Application.Assets;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Assets;
using SkiaSharp;

namespace AnimeTierlist.Infrastructure.Exports;

public readonly record struct LayoutRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;

    public float Bottom => Y + Height;
}

/// <summary>One wrapped title line, with its left edge, baseline, and measured width.</summary>
public sealed record LayoutLine(string Text, float X, float Baseline, float Width);

public sealed record LayoutCell(LiveChartId Item, string Title, AssetHash? Cover, LayoutRect CoverBox, ImmutableArray<LayoutLine> TitleLines, int CopyNumber = 1);

/// <summary>One exported row: a tier, or the unranked pool when <see cref="Color"/> is null.</summary>
public sealed record LayoutRow(string Label, TierColor? Color, LayoutRect Bounds, LayoutRect LabelBox, ImmutableArray<LayoutLine> LabelLines, ImmutableArray<LayoutCell> Cells);

public sealed record BoardPngLayout(int Width, int Height, string Heading, string Scope, ImmutableArray<LayoutRow> Rows)
{
    public IEnumerable<LayoutCell> Cells => Rows.SelectMany(r => r.Cells);
}

/// <summary>
/// Renders a saved board revision to PNG. The layout depends only on the board, the chosen scope, the fixed export
/// width, and the installed fonts; never on the window, scroll position, search filter, or display scaling.
/// </summary>
public static class BoardPngExporter
{
    public const int DefaultWidth = 1600;
    public const long MaxPixels = 40_000_000;

    private const float Margin = 24;
    private const float LabelWidth = 160;
    private const float CoverWidth = 120;
    private const float CoverHeight = 170;
    private const float CellWidth = 136;
    private const float Gap = 10;
    private const float RowPadding = 8;
    private const float TitleSize = 14;
    private const float TitleLineHeight = 18;
    private const float LabelSize = 22;
    private const float LabelLineHeight = 28;

    /// <summary>Computes the deterministic layout. Missing covers still get a box; validation happens on export.</summary>
    public static BoardPngLayout Layout(BoardState board, bool includeUnranked, int width = DefaultWidth)
    {
        ArgumentNullException.ThrowIfNull(board);
        using var text = new TextSet();
        var entries = board.Entries.ToDictionary(e => e.Key);
        var sections = board.Tiers.Select(t => (t.Name, (TierColor?)t.Color, board.Placements[t.Id])).ToList();
        if (includeUnranked)
        {
            sections.Add(("Unranked", null, board.Unranked));
        }

        var contentLeft = Margin + LabelWidth + Gap;
        var columns = Math.Max(1, (int)((width - Margin - contentLeft + Gap) / (CellWidth + Gap)));
        var rows = ImmutableArray.CreateBuilder<LayoutRow>();
        var y = Margin + 64f;
        foreach (var (label, color, items) in sections)
        {
            var top = y;
            var cells = ImmutableArray.CreateBuilder<LayoutCell>();
            var cursor = top + RowPadding;
            for (var start = 0; start < Math.Max(1, items.Length); start += columns)
            {
                var line = items.Skip(start).Take(columns).ToList();
                var wrapped = line.Select(i => text.Wrap(entries[i].PinnedTitle, CellWidth - 4, TitleSize)).ToList();
                var lines = Math.Max(1, wrapped.Count == 0 ? 1 : wrapped.Max(w => w.Count));
                for (var k = 0; k < line.Count; k++)
                {
                    var x = contentLeft + (k * (CellWidth + Gap));
                    var box = new LayoutRect(x + ((CellWidth - CoverWidth) / 2), cursor, CoverWidth, CoverHeight);
                    var titleLines = wrapped[k].Select((t, n) =>
                    {
                        var w = text.Measure(t, TitleSize);
                        return new LayoutLine(t, x + ((CellWidth - w) / 2), cursor + CoverHeight + 4 + TitleSize + (n * TitleLineHeight), w);
                    }).ToImmutableArray();
                    var entry = entries[line[k]];
                    cells.Add(new LayoutCell(entry.Item, entry.PinnedTitle, entry.PinnedCover, box, titleLines, entry.CopyNumber));
                }

                cursor += CoverHeight + 6 + (lines * TitleLineHeight) + Gap;
            }

            var labelLines = text.Wrap(label, LabelWidth - 16, LabelSize, bold: true);
            var height = Math.Max(cursor - top - Gap + RowPadding, (labelLines.Count * LabelLineHeight) + (2 * RowPadding));
            var labelBox = new LayoutRect(Margin, top, LabelWidth, height);
            var labelTop = top + ((height - (labelLines.Count * LabelLineHeight)) / 2);
            var laidLabel = labelLines.Select((t, n) => new LayoutLine(t, Margin + 8, labelTop + LabelSize + (n * LabelLineHeight), text.Measure(t, LabelSize, bold: true))).ToImmutableArray();
            rows.Add(new LayoutRow(label, color, new LayoutRect(Margin, top, width - (2 * Margin), height), labelBox, laidLabel, cells.ToImmutable()));
            y = top + height + 6;
        }

        var scope = includeUnranked ? "All tiers and the unranked shows" : "All tiers (unranked shows omitted)";
        return new BoardPngLayout(width, (int)Math.Ceiling(y + Margin), board.Name, scope, rows.ToImmutable());
    }

    /// <summary>
    /// Validates every in-scope cover, checks the output size, renders, and installs the PNG atomically. On any
    /// failure no final file is written and an existing destination is untouched.
    /// </summary>
    public static async Task ExportAsync(BoardState board, IReadOnlyDictionary<AssetHash, AssetRecord> assets, AssetStore store, string destinationFile, bool includeUnranked, bool replaceExisting, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFile);
        var destination = Path.GetFullPath(destinationFile);
        if (!replaceExisting && File.Exists(destination))
        {
            throw new ExportException("The destination file already exists.");
        }

        var layout = Layout(board, includeUnranked);
        if ((long)layout.Width * layout.Height > MaxPixels)
        {
            throw new ExportException($"The image would be {layout.Width}×{layout.Height} pixels, more than the {MaxPixels:N0}-pixel limit. Export the tiers without the unranked shows, or split the board.");
        }

        var covers = new Dictionary<AssetHash, byte[]>();
        var affected = ImmutableArray.CreateBuilder<string>();
        foreach (var cell in layout.Cells)
        {
            var tag = cell.CopyNumber > 1 ? $"{cell.Title} #{cell.CopyNumber} [{cell.Item.Value}]" : $"{cell.Title} [{cell.Item.Value}]";
            if (cell.Cover is not { } hash || !assets.TryGetValue(hash, out var record))
            {
                affected.Add($"{tag} (no cover)");
                continue;
            }

            if (covers.ContainsKey(hash))
            {
                continue;
            }

            var bytes = await ReadValidatedAsync(store, record, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                affected.Add($"{tag} (cover file missing or damaged)");
                continue;
            }

            covers[hash] = bytes;
        }

        if (affected.Count > 0)
        {
            throw new ExportException(
                "These shows in the export have no usable cover, so no PNG was written. Fetch covers to restore exact originals, update the board to fill missing covers, " +
                "move the shows to Unranked and export without it, or exclude them and remove them through a reviewed board update.",
                affected.ToImmutable());
        }

        var png = await Task.Run(() => Render(layout, covers), cancellationToken).ConfigureAwait(false);
        await InstallAsync(png, destination, layout, replaceExisting, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadValidatedAsync(AssetStore store, AssetRecord record, CancellationToken cancellationToken)
    {
        try
        {
            if (await store.CheckAsync(record, cancellationToken).ConfigureAwait(false) != AssetAvailability.Available)
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(store.PathOf(record), cancellationToken).ConfigureAwait(false);
            return ImageValidator.Validate(bytes, ImageLimits.Default) is ImageValidation.Accepted accepted && accepted.Asset == record ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[] Render(BoardPngLayout layout, Dictionary<AssetHash, byte[]> covers)
    {
        using var text = new TextSet();
        var info = new SKImageInfo(layout.Width, layout.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new ExportException("The image could not be allocated.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var lightInk = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var muted = new SKPaint { Color = new SKColor(0x55, 0x55, 0x55), IsAntialias = true };
        using var outline = new SKPaint { Color = new SKColor(0x80, 0x80, 0x80), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        using var placeholder = new SKPaint { Color = new SKColor(0xEE, 0xEE, 0xEE) };
        using var content = new SKPaint { Color = new SKColor(0xF7, 0xF7, 0xF7) };

        text.Draw(canvas, layout.Heading, Margin, Margin + 28, 28, ink, bold: true);
        text.Draw(canvas, layout.Scope, Margin, Margin + 52, 15, muted);

        var images = new Dictionary<AssetHash, SKImage>();
        try
        {
            foreach (var row in layout.Rows)
            {
                canvas.DrawRect(Rect(row.Bounds), content);
                var color = row.Color is { } c ? new SKColor(c.Red, c.Green, c.Blue) : new SKColor(0xE4, 0xE4, 0xE4);
                using (var fill = new SKPaint { Color = color })
                {
                    canvas.DrawRect(Rect(row.LabelBox), fill);
                }

                canvas.DrawRect(Rect(row.LabelBox), outline);
                var labelPaint = row.Color is { } tierColor && tierColor.PreferredInk() == TierInk.White ? lightInk : ink;
                foreach (var line in row.LabelLines)
                {
                    text.Draw(canvas, line.Text, line.X, line.Baseline, LabelSize, labelPaint, bold: true);
                }

                foreach (var cell in row.Cells)
                {
                    canvas.DrawRect(Rect(cell.CoverBox), placeholder);
                    var hash = cell.Cover!.Value;
                    if (!images.TryGetValue(hash, out var image))
                    {
                        image = SKImage.FromEncodedData(covers[hash]) ?? throw new ExportException($"The cover of '{cell.Title}' could not be decoded.");
                        images[hash] = image;
                    }

                    canvas.DrawImage(image, Rect(Letterbox(image.Width, image.Height, cell.CoverBox)), new SKSamplingOptions(SKCubicResampler.Mitchell));
                    if (cell.CopyNumber > 1)
                    {
                        DrawCopyBadge(canvas, text, cell.CoverBox, cell.CopyNumber);
                    }

                    foreach (var line in cell.TitleLines)
                    {
                        text.Draw(canvas, line.Text, line.X, line.Baseline, TitleSize, ink);
                    }
                }
            }
        }
        finally
        {
            foreach (var image in images.Values)
            {
                image.Dispose();
            }
        }

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100) ?? throw new ExportException("The PNG could not be encoded.");
        return data.ToArray();
    }

    /// <summary>Draws a big bold red copy number centered in front of a duplicated cover with a crisp dark outline.</summary>
    private static void DrawCopyBadge(SKCanvas canvas, TextSet text, LayoutRect box, int copyNumber)
    {
        var label = copyNumber.ToString(CultureInfo.InvariantCulture);
        var size = label.Length switch
        {
            1 => 76f,
            2 => 62f,
            _ => 46f,
        };
        using var font = text.CreateBoldFont(size);
        font.MeasureText(label, out var textBounds);
        var x = box.X + ((box.Width - textBounds.Width) / 2f) - textBounds.Left;
        var y = box.Y + ((box.Height - textBounds.Height) / 2f) - textBounds.Top;

        using var stroke = new SKPaint
        {
            Color = new SKColor(0x0A, 0x0A, 0x0E),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(6f, size * 0.14f),
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        };
        using var fill = new SKPaint
        {
            Color = new SKColor(0xE5, 0x1E, 0x25),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        canvas.DrawText(label, x, y, SKTextAlign.Left, font, stroke);
        canvas.DrawText(label, x, y, SKTextAlign.Left, font, fill);
    }

    /// <summary>Fits the whole cover inside the box, centred, without cropping.</summary>
    private static LayoutRect Letterbox(int width, int height, LayoutRect box)
    {
        var scale = Math.Min(box.Width / width, box.Height / height);
        var w = width * scale;
        var h = height * scale;
        return new LayoutRect(box.X + ((box.Width - w) / 2), box.Y + ((box.Height - h) / 2), w, h);
    }

    private static SKRect Rect(LayoutRect r) => SKRect.Create(r.X, r.Y, r.Width, r.Height);

    private static async Task InstallAsync(byte[] png, string destination, BoardPngLayout layout, bool replaceExisting, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination) ?? throw new ExportException("The destination must be inside a folder.");
        var temporary = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(png, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // A complete decode (bounded by the pixel limit checked before rendering) proves the file is whole.
            using (var verify = File.OpenRead(temporary))
            using (var decoded = SKBitmap.Decode(verify))
            {
                if (decoded is null || decoded.Width != layout.Width || decoded.Height != layout.Height)
                {
                    throw new ExportException("The written PNG did not validate, so it was not installed.");
                }
            }

            if (!replaceExisting && File.Exists(destination))
            {
                throw new ExportException("The destination file was created during the export, so it was left untouched.");
            }

            File.Move(temporary, destination, overwrite: replaceExisting);
        }
        catch (IOException ex)
        {
            throw new ExportException("The PNG could not be written: " + ex.Message, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ExportException("The PNG could not be written: " + ex.Message, ex);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Measures, wraps, and draws text with per-character font fallback, so Latin, Japanese, Chinese, and Korean
    /// titles render with real glyphs. Break opportunities are spaces and positions around CJK characters.
    /// </summary>
    private sealed class TextSet : IDisposable
    {
        private readonly SKTypeface _regular = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal) ?? SKTypeface.Default;
        private readonly SKTypeface _bold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
        private readonly Dictionary<(int CodePoint, bool Bold), SKTypeface> _fallback = [];
        private readonly Dictionary<(SKTypeface Face, float Size), SKFont> _fonts = [];

        public SKFont CreateBoldFont(float size) => new(_bold, size) { Subpixel = true };

        public float Measure(string text, float size, bool bold = false) =>
            Runs(text, bold).Sum(run => Font(run.Face, size).MeasureText(run.Text));

        public void Draw(SKCanvas canvas, string text, float x, float baseline, float size, SKPaint paint, bool bold = false)
        {
            foreach (var (run, face) in Runs(text, bold))
            {
                var font = Font(face, size);
                canvas.DrawText(run, x, baseline, SKTextAlign.Left, font, paint);
                x += font.MeasureText(run);
            }
        }

        public List<string> Wrap(string text, float maxWidth, float size, bool bold = false)
        {
            var lines = new List<string>();
            var line = string.Empty;
            foreach (var token in Tokens(text))
            {
                var candidate = line + token;
                if (Measure(candidate.TrimEnd(), size, bold) <= maxWidth)
                {
                    line = candidate;
                    continue;
                }

                if (line.Trim().Length > 0)
                {
                    lines.Add(line.Trim());
                    line = string.Empty;
                }

                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                // A single token wider than the line breaks between characters.
                foreach (var element in Elements(token))
                {
                    if (line.Length > 0 && Measure(line + element, size, bold) > maxWidth)
                    {
                        lines.Add(line);
                        line = string.Empty;
                    }

                    line += element;
                }
            }

            if (line.Trim().Length > 0)
            {
                lines.Add(line.Trim());
            }

            return lines.Count == 0 ? [string.Empty] : lines;
        }

        public void Dispose()
        {
            foreach (var font in _fonts.Values)
            {
                font.Dispose();
            }

            foreach (var face in _fallback.Values.Distinct().Where(f => f != _regular && f != _bold))
            {
                face.Dispose();
            }

            _regular.Dispose();
            _bold.Dispose();
        }

        private static IEnumerable<string> Elements(string text)
        {
            var e = StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext())
            {
                yield return (string)e.Current;
            }
        }

        private static IEnumerable<string> Tokens(string text)
        {
            var word = string.Empty;
            foreach (var element in Elements(text))
            {
                if (char.IsWhiteSpace(element[0]) || IsCjk(char.ConvertToUtf32(element, 0)))
                {
                    if (word.Length > 0)
                    {
                        yield return word;
                        word = string.Empty;
                    }

                    yield return element;
                }
                else
                {
                    word += element;
                }
            }

            if (word.Length > 0)
            {
                yield return word;
            }
        }

        private static bool IsCjk(int c) =>
            c is (>= 0x2E80 and <= 0x9FFF) or (>= 0xAC00 and <= 0xD7AF) or (>= 0xF900 and <= 0xFAFF) or (>= 0xFF00 and <= 0xFFEF) or (>= 0x20000 and <= 0x3FFFF);

        private IEnumerable<(string Text, SKTypeface Face)> Runs(string text, bool bold)
        {
            var primary = bold ? _bold : _regular;
            var run = string.Empty;
            SKTypeface? current = null;
            foreach (var element in Elements(text))
            {
                var face = Font(primary, 12).ContainsGlyphs(element) ? primary : Fallback(char.ConvertToUtf32(element, 0), bold, primary);
                if (current is not null && face != current)
                {
                    yield return (run, current);
                    run = string.Empty;
                }

                current = face;
                run += element;
            }

            if (current is not null)
            {
                yield return (run, current);
            }
        }

        private SKTypeface Fallback(int codePoint, bool bold, SKTypeface primary)
        {
            if (!_fallback.TryGetValue((codePoint, bold), out var face))
            {
                face = SKFontManager.Default.MatchCharacter(primary.FamilyName, bold ? SKFontStyle.Bold : SKFontStyle.Normal, null, codePoint) ?? primary;
                _fallback[(codePoint, bold)] = face;
            }

            return face;
        }

        private SKFont Font(SKTypeface face, float size)
        {
            if (!_fonts.TryGetValue((face, size), out var font))
            {
                font = new SKFont(face, size) { Subpixel = true };
                _fonts[(face, size)] = font;
            }

            return font;
        }
    }
}
