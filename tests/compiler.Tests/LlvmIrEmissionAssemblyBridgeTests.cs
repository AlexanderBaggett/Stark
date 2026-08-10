using Stark.Compiler;

namespace compiler.Tests;

public sealed partial class LlvmIrEmissionTests
{
    [Fact]
    public void DirectRootAsmCallsLowerAtTheCallSiteWithoutABridgeSymbol()
    {
        var result = Compile(
            """
            module Demo

            public unsafe ffi asm(x86_64) fn i64[min max] Syscall0(i64[min max] number)
                in("rax") number,
                out("rax") return,
                clobber("rcx", "r11")
            {
                "syscall"
            }

            unsafe fn i64[min max] Run(i64[min max] number)
            {
                return Syscall0(number);
            }
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                QualifyModuleSymbols: true));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("; direct-only asm definition omitted: Syscall0", llvm);
        Assert.Contains("call i64 asm sideeffect \"syscall\", \"={rax},0,~{rcx},~{r11},~{memory},~{dirflag},~{fpsr},~{flags}\"(i64 %arg_number) nounwind, !srcloc", llvm);
        Assert.DoesNotContain("@llvm.used", llvm);
        Assert.DoesNotContain("define dso_local i64 @Demo_Syscall0", llvm);
        Assert.DoesNotContain("call i64 @Demo_Syscall0", llvm);
    }

    [Fact]
    public void ImportedAsmFunctionsFromDifferentModulesUseDistinctBridgeSymbols()
    {
        var result = Compile(
            """
            import Alpha
            import Beta
            module Demo

            unsafe fn i32[min max] Run(i32[min max] value)
            {
                return Alpha.Identity(value) + Beta.Identity(value);
            }
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Alpha", "/virtual/Alpha.stark", IsExternal: false),
                        """
                        module Alpha

                        public unsafe ffi asm(x86_64) fn i32[min max] Identity(i32[min max] value)
                            in("rax") value,
                            out("rax") return
                        {
                            ""
                        }
                        """,
                        "/virtual/Alpha.stark"
                    ),
                    (
                        new ResolvedModuleReference("Beta", "/virtual/Beta.stark", IsExternal: false),
                        """
                        module Beta

                        public unsafe ffi asm(x86_64) fn i32[min max] Identity(i32[min max] value)
                            in("rax") value,
                            out("rax") return
                        {
                            ""
                        }
                        """,
                        "/virtual/Beta.stark"
                    )
                ])));

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("; direct-only imported asm declaration omitted: Alpha.Identity", llvm);
        Assert.Contains("; direct-only imported asm declaration omitted: Beta.Identity", llvm);
        Assert.Equal(2, llvm.Split("call i32 asm sideeffect", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("declare i32 @Alpha_Identity", llvm);
        Assert.DoesNotContain("declare i32 @Beta_Identity", llvm);
        Assert.DoesNotContain("call i32 @Alpha_Identity", llvm);
        Assert.DoesNotContain("call i32 @Beta_Identity", llvm);
        Assert.DoesNotContain("declare i32 @Identity", llvm);
    }

    [Fact]
    public void AddressTakenAsmFunctionsKeepAQualifiedBridgeSymbol()
    {
        var result = Compile(
            """
            module Demo

            public unsafe ffi asm(x86_64) fn i64[min max] Identity(i64[min max] value)
                in("rax") value,
                out("rax") return
            {
                ""
            }

            noinline unsafe fn i64[min max] Apply(
                fnptr<unsafe fn i64[min max](i64[min max])> callback,
                i64[min max] value)
            {
                return callback(value);
            }

            unsafe fn i64[min max] Run(i64[min max] value)
            {
                return Apply(Identity, value);
            }
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                QualifyModuleSymbols: true));

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);
        var identityHeader = ExtractDefinitionHeader(llvm, "Demo_Identity");

        Assert.Contains("define dso_local hidden fastcc i64 @Demo_Identity(i64 %arg_value)", llvm);
        Assert.Contains("call fastcc i64 %arg_callback", llvm);
        Assert.DoesNotContain("memory(", identityHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("; direct-only asm definition omitted: Identity", llvm);
    }

    [Fact]
    public void AddressTakenImportedAsmFunctionsMaterializeADeduplicatedBridge()
    {
        var result = Compile(
            """
            import Alpha
            module Demo

            noinline unsafe fn i64[min max] Apply(
                fnptr<unsafe fn i64[min max](i64[min max])> callback,
                i64[min max] value)
            {
                return callback(value);
            }

            unsafe fn i64[min max] Run(i64[min max] value)
            {
                return Apply(Alpha.Identity, value);
            }
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Alpha", "/virtual/Alpha.stark", IsExternal: false),
                        """
                        module Alpha

                        public unsafe ffi asm(x86_64) fn i64[min max] Identity(i64[min max] value)
                            in("rax") value,
                            out("rax") return,
                            memory(none)
                        {
                            ""
                        }
                        """,
                        "/virtual/Alpha.stark"
                    )
                ])));

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("; materialized imported asm bridge: Alpha.Identity", llvm);
        Assert.Contains("$Alpha_Identity = comdat any", llvm);
        var identityHeader = ExtractDefinitionHeader(llvm, "Alpha_Identity");
        Assert.Contains("define linkonce_odr dso_local hidden fastcc i64 @Alpha_Identity(i64 %arg_value)", identityHeader);
        Assert.Contains("memory(none) comdat", identityHeader);
        Assert.DoesNotContain("comdat memory(none)", identityHeader);
        Assert.DoesNotContain("declare i64 @Alpha_Identity", llvm);
    }

    [Fact]
    public void ExportedAsmFunctionsKeepExternallyPreemptableAbiSymbols()
    {
        var result = Compile(
            """
            module Demo

            export unsafe ffi asm(x86_64) fn i64[min max] Identity(i64[min max] value)
                in("rax") value,
                out("rax") return
            {
                ""
            }
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                QualifyModuleSymbols: true));

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define i64 @Identity(i64 %arg_value)", llvm);
        Assert.DoesNotContain("define dso_local i64 @Identity", llvm);
        Assert.DoesNotContain("@Demo_Identity", llvm);
    }

    [Fact]
    public void ExplicitAsmMemoryContractsRemoveTheUniversalMemoryBarrier()
    {
        var result = Compile(
            """
            module Demo

            export unsafe ffi asm(x86_64) fn void Observe(
                u64[min max] length,
                rawptr<u8[min max]>[length] source)
                in("rsi") length,
                in("rdi") source,
                memory(read(source))
            {
                ""
            }

            export unsafe ffi asm(x86_64) fn i64[min max] Identity(i64[min max] value)
                in("rax") value,
                out("rax") return,
                memory(none)
            {
                ""
            }
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                QualifyModuleSymbols: true));

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("define void @Observe(i64 %arg_length, ptr", llvm);
        Assert.Contains("memory(argmem: read)", ExtractDefinitionHeader(llvm, "Observe"));
        Assert.Contains("asm sideeffect \"\", \"{rsi},{rdi},~{dirflag},~{fpsr},~{flags}\"", llvm);
        Assert.Contains("memory(none)", ExtractDefinitionHeader(llvm, "Identity"));
        Assert.Contains("asm sideeffect \"\", \"={rax},0,~{dirflag},~{fpsr},~{flags}\"", llvm);
        Assert.DoesNotContain("~{memory}", llvm);
    }

    [Fact]
    public void TypedOpaqueAsmSymbolReferencesAreRetainedWithoutParsingTheTemplate()
    {
        var result = Compile(
            """
            module Demo

            export fn void Helper()
            {
            }

            export unsafe ffi asm(x86_64) fn void Invoke()
                symbol(Helper),
                memory(none)
            {
                "call Helper"
            }
            """,
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                QualifyModuleSymbols: true));

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = GetLlvm(result);

        Assert.Contains("define void @Helper()", llvm);
        Assert.Contains("@llvm.used = appending global [1 x ptr] [ptr @Helper], section \"llvm.metadata\"", llvm);
        Assert.DoesNotContain("symbol(Helper)", llvm);
    }

    [Fact]
    public void AsmMemoryContractsRequireBoundedMutableInputRegions()
    {
        var result = Compile(
            """
            module Demo

            export unsafe ffi asm(x86_64) fn void InvalidImmutable(
                u64[0 max] length,
                rawptr<u8[min max]>[length] source)
                in("rsi") length,
                in("rdi") source,
                memory(write(source))
            {
                ""
            }

            export unsafe ffi asm(x86_64) fn void InvalidUnbounded(rawmutptr<u8[min max]> destination)
                in("rdi") destination,
                memory(write(destination))
            {
                ""
            }
            """,
            new CompilerOptions(TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK2109"
                && diagnostic.Message.Contains("bounded raw-pointer region", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK2109"
                && diagnostic.Message.Contains("cannot declare writes through immutable rawptr", StringComparison.Ordinal));
    }

    [Fact]
    public void OpaqueAsmSymbolsMustResolveToExactlyOneTypedSymbol()
    {
        var result = Compile(
            """
            module Demo

            export unsafe ffi asm(x86_64) fn void Invoke()
                symbol(Missing),
                memory(none)
            {
                "call Missing"
            }
            """,
            new CompilerOptions(TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == "STK2109"
                && diagnostic.Message.Contains("does not resolve to exactly one accessible function or global", StringComparison.Ordinal));
    }
}
