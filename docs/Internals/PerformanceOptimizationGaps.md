# Performance Optimization Gaps

These tasks track places where the language front-end accepts or guarantees
performance-relevant facts, but the compiler does not carry the full fact through
MIR, SSA, ABI lowering, and LLVM emission. The fixes should implement the real
compiler behavior rather than adding benchmark-specific workarounds.

No open optimization gaps are currently tracked here.
