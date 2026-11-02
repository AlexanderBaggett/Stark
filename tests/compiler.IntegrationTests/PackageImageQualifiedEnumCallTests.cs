using System.Diagnostics;
using Stark.Compiler;

namespace compiler.IntegrationTests;

[Collection("SerialToolchain")]
public sealed class PackageImageQualifiedEnumCallTests
{
    private const string WrapBody = """
        {
            stack Qualified.Cases.Status ignored = Qualified.Cases.Status.Err(17);
            stack Item<u32[0 max]> small = Item<u32[0 max]>.Payload(7);
            return Item<T>.Payload(value);
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericBodyPreservesMixedQualifiedEnumCallsWithoutSources(bool compatibilityBody)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-package-qualified-enum-");
        try
        {
            var librarySource = Path.Combine(directory.FullName, "Cases.stark");
            await File.WriteAllTextAsync(librarySource, """
                module Qualified.Cases
                public enum Status { Ok, Err(i32[min max]), }
                """);
            var casesLibrary = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "Cases.lib" : "libCases.a");
            await CompileAsync([librarySource, "--emit-lib", "--target", target.Triple, "-o", casesLibrary]);
            File.Delete(librarySource);
            librarySource = Path.Combine(directory.FullName, "Facade.stark");
            await File.WriteAllTextAsync(librarySource, $$"""
                import Qualified.Cases
                module Facade
                public struct Counter
                {
                    u64[0 max] Value;
                    finite void Read(borrow Counter self, out u64[0 max] result) { result = self.Value; }
                }
                internal finite void WriteWide(out u64[0 max] output) { output = 4294967303; }
                public fn u64[0 max] RoundTrip<T>(T marker)
                {
                    stack Counter counter = new Counter() { Value = 4294967303 };
                    stack mut u64[0 max] result = 0;
                    counter.Read(result);
                    stack mut u64[0 max][2] indexed = { 0, 0 };
                    counter.Read(indexed[1]);
                    if (indexed[0] != 0 || indexed[1] != result) { return 0; }
                    stack mut u64[0 max] direct = 0;
                    WriteWide(direct);
                    if (direct != result) { return 0; }
                    stack fnptr<finite void(out u64[0 max])> writer = WriteWide;
                    writer(indexed[0]);
                    if (indexed[0] != result || indexed[1] != result) { return 0; }
                    return result;
                }
                public finite void SetOut<T>(T input, out T output) { output = input; }
                public finite void SetInit<T>(T input, init T output) { output = input; }
                public fn void UpdateCounter<T>(mut borrow Counter output, u64[0 max] input, T marker) { output.Value = input; }
                public noinline fn u64[0 max] LiteralBoundary<T>(u64[0 max] input, T marker)
                {
                    stack u64[0 max] bound = (u64[0 2 ** 63 - 1])(2 ** 63 - 1);
                    if (input > bound) { return 0; }
                    return bound;
                }
                public noinline fn u64[0 max] ConvertedIncrement<T>(u64[0 2 ** 63 - 1] input, T marker)
                {
                    return (u64[0 max])(input + 1);
                }
                public enum Item<T> { Empty, Payload(T), }
                public fn Item<T> Wrap<T>(T value)
                {{WrapBody}}
                public fn Status Choose<T>(T marker, u8[0 2] mode)
                {
                    if (mode == 0) { return Qualified.Cases.Status.Err(17); }
                    if (mode == 1) { return Status.Err(23); }
                    return Qualified.Cases.Status.Ok;
                }
                public fn Status Reserve<T>(T marker, u64[0 2 ** 63 - 1] count)
                {
                    stack mut dynamic T values = new();
                    if (!values.TryReserve(count) || !values.TryReserve(count))
                    {
                        return Qualified.Cases.Status.Err(17);
                    }
                    return Qualified.Cases.Status.Ok;
                }
                """);
            var library = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            await CompileAsync([librarySource, "--emit-lib", "--target", target.Triple, "-I", directory.FullName, "-o", library]);
            File.Delete(librarySource);
            if (compatibilityBody)
            {
                // Exercise the supported BodyText contract as well as typed
                // bodies; both must retain exact constructor/type identities.
                var package = Path.ChangeExtension(library, ".starkpkg");
                Assert.True(PackageImageLoader.TryLoadManifest(package, out var manifest));
                var facade = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
                var templates = facade.EffectiveGenericTemplates! with
                {
                    Functions = facade.EffectiveGenericTemplates!.Functions.Select(template =>
                        template.QualifiedResolvedName == "Facade.Wrap"
                            ? template with { TypedBody = null, BodyText = WrapBody }
                            : template).ToArray()
                };
                var replacement = facade with
                {
                    GenericTemplates = templates,
                    CompilerSections = facade.CompilerSections is { } sections
                        ? sections with { GenericTemplates = templates }
                        : null
                };
                manifest = manifest with
                {
                    Modules = manifest.Modules.Select(module => module == facade ? replacement : module).ToArray()
                };
                await File.WriteAllTextAsync(package + ".json", manifest.ToJson());
                File.Delete(package);
            }
            var app = Path.Combine(directory.FullName, "App.stark");
            await File.WriteAllTextAsync(app, """
                import Qualified.Cases
                import Facade
                module Consumer
                static mut u32[0 max] DropCount = 0;
                static mut u32[0 max] DropSum = 0;
                struct Tracked
                {
                    u32[0 max] Number;
                    mut drop
                    {
                        if (self.Number != 0) { DropCount += 1; DropSum += self.Number; }
                    }
                }
                finite law bool Check(Status status, i32[min max] expected)
                {
                    switch (status)
                    {
                        case Status.Ok: return expected == 0;
                        case Status.Err(var error): return error == expected;
                    }
                }
                finite law bool CheckItem(Item<u32[0 max]> item)
                {
                    switch (item)
                    {
                        case Item<u32[0 max]>.Empty: return false;
                        case Item<u32[0 max]>.Payload(var value): return value == 39;
                    }
                }
                finite law bool CheckWideItem(Item<u64[0 max]> item)
                {
                    switch (item)
                    {
                        case Item<u64[0 max]>.Empty: return false;
                        case Item<u64[0 max]>.Payload(var value): return value == 4294967303;
                    }
                }
                export fn i32[min max] main()
                {
                    stack u32[0 max] marker = 39;
                    if (!Check(Choose(marker, 0), 17)) { return 1; }
                    if (!Check(Choose(marker, 1), 23)) { return 2; }
                    if (!Check(Choose(marker, 2), 0)) { return 3; }
                    if (!Check(Reserve(marker, 32), 0)) { return 4; }
                    if (!Check(Reserve(marker, 128), 0)) { return 5; }
                    if (!Check(Reserve(marker, (u64[0 2 ** 63 - 1])(2 ** 63 - 1)), 17)) { return 6; }
                    if (!CheckItem(Wrap(marker))) { return 7; }
                    stack u64[0 max] wide = 4294967303;
                    if (!CheckWideItem(Wrap(wide))) { return 8; }
                    if (RoundTrip(marker) != wide) { return 9; }
                    stack mut u64[0 max] output = 0;
                    SetOut(wide, output);
                    if (output != wide) { return 10; }
                    stack mut u64[0 max][2] destinations = { 1, 2 };
                    SetInit(wide, destinations[1]);
                    if (destinations[0] != 1 || destinations[1] != wide) { return 11; }
                    stack mut Counter counter = new Counter();
                    UpdateCounter(counter, wide + 3, marker);
                    if (counter.Value != wide + 3) { return 12; }
                    {
                        stack Tracked input = new Tracked() { Number = 17 };
                        stack mut Tracked transferred = new Tracked() { Number = 0 };
                        SetOut(input, transferred);
                        if (transferred.Number != 17 || DropCount != 0) { return 13; }
                    }
                    if (DropCount != 1 || DropSum != 17) { return 14; }
                    {
                        stack Tracked input = new Tracked() { Number = 23 };
                        stack mut Tracked transferred = new Tracked() { Number = 0 };
                        SetInit(input, transferred);
                        if (transferred.Number != 23 || DropCount != 1) { return 15; }
                    }
                    if (DropCount != 2 || DropSum != 40) { return 16; }
                    if (LiteralBoundary(wide, marker) != (u64[0 max])(2 ** 63 - 1)) { return 17; }
                    if (LiteralBoundary((u64[0 max])(2 ** 63), marker) != 0) { return 18; }
                    if (ConvertedIncrement((u64[0 2 ** 63 - 1])wide, marker) != wide + 1) { return 19; }
                    if (ConvertedIncrement((u64[0 2 ** 63 - 1])(2 ** 63 - 1), marker) != (u64[0 max])(2 ** 63)) { return 20; }
                    return 0;
                }
                """);
            var output = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await CompileAsync([app, "--emit-exe", "--target", target.Triple, "--no-stark-path", "-I", directory.FullName, "-o", output]);
            using var process = Process.Start(new ProcessStartInfo(output)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            })!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"Exit {process.ExitCode}: {stdout}{stderr}");
            Assert.Equal(string.Empty, stdout);
            Assert.Equal(string.Empty, stderr);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task ClosedLawPreservesMixedLocalAndImportedEnumValuesWithoutSources()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        var directory = Directory.CreateTempSubdirectory("stark-package-mixed-enum-values-");
        try
        {
            var cases = Path.Combine(directory.FullName, "Cases.stark");
            await File.WriteAllTextAsync(cases, """
                module External.Cases
                public enum Status { Ok, NoMemory, Misuse, }
                """);
            var casesLibrary = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "Cases.lib" : "libCases.a");
            await CompileAsync([cases, "--emit-lib", "--target", target.Triple, "-o", casesLibrary]);
            File.Delete(cases);

            var facade = Path.Combine(directory.FullName, "Facade.stark");
            await File.WriteAllTextAsync(facade, """
                import External.Cases
                module Facade
                public enum InputError { OutOfMemory, TooLarge, InvalidLayout, }
                public enum Fault { Wrapped(Status), TooLarge, }
                internal finite law Fault FromError(InputError error)
                {
                    switch (error)
                    {
                        case InputError.OutOfMemory: return Fault.Wrapped(Status.NoMemory);
                        case InputError.TooLarge: return Fault.TooLarge;
                        case InputError.InvalidLayout: return Fault.Wrapped(Status.Misuse);
                    }
                }
                public inline finite law Fault Convert(InputError error) { return FromError(error); }
                """);
            var facadeLibrary = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
            await CompileAsync([facade, "--emit-lib", "--target", target.Triple, "-I", directory.FullName, "-o", facadeLibrary]);
            File.Delete(facade);

            var app = Path.Combine(directory.FullName, "App.stark");
            await File.WriteAllTextAsync(app, """
                import External.Cases
                import Facade
                module Consumer
                finite law bool Check(Fault result, Status expected)
                {
                    switch (result)
                    {
                        case Fault.TooLarge: return false;
                        case Fault.Wrapped(var status): return status == expected;
                    }
                }
                export finite i32[min max] main()
                {
                    if (!Check(Convert(InputError.OutOfMemory), Status.NoMemory)) { return 1; }
                    if (Convert(InputError.TooLarge) != Fault.TooLarge) { return 2; }
                    if (!Check(Convert(InputError.InvalidLayout), Status.Misuse)) { return 3; }
                    return 0;
                }
                """);
            var output = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            await CompileAsync([app, "--emit-exe", "--target", target.Triple, "--no-stark-path", "-I", directory.FullName, "-o", output]);
            using var process = Process.Start(new ProcessStartInfo(output)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            })!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"Exit {process.ExitCode}: {stdout}{stderr}");
            Assert.Equal(string.Empty, stdout);
            Assert.Equal(string.Empty, stderr);
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task CompileAsync(string[] arguments)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var result = await CompilerCli.RunAsync(arguments, new StringReader(string.Empty), stdout, stderr);
        Assert.True(result == 0, stdout + Environment.NewLine + stderr);
    }
}
