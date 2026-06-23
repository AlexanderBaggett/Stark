# Curl Example

`CurlGet.stark` uses the bundled `Vendor.Curl` binding to run an HTTP GET
against a caller-supplied local endpoint. It verifies both the caller-owned
buffer path and the owned response path.

Build the vendor package first:

```bash
bash vendor/build-curl-package.sh
```

Run the example with a local HTTP endpoint that returns `stark-curl-ok\n`:

```bash
cd examples
STARK_CURL_URL=http://127.0.0.1:18080/stark-vendor-curl \
  dotnet run --project ../src/compiler.csproj -- run curl
```

Expected output:

```text
Curl GET ok
```
