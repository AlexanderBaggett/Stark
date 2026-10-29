using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed partial class SsaConstStdlibHelperSpecializer
{
    private static bool TryResolveUtf16FileVariant(string functionName, string moduleName, out string targetName)
    {
        var name = functionName.Split("#(", StringSplitOptions.None)[0];
        foreach (var operation in new[] { "WriteText", "WriteLine" })
        {
            if (name == $"System.IO.File.File.{operation}"
                || moduleName == "System.IO.File" && name == $"File.{operation}")
            {
                targetName = operation + "Preencoded";
                return true;
            }
        }
        targetName = string.Empty;
        return false;
    }

    private bool TryPreencodeUtf16FileCall(
        SsaFunction function, string moduleName, SsaFunctionFactModel facts,
        IReadOnlyDictionary<string, SsaRValue> definitions, IReadOnlySet<string> constLocals,
        ISsaDirectCallOperation call, out SsaCallRValue rewritten)
    {
        rewritten = null!;
        if (call.Arguments.Count != 2
            || call.Arguments[1].Type.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            || !TryResolveUtf16FileVariant(call.FunctionName, moduleName, out var targetName)
            || !HasConstMemoryProvenance(function, call.Arguments[1], definitions, constLocals, new HashSet<string>(StringComparer.Ordinal))
            || !TryGetEncodingPayload(call.Arguments[1], function, facts, definitions,
                new HashSet<string>(StringComparer.Ordinal), out var utf8, out var utf32))
        {
            return false;
        }

        var candidates = EnumerateRetargetNameCandidates(call.FunctionName, moduleName, "System.IO.File", targetName)
            .SelectMany(name => (_typeModel.Functions.TryGetValue(name, out var direct) ? new[] { direct } : [])
                .Concat(_typeModel.Overloads.GetValueOrDefault(name) ?? []))
            .Where(signature => signature.Parameters.Count == 7
                && HaveSameType(signature.ReturnType, call.Type)
                && SameReceiverType(signature.Parameters[0].Type, call.Arguments[0].Type)
                && signature.Parameters[1].Type.Kind == call.Arguments[1].Type.Kind
                && IsReadonlyIntegerPointer(signature.Parameters[2].Type, 16)
                && signature.Parameters[3].Type is { Kind: StarkTypeKind.Integer, BitWidth: 64 }
                && IsReadonlyIntegerPointer(signature.Parameters[4].Type, 64)
                && signature.Parameters[5].Type is { Kind: StarkTypeKind.Integer, BitWidth: 64 }
                && signature.Parameters[6].Type.Kind == StarkTypeKind.Bool)
            .DistinctBy(signature => signature.Name).ToArray();
        if (candidates.Length != 1)
        {
            return false;
        }

        var payload = EncodeUtf16Payload(call.Arguments[1].Type.Kind, utf8, utf32);
        var units = payload.Units;
        var chunks = payload.Chunks;
        var ascii = payload.Ascii;
        var count = units.Count;
        var line = targetName == "WriteLinePreencoded";
        var sourceLength = call.Arguments[1].Type.Kind == StarkTypeKind.Ascii ? utf8.Length : utf32.Length;
        var combine = line && ascii && sourceLength is > 0 and <= 1023;
        if (line) units.Add(10);
        var arguments = call.Arguments.Concat(new SsaValue[]
        {
            new SsaStaticArrayAddressValue(units.ToArray(), StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(16), isMutable: false)),
            new SsaIntegerConstant(count, StarkTypeSymbols.Integer(64)),
            new SsaStaticArrayAddressValue(chunks.ToArray(), StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(64), isMutable: false)),
            new SsaIntegerConstant(chunks.Count, StarkTypeSymbols.Integer(64)),
            new SsaBoolConstant(combine)
        }).ToArray();
        rewritten = new SsaCallRValue(candidates[0].Name, arguments, call.Type,
            RetargetCallText(call.Text, targetName),
            call.IndirectArgumentLocalNames?.Concat(Enumerable.Repeat<string?>(null, 5)).ToArray(),
            call.SourceReturnType,
            call.IndirectArgumentAddresses?.Concat(Enumerable.Repeat<SsaValue?>(null, 5)).ToArray());
        return true;
    }

    private bool TryPreencodeUtf16Call(
        SsaFunction function, string moduleName, SsaFunctionFactModel facts,
        IReadOnlyDictionary<string, SsaRValue> definitions, IReadOnlySet<string> constLocals,
        ISsaDirectCallOperation call, out SsaCallRValue rewritten)
    {
        return TryPreencodeUtf16ConversionCall(function, moduleName, facts, definitions, constLocals, call, out rewritten)
            || TryPreencodeUtf16FileCall(function, moduleName, facts, definitions, constLocals, call, out rewritten);
    }

    private static bool TryResolveUtf16ConversionVariant(string functionName, string moduleName)
    {
        var name = functionName.Split("#(", StringSplitOptions.None)[0];
        return IsTextFunction(name, moduleName, "TryConvertAsciiToUtf16")
            || IsTextFunction(name, moduleName, "TryConvertUnicodeToUtf16");
    }

    private bool TryPreencodeUtf16ConversionCall(
        SsaFunction function, string moduleName, SsaFunctionFactModel facts,
        IReadOnlyDictionary<string, SsaRValue> definitions, IReadOnlySet<string> constLocals,
        ISsaDirectCallOperation call, out SsaCallRValue rewritten)
    {
        rewritten = null!;
        if (call.Arguments.Count != 4 || !TryResolveUtf16ConversionVariant(call.FunctionName, moduleName)
            || call.Type.Kind != StarkTypeKind.Bool
            || call.Arguments[2].Type.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            || !HasConstMemoryProvenance(function, call.Arguments[2], definitions, constLocals, new HashSet<string>(StringComparer.Ordinal))
            || !TryGetEncodingPayload(call.Arguments[2], function, facts, definitions, new HashSet<string>(StringComparer.Ordinal), out var utf8, out var utf32))
        {
            return false;
        }
        var signatures = EnumerateRetargetNameCandidates(call.FunctionName, moduleName, "System.Text", "TryCopyPreencodedUtf16")
            .SelectMany(name => (_typeModel.Functions.TryGetValue(name, out var direct) ? new[] { direct } : [])
                .Concat(_typeModel.Overloads.GetValueOrDefault(name) ?? []))
            .Where(signature => signature.Parameters.Count == 6 && signature.ReturnType.Kind == StarkTypeKind.Bool
                && signature.Parameters[0].Type is { Kind: StarkTypeKind.RawPointer, IsMutablePointer: true, ElementType.BitWidth: 16 }
                && signature.Parameters[1].Type is { Kind: StarkTypeKind.Integer, BitWidth: 64, IsUnsigned: true }
                && IsReadonlyIntegerPointer(signature.Parameters[2].Type, 16)
                && signature.Parameters[3].Type is { Kind: StarkTypeKind.Integer, BitWidth: 64 }
                && signature.Parameters[4].Type is { Kind: StarkTypeKind.RawPointer, IsMutablePointer: true, ElementType.BitWidth: 64 }
                && signature.Parameters[5].Type.Kind == StarkTypeKind.Bool)
            .DistinctBy(signature => signature.Name).ToArray();
        if (signatures.Length != 1) return false;
        var payload = EncodeUtf16Payload(call.Arguments[2].Type.Kind, utf8, utf32);
        var arguments = new SsaValue[]
        {
            call.Arguments[0], call.Arguments[1],
            new SsaStaticArrayAddressValue(payload.Units.ToArray(), StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(16), isMutable: false)),
            new SsaIntegerConstant(payload.Units.Count, StarkTypeSymbols.Integer(64)), call.Arguments[3],
            new SsaBoolConstant(call.Arguments[2].Type.Kind == StarkTypeKind.Ascii)
        };
        int?[] originalIndexes = [0, 1, null, null, 3, null];
        rewritten = new SsaCallRValue(signatures[0].Name, arguments, call.Type,
            RetargetCallText(call.Text, "TryCopyPreencodedUtf16"),
            call.IndirectArgumentLocalNames is { } locals ? originalIndexes.Select(index => index is { } i ? locals[i] : null).ToArray() : null,
            call.SourceReturnType,
            call.IndirectArgumentAddresses is { } addresses ? originalIndexes.Select(index => index is { } i ? addresses[i] : null).ToArray() : null);
        return true;
    }

    private static bool IsReadonlyIntegerPointer(StarkTypeSymbol type, int width) =>
        type is { Kind: StarkTypeKind.RawPointer, IsMutablePointer: false, ElementType.Kind: StarkTypeKind.Integer }
        && type.ElementType.BitWidth == width;

    private static bool SameReceiverType(StarkTypeSymbol expected, StarkTypeSymbol actual)
    {
        // Borrowed parameters and addresses of owned locals have different SSA
        // pointer forms, but must refer to the same nominal receiver type.
        if (actual.Kind == StarkTypeKind.RawPointer && actual.ElementType is { } element) actual = element;
        return expected.Kind == StarkTypeKind.Named && actual.Kind == StarkTypeKind.Named
            && expected.NamedType == actual.NamedType;
    }

    private static (List<BigInteger> Units, List<BigInteger> Chunks, bool Ascii) EncodeUtf16Payload(
        StarkTypeKind kind, byte[] utf8, int[] utf32)
    {
        var units = new List<BigInteger>();
        var chunks = new List<BigInteger>();
        var ascii = true;
        if (kind == StarkTypeKind.Ascii)
        {
            ascii = utf8.All(value => value <= 127);
            for (var offset = 0; offset < utf8.Length;)
            {
                var end = offset + Utf8ChunkLength(utf8.AsSpan(offset), 2048);
                var before = units.Count;
                for (var index = offset; index < end;)
                {
                    var status = Rune.DecodeFromUtf8(utf8.AsSpan(index, end - index), out var scalar, out var consumed);
                    AppendUtf16(units, status == OperationStatus.Done ? scalar.Value : 65533);
                    index += status == OperationStatus.Done ? consumed : 1;
                }
                chunks.Add(units.Count - before);
                offset = end;
            }
        }
        else
        {
            ascii = utf32.All(value => value is >= 0 and <= 127);
            for (var offset = 0; offset < utf32.Length; offset += 1024)
            {
                var before = units.Count;
                foreach (var scalar in utf32.AsSpan(offset, Math.Min(1024, utf32.Length - offset)))
                {
                    AppendUtf16(units, scalar);
                }
                chunks.Add(units.Count - before);
            }
        }
        return (units, chunks, ascii);
    }

    // Match File's UTF-8 chunk boundaries, including invalid/truncated input.
    private static int Utf8ChunkLength(ReadOnlySpan<byte> source, int maximum)
    {
        var limit = Math.Min(source.Length, maximum);
        var read = 0;
        while (read < limit)
        {
            var first = source[read];
            var needed = first is >= 194 and <= 223 ? 2
                : first is >= 224 and <= 239 ? 3 : first is >= 240 and <= 244 ? 4 : 1;
            if (read + needed > limit) return read == 0 ? 1 : read;
            read += needed;
        }
        return read;
    }

    private static void AppendUtf16(List<BigInteger> units, int scalar)
    {
        if (!Rune.IsValid(scalar)) scalar = 65533;
        if (scalar <= 65535) units.Add(unchecked((short)scalar));
        else
        {
            scalar -= 65536;
            units.Add(unchecked((short)((scalar >> 10) + 55296)));
            units.Add(unchecked((short)((scalar & 1023) + 56320)));
        }
    }

    private static bool TryGetEncodingPayload(
        SsaValue value, SsaFunction function, SsaFunctionFactModel facts,
        IReadOnlyDictionary<string, SsaRValue> definitions, ISet<string> visited,
        out byte[] utf8, out int[] utf32)
    {
        utf8 = [];
        utf32 = [];
        if (value is SsaStringConstant literal
            && TextLiteralDecoder.TryDecode(literal.LiteralText,
                literal.LiteralText.StartsWith('\'') ? TextLiteralKind.Character : TextLiteralKind.String,
                out var decoded, out _))
        {
            utf8 = decoded.Utf8Bytes;
            utf32 = decoded.Utf32CodeUnits;
            return true;
        }
        if (value is not SsaValueReference reference || !visited.Add(reference.Name)) return false;
        if (facts.Values.TryGetValue(reference.Name, out var known)
            && known.TextLiteralPayloadKind == SsaFactLatticeKind.Known
            && known.TextLiteralPayload is { } payload)
        {
            utf8 = Convert.FromHexString(payload.Utf8PayloadHex);
            utf32 = payload.Utf32Length == 0 ? [] : payload.Utf32PayloadHex.Split(',')
                .Select(unit => int.Parse(unit, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
            return utf8.Length == payload.Utf8Length && utf32.Length == payload.Utf32Length;
        }
        if (!definitions.TryGetValue(reference.Name, out var definition)) return false;
        if (definition is SsaUseRValue use)
            return TryGetEncodingPayload(use.Value, function, facts, definitions, visited, out utf8, out utf32);
        if (definition is SsaLoadLocalRValue local && !LocalAddressMayBeObserved(function, local.LocalName))
        {
            var stores = function.Blocks.SelectMany(block => block.Instructions).OfType<SsaStoreLocalInstruction>()
                .Where(store => store.LocalName == local.LocalName).ToArray();
            return stores.Length == 1 && TryGetEncodingPayload(stores[0].Value, function, facts, definitions, visited, out utf8, out utf32);
        }
        if (definition is SsaTextSliceRValue slice
            && TryGetEncodingPayload(slice.TextValue, function, facts, definitions, visited, out utf8, out utf32)
            && TryResolveExactNonNegativeInteger(slice.Start, definitions, out var start)
            && TryResolveExactNonNegativeInteger(slice.Length, definitions, out var length)
            && start + length <= (slice.Type.Kind == StarkTypeKind.Ascii ? utf8.Length : utf32.Length))
        {
            if (slice.Type.Kind == StarkTypeKind.Ascii) utf8 = utf8.AsSpan((int)start, (int)length).ToArray();
            else utf32 = utf32.AsSpan((int)start, (int)length).ToArray();
            return true;
        }
        return false;
    }
}
