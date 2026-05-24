+++
title = "18. Generics, Traits, and Doctrines"
weight = 180
book_part = "Part III: Packages, Effects, and Boundaries"
book_status = "draft"
prev = "/book/17-errors-without-exceptions/"
next = "/book/19-callable-values/"
aliases = ["/book/17-generics-traits-doctrines/"]

[[language_refs]]
title = "Language Reference"
href = "/reference/language/LanguageReference/"
+++

# Generics, Traits, and Doctrines

Generics let you write one function or type that works with more than one
concrete type. Traits and doctrines let you name behavior requirements, so
reusable code can say what it needs instead of pretending every type supports
every operation.

The formal reference points are:

- generic parameters: Language Reference 6.5
- traits: Language Reference 8.5
- doctrines: Language Reference 8.6

This chapter focuses on how to read and write the source code.

{{< stark-sample "assets/book/samples/generics-basics.stark" >}}

## Step 1: Write A Generic Function

Generic parameters are written after the function name:

```stark
fn T Identity<T>(T value)
{
    return value;
}
```

Inside the function, `T` is a type name. Here it appears as the return type and
as the parameter type.

When the generic type appears in an argument, Stark can usually infer it from
the call:

```stark
stack i32[min max] answer = Identity(42);
stack bool flag = Identity(true);
```

`Identity(42)` uses `T` as `i32[min max]`. `Identity(true)` uses `T` as
`bool`.

If Stark cannot infer the type argument from the call, rewrite the API so the
type appears in a parameter or use a less generic function. Do not hide the only
important type information in the return value.

## Step 2: Write A Generic Type

Structs, records, and enums can also be generic:

```stark
struct Box<T>
{
    T Value;
}

record Pair<A, B>
{
    A First;
    B Second;
}

enum Option<T>
{
    None,
    Some(T),
}
```

When you declare a value, write the type arguments:

```stark
stack Box<i32[min max]> box = new Box<i32[min max]>()
{
    Value = 7
};
stack Pair<i32[min max], bool> pair = new Pair<i32[min max], bool>()
{
    First = 10,
    Second = true
};
stack Option<bool> flag = Option<bool>.Some(true);
```

`Box<T>` is the reusable type declaration. `Box<i32[min max]>` is the type you
actually use for this value.

{{< stark-sample "assets/book/samples/generic-option.stark" >}}

Generic enums work like ordinary enums. Write the concrete enum type before the
variant name:

```stark
Option<i32[min max]>.Some(7)
Option<bool>.Some(true)
```

Use the same pattern for result-shaped enums:

```stark
enum Result<T, E>
{
    Ok(T),
    Error(E),
}
```

The caller still chooses real types for `T` and `E`.

Generic aliases can give a common shape a shorter name:

```stark
alias View<T> = borrow T[];
alias MutableView<T> = mut borrow T[];
```

Use aliases to clarify a repeated type shape, not to hide ownership or mutability
from readers.

## Step 3: Keep Generic Bodies Honest

A generic function can only use operations that are valid for its parameters.
This works because returning a value does not require any behavior from `T`:

```stark
fn T Forward<T>(T value)
{
    return value;
}
```

This is not a good unconstrained generic body:

```stark
fn T Add<T>(T left, T right)
{
    return left + right;
}
```

Not every type has `+`. If you want reusable addition, use a contract that says
which operation is available, or write concrete overloads for the types you
support.

That habit matters in Stark: generic code should say what it needs.

## Step 4: Use Traits To Name Required Behavior

A trait declares a behavior contract:

```stark
trait Reader<T>
{
    finite law T Read();
}
```

The member ends with `;` because the trait states a requirement. It does not
provide the body.

Traits are useful when an API needs to name a required operation:

```stark
trait Parser<T>
{
    law T Parse(ascii text);
}
```

A trait may use the type parameter in parameters, returns, or both:

```stark
trait Comparator<T>
{
    finite law i32[min max] Compare(borrow T left, borrow T right);
}
```

Traits are not runtime values. Do not put a trait in a field, local, parameter,
or return type. Do not construct one with `new`. If you need a callable runtime
value, use the callable forms from Chapter 19 instead.

This rejected example shows the boundary:

{{< stark-sample "assets/book/negative-samples/trait-runtime-value.stark" >}}

## Step 5: Use Doctrines For Named Law Helpers

Doctrines group `law` and `finite law` functions under one name.

Use a doctrine when the grouped functions have bodies and callers should call
them by name:

```stark
doctrine ScoreRules
{
    finite law bool IsPassing(u8[0 100] score)
    {
        return score >= 70;
    }
}

fn bool CheckPassing()
{
    return ScoreRules.IsPassing(85);
}
```

The full sample adds a second law and checks both paths:

{{< stark-sample "assets/book/samples/doctrine-rules.stark" >}}

Doctrines can be generic too:

```stark
struct Box<T>
{
    T Value;
}

doctrine Inspect<T>
{
    finite law T Read(borrow Box<T> box)
    {
        return box.Value;
    }
}

finite law i32[min max] ReadInt(borrow Box<i32[min max]> box)
{
    return Inspect<i32[min max]>.Read(box);
}
```

As with generic types, write the concrete type arguments when you call a generic
doctrine member.

Doctrines can return status values like any other `law` or `finite law`
function:

```stark
doctrine PercentRules
{
    finite law bool IsValid(u8[0 max] value)
    {
        return value <= 100;
    }

    finite law u8[0 100] Clamp(u8[0 max] value)
    {
        if (value > 100)
        {
            return 100;
        }

        return (u8[0 100])value;
    }
}
```

Like traits, doctrines are not runtime objects. You call their members through
the doctrine name; you do not allocate or store the doctrine itself.

## Step 6: Choose The Right Tool

Use a generic function when the same body truly works for multiple types:

```stark
fn T First<T>(T left, T right)
{
    return left;
}
```

Use a generic type when the shape is the same but the stored value changes:

```stark
struct Pair<A, B>
{
    A First;
    B Second;
}
```

Use a trait when you need to name a required behavior:

```stark
trait Hashable
{
    law u32[0 max] Hash();
}
```

Use a doctrine when you want a named group of law functions with bodies:

```stark
doctrine ScoreRules
{
    finite law bool IsPassing(u8[0 100] score)
    {
        return score >= 70;
    }
}
```

The practical rule is simple: make the type parameter visible, state required
behavior explicitly, and keep traits and doctrines out of ordinary runtime value
positions.
