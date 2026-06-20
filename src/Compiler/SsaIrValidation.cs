using Stark.Compiler.LlvmIrEmission;

namespace Stark.Compiler;

internal sealed class SsaIrValidator
{
    private readonly CompilerPassContext _context;
    private readonly SsaIrModule _ssa;
    private readonly AbiModel _abiModel;
    private readonly IReadOnlyDictionary<string, NamedTypeSymbol> _namedTypes;
    private readonly IReadOnlyDictionary<string, EnumLayoutSymbol> _enumLayouts;
    private readonly IReadOnlyDictionary<string, ConcreteTypeLayout> _publishedConcreteLayouts;
    private readonly IReadOnlyDictionary<string, KnownGlobalFact> _knownGlobals;
    private readonly TypeCheckModel? _typeModel;
    private readonly SpecializationCodegenStrategyModel? _specializationCodegenStrategy;

    public SsaIrValidator(
        CompilerPassContext context,
        SsaIrModule ssa,
        AbiModel abiModel,
        TypeCheckModel? typeModel = null,
        EnumLayoutModel? enumLayoutModel = null,
        IReadOnlyDictionary<string, ConcreteTypeLayout>? publishedConcreteLayouts = null,
        SpecializationCodegenStrategyModel? specializationCodegenStrategy = null,
        LoadedModuleSet? loadedModules = null)
    {
        _context = context;
        _ssa = ssa;
        _abiModel = abiModel;
        _typeModel = typeModel;
        _namedTypes = BuildNamedTypes(typeModel, loadedModules);
        _enumLayouts = BuildEnumLayouts(enumLayoutModel, loadedModules);
        _publishedConcreteLayouts = publishedConcreteLayouts ?? new Dictionary<string, ConcreteTypeLayout>(StringComparer.Ordinal);
        _specializationCodegenStrategy = specializationCodegenStrategy;
        _knownGlobals = BuildKnownGlobals(typeModel, loadedModules);
    }

    private sealed record KnownGlobalFact(StarkTypeSymbol? Type, GlobalBindingKind BindingKind);

    private static IReadOnlyDictionary<string, NamedTypeSymbol> BuildNamedTypes(
        TypeCheckModel? typeModel,
        LoadedModuleSet? loadedModules)
    {
        var namedTypes = typeModel is null
            ? new Dictionary<string, NamedTypeSymbol>(StringComparer.Ordinal)
            : new Dictionary<string, NamedTypeSymbol>(typeModel.NamedTypes, StringComparer.Ordinal);

        if (loadedModules is null)
        {
            return namedTypes;
        }

        foreach (var module in loadedModules.Modules.Values)
        {
            if (module.PackageImageFacts is not { NamedTypes.Count: > 0 } packageFacts)
            {
                continue;
            }

            foreach (var (name, namedType) in packageFacts.NamedTypes)
            {
                namedTypes.TryAdd(name, namedType);
            }
        }

        return namedTypes;
    }

    private static IReadOnlyDictionary<string, EnumLayoutSymbol> BuildEnumLayouts(
        EnumLayoutModel? enumLayoutModel,
        LoadedModuleSet? loadedModules)
    {
        var enumLayouts = enumLayoutModel is null
            ? new Dictionary<string, EnumLayoutSymbol>(StringComparer.Ordinal)
            : new Dictionary<string, EnumLayoutSymbol>(enumLayoutModel.Layouts, StringComparer.Ordinal);

        if (loadedModules is null)
        {
            return enumLayouts;
        }

        foreach (var module in loadedModules.Modules.Values)
        {
            if (module.PackageImageFacts is not { EnumLayouts.Count: > 0 } packageFacts)
            {
                continue;
            }

            foreach (var (name, layout) in packageFacts.EnumLayouts)
            {
                enumLayouts.TryAdd(name, layout);
            }
        }

        return enumLayouts;
    }

    public void Validate()
    {
        ValidateBuiltinFunctionContracts();

        foreach (var function in _ssa.Functions)
        {
            ValidateFunction(function);
        }
    }

    private static IReadOnlyDictionary<string, KnownGlobalFact> BuildKnownGlobals(
        TypeCheckModel? typeModel,
        LoadedModuleSet? loadedModules)
    {
        var globals = new Dictionary<string, KnownGlobalFact>(StringComparer.Ordinal);

        if (typeModel is not null)
        {
            foreach (var (name, global) in typeModel.Globals)
            {
                globals[name] = new KnownGlobalFact(global.Type, global.BindingKind);
            }
        }

        if (loadedModules is null)
        {
            return globals;
        }

        foreach (var module in loadedModules.Modules.Values)
        {
            if (module.PackageImageFacts is { Globals.Count: > 0 } packageFacts)
            {
                foreach (var (name, global) in packageFacts.Globals)
                {
                    globals.TryAdd(name, new KnownGlobalFact(global.Type, global.BindingKind));
                }
            }

            foreach (var declaration in module.ParseResult.Root.topLevelDeclaration())
            {
                if (declaration.globalConstantDeclaration() is { } constantDeclaration)
                {
                    foreach (var declarator in constantDeclaration.constantDeclarators().constantDeclarator())
                    {
                        AddParsedGlobal(
                            globals,
                            module,
                            declarator.Identifier().GetText(),
                            GlobalBindingKind.Const);
                    }

                    continue;
                }

                if (declaration.globalVariableDeclaration() is not { } variableDeclaration)
                {
                    continue;
                }

                var bindingKind = variableDeclaration.MUT() is not null
                    ? GlobalBindingKind.Mutable
                    : GlobalBindingKind.Immutable;
                foreach (var declarator in variableDeclaration.variableDeclarators().variableDeclarator())
                {
                    AddParsedGlobal(
                        globals,
                        module,
                        declarator.Identifier().GetText(),
                        bindingKind);
                }
            }
        }

        return globals;
    }

    private static void AddParsedGlobal(
        Dictionary<string, KnownGlobalFact> globals,
        LoadedModuleDocument module,
        string sourceName,
        GlobalBindingKind bindingKind)
    {
        var qualifiedName = $"{module.SyntaxModel.ModuleName}.{sourceName}";
        globals.TryAdd(qualifiedName, new KnownGlobalFact(Type: null, bindingKind));
        if (module.Reference.IsRoot)
        {
            globals.TryAdd(sourceName, new KnownGlobalFact(Type: null, bindingKind));
        }
    }

    private void ValidateBuiltinFunctionContracts()
    {
        foreach (var function in BuildAllFunctionSignatures().Values)
        {
            if (IsOpenGenericBuiltinSignature(function))
            {
                continue;
            }

            if (TryResolveSystemMathBuiltin(function, out var mathBuiltin))
            {
                ValidateSystemMathBuiltinContract(function, mathBuiltin);
                continue;
            }

            if (TryResolveSystemBitOperationsBuiltin(function, out var bitOperationsBuiltin))
            {
                ValidateSystemBitOperationsBuiltinContract(function, bitOperationsBuiltin);
                continue;
            }

            if (TryResolveSystemMemoryBuiltin(function, out var memoryBuiltin))
            {
                ValidateSystemMemoryBuiltinContract(function, memoryBuiltin);
                continue;
            }

            if (TryResolveSystemCollectionsBuiltin(function, out var collectionsBuiltin))
            {
                ValidateSystemCollectionsBuiltinContract(function, collectionsBuiltin);
                continue;
            }

            if (TryResolveSystemRuntimeBuiltin(function, out var runtimeBuiltin))
            {
                ValidateSystemRuntimeBuiltinContract(function, runtimeBuiltin);
                continue;
            }

            if (TryResolveSystemThreadingAtomicBuiltin(function, out var atomicBuiltin))
            {
                ValidateSystemThreadingAtomicBuiltinContract(function, atomicBuiltin);
            }
        }
    }

    private bool TryResolveSystemThreadingAtomicBuiltin(
        TypedFunctionSignature function,
        out SystemThreadingAtomicBuiltin builtin)
    {
        return SystemThreadingAtomicFacts.TryGetAtomicBuiltin(CurrentModuleName, function.DisplaySourceName, out builtin)
            || SystemThreadingAtomicFacts.TryGetAtomicBuiltin(moduleName: string.Empty, function.Name, out builtin);
    }

    /// <summary>
    /// Validates one System.Threading atomic builtin declaration against the contract the
    /// LLVM lowering relies on (doc 12 §5): operation arity, a borrowed atomic-struct
    /// receiver (mutable for mutating operations), value-typed operands, the right return
    /// type, and the value stored as the struct's single storage field at offset 0.
    /// </summary>
    private void ValidateSystemThreadingAtomicBuiltinContract(
        TypedFunctionSignature function,
        SystemThreadingAtomicBuiltin builtin)
    {
        var expectedParameterCount = builtin.Operation switch
        {
            SystemThreadingAtomicOperation.Load => 1,
            SystemThreadingAtomicOperation.CompareExchange => 3,
            _ => 2
        };

        if (function.Parameters.Count != expectedParameterCount)
        {
            ReportBuiltin(function, $"System.Threading atomic builtin '{function.Name}' expects exactly {expectedParameterCount} parameter(s) including the receiver.");
            return;
        }

        var receiverType = function.Parameters[0].Type;
        if (receiverType.Kind != StarkTypeKind.Named
            || receiverType.BorrowKind != StarkBorrowKind.Borrow
            || !IsSystemThreadingAtomicReceiverTypeName(receiverType.NamedType, builtin.AtomicTypeName))
        {
            ReportBuiltin(function, $"System.Threading atomic builtin '{function.Name}' must take 'borrow {builtin.AtomicTypeName} self' as its first parameter.");
            return;
        }

        if (builtin.Operation != SystemThreadingAtomicOperation.Load && !receiverType.IsMutableView)
        {
            ReportBuiltin(function, $"System.Threading atomic builtin '{function.Name}' mutates the value and must take 'mut borrow {builtin.AtomicTypeName} self'.");
            return;
        }

        if (ResolveNamedTypeSymbol(receiverType) is { } atomicStructType
            && !SystemThreadingAtomicFacts.HasValidAtomicFieldLayout(atomicStructType, builtin))
        {
            ReportBuiltin(function, SystemThreadingAtomicFacts.DescribeRequiredAtomicFieldLayout(builtin));
            return;
        }

        foreach (var parameter in function.Parameters.Skip(1))
        {
            if (!IsSystemThreadingAtomicValueType(parameter.Type, builtin))
            {
                ReportBuiltin(function, $"System.Threading atomic builtin '{function.Name}' parameter '{parameter.Name}' must match the atomic value type.");
                return;
            }
        }

        var returnTypeIsValid = builtin.Operation switch
        {
            SystemThreadingAtomicOperation.Store => function.ReturnType.Kind == StarkTypeKind.Void,
            SystemThreadingAtomicOperation.CompareExchange => function.ReturnType.Kind == StarkTypeKind.Bool,
            _ => IsSystemThreadingAtomicValueType(function.ReturnType, builtin)
        };

        if (!returnTypeIsValid)
        {
            ReportBuiltin(function, $"System.Threading atomic builtin '{function.Name}' has the wrong return type for operation '{builtin.Operation}'.");
        }
    }

    private static bool IsSystemThreadingAtomicReceiverTypeName(string? namedType, string atomicTypeName)
    {
        if (namedType is null)
        {
            return false;
        }

        return string.Equals(namedType, atomicTypeName, StringComparison.Ordinal)
            || string.Equals(namedType, SystemThreadingAtomicFacts.ModuleName + "." + atomicTypeName, StringComparison.Ordinal);
    }

    private static bool IsSystemThreadingAtomicValueType(StarkTypeSymbol type, SystemThreadingAtomicBuiltin builtin)
    {
        if (builtin.IsBool)
        {
            return type.Kind == StarkTypeKind.Bool;
        }

        return type.Kind == StarkTypeKind.Integer
            && type.BitWidth == builtin.ValueBitWidth
            && type.IsUnsigned == builtin.IsUnsigned;
    }

    private IReadOnlyDictionary<string, TypedFunctionSignature> BuildAllFunctionSignatures()
    {
        var functions = _typeModel?.Functions.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal)
            ?? new Dictionary<string, TypedFunctionSignature>(StringComparer.Ordinal);

        foreach (var function in _ssa.Functions)
        {
            functions.TryAdd(
                function.Name,
                new TypedFunctionSignature(
                    function.Name,
                    function.ReturnType,
                    function.Parameters,
                    SourceName: function.Name));
        }

        if (_typeModel is not null)
        {
            foreach (var strategy in _specializationCodegenStrategy?.Functions ?? [])
            {
                if (!_typeModel.Functions.TryGetValue(strategy.TemplateName, out var templateSignature))
                {
                    continue;
                }

                functions[strategy.SymbolName] = FunctionOverloadFacts.InstantiateSignature(
                    templateSignature,
                    strategy.TypeArguments,
                    strategy.SymbolName,
                    ResolveAssociatedTypeForValidation,
                    strategy.ComptimeValueArguments);
            }
        }

        return functions;
    }

    private StarkTypeSymbol? ResolveAssociatedTypeForValidation(
        StarkTypeSymbol ownerType,
        string associatedTypeName)
    {
        return AssociatedTypeFacts.TryResolveAssociatedType(
            ownerType,
            associatedTypeName,
            _namedTypes,
            out var targetType)
                ? targetType
                : null;
    }

    private bool IsOpenGenericBuiltinSignature(TypedFunctionSignature function)
    {
        if (function.IsGeneric && !function.IsGenericInstantiation)
        {
            return true;
        }

        return ContainsUnboundGenericPlaceholder(function.ReturnType)
            || function.Parameters.Any(parameter => ContainsUnboundGenericPlaceholder(parameter.Type));
    }

    private void ValidateSystemMathBuiltinContract(TypedFunctionSignature function, SystemMathBuiltinKind builtinKind)
    {
        var arity = GetSystemMathIntrinsicArity(builtinKind);
        StarkTypeSymbol scalarType;
        if (builtinKind == SystemMathBuiltinKind.SinCos)
        {
            scalarType = ValidateSystemMathSinCosBuiltinContract(function);
        }
        else
        {
            scalarType = function.ReturnType;
            if (function.ReturnType.Kind != StarkTypeKind.Float)
            {
                ReportBuiltin(function, $"System.Math builtin '{function.Name}' requires a floating-point return type.");
                return;
            }

            if (!IsSupportedFloatIntrinsicWidth(function.ReturnType.BitWidth))
            {
                ReportBuiltin(function, $"System.Math builtin '{function.Name}' return type '{function.ReturnType.DisplayName}' is not supported by LLVM intrinsic lowering.");
                return;
            }

            if (function.Parameters.Count != arity)
            {
                ReportBuiltin(function, $"System.Math builtin '{function.Name}' expects exactly {arity} parameter(s).");
                return;
            }

            foreach (var parameter in function.Parameters)
            {
                if (parameter.Type.Kind != StarkTypeKind.Float
                    || parameter.Type.BitWidth != function.ReturnType.BitWidth)
                {
                    ReportBuiltin(function, $"System.Math builtin '{function.Name}' requires all parameters to match the floating-point return type '{function.ReturnType.DisplayName}'.");
                    return;
                }
            }

            if ((builtinKind is SystemMathBuiltinKind.ReciprocalEstimate or SystemMathBuiltinKind.ReciprocalSqrtEstimate)
                && function.ReturnType.BitWidth != 32)
            {
                ReportBuiltin(function, $"System.Math builtin '{function.Name}' currently supports only 'f32' because the shared single-instruction surface is single-precision.");
                return;
            }
        }

        if (IsHardwareAsmSystemMathBuiltin(builtinKind))
        {
            ValidateSystemMathHardwareBuiltinContract(function, builtinKind, scalarType);
        }

        var abi = ResolveBuiltinAbi(function);
        if (abi.UserParameters.Count != arity)
        {
            ReportBuiltin(function, $"System.Math builtin '{abi.Name}' expects exactly {arity} user parameter(s).");
        }
    }

    private StarkTypeSymbol ValidateSystemMathSinCosBuiltinContract(TypedFunctionSignature function)
    {
        if (function.Parameters.Count != 1)
        {
            ReportBuiltin(function, $"System.Math builtin '{function.Name}' expects exactly 1 parameter.");
            return StarkTypeSymbols.Error;
        }

        var scalarType = function.Parameters[0].Type;
        if (scalarType.Kind != StarkTypeKind.Float)
        {
            ReportBuiltin(function, $"System.Math builtin '{function.Name}' requires a floating-point input parameter.");
            return StarkTypeSymbols.Error;
        }

        if (!IsSupportedFloatIntrinsicWidth(scalarType.BitWidth))
        {
            ReportBuiltin(function, $"System.Math builtin '{function.Name}' input type '{scalarType.DisplayName}' is not supported by LLVM intrinsic lowering.");
            return StarkTypeSymbols.Error;
        }

        var namedType = ResolveNamedTypeSymbol(function.ReturnType);
        if (namedType is null
            || namedType.Kind is not (DeclarationKind.Struct or DeclarationKind.Record)
            || namedType.OrderedFields.Count != 2
            || !namedType.TryGetField("Sin", out var sinField, out _)
            || !namedType.TryGetField("Cos", out var cosField, out _)
            || sinField.Type.Kind != StarkTypeKind.Float
            || cosField.Type.Kind != StarkTypeKind.Float
            || sinField.Type.BitWidth != scalarType.BitWidth
            || cosField.Type.BitWidth != scalarType.BitWidth)
        {
            ReportBuiltin(function, $"System.Math builtin '{function.Name}' requires a two-field struct/record return type with 'Sin' and 'Cos' fields matching the floating-point parameter type '{scalarType.DisplayName}'.");
        }

        return scalarType;
    }

    private void ValidateSystemMathHardwareBuiltinContract(
        TypedFunctionSignature function,
        SystemMathBuiltinKind builtinKind,
        StarkTypeSymbol scalarType)
    {
        if (scalarType.Kind == StarkTypeKind.Error)
        {
            return;
        }

        if (scalarType.BitWidth is not (32 or 64))
        {
            ReportBuiltin(function, $"System.Math single-instruction lowering currently supports only f32 and f64, but found '{scalarType.DisplayName}'.");
            return;
        }

        var architecture = StarkAsmArchitectureFacts.ResolveActiveArchitecture(_context.Options.TargetInfo);
        if (architecture is not (StarkAsmArchitecture.X86_64 or StarkAsmArchitecture.X86 or StarkAsmArchitecture.AArch64))
        {
            ReportBuiltin(function, $"System.Math builtin '{builtinKind}' currently has single-instruction lowering only on x86/x64 and aarch64 targets, but the active target is '{DescribeAsmArchitecture(architecture)}'.");
        }
    }

    private void ValidateSystemBitOperationsBuiltinContract(
        TypedFunctionSignature function,
        SystemBitOperationsBuiltinKind builtinKind)
    {
        if (function.ReturnType.Kind != StarkTypeKind.Integer)
        {
            ReportBuiltin(function, $"System.BitOperations builtin '{function.Name}' requires an integer return type.");
            return;
        }

        if (function.ReturnType.BitWidth is not (32 or 64))
        {
            ReportBuiltin(function, $"System.BitOperations builtin '{function.Name}' currently supports only 'i32' and 'i64', but found '{function.ReturnType.DisplayName}'.");
            return;
        }

        var arity = GetSystemBitOperationsSurfaceArity(builtinKind);
        if (function.Parameters.Count != arity)
        {
            ReportBuiltin(function, $"System.BitOperations builtin '{function.Name}' expects exactly {arity} parameter(s).");
            return;
        }

        foreach (var parameter in function.Parameters)
        {
            if (parameter.Type.Kind != StarkTypeKind.Integer
                || parameter.Type.BitWidth != function.ReturnType.BitWidth)
            {
                ReportBuiltin(function, $"System.BitOperations builtin '{function.Name}' requires all parameters to match the integer return type '{function.ReturnType.DisplayName}'.");
                return;
            }
        }

        var abi = ResolveBuiltinAbi(function);
        if (abi.UserParameters.Count != arity)
        {
            ReportBuiltin(function, $"System.BitOperations builtin '{abi.Name}' expects exactly {arity} user parameter(s).");
        }
    }

    private void ValidateSystemMemoryBuiltinContract(
        TypedFunctionSignature function,
        SystemMemoryBuiltinKind builtinKind)
    {
        switch (builtinKind)
        {
            case SystemMemoryBuiltinKind.Allocate:
                if (!IsSystemMemoryNamedType(function.ReturnType, "Allocation")
                    || function.Parameters.Count != 3
                    || !IsSystemMemoryNamedType(function.Parameters[0].Type, "Allocator")
                    || !IsAllocatorSizeInteger(function.Parameters[1].Type)
                    || !IsAllocatorSizeInteger(function.Parameters[2].Type))
                {
                    ReportBuiltin(function, $"System.Memory builtin '{function.Name}' must have signature 'Allocation Allocate(Allocator allocator, i64 byteLength, i64 alignment)'.");
                    return;
                }

                ValidateSystemMemoryAllocationShape(function, function.ReturnType);
                ValidateSystemMemoryScalarParameterAbi(function, ResolveBuiltinAbi(function), 1);
                ValidateSystemMemoryScalarParameterAbi(function, ResolveBuiltinAbi(function), 2);
                break;
            case SystemMemoryBuiltinKind.Reallocate:
                if (!IsSystemMemoryNamedType(function.ReturnType, "Allocation")
                    || function.Parameters.Count != 3
                    || !IsSystemMemoryNamedType(function.Parameters[0].Type, "Allocation")
                    || !IsAllocatorSizeInteger(function.Parameters[1].Type)
                    || !IsAllocatorSizeInteger(function.Parameters[2].Type))
                {
                    ReportBuiltin(function, $"System.Memory builtin '{function.Name}' must have signature 'Allocation Reallocate(Allocation allocation, i64 byteLength, i64 alignment)'.");
                    return;
                }

                ValidateSystemMemoryAllocationShape(function, function.ReturnType);
                ValidateSystemMemoryScalarParameterAbi(function, ResolveBuiltinAbi(function), 1);
                ValidateSystemMemoryScalarParameterAbi(function, ResolveBuiltinAbi(function), 2);
                break;
            case SystemMemoryBuiltinKind.Free:
                if (function.ReturnType.Kind != StarkTypeKind.Void
                    || function.Parameters.Count != 1
                    || !IsSystemMemoryNamedType(function.Parameters[0].Type, "Allocation"))
                {
                    ReportBuiltin(function, $"System.Memory builtin '{function.Name}' must have signature 'void Free(Allocation allocation)'.");
                    return;
                }

                ValidateSystemMemoryAllocationShape(function, function.Parameters[0].Type);
                break;
        }

        var abi = ResolveBuiltinAbi(function);
        var expectedArity = builtinKind == SystemMemoryBuiltinKind.Free ? 1 : 3;
        if (abi.UserParameters.Count != expectedArity)
        {
            ReportBuiltin(function, $"System.Memory builtin '{abi.Name}' expects exactly {expectedArity} user parameter(s).");
        }

        if (abi.ReturnsIndirect && abi.ReturnBufferParameter is null)
        {
            ReportBuiltin(function, $"System.Memory aggregate builtin '{abi.Name}' is missing its sret parameter.");
        }
    }

    private void ValidateSystemMemoryAllocationShape(TypedFunctionSignature function, StarkTypeSymbol allocationType)
    {
        var namedType = ResolveNamedTypeSymbol(allocationType);
        if (namedType is null
            || !IsSystemMemoryNamedType(allocationType, "Allocation")
            || namedType.OrderedFields.Count < 4
            || !string.Equals(namedType.OrderedFields[0].Name, "Pointer", StringComparison.Ordinal)
            || !string.Equals(namedType.OrderedFields[1].Name, "ByteLength", StringComparison.Ordinal)
            || !string.Equals(namedType.OrderedFields[2].Name, "Alignment", StringComparison.Ordinal)
            || !string.Equals(namedType.OrderedFields[3].Name, "Allocator", StringComparison.Ordinal))
        {
            ReportBuiltin(function, "System.Memory Allocation must contain Pointer, ByteLength, Alignment, and Allocator fields in that order.");
        }
    }

    private void ValidateSystemMemoryScalarParameterAbi(
        TypedFunctionSignature function,
        AbiFunctionSignature abi,
        int parameterIndex)
    {
        if (parameterIndex >= abi.UserParameters.Count)
        {
            return;
        }

        var parameter = abi.UserParameters[parameterIndex];
        if (parameter.Kind != AbiParameterKind.Direct)
        {
            ReportBuiltin(function, $"System.Memory scalar parameter '{parameter.SourceName}' must lower directly.");
        }
    }

    private void ValidateSystemCollectionsBuiltinContract(
        TypedFunctionSignature function,
        SystemCollectionsBuiltinKind builtinKind)
    {
        switch (builtinKind)
        {
            case SystemCollectionsBuiltinKind.ListAsSlice:
            case SystemCollectionsBuiltinKind.ListAsMutableSlice:
                ValidateSystemCollectionsListSliceContract(function, builtinKind);
                break;
            case SystemCollectionsBuiltinKind.DictionaryKeyEquals:
                ValidateSystemCollectionsDictionaryKeyContract(function, expectedParameterCount: 2);
                break;
            case SystemCollectionsBuiltinKind.DictionaryKeyHash:
                ValidateSystemCollectionsDictionaryKeyContract(function, expectedParameterCount: 1);
                break;
        }
    }

    private void ValidateSystemCollectionsListSliceContract(
        TypedFunctionSignature function,
        SystemCollectionsBuiltinKind builtinKind)
    {
        if (function.Parameters.Count != 1
            || function.Parameters[0].Type.Kind != StarkTypeKind.Named
            || function.Parameters[0].Type.BorrowKind != StarkBorrowKind.Borrow
            || ResolveNamedTypeSymbol(function.Parameters[0].Type) is not { } listType
            || !string.Equals(
                StarkTypeSymbols.GetGenericBaseName(function.Parameters[0].Type.NamedType ?? string.Empty),
                "System.Collections.List",
                StringComparison.Ordinal)
            || function.ReturnType.Kind != StarkTypeKind.Slice
            || function.ReturnType.BorrowKind != StarkBorrowKind.RetBorrow)
        {
            ReportBuiltin(function, $"System.Collections builtin '{function.Name}' must have signature 'retborrow T[] List.AsSlice(borrow List<T> self)' or 'retborrow mut T[] List.AsMutableSlice(mut borrow List<T> self)'.");
            return;
        }

        if (builtinKind == SystemCollectionsBuiltinKind.ListAsMutableSlice
            && (!function.Parameters[0].Type.IsMutableView || !function.ReturnType.IsMutableView))
        {
            ReportBuiltin(function, $"System.Collections builtin '{function.Name}' must use mutable receiver and return a mutable retborrow slice.");
            return;
        }

        if (!listType.TryGetField("Data", out var dataField, out _)
            || dataField.Type.Kind != StarkTypeKind.RawPointer
            || !listType.TryGetField("Length", out var lengthField, out _)
            || lengthField.Type.Kind != StarkTypeKind.Integer)
        {
            if (!listType.TryGetField("Items", out var itemsField, out _)
                || itemsField.Type.Kind != StarkTypeKind.Dynamic
                || itemsField.Type.ElementType is null)
            {
                ReportBuiltin(function, "System.Collections List<T> must contain Data/Length fields or a dynamic Items field for slice-view builtins.");
                return;
            }
        }

        var abi = ResolveBuiltinAbi(function);
        if (abi.UserParameters.Count != 1)
        {
            ReportBuiltin(function, $"System.Collections builtin '{function.Name}' expects exactly one receiver parameter.");
        }
        else if (abi.UserParameters[0].Kind != AbiParameterKind.IndirectIn)
        {
            ReportBuiltin(function, $"System.Collections builtin '{function.Name}' receiver must lower as an indirect input pointer.");
        }
    }

    private void ValidateSystemCollectionsDictionaryKeyContract(
        TypedFunctionSignature function,
        int expectedParameterCount)
    {
        if (function.Parameters.Count != expectedParameterCount)
        {
            ReportBuiltin(function, $"System.Collections builtin '{function.Name}' expects {expectedParameterCount} key parameter(s).");
            return;
        }

        if (expectedParameterCount == 1)
        {
            if (function.ReturnType.Kind != StarkTypeKind.Integer || function.ReturnType.BitWidth != 64)
            {
                ReportBuiltin(function, $"System.Collections builtin '{function.Name}' must return 'u64[0 max]'.");
                return;
            }
        }
        else if (function.ReturnType.Kind != StarkTypeKind.Bool)
        {
            ReportBuiltin(function, $"System.Collections builtin '{function.Name}' must return 'bool'.");
            return;
        }

        var keyType = NormalizeType(function.Parameters[0].Type);
        if (function.Parameters[0].Type.BorrowKind == StarkBorrowKind.None)
        {
            ReportBuiltin(function, $"System.Collections builtin '{function.Name}' key parameters must use 'borrow'.");
            return;
        }

        for (var index = 1; index < function.Parameters.Count; index++)
        {
            var parameterType = NormalizeType(function.Parameters[index].Type);
            if (function.Parameters[index].Type.BorrowKind == StarkBorrowKind.None
                || parameterType != keyType)
            {
                ReportBuiltin(function, $"System.Collections builtin '{function.Name}' key parameters must all borrow the same key type.");
                return;
            }
        }

        if (!SystemCollectionsDictionaryKeyFacts.TryResolveContract(
                keyType,
                _typeModel?.Overloads ?? new Dictionary<string, IReadOnlyList<TypedFunctionSignature>>(StringComparer.Ordinal),
                out _,
                out _))
        {
            ReportBuiltin(function, $"System.Collections DictionaryKey builtin '{function.Name}' does not support key type '{keyType.DisplayName}'.");
            return;
        }

        var abi = ResolveBuiltinAbi(function);
        if (abi.UserParameters.Count != expectedParameterCount)
        {
            ReportBuiltin(function, $"System.Collections builtin '{function.Name}' expects {expectedParameterCount} key parameter(s).");
            return;
        }

        foreach (var parameter in abi.UserParameters)
        {
            if (parameter.Kind is not (AbiParameterKind.Direct or AbiParameterKind.IndirectIn))
            {
                ReportBuiltin(function, $"System.Collections dictionary key parameter '{parameter.SourceName}' must lower directly or as an indirect input.");
            }
        }
    }

    private void ValidateSystemRuntimeBuiltinContract(
        TypedFunctionSignature function,
        SystemRuntimeBuiltinKind builtinKind)
    {
        var expectedResultName = builtinKind == SystemRuntimeBuiltinKind.GetByteSliceParts
            ? "ByteSliceParts"
            : "MutableByteSliceParts";
        var isMutable = builtinKind == SystemRuntimeBuiltinKind.GetMutableByteSliceParts;

        if (function.Parameters.Count != 1)
        {
            ReportBuiltin(function, $"System.Runtime byte slice parts builtin '{function.Name}' expects exactly one parameter.");
            return;
        }

        var sourceType = function.Parameters[0].Type;
        if (sourceType.Kind != StarkTypeKind.Slice
            || sourceType.BorrowKind != StarkBorrowKind.Borrow
            || sourceType.IsMutableView != isMutable
            || sourceType.ElementType is not { Kind: StarkTypeKind.Integer, BitWidth: 8 })
        {
            var mutability = isMutable ? "mut borrow" : "borrow";
            ReportBuiltin(function, $"System.Runtime byte slice parts builtin '{function.Name}' must take '{mutability} i8[]'.");
            return;
        }

        if (!IsSystemRuntimeNamedType(function.ReturnType, expectedResultName))
        {
            ReportBuiltin(function, $"System.Runtime byte slice parts builtin '{function.Name}' must return '{expectedResultName}'.");
            return;
        }

        var abi = ResolveBuiltinAbi(function);
        if (abi.UserParameters.Count != 1)
        {
            ReportBuiltin(function, $"System.Runtime byte slice parts builtin '{abi.Name}' expects exactly one user parameter.");
        }
    }

    private AbiFunctionSignature ResolveBuiltinAbi(TypedFunctionSignature function)
    {
        if (_abiModel.Functions.TryGetValue(function.Name, out var abiFunction))
        {
            return abiFunction;
        }

        return LlvmSpecializationEmissionPlanner.BuildSyntheticAbiSignature(
            function,
            function.Name,
            isFfi: false,
            _namedTypes,
            _enumLayouts,
            function.IsVarargs);
    }

    private void ValidateFunction(SsaFunction function)
    {
        if (!function.HasBody || !function.SupportsDirectCodeGeneration)
        {
            return;
        }

        if (function.Blocks.Count == 0)
        {
            Report(function, function.Location, "body has no basic blocks.");
            return;
        }

        var blockIds = new HashSet<int>();
        var duplicateBlockIds = new HashSet<int>();
        foreach (var block in function.Blocks)
        {
            if (!blockIds.Add(block.Id))
            {
                duplicateBlockIds.Add(block.Id);
            }
        }

        foreach (var blockId in duplicateBlockIds)
        {
            Report(function, function.Location, $"basic block id '{blockId}' is defined more than once.");
        }

        if (!blockIds.Contains(function.EntryBlockId))
        {
            Report(function, function.Location, $"entry block '{function.EntryBlockId}' is not present in the function body.");
        }

        ValidateValueDefinitionUniqueness(function);
        var valueDefinitions = CollectValueDefinitions(function);
        var localDefinitions = CollectLocalDefinitions(function);
        var predecessorsByBlock = CollectPredecessors(function);
        var hasCurrentAbi = _abiModel.Functions.TryGetValue(function.Name, out var currentAbi);
        if (!hasCurrentAbi)
        {
            Report(function, function.Location, $"function '{function.Name}' is missing ABI lowering.");
        }
        else
        {
            ValidateCurrentFunctionAbi(function, currentAbi!, function.Location);
        }

        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                ValidatePhi(function, block, phi, blockIds, predecessorsByBlock, valueDefinitions);
            }

            foreach (var instruction in block.Instructions)
            {
                ValidateInstruction(function, instruction, valueDefinitions, localDefinitions, currentAbi);
            }

            ValidateTerminator(function, block.Terminator, blockIds, valueDefinitions, localDefinitions, currentAbi);
        }

        ValidateArenaFrameShape(function);
    }

    private void ValidateArenaFrameShape(SsaFunction function)
    {
        var usesArenaFrame = false;
        var enterCount = 0;
        var leaveCount = 0;

        foreach (var block in function.Blocks)
        {
            for (var index = 0; index < block.Instructions.Count; index++)
            {
                var instruction = block.Instructions[index];
                switch (instruction)
                {
                    case SsaArenaFrameEnterInstruction enter:
                        enterCount++;
                        if (block.Id != function.EntryBlockId)
                        {
                            Report(function, enter.Location, "arena frame enter must be emitted in the entry block.");
                        }

                        if (index != 0)
                        {
                            Report(function, enter.Location, "arena frame enter must be the first SSA instruction in the entry block.");
                        }

                        break;
                    case SsaArenaFrameLeaveInstruction leave:
                        leaveCount++;
                        if (block.Terminator.Kind != SsaTerminatorKind.Return)
                        {
                            Report(function, leave.Location, "arena frame leave must be emitted only in blocks that return.");
                        }

                        if (index != block.Instructions.Count - 1)
                        {
                            Report(function, leave.Location, "arena frame leave must be the final SSA instruction before return.");
                        }

                        break;
                    case SsaAllocateLocalInstruction { StorageClass: "arena" }:
                    case SsaValueInstruction { Value: SsaDynamicStorageAllocationRValue { AllocationKind: DynamicStorageAllocationKind.Arena } }:
                    case SsaValueInstruction { Value: SsaDynamicStorageReserveRValue { AllocationKind: DynamicStorageAllocationKind.Arena } }:
                    case SsaValueInstruction { Value: SsaDynamicStorageTryReserveRValue { AllocationKind: DynamicStorageAllocationKind.Arena } }:
                    case SsaValueInstruction { Value: SsaDynamicStorageTryReserveCapacityRValue { AllocationKind: DynamicStorageAllocationKind.Arena } }:
                        usesArenaFrame = true;
                        break;
                }
            }
        }

        if (!usesArenaFrame)
        {
            return;
        }

        if (enterCount != 1)
        {
            Report(function, function.Location, $"arena-using function must emit exactly one arena frame enter, found {enterCount}.");
        }

        foreach (var block in function.Blocks)
        {
            if (block.Terminator.Kind == SsaTerminatorKind.Return)
            {
                if (block.Instructions.LastOrDefault() is not SsaArenaFrameLeaveInstruction)
                {
                    Report(function, block.Terminator.Location, "arena-using return block must end with arena frame leave before return.");
                }

                continue;
            }

            if (block.Terminator.Kind == SsaTerminatorKind.TailCall)
            {
                Report(function, block.Terminator.Location, "arena-using function cannot lower a tail call while arena frame cleanup is pending.");
            }
        }

        var returnBlockCount = function.Blocks.Count(static block => block.Terminator.Kind == SsaTerminatorKind.Return);
        if (leaveCount != returnBlockCount)
        {
            Report(function, function.Location, $"arena-using function must emit one arena frame leave per return block, found {leaveCount} leave(s) for {returnBlockCount} return block(s).");
        }
    }

    private void ValidateValueDefinitionUniqueness(SsaFunction function)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in function.Parameters)
        {
            var valueName = $"arg_{parameter.Name}";
            if (!seen.Add(valueName))
            {
                Report(function, function.Location, $"SSA value '%{valueName}' is defined more than once.");
            }
        }

        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                if (!seen.Add(phi.ResultName))
                {
                    Report(function, phi.Location, $"SSA value '%{phi.ResultName}' is defined more than once.");
                }
            }

            foreach (var valueInstruction in block.Instructions.OfType<SsaValueInstruction>())
            {
                if (!seen.Add(valueInstruction.ResultName))
                {
                    Report(function, valueInstruction.Location, $"SSA value '%{valueInstruction.ResultName}' is defined more than once.");
                }
            }
        }
    }

    private static IReadOnlyDictionary<int, HashSet<int>> CollectPredecessors(SsaFunction function)
    {
        var predecessors = function.Blocks.ToDictionary(
            static block => block.Id,
            static _ => new HashSet<int>(),
            EqualityComparer<int>.Default);

        foreach (var block in function.Blocks)
        {
            foreach (var target in block.Terminator.Targets)
            {
                if (predecessors.TryGetValue(target, out var targetPredecessors))
                {
                    targetPredecessors.Add(block.Id);
                }
            }
        }

        return predecessors;
    }

    private void ValidatePhi(
        SsaFunction function,
        SsaBasicBlock block,
        SsaPhi phi,
        ISet<int> blockIds,
        IReadOnlyDictionary<int, HashSet<int>> predecessorsByBlock,
        ISet<string> valueDefinitions)
    {
        if (phi.Incomings.Count == 0)
        {
            Report(function, phi.Location, $"phi '{phi.ResultName}' requires at least one incoming value.");
        }

        var actualPredecessors = predecessorsByBlock.TryGetValue(block.Id, out var predecessors)
            ? predecessors
            : new HashSet<int>();
        var incomingPredecessors = new HashSet<int>();

        foreach (var incoming in phi.Incomings)
        {
            if (!incomingPredecessors.Add(incoming.PredecessorBlockId))
            {
                Report(function, phi.Location, $"phi '{phi.ResultName}' has more than one incoming value for predecessor block '{incoming.PredecessorBlockId}'.");
            }

            if (!blockIds.Contains(incoming.PredecessorBlockId))
            {
                Report(function, phi.Location, $"phi '{phi.ResultName}' references missing predecessor block '{incoming.PredecessorBlockId}'.");
            }
            else if (!actualPredecessors.Contains(incoming.PredecessorBlockId))
            {
                Report(function, phi.Location, $"phi '{phi.ResultName}' incoming predecessor block '{incoming.PredecessorBlockId}' does not branch to block '{block.Id}'.");
            }

            ValidateValue(function, incoming.Value, valueDefinitions, phi.Location);
            ValidateValueShape(function, phi.Type, incoming.Value.Type, $"phi '{phi.ResultName}' incoming value", phi.Location);
        }

        foreach (var predecessor in actualPredecessors)
        {
            if (!incomingPredecessors.Contains(predecessor))
            {
                Report(function, phi.Location, $"phi '{phi.ResultName}' is missing an incoming value for predecessor block '{predecessor}'.");
            }
        }
    }

    private static HashSet<string> CollectValueDefinitions(SsaFunction function)
    {
        var definitions = function.Parameters
            .Select(static parameter => $"arg_{parameter.Name}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var block in function.Blocks)
        {
            foreach (var phi in block.Phis)
            {
                definitions.Add(phi.ResultName);
            }

            foreach (var valueInstruction in block.Instructions.OfType<SsaValueInstruction>())
            {
                definitions.Add(valueInstruction.ResultName);
            }
        }

        return definitions;
    }

    private static IReadOnlyDictionary<string, StarkTypeSymbol> CollectLocalDefinitions(SsaFunction function)
    {
        return function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaAllocateLocalInstruction>()
            .GroupBy(static local => local.LocalName, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Last().LocalType, StringComparer.Ordinal);
    }

    private void ValidateCurrentFunctionAbi(
        SsaFunction function,
        AbiFunctionSignature abi,
        SourceLocation? location)
    {
        ValidateValueShape(function, function.ReturnType, abi.SourceReturnType, $"function '{function.Name}' ABI source return", location);

        if (abi.ReturnsIndirect)
        {
            if (function.ReturnType.Kind == StarkTypeKind.Void)
            {
                Report(function, location, $"function '{function.Name}' ABI uses an sret parameter for a void return.");
            }

            if (abi.LlvmReturnType.Kind != StarkTypeKind.Void)
            {
                Report(function, location, $"function '{function.Name}' ABI with an sret parameter must return void at the LLVM level, but found '{abi.LlvmReturnType.DisplayName}'.");
            }

            if (abi.ReturnBufferParameter is not { } sretParameter)
            {
                Report(function, location, $"function '{function.Name}' ABI is missing its sret parameter.");
            }
            else
            {
                ValidateValueShape(function, function.ReturnType, sretParameter.SourceType, $"function '{function.Name}' sret source type", location);
                ValidateRawPointerType(function, sretParameter.LlvmType, $"function '{function.Name}' sret LLVM parameter", location);
                ValidatePointerElementShape(function, sretParameter.LlvmType, function.ReturnType, $"function '{function.Name}' sret LLVM parameter", location);
            }
        }
        else if (function.ReturnType.Kind != StarkTypeKind.Void)
        {
            ValidateLlvmReturnShape(function, abi.LlvmReturnType, $"function '{function.Name}' ABI LLVM return", location);
        }
        else if (abi.LlvmReturnType.Kind != StarkTypeKind.Void)
        {
            Report(function, location, $"function '{function.Name}' ABI LLVM return must be void, but found '{abi.LlvmReturnType.DisplayName}'.");
        }

        var userParameters = abi.UserParameters;
        if (userParameters.Count != function.Parameters.Count)
        {
            Report(
                function,
                location,
                $"function '{function.Name}' ABI user parameter count mismatch: expected {function.Parameters.Count}, got {userParameters.Count}.");
        }

        var parameterCount = Math.Min(userParameters.Count, function.Parameters.Count);
        for (var index = 0; index < parameterCount; index++)
        {
            var sourceParameter = function.Parameters[index];
            var abiParameter = userParameters[index];
            ValidateValueShape(
                function,
                sourceParameter.Type,
                abiParameter.SourceType,
                $"function '{function.Name}' ABI parameter {index + 1} source type",
                location);

            switch (abiParameter.Kind)
            {
                case AbiParameterKind.Direct:
                    ValidateValueShape(
                        function,
                        abiParameter.SourceType,
                        abiParameter.LlvmType,
                        $"function '{function.Name}' ABI parameter {index + 1} direct LLVM type",
                        location);
                    break;
                case AbiParameterKind.IndirectIn:
                    ValidateRawPointerType(function, abiParameter.LlvmType, $"function '{function.Name}' ABI parameter {index + 1} indirect LLVM type", location);
                    ValidatePointerElementShape(function, abiParameter.LlvmType, abiParameter.SourceType, $"function '{function.Name}' ABI parameter {index + 1} indirect LLVM type", location);
                    break;
                default:
                    Report(function, location, $"function '{function.Name}' ABI user parameter {index + 1} has unsupported kind '{abiParameter.Kind}'.");
                    break;
            }
        }
    }

    private void ValidateLlvmReturnShape(
        SsaFunction function,
        StarkTypeSymbol llvmReturnType,
        string usage,
        SourceLocation? location)
    {
        if (IsReturnCompatible(function.ReturnType, llvmReturnType))
        {
            return;
        }

        Report(function, location, $"{usage} type '{llvmReturnType.DisplayName}' does not match expected LLVM value shape '{function.ReturnType.DisplayName}'.");
    }

    private void ValidateInstruction(
        SsaFunction function,
        SsaInstruction instruction,
        ISet<string> valueDefinitions,
        IReadOnlyDictionary<string, StarkTypeSymbol> localDefinitions,
        AbiFunctionSignature? currentAbi)
    {
        switch (instruction)
        {
            case SsaValueInstruction valueInstruction:
                ValidateScopedNoAliasGroups(function, valueInstruction.ScopedNoAliasGroups, valueInstruction.Location);
                ValidateRValue(function, valueInstruction.Value, valueDefinitions, localDefinitions, currentAbi, valueInstruction.Location);
                break;
            case SsaCallInstruction call:
                ValidateScopedNoAliasGroups(function, call.ScopedNoAliasGroups, call.Location);
                foreach (var argument in call.Arguments)
                {
                    ValidateValue(function, argument, valueDefinitions, call.Location);
                }

                ValidateOptionalValues(function, call.IndirectArgumentAddresses, valueDefinitions, call.Location);
                ValidateDirectCall(function, call, localDefinitions, currentAbi, call.Location);
                break;
            case SsaIndirectCallInstruction indirectCall:
                ValidateScopedNoAliasGroups(function, indirectCall.ScopedNoAliasGroups, indirectCall.Location);
                ValidateValue(function, indirectCall.Target, valueDefinitions, indirectCall.Location);
                foreach (var argument in indirectCall.Arguments)
                {
                    ValidateValue(function, argument, valueDefinitions, indirectCall.Location);
                }

                ValidateOptionalValues(function, indirectCall.IndirectArgumentAddresses, valueDefinitions, indirectCall.Location);
                ValidateIndirectCall(function, indirectCall, localDefinitions, currentAbi, indirectCall.Location);
                break;
            case SsaAllocateLocalInstruction allocateLocal:
                if (allocateLocal.StorageClass is not ("stack" or "match" or "heap" or "arena"))
                {
                    Report(function, allocateLocal.Location, $"local '{allocateLocal.LocalName}' has invalid storage class '{allocateLocal.StorageClass}'.");
                }

                break;
            case SsaLifetimeStartInstruction lifetimeStart:
                ValidateLocalExists(function, lifetimeStart.LocalName, localDefinitions, lifetimeStart.Location);
                break;
            case SsaLifetimeEndInstruction lifetimeEnd:
                ValidateLocalExists(function, lifetimeEnd.LocalName, localDefinitions, lifetimeEnd.Location);
                break;
            case SsaDeallocateLocalInstruction deallocateLocal:
                ValidateLocalExists(function, deallocateLocal.LocalName, localDefinitions, deallocateLocal.Location);
                if (deallocateLocal.StorageClass != "heap")
                {
                    Report(function, deallocateLocal.Location, $"local '{deallocateLocal.LocalName}' has invalid deallocation storage class '{deallocateLocal.StorageClass}'.");
                }

                break;
            case SsaArenaFrameEnterInstruction:
            case SsaArenaFrameLeaveInstruction:
                break;
            case SsaStoreLocalInstruction storeLocal:
                ValidateLocalExists(function, storeLocal.LocalName, localDefinitions, storeLocal.Location);
                ValidateValue(function, storeLocal.Value, valueDefinitions, storeLocal.Location);
                break;
            case SsaStoreIndirectInstruction storeIndirect:
                ValidateScopedNoAliasGroups(function, storeIndirect.ScopedNoAliasGroups, storeIndirect.Location);
                ValidateValue(function, storeIndirect.Address, valueDefinitions, storeIndirect.Location);
                ValidateValue(function, storeIndirect.Value, valueDefinitions, storeIndirect.Location);
                ValidateRawPointerValue(function, storeIndirect.Address, "indirect store address", storeIndirect.Location);
                ValidatePointerElementShape(function, storeIndirect.Address.Type, storeIndirect.ValueType, "indirect store address", storeIndirect.Location);
                ValidateValueShape(function, storeIndirect.ValueType, storeIndirect.Value.Type, "indirect store value", storeIndirect.Location);
                break;
            case SsaCopyMemoryInstruction copyMemory:
                ValidateScopedNoAliasGroups(function, copyMemory.ScopedNoAliasGroups, copyMemory.Location);
                ValidateValue(function, copyMemory.DestinationAddress, valueDefinitions, copyMemory.Location);
                ValidateValue(function, copyMemory.SourceAddress, valueDefinitions, copyMemory.Location);
                ValidateRawPointerValue(function, copyMemory.DestinationAddress, "copy destination address", copyMemory.Location);
                ValidateRawPointerValue(function, copyMemory.SourceAddress, "copy source address", copyMemory.Location);
                ValidateCopyMemoryAddressShape(function, copyMemory.DestinationAddress.Type, copyMemory.CopyType, "copy destination address", copyMemory.Location);
                ValidateCopyMemoryAddressShape(function, copyMemory.SourceAddress.Type, copyMemory.CopyType, "copy source address", copyMemory.Location);
                ValidateConcreteLayout(function, copyMemory.CopyType, "copy memory", copyMemory.Location);
                break;
            case SsaStoreGlobalInstruction storeGlobal:
                ValidateValue(function, storeGlobal.Value, valueDefinitions, storeGlobal.Location);
                ValidateGlobalStore(function, storeGlobal, storeGlobal.Location);
                break;
            default:
                Report(function, null, $"unsupported SSA instruction type '{instruction.GetType().Name}' reached validation.");
                break;
        }
    }

    private void ValidateScopedNoAliasGroups(
        SsaFunction function,
        IReadOnlyList<ScopedNoAliasGroup>? groups,
        SourceLocation? fallbackLocation)
    {
        if (groups is not { Count: > 0 })
        {
            return;
        }

        var seenScopeIds = new HashSet<string>(StringComparer.Ordinal);
        var parameterNames = function.Parameters
            .Select(static parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var proof = group.ProofCarrier;
            var location = proof?.Location ?? fallbackLocation;
            if (string.IsNullOrWhiteSpace(group.ScopeId))
            {
                Report(function, location, "scoped noalias group is missing a scope id.");
            }
            else if (!seenScopeIds.Add(group.ScopeId))
            {
                Report(function, location, $"scoped noalias group '{group.ScopeId}' is attached more than once to the same SSA instruction.");
            }

            ValidateAliasProofRoots(function, parameterNames, group.ScopeId, group.RootKeys, location, "scoped noalias group");

            if (proof is null)
            {
                Report(function, fallbackLocation, $"scoped noalias group '{group.ScopeId}' is missing its alias proof carrier.");
                continue;
            }

            if (!Enum.IsDefined(typeof(AliasProofCarrierKind), proof.Kind))
            {
                Report(function, location, $"alias proof carrier '{proof.ProofId}' has unknown proof kind '{(int)proof.Kind}'.");
            }

            if (string.IsNullOrWhiteSpace(proof.ProofId))
            {
                Report(function, location, "alias proof carrier is missing a proof id.");
            }
            else if (!string.Equals(proof.ProofId, group.ScopeId, StringComparison.Ordinal))
            {
                Report(function, location, $"alias proof carrier '{proof.ProofId}' does not match scoped noalias group '{group.ScopeId}'.");
            }

            ValidateAliasProofRoots(function, parameterNames, proof.ProofId, proof.RootKeys, location, "alias proof carrier");

            if (!RootSetsEqual(group.RootKeys, proof.RootKeys))
            {
                Report(function, location, $"alias proof carrier '{proof.ProofId}' roots do not match scoped noalias group '{group.ScopeId}' roots.");
            }
        }
    }

    private void ValidateAliasProofRoots(
        SsaFunction function,
        ISet<string> parameterNames,
        string proofId,
        IReadOnlyList<string> rootKeys,
        SourceLocation? location,
        string usage)
    {
        var roots = rootKeys
            .Where(static rootKey => !string.IsNullOrWhiteSpace(rootKey))
            .ToArray();
        var distinctRoots = roots
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (roots.Length < 2)
        {
            Report(function, location, $"{usage} '{proofId}' must name at least two memory roots.");
        }

        if (roots.Length != rootKeys.Count || distinctRoots.Length != roots.Length)
        {
            Report(function, location, $"{usage} '{proofId}' contains blank or duplicate memory roots.");
        }

        foreach (var root in distinctRoots)
        {
            if (!IsValidAliasProofRootKey(root))
            {
                Report(function, location, $"{usage} '{proofId}' uses invalid memory-root key '{root}'.");
                continue;
            }

            if (!TryGetAliasProofParameterName(root, out var parameterName)
                || !parameterNames.Contains(parameterName))
            {
                Report(function, location, $"{usage} '{proofId}' uses unknown parameter memory-root key '{root}'.");
            }
        }
    }

    private static bool RootSetsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var leftRoots = CanonicalAliasProofRoots(left);
        var rightRoots = CanonicalAliasProofRoots(right);
        return leftRoots.SequenceEqual(rightRoots, StringComparer.Ordinal);
    }

    private static string[] CanonicalAliasProofRoots(IReadOnlyList<string> rootKeys)
    {
        return rootKeys
            .Where(static rootKey => !string.IsNullOrWhiteSpace(rootKey))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static rootKey => rootKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsValidAliasProofRootKey(string rootKey)
    {
        if (!rootKey.StartsWith("param:", StringComparison.Ordinal))
        {
            return false;
        }

        var suffixStart = rootKey.IndexOf('[', StringComparison.Ordinal);
        if (suffixStart < 0)
        {
            return rootKey.Length > "param:".Length;
        }

        return suffixStart > "param:".Length
            && rootKey.EndsWith(']')
            && rootKey.Length > suffixStart + 2;
    }

    private static bool TryGetAliasProofParameterName(string rootKey, out string parameterName)
    {
        parameterName = string.Empty;
        if (!rootKey.StartsWith("param:", StringComparison.Ordinal))
        {
            return false;
        }

        var nameStart = "param:".Length;
        var nameEnd = rootKey.IndexOf('[', StringComparison.Ordinal);
        if (nameEnd < 0)
        {
            nameEnd = rootKey.Length;
        }

        if (nameEnd <= nameStart)
        {
            return false;
        }

        parameterName = rootKey[nameStart..nameEnd];
        return true;
    }

    private void ValidateRValue(
        SsaFunction function,
        SsaRValue value,
        ISet<string> valueDefinitions,
        IReadOnlyDictionary<string, StarkTypeSymbol> localDefinitions,
        AbiFunctionSignature? currentAbi,
        SourceLocation? location)
    {
        switch (value)
        {
            case SsaUseRValue use:
                ValidateValue(function, use.Value, valueDefinitions, location);
                break;
            case SsaUnaryRValue unary:
                ValidateValue(function, unary.Operand, valueDefinitions, location);
                ValidateUnary(function, unary, location);
                break;
            case SsaBinaryRValue binary:
                ValidateValue(function, binary.Left, valueDefinitions, location);
                ValidateValue(function, binary.Right, valueDefinitions, location);
                ValidateBinary(function, binary, location);
                break;
            case SsaSelectRValue select:
                ValidateValue(function, select.Condition, valueDefinitions, location);
                ValidateValue(function, select.WhenTrue, valueDefinitions, location);
                ValidateValue(function, select.WhenFalse, valueDefinitions, location);
                if (select.Condition.Type.Kind != StarkTypeKind.Bool)
                {
                    Report(function, location, $"select condition must be 'bool', but found '{select.Condition.Type.DisplayName}'.");
                }

                ValidateValueShape(function, select.Type, select.WhenTrue.Type, "select true arm", location);
                ValidateValueShape(function, select.Type, select.WhenFalse.Type, "select false arm", location);
                break;
            case SsaCallRValue call:
                foreach (var argument in call.Arguments)
                {
                    ValidateValue(function, argument, valueDefinitions, location);
                }

                ValidateOptionalValues(function, call.IndirectArgumentAddresses, valueDefinitions, location);
                ValidateDirectCall(function, call, localDefinitions, currentAbi, location);
                break;
            case SsaIndirectCallRValue indirectCall:
                ValidateValue(function, indirectCall.Target, valueDefinitions, location);
                foreach (var argument in indirectCall.Arguments)
                {
                    ValidateValue(function, argument, valueDefinitions, location);
                }

                ValidateOptionalValues(function, indirectCall.IndirectArgumentAddresses, valueDefinitions, location);
                ValidateIndirectCall(function, indirectCall, localDefinitions, currentAbi, location);
                break;
            case SsaConvertRValue convert:
                ValidateValue(function, convert.Operand, valueDefinitions, location);
                ValidateConversion(function, convert, location);
                break;
            case SsaExtractFieldRValue extractField:
                ValidateValue(function, extractField.Target, valueDefinitions, location);
                ValidateAggregateFieldRead(function, extractField, location);
                break;
            case SsaInsertFieldRValue insertField:
                ValidateValue(function, insertField.Target, valueDefinitions, location);
                ValidateValue(function, insertField.Value, valueDefinitions, location);
                ValidateAggregateFieldWrite(function, insertField, location);
                break;
            case SsaExtractIndexRValue extractIndex:
                ValidateValue(function, extractIndex.Target, valueDefinitions, location);
                ValidateAggregateIndexRead(function, extractIndex, location);
                break;
            case SsaInsertIndexRValue insertIndex:
                ValidateValue(function, insertIndex.Target, valueDefinitions, location);
                ValidateValue(function, insertIndex.Value, valueDefinitions, location);
                ValidateAggregateIndexWrite(function, insertIndex, location);
                break;
            case SsaMakeSliceFromLocalRValue makeSlice:
                ValidateLocalExists(function, makeSlice.LocalName, localDefinitions, location);
                if (makeSlice.SourceType.Kind != StarkTypeKind.FixedArray || makeSlice.SourceType.ElementType is null)
                {
                    Report(function, location, $"slice creation from local '{makeSlice.LocalName}' requires a fixed array source, but found '{makeSlice.SourceType.DisplayName}'.");
                }
                else if (makeSlice.SourceType.FixedLength is not int)
                {
                    Report(function, location, $"slice creation from local '{makeSlice.LocalName}' requires a fixed array source with a known length.");
                }

                ValidateSliceResultType(function, makeSlice.Type, makeSlice.SourceType.ElementType, "slice creation from local", location);

                break;
            case SsaMakeSliceFromPointerRValue makeSlice:
                ValidateValue(function, makeSlice.Pointer, valueDefinitions, location);
                ValidateValue(function, makeSlice.Length, valueDefinitions, location);
                ValidateIntegerValue(function, makeSlice.Length, "slice length", location);
                ValidateRawPointerValue(function, makeSlice.Pointer, "slice pointer", location);
                ValidateSliceResultType(function, makeSlice.Type, makeSlice.Pointer.Type.ElementType, "slice creation from pointer", location);
                break;
            case SsaDynamicStorageAllocationRValue allocation:
                ValidateValue(function, allocation.Capacity, valueDefinitions, location);
                ValidateDynamicType(function, allocation.Type, "dynamic storage allocation", location);
                ValidateIntegerValue(function, allocation.Capacity, "dynamic storage capacity", location);
                ValidateDynamicStorageCapacityInteger(function, allocation.Capacity, "dynamic storage capacity", location);
                ValidateDynamicElementLayout(function, allocation.Type, "dynamic storage allocation", location);
                break;
            case SsaDynamicStorageFreeRValue free:
                ValidateValue(function, free.Storage, valueDefinitions, location);
                ValidateDynamicType(function, free.Storage.Type, "dynamic storage free", location);
                break;
            case SsaHeapStorageFreeRValue free:
                ValidateValue(function, free.Pointer, valueDefinitions, location);
                ValidateRawPointerValue(function, free.Pointer, "heap storage free", location);
                break;
            case SsaDynamicStorageReserveRValue reserve:
                ValidateValue(function, reserve.StorageAddress, valueDefinitions, location);
                ValidateValue(function, reserve.AdditionalCapacity, valueDefinitions, location);
                ValidateRawPointerValue(function, reserve.StorageAddress, "dynamic storage Reserve address", location);
                ValidatePointerElementShape(function, reserve.StorageAddress.Type, reserve.StorageType, "dynamic storage Reserve address", location);
                ValidateDynamicType(function, reserve.StorageType, "dynamic storage Reserve", location);
                ValidateIntegerValue(function, reserve.AdditionalCapacity, "dynamic storage Reserve additional capacity", location);
                ValidateDynamicStorageCapacityInteger(function, reserve.AdditionalCapacity, "dynamic storage Reserve additional capacity", location);
                ValidateDynamicElementLayout(function, reserve.StorageType, "dynamic storage Reserve", location);
                break;
            case SsaDynamicStorageTryReserveRValue reserve:
                ValidateValue(function, reserve.StorageAddress, valueDefinitions, location);
                ValidateValue(function, reserve.AdditionalCapacity, valueDefinitions, location);
                ValidateRawPointerValue(function, reserve.StorageAddress, "dynamic storage TryReserve address", location);
                ValidatePointerElementShape(function, reserve.StorageAddress.Type, reserve.StorageType, "dynamic storage TryReserve address", location);
                ValidateDynamicType(function, reserve.StorageType, "dynamic storage TryReserve", location);
                ValidateIntegerValue(function, reserve.AdditionalCapacity, "dynamic storage TryReserve additional capacity", location);
                ValidateDynamicStorageCapacityInteger(function, reserve.AdditionalCapacity, "dynamic storage TryReserve additional capacity", location);
                ValidateDynamicElementLayout(function, reserve.StorageType, "dynamic storage TryReserve", location);
                break;
            case SsaDynamicStorageTryReserveCapacityRValue reserve:
                ValidateValue(function, reserve.StorageAddress, valueDefinitions, location);
                ValidateValue(function, reserve.TargetCapacity, valueDefinitions, location);
                ValidateRawPointerValue(function, reserve.StorageAddress, "dynamic storage TryReserveCapacity address", location);
                ValidatePointerElementShape(function, reserve.StorageAddress.Type, reserve.StorageType, "dynamic storage TryReserveCapacity address", location);
                ValidateDynamicType(function, reserve.StorageType, "dynamic storage TryReserveCapacity", location);
                ValidateIntegerValue(function, reserve.TargetCapacity, "dynamic storage TryReserveCapacity target capacity", location);
                ValidateDynamicStorageCapacityInteger(function, reserve.TargetCapacity, "dynamic storage TryReserveCapacity target capacity", location);
                ValidateDynamicElementLayout(function, reserve.StorageType, "dynamic storage TryReserveCapacity", location);
                break;
            case SsaDynamicStorageMoveLastRValue moveLast:
                ValidateValue(function, moveLast.StorageAddress, valueDefinitions, location);
                ValidateRawPointerValue(function, moveLast.StorageAddress, "dynamic storage MoveLast address", location);
                ValidatePointerElementShape(function, moveLast.StorageAddress.Type, moveLast.StorageType, "dynamic storage MoveLast address", location);
                ValidateDynamicMoveShape(function, moveLast.StorageType, moveLast.Type, "dynamic storage MoveLast", location);
                ValidateDynamicElementLayout(function, moveLast.StorageType, "dynamic storage MoveLast", location);
                break;
            case SsaDynamicStorageMoveAtRValue moveAt:
                ValidateValue(function, moveAt.StorageAddress, valueDefinitions, location);
                ValidateValue(function, moveAt.Index, valueDefinitions, location);
                ValidateRawPointerValue(function, moveAt.StorageAddress, "dynamic storage MoveAt address", location);
                ValidatePointerElementShape(function, moveAt.StorageAddress.Type, moveAt.StorageType, "dynamic storage MoveAt address", location);
                ValidateIntegerValue(function, moveAt.Index, "dynamic storage MoveAt index", location);
                ValidateDynamicStorageCapacityInteger(function, moveAt.Index, "dynamic storage MoveAt index", location);
                ValidateDynamicMoveShape(function, moveAt.StorageType, moveAt.Type, "dynamic storage MoveAt", location);
                ValidateDynamicElementLayout(function, moveAt.StorageType, "dynamic storage MoveAt", location);
                break;
            case SsaLoadSliceElementRValue loadSlice:
                ValidateValue(function, loadSlice.Slice, valueDefinitions, location);
                ValidateValue(function, loadSlice.Index, valueDefinitions, location);
                ValidateIntegerValue(function, loadSlice.Index, "slice index", location);
                if (loadSlice.Slice.Type.Kind != StarkTypeKind.Slice || loadSlice.Slice.Type.ElementType is not { } sliceElementType)
                {
                    Report(function, location, $"slice element load requires a slice value, but found '{loadSlice.Slice.Type.DisplayName}'.");
                }
                else
                {
                    ValidateValueShape(function, sliceElementType, loadSlice.Type, "slice element load result", location);
                }

                break;
            case SsaTextSliceRValue textSlice:
                ValidateValue(function, textSlice.TextValue, valueDefinitions, location);
                ValidateValue(function, textSlice.Start, valueDefinitions, location);
                ValidateValue(function, textSlice.Length, valueDefinitions, location);
                ValidateTextValue(function, textSlice.TextValue, "text slicing", location);
                ValidateTextType(function, textSlice.Type, "text slicing result", location);
                if (IsTextType(textSlice.TextValue.Type)
                    && IsTextType(textSlice.Type)
                    && textSlice.TextValue.Type.Kind != textSlice.Type.Kind)
                {
                    Report(function, location, $"text slicing result type '{textSlice.Type.DisplayName}' must match source text type '{textSlice.TextValue.Type.DisplayName}'.");
                }

                ValidateIntegerValue(function, textSlice.Start, "text slice start", location);
                ValidateIntegerValue(function, textSlice.Length, "text slice length", location);
                break;
            case SsaAddressOfLocalRValue addressOfLocal:
                ValidateLocalExists(function, addressOfLocal.LocalName, localDefinitions, location);
                ValidateRawPointerType(function, addressOfLocal.Type, "address-of local result", location);
                ValidatePointerElementShape(function, addressOfLocal.Type, addressOfLocal.PointeeType, "address-of local result", location);
                break;
            case SsaAddressOfParameterRValue addressOfParameter:
                var parameter = function.Parameters.FirstOrDefault(parameter => string.Equals(parameter.Name, addressOfParameter.ParameterName, StringComparison.Ordinal));
                if (parameter is null)
                {
                    Report(function, location, $"address-of references unknown parameter '{addressOfParameter.ParameterName}'.");
                }
                else
                {
                    ValidateValueShape(function, parameter.Type, addressOfParameter.PointeeType, "address-of parameter pointee", location);
                }

                if (currentAbi is not null)
                {
                    var abiParameter = currentAbi.UserParameters.FirstOrDefault(
                        parameter => string.Equals(parameter.SourceName, addressOfParameter.ParameterName, StringComparison.Ordinal));
                    if (abiParameter is null)
                    {
                        Report(function, location, $"address-of parameter '{addressOfParameter.ParameterName}' is missing ABI user-parameter lowering.");
                    }
                    else
                    {
                        ValidateValueShape(function, abiParameter.SourceType, addressOfParameter.PointeeType, "address-of parameter ABI pointee", location);
                    }
                }

                ValidateRawPointerType(function, addressOfParameter.Type, "address-of parameter result", location);
                ValidatePointerElementShape(function, addressOfParameter.Type, addressOfParameter.PointeeType, "address-of parameter result", location);
                break;
            case SsaFieldAddressRValue fieldAddress:
                ValidateValue(function, fieldAddress.Address, valueDefinitions, location);
                ValidateRawPointerValue(function, fieldAddress.Address, "field address base", location);
                ValidatePointerElementShape(function, fieldAddress.Address.Type, fieldAddress.AggregateType, "field address base", location);
                ValidateRawPointerType(function, fieldAddress.Type, "field address result", location);
                break;
            case SsaElementAddressRValue elementAddress:
                ValidateValue(function, elementAddress.Address, valueDefinitions, location);
                if (elementAddress.Index is null && elementAddress.ConstantIndex is null)
                {
                    Report(function, location, "element address is missing both a constant index and a dynamic index.");
                }

                if (elementAddress.Index is not null && elementAddress.ConstantIndex is not null)
                {
                    Report(function, location, "element address cannot have both a constant index and a dynamic index.");
                }

                if (elementAddress.Index is not null)
                {
                    ValidateValue(function, elementAddress.Index, valueDefinitions, location);
                    ValidateIntegerValue(function, elementAddress.Index, "element address index", location);
                }

                ValidateRawPointerValue(function, elementAddress.Address, "element address base", location);
                ValidatePointerElementShape(function, elementAddress.Address.Type, elementAddress.AggregateType, "element address base", location);
                ValidateRawPointerType(function, elementAddress.Type, "element address result", location);
                ValidateElementAddressResult(function, elementAddress, location);
                break;
            case SsaSliceElementAddressRValue sliceElementAddress:
                ValidateValue(function, sliceElementAddress.Slice, valueDefinitions, location);
                ValidateValue(function, sliceElementAddress.Index, valueDefinitions, location);
                ValidateIntegerValue(function, sliceElementAddress.Index, "slice element address index", location);
                ValidateRawPointerType(function, sliceElementAddress.Type, "slice element address result", location);
                if (sliceElementAddress.Type.ElementType is null)
                {
                    Report(function, location, "slice element address result type is missing its raw pointer element type.");
                }
                else if (sliceElementAddress.Slice.Type.Kind != StarkTypeKind.Slice || sliceElementAddress.Slice.Type.ElementType is not { } addressSliceElementType)
                {
                    Report(function, location, $"slice element address requires a slice value, but found '{sliceElementAddress.Slice.Type.DisplayName}'.");
                }
                else
                {
                    ValidateValueShape(function, addressSliceElementType, sliceElementAddress.Type.ElementType, "slice element address pointee", location);
                }

                break;
            case SsaLoadIndirectRValue loadIndirect:
                ValidateValue(function, loadIndirect.Address, valueDefinitions, location);
                ValidateRawPointerValue(function, loadIndirect.Address, "indirect load address", location);
                ValidatePointerElementShape(function, loadIndirect.Address.Type, loadIndirect.Type, "indirect load address", location);
                break;
            case SsaLoadGlobalRValue loadGlobal:
                ValidateGlobalLoad(function, loadGlobal, location);
                break;
            case SsaLoadLocalRValue loadLocal:
                ValidateLocalExists(function, loadLocal.LocalName, localDefinitions, location);
                break;
            case SsaDynVTableSlotRValue vtableSlot:
                ValidateValue(function, vtableSlot.VtablePointer, valueDefinitions, location);
                ValidateRawPointerValue(function, vtableSlot.VtablePointer, "dyn vtable slot base", location);
                if (vtableSlot.SlotIndex < 0)
                {
                    Report(function, location, $"dyn vtable slot index '{vtableSlot.SlotIndex}' must be non-negative.");
                }

                if (vtableSlot.Type.Kind != StarkTypeKind.FunctionPointer)
                {
                    Report(function, location, $"dyn vtable slot result must be a function pointer, but found '{vtableSlot.Type.DisplayName}'.");
                }

                break;
            default:
                Report(function, location, $"unsupported SSA rvalue type '{value.GetType().Name}' reached validation.");
                break;
        }
    }

    private void ValidateAggregateFieldRead(
        SsaFunction function,
        SsaExtractFieldRValue extractField,
        SourceLocation? location)
    {
        if (!TryGetAggregateElementType(
                function,
                extractField.Target.Type,
                extractField.FieldIndex,
                extractField.FieldName,
                requireFieldAccess: true,
                "field extraction",
                location,
                out var fieldType))
        {
            return;
        }

        ValidateValueShape(function, fieldType, extractField.Type, $"field extraction '{extractField.FieldName}' result", location);
    }

    private void ValidateAggregateFieldWrite(
        SsaFunction function,
        SsaInsertFieldRValue insertField,
        SourceLocation? location)
    {
        ValidateValueShape(function, insertField.Target.Type, insertField.Type, "field insertion result", location);

        if (!TryGetAggregateElementType(
                function,
                insertField.Target.Type,
                insertField.FieldIndex,
                insertField.FieldName,
                requireFieldAccess: true,
                "field insertion",
                location,
                out var fieldType))
        {
            return;
        }

        ValidateValueShape(function, fieldType, insertField.Value.Type, $"field insertion '{insertField.FieldName}' value", location);
    }

    private void ValidateAggregateIndexRead(
        SsaFunction function,
        SsaExtractIndexRValue extractIndex,
        SourceLocation? location)
    {
        ValidateIndexedOperationFamily(
            function,
            extractIndex.OperationFamily,
            extractIndex.Target.Type,
            "index extraction",
            location);

        if (!TryGetAggregateElementType(
                function,
                extractIndex.Target.Type,
                extractIndex.ElementIndex,
                fieldName: null,
                requireFieldAccess: false,
                usage: "index extraction",
                location: location,
                out var elementType))
        {
            return;
        }

        ValidateValueShape(function, elementType, extractIndex.Type, $"index extraction {extractIndex.ElementIndex} result", location);
    }

    private void ValidateAggregateIndexWrite(
        SsaFunction function,
        SsaInsertIndexRValue insertIndex,
        SourceLocation? location)
    {
        ValidateValueShape(function, insertIndex.Target.Type, insertIndex.Type, "index insertion result", location);
        ValidateIndexedOperationFamily(
            function,
            insertIndex.OperationFamily,
            insertIndex.Target.Type,
            "index insertion",
            location);

        if (!TryGetAggregateElementType(
                function,
                insertIndex.Target.Type,
                insertIndex.ElementIndex,
                fieldName: null,
                requireFieldAccess: false,
                usage: "index insertion",
                location: location,
                out var elementType))
        {
            return;
        }

        ValidateValueShape(function, elementType, insertIndex.Value.Type, $"index insertion {insertIndex.ElementIndex} value", location);
    }

    private void ValidateIndexedOperationFamily(
        SsaFunction function,
        IndexedElementOperationFamily operationFamily,
        StarkTypeSymbol targetType,
        string usage,
        SourceLocation? location)
    {
        var normalizedType = NormalizeType(targetType);
        var isValid = operationFamily switch
        {
            IndexedElementOperationFamily.FixedArrayElement => normalizedType.Kind == StarkTypeKind.FixedArray,
            IndexedElementOperationFamily.ViewComponent => normalizedType.Kind is StarkTypeKind.Slice
                or StarkTypeKind.Ascii
                or StarkTypeKind.Unicode,
            IndexedElementOperationFamily.ClosureComponent => normalizedType.Kind == StarkTypeKind.Closure,
            IndexedElementOperationFamily.DynTraitComponent => normalizedType.Kind == StarkTypeKind.DynTrait,
            _ => false
        };

        if (!isValid)
        {
            Report(
                function,
                location,
                $"{usage} operation family '{operationFamily}' is not valid for '{targetType.DisplayName}'.");
        }
    }

    private bool TryGetAggregateElementType(
        SsaFunction function,
        StarkTypeSymbol aggregateType,
        int index,
        string? fieldName,
        bool requireFieldAccess,
        string usage,
        SourceLocation? location,
        out StarkTypeSymbol elementType)
    {
        var normalizedType = NormalizeType(aggregateType);
        elementType = StarkTypeSymbols.Error;

        if (requireFieldAccess)
        {
            return normalizedType.Kind switch
            {
                StarkTypeKind.Ascii or StarkTypeKind.Unicode => TryGetTextViewFieldType(function, aggregateType, index, fieldName, usage, location, out elementType),
                StarkTypeKind.Slice => TryGetSliceViewFieldType(function, aggregateType, index, fieldName, usage, location, out elementType),
                StarkTypeKind.Dynamic => TryGetDynamicStorageElementType(function, aggregateType, index, fieldName, usage, location, out elementType),
                StarkTypeKind.Named => TryGetNamedAggregateElementType(function, aggregateType, index, fieldName, usage, location, out elementType),
                _ => ReportInvalidAggregateElementAccess(function, aggregateType, usage, "named, dynamic, text, or slice aggregate", location)
            };
        }

        return normalizedType.Kind switch
        {
            StarkTypeKind.Ascii or StarkTypeKind.Unicode => TryGetTextViewElementType(function, aggregateType, index, usage, location, out elementType),
            StarkTypeKind.Slice => TryGetSliceViewElementType(function, aggregateType, index, usage, location, out elementType),
            StarkTypeKind.FixedArray => TryGetFixedArrayElementType(function, aggregateType, index, usage, location, out elementType),
            StarkTypeKind.Dynamic => TryGetDynamicStorageElementType(function, aggregateType, index, fieldName: null, usage: usage, location: location, out elementType),
            StarkTypeKind.Closure => TryGetClosureElementType(function, aggregateType, index, usage, location, out elementType),
            StarkTypeKind.DynTrait => TryGetDynTraitElementType(function, aggregateType, index, usage, location, out elementType),
            StarkTypeKind.Named => TryGetNamedAggregateElementType(function, aggregateType, index, fieldName: null, usage: usage, location: location, out elementType),
            _ => ReportInvalidAggregateElementAccess(function, aggregateType, usage, "aggregate or view", location)
        };
    }

    // The fat-pointer components of a `dyn Trait` value: slot 0 is the erased data
    // pointer (rawmutptr<i8>), slot 1 is the read-only typed vtable pointer
    // (rawptr<Trait.Vtable>).
    private bool TryGetDynTraitElementType(
        SsaFunction function,
        StarkTypeSymbol aggregateType,
        int index,
        string usage,
        SourceLocation? location,
        out StarkTypeSymbol elementType)
    {
        elementType = index switch
        {
            0 => StarkTypeSymbols.RawPointer(StarkTypeSymbols.Integer(8), isMutable: true),
            1 => StarkTypeSymbols.DynTraitVtablePointerForTraitObject(aggregateType),
            _ => StarkTypeSymbols.Error
        };

        if (index is 0 or 1)
        {
            return true;
        }

        Report(function, location, $"{usage} index {index} is out of range for trait object '{aggregateType.DisplayName}'.");
        return false;
    }

    private bool TryGetClosureElementType(
        SsaFunction function,
        StarkTypeSymbol aggregateType,
        int index,
        string usage,
        SourceLocation? location,
        out StarkTypeSymbol elementType)
    {
        elementType = index switch
        {
            0 => CallableValueFacts.BuildClosureInvokeFunctionPointerType(aggregateType),
            1 => CallableValueFacts.BuildClosureEnvironmentPointerType(aggregateType),
            2 when aggregateType.ClosureStorageKind == StarkClosureStorageKind.Heap
                => CallableValueFacts.BuildClosureDropFunctionPointerType(),
            _ => StarkTypeSymbols.Error
        };

        if (index is 0 or 1
            || index == 2 && aggregateType.ClosureStorageKind == StarkClosureStorageKind.Heap)
        {
            return true;
        }

        Report(function, location, $"{usage} index {index} is out of range for closure value '{aggregateType.DisplayName}'.");
        return false;
    }

    private bool TryGetTextViewFieldType(
        SsaFunction function,
        StarkTypeSymbol aggregateType,
        int index,
        string? fieldName,
        string usage,
        SourceLocation? location,
        out StarkTypeSymbol elementType)
    {
        if (!TryValidateViewFieldName(function, aggregateType, index, fieldName, usage, location))
        {
            elementType = StarkTypeSymbols.Error;
            return false;
        }

        return TryGetTextViewElementType(function, aggregateType, index, usage, location, out elementType);
    }

    private bool TryGetSliceViewFieldType(
        SsaFunction function,
        StarkTypeSymbol aggregateType,
        int index,
        string? fieldName,
        string usage,
        SourceLocation? location,
        out StarkTypeSymbol elementType)
    {
        if (!TryValidateViewFieldName(function, aggregateType, index, fieldName, usage, location))
        {
            elementType = StarkTypeSymbols.Error;
            return false;
        }

        return TryGetSliceViewElementType(function, aggregateType, index, usage, location, out elementType);
    }

    private bool TryValidateViewFieldName(
        SsaFunction function,
        StarkTypeSymbol aggregateType,
        int index,
        string? fieldName,
        string usage,
        SourceLocation? location)
    {
        var expectedFieldName = index switch
        {
            0 => "data",
            1 => "length",
            _ => null
        };
        if (expectedFieldName is null)
        {
            return true;
        }

        if (fieldName is null
            || string.Equals(fieldName, expectedFieldName, StringComparison.Ordinal)
            || (index == 0 && string.Equals(fieldName, "Data", StringComparison.Ordinal))
            || (index == 1 && string.Equals(fieldName, "Length", StringComparison.Ordinal)))
        {
            return true;
        }

        Report(function, location, $"{usage} field '{fieldName}' index '{index}' refers to field '{expectedFieldName}' in '{aggregateType.DisplayName}'.");
        return false;
    }

    private bool TryGetTextViewElementType(
        SsaFunction function,
        StarkTypeSymbol aggregateType,
        int index,
        string usage,
        SourceLocation? location,
        out StarkTypeSymbol elementType)
    {
        elementType = StarkTypeSymbols.Error;
        var unitType = TryGetTextUnitType(NormalizeType(aggregateType));
        if (unitType is null)
        {
            Report(function, location, $"{usage} requires ascii/unicode text, but found '{aggregateType.DisplayName}'.");
            return false;
        }

        if (index is not 0 and not 1)
        {
            Report(function, location, $"{usage} index '{index}' is out of range for '{aggregateType.DisplayName}' with 2 field(s).");
            return false;
        }

        elementType = index == 0
            ? StarkTypeSymbols.RawPointer(unitType, isMutable: false)
            : StarkTypeSymbols.Integer(64);
        return true;
    }

    private bool TryGetSliceViewElementType(
        SsaFunction function,
        StarkTypeSymbol aggregateType,
        int index,
        string usage,
        SourceLocation? location,
        out StarkTypeSymbol elementType)
    {
        var normalizedType = NormalizeType(aggregateType);
        elementType = StarkTypeSymbols.Error;
        if (normalizedType.ElementType is not { } sliceElementType)
        {
            Report(function, location, $"{usage} requires a slice with a known element type, but found '{aggregateType.DisplayName}'.");
            return false;
        }

        if (index is not 0 and not 1)
        {
            Report(function, location, $"{usage} index '{index}' is out of range for '{aggregateType.DisplayName}' with 2 field(s).");
            return false;
        }

        elementType = index == 0
            ? StarkTypeSymbols.RawPointer(sliceElementType, isMutable: normalizedType.IsMutableView)
            : StarkTypeSymbols.Integer(64);
        return true;
    }

    private bool TryGetFixedArrayElementType(
        SsaFunction function,
        StarkTypeSymbol aggregateType,
        int index,
        string usage,
        SourceLocation? location,
        out StarkTypeSymbol elementType)
    {
        var normalizedType = NormalizeType(aggregateType);
        elementType = StarkTypeSymbols.Error;

        if (normalizedType.ElementType is not { } fixedArrayElementType
            || normalizedType.FixedLength is not int fixedLength)
        {
            Report(function, location, $"{usage} requires a fixed array with known element type and length, but found '{aggregateType.DisplayName}'.");
            return false;
        }

        if (index < 0 || index >= fixedLength)
        {
            Report(function, location, $"{usage} index '{index}' is out of range for '{aggregateType.DisplayName}' with length {fixedLength}.");
            return false;
        }

        elementType = fixedArrayElementType;
        return true;
    }

    private bool TryGetDynamicStorageElementType(
        SsaFunction function,
        StarkTypeSymbol aggregateType,
        int index,
        string? fieldName,
        string usage,
        SourceLocation? location,
        out StarkTypeSymbol elementType)
    {
        var normalizedType = NormalizeType(aggregateType);
        elementType = StarkTypeSymbols.Error;

        if (normalizedType.ElementType is null)
        {
            Report(function, location, $"{usage} requires a dynamic storage value with a known element type, but found '{aggregateType.DisplayName}'.");
            return false;
        }

        var expectedFieldName = index switch
        {
            0 => "Data",
            1 => "Length",
            2 => "Capacity",
            _ => null
        };
        if (expectedFieldName is null)
        {
            Report(function, location, $"{usage} index '{index}' is out of range for '{aggregateType.DisplayName}' with 3 field(s).");
            return false;
        }

        if (fieldName is not null && !string.Equals(fieldName, expectedFieldName, StringComparison.Ordinal))
        {
            Report(function, location, $"{usage} field '{fieldName}' index '{index}' refers to field '{expectedFieldName}'.");
            return false;
        }

        elementType = index == 0
            ? StarkTypeSymbols.RawPointer(normalizedType.ElementType, isMutable: true)
            : StarkTypeSymbols.Integer(64);
        return true;
    }

    private bool TryGetNamedAggregateElementType(
        SsaFunction function,
        StarkTypeSymbol aggregateType,
        int index,
        string? fieldName,
        string usage,
        SourceLocation? location,
        out StarkTypeSymbol elementType)
    {
        elementType = StarkTypeSymbols.Error;

        if (ResolveNamedTypeSymbol(NormalizeType(aggregateType)) is not { } namedType
            || !LlvmAggregateEmissionSupport.TryGetScalarizableNamedAggregateFields(namedType, _enumLayouts, out var orderedFields))
        {
            Report(function, location, $"{usage} requires a scalarizable named aggregate, but found '{aggregateType.DisplayName}'.");
            return false;
        }

        if (index < 0 || index >= orderedFields.Count)
        {
            Report(function, location, $"{usage} index '{index}' is out of range for '{aggregateType.DisplayName}' with {orderedFields.Count} field(s).");
            return false;
        }

        var field = orderedFields[index];
        if (fieldName is not null && !string.Equals(field.Name, fieldName, StringComparison.Ordinal))
        {
            Report(function, location, $"{usage} field '{fieldName}' index '{index}' refers to field '{field.Name}'.");
            return false;
        }

        elementType = field.Type;
        return true;
    }

    private bool ReportInvalidAggregateElementAccess(
        SsaFunction function,
        StarkTypeSymbol aggregateType,
        string usage,
        string expectedShape,
        SourceLocation? location)
    {
        Report(function, location, $"{usage} requires a {expectedShape}, but found '{aggregateType.DisplayName}'.");
        return false;
    }

    private void ValidateDirectCall(
        SsaFunction function,
        ISsaDirectCallOperation call,
        IReadOnlyDictionary<string, StarkTypeSymbol> localDefinitions,
        AbiFunctionSignature? currentAbi,
        SourceLocation? location)
    {
        if (!_abiModel.Functions.TryGetValue(call.FunctionName, out var abiCallee))
        {
            Report(function, location, $"call target '{call.FunctionName}' is missing ABI lowering.");
            return;
        }

        ValidateCallArity(function, call.FunctionName, call.Arguments.Count, abiCallee, location);
        ValidateCallArgumentAbi(
            function,
            $"call '{call.FunctionName}'",
            call.Arguments,
            abiCallee,
            call.IndirectArgumentAddresses,
            call.IndirectArgumentLocalNames,
            localDefinitions,
            currentAbi,
            location);
        var sourceReturnType = call.SourceReturnType ?? call.Type;
        if (IsTextType(sourceReturnType) && abiCallee.LlvmReturnType.Kind == StarkTypeKind.RawPointer)
        {
            Report(function, location, $"FFI text-view return from '{call.FunctionName}' reached SSA; return raw pointer plus explicit length/status and wrap it in Stark code.");
        }
    }

    private void ValidateIndirectCall(
        SsaFunction function,
        ISsaIndirectCallOperation call,
        IReadOnlyDictionary<string, StarkTypeSymbol> localDefinitions,
        AbiFunctionSignature? currentAbi,
        SourceLocation? location)
    {
        if (!TryBuildIndirectCallAbi(function, call, location, out var abiCallee))
        {
            return;
        }

        var parameterTypes = call.Target.Type.FunctionPointerParameterTypes!;
        if (parameterTypes.Count != call.Arguments.Count)
        {
            Report(
                function,
                location,
                $"indirect call argument count mismatch: expected {parameterTypes.Count}, got {call.Arguments.Count}.");
        }

        ValidateCallArgumentAbi(
            function,
            "indirect call",
            call.Arguments,
            abiCallee,
            call.IndirectArgumentAddresses,
            call.IndirectArgumentLocalNames,
            localDefinitions,
            currentAbi,
            location);
    }

    private bool TryBuildIndirectCallAbi(
        SsaFunction function,
        ISsaIndirectCallOperation call,
        SourceLocation? location,
        out AbiFunctionSignature abiCallee)
    {
        if (call.Target.Type.FunctionPointerReturnType is null
            || call.Target.Type.FunctionPointerParameterTypes is null)
        {
            Report(function, location, "indirect call target is missing function-pointer ABI metadata.");
            abiCallee = null!;
            return false;
        }

        var signature = new TypedFunctionSignature(
            "$indirect",
            call.Target.Type.FunctionPointerReturnType,
            call.Target.Type.FunctionPointerParameterTypes
                .Select((parameterType, index) => new TypedParameterSymbol(
                    $"arg{index}",
                    parameterType,
                    RawPointerElementCountExpression: StarkTypeSymbols.GetFunctionPointerParameterRawPointerElementCountExpression(
                        call.Target.Type,
                        index)))
                .ToArray(),
            Kind: call.Target.Type.FunctionPointerKind ?? StarkFunctionKind.Fn,
            IsTailCallable: call.Target.Type.FunctionPointerIsTailCallable,
            DisjointParameterGroups: call.Target.Type.FunctionPointerDisjointParameterGroups ?? [],
            OverlapParameterGroups: call.Target.Type.FunctionPointerOverlapParameterGroups ?? [],
            SameParameterGroups: call.Target.Type.FunctionPointerSameParameterGroups ?? [],
            PointeeDeadOnReturnParameterNames: call.Target.Type.FunctionPointerPointeeDeadOnReturnParameterNames ?? []);
        abiCallee = LlvmSpecializationEmissionPlanner.BuildSyntheticAbiSignature(
            signature,
            "$indirect",
            isFfi: false,
            _namedTypes,
            _enumLayouts);
        return true;
    }

    private void ValidateCallArity(
        SsaFunction function,
        string functionName,
        int argumentCount,
        AbiFunctionSignature abiCallee,
        SourceLocation? location)
    {
        var userParameterCount = abiCallee.UserParameters.Count;
        if (abiCallee.IsVarargs)
        {
            if (argumentCount < userParameterCount)
            {
                Report(function, location, $"ABI parameter count mismatch for '{functionName}': expected at least {userParameterCount}, got {argumentCount}.");
            }

            return;
        }

        if (argumentCount != userParameterCount)
        {
            Report(function, location, $"ABI parameter count mismatch for '{functionName}': expected {userParameterCount}, got {argumentCount}.");
        }
    }

    private void ValidateCallArgumentAbi(
        SsaFunction function,
        string callDisplayName,
        IReadOnlyList<SsaValue> arguments,
        AbiFunctionSignature abiCallee,
        IReadOnlyList<SsaValue?>? indirectArgumentAddresses,
        IReadOnlyList<string?>? indirectArgumentLocalNames,
        IReadOnlyDictionary<string, StarkTypeSymbol> localDefinitions,
        AbiFunctionSignature? currentAbi,
        SourceLocation? location)
    {
        if (indirectArgumentAddresses is { Count: var addressCount } && addressCount > arguments.Count)
        {
            Report(
                function,
                location,
                $"{callDisplayName} carries {addressCount} indirect argument address slots for {arguments.Count} argument(s).");
        }

        if (indirectArgumentLocalNames is { Count: var localCount } && localCount > arguments.Count)
        {
            Report(
                function,
                location,
                $"{callDisplayName} carries {localCount} indirect argument local slots for {arguments.Count} argument(s).");
        }

        var userParameters = abiCallee.UserParameters;
        var checkedCount = Math.Min(arguments.Count, userParameters.Count);
        for (var index = 0; index < checkedCount; index++)
        {
            var parameter = userParameters[index];
            var argument = arguments[index];
            var argumentDisplayName = $"{callDisplayName} argument {index + 1}";
            var indirectAddress = GetOptionalIndirectArgumentAddress(indirectArgumentAddresses, index);
            var indirectLocal = GetOptionalIndirectArgumentLocal(indirectArgumentLocalNames, index);

            ValidateValueShape(function, parameter.SourceType, argument.Type, argumentDisplayName, location);

            if (parameter.Kind == AbiParameterKind.Direct)
            {
                if (indirectAddress is not null)
                {
                    Report(function, location, $"{argumentDisplayName} targets a direct ABI parameter but carries an indirect argument address.");
                }

                if (!string.IsNullOrWhiteSpace(indirectLocal))
                {
                    Report(function, location, $"{argumentDisplayName} targets a direct ABI parameter but carries promoted local '{indirectLocal}'.");
                }

                continue;
            }

            if (parameter.Kind != AbiParameterKind.IndirectIn)
            {
                Report(function, location, $"{argumentDisplayName} uses unsupported ABI parameter kind '{parameter.Kind}'.");
                continue;
            }

            if (indirectAddress is not null && !string.IsNullOrWhiteSpace(indirectLocal))
            {
                Report(function, location, $"{argumentDisplayName} cannot carry both an indirect argument address and promoted local '{indirectLocal}'.");
            }

            if (indirectAddress is not null)
            {
                ValidateRawPointerValue(function, indirectAddress, $"{argumentDisplayName} indirect address", location);
                ValidatePointerElementShape(function, indirectAddress.Type, parameter.SourceType, $"{argumentDisplayName} indirect address", location);
            }

            if (!string.IsNullOrWhiteSpace(indirectLocal))
            {
                ValidatePromotedIndirectArgument(
                    function,
                    argumentDisplayName,
                    indirectLocal!,
                    parameter.SourceType,
                    localDefinitions,
                    currentAbi,
                    location);
            }
        }

        for (var index = userParameters.Count; index < arguments.Count; index++)
        {
            var indirectAddress = GetOptionalIndirectArgumentAddress(indirectArgumentAddresses, index);
            var indirectLocal = GetOptionalIndirectArgumentLocal(indirectArgumentLocalNames, index);
            if (indirectAddress is not null)
            {
                Report(function, location, $"{callDisplayName} vararg argument {index + 1} cannot carry an indirect argument address.");
            }

            if (!string.IsNullOrWhiteSpace(indirectLocal))
            {
                Report(function, location, $"{callDisplayName} vararg argument {index + 1} cannot carry promoted local '{indirectLocal}'.");
            }
        }
    }

    private void ValidatePromotedIndirectArgument(
        SsaFunction function,
        string argumentDisplayName,
        string promotedLocal,
        StarkTypeSymbol expectedType,
        IReadOnlyDictionary<string, StarkTypeSymbol> localDefinitions,
        AbiFunctionSignature? currentAbi,
        SourceLocation? location)
    {
        var promotedParameter = currentAbi?.UserParameters.FirstOrDefault(
            parameter => string.Equals(parameter.SourceName, promotedLocal, StringComparison.Ordinal));
        if (promotedParameter is not null)
        {
            ValidateValueShape(function, expectedType, promotedParameter.SourceType, $"{argumentDisplayName} promoted parameter '{promotedLocal}'", location);
            return;
        }

        if (localDefinitions.TryGetValue(promotedLocal, out var localType))
        {
            ValidateValueShape(function, expectedType, localType, $"{argumentDisplayName} promoted local '{promotedLocal}'", location);
            return;
        }

        Report(function, location, $"{argumentDisplayName} promotes unknown local or parameter '{promotedLocal}'.");
    }

    private static SsaValue? GetOptionalIndirectArgumentAddress(IReadOnlyList<SsaValue?>? addresses, int index)
    {
        return addresses is not null && index < addresses.Count
            ? addresses[index]
            : null;
    }

    private static string? GetOptionalIndirectArgumentLocal(IReadOnlyList<string?>? localNames, int index)
    {
        return localNames is not null && index < localNames.Count
            ? localNames[index]
            : null;
    }

    private void ValidateTerminator(
        SsaFunction function,
        SsaTerminator terminator,
        ISet<int> blockIds,
        ISet<string> valueDefinitions,
        IReadOnlyDictionary<string, StarkTypeSymbol> localDefinitions,
        AbiFunctionSignature? currentAbi)
    {
        foreach (var target in terminator.Targets)
        {
            if (!blockIds.Contains(target))
            {
                Report(function, terminator.Location, $"terminator references missing target block '{target}'.");
            }
        }

        switch (terminator.Kind)
        {
            case SsaTerminatorKind.Goto:
                if (terminator.Targets.Count != 1)
                {
                    Report(function, terminator.Location, $"goto terminator requires exactly one target, but found {terminator.Targets.Count}.");
                }

                break;
            case SsaTerminatorKind.Branch:
                if (terminator.Targets.Count != 2)
                {
                    Report(function, terminator.Location, $"branch terminator requires exactly two targets, but found {terminator.Targets.Count}.");
                }

                if (terminator.Condition is null)
                {
                    Report(function, terminator.Location, "branch terminator is missing its condition.");
                }
                else
                {
                    ValidateValue(function, terminator.Condition, valueDefinitions, terminator.Location);
                    if (terminator.Condition.Type.Kind != StarkTypeKind.Bool)
                    {
                        Report(function, terminator.Location, $"branch condition must be 'bool', but found '{terminator.Condition.Type.DisplayName}'.");
                    }
                }

                break;
            case SsaTerminatorKind.Switch:
                StarkTypeSymbol? switchConditionType = null;
                if (terminator.Condition is null)
                {
                    Report(function, terminator.Location, "switch terminator is missing its condition.");
                }
                else
                {
                    ValidateValue(function, terminator.Condition, valueDefinitions, terminator.Location);
                    ValidateSwitchConditionType(function, terminator.Condition.Type, terminator.Location);
                    switchConditionType = terminator.Condition.Type;
                }

                if (terminator.DefaultTarget is not { } defaultTarget)
                {
                    Report(function, terminator.Location, "switch terminator is missing its default target.");
                }
                else if (!blockIds.Contains(defaultTarget))
                {
                    Report(function, terminator.Location, $"switch terminator references missing default target block '{defaultTarget}'.");
                }

                foreach (var switchCase in terminator.SwitchCases ?? [])
                {
                    if (!blockIds.Contains(switchCase.TargetBlockId))
                    {
                        Report(function, terminator.Location, $"switch case '{switchCase.Label}' references missing target block '{switchCase.TargetBlockId}'.");
                    }

                    ValidateValue(function, switchCase.MatchValue, valueDefinitions, terminator.Location);
                    if (switchConditionType is not null)
                    {
                        ValidateValueShape(
                            function,
                            switchConditionType,
                            switchCase.MatchValue.Type,
                            $"switch case '{switchCase.Label}' match value",
                            terminator.Location);
                    }
                }

                break;
            case SsaTerminatorKind.Return:
                if (function.ReturnType.Kind == StarkTypeKind.Void)
                {
                    if (terminator.Value is not null)
                    {
                        Report(function, terminator.Location, "void return terminator carries a value.");
                    }

                    break;
                }

                if (terminator.Value is null)
                {
                    Report(function, terminator.Location, "return terminator is missing a return value.");
                    break;
                }

                ValidateValue(function, terminator.Value, valueDefinitions, terminator.Location);
                if (!IsReturnCompatible(function.ReturnType, terminator.Value.Type))
                {
                    Report(function, terminator.Location, $"return value type '{terminator.Value.Type.DisplayName}' cannot be assigned to function return type '{function.ReturnType.DisplayName}'.");
                }

                break;
            case SsaTerminatorKind.TailCall:
                if (terminator.Targets.Count != 0)
                {
                    Report(function, terminator.Location, $"tail-call terminator requires zero targets, but found {terminator.Targets.Count}.");
                }

                if (terminator.TailDirectCall is null && terminator.TailIndirectCall is null)
                {
                    Report(function, terminator.Location, "tail-call terminator is missing its call operation.");
                    break;
                }

                if (terminator.TailDirectCall is not null && terminator.TailIndirectCall is not null)
                {
                    Report(function, terminator.Location, "tail-call terminator cannot carry both direct and indirect call operations.");
                    break;
                }

                if (terminator.TailDirectCall is { } directTailCall)
                {
                    foreach (var argument in directTailCall.Arguments)
                    {
                        ValidateValue(function, argument, valueDefinitions, terminator.Location);
                    }

                    ValidateOptionalValues(function, directTailCall.IndirectArgumentAddresses, valueDefinitions, terminator.Location);
                    ValidateDirectCall(function, directTailCall, localDefinitions, currentAbi, terminator.Location);
                    if (_abiModel.Functions.TryGetValue(directTailCall.FunctionName, out var directTailCalleeAbi))
                    {
                        ValidateMustTailAbi(function, currentAbi, directTailCalleeAbi, directTailCall.Text, terminator.Location);
                    }

                    ValidateTailCallResult(function, directTailCall.Type, terminator.Location);
                }
                else if (terminator.TailIndirectCall is { } indirectTailCall)
                {
                    ValidateValue(function, indirectTailCall.Target, valueDefinitions, terminator.Location);
                    foreach (var argument in indirectTailCall.Arguments)
                    {
                        ValidateValue(function, argument, valueDefinitions, terminator.Location);
                    }

                    ValidateOptionalValues(function, indirectTailCall.IndirectArgumentAddresses, valueDefinitions, terminator.Location);
                    ValidateIndirectCall(function, indirectTailCall, localDefinitions, currentAbi, terminator.Location);
                    if (TryBuildIndirectCallAbi(function, indirectTailCall, terminator.Location, out var indirectTailCalleeAbi))
                    {
                        ValidateMustTailAbi(function, currentAbi, indirectTailCalleeAbi, indirectTailCall.Text, terminator.Location);
                    }

                    ValidateTailCallResult(function, indirectTailCall.Type, terminator.Location);
                }

                break;
            case SsaTerminatorKind.Unreachable:
                break;
            default:
                Report(function, terminator.Location, $"unsupported SSA terminator kind '{terminator.Kind}' reached validation.");
                break;
        }
    }

    private void ValidateTailCallResult(
        SsaFunction function,
        StarkTypeSymbol resultType,
        SourceLocation? location)
    {
        if (function.ReturnType.Kind == StarkTypeKind.Void)
        {
            if (resultType.Kind != StarkTypeKind.Void)
            {
                Report(function, location, $"void tail-call terminator carries result type '{resultType.DisplayName}'.");
            }

            return;
        }

        if (resultType.Kind == StarkTypeKind.Void)
        {
            Report(function, location, $"tail-call terminator is missing return value for function return type '{function.ReturnType.DisplayName}'.");
            return;
        }

        if (!IsReturnCompatible(function.ReturnType, resultType))
        {
            Report(function, location, $"tail-call result type '{resultType.DisplayName}' cannot be assigned to function return type '{function.ReturnType.DisplayName}'.");
        }
    }

    private void ValidateMustTailAbi(
        SsaFunction function,
        AbiFunctionSignature? callerAbi,
        AbiFunctionSignature calleeAbi,
        string callText,
        SourceLocation? location)
    {
        if (callerAbi is null)
        {
            Report(function, location, $"tail call '{callText}' is missing caller ABI lowering.");
            return;
        }

        if (!callerAbi.UsesTailCallingConvention)
        {
            Report(function, location, $"tail call '{callText}' requires caller '{function.Name}' to lower with tailcc.");
        }

        if (!calleeAbi.UsesTailCallingConvention)
        {
            Report(function, location, $"tail call '{callText}' targets a function that did not lower with tailcc.");
        }

        if (calleeAbi.IsFfi || calleeAbi.IsVarargs || calleeAbi.FfiAbi is not null)
        {
            Report(function, location, $"tail call '{callText}' targets an FFI or varargs ABI.");
        }

        if (callerAbi.ReturnsIndirect)
        {
            Report(function, location, $"tail call '{callText}' is inside caller '{function.Name}' with an indirect-return ABI shape.");
        }

        if (calleeAbi.ReturnsIndirect)
        {
            Report(function, location, $"tail call '{callText}' targets an indirect-return ABI shape.");
        }

        foreach (var parameter in calleeAbi.UserParameters.Where(IsUnsupportedMustTailParameter))
        {
            Report(function, location, $"tail call '{callText}' targets parameter '{parameter.SourceName}' of type '{parameter.SourceType.DisplayName}' with a hidden indirect ABI shape.");
        }

        var callerReturnType = MapValidationLlvmType(callerAbi.LlvmReturnType);
        var calleeReturnType = MapValidationLlvmType(calleeAbi.LlvmReturnType);
        if (!string.Equals(callerReturnType, calleeReturnType, StringComparison.Ordinal))
        {
            Report(function, location, $"tail call '{callText}' return ABI '{calleeAbi.LlvmReturnType.DisplayName}' does not match caller return ABI '{callerAbi.LlvmReturnType.DisplayName}'.");
        }

        var callerParameters = callerAbi.UserParameters;
        var calleeParameters = calleeAbi.UserParameters;
        if (callerParameters.Count != calleeParameters.Count)
        {
            Report(function, location, $"tail call '{callText}' has {calleeParameters.Count} ABI parameter(s), but caller '{function.Name}' has {callerParameters.Count}.");
            return;
        }

        for (var index = 0; index < callerParameters.Count; index++)
        {
            var callerType = MapValidationLlvmType(callerParameters[index].LlvmType);
            var calleeType = MapValidationLlvmType(calleeParameters[index].LlvmType);
            if (!string.Equals(callerType, calleeType, StringComparison.Ordinal))
            {
                Report(function, location, $"tail call '{callText}' ABI parameter {index + 1} type '{calleeParameters[index].LlvmType.DisplayName}' does not match caller parameter type '{callerParameters[index].LlvmType.DisplayName}'.");
            }
        }
    }

    private static bool IsUnsupportedMustTailParameter(AbiParameterSymbol parameter)
    {
        return parameter.Kind != AbiParameterKind.Direct
            && (parameter.Kind != AbiParameterKind.IndirectIn
                || AbiLoweringHeuristics.IsByValueIndirectParameter(parameter));
    }

    private void ValidateSwitchConditionType(
        SsaFunction function,
        StarkTypeSymbol conditionType,
        SourceLocation? location)
    {
        var normalizedType = NormalizeType(conditionType);
        switch (normalizedType.Kind)
        {
            case StarkTypeKind.Bool:
                return;
            case StarkTypeKind.Integer:
                if (!IsConcreteIntegerType(normalizedType))
                {
                    Report(function, location, $"switch condition type '{conditionType.DisplayName}' must be a concrete integer.");
                }

                return;
            default:
                Report(function, location, $"switch condition type '{conditionType.DisplayName}' must be bool or a concrete integer.");
                return;
        }
    }

    private void ValidateBinary(SsaFunction function, SsaBinaryRValue binary, SourceLocation? location)
    {
        switch (binary.Operator)
        {
            case SsaBinaryOperator.Add:
            case SsaBinaryOperator.Subtract:
            case SsaBinaryOperator.Multiply:
            case SsaBinaryOperator.Divide:
            case SsaBinaryOperator.Modulo:
                if (!IsConcreteNumericType(binary.Type))
                {
                    Report(function, location, $"binary operator '{binary.Operator}' requires a concrete integer or supported float result, but found '{binary.Type.DisplayName}'.");
                    break;
                }

                ValidateValueShape(function, binary.Type, binary.Left.Type, $"binary operator '{binary.Operator}' left operand", location);
                ValidateValueShape(function, binary.Type, binary.Right.Type, $"binary operator '{binary.Operator}' right operand", location);
                break;
            case SsaBinaryOperator.WrappingAdd:
            case SsaBinaryOperator.WrappingSubtract:
            case SsaBinaryOperator.WrappingMultiply:
                if (!IsConcreteIntegerType(binary.Type))
                {
                    Report(function, location, $"wrapping integer operator '{binary.Operator}' requires a concrete integer result, but found '{binary.Type.DisplayName}'.");
                    break;
                }

                ValidateValueShape(function, binary.Type, binary.Left.Type, $"wrapping integer operator '{binary.Operator}' left operand", location);
                ValidateValueShape(function, binary.Type, binary.Right.Type, $"wrapping integer operator '{binary.Operator}' right operand", location);
                break;
            case SsaBinaryOperator.SaturatingAdd:
            case SsaBinaryOperator.SaturatingSubtract:
            case SsaBinaryOperator.SaturatingMultiply:
                if (!IsConcreteIntegerType(binary.Type))
                {
                    Report(function, location, $"saturating integer operator '{binary.Operator}' requires a concrete integer bit width.");
                    break;
                }

                ValidateValueShape(function, binary.Type, binary.Left.Type, $"saturating integer operator '{binary.Operator}' left operand", location);
                ValidateValueShape(function, binary.Type, binary.Right.Type, $"saturating integer operator '{binary.Operator}' right operand", location);
                break;
            case SsaBinaryOperator.BitwiseAnd:
            case SsaBinaryOperator.BitwiseXor:
            case SsaBinaryOperator.BitwiseOr:
                if (binary.Type.Kind == StarkTypeKind.Integer && !IsConcreteIntegerType(binary.Type))
                {
                    Report(function, location, $"binary operator '{binary.Operator}' requires a concrete integer result, but found '{binary.Type.DisplayName}'.");
                    break;
                }

                if (binary.Type.Kind is not (StarkTypeKind.Integer or StarkTypeKind.Bool))
                {
                    Report(function, location, $"binary operator '{binary.Operator}' requires an integer or bool result, but found '{binary.Type.DisplayName}'.");
                    break;
                }

                ValidateValueShape(function, binary.Type, binary.Left.Type, $"binary operator '{binary.Operator}' left operand", location);
                ValidateValueShape(function, binary.Type, binary.Right.Type, $"binary operator '{binary.Operator}' right operand", location);
                break;
            case SsaBinaryOperator.ShiftLeft:
            case SsaBinaryOperator.ShiftRight:
                if (!IsConcreteIntegerType(binary.Type))
                {
                    Report(function, location, $"shift operator '{binary.Operator}' requires a concrete integer result, but found '{binary.Type.DisplayName}'.");
                    break;
                }

                ValidateValueShape(function, binary.Type, binary.Left.Type, $"shift operator '{binary.Operator}' left operand", location);
                ValidateValueShape(function, binary.Type, binary.Right.Type, $"shift operator '{binary.Operator}' right operand", location);
                break;
            case SsaBinaryOperator.Exponent:
                if (!IsConcreteNumericType(binary.Type))
                {
                    Report(function, location, $"exponent operator result type '{binary.Type.DisplayName}' is not supported by LLVM emission.");
                    break;
                }

                if (binary.Type.Kind == StarkTypeKind.Integer && !IsConcreteIntegerType(binary.Type))
                {
                    Report(function, location, $"integer exponent operator result type '{binary.Type.DisplayName}' requires a concrete bit width.");
                    break;
                }

                ValidateValueShape(function, binary.Type, binary.Left.Type, "exponent left operand", location);
                ValidateValueShape(function, binary.Type, binary.Right.Type, "exponent right operand", location);
                break;
            case SsaBinaryOperator.WrappingExponent:
                if (!IsConcreteIntegerType(binary.Type))
                {
                    Report(function, location, $"wrapping exponent operator result type '{binary.Type.DisplayName}' requires a concrete integer bit width.");
                    break;
                }

                ValidateValueShape(function, binary.Type, binary.Left.Type, "wrapping exponent left operand", location);
                ValidateValueShape(function, binary.Type, binary.Right.Type, "wrapping exponent right operand", location);
                break;
            case SsaBinaryOperator.Equal:
            case SsaBinaryOperator.NotEqual:
            case SsaBinaryOperator.LessThan:
            case SsaBinaryOperator.LessThanOrEqual:
            case SsaBinaryOperator.GreaterThan:
            case SsaBinaryOperator.GreaterThanOrEqual:
                ValidateComparisonBinary(function, binary, location);
                break;
        }
    }

    private void ValidateComparisonBinary(SsaFunction function, SsaBinaryRValue binary, SourceLocation? location)
    {
        if (binary.Type.Kind != StarkTypeKind.Bool)
        {
            Report(function, location, $"comparison operator '{binary.Operator}' requires a bool result, but found '{binary.Type.DisplayName}'.");
            return;
        }

        var leftType = NormalizeType(binary.Left.Type);
        var rightType = NormalizeType(binary.Right.Type);
        var isEquality = binary.Operator is SsaBinaryOperator.Equal or SsaBinaryOperator.NotEqual;

        switch (leftType.Kind)
        {
            case StarkTypeKind.Integer or StarkTypeKind.Float:
                if (!IsConcreteNumericType(leftType))
                {
                    Report(function, location, $"comparison operator '{binary.Operator}' requires a concrete integer or supported float operand, but found '{binary.Left.Type.DisplayName}'.");
                    break;
                }

                ValidateValueShape(function, binary.Left.Type, binary.Right.Type, $"comparison operator '{binary.Operator}' right operand", location);
                break;
            case StarkTypeKind.Bool:
                ValidateValueShape(function, binary.Left.Type, binary.Right.Type, $"comparison operator '{binary.Operator}' right operand", location);
                break;
            case StarkTypeKind.RawPointer:
                if (rightType.Kind is not (StarkTypeKind.RawPointer or StarkTypeKind.Null))
                {
                    Report(function, location, $"comparison operator '{binary.Operator}' raw pointer right operand must be a raw pointer or null, but found '{binary.Right.Type.DisplayName}'.");
                }

                break;
            case StarkTypeKind.Ascii or StarkTypeKind.Unicode:
                if (rightType.Kind != leftType.Kind)
                {
                    Report(function, location, $"comparison operator '{binary.Operator}' text operands must have the same text kind, but found '{binary.Left.Type.DisplayName}' and '{binary.Right.Type.DisplayName}'.");
                }

                break;
            case StarkTypeKind.Slice:
                if (!isEquality)
                {
                    Report(function, location, $"comparison operator '{binary.Operator}' is not supported for slice values.");
                    break;
                }

                ValidateValueShape(function, binary.Left.Type, binary.Right.Type, $"comparison operator '{binary.Operator}' slice right operand", location);
                break;
            case StarkTypeKind.FixedArray:
                if (leftType.ElementType is null || leftType.FixedLength is null)
                {
                    Report(function, location, $"comparison operator '{binary.Operator}' requires a fixed array operand with a known element type and length.");
                    break;
                }

                ValidateValueShape(function, binary.Left.Type, binary.Right.Type, $"comparison operator '{binary.Operator}' fixed-array right operand", location);
                if (!isEquality && !IsOrderedComparisonSupportedType(leftType, new HashSet<string>(StringComparer.Ordinal)))
                {
                    Report(function, location, $"comparison operator '{binary.Operator}' fixed-array operand type '{binary.Left.Type.DisplayName}' is not supported by ordered comparison helper lowering.");
                }

                break;
            case StarkTypeKind.Named:
                if (rightType.Kind != StarkTypeKind.Named
                    || !string.Equals(leftType.NamedType, rightType.NamedType, StringComparison.Ordinal))
                {
                    Report(function, location, $"comparison operator '{binary.Operator}' named aggregate operands must have the same named type, but found '{binary.Left.Type.DisplayName}' and '{binary.Right.Type.DisplayName}'.");
                }

                if (!isEquality && !IsOrderedComparisonSupportedType(leftType, new HashSet<string>(StringComparer.Ordinal)))
                {
                    Report(function, location, $"comparison operator '{binary.Operator}' named aggregate operand type '{binary.Left.Type.DisplayName}' is not supported by ordered comparison helper lowering.");
                }

                break;
            default:
                Report(function, location, $"comparison operator '{binary.Operator}' is not supported for '{binary.Left.Type.DisplayName}'.");
                break;
        }
    }

    private void ValidateUnary(SsaFunction function, SsaUnaryRValue unary, SourceLocation? location)
    {
        switch (unary.Operator)
        {
            case SsaUnaryOperator.Negate:
                if (unary.Type.Kind is not (StarkTypeKind.Integer or StarkTypeKind.Float))
                {
                    Report(function, location, $"unary operator '{unary.Operator}' requires an integer or float result, but found '{unary.Type.DisplayName}'.");
                }
                else if (!IsConcreteNumericType(unary.Type))
                {
                    Report(function, location, $"unary operator '{unary.Operator}' result type '{unary.Type.DisplayName}' must be a concrete integer or supported LLVM float type.");
                }

                ValidateValueShape(function, unary.Type, unary.Operand.Type, $"unary operator '{unary.Operator}' operand", location);
                break;
            case SsaUnaryOperator.LogicalNot:
                if (unary.Type.Kind != StarkTypeKind.Bool || unary.Operand.Type.Kind != StarkTypeKind.Bool)
                {
                    Report(function, location, $"unary operator '{unary.Operator}' requires a bool operand and bool result.");
                }

                break;
            case SsaUnaryOperator.BitwiseNot:
                if (unary.Type.Kind != StarkTypeKind.Integer || unary.Operand.Type.Kind != StarkTypeKind.Integer)
                {
                    Report(function, location, $"unary operator '{unary.Operator}' requires an integer operand and integer result.");
                }
                else if (!IsConcreteIntegerType(unary.Type))
                {
                    Report(function, location, $"unary operator '{unary.Operator}' result type '{unary.Type.DisplayName}' must be a concrete integer.");
                }

                ValidateValueShape(function, unary.Type, unary.Operand.Type, $"unary operator '{unary.Operator}' operand", location);
                break;
        }
    }

    private void ValidateOptionalValues(
        SsaFunction function,
        IReadOnlyList<SsaValue?>? values,
        ISet<string> valueDefinitions,
        SourceLocation? location)
    {
        if (values is null)
        {
            return;
        }

        foreach (var value in values)
        {
            if (value is not null)
            {
                ValidateValue(function, value, valueDefinitions, location);
            }
        }
    }

    private void ValidateValue(
        SsaFunction function,
        SsaValue value,
        ISet<string> valueDefinitions,
        SourceLocation? location)
    {
        if (value is SsaValueReference reference
            && !valueDefinitions.Contains(reference.Name))
        {
            Report(function, location, $"value reference '%{reference.Name}' is not defined in this SSA function.");
            if (Environment.GetEnvironmentVariable("STARK_DEBUG_SSA_VALIDATE") == "1")
            {
                Console.Error.WriteLine($"[ssa-validate-debug] function '{function.Name}' missing '%{reference.Name}'; blocks:");
                foreach (var debugBlock in function.Blocks)
                {
                    var names = debugBlock.Phis.Select(static phi => phi.ResultName)
                        .Concat(debugBlock.Instructions.OfType<SsaValueInstruction>().Select(static instruction => instruction.ResultName));
                    Console.Error.WriteLine($"  block {debugBlock.Id} '{debugBlock.Label}': defs [{string.Join(", ", names)}] terminator {debugBlock.Terminator.Kind} -> [{string.Join(", ", debugBlock.Terminator.Targets)}]");
                    foreach (var debugPhi in debugBlock.Phis)
                    {
                        Console.Error.WriteLine($"    phi {debugPhi.ResultName}: {string.Join(", ", debugPhi.Incomings.Select(static incoming => $"[{(incoming.Value is SsaValueReference r ? "%" + r.Name : incoming.Value.GetType().Name)} from {incoming.PredecessorBlockId}]"))}");
                    }
                }
            }
        }

        switch (value)
        {
            case SsaValueReference:
            case SsaFloatConstant:
                break;
            case SsaIntegerConstant integerConstant:
                ValidateIntegerConstant(function, integerConstant, location);
                break;
            case SsaStringConstant stringConstant:
                ValidateTextType(function, stringConstant.Type, "string constant", location);
                break;
            case SsaTextDataAddressValue textDataAddress:
                ValidateTextDataAddress(function, textDataAddress, location);
                break;
            case SsaBoolConstant:
            case SsaNullConstant:
            case SsaUndefValue:
            case SsaZeroInitializerValue:
                break;
            case SsaGlobalAddressValue globalAddress:
                ValidateGlobalAddress(function, globalAddress, location);
                ValidateRawPointerType(function, globalAddress.Type, "global address result", location);
                ValidatePointerElementShape(function, globalAddress.Type, globalAddress.PointeeType, "global address result", location);
                break;
            case SsaFunctionAddressValue functionAddress:
                ValidateFunctionAddress(function, functionAddress, location);
                break;
            case SsaClosureValue closure:
                ValidateClosureValue(function, closure, location);
                break;
            default:
                Report(function, location, $"unsupported SSA value type '{value.GetType().Name}' reached validation.");
                break;
        }
    }

    private void ValidateIntegerConstant(
        SsaFunction function,
        SsaIntegerConstant constant,
        SourceLocation? location)
    {
        if (!IsConcreteIntegerType(constant.Type))
        {
            Report(
                function,
                location,
                $"integer constant value '{constant.Value}' must use a concrete integer storage type, but found '{constant.Type.DisplayName}'.");
            return;
        }

        if (!StarkTypeSymbols.IntegerValueFitsStorage(constant.Value, constant.Type))
        {
            Report(
                function,
                location,
                $"integer constant value '{constant.Value}' does not fit storage type '{constant.Type.DisplayName}'.");
            return;
        }

        if (!StarkTypeSymbols.IntegerValueFitsEffectiveRange(constant.Value, constant.Type))
        {
            Report(
                function,
                location,
                $"integer constant value '{constant.Value}' is outside effective range '{constant.Type.DisplayName}'.");
        }
    }

    private void ValidateGlobalLoad(
        SsaFunction function,
        SsaLoadGlobalRValue loadGlobal,
        SourceLocation? location)
    {
        if (!TryGetKnownGlobal(function, loadGlobal.GlobalName, "global load", location, out var global))
        {
            return;
        }

        if (global.Type is { } globalType)
        {
            ValidateValueShape(function, globalType, loadGlobal.Type, $"global load '{loadGlobal.GlobalName}' result", location);
        }
    }

    private void ValidateGlobalAddress(
        SsaFunction function,
        SsaGlobalAddressValue globalAddress,
        SourceLocation? location)
    {
        // Synthesized trait-object vtables are emitted by the module surface emitter,
        // not the user/global type model, so they are not in the known-global set.
        if (DynTraitFacts.IsVtableGlobalName(globalAddress.GlobalName))
        {
            return;
        }

        if (!TryGetKnownGlobal(function, globalAddress.GlobalName, "global address", location, out var global))
        {
            return;
        }

        if (global.Type is { } globalType)
        {
            ValidateValueShape(function, globalType, globalAddress.PointeeType, $"global address '{globalAddress.GlobalName}' pointee", location);
        }
    }

    private void ValidateGlobalStore(
        SsaFunction function,
        SsaStoreGlobalInstruction storeGlobal,
        SourceLocation? location)
    {
        ValidateValueShape(function, storeGlobal.GlobalType, storeGlobal.Value.Type, $"global store '{storeGlobal.GlobalName}' value", location);

        if (!TryGetKnownGlobal(function, storeGlobal.GlobalName, "global store", location, out var global))
        {
            return;
        }

        if (global.BindingKind != GlobalBindingKind.Mutable)
        {
            Report(function, location, $"global store target '{storeGlobal.GlobalName}' must be mutable, but its binding is '{global.BindingKind}'.");
        }

        if (global.Type is { } globalType)
        {
            ValidateValueShape(function, globalType, storeGlobal.GlobalType, $"global store '{storeGlobal.GlobalName}' target", location);
        }
    }

    private bool TryGetKnownGlobal(
        SsaFunction function,
        string globalName,
        string usage,
        SourceLocation? location,
        out KnownGlobalFact global)
    {
        if (_knownGlobals.TryGetValue(globalName, out global!))
        {
            return true;
        }

        if (_typeModel is null && _knownGlobals.Count == 0)
        {
            return false;
        }

        Report(function, location, $"{usage} references unknown global '{globalName}'.");
        return false;
    }

    private void ValidateLocalExists(
        SsaFunction function,
        string localName,
        IReadOnlyDictionary<string, StarkTypeSymbol> localDefinitions,
        SourceLocation? location)
    {
        if (!localDefinitions.ContainsKey(localName))
        {
            Report(function, location, $"local '{localName}' is used before it is allocated in SSA.");
        }
    }

    private static bool IsReturnCompatible(StarkTypeSymbol returnType, StarkTypeSymbol valueType)
    {
        if (TypeCompatibilityFacts.CanAssign(returnType, valueType))
        {
            return true;
        }

        if (returnType.BorrowKind == StarkBorrowKind.None)
        {
            return false;
        }

        var returnedValueType = StarkTypeSymbols.BorrowReturnValueType(returnType);
        if (TypeCompatibilityFacts.CanAssign(returnedValueType, valueType))
        {
            return true;
        }

        return StarkTypeSymbols.IsPointerBackedBorrowReturn(returnType)
            && valueType.Kind == StarkTypeKind.RawPointer
            && valueType.ElementType is { } pointeeType
            && (!returnType.IsMutableView || valueType.IsMutablePointer)
            && TypeCompatibilityFacts.CanAssign(returnedValueType, pointeeType);
    }

    private void ValidateDynamicType(
        SsaFunction function,
        StarkTypeSymbol storageType,
        string operation,
        SourceLocation? location)
    {
        if (storageType.Kind != StarkTypeKind.Dynamic || storageType.ElementType is null)
        {
            Report(function, location, $"{operation} requires a dynamic storage type, but found '{storageType.DisplayName}'.");
        }
    }

    private void ValidateDynamicMoveShape(
        SsaFunction function,
        StarkTypeSymbol storageType,
        StarkTypeSymbol resultType,
        string operation,
        SourceLocation? location)
    {
        ValidateDynamicType(function, storageType, operation, location);
        if (storageType.ElementType is { } elementType
            && NormalizeType(elementType) != NormalizeType(resultType))
        {
            Report(function, location, $"{operation} result type '{resultType.DisplayName}' does not match element type '{elementType.DisplayName}'.");
        }
    }

    private void ValidateDynamicElementLayout(
        SsaFunction function,
        StarkTypeSymbol storageType,
        string operation,
        SourceLocation? location)
    {
        if (storageType.Kind != StarkTypeKind.Dynamic || storageType.ElementType is not { } elementType)
        {
            return;
        }

        var normalizedElementType = NormalizeType(elementType);
        if (LlvmAggregateEmissionSupport.TryGetConcreteTypeLayout(
                normalizedElementType,
                _context.Options.TargetInfo,
                _namedTypes,
                _enumLayouts,
                _publishedConcreteLayouts) is not { SizeBytes: > 0 })
        {
            Report(function, location, $"{operation} requires a concrete element layout for '{elementType.DisplayName}'.");
        }
    }

    private void ValidateConcreteLayout(
        SsaFunction function,
        StarkTypeSymbol type,
        string operation,
        SourceLocation? location)
    {
        if (TryGetConcreteTypeLayout(type) is not { SizeBytes: > 0 })
        {
            Report(function, location, $"{operation} requires a concrete non-empty layout for '{type.DisplayName}'.");
        }
    }

    private void ValidateCopyMemoryAddressShape(
        SsaFunction function,
        StarkTypeSymbol pointerType,
        StarkTypeSymbol copyType,
        string usage,
        SourceLocation? location)
    {
        if (pointerType.Kind != StarkTypeKind.RawPointer)
        {
            return;
        }

        if (pointerType.ElementType is not { } actualElementType)
        {
            Report(function, location, $"{usage} raw pointer type is missing its pointee type.");
            return;
        }

        if (HaveSameLlvmValueShape(copyType, actualElementType))
        {
            return;
        }

        var normalizedCopyType = NormalizeType(copyType);
        if (normalizedCopyType.Kind == StarkTypeKind.FixedArray
            && normalizedCopyType.ElementType is { } copyElementType
            && HaveSameLlvmValueShape(copyElementType, actualElementType))
        {
            return;
        }

        Report(function, location, $"{usage} pointee type '{actualElementType.DisplayName}' does not match expected LLVM value shape '{copyType.DisplayName}'.");
    }

    private void ValidateSliceResultType(
        SsaFunction function,
        StarkTypeSymbol sliceType,
        StarkTypeSymbol? elementType,
        string usage,
        SourceLocation? location)
    {
        if (sliceType.Kind != StarkTypeKind.Slice || sliceType.ElementType is not { } sliceElementType)
        {
            Report(function, location, $"{usage} requires a slice result type, but found '{sliceType.DisplayName}'.");
            return;
        }

        if (elementType is not null)
        {
            ValidateValueShape(function, elementType, sliceElementType, $"{usage} element", location);
        }
    }

    private void ValidateElementAddressResult(
        SsaFunction function,
        SsaElementAddressRValue elementAddress,
        SourceLocation? location)
    {
        var aggregateType = NormalizeType(elementAddress.AggregateType);
        if (aggregateType.Kind == StarkTypeKind.FixedArray)
        {
            if (aggregateType.ElementType is not { } fixedArrayElementType)
            {
                Report(function, location, "element address fixed-array aggregate type is missing its element type.");
                return;
            }

            // A fixed-array aggregate type reaches element addressing two ways:
            // indexing into a fixed array (result pointee is the array element)
            // and addressing an element of dynamic or raw storage whose element
            // type is itself a fixed array (result pointee is the whole array).
            // Accept either pointee shape.
            if (elementAddress.Type.Kind == StarkTypeKind.RawPointer
                && elementAddress.Type.ElementType is { } pointeeType
                && (HaveSameLlvmValueShape(fixedArrayElementType, pointeeType)
                    || HaveSameLlvmValueShape(aggregateType, pointeeType)))
            {
                return;
            }

            ValidatePointerElementShape(function, elementAddress.Type, fixedArrayElementType, "element address result", location);
            return;
        }

        ValidatePointerElementShape(function, elementAddress.Type, aggregateType, "element address result", location);
    }

    private void ValidateRawPointerValue(
        SsaFunction function,
        SsaValue value,
        string usage,
        SourceLocation? location)
    {
        ValidateRawPointerType(function, value.Type, usage, location);
    }

    private void ValidateRawPointerType(
        SsaFunction function,
        StarkTypeSymbol type,
        string usage,
        SourceLocation? location)
    {
        if (type.Kind != StarkTypeKind.RawPointer)
        {
            Report(function, location, $"{usage} must be a raw pointer, but found '{type.DisplayName}'.");
        }
    }

    private void ValidatePointerElementShape(
        SsaFunction function,
        StarkTypeSymbol pointerType,
        StarkTypeSymbol expectedElementType,
        string usage,
        SourceLocation? location)
    {
        if (pointerType.Kind != StarkTypeKind.RawPointer)
        {
            return;
        }

        if (pointerType.ElementType is not { } actualElementType)
        {
            Report(function, location, $"{usage} raw pointer type is missing its pointee type.");
            return;
        }

        ValidateValueShape(function, expectedElementType, actualElementType, $"{usage} pointee", location);
    }

    private void ValidateValueShape(
        SsaFunction function,
        StarkTypeSymbol expectedType,
        StarkTypeSymbol actualType,
        string usage,
        SourceLocation? location)
    {
        if (!HaveSameLlvmValueShape(expectedType, actualType))
        {
            Report(function, location, $"{usage} type '{actualType.DisplayName}' does not match expected LLVM value shape '{expectedType.DisplayName}'.");
        }
    }

    private void ValidateIntegerValue(
        SsaFunction function,
        SsaValue value,
        string usage,
        SourceLocation? location)
    {
        if (!IsConcreteIntegerType(value.Type))
        {
            Report(function, location, $"{usage} must be a concrete integer, but found '{value.Type.DisplayName}'.");
        }
    }

    private void ValidateDynamicStorageCapacityInteger(
        SsaFunction function,
        SsaValue value,
        string usage,
        SourceLocation? location)
    {
        if (value.Type.Kind != StarkTypeKind.Integer || value.Type.BitWidth is not int bitWidth)
        {
            return;
        }

        if (bitWidth is <= 0 or > 64)
        {
            Report(function, location, $"{usage} width '{bitWidth}' is not supported by dynamic storage lowering.");
        }
    }

    private static bool IsConcreteNumericType(StarkTypeSymbol type)
    {
        return IsConcreteIntegerType(type) || IsConcreteFloatType(type);
    }

    private static bool IsConcreteIntegerType(StarkTypeSymbol type)
    {
        if (!StarkTypeSymbols.TryGetIntegerStorageBounds(type, out var storageMin, out var storageMax))
        {
            return false;
        }

        if (type.RangeMin is null && type.RangeMax is null)
        {
            return true;
        }

        if (type.RangeMin is not { } rangeMin || type.RangeMax is not { } rangeMax)
        {
            return false;
        }

        return rangeMin <= rangeMax
               && rangeMin >= storageMin
               && rangeMax <= storageMax;
    }

    private static bool IsConcreteFloatType(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.Float && IsSupportedFloatIntrinsicWidth(type.BitWidth);
    }

    private static bool IsSupportedFloatIntrinsicWidth(int? bitWidth)
    {
        return bitWidth is 16 or 32 or 64 or 80 or 128;
    }

    private void ValidateTextValue(
        SsaFunction function,
        SsaValue value,
        string usage,
        SourceLocation? location)
    {
        ValidateTextType(function, value.Type, usage, location);
    }

    private void ValidateTextType(
        SsaFunction function,
        StarkTypeSymbol type,
        string usage,
        SourceLocation? location)
    {
        if (!IsTextType(type))
        {
            Report(function, location, $"{usage} requires an ascii/unicode value, but found '{type.DisplayName}'.");
        }
    }

    private void ValidateTextDataAddress(
        SsaFunction function,
        SsaTextDataAddressValue textDataAddress,
        SourceLocation? location)
    {
        ValidateTextType(function, textDataAddress.TextType, "text data address literal", location);
        ValidateRawPointerType(function, textDataAddress.Type, "text data address result", location);

        if (TryGetTextUnitType(textDataAddress.TextType) is { } expectedUnitType)
        {
            ValidatePointerElementShape(function, textDataAddress.Type, expectedUnitType, "text data address result", location);
        }
    }

    private void ValidateFunctionAddress(
        SsaFunction function,
        SsaFunctionAddressValue functionAddress,
        SourceLocation? location)
    {
        if (functionAddress.Type.Kind != StarkTypeKind.FunctionPointer)
        {
            Report(function, location, $"function address '{functionAddress.FunctionName}' requires a function-pointer type, but found '{functionAddress.Type.DisplayName}'.");
            return;
        }

        if (functionAddress.Type.FunctionPointerKind is null
            || functionAddress.Type.FunctionPointerReturnType is null
            || functionAddress.Type.FunctionPointerParameterTypes is null)
        {
            Report(function, location, $"function address '{functionAddress.FunctionName}' is missing function-pointer ABI metadata.");
            return;
        }

        if (!_abiModel.Functions.TryGetValue(functionAddress.FunctionName, out var abiFunction))
        {
            Report(function, location, $"function address target '{functionAddress.FunctionName}' is missing ABI lowering.");
            return;
        }

        ValidateValueShape(
            function,
            functionAddress.Type.FunctionPointerReturnType,
            abiFunction.SourceReturnType,
            $"function address '{functionAddress.FunctionName}' return type",
            location);

        var pointerParameters = functionAddress.Type.FunctionPointerParameterTypes;
        if (pointerParameters.Count != abiFunction.UserParameters.Count)
        {
            Report(
                function,
                location,
                $"function address '{functionAddress.FunctionName}' parameter count mismatch: expected {pointerParameters.Count}, got {abiFunction.UserParameters.Count}.");
        }

        var parameterCount = Math.Min(pointerParameters.Count, abiFunction.UserParameters.Count);
        for (var index = 0; index < parameterCount; index++)
        {
            ValidateValueShape(
                function,
                pointerParameters[index],
                abiFunction.UserParameters[index].SourceType,
                $"function address '{functionAddress.FunctionName}' parameter {index + 1}",
                location);

            var actualCountExpression = StarkTypeSymbols.GetFunctionPointerParameterRawPointerElementCountExpression(
                functionAddress.Type,
                index);
            var expectedCountExpression = MapAbiRawPointerElementCountExpression(
                abiFunction.UserParameters[index].RawPointerElementCountExpression,
                abiFunction.UserParameters);
            if (!string.Equals(actualCountExpression, expectedCountExpression, StringComparison.Ordinal))
            {
                Report(
                    function,
                    location,
                    $"function address '{functionAddress.FunctionName}' parameter {index + 1} bounded raw-pointer count mismatch: expected '{expectedCountExpression ?? "<none>"}', got '{actualCountExpression ?? "<none>"}'.");
            }
        }
    }

    private void ValidateClosureValue(
        SsaFunction function,
        SsaClosureValue closure,
        SourceLocation? location)
    {
        if (closure.Type.Kind != StarkTypeKind.Closure)
        {
            Report(function, location, $"closure value '{closure.InvokeFunctionName}' requires a closure type, but found '{closure.Type.DisplayName}'.");
            return;
        }

        ValidateFunctionAddress(
            function,
            new SsaFunctionAddressValue(
                closure.InvokeFunctionName,
                CallableValueFacts.BuildClosureInvokeFunctionPointerType(closure.Type)),
            location);
        if (closure.Type.ClosureStorageKind == StarkClosureStorageKind.Heap)
        {
            ValidateFunctionAddress(
                function,
                new SsaFunctionAddressValue(
                    CallableValueFacts.EmptyClosureDropFunctionName,
                    CallableValueFacts.BuildClosureDropFunctionPointerType()),
                location);
        }
    }

    private static string? MapAbiRawPointerElementCountExpression(
        string? expression,
        IReadOnlyList<AbiParameterSymbol> parameters)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        for (var index = 0; index < parameters.Count; index++)
        {
            if (string.Equals(expression, parameters[index].SourceName, StringComparison.Ordinal))
            {
                return $"arg{index}";
            }
        }

        return expression;
    }

    private bool IsOrderedComparisonSupportedType(StarkTypeSymbol type, ISet<string> activeNamedTypes)
    {
        var normalizedType = NormalizeType(type);
        switch (normalizedType.Kind)
        {
            case StarkTypeKind.Integer:
                return normalizedType.BitWidth is not null;
            case StarkTypeKind.Float:
                return IsSupportedFloatIntrinsicWidth(normalizedType.BitWidth);
            case StarkTypeKind.Bool:
            case StarkTypeKind.RawPointer:
            case StarkTypeKind.Ascii:
            case StarkTypeKind.Unicode:
                return true;
            case StarkTypeKind.FixedArray:
                return normalizedType.ElementType is { } elementType
                    && normalizedType.FixedLength is int
                    && IsOrderedComparisonSupportedType(elementType, activeNamedTypes);
            case StarkTypeKind.Named:
                return IsNamedOrderedComparisonSupported(normalizedType, activeNamedTypes);
            default:
                return false;
        }
    }

    private bool IsNamedOrderedComparisonSupported(StarkTypeSymbol type, ISet<string> activeNamedTypes)
    {
        if (type.NamedType is not { } typeName
            || !_namedTypes.TryGetValue(typeName, out var namedType)
            || !activeNamedTypes.Add(typeName)
            || TryGetConcreteTypeLayout(type) is not { SizeBytes: > 0 })
        {
            return false;
        }

        try
        {
            return namedType.Kind switch
            {
                DeclarationKind.Struct or DeclarationKind.Record =>
                    namedType.OrderedFields.All(field => IsOrderedComparisonSupportedType(field.Type, activeNamedTypes)),
                DeclarationKind.Enum when _enumLayouts.TryGetValue(namedType.Name, out var enumLayout) =>
                    enumLayout.OrderedFields.All(field => IsOrderedComparisonSupportedType(field.Type, activeNamedTypes)),
                _ => false
            };
        }
        finally
        {
            activeNamedTypes.Remove(typeName);
        }
    }

    private ConcreteTypeLayout? TryGetConcreteTypeLayout(StarkTypeSymbol type)
    {
        return LlvmAggregateEmissionSupport.TryGetConcreteTypeLayout(
            NormalizeType(type),
            _context.Options.TargetInfo,
            _namedTypes,
            _enumLayouts,
            _publishedConcreteLayouts);
    }

    private static StarkTypeSymbol? TryGetTextUnitType(StarkTypeSymbol type)
    {
        return type.Kind switch
        {
            StarkTypeKind.Ascii => StarkTypeSymbols.Integer(8),
            StarkTypeKind.Unicode => StarkTypeSymbols.Integer(32),
            _ => null
        };
    }

    private void ValidateConversion(SsaFunction function, SsaConvertRValue convert, SourceLocation? location)
    {
        if (IsPointerBackedBorrowRuntimePointerConversion(convert.Operand.Type, convert.TargetType))
        {
            return;
        }

        var sourceType = NormalizeType(convert.Operand.Type);
        var targetType = NormalizeType(convert.TargetType);
        if (HaveSameLlvmValueShape(sourceType, targetType))
        {
            return;
        }

        switch (sourceType.Kind)
        {
            case StarkTypeKind.Integer when targetType.Kind == StarkTypeKind.Integer:
                ValidateConcreteIntegerType(function, sourceType, "conversion source", location);
                ValidateConcreteIntegerType(function, targetType, "conversion target", location);
                return;
            case StarkTypeKind.Integer when targetType.Kind == StarkTypeKind.Float:
                ValidateConcreteIntegerType(function, sourceType, "conversion source", location);
                ValidateConcreteFloatType(function, targetType, "conversion target", location);
                return;
            case StarkTypeKind.Integer when targetType.Kind == StarkTypeKind.RawPointer:
                ValidateConcreteIntegerType(function, sourceType, "conversion source", location);
                return;
            case StarkTypeKind.Float when targetType.Kind == StarkTypeKind.Integer:
                ValidateConcreteFloatType(function, sourceType, "conversion source", location);
                ValidateConcreteIntegerType(function, targetType, "conversion target", location);
                return;
            case StarkTypeKind.Float when targetType.Kind == StarkTypeKind.Float:
                ValidateConcreteFloatType(function, sourceType, "conversion source", location);
                ValidateConcreteFloatType(function, targetType, "conversion target", location);
                return;
            case StarkTypeKind.RawPointer when targetType.Kind == StarkTypeKind.RawPointer:
                return;
            case StarkTypeKind.RawPointer when targetType.Kind == StarkTypeKind.Integer:
                ValidateConcreteIntegerType(function, targetType, "conversion target", location);
                return;
            default:
                Report(function, location, $"conversion from '{convert.Operand.Type.DisplayName}' to '{convert.TargetType.DisplayName}' is not supported by SSA LLVM emission.");
                return;
        }
    }

    private static bool IsPointerBackedBorrowRuntimePointerConversion(
        StarkTypeSymbol sourceType,
        StarkTypeSymbol targetType)
    {
        if (StarkTypeSymbols.IsPointerBackedBorrowType(sourceType)
            && targetType.Kind == StarkTypeKind.RawPointer
            && targetType.ElementType is { } targetElementType)
        {
            return HaveSameLlvmValueShape(StarkTypeSymbols.BorrowReturnValueType(sourceType), targetElementType);
        }

        if (sourceType.Kind == StarkTypeKind.RawPointer
            && sourceType.ElementType is { } sourceElementType
            && StarkTypeSymbols.IsPointerBackedBorrowType(targetType))
        {
            return HaveSameLlvmValueShape(sourceElementType, StarkTypeSymbols.BorrowReturnValueType(targetType));
        }

        return false;
    }

    private void ValidateConcreteIntegerType(
        SsaFunction function,
        StarkTypeSymbol type,
        string usage,
        SourceLocation? location)
    {
        if (!IsConcreteIntegerType(type))
        {
            Report(function, location, $"{usage} type '{type.DisplayName}' must be a concrete integer for SSA LLVM emission.");
        }
    }

    private void ValidateConcreteFloatType(
        SsaFunction function,
        StarkTypeSymbol type,
        string usage,
        SourceLocation? location)
    {
        if (!IsConcreteFloatType(type))
        {
            Report(function, location, $"{usage} type '{type.DisplayName}' must be a supported LLVM float type for SSA LLVM emission.");
        }
    }

    private static bool IsTextType(StarkTypeSymbol type)
    {
        return type.Kind is StarkTypeKind.Ascii or StarkTypeKind.Unicode;
    }

    private static bool HaveSameLlvmValueShape(StarkTypeSymbol expectedType, StarkTypeSymbol actualType)
    {
        var expected = NormalizeType(expectedType);
        var actual = NormalizeType(actualType);
        if (expected.Kind != actual.Kind)
        {
            return false;
        }

        return expected.Kind switch
        {
            StarkTypeKind.Void or StarkTypeKind.Bool or StarkTypeKind.Ascii or StarkTypeKind.Unicode => true,
            StarkTypeKind.Integer or StarkTypeKind.Float => expected.BitWidth == actual.BitWidth,
            StarkTypeKind.RawPointer or StarkTypeKind.FunctionPointer or StarkTypeKind.Null => true,
            StarkTypeKind.FixedArray => expected.FixedLength == actual.FixedLength
                && expected.ElementType is not null
                && actual.ElementType is not null
                && HaveSameLlvmValueShape(expected.ElementType, actual.ElementType),
            StarkTypeKind.Slice or StarkTypeKind.Dynamic => expected.ElementType is not null
                && actual.ElementType is not null
                && HaveSameLlvmValueShape(expected.ElementType, actual.ElementType),
            StarkTypeKind.Closure => true,
            StarkTypeKind.Named => string.Equals(expected.NamedType, actual.NamedType, StringComparison.Ordinal),
            _ => expected == actual
        };
    }

    private static StarkTypeSymbol NormalizeType(StarkTypeSymbol type)
    {
        return type with
        {
            BorrowKind = StarkBorrowKind.None,
            AccessKind = StarkAccessKind.None,
            InitializationKind = StarkInitializationKind.None,
            IsMutableView = false
        };
    }

    private bool ContainsUnboundGenericPlaceholder(StarkTypeSymbol type)
    {
        var normalized = NormalizeType(type);
        if (normalized.Kind == StarkTypeKind.Named
            && normalized.NamedType is { } namedType
            && normalized.TypeArguments is not { Count: > 0 }
            && !namedType.Contains('.', StringComparison.Ordinal)
            && !_namedTypes.ContainsKey(namedType))
        {
            return true;
        }

        if (normalized.ElementType is not null
            && ContainsUnboundGenericPlaceholder(normalized.ElementType))
        {
            return true;
        }

        if (normalized.TypeArguments is { Count: > 0 }
            && normalized.TypeArguments.Any(ContainsUnboundGenericPlaceholder))
        {
            return true;
        }

        if (normalized.FunctionPointerReturnType is not null
            && ContainsUnboundGenericPlaceholder(normalized.FunctionPointerReturnType))
        {
            return true;
        }

        if (normalized.FunctionPointerParameterTypes is { Count: > 0 }
            && normalized.FunctionPointerParameterTypes.Any(ContainsUnboundGenericPlaceholder))
        {
            return true;
        }

        if (normalized.ClosureReturnType is not null
            && ContainsUnboundGenericPlaceholder(normalized.ClosureReturnType))
        {
            return true;
        }

        if (normalized.ClosureParameterTypes is { Count: > 0 }
            && normalized.ClosureParameterTypes.Any(ContainsUnboundGenericPlaceholder))
        {
            return true;
        }

        return normalized.Kind == StarkTypeKind.AssociatedType;
    }

    private string CurrentModuleName => _typeModel?.ModuleName ?? _ssa.ModuleName;

    private bool TryResolveSystemMathBuiltin(
        TypedFunctionSignature function,
        out SystemMathBuiltinKind builtinKind)
    {
        return TryGetSystemMathBuiltin(CurrentModuleName, function.DisplaySourceName, out builtinKind)
            || TryGetSystemMathBuiltin(moduleName: string.Empty, function.Name, out builtinKind);
    }

    private bool TryResolveSystemBitOperationsBuiltin(
        TypedFunctionSignature function,
        out SystemBitOperationsBuiltinKind builtinKind)
    {
        return TryGetSystemBitOperationsBuiltin(CurrentModuleName, function.DisplaySourceName, out builtinKind)
            || TryGetSystemBitOperationsBuiltin(moduleName: string.Empty, function.Name, out builtinKind);
    }

    private bool TryResolveSystemMemoryBuiltin(
        TypedFunctionSignature function,
        out SystemMemoryBuiltinKind builtinKind)
    {
        return TryGetSystemMemoryBuiltin(CurrentModuleName, function.DisplaySourceName, out builtinKind)
            || TryGetSystemMemoryBuiltin(moduleName: string.Empty, function.Name, out builtinKind);
    }

    private bool TryResolveSystemCollectionsBuiltin(
        TypedFunctionSignature function,
        out SystemCollectionsBuiltinKind builtinKind)
    {
        if (TryGetSystemCollectionsBuiltin(CurrentModuleName, function.TemplateName ?? function.DisplaySourceName, out builtinKind)
            || TryGetSystemCollectionsBuiltin(CurrentModuleName, function.DisplaySourceName, out builtinKind)
            || TryGetSystemCollectionsBuiltin(moduleName: string.Empty, function.TemplateName ?? function.Name, out builtinKind))
        {
            return builtinKind is not (SystemCollectionsBuiltinKind.DictionaryKeyEquals or SystemCollectionsBuiltinKind.DictionaryKeyHash)
                || !function.IsGeneric
                || function.IsGenericInstantiation;
        }

        return false;
    }

    private bool TryResolveSystemRuntimeBuiltin(
        TypedFunctionSignature function,
        out SystemRuntimeBuiltinKind builtinKind)
    {
        return TryGetSystemRuntimeBuiltin(CurrentModuleName, function.TemplateName ?? function.DisplaySourceName, out builtinKind)
            || TryGetSystemRuntimeBuiltin(CurrentModuleName, function.DisplaySourceName, out builtinKind)
            || TryGetSystemRuntimeBuiltin(moduleName: string.Empty, function.TemplateName ?? function.Name, out builtinKind);
    }

    private static bool TryGetSystemMathBuiltin(
        string moduleName,
        string functionName,
        out SystemMathBuiltinKind builtinKind)
    {
        builtinKind = default;

        string sourceName;
        if (functionName.Contains('.', StringComparison.Ordinal))
        {
            const string prefix = "System.Math.";
            if (!functionName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName[prefix.Length..];
        }
        else
        {
            if (!string.Equals(moduleName, "System.Math", StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName;
        }

        builtinKind = sourceName switch
        {
            "Sin" => SystemMathBuiltinKind.Sin,
            "Cos" => SystemMathBuiltinKind.Cos,
            "Tan" => SystemMathBuiltinKind.Tan,
            "Exp" => SystemMathBuiltinKind.Exp,
            "Exp2" => SystemMathBuiltinKind.Exp2,
            "Log" => SystemMathBuiltinKind.Log,
            "Log2" => SystemMathBuiltinKind.Log2,
            "Log10" => SystemMathBuiltinKind.Log10,
            "Asin" => SystemMathBuiltinKind.Asin,
            "Acos" => SystemMathBuiltinKind.Acos,
            "Atan" => SystemMathBuiltinKind.Atan,
            "Atan2" => SystemMathBuiltinKind.Atan2,
            "Pow" => SystemMathBuiltinKind.Pow,
            "Sinh" => SystemMathBuiltinKind.Sinh,
            "Cosh" => SystemMathBuiltinKind.Cosh,
            "Tanh" => SystemMathBuiltinKind.Tanh,
            "SinCos" => SystemMathBuiltinKind.SinCos,
            "Sqrt" => SystemMathBuiltinKind.Sqrt,
            "FusedMultiplyAdd" => SystemMathBuiltinKind.FusedMultiplyAdd,
            "ReciprocalEstimate" => SystemMathBuiltinKind.ReciprocalEstimate,
            "ReciprocalSqrtEstimate" => SystemMathBuiltinKind.ReciprocalSqrtEstimate,
            "Ceiling" => SystemMathBuiltinKind.Ceiling,
            "Floor" => SystemMathBuiltinKind.Floor,
            "Truncate" => SystemMathBuiltinKind.Truncate,
            "Round" => SystemMathBuiltinKind.Round,
            "Min" => SystemMathBuiltinKind.Min,
            "Max" => SystemMathBuiltinKind.Max,
            _ => default
        };

        return sourceName is
            "Sin" or "Cos" or "Tan"
            or "Exp" or "Exp2"
            or "Log" or "Log2" or "Log10"
            or "Asin" or "Acos" or "Atan" or "Atan2"
            or "Pow"
            or "Sinh" or "Cosh" or "Tanh"
            or "SinCos"
            or "Sqrt" or "FusedMultiplyAdd" or "ReciprocalEstimate" or "ReciprocalSqrtEstimate"
            or "Ceiling" or "Floor" or "Truncate" or "Round"
            or "Min" or "Max";
    }

    private static bool TryGetSystemBitOperationsBuiltin(
        string moduleName,
        string functionName,
        out SystemBitOperationsBuiltinKind builtinKind)
    {
        builtinKind = default;

        string sourceName;
        if (functionName.Contains('.', StringComparison.Ordinal))
        {
            const string prefix = "System.BitOperations.";
            if (!functionName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName[prefix.Length..];
        }
        else
        {
            if (!string.Equals(moduleName, "System.BitOperations", StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName;
        }

        builtinKind = sourceName switch
        {
            "LeadingZeroCount" => SystemBitOperationsBuiltinKind.LeadingZeroCount,
            "TrailingZeroCount" => SystemBitOperationsBuiltinKind.TrailingZeroCount,
            "PopCount" => SystemBitOperationsBuiltinKind.PopCount,
            "RotateLeft" => SystemBitOperationsBuiltinKind.RotateLeft,
            "RotateRight" => SystemBitOperationsBuiltinKind.RotateRight,
            _ => default
        };

        return sourceName is
            "LeadingZeroCount"
            or "TrailingZeroCount"
            or "PopCount"
            or "RotateLeft"
            or "RotateRight";
    }

    private static bool TryGetSystemMemoryBuiltin(
        string moduleName,
        string functionName,
        out SystemMemoryBuiltinKind builtinKind)
    {
        builtinKind = default;

        string sourceName;
        if (functionName.Contains('.', StringComparison.Ordinal))
        {
            const string prefix = "System.Memory.";
            if (!functionName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName[prefix.Length..];
        }
        else
        {
            if (!string.Equals(moduleName, "System.Memory", StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName;
        }

        builtinKind = sourceName switch
        {
            "Allocate" => SystemMemoryBuiltinKind.Allocate,
            "Reallocate" => SystemMemoryBuiltinKind.Reallocate,
            "Free" => SystemMemoryBuiltinKind.Free,
            _ => default
        };

        return sourceName is "Allocate" or "Reallocate" or "Free";
    }

    private static bool TryGetSystemCollectionsBuiltin(
        string moduleName,
        string functionName,
        out SystemCollectionsBuiltinKind builtinKind)
    {
        builtinKind = default;

        string sourceName;
        if (functionName.Contains('.', StringComparison.Ordinal))
        {
            const string prefix = "System.Collections.";
            if (!functionName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName[prefix.Length..];
        }
        else
        {
            if (!string.Equals(moduleName, "System.Collections", StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName;
        }

        builtinKind = sourceName switch
        {
            "List.AsSlice" => SystemCollectionsBuiltinKind.ListAsSlice,
            "List.AsMutableSlice" => SystemCollectionsBuiltinKind.ListAsMutableSlice,
            "DictionaryKey.Equals" => SystemCollectionsBuiltinKind.DictionaryKeyEquals,
            "DictionaryKey.Hash" => SystemCollectionsBuiltinKind.DictionaryKeyHash,
            _ => default
        };

        return sourceName is "List.AsSlice" or "List.AsMutableSlice" or "DictionaryKey.Equals" or "DictionaryKey.Hash";
    }

    private static bool TryGetSystemRuntimeBuiltin(
        string moduleName,
        string functionName,
        out SystemRuntimeBuiltinKind builtinKind)
    {
        builtinKind = default;

        string sourceName;
        if (functionName.Contains('.', StringComparison.Ordinal))
        {
            const string prefix = "System.Runtime.";
            if (!functionName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName[prefix.Length..];
        }
        else
        {
            if (!string.Equals(moduleName, "System.Runtime", StringComparison.Ordinal))
            {
                return false;
            }

            sourceName = functionName;
        }

        builtinKind = sourceName switch
        {
            "GetByteSliceParts" => SystemRuntimeBuiltinKind.GetByteSliceParts,
            "GetMutableByteSliceParts" => SystemRuntimeBuiltinKind.GetMutableByteSliceParts,
            _ => default
        };

        return sourceName is "GetByteSliceParts" or "GetMutableByteSliceParts";
    }

    private static int GetSystemMathIntrinsicArity(SystemMathBuiltinKind builtinKind)
    {
        return builtinKind switch
        {
            SystemMathBuiltinKind.Atan2 or SystemMathBuiltinKind.Pow => 2,
            SystemMathBuiltinKind.FusedMultiplyAdd => 3,
            SystemMathBuiltinKind.Min or SystemMathBuiltinKind.Max => 2,
            _ => 1
        };
    }

    private static bool IsHardwareAsmSystemMathBuiltin(SystemMathBuiltinKind builtinKind)
    {
        return builtinKind is
            SystemMathBuiltinKind.Sqrt
            or SystemMathBuiltinKind.FusedMultiplyAdd
            or SystemMathBuiltinKind.ReciprocalEstimate
            or SystemMathBuiltinKind.ReciprocalSqrtEstimate
            or SystemMathBuiltinKind.Ceiling
            or SystemMathBuiltinKind.Floor
            or SystemMathBuiltinKind.Truncate
            or SystemMathBuiltinKind.Round;
    }

    private static int GetSystemBitOperationsSurfaceArity(SystemBitOperationsBuiltinKind builtinKind)
    {
        return builtinKind switch
        {
            SystemBitOperationsBuiltinKind.RotateLeft or SystemBitOperationsBuiltinKind.RotateRight => 2,
            _ => 1
        };
    }

    private static bool IsSystemMemoryNamedType(StarkTypeSymbol type, string localName)
    {
        if (type.Kind != StarkTypeKind.Named)
        {
            return false;
        }

        var name = type.NamedType ?? type.DisplayName;
        return string.Equals(name, localName, StringComparison.Ordinal)
            || name.EndsWith($".{localName}", StringComparison.Ordinal)
            || string.Equals(type.DisplayName, localName, StringComparison.Ordinal)
            || type.DisplayName.EndsWith($".{localName}", StringComparison.Ordinal);
    }

    private static bool IsSystemRuntimeNamedType(StarkTypeSymbol type, string localName)
    {
        if (type.Kind != StarkTypeKind.Named)
        {
            return false;
        }

        var name = type.NamedType ?? type.DisplayName;
        return string.Equals(name, localName, StringComparison.Ordinal)
            || name.EndsWith($".{localName}", StringComparison.Ordinal)
            || string.Equals(type.DisplayName, localName, StringComparison.Ordinal)
            || type.DisplayName.EndsWith($".{localName}", StringComparison.Ordinal);
    }

    private static bool IsAllocatorSizeInteger(StarkTypeSymbol type)
    {
        return type.Kind == StarkTypeKind.Integer && type.BitWidth == 64;
    }

    private NamedTypeSymbol? ResolveNamedTypeSymbol(StarkTypeSymbol type)
    {
        return LlvmAggregateEmissionSupport.ResolveNamedTypeSymbol(type, _namedTypes);
    }

    private string MapValidationLlvmType(StarkTypeSymbol type)
    {
        if (StarkTypeSymbols.IsPointerBackedBorrowType(type))
        {
            return "ptr";
        }

        return NormalizeType(type).Kind switch
        {
            StarkTypeKind.Void => "void",
            StarkTypeKind.Bool => "i1",
            StarkTypeKind.Integer => $"i{type.BitWidth}",
            StarkTypeKind.Float when type.BitWidth == 16 => "half",
            StarkTypeKind.Float when type.BitWidth == 32 => "float",
            StarkTypeKind.Float when type.BitWidth == 64 => "double",
            StarkTypeKind.Float when type.BitWidth == 80 => "x86_fp80",
            StarkTypeKind.Float when type.BitWidth == 128 => "fp128",
            StarkTypeKind.RawPointer or StarkTypeKind.FunctionPointer or StarkTypeKind.Null => "ptr",
            StarkTypeKind.Closure => type.ClosureStorageKind == StarkClosureStorageKind.Heap
                ? "{ ptr, ptr, ptr }"
                : "{ ptr, ptr }",
            StarkTypeKind.DynTrait => "{ ptr, ptr }",
            StarkTypeKind.FixedArray when type.ElementType is not null && type.FixedLength is int fixedLength => $"[{fixedLength} x {MapValidationLlvmType(type.ElementType)}]",
            StarkTypeKind.Slice => "{ ptr, i64 }",
            StarkTypeKind.Dynamic => "{ ptr, i64, i64 }",
            StarkTypeKind.Ascii => "%ascii",
            StarkTypeKind.Unicode => "%unicode",
            StarkTypeKind.Named when type.NamedType is not null
                                     && _namedTypes.TryGetValue(type.NamedType, out var namedType)
                                     && (namedType.Kind is DeclarationKind.Struct or DeclarationKind.Record
                                         || (namedType.Kind == DeclarationKind.Enum && _enumLayouts.ContainsKey(namedType.Name)))
                => $"%{type.NamedType}",
            StarkTypeKind.Named when ResolveNamedTypeSymbol(type) is { } resolvedNamedType
                                     && LlvmAggregateEmissionSupport.TryGetScalarizableNamedAggregateFields(resolvedNamedType, _enumLayouts, out var orderedFields)
                => $"{{ {string.Join(", ", orderedFields.Select(field => MapValidationLlvmType(field.Type)))} }}",
            StarkTypeKind.Named => "ptr",
            _ => "ptr"
        };
    }

    private static string DescribeAsmArchitecture(StarkAsmArchitecture architecture)
    {
        return architecture switch
        {
            StarkAsmArchitecture.X86_64 => "x86_64",
            StarkAsmArchitecture.AArch64 => "aarch64",
            StarkAsmArchitecture.RiscV64 => "riscv64",
            StarkAsmArchitecture.X86 => "x86",
            StarkAsmArchitecture.Arm32 => "arm",
            _ => "unknown"
        };
    }

    private enum SystemMathBuiltinKind
    {
        Sin,
        Cos,
        Tan,
        Exp,
        Exp2,
        Log,
        Log2,
        Log10,
        Asin,
        Acos,
        Atan,
        Atan2,
        Pow,
        Sinh,
        Cosh,
        Tanh,
        SinCos,
        Sqrt,
        FusedMultiplyAdd,
        ReciprocalEstimate,
        ReciprocalSqrtEstimate,
        Ceiling,
        Floor,
        Truncate,
        Round,
        Min,
        Max
    }

    private enum SystemBitOperationsBuiltinKind
    {
        LeadingZeroCount,
        TrailingZeroCount,
        PopCount,
        RotateLeft,
        RotateRight
    }

    private enum SystemMemoryBuiltinKind
    {
        Allocate,
        Reallocate,
        Free
    }

    private enum SystemRuntimeBuiltinKind
    {
        GetByteSliceParts,
        GetMutableByteSliceParts
    }

    private enum SystemCollectionsBuiltinKind
    {
        ListAsSlice,
        ListAsMutableSlice,
        DictionaryKeyEquals,
        DictionaryKeyHash
    }

    private void Report(SsaFunction function, SourceLocation? location, string message)
    {
        _context.Diagnostics.Error(
            "STK5002",
            $"SSA validation failed in function '{function.Name}': {message}",
            "validate-ssa",
            location ?? function.Location ?? SourceLocation.Synthetic(_context.Input.FilePath));
    }

    private void ReportBuiltin(TypedFunctionSignature function, string message)
    {
        _context.Diagnostics.Error(
            "STK5002",
            $"SSA validation failed for builtin function '{function.Name}': {message}",
            "validate-ssa",
            SourceLocation.Synthetic(_context.Input.FilePath));
    }
}
