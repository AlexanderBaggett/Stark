using System.Text;
using System.Text.Json;
using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemIOPathLiteralFactsTests : StandardLibraryTestSuite
{
    [Fact]
    public async Task PackagedLiteralAndRuntimeFactsAgreeAcrossCopiesAndNativeObservers()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target))
        {
            return;
        }
        var windows = OperatingSystem.IsWindows();
        var cases = new (string Path, int[] Fields)[]
        {
            ("", [0, 0, 0, 0, 0, 0, 0, 0]),
            ("alpha/beta.txt", [14, 14, 0, 6, 10, 5, 1, 0]),
            ("alpha/beta/", [11, 10, 0, 6, 6, 5, 0, 0]),
            (".hidden", [7, 7, 0, 0, 0, 0, 0, 0]),
            ("/", [1, 1, 1, 1, 0, 1, 0, windows ? 0 : 1]),
            ("C:file.txt", [10, 10, windows ? 2 : 0, windows ? 2 : 0, 6, windows ? 2 : 0, 1, 0]),
            ("C:/file.txt", [11, 11, windows ? 3 : 0, 3, 7, windows ? 3 : 2, 1, windows ? 1 : 0]),
            ("C:/", [3, 2, windows ? 3 : 0, windows ? 2 : 0, windows ? 1 : 0, windows ? 3 : 0, 0, windows ? 1 : 0]),
            ("C:\\file.txt", [11, 11, windows ? 3 : 0, windows ? 3 : 0, 7, windows ? 3 : 0, 1, windows ? 1 : 0])
        };
        var package = await SharedStdlibPackage.GetDirectoryAsync();
        var directory = Directory.CreateTempSubdirectory("stark-path-literal-facts-");
        try
        {
            var source = Path.Combine(directory.FullName, "App.stark");
            var observer = Path.Combine(directory.FullName, "observer.c");
            var executable = Path.Combine(directory.FullName, windows ? "app.exe" : "app");
            var c = new StringBuilder("""
                #include <stdint.h>
                struct facts {
                    const char *path;
                    int64_t path_length, length, end, root, segment, extension, directory;
                    unsigned char has_extension, absolute;
                };
                static const int64_t expected[][8] = {
                """);
            foreach (var entry in cases)
                c.AppendLine("{" + string.Join(",", entry.Fields) + "},");
            c.AppendLine("};\nstatic const char *paths[] = {");
            foreach (var entry in cases)
                c.AppendLine(JsonSerializer.Serialize(entry.Path) + ",");
            c.AppendLine("""
                };
                int32_t inspect_facts(const struct facts *facts, uint32_t index)
                {
                    const int64_t *want = expected[index];
                    if (facts->path_length != want[0] || facts->length != want[0]
                        || facts->end != want[1] || facts->root != want[2]
                        || facts->segment != want[3] || facts->extension != want[4]
                        || facts->directory != want[5] || (facts->has_extension & 1) != want[6]
                        || (facts->absolute & 1) != want[7]) return 1;
                    for (int64_t i = 0; i < facts->path_length; ++i)
                        if (facts->path[i] != paths[index][i]) return 2;
                    return 0;
                }
                """);
            await File.WriteAllTextAsync(observer, c.ToString());
            var app = new StringBuilder("""
                import System.IO.Path
                module App

                unsafe ffi fn i32[min max] inspect_facts(rawptr<PathFacts> facts, u32[0 max] index);

                export noinline finite law PathFacts RuntimeFacts(ascii path)
                {
                    return GetFacts(path);
                }

                export unsafe fn i32[min max] main()
                {
                """);
            app.AppendLine();
            for (var i = 0; i < cases.Length; i++)
            {
                var literal = JsonSerializer.Serialize(cases[i].Path);
                app.AppendLine($"    stack PathFacts folded{i} = GetFacts({literal});");
                app.AppendLine($"    stack PathFacts copied{i} = folded{i};");
                app.AppendLine($"    stack PathFacts runtime{i} = RuntimeFacts({literal});");
                app.AppendLine($"    if (inspect_facts(&copied{i}, {i}) != 0 || inspect_facts(&runtime{i}, {i}) != 0)");
                app.AppendLine("    {");
                app.AppendLine($"        return {i + 1};");
                app.AppendLine("    }");
            }
            app.AppendLine("    return 0;\n}");
            await File.WriteAllTextAsync(source, app.ToString());
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = await CompilerCli.RunAsync(
                [source, "--emit-exe", "-I", package, "-o", executable, "--target", target.Triple, "--native-source", observer],
                new StringReader(string.Empty), stdout, stderr);
            Assert.True(exit == 0, stdout + Environment.NewLine + stderr);
            var execution = await RunProcessWithUtf8StdinAsync(executable, directory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
