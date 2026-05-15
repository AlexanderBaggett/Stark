+++
title = "Build Your Own Git"
weight = 130
+++

This intermediate slice models a small Git-like metadata tool. Separate
executables initialize a repository, write a demo commit, update refs, inspect
objects, and report status.

## Build And Run

```bash
./scripts/build-stdlib.sh
dotnet run --project src -- examples/build-your-own-git/Init.stark --emit-exe -I stdlib/dist -o examples/build-your-own-git/init
dotnet run --project src -- examples/build-your-own-git/Commit.stark --emit-exe -I stdlib/dist -o examples/build-your-own-git/commit
dotnet run --project src -- examples/build-your-own-git/Ref.stark --emit-exe -I stdlib/dist -o examples/build-your-own-git/ref
dotnet run --project src -- examples/build-your-own-git/Objects.stark --emit-exe -I stdlib/dist -o examples/build-your-own-git/objects
dotnet run --project src -- examples/build-your-own-git/Inspect.stark --emit-exe -I stdlib/dist -o examples/build-your-own-git/inspect
dotnet run --project src -- examples/build-your-own-git/Status.stark --emit-exe -I stdlib/dist -o examples/build-your-own-git/status
```

Expected outputs include `Initialized starkgit-demo/.starkgit`, `Wrote demo
commit object`, `Updated main ref`, `Object demo-commit`, `Repository metadata
present`, and `Repository status clean`.

Status: covered by `ExamplesCompileRunTests.BuildYourOwnGitExamplesInitializeWriteCommitUpdateRefListInspectAndReportStatusWithStdlibPackage`.

## Source Files

- [Init.stark](/reference/examples/build-your-own-git/Init.stark)
- [Commit.stark](/reference/examples/build-your-own-git/Commit.stark)
- [Ref.stark](/reference/examples/build-your-own-git/Ref.stark)
- [Objects.stark](/reference/examples/build-your-own-git/Objects.stark)
- [Inspect.stark](/reference/examples/build-your-own-git/Inspect.stark)
- [Status.stark](/reference/examples/build-your-own-git/Status.stark)
- [Stark.toml](/reference/examples/build-your-own-git/Stark.toml)

{{< file-sample "static/reference/examples/build-your-own-git/Init.stark" "stark" >}}

## Related

- [File Processing Project](/book/35-project-file-processing/)
- [`System.FileSystem`](/reference/standard-library/System.FileSystem/)
- [`System.IO.File`](/reference/standard-library/System.IO.File/)
