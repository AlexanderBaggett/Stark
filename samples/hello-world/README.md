# Hello World Sample

Build the standard library package into the sample-local package directory:

```bash
mkdir -p samples/hello-world/packages
./scripts/build-stdlib.sh samples/hello-world/packages
```

Compile and run the sample:

```bash
dotnet run --project src -- samples/hello-world/Hello.stark --emit-exe -I samples/hello-world/packages -o samples/hello-world/hello
./samples/hello-world/hello
```
