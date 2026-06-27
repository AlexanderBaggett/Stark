# cgltf Example

This example parses the checked-in `assets/tiny-triangle.gltf` file through
`Vendor.Cgltf`, validates core mesh/material/node/buffer/accessor metadata, and
loads embedded data-URI buffers through the explicit buffer-loading policy.

Build the vendor package first:

```sh
bash vendor/build-cgltf-package.sh
cd examples
stark run cgltf
```

Set `STARK_CGLTF_ASSET_PATH` to point at another `.gltf` file.
