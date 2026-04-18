using System.IO;
using System.Globalization;
using System.Numerics;
using System.Text;
using Stark.Parsing;

namespace Stark.Compiler.LlvmIrEmission;

internal sealed class DebugMetadataEmitter
{
    private readonly string _defaultSourcePath;
    private readonly Func<StarkTypeSymbol, ConcreteTypeLayout?> _tryGetConcreteTypeLayout;
    private readonly List<string> _definitions = [];
    private readonly Dictionary<string, string> _fileRefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _typeRefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tupleRefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _subroutineTypeRefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _rangeRefs = new(StringComparer.Ordinal);
    private readonly string _compileUnitRef;
    private readonly string _debugInfoVersionRef;
    private readonly string _dwarfVersionRef;
    private readonly string _defaultFileRef;
    private readonly string _emptyTupleRef;
    private bool _hasFunctions;
    private int _nextMetadataId;

    public DebugMetadataEmitter(
        string defaultSourcePath,
        bool isOptimizedBuild,
        Func<StarkTypeSymbol, ConcreteTypeLayout?> tryGetConcreteTypeLayout)
    {
        _defaultSourcePath = string.IsNullOrWhiteSpace(defaultSourcePath)
            ? "module.stark"
            : defaultSourcePath;
        _tryGetConcreteTypeLayout = tryGetConcreteTypeLayout;
        _defaultFileRef = GetFileRef(_defaultSourcePath);
        _emptyTupleRef = CreateMetadata("!{}");
        _compileUnitRef = CreateMetadata(
            $"distinct !DICompileUnit(language: DW_LANG_C, file: {_defaultFileRef}, producer: \"Stark Compiler\", isOptimized: {isOptimizedBuild.ToString().ToLowerInvariant()}, runtimeVersion: 0, emissionKind: FullDebug)");
        _debugInfoVersionRef = CreateMetadata("!{i32 2, !\"Debug Info Version\", i32 3}");
        _dwarfVersionRef = CreateMetadata("!{i32 7, !\"Dwarf Version\", i32 5}");
    }

    public bool Enabled => true;

    public string EmptyTupleRef => _emptyTupleRef;

    public string? GetValueRangeMetadataRef(StarkTypeSymbol type)
    {
        if (!TryBuildValueRangeMetadata(type, out var metadataBody))
        {
            return null;
        }

        if (_rangeRefs.TryGetValue(metadataBody, out var existing))
        {
            return existing;
        }

        var rangeRef = CreateMetadata(metadataBody);
        _rangeRefs[metadataBody] = rangeRef;
        return rangeRef;
    }

    public DebugFunctionContext CreateFunctionContext(
        string sourceName,
        string linkageName,
        SourceLocation location,
        TypedFunctionSignature function)
    {
        _hasFunctions = true;

        var normalizedLocation = ResolveLocation(location);
        var fileRef = GetFileRef(normalizedLocation.FilePath);
        var subroutineTypeRef = GetSubroutineTypeRef(function);
        var subprogramRef = CreateMetadata(
            $"distinct !DISubprogram(name: \"{EscapeMetadataString(sourceName)}\", linkageName: \"{EscapeMetadataString(linkageName)}\", scope: {fileRef}, file: {fileRef}, line: {normalizedLocation.Line}, type: {subroutineTypeRef}, scopeLine: {normalizedLocation.Line}, spFlags: DISPFlagDefinition, unit: {_compileUnitRef}, retainedNodes: {_emptyTupleRef})");

        return new DebugFunctionContext(this, subprogramRef, fileRef, normalizedLocation);
    }

    public void EmitModuleMetadata(StringBuilder builder)
    {
        if (!_hasFunctions)
        {
            return;
        }

        builder.AppendLine($"!llvm.dbg.cu = !{{{_compileUnitRef}}}");
        builder.AppendLine($"!llvm.module.flags = !{{{_debugInfoVersionRef}, {_dwarfVersionRef}}}");
        foreach (var definition in _definitions)
        {
            builder.AppendLine(definition);
        }
    }

    public SourceLocation ResolveLocation(SourceLocation? location)
    {
        var filePath = string.IsNullOrWhiteSpace(location?.FilePath)
            ? _defaultSourcePath
            : location!.FilePath!;
        var line = location is { Line: > 0 } ? location.Line : 1;
        var column = location is { Column: > 0 } ? location.Column : 1;
        return new SourceLocation(filePath, line, column);
    }

    public string GetFileRef(string? filePath)
    {
        var normalizedPath = string.IsNullOrWhiteSpace(filePath)
            ? _defaultSourcePath
            : filePath!;
        if (_fileRefs.TryGetValue(normalizedPath, out var existing))
        {
            return existing;
        }

        var fileName = Path.GetFileName(normalizedPath);
        var directory = Path.GetDirectoryName(normalizedPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = normalizedPath;
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = ".";
        }

        var fileRef = CreateMetadata(
            $"!DIFile(filename: \"{EscapeMetadataString(fileName)}\", directory: \"{EscapeMetadataString(directory)}\")");
        _fileRefs[normalizedPath] = fileRef;
        return fileRef;
    }

    public string GetTypeRef(StarkTypeSymbol type)
    {
        if (type.Kind == StarkTypeKind.Void)
        {
            return "null";
        }

        var key = type.DisplayName;
        if (_typeRefs.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var typeRef = type.Kind switch
        {
            StarkTypeKind.Bool => CreateMetadata("!DIBasicType(name: \"bool\", size: 1, encoding: DW_ATE_boolean)"),
            StarkTypeKind.Integer when type.BitWidth is int bitWidth
                => CreateMetadata($"!DIBasicType(name: \"{EscapeMetadataString(type.DisplayName)}\", size: {bitWidth}, encoding: DW_ATE_signed)"),
            StarkTypeKind.Float when type.BitWidth is int bitWidth
                => CreateMetadata($"!DIBasicType(name: \"{EscapeMetadataString(type.DisplayName)}\", size: {bitWidth}, encoding: DW_ATE_float)"),
            StarkTypeKind.RawPointer => CreatePointerTypeRef(type),
            StarkTypeKind.FixedArray => CreateFixedArrayTypeRef(type),
            StarkTypeKind.Slice => CreateOpaqueCompositeTypeRef(type.DisplayName, type),
            StarkTypeKind.Ascii => CreateOpaqueCompositeTypeRef(type.DisplayName, type),
            StarkTypeKind.Unicode => CreateOpaqueCompositeTypeRef(type.DisplayName, type),
            StarkTypeKind.Named => CreateOpaqueCompositeTypeRef(type.DisplayName, type),
            StarkTypeKind.Null => CreatePointerTypeRef(StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: false)),
            _ => CreateOpaqueCompositeTypeRef(type.DisplayName, type)
        };

        _typeRefs[key] = typeRef;
        return typeRef;
    }

    public string CreateLocationRef(SourceLocation location, string scopeRef)
    {
        var normalizedLocation = ResolveLocation(location);
        return CreateMetadata(
            $"!DILocation(line: {normalizedLocation.Line}, column: {normalizedLocation.Column}, scope: {scopeRef})");
    }

    public string CreateParameterVariableRef(
        string name,
        StarkTypeSymbol type,
        int argIndex,
        string scopeRef,
        string fileRef,
        int line)
    {
        return CreateMetadata(
            $"!DILocalVariable(name: \"{EscapeMetadataString(name)}\", arg: {argIndex}, scope: {scopeRef}, file: {fileRef}, line: {line}, type: {GetTypeRef(type)})");
    }

    public string CreateLocalVariableRef(
        string name,
        StarkTypeSymbol type,
        string scopeRef,
        string fileRef,
        int line)
    {
        return CreateMetadata(
            $"!DILocalVariable(name: \"{EscapeMetadataString(name)}\", scope: {scopeRef}, file: {fileRef}, line: {line}, type: {GetTypeRef(type)})");
    }

    private string CreatePointerTypeRef(StarkTypeSymbol pointerType)
    {
        var pointeeRef = pointerType.ElementType is null
            ? "null"
            : GetTypeRef(pointerType.ElementType);
        var pointerBits = (_tryGetConcreteTypeLayout(pointerType)?.SizeBytes ?? 8) * 8;
        return CreateMetadata(
            $"!DIDerivedType(tag: DW_TAG_pointer_type, baseType: {pointeeRef}, size: {pointerBits})");
    }

    private string CreateFixedArrayTypeRef(StarkTypeSymbol arrayType)
    {
        if (arrayType.ElementType is null || arrayType.FixedLength is not int fixedLength)
        {
            return CreateOpaqueCompositeTypeRef(arrayType.DisplayName, arrayType);
        }

        var subrangeRef = CreateMetadata($"!DISubrange(count: {fixedLength})");
        var elementsRef = GetTupleRef([subrangeRef]);
        var sizeBits = (_tryGetConcreteTypeLayout(arrayType)?.SizeBytes ?? 0) * 8;
        return CreateMetadata(
            $"!DICompositeType(tag: DW_TAG_array_type, baseType: {GetTypeRef(arrayType.ElementType)}, size: {sizeBits}, elements: {elementsRef})");
    }

    private string CreateOpaqueCompositeTypeRef(string name, StarkTypeSymbol type)
    {
        var sizeBits = (_tryGetConcreteTypeLayout(type)?.SizeBytes ?? 0) * 8;
        return CreateMetadata(
            $"!DICompositeType(tag: DW_TAG_structure_type, name: \"{EscapeMetadataString(name)}\", file: {_defaultFileRef}, size: {sizeBits}, elements: {_emptyTupleRef})");
    }

    private string GetSubroutineTypeRef(TypedFunctionSignature function)
    {
        var key = $"{function.ReturnType.DisplayName}({string.Join(",", function.Parameters.Select(static parameter => parameter.Type.DisplayName))})";
        if (_subroutineTypeRefs.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var typeRefs = new List<string> { GetTypeRef(function.ReturnType) };
        typeRefs.AddRange(function.Parameters.Select(parameter => GetTypeRef(parameter.Type)));
        var tupleRef = GetTupleRef(typeRefs);
        var subroutineRef = CreateMetadata($"!DISubroutineType(types: {tupleRef})");
        _subroutineTypeRefs[key] = subroutineRef;
        return subroutineRef;
    }

    private string GetTupleRef(IReadOnlyList<string> items)
    {
        var key = string.Join("|", items);
        if (_tupleRefs.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var tupleRef = CreateMetadata("!{" + string.Join(", ", items) + "}");
        _tupleRefs[key] = tupleRef;
        return tupleRef;
    }

    private string CreateMetadata(string body)
    {
        var reference = "!" + _nextMetadataId++;
        _definitions.Add(reference + " = " + body);
        return reference;
    }

    private static bool TryBuildValueRangeMetadata(StarkTypeSymbol type, out string metadataBody)
    {
        metadataBody = string.Empty;

        if (!TryGetValueRange(type, out var bitWidth, out var min, out var max))
        {
            return false;
        }

        var valueCount = max - min + BigInteger.One;
        var domainSize = BigInteger.One << bitWidth;
        if (valueCount <= BigInteger.Zero || valueCount >= domainSize)
        {
            // LLVM rejects empty/full !range sets. For bool, Stark's i1
            // representation already constrains the value to the full bool
            // domain, so there is no valid extra !range metadata to emit.
            return false;
        }

        var llvmType = $"i{bitWidth}";
        var lower = FormatTwosComplementInteger(min, bitWidth);
        var upperExclusive = FormatTwosComplementInteger(max + BigInteger.One, bitWidth);
        metadataBody = $"!{{{llvmType} {lower}, {llvmType} {upperExclusive}}}";
        return true;
    }

    private static bool TryGetValueRange(
        StarkTypeSymbol type,
        out int bitWidth,
        out BigInteger min,
        out BigInteger max)
    {
        var normalizedType = type with
        {
            BorrowKind = StarkBorrowKind.None,
            AccessKind = StarkAccessKind.None,
            InitializationKind = StarkInitializationKind.None,
            IsMutableView = false
        };

        if (normalizedType.Kind == StarkTypeKind.Bool)
        {
            bitWidth = 1;
            min = BigInteger.Zero;
            max = BigInteger.One;
            return true;
        }

        if (normalizedType.Kind != StarkTypeKind.Integer || normalizedType.BitWidth is not int width || width <= 0)
        {
            bitWidth = default;
            min = default;
            max = default;
            return false;
        }

        bitWidth = width;
        if (normalizedType.RangeMin is not null && normalizedType.RangeMax is not null)
        {
            min = normalizedType.RangeMin.Value;
            max = normalizedType.RangeMax.Value;
            return true;
        }

        min = -(BigInteger.One << (width - 1));
        max = (BigInteger.One << (width - 1)) - BigInteger.One;
        return true;
    }

    private static string FormatTwosComplementInteger(BigInteger value, int bitWidth)
    {
        var domainSize = BigInteger.One << bitWidth;
        var normalized = value % domainSize;
        if (normalized < BigInteger.Zero)
        {
            normalized += domainSize;
        }

        var signedThreshold = BigInteger.One << (bitWidth - 1);
        if (normalized >= signedThreshold)
        {
            normalized -= domainSize;
        }

        return normalized.ToString(CultureInfo.InvariantCulture);
    }

    private static string EscapeMetadataString(string value)
    {
        return EscapeIrText(value).Replace("\n", "\\0A", StringComparison.Ordinal);
    }

    private static string EscapeIrText(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}

internal sealed class DebugFunctionContext
{
    private readonly DebugMetadataEmitter _owner;
    private readonly string _fileRef;
    private readonly Dictionary<(int Line, int Column), string> _locationRefs = [];
    private readonly Dictionary<(string Name, int ArgIndex), string> _parameterRefs = [];
    private readonly Dictionary<(string Name, int Line, int Column), string> _localRefs = [];

    public DebugFunctionContext(
        DebugMetadataEmitter owner,
        string subprogramRef,
        string fileRef,
        SourceLocation functionLocation)
    {
        _owner = owner;
        SubprogramRef = subprogramRef;
        _fileRef = fileRef;
        FunctionLocation = functionLocation;
    }

    public string SubprogramRef { get; }

    public SourceLocation FunctionLocation { get; }

    public string GetLocationRef(SourceLocation? location)
    {
        var normalizedLocation = _owner.ResolveLocation(location ?? FunctionLocation);
        var key = (normalizedLocation.Line, normalizedLocation.Column);
        if (_locationRefs.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var locationRef = _owner.CreateLocationRef(normalizedLocation, SubprogramRef);
        _locationRefs[key] = locationRef;
        return locationRef;
    }

    public string GetParameterVariableRef(string name, StarkTypeSymbol type, int argIndex)
    {
        var key = (name, argIndex);
        if (_parameterRefs.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var variableRef = _owner.CreateParameterVariableRef(
            name,
            type,
            argIndex,
            SubprogramRef,
            _fileRef,
            FunctionLocation.Line);
        _parameterRefs[key] = variableRef;
        return variableRef;
    }

    public string GetLocalVariableRef(string name, StarkTypeSymbol type, SourceLocation? location)
    {
        var normalizedLocation = _owner.ResolveLocation(location ?? FunctionLocation);
        var key = (name, normalizedLocation.Line, normalizedLocation.Column);
        if (_localRefs.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var variableRef = _owner.CreateLocalVariableRef(
            name,
            type,
            SubprogramRef,
            _owner.GetFileRef(normalizedLocation.FilePath),
            normalizedLocation.Line);
        _localRefs[key] = variableRef;
        return variableRef;
    }
}
