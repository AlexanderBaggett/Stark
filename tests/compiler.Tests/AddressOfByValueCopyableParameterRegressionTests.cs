using Stark.Compiler;

namespace compiler.Tests;

/// <summary>
/// Regression for STK5002 when the SSA direct-call inliner inlines a multi-block
/// function that takes the address of one of its own by-value
/// <c>where Copyable(T)</c> parameters to form a borrow.
///
/// <para>
/// <c>System.Testing.ContainsElement&lt;T&gt;(borrow T[] values, T expected)</c>
/// borrows the by-value <c>expected</c> parameter to pass it as the second
/// <c>borrow T</c> argument of the compiler-known
/// <c>System.Collections.DictionaryKey.Equals</c> doctrine method. That borrow
/// lowers to an <c>address-of parameter 'expected'</c> node inside the
/// monomorphized (multi-block, because of its loop) <c>ContainsElement&lt;i32&gt;</c>
/// body. When the inliner folds that body into the calling function, it must spill
/// the by-value argument into an addressable slot and remap <c>&amp;expected</c> to
/// it.
/// </para>
///
/// <para>
/// The inliner's address-taken detection used to scan only the candidate's flat
/// <c>Instructions</c> list, which is populated for single-block candidates only;
/// for a multi-block candidate it was empty, so no remapping was created and the
/// inlined <c>&amp;expected</c> referenced a parameter that does not exist in the
/// caller. validate-ssa then rejected it:
/// </para>
/// <code>
///   STK5002 [validate-ssa]: address-of references unknown parameter 'expected'.
///   STK5002 [validate-ssa]: address-of parameter 'expected' is missing ABI user-parameter lowering.
/// </code>
///
/// <para>
/// The source-level workaround was to rebind the parameter into a
/// <c>stack T needle = expected;</c> local first and borrow the local. After the
/// fix (scan all candidate blocks) the direct form lowers, inlines, and validates
/// cleanly, so the workaround is no longer required. This drives the real stdlib
/// <c>System.Testing.ContainsElement</c> through full LLVM IR emission (which runs
/// the inliner before validate-ssa) so a regression fails here.
/// </para>
/// </summary>
public sealed class AddressOfByValueCopyableParameterRegressionTests
{
    [Fact]
    public void InliningAMultiBlockBorrowOfAByValueCopyableParameterValidates()
    {
        var repositoryRoot = FindRepositoryRoot();
        var stdlibRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var targetInfo = new LlvmTargetInfo("x86_64-unknown-linux-gnu", null);

        var tempDirectory = Directory.CreateTempSubdirectory("stark-addressof-byvalue-copyable-param-");
        try
        {
            var sourcePath = Path.Combine(tempDirectory.FullName, "Consumer.stark");

            // Runtime parameters keep the call out of comptime folding, so the
            // generic predicate is actually monomorphized at this call site, then
            // inlined into Probe (where the `&expected` borrow must be remapped).
            var source = """
                import System.Testing
                module Consumer

                public fn bool Probe(borrow i32[min max][] values, i32[min max] needle)
                {
                    return System.Testing.ContainsElement<i32[min max]>(values, needle);
                }
                """;

            var searchDirectories = new[] { tempDirectory.FullName, stdlibRoot };
            var result = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(source, sourcePath),
                new CompilerOptions(
                    EmitLlvmIr: true,
                    TargetInfo: targetInfo,
                    ModuleResolver: new TargetAwareStdLibModuleResolver(
                        new FileSystemModuleResolver(searchDirectories),
                        searchDirectories,
                        targetInfo)));

            Assert.DoesNotContain(
                result.Diagnostics,
                static diagnostic => diagnostic.Code == "STK5002");
            Assert.True(
                result.Succeeded,
                string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Stark.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate the Stark repository root for the address-of-parameter regression test.");
    }
}
