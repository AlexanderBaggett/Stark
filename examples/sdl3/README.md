# SDL3 Example

Build the bundled SDL3 vendor package first:

```bash
bash vendor/build-sdl3-package.sh
```

Then build or run this example with the Stark project command from this
directory. On Linux CI or other headless machines, set SDL's dummy backends:

```bash
SDL_VIDEODRIVER=dummy SDL_AUDIODRIVER=dummy stark run
```
