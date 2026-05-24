+++
title = "Standard Library"
weight = 110
+++

This example combines `System.BitOperations` with `System.Console` output and
explicit IO status handling.

## Build And Run

```bash
./scripts/build-stdlib.sh
dotnet run --project src -- examples/standard-library/StandardLibrary.stark --emit-exe -I stdlib/dist -o examples/standard-library/standard-library
./examples/standard-library/standard-library
```

Expected output:

```text
Standard library ready
```

Status: covered by `ExamplesCompileRunTests.StandardLibraryExampleCompilesAndRunsWithStdlibPackage`.

## Source Files

- [StandardLibrary.stark](/reference/examples/standard-library/StandardLibrary.stark)
- [Stark.toml](/reference/examples/standard-library/Stark.toml)

### StandardLibrary.stark

{{< file-sample "static/reference/examples/standard-library/StandardLibrary.stark" "stark" >}}

### Stark.toml

{{< file-sample "static/reference/examples/standard-library/Stark.toml" "toml" >}}

## Related

- [The Standard Library](/book/21-console-process-platform/)
- [Standard library reference](/reference/standard-library/StandardLibrary/)
