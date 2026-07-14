using System.Numerics;
using Stark.Compiler;

namespace compiler.Tests;

public sealed class PackageImageTemplateLiteralLookupTests
{
    [Fact]
    public void RootStampedLiteralFactsAtSameLocationRemainDistinctByLiteralText()
    {
        const string rootPath = "/virtual/Root.stark";
        var sharedLocation = new SourceLocation(rootPath, Line: 19, Column: 16);
        LiteralTypingRecord[] records =
        [
            new("-1", StarkTypeSymbols.Integer(8, new BigInteger(-1), new BigInteger(-1)), sharedLocation),
            new("null", StarkTypeSymbols.Null, sharedLocation)
        ];

        var lookup = PackageImageBuilder.BuildTemplateLiteralLookup(records, rootPath);

        Assert.Equal(2, lookup.Count);
        Assert.True(PackageImageBuilder.TryGetTemplateLiteralTypingRecord(
            lookup,
            sharedLocation.Line,
            sharedLocation.Column,
            "-1",
            out var integerLiteral));
        Assert.Equal(StarkTypeKind.Integer, integerLiteral.Type.Kind);
        Assert.Equal(new BigInteger(-1), integerLiteral.Type.RangeMin);
        Assert.Equal(new BigInteger(-1), integerLiteral.Type.RangeMax);

        Assert.True(PackageImageBuilder.TryGetTemplateLiteralTypingRecord(
            lookup,
            sharedLocation.Line,
            sharedLocation.Column,
            "null",
            out var nullLiteral));
        Assert.Equal(StarkTypeKind.Null, nullLiteral.Type.Kind);
    }
}
