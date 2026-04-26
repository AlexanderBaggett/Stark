+++
title = "Simple Database"
weight = 150
+++

This tiny in-memory database example uses a prepared-statement shape, enum
result codes, a VM-style execute method, and a fixed-capacity append-only table.

## Build And Run

```bash
dotnet run --project src -- examples/simple-database/MemoryTable.stark --emit-exe -o examples/simple-database/memory-table
./examples/simple-database/memory-table
```

Expected behavior: exits with status `0` and no output.

Status: covered by `ExamplesCompileRunTests.SimpleDatabaseExampleCompilesAndRuns`.

## Source Files

- [MemoryTable.stark](/reference/examples/simple-database/MemoryTable.stark)
- [Stark.toml](/reference/examples/simple-database/Stark.toml)

{{< file-sample "static/reference/examples/simple-database/MemoryTable.stark" "stark" >}}

## Related

- [Enums and Pattern Matching](/book/12-enums-patterns/)
- [Memory and Collections](/book/21-memory-collections/)
