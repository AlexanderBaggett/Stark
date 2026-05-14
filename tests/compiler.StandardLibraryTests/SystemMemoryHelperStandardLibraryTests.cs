using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemMemoryHelperStandardLibraryTests : StandardLibraryTestSuite
{
    private const string MemoryProgram = """
        import System.Memory
        module App

        fn bool Ok(MemoryStatus status) {
            switch (status) {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn bool InitialBytesOk(borrow i8[min max][] values) {
            return values[0] == 1 && values[3] == 4;
        }

        fn bool FilledBytesOk(borrow i8[min max][] values) {
            return values[4] == 9 && values[7] == 9;
        }

        fn bool OverwrittenBytesOk(borrow i8[min max][] values) {
            return values[0] == 7 && values[1] == 7;
        }

        fn bool InitialCodePointsOk(borrow i32[min max][] values) {
            return values[0] == 65 && values[7] == 90;
        }

        fn bool CopiedBytesOk(borrow i8[min max][] values) {
            return values[2] == 1 && values[3] == 2 && values[4] == 3 && values[5] == 4;
        }

        fn bool MovedBytesOk(borrow i8[min max][] values) {
            return values[0] == 3 && values[1] == 4 && values[2] == 5 && values[3] == 6;
        }

        fn bool CopiedCodePointsOk(borrow i32[min max][] values) {
            return values[2] == 65 && values[3] == 66 && values[4] == 67 && values[5] == 68;
        }

        fn bool MovedCodePointsOk(borrow i32[min max][] values) {
            return values[0] == 67 && values[1] == 68 && values[2] == 69 && values[3] == 70;
        }

        export unsafe fn i32[min max] main() {
            if (!System.Memory.SupportsDynamicAllocator(Allocator.Default())) {
                return 1;
            }

            stack Allocator customAllocator = new Allocator() {
                Kind = 1
            };
            if (System.Memory.SupportsDynamicAllocator(customAllocator)) {
                return 2;
            }

            stack mut dynamic i8[min max] bytes = new();
            stack mut i8[min max][4] sourceBytes = { 1, 2, 3, 4 };
            stack u64[0 2 ** 63 - 1] two = 2;
            stack u64[0 2 ** 63 - 1] four = 4;
            stack u64[0 2 ** 63 - 1] eight = 8;
            if (!Ok(System.Memory.ReserveBytes(bytes, eight))) {
                return 3;
            }

            if (bytes.Length != 0) {
                return 20;
            }

            if (bytes.Capacity < 8) {
                return 21;
            }

            if (!Ok(System.Memory.AppendBytes(bytes, sourceBytes, four))) {
                return 4;
            }

            if (bytes.Length != 4 || !InitialBytesOk(bytes[0, bytes.Length])) {
                return 5;
            }

            if (!Ok(System.Memory.AppendFillBytes(bytes, 9, four))) {
                return 6;
            }

            if (bytes.Length != 8 || !FilledBytesOk(bytes[0, bytes.Length])) {
                return 7;
            }

            if (!Ok(System.Memory.FillInitializedBytes(bytes[0, two], 7, two))) {
                return 8;
            }

            if (!OverwrittenBytesOk(bytes[0, bytes.Length])) {
                return 9;
            }

            stack u64[0 2 ** 63 - 1] aliasedByteCount = bytes.Length;
            if (!Ok(System.Memory.AppendBytes(bytes, bytes[0, aliasedByteCount], aliasedByteCount))) {
                return 10;
            }

            stack i8[min max][] aliasedBytes = bytes[0, bytes.Length];
            if (bytes.Length != 16 || aliasedBytes[8] != aliasedBytes[0] || aliasedBytes[15] != aliasedBytes[7]) {
                return 11;
            }

            stack mut dynamic i32[min max] codePoints = new();
            stack mut i32[min max][4] sourceCodePoints = { 65, 66, 67, 68 };
            if (!Ok(System.Memory.ReserveCodePoints(codePoints, eight))) {
                return 13;
            }

            if (codePoints.Length != 0) {
                return 22;
            }

            if (codePoints.Capacity < 8) {
                return 23;
            }

            if (!Ok(System.Memory.AppendCodePoints(codePoints, sourceCodePoints, four))) {
                return 14;
            }

            if (!Ok(System.Memory.AppendFillCodePoints(codePoints, 90, four))) {
                return 15;
            }

            if (codePoints.Length != 8 || !InitialCodePointsOk(codePoints[0, codePoints.Length])) {
                return 16;
            }

            stack u64[0 2 ** 63 - 1] aliasedCodePointCount = codePoints.Length;
            if (!Ok(System.Memory.AppendCodePoints(codePoints, codePoints[0, aliasedCodePointCount], aliasedCodePointCount))) {
                return 17;
            }

            stack i32[min max][] aliasedCodePoints = codePoints[0, codePoints.Length];
            if (codePoints.Length != 16 || aliasedCodePoints[8] != aliasedCodePoints[0] || aliasedCodePoints[15] != aliasedCodePoints[7]) {
                return 18;
            }

            stack mut dynamic i8[min max] byteMoveBuffer = new();
            if (!Ok(System.Memory.ReserveBytes(byteMoveBuffer, eight))) {
                return 24;
            }

            for willexit (stack mut u64[0 2 ** 63 - 1] byteMoveIndex = 0; byteMoveIndex < eight; byteMoveIndex += 1) {
                init byteMoveBuffer[byteMoveBuffer.Length] = (i8[min max])(byteMoveIndex + 1);
            }

            if (!Ok(System.Memory.CopyBytes(byteMoveBuffer[0, four], byteMoveBuffer[two, four], four))) {
                return 25;
            }

            if (!CopiedBytesOk(byteMoveBuffer[0, byteMoveBuffer.Length])) {
                return 26;
            }

            stack mut dynamic i8[min max] byteMoveOnlyBuffer = new();
            if (!Ok(System.Memory.ReserveBytes(byteMoveOnlyBuffer, eight))) {
                return 27;
            }

            for willexit (stack mut u64[0 2 ** 63 - 1] byteMoveOnlyIndex = 0; byteMoveOnlyIndex < eight; byteMoveOnlyIndex += 1) {
                init byteMoveOnlyBuffer[byteMoveOnlyBuffer.Length] = (i8[min max])(byteMoveOnlyIndex + 1);
            }

            if (!Ok(System.Memory.MoveBytes(byteMoveOnlyBuffer[two, four], byteMoveOnlyBuffer[0, four], four))) {
                return 27;
            }

            if (!MovedBytesOk(byteMoveOnlyBuffer[0, byteMoveOnlyBuffer.Length])) {
                return 28;
            }

            stack mut dynamic i32[min max] codePointMoveBuffer = new();
            if (!Ok(System.Memory.ReserveCodePoints(codePointMoveBuffer, eight))) {
                return 29;
            }

            for willexit (stack mut u64[0 2 ** 63 - 1] codePointMoveIndex = 0; codePointMoveIndex < eight; codePointMoveIndex += 1) {
                init codePointMoveBuffer[codePointMoveBuffer.Length] =
                    (i32[min max])(65 + codePointMoveIndex);
            }

            if (!Ok(System.Memory.CopyCodePoints(codePointMoveBuffer[0, four], codePointMoveBuffer[two, four], four))) {
                return 30;
            }

            if (!CopiedCodePointsOk(codePointMoveBuffer[0, codePointMoveBuffer.Length])) {
                return 31;
            }

            stack mut dynamic i32[min max] codePointMoveOnlyBuffer = new();
            if (!Ok(System.Memory.ReserveCodePoints(codePointMoveOnlyBuffer, eight))) {
                return 32;
            }

            for willexit (stack mut u64[0 2 ** 63 - 1] codePointMoveOnlyIndex = 0; codePointMoveOnlyIndex < eight; codePointMoveOnlyIndex += 1) {
                init codePointMoveOnlyBuffer[codePointMoveOnlyBuffer.Length] =
                    (i32[min max])(65 + codePointMoveOnlyIndex);
            }

            if (!Ok(System.Memory.MoveCodePoints(codePointMoveOnlyBuffer[two, four], codePointMoveOnlyBuffer[0, four], four))) {
                return 32;
            }

            if (!MovedCodePointsOk(codePointMoveOnlyBuffer[0, codePointMoveOnlyBuffer.Length])) {
                return 33;
            }

            return 0;
        }
        """;

    private const string MemoryCopyProgram = """
        import System.Memory
        module CopyApp

        fn bool Ok(MemoryStatus status) {
            switch (status) {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn i64[min max] SumBytes(borrow i8[min max][] values, u64[0 2 ** 63 - 1] count) {
            stack mut i64[min max] checksum = 0;
            for willexit (stack mut u64[0 2 ** 63 - 1] index = 0; index < count; index += 1) {
                checksum += (i64[min max])values[index];
            }

            return checksum;
        }

        fn i64[min max] SumCodePoints(borrow i32[min max][] values, u64[0 2 ** 63 - 1] count) {
            stack mut i64[min max] checksum = 0;
            for willexit (stack mut u64[0 2 ** 63 - 1] index = 0; index < count; index += 1) {
                checksum += (i64[min max])values[index];
            }

            return checksum;
        }

        export fn i32[min max] main() {
            stack u64[0 2 ** 63 - 1] count = 32;
            stack mut i8[min max][32] byteSource = {
                3, 3, 3, 3, 3, 3, 3, 3,
                3, 3, 3, 3, 3, 3, 3, 3,
                3, 3, 3, 3, 3, 3, 3, 3,
                3, 3, 3, 3, 3, 3, 3, 3
            };
            stack mut i8[min max][32] byteDestination = {
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0
            };
            stack mut i32[min max][32] codePointSource = {
                65, 65, 65, 65, 65, 65, 65, 65,
                65, 65, 65, 65, 65, 65, 65, 65,
                65, 65, 65, 65, 65, 65, 65, 65,
                65, 65, 65, 65, 65, 65, 65, 65
            };
            stack mut i32[min max][32] codePointDestination = {
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0
            };

            if (!Ok(System.Memory.CopyBytesDisjoint(byteSource, byteDestination, count))) {
                return 1;
            }

            stack mut i64[min max] checksum = SumBytes(byteDestination, count);
            if (!Ok(System.Memory.FillInitializedBytes(byteDestination, 7, count))) {
                return 2;
            }
            checksum += SumBytes(byteDestination, count);

            if (!Ok(System.Memory.CopyCodePointsDisjoint(codePointSource, codePointDestination, count))) {
                return 3;
            }
            checksum += SumCodePoints(codePointDestination, count);

            if (!Ok(System.Memory.FillInitializedCodePoints(codePointDestination, 90, count))) {
                return 4;
            }
            checksum += SumCodePoints(codePointDestination, count);

            if (checksum != 5280) {
                return 5;
            }

            return 0;
        }
        """;

    private const string MemoryMoveProgram = """
        import System.Memory
        module MoveApp

        fn bool Ok(MemoryStatus status) {
            switch (status) {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn MemoryStatus SeedBytes(mut borrow dynamic i8[min max] values, u64[0 2 ** 63 - 1] count) {
            if (!values.TryReserve(count)) {
                return MemoryStatus.Err(MemoryError.OutOfMemory);
            }

            for willexit (stack mut u64[0 2 ** 63 - 1] index = 0; index < count; index += 1) {
                init values[values.Length] = (i8[min max])(index + 1);
            }

            return MemoryStatus.Ok;
        }

        fn MemoryStatus SeedCodePoints(mut borrow dynamic i32[min max] values, u64[0 2 ** 63 - 1] count) {
            if (!values.TryReserve(count)) {
                return MemoryStatus.Err(MemoryError.OutOfMemory);
            }

            for willexit (stack mut u64[0 2 ** 63 - 1] index = 0; index < count; index += 1) {
                init values[values.Length] = (i32[min max])(65 + index);
            }

            return MemoryStatus.Ok;
        }

        fn i32[min max] CheckByteMoves() {
            stack u64[0 2 ** 63 - 1] zero = 0;
            stack u64[0 2 ** 63 - 1] two = 2;
            stack u64[0 2 ** 63 - 1] four = 4;
            stack u64[0 2 ** 63 - 1] eight = 8;

            stack mut dynamic i8[min max] copyThenMove = new();
            if (!Ok(SeedBytes(copyThenMove, eight))) {
                return 1;
            }

            if (!Ok(System.Memory.CopyBytes(copyThenMove[zero, four], copyThenMove[two, four], four))) {
                return 2;
            }

            if (!Ok(System.Memory.MoveBytes(copyThenMove[two, four], copyThenMove[zero, four], four))) {
                return 3;
            }

            stack i8[min max][] copyThenMoveValues = copyThenMove[zero, copyThenMove.Length];
            if (copyThenMoveValues[0] != 1 || copyThenMoveValues[1] != 2 || copyThenMoveValues[2] != 3 || copyThenMoveValues[3] != 4 || copyThenMoveValues[4] != 3 || copyThenMoveValues[5] != 4) {
                return 4;
            }

            stack mut dynamic i8[min max] forwardOverlap = new();
            if (!Ok(SeedBytes(forwardOverlap, eight))) {
                return 5;
            }

            if (!Ok(System.Memory.MoveBytes(forwardOverlap[two, four], forwardOverlap[zero, four], four))) {
                return 6;
            }

            stack i8[min max][] forwardOverlapValues = forwardOverlap[zero, forwardOverlap.Length];
            if (forwardOverlapValues[0] != 3 || forwardOverlapValues[1] != 4 || forwardOverlapValues[2] != 5 || forwardOverlapValues[3] != 6 || forwardOverlapValues[4] != 5 || forwardOverlapValues[5] != 6) {
                return 7;
            }

            stack mut dynamic i8[min max] backwardOverlap = new();
            if (!Ok(SeedBytes(backwardOverlap, eight))) {
                return 8;
            }

            if (!Ok(System.Memory.MoveBytes(backwardOverlap[zero, four], backwardOverlap[two, four], four))) {
                return 9;
            }

            stack i8[min max][] backwardOverlapValues = backwardOverlap[zero, backwardOverlap.Length];
            if (backwardOverlapValues[0] != 1 || backwardOverlapValues[1] != 2 || backwardOverlapValues[2] != 1 || backwardOverlapValues[3] != 2 || backwardOverlapValues[4] != 3 || backwardOverlapValues[5] != 4 || backwardOverlapValues[6] != 7 || backwardOverlapValues[7] != 8) {
                return 10;
            }

            stack mut dynamic i8[min max] sameRange = new();
            if (!Ok(SeedBytes(sameRange, eight))) {
                return 11;
            }

            if (!Ok(System.Memory.MoveBytes(sameRange[zero, four], sameRange[zero, four], four))) {
                return 12;
            }

            stack i8[min max][] sameRangeValues = sameRange[zero, sameRange.Length];
            if (sameRangeValues[0] != 1 || sameRangeValues[1] != 2 || sameRangeValues[2] != 3 || sameRangeValues[3] != 4 || sameRangeValues[7] != 8) {
                return 13;
            }

            stack mut dynamic i8[min max] disjointMove = new();
            if (!Ok(SeedBytes(disjointMove, eight))) {
                return 14;
            }

            if (!Ok(System.Memory.MoveBytes(disjointMove[zero, two], disjointMove[four, two], two))) {
                return 15;
            }

            stack i8[min max][] disjointMoveValues = disjointMove[zero, disjointMove.Length];
            if (disjointMoveValues[0] != 1 || disjointMoveValues[1] != 2 || disjointMoveValues[4] != 1 || disjointMoveValues[5] != 2 || disjointMoveValues[6] != 7 || disjointMoveValues[7] != 8) {
                return 16;
            }

            stack mut dynamic i8[min max] zeroMove = new();
            if (!Ok(SeedBytes(zeroMove, eight))) {
                return 17;
            }

            if (!Ok(System.Memory.MoveBytes(zeroMove[zero, zero], zeroMove[two, zero], zero))) {
                return 18;
            }

            stack i8[min max][] zeroMoveValues = zeroMove[zero, zeroMove.Length];
            if (zeroMoveValues[0] != 1 || zeroMoveValues[1] != 2 || zeroMoveValues[2] != 3 || zeroMoveValues[7] != 8) {
                return 19;
            }

            return 0;
        }

        fn i32[min max] CheckCodePointMoves() {
            stack u64[0 2 ** 63 - 1] zero = 0;
            stack u64[0 2 ** 63 - 1] two = 2;
            stack u64[0 2 ** 63 - 1] four = 4;
            stack u64[0 2 ** 63 - 1] eight = 8;

            stack mut dynamic i32[min max] copyThenMove = new();
            if (!Ok(SeedCodePoints(copyThenMove, eight))) {
                return 1;
            }

            if (!Ok(System.Memory.CopyCodePoints(copyThenMove[zero, four], copyThenMove[two, four], four))) {
                return 2;
            }

            if (!Ok(System.Memory.MoveCodePoints(copyThenMove[two, four], copyThenMove[zero, four], four))) {
                return 3;
            }

            stack i32[min max][] copyThenMoveValues = copyThenMove[zero, copyThenMove.Length];
            if (copyThenMoveValues[0] != 65 || copyThenMoveValues[1] != 66 || copyThenMoveValues[2] != 67 || copyThenMoveValues[3] != 68 || copyThenMoveValues[4] != 67 || copyThenMoveValues[5] != 68) {
                return 4;
            }

            stack mut dynamic i32[min max] forwardOverlap = new();
            if (!Ok(SeedCodePoints(forwardOverlap, eight))) {
                return 5;
            }

            if (!Ok(System.Memory.MoveCodePoints(forwardOverlap[two, four], forwardOverlap[zero, four], four))) {
                return 6;
            }

            stack i32[min max][] forwardOverlapValues = forwardOverlap[zero, forwardOverlap.Length];
            if (forwardOverlapValues[0] != 67 || forwardOverlapValues[1] != 68 || forwardOverlapValues[2] != 69 || forwardOverlapValues[3] != 70 || forwardOverlapValues[4] != 69 || forwardOverlapValues[5] != 70) {
                return 7;
            }

            stack mut dynamic i32[min max] backwardOverlap = new();
            if (!Ok(SeedCodePoints(backwardOverlap, eight))) {
                return 8;
            }

            if (!Ok(System.Memory.MoveCodePoints(backwardOverlap[zero, four], backwardOverlap[two, four], four))) {
                return 9;
            }

            stack i32[min max][] backwardOverlapValues = backwardOverlap[zero, backwardOverlap.Length];
            if (backwardOverlapValues[0] != 65 || backwardOverlapValues[1] != 66 || backwardOverlapValues[2] != 65 || backwardOverlapValues[3] != 66 || backwardOverlapValues[4] != 67 || backwardOverlapValues[5] != 68 || backwardOverlapValues[6] != 71 || backwardOverlapValues[7] != 72) {
                return 10;
            }

            stack mut dynamic i32[min max] sameRange = new();
            if (!Ok(SeedCodePoints(sameRange, eight))) {
                return 11;
            }

            if (!Ok(System.Memory.MoveCodePoints(sameRange[zero, four], sameRange[zero, four], four))) {
                return 12;
            }

            stack i32[min max][] sameRangeValues = sameRange[zero, sameRange.Length];
            if (sameRangeValues[0] != 65 || sameRangeValues[1] != 66 || sameRangeValues[2] != 67 || sameRangeValues[3] != 68 || sameRangeValues[7] != 72) {
                return 13;
            }

            stack mut dynamic i32[min max] disjointMove = new();
            if (!Ok(SeedCodePoints(disjointMove, eight))) {
                return 14;
            }

            if (!Ok(System.Memory.MoveCodePoints(disjointMove[zero, two], disjointMove[four, two], two))) {
                return 15;
            }

            stack i32[min max][] disjointMoveValues = disjointMove[zero, disjointMove.Length];
            if (disjointMoveValues[0] != 65 || disjointMoveValues[1] != 66 || disjointMoveValues[4] != 65 || disjointMoveValues[5] != 66 || disjointMoveValues[6] != 71 || disjointMoveValues[7] != 72) {
                return 16;
            }

            stack mut dynamic i32[min max] zeroMove = new();
            if (!Ok(SeedCodePoints(zeroMove, eight))) {
                return 17;
            }

            if (!Ok(System.Memory.MoveCodePoints(zeroMove[zero, zero], zeroMove[two, zero], zero))) {
                return 18;
            }

            stack i32[min max][] zeroMoveValues = zeroMove[zero, zeroMove.Length];
            if (zeroMoveValues[0] != 65 || zeroMoveValues[1] != 66 || zeroMoveValues[2] != 67 || zeroMoveValues[7] != 72) {
                return 19;
            }

            return 0;
        }

        export fn i32[min max] main() {
            stack i32[min max] byteStatus = CheckByteMoves();
            if (byteStatus != 0) {
                return byteStatus;
            }

            stack i32[min max] codePointStatus = CheckCodePointMoves();
            if (codePointStatus != 0) {
                return codePointStatus + 100;
            }

            return 0;
        }
        """;

    private const string MemoryAppendDisjointProgram = """
        import System.Memory
        module AppendDisjointApp

        fn bool Ok(MemoryStatus status) {
            switch (status) {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        export unsafe fn i32[min max] main() {
            stack u64[0 2 ** 63 - 1] four = 4;
            stack mut i8[min max][4] byteSource = { 1, 2, 3, 4 };
            stack mut dynamic i8[min max] bytes = new();
            unsafe {
                if (!Ok(System.Memory.AppendBytesDisjoint(bytes, byteSource, four))) {
                    return 1;
                }
            }

            if (bytes.Length != 4) {
                return 2;
            }

            stack i8[min max][] byteView = bytes[0, bytes.Length];
            if (byteView[0] != 1 || byteView[1] != 2 || byteView[2] != 3 || byteView[3] != 4) {
                return 3;
            }

            stack mut i32[min max][4] codePointSource = { 65, 66, 67, 68 };
            stack mut dynamic i32[min max] codePoints = new();
            unsafe {
                if (!Ok(System.Memory.AppendCodePointsDisjoint(codePoints, codePointSource, four))) {
                    return 4;
                }
            }

            if (codePoints.Length != 4) {
                return 5;
            }

            stack i32[min max][] codePointView = codePoints[0, codePoints.Length];
            if (codePointView[0] != 65 || codePointView[1] != 66 || codePointView[2] != 67 || codePointView[3] != 68) {
                return 6;
            }

            return 0;
        }
        """;

    [Fact]
    public void StdLibSourceMemorySurfaceCompilesAndLowersIntrinsics()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibMemorySurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Memory
                module Demo

                fn MemoryStatus InitBytes(
                    borrow i8[min max][] source,
                    init i8[min max][] destination,
                    u64[0 2 ** 63 - 1] count) {
                    return System.Memory.InitializeBytesDisjoint(source, destination, count);
                }

                fn MemoryStatus FillBytes(init i8[min max][] destination, u64[0 2 ** 63 - 1] count) {
                    return System.Memory.FillBytes(destination, 1, count);
                }

                fn MemoryStatus AppendBytes(
                    borrow mut dynamic i8[min max] destination,
                    borrow i8[min max][] source,
                    u64[0 2 ** 63 - 1] count) {
                    return System.Memory.AppendBytes(destination, source, count);
                }

                fn MemoryStatus AppendBytesDisjoint(
                    mut borrow dynamic i8[min max] destination,
                    borrow i8[min max][] source,
                    u64[0 2 ** 63 - 1] count) {
                    return System.Memory.AppendBytesDisjoint(destination, source, count);
                }

                fn MemoryStatus CopyCodePoints(
                    borrow i32[min max][] source,
                    borrow mut i32[min max][] destination,
                    u64[0 2 ** 63 - 1] count) {
                    return System.Memory.CopyCodePointsDisjoint(source, destination, count);
                }

                fn MemoryStatus MoveBytes(
                    borrow i8[min max][] source,
                    borrow mut i8[min max][] destination,
                    u64[0 2 ** 63 - 1] count) {
                    return System.Memory.MoveBytes(source, destination, count);
                }
                """,
                appPath),
            new CompilerOptions(
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;

        Assert.Contains("System_Memory_InitializeBytesDisjoint", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Memory_FillBytes", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Memory_AppendBytesDisjoint", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Memory_CopyCodePointsDisjoint", llvm, StringComparison.Ordinal);
        Assert.Contains("System_Memory_MoveBytes", llvm, StringComparison.Ordinal);
        Assert.Contains("@llvm.memcpy.p0.p0.i64", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceMemoryModuleLowersRuntimeDisjointAppendFastPaths()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var modulePath = Path.Combine(sourceRoot, "System", "Memory.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(modulePath), modulePath),
            new CompilerOptions(
                OptimizationLevel: CompilerOptimizationLevel.O0,
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        var appendBytesBody = ExtractLlvmFunctionBody(llvm, "@AppendBytes(");
        var appendCodePointsBody = ExtractLlvmFunctionBody(llvm, "@AppendCodePoints(");
        var appendBytesDisjointBody = ExtractLlvmFunctionBody(llvm, "@AppendBytesDisjoint(");
        var appendCodePointsDisjointBody = ExtractLlvmFunctionBody(llvm, "@AppendCodePointsDisjoint(");
        var copyBytesBody = ExtractLlvmFunctionBody(llvm, "@CopyBytes(");
        var copyCodePointsBody = ExtractLlvmFunctionBody(llvm, "@CopyCodePoints(");
        var moveBytesBody = ExtractLlvmFunctionBody(llvm, "@MoveBytes(");
        var moveCodePointsBody = ExtractLlvmFunctionBody(llvm, "@MoveCodePoints(");
        var moveBytesInfallibleBody = ExtractLlvmFunctionBody(llvm, "@MoveBytesInfallible(");
        var moveCodePointsInfallibleBody = ExtractLlvmFunctionBody(llvm, "@MoveCodePointsInfallible(");

        Assert.Contains("icmp ule ptr", appendBytesBody, StringComparison.Ordinal);
        Assert.Contains("icmp ule ptr", appendCodePointsBody, StringComparison.Ordinal);
        Assert.Contains("icmp ule ptr", copyBytesBody, StringComparison.Ordinal);
        Assert.Contains("icmp ule ptr", copyCodePointsBody, StringComparison.Ordinal);
        Assert.DoesNotContain("icmp ule ptr", appendBytesDisjointBody, StringComparison.Ordinal);
        Assert.DoesNotContain("icmp ule ptr", appendCodePointsDisjointBody, StringComparison.Ordinal);
        Assert.DoesNotContain("icmp ule ptr", moveBytesInfallibleBody, StringComparison.Ordinal);
        Assert.DoesNotContain("icmp ule ptr", moveCodePointsInfallibleBody, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot", appendBytesDisjointBody, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot", appendCodePointsDisjointBody, StringComparison.Ordinal);
        Assert.Contains("@MoveBytesInfallible", moveBytesBody, StringComparison.Ordinal);
        Assert.Contains("@MoveCodePointsInfallible", moveCodePointsBody, StringComparison.Ordinal);
        Assert.Contains("icmp ult ptr", moveBytesInfallibleBody, StringComparison.Ordinal);
        Assert.Contains("icmp ult ptr", moveCodePointsInfallibleBody, StringComparison.Ordinal);
        Assert.DoesNotContain("__stark_runtime_alloc", moveBytesInfallibleBody, StringComparison.Ordinal);
        Assert.DoesNotContain("__stark_runtime_alloc", moveCodePointsInfallibleBody, StringComparison.Ordinal);
        Assert.DoesNotContain("__stark_runtime_try_realloc", moveBytesInfallibleBody, StringComparison.Ordinal);
        Assert.DoesNotContain("__stark_runtime_try_realloc", moveCodePointsInfallibleBody, StringComparison.Ordinal);
        Assert.DoesNotContain("__stark_dynamic_try_reserve", moveBytesInfallibleBody, StringComparison.Ordinal);
        Assert.DoesNotContain("__stark_dynamic_try_reserve", moveCodePointsInfallibleBody, StringComparison.Ordinal);
        Assert.Contains("!llvm.access.group", copyBytesBody, StringComparison.Ordinal);
        Assert.Contains("!llvm.access.group", copyCodePointsBody, StringComparison.Ordinal);
        Assert.Contains("!\"llvm.loop.parallel_accesses\"", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceMemoryModuleLowersHotByteTailAppendsToIntrinsics()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var modulePath = Path.Combine(sourceRoot, "System", "Memory.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(modulePath), modulePath),
            new CompilerOptions(
                OptimizationLevel: CompilerOptimizationLevel.O3,
                EmitLlvmIr: true,
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        var appendBytesDisjointBody = ExtractLlvmFunctionBody(llvm, "@AppendBytesDisjoint(");
        var appendCodePointsDisjointBody = ExtractLlvmFunctionBody(llvm, "@AppendCodePointsDisjoint(");
        var appendFillBytesBody = ExtractLlvmFunctionBody(llvm, "@AppendFillBytes(");
        var initializeBytesDisjointBody = ExtractLlvmFunctionBody(llvm, "define fastcc noundef %MemoryStatus @InitializeBytesDisjoint(");
        var initializeCodePointsDisjointBody = ExtractLlvmFunctionBody(llvm, "define fastcc noundef %MemoryStatus @InitializeCodePointsDisjoint(");
        var fillBytesBody = ExtractLlvmFunctionBody(llvm, "define fastcc noundef %MemoryStatus @FillBytes(");

        Assert.Contains("@InitializeBytesDisjoint", appendBytesDisjointBody, StringComparison.Ordinal);
        Assert.Contains("@InitializeCodePointsDisjoint", appendCodePointsDisjointBody, StringComparison.Ordinal);
        Assert.Contains("@FillBytes", appendFillBytesBody, StringComparison.Ordinal);
        Assert.Contains("@llvm.memcpy.p0.p0.i64", initializeBytesDisjointBody, StringComparison.Ordinal);
        Assert.Contains("@llvm.memcpy.p0.p0.i64", initializeCodePointsDisjointBody, StringComparison.Ordinal);
        Assert.Contains("@llvm.memset.p0.i64", fillBytesBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_phi = phi", appendBytesDisjointBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_phi = phi", appendCodePointsDisjointBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_phi = phi", appendFillBytesBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackagedMemoryHelpersPreserveImportedAttributes()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var modulePath = Path.Combine(sourceRoot, "System", "Memory.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-memory-package-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        Directory.CreateDirectory(packageDirectory);

        var libraryFileName = OperatingSystem.IsWindows() ? "SystemMemory.lib" : "libSystemMemory.a";
        var manifestPath = Path.Combine(packageDirectory, Path.GetFileNameWithoutExtension(libraryFileName) + ".starkpkg.json");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var llvmPath = Path.Combine(tempDirectory.FullName, "App.ll");

        try
        {
            var packageStdout = new StringWriter();
            var packageStderr = new StringWriter();
            var packageExitCode = await CompilerCli.RunAsync(
                [modulePath, "--emit-pkg", "--package-library-file", libraryFileName, "-I", sourceRoot, "-o", manifestPath],
                new StringReader(string.Empty),
                packageStdout,
                packageStderr);

            Assert.True(packageExitCode == 0, packageStdout + Environment.NewLine + packageStderr);

            await File.WriteAllTextAsync(
                appPath,
                """
                import System.Memory
                module App

                fn System.Memory.MemoryStatus UseCopy(
                    borrow i8[min max][] source,
                    init i8[min max][] destination,
                    u64[0 2 ** 63 - 1] count) {
                    return System.Memory.InitializeBytesDisjoint(source, destination, count);
                }

                fn System.Memory.MemoryStatus UseFill(
                    init i8[min max][] destination,
                    i8[min max] value,
                    u64[0 2 ** 63 - 1] count) {
                    return System.Memory.FillBytes(destination, value, count);
                }
                """);

            var llvmStdout = new StringWriter();
            var llvmStderr = new StringWriter();
            var llvmExitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-llvm", "-I", packageDirectory, "-o", llvmPath],
                new StringReader(string.Empty),
                llvmStdout,
                llvmStderr);

            Assert.True(llvmExitCode == 0, llvmStdout + Environment.NewLine + llvmStderr);
            var llvm = await File.ReadAllTextAsync(llvmPath);

            var copyDeclaration = ExtractLlvmDeclaration(llvm, "@System_Memory_InitializeBytesDisjoint(");
            var fillDeclaration = ExtractLlvmDeclaration(llvm, "@System_Memory_FillBytes(");

            var sourceResult = DefaultCompilerPipeline.Create().Run(
                new CompilationInput(File.ReadAllText(modulePath), modulePath),
                new CompilerOptions(
                    OptimizationLevel: CompilerOptimizationLevel.O3,
                    EmitLlvmIr: true,
                    ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

            Assert.True(sourceResult.Succeeded, string.Join(Environment.NewLine, sourceResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            var sourceLlvm = sourceResult.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
            var copyDefinition = ExtractLlvmFunctionBody(
                sourceLlvm,
                "define fastcc noundef %MemoryStatus @InitializeBytesDisjoint(");
            var fillDefinition = ExtractLlvmFunctionBody(
                sourceLlvm,
                "define fastcc noundef %MemoryStatus @FillBytes(");

            string[] copyAttributes = ["readonly nocapture", "noalias nocapture", "nounwind willreturn mustprogress", "alwaysinline"];
            string[] fillAttributes = ["noalias nocapture", "memory(write, argmem: readwrite)", "nounwind willreturn mustprogress", "alwaysinline"];
            AssertContainsAll(copyDeclaration, copyAttributes);
            AssertContainsAll(copyDefinition, copyAttributes);
            AssertContainsAll(fillDeclaration, fillAttributes);
            AssertContainsAll(fillDefinition, fillAttributes);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task SourceStdLibMemoryExecutableRuns()
    {
        await AssertSourceExecutableRunsAsync(MemoryProgram, "stark-stdlib-memory-");
    }

    [Fact]
    public async Task SourceStdLibMemoryCopyExecutableRuns()
    {
        await AssertSourceExecutableRunsAsync(MemoryCopyProgram, "stark-stdlib-memory-copy-");
    }

    [Fact]
    public async Task SourceStdLibMemoryMoveExecutableRuns()
    {
        await AssertSourceExecutableRunsAsync(MemoryMoveProgram, "stark-stdlib-memory-move-");
    }

    [Fact]
    public async Task SourceStdLibMemoryAppendDisjointExecutableRuns()
    {
        await AssertSourceExecutableRunsAsync(MemoryAppendDisjointProgram, "stark-stdlib-memory-append-disjoint-");
    }

    private async Task AssertSourceExecutableRunsAsync(string source, string tempPrefix)
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory(tempPrefix);
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, source);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
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

    private static void AssertContainsAll(string text, IEnumerable<string> fragments)
    {
        foreach (var fragment in fragments)
        {
            Assert.Contains(fragment, text, StringComparison.Ordinal);
        }
    }

    private static string ExtractLlvmDeclaration(string llvm, string functionSignatureFragment)
    {
        var signatureIndex = llvm.IndexOf(functionSignatureFragment, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Unable to find LLVM declaration fragment '{functionSignatureFragment}'.");

        var start = llvm.LastIndexOf("\ndeclare ", signatureIndex, StringComparison.Ordinal);
        if (start < 0)
        {
            start = llvm.LastIndexOf("declare ", signatureIndex, StringComparison.Ordinal);
        }

        Assert.True(start >= 0, $"Unable to find LLVM declaration start for '{functionSignatureFragment}'.");

        var end = llvm.IndexOf('\n', signatureIndex);
        if (end < 0)
        {
            end = llvm.Length;
        }

        return llvm[start..end];
    }
}

