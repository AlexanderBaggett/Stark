using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemExperimentalNetTcpStandardLibraryTests : StandardLibraryTestSuite
{
    [Fact]
    public void StdLibSourceExperimentalNetTcpSurfaceCompiles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibExperimentalNetTcpSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Net
                import System.Net.Tcp
                import System.Runtime.Buffer
                import System.Net
                module Demo

                fn bool StatusOk(System.Net.NetStatus status) {
                    switch (status) {
                        case System.Net.NetStatus.Ok:
                            return true;
                        case System.Net.NetStatus.Err(var error):
                            return false;
                    }
                }

                fn bool CountFailed(System.Net.NetResult<u64[0 2 ** 63 - 1]> result) {
                    switch (result) {
                        case System.Net.NetResult<u64[0 2 ** 63 - 1]>.Ok(var value):
                            return false;
                        case System.Net.NetResult<u64[0 2 ** 63 - 1]>.Err(var error):
                            return true;
                    }
                }

                fn bool ClientFailed(System.Net.NetResult<System.Net.Tcp.TcpClient> result) {
                    switch (result) {
                        case System.Net.NetResult<System.Net.Tcp.TcpClient>.Ok(var value):
                            return false;
                        case System.Net.NetResult<System.Net.Tcp.TcpClient>.Err(var error):
                            return true;
                    }
                }

                fn i32[min max] RunClosedSurface() {
                    stack mut System.Net.Tcp.TcpClient client = new();
                    stack mut i8[min max][4] rawBuffer = { 1, 2, 3, 4 };
                    stack mut i8[min max][2] rawFirst = { 1, 2 };
                    stack mut i8[min max][2] rawSecond = { 3, 4 };
                    stack mut System.Runtime.Buffer.FixedByteBuffer512 fixedBuffer = new();
                    stack mut System.Runtime.Buffer.DynamicByteBuffer dynamicBuffer = new();

                    if (client.IsOpen()) {
                        return 1;
                    }

                    if (!CountFailed(client.Read(rawBuffer))) {
                        return 2;
                    }

                    if (!CountFailed(client.Read(fixedBuffer))) {
                        return 3;
                    }

                    if (!CountFailed(client.Read(dynamicBuffer, 4))) {
                        return 4;
                    }

                    if (!CountFailed(client.Write(rawBuffer))) {
                        return 5;
                    }

                    if (!CountFailed(client.ReadVectored(rawFirst, rawSecond))) {
                        return 6;
                    }

                    if (!CountFailed(client.WriteVectored(rawFirst, rawSecond))) {
                        return 7;
                    }

                    if (!CountFailed(client.Write(fixedBuffer))) {
                        return 8;
                    }

                    if (StatusOk(client.WaitReadable(0)) || StatusOk(client.WaitWritable(0))) {
                        return 9;
                    }

                    if (StatusOk(client.Shutdown(System.Net.Tcp.TcpShutdown.Both))) {
                        return 10;
                    }

                    if (!StatusOk(client.Close())) {
                        return 11;
                    }

                    stack mut System.Net.Tcp.TcpListener listener = new();
                    if (listener.IsOpen()) {
                        return 12;
                    }

                    if (!ClientFailed(listener.Accept())) {
                        return 13;
                    }

                    if (StatusOk(listener.WaitReadable(0))) {
                        return 14;
                    }

                    if (!StatusOk(listener.Close())) {
                        return 15;
                    }

                    return 0;
                }
                """,
                appPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("System_Net_Tcp", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Runtime_Buffer_FixedByteBuffer512_WriteFill", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Runtime_Platform_ReadSocket", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Runtime_Platform_WriteSocket", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Runtime_Platform_ReadSocketVector2", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Runtime_Platform_WriteSocketVector2", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Runtime_Platform_WaitReadable", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Runtime_Platform_WaitWritable", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("System_Experimental_Net_Tcp", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceExperimentalNetTcpBufferReadsUseBulkPaths()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var modulePath = Path.Combine(sourceRoot, "System", "Net", "Tcp.stark");
        var platformPath = Path.Combine(sourceRoot, "System", "Runtime", "Platform.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(modulePath), modulePath),
            new CompilerOptions(
                OptimizationLevel: CompilerOptimizationLevel.O3,
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        var fixed512Read = ExtractLlvmFunctionBody(llvm, "@TcpClient_Read__mutborrowTcpClient_mutborrowSystem_Runtime_Buffer_FixedByteBuffer512_(");
        var fixed4096Read = ExtractLlvmFunctionBody(llvm, "@TcpClient_Read__mutborrowTcpClient_mutborrowSystem_Runtime_Buffer_FixedByteBuffer4096_(");
        var fixed8192Read = ExtractLlvmFunctionBody(llvm, "@TcpClient_Read__mutborrowTcpClient_mutborrowSystem_Runtime_Buffer_FixedByteBuffer8192_(");
        var dynamicRead = ExtractLlvmFunctionBody(llvm, "@TcpClient_Read__mutborrowTcpClient_mutborrowSystem_Runtime_Buffer_DynamicByteBuffer_u64_0_2__63__1__(");
        var fixedReads = string.Join(Environment.NewLine, [fixed512Read, fixed4096Read, fixed8192Read]);

        Assert.Contains("ptr noundef nonnull noalias nocapture dereferenceable(528) align 8 %arg_destination", fixed512Read, StringComparison.Ordinal);
        Assert.Contains("System_Runtime_Platform_ReadSocket", fixedReads, StringComparison.Ordinal);
        Assert.Contains("System_Runtime_Platform_ReadSocketVector2", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Runtime_Platform_WriteSocketVector2", llvm, StringComparison.Ordinal);
        Assert.Contains("FixedByteBuffer512_WriteSlice__mutborrowFixedByteBuffer512_", fixed512Read, StringComparison.Ordinal);
        Assert.Contains("FixedByteBuffer512_AdvanceWrite", fixed512Read, StringComparison.Ordinal);
        Assert.Contains("FixedByteBuffer4096_WriteSlice__mutborrowFixedByteBuffer4096_", fixed4096Read, StringComparison.Ordinal);
        Assert.Contains("FixedByteBuffer4096_AdvanceWrite", fixed4096Read, StringComparison.Ordinal);
        Assert.Contains("FixedByteBuffer8192_WriteSlice__mutborrowFixedByteBuffer8192_", fixed8192Read, StringComparison.Ordinal);
        Assert.Contains("FixedByteBuffer8192_AdvanceWrite", fixed8192Read, StringComparison.Ordinal);
        Assert.DoesNotContain("FixedByteBuffer512_WriteByte", fixed512Read, StringComparison.Ordinal);
        Assert.DoesNotContain("FixedByteBuffer4096_WriteByte", fixed4096Read, StringComparison.Ordinal);
        Assert.DoesNotContain("FixedByteBuffer8192_WriteByte", fixed8192Read, StringComparison.Ordinal);
        Assert.DoesNotContain("alloca [512 x i8]", fixed512Read, StringComparison.Ordinal);
        Assert.DoesNotContain("alloca [4096 x i8]", fixed4096Read, StringComparison.Ordinal);
        Assert.DoesNotContain("alloca [8192 x i8]", fixed8192Read, StringComparison.Ordinal);

        Assert.Contains("System_Runtime_Platform_ReadSocket", dynamicRead, StringComparison.Ordinal);
        Assert.Contains("DynamicByteBuffer_WriteSlice", dynamicRead, StringComparison.Ordinal);
        Assert.DoesNotContain("DynamicByteBuffer_WriteByte", dynamicRead, StringComparison.Ordinal);

        var platformSource = File.ReadAllText(platformPath);
        Assert.Contains("rawmutptr<i8[min max]>[capacity] buffer", platformSource, StringComparison.Ordinal);
        Assert.Contains("rawptr<i8[min max]>[length] buffer", platformSource, StringComparison.Ordinal);
    }

    private static string ExtractLlvmFunctionBody(string llvm, string functionSignatureFragment)
    {
        var signatureIndex = llvm.IndexOf(functionSignatureFragment, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Unable to find LLVM function fragment '{functionSignatureFragment}'.");

        var start = llvm.LastIndexOf("\ndefine ", signatureIndex, StringComparison.Ordinal);
        if (start < 0)
        {
            start = llvm.LastIndexOf("define ", signatureIndex, StringComparison.Ordinal);
        }

        Assert.True(start >= 0, $"Unable to find LLVM function start for '{functionSignatureFragment}'.");

        var next = llvm.IndexOf("\ndefine ", signatureIndex + functionSignatureFragment.Length, StringComparison.Ordinal);
        if (next < 0)
        {
            next = llvm.Length;
        }

        return llvm[start..next];
    }
}
