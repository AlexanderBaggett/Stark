# Stark Performance Cookbook

Stark performance work starts by making source facts visible: storage,
ownership, ranges, memory separation, failure, and call indirection.

## Start With The Small Kernel

Separate setup, IO, parsing, allocation, and logging from the work being
measured unless those operations are the workload.

```stark
finite law i32[min max] SumFour(i32[min max][4] values)
{
    return values[0] + values[1] + values[2] + values[3];
}

fn i32[min max] BuildAndRun()
{
    stack i32[min max][4] values =
    {
        1, 2, 3, 4
    };

    return SumFour(values);
}
```

Name benchmarks after the source task they perform. If allocation or text
formatting is included, make that clear in the benchmark name.

## Use Direct Calls First

Prefer named functions and direct calls in hot code.

```stark
finite law i32[min max] Double(i32[min max] value)
{
    return value * 2;
}

finite law i32[min max] UseDirect(i32[min max] value)
{
    return Double(value);
}
```

Use `fnptr` only when an API needs a thin callback value. Use closure forms
only when captures are needed.

## Make Non-Overlap Explicit

Ordinary memory-backed parameters are non-overlapping by default. Use
`where overlap(...)` when the function deliberately accepts overlapping
regions, and `if disjoint(...)` when a mixed API can branch to a faster path.

```stark
fn void AddSeparate(
    borrow i32[min max][] left,
    borrow i32[min max][] right,
    borrow mut i32[min max][] output)
{
    return;
}

fn bool TryAddFast(
    borrow i32[min max][] left,
    borrow i32[min max][] right,
    borrow mut i32[min max][] output)
    where overlap(left, output), overlap(right, output)
{
    if disjoint(left, output)
    {
        if disjoint(right, output)
        {
            AddSeparate(left, right, output);
            return true;
        }
    }

    return false;
}
```

Use the least permissive contract that matches the API.

## Mark Independent Loops Only With Proof

Use `independent` when iterations do not read or write values produced by
other iterations.

```stark
fn void AddOne(
    borrow i32[min max][] input,
    borrow mut i32[min max][] output,
    u64[0 max] length)
{
    for willexit independent (stack mut u64[0 max] index = 0; index < length; index += 1)
    {
        output[index] = input[index] + 1;
    }

    return;
}
```

Avoid `independent` when there are loop-carried dependencies, early exits,
unclear call effects, or hidden shared state.

## Bound Raw Pointer Loops

For unsafe contiguous memory, prefer `rawptr<T>[count]` and
`rawmutptr<T>[count]`.

```stark
unsafe fn void Fill(
    i64[0 max] length,
    rawmutptr<i32[min max]>[length] destination,
    i32[min max] value)
{
    stack mut i32[min max][] view = slice(destination, length);

    for willexit independent (stack mut i64[0 max] index = 0; index < length; index += 1)
    {
        view[index] = value;
    }

    return;
}
```

The region bound makes the pointer shape reviewable and lets memory contracts
name subregions.

## Use Const For Deep Read-Only Data

Use `const` parameters when the reachable object graph is deeply immutable.

```stark
struct Table
{
    i32[min max][3] Values;
}

finite law i32[min max] ReadMiddle(const Table table)
{
    return table.Values[1];
}
```

`const` is a read-only promise, not a non-overlap promise. Combine it with
memory contracts only when separation also matters.

## Keep Allocation Visible

Growable collections and owned text can allocate. Let the API return
allocation status when growth may fail.

```stark
import System.Collections
import System.Memory

fn bool PushScore(mut borrow List<i32[min max]> scores, i32[min max] score)
{
    stack MemoryStatus pushed = scores.Push(score);
    switch (pushed)
    {
        case MemoryStatus.Ok:
            return true;
        case MemoryStatus.Err(var error):
            return false;
    }
}
```

Use fixed-capacity text when the maximum size is part of the function.

```stark
fn Ascii SmallLabel(i32[min max] value)
{
    stack Ascii label[64] = $"Value: {value}";
    return label;
}
```

## Use Function Modifiers Deliberately

```stark
inline finite law i32[min max] ClampToZero(i32[min max] value)
{
    if (value < 0)
    {
        return 0;
    }

    return value;
}

hot fn i32[min max] ScoreHotPath(i32[min max] value)
{
    return ClampToZero(value) + 1;
}

noinline cold fn i32[min max] ReportUnexpected(i32[min max] value)
{
    return value;
}
```

- Use `inline` for tiny wrappers on hot paths.
- Use `inlinehint` when inlining is usually helpful but should remain a hint.
- Use `noinline` for setup, platform calls, diagnostics, and code-size
  boundaries.
- Use `hot` only for code that actually sits on the hot path.
- Use `cold` for uncommon failure or reporting paths.

## Pick Numeric Semantics Directly

Ordinary arithmetic is for cases where overflow is not part of the intended
operation.

```stark
left + right
left - right
left * right
```

Use wrapping when wraparound is the rule:

```stark
hash = (hash *% 16777619) +% value;
```

Use saturating arithmetic when clamping is the rule:

```stark
progress = progress +| step;
```

Use `strictfp` only when strict floating-point behavior is part of the API.

```stark
strictfp finite law f64 StrictRatio(f64 used, f64 total)
{
    return used / total;
}
```

Use ordinary floating point for code where fast numeric behavior is acceptable.

## Benchmark Checklist

- Build with the intended profile, usually release.
- Name the benchmark after the source work being timed.
- Keep setup outside the timed function unless setup is the workload.
- Compare the same programmer-facing task across languages.
- Keep output, logging, parsing, and file IO out of kernels unless intentional.
- Use fixed arrays, slices, and explicit buffers when the workload is fixed.
- Handle allocation status when the workload includes growth or owned text.
- Use direct calls for kernels unless callback indirection is the workload.
- Inspect generated output only after the source task is confirmed comparable.
