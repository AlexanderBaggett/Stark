# LZ4 Example

`LZ4RoundTrip.stark` uses the bundled `Vendor.LZ4` binding to compress and
decompress a small byte payload with caller-owned buffers.

Build the vendor package first:

```bash
bash vendor/build-lz4-package.sh
```

Then run the example:

```bash
cd examples
dotnet run --project ../src/compiler.csproj -- run lz4
```

Expected output:

```text
LZ4 round trip ok
```
