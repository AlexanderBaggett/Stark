# Zlib Example

`ZlibRoundTrip.stark` uses the bundled `Vendor.Zlib` binding to compress and
decompress a small byte payload with caller-owned buffers. It verifies both the
direct one-shot API and the streaming API.

Build the vendor package first:

```bash
bash vendor/build-zlib-package.sh
```

Then run the example:

```bash
cd examples
dotnet run --project ../src/compiler.csproj -- run zlib
```

Expected output:

```text
Zlib round trip ok
```
