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

        [StructLayout(C), Align(64)]
        struct Plain
        {
            u32[0 max] Number;
            bool Flag;
        }

        struct Resource
        {
            u8[0 9] Id;
            drop { if (self.Id != 0) { DropOrder = DropOrder * 10 + self.Id; } }
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

        fn bool CheckTransitions()
        {
            stack mut LinkedList<u32[0 max]> list = new();
            stack mut u32[0 max][64] expected = {0};
            stack mut u8[0 64] length = 0;
            for willexit (stack mut u16[0 512] step = 0; step < 512; step += 1)
            {
                stack u64[0 max] action = (step * 17 + step / 7) % 4;
                stack u32[0 max] input = (u32[0 max])(step * 257 + 31);
                if (length == 0 || (action < 2 && length < 64))
                {
                    if (action == 0)
                    {
                        if (!Ok(list.AddFirst(input))) { return false; }
                        stack mut u8[0 64] index = length;
                        while willexit (index > 0)
                        {
                            expected[index] = expected[index - 1];
                            index -= 1;
                        }
                        expected[0] = input;
                    }
                    else
                    {
                        if (!Ok(list.AddLast(input))) { return false; }
                        expected[length] = input;
                    }
                    length += 1;
                }
                else
                {
                    stack mut u32[0 max] actual = 123456;
                    if (action == 2)
                    {
                        if (!list.TryRemoveFirst(actual) || actual != expected[0]) { return false; }
                        stack mut u8[0 64] index = 1;
                        while willexit (index < length)
                        {
                            expected[index - 1] = expected[index];
                            index += 1;
                        }
                    }
                    else if (!list.TryRemoveLast(actual) || actual != expected[length - 1]) { return false; }
                    length -= 1;
                }
                if (list.Count() != length || list.IsEmpty() != (length == 0)) { return false; }
                if (step % 31 == 0 && !Ok(list.ReserveNodes(70))) { return false; }
            }
            if (Ok(list.ReserveNodes((u64[0 2 ** 63 - 1])(2 ** 63 - 1))) || list.Count() != length) { return false; }
            stack mut u32[0 max] actual = 0;
            while willexit (length > 0)
            {
                if (!list.TryRemoveLast(actual) || actual != expected[length - 1]) { return false; }
                length -= 1;
            }
            actual = 987654;
            return list.IsEmpty() && !list.TryRemoveFirst(actual) && !list.TryRemoveLast(actual);
        }

        export fn i32[min max] main()
        {
            if (!CheckCopyable()) { return 1; }
            if (!CheckTransitions()) { return 14; }
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
            DropOrder = 0;
            {
                stack mut LinkedList<Envelope> owned = new();
                if (!Ok(owned.AddLast(new Envelope() { Item = new Resource() { Id = 1 } }))
                    || !Ok(owned.AddLast(new Envelope() { Item = new Resource() { Id = 2 } }))) { return 7; }
                {
                    stack mut Envelope removed = new Envelope() { Item = new Resource() { Id = 0 } };
                    if (!owned.TryRemoveFirst(removed) || removed.Item.Id != 1 || DropOrder != 0) { return 8; }
                }
                if (DropOrder != 1) { return 9; }
                if (!Ok(owned.AddFirst(new Envelope() { Item = new Resource() { Id = 3 } }))) { return 10; }
                {
                    stack mut Envelope removed = new Envelope() { Item = new Resource() { Id = 0 } };
                    if (!owned.TryRemoveLast(removed) || removed.Item.Id != 2 || DropOrder != 1) { return 11; }
                }
                if (DropOrder != 12 || owned.Count() != 1) { return 12; }
            }
            if (DropOrder != 123) { return 13; }
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

    [Fact]
    public async Task FailedFirstAllocationDropsOwnedInputAndKeepsListEmpty()
    {
        if (!OperatingSystem.IsLinux() || !NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-linked-list-failure-");
        try
        {
            var input = Path.Combine(directory.FullName, "Failure.stark");
            var native = Path.Combine(directory.FullName, "FailMappings.c");
            var output = Path.Combine(directory.FullName, "failure");
            await File.WriteAllTextAsync(input, """
                import System.Collections
                import System.Memory
                module Failure
                static mut u32[0 max] Drops = 0;
                struct Resource { u8[0 max] Id; drop { Drops += self.Id; } }
                struct Envelope { Resource Item; }
                unsafe ffi(c) fn i32[min max] deny_mappings();
                export unsafe fn i32[min max] main()
                {
                    if (deny_mappings() != 0) return 77;
                    {
                        stack mut LinkedList<Envelope> list = new();
                        stack MemoryStatus status = list.AddLast(new Envelope() { Item = new Resource() { Id = 1 } });
                        switch (status)
                        {
                            case MemoryStatus.Ok: return 1;
                            case MemoryStatus.Err(var error):
                                if (error != MemoryError.OutOfMemory) return 2;
                        }
                        if (!list.IsEmpty() || list.Count() != 0 || Drops != 1) return 3;
                    }
                    if (Drops != 1) return 4;
                    return 0;
                }
                """);
            await File.WriteAllTextAsync(native, """
                #include <errno.h>
                #include <stddef.h>
                #include <linux/filter.h>
                #include <linux/seccomp.h>
                #include <sys/prctl.h>
                #include <sys/syscall.h>
                int deny_mappings(void) {
                    struct sock_filter code[] = {
                        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, nr)),
                        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, SYS_mmap, 0, 1),
                        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ERRNO | ENOMEM),
                        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ALLOW)
                    };
                    struct sock_fprog filter = { .len = sizeof(code) / sizeof(code[0]), .filter = code };
                    if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0)) return -1;
                    return prctl(PR_SET_SECCOMP, SECCOMP_MODE_FILTER, &filter);
                }
                """);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var compile = await CompilerCli.RunAsync(
                [input, "--emit-exe", "--target", target.Triple, "--native-source", native,
                 "--no-stark-path", "-I", Path.Combine(FindRepositoryRoot(), "stdlib", "src"), "-o", output],
                new StringReader(string.Empty), stdout, stderr);
            Assert.True(compile == 0, stdout + Environment.NewLine + stderr);
            var execution = await RunProcessWithUtf8StdinAsync(output, directory.FullName, string.Empty);
            // Some runners prohibit installing a seccomp filter even with no_new_privs.
            if (execution.ExitCode == 77) return;
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
        }
        finally { directory.Delete(recursive: true); }
    }
}
