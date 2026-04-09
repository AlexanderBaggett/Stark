using Stark.Compiler;

namespace compiler.Tests;

public sealed class TypeCheckingTests
{
    [Fact]
    public void IntegerExponentiationTypeChecks()
    {
        var result = Compile(
            """
            module Demo

            finite law i32 Run() {
                return 2 ** 3;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void BitwiseXorRequiresIntegerOperands()
    {
        var result = Compile(
            """
            module Demo

            finite law f32 Run() {
                return 1.0 ^ 2.0;
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("integer operands", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitArithmeticOperatorsTypeCheckWithoutPlaceholderDiagnostics()
    {
        var result = Compile(
            """
            module Demo

            finite law i32 Run(i32 left, i32 right) {
                stack mut i32 value = left;
                value +%= right;
                stack i32 product = left *| right;
                return -%value +% product +| 3;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "STK3008");
    }

    [Fact]
    public void StrictFpModifierTypeChecksNowThatLoweringExists()
    {
        var result = Compile(
            """
            module Demo

            strictfp finite law f32 Run(f32 left, f32 right) {
                return left + right;
            }
            """);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "STK3008");
    }

    [Fact]
    public void ExplicitConversionsPointerOperatorsAndSliceViewsTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            finite law i32 Run(i64 bits, ascii text) {
                stack mut i32 value = 7;
                stack rawmutptr<i32> ptr = &value;
                stack rawptr<i32> readonlyPtr = (rawptr<i32>)ptr;
                *ptr = (i32)bits;
                stack i64 address = (i64)ptr;
                stack rawmutptr<i32> roundTrip = (rawmutptr<i32>)address;
                stack i32[2] values = { 1, 2 };
                stack i32[] view = (i32[])values;
                return *roundTrip + view[0];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void TextEscapeLiteralsPreferUtf8BackedAsciiUnlessExplicitlyConverted()
    {
        var result = Compile(
            """
            module Demo

            finite law ascii AsciiString() {
                return "\0\b\t\n\f\r\\\"\'";
            }

            finite law ascii AsciiChar() {
                return '\x41';
            }

            finite law unicode UnicodeString() {
                return (unicode)"\xC9";
            }

            finite law unicode UnicodeChar() {
                return (unicode)'\u03B1';
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ExplicitLiteralTextConversionsTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            finite law unicode Widen() {
                return (unicode)"Hello";
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void FixedArrayLengthsAcceptConstantArithmeticExpressions()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32[1 + 2] values) {
                return values[2];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void FixedArrayInitializersCanOmitTrailingElements()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run() {
                stack i32[3] values = { 1, 2 };
                return values[0] + values[1] + values[2];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ExplicitNonAsciiLiteralToAsciiConversionTypeChecks()
    {
        var result = Compile(
            """
            module Demo

            finite law ascii Run() {
                return (ascii)"\u03B1";
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void FrozenReachableViewsTypeCheckAsReadonlyAliases()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            struct PtrBox {
                rawmutptr<i32> Ptr;
            }

            finite law void Run(frozen Box box, frozen PtrBox ptrBox) {
                stack rawptr<frozen i32> valuePtr = &box.Value;
                stack rawptr<frozen i32> readonlyPtr = ptrBox.Ptr;
                stack bool same = *valuePtr == *readonlyPtr;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void AggregateSwitchPatternsTypeCheckOnScalarFields()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32 Left, i32 Right) { }

            finite law i32 Run(Pair value) {
                switch (value) {
                    case Pair(1, var right):
                        return right;
                    case Pair(_, _):
                        return 0;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void GuardedSwitchLabelsDoNotContributeToReachabilityCoverage()
    {
        var result = Compile(
            """
            module Demo

            finite law i32 Run(bool value, bool allow) {
                switch (value) {
                    case true when allow:
                        return 1;
                    case true:
                        return 2;
                    default:
                        return 0;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "STK3019");
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void NestedAggregateSwitchPatternsTypeCheckOnScalarLeaves()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32 Left, i32 Right) { }
            record Outer(Pair Values, i32 Tail) { }

            finite law i32 Run(Outer value) {
                switch (value) {
                    case Outer(Pair(1, var right), var tail):
                        return right + tail;
                    default:
                        return 0;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void EnumSwitchPatternsTypeCheckOnCasePayloadCaptures()
    {
        var result = Compile(
            """
            module Demo

            enum Token {
                End,
                Integer(i32),
                Move { X: i32, Y: i32 },
            }

            finite law i32 Run(Token token) {
                switch (token) {
                    case Token.End:
                        return 0;
                    case Token.Integer(var value):
                        return value;
                    case Token.Move { X: var x, Y: var y }:
                        return x + y;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    // ---- Generic type instantiation ----

    [Fact]
    public void GenericEnumInstantiationTypeChecks()
    {
        var result = Compile(
            """
            module Demo

            enum Option<T> {
                None,
                Some(T),
            }

            finite law bool HasValue(Option<i32> opt) {
                switch (opt) {
                    case Option<i32>.None:
                        return false;
                    case Option<i32>.Some(var value):
                        return value > 0;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Option"), "generic template should be registered");
        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Option<i32>"), "monomorphized type should be registered");
        var monomorphized = typeCheckModel.NamedTypes["Option<i32>"];
        Assert.Equal(DeclarationKind.Enum, monomorphized.Kind);
        Assert.Equal(2, monomorphized.Variants.Count);
        Assert.True(typeCheckModel.NamedTypes["Option"].IsGeneric, "template should be marked generic");
    }

    [Fact]
    public void GenericRecordInstantiationTypeChecks()
    {
        var result = Compile(
            """
            module Demo

            record Pair<A, B>(A First, B Second) { }

            finite law i32 Sum(Pair<i32, i32> pair) {
                return pair.First + pair.Second;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Pair<i32,i32>"), "monomorphized pair should be registered");
        var concrete = typeCheckModel.NamedTypes["Pair<i32,i32>"];
        Assert.Equal(2, concrete.OrderedFields.Count);
        Assert.Equal(StarkTypeKind.Integer, concrete.OrderedFields[0].Type.Kind);
        Assert.Equal(StarkTypeKind.Integer, concrete.OrderedFields[1].Type.Kind);
    }

    [Fact]
    public void GenericRecordPrimaryConstructorInstantiationTypeChecks()
    {
        var result = Compile(
            """
            module Demo

            record Pair<A, B>(A First, B Second) { }

            finite law i32 Sum() {
                stack Pair<i32, i32> pair = new Pair<i32, i32>(3, 4);
                return pair.First + pair.Second;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
    }

    [Fact]
    public void GenericTypeUsesRecordConcreteInstantiationTriggers()
    {
        var result = Compile(
            """
            module Demo

            record Pair<A, B>(A First, B Second) { }

            fn bool Accept(Pair<i32, bool> pair) {
                return pair.Second;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var trigger = Assert.Single(typeCheckModel.TypeTriggers);
        Assert.Equal("Pair<i32,bool>", trigger.TypeName);
        Assert.Equal(["i32", "bool"], trigger.TypeArguments.Select(static type => type.DisplayName));
    }

    [Fact]
    public void NestedGenericTypesInsideContainersMonomorphizeAndRecordTriggers()
    {
        var result = Compile(
            """
            module Demo

            record Pair<A, B>(A First, B Second) { }

            fn i32 Read(rawptr<Pair<i32, bool>> ptr) {
                if ((*ptr).Second) {
                    return (*ptr).First;
                }

                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Pair<i32,bool>"));
        Assert.Contains(typeCheckModel.TypeTriggers, static trigger => trigger.TypeName == "Pair<i32,bool>");
    }

    [Fact]
    public void GenericFunctionBodiesCanUseTheirTypeParametersInLocalTypes()
    {
        var result = Compile(
            """
            module Demo

            fn T Identity<T>(T value) {
                stack T copy = value;
                return copy;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.Functions.TryGetValue("Identity", out var signature));
        Assert.True(signature.IsGeneric);
        Assert.Equal(["T"], signature.GenericParams);
        Assert.Equal("T", signature.ReturnType.DisplayName);
        Assert.Equal("T", Assert.Single(signature.Parameters).Type.DisplayName);
    }

    [Fact]
    public void GenericMethodBodiesCanUseTheirTypeParametersInLocalTypes()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                fn T Echo<T>(T value) {
                    stack T copy = value;
                    return copy;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.Functions.TryGetValue("Box.Echo", out var signature));
        Assert.True(signature.IsGeneric);
        Assert.Equal(["T"], signature.GenericParams);
        Assert.Equal("T", signature.ReturnType.DisplayName);
        Assert.Equal("T", Assert.Single(signature.Parameters).Type.DisplayName);
    }

    [Fact]
    public void GenericFunctionCallsRecordConcreteInstantiationTriggers()
    {
        var result = Compile(
            """
            module Demo

            fn T Identity<T>(T value) {
                return value;
            }

            fn i32 Run() {
                stack i32 value = 42;
                return Identity(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var trigger = Assert.Single(typeCheckModel.InstantiationTriggers);
        Assert.Equal("Identity", trigger.FunctionName);
        Assert.Equal(["i32"], trigger.TypeArguments.Select(static type => type.DisplayName));
        Assert.True(trigger.Signature.IsGenericInstantiation);
        Assert.Equal("i32", trigger.Signature.ReturnType.DisplayName);
        Assert.Equal("i32", Assert.Single(trigger.Signature.Parameters).Type.DisplayName);
    }

    [Fact]
    public void GenericMethodCallsRecordConcreteInstantiationTriggers()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                fn T Echo<T>(borrow Box self, T value) {
                    return value;
                }
            }

            fn i32 Run() {
                stack Box box = new Box();
                stack i32 value = 42;
                return box.Echo(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var trigger = Assert.Single(typeCheckModel.InstantiationTriggers);
        Assert.Equal("Box.Echo", trigger.FunctionName);
        Assert.Equal(["i32"], trigger.TypeArguments.Select(static type => type.DisplayName));
        Assert.True(trigger.Signature.IsGenericInstantiation);
        Assert.Equal("i32", trigger.Signature.ReturnType.DisplayName);
        Assert.Equal(2, trigger.Signature.Parameters.Count);
        Assert.Equal("borrow Box", trigger.Signature.Parameters[0].Type.DisplayName);
        Assert.Equal("i32", trigger.Signature.Parameters[1].Type.DisplayName);
    }

    [Fact]
    public void RepeatedGenericFunctionCallsReuseOneCachedInstantiationTrigger()
    {
        var result = Compile(
            """
            module Demo

            fn T Identity<T>(T value) {
                return value;
            }

            fn i32 Run(i32 left, i32 right) {
                return Identity(left) + Identity(right);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var trigger = Assert.Single(typeCheckModel.InstantiationTriggers);
        Assert.Equal("Identity", trigger.FunctionName);
        Assert.Equal(["i32"], trigger.TypeArguments.Select(static type => type.DisplayName));
    }

    [Fact]
    public void RepeatedGenericTypeUsesReuseOneCachedInstantiationTrigger()
    {
        var result = Compile(
            """
            module Demo

            record Pair<T>(T Value) { }

            fn i32 Add(Pair<i32> left, Pair<i32> right) {
                return left.Value + right.Value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var trigger = Assert.Single(typeCheckModel.TypeTriggers);
        Assert.Equal("Pair<i32>", trigger.TypeName);
        Assert.Equal(["i32"], trigger.TypeArguments.Select(static type => type.DisplayName));
    }

    [Fact]
    public void ConcreteOverloadsBeatMatchingGenericInstantiationTriggers()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Parse(i32 value) {
                return value;
            }

            fn T Parse<T>(T value) {
                return value;
            }

            fn i32 Run() {
                stack i32 value = 42;
                return Parse(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Empty(typeCheckModel.InstantiationTriggers);
    }

    [Fact]
    public void TypeAliasesResolveToTheirUnderlyingTypes()
    {
        var result = Compile(
            """
            module Demo

            alias Byte = i8;

            fn Byte Inc(Byte value) {
                return value + 1;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.TypeAliases.ContainsKey("Byte"));
        Assert.True(typeCheckModel.Functions.TryGetValue("Inc", out var signature));
        Assert.Equal("i8", signature.ReturnType.DisplayName);
        Assert.Equal("i8", Assert.Single(signature.Parameters).Type.DisplayName);
    }

    [Fact]
    public void GenericTypeAliasesSubstituteIntoTheirUnderlyingTypes()
    {
        var result = Compile(
            """
            module Demo

            alias Ptr<T> = rawptr<T>;

            fn i32 Read(Ptr<i32> value) {
                return *value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.TypeAliases.ContainsKey("Ptr"));
        var parameter = Assert.Single(typeCheckModel.Functions["Read"].Parameters).Type;
        Assert.Equal(StarkTypeKind.RawPointer, parameter.Kind);
        Assert.NotNull(parameter.ElementType);
        Assert.Equal("i32", parameter.ElementType!.DisplayName);
    }

    [Fact]
    public void GenericTypeWithWrongArgCountIsAnError()
    {
        var result = Compile(
            """
            module Demo

            enum Option<T> {
                None,
                Some(T),
            }

            finite law void Bad(Option<i32, bool> opt) {
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static d => d.Code == "STK3019");
    }

    [Fact]
    public void GenericEnumVariantFieldTypeIsSubstituted()
    {
        var result = Compile(
            """
            module Demo

            enum Result<T, E> {
                Ok(T),
                Err(E),
            }

            finite law i32 Unwrap(Result<i32, bool> res) {
                switch (res) {
                    case Result<i32, bool>.Ok(var value):
                        return value;
                    case Result<i32, bool>.Err(var err):
                        if (err) {
                            return -1;
                        }
                        return -1;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Result<i32,bool>"));
        var concrete = typeCheckModel.NamedTypes["Result<i32,bool>"];
        var okVariant = concrete.Variants.Single(static v => v.Name == "Ok");
        Assert.Equal(StarkTypeKind.Integer, okVariant.Fields[0].Type.Kind);
        var errVariant = concrete.Variants.Single(static v => v.Name == "Err");
        Assert.Equal(StarkTypeKind.Bool, errVariant.Fields[0].Type.Kind);
    }

    [Fact]
    public void NonGenericTypeWithTypeArgumentsIsAnError()
    {
        var result = Compile(
            """
            module Demo

            record Point(i32 X, i32 Y) { }

            finite law void Bad(Point<i32> p) {
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static d => d.Code == "STK3019");
    }

    [Fact]
    public void TopLevelOverloadGroupsRegisterDistinctFunctionsAndResolveCalls()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Parse(i32 value) {
                return value;
            }

            fn bool Parse(bool value) {
                return value;
            }

            fn i32 Run() {
                return Parse(42);
            }

            fn bool RunBool() {
                return Parse(true);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.Overloads.TryGetValue("Parse", out var overloads));
        Assert.Equal(2, overloads.Count);
        Assert.Equal(
            2,
            typeCheckModel.Functions.Keys.Count(static name => name.StartsWith("Parse#(", StringComparison.Ordinal)));
    }

    [Fact]
    public void MethodOverloadGroupsRegisterDistinctFunctionsAndResolveCalls()
    {
        var result = Compile(
            """
            module Demo

            struct Counter {
                i32 Value;

                fn i32 Scale(borrow Counter self, i32 factor) {
                    return self.Value * factor;
                }

                fn i32 Scale(borrow Counter self, bool doubleIt) {
                    if (doubleIt) {
                        return self.Value * 2;
                    }

                    return self.Value;
                }
            }

            fn i32 Run() {
                stack Counter counter = new Counter() { Value = 3 };
                return counter.Scale(4);
            }

            fn i32 RunBool() {
                stack Counter counter = new Counter() { Value = 3 };
                return counter.Scale(true);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.Overloads.TryGetValue("Counter.Scale", out var overloads));
        Assert.Equal(2, overloads.Count);
        Assert.Equal(
            2,
            typeCheckModel.Functions.Keys.Count(static name => name.StartsWith("Counter.Scale#(", StringComparison.Ordinal)));
    }

    private static CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source), options);
    }
}
