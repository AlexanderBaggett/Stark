using System.Numerics;
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

            finite law i32[min max] Run()
            {
                return 2 ** 3;
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void FunctionItemsPromoteToExplicitFunctionPointersAndIndirectCallsTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Add(i32[min max] left, i32[min max] right)
            {
                return left + right;
            }

            fn i32[min max] Run()
            {
                stack fnptr<fn i32[min max](i32[min max], i32[min max])> op = Add;
                return op(40, 2);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        var promotion = Assert.Single(typeCheckModel.FunctionPointerPromotions);
        Assert.Equal("Add", promotion.Signature.Name);
        Assert.Equal(StarkTypeKind.FunctionPointer, promotion.TargetType.Kind);
        var addressTaken = Assert.Single(typeCheckModel.AddressTakenFunctions);
        Assert.Equal("Add", addressTaken.Signature.Name);
        Assert.Equal(StarkFunctionKind.Fn, addressTaken.Signature.Kind);
        var indirectCall = Assert.Single(typeCheckModel.IndirectCalls);
        Assert.Equal(StarkTypeKind.FunctionPointer, indirectCall.FunctionPointerType.Kind);
    }

    [Fact]
    public void CompileTimeStructuralFactsCannotPromoteToRuntimeCallableValues()
    {
        var pointerResult = Compile(
            """
            module Demo

            finite law void Run()
            {
                stack fnptr<finite law bool()> fact = System.Compiler.IsInteger<i32[min max]>;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(pointerResult.Succeeded);
        Assert.Contains(
            pointerResult.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3054"
                && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));

        var closureResult = Compile(
            """
            module Demo

            finite law void Run()
            {
                stack closure<finite law bool()> fact = System.Compiler.IsInteger<i32[min max]>;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(closureResult.Succeeded);
        Assert.Contains(
            closureResult.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3054"
                && diagnostic.Message.Contains("may only be used inside a `comptime` expression or block", StringComparison.Ordinal));
    }

    [Fact]
    public void FunctionPointerAbiIsPartOfTypeIdentity()
    {
        var result = Compile(
            """
            module Demo

            unsafe ffi(c) fn i32[min max] CRead();
            unsafe ffi(win64) fn i32[min max] WinRead();

            unsafe fn void Run()
            {
                stack mut fnptr<unsafe ffi(c) fn i32[min max]()> cReader = CRead;
                stack fnptr<unsafe ffi(win64) fn i32[min max]()> winReader = WinRead;
                cReader = winReader;
                return;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                TargetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null)));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("fnptr<unsafe ffi(c) fn", StringComparison.Ordinal)
                && diagnostic.Message.Contains("fnptr<unsafe ffi(win64) fn", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitFfiFunctionPointerAbiPromotesMatchingForeignFunctionItems()
    {
        var result = Compile(
            """
            module Demo

            unsafe ffi(win64) fn i32[min max] Read(i32[min max] value);

            unsafe fn i32[min max] Run(i32[min max] value)
            {
                stack fnptr<unsafe ffi(win64) fn i32[min max](i32[min max])> reader = Read;
                return reader(value);
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                TargetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null)));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        var promotion = Assert.Single(typeCheckModel.FunctionPointerPromotions);
        Assert.Equal("Read", promotion.Signature.Name);
        Assert.Equal(StarkFfiAbi.Win64, promotion.TargetType.FunctionPointerAbi);
        Assert.True(promotion.TargetType.FunctionPointerIsUnsafe);
        var indirectCall = Assert.Single(typeCheckModel.IndirectCalls);
        Assert.Equal(StarkFfiAbi.Win64, indirectCall.FunctionPointerType.FunctionPointerAbi);
        Assert.True(indirectCall.FunctionPointerType.FunctionPointerIsUnsafe);
    }

    [Fact]
    public void StructLayoutAttributesProduceConcreteFieldLayoutFacts()
    {
        var result = Compile(
            """
            module Demo

            [StructLayout(C), Pack(1), Align(4)]
            struct Packet
            {
                u8[0 max] Tag;
                u32[0 max] Length;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var packet = typeCheckModel.NamedTypes["Packet"];
        Assert.Equal(StructLayoutKind.C, packet.Layout?.Kind);
        Assert.Equal(1, packet.Layout?.PackBytes);
        Assert.Equal(4, packet.Layout?.AlignBytes);

        var layout = ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(
            StarkTypeSymbols.Named("Packet"),
            typeCheckModel.NamedTypes);
        Assert.NotNull(layout);
        Assert.Equal(8, layout.SizeBytes);
        Assert.Equal(4, layout.AlignmentBytes);

        var tag = Assert.Single(layout.Fields, static field => field.Name == "Tag");
        Assert.Equal(0, tag.OffsetBytes);
        Assert.Equal(1, tag.EffectiveAlignmentBytes);
        Assert.False(tag.IsMisaligned);

        var length = Assert.Single(layout.Fields, static field => field.Name == "Length");
        Assert.Equal(1, length.OffsetBytes);
        Assert.Equal(4, length.NaturalAlignmentBytes);
        Assert.Equal(1, length.EffectiveAlignmentBytes);
        Assert.True(length.IsMisaligned);
    }

    [Theory]
    [InlineData("x86_64-unknown-linux-gnu", 64, 64, 64, true)]
    [InlineData("x86_64-pc-windows-msvc", 32, 64, 64, true)]
    [InlineData("i686-unknown-linux-gnu", 32, 32, 32, true)]
    [InlineData("aarch64-unknown-linux-gnu", 64, 64, 64, false)]
    public void SystemCPrimitiveAliasesResolveForTargetDataModel(
        string targetTriple,
        int expectedLongWidth,
        int expectedSizeTWidth,
        int expectedPtrDiffWidth,
        bool expectedPlainCharSigned)
    {
        var result = Compile(
            """
            module Demo

            unsafe ffi(c) fn System.C.c_int Read(
                System.C.c_long value,
                System.C.c_ulong flags,
                System.C.c_size_t bytes,
                System.C.c_ptrdiff_t delta,
                rawptr<System.C.c_char> text);
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                TargetInfo: new LlvmTargetInfo(targetTriple, null)));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var read = typeCheckModel.Functions["Read"];
        AssertFullIntegerRange(read.ReturnType, 32, isUnsigned: false);
        Assert.Equal("System.C.c_int", read.ReturnType.CSourceAliasName);
        AssertFullIntegerRange(read.Parameters[0].Type, expectedLongWidth, isUnsigned: false);
        Assert.Equal("System.C.c_long", read.Parameters[0].Type.CSourceAliasName);
        AssertFullIntegerRange(read.Parameters[1].Type, expectedLongWidth, isUnsigned: true);
        Assert.Equal("System.C.c_ulong", read.Parameters[1].Type.CSourceAliasName);
        AssertFullIntegerRange(read.Parameters[2].Type, expectedSizeTWidth, isUnsigned: true);
        Assert.Equal("System.C.c_size_t", read.Parameters[2].Type.CSourceAliasName);
        AssertFullIntegerRange(read.Parameters[3].Type, expectedPtrDiffWidth, isUnsigned: false);
        Assert.Equal("System.C.c_ptrdiff_t", read.Parameters[3].Type.CSourceAliasName);

        var text = read.Parameters[4].Type;
        Assert.Equal(StarkTypeKind.RawPointer, text.Kind);
        Assert.False(text.IsMutablePointer);
        Assert.NotNull(text.ElementType);
        AssertFullIntegerRange(text.ElementType!, 8, isUnsigned: !expectedPlainCharSigned);
        Assert.Equal("System.C.c_char", text.ElementType!.CSourceAliasName);
    }

    [Theory]
    [InlineData("x86_64-unknown-linux-gnu", 24, 8, 8, 16)]
    [InlineData("x86_64-pc-windows-msvc", 16, 8, 4, 8)]
    [InlineData("i686-unknown-linux-gnu", 12, 4, 4, 8)]
    public void SystemCAliasesParticipateInCStructLayout(
        string targetTriple,
        int expectedSize,
        int expectedAlignment,
        int expectedCountOffset,
        int expectedBytesOffset)
    {
        var result = Compile(
            """
            module Demo

            [StructLayout(C)]
            struct NativeRecord
            {
                System.C.c_char Tag;
                System.C.c_long Count;
                System.C.c_size_t Bytes;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                TargetInfo: new LlvmTargetInfo(targetTriple, null)));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var layout = ConcreteTypeLayoutHelper.TryGetConcreteTypeLayout(
            StarkTypeSymbols.Named("NativeRecord"),
            typeCheckModel.NamedTypes);
        Assert.NotNull(layout);
        Assert.Equal(expectedSize, layout.SizeBytes);
        Assert.Equal(expectedAlignment, layout.AlignmentBytes);
        Assert.Equal(0, Assert.Single(layout.Fields, static field => field.Name == "Tag").OffsetBytes);
        Assert.Equal(expectedCountOffset, Assert.Single(layout.Fields, static field => field.Name == "Count").OffsetBytes);
        Assert.Equal(expectedBytesOffset, Assert.Single(layout.Fields, static field => field.Name == "Bytes").OffsetBytes);
    }

    [Fact]
    public void SystemCCVoidIsValidOnlyBehindRawPointers()
    {
        var good = Compile(
            """
            module Demo

            unsafe ffi(c) fn rawmutptr<System.C.c_void> Allocate(System.C.c_size_t bytes);
            unsafe ffi(c) fn void Free(rawmutptr<System.C.c_void> ptr);

            unsafe fn rawptr<System.C.c_void> Identity(rawptr<System.C.c_void> ptr)
            {
                return ptr;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(good.Succeeded, string.Join(", ", good.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(good.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        AssertRawPointerToCVoid(typeCheckModel.Functions["Allocate"].ReturnType, isMutable: true);
        AssertRawPointerToCVoid(typeCheckModel.Functions["Free"].Parameters[0].Type, isMutable: true);
        AssertRawPointerToCVoid(typeCheckModel.Functions["Identity"].ReturnType, isMutable: false);
        AssertRawPointerToCVoid(typeCheckModel.Functions["Identity"].Parameters[0].Type, isMutable: false);

        var bad = Compile(
            """
            module Demo

            unsafe ffi(c) fn System.C.c_void BadReturn();

            struct BadField
            {
                System.C.c_void Value;
            }

            unsafe fn void BadLocal()
            {
                stack System.C.c_void value;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(bad.Succeeded);
        Assert.Contains(
            bad.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3050"
                && diagnostic.Message.Contains("System.C.c_void", StringComparison.Ordinal)
                && diagnostic.Message.Contains("rawptr<System.C.c_void>", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("x86_64-unknown-linux-gnu", true)]
    [InlineData("aarch64-unknown-linux-gnu", false)]
    public void SystemCCharSignednessIsCompileTimeTargetFact(string targetTriple, bool expectedSigned)
    {
        var result = Compile(
            """
            module Demo

            const bool PlainCharSigned = System.C.c_char_is_signed;
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                TargetInfo: new LlvmTargetInfo(targetTriple, null)));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var constant = typeCheckModel.Globals["PlainCharSigned"];
        Assert.True(constant.IsConst);
        Assert.Equal(StarkTypeSymbols.Bool, constant.Type);
        Assert.Equal(expectedSigned, constant.ConstantInitializer?.BoolValue);
    }

    [Fact]
    public void StructLayoutDiagnosticsRejectInvalidLayoutShapes()
    {
        var result = Compile(
            """
            module Demo

            [StructLayout(C), Pack(3)]
            struct BadPack
            {
                u32[0 max] Value;
            }

            [StructLayout(C)]
            struct BadField
            {
                ascii Text;
            }

            [StructLayout(Explicit)]
            struct MissingOffset
            {
                u32[0 max] Value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3048"
                && diagnostic.Message.Contains("[Pack(N)] requires a positive power-of-two integer literal", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3049"
                && diagnostic.Message.Contains("not FFI-layout safe", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3048"
                && diagnostic.Message.Contains("Every field in [StructLayout(Explicit)]", StringComparison.Ordinal));
    }

    [Fact]
    public void SafeBorrowsRejectMisalignedPackedFields()
    {
        var result = Compile(
            """
            module Demo

            [StructLayout(C), Pack(1)]
            struct Packet
            {
                u8[0 max] Tag;
                u32[0 max] Value;
            }

            fn void Touch(borrow u32[0 max] value)
            {
                return;
            }

            fn void Run(borrow Packet packet)
            {
                Touch(packet.Value);
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3049"
                && diagnostic.Message.Contains("cannot form a safe borrow to member 'Value'", StringComparison.Ordinal));
    }

    [Fact]
    public void FunctionPointerOverlapContractsAllowAliasingIndirectCalls()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn void Touch(borrow mut Box left, borrow mut Box right) where overlap(left, right)
            {
                return;
            }

            fn void Run(borrow mut Box box)
            {
                stack fnptr<fn void(borrow mut Box, borrow mut Box) where overlap(arg0, arg1)> op = Touch;
                op(box, box);
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        var promotion = Assert.Single(typeCheckModel.FunctionPointerPromotions);
        Assert.Equal("Touch", promotion.Signature.Name);
        var indirectCall = Assert.Single(typeCheckModel.IndirectCalls);
        var overlapGroup = Assert.Single(indirectCall.FunctionPointerType.FunctionPointerOverlapParameterGroups ?? []);
        Assert.Equal(["arg0", "arg1"], overlapGroup.ParameterNames);
    }

    [Fact]
    public void FunctionPointerOverlapTargetRejectsDefaultDisjointFunctionItems()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn void Touch(borrow mut Box left, borrow mut Box right)
            {
                return;
            }

            fn void Run()
            {
                stack fnptr<fn void(borrow mut Box, borrow mut Box) where overlap(arg0, arg1)> op = Touch;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("cannot be promoted", StringComparison.Ordinal)
                && diagnostic.Message.Contains("where overlap(arg0, arg1)", StringComparison.Ordinal));
    }

    [Fact]
    public void FunctionPointerDeadOnReturnContractsArePreservedByPromotion()
    {
        var accepted = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn bool Destroy(out Box box) where dead_on_return(box)
            {
                box = new Box()
                {
                    Value = 0
                };
                return true;
            }

            unsafe fn void Run()
            {
                stack fnptr<fn bool(out Box) where dead_on_return(arg0)> op = Destroy;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(accepted.Succeeded, string.Join(", ", accepted.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(accepted.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        var promotion = Assert.Single(typeCheckModel.FunctionPointerPromotions);
        Assert.Equal(["arg0"], promotion.TargetType.FunctionPointerPointeeDeadOnReturnParameterNames);

        var rejected = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn bool Destroy(out Box box) where dead_on_return(box)
            {
                box = new Box()
                {
                    Value = 0
                };
                return true;
            }

            unsafe fn void Run()
            {
                stack fnptr<fn bool(out Box)> op = Destroy;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(rejected.Succeeded);
        Assert.Contains(
            rejected.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("cannot be promoted", StringComparison.Ordinal)
                && diagnostic.Message.Contains("dead_on_return(arg0)", StringComparison.Ordinal));
    }

    [Fact]
    public void FunctionPointerSameTargetsAcceptOverlapFunctionsAndRequireSameArguments()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn void Touch(borrow mut Box left, borrow mut Box right) where overlap(left, right)
            {
                return;
            }

            fn void Run(borrow mut Box box)
            {
                stack fnptr<fn void(borrow mut Box, borrow mut Box) where same(arg0, arg1)> op = Touch;
                op(box, box);
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        var indirectCall = Assert.Single(typeCheckModel.IndirectCalls);
        var sameGroup = Assert.Single(indirectCall.FunctionPointerType.FunctionPointerSameParameterGroups ?? []);
        Assert.Equal(["arg0", "arg1"], sameGroup.ParameterNames);
    }

    [Fact]
    public void ClosureTypesResolveInFunctionSignatures()
    {
        var result = Compile(
            """
            module Demo

            struct Ui
            {
                i32[min max] Frame;
            }

            struct Packet
            {
                i32[min max] Code;
            }

            fn void RegisterInline(inline closure<fn void(borrow mut Ui)> body);
            fn void RegisterBorrow(borrow closure<fn void(borrow mut Ui)> body);
            fn void RegisterMut(mut borrow closure<mut fn void(i32[min max])> sink);
            fn void RegisterOnce(inline closure<once finite Packet()> producer);
            fn void RegisterOverlap(borrow closure<fn void(borrow mut Ui, borrow mut Ui) where overlap(arg0, arg1)> body);
            unsafe fn void RegisterBounded(borrow closure<finite void(rawptr<i32[min max]>[arg1], u8[1 10])> body);
            fn heap closure<fn i32[min max](i32[min max])> Factory(heap closure<fn i32[min max](i32[min max])> seed);
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var inline = Assert.Single(typeCheckModel.Functions["RegisterInline"].Parameters);
        Assert.Equal(StarkTypeKind.Closure, inline.Type.Kind);
        Assert.Equal(StarkClosureStorageKind.Inline, inline.Type.ClosureStorageKind);
        Assert.Equal(StarkClosureCallCapability.None, inline.Type.ClosureCallCapability);
        Assert.Equal(StarkFunctionKind.Fn, inline.Type.ClosureFunctionKind);

        var borrowed = Assert.Single(typeCheckModel.Functions["RegisterBorrow"].Parameters);
        Assert.Equal(StarkTypeKind.Closure, borrowed.Type.Kind);
        Assert.Equal(StarkBorrowKind.Borrow, borrowed.Type.BorrowKind);
        Assert.Equal(StarkClosureStorageKind.Unspecified, borrowed.Type.ClosureStorageKind);

        var mutableBorrow = Assert.Single(typeCheckModel.Functions["RegisterMut"].Parameters);
        Assert.Equal(StarkBorrowKind.Borrow, mutableBorrow.Type.BorrowKind);
        Assert.True(mutableBorrow.Type.IsMutableView);
        Assert.Equal(StarkClosureCallCapability.Mut, mutableBorrow.Type.ClosureCallCapability);

        var once = Assert.Single(typeCheckModel.Functions["RegisterOnce"].Parameters);
        Assert.Equal(StarkClosureCallCapability.Once, once.Type.ClosureCallCapability);
        Assert.Equal(StarkFunctionKind.Finite, once.Type.ClosureFunctionKind);

        var overlap = Assert.Single(typeCheckModel.Functions["RegisterOverlap"].Parameters);
        var overlapGroup = Assert.Single(overlap.Type.ClosureOverlapParameterGroups ?? []);
        Assert.Equal(["arg0", "arg1"], overlapGroup.ParameterNames);

        var bounded = Assert.Single(typeCheckModel.Functions["RegisterBounded"].Parameters);
        Assert.Equal("arg1", StarkTypeSymbols.GetClosureParameterRawPointerElementCountExpression(bounded.Type, 0));

        var factory = typeCheckModel.Functions["Factory"];
        Assert.Equal(StarkTypeKind.Closure, factory.ReturnType.Kind);
        Assert.Equal(StarkClosureStorageKind.Heap, factory.ReturnType.ClosureStorageKind);
    }

    [Fact]
    public void LawClosureTypesRejectMutableOrOnceCapabilities()
    {
        var result = Compile(
            """
            module Demo

            fn void RegisterMutableLaw(inline closure<mut law void()> body);
            fn void RegisterOnceLaw(heap closure<once finite law void()> body);
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Equal(
            2,
            result.Diagnostics.Count(diagnostic =>
                diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("Law and finite law closure signatures cannot use 'mut' or 'once'", StringComparison.Ordinal)));
    }

    [Fact]
    public void FunctionItemPromotionPreservesFunctionKindFacts()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[min max] Always()
            {
                return 7;
            }

            fn void Run()
            {
                stack fnptr<fn i32[min max]()> plain = Always;
                stack fnptr<finite i32[min max]()> finiteOnly = Always;
                stack fnptr<law i32[min max]()> lawOnly = Always;
                stack fnptr<finite law i32[min max]()> strict = Always;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Equal(4, typeCheckModel.FunctionPointerPromotions.Count);
        Assert.All(typeCheckModel.FunctionPointerPromotions, static promotion => Assert.Equal("Always", promotion.Signature.Name));
        var addressTaken = Assert.Single(typeCheckModel.AddressTakenFunctions);
        Assert.Equal("Always", addressTaken.Signature.Name);
        Assert.Equal(StarkFunctionKind.FiniteLaw, addressTaken.Signature.Kind);
    }

    [Fact]
    public void FunctionItemsPromoteFromEachDeclaredFunctionKind()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Plain()
            {
                return 1;
            }

            finite i32[min max] FiniteOnly()
            {
                return 2;
            }

            law i32[min max] LawOnly()
            {
                return 3;
            }

            finite law i32[min max] Strict()
            {
                return 4;
            }

            fn void Run()
            {
                stack fnptr<fn i32[min max]()> plain = Plain;
                stack fnptr<finite i32[min max]()> finiteOnly = FiniteOnly;
                stack fnptr<law i32[min max]()> lawOnly = LawOnly;
                stack fnptr<finite law i32[min max]()> strict = Strict;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        Assert.Equal(4, typeCheckModel.FunctionPointerPromotions.Count);
        Assert.Equal(4, typeCheckModel.AddressTakenFunctions.Count);
        Assert.Contains(typeCheckModel.AddressTakenFunctions, static addressTaken => addressTaken.Signature.Name == "Plain" && addressTaken.Signature.Kind == StarkFunctionKind.Fn);
        Assert.Contains(typeCheckModel.AddressTakenFunctions, static addressTaken => addressTaken.Signature.Name == "FiniteOnly" && addressTaken.Signature.Kind == StarkFunctionKind.Finite);
        Assert.Contains(typeCheckModel.AddressTakenFunctions, static addressTaken => addressTaken.Signature.Name == "LawOnly" && addressTaken.Signature.Kind == StarkFunctionKind.Law);
        Assert.Contains(typeCheckModel.AddressTakenFunctions, static addressTaken => addressTaken.Signature.Name == "Strict" && addressTaken.Signature.Kind == StarkFunctionKind.FiniteLaw);
    }

    [Fact]
    public void FunctionItemPromotionRejectsUnsatisfiedFunctionKindObligations()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Plain()
            {
                return 1;
            }

            finite i32[min max] FiniteOnly()
            {
                return 2;
            }

            law i32[min max] LawOnly()
            {
                return 3;
            }

            fn void Run()
            {
                stack fnptr<finite i32[min max]()> needsFinite = Plain;
                stack fnptr<law i32[min max]()> needsLaw = Plain;
                stack fnptr<finite law i32[min max]()> needsBothFromFinite = FiniteOnly;
                stack fnptr<finite law i32[min max]()> needsBothFromLaw = LawOnly;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Function item 'Plain' cannot be promoted", StringComparison.Ordinal)
                && diagnostic.Message.Contains("finite", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Function item 'Plain' cannot be promoted", StringComparison.Ordinal)
                && diagnostic.Message.Contains("law", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Function item 'FiniteOnly' cannot be promoted", StringComparison.Ordinal)
                && diagnostic.Message.Contains("finite law", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Function item 'LawOnly' cannot be promoted", StringComparison.Ordinal)
                && diagnostic.Message.Contains("finite law", StringComparison.Ordinal));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Empty(typeCheckModel.AddressTakenFunctions);
    }

    [Fact]
    public void FunctionItemsPromoteInReturnAndArgumentTargetPositions()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Target()
            {
                return 41;
            }

            fn fnptr<fn i32[min max]()> Factory()
            {
                return Target;
            }

            fn i32[min max] Apply(fnptr<fn i32[min max]()> callback)
            {
                return callback() + 1;
            }

            fn i32[min max] Run()
            {
                return Apply(Target);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Equal(2, typeCheckModel.FunctionPointerPromotions.Count);
        Assert.All(typeCheckModel.FunctionPointerPromotions, static promotion => Assert.Equal("Target", promotion.Signature.Name));
        var addressTaken = Assert.Single(typeCheckModel.AddressTakenFunctions);
        Assert.Equal("Target", addressTaken.Signature.Name);
        Assert.Equal(StarkFunctionKind.Fn, addressTaken.Signature.Kind);
        var indirectCall = Assert.Single(typeCheckModel.IndirectCalls);
        Assert.Equal(StarkTypeKind.FunctionPointer, indirectCall.FunctionPointerType.Kind);
    }

    [Fact]
    public void OverloadedFunctionItemPromotionsPreserveDistinctAddressTakenFacts()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Pick()
            {
                return 1;
            }

            fn i32[min max] Pick(i32[min max] value)
            {
                return value;
            }

            fn i32[min max] Run()
            {
                stack fnptr<fn i32[min max]()> first = Pick;
                stack fnptr<fn i32[min max](i32[min max])> second = Pick;
                return first() + second(2);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Equal(2, typeCheckModel.FunctionPointerPromotions.Count);
        Assert.All(typeCheckModel.FunctionPointerPromotions, static promotion => Assert.Equal("Pick", promotion.Signature.DisplaySourceName));
        Assert.Equal(2, typeCheckModel.AddressTakenFunctions.Count);
        Assert.All(typeCheckModel.AddressTakenFunctions, static addressTaken => Assert.Equal("Pick", addressTaken.Signature.DisplaySourceName));
        Assert.Equal(
            2,
            typeCheckModel.AddressTakenFunctions
                .Select(static addressTaken => addressTaken.Signature.Name)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void FunctionPointerPromotionRejectsAbiMismatchedFunctionItems()
    {
        var result = Compile(
            """
            module Demo

            fn i64[min max] Wide(i64[min max] value)
            {
                return value;
            }

            fn void Run()
            {
                stack fnptr<fn i32[min max](i32[min max])> op = Wide;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("cannot be promoted", StringComparison.Ordinal));
    }

    [Fact]
    public void NonCapturingLambdasTypeCheckAsExplicitFunctionPointers()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Apply(fnptr<fn i32[min max](i32[min max])> op)
            {
                return op(41);
            }

            fn i32[min max] Run()
            {
                stack fnptr<fn i32[min max](i32[min max])> increment = (i32[min max] value) => value + 1;
                return Apply((i32[min max] value) => value + 1);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Equal(2, typeCheckModel.Lambdas.Count);
        Assert.All(typeCheckModel.Lambdas, static lambda => Assert.Equal(StarkFunctionKind.Fn, lambda.FunctionPointerType.FunctionPointerKind));
    }

    [Fact]
    public void LambdasTypeCheckAsExplicitClosureTargets()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] ApplyBorrow(borrow closure<fn i32[min max](i32[min max])> op)
            {
                return 0;
            }

            fn void ApplyInline(inline closure<fn void(i32[min max])> op)
            {
                return;
            }

            fn heap closure<fn i32[min max](i32[min max])> MakeHeap(i32[min max] offset)
            {
                return heap capture(copy offset) (i32[min max] value) => value + offset;
            }

            fn i32[min max] Run()
            {
                stack i32[min max] offset = 1;
                ApplyInline((i32[min max] value) =>
                {
                    return;
                });
                return ApplyBorrow(capture(copy offset) (i32[min max] value) => value + offset);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Empty(typeCheckModel.Lambdas);
        Assert.Equal(3, typeCheckModel.ClosureLambdas.Count);
        Assert.Contains(typeCheckModel.ClosureLambdas, static lambda => lambda.ClosureType.ClosureStorageKind == StarkClosureStorageKind.Inline);
        Assert.Contains(typeCheckModel.ClosureLambdas, static lambda => lambda.ClosureType.BorrowKind == StarkBorrowKind.Borrow);
        Assert.Contains(typeCheckModel.ClosureLambdas, static lambda => lambda.ClosureType.ClosureStorageKind == StarkClosureStorageKind.Heap);
        Assert.Contains(typeCheckModel.LambdaCaptures, static capture => capture.Name == "offset" && capture.Mode == "copy");
    }

    [Fact]
    public void FunctionItemsPromoteToExplicitClosureTargets()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] AddOne(i32[min max] value)
            {
                return value + 1;
            }

            fn i32[min max] ApplyBorrow(borrow closure<fn i32[min max](i32[min max])> op, i32[min max] value)
            {
                return op(value);
            }

            inline fn i32[min max] ApplyInline(inline closure<fn i32[min max](i32[min max])> op, i32[min max] value)
            {
                return op(value);
            }

            fn heap closure<fn i32[min max](i32[min max])> Make()
            {
                return AddOne;
            }

            fn i32[min max] Run()
            {
                stack heap closure<fn i32[min max](i32[min max])> op = Make();
                return ApplyBorrow(AddOne, 20) + ApplyInline(AddOne, 20) + op(0);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Equal(3, typeCheckModel.ClosureFunctionPromotions.Count);
        Assert.All(typeCheckModel.ClosureFunctionPromotions, static promotion => Assert.Equal("AddOne", promotion.Signature.Name));
        Assert.Contains(typeCheckModel.ClosureFunctionPromotions, static promotion => promotion.ClosureType.BorrowKind == StarkBorrowKind.Borrow);
        Assert.Contains(typeCheckModel.ClosureFunctionPromotions, static promotion => promotion.ClosureType.ClosureStorageKind == StarkClosureStorageKind.Inline);
        Assert.Contains(typeCheckModel.ClosureFunctionPromotions, static promotion => promotion.ClosureType.ClosureStorageKind == StarkClosureStorageKind.Heap);
    }

    [Fact]
    public void ClosureCallsTypeCheckAndRecordArgumentFacts()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Apply(borrow closure<finite i32[min max](i32[min max])> op, i32[min max] value)
            {
                return op(value);
            }

            fn void Push(mut borrow closure<mut fn void(i32[min max])> sink)
            {
                sink(7);
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "validate-lowering-contract"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Equal(2, typeCheckModel.ClosureCalls.Count);
        Assert.All(typeCheckModel.ClosureCalls, static call => Assert.Single(call.Arguments));
        Assert.Contains(typeCheckModel.ClosureCalls, static call => call.ClosureType.ClosureFunctionKind == StarkFunctionKind.Finite);
        Assert.Contains(typeCheckModel.ClosureCalls, static call => call.ClosureType.ClosureCallCapability == StarkClosureCallCapability.Mut);
    }

    [Fact]
    public void MutableClosureCallsRequireMutableClosureAccess()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(borrow closure<mut fn void()> op)
            {
                op();
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3008"
                && diagnostic.Message.Contains("requires mutable access to the closure value", StringComparison.Ordinal));
    }

    [Fact]
    public void HeapClosureLambdasRequireHeapPrefixAndHeapSafeCaptures()
    {
        var missingPrefix = Compile(
            """
            module Demo

            fn heap closure<fn i32[min max](i32[min max])> Bad(i32[min max] offset)
            {
                return capture(copy offset) (i32[min max] value) => value + offset;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(missingPrefix.Succeeded);
        Assert.Contains(
            missingPrefix.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3008"
                && diagnostic.Message.Contains("must use the explicit 'heap' lambda prefix", StringComparison.Ordinal));

        var borrowedCapture = Compile(
            """
            module Demo

            fn heap closure<fn i32[min max]()> Bad(i32[min max] value)
            {
                return heap capture(read value) () => value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(borrowedCapture.Succeeded);
        Assert.Contains(
            borrowedCapture.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3008"
                && diagnostic.Message.Contains("Heap closure capture mode 'read'", StringComparison.Ordinal));

        var nonEscapingBorrowCapture = Compile(
            """
            module Demo

            fn heap closure<fn i32[min max]()> Bad(borrow closure<fn i32[min max]()> op)
            {
                return heap capture(copy op) () => op();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(nonEscapingBorrowCapture.Succeeded);
        Assert.Contains(
            nonEscapingBorrowCapture.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3008"
                && diagnostic.Message.Contains("non-escaping type 'borrow closure", StringComparison.Ordinal));
    }

    [Fact]
    public void InlineClosureTypesAreOnlyValidAsParameters()
    {
        var result = Compile(
            """
            module Demo

            fn inline closure<fn void()> BadReturn()
            {
                return () =>
                {
                    return;
                };
            }

            fn void BadLocal()
            {
                stack inline closure<fn void()> local = () =>
                {
                    return;
                };
                return;
            }

            struct BadField
            {
                inline closure<fn void()> Callback;
            }

            fn void BadNestedParameter(fnptr<fn void(inline closure<fn void()>)> callback)
            {
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.True(
            result.Diagnostics.Count(static diagnostic => diagnostic.Code == "STK3008"
                && diagnostic.Message.Contains("Inline closure type", StringComparison.Ordinal)
                && diagnostic.Message.Contains("only valid as a function parameter directly", StringComparison.Ordinal)) >= 3,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void NonCapturingLambdasCannotUseOuterLocalsWithoutCaptureList()
    {
        var result = Compile(
            """
            module Demo

            fn void Run()
            {
                stack i32[min max] offset = 1;
                stack fnptr<fn i32[min max](i32[min max])> increment = (i32[min max] value) => value + offset;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3003"
                && diagnostic.Message.Contains("Unknown symbol 'offset'", StringComparison.Ordinal));
    }

    [Fact]
    public void CapturingLambdaSyntaxIsCheckedButNotLoweredYet()
    {
        var result = Compile(
            """
            module Demo

            fn void Run()
            {
                stack i32[min max] offset = 1;
                stack fnptr<fn i32[min max](i32[min max])> increment = capture(copy offset) (i32[min max] value) => value + offset;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            IsFunctionPointerCaptureDiagnostic);
    }

    [Fact]
    public void ExplicitCaptureListDoesNotExposeUnlistedOuterLocals()
    {
        var result = Compile(
            """
            module Demo

            fn void Run()
            {
                stack i32[min max] offset = 1;
                stack i32[min max] secret = 2;
                stack fnptr<fn i32[min max](i32[min max])> increment = capture(copy offset) (i32[min max] value) => value + secret;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3003"
                && diagnostic.Message.Contains("Unknown symbol 'secret'", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitCaptureListsRejectDuplicateCapturedLocals()
    {
        var result = Compile(
            """
            module Demo

            fn void Run()
            {
                stack i32[min max] value = 1;
                stack fnptr<fn i32[min max]()> callback = capture(copy value, read value) () => value;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3006"
                && diagnostic.Message.Contains("Lambda capture 'value' is listed more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void LambdaParametersCannotReuseCapturedNames()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Apply(
                borrow closure<fn i32[min max](i32[min max])> op,
                i32[min max] value)
                {
                    return op(value);
            }

            fn i32[min max] Run()
            {
                stack i32[min max] value = 1;
                return Apply(
                    capture(copy value) (i32[min max] value) => value + 1,
                    41);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3006"
                && diagnostic.Message.Contains("Lambda parameter 'value' reuses a captured name", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitCaptureListsReportUnknownClauseNamesAndModes()
    {
        var result = Compile(
            """
            module Demo

            fn void Run()
            {
                stack i32[min max] value = 1;
                stack fnptr<fn i32[min max]()> wrongClause = captures(copy value) () => value;
                stack fnptr<fn i32[min max]()> wrongMode = capture(clone value) () => value;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3008"
                && diagnostic.Message.Contains("Unknown lambda capture clause 'captures'", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3008"
                && diagnostic.Message.Contains("Unknown lambda capture mode 'clone'", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsafeLambdaCaptureModesRequireUnsafeContext()
    {
        var bad = Compile(
            """
            module Demo

            fn void Run(i32[min max] token)
            {
                stack fnptr<fn i32[min max]()> callback = capture(unsafe addr token) () => 1;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(bad.Succeeded);
        Assert.Contains(
            bad.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("requires an unsafe context", StringComparison.Ordinal));

        var good = Compile(
            """
            module Demo

            fn void Run(i32[min max] token, i32[min max] sharedState)
            {
                unsafe
                {
                    stack fnptr<fn i32[min max]()> callback = capture(unsafe addr token, unsafe shared sharedState) () => 1;
                }

                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(good.Succeeded);
        Assert.DoesNotContain(
            good.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024");
        Assert.Contains(
            good.Diagnostics,
            IsFunctionPointerCaptureDiagnostic);
    }

    [Fact]
    public void UnsafeLambdaCaptureModesRequireExplicitUnsafeMarkerAndRejectSafeModeMarkers()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(
                i32[min max] token,
                i32[min max] sharedState,
                i32[min max] copyValue)
                {
                    unsafe
                {
                    stack fnptr<fn i32[min max]()> byAddress = capture(addr token) () => 1;
                    stack fnptr<fn i32[min max]()> sharedCallback = capture(shared sharedState) () => 2;
                    stack fnptr<fn i32[min max]()> copied = capture(unsafe copy copyValue) () => 3;
                }

                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("Capture mode 'addr' must be written as 'unsafe addr'", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("Capture mode 'shared' must be written as 'unsafe shared'", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("Only 'addr' and 'shared' capture modes may be marked unsafe", StringComparison.Ordinal));
    }

    [Fact]
    public void LambdaCaptureModeFactsArePreservedInTypeCheckModel()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(
                i32[min max] copyValue,
                i32[min max] readValue,
                i32[min max] moveValue,
                i32[min max] addrValue,
                i32[min max] sharedValue)
                {
                    unsafe
                {
                    stack fnptr<fn i32[min max]()> callback =
                        capture(copy copyValue, read readValue, move moveValue, unsafe addr addrValue, unsafe shared sharedValue) () => copyValue + readValue;
                }

                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            IsFunctionPointerCaptureDiagnostic);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Equal(5, typeCheckModel.LambdaCaptures.Count);

        AssertCapture(typeCheckModel, "copyValue", "copy", isUnsafe: false);
        AssertCapture(typeCheckModel, "readValue", "read", isUnsafe: false);
        AssertCapture(typeCheckModel, "moveValue", "move", isUnsafe: false);
        AssertCapture(typeCheckModel, "addrValue", "addr", isUnsafe: true);
        AssertCapture(typeCheckModel, "sharedValue", "shared", isUnsafe: true);
        Assert.Single(typeCheckModel.LambdaCaptures.Select(static capture => capture.LambdaLocation).Distinct());
    }

    [Fact]
    public void CopyLambdaCapturesRejectMoveOnlyBindings()
    {
        var result = Compile(
            """
            module Demo

            struct Token
            {
                i32[min max] Value;
            }

            fn void Run()
            {
                stack Token token = new Token()
                {
                    Value = 1
                };
                stack fnptr<fn i32[min max]()> callback =
                    capture(copy token) () => token.Value;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Capture mode 'copy' cannot copy 'token'", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Use 'move' to transfer ownership", StringComparison.Ordinal));
    }

    [Fact]
    public void CopyLambdaCapturesAllowTextViews()
    {
        var result = Compile(
            """
            module Demo

            fn void Run()
            {
                stack ascii label = "Score: ";
                stack unicode word = "Ready";
                stack fnptr<fn i32[min max]()> callback =
                    capture(copy label, copy word) () => 1;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Capture mode 'copy' cannot copy", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            IsFunctionPointerCaptureDiagnostic);
    }

    [Fact]
    public void ReadAndCopyLambdaCapturesDoNotExposeWritableBindings()
    {
        var result = Compile(
            """
            module Demo

            fn void Run()
            {
                stack mut i32[min max] readValue = 1;
                stack mut i32[min max] copyValue = 2;
                stack fnptr<fn void()> readCallback = capture(read readValue) () =>
                {
                    readValue = 3;
                    return;
                };
                stack fnptr<fn void()> copyCallback = capture(copy copyValue) () =>
                {
                    copyValue = 4;
                    return;
                };
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3007"
                && diagnostic.Message.Contains("Cannot assign to immutable local 'readValue'", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3007"
                && diagnostic.Message.Contains("Cannot assign to immutable local 'copyValue'", StringComparison.Ordinal));
    }

    [Fact]
    public void MutLambdaCapturesExposeWritableBindingsInLambdaBody()
    {
        var result = Compile(
            """
            module Demo

            fn void Run()
            {
                stack mut i32[min max] value = 1;
                stack fnptr<fn void()> callback = capture(mut value) () =>
                {
                    value = 2;
                    return;
                };
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3007");
        Assert.Contains(
            result.Diagnostics,
            IsFunctionPointerCaptureDiagnostic);
    }

    [Fact]
    public void OutAndInitLambdaCapturesRejectReadsInLambdaBody()
    {
        var result = Compile(
            """
            module Demo

            fn void Run()
            {
                stack mut i32[min max] outValue = 1;
                stack mut i32[min max] initValue = 2;
                stack fnptr<fn void()> outCallback = capture(out outValue) () =>
                {
                    stack i32[min max] readOut = outValue;
                    return;
                };
                stack fnptr<fn void()> initCallback = capture(init initValue) () =>
                {
                    stack i32[min max] readInit = initValue;
                    return;
                };
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Assignment expects 'i32", StringComparison.Ordinal)
                && diagnostic.Message.Contains("out i32", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Assignment expects 'i32", StringComparison.Ordinal)
                && diagnostic.Message.Contains("init i32", StringComparison.Ordinal));
    }

    [Fact]
    public void OutAndInitLambdaCapturesAllowWritesInLambdaBody()
    {
        var result = Compile(
            """
            module Demo

            fn void Run()
            {
                stack mut i32[min max] outValue = 1;
                stack mut i32[min max] initValue = 2;
                stack fnptr<fn void()> callback = capture(out outValue, init initValue) () =>
                {
                    outValue = 3;
                    initValue = 4;
                    return;
                };
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code is "STK3002" or "STK3007");
        Assert.Contains(
            result.Diagnostics,
            IsFunctionPointerCaptureDiagnostic);
    }

    [Fact]
    public void UnsafeAddrLambdaCapturesExposeReadonlyAddressNotCapturedValue()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(i32[min max] token)
            {
                unsafe
                {
                    stack fnptr<fn void()> badRead = capture(unsafe addr token) () =>
                    {
                        stack i32[min max] value = token;
                        return;
                    };

                    stack fnptr<fn void()> goodAddress = capture(unsafe addr token) () =>
                    {
                        stack rawptr<frozen i32[min max]> address = token;
                        return;
                    };
                }

                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Assignment expects 'i32", StringComparison.Ordinal)
                && diagnostic.Message.Contains("rawptr<frozen i32", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("address", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsafeSharedLambdaCapturesExposeSharedReadOnlyBindings()
    {
        var result = Compile(
            """
            module Demo

            fn void Run(i32[min max] sharedValue)
            {
                unsafe
                {
                    stack fnptr<fn void()> callback = capture(unsafe shared sharedValue) () =>
                    {
                        stack shared i32[min max] value = sharedValue;
                        sharedValue = 3;
                        return;
                    };
                }

                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("value", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3007"
                && diagnostic.Message.Contains("Cannot assign to immutable local 'sharedValue'", StringComparison.Ordinal));
    }

    [Fact]
    public void WritableLambdaCaptureModesRequireWritableBindings()
    {
        var bad = Compile(
            """
            module Demo

            fn void Run(
                i32[min max] mutValue,
                i32[min max] outValue,
                i32[min max] initValue)
                {
                    stack fnptr<fn i32[min max]()> callback =
                    capture(mut mutValue, out outValue, init initValue) () => 1;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(bad.Succeeded);
        Assert.Contains(
            bad.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Capture mode 'mut' needs 'mutValue'", StringComparison.Ordinal));
        Assert.Contains(
            bad.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Capture mode 'out' needs 'outValue'", StringComparison.Ordinal));
        Assert.Contains(
            bad.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Capture mode 'init' needs 'initValue'", StringComparison.Ordinal));

        var good = Compile(
            """
            module Demo

            fn void Run()
            {
                stack mut i32[min max] mutValue = 1;
                stack mut i32[min max] outValue = 2;
                stack mut i32[min max] initValue = 3;
                stack fnptr<fn i32[min max]()> callback =
                    capture(mut mutValue, out outValue, init initValue) () => 1;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(good.Succeeded);
        Assert.DoesNotContain(
            good.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002");
        Assert.Contains(
            good.Diagnostics,
            IsFunctionPointerCaptureDiagnostic);
    }

    [Fact]
    public void UnsafeFunctionsRequireUnsafeContextDuringTypeChecking()
    {
        var bad = Compile(
            """
            module Demo

            unsafe fn void Touch();

            fn void Run()
            {
                Touch();
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(bad.Succeeded);
        Assert.Contains(
            bad.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("Unsafe function 'Touch' requires an unsafe context", StringComparison.Ordinal));

        var good = Compile(
            """
            module Demo

            unsafe fn void Touch();

            fn void Run()
            {
                unsafe
                {
                    Touch();
                }

                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(good.Succeeded, string.Join(", ", good.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void RawPointerSignaturesRequireUnsafeFunctions()
    {
        var result = Compile(
            """
            module Demo

            fn void Touch(rawptr<i32[min max]> pointer);
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("uses raw pointer types and must be declared 'unsafe'", StringComparison.Ordinal));
    }

    [Fact]
    public void RawPointerLocalOperationsRequireUnsafeContext()
    {
        var result = Compile(
            """
            module Demo

            fn void Run()
            {
                stack mut i32[min max] value = 1;
                stack rawmutptr<i32[min max]> pointer = &value;
                *pointer = 2;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("local raw pointer declarations", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("Raw pointer address-of operator", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("Raw pointer dereference operator", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsafeBlocksPermitRawPointerLocalOperations()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run()
            {
                stack mut i32[min max] value = 1;
                unsafe
                {
                    stack rawmutptr<i32[min max]> pointer = &value;
                    *pointer = 2;
                }

                return value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void FfiDeclarationsRequireUnsafeModifier()
    {
        var result = Compile(
            """
            module Demo

            ffi fn void NativeCall();
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("FFI and assembly function 'NativeCall' must be declared 'unsafe'", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsafeFunctionItemsDoNotPromoteToOrdinaryFunctionPointers()
    {
        var outsideUnsafe = Compile(
            """
            module Demo

            unsafe fn i32[min max] Touch()
            {
                return 1;
            }

            fn void Run()
            {
                stack fnptr<fn i32[min max]()> callback = Touch;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(outsideUnsafe.Succeeded);
        Assert.Contains(
            outsideUnsafe.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("cannot be promoted to ordinary function pointer", StringComparison.Ordinal));
        Assert.True(outsideUnsafe.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? outsideUnsafeModel));
        Assert.NotNull(outsideUnsafeModel);
        Assert.Empty(outsideUnsafeModel.AddressTakenFunctions);

        var insideUnsafe = Compile(
            """
            module Demo

            unsafe fn i32[min max] Touch()
            {
                return 1;
            }

            fn void Run()
            {
                unsafe
                {
                    stack fnptr<fn i32[min max]()> callback = Touch;
                }

                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(insideUnsafe.Succeeded);
        Assert.Contains(
            insideUnsafe.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("cannot be promoted to ordinary function pointer", StringComparison.Ordinal));
        Assert.True(insideUnsafe.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? insideUnsafeModel));
        Assert.NotNull(insideUnsafeModel);
        Assert.Empty(insideUnsafeModel.AddressTakenFunctions);
    }

    [Fact]
    public void UnsafeFunctionItemsPromoteToUnsafeFunctionPointersAndCallsRequireUnsafeContext()
    {
        var promotion = Compile(
            """
            module Demo

            unsafe fn i32[min max] Touch()
            {
                return 1;
            }

            fn void Register()
            {
                stack fnptr<unsafe fn i32[min max]()> callback = Touch;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(promotion.Succeeded, string.Join(", ", promotion.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(promotion.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? promotionModel));
        Assert.NotNull(promotionModel);
        var promoted = Assert.Single(promotionModel.FunctionPointerPromotions);
        Assert.True(promoted.TargetType.FunctionPointerIsUnsafe);
        Assert.Single(promotionModel.AddressTakenFunctions);

        var outsideUnsafeCall = Compile(
            """
            module Demo

            unsafe fn i32[min max] Touch()
            {
                return 1;
            }

            fn i32[min max] Run()
            {
                stack fnptr<unsafe fn i32[min max]()> callback = Touch;
                return callback();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(outsideUnsafeCall.Succeeded);
        Assert.Contains(
            outsideUnsafeCall.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("Unsafe function pointer", StringComparison.Ordinal));

        var insideUnsafeCall = Compile(
            """
            module Demo

            unsafe fn i32[min max] Touch()
            {
                return 1;
            }

            unsafe fn i32[min max] Run()
            {
                stack fnptr<unsafe fn i32[min max]()> callback = Touch;
                return callback();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(insideUnsafeCall.Succeeded, string.Join(", ", insideUnsafeCall.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void UnsafeFunctionItemsDoNotPromoteInReturnOrArgumentTargetPositions()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Touch()
            {
                return 1;
            }

            fn fnptr<fn i32[min max]()> Factory()
            {
                return Touch;
            }

            fn void Register(fnptr<fn i32[min max]()> callback)
            {
                return;
            }

            fn void Run()
            {
                Register(Touch);
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.Empty(typeCheckModel.AddressTakenFunctions);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3024"
                && diagnostic.Message.Contains("cannot be promoted to ordinary function pointer", StringComparison.Ordinal));
        Assert.True(
            result.Diagnostics.Count(static diagnostic => diagnostic.Code == "STK3024") >= 2,
            string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void TypeRelativeIntegerRangeEndpointsResolveAgainstContainingIntegerType()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Signed(i32[min max] value)
            {
                return value;
            }

            fn i64[0 max] NonNegative(i64[0 max] value)
            {
                return value;
            }

            fn u8[min 127] BytePrefix(u8[min 127] value)
            {
                return value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var signed = typeCheckModel.Functions["Signed"];
        AssertIntegerRange(signed.ReturnType, 32, new BigInteger(int.MinValue), new BigInteger(int.MaxValue));
        AssertIntegerRange(signed.Parameters[0].Type, 32, new BigInteger(int.MinValue), new BigInteger(int.MaxValue));

        var nonNegative = typeCheckModel.Functions["NonNegative"];
        AssertIntegerRange(nonNegative.ReturnType, 64, BigInteger.Zero, new BigInteger(long.MaxValue));
        AssertIntegerRange(nonNegative.Parameters[0].Type, 64, BigInteger.Zero, new BigInteger(long.MaxValue));

        var bytePrefix = typeCheckModel.Functions["BytePrefix"];
        AssertIntegerRange(bytePrefix.ReturnType, 8, BigInteger.Zero, new BigInteger(127), isUnsigned: true);
        AssertIntegerRange(bytePrefix.Parameters[0].Type, 8, BigInteger.Zero, new BigInteger(127), isUnsigned: true);
        Assert.Equal("u8[0 127]", bytePrefix.ReturnType.DisplayName);
    }

    [Fact]
    public void ImportedModulePublicMembersResolveByFinalName()
    {
        var result = Compile(
            """
            import Lib.Foundation
            module Demo

            fn u32[0 2147483647] Use()
            {
                stack Box box = new()
                {
                    Value = Identity(Answer)
                };
                stack Status status = Status.Ok;
                switch (status)
                {
                    case Status.Ok:
                        return box.Value;
                    case Status.Err:
                        return Worker.Value();
                }
            }
            """,
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Lib.Foundation", "Lib/Foundation.stark"),
                        """
                        module Lib.Foundation

                        public const Answer = 41;

                        public struct Box
                        {
                            u32[0 2147483647] Value;
                        }

                        public enum Status
                        {
                            Ok,
                            Err
                        }

                        public struct Worker
                        {
                            static finite law u32[0 2147483647] Value()
                            {
                                return 7;
                            }
                        }

                        public fn u32[0 2147483647] Identity(u32[0 2147483647] value)
                        {
                            return value;
                        }
                        """,
                        "Lib/Foundation.stark")
                ])));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void ScalarConstDeclarationsInferSmallestExactNumericTypes()
    {
        var result = Compile(
            """
            module Demo

            const BoardWidth = 80;
            const u8 BoardWidthTyped = 80;
            const BoardWidthWide = 80;
            const Negative = -129;
            const BigCount = 2 ** 16;
            const u8 UnsignedSmall = 80;
            const u32 UnsignedWide = 4294967295;
            const SmallFloat = 3.5;
            const FloatLiteral = 3.5f;
            const f32 ExplicitFloat = 3.5;
            const f64 ExplicitSmallFloat = 3.5f;

            finite law u8[0 max] UseBoardWidth()
            {
                return BoardWidth;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        AssertIntegerRange(typeCheckModel.Globals["BoardWidth"].Type, 8, new BigInteger(80), new BigInteger(80), isUnsigned: true);
        AssertIntegerRange(typeCheckModel.Globals["BoardWidthTyped"].Type, 8, new BigInteger(80), new BigInteger(80), isUnsigned: true);
        AssertIntegerRange(typeCheckModel.Globals["BoardWidthWide"].Type, 8, new BigInteger(80), new BigInteger(80), isUnsigned: true);
        AssertIntegerRange(typeCheckModel.Globals["Negative"].Type, 16, new BigInteger(-129), new BigInteger(-129));
        AssertIntegerRange(typeCheckModel.Globals["BigCount"].Type, 24, new BigInteger(65536), new BigInteger(65536), isUnsigned: true);
        AssertIntegerRange(typeCheckModel.Globals["UnsignedSmall"].Type, 8, new BigInteger(80), new BigInteger(80), isUnsigned: true);
        AssertIntegerRange(typeCheckModel.Globals["UnsignedWide"].Type, 32, BigInteger.Parse("4294967295"), BigInteger.Parse("4294967295"), isUnsigned: true);
        Assert.Equal(StarkTypeKind.Float, typeCheckModel.Globals["SmallFloat"].Type.Kind);
        Assert.Equal(64, typeCheckModel.Globals["SmallFloat"].Type.BitWidth);
        Assert.Equal(StarkTypeKind.Float, typeCheckModel.Globals["FloatLiteral"].Type.Kind);
        Assert.Equal(32, typeCheckModel.Globals["FloatLiteral"].Type.BitWidth);
        Assert.Equal(StarkTypeKind.Float, typeCheckModel.Globals["ExplicitFloat"].Type.Kind);
        Assert.Equal(32, typeCheckModel.Globals["ExplicitFloat"].Type.BitWidth);
        Assert.Equal(StarkTypeKind.Float, typeCheckModel.Globals["ExplicitSmallFloat"].Type.Kind);
        Assert.Equal(32, typeCheckModel.Globals["ExplicitSmallFloat"].Type.BitWidth);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning
            && diagnostic.Message.Contains("ExplicitFloat", StringComparison.Ordinal)
            && diagnostic.Message.Contains("f32", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning
            && diagnostic.Message.Contains("ExplicitSmallFloat", StringComparison.Ordinal)
            && diagnostic.Message.Contains("f32", StringComparison.Ordinal));
    }

    [Fact]
    public void StrictIntegerRangesRejectExplicitScalarConstWrongWidthOrSign()
    {
        var result = Compile(
            """
            module Demo

            const i32 PositiveSigned = 80;
            const u32 PositiveWide = 80;
            const i32 NegativeWide = -1;
            const u8 CorrectUnsigned = 255;
            const i8 CorrectSigned = -1;
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                EnforceIntegerRangeStorageRules: true));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("PositiveSigned", StringComparison.Ordinal)
                && diagnostic.Message.Contains("i32", StringComparison.Ordinal)
                && diagnostic.Message.Contains("u8", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("PositiveWide", StringComparison.Ordinal)
                && diagnostic.Message.Contains("u32", StringComparison.Ordinal)
                && diagnostic.Message.Contains("u8", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("NegativeWide", StringComparison.Ordinal)
                && diagnostic.Message.Contains("i32", StringComparison.Ordinal)
                && diagnostic.Message.Contains("i8", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Message.Contains("CorrectUnsigned", StringComparison.Ordinal)
                || diagnostic.Message.Contains("CorrectSigned", StringComparison.Ordinal));
    }

    [Fact]
    public void ScalarConstDeclarationsReportFriendlyNumericTypeDiagnostics()
    {
        var result = Compile(
            """
            module Demo

            const i32[min max] Ranged = 80;
            const i8 TooSmall = 200;
            const f32 TooPrecise = 0.1;
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3002"
            && diagnostic.Message.Contains("already has one exact value", StringComparison.Ordinal)
            && diagnostic.Message.Contains("does not need an integer range", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3002"
            && diagnostic.Message.Contains("TooSmall", StringComparison.Ordinal)
            && diagnostic.Message.Contains("does not fit in i8", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3002"
            && diagnostic.Message.Contains("TooPrecise", StringComparison.Ordinal)
            && diagnostic.Message.Contains("without changing it", StringComparison.Ordinal));
    }

    [Fact]
    public void HugeCompileTimeIntegerConstCannotBecomeRuntimeStorage()
    {
        var result = Compile(
            """
            module Demo

            const Huge = 2 ** 1024;
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3013"
                && diagnostic.Message.Contains("Type 'integer' is compile-time-only", StringComparison.Ordinal)
                && diagnostic.Message.Contains("a global constant type", StringComparison.Ordinal));
    }

    [Fact]
    public void HugeCompileTimeIntegerConversionReportsConcreteTargetOverflow()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law i1024[min max] Run()
            {
                return (i1024[min max])(2 ** 1024);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("Compile-time integer value", StringComparison.Ordinal)
                && diagnostic.Message.Contains("does not fit in 'i1024'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompileTimeUnaryIntegerConstantsReinferExactResultStorage()
    {
        var result = Compile(
            """
            module Demo

            const MinI32 = -(2 ** 31);
            const MinusOne = ~0;

            fn i32[min max] ReadMin()
            {
                return MinI32;
            }

            fn i8[min max] ReadMinusOne()
            {
                return MinusOne;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DictionaryAllowsCompilerProvenKeyTypes()
    {
        var result = Compile(
            """
            import System.Collections
            module Demo

            fn void Use(Dictionary<i32[0 max], bool> integers, Dictionary<bool, i32[0 max]> flags)
            {
                return;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("System.Collections", "System/Collections.stark"),
                        """
                        module System.Collections

                        public struct Dictionary<K, V>
                        {
                            K Key;
                            V Value;
                        }
                        """,
                        "System/Collections.stark")
                ])));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3023");
    }

    [Fact]
    public void DictionaryRejectsUnprovenKeyTypes()
    {
        var result = Compile(
            """
            import System.Collections
            module Demo

            struct Box
            {
                i32[0 max] Value;
            }

            fn void Use(Dictionary<Box, i32[0 max]> boxes)
            {
                return;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("System.Collections", "System/Collections.stark"),
                        """
                        module System.Collections

                        public struct Dictionary<K, V>
                        {
                            K Key;
                            V Value;
                        }
                        """,
                        "System/Collections.stark")
                ])));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3023"
                && diagnostic.Message.Contains("Dictionary<Box, V> collection use", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Dictionary key type 'Box'", StringComparison.Ordinal)
                && diagnostic.Message.Contains("System.Collections.DictionaryKey<Box>", StringComparison.Ordinal));
    }

    [Fact]
    public void DictionaryAllowsExplicitStaticHashEqualsKeyContract()
    {
        var result = Compile(
            """
            import System.Collections
            module Demo

            struct Symbol
            {
                u32[0 max] Id;

                static finite law u64[0 max] Hash(borrow Symbol value)
                {
                    return (u64[0 max])value.Id;
                }

                static finite law bool Equals(borrow Symbol left, borrow Symbol right)
                    where overlap(left, right)
                {
                    return left.Id == right.Id;
                }
            }

            fn void Use(Dictionary<Symbol, i32[0 max]> symbols)
            {
                return;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("System.Collections", "System/Collections.stark"),
                        """
                        module System.Collections

                        public struct Dictionary<K, V>
                        {
                            K Key;
                            V Value;
                        }
                        """,
                        "System/Collections.stark")
                ])));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3023");
    }

    [Fact]
    public void HashSetRequiresExplicitHashEqualsKeyContractForNonPrimitiveKeys()
    {
        var result = Compile(
            """
            import System.Collections
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn void Use(HashSet<Box> symbols)
            {
                return;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("System.Collections", "System/Collections.stark"),
                        """
                        module System.Collections

                        public struct HashSet<T>
                        {
                            T Key;
                        }
                        """,
                        "System/Collections.stark")
                ])));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3023"
                && diagnostic.Message.Contains("HashSet<Box> collection use", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Dictionary key type 'Box'", StringComparison.Ordinal)
                && diagnostic.Message.Contains("System.Collections.DictionaryKey<Box>", StringComparison.Ordinal));
    }

    [Fact]
    public void HashSetAllowsExplicitStaticHashEqualsKeyContract()
    {
        var result = Compile(
            """
            import System.Collections
            module Demo

            struct Symbol
            {
                u32[0 max] Id;

                static finite law u64[0 max] Hash(borrow Symbol value)
                {
                    return (u64[0 max])value.Id;
                }

                static finite law bool Equals(borrow Symbol left, borrow Symbol right)
                    where overlap(left, right)
                {
                    return left.Id == right.Id;
                }
            }

            fn void Use(HashSet<Symbol> symbols)
            {
                return;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("System.Collections", "System/Collections.stark"),
                        """
                        module System.Collections

                        public struct HashSet<T>
                        {
                            T Key;
                        }
                        """,
                        "System/Collections.stark")
                ])));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3023");
    }

    [Fact]
    public void DictionaryRejectsStaticHashEqualsContractWithoutOverlap()
    {
        var result = Compile(
            """
            import System.Collections
            module Demo

            struct Symbol
            {
                u32[0 max] Id;

                static finite law u64[0 max] Hash(borrow Symbol value)
                {
                    return (u64[0 max])value.Id;
                }

                static finite law bool Equals(borrow Symbol left, borrow Symbol right)
                {
                    return left.Id == right.Id;
                }
            }

            fn void Use(Dictionary<Symbol, i32[0 max]> symbols)
            {
                return;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("System.Collections", "System/Collections.stark"),
                        """
                        module System.Collections

                        public struct Dictionary<K, V>
                        {
                            K Key;
                            V Value;
                        }
                        """,
                        "System/Collections.stark")
                ])));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3023"
                && diagnostic.Message.Contains("Incompatible Equals contract method", StringComparison.Ordinal)
                && diagnostic.Message.Contains("where overlap(left, right)", StringComparison.Ordinal));
    }

    [Fact]
    public void DictionaryRejectsStaticHashEqualsContractWithWrongHashReturn()
    {
        var result = Compile(
            """
            import System.Collections
            module Demo

            struct Symbol
            {
                u32[0 max] Id;

                static finite law i64[min max] Hash(borrow Symbol value)
                {
                    return (i64[min max])value.Id;
                }

                static finite law bool Equals(borrow Symbol left, borrow Symbol right)
                    where overlap(left, right)
                {
                    return left.Id == right.Id;
                }
            }

            fn void Use(Dictionary<Symbol, i32[0 max]> symbols)
            {
                return;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("System.Collections", "System/Collections.stark"),
                        """
                        module System.Collections

                        public struct Dictionary<K, V>
                        {
                            K Key;
                            V Value;
                        }
                        """,
                        "System/Collections.stark")
                ])));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3023"
                && diagnostic.Message.Contains("Incompatible Hash contract method", StringComparison.Ordinal)
                && diagnostic.Message.Contains("must return 'u64[0 max]'", StringComparison.Ordinal)
                && diagnostic.Message.Contains("u64[0 max] Hash", StringComparison.Ordinal));
    }

    [Fact]
    public void DictionaryRejectsStaticHashEqualsContractMissingEquals()
    {
        var result = Compile(
            """
            import System.Collections
            module Demo

            struct Symbol
            {
                u32[0 max] Id;

                static finite law u64[0 max] Hash(borrow Symbol value)
                {
                    return (u64[0 max])value.Id;
                }
            }

            fn void Use(Dictionary<Symbol, i32[0 max]> symbols)
            {
                return;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("System.Collections", "System/Collections.stark"),
                        """
                        module System.Collections

                        public struct Dictionary<K, V>
                        {
                            K Key;
                            V Value;
                        }
                        """,
                        "System/Collections.stark")
                ])));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3023"
                && diagnostic.Message.Contains("Missing required Equals contract method", StringComparison.Ordinal)
                && diagnostic.Message.Contains("bool Equals(borrow Symbol left, borrow Symbol right)", StringComparison.Ordinal));
    }

    [Fact]
    public void HashSetRejectsStaticHashEqualsContractWithWrongEqualsReturn()
    {
        var result = Compile(
            """
            import System.Collections
            module Demo

            struct Symbol
            {
                u32[0 max] Id;

                static finite law u64[0 max] Hash(borrow Symbol value)
                {
                    return (u64[0 max])value.Id;
                }

                static finite law u64[0 max] Equals(borrow Symbol left, borrow Symbol right)
                    where overlap(left, right)
                {
                    return (u64[0 max])left.Id;
                }
            }

            fn void Use(HashSet<Symbol> symbols)
            {
                return;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("System.Collections", "System/Collections.stark"),
                        """
                        module System.Collections

                        public struct HashSet<T>
                        {
                            T Key;
                        }
                        """,
                        "System/Collections.stark")
                ])));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3023"
                && diagnostic.Message.Contains("HashSet<Symbol> collection use", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Incompatible Equals contract method", StringComparison.Ordinal)
                && diagnostic.Message.Contains("must return 'bool'", StringComparison.Ordinal));
    }

    [Fact]
    public void DictionaryRejectsUnprovenKeyTypesAfterGenericMonomorphization()
    {
        var result = Compile(
            """
            import System.Collections
            module Demo

            struct Box
            {
                i32[0 max] Value;
            }

            fn void Use<K>(K key)
            {
                stack Dictionary<K, i32[0 max]> values = new();
                return;
            }

            fn void Bad(Box key)
            {
                Use(key);
                return;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "monomorphization-plan",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("System.Collections", "System/Collections.stark"),
                        """
                        module System.Collections

                        public struct Dictionary<K, V>
                        {
                            K Key;
                            V Value;
                        }
                        """,
                        "System/Collections.stark")
                ])));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "STK3023");
    }

    [Fact]
    public void AmbiguousImportedTypeFinalNamesRequireQualification()
    {
        var result = Compile(
            """
            import Left
            import Right
            module Demo

            fn i32[0 max] Use()
            {
                stack Value value = new()
                {
                    X = 1
                };
                return value.X;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Left", "Left.stark"),
                        """
                        module Left

                        public struct Value
                        {
                            i32[0 max] X;
                        }
                        """,
                        "Left.stark"),
                    (
                        new ResolvedModuleReference("Right", "Right.stark"),
                        """
                        module Right

                        public struct Value
                        {
                            i32[0 max] X;
                        }
                        """,
                        "Right.stark")
                ])));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3004"
                && diagnostic.Message.Contains("Imported type name 'Value'", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Left.Value", StringComparison.Ordinal)
                && diagnostic.Message.Contains("Right.Value", StringComparison.Ordinal));
    }

    [Fact]
    public void ConstantArithmeticIntegerRangeEndpointsResolveAtCompileTime()
    {
        var result = Compile(
            """
            module Demo

            fn u48[10 ** 2 10 ** 10] DecimalPowers(u48[10 ** 2 10 ** 10] value)
            {
                return value;
            }

            fn u24[2 ** 4 2 ** 16] BinaryPowers(u24[2 ** 4 2 ** 16] value)
            {
                return value;
            }

            fn u32[1024 * 1024 1024 * 1024 * 1024] Sizes(u32[1024 * 1024 1024 * 1024 * 1024] value)
            {
                return value;
            }

            fn u8[(1 + 2) * 3 20 / 2 + 1] MixedArithmetic(u8[(1 + 2) * 3 20 / 2 + 1] value)
            {
                return value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var decimalPowers = typeCheckModel.Functions["DecimalPowers"];
        AssertIntegerRange(decimalPowers.ReturnType, 48, new BigInteger(100), BigInteger.Parse("10000000000"), isUnsigned: true);
        AssertIntegerRange(decimalPowers.Parameters[0].Type, 48, new BigInteger(100), BigInteger.Parse("10000000000"), isUnsigned: true);

        var binaryPowers = typeCheckModel.Functions["BinaryPowers"];
        AssertIntegerRange(binaryPowers.ReturnType, 24, new BigInteger(16), new BigInteger(65536), isUnsigned: true);
        AssertIntegerRange(binaryPowers.Parameters[0].Type, 24, new BigInteger(16), new BigInteger(65536), isUnsigned: true);

        var sizes = typeCheckModel.Functions["Sizes"];
        AssertIntegerRange(sizes.ReturnType, 32, new BigInteger(1048576), new BigInteger(1073741824), isUnsigned: true);
        AssertIntegerRange(sizes.Parameters[0].Type, 32, new BigInteger(1048576), new BigInteger(1073741824), isUnsigned: true);

        var mixedArithmetic = typeCheckModel.Functions["MixedArithmetic"];
        AssertIntegerRange(mixedArithmetic.ReturnType, 8, new BigInteger(9), new BigInteger(11), isUnsigned: true);
        AssertIntegerRange(mixedArithmetic.Parameters[0].Type, 8, new BigInteger(9), new BigInteger(11), isUnsigned: true);
    }

    [Fact]
    public void StrictIntegerRangesRejectSignedEndpointsOutsideBaseType()
    {
        var result = Compile(
            """
            module Demo

            fn i32[10**2 10**10] TooWide(i8[-200 0] value)
            {
                return 0;
            }
            """,
            new CompilerOptions(
                StopAfterPassId: "type-check",
                EnforceIntegerRangeStorageRules: true));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("i32", StringComparison.Ordinal)
                && diagnostic.Message.Contains("between", StringComparison.Ordinal)
                && diagnostic.Message.Contains("u48[100 10000000000]", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("i8", StringComparison.Ordinal)
                && diagnostic.Message.Contains("between", StringComparison.Ordinal)
                && diagnostic.Message.Contains("i16[-200 0]", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsupportedIntegerRangeEndpointIdentifiersAreRejected()
    {
        var result = Compile(
            """
            module Demo

            fn i32[foo max] Bad()
            {
                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("Integer range endpoint 'foo'", StringComparison.Ordinal));
    }

    [Fact]
    public void ManualFullWidthIntegerRangeEndpointsAreRejectedInFavorOfMinMax()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] BadSigned()
            {
                return 0;
            }

            fn u8[0 255] BadUnsignedMax(u8[0 255] value)
            {
                return value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("minimum value for i32", StringComparison.Ordinal)
                && diagnostic.Message.Contains("use the `min` shorthand", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("maximum value for i32", StringComparison.Ordinal)
                && diagnostic.Message.Contains("use the `max` shorthand", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("maximum value for u8", StringComparison.Ordinal)
                && diagnostic.Message.Contains("use the `max` shorthand", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsignedIntegerRangeLowerBoundMayUseZeroInsteadOfMin()
    {
        var result = Compile(
            """
            module Demo

            fn u8[0 max] Good(u8[0 max] value)
            {
                return value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void ConstantArithmeticIntegerRangeEndpointOverflowIsRejected()
    {
        var result = Compile(
            """
            module Demo

            fn i32[0 2 ** 2048] Bad()
            {
                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("overflowed", StringComparison.Ordinal));
    }

    [Fact]
    public void ConstantArithmeticIntegerRangeEndpointDivisionByZeroIsRejected()
    {
        var result = Compile(
            """
            module Demo

            fn i32[0 10 / 0] Bad()
            {
                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("divide by zero", StringComparison.Ordinal));
    }

    [Fact]
    public void ReversedTypeRelativeIntegerRangeEndpointsAreRejected()
    {
        var result = Compile(
            """
            module Demo

            fn i32[max min] Bad()
            {
                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("lower bound", StringComparison.Ordinal)
                && diagnostic.Message.Contains("upper bound", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsignedIntegerRangeEndpointsBelowZeroAreRejected()
    {
        var result = Compile(
            """
            module Demo

            fn u8[-1 10] Bad(u8[-1 10] value)
            {
                return value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("u8", StringComparison.Ordinal)
                && diagnostic.Message.Contains("0", StringComparison.Ordinal)
                && diagnostic.Message.Contains("255", StringComparison.Ordinal));
    }

    [Fact]
    public void ReversedConstantArithmeticIntegerRangeEndpointsAreRejected()
    {
        var result = Compile(
            """
            module Demo

            fn i32[2 ** 8 2 ** 4] Bad()
            {
                return 0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3014"
                && diagnostic.Message.Contains("lower bound", StringComparison.Ordinal)
                && diagnostic.Message.Contains("upper bound", StringComparison.Ordinal));
    }

    [Fact]
    public void TypeRelativeIntegerEndpointNamesAreRejectedOutsideIntegerRanges()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Bad()
            {
                return min;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3003"
                && diagnostic.Message.Contains("Unknown symbol 'min'", StringComparison.Ordinal));
    }

    [Fact]
    public void BitwiseXorRequiresIntegerOperands()
    {
        var result = Compile(
            """
            module Demo

            finite law f32 Run()
            {
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

            finite law i32[min max] Run(i32[min max] left, i32[min max] right)
            {
                stack mut i32[min max] value = left;
                value +%= right;
                stack i32[min max] product = left *| right;
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

            strictfp finite law f32 Run(f32 left, f32 right)
            {
                return left + right;
            }
            """);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "STK3008");
    }

    [Fact]
    public void FloatingPointArithmeticChainsTypeCheckAcrossMixedNumericOperands()
    {
        var result = Compile(
            """
            module Demo

            strictfp finite law f64 Run(f32 left, i32[min max] middle, f64 right, f32 divisor)
            {
                return left + middle * right / divisor - 1.0;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ExplicitConversionsPointerOperatorsAndSliceViewsTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law i32[min max] Run(i64[min max] bits, ascii text)
            {
                stack mut i32[min max] value = 7;
                stack rawmutptr<i32[min max]> ptr = &value;
                stack rawptr<i32[min max]> readonlyPtr = (rawptr<i32[min max]>)ptr;
                *ptr = (i32[min max])bits;
                stack i64[min max] address = (i64[min max])ptr;
                stack rawmutptr<i32[min max]> roundTrip = (rawmutptr<i32[min max]>)address;
                stack i32[min max][2] values =
                {
                    1, 2
                };
                stack i32[min max][] view = (i32[min max][])values;
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

            finite law ascii AsciiString()
            {
                return "\0\b\t\n\f\r\\\"\'";
            }

            finite law ascii AsciiChar()
            {
                return '\x41';
            }

            finite law unicode UnicodeString()
            {
                return (unicode)"\xC9";
            }

            finite law unicode UnicodeChar()
            {
                return (unicode)'\u03B1';
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void RawAndMultilineTextLiteralsTypeCheckAsTextLiterals()
    {
        var result = Compile(
            """""
            module Demo

            finite law ascii RawPath()
            {
                return raw"c:\compiler\stage1";
            }

            finite law ascii Multiline()
            {
                return raw"""
                alpha
                beta
                """;
            }

            finite law ascii Interpolated()
            {
                return $raw"Score: {100}\n";
            }

            finite law unicode Wide()
            {
                return (unicode)raw"\u03B1";
            }
            """"",
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ConstGlobalAggregateProjectionsCanBindToFrozenParameters()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            struct Holder
            {
                Box Item;
            }

            const Holder Current = new Holder()
            {
                Item = new Box()
                {
                    Value = 7
                }
            };

            fn i32[min max] Read(frozen Box box)
            {
                return 7;
            }

            fn i32[min max] Run()
            {
                return Read(Current.Item);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void ExplicitLiteralTextConversionsTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            finite law unicode Widen()
            {
                return (unicode)"Hello";
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void CompileTimeTextConcatenationTypeChecksAsTextLiteral()
    {
        var result = Compile(
            """
            module Demo

            finite law ascii Label()
            {
                return "Score: " + "100";
            }

            finite law unicode WideLabel()
            {
                return "Score: " + "100";
            }

            finite law unicode ExplicitWideLabel()
            {
                return (unicode)"Score: " + (unicode)"100";
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void CompileTimeInterpolatedTextTypeChecksAsTextLiteral()
    {
        var result = Compile(
            """
            module Demo

            finite law ascii Label()
            {
                const score = 100;
                return $"Score: {score}, ready: {true}";
            }

            finite law unicode WideLabel()
            {
                return $"Score: {100}";
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void FixedCapacityRuntimeInterpolatedTextTypeChecksWithKnownFormatter()
    {
        var result = Compile(
            """
            module System.Text

            public finite law ascii AsciiView(Ascii source);
            public unsafe fn bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);
            public unsafe fn bool TryFormatI32Ascii(rawmutptr<Ascii> destination, i32[min max] value);

            fn Ascii Label(i32[min max] score)
            {
                stack Ascii label[64] = $"Score: {score}";
                return label;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void FixedCapacityTextConcatenationTypeChecksForAsciiAndUnicodeBuffers()
    {
        var result = Compile(
            """
            module System.Text

            public finite law ascii AsciiView(Ascii source);
            public finite law unicode UnicodeView(Unicode source);
            public unsafe fn bool TryConcatAscii(rawmutptr<Ascii> destination, ascii left, ascii right);
            public unsafe fn bool TryConcatUnicode(rawmutptr<Unicode> destination, unicode left, unicode right);

            fn Ascii JoinAscii(Ascii left, ascii right)
            {
                stack Ascii combined[64] = left + right;
                return combined;
            }

            fn Unicode JoinUnicode(Unicode left, unicode right)
            {
                stack Unicode combined[64] = left + right;
                return combined;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void EmptyTextSlicesTypeCheckAsSameTextKind()
    {
        var result = Compile(
            """
            module Demo

            fn ascii SliceAscii(ascii text)
            {
                return text[];
            }

            fn unicode SliceUnicode(unicode text)
            {
                return text[];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void FixedArrayLengthsAcceptConstantArithmeticExpressions()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Run(i32[min max][1 + 2] values)
            {
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

            fn i32[min max] Run()
            {
                stack i32[min max][3] values =
                {
                    1, 2
                };
                return values[0] + values[1] + values[2];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ScalarizableNamedAggregatesAreOrderedComparable()
    {
        var result = Compile(
            """
            module Demo

            record Many(i32[min max] A, i32[min max] B, i32[min max] C, i32[min max] D, i32[min max] E)
            {
            }

            fn bool Less(Many left, Many right)
            {
                return left < right;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ScalarizableEnumsAreOrderedComparable()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                None,
                Many(i32[min max], i32[min max], i32[min max], i32[min max], i32[min max]),
            }

            fn bool Less(Token left, Token right)
            {
                return left < right;
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

            finite law ascii Run()
            {
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

            struct Box
            {
                i32[min max] Value;
            }

            struct PtrBox
            {
                rawmutptr<i32[min max]> Ptr;
            }

            unsafe finite law void Run(frozen Box box, frozen PtrBox ptrBox)
            {
                stack rawptr<frozen i32[min max]> valuePtr = &box.Value;
                stack rawptr<frozen i32[min max]> readonlyPtr = ptrBox.Ptr;
                stack bool valuesEqual = *valuePtr == *readonlyPtr;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ConstParameterProvenanceTypeChecksAsReadonlyAliases()
    {
        var result = Compile(
            """
            module Demo

            struct PtrBox
            {
                rawmutptr<i32[min max]> Ptr;
            }

            unsafe fn void Inspect(const PtrBox box, const rawmutptr<i32[min max]> ptr)
            {
                stack rawptr<frozen i32[min max]> fieldPtr = box.Ptr;
                stack rawptr<frozen i32[min max]> directPtr = ptr;
                stack i32[min max] value = *ptr;
                stack bool valuesEqual = *fieldPtr == *directPtr;
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ConstParametersCanBeForwardedToConstParameters()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn void Inspect(const Box box)
            {
                return;
            }

            fn void Forward(const Box box)
            {
                Inspect(box);
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void ConstRawPointerProvenanceFlowsThroughImmutableLocal()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn void Inspect(const rawmutptr<i32[min max]> ptr)
            {
                return;
            }

            unsafe fn void Forward(const rawmutptr<i32[min max]> ptr)
            {
                stack rawptr<frozen i32[min max]> local = ptr;
                Inspect(local);
                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void GenericConstRawPointerCallsInferFromConstProvenanceView()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn rawptr<frozen i32[min max]> Forward<T>(const rawmutptr<i32[min max]> ptr, T tag)
            {
                return ptr;
            }

            unsafe fn rawptr<frozen i32[min max]> Run(const rawmutptr<i32[min max]> ptr)
            {
                stack i32[min max] tag = 0;
                return Forward(ptr, tag);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void ConstRawSliceProvenanceFlowsThroughImmutableLocal()
    {
        var result = Compile(
            """
            module Demo

            fn void Inspect(const i32[min max][] view)
            {
                return;
            }

            unsafe fn void Forward(
                const rawmutptr<i32[min max]>[count] pointer,
                i32[1 10] count)
                {
                    unsafe
                {
                    stack frozen i32[min max][] view = slice(pointer, count);
                    Inspect(view);
                }

                return;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void AggregateSwitchPatternsTypeCheckOnScalarFields()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[min max] Left, i32[min max] Right)
            {
            }

            finite law i32[min max] Run(Pair value)
            {
                switch (value)
                {
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
    public void AggregatePropertySwitchPatternsTypeCheckOnNamedFields()
    {
        var result = Compile(
            """
            module Demo

            struct Boxed
            {
                i32[min max] Value;
                bool Enabled;
            }

            finite law i32[min max] Run(Boxed value)
            {
                switch (value)
                {
                    case Boxed { Enabled: true, Value: var found }:
                        return found;
                    case Boxed { Value: _, Enabled: _ }:
                        return 0;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ListSwitchPatternsTypeCheckOnFixedArrays()
    {
        var result = Compile(
            """
            module Demo

            finite law i32[min max] Run(i32[min max][2] values)
            {
                switch (values)
                {
                    case [1, var right]:
                        return right;
                    case [_, _]:
                        return 0;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void ListSwitchPatternsTypeCheckOnSlices()
    {
        var result = Compile(
            """
            module Demo

            unsafe finite law i32[min max] Run(i32[min max][2] source)
            {
                stack i32[min max][] values = source;
                switch (values)
                {
                    case [var left, var right]:
                        return left + right;
                    default:
                        return 0;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void NamedAggregateWholeValueSwitchCapturesTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[min max] Left, i32[min max] Right)
            {
            }

            finite law i32[min max] Run(Pair value)
            {
                switch (value)
                {
                    case var whole:
                        return whole.Left;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void NestedAggregateWholeValueSwitchCapturesTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            record Pair(i32[min max] Left, i32[min max] Right)
            {
            }
            record Outer(Pair Values, i32[min max] Tail)
            {
            }

            finite law i32[min max] Run(Outer value)
            {
                switch (value)
                {
                    case Outer(Pair capture, var tail):
                        return capture.Right + tail;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    [Fact]
    public void EnumWholeValueSwitchCapturesTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            enum Token
            {
                Empty,
                Pair(i32[min max], i32[min max]),
            }

            finite law i32[min max] Run(Token value)
            {
                switch (value)
                {
                    case Token.Pair capture:
                        switch (capture)
                        {
                            case Token.Pair(var left, var right):
                                return left + right;
                            default:
                                return 0;
                        }
                    default:
                        return -1;
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

            finite law i32[min max] Run(bool value, bool allow)
            {
                switch (value)
                {
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

            record Pair(i32[min max] Left, i32[min max] Right)
            {
            }
            record Outer(Pair Values, i32[min max] Tail)
            {
            }

            finite law i32[min max] Run(Outer value)
            {
                switch (value)
                {
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

            enum Token
            {
                End,
                Integer(i32[min max]),
                Move
                {
                    X: i32[min max], Y: i32[min max]
                },
            }

            finite law i32[min max] Run(Token token)
            {
                switch (token)
                {
                    case Token.End:
                        return 0;
                    case Token.Integer(var value):
                        return value;
                    case Token.Move
                    {
                        X: var x, Y: var y
                    }:
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

            enum Option<T>
            {
                None,
                Some(T),
            }

            finite law bool HasValue(Option<i32[min max]> opt)
            {
                switch (opt)
                {
                    case Option<i32[min max]>.None:
                        return false;
                    case Option<i32[min max]>.Some(var value):
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

            record Pair<A, B>(A First, B Second)
            {
            }

            finite law i32[min max] Sum(Pair<i32[min max], i32[min max]> pair)
            {
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

            record Pair<A, B>(A First, B Second)
            {
            }

            finite law i32[min max] Sum()
            {
                stack Pair<i32[min max], i32[min max]> pair = new Pair<i32[min max], i32[min max]>(3, 4);
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

            record Pair<A, B>(A First, B Second)
            {
            }

            fn bool Accept(Pair<i32[min max], bool> pair)
            {
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

            record Pair<A, B>(A First, B Second)
            {
            }

            unsafe fn i32[min max] Read(rawptr<Pair<i32[min max], bool>> ptr)
            {
                if ((*ptr).Second)
                {
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

            fn T Identity<T>(T value)
            {
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

            struct Box
            {
                fn T Echo<T>(T value)
                {
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

            fn T Identity<T>(T value)
            {
                return value;
            }

            fn i32[min max] Run()
            {
                stack i32[min max] value = 42;
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

            struct Box
            {
                fn T Echo<T>(borrow Box self, T value)
                {
                    return value;
                }
            }

            fn i32[min max] Run()
            {
                stack Box box = new Box();
                stack i32[min max] value = 42;
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
    public void GenericMethodsOnGenericTypesRecordConcreteInstantiationTriggers()
    {
        var result = Compile(
            """
            module Demo

            struct Box<T>
            {
                T Value;

                fn T Echo(borrow Box<T> self, T value)
                {
                    return value;
                }
            }

            fn i32[min max] Run()
            {
                stack Box<i32[min max]> box = new Box<i32[min max]>()
                {
                    Value = 1
                };
                stack i32[min max] value = 42;
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
        Assert.Equal("borrow Box<i32>", trigger.Signature.Parameters[0].Type.DisplayName);
        Assert.Equal("i32", trigger.Signature.Parameters[1].Type.DisplayName);
    }

    [Fact]
    public void GenericNestedMemberOutCallsInferStorageType()
    {
        var result = Compile(
            """
            module Demo

            struct Cell<T>
            {
                T Value;

                fn bool TryTake(mut borrow Cell<T> self, out T value)
                {
                    value = self.Value;
                    return true;
                }
            }

            struct Owner<T>
            {
                Cell<T> Inner;

                fn bool TryTake(mut borrow Owner<T> self, out T value)
                {
                    return self.Inner.TryTake(value);
                }
            }

            fn bool Run(mut borrow Owner<i32[0 max]> owner, out i32[0 max] value)
            {
                return owner.TryTake(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
    }

    [Fact]
    public void RepeatedGenericFunctionCallsReuseOneCachedInstantiationTrigger()
    {
        var result = Compile(
            """
            module Demo

            fn T Identity<T>(T value)
            {
                return value;
            }

            fn i32[min max] Run(i32[min max] left, i32[min max] right)
            {
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

            record Pair<T>(T Value)
            {
            }

            fn i32[min max] Add(Pair<i32[min max]> left, Pair<i32[min max]> right)
            {
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

            fn i32[min max] Parse(i32[min max] value)
            {
                return value;
            }

            fn T Parse<T>(T value)
            {
                return value;
            }

            fn i32[min max] Run()
            {
                stack i32[min max] value = 42;
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

            alias Byte = i8[min max];

            fn Byte Inc(Byte value)
            {
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

            unsafe fn i32[min max] Read(Ptr<i32[min max]> value)
            {
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
    public void GenericTypeAliasesSubstituteIntoClosureSignatures()
    {
        var result = Compile(
            """
            module Demo

            alias Mapper<T> = heap closure<fn T(T)>;

            fn Mapper<i32[min max]> Factory(Mapper<i32[min max]> mapper)
            {
                return mapper;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.TypeAliases.ContainsKey("Mapper"));
        var signature = typeCheckModel.Functions["Factory"];
        Assert.Equal(StarkTypeKind.Closure, signature.ReturnType.Kind);
        Assert.Equal("i32", signature.ReturnType.ClosureReturnType?.DisplayName);
        var closureParameter = Assert.Single(signature.ReturnType.ClosureParameterTypes ?? []);
        Assert.Equal("i32", closureParameter.DisplayName);
    }

    [Fact]
    public void GenericEnumAliasesCanQualifyVariants()
    {
        var result = Compile(
            """
            module Demo

            enum ResultCore<T, E>
            {
                Ok(T),
                Err(E),
            }

            alias Result<T, E> = ResultCore<T, E>;

            finite law i32[min max] Run()
            {
                stack Result<i32[min max], bool> result = Result<i32[min max], bool>.Ok(7);
                switch (result)
                {
                    case Result<i32[min max], bool>.Ok(var value):
                        return value;
                    case Result<i32[min max], bool>.Err(var failed):
                        if (failed)
                        {
                            return -1;
                        }

                        return -2;
                }
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.NamedTypes.ContainsKey("ResultCore<i32,bool>"), "alias-qualified variants should instantiate the underlying enum");
    }

    [Fact]
    public void GenericTypeWithWrongArgCountIsAnError()
    {
        var result = Compile(
            """
            module Demo

            enum Option<T>
            {
                None,
                Some(T),
            }

            finite law void Bad(Option<i32[min max], bool> opt)
            {
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

            enum Result<T, E>
            {
                Ok(T),
                Err(E),
            }

            finite law i32[min max] Unwrap(Result<i32[min max], bool> res)
            {
                switch (res)
                {
                    case Result<i32[min max], bool>.Ok(var value):
                        return value;
                    case Result<i32[min max], bool>.Err(var err):
                        if (err)
                        {
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

            record Point(i32[min max] X, i32[min max] Y)
            {
            }

            finite law void Bad(Point<i32[min max]> p)
            {
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

            fn i32[min max] Parse(i32[min max] value)
            {
                return value;
            }

            fn bool Parse(bool value)
            {
                return value;
            }

            fn i32[min max] Run()
            {
                return Parse(42);
            }

            fn bool RunBool()
            {
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
    public void NumericOverloadResolutionPrefersExactAndNarrowSafeMatches()
    {
        var result = Compile(
            """
            module Demo

            fn bool PickFloat(f64 value)
            {
                return true;
            }

            fn i32[min max] PickFloat(f32 value)
            {
                return 1;
            }

            fn bool PickInteger(u32[0 max] value)
            {
                return true;
            }

            fn i32[min max] PickInteger(i48[min max] value)
            {
                return 2;
            }

            fn i32[min max] PickInteger(i64[min max] value)
            {
                return 3;
            }

            fn i32[min max] PickInteger(f64 value)
            {
                return 4;
            }

            fn bool RunFloat()
            {
                stack f64 value = 3.5;
                return PickFloat(value);
            }

            fn bool RunInteger()
            {
                stack u32[0 max] value = (u32[0 max])42;
                return PickInteger(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
    }

    [Fact]
    public void MethodOverloadGroupsRegisterDistinctFunctionsAndResolveCalls()
    {
        var result = Compile(
            """
            module Demo

            struct Counter
            {
                i32[min max] Value;

                fn i32[min max] Scale(borrow Counter self, i32[min max] factor)
                {
                    return self.Value * factor;
                }

                fn i32[min max] Scale(borrow Counter self, bool doubleIt)
                {
                    if (doubleIt)
                    {
                        return self.Value * 2;
                    }

                    return self.Value;
                }
            }

            fn i32[min max] Run()
            {
                stack Counter counter = new Counter()
                {
                    Value = 3
                };
                return counter.Scale(4);
            }

            fn i32[min max] RunBool()
            {
                stack Counter counter = new Counter()
                {
                    Value = 3
                };
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

    [Fact]
    public void TargetTypedObjectCreationResolvesFromDestinationType()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;

                Box()
                {
                    self.Value = 0;
                }

                Box(i32[min max] value)
                {
                    self.Value = value;
                }
            }

            fn Box Make(i32[min max] value)
            {
                return new(value);
            }

            fn i32[min max] Run(i32[min max] value)
            {
                stack Box empty = new();
                stack Box initialized = new()
                {
                    Value = value
                };
                stack mut Box assigned = new(value);
                assigned = new(value);
                return assigned.Value + empty.Value + initialized.Value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var objectCreations = typeCheckModel.ObjectCreations.ToArray();
        Assert.Equal(5, objectCreations.Length);
        Assert.All(objectCreations, static objectCreation => Assert.Equal("Box", objectCreation.CreatedType.DisplayName));
        Assert.Equal(5, objectCreations.Count(static objectCreation => objectCreation.Constructor is not null));
        Assert.Single(objectCreations, static objectCreation => objectCreation.Members.Count == 1);
    }

    [Fact]
    public void TargetTypedObjectCreationResolvesAllocatorTakingConstructorOverload()
    {
        var result = Compile(
            """
            module Demo

            struct Allocator
            {
                i32[0 255] Tag;
            }

            struct List
            {
                i32[0 max] Capacity;

                List()
                {
                    self.Capacity = 0;
                }

                List(Allocator allocator)
                {
                    self.Capacity = allocator.Tag;
                }
            }

            fn List MakeDefault()
            {
                return new();
            }

            fn List MakeCustom(Allocator allocator)
            {
                return new(allocator);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        var constructors = typeCheckModel.ObjectCreations
            .Where(static objectCreation => objectCreation.CreatedType.DisplayName == "List")
            .Select(static objectCreation => objectCreation.Constructor)
            .ToArray();

        Assert.Contains(constructors, static constructor => constructor is { Parameters.Count: 0 });
        Assert.Contains(constructors, static constructor => constructor is { Parameters.Count: 1 }
            && constructor.Parameters[0].Type.DisplayName == "Allocator");
    }

    [Fact]
    public void TargetTypedObjectCreationRequiresNamedDestinationType()
    {
        var result = Compile(
            """
            module Demo

            fn void MissingTarget()
            {
                new();
            }

            fn void NonNamedTarget()
            {
                stack i32[min max] value = new();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("requires an expected named target type", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("requires a named target type", StringComparison.Ordinal)
                && diagnostic.Message.Contains("i32", StringComparison.Ordinal));
    }

    [Fact]
    public void DynamicStorageCreationCapacityAndInitIndexTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            fn i64[0 max] Run()
            {
                stack mut dynamic i32[0 max] values = new(4);
                values.Reserve(8);
                init values[0] = 7;
                return values.Length + values.Capacity;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DynamicStorageTryReserveReturnsBool()
    {
        var result = Compile(
            """
            module Demo

            fn bool Run()
            {
                stack mut dynamic i32[0 max] values = new(4);
                stack bool grew = values.TryReserve(8);
                return grew;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DynamicStorageTryReserveCapacityReturnsBool()
    {
        var result = Compile(
            """
            module Demo

            fn bool Run()
            {
                stack mut dynamic i32[0 max] values = new(4);
                stack bool grew = values.TryReserveCapacity(8);
                return grew;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DynamicStorageReserveRequiresMutableOwnerAndNonNegativeAdditionalCapacity()
    {
        var result = Compile(
            """
            module Demo

            fn void ImmutableOwner()
            {
                stack dynamic i32[0 max] values = new(4);
                values.Reserve(8);
            }

            fn void NegativeAdditionalCapacity()
            {
                stack mut dynamic i32[0 max] values = new(4);
                values.Reserve(-1);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3007"
                && diagnostic.Message.Contains("Cannot assign to immutable local 'values'", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("must be provably non-negative", StringComparison.Ordinal));
    }

    [Fact]
    public void DynamicStorageMoveLastReturnsElementType()
    {
        var result = Compile(
            """
            module Demo

            fn i32[0 max] Run()
            {
                stack mut dynamic i32[0 max] values = new(1);
                init values[0] = 42;
                stack i32[0 max] moved = values.MoveLast();
                return moved;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DynamicStorageMoveAtReturnsElementType()
    {
        var result = Compile(
            """
            module Demo

            fn i32[0 max] Run()
            {
                stack mut dynamic i32[0 max] values = new(2);
                init values[0] = 10;
                init values[1] = 20;
                stack i32[0 max] moved = values.MoveAt(0);
                return moved + values[0];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void DynamicStorageMoveLastRequiresMutableOwnerAndNoArguments()
    {
        var result = Compile(
            """
            module Demo

            fn void ImmutableOwner()
            {
                stack dynamic i32[0 max] values = new(1);
                values.MoveLast();
            }

            fn void ExtraArgument()
            {
                stack mut dynamic i32[0 max] values = new(1);
                values.MoveLast(1);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3007"
                && diagnostic.Message.Contains("Cannot assign to immutable local 'values'", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3009"
                && diagnostic.Message.Contains("MoveLast expects no arguments", StringComparison.Ordinal));
    }

    [Fact]
    public void DynamicStorageMoveAtRequiresMutableOwnerOneIntegerArgument()
    {
        var result = Compile(
            """
            module Demo

            fn void ImmutableOwner()
            {
                stack dynamic i32[0 max] values = new(1);
                values.MoveAt(0);
            }

            fn void MissingArgument()
            {
                stack mut dynamic i32[0 max] values = new(1);
                values.MoveAt();
            }

            fn void ExtraArgument()
            {
                stack mut dynamic i32[0 max] values = new(1);
                values.MoveAt(0, 1);
            }

            fn void NonIntegerIndex(bool flag)
            {
                stack mut dynamic i32[0 max] values = new(1);
                values.MoveAt(flag);
            }

            fn void NegativeIndex()
            {
                stack mut dynamic i32[0 max] values = new(1);
                values.MoveAt(-1);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3007"
                && diagnostic.Message.Contains("Cannot assign to immutable local 'values'", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3009"
                && diagnostic.Message.Contains("MoveAt expects one index argument but received 0", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3009"
                && diagnostic.Message.Contains("MoveAt expects one index argument but received 2", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("MoveAt index must be an integer", StringComparison.Ordinal)
                && diagnostic.Message.Contains("bool", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("MoveAt index must be provably non-negative", StringComparison.Ordinal));
    }

    [Fact]
    public void DynamicStorageReserveOperationsRequireMutableOwnerAndOneIntegerArgument()
    {
        var result = Compile(
            """
            module Demo

            fn void ReserveImmutableOwner()
            {
                stack dynamic i32[0 max] values = new(1);
                values.Reserve(1);
            }

            fn void ReserveMissingArgument()
            {
                stack mut dynamic i32[0 max] values = new(1);
                values.Reserve();
            }

            fn void TryReserveExtraArgument()
            {
                stack mut dynamic i32[0 max] values = new(1);
                values.TryReserve(1, 2);
            }

            fn void TryReserveCapacityNonInteger(bool flag)
            {
                stack mut dynamic i32[0 max] values = new(1);
                values.TryReserveCapacity(flag);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3007"
                && diagnostic.Message.Contains("Cannot assign to immutable local 'values'", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3009"
                && diagnostic.Message.Contains("Reserve expects one additional-capacity argument but received 0", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3009"
                && diagnostic.Message.Contains("TryReserve expects one additional-capacity argument but received 2", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("TryReserveCapacity target capacity must be an integer", StringComparison.Ordinal)
                && diagnostic.Message.Contains("bool", StringComparison.Ordinal));
    }

    [Fact]
    public void DynamicStorageOperationsRequireAddressableOwner()
    {
        var result = Compile(
            """
            module Demo

            fn dynamic i32[0 max] Make()
            {
                return new(1);
            }

            fn void Run()
            {
                Make().MoveLast();
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3007"
                && diagnostic.Message.Contains("MoveLast requires a mutable addressable dynamic owner", StringComparison.Ordinal)
                && diagnostic.Message.Contains("call to 'Make'", StringComparison.Ordinal));
    }

    [Fact]
    public void DynamicStorageSpareRangeCanBindInitSliceView()
    {
        var result = Compile(
            """
            module Demo

            fn void Run()
            {
                stack mut dynamic i32[0 max] values = new(4);
                stack init i32[0 max][] spare = init values[0, 4];
                init spare[0] = 1;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void InitSliceElementsAreWriteOnlyUntilInitialized()
    {
        var good = Compile(
            """
            module Demo

            fn void Fill(init i32[0 max][] destination, i32[0 max] value)
            {
                init destination[0] = value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(good.Succeeded, string.Join(", ", good.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

        var bad = Compile(
            """
            module Demo

            fn i32[0 max] Read(init i32[0 max][] destination)
            {
                return destination[0];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.False(bad.Succeeded);
        Assert.Contains(
            bad.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("found 'init i32", StringComparison.Ordinal));
    }

    [Fact]
    public void TextLiteralsHaveConstProvenanceForConstParameters()
    {
        var result = Compile(
            """
            module Demo

            fn bool AcceptAscii(const ascii value)
            {
                return true;
            }

            fn bool AcceptUnicode(const unicode value)
            {
                return true;
            }

            fn bool Run()
            {
                return AcceptAscii("alpha")
                    && AcceptAscii("al" + "pha")
                    && AcceptAscii((ascii)"alpha")
                    && AcceptUnicode((unicode)"alpha");
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    private static void AssertIntegerRange(StarkTypeSymbol type, int bitWidth, BigInteger min, BigInteger max, bool isUnsigned = false)
    {
        Assert.Equal(StarkTypeKind.Integer, type.Kind);
        Assert.Equal(bitWidth, type.BitWidth);
        Assert.Equal((BigInteger?)min, type.RangeMin);
        Assert.Equal((BigInteger?)max, type.RangeMax);
        Assert.Equal(isUnsigned, type.IsUnsigned);
    }

    private static void AssertFullIntegerRange(StarkTypeSymbol type, int bitWidth, bool isUnsigned)
    {
        var min = isUnsigned
            ? BigInteger.Zero
            : -(BigInteger.One << (bitWidth - 1));
        var max = isUnsigned
            ? (BigInteger.One << bitWidth) - BigInteger.One
            : (BigInteger.One << (bitWidth - 1)) - BigInteger.One;

        AssertIntegerRange(type, bitWidth, min, max, isUnsigned);
    }

    private static void AssertRawPointerToCVoid(StarkTypeSymbol type, bool isMutable)
    {
        Assert.Equal(StarkTypeKind.RawPointer, type.Kind);
        Assert.Equal(isMutable, type.IsMutablePointer);
        Assert.NotNull(type.ElementType);
        Assert.Equal(StarkTypeSymbols.CVoid, type.ElementType);
    }

    private static void AssertCapture(TypeCheckModel typeCheckModel, string name, string mode, bool isUnsafe)
    {
        var capture = Assert.Single(typeCheckModel.LambdaCaptures, item => item.Name == name);
        Assert.Equal(mode, capture.Mode);
        Assert.Equal(isUnsafe, capture.IsUnsafe);
        Assert.Equal(StarkTypeKind.Integer, capture.Type.Kind);
        Assert.Equal("Run", capture.EnclosingFunctionName);
    }

    private static bool IsFunctionPointerCaptureDiagnostic(CompilerDiagnostic diagnostic)
    {
        return diagnostic.Code == "STK3008"
            && diagnostic.Message.Contains("lambda converted to 'fnptr<...>' cannot capture local state", StringComparison.Ordinal)
            && diagnostic.Message.Contains("function pointers do not carry closure storage", StringComparison.Ordinal);
    }

    private static CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source), options);
    }
}
