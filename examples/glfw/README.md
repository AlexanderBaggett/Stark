# GLFW Example

This example uses `Vendor.GLFW` to initialize GLFW, configure a hidden
no-graphics-API window, install the bundled event bridge, query input/window
state, and exit cleanly. On headless machines with no usable GLFW platform
backend it prints `GLFW unavailable` and exits successfully.

Build the vendor package first:

```sh
bash vendor/build-glfw-package.sh
cd examples
stark run glfw
```
