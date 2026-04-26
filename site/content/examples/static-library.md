+++
title = "Static Library"
weight = 190
+++

This library-style example exercises cross-module code and the `--emit-lib`
package flow.

## Build And Run

```bash
dotnet run --project src -- examples/static-library/Facade.stark --emit-lib -o examples/static-library/libFacade.a
```

That command emits an archive plus a sidecar `.starkpkg.json` manifest. The
integration test compiles a temporary consumer and expects it to exit with
status `20`.

Status: covered by `ExamplesCompileRunTests.StaticLibraryExampleBuildsAndRunsFromPackage`.

## Source Files

- [Facade.stark](/reference/examples/static-library/Facade.stark)
- [Math.stark](/reference/examples/static-library/Math.stark)
- [Stark.toml](/reference/examples/static-library/Stark.toml)

{{< file-sample "static/reference/examples/static-library/Facade.stark" "stark" >}}

## Related

- [Package Manifest Reference](/book/appendix-f-package-manifest/)
- [Projects and solutions](/reference/language/ProjectsAndSolutions/)
