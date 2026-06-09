# Test Data Providers

`stark test` supports computed `[Theory]` rows through typed indexed providers:

```stark
record ParseCase(ascii Scenario, ascii Source) { }

finite law ParseCase ParseCases(u64[0 2 ** 63 - 1] index)
{
    switch (index)
    {
        case 0:
            return new ParseCase("empty module", "module Demo");
        default:
            return new ParseCase("function", "module Demo\nfn void Run() { return; }");
    }
}

[Theory]
[MemberData(ParseCases, ParseCase, 2, Scenario, Source)]
finite law bool ValidProgramParses(ascii scenario, ascii source)
{
    return ParserAccepts(source);
}
```

The attribute shape is:

```stark
[MemberData(provider, rowType, count)]
[MemberData(provider, rowType, count, Field0, Field1)]
```

`provider` is a qualified function name. `rowType` is a named row record or
struct. `count` is a positive integer literal. Optional field names map row
fields to theory parameters by parameter order; without explicit names, the
generated runner uses the theory parameter names as row field names.

The generated runner expands one static entry per row. For a selected row it
emits one stack local and one direct call:

```stark
stack ParseCase __stark_member_data_0 = ParseCases(0);
if (System.Testing.RunFact(
    "ValidProgramParses[ParseCases:0]",
    ValidProgramParses(__stark_member_data_0.Scenario, __stark_member_data_0.Source)) != 0)
{
    failed = 1;
}
```

This shape is deliberately not xUnit's erased `IEnumerable<object[]>` model.
Typed indexed providers keep row layout visible to the compiler, avoid boxing,
avoid iterator allocation, allow filters such as `--filter ParseCases:1` to
materialize only selected rows, and give LLVM direct calls plus ordinary field
loads.

Prefer `finite law` providers for pure static data tables. Use plain `fn` only
when the provider performs real effects such as reading generated fixtures.
