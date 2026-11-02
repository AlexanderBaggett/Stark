using System.Diagnostics;
using System.Text.RegularExpressions;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class LlvmAddressedAggregateCallTests
{
    [Fact]
    public async Task AddressedCallsAvoidUnusedAggregateLoadsAndPreserveSnapshots()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var target)) return;
        const string source = """
            module AddressedAggregate
            public struct Packet
            {
                public i8[min max][1024] Payload;
                public i32[min max] Marker;
                public noinline finite i32[min max] Touch(mut borrow Packet self)
                {
                    self.Marker = 31;
                    self.Payload[0] = 19;
                    return self.Marker;
                }
            }
            public struct Envelope { public Packet Value; public i32[min max] Tag; }
            public noinline finite law Packet Construct(i8[min max] first, i8[min max] last)
            {
                stack mut Packet packet = new Packet();
                packet.Payload[0] = first;
                packet.Payload[1023] = last;
                packet.Marker = 13;
                return packet;
            }
            public noinline finite law Envelope Wrap(Packet packet)
            {
                return new Envelope() { Value = packet, Tag = 29 };
            }
            noinline finite law i32[min max] Read(borrow Packet packet) { return packet.Marker; }
            noinline finite law i32[min max] ReadPayload(borrow Packet packet)
            {
                return (i32[min max])packet.Payload[0] + (i32[min max])packet.Payload[1023];
            }
            public noinline finite law i32[min max] Forward(Packet packet) { return Read(packet); }
            public noinline finite i32[min max] Mutate(Packet packet)
            {
                stack mut Packet local = packet;
                stack i32[min max] marker = local.Touch();
                return marker + ReadPayload(local);
            }
            public noinline finite law i32[min max] ForwardIndirect(Packet packet, fnptr<finite law i32[min max](borrow Packet)> reader)
            {
                return reader(packet);
            }
            unsafe noinline finite law i32[min max] ReadScalar(borrow i32[min max] value) { return *((rawptr<i32[min max]>)(&value)); }
            export unsafe finite i32[min max] main()
            {
                stack mut Packet packet = new Packet() { Marker = 17 };
                packet.Payload[0] = 5;
                packet.Payload[1023] = -8;
                stack Packet snapshot = packet;
                packet.Marker = 25;
                packet.Payload[1023] = 11;
                if (Forward(packet) != 25 || Read(snapshot) != 17) { return 1; }
                stack fnptr<finite law i32[min max](borrow Packet)> reader = Read;
                if (reader(snapshot) != 17) { return 3; }
                if (ForwardIndirect(snapshot, reader) != 17 || Mutate(packet) != 61 || Read(snapshot) != 17) { return 4; }
                if (ReadPayload(snapshot) != -3 || ReadPayload(packet) != 16) { return 5; }
                stack i32[min max] scalar = 9;
                if (ReadScalar(scalar) != 9) { return 2; }
                stack Envelope envelope = Wrap(Construct(-9, 23));
                if (ReadPayload(envelope.Value) != 14 || Read(envelope.Value) != 13 || envelope.Tag != 29) { return 6; }
                return 0;
            }
            """;
        var result = DefaultCompilerPipeline.Create().Run(new CompilationInput(source),
            new CompilerOptions(EmitLlvmIr: true, TargetInfo: target));
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
        var ir = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        foreach (var name in new[] { "Forward", "ForwardIndirect", "Mutate" })
        {
            var forward = Regex.Match(ir, $@"(?ms)^define[^\n]*@{name}\([^\n]*\n?\{{\n.*?^}}");
            Assert.True(forward.Success, ir);
            Assert.DoesNotContain("load %Packet,", forward.Value, StringComparison.Ordinal);
        }
        foreach (Match function in Regex.Matches(ir, @"(?ms)^define[^\n]*\n?\{\n.*?^}"))
        {
            foreach (Match load in Regex.Matches(function.Value, @"(?m)^\s*(%[\w.$-]+) = load %(?:Packet|Envelope),"))
            {
                var uses = Regex.Matches(function.Value, @"%[\w.$-]+")
                    .Count(token => token.Value == load.Groups[1].Value);
                Assert.True(uses > 1, $"Unused aggregate snapshot: {load.Value}");
            }
        }
        var directory = Directory.CreateTempSubdirectory("stark-addressed-aggregate-");
        try
        {
            var binary = Path.Combine(directory.FullName, OperatingSystem.IsWindows() ? "app.exe" : "app");
            var native = NativeToolchain.EmitExecutable(ir, binary, target);
            Assert.True(native.Succeeded, native.StandardError);
            using var process = Process.Start(new ProcessStartInfo(binary) { UseShellExecute = false });
            Assert.NotNull(process);
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        finally { directory.Delete(recursive: true); }
    }
}
