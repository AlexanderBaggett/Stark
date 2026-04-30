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
                import System.Experimental.Net
                import System.Experimental.Net.Tcp
                import System.Experimental.Runtime.Buffer
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

                fn bool CountFailed(System.Net.NetResult<i64[0 max]> result) {
                    switch (result) {
                        case System.Net.NetResult<i64[0 max]>.Ok(var value):
                            return false;
                        case System.Net.NetResult<i64[0 max]>.Err(var error):
                            return true;
                    }
                }

                fn bool ClientFailed(System.Net.NetResult<System.Experimental.Net.Tcp.TcpClient> result) {
                    switch (result) {
                        case System.Net.NetResult<System.Experimental.Net.Tcp.TcpClient>.Ok(var value):
                            return false;
                        case System.Net.NetResult<System.Experimental.Net.Tcp.TcpClient>.Err(var error):
                            return true;
                    }
                }

                fn i32[min max] RunClosedSurface() {
                    stack mut System.Experimental.Net.Tcp.TcpClient client = new();
                    stack mut i8[-128 127][4] rawBuffer = { 1, 2, 3, 4 };
                    stack mut System.Experimental.Runtime.Buffer.FixedByteBuffer512 fixedBuffer = new();
                    stack mut System.Experimental.Runtime.Buffer.DynamicByteBuffer dynamicBuffer = new();

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

                    if (!CountFailed(client.Write(fixedBuffer))) {
                        return 6;
                    }

                    if (StatusOk(client.WaitReadable(0)) || StatusOk(client.WaitWritable(0))) {
                        return 7;
                    }

                    if (StatusOk(client.Shutdown(System.Experimental.Net.Tcp.TcpShutdown.Both))) {
                        return 8;
                    }

                    if (!StatusOk(client.Close())) {
                        return 9;
                    }

                    stack mut System.Experimental.Net.Tcp.TcpListener listener = new();
                    if (listener.IsOpen()) {
                        return 10;
                    }

                    if (!ClientFailed(listener.Accept())) {
                        return 11;
                    }

                    if (StatusOk(listener.WaitReadable(0))) {
                        return 12;
                    }

                    if (!StatusOk(listener.Close())) {
                        return 13;
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

        Assert.Contains("System_Experimental_Net_Tcp", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Experimental_Runtime_Buffer_FixedByteBuffer512_WriteFill", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Runtime_Platform_ReadSocket", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Runtime_Platform_WriteSocket", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Runtime_Platform_WaitReadable", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Runtime_Platform_WaitWritable", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("System_Net_Tcp", llvm, StringComparison.Ordinal);
    }
}
