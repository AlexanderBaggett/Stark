using Stark.Compiler;

namespace compiler.Tests;

public sealed class MidLevelIrArtifactValidationTests
{
    [Fact]
    public void ValueCallRValuesRejectVoidResultTypes()
    {
        Assert.Throws<ArgumentException>(() => new MidLevelIrCallRValue(
            "Drop",
            [],
            StarkTypeSymbols.Void,
            "Drop()"));

        var functionPointerType = StarkTypeSymbols.FunctionPointer(
            StarkFunctionKind.Fn,
            StarkTypeSymbols.Void,
            []);
        Assert.Throws<ArgumentException>(() => new MidLevelIrIndirectCallRValue(
            new MidLevelIrFunctionAddressOperand("Drop", functionPointerType),
            [],
            StarkTypeSymbols.Void,
            "fp()"));
    }

    [Fact]
    public void SsaValueCallRValuesRejectVoidResultTypes()
    {
        Assert.Throws<ArgumentException>(() => new SsaCallRValue(
            "Drop",
            [],
            StarkTypeSymbols.Void,
            "Drop()"));

        var functionPointerType = StarkTypeSymbols.FunctionPointer(
            StarkFunctionKind.Fn,
            StarkTypeSymbols.Void,
            []);

        Assert.Throws<ArgumentException>(() => new SsaIndirectCallRValue(
            new SsaFunctionAddressValue("Drop", functionPointerType),
            [],
            StarkTypeSymbols.Void,
            "fp()"));
    }

    [Fact]
    public void IndexRValuesRejectMismatchedOperationFamilies()
    {
        var elementType = StarkTypeSymbols.Integer(32);
        var slice = new MidLevelIrLocalOperand("items", StarkTypeSymbols.Slice(elementType));

        Assert.Throws<ArgumentException>(() => new MidLevelIrExtractIndexRValue(
            slice,
            ElementIndex: 0,
            IndexedElementOperationFamily.FixedArrayElement,
            elementType,
            "items[0]"));

        var ssaSlice = new SsaValueReference("items", StarkTypeSymbols.Slice(elementType));
        Assert.Throws<ArgumentException>(() => new SsaExtractIndexRValue(
            ssaSlice,
            ElementIndex: 0,
            IndexedElementOperationFamily.FixedArrayElement,
            elementType,
            "items[0]"));
    }

    [Fact]
    public void IndexRValuesRejectMismatchedResultAndValueTypes()
    {
        var i32 = StarkTypeSymbols.Integer(32);
        var i64 = StarkTypeSymbols.Integer(64);
        var arrayType = StarkTypeSymbols.FixedArray(i32, fixedLength: 2);
        var mirArray = new MidLevelIrLocalOperand("values", arrayType);

        Assert.Throws<ArgumentException>(() => new MidLevelIrExtractIndexRValue(
            mirArray,
            ElementIndex: 0,
            IndexedElementOperationFamily.FixedArrayElement,
            i64,
            "values[0]"));

        Assert.Throws<ArgumentException>(() => new MidLevelIrInsertIndexRValue(
            mirArray,
            ElementIndex: 0,
            IndexedElementOperationFamily.FixedArrayElement,
            new MidLevelIrBoolConstantOperand(true),
            arrayType,
            "values[0] = true"));

        var ssaArray = new SsaZeroInitializerValue(arrayType);
        Assert.Throws<ArgumentException>(() => new SsaExtractIndexRValue(
            ssaArray,
            ElementIndex: 0,
            IndexedElementOperationFamily.FixedArrayElement,
            i64,
            "values[0]"));

        Assert.Throws<ArgumentException>(() => new SsaInsertIndexRValue(
            ssaArray,
            ElementIndex: 0,
            IndexedElementOperationFamily.FixedArrayElement,
            new SsaBoolConstant(true),
            arrayType,
            "values[0] = true"));
    }

    [Fact]
    public void ViewIndexRValuesRejectMismatchedComponentTypes()
    {
        var i32 = StarkTypeSymbols.Integer(32);
        var sliceType = StarkTypeSymbols.Slice(i32);
        var mirSlice = new MidLevelIrLocalOperand("items", sliceType);

        Assert.Throws<ArgumentException>(() => new MidLevelIrExtractIndexRValue(
            mirSlice,
            ElementIndex: 0,
            IndexedElementOperationFamily.ViewComponent,
            i32,
            "items.Data"));

        var ssaSlice = new SsaValueReference("items", sliceType);
        Assert.Throws<ArgumentException>(() => new SsaExtractIndexRValue(
            ssaSlice,
            ElementIndex: 1,
            IndexedElementOperationFamily.ViewComponent,
            StarkTypeSymbols.RawPointer(i32, isMutable: false),
            "items.Length"));
    }

    [Fact]
    public void ObjectConstructionOperandsRequireResolvedConstructorFacts()
    {
        var boxType = StarkTypeSymbols.Named("Box");
        var value = new MidLevelIrLocalOperand("$tmp0", boxType);
        var constructor = new TypedConstructorShape(
            "Box",
            [],
            IsPrimaryShape: false);

        Assert.Throws<ArgumentException>(() => new MidLevelIrObjectConstructionOperand(
            value,
            new MidLevelIrObjectConstructionFacts(
                boxType,
                "Box",
                MidLevelIrObjectConstructionKind.ExplicitConstructor,
                constructor,
                [])));
    }

    [Fact]
    public void EnumConstructionOperandsRequireVariantPayloadFacts()
    {
        var enumType = StarkTypeSymbols.Named("Token");
        var tagField = new FieldSymbol("$tag", StarkTypeSymbols.Integer(32));
        var payloadField = new EnumVariantLayoutFieldSymbol(
            SourcePosition: 0,
            SourceFieldName: "Value",
            StorageFieldName: "$Value_Value",
            StorageFieldIndex: 1,
            Type: StarkTypeSymbols.Integer(32));
        var variant = new EnumVariantLayoutSymbol(
            "Value",
            TagValue: 1,
            UsesNamedFields: true,
            [payloadField]);
        var layout = new EnumLayoutSymbol(
            "Token",
            EnumLayoutKind.DirectTag,
            tagField,
            [tagField, new FieldSymbol(payloadField.StorageFieldName, payloadField.Type)],
            new Dictionary<string, EnumVariantLayoutSymbol>(StringComparer.Ordinal)
            {
                [variant.Name] = variant
            });

        Assert.Throws<ArgumentException>(() => new MidLevelIrEnumConstructionOperand(
            new MidLevelIrLocalOperand("$tmp0", enumType),
            new MidLevelIrEnumConstructionFacts(
                enumType,
                layout,
                variant,
                [])));
    }
}
