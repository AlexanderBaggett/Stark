using System.Text;

namespace Stark.Compiler;

internal sealed class LlvmIrEmitter
{
    private const string AsciiStringTypeName = "stark_ascii";
    private const string UnicodeStringTypeName = "stark_unicode";
    private readonly CompilationInput _input;
    private readonly SyntaxModel _syntaxModel;
    private readonly FunctionEffectModel _effectModel;
    private readonly TypeCheckModel _typeModel;
    private readonly AbiModel _abiModel;
    private readonly SsaIrModule _ssa;
    private readonly LlvmTargetInfo? _targetInfo;
    private readonly bool _internalizeModulePrivate;
    private readonly IReadOnlyDictionary<string, EmittedStringConstant> _stringConstants;

    public LlvmIrEmitter(
        CompilationInput input,
        SyntaxModel syntaxModel,
        FunctionEffectModel effectModel,
        TypeCheckModel typeModel,
        AbiModel abiModel,
        SsaIrModule ssa,
        LlvmTargetInfo? targetInfo = null,
        bool internalizeModulePrivate = false)
    {
        _input = input;
        _syntaxModel = syntaxModel;
        _effectModel = effectModel;
        _typeModel = typeModel;
        _abiModel = abiModel;
        _ssa = ssa;
        _targetInfo = targetInfo;
        _internalizeModulePrivate = internalizeModulePrivate;
        _stringConstants = CollectStringConstants(ssa);
    }

    public LlvmIrModule Emit()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"; ModuleID = '{_syntaxModel.ModuleName}'");
        builder.AppendLine($"source_filename = \"{EscapeFileName(_input.FilePath ?? $"{_syntaxModel.ModuleName}.stark")}\"");

        if (!string.IsNullOrWhiteSpace(_targetInfo?.DataLayout))
        {
            builder.AppendLine($"target datalayout = \"{EscapeFileName(_targetInfo!.DataLayout!)}\"");
        }

        builder.AppendLine($"target triple = \"{EscapeFileName(_targetInfo?.Triple ?? "unknown-unknown-unknown")}\"");
        builder.AppendLine();
        builder.AppendLine("; LLVM IR for the currently supported Stark SSA subset.");
        builder.AppendLine("; Unsupported constructs still fall back to declarations.");
        builder.AppendLine();

        EmitBuiltinTypeDefinitions(builder);
        EmitNamedTypeDefinitions(builder);
        EmitStringConstants(builder);
        EmitGlobals(builder);
        EmitIntrinsicDeclarations(builder);

        var rootFunctionNames = new HashSet<string>(
            _syntaxModel.Declarations
                .Where(static declaration => declaration.Function is not null)
                .Select(static declaration => declaration.Function!.Name),
            StringComparer.Ordinal);

        foreach (var declaration in _syntaxModel.Declarations.Where(static declaration => declaration.Function is not null))
        {
            var function = declaration.Function!;
            var effects = _effectModel.Functions[function.Name];
            var signature = _typeModel.Functions[function.Name];
            var abiSignature = _abiModel.Functions[function.Name];
            var ssaFunction = _ssa.Functions.FirstOrDefault(item => item.Name == function.Name);

            builder.AppendLine($"; visibility: {declaration.Visibility.ToString().ToLowerInvariant()}");

            var definitionInternalize = function.HasBody && ShouldInternalize(declaration.Visibility);

            if (function.HasBody
                && ssaFunction is not null
                && ssaFunction.SupportsDirectCodeGeneration)
            {
                try
                {
                    EmitFunctionDefinition(builder, definitionInternalize, signature, abiSignature, effects, ssaFunction);
                    builder.AppendLine();
                    continue;
                }
                catch (UnsupportedBodyEmissionException exception)
                {
                    builder.AppendLine($"; LLVM body emission fallback for {function.Name}: {exception.Message}");
                }
            }
            else if (function.HasBody)
            {
                builder.AppendLine($"; LLVM body emission pending for {function.Name}");
            }

            builder.AppendLine(BuildDeclarationSignature(false, signature, abiSignature, effects));
            builder.AppendLine();
        }

        foreach (var abiFunction in _abiModel.Functions.Values
                     .Where(function => !rootFunctionNames.Contains(function.Name))
                     .OrderBy(static function => function.Name, StringComparer.Ordinal))
        {
            if (!_typeModel.Functions.TryGetValue(abiFunction.Name, out var signature)
                || !_effectModel.Functions.TryGetValue(abiFunction.Name, out var effects))
            {
                continue;
            }

            builder.AppendLine($"; imported declaration: {abiFunction.Name}");
            builder.AppendLine(BuildDeclarationSignature(false, signature, abiFunction, effects));
            builder.AppendLine();
        }

        return new LlvmIrModule(_syntaxModel.ModuleName, builder.ToString().TrimEnd());
    }

    private void EmitGlobals(StringBuilder builder)
    {
        foreach (var declaration in _syntaxModel.Declarations)
        {
            if (declaration.Kind is not (DeclarationKind.GlobalConstant or DeclarationKind.GlobalVariable))
            {
                continue;
            }

            if (!_typeModel.Globals.TryGetValue(declaration.Name, out var type))
            {
                continue;
            }

            builder.AppendLine($"; visibility: {declaration.Visibility.ToString().ToLowerInvariant()}");
            builder.AppendLine($"@{declaration.Name} = external global {MapType(type)}");
            builder.AppendLine();
        }
    }

    private void EmitIntrinsicDeclarations(StringBuilder builder)
    {
        var declarations = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var binary in EnumerateBinaryOperations()
                     .Where(static binary => binary.Operator == SsaBinaryOperator.Exponent && binary.Type.Kind == StarkTypeKind.Float))
        {
            var llvmType = MapType(binary.Type);
            var suffix = GetFloatIntrinsicSuffix(binary.Type);
            declarations.Add($"declare {llvmType} @llvm.pow.{suffix}({llvmType}, {llvmType})");
        }

        foreach (var declaration in declarations)
        {
            builder.AppendLine(declaration);
        }

        if (declarations.Count != 0)
        {
            builder.AppendLine();
        }
    }

    private static void EmitBuiltinTypeDefinitions(StringBuilder builder)
    {
        builder.AppendLine($"%{AsciiStringTypeName} = type {{ ptr, i64 }}");
        builder.AppendLine($"%{UnicodeStringTypeName} = type {{ ptr, i64 }}");
        builder.AppendLine();
    }

    private void EmitNamedTypeDefinitions(StringBuilder builder)
    {
        var emittedAny = false;

        foreach (var namedType in _typeModel.NamedTypes.Values
                     .Where(static type => type.Kind is DeclarationKind.Struct or DeclarationKind.Record)
                     .OrderBy(static type => type.Name, StringComparer.Ordinal))
        {
            emittedAny = true;
            var fields = namedType.OrderedFields.Count == 0
                ? string.Empty
                : string.Join(", ", namedType.OrderedFields.Select(field => MapType(field.Type)));
            builder.AppendLine($"%{EscapeIdentifier(namedType.Name)} = type {{ {fields} }}");
        }

        if (emittedAny)
        {
            builder.AppendLine();
        }
    }

    private void EmitStringConstants(StringBuilder builder)
    {
        foreach (var constant in _stringConstants.Values.OrderBy(static item => item.SymbolName, StringComparer.Ordinal))
        {
            builder.AppendLine($"@{constant.SymbolName} = private unnamed_addr constant {constant.ArrayType} {constant.Initializer}");
        }

        if (_stringConstants.Count != 0)
        {
            builder.AppendLine();
        }
    }

    private void EmitFunctionDefinition(
        StringBuilder builder,
        bool internalize,
        TypedFunctionSignature function,
        AbiFunctionSignature abiFunction,
        FunctionEffectProfile effects,
        SsaFunction ssaFunction)
    {
        builder.AppendLine(BuildDefinitionSignature(internalize, function, abiFunction, effects));
        builder.AppendLine("{");

        var bodyEmitter = new FunctionBodyEmitter(builder, function, abiFunction, _abiModel, ssaFunction, _stringConstants, MapType);
        bodyEmitter.Emit();

        builder.AppendLine("}");
    }

    private string BuildDeclarationSignature(bool internalize, TypedFunctionSignature function, AbiFunctionSignature abiFunction, FunctionEffectProfile effects)
    {
        var segments = new List<string> { "declare" };

        if (internalize)
        {
            segments.Add("internal");
        }

        if (effects.UseFastCallingConvention)
        {
            segments.Add("fastcc");
        }

        segments.Add(MapType(abiFunction.LlvmReturnType));
        segments.Add($"@{EscapeIdentifier(abiFunction.SymbolName)}({string.Join(", ", abiFunction.Parameters.Select(parameter => RenderAbiParameter(parameter, includeName: false)))})");

        var attributes = BuildFunctionAttributes(effects);
        if (!string.IsNullOrWhiteSpace(attributes))
        {
            segments.Add(attributes);
        }

        return string.Join(" ", segments);
    }

    private string BuildDefinitionSignature(bool internalize, TypedFunctionSignature function, AbiFunctionSignature abiFunction, FunctionEffectProfile effects)
    {
        var segments = new List<string> { "define" };

        if (internalize)
        {
            segments.Add("internal");
        }

        if (effects.UseFastCallingConvention)
        {
            segments.Add("fastcc");
        }

        segments.Add(MapType(abiFunction.LlvmReturnType));
        segments.Add($"@{EscapeIdentifier(abiFunction.SymbolName)}({string.Join(", ", abiFunction.Parameters.Select(parameter => RenderAbiParameter(parameter, includeName: true)))})");

        var attributes = BuildFunctionAttributes(effects);
        if (!string.IsNullOrWhiteSpace(attributes))
        {
            segments.Add(attributes);
        }

        return string.Join(" ", segments);
    }

    private string RenderAbiParameter(AbiParameterSymbol parameter, bool includeName)
    {
        var segments = new List<string> { MapType(parameter.LlvmType) };
        segments.AddRange(DeriveAbiParameterAttributes(parameter));

        if (includeName)
        {
            segments.Add($"%{EscapeIdentifier(parameter.LlvmName)}");
        }

        return string.Join(" ", segments);
    }

    private IReadOnlyList<string> DeriveAbiParameterAttributes(AbiParameterSymbol parameter)
    {
        var attributes = new List<string>();

        if (parameter.Kind == AbiParameterKind.SRet)
        {
            attributes.Add("noalias");
            attributes.Add($"sret({MapType(parameter.SourceType)})");
            attributes.Add("nonnull");
            if (TryGetConcreteTypeLayout(parameter.SourceType) is { } sretLayout)
            {
                attributes.Add($"dereferenceable({sretLayout.SizeBytes})");
                if (sretLayout.AlignmentBytes > 1)
                {
                    attributes.Add($"align {sretLayout.AlignmentBytes}");
                }
            }

            return attributes;
        }

        if (parameter.Kind == AbiParameterKind.IndirectIn)
        {
            attributes.Add("nonnull");
            if (parameter.SourceType.InitializationKind != StarkInitializationKind.None)
            {
                attributes.Add("noalias");
                attributes.Add("writeonly");
            }
            else
            {
                attributes.Add("noalias");
                attributes.Add("readonly");
            }

            if (TryGetConcreteTypeLayout(parameter.SourceType) is { } indirectLayout)
            {
                attributes.Add($"dereferenceable({indirectLayout.SizeBytes})");
                if (indirectLayout.AlignmentBytes > 1)
                {
                    attributes.Add($"align {indirectLayout.AlignmentBytes}");
                }
            }

            return attributes;
        }

        if (parameter.LlvmType.Kind != StarkTypeKind.RawPointer)
        {
            return attributes;
        }

        if (parameter.SourceType.BorrowKind != StarkBorrowKind.None || parameter.SourceType.InitializationKind != StarkInitializationKind.None)
        {
            attributes.Add("nonnull");
        }

        if (parameter.SourceType.InitializationKind != StarkInitializationKind.None)
        {
            attributes.Add("noalias");
            attributes.Add("writeonly");
        }
        else if (parameter.SourceType.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode
                 || (parameter.SourceType.Kind == StarkTypeKind.RawPointer && !parameter.SourceType.IsMutablePointer)
                 || (parameter.SourceType.BorrowKind != StarkBorrowKind.None && !parameter.SourceType.IsMutableView))
        {
            attributes.Add("readonly");
        }

        return attributes;
    }

    private static string BuildFunctionAttributes(FunctionEffectProfile effects)
    {
        var attributes = new List<string>();

        if (effects.NoUnwind)
        {
            attributes.Add("nounwind");
        }

        if (effects.WillReturn)
        {
            attributes.Add("willreturn");
        }

        if (effects.MustProgress)
        {
            attributes.Add("mustprogress");
        }

        if (effects.NoSync)
        {
            attributes.Add("nosync");
        }

        if (effects.NoFree)
        {
            attributes.Add("nofree");
        }

        if (effects.IsPure)
        {
            attributes.Add(effects.ReadsArgumentMemory ? "memory(argmem: read)" : "memory(none)");
        }

        if (effects.IsHot)
        {
            attributes.Add("hot");
        }

        if (effects.IsCold)
        {
            attributes.Add("cold");
        }

        attributes.Add(effects.InlinePreference switch
        {
            InlinePreference.Inline => "alwaysinline",
            InlinePreference.NoInline => "noinline",
            _ => "inlinehint"
        });

        return string.Join(" ", attributes);
    }

    private bool ShouldInternalize(StarkVisibility visibility) => _internalizeModulePrivate && visibility == StarkVisibility.Module;

    private IEnumerable<SsaBinaryRValue> EnumerateBinaryOperations()
    {
        return _ssa.Functions
            .SelectMany(static function => function.Blocks)
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(static instruction => instruction.Value)
            .OfType<SsaBinaryRValue>();
    }

    private string MapType(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.Void => "void",
            StarkTypeKind.Bool => "i1",
            StarkTypeKind.Integer => $"i{type.BitWidth}",
            StarkTypeKind.Float when type.BitWidth == 16 => "half",
            StarkTypeKind.Float when type.BitWidth == 32 => "float",
            StarkTypeKind.Float when type.BitWidth == 64 => "double",
            StarkTypeKind.Float when type.BitWidth == 80 => "x86_fp80",
            StarkTypeKind.Float when type.BitWidth == 128 => "fp128",
            StarkTypeKind.RawPointer => "ptr",
            StarkTypeKind.FixedArray when type.ElementType is not null && type.FixedLength is int fixedLength => $"[{fixedLength} x {MapType(type.ElementType)}]",
            StarkTypeKind.Slice => "{ ptr, i64 }",
            StarkTypeKind.Ascii => $"%{AsciiStringTypeName}",
            StarkTypeKind.Unicode => $"%{UnicodeStringTypeName}",
            StarkTypeKind.Named when type.NamedType is not null
                                     && _typeModel.NamedTypes.TryGetValue(type.NamedType, out var namedType)
                                     && namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record
                => $"%{EscapeIdentifier(type.NamedType)}",
            StarkTypeKind.Named => "ptr",
            StarkTypeKind.Null => "ptr",
            _ => "ptr"
        };
    }

    private static string GetFloatIntrinsicSuffix(StarkTypeSymbol type)
    {
        return type.BitWidth switch
        {
            16 => "f16",
            32 => "f32",
            64 => "f64",
            80 => "f80",
            128 => "f128",
            _ => throw new InvalidOperationException($"Unsupported float intrinsic width '{type.BitWidth}'.")
        };
    }

    private static string EscapeFileName(string filePath)
    {
        return filePath.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string EscapeIdentifier(string identifier)
    {
        var builder = new StringBuilder(identifier.Length);
        foreach (var ch in identifier)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        return builder.ToString();
    }

    private static IReadOnlyDictionary<string, EmittedStringConstant> CollectStringConstants(SsaIrModule ssa)
    {
        var result = new Dictionary<string, EmittedStringConstant>(StringComparer.Ordinal);
        var index = 0;

        foreach (var function in ssa.Functions)
        {
            foreach (var block in function.Blocks)
            {
                foreach (var phi in block.Phis)
                {
                    foreach (var incoming in phi.Incomings)
                    {
                        AddStringConstant(incoming.Value, result, ref index);
                    }
                }

                foreach (var instruction in block.Instructions)
                {
                    switch (instruction)
                    {
                        case SsaValueInstruction valueInstruction:
                            AddStringConstant(valueInstruction.Value, result, ref index);
                            break;
                        case SsaStoreGlobalInstruction storeGlobal:
                            AddStringConstant(storeGlobal.Value, result, ref index);
                            break;
                    }
                }

                AddStringConstant(block.Terminator.Condition, result, ref index);
                AddStringConstant(block.Terminator.Value, result, ref index);
            }
        }

        return result;
    }

    private static void AddStringConstant(object? source, Dictionary<string, EmittedStringConstant> constants, ref int index)
    {
        switch (source)
        {
            case null:
                return;
            case SsaStringConstant text:
                AddStringLiteral(text.LiteralText, constants, ref index);
                return;
            case SsaUseRValue use:
                AddStringConstant(use.Value, constants, ref index);
                return;
            case SsaUnaryRValue unary:
                AddStringConstant(unary.Operand, constants, ref index);
                return;
            case SsaBinaryRValue binary:
                AddStringConstant(binary.Left, constants, ref index);
                AddStringConstant(binary.Right, constants, ref index);
                return;
            case SsaCallRValue call:
                foreach (var argument in call.Arguments)
                {
                    AddStringConstant(argument, constants, ref index);
                }

                return;
            case SsaConvertRValue convert:
                AddStringConstant(convert.Operand, constants, ref index);
                return;
            case SsaExtractFieldRValue extract:
                AddStringConstant(extract.Target, constants, ref index);
                return;
            case SsaInsertFieldRValue insert:
                AddStringConstant(insert.Target, constants, ref index);
                AddStringConstant(insert.Value, constants, ref index);
                return;
            case SsaExtractIndexRValue extractIndex:
                AddStringConstant(extractIndex.Target, constants, ref index);
                return;
            case SsaInsertIndexRValue insertIndex:
                AddStringConstant(insertIndex.Target, constants, ref index);
                AddStringConstant(insertIndex.Value, constants, ref index);
                return;
            case SsaLoadSliceElementRValue loadSlice:
                AddStringConstant(loadSlice.Slice, constants, ref index);
                AddStringConstant(loadSlice.Index, constants, ref index);
                return;
            case SsaFieldAddressRValue fieldAddress:
                AddStringConstant(fieldAddress.Address, constants, ref index);
                return;
            case SsaElementAddressRValue elementAddress:
                AddStringConstant(elementAddress.Address, constants, ref index);
                AddStringConstant(elementAddress.Index, constants, ref index);
                return;
            case SsaSliceElementAddressRValue sliceElementAddress:
                AddStringConstant(sliceElementAddress.Slice, constants, ref index);
                AddStringConstant(sliceElementAddress.Index, constants, ref index);
                return;
            case SsaLoadIndirectRValue loadIndirect:
                AddStringConstant(loadIndirect.Address, constants, ref index);
                return;
        }
    }

    private static void AddStringLiteral(string literalText, Dictionary<string, EmittedStringConstant> constants, ref int index)
    {
        if (constants.ContainsKey(literalText))
        {
            return;
        }

        var bytes = DecodeStringLiteral(literalText);
        var terminated = new byte[bytes.Length + 1];
        bytes.CopyTo(terminated, 0);

        constants[literalText] = new EmittedStringConstant(
            SymbolName: $".str.{index++}",
            ArrayType: $"[{terminated.Length} x i8]",
            Initializer: EncodeLlvmByteString(terminated),
            DataLength: bytes.Length);
    }

    private static byte[] DecodeStringLiteral(string literalText)
    {
        var content = literalText.Length >= 2 ? literalText[1..^1] : literalText;
        var chars = new List<char>();

        for (var index = 0; index < content.Length; index++)
        {
            var ch = content[index];
            if (ch != '\\')
            {
                chars.Add(ch);
                continue;
            }

            if (index + 1 >= content.Length)
            {
                chars.Add('\\');
                break;
            }

            index++;
            var escape = content[index];
            switch (escape)
            {
                case '\\':
                    chars.Add('\\');
                    break;
                case '"':
                    chars.Add('"');
                    break;
                case 'n':
                    chars.Add('\n');
                    break;
                case 'r':
                    chars.Add('\r');
                    break;
                case 't':
                    chars.Add('\t');
                    break;
                case '0':
                    chars.Add('\0');
                    break;
                case 'u' when index + 4 < content.Length:
                    var hex = content.Substring(index + 1, 4);
                    chars.Add((char)Convert.ToInt32(hex, 16));
                    index += 4;
                    break;
                default:
                    chars.Add(escape);
                    break;
            }
        }

        return Encoding.UTF8.GetBytes(chars.ToArray());
    }

    private static string EncodeLlvmByteString(byte[] bytes)
    {
        var builder = new StringBuilder();
        builder.Append("c\"");
        foreach (var value in bytes)
        {
            if (value >= 0x20 && value <= 0x7E && value is not (byte)'\\' and not (byte)'"')
            {
                builder.Append((char)value);
            }
            else
            {
                builder.Append('\\');
                builder.Append(value.ToString("X2"));
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private ConcreteTypeLayout? TryGetConcreteTypeLayout(StarkTypeSymbol type)
    {
        return ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(type, _typeModel.NamedTypes);
    }

    private sealed class FunctionBodyEmitter
    {
        private readonly StringBuilder _builder;
        private readonly TypedFunctionSignature _function;
        private readonly AbiFunctionSignature _abiFunction;
        private readonly AbiModel _abiModel;
        private readonly SsaFunction _ssaFunction;
        private readonly IReadOnlyDictionary<string, EmittedStringConstant> _stringConstants;
        private readonly Func<StarkTypeSymbol, string> _mapType;
        private readonly HashSet<string> _allocatedLocalSlots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _materializedParameters = new(StringComparer.Ordinal);
        private int _nextAbiTempId;

        public FunctionBodyEmitter(
            StringBuilder builder,
            TypedFunctionSignature function,
            AbiFunctionSignature abiFunction,
            AbiModel abiModel,
            SsaFunction ssaFunction,
            IReadOnlyDictionary<string, EmittedStringConstant> stringConstants,
            Func<StarkTypeSymbol, string> mapType)
        {
            _builder = builder;
            _function = function;
            _abiFunction = abiFunction;
            _abiModel = abiModel;
            _ssaFunction = ssaFunction;
            _stringConstants = stringConstants;
            _mapType = mapType;
        }

        public void Emit()
        {
            if (_ssaFunction.Blocks.Count == 0)
            {
                EmitFallbackTerminal();
                return;
            }

            foreach (var block in _ssaFunction.Blocks)
            {
                AppendLine($"{FormatBlockLabel(block.Id)}:");

                if (block.Id == _ssaFunction.EntryBlockId)
                {
                    EmitEntryParameterMaterialization();
                }

                foreach (var phi in block.Phis)
                {
                    EmitPhi(phi);
                }

                foreach (var instruction in block.Instructions)
                {
                    EmitInstruction(instruction);
                }

                EmitTerminator(block.Terminator);
                AppendLine(string.Empty);
            }
        }

        private void EmitPhi(SsaPhi phi)
        {
            var incoming = string.Join(
                ", ",
                phi.Incomings.Select(entry => $"[ {FormatValue(entry.Value)}, %{FormatBlockLabel(entry.PredecessorBlockId)} ]"));
            AppendLine($"  %{EscapeIdentifier(phi.ResultName)} = phi {MapType(phi.Type)} {incoming}");
        }

        private void EmitInstruction(SsaInstruction instruction)
        {
            switch (instruction)
            {
                case SsaValueInstruction valueInstruction:
                    EmitValueInstruction(valueInstruction);
                    return;
                case SsaAllocateLocalInstruction allocateLocal:
                    EmitAllocateLocal(allocateLocal);
                    return;
                case SsaStoreLocalInstruction storeLocal:
                    EmitStoreLocal(storeLocal);
                    return;
                case SsaStoreIndirectInstruction storeIndirect:
                    EmitStoreIndirect(storeIndirect);
                    return;
                case SsaStoreGlobalInstruction storeGlobal:
                    AppendLine(
                        $"  store {MapType(storeGlobal.GlobalType)} {FormatValue(storeGlobal.Value)}, ptr @{storeGlobal.GlobalName}");
                    return;
                default:
                    throw new UnsupportedBodyEmissionException($"Unsupported SSA instruction '{instruction.GetType().Name}'.");
            }
        }

        private void EmitValueInstruction(SsaValueInstruction instruction)
        {
            var result = $"%{EscapeIdentifier(instruction.ResultName)}";
            switch (instruction.Value)
            {
                case SsaUseRValue use:
                    AppendLine($"  {result} = add {MapType(use.Type)} {FormatValue(use.Value)}, 0");
                    return;
                case SsaLoadGlobalRValue load:
                    AppendLine($"  {result} = load {MapType(load.Type)}, ptr @{load.GlobalName}");
                    return;
                case SsaLoadLocalRValue loadLocal:
                    EnsureLocalSlotExists(loadLocal.LocalName, loadLocal.Type);
                    AppendLine($"  {result} = load {MapType(loadLocal.Type)}, ptr %{EscapeIdentifier($"slot_{loadLocal.LocalName}")}");
                    return;
                case SsaConvertRValue convert:
                    EmitConvert(result, convert);
                    return;
                case SsaExtractFieldRValue extract:
                    AppendLine($"  {result} = extractvalue {MapType(extract.Target.Type)} {FormatValue(extract.Target)}, {extract.FieldIndex}");
                    return;
                case SsaInsertFieldRValue insert:
                    AppendLine($"  {result} = insertvalue {MapType(insert.Target.Type)} {FormatValue(insert.Target)}, {MapType(insert.Value.Type)} {FormatValue(insert.Value)}, {insert.FieldIndex}");
                    return;
                case SsaExtractIndexRValue extractIndex:
                    AppendLine($"  {result} = extractvalue {MapType(extractIndex.Target.Type)} {FormatValue(extractIndex.Target)}, {extractIndex.ElementIndex}");
                    return;
                case SsaInsertIndexRValue insertIndex:
                    AppendLine($"  {result} = insertvalue {MapType(insertIndex.Target.Type)} {FormatValue(insertIndex.Target)}, {MapType(insertIndex.Value.Type)} {FormatValue(insertIndex.Value)}, {insertIndex.ElementIndex}");
                    return;
                case SsaMakeSliceFromLocalRValue makeSlice:
                    EmitMakeSliceFromLocal(result, makeSlice);
                    return;
                case SsaLoadSliceElementRValue loadSlice:
                    EmitLoadSliceElement(result, loadSlice);
                    return;
                case SsaAddressOfLocalRValue addressOfLocal:
                    EmitAddressOfLocal(result, addressOfLocal);
                    return;
                case SsaFieldAddressRValue fieldAddress:
                    EmitFieldAddress(result, fieldAddress);
                    return;
                case SsaElementAddressRValue elementAddress:
                    EmitElementAddress(result, elementAddress);
                    return;
                case SsaSliceElementAddressRValue sliceElementAddress:
                    EmitSliceElementAddress(result, sliceElementAddress);
                    return;
                case SsaLoadIndirectRValue loadIndirect:
                    AppendLine($"  {result} = load {MapType(loadIndirect.Type)}, ptr {FormatValue(loadIndirect.Address)}");
                    return;
                case SsaUnaryRValue unary:
                    EmitUnary(result, unary);
                    return;
                case SsaBinaryRValue binary:
                    EmitBinary(result, binary);
                    return;
                case SsaCallRValue call:
                    EmitCall(result, call);
                    return;
                default:
                    throw new UnsupportedBodyEmissionException($"Unsupported SSA rvalue '{instruction.Value.GetType().Name}'.");
            }
        }

        private void EmitConvert(string result, SsaConvertRValue convert)
        {
            var sourceType = convert.Operand.Type;
            var targetType = convert.TargetType;

            if (sourceType.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.Integer)
            {
                if (sourceType.BitWidth == targetType.BitWidth)
                {
                    AppendLine($"  {result} = add {MapType(targetType)} {FormatValue(convert.Operand)}, 0");
                    return;
                }

                var opcode = sourceType.BitWidth < targetType.BitWidth ? "sext" : "trunc";
                AppendLine($"  {result} = {opcode} {MapType(sourceType)} {FormatValue(convert.Operand)} to {MapType(targetType)}");
                return;
            }

            if (sourceType.Kind == StarkTypeKind.Integer && targetType.Kind == StarkTypeKind.Float)
            {
                AppendLine($"  {result} = sitofp {MapType(sourceType)} {FormatValue(convert.Operand)} to {MapType(targetType)}");
                return;
            }

            if (sourceType.Kind == StarkTypeKind.Float && targetType.Kind == StarkTypeKind.Float)
            {
                if (sourceType.BitWidth == targetType.BitWidth)
                {
                    AppendLine($"  {result} = fadd {MapType(targetType)} {FormatValue(convert.Operand)}, 0.0");
                    return;
                }

                var opcode = sourceType.BitWidth < targetType.BitWidth ? "fpext" : "fptrunc";
                AppendLine($"  {result} = {opcode} {MapType(sourceType)} {FormatValue(convert.Operand)} to {MapType(targetType)}");
                return;
            }

            throw new UnsupportedBodyEmissionException(
                $"Unsupported SSA conversion from '{sourceType.DisplayName}' to '{targetType.DisplayName}'.");
        }

        private void EmitUnary(string result, SsaUnaryRValue unary)
        {
            switch (unary.Operator)
            {
                case SsaUnaryOperator.Negate when unary.Type.Kind == StarkTypeKind.Integer:
                    AppendLine($"  {result} = sub {MapType(unary.Type)} 0, {FormatValue(unary.Operand)}");
                    return;
                case SsaUnaryOperator.Negate when unary.Type.Kind == StarkTypeKind.Float:
                    AppendLine($"  {result} = fneg {MapType(unary.Type)} {FormatValue(unary.Operand)}");
                    return;
                case SsaUnaryOperator.LogicalNot:
                    AppendLine($"  {result} = xor i1 {FormatValue(unary.Operand)}, true");
                    return;
                case SsaUnaryOperator.BitwiseNot:
                    AppendLine($"  {result} = xor {MapType(unary.Type)} {FormatValue(unary.Operand)}, -1");
                    return;
                default:
                    throw new UnsupportedBodyEmissionException($"Unsupported SSA unary operator '{unary.Operator}'.");
            }
        }

        private void EmitBinary(string result, SsaBinaryRValue binary)
        {
            if (binary.Type.Kind == StarkTypeKind.Integer)
            {
                var opcode = binary.Operator switch
                {
                    SsaBinaryOperator.Add => "add",
                    SsaBinaryOperator.Subtract => "sub",
                    SsaBinaryOperator.Multiply => "mul",
                    SsaBinaryOperator.Divide => "sdiv",
                    SsaBinaryOperator.Modulo => "srem",
                    SsaBinaryOperator.BitwiseAnd => "and",
                    SsaBinaryOperator.BitwiseXor => "xor",
                    SsaBinaryOperator.BitwiseOr => "or",
                    SsaBinaryOperator.ShiftLeft => "shl",
                    SsaBinaryOperator.ShiftRight => "ashr",
                    _ => string.Empty
                };

                if (!string.IsNullOrEmpty(opcode))
                {
                    AppendLine($"  {result} = {opcode} {MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                    return;
                }
            }

            if (binary.Type.Kind == StarkTypeKind.Float)
            {
                var opcode = binary.Operator switch
                {
                    SsaBinaryOperator.Add => "fadd",
                    SsaBinaryOperator.Subtract => "fsub",
                    SsaBinaryOperator.Multiply => "fmul",
                    SsaBinaryOperator.Divide => "fdiv",
                    SsaBinaryOperator.Modulo => "frem",
                    _ => string.Empty
                };

                if (!string.IsNullOrEmpty(opcode))
                {
                    AppendLine($"  {result} = {opcode} {MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                    return;
                }

                if (binary.Operator == SsaBinaryOperator.Exponent)
                {
                    EmitFloatExponent(result, binary);
                    return;
                }
            }

            if (binary.Type.Kind == StarkTypeKind.Bool)
            {
                if (binary.Left.Type.Kind == StarkTypeKind.Integer || binary.Left.Type.Kind == StarkTypeKind.Bool)
                {
                    var predicate = binary.Operator switch
                    {
                        SsaBinaryOperator.Equal => "eq",
                        SsaBinaryOperator.NotEqual => "ne",
                        SsaBinaryOperator.LessThan => "slt",
                        SsaBinaryOperator.LessThanOrEqual => "sle",
                        SsaBinaryOperator.GreaterThan => "sgt",
                        SsaBinaryOperator.GreaterThanOrEqual => "sge",
                        _ => string.Empty
                    };

                    if (!string.IsNullOrEmpty(predicate))
                    {
                        AppendLine($"  {result} = icmp {predicate} {MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                        return;
                    }
                }

                if (binary.Left.Type.Kind == StarkTypeKind.Float)
                {
                    var predicate = binary.Operator switch
                    {
                        SsaBinaryOperator.Equal => "oeq",
                        SsaBinaryOperator.NotEqual => "one",
                        SsaBinaryOperator.LessThan => "olt",
                        SsaBinaryOperator.LessThanOrEqual => "ole",
                        SsaBinaryOperator.GreaterThan => "ogt",
                        SsaBinaryOperator.GreaterThanOrEqual => "oge",
                        _ => string.Empty
                    };

                    if (!string.IsNullOrEmpty(predicate))
                    {
                        AppendLine($"  {result} = fcmp {predicate} {MapType(binary.Left.Type)} {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                        return;
                    }
                }

                if (binary.Left.Type.Kind == StarkTypeKind.RawPointer)
                {
                    var predicate = binary.Operator switch
                    {
                        SsaBinaryOperator.Equal => "eq",
                        SsaBinaryOperator.NotEqual => "ne",
                        _ => string.Empty
                    };

                    if (!string.IsNullOrEmpty(predicate))
                    {
                        AppendLine($"  {result} = icmp {predicate} ptr {FormatValue(binary.Left)}, {FormatValue(binary.Right)}");
                        return;
                    }
                }
            }

            throw new UnsupportedBodyEmissionException(
                $"Unsupported SSA binary operator '{binary.Operator}' for '{binary.Left.Type.DisplayName}'.");
        }

        private void EmitFloatExponent(string result, SsaBinaryRValue binary)
        {
            var llvmType = MapType(binary.Left.Type);
            var intrinsicName = $"@llvm.pow.{LlvmIrEmitter.GetFloatIntrinsicSuffix(binary.Left.Type)}";
            AppendLine($"  {result} = call {llvmType} {intrinsicName}({llvmType} {FormatValue(binary.Left)}, {llvmType} {FormatValue(binary.Right)})");
        }

        private void EmitCall(string result, SsaCallRValue call)
        {
            if (!_abiModel.Functions.TryGetValue(call.FunctionName, out var abiCallee))
            {
                throw new UnsupportedBodyEmissionException($"Missing ABI lowering for call target '{call.FunctionName}'.");
            }

            if (IsStringType(call.Type) && abiCallee.LlvmReturnType.Kind == StarkTypeKind.RawPointer)
            {
                throw new UnsupportedBodyEmissionException(
                    $"FFI string returns are not yet supported for '{call.FunctionName}'.");
            }

            var arguments = new List<string>();
            string? indirectReturnSlot = null;

            if (abiCallee.ReturnsIndirect)
            {
                indirectReturnSlot = $"%{EscapeIdentifier(CreateAbiTempName("callret_slot"))}";
                AppendLine($"  {indirectReturnSlot} = alloca {MapType(call.Type)}");
                arguments.Add($"ptr sret({MapType(call.Type)}) {indirectReturnSlot}");
            }

            var userParameters = abiCallee.UserParameters;
            if (userParameters.Count != call.Arguments.Count)
            {
                throw new UnsupportedBodyEmissionException(
                    $"ABI parameter count mismatch for '{call.FunctionName}': expected {userParameters.Count}, got {call.Arguments.Count}.");
            }

            for (var index = 0; index < userParameters.Count; index++)
            {
                var parameter = userParameters[index];
                var argument = call.Arguments[index];

                if (parameter.Kind == AbiParameterKind.Direct)
                {
                    arguments.Add(RenderDirectArgument(parameter, argument));
                    continue;
                }

                var tempSlot = $"%{EscapeIdentifier(CreateAbiTempName($"callarg_{parameter.SourceName}"))}";
                AppendLine($"  {tempSlot} = alloca {MapType(parameter.SourceType)}");
                AppendLine($"  store {MapType(parameter.SourceType)} {FormatValue(argument)}, ptr {tempSlot}");
                arguments.Add($"ptr {tempSlot}");
            }

            var renderedArguments = string.Join(", ", arguments);

            if (abiCallee.ReturnsIndirect)
            {
                AppendLine($"  call void @{EscapeIdentifier(abiCallee.SymbolName)}({renderedArguments})");
                AppendLine($"  {result} = load {MapType(call.Type)}, ptr {indirectReturnSlot}");
                return;
            }

            if (call.Type.Kind == StarkTypeKind.Void)
            {
                AppendLine($"  call void @{EscapeIdentifier(abiCallee.SymbolName)}({renderedArguments})");
                return;
            }

            AppendLine($"  {result} = call {MapType(abiCallee.LlvmReturnType)} @{EscapeIdentifier(abiCallee.SymbolName)}({renderedArguments})");
        }

        private void EmitAllocateLocal(SsaAllocateLocalInstruction allocateLocal)
        {
            var slotName = EscapeIdentifier($"slot_{allocateLocal.LocalName}");
            if (_allocatedLocalSlots.Add(slotName))
            {
                AppendLine($"  %{slotName} = alloca {MapType(allocateLocal.LocalType)}");
            }
        }

        private void EmitStoreLocal(SsaStoreLocalInstruction storeLocal)
        {
            EnsureLocalSlotExists(storeLocal.LocalName, storeLocal.LocalType);
            AppendLine($"  store {MapType(storeLocal.LocalType)} {FormatValue(storeLocal.Value)}, ptr %{EscapeIdentifier($"slot_{storeLocal.LocalName}")}");
        }

        private void EmitStoreIndirect(SsaStoreIndirectInstruction storeIndirect)
        {
            AppendLine($"  store {MapType(storeIndirect.ValueType)} {FormatValue(storeIndirect.Value)}, ptr {FormatValue(storeIndirect.Address)}");
        }

        private void EmitMakeSliceFromLocal(string result, SsaMakeSliceFromLocalRValue makeSlice)
        {
            EnsureLocalSlotExists(makeSlice.LocalName, makeSlice.SourceType);

            if (makeSlice.SourceType.Kind != StarkTypeKind.FixedArray
                || makeSlice.SourceType.ElementType is null
                || makeSlice.SourceType.FixedLength is not int fixedLength)
            {
                throw new UnsupportedBodyEmissionException($"Slice creation from '{makeSlice.SourceType.DisplayName}' is not supported.");
            }

            var slotName = $"%{EscapeIdentifier($"slot_{makeSlice.LocalName}")}";
            var elementPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_data")}";
            var withPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_p0")}";

            AppendLine($"  {elementPointer} = getelementptr inbounds {MapType(makeSlice.SourceType)}, ptr {slotName}, i32 0, i32 0");
            AppendLine($"  {withPointer} = insertvalue {MapType(makeSlice.Type)} zeroinitializer, ptr {elementPointer}, 0");
            AppendLine($"  {result} = insertvalue {MapType(makeSlice.Type)} {withPointer}, i64 {fixedLength}, 1");
        }

        private void EmitLoadSliceElement(string result, SsaLoadSliceElementRValue loadSlice)
        {
            var dataPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_data")}";
            var elementPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_ptr")}";

            AppendLine($"  {dataPointer} = extractvalue {MapType(loadSlice.Slice.Type)} {FormatValue(loadSlice.Slice)}, 0");
            AppendLine($"  {elementPointer} = getelementptr inbounds {MapType(loadSlice.Type)}, ptr {dataPointer}, {MapType(loadSlice.Index.Type)} {FormatValue(loadSlice.Index)}");
            AppendLine($"  {result} = load {MapType(loadSlice.Type)}, ptr {elementPointer}");
        }

        private void EmitAddressOfLocal(string result, SsaAddressOfLocalRValue addressOfLocal)
        {
            EnsureLocalSlotExists(addressOfLocal.LocalName, addressOfLocal.PointeeType);
            AppendLine($"  {result} = getelementptr inbounds {MapType(addressOfLocal.PointeeType)}, ptr %{EscapeIdentifier($"slot_{addressOfLocal.LocalName}")}, i32 0");
        }

        private void EmitFieldAddress(string result, SsaFieldAddressRValue fieldAddress)
        {
            AppendLine($"  {result} = getelementptr inbounds {MapType(fieldAddress.AggregateType)}, ptr {FormatValue(fieldAddress.Address)}, i32 0, i32 {fieldAddress.FieldIndex}");
        }

        private void EmitElementAddress(string result, SsaElementAddressRValue elementAddress)
        {
            if (elementAddress.AggregateType.Kind == StarkTypeKind.FixedArray)
            {
                var indexValue = elementAddress.ConstantIndex is int constantIndex
                    ? constantIndex.ToString()
                    : $"{MapType(elementAddress.Index!.Type)} {FormatValue(elementAddress.Index)}";

                if (elementAddress.ConstantIndex is int fixedArrayConstantIndex)
                {
                    AppendLine($"  {result} = getelementptr inbounds {MapType(elementAddress.AggregateType)}, ptr {FormatValue(elementAddress.Address)}, i32 0, i32 {fixedArrayConstantIndex}");
                }
                else
                {
                    AppendLine($"  {result} = getelementptr inbounds {MapType(elementAddress.AggregateType)}, ptr {FormatValue(elementAddress.Address)}, i32 0, {indexValue}");
                }

                return;
            }

            if (elementAddress.ConstantIndex is int scalarConstant)
            {
                AppendLine($"  {result} = getelementptr inbounds {MapType(elementAddress.AggregateType)}, ptr {FormatValue(elementAddress.Address)}, i32 {scalarConstant}");
                return;
            }

            if (elementAddress.Index is null)
            {
                throw new UnsupportedBodyEmissionException("Element address is missing its dynamic index.");
            }

            AppendLine($"  {result} = getelementptr inbounds {MapType(elementAddress.AggregateType)}, ptr {FormatValue(elementAddress.Address)}, {MapType(elementAddress.Index.Type)} {FormatValue(elementAddress.Index)}");
        }

        private void EmitSliceElementAddress(string result, SsaSliceElementAddressRValue sliceElementAddress)
        {
            var dataPointer = $"%{EscapeIdentifier($"{result.TrimStart('%')}_data")}";
            var elementType = sliceElementAddress.Type.ElementType ?? throw new UnsupportedBodyEmissionException("Slice element address requires a raw pointer element type.");

            AppendLine($"  {dataPointer} = extractvalue {MapType(sliceElementAddress.Slice.Type)} {FormatValue(sliceElementAddress.Slice)}, 0");
            AppendLine($"  {result} = getelementptr inbounds {MapType(elementType)}, ptr {dataPointer}, {MapType(sliceElementAddress.Index.Type)} {FormatValue(sliceElementAddress.Index)}");
        }

        private void EmitTerminator(SsaTerminator terminator)
        {
            switch (terminator.Kind)
            {
                case SsaTerminatorKind.Goto:
                    AppendLine($"  br label %{FormatBlockLabel(terminator.Targets[0])}");
                    return;
                case SsaTerminatorKind.Branch:
                    if (terminator.Condition is null)
                    {
                        throw new UnsupportedBodyEmissionException("SSA branch is missing a condition.");
                    }

                    AppendLine(
                        $"  br i1 {FormatValue(terminator.Condition)}, label %{FormatBlockLabel(terminator.Targets[0])}, label %{FormatBlockLabel(terminator.Targets[1])}");
                    return;
                case SsaTerminatorKind.Switch:
                    if (terminator.Condition is null || terminator.DefaultTarget is null)
                    {
                        throw new UnsupportedBodyEmissionException("SSA switch is missing its condition or default target.");
                    }

                    if (terminator.SwitchCases is null || terminator.SwitchCases.Count == 0)
                    {
                        AppendLine($"  br label %{FormatBlockLabel(terminator.DefaultTarget.Value)}");
                        return;
                    }

                    var switchCases = string.Join(
                        " ",
                        terminator.SwitchCases.Select(
                            switchCase => $"{MapType(switchCase.MatchValue.Type)} {FormatValue(switchCase.MatchValue)}, label %{FormatBlockLabel(switchCase.TargetBlockId)}"));

                    AppendLine(
                        $"  switch {MapType(terminator.Condition.Type)} {FormatValue(terminator.Condition)}, label %{FormatBlockLabel(terminator.DefaultTarget.Value)} [ {switchCases} ]");
                    return;
                case SsaTerminatorKind.Return:
                    if (_abiFunction.ReturnsIndirect)
                    {
                        if (terminator.Value is null || _abiFunction.ReturnBufferParameter is null)
                        {
                            throw new UnsupportedBodyEmissionException("SSA aggregate return is missing its value or sret parameter.");
                        }

                        AppendLine($"  store {MapType(_function.ReturnType)} {FormatValue(terminator.Value)}, ptr %{EscapeIdentifier(_abiFunction.ReturnBufferParameter.LlvmName)}");
                        AppendLine("  ret void");
                        return;
                    }

                    if (_function.ReturnType.Kind == StarkTypeKind.Void)
                    {
                        AppendLine("  ret void");
                        return;
                    }

                    if (terminator.Value is null)
                    {
                        throw new UnsupportedBodyEmissionException("SSA return is missing a return value.");
                    }

                    AppendLine($"  ret {MapType(_function.ReturnType)} {FormatValue(terminator.Value)}");
                    return;
                case SsaTerminatorKind.Unreachable:
                    AppendLine("  unreachable");
                    return;
                default:
                    throw new UnsupportedBodyEmissionException($"Unsupported SSA terminator '{terminator.Kind}'.");
            }
        }

        private void EmitFallbackTerminal()
        {
            if (_abiFunction.ReturnsIndirect || _function.ReturnType.Kind == StarkTypeKind.Void)
            {
                AppendLine("  ret void");
                return;
            }

            throw new UnsupportedBodyEmissionException("SSA function body has no blocks.");
        }

        private static string FormatBlockLabel(int blockId) => $"bb{blockId}";

        private string FormatValue(SsaValue value)
        {
            return value switch
            {
                SsaValueReference reference => FormatValueReference(reference),
                SsaIntegerConstant integer => integer.Value.ToString(),
                SsaFloatConstant floating => floating.LiteralText,
                SsaStringConstant text => FormatStringConstantValue(text),
                SsaBoolConstant boolean => boolean.Value ? "true" : "false",
                SsaNullConstant => "null",
                SsaZeroInitializerValue => "zeroinitializer",
                SsaUndefValue => "undef",
                _ => throw new UnsupportedBodyEmissionException($"Unsupported SSA value '{value.GetType().Name}'.")
            };
        }

        private string RenderDirectArgument(AbiParameterSymbol parameter, SsaValue argument)
        {
            if (parameter.LlvmType.Kind == StarkTypeKind.RawPointer && IsStringType(parameter.SourceType))
            {
                return $"ptr {ExtractStringDataPointer(argument)}";
            }

            return $"{MapType(parameter.LlvmType)} {FormatValue(argument)}";
        }

        private string FormatStringConstantValue(SsaStringConstant text)
        {
            var pointer = FormatStringDataPointer(text.LiteralText);
            var constant = _stringConstants[text.LiteralText];
            return $"{{ ptr {pointer}, i64 {constant.DataLength} }}";
        }

        private string ExtractStringDataPointer(SsaValue value)
        {
            if (!IsStringType(value.Type))
            {
                throw new UnsupportedBodyEmissionException($"Value '{value.Text}' is not a lowered string.");
            }

            if (value is SsaStringConstant stringConstant)
            {
                return FormatStringDataPointer(stringConstant.LiteralText);
            }

            var tempName = $"%{EscapeIdentifier(CreateAbiTempName("str_data"))}";
            AppendLine($"  {tempName} = extractvalue {MapType(value.Type)} {FormatValue(value)}, 0");
            return tempName;
        }

        private string FormatStringDataPointer(string literalText)
        {
            if (!_stringConstants.TryGetValue(literalText, out var constant))
            {
                throw new UnsupportedBodyEmissionException($"Missing string constant for literal '{literalText}'.");
            }

            return $"getelementptr inbounds ({constant.ArrayType}, ptr @{constant.SymbolName}, i32 0, i32 0)";
        }

        private void EnsureLocalSlotExists(string localName, StarkTypeSymbol localType)
        {
            var slotName = EscapeIdentifier($"slot_{localName}");
            if (_allocatedLocalSlots.Add(slotName))
            {
                AppendLine($"  %{slotName} = alloca {MapType(localType)}");
            }
        }

        private void EmitEntryParameterMaterialization()
        {
            foreach (var parameter in _abiFunction.UserParameters)
            {
                if (parameter.Kind != AbiParameterKind.IndirectIn)
                {
                    continue;
                }

                var materializedName = $"%{EscapeIdentifier(CreateAbiTempName($"arg_{parameter.SourceName}_value"))}";
                AppendLine($"  {materializedName} = load {MapType(parameter.SourceType)}, ptr %{EscapeIdentifier(parameter.LlvmName)}");
                _materializedParameters[parameter.LlvmName] = materializedName;
            }
        }

        private string FormatValueReference(SsaValueReference reference)
        {
            return _materializedParameters.TryGetValue(reference.Name, out var materialized)
                ? materialized
                : $"%{EscapeIdentifier(reference.Name)}";
        }

        private string CreateAbiTempName(string purpose) => $"abi_{purpose}_{_nextAbiTempId++}";

        private string MapType(StarkTypeSymbol type) => _mapType(type);

        private static bool IsStringType(StarkTypeSymbol type)
        {
            return type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode;
        }

        private void AppendLine(string text) => _builder.AppendLine(text);
    }

    private sealed class UnsupportedBodyEmissionException : Exception
    {
        public UnsupportedBodyEmissionException(string message)
            : base(message)
        {
        }
    }

    private sealed record EmittedStringConstant(string SymbolName, string ArrayType, string Initializer, int DataLength);
}
