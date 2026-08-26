+++
title = "HTTP GET"
weight = 120
+++

This example performs an HTTP GET request to `http://example.com/`. Stark code
builds the request, connects through `System.Net.Tcp`, checks write/read status,
and streams the response without a C shim or machine-local native library.

## Build And Run

```bash
cd examples
dotnet run --project ../src -- build http-get
./build/dev/<target-triple>/stage0/bin/http-get/http-get
```

Expected behavior: writes the HTTP response to stdout when outbound networking
is available. Release qualification builds it on every target but does not make
the build depend on external network availability.

Status: manual/networked example.

## Source Files

- [HttpGet.stark](samples/HttpGet.stark)
- [Stark.toml](samples/Stark.toml)

### HttpGet.stark

{{< file-sample "samples/HttpGet.stark" "stark" >}}

### Stark.toml

{{< file-sample "samples/Stark.toml" "toml" >}}

## Related

- [Threading and TCP](/book/24-threading-tcp/)
- [FFI, Raw Pointers, and Native Packages](/book/20-ffi-raw-pointers-native-packages/)
