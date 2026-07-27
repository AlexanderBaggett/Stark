# Miniaudio Example

This example decodes an embedded 16-bit PCM WAV payload with
`Vendor.Miniaudio`, reads f32 samples into a caller-owned fixed buffer, seeks
back to the start, and verifies the first frame again. It is deterministic and
does not require an audio device.

Build the vendor package first:

```sh
bash vendor/build-miniaudio-package.sh
cd examples
stark run miniaudio
```
