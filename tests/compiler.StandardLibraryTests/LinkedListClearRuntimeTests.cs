using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class LinkedListClearRuntimeTests : StandardLibraryTestSuite
{
    private const string Program = """
        import System.Collections
        import System.Memory
        module ClearTest

        static mut u32[0 max] DropOrder = 0;

        finite bool Ok(MemoryStatus status)
        {
            switch (status)
            {
                case MemoryStatus.Ok: return true;
                case MemoryStatus.Err(var error): return false;
            }
        }

        struct Plain
        {
            u32[0 max] Number;
            bool Flag;
        }

        struct Resource
        {
            u8[0 9] Id;
            drop { DropOrder = DropOrder * 10 + self.Id; }
        }

        struct Envelope
        {
            Resource Item;
        }

        fn bool CheckCopyable()
        {
            stack mut LinkedList<Plain> list = new();
            for willexit (stack mut u8[0 3] round = 0; round < 3; round += 1)
            {
                if (!Ok(list.ReserveNodes(65))) { return false; }
                for willexit (stack mut u8[0 32] i = 0; i < 32; i += 1)
                {
                    if (!Ok(list.AddLast(new Plain() { Number = i, Flag = true }))) { return false; }
                }
                stack mut Plain value = new Plain() { Number = 999, Flag = false };
                for willexit (stack mut u8[0 8] i = 0; i < 8; i += 1)
                {
                    if (!list.TryRemoveFirst(value) || value.Number != i || !value.Flag) { return false; }
                }
                if (list.Count() != 24) { return false; }
                list.Clear();
                list.Clear();
                if (!list.IsEmpty() || list.Count() != 0 || list.TryRemoveLast(value)) { return false; }
                for willexit (stack mut u8[0 64] i = 0; i < 64; i += 1)
                {
                    if (!Ok(list.AddFirst(new Plain() { Number = (u32[0 max])(i + 64), Flag = false }))) { return false; }
                }
                for willexit (stack mut u8[0 64] i = 0; i < 64; i += 1)
                {
                    if (!list.TryRemoveLast(value) || value.Number != i + 64 || value.Flag) { return false; }
                }
                if (!list.IsEmpty() || list.TryRemoveFirst(value)) { return false; }
                list.Clear();
            }
            return true;
        }

        export fn i32[min max] main()
        {
            if (!CheckCopyable()) { return 1; }
            {
                stack mut LinkedList<Envelope> owned = new();
                if (!Ok(owned.AddLast(new Envelope() { Item = new Resource() { Id = 1 } }))
                    || !Ok(owned.AddFirst(new Envelope() { Item = new Resource() { Id = 2 } }))
                    || !Ok(owned.AddLast(new Envelope() { Item = new Resource() { Id = 3 } }))) { return 2; }
                owned.Clear();
                if (DropOrder != 213 || !owned.IsEmpty()) { return 3; }
                owned.Clear();
                if (DropOrder != 213) { return 4; }
                if (!Ok(owned.AddLast(new Envelope() { Item = new Resource() { Id = 4 } }))) { return 5; }
            }
            if (DropOrder != 2134) { return 6; }
            return 0;
        }
        """;

    [Fact]
    public async Task ClearReusesCopyableStorageAndPreservesNestedOwnedDropOrder()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-linked-list-clear-");
        try
        {
            var input = Path.Combine(directory.FullName, "App.stark");
            var output = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await File.WriteAllTextAsync(input, Program);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var compile = await CompilerCli.RunAsync(
                [input, "--emit-exe", "--target", target.Triple, "--no-stark-path", "-I", Path.Combine(FindRepositoryRoot(), "stdlib", "src"), "-o", output],
                new StringReader(string.Empty), stdout, stderr);
            Assert.True(compile == 0, stdout + Environment.NewLine + stderr);
            var execution = await RunProcessWithUtf8StdinAsync(output, directory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
        }
        finally { directory.Delete(recursive: true); }
    }
}
