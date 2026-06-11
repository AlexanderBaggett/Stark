using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stark.Compiler;

/// <summary>
/// Host binary package image container: "STARKPKG" magic, format version, payload
/// encoding id, then a Brotli-compressed canonical UTF-8 JSON payload of the package
/// manifest. The binary artifact is the compiler's normal load path; indented JSON is an
/// opt-in inspection sidecar (`--package-image-json`) or rendered on demand by
/// `--inspect-pkg`. The self-hosted compiler owns the long-term sectioned encoding
/// (docs/Self-host-Prep/20-package-image-format.md); this container makes the host
/// toolchain's default artifact binary-first without freezing that byte-level spec.
/// </summary>
internal static class PackageImageBinaryFormat
{
    public const string FileExtension = ".starkpkg";
    public const string JsonFileExtension = ".starkpkg.json";

    private const uint FormatVersion = 1;
    private const uint PayloadEncodingBrotliUtf8Json = 1;
    private const int HeaderLength = 8 + sizeof(uint) + sizeof(uint) + sizeof(ulong);

    private static ReadOnlySpan<byte> Magic => "STARKPKG"u8;

    private static readonly JsonSerializerOptions PayloadSerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static bool HasBinaryMagic(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= Magic.Length && bytes[..Magic.Length].SequenceEqual(Magic);

    public static bool HasBinaryFileName(string path) =>
        path.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase)
        && !path.EndsWith(JsonFileExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normalizes a requested package image output path to the binary artifact name:
    /// a legacy `.starkpkg.json` request drops the `.json` suffix, and any other name
    /// gains `.starkpkg` unless it already carries it.
    /// </summary>
    public static string NormalizeBinaryImagePath(string path)
    {
        if (path.EndsWith(JsonFileExtension, StringComparison.OrdinalIgnoreCase))
        {
            return path[..^".json".Length];
        }

        return HasBinaryFileName(path) ? path : path + FileExtension;
    }

    public static string JsonSidecarPath(string binaryImagePath) => binaryImagePath + ".json";

    public static byte[] Encode(StarkPackageManifest manifest)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, PayloadSerializerOptions);
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(payload);
        }

        var compressed = output.GetBuffer().AsSpan(0, (int)output.Length);
        var bytes = new byte[HeaderLength + compressed.Length];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), PayloadEncodingBrotliUtf8Json);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), (ulong)compressed.Length);
        compressed.CopyTo(bytes.AsSpan(HeaderLength));
        return bytes;
    }

    public static bool TryDecode(byte[] bytes, out StarkPackageManifest manifest)
    {
        manifest = default!;
        if (!HasBinaryMagic(bytes) || bytes.Length < HeaderLength)
        {
            return false;
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        var encoding = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        var payloadLength = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16));
        if (version != FormatVersion
            || encoding != PayloadEncodingBrotliUtf8Json
            || payloadLength != (ulong)(bytes.Length - HeaderLength))
        {
            return false;
        }

        try
        {
            using var input = new MemoryStream(bytes, HeaderLength, bytes.Length - HeaderLength, writable: false);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            var parsed = JsonSerializer.Deserialize<StarkPackageManifest>(brotli, PayloadSerializerOptions);
            if (parsed is null)
            {
                return false;
            }

            manifest = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
