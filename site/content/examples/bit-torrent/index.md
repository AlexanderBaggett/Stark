+++
title = "BitTorrent"
weight = 160
+++

The BitTorrent examples are protocol slices: a tracker-response parser and a
peer-handshake encoder/validator. Both use fixed storage and explicit status
checks.

## Build And Run

```bash
dotnet run --project src -- examples/bit-torrent/TrackerResponse.stark --emit-exe -o examples/bit-torrent/tracker-response
dotnet run --project src -- examples/bit-torrent/Handshake.stark --emit-exe -o examples/bit-torrent/handshake
./examples/bit-torrent/tracker-response
./examples/bit-torrent/handshake
```

Expected behavior: each executable exits with status `0` and no output.

Status: covered by `ExamplesCompileRunTests.BitTorrentTrackerResponseExampleCompilesAndRuns` and `ExamplesCompileRunTests.BitTorrentHandshakeExampleCompilesAndRuns`.

## Source Files

- [TrackerResponse.stark](samples/TrackerResponse.stark)
- [Handshake.stark](samples/Handshake.stark)
- [Stark.toml](samples/Stark.toml)

### TrackerResponse.stark

{{< file-sample "samples/TrackerResponse.stark" "stark" >}}

### Handshake.stark

{{< file-sample "samples/Handshake.stark" "stark" >}}

### Stark.toml

{{< file-sample "samples/Stark.toml" "toml" >}}

## Related

- [Arrays, Slices, Text, and Views](/book/14-arrays-slices-text/)
- [Performance Model](/book/29-performance-model/)
