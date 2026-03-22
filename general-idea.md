# Stark Lang
This langauge is intended to be faster than C or Rust.
This language designed to be brutally fast by specifying all of the LLVM optimizations as built-in language features.
In many cases this language will restrict most things or require specificity about most things in order to achieve bleeding edge performance.

This language will be more restrictive than Rust and Haskell and possibly harder to understand. And more restrictive then C99 with restricted mode
This language will envitably be extrememly verbose in order to convey as much info as possible to the LLVM compiler backend.

Speed, performance and memory usage are priorities. Ease of use, is very low on the list.

Since this langauge will inevitably hard to use, we would like to provide SOME syntactic sugar in scenarios where it saves the developer time, without adding hidden bloat. 

And it is designed to be as fast and as optimized as possible and needs to beat C performance on most benchmarks either through restrictions or intense specificity that correlates 1 to 1 with LLVM Compiler options, or by the fact that most unperformant IR can't be generated because of the restrictiveness


try to always use fastcc

## Functions
- 3 types
- Keywords for different types
- 1. finite (translates to mustprogress and willreturn)
- 2. law (pure, no side effects, readonly garuntees)
- 3. finite law (both)
- 4. fn (everything else)
### Additional function keywords
- Inline, 
- NoInline, 
- InlineHint (the default) 
- hot, 
- cold
- ffi (prevents the default fastcc that is normally used)
- inline, noinline, and inlinehint are mutually exclusive.
- hot and cold are mutually exclusive.
- cold should not imply coldcc automatically unless you are very sure.
- nounwind should still be inferred/emitted by default on everything internal that qualifies, not made a user  - keyword.

## Loops
- Keywords for or while + either (infinite, non-deterministic, willexit )
- for loops otherwise identical to C-style for loops


## Syntax 
- Like C# but without classes or public/private modifiers
- with some python-like syntatic sugar and expansions but only ones that have no additional overhead cost.
- uninitialized variables are not allowed. e.g no  int x; or somestruct y;

## Branching 
- if/else
- switch 
- pattern matching
- can assign branch weights for performance tuning w1, w99 etc

## Pointers
- Must declare Alias, NoAlias, Unique, Readonly, WriteOnly
- The Unique Constraint: A pointer can be marked as "Unique." If a function takes a Unique array, the compiler ensures that no other part of the program holds a reference to that array.
- No pointers to pointers
- Pointers can and must be freed in the scope they are declared
- Pointers must declare Local or NonLocal when passing to a function


## No Nulls ever
- Don't exist
- you either succeeded and got back your result
- or you succeeded and got back your empty array of values
- strings being arrays of chars are empty e.g. ""
- 

## Poison values
- array indices are pointer-width (no poison values or widening)

# Borrower System
- See markdown document BorrowerSystem.md

## Structs
- Regular Structs
    - can contain functions
        - works like rust

## Records
- Data only

## Traits
- Same as Rust, but with C#-style syntax

## Doctorine
- a group of law functions
- bundle of implementations with no owned data.
- compile-time only
- no identity
- cannot be heap-allocated
- cannot capture environment
- static dispatch by default
- specializable when passed as a generic parameter
- purity / no side effects / 
- readonly guarantees
- Use C# class syntax

## Mutability
- Everything is immutable by default
- Can bail out with `mut` keyword

## Syntax 
- Like C# but without classes or public/private modifiers
- with some python-like syntatic sugar and expansions but only ones that have no additional overhead cost.
- uninitialized variables are not allowed. e.g no  int x; or somestruct y;


## Operators
- standard + - % / * < > = == != <= >= ? && || & | operators
- all bit operators, bit shifting etc
- indexing arrays and queues via []
- any operator standard in C is available here
- 

## Arithmatic
- make default overflow UB/illegal
- unchecked
- saturation and wrapping operators exist and are explicit
- ^ operator for exponents


## Semi Formal Grammar (incomplete)        


1. Types
   1. Integers
      1. i +  one of the following (essentially powers of 2 with 1 step in between)
         1. 2
         2. 4
         4. 8
         6. 16
         8. 24
         9. 32
         10. 48
         11. 64
         12. 96
         13. 128
         14. 192
         15. 256
         16. 384
         17. 512
         18. 768
         19. 1024
      2. + a range
         1. `[` some-integer some-integer `]`

      
   2. Floating point  
      1.  Emit `fast` LLVM IR on all calls unless ffi  
      2. Required one or none of  
         1. ffi (disables fast)  
      3. f \+ one of the following  
         1. 16  
         2. 32  
         3. 64  
         4. 80  
         5. 128  

2. Allocation  
   1. Required One of  
      1. stack  
      2. heap  
      3. register  
      4. static  
      5. arena
   2. Required 0 or 1 of
      1. mut (mutability like Rust)

3. Functions  
   1. Calling convention attribute (required)  
      1. Required 0 or 1 of  
         1. (fastcc)  defaulted on  
         2. ffi  
   2. Attributes  
      1. Inlining  
         1. Required 0 or 1 of  
            1. inline  
            2. noinline  
            3. cold  
            4. hot  
            5. (inlinehint) the default  

      3. Value Attributes  
         1. noundef (defaulted on)  

         
### Model 1: No Exceptions (`nounwind` everywhere)