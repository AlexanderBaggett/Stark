+++
title = "HTTPS GET"
weight = 120
+++

This example performs an HTTPS GET request to `https://www.google.com/`. Stark
code builds the request, checks write/read status, and streams the response;
`HttpsNative.c` supplies the TLS transport through OpenSSL.

## Build And Run

```bash
cd examples
dotnet run --project ../src -- build http-get
./.stark/build/dev/http-get/http-get
```

Expected behavior: writes the HTTPS response to stdout when the network path
and OpenSSL setup are available. This example requires outbound networking and
is not part of the ordinary no-network integration run.

Status: manual/networked example.

## Source Files

- [HttpGet.stark](samples/HttpGet.stark)
- [HttpsNative.c](samples/HttpsNative.c)
- [Stark.toml](samples/Stark.toml)

### HttpGet.stark

{{< file-sample "samples/HttpGet.stark" "stark" >}}

### HttpsNative.c

{{< file-sample "samples/HttpsNative.c" "c" >}}

### Stark.toml

{{< file-sample "samples/Stark.toml" "toml" >}}

## Related

- [Threading and TCP](/book/24-threading-tcp/)
- [FFI, Raw Pointers, and Native Packages](/book/20-ffi-raw-pointers-native-packages/)
