+++
title = "Hello"
weight = 10
+++

The hello example is the smallest standard-library-backed executable. It imports
`System.Console` and calls `WriteLine`.

## Build And Run

```bash
./scripts/build-stdlib.sh
dotnet run --project src -- examples/hello.stark --emit-exe -I stdlib/dist -o examples/hello
./examples/hello
```

Expected output:

```text
Hello, World!
```

Status: covered by `ExamplesCompileRunTests.HelloExampleCompilesAndRunsWithStdlibPackage`.

## Source Files

- [hello.stark](samples/hello.stark)
- [hello/Stark.toml](samples/Stark.toml)

### hello.stark

{{< file-sample "samples/hello.stark" "stark" >}}

### hello/Stark.toml

{{< file-sample "samples/Stark.toml" "toml" >}}

## Related

- [Getting Started](/getting-started/)
- [Console reference](/reference/standard-library/System.Console/)
- [Installing Stark and Building Programs](/book/02-installing-stark/)
