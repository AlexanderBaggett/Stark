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

- [hello.stark](/reference/examples/hello.stark)
- [hello/Stark.toml](/reference/examples/hello/Stark.toml)

### hello.stark

{{< file-sample "static/reference/examples/hello.stark" "stark" >}}

### hello/Stark.toml

{{< file-sample "static/reference/examples/hello/Stark.toml" "toml" >}}

## Related

- [Getting Started](/getting-started/)
- [Console reference](/reference/standard-library/System.Console/)
- [Installing Stark and Building Programs](/book/02-installing-stark/)
