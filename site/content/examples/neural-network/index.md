+++
title = "Neural Network"
weight = 140
+++

This fixed-topology inference example uses integer fixed-point style values, a
two-neuron hidden layer, ReLU activation, and a final score threshold without
heap allocation or dynamic dispatch.

## Build And Run

```bash
dotnet run --project src -- examples/neural-network/Inference.stark --emit-exe -o examples/neural-network/inference
./examples/neural-network/inference
```

Expected behavior: exits with status `0` and no output.

Status: covered by `ExamplesCompileRunTests.NeuralNetworkExampleCompilesAndRuns`.

## Source Files

- [Inference.stark](samples/Inference.stark)
- [Stark.toml](samples/Stark.toml)

### Inference.stark

{{< file-sample "samples/Inference.stark" "stark" >}}

### Stark.toml

{{< file-sample "samples/Stark.toml" "toml" >}}

## Related

- [Performance Model](/book/29-performance-model/)
- [Memory Layout and ABI](/book/30-memory-layout-abi/)
