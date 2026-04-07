# Static Library Consumer Sample

Create the package output directory and build the Stark library package:

```bash
mkdir -p samples/static-library-consumer/packages
dotnet run --project src -- samples/static-library-consumer/library/Facade.stark --emit-lib -o samples/static-library-consumer/packages/libFacade.a
```

Compile and run the consumer app against the packaged library:

```bash
dotnet run --project src -- samples/static-library-consumer/app/App.stark --emit-exe -I samples/static-library-consumer/packages -o samples/static-library-consumer/app/app
./samples/static-library-consumer/app/app
```
