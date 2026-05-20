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

- [TrackerResponse.stark](/reference/examples/bit-torrent/TrackerResponse.stark)
- [Handshake.stark](/reference/examples/bit-torrent/Handshake.stark)
- [Stark.toml](/reference/examples/bit-torrent/Stark.toml)

{{< file-sample "static/reference/examples/bit-torrent/TrackerResponse.stark" "stark" >}}

## Related

- [Arrays, Slices, Text, and Views](/book/14-arrays-slices-text/)
- [Performance Model](/book/26-performance-model/)
