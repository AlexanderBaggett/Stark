# Vulkan Example

This example creates a GLFW no-OpenGL window, creates a Vulkan instance and
surface, builds the minimal swapchain/render-pass/pipeline path in Stark, and
presents a colored triangle. `VulkanInfo.stark` remains as a smaller loader
diagnostic sample for machines without a display.

Build the vendor packages first:

```sh
bash vendor/build-vulkan-package.sh
bash vendor/build-glfw-package.sh
```

Then build or run this example with the normal Stark project tooling from this
directory:

```sh
stark run
```
