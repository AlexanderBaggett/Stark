+++
title = "35. Inspecting Generated Output"
weight = 350
book_part = "Part V: Performance and Systems Programming"
book_status = "draft"
prev = "/book/34-reading-diagnostics/"
next = "/book/36-project-command-line-text/"
aliases = ["/book/29-generated-ir/", "/book/31-generated-ir/", "/book/32-generated-ir/"]
+++

# Inspecting Generated Output

Most Stark programmers should not need to read generated output every day. This
chapter treats output inspection as a lab exercise: choose one source rule,
emit the output, and look only for the evidence related to that question.

## Step 1: Ask One Performance Question

Inspect generated output only after you can phrase the question narrowly:

- did this direct call stay direct?
- did this generic function get used with the expected concrete type?
- did this fixed array stay free of hidden allocation?
- did this range show up in the output shape you expected?
- did an FFI boundary stay small and explicit?

Do not use generated output as the first way to learn ordinary Stark. Source
behavior comes first. Output inspection is a debugging and performance tool.

Start from a tiny source question:

```stark
inline finite law i32[min max] Double(i32[min max] value)
{
    return value * 2;
}

finite law i32[min max] UseDouble(i32[min max] value)
{
    return Double(value) + 1;
}
```

The question is not "what does everything look like?" It is "does this tiny
helper stay out of the way of the arithmetic I care about?"

For storage, write the question just as narrowly:

```stark
finite law i32[min max] First(i32[min max][4] values)
{
    return values[0];
}
```

Now the inspection target is whether the fixed array stays as fixed storage,
not whether every surrounding symbol name looks nice.

## Step 2: Pick The Right Output

Stark can expose different output views:

- a source-shape view is useful when checking whether a source construct kept
  the expected shape
- a dataflow view is useful when checking constants and simplified control flow
- low-level output is useful when checking native calls and interop shape

Generated names are not a user-facing promise. Prefer looking for behavioral
questions: direct call versus indirect call, allocation versus no allocation,
return value versus out pointer, or raw boundary versus safe API.

## Step 3: Compile A Tiny Checked Program

Use a tiny checked program when inspecting output:

{{< stark-sample "assets/book/samples/performance-tight-loop.stark" >}}

If the question is about function attributes, use a sample that names the
source rules directly:

{{< stark-sample "assets/book/samples/function-guarantees.stark" >}}

Build one small question at a time. For example:

```bash
dotnet run --project src -- site/assets/book/samples/performance-tight-loop.stark --emit-llvm
```

Then look for the shape you care about. For this sample, useful questions
include:

- `SumFixed` and `HasExpectedTotal` are direct Stark functions
- the loop counter and total are explicit local scalar values
- the fixed array is source-visible storage
- no collection, text, or allocator API is involved
- the entrypoint is a safe `export fn main`

The point is not to memorize generated names. The point is to connect a
specific source rule to a specific output question.

## Step 4: Keep The Question Small

Generated output is noisy. Inspect one thing at a time:

- is this call direct or indirect?
- is this storage stack, heap, or caller-owned?
- does this FFI wrapper stay at the edge?
- did this operation allocate?
- did this result return directly or through an `out` destination?

Match each question to a small source pattern:

```stark
fn bool TryWrite(out i32[min max] result)
{
    result = 42;
    return true;
}
```

For that snippet, the useful question is whether the result uses the explicit
destination you wrote.

For FFI, keep the wrapper equally small:

```stark
unsafe ffi fn i32[min max] native_value();

unsafe fn i32[min max] ReadValue()
{
    return native_value();
}
```

The useful question is whether the foreign call remains at the wrapper
boundary and ordinary Stark callers stay outside it.

## Step 5: Return To Source-Level Explanations

Generated output is excellent for investigation, but normal user-facing
documentation should teach the source rule first. Use output inspection only
when the reader has deliberately opted into it.
