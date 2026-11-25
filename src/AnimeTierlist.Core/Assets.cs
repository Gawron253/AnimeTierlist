namespace AnimeTierlist.Core;

/// <summary>Image formats admitted to the project asset store. Extensions are derived from decoded bytes, never URLs.</summary>
public enum AssetFormat
{
    Jpeg,
    Png,
    WebP,
}

/// <summary>
/// A validated, installed original. Created only after decoding, hashing, and atomic installation.
/// Later file loss or damage keeps this record; availability is checked separately.
/// </summary>
public sealed record AssetRecord
{
    public AssetRecord(AssetHash hash, AssetFormat format, int width, int height, long byteLength)
    {
        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteLength);
        Hash = hash;
        Format = format;
        Width = width;
        Height = height;
        ByteLength = byteLength;
    }

    public AssetHash Hash { get; }

    public AssetFormat Format { get; }

    public int Width { get; }

    public int Height { get; }

    public long ByteLength { get; }

    /// <summary>Canonical project-relative path: <c>assets/&lt;sha256&gt;.&lt;ext&gt;</c>.</summary>
    public string RelativePath => $"assets/{Hash.Value}.{Extension(Format)}";

    public static string Extension(AssetFormat format) => format switch
    {
        AssetFormat.Jpeg => "jpg",
        AssetFormat.Png => "png",
        AssetFormat.WebP => "webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}
