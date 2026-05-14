+++
title = "13. Arrays, Slices, Dynamic Storage, Text, and Views"
weight = 130
book_part = "Part II: Stark's Core Language"
book_status = "draft"
prev = "/book/12-enums-patterns/"
next = "/book/14-modules-visibility-packages/"

[[language_refs]]
title = "Language Reference"
href = "/reference/language/LanguageReference/"

[[stdlib_refs]]
title = "System.Text"
href = "/reference/standard-library/System.Text/"

[[example_refs]]
title = "Type System Examples"
href = "/reference/examples/type-system/TypeSystem.stark"

[[example_refs]]
title = "Standard Library Examples"
href = "/reference/examples/standard-library/StandardLibrary.stark"
+++

# Arrays, Slices, Dynamic Storage, Text, and Views

Arrays, slices, dynamic storage, and text all deal with contiguous data, but
they do not have the same ownership story. Stark keeps that distinction visible.

{{< stark-sample "assets/book/samples/arrays-text-views.stark" >}}

## Step 1: Start With Fixed Storage

`T[N]` is a fixed array with `N` elements:

```stark
stack i32[min max][3] values = { 1, 2, 3 };
```

The backing storage is real storage owned by the array value. Indexing uses the
same postfix form as other C-family languages:

```stark
values[0]
values[1]
values[2]
```

Omitted trailing fixed-array elements are zero-initialized when the target
array size is known.

## Step 2: Borrow Views Instead Of Inventing Backing Storage

`T[]` is a slice view. It refers to backing storage created elsewhere. It does
not allocate and it does not secretly create an array.

That means this intentionally invalid example is rejected:

{{< stark-sample "assets/book/negative-samples/slice-literal-hidden-storage.stark" >}}

The fix is to write the backing storage first, then form a view:

```stark
stack i32[min max][3] values = { 1, 2, 3 };
stack i32[min max][] view = values;
```

That is the Stark rule in miniature: if storage exists, make it visible.

## Step 3: Use `dynamic T` When Capacity Must Grow

`dynamic T` is owned, growable storage for elements of `T`. It has a visible
`Length`, a visible `Capacity`, and spare slots that can be initialized without
turning the implementation into public raw pointer code.

{{< stark-sample "assets/book/samples/dynamic-storage.stark" >}}

The important operations are direct:

- `Reserve(additional)` preserves initialized elements and makes room for more
- `TryReserve(additional)` does the same growth work but returns `false` instead
  of trapping when capacity or allocation fails
- `init items[index] = value` constructs an element in spare storage
- `items[0, items.Length]` creates a normal initialized slice view
- `MoveLast()` moves the tail element out and decrements `Length`
- `MoveAt(index)` moves one initialized element out, shifts the later suffix
  left, and decrements `Length`

This is the safe shape for vector-like storage. The compiler can see the owner,
the initialized prefix, the spare-capacity writes, and the element type.

The same storage shape works for collection backing stores, byte builders, and
path/text buffers. The public surface can stay slice-shaped while the
implementation keeps spare capacity private:

{{< stark-sample "assets/book/samples/dynamic-storage-patterns.stark" >}}

## Step 4: Keep Text Views Zero-Copy

`ascii` is a UTF-8 text view. `unicode` is a UTF-32 text view.

Text indexing and slicing create views of the same text kind:

```stark
text[]
text[index]
text[start, length]
```

In the sample, `text[0]` returns a one-element `ascii` view, so it can be
compared with `"s"` or returned as `ascii`.

## Step 5: Choose Owned Text When Storage Must Survive

`Ascii` and `Unicode` are owning text container forms. Use them when code needs
owned text storage rather than a borrowed view into existing text.

When runtime text needs caller-owned destination storage, Stark asks for the
capacity in the source:

```stark
stack Ascii label[64] = $"Score: {score}";
```

The capacity is part of the local storage choice. If the operation does not fit
the selected destination, Stark does not silently truncate and does not throw a
hidden exception.

This checked sample formats the same numeric value into caller-owned ASCII and
Unicode buffers:

{{< stark-sample "assets/book/stdlib-samples/text-formatting.stark" >}}

## Step 6: Format And Parse Through Explicit APIs

The standard text APIs expose conversion costs and failure:

- fixed-buffer formatting writes into caller-owned `Ascii` or `Unicode`
- owned formatting returns an allocation-aware result type
- parsing returns result/status data instead of throwing

This keeps text processing aligned with the rest of Stark: allocation, storage
capacity, and recoverable failure are visible in the API.
