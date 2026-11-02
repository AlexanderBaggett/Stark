using System.Numerics;
using System.Text;

namespace Stark.Compiler;

internal sealed partial class SsaConstStdlibHelperSpecializer
{
    private static bool TryResolveConsoleWrite(string functionName, string moduleName,
        out bool buffered, out bool line, out bool error)
    {
        var name = functionName.Split("#(", StringSplitOptions.None)[0];
        if (moduleName == "System.Console" && !name.StartsWith("System.Console.", StringComparison.Ordinal))
            name = "System.Console." + name;
        buffered = name.StartsWith("System.Console.BufferedUtf8Writer.", StringComparison.Ordinal);
        var prefix = buffered ? "System.Console.BufferedUtf8Writer." : "System.Console.";
        var operation = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : "";
        line = operation is "WriteLine" or "WriteErrorLine";
        error = operation is "WriteError" or "WriteErrorLine";
        return operation is "Write" or "WriteLine" || !buffered && error;
    }

    private bool TryPreencodeConsoleCall(SsaFunction function, string moduleName, SsaFunctionFactModel facts,
        IReadOnlyDictionary<string, SsaRValue> definitions, IReadOnlySet<string> constLocals,
        ISsaDirectCallOperation call, out SsaCallRValue rewritten)
    {
        rewritten = null!;
        if (!TryResolveConsoleWrite(call.FunctionName, moduleName, out var buffered, out var line, out var error)
            || call.Arguments.Count != (buffered ? 2 : 1)
            // Windows immediate text writes distinguish console handles from byte sinks.
            || !buffered && IsWindowsTarget(_targetInfo)) return false;
        var text = call.Arguments[buffered ? 1 : 0];
        if (text.Type.Kind is not (StarkTypeKind.Ascii or StarkTypeKind.Unicode)
            || !HasConstMemoryProvenance(function, text, definitions, constLocals, new HashSet<string>(StringComparer.Ordinal))
            || !TryGetEncodingPayload(text, function, facts, definitions, new HashSet<string>(StringComparer.Ordinal),
                out var utf8, out var utf32)) return false;

        var bytes = new List<BigInteger>();
        if (text.Type.Kind == StarkTypeKind.Ascii)
            bytes.AddRange(utf8.Select(value => new BigInteger(unchecked((sbyte)value))));
        else
        {
            Span<byte> encoded = stackalloc byte[4];
            foreach (var scalar in utf32)
            {
                var rune = new Rune(Rune.IsValid(scalar) ? scalar : 65533);
                var count = rune.EncodeToUtf8(encoded);
                for (var i = 0; i < count; i++) bytes.Add(unchecked((sbyte)encoded[i]));
            }
        }
        if (line) bytes.Add(10);
        // Preserve the existing immediate path's chunk/syscall and failure boundaries.
        if (!buffered && bytes.Count > (text.Type.Kind == StarkTypeKind.Unicode ? 4096 : 8192)
            || !buffered && !line && text.Type.Kind == StarkTypeKind.Ascii) return false;

        var helper = buffered ? "BufferedUtf8Writer.WritePreencodedUtf8" : "WritePreencodedUtf8";
        var candidates = EnumerateRetargetNameCandidates(call.FunctionName, moduleName, "System.Console", helper)
            .SelectMany(name => (_typeModel.Functions.TryGetValue(name, out var direct) ? new[] { direct } : [])
                .Concat(_typeModel.Overloads.GetValueOrDefault(name) ?? []))
            .Where(signature => signature.Parameters.Count == 3 && HaveSameType(signature.ReturnType, call.Type)
                && IsReadonlyIntegerPointer(signature.Parameters[buffered ? 1 : 0].Type, 8)
                && signature.Parameters[buffered ? 2 : 1].Type is { Kind: StarkTypeKind.Integer, BitWidth: 64 }
                && (buffered ? SameReceiverType(signature.Parameters[0].Type, call.Arguments[0].Type)
                    : signature.Parameters[2].Type.Kind == StarkTypeKind.Bool))
            .DistinctBy(signature => signature.Name).ToArray();
        if (candidates.Length != 1) return false;
        var pointer = new SsaStaticArrayAddressValue(bytes.ToArray(),
            StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false));
        var length = new SsaIntegerConstant(bytes.Count, StarkTypeSymbols.Integer(64));
        SsaValue[] arguments = buffered ? [call.Arguments[0], pointer, length] : [pointer, length, new SsaBoolConstant(error)];
        rewritten = new SsaCallRValue(candidates[0].Name, arguments, call.Type, RetargetCallText(call.Text, "WritePreencodedUtf8"),
            call.IndirectArgumentLocalNames is null ? null : buffered ? [call.IndirectArgumentLocalNames[0], null, null] : [null, null, null],
            call.SourceReturnType,
            call.IndirectArgumentAddresses is null ? null : buffered ? [call.IndirectArgumentAddresses[0], null, null] : [null, null, null]);
        return true;
    }
}
