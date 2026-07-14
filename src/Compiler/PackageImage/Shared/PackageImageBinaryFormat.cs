using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stark.Compiler;

/// <summary>
/// Host binary package image container: "STARKPKG" magic, exact format version,
/// and a fixed-width section directory. MANF carries the Brotli-compressed
/// canonical UTF-8 JSON manifest, while STRS/PINF carry compact binary package
/// identity, target, and profile facts for fast compatibility checks.
/// </summary>
internal static class PackageImageBinaryFormat
{
    public const string FileExtension = ".starkpkg";
    public const string JsonFileExtension = ".starkpkg.json";

    private const string DiagnosticStage = "package-image-binary";
    internal const uint CurrentFormatVersion = 2;
    internal const uint LegacyFormatVersion = 1;
    private const uint SectionFlagsRequired = 1;
    private const uint SectionEncodingRaw = 0;
    private const uint SectionEncodingBrotliUtf8Json = 1;
    private const uint NullStringIndex = uint.MaxValue;
    private const uint ManifestSectionId = (byte)'M'
        | ((uint)(byte)'A' << 8)
        | ((uint)(byte)'N' << 16)
        | ((uint)(byte)'F' << 24);
    private const uint StringTableSectionId = (byte)'S'
        | ((uint)(byte)'T' << 8)
        | ((uint)(byte)'R' << 16)
        | ((uint)(byte)'S' << 24);
    private const uint PackageFactsSectionId = (byte)'P'
        | ((uint)(byte)'I' << 8)
        | ((uint)(byte)'N' << 16)
        | ((uint)(byte)'F' << 24);
    private const int LegacyHeaderLength = 8 + sizeof(uint) + sizeof(uint) + sizeof(ulong);
    private const int SectionedHeaderLength = 8 + sizeof(uint) + sizeof(uint) + sizeof(ulong);
    private const int SectionDirectoryEntryLength =
        sizeof(uint)  // section id
        + sizeof(uint) // flags
        + sizeof(ulong) // offset
        + sizeof(ulong) // length
        + sizeof(uint) // encoding
        + sizeof(uint); // reserved

    private static ReadOnlySpan<byte> Magic => "STARKPKG"u8;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions PayloadSerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static bool HasBinaryMagic(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= Magic.Length && bytes[..Magic.Length].SequenceEqual(Magic);

    internal static bool IsSupportedFormatVersion(int version) =>
        version == LegacyFormatVersion || version == CurrentFormatVersion;

    internal static bool TryReadFormatVersion(ReadOnlySpan<byte> bytes, out uint version)
    {
        if (!HasBinaryMagic(bytes) || bytes.Length < Magic.Length + sizeof(uint))
        {
            version = 0;
            return false;
        }

        version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[Magic.Length..]);
        return true;
    }

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
        var stringTableBuilder = new StringTableBuilder();
        AddPackageFactStrings(manifest, stringTableBuilder);
        var stringTable = EncodeStringTable(stringTableBuilder.Values);
        var packageFacts = EncodePackageFacts(manifest, stringTableBuilder);

        const uint sectionCount = 3;
        const ulong directoryLength = sectionCount * SectionDirectoryEntryLength;
        const int directoryOffset = SectionedHeaderLength;
        const int dataOffset = SectionedHeaderLength + (int)directoryLength;
        var stringTableOffset = dataOffset;
        var packageFactsOffset = stringTableOffset + stringTable.Length;
        var manifestOffset = packageFactsOffset + packageFacts.Length;

        var bytes = new byte[manifestOffset + compressed.Length];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), CurrentFormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), sectionCount);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), directoryLength);
        WriteSectionDirectoryEntry(
            bytes.AsSpan(directoryOffset, SectionDirectoryEntryLength),
            StringTableSectionId,
            SectionFlagsRequired,
            (ulong)stringTableOffset,
            (ulong)stringTable.Length,
            SectionEncodingRaw);
        WriteSectionDirectoryEntry(
            bytes.AsSpan(directoryOffset + SectionDirectoryEntryLength, SectionDirectoryEntryLength),
            PackageFactsSectionId,
            SectionFlagsRequired,
            (ulong)packageFactsOffset,
            (ulong)packageFacts.Length,
            SectionEncodingRaw);
        WriteSectionDirectoryEntry(
            bytes.AsSpan(directoryOffset + (2 * SectionDirectoryEntryLength), SectionDirectoryEntryLength),
            ManifestSectionId,
            SectionFlagsRequired,
            (ulong)manifestOffset,
            (ulong)compressed.Length,
            SectionEncodingBrotliUtf8Json);
        stringTable.CopyTo(bytes.AsSpan(stringTableOffset));
        packageFacts.CopyTo(bytes.AsSpan(packageFactsOffset));
        compressed.CopyTo(bytes.AsSpan(manifestOffset));
        return bytes;
    }

    private static void AddPackageFactStrings(StarkPackageManifest manifest, StringTableBuilder strings)
    {
        strings.AddRequired(manifest.RootModule);
        strings.AddRequired(manifest.LibraryFileName);
        strings.AddOptional(manifest.BuildProfile?.Name);

        if (manifest.Target is not { } target)
        {
            return;
        }

        strings.AddRequired(target.Triple);
        strings.AddOptional(target.DataLayout);
        strings.AddOptional(target.Cpu);
        strings.AddRequired(target.RelocationModel);
        strings.AddOptional(target.CodeModel);
        foreach (var feature in target.Features ?? [])
        {
            strings.AddRequired(feature);
        }

        strings.AddOptional(target.CDataModel?.Kind);
    }

    private static byte[] EncodeStringTable(IReadOnlyList<string> strings)
    {
        using var output = new MemoryStream();
        WriteUInt32(output, checked((uint)strings.Count));
        foreach (var value in strings)
        {
            var bytes = StrictUtf8.GetBytes(value);
            WriteUInt32(output, checked((uint)bytes.Length));
            output.Write(bytes);
        }

        return output.ToArray();
    }

    private static byte[] EncodePackageFacts(StarkPackageManifest manifest, StringTableBuilder strings)
    {
        using var output = new MemoryStream();
        WriteStringIndex(output, strings.AddRequired(manifest.RootModule));
        WriteStringIndex(output, strings.AddRequired(manifest.LibraryFileName));
        WriteStringIndex(output, strings.AddOptional(manifest.BuildProfile?.Name));

        if (manifest.Target is { } target)
        {
            WriteStringIndex(output, strings.AddRequired(target.Triple));
            WriteStringIndex(output, strings.AddOptional(target.DataLayout));
            WriteStringIndex(output, strings.AddOptional(target.Cpu));
            WriteStringIndex(output, strings.AddRequired(target.RelocationModel));
            WriteStringIndex(output, strings.AddOptional(target.CodeModel));
            WriteUInt32(output, checked((uint)(target.Features?.Count ?? 0)));
            foreach (var feature in target.Features ?? [])
            {
                WriteStringIndex(output, strings.AddRequired(feature));
            }

            if (target.CDataModel is { } cDataModel)
            {
                WriteUInt32(output, 1);
                WriteStringIndex(output, strings.AddOptional(cDataModel.Kind));
                WriteUInt32(output, cDataModel.CharIsSigned ? 1u : 0u);
                WriteUInt32(output, checked((uint)cDataModel.PointerBitWidth));
                WriteUInt32(output, checked((uint)cDataModel.LongBitWidth));
                WriteUInt32(output, checked((uint)cDataModel.SizeTBitWidth));
                WriteUInt32(output, checked((uint)cDataModel.PtrDiffTBitWidth));
            }
            else
            {
                WriteUInt32(output, 0);
            }

            if (target.AggregateLayout is { } aggregateLayout)
            {
                WriteUInt32(output, 1);
                WriteUInt32(output, checked((uint)aggregateLayout.PointerSizeBytes));
                WriteUInt32(output, checked((uint)aggregateLayout.PointerAlignmentBytes));
            }
            else
            {
                WriteUInt32(output, 0);
            }
        }
        else
        {
            WriteStringIndex(output, NullStringIndex);
            WriteStringIndex(output, NullStringIndex);
            WriteStringIndex(output, NullStringIndex);
            WriteStringIndex(output, NullStringIndex);
            WriteStringIndex(output, NullStringIndex);
            WriteUInt32(output, 0);
            WriteUInt32(output, 0);
            WriteUInt32(output, 0);
        }

        return output.ToArray();
    }

    public static bool TryDecode(byte[] bytes, out StarkPackageManifest manifest)
    {
        return TryDecode(bytes, imagePath: null, out manifest, out _);
    }

    public static bool TryDecode(
        byte[] bytes,
        string? imagePath,
        out StarkPackageManifest manifest,
        out IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        manifest = default!;
        if (bytes.Length < Magic.Length)
        {
            diagnostics =
            [
                CreateDiagnostic(
                    "STK7120",
                    $"Package image binary header is truncated before the STARKPKG magic. Expected at least {Magic.Length} bytes, found {bytes.Length}.",
                    imagePath)
            ];
            return false;
        }

        if (!HasBinaryMagic(bytes))
        {
            diagnostics =
            [
                CreateDiagnostic(
                    "STK7120",
                    "Package image binary header does not start with the STARKPKG magic.",
                    imagePath)
            ];
            return false;
        }

        if (bytes.Length < Magic.Length + sizeof(uint))
        {
            diagnostics =
            [
                CreateDiagnostic(
                    "STK7120",
                    $"Package image binary header is truncated before the format version. Expected at least {Magic.Length + sizeof(uint)} bytes, found {bytes.Length}.",
                    imagePath)
            ];
            return false;
        }

        _ = TryReadFormatVersion(bytes, out var version);
        return version switch
        {
            LegacyFormatVersion => TryDecodeLegacy(bytes, imagePath, out manifest, out diagnostics),
            CurrentFormatVersion => TryDecodeSectioned(bytes, imagePath, out manifest, out diagnostics),
            _ => Fail(
                out diagnostics,
                "STK7121",
                $"Package image binary format version {version} is not supported; expected {LegacyFormatVersion} or {CurrentFormatVersion}.",
                imagePath)
        };
    }

    private static bool TryDecodeLegacy(
        byte[] bytes,
        string? imagePath,
        out StarkPackageManifest manifest,
        out IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        manifest = default!;

        if (bytes.Length < LegacyHeaderLength)
        {
            diagnostics =
            [
                CreateDiagnostic(
                    "STK7120",
                    $"Package image binary header is truncated. Expected at least {LegacyHeaderLength} bytes, found {bytes.Length}.",
                    imagePath)
            ];
            return false;
        }

        var encoding = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        var payloadLength = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16));

        if (encoding != SectionEncodingBrotliUtf8Json)
        {
            diagnostics =
            [
                CreateDiagnostic(
                    "STK7122",
                    $"Package image payload encoding {encoding} is not supported; expected Brotli-compressed UTF-8 JSON ({SectionEncodingBrotliUtf8Json}).",
                    imagePath)
            ];
            return false;
        }

        var actualPayloadLength = (ulong)(bytes.Length - LegacyHeaderLength);
        if (payloadLength != actualPayloadLength)
        {
            diagnostics =
            [
                CreateDiagnostic(
                    "STK7123",
                    $"Package image payload length is {payloadLength}, but the file contains {actualPayloadLength} payload bytes.",
                    imagePath)
            ];
            return false;
        }

        return TryDecodeBrotliJsonPayload(
            bytes,
            LegacyHeaderLength,
            bytes.Length - LegacyHeaderLength,
            imagePath,
            out manifest,
            out diagnostics);
    }

    private static bool TryDecodeSectioned(
        byte[] bytes,
        string? imagePath,
        out StarkPackageManifest manifest,
        out IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        manifest = default!;

        if (bytes.Length < SectionedHeaderLength)
        {
            diagnostics =
            [
                CreateDiagnostic(
                    "STK7120",
                    $"Package image binary header is truncated. Expected at least {SectionedHeaderLength} bytes, found {bytes.Length}.",
                    imagePath)
            ];
            return false;
        }

        var sectionCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        var directoryLength = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16));
        var expectedDirectoryLength = (ulong)sectionCount * SectionDirectoryEntryLength;
        if (directoryLength != expectedDirectoryLength)
        {
            diagnostics =
            [
                CreateDiagnostic(
                    "STK7130",
                    $"Package image section directory length is {directoryLength}, but {sectionCount} section entries require {expectedDirectoryLength} bytes.",
                    imagePath)
            ];
            return false;
        }

        var dataOffset = (ulong)SectionedHeaderLength + directoryLength;
        if (dataOffset > (ulong)bytes.Length)
        {
            diagnostics =
            [
                CreateDiagnostic(
                    "STK7130",
                    $"Package image section directory is truncated. Expected {directoryLength} bytes after the header, but the file is {bytes.Length} bytes long.",
                    imagePath)
            ];
            return false;
        }

        SectionDirectoryEntry manifestEntry = default;
        SectionDirectoryEntry stringTableEntry = default;
        SectionDirectoryEntry packageFactsEntry = default;
        var foundManifest = false;
        var foundStringTable = false;
        var foundPackageFacts = false;

        for (var index = 0u; index < sectionCount; index++)
        {
            var entryOffset = SectionedHeaderLength + checked((int)index * SectionDirectoryEntryLength);
            var entrySpan = bytes.AsSpan(entryOffset, SectionDirectoryEntryLength);
            var entry = new SectionDirectoryEntry(
                SectionId: BinaryPrimitives.ReadUInt32LittleEndian(entrySpan),
                Flags: BinaryPrimitives.ReadUInt32LittleEndian(entrySpan[4..]),
                Offset: BinaryPrimitives.ReadUInt64LittleEndian(entrySpan[8..]),
                Length: BinaryPrimitives.ReadUInt64LittleEndian(entrySpan[16..]),
                Encoding: BinaryPrimitives.ReadUInt32LittleEndian(entrySpan[24..]),
                Reserved: BinaryPrimitives.ReadUInt32LittleEndian(entrySpan[28..]));

            if (entry.Reserved != 0)
            {
                diagnostics =
                [
                    CreateDiagnostic(
                        "STK7130",
                        $"Package image section '{FormatSectionId(entry.SectionId)}' has non-zero reserved directory bits.",
                        imagePath)
                ];
                return false;
            }

            if ((entry.Flags & ~SectionFlagsRequired) != 0)
            {
                diagnostics =
                [
                    CreateDiagnostic(
                        "STK7130",
                        $"Package image section '{FormatSectionId(entry.SectionId)}' uses unsupported directory flags 0x{entry.Flags:X8}.",
                        imagePath)
                ];
                return false;
            }

            if (!TryValidateSectionRange(
                entry.SectionId,
                entry.Offset,
                entry.Length,
                dataOffset,
                (ulong)bytes.Length,
                imagePath,
                out diagnostics))
            {
                return false;
            }

            switch (entry.SectionId)
            {
                case ManifestSectionId:
                    if (!TryAcceptKnownSection(
                        entry,
                        "MANF",
                        SectionEncodingBrotliUtf8Json,
                        foundManifest,
                        imagePath,
                        out diagnostics))
                    {
                        return false;
                    }

                    foundManifest = true;
                    manifestEntry = entry;
                    break;
                case StringTableSectionId:
                    if (!TryAcceptKnownSection(
                        entry,
                        "STRS",
                        SectionEncodingRaw,
                        foundStringTable,
                        imagePath,
                        out diagnostics))
                    {
                        return false;
                    }

                    foundStringTable = true;
                    stringTableEntry = entry;
                    break;
                case PackageFactsSectionId:
                    if (!TryAcceptKnownSection(
                        entry,
                        "PINF",
                        SectionEncodingRaw,
                        foundPackageFacts,
                        imagePath,
                        out diagnostics))
                    {
                        return false;
                    }

                    foundPackageFacts = true;
                    packageFactsEntry = entry;
                    break;
                default:
                    if ((entry.Flags & SectionFlagsRequired) != 0)
                    {
                        diagnostics =
                        [
                            CreateDiagnostic(
                                "STK7131",
                                $"Package image contains unknown required section '{FormatSectionId(entry.SectionId)}'. Rebuild the package with this compiler or a compatible one.",
                                imagePath)
                        ];
                        return false;
                    }

                    break;
            }
        }

        if (!foundManifest)
        {
            diagnostics =
            [
                CreateDiagnostic(
                    "STK7133",
                    "Package image does not contain the required MANF manifest section.",
                    imagePath)
            ];
            return false;
        }

        if (!foundStringTable)
        {
            diagnostics =
            [
                CreateDiagnostic(
                    "STK7133",
                    "Package image does not contain the required STRS string-table section.",
                    imagePath)
            ];
            return false;
        }

        if (!foundPackageFacts)
        {
            diagnostics =
            [
                CreateDiagnostic(
                    "STK7133",
                    "Package image does not contain the required PINF package-facts section.",
                    imagePath)
            ];
            return false;
        }

        if (!TryDecodeStringTable(bytes, stringTableEntry, imagePath, out var strings, out diagnostics)
            || !TryDecodePackageFacts(bytes, packageFactsEntry, strings, imagePath, out var binaryFacts, out diagnostics))
        {
            return false;
        }

        if (!TryDecodeBrotliJsonPayload(
            bytes,
            checked((int)manifestEntry.Offset),
            checked((int)manifestEntry.Length),
            imagePath,
            out manifest,
            out diagnostics))
        {
            return false;
        }

        if (!TryValidateBinaryFactsMatchManifest(binaryFacts!, manifest, imagePath, out diagnostics))
        {
            manifest = default!;
            return false;
        }

        return true;
    }

    private static bool TryAcceptKnownSection(
        SectionDirectoryEntry entry,
        string sectionName,
        uint expectedEncoding,
        bool alreadyFound,
        string? imagePath,
        out IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        if (alreadyFound)
        {
            diagnostics =
            [
                CreateDiagnostic(
                    "STK7134",
                    $"Package image contains more than one {sectionName} section.",
                    imagePath)
            ];
            return false;
        }

        if (entry.Encoding != expectedEncoding)
        {
            diagnostics =
            [
                CreateDiagnostic(
                    "STK7122",
                    $"Package image {sectionName} section encoding {entry.Encoding} is not supported; expected {FormatSectionEncoding(expectedEncoding)} ({expectedEncoding}).",
                    imagePath)
            ];
            return false;
        }

        diagnostics = [];
        return true;
    }

    private static bool TryDecodeStringTable(
        byte[] bytes,
        SectionDirectoryEntry entry,
        string? imagePath,
        out IReadOnlyList<string> strings,
        out IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        strings = [];
        var offset = checked((int)entry.Offset);
        var end = checked(offset + (int)entry.Length);
        if (!TryReadUInt32(bytes, end, ref offset, out var count))
        {
            return Fail(
                out diagnostics,
                "STK7136",
                "Package image STRS string-table section is truncated before the string count.",
                imagePath);
        }

        var values = new List<string>();
        for (var index = 0u; index < count; index++)
        {
            if (!TryReadUInt32(bytes, end, ref offset, out var byteLength)
                || byteLength > end - offset)
            {
                return Fail(
                    out diagnostics,
                    "STK7136",
                    $"Package image STRS string-table entry {index} has a truncated byte payload.",
                    imagePath);
            }

            try
            {
                values.Add(StrictUtf8.GetString(bytes, offset, checked((int)byteLength)));
            }
            catch (DecoderFallbackException exception)
            {
                return Fail(
                    out diagnostics,
                    "STK7136",
                    $"Package image STRS string-table entry {index} is not valid UTF-8: {exception.Message}",
                    imagePath);
            }

            offset += checked((int)byteLength);
        }

        if (offset != end)
        {
            return Fail(
                out diagnostics,
                "STK7136",
                $"Package image STRS string-table section has {end - offset} trailing bytes.",
                imagePath);
        }

        strings = values;
        diagnostics = [];
        return true;
    }

    private static bool TryDecodePackageFacts(
        byte[] bytes,
        SectionDirectoryEntry entry,
        IReadOnlyList<string> strings,
        string? imagePath,
        out PackageImageBinaryFacts? facts,
        out IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        facts = null;
        var offset = checked((int)entry.Offset);
        var end = checked(offset + (int)entry.Length);

        if (!TryReadString(bytes, end, ref offset, strings, required: true, out var rootModule, out diagnostics, imagePath, "root module")
            || !TryReadString(bytes, end, ref offset, strings, required: true, out var libraryFileName, out diagnostics, imagePath, "library file name")
            || !TryReadString(bytes, end, ref offset, strings, required: false, out var buildProfile, out diagnostics, imagePath, "build profile")
            || !TryReadString(bytes, end, ref offset, strings, required: false, out var targetTriple, out diagnostics, imagePath, "target triple")
            || !TryReadString(bytes, end, ref offset, strings, required: false, out var targetDataLayout, out diagnostics, imagePath, "target data layout")
            || !TryReadString(bytes, end, ref offset, strings, required: false, out var targetCpu, out diagnostics, imagePath, "target CPU")
            || !TryReadString(bytes, end, ref offset, strings, required: false, out var relocationModel, out diagnostics, imagePath, "relocation model")
            || !TryReadString(bytes, end, ref offset, strings, required: false, out var codeModel, out diagnostics, imagePath, "code model")
            || !TryReadUInt32WithDiagnostic(bytes, end, ref offset, "target feature count", imagePath, out var featureCount, out diagnostics))
        {
            return false;
        }

        var features = new List<string>();
        for (var index = 0u; index < featureCount; index++)
        {
            if (!TryReadString(bytes, end, ref offset, strings, required: true, out var feature, out diagnostics, imagePath, $"target feature {index}"))
            {
                return false;
            }

            features.Add(feature!);
        }

        if (!TryReadUInt32WithDiagnostic(bytes, end, ref offset, "C data-model presence", imagePath, out var hasCDataModel, out diagnostics))
        {
            return false;
        }

        StarkPackageCDataModelManifest? cDataModel = null;
        if (hasCDataModel == 1)
        {
            if (!TryReadString(bytes, end, ref offset, strings, required: true, out var cDataModelKind, out diagnostics, imagePath, "C data-model kind")
                || !TryReadUInt32WithDiagnostic(bytes, end, ref offset, "C char signedness", imagePath, out var charIsSigned, out diagnostics)
                || !TryReadUInt32WithDiagnostic(bytes, end, ref offset, "C pointer bit width", imagePath, out var pointerBitWidth, out diagnostics)
                || !TryReadUInt32WithDiagnostic(bytes, end, ref offset, "C long bit width", imagePath, out var longBitWidth, out diagnostics)
                || !TryReadUInt32WithDiagnostic(bytes, end, ref offset, "C size_t bit width", imagePath, out var sizeTBitWidth, out diagnostics)
                || !TryReadUInt32WithDiagnostic(bytes, end, ref offset, "C ptrdiff_t bit width", imagePath, out var ptrDiffTBitWidth, out diagnostics))
            {
                return false;
            }

            if (charIsSigned > 1)
            {
                return Fail(
                    out diagnostics,
                    "STK7136",
                    $"Package image PINF C char signedness value {charIsSigned} is not supported.",
                    imagePath);
            }

            cDataModel = new StarkPackageCDataModelManifest(
                cDataModelKind!,
                charIsSigned == 1,
                checked((int)pointerBitWidth),
                checked((int)longBitWidth),
                checked((int)sizeTBitWidth),
                checked((int)ptrDiffTBitWidth));
        }
        else if (hasCDataModel != 0)
        {
            return Fail(
                out diagnostics,
                "STK7136",
                $"Package image PINF C data-model presence value {hasCDataModel} is not supported.",
                imagePath);
        }

        if (!TryReadUInt32WithDiagnostic(bytes, end, ref offset, "aggregate layout presence", imagePath, out var hasAggregateLayout, out diagnostics))
        {
            return false;
        }

        StarkPackageAggregateLayoutManifest? aggregateLayout = null;
        if (hasAggregateLayout == 1)
        {
            if (!TryReadUInt32WithDiagnostic(bytes, end, ref offset, "aggregate pointer size", imagePath, out var pointerSizeBytes, out diagnostics)
                || !TryReadUInt32WithDiagnostic(bytes, end, ref offset, "aggregate pointer alignment", imagePath, out var pointerAlignmentBytes, out diagnostics))
            {
                return false;
            }

            aggregateLayout = new StarkPackageAggregateLayoutManifest(
                checked((int)pointerSizeBytes),
                checked((int)pointerAlignmentBytes));
        }
        else if (hasAggregateLayout != 0)
        {
            return Fail(
                out diagnostics,
                "STK7136",
                $"Package image PINF aggregate-layout presence value {hasAggregateLayout} is not supported.",
                imagePath);
        }

        if (offset != end)
        {
            return Fail(
                out diagnostics,
                "STK7136",
                $"Package image PINF package-facts section has {end - offset} trailing bytes.",
                imagePath);
        }

        facts = new PackageImageBinaryFacts(
            rootModule!,
            libraryFileName!,
            buildProfile,
            targetTriple,
            targetDataLayout,
            targetCpu,
            relocationModel,
            codeModel,
            features,
            cDataModel,
            aggregateLayout);
        diagnostics = [];
        return true;
    }

    private static bool TryValidateBinaryFactsMatchManifest(
        PackageImageBinaryFacts facts,
        StarkPackageManifest manifest,
        string? imagePath,
        out IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        if (!TryCompareBinaryFact(facts.RootModule, manifest.RootModule, "root module", imagePath, out diagnostics)
            || !TryCompareBinaryFact(facts.LibraryFileName, manifest.LibraryFileName, "library file name", imagePath, out diagnostics)
            || !TryCompareBinaryFact(facts.BuildProfile, manifest.BuildProfile?.Name, "build profile", imagePath, out diagnostics)
            || !TryCompareBinaryFact(facts.TargetTriple, manifest.Target?.Triple, "target triple", imagePath, out diagnostics)
            || !TryCompareBinaryFact(facts.TargetDataLayout, manifest.Target?.DataLayout, "target data layout", imagePath, out diagnostics)
            || !TryCompareBinaryFact(facts.TargetCpu, manifest.Target?.Cpu, "target CPU", imagePath, out diagnostics)
            || !TryCompareBinaryFact(facts.RelocationModel, manifest.Target?.RelocationModel, "relocation model", imagePath, out diagnostics)
            || !TryCompareBinaryFact(facts.CodeModel, manifest.Target?.CodeModel, "code model", imagePath, out diagnostics)
            || !TryCompareBinaryFactList(facts.TargetFeatures, manifest.Target?.Features, "target features", imagePath, out diagnostics)
            || !TryCompareCDataModel(facts.CDataModel, manifest.Target?.CDataModel, imagePath, out diagnostics)
            || !TryCompareAggregateLayout(facts.AggregateLayout, manifest.Target?.AggregateLayout, imagePath, out diagnostics))
        {
            return false;
        }

        diagnostics = [];
        return true;
    }

    private static bool TryDecodeBrotliJsonPayload(
        byte[] bytes,
        int offset,
        int length,
        string? imagePath,
        out StarkPackageManifest manifest,
        out IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        manifest = default!;

        try
        {
            using var input = new MemoryStream(bytes, offset, length, writable: false);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            var parsed = JsonSerializer.Deserialize<StarkPackageManifest>(brotli, PayloadSerializerOptions);
            if (parsed is null)
            {
                diagnostics =
                [
                    CreateDiagnostic(
                        "STK7125",
                        "Package image JSON payload did not contain a Stark package image document.",
                        imagePath)
                ];
                return false;
            }

            manifest = parsed;
            diagnostics = [];
            return true;
        }
        catch (InvalidDataException exception)
        {
            diagnostics =
            [
                CreateDiagnostic(
                    "STK7124",
                    $"Package image Brotli payload is malformed: {exception.Message}",
                    imagePath)
            ];
            return false;
        }
        catch (JsonException exception)
        {
            diagnostics =
            [
                CreateDiagnostic(
                    "STK7125",
                    $"Package image JSON payload is malformed: {exception.Message}",
                    imagePath)
            ];
            return false;
        }
        catch
        {
            diagnostics =
            [
                CreateDiagnostic(
                    "STK7126",
                    "Package image binary payload could not be decoded.",
                    imagePath)
            ];
            return false;
        }
    }

    private static bool TryValidateSectionRange(
        uint sectionId,
        ulong sectionOffset,
        ulong sectionLength,
        ulong dataOffset,
        ulong fileLength,
        string? imagePath,
        out IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        if (sectionOffset < dataOffset
            || sectionOffset > fileLength
            || sectionLength > fileLength - sectionOffset)
        {
            diagnostics =
            [
                CreateDiagnostic(
                    "STK7132",
                    $"Package image section '{FormatSectionId(sectionId)}' has invalid offset/length ({sectionOffset}, {sectionLength}) for a {fileLength}-byte file.",
                    imagePath)
            ];
            return false;
        }

        diagnostics = [];
        return true;
    }

    private static bool TryReadString(
        byte[] bytes,
        int end,
        ref int offset,
        IReadOnlyList<string> strings,
        bool required,
        out string? value,
        out IReadOnlyList<CompilerDiagnostic> diagnostics,
        string? imagePath,
        string fieldName)
    {
        value = null;
        if (!TryReadUInt32WithDiagnostic(bytes, end, ref offset, fieldName, imagePath, out var index, out diagnostics))
        {
            return false;
        }

        if (index == NullStringIndex)
        {
            if (!required)
            {
                diagnostics = [];
                return true;
            }

            return Fail(
                out diagnostics,
                "STK7136",
                $"Package image PINF {fieldName} must name a string-table entry.",
                imagePath);
        }

        if (index >= strings.Count)
        {
            return Fail(
                out diagnostics,
                "STK7136",
                $"Package image PINF {fieldName} string index {index} is outside the STRS string table.",
                imagePath);
        }

        value = strings[(int)index];
        diagnostics = [];
        return true;
    }

    private static bool TryReadUInt32WithDiagnostic(
        byte[] bytes,
        int end,
        ref int offset,
        string fieldName,
        string? imagePath,
        out uint value,
        out IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        if (TryReadUInt32(bytes, end, ref offset, out value))
        {
            diagnostics = [];
            return true;
        }

        return Fail(
            out diagnostics,
            "STK7136",
            $"Package image PINF package-facts section is truncated before {fieldName}.",
            imagePath);
    }

    private static bool TryReadUInt32(byte[] bytes, int end, ref int offset, out uint value)
    {
        if (offset > end - sizeof(uint))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)));
        offset += sizeof(uint);
        return true;
    }

    private static bool TryCompareBinaryFact(
        string? binaryValue,
        string? manifestValue,
        string fieldName,
        string? imagePath,
        out IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        if (string.Equals(binaryValue, manifestValue, StringComparison.Ordinal))
        {
            diagnostics = [];
            return true;
        }

        diagnostics =
        [
            CreateDiagnostic(
                "STK7135",
                $"Package image binary {fieldName} fact '{FormatNullable(binaryValue)}' does not match manifest {fieldName} '{FormatNullable(manifestValue)}'.",
                imagePath)
        ];
        return false;
    }

    private static bool TryCompareBinaryFactList(
        IReadOnlyList<string> binaryValues,
        IReadOnlyList<string>? manifestValues,
        string fieldName,
        string? imagePath,
        out IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        var expected = manifestValues ?? [];
        if (binaryValues.Count == expected.Count
            && binaryValues.SequenceEqual(expected, StringComparer.Ordinal))
        {
            diagnostics = [];
            return true;
        }

        diagnostics =
        [
            CreateDiagnostic(
                "STK7135",
                $"Package image binary {fieldName} facts do not match manifest {fieldName}.",
                imagePath)
        ];
        return false;
    }

    private static bool TryCompareCDataModel(
        StarkPackageCDataModelManifest? binaryValue,
        StarkPackageCDataModelManifest? manifestValue,
        string? imagePath,
        out IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        if (binaryValue == manifestValue)
        {
            diagnostics = [];
            return true;
        }

        diagnostics =
        [
            CreateDiagnostic(
                "STK7135",
                "Package image binary C data-model facts do not match manifest C data-model facts.",
                imagePath)
        ];
        return false;
    }

    private static bool TryCompareAggregateLayout(
        StarkPackageAggregateLayoutManifest? binaryValue,
        StarkPackageAggregateLayoutManifest? manifestValue,
        string? imagePath,
        out IReadOnlyList<CompilerDiagnostic> diagnostics)
    {
        if (binaryValue == manifestValue)
        {
            diagnostics = [];
            return true;
        }

        diagnostics =
        [
            CreateDiagnostic(
                "STK7135",
                "Package image binary aggregate-layout facts do not match manifest aggregate-layout facts.",
                imagePath)
        ];
        return false;
    }

    private static void WriteSectionDirectoryEntry(
        Span<byte> destination,
        uint sectionId,
        uint flags,
        ulong offset,
        ulong length,
        uint encoding)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, sectionId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], flags);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], offset);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], length);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..], encoding);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[28..], 0);
    }

    private static void WriteStringIndex(Stream output, uint index)
    {
        WriteUInt32(output, index);
    }

    private static void WriteUInt32(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static bool Fail(
        out IReadOnlyList<CompilerDiagnostic> diagnostics,
        string code,
        string message,
        string? imagePath)
    {
        diagnostics = [CreateDiagnostic(code, message, imagePath)];
        return false;
    }

    private static string FormatNullable(string? value) =>
        value is null ? "<none>" : value;

    private static string FormatSectionEncoding(uint encoding)
    {
        return encoding switch
        {
            SectionEncodingRaw => "raw binary",
            SectionEncodingBrotliUtf8Json => "Brotli-compressed UTF-8 JSON",
            _ => "unknown"
        };
    }

    private static string FormatSectionId(uint sectionId)
    {
        Span<char> chars =
        [
            (char)(sectionId & 0xFF),
            (char)((sectionId >> 8) & 0xFF),
            (char)((sectionId >> 16) & 0xFF),
            (char)((sectionId >> 24) & 0xFF)
        ];

        foreach (var c in chars)
        {
            if (c < ' ' || c > '~')
            {
                return $"0x{sectionId:X8}";
            }
        }

        return new string(chars);
    }

    private sealed class StringTableBuilder
    {
        private readonly Dictionary<string, uint> _indexes = new(StringComparer.Ordinal);
        private readonly List<string> _values = [];

        public IReadOnlyList<string> Values => _values;

        public uint AddRequired(string value)
        {
            if (_indexes.TryGetValue(value, out var index))
            {
                return index;
            }

            index = checked((uint)_values.Count);
            _indexes.Add(value, index);
            _values.Add(value);
            return index;
        }

        public uint AddOptional(string? value)
        {
            return value is null ? NullStringIndex : AddRequired(value);
        }
    }

    private readonly record struct SectionDirectoryEntry(
        uint SectionId,
        uint Flags,
        ulong Offset,
        ulong Length,
        uint Encoding,
        uint Reserved);

    private sealed record PackageImageBinaryFacts(
        string RootModule,
        string LibraryFileName,
        string? BuildProfile,
        string? TargetTriple,
        string? TargetDataLayout,
        string? TargetCpu,
        string? RelocationModel,
        string? CodeModel,
        IReadOnlyList<string> TargetFeatures,
        StarkPackageCDataModelManifest? CDataModel,
        StarkPackageAggregateLayoutManifest? AggregateLayout);

    private static CompilerDiagnostic CreateDiagnostic(string code, string message, string? imagePath)
    {
        return new CompilerDiagnostic(
            Code: code,
            Severity: DiagnosticSeverity.Error,
            Message: message,
            Stage: DiagnosticStage,
            Location: SourceLocation.Synthetic(imagePath));
    }
}
