# Vulkan Example

This example uses `Vendor.Vulkan` to query the Vulkan loader version, global
entry-point availability, and instance layer/extension counts. It does not
create a window, surface, swapchain, or device, so it can run on machines with
the Vulkan loader installed even when no display is available.

Build the package first:

```sh
bash vendor/build-vulkan-package.sh
```

Then build or run this example with the normal Stark project tooling.
