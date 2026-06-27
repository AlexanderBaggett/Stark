# STB Image Example

This example loads a tiny embedded PPM image through `Vendor.STB.Image`,
inspects the decoded dimensions and channel count, mutates pixel data, and
resizes the image through the caller-buffer-first resize path.

Build the vendor package first:

```sh
bash vendor/build-stb-image-package.sh
cd examples
stark run stb-image
```
