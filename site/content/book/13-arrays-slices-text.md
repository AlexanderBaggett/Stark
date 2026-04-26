+++
title = "13. Arrays, Slices, Text, and Views"
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

# Arrays, Slices, Text, and Views

Arrays, slices, and text all deal with contiguous data, but they do not have the
same ownership story. Stark keeps that distinction visible.

{{< stark-sample "assets/book/samples/arrays-text-views.stark" >}}

## Fixed Arrays Own Their Elements

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

## Slices Are Views

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

## Text Views

`ascii` is a UTF-8 text view. `unicode` is a UTF-32 text view.

Text indexing and slicing create views of the same text kind:

```stark
text[]
text[index]
text[start, length]
```

In the sample, `text[0]` returns a one-element `ascii` view, so it can be
compared with `"s"` or returned as `ascii`.

## Owned Text

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

## Formatting And Parsing

The standard text APIs expose conversion costs and failure:

- fixed-buffer formatting writes into caller-owned `Ascii` or `Unicode`
- owned formatting returns an allocation-aware result type
- parsing returns result/status data instead of throwing

This keeps text processing aligned with the rest of Stark: allocation, storage
capacity, and recoverable failure are visible in the API.
