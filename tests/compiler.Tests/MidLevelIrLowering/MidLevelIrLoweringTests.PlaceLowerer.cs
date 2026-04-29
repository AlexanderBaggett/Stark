using System.Numerics;
using Stark.Compiler;

namespace compiler.Tests;

public sealed partial class MidLevelIrLoweringTests
{
    [Fact]
    public void FieldAssignmentLowersToAggregateUpdate()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Run() {
                stack mut Box box = new Box() { Value = 1 };
                box.Value = 2;
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(statements, statement => statement.Text.Contains("box.Value = 2", StringComparison.Ordinal));
        Assert.True(statements.Count(static statement => statement.Value is MidLevelIrInsertFieldRValue) >= 2);
    }

    [Fact]
    public void FieldCompoundAssignmentLowersToAggregateReadModifyWrite()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Run() {
                stack mut Box box = new Box() { Value = 1 };
                box.Value += 2;
                return box.Value;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(statements, statement => statement.Text.Contains("box.Value += 2", StringComparison.Ordinal));
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Add });
        Assert.True(statements.Count(static statement => statement.Value is MidLevelIrInsertFieldRValue) >= 2);
    }

    [Fact]
    public void FixedArrayInitializerAndConstantIndexLowerToAggregateIndexOperations()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run() {
                stack i32[-2147483648 2147483647][3] values = { 1, 2, 3 };
                return values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrInsertIndexRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractIndexRValue);
    }

    [Fact]
    public void PartialFixedArrayInitializersInsideObjectInitializersLowerWithZeroFilledTails()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647][3] Values;
            }

            fn i32[-2147483648 2147483647] Run() {
                stack Box box = new Box() { Values = { 1, 2 } };
                return box.Values[2];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrInsertFieldRValue { FieldName: "Values" });
        Assert.True(statements.Count(static statement => statement.Value is MidLevelIrInsertIndexRValue) >= 2);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractIndexRValue { ElementIndex: 2 });
    }

    [Fact]
    public void FixedArrayElementAssignmentLowersToAggregateIndexUpdate()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run() {
                stack mut i32[-2147483648 2147483647][3] values = { 1, 2, 3 };
                values[1] = 9;
                return values[1];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.True(statements.Count(static statement => statement.Value is MidLevelIrInsertIndexRValue) >= 2);
        Assert.Contains(statements, statement => statement.Text.Contains("values[1] = 9", StringComparison.Ordinal));
    }

    [Fact]
    public void LocalFixedArrayCanLowerToSliceAndDynamicSliceRead()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] index) {
                stack i32[-2147483648 2147483647][3] values = { 4, 7, 9 };
                stack i32[-2147483648 2147483647][] view = values;
                return view[index];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Locals, local => local.Name == "values" && local.IsAddressable);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrMakeSliceFromLocalRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrSliceElementAddressRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrLoadIndirectRValue);
    }

    [Fact]
    public void TextSlicesLowerToViewProducingMir()
    {
        var result = Compile(
            """
            module Demo

            fn ascii Run(ascii text, i32[-2147483648 2147483647] start, i32[-2147483648 2147483647] length) {
                return text[start, length];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrTextSliceRValue
            {
                Type.Kind: StarkTypeKind.Ascii,
                Start.Type.Kind: StarkTypeKind.Integer,
                Start.Type.BitWidth: 64,
                Length.Type.Kind: StarkTypeKind.Integer,
                Length.Type.BitWidth: 64
            });
    }

    [Fact]
    public void EmptyTextSlicesLowerToIdentityTextOperands()
    {
        var result = Compile(
            """
            module Demo

            fn ascii SliceAscii(ascii text) {
                return text[];
            }

            fn unicode SliceUnicode(unicode text) {
                return text[];
            }
            """);

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var functions = GetMir(result).Functions.ToArray();

        Assert.Equal(2, functions.Length);
        Assert.All(functions, function => Assert.True(function.SupportsDirectCodeGeneration));
        Assert.All(
            functions,
            function =>
            {
                Assert.IsType<MidLevelIrParameterOperand>(Assert.Single(function.Blocks).Terminator.Value);
                Assert.DoesNotContain(
                    function.Blocks.SelectMany(static block => block.Statements),
                    static statement => statement.Value is MidLevelIrTextSliceRValue);
            });
    }

    [Fact]
    public void SingleElementTextIndexingLowersToUnitLengthTextViews()
    {
        var result = Compile(
            """
            module Demo

            fn ascii PickAscii(ascii text, i32[-2147483648 2147483647] index) {
                return text[index];
            }

            fn unicode PickUnicode(unicode text, i32[-2147483648 2147483647] index) {
                return text[index];
            }
            """);

        Assert.True(result.Succeeded);
        var functions = GetMir(result).Functions.ToArray();

        Assert.Equal(2, functions.Length);
        Assert.All(functions, function => Assert.True(function.SupportsDirectCodeGeneration));
        Assert.All(
            functions,
            function =>
            {
                var textSlice = Assert.Single(
                    function.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value).OfType<MidLevelIrTextSliceRValue>());
                var length = Assert.IsType<MidLevelIrIntegerConstantOperand>(textSlice.Length);
                Assert.Equal(StarkTypeKind.Integer, textSlice.Start.Type.Kind);
                Assert.Equal(64, textSlice.Start.Type.BitWidth);
                Assert.Equal(StarkTypeKind.Integer, length.Type.Kind);
                Assert.Equal(64, length.Type.BitWidth);
                Assert.Equal(BigInteger.One, length.Value);
            });
    }

    [Fact]
    public void DynamicFixedArrayIndexMutationUsesAddressBasedMemoryAccess()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] index) {
                stack mut i32[-2147483648 2147483647][3] values = { 1, 2, 3 };
                values[index] = 9;
                return values[index];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Locals, local => local.Name == "values" && local.IsAddressable);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrAddressOfLocalRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrElementAddressRValue);
        Assert.Contains(statements, static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrLoadIndirectRValue);
    }

    [Fact]
    public void FixedArrayParameterDynamicIndexUsesAddressBasedMemoryAccess()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647][3] values, i32[-2147483648 2147483647] index) {
                return values[index];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrAddressOfParameterRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrElementAddressRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrLoadIndirectRValue);
    }

    [Fact]
    public void NestedLvalueChainsWithDynamicIndexCompoundAssignmentsUseAddressBasedMemoryAccess()
    {
        var result = Compile(
            """
            module Demo

            struct Cell {
                i32[-2147483648 2147483647] Value;
            }

            struct Holder {
                Cell[2] Cells;
            }

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] index) {
                stack mut Holder holder = new Holder() {
                    Cells = { new Cell() { Value = 1 }, new Cell() { Value = 2 } }
                };
                holder.Cells[index].Value += 4;
                return holder.Cells[index].Value;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Locals, local => local.Name == "holder" && local.IsAddressable);
        Assert.Contains(statements, statement => statement.Text.Contains("holder.Cells[index].Value += 4", StringComparison.Ordinal));
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrFieldAddressRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrElementAddressRValue);
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Add });
        Assert.Contains(statements, static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect);
    }

    [Fact]
    public void SliceMutationUsesAddressBasedMemoryAccess()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run(mut i32[-2147483648 2147483647][] view, i32[-2147483648 2147483647] index) {
                view[index] = 9;
                return view[index];
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrSliceElementAddressRValue);
        Assert.Contains(statements, static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrLoadIndirectRValue);
    }

    [Fact]
    public void MixedCallMemberAndIndexPostfixChainsLowerToMir()
    {
        var result = Compile(
            """
            module Demo

            struct Cell {
                i32[-2147483648 2147483647] Value;
            }

            struct Holder {
                Cell[2] Cells;
            }

            fn Holder Make() {
                return new Holder() {
                    Cells = { new Cell() { Value = 3 }, new Cell() { Value = 5 } }
                };
            }

            fn i32[-2147483648 2147483647] Run() {
                return Make().Cells[1].Value;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions, static function => function.Name == "Run");
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrCallRValue { FunctionName: "Make" });
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldName: "Cells" });
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrExtractIndexRValue { ElementIndex: 1 });
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrExtractFieldRValue { FieldName: "Value" });
    }

    [Fact]
    public void ExplicitPointerOperatorsAndConversionsLowerToMir()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run(i64[-9223372036854775808 9223372036854775807] bits) {
                stack mut i32[-2147483648 2147483647] value = 1;
                stack rawmutptr<i32[-2147483648 2147483647]> ptr = &value;
                stack rawptr<i32[-2147483648 2147483647]> readonlyPtr = (rawptr<i32[-2147483648 2147483647]>)ptr;
                *ptr = (i32[-2147483648 2147483647])bits;
                return *readonlyPtr;
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Locals, local => local.Name == "value" && local.IsAddressable);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrAddressOfLocalRValue);
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrConvertRValue convert
                && convert.TargetType.Kind == StarkTypeKind.RawPointer
                && convert.Operand.Type.Kind == StarkTypeKind.RawPointer);
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrConvertRValue convert
                && convert.TargetType.Kind == StarkTypeKind.Integer
                && convert.Operand.Type.Kind == StarkTypeKind.Integer);
        Assert.Contains(statements, static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrLoadIndirectRValue);
    }

    [Fact]
    public void FieldAndGlobalAddressExpressionsLowerToMirAddresses()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            static mut i32[-2147483648 2147483647] Counter = 0;

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] input) {
                stack mut Box box = new Box() { Value = 1 };
                *(&(box.Value)) = input;
                Counter = *(&(box.Value));
                return *(&Counter);
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(function.Locals, local => local.Name == "box" && local.IsAddressable);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrFieldAddressRValue);
        Assert.Contains(statements, static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect);
        Assert.Contains(
            statements,
            statement => statement.Value is MidLevelIrLoadIndirectRValue load
                && load.Address is MidLevelIrGlobalAddressOperand { Name: "Counter" });
    }

    [Fact]
    public void IndexedFieldAddressBehindRawPointerLowersToParameterBackedMirAddresses()
    {
        var result = Compile(
            """
            module Demo

            struct Buffer {
                i8[-128 127][16] Storage;
                i64[-9223372036854775808 9223372036854775807] WritePos;
            }

            fn i32[-2147483648 2147483647] Touch(rawmutptr<Buffer> buffer, i64[-9223372036854775808 9223372036854775807] index, i8[-128 127] value) {
                *(&(*buffer).Storage[index]) = value;
                return (i32[-2147483648 2147483647])*(&(*buffer).Storage[index]);
            }
            """);

        Assert.True(result.Succeeded);
        var function = Assert.Single(GetMir(result).Functions);
        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();

        Assert.True(function.SupportsDirectCodeGeneration);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrFieldAddressRValue);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrElementAddressRValue);
        Assert.Contains(statements, static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect);
        Assert.Contains(statements, static statement => statement.Value is MidLevelIrLoadIndirectRValue);
    }

    [Fact]
    public void ImmutableGlobalAddressesLowerToReadonlyMirAddresses()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            static i32[-2147483648 2147483647] Counter = 0;
            static Box Current = new Box() { Value = 5 };

            fn rawptr<i32[-2147483648 2147483647]> CounterPtr() {
                return &Counter;
            }

            fn rawptr<i32[-2147483648 2147483647]> FieldPtr() {
                return &(Current.Value);
            }
            """);

        Assert.True(result.Succeeded);

        var mir = GetMir(result);
        var counterPtr = Assert.Single(mir.Functions, static function => function.Name == "CounterPtr");
        var fieldPtr = Assert.Single(mir.Functions, static function => function.Name == "FieldPtr");

        Assert.True(counterPtr.SupportsDirectCodeGeneration);
        Assert.True(fieldPtr.SupportsDirectCodeGeneration);
        Assert.IsType<MidLevelIrGlobalAddressOperand>(Assert.Single(counterPtr.Blocks).Terminator.Value);
        Assert.Equal(
            new[] { false },
            counterPtr.Blocks
                .Select(block => block.Terminator.Value)
                .OfType<MidLevelIrGlobalAddressOperand>()
                .Select(static address => address.Type.IsMutablePointer)
                .ToArray());

        var fieldStatements = fieldPtr.Blocks.SelectMany(static block => block.Statements).ToArray();
        Assert.Contains(
            fieldStatements,
            static statement => statement.Value is MidLevelIrFieldAddressRValue { Type.IsMutablePointer: false });
        Assert.DoesNotContain(
            fieldStatements,
            static statement => statement.Value is MidLevelIrConvertRValue
            {
                TargetType: { Kind: StarkTypeKind.RawPointer },
                Operand.Type: { Kind: StarkTypeKind.RawPointer }
            });
    }

    [Fact]
    public void ConstGlobalAddressesLowerToFrozenMirAddresses()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            const Box Current = new Box() { Value = 5 };

            fn rawptr<frozen Box> BoxPtr() {
                return &Current;
            }

            fn rawptr<frozen i32[-2147483648 2147483647]> FieldPtr() {
                return &(Current.Value);
            }
            """);

        Assert.True(result.Succeeded);

        var mir = GetMir(result);
        var boxPtr = Assert.Single(mir.Functions, static function => function.Name == "BoxPtr");
        var fieldPtr = Assert.Single(mir.Functions, static function => function.Name == "FieldPtr");

        Assert.True(boxPtr.SupportsDirectCodeGeneration);
        Assert.True(fieldPtr.SupportsDirectCodeGeneration);

        var returnedBoxAddress = Assert.IsType<MidLevelIrGlobalAddressOperand>(Assert.Single(boxPtr.Blocks).Terminator.Value);
        Assert.Equal(StarkTypeKind.RawPointer, returnedBoxAddress.Type.Kind);
        Assert.False(returnedBoxAddress.Type.IsMutablePointer);
        Assert.NotNull(returnedBoxAddress.Type.ElementType);
        Assert.Equal(StarkAccessKind.Frozen, returnedBoxAddress.Type.ElementType!.AccessKind);

        var fieldStatements = fieldPtr.Blocks.SelectMany(static block => block.Statements).ToArray();
        Assert.Contains(
            fieldStatements,
            static statement => statement.Value is MidLevelIrFieldAddressRValue
            {
                Type.Kind: StarkTypeKind.RawPointer,
                Type.IsMutablePointer: false,
                Type.ElementType.AccessKind: StarkAccessKind.Frozen
            });

        var returnedFieldAddress = Assert.IsType<MidLevelIrLocalOperand>(Assert.Single(fieldPtr.Blocks).Terminator.Value);
        Assert.Equal(StarkTypeKind.RawPointer, returnedFieldAddress.Type.Kind);
        Assert.False(returnedFieldAddress.Type.IsMutablePointer);
        Assert.NotNull(returnedFieldAddress.Type.ElementType);
        Assert.Equal(StarkAccessKind.Frozen, returnedFieldAddress.Type.ElementType!.AccessKind);
    }

    [Fact]
    public void ConstParameterAddressesLowerToFrozenMirAddresses()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn rawptr<frozen i32[-2147483648 2147483647]> FieldPtr(const Box box) {
                return &(box.Value);
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

        var function = Assert.Single(GetMir(result).Functions);
        Assert.True(function.SupportsDirectCodeGeneration);

        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrAddressOfParameterRValue
            {
                PointeeType.AccessKind: StarkAccessKind.Frozen,
                Type.Kind: StarkTypeKind.RawPointer,
                Type.IsMutablePointer: false
            });
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrFieldAddressRValue
            {
                Type.Kind: StarkTypeKind.RawPointer,
                Type.IsMutablePointer: false,
                Type.ElementType.AccessKind: StarkAccessKind.Frozen
            });

        var returnedFieldAddress = Assert.IsType<MidLevelIrLocalOperand>(Assert.Single(function.Blocks).Terminator.Value);
        Assert.Equal(StarkTypeKind.RawPointer, returnedFieldAddress.Type.Kind);
        Assert.False(returnedFieldAddress.Type.IsMutablePointer);
        Assert.NotNull(returnedFieldAddress.Type.ElementType);
        Assert.Equal(StarkAccessKind.Frozen, returnedFieldAddress.Type.ElementType!.AccessKind);
    }

    [Fact]
    public void ConstProvenanceLocalsAreMarkedInMir()
    {
        var result = Compile(
            """
            module Demo

            fn rawptr<frozen i32[-2147483648 2147483647]> Forward(const rawmutptr<i32[-2147483648 2147483647]> ptr) {
                stack rawptr<frozen i32[-2147483648 2147483647]> local = ptr;
                return local;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

        var function = Assert.Single(GetMir(result).Functions);
        var local = Assert.Single(function.Locals, static local => local.Name == "local");
        Assert.True(local.HasConstProvenance);
    }

    [Fact]
    public void FrozenSliceAddressesLowerToReadonlyMirAddresses()
    {
        var result = Compile(
            """
            module Demo

            fn rawptr<frozen i32[-2147483648 2147483647]> FirstPtr(frozen i32[-2147483648 2147483647][] view) {
                return &(view[0]);
            }
            """);

        Assert.True(result.Succeeded);

        var function = Assert.Single(GetMir(result).Functions);
        Assert.True(function.SupportsDirectCodeGeneration);

        var statements = function.Blocks.SelectMany(static block => block.Statements).ToArray();
        Assert.Contains(
            statements,
            static statement => statement.Value is MidLevelIrSliceElementAddressRValue { Type.IsMutablePointer: false });

        var returned = Assert.IsType<MidLevelIrLocalOperand>(Assert.Single(function.Blocks).Terminator.Value);
        Assert.Equal(StarkTypeKind.RawPointer, returned.Type.Kind);
        Assert.False(returned.Type.IsMutablePointer);
    }
}
