+++
title = "HTTP GET"
weight = 120
+++

This example is a minimal HTTP/1.1 client over `System.Net.Tcp`. Stark does not
ship DNS or TLS yet, so it connects to a fixed IPv4 endpoint and sends a plain
HTTP request.

## Build And Run

```bash
cd examples
dotnet run --project ../src -- build http-get
./.stark/build/dev/http-get/http-get
```

Expected behavior: writes the HTTP response to stdout when the network path is
available. This example requires outbound networking and is not part of the
ordinary no-network integration run.

Status: manual/networked example.

## Source Files

- [HttpGet.stark](/reference/examples/http-get/HttpGet.stark)
- [Stark.toml](/reference/examples/http-get/Stark.toml)

{{< file-sample "static/reference/examples/http-get/HttpGet.stark" "stark" >}}

## Related

- [Threading and TCP](/book/23-threading-tcp/)
- [`System.Net.Tcp`](/reference/standard-library/System.Net.Tcp/)
