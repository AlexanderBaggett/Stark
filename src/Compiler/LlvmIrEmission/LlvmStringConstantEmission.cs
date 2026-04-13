namespace Stark.Compiler.LlvmIrEmission;

internal sealed record EmittedStringConstant(
    string SymbolName,
    string ArrayType,
    string Initializer,
    int DataLength,
    int AlignmentBytes);
