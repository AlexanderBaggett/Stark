+++
title = "Examples"
weight = 40
+++

Canonical Stark examples live in the repository `examples/` directory and are
covered by integration tests where native dependencies allow. If you are setting
up Stark for the first time, start with [Getting Started](/getting-started/)
before running these examples.

## Starter Programs

<div class="example-grid">
  <article class="example-card">
    <strong>Hello</strong>
    <span>Small standard-library-backed program and executable manifest.</span>
    <a href="/examples/hello/">Details</a>
    <a href="/reference/examples/hello.stark">Source</a>
  </article>
  <article class="example-card">
    <strong>Basic Syntax</strong>
    <span>Declarations, expressions, stack locals, mutation, loops, and switch.</span>
    <a href="/examples/basic-syntax/">Details</a>
    <a href="/reference/examples/basic-syntax/BasicSyntax.stark">Source</a>
  </article>
  <article class="example-card">
    <strong>Type System</strong>
    <span>Range aliases, constrained fields, records, enums, and equality.</span>
    <a href="/examples/type-system/">Details</a>
    <a href="/reference/examples/type-system/TypeSystem.stark">Source</a>
  </article>
  <article class="example-card">
    <strong>Arithmetic</strong>
    <span>Integer operators, local variables, and guard-style status returns.</span>
    <a href="/examples/arithmetic/">Details</a>
    <a href="/reference/examples/arithmetic/Arithmetic.stark">Source</a>
  </article>
  <article class="example-card">
    <strong>Control Flow</strong>
    <span>A compact loop and switch sample.</span>
    <a href="/examples/control-flow/">Details</a>
    <a href="/reference/examples/control-flow/ControlFlow.stark">Source</a>
  </article>
  <article class="example-card">
    <strong>Data Model</strong>
    <span>Structs, records, and object initializers.</span>
    <a href="/examples/data-model/">Details</a>
    <a href="/reference/examples/data-model/DataModel.stark">Source</a>
  </article>
</div>

## Ownership, Modules, and Packages

<div class="example-grid">
  <article class="example-card">
    <strong>Borrowing</strong>
    <span>Ownership moves, borrow kinds, out parameters, and move diagnostics.</span>
    <a href="/examples/borrowing/">Details</a>
    <a href="/reference/examples/borrowing/Borrowing.stark">Source</a>
  </article>
  <article class="example-card">
    <strong>Modules</strong>
    <span>Three-file module boundary example with public and internal APIs.</span>
    <a href="/examples/modules/">Details</a>
    <a href="/reference/examples/modules/App.stark">Source</a>
  </article>
  <article class="example-card">
    <strong>Multi-Module</strong>
    <span>Cross-module imports and a public helper in a sibling file.</span>
    <a href="/examples/multi-module/">Details</a>
    <a href="/reference/examples/multi-module/App.stark">Source</a>
  </article>
  <article class="example-card">
    <strong>Static Library</strong>
    <span>Library package flow through `--emit-lib` and sidecar manifest output.</span>
    <a href="/examples/static-library/">Details</a>
    <a href="/reference/examples/static-library/Facade.stark">Source</a>
  </article>
</div>

## Standard Library and Systems Slices

<div class="example-grid">
  <article class="example-card">
    <strong>Standard Library</strong>
    <span>Console, bit operations, output status, and packaged `System` usage.</span>
    <a href="/examples/standard-library/">Details</a>
    <a href="/reference/examples/standard-library/StandardLibrary.stark">Source</a>
  </article>
  <article class="example-card">
    <strong>HTTP GET</strong>
    <span>Manual networked HTTP/1.1 client over `System.Net.Tcp`.</span>
    <a href="/examples/http-get/">Details</a>
    <a href="/reference/examples/http-get/HttpGet.stark">Source</a>
  </article>
  <article class="example-card">
    <strong>Build Your Own Git</strong>
    <span>Filesystem-backed metadata slices for a tiny Git-like tool.</span>
    <a href="/examples/build-your-own-git/">Details</a>
    <a href="/reference/examples/build-your-own-git/Init.stark">Source</a>
  </article>
  <article class="example-card">
    <strong>Neural Network</strong>
    <span>Fixed-topology inference with explicit storage and integer-style work.</span>
    <a href="/examples/neural-network/">Details</a>
    <a href="/reference/examples/neural-network/Inference.stark">Source</a>
  </article>
  <article class="example-card">
    <strong>Simple Database</strong>
    <span>Prepared-statement and table-storage shape for a tiny database slice.</span>
    <a href="/examples/simple-database/">Details</a>
    <a href="/reference/examples/simple-database/MemoryTable.stark">Source</a>
  </article>
  <article class="example-card">
    <strong>BitTorrent</strong>
    <span>Tracker response parsing and peer handshake construction.</span>
    <a href="/examples/bit-torrent/">Details</a>
    <a href="/reference/examples/bit-torrent/TrackerResponse.stark">Source</a>
  </article>
</div>

## Native and Graphical Examples

<div class="example-grid">
  <article class="example-card">
    <strong>FFI</strong>
    <span>Native boundary basics for explicit ABI calls.</span>
    <a href="/examples/ffi/">Details</a>
    <a href="/reference/examples/ffi/Ffi.stark">Source</a>
  </article>
  <article class="example-card">
    <strong>Raylib</strong>
    <span>Native graphics binding package. Requires local Raylib configuration.</span>
    <a href="/examples/raylib/">Details</a>
    <a href="/reference/examples/raylib/README.md">Setup</a>
  </article>
  <article class="example-card">
    <strong>Breakout</strong>
    <span>Headless deterministic game core plus optional Raylib shell.</span>
    <a href="/examples/breakout/">Details</a>
    <a href="/reference/examples/breakout/BreakoutCore.stark">Source</a>
  </article>
</div>

## Manifests

The examples directory uses `Stark.toml` project manifests and a root
`Stark.solution.toml` solution manifest for `stark build` and `stark run`.

- [examples/Stark.solution.toml](/reference/examples/Stark.solution.toml)
- [hello/Stark.toml](/reference/examples/hello/Stark.toml)
- [static-library/Stark.toml](/reference/examples/static-library/Stark.toml)
- [raylib/Stark.toml](/reference/examples/raylib/Stark.toml)
