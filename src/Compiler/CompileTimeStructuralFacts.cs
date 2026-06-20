using System.Numerics;
using Antlr4.Runtime;
using Stark.Parsing;

namespace Stark.Compiler;

internal enum CompileTimeStructuralFactKind
{
    IsBool,
    IsInteger,
    IsFloat,
    IsRawPointer,
    IsFixedArray,
    IsSlice,
    IsDynamic,
    IsFunctionPointer,
    IsClosure,
    IsDynTrait,
    IsNamed,
    IsStruct,
    IsRecord,
    IsEnum,
    IsTrait,
    IsDoctrine,
    HasConcreteLayout,
    TypeSize,
    TypeAlign,
    TypeIsZeroSized,
    TypeIntegerBitWidth,
    TypeFloatBitWidth,
    TypeIntegerIsSigned,
    TypeIntegerIsUnsigned,
    TypeIntegerIsFullRange,
    TypeIntegerMinIs,
    TypeIntegerMaxIs,
    RawPointerElementTypeIs,
    RawPointerElementTypeIsBool,
    RawPointerElementTypeIsInteger,
    RawPointerElementTypeIsFloat,
    RawPointerElementTypeIsRawPointer,
    RawPointerElementTypeIsFixedArray,
    RawPointerElementTypeIsSlice,
    RawPointerElementTypeIsDynamic,
    RawPointerElementTypeIsFunctionPointer,
    RawPointerElementTypeIsClosure,
    RawPointerElementTypeIsDynTrait,
    RawPointerElementTypeIsNamed,
    RawPointerElementTypeIsStruct,
    RawPointerElementTypeIsRecord,
    RawPointerElementTypeIsEnum,
    RawPointerElementTypeIsTrait,
    RawPointerElementTypeIsDoctrine,
    RawPointerElementTypeHasConcreteLayout,
    RawPointerElementTypeHasCSourceAlias,
    RawPointerElementTypeCSourceAliasName,
    RawPointerIsMutable,
    RawPointerIsReadOnly,
    TypeElementTypeIs,
    TypeElementTypeIsBool,
    TypeElementTypeIsInteger,
    TypeElementTypeIsFloat,
    TypeElementTypeIsRawPointer,
    TypeElementTypeIsFixedArray,
    TypeElementTypeIsSlice,
    TypeElementTypeIsDynamic,
    TypeElementTypeIsFunctionPointer,
    TypeElementTypeIsClosure,
    TypeElementTypeIsDynTrait,
    TypeElementTypeIsNamed,
    TypeElementTypeIsStruct,
    TypeElementTypeIsRecord,
    TypeElementTypeIsEnum,
    TypeElementTypeIsTrait,
    TypeElementTypeIsDoctrine,
    TypeElementTypeHasConcreteLayout,
    TypeElementTypeHasCSourceAlias,
    TypeElementTypeCSourceAliasName,
    TypeFixedArrayLength,
    TypeFixedArrayLengthIs,
    TypeHasQualifiers,
    TypeBorrowKindIsNone,
    TypeBorrowKindIsBorrow,
    TypeBorrowKindIsRetBorrow,
    TypeBorrowKindIsStoreBorrow,
    TypeAccessKindIsNone,
    TypeAccessKindIsShared,
    TypeAccessKindIsFrozen,
    TypeInitializationKindIsNone,
    TypeInitializationKindIsOut,
    TypeInitializationKindIsInit,
    TypeIsMutableView,
    TypeUnqualifiedTypeIs,
    FunctionPointerParameterCount,
    FunctionPointerReturnTypeIs,
    FunctionPointerParameterTypeIs,
    FunctionPointerReturnTypeIsBool,
    FunctionPointerReturnTypeIsInteger,
    FunctionPointerReturnTypeIsFloat,
    FunctionPointerReturnTypeIsRawPointer,
    FunctionPointerReturnTypeIsFixedArray,
    FunctionPointerReturnTypeIsSlice,
    FunctionPointerReturnTypeIsDynamic,
    FunctionPointerReturnTypeIsFunctionPointer,
    FunctionPointerReturnTypeIsClosure,
    FunctionPointerReturnTypeIsDynTrait,
    FunctionPointerReturnTypeIsNamed,
    FunctionPointerReturnTypeIsStruct,
    FunctionPointerReturnTypeIsRecord,
    FunctionPointerReturnTypeIsEnum,
    FunctionPointerReturnTypeIsTrait,
    FunctionPointerReturnTypeIsDoctrine,
    FunctionPointerReturnTypeHasConcreteLayout,
    FunctionPointerParameterTypeIsBool,
    FunctionPointerParameterTypeIsInteger,
    FunctionPointerParameterTypeIsFloat,
    FunctionPointerParameterTypeIsRawPointer,
    FunctionPointerParameterTypeIsFixedArray,
    FunctionPointerParameterTypeIsSlice,
    FunctionPointerParameterTypeIsDynamic,
    FunctionPointerParameterTypeIsFunctionPointer,
    FunctionPointerParameterTypeIsClosure,
    FunctionPointerParameterTypeIsDynTrait,
    FunctionPointerParameterTypeIsNamed,
    FunctionPointerParameterTypeIsStruct,
    FunctionPointerParameterTypeIsRecord,
    FunctionPointerParameterTypeIsEnum,
    FunctionPointerParameterTypeIsTrait,
    FunctionPointerParameterTypeIsDoctrine,
    FunctionPointerParameterTypeHasConcreteLayout,
    FunctionPointerReturnTypeDisplayName,
    FunctionPointerReturnTypeBaseName,
    FunctionPointerReturnTypeModuleName,
    FunctionPointerReturnTypeIsGenericInstantiation,
    FunctionPointerReturnTypeArgumentCount,
    FunctionPointerReturnTypeComptimeArgumentCount,
    FunctionPointerReturnTypeHasCSourceAlias,
    FunctionPointerReturnTypeCSourceAliasName,
    FunctionPointerReturnTypeHasQualifiers,
    FunctionPointerReturnTypeBorrowKindIsNone,
    FunctionPointerReturnTypeBorrowKindIsBorrow,
    FunctionPointerReturnTypeBorrowKindIsRetBorrow,
    FunctionPointerReturnTypeBorrowKindIsStoreBorrow,
    FunctionPointerReturnTypeAccessKindIsNone,
    FunctionPointerReturnTypeAccessKindIsShared,
    FunctionPointerReturnTypeAccessKindIsFrozen,
    FunctionPointerReturnTypeInitializationKindIsNone,
    FunctionPointerReturnTypeInitializationKindIsOut,
    FunctionPointerReturnTypeInitializationKindIsInit,
    FunctionPointerReturnTypeIsMutableView,
    FunctionPointerReturnTypeUnqualifiedTypeIs,
    FunctionPointerParameterTypeDisplayName,
    FunctionPointerParameterTypeBaseName,
    FunctionPointerParameterTypeModuleName,
    FunctionPointerParameterTypeIsGenericInstantiation,
    FunctionPointerParameterTypeArgumentCount,
    FunctionPointerParameterTypeComptimeArgumentCount,
    FunctionPointerParameterTypeHasCSourceAlias,
    FunctionPointerParameterTypeCSourceAliasName,
    FunctionPointerParameterTypeHasQualifiers,
    FunctionPointerParameterTypeBorrowKindIsNone,
    FunctionPointerParameterTypeBorrowKindIsBorrow,
    FunctionPointerParameterTypeBorrowKindIsRetBorrow,
    FunctionPointerParameterTypeBorrowKindIsStoreBorrow,
    FunctionPointerParameterTypeAccessKindIsNone,
    FunctionPointerParameterTypeAccessKindIsShared,
    FunctionPointerParameterTypeAccessKindIsFrozen,
    FunctionPointerParameterTypeInitializationKindIsNone,
    FunctionPointerParameterTypeInitializationKindIsOut,
    FunctionPointerParameterTypeInitializationKindIsInit,
    FunctionPointerParameterTypeIsMutableView,
    FunctionPointerParameterTypeUnqualifiedTypeIs,
    FunctionPointerReturnTypeArgumentTypeIs,
    FunctionPointerReturnTypeArgumentTypeIsBool,
    FunctionPointerReturnTypeArgumentTypeIsInteger,
    FunctionPointerReturnTypeArgumentTypeIsFloat,
    FunctionPointerReturnTypeArgumentTypeIsRawPointer,
    FunctionPointerReturnTypeArgumentTypeIsFixedArray,
    FunctionPointerReturnTypeArgumentTypeIsSlice,
    FunctionPointerReturnTypeArgumentTypeIsDynamic,
    FunctionPointerReturnTypeArgumentTypeIsFunctionPointer,
    FunctionPointerReturnTypeArgumentTypeIsClosure,
    FunctionPointerReturnTypeArgumentTypeIsDynTrait,
    FunctionPointerReturnTypeArgumentTypeIsNamed,
    FunctionPointerReturnTypeArgumentTypeIsStruct,
    FunctionPointerReturnTypeArgumentTypeIsRecord,
    FunctionPointerReturnTypeArgumentTypeIsEnum,
    FunctionPointerReturnTypeArgumentTypeIsTrait,
    FunctionPointerReturnTypeArgumentTypeIsDoctrine,
    FunctionPointerReturnTypeArgumentTypeHasConcreteLayout,
    FunctionPointerReturnTypeArgumentTypeDisplayName,
    FunctionPointerReturnTypeArgumentTypeBaseName,
    FunctionPointerReturnTypeArgumentTypeModuleName,
    FunctionPointerReturnTypeArgumentTypeIsGenericInstantiation,
    FunctionPointerReturnTypeArgumentTypeArgumentCount,
    FunctionPointerReturnTypeArgumentTypeComptimeArgumentCount,
    FunctionPointerParameterTypeArgumentTypeIs,
    FunctionPointerParameterTypeArgumentTypeIsBool,
    FunctionPointerParameterTypeArgumentTypeIsInteger,
    FunctionPointerParameterTypeArgumentTypeIsFloat,
    FunctionPointerParameterTypeArgumentTypeIsRawPointer,
    FunctionPointerParameterTypeArgumentTypeIsFixedArray,
    FunctionPointerParameterTypeArgumentTypeIsSlice,
    FunctionPointerParameterTypeArgumentTypeIsDynamic,
    FunctionPointerParameterTypeArgumentTypeIsFunctionPointer,
    FunctionPointerParameterTypeArgumentTypeIsClosure,
    FunctionPointerParameterTypeArgumentTypeIsDynTrait,
    FunctionPointerParameterTypeArgumentTypeIsNamed,
    FunctionPointerParameterTypeArgumentTypeIsStruct,
    FunctionPointerParameterTypeArgumentTypeIsRecord,
    FunctionPointerParameterTypeArgumentTypeIsEnum,
    FunctionPointerParameterTypeArgumentTypeIsTrait,
    FunctionPointerParameterTypeArgumentTypeIsDoctrine,
    FunctionPointerParameterTypeArgumentTypeHasConcreteLayout,
    FunctionPointerParameterTypeArgumentTypeDisplayName,
    FunctionPointerParameterTypeArgumentTypeBaseName,
    FunctionPointerParameterTypeArgumentTypeModuleName,
    FunctionPointerParameterTypeArgumentTypeIsGenericInstantiation,
    FunctionPointerParameterTypeArgumentTypeArgumentCount,
    FunctionPointerParameterTypeArgumentTypeComptimeArgumentCount,
    FunctionPointerParameterHasRawPointerElementCountExpression,
    FunctionPointerParameterRawPointerElementCountExpression,
    FunctionPointerKindIsFn,
    FunctionPointerKindIsFinite,
    FunctionPointerKindIsLaw,
    FunctionPointerKindIsFiniteLaw,
    FunctionPointerIsUnsafe,
    FunctionPointerHasFfiAbi,
    FunctionPointerAbiIsC,
    FunctionPointerAbiIsCDecl,
    FunctionPointerAbiIsStdCall,
    FunctionPointerAbiIsFastCall,
    FunctionPointerAbiIsThisCall,
    FunctionPointerAbiIsVectorCall,
    FunctionPointerAbiIsSysV,
    FunctionPointerAbiIsWin64,
    FunctionPointerAbiIsAapcs,
    FunctionPointerAbiIsAapcs64,
    FunctionPointerParametersAreDisjoint,
    FunctionPointerParametersOverlap,
    FunctionPointerParametersAreSame,
    ClosureParameterCount,
    ClosureReturnTypeIs,
    ClosureParameterTypeIs,
    ClosureReturnTypeIsBool,
    ClosureReturnTypeIsInteger,
    ClosureReturnTypeIsFloat,
    ClosureReturnTypeIsRawPointer,
    ClosureReturnTypeIsFixedArray,
    ClosureReturnTypeIsSlice,
    ClosureReturnTypeIsDynamic,
    ClosureReturnTypeIsFunctionPointer,
    ClosureReturnTypeIsClosure,
    ClosureReturnTypeIsDynTrait,
    ClosureReturnTypeIsNamed,
    ClosureReturnTypeIsStruct,
    ClosureReturnTypeIsRecord,
    ClosureReturnTypeIsEnum,
    ClosureReturnTypeIsTrait,
    ClosureReturnTypeIsDoctrine,
    ClosureReturnTypeHasConcreteLayout,
    ClosureParameterTypeIsBool,
    ClosureParameterTypeIsInteger,
    ClosureParameterTypeIsFloat,
    ClosureParameterTypeIsRawPointer,
    ClosureParameterTypeIsFixedArray,
    ClosureParameterTypeIsSlice,
    ClosureParameterTypeIsDynamic,
    ClosureParameterTypeIsFunctionPointer,
    ClosureParameterTypeIsClosure,
    ClosureParameterTypeIsDynTrait,
    ClosureParameterTypeIsNamed,
    ClosureParameterTypeIsStruct,
    ClosureParameterTypeIsRecord,
    ClosureParameterTypeIsEnum,
    ClosureParameterTypeIsTrait,
    ClosureParameterTypeIsDoctrine,
    ClosureParameterTypeHasConcreteLayout,
    ClosureReturnTypeDisplayName,
    ClosureReturnTypeBaseName,
    ClosureReturnTypeModuleName,
    ClosureReturnTypeIsGenericInstantiation,
    ClosureReturnTypeArgumentCount,
    ClosureReturnTypeComptimeArgumentCount,
    ClosureReturnTypeHasCSourceAlias,
    ClosureReturnTypeCSourceAliasName,
    ClosureReturnTypeHasQualifiers,
    ClosureReturnTypeBorrowKindIsNone,
    ClosureReturnTypeBorrowKindIsBorrow,
    ClosureReturnTypeBorrowKindIsRetBorrow,
    ClosureReturnTypeBorrowKindIsStoreBorrow,
    ClosureReturnTypeAccessKindIsNone,
    ClosureReturnTypeAccessKindIsShared,
    ClosureReturnTypeAccessKindIsFrozen,
    ClosureReturnTypeInitializationKindIsNone,
    ClosureReturnTypeInitializationKindIsOut,
    ClosureReturnTypeInitializationKindIsInit,
    ClosureReturnTypeIsMutableView,
    ClosureReturnTypeUnqualifiedTypeIs,
    ClosureParameterTypeDisplayName,
    ClosureParameterTypeBaseName,
    ClosureParameterTypeModuleName,
    ClosureParameterTypeIsGenericInstantiation,
    ClosureParameterTypeArgumentCount,
    ClosureParameterTypeComptimeArgumentCount,
    ClosureParameterTypeHasCSourceAlias,
    ClosureParameterTypeCSourceAliasName,
    ClosureParameterTypeHasQualifiers,
    ClosureParameterTypeBorrowKindIsNone,
    ClosureParameterTypeBorrowKindIsBorrow,
    ClosureParameterTypeBorrowKindIsRetBorrow,
    ClosureParameterTypeBorrowKindIsStoreBorrow,
    ClosureParameterTypeAccessKindIsNone,
    ClosureParameterTypeAccessKindIsShared,
    ClosureParameterTypeAccessKindIsFrozen,
    ClosureParameterTypeInitializationKindIsNone,
    ClosureParameterTypeInitializationKindIsOut,
    ClosureParameterTypeInitializationKindIsInit,
    ClosureParameterTypeIsMutableView,
    ClosureParameterTypeUnqualifiedTypeIs,
    ClosureReturnTypeArgumentTypeIs,
    ClosureReturnTypeArgumentTypeIsBool,
    ClosureReturnTypeArgumentTypeIsInteger,
    ClosureReturnTypeArgumentTypeIsFloat,
    ClosureReturnTypeArgumentTypeIsRawPointer,
    ClosureReturnTypeArgumentTypeIsFixedArray,
    ClosureReturnTypeArgumentTypeIsSlice,
    ClosureReturnTypeArgumentTypeIsDynamic,
    ClosureReturnTypeArgumentTypeIsFunctionPointer,
    ClosureReturnTypeArgumentTypeIsClosure,
    ClosureReturnTypeArgumentTypeIsDynTrait,
    ClosureReturnTypeArgumentTypeIsNamed,
    ClosureReturnTypeArgumentTypeIsStruct,
    ClosureReturnTypeArgumentTypeIsRecord,
    ClosureReturnTypeArgumentTypeIsEnum,
    ClosureReturnTypeArgumentTypeIsTrait,
    ClosureReturnTypeArgumentTypeIsDoctrine,
    ClosureReturnTypeArgumentTypeHasConcreteLayout,
    ClosureReturnTypeArgumentTypeDisplayName,
    ClosureReturnTypeArgumentTypeBaseName,
    ClosureReturnTypeArgumentTypeModuleName,
    ClosureReturnTypeArgumentTypeIsGenericInstantiation,
    ClosureReturnTypeArgumentTypeArgumentCount,
    ClosureReturnTypeArgumentTypeComptimeArgumentCount,
    ClosureParameterTypeArgumentTypeIs,
    ClosureParameterTypeArgumentTypeIsBool,
    ClosureParameterTypeArgumentTypeIsInteger,
    ClosureParameterTypeArgumentTypeIsFloat,
    ClosureParameterTypeArgumentTypeIsRawPointer,
    ClosureParameterTypeArgumentTypeIsFixedArray,
    ClosureParameterTypeArgumentTypeIsSlice,
    ClosureParameterTypeArgumentTypeIsDynamic,
    ClosureParameterTypeArgumentTypeIsFunctionPointer,
    ClosureParameterTypeArgumentTypeIsClosure,
    ClosureParameterTypeArgumentTypeIsDynTrait,
    ClosureParameterTypeArgumentTypeIsNamed,
    ClosureParameterTypeArgumentTypeIsStruct,
    ClosureParameterTypeArgumentTypeIsRecord,
    ClosureParameterTypeArgumentTypeIsEnum,
    ClosureParameterTypeArgumentTypeIsTrait,
    ClosureParameterTypeArgumentTypeIsDoctrine,
    ClosureParameterTypeArgumentTypeHasConcreteLayout,
    ClosureParameterTypeArgumentTypeDisplayName,
    ClosureParameterTypeArgumentTypeBaseName,
    ClosureParameterTypeArgumentTypeModuleName,
    ClosureParameterTypeArgumentTypeIsGenericInstantiation,
    ClosureParameterTypeArgumentTypeArgumentCount,
    ClosureParameterTypeArgumentTypeComptimeArgumentCount,
    ClosureParameterHasRawPointerElementCountExpression,
    ClosureParameterRawPointerElementCountExpression,
    ClosureKindIsFn,
    ClosureKindIsFinite,
    ClosureKindIsLaw,
    ClosureKindIsFiniteLaw,
    ClosureStorageIsBorrow,
    ClosureStorageIsHeap,
    ClosureStorageIsInline,
    ClosureCallCapabilityIsNormal,
    ClosureCallCapabilityIsMut,
    ClosureCallCapabilityIsOnce,
    ClosureParametersAreDisjoint,
    ClosureParametersOverlap,
    ClosureParametersAreSame,
    DynTraitIsView,
    DynTraitIsHeap,
    DynTraitTargetTypeIs,
    MethodCount,
    MethodName,
    MethodModuleName,
    MethodVisibilityIsModule,
    MethodVisibilityIsInternal,
    MethodVisibilityIsPublic,
    MethodVisibilityIsExport,
    MethodParameterCount,
    MethodParameterName,
    MethodReturnTypeIs,
    MethodParameterTypeIs,
    MethodReturnTypeIsBool,
    MethodReturnTypeIsInteger,
    MethodReturnTypeIsFloat,
    MethodReturnTypeIsRawPointer,
    MethodReturnTypeIsFixedArray,
    MethodReturnTypeIsSlice,
    MethodReturnTypeIsDynamic,
    MethodReturnTypeIsFunctionPointer,
    MethodReturnTypeIsClosure,
    MethodReturnTypeIsDynTrait,
    MethodReturnTypeIsNamed,
    MethodReturnTypeIsStruct,
    MethodReturnTypeIsRecord,
    MethodReturnTypeIsEnum,
    MethodReturnTypeIsTrait,
    MethodReturnTypeIsDoctrine,
    MethodReturnTypeHasConcreteLayout,
    MethodParameterTypeIsBool,
    MethodParameterTypeIsInteger,
    MethodParameterTypeIsFloat,
    MethodParameterTypeIsRawPointer,
    MethodParameterTypeIsFixedArray,
    MethodParameterTypeIsSlice,
    MethodParameterTypeIsDynamic,
    MethodParameterTypeIsFunctionPointer,
    MethodParameterTypeIsClosure,
    MethodParameterTypeIsDynTrait,
    MethodParameterTypeIsNamed,
    MethodParameterTypeIsStruct,
    MethodParameterTypeIsRecord,
    MethodParameterTypeIsEnum,
    MethodParameterTypeIsTrait,
    MethodParameterTypeIsDoctrine,
    MethodParameterTypeHasConcreteLayout,
    MethodKindIsFn,
    MethodKindIsFinite,
    MethodKindIsLaw,
    MethodKindIsFiniteLaw,
    MethodIsStatic,
    MethodHasBody,
    MethodIsUnsafe,
    MethodIsVarargs,
    MethodHasFfiAbi,
    MethodAbiIsC,
    MethodAbiIsCDecl,
    MethodAbiIsStdCall,
    MethodAbiIsFastCall,
    MethodAbiIsThisCall,
    MethodAbiIsVectorCall,
    MethodAbiIsSysV,
    MethodAbiIsWin64,
    MethodAbiIsAapcs,
    MethodAbiIsAapcs64,
    MethodParametersAreDisjoint,
    MethodParametersOverlap,
    MethodParametersAreSame,
    MethodGenericParameterCount,
    MethodGenericParameterName,
    MethodGenericParameterTraitBoundCount,
    MethodGenericParameterTraitBoundTypeIs,
    MethodGenericParameterTraitBoundTypeIsBool,
    MethodGenericParameterTraitBoundTypeIsInteger,
    MethodGenericParameterTraitBoundTypeIsFloat,
    MethodGenericParameterTraitBoundTypeIsRawPointer,
    MethodGenericParameterTraitBoundTypeIsFixedArray,
    MethodGenericParameterTraitBoundTypeIsSlice,
    MethodGenericParameterTraitBoundTypeIsDynamic,
    MethodGenericParameterTraitBoundTypeIsFunctionPointer,
    MethodGenericParameterTraitBoundTypeIsClosure,
    MethodGenericParameterTraitBoundTypeIsDynTrait,
    MethodGenericParameterTraitBoundTypeIsNamed,
    MethodGenericParameterTraitBoundTypeIsStruct,
    MethodGenericParameterTraitBoundTypeIsRecord,
    MethodGenericParameterTraitBoundTypeIsEnum,
    MethodGenericParameterTraitBoundTypeIsTrait,
    MethodGenericParameterTraitBoundTypeIsDoctrine,
    MethodGenericParameterTraitBoundTypeHasConcreteLayout,
    MethodGenericParameterTraitBoundTypeDisplayName,
    MethodGenericParameterTraitBoundTypeBaseName,
    MethodGenericParameterTraitBoundTypeModuleName,
    MethodGenericParameterTraitBoundTypeIsGenericInstantiation,
    MethodGenericParameterTraitBoundTypeArgumentCount,
    MethodGenericParameterTraitBoundTypeComptimeArgumentCount,
    MethodComptimeGenericParameterCount,
    MethodComptimeGenericParameterName,
    MethodComptimeGenericParameterTypeIs,
    MethodComptimeGenericParameterTypeIsBool,
    MethodComptimeGenericParameterTypeIsInteger,
    MethodComptimeGenericParameterTypeIsFloat,
    MethodComptimeGenericParameterTypeIsRawPointer,
    MethodComptimeGenericParameterTypeIsFixedArray,
    MethodComptimeGenericParameterTypeIsSlice,
    MethodComptimeGenericParameterTypeIsDynamic,
    MethodComptimeGenericParameterTypeIsFunctionPointer,
    MethodComptimeGenericParameterTypeIsClosure,
    MethodComptimeGenericParameterTypeIsDynTrait,
    MethodComptimeGenericParameterTypeIsNamed,
    MethodComptimeGenericParameterTypeIsStruct,
    MethodComptimeGenericParameterTypeIsRecord,
    MethodComptimeGenericParameterTypeIsEnum,
    MethodComptimeGenericParameterTypeIsTrait,
    MethodComptimeGenericParameterTypeIsDoctrine,
    MethodComptimeGenericParameterTypeHasConcreteLayout,
    MethodComptimeGenericParameterTypeDisplayName,
    MethodComptimeGenericParameterTypeBaseName,
    MethodComptimeGenericParameterTypeModuleName,
    MethodComptimeGenericParameterTypeIsGenericInstantiation,
    MethodComptimeGenericParameterTypeArgumentCount,
    MethodComptimeGenericParameterTypeComptimeArgumentCount,
    MethodThreadSafetyLawPredicateCount,
    MethodThreadSafetyLawPredicateLawName,
    MethodThreadSafetyLawPredicateTypeIs,
    MethodThreadSafetyLawPredicateTypeIsBool,
    MethodThreadSafetyLawPredicateTypeIsInteger,
    MethodThreadSafetyLawPredicateTypeIsFloat,
    MethodThreadSafetyLawPredicateTypeIsRawPointer,
    MethodThreadSafetyLawPredicateTypeIsFixedArray,
    MethodThreadSafetyLawPredicateTypeIsSlice,
    MethodThreadSafetyLawPredicateTypeIsDynamic,
    MethodThreadSafetyLawPredicateTypeIsFunctionPointer,
    MethodThreadSafetyLawPredicateTypeIsClosure,
    MethodThreadSafetyLawPredicateTypeIsDynTrait,
    MethodThreadSafetyLawPredicateTypeIsNamed,
    MethodThreadSafetyLawPredicateTypeIsStruct,
    MethodThreadSafetyLawPredicateTypeIsRecord,
    MethodThreadSafetyLawPredicateTypeIsEnum,
    MethodThreadSafetyLawPredicateTypeIsTrait,
    MethodThreadSafetyLawPredicateTypeIsDoctrine,
    MethodThreadSafetyLawPredicateTypeHasConcreteLayout,
    MethodThreadSafetyLawPredicateTypeDisplayName,
    MethodThreadSafetyLawPredicateTypeBaseName,
    MethodThreadSafetyLawPredicateTypeModuleName,
    MethodThreadSafetyLawPredicateTypeIsGenericInstantiation,
    MethodThreadSafetyLawPredicateTypeArgumentCount,
    MethodThreadSafetyLawPredicateTypeComptimeArgumentCount,
    MethodReturnTypeDisplayName,
    MethodReturnTypeBaseName,
    MethodReturnTypeModuleName,
    MethodReturnTypeIsGenericInstantiation,
    MethodReturnTypeArgumentCount,
    MethodReturnTypeComptimeArgumentCount,
    MethodReturnTypeHasCSourceAlias,
    MethodReturnTypeCSourceAliasName,
    MethodReturnTypeHasQualifiers,
    MethodReturnTypeBorrowKindIsNone,
    MethodReturnTypeBorrowKindIsBorrow,
    MethodReturnTypeBorrowKindIsRetBorrow,
    MethodReturnTypeBorrowKindIsStoreBorrow,
    MethodReturnTypeAccessKindIsNone,
    MethodReturnTypeAccessKindIsShared,
    MethodReturnTypeAccessKindIsFrozen,
    MethodReturnTypeInitializationKindIsNone,
    MethodReturnTypeInitializationKindIsOut,
    MethodReturnTypeInitializationKindIsInit,
    MethodReturnTypeIsMutableView,
    MethodReturnTypeUnqualifiedTypeIs,
    MethodParameterTypeDisplayName,
    MethodParameterTypeBaseName,
    MethodParameterTypeModuleName,
    MethodParameterTypeIsGenericInstantiation,
    MethodParameterTypeArgumentCount,
    MethodParameterTypeComptimeArgumentCount,
    MethodParameterTypeHasCSourceAlias,
    MethodParameterTypeCSourceAliasName,
    MethodParameterTypeHasQualifiers,
    MethodParameterTypeBorrowKindIsNone,
    MethodParameterTypeBorrowKindIsBorrow,
    MethodParameterTypeBorrowKindIsRetBorrow,
    MethodParameterTypeBorrowKindIsStoreBorrow,
    MethodParameterTypeAccessKindIsNone,
    MethodParameterTypeAccessKindIsShared,
    MethodParameterTypeAccessKindIsFrozen,
    MethodParameterTypeInitializationKindIsNone,
    MethodParameterTypeInitializationKindIsOut,
    MethodParameterTypeInitializationKindIsInit,
    MethodParameterTypeIsMutableView,
    MethodParameterTypeUnqualifiedTypeIs,
    MethodReturnTypeArgumentTypeIs,
    MethodReturnTypeArgumentTypeIsBool,
    MethodReturnTypeArgumentTypeIsInteger,
    MethodReturnTypeArgumentTypeIsFloat,
    MethodReturnTypeArgumentTypeIsRawPointer,
    MethodReturnTypeArgumentTypeIsFixedArray,
    MethodReturnTypeArgumentTypeIsSlice,
    MethodReturnTypeArgumentTypeIsDynamic,
    MethodReturnTypeArgumentTypeIsFunctionPointer,
    MethodReturnTypeArgumentTypeIsClosure,
    MethodReturnTypeArgumentTypeIsDynTrait,
    MethodReturnTypeArgumentTypeIsNamed,
    MethodReturnTypeArgumentTypeIsStruct,
    MethodReturnTypeArgumentTypeIsRecord,
    MethodReturnTypeArgumentTypeIsEnum,
    MethodReturnTypeArgumentTypeIsTrait,
    MethodReturnTypeArgumentTypeIsDoctrine,
    MethodReturnTypeArgumentTypeHasConcreteLayout,
    MethodReturnTypeArgumentTypeDisplayName,
    MethodReturnTypeArgumentTypeBaseName,
    MethodReturnTypeArgumentTypeModuleName,
    MethodReturnTypeArgumentTypeIsGenericInstantiation,
    MethodReturnTypeArgumentTypeArgumentCount,
    MethodReturnTypeArgumentTypeComptimeArgumentCount,
    MethodParameterTypeArgumentTypeIs,
    MethodParameterTypeArgumentTypeIsBool,
    MethodParameterTypeArgumentTypeIsInteger,
    MethodParameterTypeArgumentTypeIsFloat,
    MethodParameterTypeArgumentTypeIsRawPointer,
    MethodParameterTypeArgumentTypeIsFixedArray,
    MethodParameterTypeArgumentTypeIsSlice,
    MethodParameterTypeArgumentTypeIsDynamic,
    MethodParameterTypeArgumentTypeIsFunctionPointer,
    MethodParameterTypeArgumentTypeIsClosure,
    MethodParameterTypeArgumentTypeIsDynTrait,
    MethodParameterTypeArgumentTypeIsNamed,
    MethodParameterTypeArgumentTypeIsStruct,
    MethodParameterTypeArgumentTypeIsRecord,
    MethodParameterTypeArgumentTypeIsEnum,
    MethodParameterTypeArgumentTypeIsTrait,
    MethodParameterTypeArgumentTypeIsDoctrine,
    MethodParameterTypeArgumentTypeHasConcreteLayout,
    MethodParameterTypeArgumentTypeDisplayName,
    MethodParameterTypeArgumentTypeBaseName,
    MethodParameterTypeArgumentTypeModuleName,
    MethodParameterTypeArgumentTypeIsGenericInstantiation,
    MethodParameterTypeArgumentTypeArgumentCount,
    MethodParameterTypeArgumentTypeComptimeArgumentCount,
    MethodParameterHasRawPointerElementCountExpression,
    MethodParameterRawPointerElementCountExpression,
    FieldCount,
    EnumVariantCount,
    FieldOffset,
    FieldSize,
    FieldAlign,
    FieldIsMisaligned,
    StructLayoutIsAuto,
    StructLayoutIsC,
    StructLayoutIsExplicit,
    StructHasPack,
    StructPack,
    StructHasAlign,
    StructAlign,
    FieldHasExplicitOffset,
    FieldExplicitOffset,
    FieldTypeIsBool,
    FieldTypeIsInteger,
    FieldTypeIsFloat,
    FieldTypeIsRawPointer,
    FieldTypeIsFixedArray,
    FieldTypeIsSlice,
    FieldTypeIsDynamic,
    FieldTypeIsFunctionPointer,
    FieldTypeIsClosure,
    FieldTypeIsDynTrait,
    FieldTypeIsNamed,
    FieldTypeIsStruct,
    FieldTypeIsRecord,
    FieldTypeIsEnum,
    FieldTypeIsTrait,
    FieldTypeIsDoctrine,
    FieldTypeHasConcreteLayout,
    FieldTypeDisplayName,
    FieldTypeBaseName,
    FieldTypeModuleName,
    FieldTypeIsGenericInstantiation,
    FieldTypeArgumentCount,
    FieldTypeComptimeArgumentCount,
    FieldTypeHasCSourceAlias,
    FieldTypeCSourceAliasName,
    FieldTypeHasQualifiers,
    FieldTypeBorrowKindIsNone,
    FieldTypeBorrowKindIsBorrow,
    FieldTypeBorrowKindIsRetBorrow,
    FieldTypeBorrowKindIsStoreBorrow,
    FieldTypeAccessKindIsNone,
    FieldTypeAccessKindIsShared,
    FieldTypeAccessKindIsFrozen,
    FieldTypeInitializationKindIsNone,
    FieldTypeInitializationKindIsOut,
    FieldTypeInitializationKindIsInit,
    FieldTypeIsMutableView,
    FieldTypeUnqualifiedTypeIs,
    TypeGenericParameterCount,
    TypeGenericParameterName,
    TypeComptimeGenericParameterCount,
    TypeComptimeGenericParameterName,
    TypeComptimeGenericParameterTypeIs,
    TypeComptimeGenericParameterTypeIsBool,
    TypeComptimeGenericParameterTypeIsInteger,
    TypeComptimeGenericParameterTypeIsFloat,
    TypeComptimeGenericParameterTypeIsRawPointer,
    TypeComptimeGenericParameterTypeIsFixedArray,
    TypeComptimeGenericParameterTypeIsSlice,
    TypeComptimeGenericParameterTypeIsDynamic,
    TypeComptimeGenericParameterTypeIsFunctionPointer,
    TypeComptimeGenericParameterTypeIsClosure,
    TypeComptimeGenericParameterTypeIsDynTrait,
    TypeComptimeGenericParameterTypeIsNamed,
    TypeComptimeGenericParameterTypeIsStruct,
    TypeComptimeGenericParameterTypeIsRecord,
    TypeComptimeGenericParameterTypeIsEnum,
    TypeComptimeGenericParameterTypeIsTrait,
    TypeComptimeGenericParameterTypeIsDoctrine,
    TypeComptimeGenericParameterTypeHasConcreteLayout,
    TypeComptimeGenericParameterTypeDisplayName,
    TypeComptimeGenericParameterTypeBaseName,
    TypeComptimeGenericParameterTypeModuleName,
    TypeComptimeGenericParameterTypeIsGenericInstantiation,
    TypeComptimeGenericParameterTypeArgumentCount,
    TypeComptimeGenericParameterTypeComptimeArgumentCount,
    TypeDisplayName,
    TypeBaseName,
    TypeModuleName,
    TypeVisibilityIsModule,
    TypeVisibilityIsInternal,
    TypeVisibilityIsPublic,
    TypeVisibilityIsExport,
    TypeHasCSourceAlias,
    TypeCSourceAliasName,
    TypeIsGenericInstantiation,
    TypeArgumentCount,
    TypeArgumentTypeIs,
    TypeArgumentTypeIsBool,
    TypeArgumentTypeIsInteger,
    TypeArgumentTypeIsFloat,
    TypeArgumentTypeIsRawPointer,
    TypeArgumentTypeIsFixedArray,
    TypeArgumentTypeIsSlice,
    TypeArgumentTypeIsDynamic,
    TypeArgumentTypeIsFunctionPointer,
    TypeArgumentTypeIsClosure,
    TypeArgumentTypeIsDynTrait,
    TypeArgumentTypeIsNamed,
    TypeArgumentTypeIsStruct,
    TypeArgumentTypeIsRecord,
    TypeArgumentTypeIsEnum,
    TypeArgumentTypeIsTrait,
    TypeArgumentTypeIsDoctrine,
    TypeArgumentTypeHasConcreteLayout,
    TypeArgumentTypeDisplayName,
    TypeArgumentTypeBaseName,
    TypeArgumentTypeModuleName,
    TypeArgumentTypeIsGenericInstantiation,
    TypeArgumentTypeArgumentCount,
    TypeArgumentTypeComptimeArgumentCount,
    TypeComptimeArgumentCount,
    TypeComptimeArgumentName,
    TypeComptimeArgumentTypeIs,
    TypeComptimeArgumentTypeIsBool,
    TypeComptimeArgumentTypeIsInteger,
    TypeComptimeArgumentTypeIsFloat,
    TypeComptimeArgumentTypeIsRawPointer,
    TypeComptimeArgumentTypeIsFixedArray,
    TypeComptimeArgumentTypeIsSlice,
    TypeComptimeArgumentTypeIsDynamic,
    TypeComptimeArgumentTypeIsFunctionPointer,
    TypeComptimeArgumentTypeIsClosure,
    TypeComptimeArgumentTypeIsDynTrait,
    TypeComptimeArgumentTypeIsNamed,
    TypeComptimeArgumentTypeIsStruct,
    TypeComptimeArgumentTypeIsRecord,
    TypeComptimeArgumentTypeIsEnum,
    TypeComptimeArgumentTypeIsTrait,
    TypeComptimeArgumentTypeIsDoctrine,
    TypeComptimeArgumentTypeHasConcreteLayout,
    TypeComptimeArgumentTypeDisplayName,
    TypeComptimeArgumentTypeBaseName,
    TypeComptimeArgumentTypeModuleName,
    TypeComptimeArgumentTypeIsGenericInstantiation,
    TypeComptimeArgumentTypeArgumentCount,
    TypeComptimeArgumentTypeComptimeArgumentCount,
    TypeComptimeArgumentValueIs,
    EnumVariantPayloadCount,
    EnumVariantTag,
    EnumTagOffset,
    EnumTagSize,
    EnumTagAlign,
    EnumTagIsMisaligned,
    EnumVariantPayloadOffset,
    EnumVariantPayloadSize,
    EnumVariantPayloadAlign,
    EnumVariantPayloadIsMisaligned,
    EnumVariantIsOk,
    EnumVariantIsErr,
    EnumVariantIsErrorFunnel,
    Implements,
    ImplementedTraitCount,
    ImplementedTraitTypeIs,
    ImplementedTraitTypeIsBool,
    ImplementedTraitTypeIsInteger,
    ImplementedTraitTypeIsFloat,
    ImplementedTraitTypeIsRawPointer,
    ImplementedTraitTypeIsFixedArray,
    ImplementedTraitTypeIsSlice,
    ImplementedTraitTypeIsDynamic,
    ImplementedTraitTypeIsFunctionPointer,
    ImplementedTraitTypeIsClosure,
    ImplementedTraitTypeIsDynTrait,
    ImplementedTraitTypeIsNamed,
    ImplementedTraitTypeIsStruct,
    ImplementedTraitTypeIsRecord,
    ImplementedTraitTypeIsEnum,
    ImplementedTraitTypeIsTrait,
    ImplementedTraitTypeIsDoctrine,
    ImplementedTraitTypeHasConcreteLayout,
    ImplementedTraitTypeDisplayName,
    ImplementedTraitTypeBaseName,
    ImplementedTraitTypeModuleName,
    ImplementedTraitTypeIsGenericInstantiation,
    ImplementedTraitTypeArgumentCount,
    ImplementedTraitTypeComptimeArgumentCount,
    ImplementedTraitTypeArgumentTypeIs,
    ImplementedTraitTypeArgumentTypeIsBool,
    ImplementedTraitTypeArgumentTypeIsInteger,
    ImplementedTraitTypeArgumentTypeIsFloat,
    ImplementedTraitTypeArgumentTypeIsRawPointer,
    ImplementedTraitTypeArgumentTypeIsFixedArray,
    ImplementedTraitTypeArgumentTypeIsSlice,
    ImplementedTraitTypeArgumentTypeIsDynamic,
    ImplementedTraitTypeArgumentTypeIsFunctionPointer,
    ImplementedTraitTypeArgumentTypeIsClosure,
    ImplementedTraitTypeArgumentTypeIsDynTrait,
    ImplementedTraitTypeArgumentTypeIsNamed,
    ImplementedTraitTypeArgumentTypeIsStruct,
    ImplementedTraitTypeArgumentTypeIsRecord,
    ImplementedTraitTypeArgumentTypeIsEnum,
    ImplementedTraitTypeArgumentTypeIsTrait,
    ImplementedTraitTypeArgumentTypeIsDoctrine,
    ImplementedTraitTypeArgumentTypeHasConcreteLayout,
    ImplementedTraitTypeArgumentTypeDisplayName,
    ImplementedTraitTypeArgumentTypeBaseName,
    ImplementedTraitTypeArgumentTypeModuleName,
    ImplementedTraitTypeArgumentTypeIsGenericInstantiation,
    ImplementedTraitTypeArgumentTypeArgumentCount,
    ImplementedTraitTypeArgumentTypeComptimeArgumentCount,
    ImplementedTraitTypeComptimeArgumentName,
    ImplementedTraitTypeComptimeArgumentTypeIs,
    ImplementedTraitTypeComptimeArgumentTypeIsBool,
    ImplementedTraitTypeComptimeArgumentTypeIsInteger,
    ImplementedTraitTypeComptimeArgumentTypeIsFloat,
    ImplementedTraitTypeComptimeArgumentTypeIsRawPointer,
    ImplementedTraitTypeComptimeArgumentTypeIsFixedArray,
    ImplementedTraitTypeComptimeArgumentTypeIsSlice,
    ImplementedTraitTypeComptimeArgumentTypeIsDynamic,
    ImplementedTraitTypeComptimeArgumentTypeIsFunctionPointer,
    ImplementedTraitTypeComptimeArgumentTypeIsClosure,
    ImplementedTraitTypeComptimeArgumentTypeIsDynTrait,
    ImplementedTraitTypeComptimeArgumentTypeIsNamed,
    ImplementedTraitTypeComptimeArgumentTypeIsStruct,
    ImplementedTraitTypeComptimeArgumentTypeIsRecord,
    ImplementedTraitTypeComptimeArgumentTypeIsEnum,
    ImplementedTraitTypeComptimeArgumentTypeIsTrait,
    ImplementedTraitTypeComptimeArgumentTypeIsDoctrine,
    ImplementedTraitTypeComptimeArgumentTypeHasConcreteLayout,
    ImplementedTraitTypeComptimeArgumentTypeDisplayName,
    ImplementedTraitTypeComptimeArgumentTypeBaseName,
    ImplementedTraitTypeComptimeArgumentTypeModuleName,
    ImplementedTraitTypeComptimeArgumentTypeIsGenericInstantiation,
    ImplementedTraitTypeComptimeArgumentTypeArgumentCount,
    ImplementedTraitTypeComptimeArgumentTypeComptimeArgumentCount,
    ImplementedTraitTypeComptimeArgumentValueIs,
    AssociatedTypeCount,
    AssociatedTypeName,
    AssociatedTypeHasTarget,
    AssociatedTypeTargetTypeIs,
    AssociatedTypeTargetTypeIsBool,
    AssociatedTypeTargetTypeIsInteger,
    AssociatedTypeTargetTypeIsFloat,
    AssociatedTypeTargetTypeIsRawPointer,
    AssociatedTypeTargetTypeIsFixedArray,
    AssociatedTypeTargetTypeIsSlice,
    AssociatedTypeTargetTypeIsDynamic,
    AssociatedTypeTargetTypeIsFunctionPointer,
    AssociatedTypeTargetTypeIsClosure,
    AssociatedTypeTargetTypeIsDynTrait,
    AssociatedTypeTargetTypeIsNamed,
    AssociatedTypeTargetTypeIsStruct,
    AssociatedTypeTargetTypeIsRecord,
    AssociatedTypeTargetTypeIsEnum,
    AssociatedTypeTargetTypeIsTrait,
    AssociatedTypeTargetTypeIsDoctrine,
    AssociatedTypeTargetTypeHasConcreteLayout,
    AssociatedTypeTargetTypeDisplayName,
    AssociatedTypeTargetTypeBaseName,
    AssociatedTypeTargetTypeModuleName,
    AssociatedTypeTargetTypeIsGenericInstantiation,
    AssociatedTypeTargetTypeArgumentCount,
    AssociatedTypeTargetTypeComptimeArgumentCount,
    FieldTypeIs,
    EnumVariantPayloadTypeIs,
    EnumVariantAbsorbsErrorTypeIs,
    EnumVariantPayloadTypeIsBool,
    EnumVariantPayloadTypeIsInteger,
    EnumVariantPayloadTypeIsFloat,
    EnumVariantPayloadTypeIsRawPointer,
    EnumVariantPayloadTypeIsFixedArray,
    EnumVariantPayloadTypeIsSlice,
    EnumVariantPayloadTypeIsDynamic,
    EnumVariantPayloadTypeIsFunctionPointer,
    EnumVariantPayloadTypeIsClosure,
    EnumVariantPayloadTypeIsDynTrait,
    EnumVariantPayloadTypeIsNamed,
    EnumVariantPayloadTypeIsStruct,
    EnumVariantPayloadTypeIsRecord,
    EnumVariantPayloadTypeIsEnum,
    EnumVariantPayloadTypeIsTrait,
    EnumVariantPayloadTypeIsDoctrine,
    EnumVariantPayloadTypeHasConcreteLayout,
    EnumVariantPayloadTypeDisplayName,
    EnumVariantPayloadTypeBaseName,
    EnumVariantPayloadTypeModuleName,
    EnumVariantPayloadTypeIsGenericInstantiation,
    EnumVariantPayloadTypeArgumentCount,
    EnumVariantPayloadTypeComptimeArgumentCount,
    EnumVariantPayloadTypeHasCSourceAlias,
    EnumVariantPayloadTypeCSourceAliasName,
    EnumVariantPayloadTypeHasQualifiers,
    EnumVariantPayloadTypeBorrowKindIsNone,
    EnumVariantPayloadTypeBorrowKindIsBorrow,
    EnumVariantPayloadTypeBorrowKindIsRetBorrow,
    EnumVariantPayloadTypeBorrowKindIsStoreBorrow,
    EnumVariantPayloadTypeAccessKindIsNone,
    EnumVariantPayloadTypeAccessKindIsShared,
    EnumVariantPayloadTypeAccessKindIsFrozen,
    EnumVariantPayloadTypeInitializationKindIsNone,
    EnumVariantPayloadTypeInitializationKindIsOut,
    EnumVariantPayloadTypeInitializationKindIsInit,
    EnumVariantPayloadTypeIsMutableView,
    EnumVariantPayloadTypeUnqualifiedTypeIs,
    FieldName,
    FieldVisibilityIsModule,
    FieldVisibilityIsInternal,
    FieldVisibilityIsPublic,
    FieldVisibilityIsExport,
    EnumVariantName,
    EnumVariantUsesNamedFields,
    EnumVariantPayloadHasName,
    EnumVariantPayloadName,
    TypeThreadSafetyLawAttributeCount,
    TypeThreadSafetyLawAttributeLawName,
    TypeThreadSafetyLawAttributeIsGrant,
    TypeThreadSafetyLawAttributeIsDeny,
    TypeThreadSafetyLawAttributeHasCondition,
    TypeThreadSafetyLawAttributeConditionLawName,
    TypeThreadSafetyLawAttributeConditionTypeIs,
    TypeThreadSafetyLawAttributeConditionTypeIsBool,
    TypeThreadSafetyLawAttributeConditionTypeIsInteger,
    TypeThreadSafetyLawAttributeConditionTypeIsFloat,
    TypeThreadSafetyLawAttributeConditionTypeIsRawPointer,
    TypeThreadSafetyLawAttributeConditionTypeIsFixedArray,
    TypeThreadSafetyLawAttributeConditionTypeIsSlice,
    TypeThreadSafetyLawAttributeConditionTypeIsDynamic,
    TypeThreadSafetyLawAttributeConditionTypeIsFunctionPointer,
    TypeThreadSafetyLawAttributeConditionTypeIsClosure,
    TypeThreadSafetyLawAttributeConditionTypeIsDynTrait,
    TypeThreadSafetyLawAttributeConditionTypeIsNamed,
    TypeThreadSafetyLawAttributeConditionTypeIsStruct,
    TypeThreadSafetyLawAttributeConditionTypeIsRecord,
    TypeThreadSafetyLawAttributeConditionTypeIsEnum,
    TypeThreadSafetyLawAttributeConditionTypeIsTrait,
    TypeThreadSafetyLawAttributeConditionTypeIsDoctrine,
    TypeThreadSafetyLawAttributeConditionTypeHasConcreteLayout,
    TypeThreadSafetyLawAttributeConditionTypeDisplayName,
    TypeThreadSafetyLawAttributeConditionTypeBaseName,
    TypeThreadSafetyLawAttributeConditionTypeModuleName,
    TypeThreadSafetyLawAttributeConditionTypeIsGenericInstantiation,
    TypeThreadSafetyLawAttributeConditionTypeArgumentCount,
    TypeThreadSafetyLawAttributeConditionTypeComptimeArgumentCount,
    FieldThreadSafetyLawAttributeCount,
    FieldThreadSafetyLawAttributeLawName,
    FieldThreadSafetyLawAttributeIsGrant,
    FieldThreadSafetyLawAttributeIsDeny,
    FieldThreadSafetyLawAttributeHasCondition,
    FieldThreadSafetyLawAttributeConditionLawName,
    FieldThreadSafetyLawAttributeConditionTypeIs,
    FieldThreadSafetyLawAttributeConditionTypeIsBool,
    FieldThreadSafetyLawAttributeConditionTypeIsInteger,
    FieldThreadSafetyLawAttributeConditionTypeIsFloat,
    FieldThreadSafetyLawAttributeConditionTypeIsRawPointer,
    FieldThreadSafetyLawAttributeConditionTypeIsFixedArray,
    FieldThreadSafetyLawAttributeConditionTypeIsSlice,
    FieldThreadSafetyLawAttributeConditionTypeIsDynamic,
    FieldThreadSafetyLawAttributeConditionTypeIsFunctionPointer,
    FieldThreadSafetyLawAttributeConditionTypeIsClosure,
    FieldThreadSafetyLawAttributeConditionTypeIsDynTrait,
    FieldThreadSafetyLawAttributeConditionTypeIsNamed,
    FieldThreadSafetyLawAttributeConditionTypeIsStruct,
    FieldThreadSafetyLawAttributeConditionTypeIsRecord,
    FieldThreadSafetyLawAttributeConditionTypeIsEnum,
    FieldThreadSafetyLawAttributeConditionTypeIsTrait,
    FieldThreadSafetyLawAttributeConditionTypeIsDoctrine,
    FieldThreadSafetyLawAttributeConditionTypeHasConcreteLayout,
    FieldThreadSafetyLawAttributeConditionTypeDisplayName,
    FieldThreadSafetyLawAttributeConditionTypeBaseName,
    FieldThreadSafetyLawAttributeConditionTypeModuleName,
    FieldThreadSafetyLawAttributeConditionTypeIsGenericInstantiation,
    FieldThreadSafetyLawAttributeConditionTypeArgumentCount,
    FieldThreadSafetyLawAttributeConditionTypeComptimeArgumentCount
}

internal sealed record CompileTimeStructuralFactArguments(
    StarkTypeSymbol TargetType,
    IReadOnlyList<StarkTypeSymbol>? AdditionalTypeArgumentList = null,
    IReadOnlyList<ComptimeValueArgumentSymbol>? ComptimeValueArgumentList = null)
{
    public IReadOnlyList<StarkTypeSymbol> AdditionalTypeArguments =>
        AdditionalTypeArgumentList ?? [];

    public IReadOnlyList<ComptimeValueArgumentSymbol> ComptimeValueArguments =>
        ComptimeValueArgumentList ?? [];
}

internal enum CompileTimeStructuralTypePredicate
{
    None,
    Bool,
    Integer,
    Float,
    RawPointer,
    FixedArray,
    Slice,
    Dynamic,
    FunctionPointer,
    Closure,
    DynTrait,
    Named,
    Struct,
    Record,
    Enum,
    Trait,
    Doctrine,
    ConcreteLayout
}

internal static class CompileTimeStructuralFacts
{
    private const string NamespacePrefix = "System.Compiler.";

    public static bool TryGetFactKind(string name, out CompileTimeStructuralFactKind kind)
    {
        kind = default;
        if (!name.StartsWith(NamespacePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var localName = name[NamespacePrefix.Length..];
        if (TryGetCallableNestedTypeArgumentFactKind(localName, out kind))
        {
            return true;
        }

        kind = localName switch
        {
            "IsBool" => CompileTimeStructuralFactKind.IsBool,
            "IsInteger" => CompileTimeStructuralFactKind.IsInteger,
            "IsFloat" => CompileTimeStructuralFactKind.IsFloat,
            "IsRawPointer" => CompileTimeStructuralFactKind.IsRawPointer,
            "IsFixedArray" => CompileTimeStructuralFactKind.IsFixedArray,
            "IsSlice" => CompileTimeStructuralFactKind.IsSlice,
            "IsDynamic" => CompileTimeStructuralFactKind.IsDynamic,
            "IsFunctionPointer" => CompileTimeStructuralFactKind.IsFunctionPointer,
            "IsClosure" => CompileTimeStructuralFactKind.IsClosure,
            "IsDynTrait" => CompileTimeStructuralFactKind.IsDynTrait,
            "IsNamed" => CompileTimeStructuralFactKind.IsNamed,
            "IsStruct" => CompileTimeStructuralFactKind.IsStruct,
            "IsRecord" => CompileTimeStructuralFactKind.IsRecord,
            "IsEnum" => CompileTimeStructuralFactKind.IsEnum,
            "IsTrait" => CompileTimeStructuralFactKind.IsTrait,
            "IsDoctrine" => CompileTimeStructuralFactKind.IsDoctrine,
            "HasConcreteLayout" => CompileTimeStructuralFactKind.HasConcreteLayout,
            "TypeSize" => CompileTimeStructuralFactKind.TypeSize,
            "TypeAlign" => CompileTimeStructuralFactKind.TypeAlign,
            "TypeIsZeroSized" => CompileTimeStructuralFactKind.TypeIsZeroSized,
            "TypeIntegerBitWidth" => CompileTimeStructuralFactKind.TypeIntegerBitWidth,
            "TypeFloatBitWidth" => CompileTimeStructuralFactKind.TypeFloatBitWidth,
            "TypeIntegerIsSigned" => CompileTimeStructuralFactKind.TypeIntegerIsSigned,
            "TypeIntegerIsUnsigned" => CompileTimeStructuralFactKind.TypeIntegerIsUnsigned,
            "TypeIntegerIsFullRange" => CompileTimeStructuralFactKind.TypeIntegerIsFullRange,
            "TypeIntegerMinIs" => CompileTimeStructuralFactKind.TypeIntegerMinIs,
            "TypeIntegerMaxIs" => CompileTimeStructuralFactKind.TypeIntegerMaxIs,
            "RawPointerElementTypeIs" => CompileTimeStructuralFactKind.RawPointerElementTypeIs,
            "RawPointerElementTypeIsBool" => CompileTimeStructuralFactKind.RawPointerElementTypeIsBool,
            "RawPointerElementTypeIsInteger" => CompileTimeStructuralFactKind.RawPointerElementTypeIsInteger,
            "RawPointerElementTypeIsFloat" => CompileTimeStructuralFactKind.RawPointerElementTypeIsFloat,
            "RawPointerElementTypeIsRawPointer" => CompileTimeStructuralFactKind.RawPointerElementTypeIsRawPointer,
            "RawPointerElementTypeIsFixedArray" => CompileTimeStructuralFactKind.RawPointerElementTypeIsFixedArray,
            "RawPointerElementTypeIsSlice" => CompileTimeStructuralFactKind.RawPointerElementTypeIsSlice,
            "RawPointerElementTypeIsDynamic" => CompileTimeStructuralFactKind.RawPointerElementTypeIsDynamic,
            "RawPointerElementTypeIsFunctionPointer" => CompileTimeStructuralFactKind.RawPointerElementTypeIsFunctionPointer,
            "RawPointerElementTypeIsClosure" => CompileTimeStructuralFactKind.RawPointerElementTypeIsClosure,
            "RawPointerElementTypeIsDynTrait" => CompileTimeStructuralFactKind.RawPointerElementTypeIsDynTrait,
            "RawPointerElementTypeIsNamed" => CompileTimeStructuralFactKind.RawPointerElementTypeIsNamed,
            "RawPointerElementTypeIsStruct" => CompileTimeStructuralFactKind.RawPointerElementTypeIsStruct,
            "RawPointerElementTypeIsRecord" => CompileTimeStructuralFactKind.RawPointerElementTypeIsRecord,
            "RawPointerElementTypeIsEnum" => CompileTimeStructuralFactKind.RawPointerElementTypeIsEnum,
            "RawPointerElementTypeIsTrait" => CompileTimeStructuralFactKind.RawPointerElementTypeIsTrait,
            "RawPointerElementTypeIsDoctrine" => CompileTimeStructuralFactKind.RawPointerElementTypeIsDoctrine,
            "RawPointerElementTypeHasConcreteLayout" => CompileTimeStructuralFactKind.RawPointerElementTypeHasConcreteLayout,
            "RawPointerElementTypeHasCSourceAlias" => CompileTimeStructuralFactKind.RawPointerElementTypeHasCSourceAlias,
            "RawPointerElementTypeCSourceAliasName" => CompileTimeStructuralFactKind.RawPointerElementTypeCSourceAliasName,
            "RawPointerIsMutable" => CompileTimeStructuralFactKind.RawPointerIsMutable,
            "RawPointerIsReadOnly" => CompileTimeStructuralFactKind.RawPointerIsReadOnly,
            "TypeElementTypeIs" => CompileTimeStructuralFactKind.TypeElementTypeIs,
            "TypeElementTypeIsBool" => CompileTimeStructuralFactKind.TypeElementTypeIsBool,
            "TypeElementTypeIsInteger" => CompileTimeStructuralFactKind.TypeElementTypeIsInteger,
            "TypeElementTypeIsFloat" => CompileTimeStructuralFactKind.TypeElementTypeIsFloat,
            "TypeElementTypeIsRawPointer" => CompileTimeStructuralFactKind.TypeElementTypeIsRawPointer,
            "TypeElementTypeIsFixedArray" => CompileTimeStructuralFactKind.TypeElementTypeIsFixedArray,
            "TypeElementTypeIsSlice" => CompileTimeStructuralFactKind.TypeElementTypeIsSlice,
            "TypeElementTypeIsDynamic" => CompileTimeStructuralFactKind.TypeElementTypeIsDynamic,
            "TypeElementTypeIsFunctionPointer" => CompileTimeStructuralFactKind.TypeElementTypeIsFunctionPointer,
            "TypeElementTypeIsClosure" => CompileTimeStructuralFactKind.TypeElementTypeIsClosure,
            "TypeElementTypeIsDynTrait" => CompileTimeStructuralFactKind.TypeElementTypeIsDynTrait,
            "TypeElementTypeIsNamed" => CompileTimeStructuralFactKind.TypeElementTypeIsNamed,
            "TypeElementTypeIsStruct" => CompileTimeStructuralFactKind.TypeElementTypeIsStruct,
            "TypeElementTypeIsRecord" => CompileTimeStructuralFactKind.TypeElementTypeIsRecord,
            "TypeElementTypeIsEnum" => CompileTimeStructuralFactKind.TypeElementTypeIsEnum,
            "TypeElementTypeIsTrait" => CompileTimeStructuralFactKind.TypeElementTypeIsTrait,
            "TypeElementTypeIsDoctrine" => CompileTimeStructuralFactKind.TypeElementTypeIsDoctrine,
            "TypeElementTypeHasConcreteLayout" => CompileTimeStructuralFactKind.TypeElementTypeHasConcreteLayout,
            "TypeElementTypeHasCSourceAlias" => CompileTimeStructuralFactKind.TypeElementTypeHasCSourceAlias,
            "TypeElementTypeCSourceAliasName" => CompileTimeStructuralFactKind.TypeElementTypeCSourceAliasName,
            "TypeFixedArrayLength" => CompileTimeStructuralFactKind.TypeFixedArrayLength,
            "TypeFixedArrayLengthIs" => CompileTimeStructuralFactKind.TypeFixedArrayLengthIs,
            "TypeHasQualifiers" => CompileTimeStructuralFactKind.TypeHasQualifiers,
            "TypeBorrowKindIsNone" => CompileTimeStructuralFactKind.TypeBorrowKindIsNone,
            "TypeBorrowKindIsBorrow" => CompileTimeStructuralFactKind.TypeBorrowKindIsBorrow,
            "TypeBorrowKindIsRetBorrow" => CompileTimeStructuralFactKind.TypeBorrowKindIsRetBorrow,
            "TypeBorrowKindIsStoreBorrow" => CompileTimeStructuralFactKind.TypeBorrowKindIsStoreBorrow,
            "TypeAccessKindIsNone" => CompileTimeStructuralFactKind.TypeAccessKindIsNone,
            "TypeAccessKindIsShared" => CompileTimeStructuralFactKind.TypeAccessKindIsShared,
            "TypeAccessKindIsFrozen" => CompileTimeStructuralFactKind.TypeAccessKindIsFrozen,
            "TypeInitializationKindIsNone" => CompileTimeStructuralFactKind.TypeInitializationKindIsNone,
            "TypeInitializationKindIsOut" => CompileTimeStructuralFactKind.TypeInitializationKindIsOut,
            "TypeInitializationKindIsInit" => CompileTimeStructuralFactKind.TypeInitializationKindIsInit,
            "TypeIsMutableView" => CompileTimeStructuralFactKind.TypeIsMutableView,
            "TypeUnqualifiedTypeIs" => CompileTimeStructuralFactKind.TypeUnqualifiedTypeIs,
            "FunctionPointerParameterCount" => CompileTimeStructuralFactKind.FunctionPointerParameterCount,
            "FunctionPointerReturnTypeIs" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeIs,
            "FunctionPointerParameterTypeIs" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeIs,
            "FunctionPointerReturnTypeIsBool" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsBool,
            "FunctionPointerReturnTypeIsInteger" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsInteger,
            "FunctionPointerReturnTypeIsFloat" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsFloat,
            "FunctionPointerReturnTypeIsRawPointer" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsRawPointer,
            "FunctionPointerReturnTypeIsFixedArray" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsFixedArray,
            "FunctionPointerReturnTypeIsSlice" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsSlice,
            "FunctionPointerReturnTypeIsDynamic" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsDynamic,
            "FunctionPointerReturnTypeIsFunctionPointer" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsFunctionPointer,
            "FunctionPointerReturnTypeIsClosure" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsClosure,
            "FunctionPointerReturnTypeIsDynTrait" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsDynTrait,
            "FunctionPointerReturnTypeIsNamed" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsNamed,
            "FunctionPointerReturnTypeIsStruct" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsStruct,
            "FunctionPointerReturnTypeIsRecord" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsRecord,
            "FunctionPointerReturnTypeIsEnum" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsEnum,
            "FunctionPointerReturnTypeIsTrait" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsTrait,
            "FunctionPointerReturnTypeIsDoctrine" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsDoctrine,
            "FunctionPointerReturnTypeHasConcreteLayout" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeHasConcreteLayout,
            "FunctionPointerParameterTypeIsBool" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsBool,
            "FunctionPointerParameterTypeIsInteger" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsInteger,
            "FunctionPointerParameterTypeIsFloat" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsFloat,
            "FunctionPointerParameterTypeIsRawPointer" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsRawPointer,
            "FunctionPointerParameterTypeIsFixedArray" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsFixedArray,
            "FunctionPointerParameterTypeIsSlice" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsSlice,
            "FunctionPointerParameterTypeIsDynamic" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsDynamic,
            "FunctionPointerParameterTypeIsFunctionPointer" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsFunctionPointer,
            "FunctionPointerParameterTypeIsClosure" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsClosure,
            "FunctionPointerParameterTypeIsDynTrait" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsDynTrait,
            "FunctionPointerParameterTypeIsNamed" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsNamed,
            "FunctionPointerParameterTypeIsStruct" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsStruct,
            "FunctionPointerParameterTypeIsRecord" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsRecord,
            "FunctionPointerParameterTypeIsEnum" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsEnum,
            "FunctionPointerParameterTypeIsTrait" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsTrait,
            "FunctionPointerParameterTypeIsDoctrine" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsDoctrine,
            "FunctionPointerParameterTypeHasConcreteLayout" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeHasConcreteLayout,
            "FunctionPointerReturnTypeDisplayName" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeDisplayName,
            "FunctionPointerReturnTypeBaseName" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeBaseName,
            "FunctionPointerReturnTypeModuleName" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeModuleName,
            "FunctionPointerReturnTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsGenericInstantiation,
            "FunctionPointerReturnTypeArgumentCount" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeArgumentCount,
            "FunctionPointerReturnTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeComptimeArgumentCount,
            "FunctionPointerReturnTypeHasCSourceAlias" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeHasCSourceAlias,
            "FunctionPointerReturnTypeCSourceAliasName" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeCSourceAliasName,
            "FunctionPointerReturnTypeHasQualifiers" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeHasQualifiers,
            "FunctionPointerReturnTypeBorrowKindIsNone" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeBorrowKindIsNone,
            "FunctionPointerReturnTypeBorrowKindIsBorrow" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeBorrowKindIsBorrow,
            "FunctionPointerReturnTypeBorrowKindIsRetBorrow" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeBorrowKindIsRetBorrow,
            "FunctionPointerReturnTypeBorrowKindIsStoreBorrow" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeBorrowKindIsStoreBorrow,
            "FunctionPointerReturnTypeAccessKindIsNone" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeAccessKindIsNone,
            "FunctionPointerReturnTypeAccessKindIsShared" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeAccessKindIsShared,
            "FunctionPointerReturnTypeAccessKindIsFrozen" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeAccessKindIsFrozen,
            "FunctionPointerReturnTypeInitializationKindIsNone" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeInitializationKindIsNone,
            "FunctionPointerReturnTypeInitializationKindIsOut" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeInitializationKindIsOut,
            "FunctionPointerReturnTypeInitializationKindIsInit" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeInitializationKindIsInit,
            "FunctionPointerReturnTypeIsMutableView" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsMutableView,
            "FunctionPointerReturnTypeUnqualifiedTypeIs" => CompileTimeStructuralFactKind.FunctionPointerReturnTypeUnqualifiedTypeIs,
            "FunctionPointerParameterTypeDisplayName" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeDisplayName,
            "FunctionPointerParameterTypeBaseName" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeBaseName,
            "FunctionPointerParameterTypeModuleName" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeModuleName,
            "FunctionPointerParameterTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsGenericInstantiation,
            "FunctionPointerParameterTypeArgumentCount" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeArgumentCount,
            "FunctionPointerParameterTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeComptimeArgumentCount,
            "FunctionPointerParameterTypeHasCSourceAlias" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeHasCSourceAlias,
            "FunctionPointerParameterTypeCSourceAliasName" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeCSourceAliasName,
            "FunctionPointerParameterTypeHasQualifiers" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeHasQualifiers,
            "FunctionPointerParameterTypeBorrowKindIsNone" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeBorrowKindIsNone,
            "FunctionPointerParameterTypeBorrowKindIsBorrow" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeBorrowKindIsBorrow,
            "FunctionPointerParameterTypeBorrowKindIsRetBorrow" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeBorrowKindIsRetBorrow,
            "FunctionPointerParameterTypeBorrowKindIsStoreBorrow" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeBorrowKindIsStoreBorrow,
            "FunctionPointerParameterTypeAccessKindIsNone" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeAccessKindIsNone,
            "FunctionPointerParameterTypeAccessKindIsShared" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeAccessKindIsShared,
            "FunctionPointerParameterTypeAccessKindIsFrozen" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeAccessKindIsFrozen,
            "FunctionPointerParameterTypeInitializationKindIsNone" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeInitializationKindIsNone,
            "FunctionPointerParameterTypeInitializationKindIsOut" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeInitializationKindIsOut,
            "FunctionPointerParameterTypeInitializationKindIsInit" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeInitializationKindIsInit,
            "FunctionPointerParameterTypeIsMutableView" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsMutableView,
            "FunctionPointerParameterTypeUnqualifiedTypeIs" => CompileTimeStructuralFactKind.FunctionPointerParameterTypeUnqualifiedTypeIs,
            "FunctionPointerParameterHasRawPointerElementCountExpression" => CompileTimeStructuralFactKind.FunctionPointerParameterHasRawPointerElementCountExpression,
            "FunctionPointerParameterRawPointerElementCountExpression" => CompileTimeStructuralFactKind.FunctionPointerParameterRawPointerElementCountExpression,
            "FunctionPointerKindIsFn" => CompileTimeStructuralFactKind.FunctionPointerKindIsFn,
            "FunctionPointerKindIsFinite" => CompileTimeStructuralFactKind.FunctionPointerKindIsFinite,
            "FunctionPointerKindIsLaw" => CompileTimeStructuralFactKind.FunctionPointerKindIsLaw,
            "FunctionPointerKindIsFiniteLaw" => CompileTimeStructuralFactKind.FunctionPointerKindIsFiniteLaw,
            "FunctionPointerIsUnsafe" => CompileTimeStructuralFactKind.FunctionPointerIsUnsafe,
            "FunctionPointerHasFfiAbi" => CompileTimeStructuralFactKind.FunctionPointerHasFfiAbi,
            "FunctionPointerAbiIsC" => CompileTimeStructuralFactKind.FunctionPointerAbiIsC,
            "FunctionPointerAbiIsCDecl" => CompileTimeStructuralFactKind.FunctionPointerAbiIsCDecl,
            "FunctionPointerAbiIsStdCall" => CompileTimeStructuralFactKind.FunctionPointerAbiIsStdCall,
            "FunctionPointerAbiIsFastCall" => CompileTimeStructuralFactKind.FunctionPointerAbiIsFastCall,
            "FunctionPointerAbiIsThisCall" => CompileTimeStructuralFactKind.FunctionPointerAbiIsThisCall,
            "FunctionPointerAbiIsVectorCall" => CompileTimeStructuralFactKind.FunctionPointerAbiIsVectorCall,
            "FunctionPointerAbiIsSysV" => CompileTimeStructuralFactKind.FunctionPointerAbiIsSysV,
            "FunctionPointerAbiIsWin64" => CompileTimeStructuralFactKind.FunctionPointerAbiIsWin64,
            "FunctionPointerAbiIsAapcs" => CompileTimeStructuralFactKind.FunctionPointerAbiIsAapcs,
            "FunctionPointerAbiIsAapcs64" => CompileTimeStructuralFactKind.FunctionPointerAbiIsAapcs64,
            "FunctionPointerParametersAreDisjoint" => CompileTimeStructuralFactKind.FunctionPointerParametersAreDisjoint,
            "FunctionPointerParametersOverlap" => CompileTimeStructuralFactKind.FunctionPointerParametersOverlap,
            "FunctionPointerParametersAreSame" => CompileTimeStructuralFactKind.FunctionPointerParametersAreSame,
            "ClosureParameterCount" => CompileTimeStructuralFactKind.ClosureParameterCount,
            "ClosureReturnTypeIs" => CompileTimeStructuralFactKind.ClosureReturnTypeIs,
            "ClosureParameterTypeIs" => CompileTimeStructuralFactKind.ClosureParameterTypeIs,
            "ClosureReturnTypeIsBool" => CompileTimeStructuralFactKind.ClosureReturnTypeIsBool,
            "ClosureReturnTypeIsInteger" => CompileTimeStructuralFactKind.ClosureReturnTypeIsInteger,
            "ClosureReturnTypeIsFloat" => CompileTimeStructuralFactKind.ClosureReturnTypeIsFloat,
            "ClosureReturnTypeIsRawPointer" => CompileTimeStructuralFactKind.ClosureReturnTypeIsRawPointer,
            "ClosureReturnTypeIsFixedArray" => CompileTimeStructuralFactKind.ClosureReturnTypeIsFixedArray,
            "ClosureReturnTypeIsSlice" => CompileTimeStructuralFactKind.ClosureReturnTypeIsSlice,
            "ClosureReturnTypeIsDynamic" => CompileTimeStructuralFactKind.ClosureReturnTypeIsDynamic,
            "ClosureReturnTypeIsFunctionPointer" => CompileTimeStructuralFactKind.ClosureReturnTypeIsFunctionPointer,
            "ClosureReturnTypeIsClosure" => CompileTimeStructuralFactKind.ClosureReturnTypeIsClosure,
            "ClosureReturnTypeIsDynTrait" => CompileTimeStructuralFactKind.ClosureReturnTypeIsDynTrait,
            "ClosureReturnTypeIsNamed" => CompileTimeStructuralFactKind.ClosureReturnTypeIsNamed,
            "ClosureReturnTypeIsStruct" => CompileTimeStructuralFactKind.ClosureReturnTypeIsStruct,
            "ClosureReturnTypeIsRecord" => CompileTimeStructuralFactKind.ClosureReturnTypeIsRecord,
            "ClosureReturnTypeIsEnum" => CompileTimeStructuralFactKind.ClosureReturnTypeIsEnum,
            "ClosureReturnTypeIsTrait" => CompileTimeStructuralFactKind.ClosureReturnTypeIsTrait,
            "ClosureReturnTypeIsDoctrine" => CompileTimeStructuralFactKind.ClosureReturnTypeIsDoctrine,
            "ClosureReturnTypeHasConcreteLayout" => CompileTimeStructuralFactKind.ClosureReturnTypeHasConcreteLayout,
            "ClosureParameterTypeIsBool" => CompileTimeStructuralFactKind.ClosureParameterTypeIsBool,
            "ClosureParameterTypeIsInteger" => CompileTimeStructuralFactKind.ClosureParameterTypeIsInteger,
            "ClosureParameterTypeIsFloat" => CompileTimeStructuralFactKind.ClosureParameterTypeIsFloat,
            "ClosureParameterTypeIsRawPointer" => CompileTimeStructuralFactKind.ClosureParameterTypeIsRawPointer,
            "ClosureParameterTypeIsFixedArray" => CompileTimeStructuralFactKind.ClosureParameterTypeIsFixedArray,
            "ClosureParameterTypeIsSlice" => CompileTimeStructuralFactKind.ClosureParameterTypeIsSlice,
            "ClosureParameterTypeIsDynamic" => CompileTimeStructuralFactKind.ClosureParameterTypeIsDynamic,
            "ClosureParameterTypeIsFunctionPointer" => CompileTimeStructuralFactKind.ClosureParameterTypeIsFunctionPointer,
            "ClosureParameterTypeIsClosure" => CompileTimeStructuralFactKind.ClosureParameterTypeIsClosure,
            "ClosureParameterTypeIsDynTrait" => CompileTimeStructuralFactKind.ClosureParameterTypeIsDynTrait,
            "ClosureParameterTypeIsNamed" => CompileTimeStructuralFactKind.ClosureParameterTypeIsNamed,
            "ClosureParameterTypeIsStruct" => CompileTimeStructuralFactKind.ClosureParameterTypeIsStruct,
            "ClosureParameterTypeIsRecord" => CompileTimeStructuralFactKind.ClosureParameterTypeIsRecord,
            "ClosureParameterTypeIsEnum" => CompileTimeStructuralFactKind.ClosureParameterTypeIsEnum,
            "ClosureParameterTypeIsTrait" => CompileTimeStructuralFactKind.ClosureParameterTypeIsTrait,
            "ClosureParameterTypeIsDoctrine" => CompileTimeStructuralFactKind.ClosureParameterTypeIsDoctrine,
            "ClosureParameterTypeHasConcreteLayout" => CompileTimeStructuralFactKind.ClosureParameterTypeHasConcreteLayout,
            "ClosureReturnTypeDisplayName" => CompileTimeStructuralFactKind.ClosureReturnTypeDisplayName,
            "ClosureReturnTypeBaseName" => CompileTimeStructuralFactKind.ClosureReturnTypeBaseName,
            "ClosureReturnTypeModuleName" => CompileTimeStructuralFactKind.ClosureReturnTypeModuleName,
            "ClosureReturnTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.ClosureReturnTypeIsGenericInstantiation,
            "ClosureReturnTypeArgumentCount" => CompileTimeStructuralFactKind.ClosureReturnTypeArgumentCount,
            "ClosureReturnTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.ClosureReturnTypeComptimeArgumentCount,
            "ClosureReturnTypeHasCSourceAlias" => CompileTimeStructuralFactKind.ClosureReturnTypeHasCSourceAlias,
            "ClosureReturnTypeCSourceAliasName" => CompileTimeStructuralFactKind.ClosureReturnTypeCSourceAliasName,
            "ClosureReturnTypeHasQualifiers" => CompileTimeStructuralFactKind.ClosureReturnTypeHasQualifiers,
            "ClosureReturnTypeBorrowKindIsNone" => CompileTimeStructuralFactKind.ClosureReturnTypeBorrowKindIsNone,
            "ClosureReturnTypeBorrowKindIsBorrow" => CompileTimeStructuralFactKind.ClosureReturnTypeBorrowKindIsBorrow,
            "ClosureReturnTypeBorrowKindIsRetBorrow" => CompileTimeStructuralFactKind.ClosureReturnTypeBorrowKindIsRetBorrow,
            "ClosureReturnTypeBorrowKindIsStoreBorrow" => CompileTimeStructuralFactKind.ClosureReturnTypeBorrowKindIsStoreBorrow,
            "ClosureReturnTypeAccessKindIsNone" => CompileTimeStructuralFactKind.ClosureReturnTypeAccessKindIsNone,
            "ClosureReturnTypeAccessKindIsShared" => CompileTimeStructuralFactKind.ClosureReturnTypeAccessKindIsShared,
            "ClosureReturnTypeAccessKindIsFrozen" => CompileTimeStructuralFactKind.ClosureReturnTypeAccessKindIsFrozen,
            "ClosureReturnTypeInitializationKindIsNone" => CompileTimeStructuralFactKind.ClosureReturnTypeInitializationKindIsNone,
            "ClosureReturnTypeInitializationKindIsOut" => CompileTimeStructuralFactKind.ClosureReturnTypeInitializationKindIsOut,
            "ClosureReturnTypeInitializationKindIsInit" => CompileTimeStructuralFactKind.ClosureReturnTypeInitializationKindIsInit,
            "ClosureReturnTypeIsMutableView" => CompileTimeStructuralFactKind.ClosureReturnTypeIsMutableView,
            "ClosureReturnTypeUnqualifiedTypeIs" => CompileTimeStructuralFactKind.ClosureReturnTypeUnqualifiedTypeIs,
            "ClosureParameterTypeDisplayName" => CompileTimeStructuralFactKind.ClosureParameterTypeDisplayName,
            "ClosureParameterTypeBaseName" => CompileTimeStructuralFactKind.ClosureParameterTypeBaseName,
            "ClosureParameterTypeModuleName" => CompileTimeStructuralFactKind.ClosureParameterTypeModuleName,
            "ClosureParameterTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.ClosureParameterTypeIsGenericInstantiation,
            "ClosureParameterTypeArgumentCount" => CompileTimeStructuralFactKind.ClosureParameterTypeArgumentCount,
            "ClosureParameterTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.ClosureParameterTypeComptimeArgumentCount,
            "ClosureParameterTypeHasCSourceAlias" => CompileTimeStructuralFactKind.ClosureParameterTypeHasCSourceAlias,
            "ClosureParameterTypeCSourceAliasName" => CompileTimeStructuralFactKind.ClosureParameterTypeCSourceAliasName,
            "ClosureParameterTypeHasQualifiers" => CompileTimeStructuralFactKind.ClosureParameterTypeHasQualifiers,
            "ClosureParameterTypeBorrowKindIsNone" => CompileTimeStructuralFactKind.ClosureParameterTypeBorrowKindIsNone,
            "ClosureParameterTypeBorrowKindIsBorrow" => CompileTimeStructuralFactKind.ClosureParameterTypeBorrowKindIsBorrow,
            "ClosureParameterTypeBorrowKindIsRetBorrow" => CompileTimeStructuralFactKind.ClosureParameterTypeBorrowKindIsRetBorrow,
            "ClosureParameterTypeBorrowKindIsStoreBorrow" => CompileTimeStructuralFactKind.ClosureParameterTypeBorrowKindIsStoreBorrow,
            "ClosureParameterTypeAccessKindIsNone" => CompileTimeStructuralFactKind.ClosureParameterTypeAccessKindIsNone,
            "ClosureParameterTypeAccessKindIsShared" => CompileTimeStructuralFactKind.ClosureParameterTypeAccessKindIsShared,
            "ClosureParameterTypeAccessKindIsFrozen" => CompileTimeStructuralFactKind.ClosureParameterTypeAccessKindIsFrozen,
            "ClosureParameterTypeInitializationKindIsNone" => CompileTimeStructuralFactKind.ClosureParameterTypeInitializationKindIsNone,
            "ClosureParameterTypeInitializationKindIsOut" => CompileTimeStructuralFactKind.ClosureParameterTypeInitializationKindIsOut,
            "ClosureParameterTypeInitializationKindIsInit" => CompileTimeStructuralFactKind.ClosureParameterTypeInitializationKindIsInit,
            "ClosureParameterTypeIsMutableView" => CompileTimeStructuralFactKind.ClosureParameterTypeIsMutableView,
            "ClosureParameterTypeUnqualifiedTypeIs" => CompileTimeStructuralFactKind.ClosureParameterTypeUnqualifiedTypeIs,
            "ClosureParameterHasRawPointerElementCountExpression" => CompileTimeStructuralFactKind.ClosureParameterHasRawPointerElementCountExpression,
            "ClosureParameterRawPointerElementCountExpression" => CompileTimeStructuralFactKind.ClosureParameterRawPointerElementCountExpression,
            "ClosureKindIsFn" => CompileTimeStructuralFactKind.ClosureKindIsFn,
            "ClosureKindIsFinite" => CompileTimeStructuralFactKind.ClosureKindIsFinite,
            "ClosureKindIsLaw" => CompileTimeStructuralFactKind.ClosureKindIsLaw,
            "ClosureKindIsFiniteLaw" => CompileTimeStructuralFactKind.ClosureKindIsFiniteLaw,
            "ClosureStorageIsBorrow" => CompileTimeStructuralFactKind.ClosureStorageIsBorrow,
            "ClosureStorageIsHeap" => CompileTimeStructuralFactKind.ClosureStorageIsHeap,
            "ClosureStorageIsInline" => CompileTimeStructuralFactKind.ClosureStorageIsInline,
            "ClosureCallCapabilityIsNormal" => CompileTimeStructuralFactKind.ClosureCallCapabilityIsNormal,
            "ClosureCallCapabilityIsMut" => CompileTimeStructuralFactKind.ClosureCallCapabilityIsMut,
            "ClosureCallCapabilityIsOnce" => CompileTimeStructuralFactKind.ClosureCallCapabilityIsOnce,
            "ClosureParametersAreDisjoint" => CompileTimeStructuralFactKind.ClosureParametersAreDisjoint,
            "ClosureParametersOverlap" => CompileTimeStructuralFactKind.ClosureParametersOverlap,
            "ClosureParametersAreSame" => CompileTimeStructuralFactKind.ClosureParametersAreSame,
            "DynTraitIsView" => CompileTimeStructuralFactKind.DynTraitIsView,
            "DynTraitIsHeap" => CompileTimeStructuralFactKind.DynTraitIsHeap,
            "DynTraitTargetTypeIs" => CompileTimeStructuralFactKind.DynTraitTargetTypeIs,
            "MethodCount" => CompileTimeStructuralFactKind.MethodCount,
            "MethodName" => CompileTimeStructuralFactKind.MethodName,
            "MethodModuleName" => CompileTimeStructuralFactKind.MethodModuleName,
            "MethodVisibilityIsModule" => CompileTimeStructuralFactKind.MethodVisibilityIsModule,
            "MethodVisibilityIsInternal" => CompileTimeStructuralFactKind.MethodVisibilityIsInternal,
            "MethodVisibilityIsPublic" => CompileTimeStructuralFactKind.MethodVisibilityIsPublic,
            "MethodVisibilityIsExport" => CompileTimeStructuralFactKind.MethodVisibilityIsExport,
            "MethodParameterCount" => CompileTimeStructuralFactKind.MethodParameterCount,
            "MethodParameterName" => CompileTimeStructuralFactKind.MethodParameterName,
            "MethodReturnTypeIs" => CompileTimeStructuralFactKind.MethodReturnTypeIs,
            "MethodParameterTypeIs" => CompileTimeStructuralFactKind.MethodParameterTypeIs,
            "MethodReturnTypeIsBool" => CompileTimeStructuralFactKind.MethodReturnTypeIsBool,
            "MethodReturnTypeIsInteger" => CompileTimeStructuralFactKind.MethodReturnTypeIsInteger,
            "MethodReturnTypeIsFloat" => CompileTimeStructuralFactKind.MethodReturnTypeIsFloat,
            "MethodReturnTypeIsRawPointer" => CompileTimeStructuralFactKind.MethodReturnTypeIsRawPointer,
            "MethodReturnTypeIsFixedArray" => CompileTimeStructuralFactKind.MethodReturnTypeIsFixedArray,
            "MethodReturnTypeIsSlice" => CompileTimeStructuralFactKind.MethodReturnTypeIsSlice,
            "MethodReturnTypeIsDynamic" => CompileTimeStructuralFactKind.MethodReturnTypeIsDynamic,
            "MethodReturnTypeIsFunctionPointer" => CompileTimeStructuralFactKind.MethodReturnTypeIsFunctionPointer,
            "MethodReturnTypeIsClosure" => CompileTimeStructuralFactKind.MethodReturnTypeIsClosure,
            "MethodReturnTypeIsDynTrait" => CompileTimeStructuralFactKind.MethodReturnTypeIsDynTrait,
            "MethodReturnTypeIsNamed" => CompileTimeStructuralFactKind.MethodReturnTypeIsNamed,
            "MethodReturnTypeIsStruct" => CompileTimeStructuralFactKind.MethodReturnTypeIsStruct,
            "MethodReturnTypeIsRecord" => CompileTimeStructuralFactKind.MethodReturnTypeIsRecord,
            "MethodReturnTypeIsEnum" => CompileTimeStructuralFactKind.MethodReturnTypeIsEnum,
            "MethodReturnTypeIsTrait" => CompileTimeStructuralFactKind.MethodReturnTypeIsTrait,
            "MethodReturnTypeIsDoctrine" => CompileTimeStructuralFactKind.MethodReturnTypeIsDoctrine,
            "MethodReturnTypeHasConcreteLayout" => CompileTimeStructuralFactKind.MethodReturnTypeHasConcreteLayout,
            "MethodParameterTypeIsBool" => CompileTimeStructuralFactKind.MethodParameterTypeIsBool,
            "MethodParameterTypeIsInteger" => CompileTimeStructuralFactKind.MethodParameterTypeIsInteger,
            "MethodParameterTypeIsFloat" => CompileTimeStructuralFactKind.MethodParameterTypeIsFloat,
            "MethodParameterTypeIsRawPointer" => CompileTimeStructuralFactKind.MethodParameterTypeIsRawPointer,
            "MethodParameterTypeIsFixedArray" => CompileTimeStructuralFactKind.MethodParameterTypeIsFixedArray,
            "MethodParameterTypeIsSlice" => CompileTimeStructuralFactKind.MethodParameterTypeIsSlice,
            "MethodParameterTypeIsDynamic" => CompileTimeStructuralFactKind.MethodParameterTypeIsDynamic,
            "MethodParameterTypeIsFunctionPointer" => CompileTimeStructuralFactKind.MethodParameterTypeIsFunctionPointer,
            "MethodParameterTypeIsClosure" => CompileTimeStructuralFactKind.MethodParameterTypeIsClosure,
            "MethodParameterTypeIsDynTrait" => CompileTimeStructuralFactKind.MethodParameterTypeIsDynTrait,
            "MethodParameterTypeIsNamed" => CompileTimeStructuralFactKind.MethodParameterTypeIsNamed,
            "MethodParameterTypeIsStruct" => CompileTimeStructuralFactKind.MethodParameterTypeIsStruct,
            "MethodParameterTypeIsRecord" => CompileTimeStructuralFactKind.MethodParameterTypeIsRecord,
            "MethodParameterTypeIsEnum" => CompileTimeStructuralFactKind.MethodParameterTypeIsEnum,
            "MethodParameterTypeIsTrait" => CompileTimeStructuralFactKind.MethodParameterTypeIsTrait,
            "MethodParameterTypeIsDoctrine" => CompileTimeStructuralFactKind.MethodParameterTypeIsDoctrine,
            "MethodParameterTypeHasConcreteLayout" => CompileTimeStructuralFactKind.MethodParameterTypeHasConcreteLayout,
            "MethodKindIsFn" => CompileTimeStructuralFactKind.MethodKindIsFn,
            "MethodKindIsFinite" => CompileTimeStructuralFactKind.MethodKindIsFinite,
            "MethodKindIsLaw" => CompileTimeStructuralFactKind.MethodKindIsLaw,
            "MethodKindIsFiniteLaw" => CompileTimeStructuralFactKind.MethodKindIsFiniteLaw,
            "MethodIsStatic" => CompileTimeStructuralFactKind.MethodIsStatic,
            "MethodHasBody" => CompileTimeStructuralFactKind.MethodHasBody,
            "MethodIsUnsafe" => CompileTimeStructuralFactKind.MethodIsUnsafe,
            "MethodIsVarargs" => CompileTimeStructuralFactKind.MethodIsVarargs,
            "MethodHasFfiAbi" => CompileTimeStructuralFactKind.MethodHasFfiAbi,
            "MethodAbiIsC" => CompileTimeStructuralFactKind.MethodAbiIsC,
            "MethodAbiIsCDecl" => CompileTimeStructuralFactKind.MethodAbiIsCDecl,
            "MethodAbiIsStdCall" => CompileTimeStructuralFactKind.MethodAbiIsStdCall,
            "MethodAbiIsFastCall" => CompileTimeStructuralFactKind.MethodAbiIsFastCall,
            "MethodAbiIsThisCall" => CompileTimeStructuralFactKind.MethodAbiIsThisCall,
            "MethodAbiIsVectorCall" => CompileTimeStructuralFactKind.MethodAbiIsVectorCall,
            "MethodAbiIsSysV" => CompileTimeStructuralFactKind.MethodAbiIsSysV,
            "MethodAbiIsWin64" => CompileTimeStructuralFactKind.MethodAbiIsWin64,
            "MethodAbiIsAapcs" => CompileTimeStructuralFactKind.MethodAbiIsAapcs,
            "MethodAbiIsAapcs64" => CompileTimeStructuralFactKind.MethodAbiIsAapcs64,
            "MethodParametersAreDisjoint" => CompileTimeStructuralFactKind.MethodParametersAreDisjoint,
            "MethodParametersOverlap" => CompileTimeStructuralFactKind.MethodParametersOverlap,
            "MethodParametersAreSame" => CompileTimeStructuralFactKind.MethodParametersAreSame,
            "MethodGenericParameterCount" => CompileTimeStructuralFactKind.MethodGenericParameterCount,
            "MethodGenericParameterName" => CompileTimeStructuralFactKind.MethodGenericParameterName,
            "MethodGenericParameterTraitBoundCount" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundCount,
            "MethodGenericParameterTraitBoundTypeIs" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIs,
            "MethodGenericParameterTraitBoundTypeIsBool" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsBool,
            "MethodGenericParameterTraitBoundTypeIsInteger" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsInteger,
            "MethodGenericParameterTraitBoundTypeIsFloat" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsFloat,
            "MethodGenericParameterTraitBoundTypeIsRawPointer" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsRawPointer,
            "MethodGenericParameterTraitBoundTypeIsFixedArray" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsFixedArray,
            "MethodGenericParameterTraitBoundTypeIsSlice" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsSlice,
            "MethodGenericParameterTraitBoundTypeIsDynamic" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsDynamic,
            "MethodGenericParameterTraitBoundTypeIsFunctionPointer" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsFunctionPointer,
            "MethodGenericParameterTraitBoundTypeIsClosure" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsClosure,
            "MethodGenericParameterTraitBoundTypeIsDynTrait" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsDynTrait,
            "MethodGenericParameterTraitBoundTypeIsNamed" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsNamed,
            "MethodGenericParameterTraitBoundTypeIsStruct" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsStruct,
            "MethodGenericParameterTraitBoundTypeIsRecord" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsRecord,
            "MethodGenericParameterTraitBoundTypeIsEnum" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsEnum,
            "MethodGenericParameterTraitBoundTypeIsTrait" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsTrait,
            "MethodGenericParameterTraitBoundTypeIsDoctrine" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsDoctrine,
            "MethodGenericParameterTraitBoundTypeHasConcreteLayout" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeHasConcreteLayout,
            "MethodGenericParameterTraitBoundTypeDisplayName" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeDisplayName,
            "MethodGenericParameterTraitBoundTypeBaseName" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeBaseName,
            "MethodGenericParameterTraitBoundTypeModuleName" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeModuleName,
            "MethodGenericParameterTraitBoundTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsGenericInstantiation,
            "MethodGenericParameterTraitBoundTypeArgumentCount" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeArgumentCount,
            "MethodGenericParameterTraitBoundTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeComptimeArgumentCount,
            "MethodComptimeGenericParameterCount" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterCount,
            "MethodComptimeGenericParameterName" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterName,
            "MethodComptimeGenericParameterTypeIs" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIs,
            "MethodComptimeGenericParameterTypeIsBool" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsBool,
            "MethodComptimeGenericParameterTypeIsInteger" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsInteger,
            "MethodComptimeGenericParameterTypeIsFloat" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsFloat,
            "MethodComptimeGenericParameterTypeIsRawPointer" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsRawPointer,
            "MethodComptimeGenericParameterTypeIsFixedArray" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsFixedArray,
            "MethodComptimeGenericParameterTypeIsSlice" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsSlice,
            "MethodComptimeGenericParameterTypeIsDynamic" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsDynamic,
            "MethodComptimeGenericParameterTypeIsFunctionPointer" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsFunctionPointer,
            "MethodComptimeGenericParameterTypeIsClosure" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsClosure,
            "MethodComptimeGenericParameterTypeIsDynTrait" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsDynTrait,
            "MethodComptimeGenericParameterTypeIsNamed" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsNamed,
            "MethodComptimeGenericParameterTypeIsStruct" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsStruct,
            "MethodComptimeGenericParameterTypeIsRecord" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsRecord,
            "MethodComptimeGenericParameterTypeIsEnum" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsEnum,
            "MethodComptimeGenericParameterTypeIsTrait" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsTrait,
            "MethodComptimeGenericParameterTypeIsDoctrine" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsDoctrine,
            "MethodComptimeGenericParameterTypeHasConcreteLayout" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeHasConcreteLayout,
            "MethodComptimeGenericParameterTypeDisplayName" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeDisplayName,
            "MethodComptimeGenericParameterTypeBaseName" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeBaseName,
            "MethodComptimeGenericParameterTypeModuleName" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeModuleName,
            "MethodComptimeGenericParameterTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsGenericInstantiation,
            "MethodComptimeGenericParameterTypeArgumentCount" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeArgumentCount,
            "MethodComptimeGenericParameterTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeComptimeArgumentCount,
            "MethodThreadSafetyLawPredicateCount" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateCount,
            "MethodThreadSafetyLawPredicateLawName" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateLawName,
            "MethodThreadSafetyLawPredicateTypeIs" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIs,
            "MethodThreadSafetyLawPredicateTypeIsBool" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsBool,
            "MethodThreadSafetyLawPredicateTypeIsInteger" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsInteger,
            "MethodThreadSafetyLawPredicateTypeIsFloat" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsFloat,
            "MethodThreadSafetyLawPredicateTypeIsRawPointer" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsRawPointer,
            "MethodThreadSafetyLawPredicateTypeIsFixedArray" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsFixedArray,
            "MethodThreadSafetyLawPredicateTypeIsSlice" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsSlice,
            "MethodThreadSafetyLawPredicateTypeIsDynamic" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsDynamic,
            "MethodThreadSafetyLawPredicateTypeIsFunctionPointer" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsFunctionPointer,
            "MethodThreadSafetyLawPredicateTypeIsClosure" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsClosure,
            "MethodThreadSafetyLawPredicateTypeIsDynTrait" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsDynTrait,
            "MethodThreadSafetyLawPredicateTypeIsNamed" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsNamed,
            "MethodThreadSafetyLawPredicateTypeIsStruct" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsStruct,
            "MethodThreadSafetyLawPredicateTypeIsRecord" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsRecord,
            "MethodThreadSafetyLawPredicateTypeIsEnum" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsEnum,
            "MethodThreadSafetyLawPredicateTypeIsTrait" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsTrait,
            "MethodThreadSafetyLawPredicateTypeIsDoctrine" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsDoctrine,
            "MethodThreadSafetyLawPredicateTypeHasConcreteLayout" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeHasConcreteLayout,
            "MethodThreadSafetyLawPredicateTypeDisplayName" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeDisplayName,
            "MethodThreadSafetyLawPredicateTypeBaseName" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeBaseName,
            "MethodThreadSafetyLawPredicateTypeModuleName" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeModuleName,
            "MethodThreadSafetyLawPredicateTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsGenericInstantiation,
            "MethodThreadSafetyLawPredicateTypeArgumentCount" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeArgumentCount,
            "MethodThreadSafetyLawPredicateTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeComptimeArgumentCount,
            "MethodReturnTypeDisplayName" => CompileTimeStructuralFactKind.MethodReturnTypeDisplayName,
            "MethodReturnTypeBaseName" => CompileTimeStructuralFactKind.MethodReturnTypeBaseName,
            "MethodReturnTypeModuleName" => CompileTimeStructuralFactKind.MethodReturnTypeModuleName,
            "MethodReturnTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.MethodReturnTypeIsGenericInstantiation,
            "MethodReturnTypeArgumentCount" => CompileTimeStructuralFactKind.MethodReturnTypeArgumentCount,
            "MethodReturnTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.MethodReturnTypeComptimeArgumentCount,
            "MethodReturnTypeHasCSourceAlias" => CompileTimeStructuralFactKind.MethodReturnTypeHasCSourceAlias,
            "MethodReturnTypeCSourceAliasName" => CompileTimeStructuralFactKind.MethodReturnTypeCSourceAliasName,
            "MethodReturnTypeHasQualifiers" => CompileTimeStructuralFactKind.MethodReturnTypeHasQualifiers,
            "MethodReturnTypeBorrowKindIsNone" => CompileTimeStructuralFactKind.MethodReturnTypeBorrowKindIsNone,
            "MethodReturnTypeBorrowKindIsBorrow" => CompileTimeStructuralFactKind.MethodReturnTypeBorrowKindIsBorrow,
            "MethodReturnTypeBorrowKindIsRetBorrow" => CompileTimeStructuralFactKind.MethodReturnTypeBorrowKindIsRetBorrow,
            "MethodReturnTypeBorrowKindIsStoreBorrow" => CompileTimeStructuralFactKind.MethodReturnTypeBorrowKindIsStoreBorrow,
            "MethodReturnTypeAccessKindIsNone" => CompileTimeStructuralFactKind.MethodReturnTypeAccessKindIsNone,
            "MethodReturnTypeAccessKindIsShared" => CompileTimeStructuralFactKind.MethodReturnTypeAccessKindIsShared,
            "MethodReturnTypeAccessKindIsFrozen" => CompileTimeStructuralFactKind.MethodReturnTypeAccessKindIsFrozen,
            "MethodReturnTypeInitializationKindIsNone" => CompileTimeStructuralFactKind.MethodReturnTypeInitializationKindIsNone,
            "MethodReturnTypeInitializationKindIsOut" => CompileTimeStructuralFactKind.MethodReturnTypeInitializationKindIsOut,
            "MethodReturnTypeInitializationKindIsInit" => CompileTimeStructuralFactKind.MethodReturnTypeInitializationKindIsInit,
            "MethodReturnTypeIsMutableView" => CompileTimeStructuralFactKind.MethodReturnTypeIsMutableView,
            "MethodReturnTypeUnqualifiedTypeIs" => CompileTimeStructuralFactKind.MethodReturnTypeUnqualifiedTypeIs,
            "MethodParameterTypeDisplayName" => CompileTimeStructuralFactKind.MethodParameterTypeDisplayName,
            "MethodParameterTypeBaseName" => CompileTimeStructuralFactKind.MethodParameterTypeBaseName,
            "MethodParameterTypeModuleName" => CompileTimeStructuralFactKind.MethodParameterTypeModuleName,
            "MethodParameterTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.MethodParameterTypeIsGenericInstantiation,
            "MethodParameterTypeArgumentCount" => CompileTimeStructuralFactKind.MethodParameterTypeArgumentCount,
            "MethodParameterTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.MethodParameterTypeComptimeArgumentCount,
            "MethodParameterTypeHasCSourceAlias" => CompileTimeStructuralFactKind.MethodParameterTypeHasCSourceAlias,
            "MethodParameterTypeCSourceAliasName" => CompileTimeStructuralFactKind.MethodParameterTypeCSourceAliasName,
            "MethodParameterTypeHasQualifiers" => CompileTimeStructuralFactKind.MethodParameterTypeHasQualifiers,
            "MethodParameterTypeBorrowKindIsNone" => CompileTimeStructuralFactKind.MethodParameterTypeBorrowKindIsNone,
            "MethodParameterTypeBorrowKindIsBorrow" => CompileTimeStructuralFactKind.MethodParameterTypeBorrowKindIsBorrow,
            "MethodParameterTypeBorrowKindIsRetBorrow" => CompileTimeStructuralFactKind.MethodParameterTypeBorrowKindIsRetBorrow,
            "MethodParameterTypeBorrowKindIsStoreBorrow" => CompileTimeStructuralFactKind.MethodParameterTypeBorrowKindIsStoreBorrow,
            "MethodParameterTypeAccessKindIsNone" => CompileTimeStructuralFactKind.MethodParameterTypeAccessKindIsNone,
            "MethodParameterTypeAccessKindIsShared" => CompileTimeStructuralFactKind.MethodParameterTypeAccessKindIsShared,
            "MethodParameterTypeAccessKindIsFrozen" => CompileTimeStructuralFactKind.MethodParameterTypeAccessKindIsFrozen,
            "MethodParameterTypeInitializationKindIsNone" => CompileTimeStructuralFactKind.MethodParameterTypeInitializationKindIsNone,
            "MethodParameterTypeInitializationKindIsOut" => CompileTimeStructuralFactKind.MethodParameterTypeInitializationKindIsOut,
            "MethodParameterTypeInitializationKindIsInit" => CompileTimeStructuralFactKind.MethodParameterTypeInitializationKindIsInit,
            "MethodParameterTypeIsMutableView" => CompileTimeStructuralFactKind.MethodParameterTypeIsMutableView,
            "MethodParameterTypeUnqualifiedTypeIs" => CompileTimeStructuralFactKind.MethodParameterTypeUnqualifiedTypeIs,
            "MethodParameterHasRawPointerElementCountExpression" => CompileTimeStructuralFactKind.MethodParameterHasRawPointerElementCountExpression,
            "MethodParameterRawPointerElementCountExpression" => CompileTimeStructuralFactKind.MethodParameterRawPointerElementCountExpression,
            "FieldCount" => CompileTimeStructuralFactKind.FieldCount,
            "EnumVariantCount" => CompileTimeStructuralFactKind.EnumVariantCount,
            "FieldOffset" => CompileTimeStructuralFactKind.FieldOffset,
            "FieldSize" => CompileTimeStructuralFactKind.FieldSize,
            "FieldAlign" => CompileTimeStructuralFactKind.FieldAlign,
            "FieldIsMisaligned" => CompileTimeStructuralFactKind.FieldIsMisaligned,
            "StructLayoutIsAuto" => CompileTimeStructuralFactKind.StructLayoutIsAuto,
            "StructLayoutIsC" => CompileTimeStructuralFactKind.StructLayoutIsC,
            "StructLayoutIsExplicit" => CompileTimeStructuralFactKind.StructLayoutIsExplicit,
            "StructHasPack" => CompileTimeStructuralFactKind.StructHasPack,
            "StructPack" => CompileTimeStructuralFactKind.StructPack,
            "StructHasAlign" => CompileTimeStructuralFactKind.StructHasAlign,
            "StructAlign" => CompileTimeStructuralFactKind.StructAlign,
            "FieldHasExplicitOffset" => CompileTimeStructuralFactKind.FieldHasExplicitOffset,
            "FieldExplicitOffset" => CompileTimeStructuralFactKind.FieldExplicitOffset,
            "FieldTypeIsBool" => CompileTimeStructuralFactKind.FieldTypeIsBool,
            "FieldTypeIsInteger" => CompileTimeStructuralFactKind.FieldTypeIsInteger,
            "FieldTypeIsFloat" => CompileTimeStructuralFactKind.FieldTypeIsFloat,
            "FieldTypeIsRawPointer" => CompileTimeStructuralFactKind.FieldTypeIsRawPointer,
            "FieldTypeIsFixedArray" => CompileTimeStructuralFactKind.FieldTypeIsFixedArray,
            "FieldTypeIsSlice" => CompileTimeStructuralFactKind.FieldTypeIsSlice,
            "FieldTypeIsDynamic" => CompileTimeStructuralFactKind.FieldTypeIsDynamic,
            "FieldTypeIsFunctionPointer" => CompileTimeStructuralFactKind.FieldTypeIsFunctionPointer,
            "FieldTypeIsClosure" => CompileTimeStructuralFactKind.FieldTypeIsClosure,
            "FieldTypeIsDynTrait" => CompileTimeStructuralFactKind.FieldTypeIsDynTrait,
            "FieldTypeIsNamed" => CompileTimeStructuralFactKind.FieldTypeIsNamed,
            "FieldTypeIsStruct" => CompileTimeStructuralFactKind.FieldTypeIsStruct,
            "FieldTypeIsRecord" => CompileTimeStructuralFactKind.FieldTypeIsRecord,
            "FieldTypeIsEnum" => CompileTimeStructuralFactKind.FieldTypeIsEnum,
            "FieldTypeIsTrait" => CompileTimeStructuralFactKind.FieldTypeIsTrait,
            "FieldTypeIsDoctrine" => CompileTimeStructuralFactKind.FieldTypeIsDoctrine,
            "FieldTypeHasConcreteLayout" => CompileTimeStructuralFactKind.FieldTypeHasConcreteLayout,
            "FieldTypeDisplayName" => CompileTimeStructuralFactKind.FieldTypeDisplayName,
            "FieldTypeBaseName" => CompileTimeStructuralFactKind.FieldTypeBaseName,
            "FieldTypeModuleName" => CompileTimeStructuralFactKind.FieldTypeModuleName,
            "FieldTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.FieldTypeIsGenericInstantiation,
            "FieldTypeArgumentCount" => CompileTimeStructuralFactKind.FieldTypeArgumentCount,
            "FieldTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.FieldTypeComptimeArgumentCount,
            "FieldTypeHasCSourceAlias" => CompileTimeStructuralFactKind.FieldTypeHasCSourceAlias,
            "FieldTypeCSourceAliasName" => CompileTimeStructuralFactKind.FieldTypeCSourceAliasName,
            "FieldTypeHasQualifiers" => CompileTimeStructuralFactKind.FieldTypeHasQualifiers,
            "FieldTypeBorrowKindIsNone" => CompileTimeStructuralFactKind.FieldTypeBorrowKindIsNone,
            "FieldTypeBorrowKindIsBorrow" => CompileTimeStructuralFactKind.FieldTypeBorrowKindIsBorrow,
            "FieldTypeBorrowKindIsRetBorrow" => CompileTimeStructuralFactKind.FieldTypeBorrowKindIsRetBorrow,
            "FieldTypeBorrowKindIsStoreBorrow" => CompileTimeStructuralFactKind.FieldTypeBorrowKindIsStoreBorrow,
            "FieldTypeAccessKindIsNone" => CompileTimeStructuralFactKind.FieldTypeAccessKindIsNone,
            "FieldTypeAccessKindIsShared" => CompileTimeStructuralFactKind.FieldTypeAccessKindIsShared,
            "FieldTypeAccessKindIsFrozen" => CompileTimeStructuralFactKind.FieldTypeAccessKindIsFrozen,
            "FieldTypeInitializationKindIsNone" => CompileTimeStructuralFactKind.FieldTypeInitializationKindIsNone,
            "FieldTypeInitializationKindIsOut" => CompileTimeStructuralFactKind.FieldTypeInitializationKindIsOut,
            "FieldTypeInitializationKindIsInit" => CompileTimeStructuralFactKind.FieldTypeInitializationKindIsInit,
            "FieldTypeIsMutableView" => CompileTimeStructuralFactKind.FieldTypeIsMutableView,
            "FieldTypeUnqualifiedTypeIs" => CompileTimeStructuralFactKind.FieldTypeUnqualifiedTypeIs,
            "TypeGenericParameterCount" => CompileTimeStructuralFactKind.TypeGenericParameterCount,
            "TypeGenericParameterName" => CompileTimeStructuralFactKind.TypeGenericParameterName,
            "TypeComptimeGenericParameterCount" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterCount,
            "TypeComptimeGenericParameterName" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterName,
            "TypeComptimeGenericParameterTypeIs" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIs,
            "TypeComptimeGenericParameterTypeIsBool" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsBool,
            "TypeComptimeGenericParameterTypeIsInteger" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsInteger,
            "TypeComptimeGenericParameterTypeIsFloat" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsFloat,
            "TypeComptimeGenericParameterTypeIsRawPointer" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsRawPointer,
            "TypeComptimeGenericParameterTypeIsFixedArray" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsFixedArray,
            "TypeComptimeGenericParameterTypeIsSlice" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsSlice,
            "TypeComptimeGenericParameterTypeIsDynamic" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsDynamic,
            "TypeComptimeGenericParameterTypeIsFunctionPointer" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsFunctionPointer,
            "TypeComptimeGenericParameterTypeIsClosure" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsClosure,
            "TypeComptimeGenericParameterTypeIsDynTrait" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsDynTrait,
            "TypeComptimeGenericParameterTypeIsNamed" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsNamed,
            "TypeComptimeGenericParameterTypeIsStruct" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsStruct,
            "TypeComptimeGenericParameterTypeIsRecord" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsRecord,
            "TypeComptimeGenericParameterTypeIsEnum" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsEnum,
            "TypeComptimeGenericParameterTypeIsTrait" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsTrait,
            "TypeComptimeGenericParameterTypeIsDoctrine" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsDoctrine,
            "TypeComptimeGenericParameterTypeHasConcreteLayout" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeHasConcreteLayout,
            "TypeComptimeGenericParameterTypeDisplayName" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeDisplayName,
            "TypeComptimeGenericParameterTypeBaseName" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeBaseName,
            "TypeComptimeGenericParameterTypeModuleName" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeModuleName,
            "TypeComptimeGenericParameterTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsGenericInstantiation,
            "TypeComptimeGenericParameterTypeArgumentCount" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeArgumentCount,
            "TypeComptimeGenericParameterTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeComptimeArgumentCount,
            "TypeDisplayName" => CompileTimeStructuralFactKind.TypeDisplayName,
            "TypeBaseName" => CompileTimeStructuralFactKind.TypeBaseName,
            "TypeModuleName" => CompileTimeStructuralFactKind.TypeModuleName,
            "TypeVisibilityIsModule" => CompileTimeStructuralFactKind.TypeVisibilityIsModule,
            "TypeVisibilityIsInternal" => CompileTimeStructuralFactKind.TypeVisibilityIsInternal,
            "TypeVisibilityIsPublic" => CompileTimeStructuralFactKind.TypeVisibilityIsPublic,
            "TypeVisibilityIsExport" => CompileTimeStructuralFactKind.TypeVisibilityIsExport,
            "TypeHasCSourceAlias" => CompileTimeStructuralFactKind.TypeHasCSourceAlias,
            "TypeCSourceAliasName" => CompileTimeStructuralFactKind.TypeCSourceAliasName,
            "TypeIsGenericInstantiation" => CompileTimeStructuralFactKind.TypeIsGenericInstantiation,
            "TypeArgumentCount" => CompileTimeStructuralFactKind.TypeArgumentCount,
            "TypeArgumentTypeIs" => CompileTimeStructuralFactKind.TypeArgumentTypeIs,
            "TypeArgumentTypeIsBool" => CompileTimeStructuralFactKind.TypeArgumentTypeIsBool,
            "TypeArgumentTypeIsInteger" => CompileTimeStructuralFactKind.TypeArgumentTypeIsInteger,
            "TypeArgumentTypeIsFloat" => CompileTimeStructuralFactKind.TypeArgumentTypeIsFloat,
            "TypeArgumentTypeIsRawPointer" => CompileTimeStructuralFactKind.TypeArgumentTypeIsRawPointer,
            "TypeArgumentTypeIsFixedArray" => CompileTimeStructuralFactKind.TypeArgumentTypeIsFixedArray,
            "TypeArgumentTypeIsSlice" => CompileTimeStructuralFactKind.TypeArgumentTypeIsSlice,
            "TypeArgumentTypeIsDynamic" => CompileTimeStructuralFactKind.TypeArgumentTypeIsDynamic,
            "TypeArgumentTypeIsFunctionPointer" => CompileTimeStructuralFactKind.TypeArgumentTypeIsFunctionPointer,
            "TypeArgumentTypeIsClosure" => CompileTimeStructuralFactKind.TypeArgumentTypeIsClosure,
            "TypeArgumentTypeIsDynTrait" => CompileTimeStructuralFactKind.TypeArgumentTypeIsDynTrait,
            "TypeArgumentTypeIsNamed" => CompileTimeStructuralFactKind.TypeArgumentTypeIsNamed,
            "TypeArgumentTypeIsStruct" => CompileTimeStructuralFactKind.TypeArgumentTypeIsStruct,
            "TypeArgumentTypeIsRecord" => CompileTimeStructuralFactKind.TypeArgumentTypeIsRecord,
            "TypeArgumentTypeIsEnum" => CompileTimeStructuralFactKind.TypeArgumentTypeIsEnum,
            "TypeArgumentTypeIsTrait" => CompileTimeStructuralFactKind.TypeArgumentTypeIsTrait,
            "TypeArgumentTypeIsDoctrine" => CompileTimeStructuralFactKind.TypeArgumentTypeIsDoctrine,
            "TypeArgumentTypeHasConcreteLayout" => CompileTimeStructuralFactKind.TypeArgumentTypeHasConcreteLayout,
            "TypeArgumentTypeDisplayName" => CompileTimeStructuralFactKind.TypeArgumentTypeDisplayName,
            "TypeArgumentTypeBaseName" => CompileTimeStructuralFactKind.TypeArgumentTypeBaseName,
            "TypeArgumentTypeModuleName" => CompileTimeStructuralFactKind.TypeArgumentTypeModuleName,
            "TypeArgumentTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.TypeArgumentTypeIsGenericInstantiation,
            "TypeArgumentTypeArgumentCount" => CompileTimeStructuralFactKind.TypeArgumentTypeArgumentCount,
            "TypeArgumentTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.TypeArgumentTypeComptimeArgumentCount,
            "TypeComptimeArgumentCount" => CompileTimeStructuralFactKind.TypeComptimeArgumentCount,
            "TypeComptimeArgumentName" => CompileTimeStructuralFactKind.TypeComptimeArgumentName,
            "TypeComptimeArgumentTypeIs" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIs,
            "TypeComptimeArgumentTypeIsBool" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsBool,
            "TypeComptimeArgumentTypeIsInteger" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsInteger,
            "TypeComptimeArgumentTypeIsFloat" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsFloat,
            "TypeComptimeArgumentTypeIsRawPointer" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsRawPointer,
            "TypeComptimeArgumentTypeIsFixedArray" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsFixedArray,
            "TypeComptimeArgumentTypeIsSlice" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsSlice,
            "TypeComptimeArgumentTypeIsDynamic" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsDynamic,
            "TypeComptimeArgumentTypeIsFunctionPointer" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsFunctionPointer,
            "TypeComptimeArgumentTypeIsClosure" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsClosure,
            "TypeComptimeArgumentTypeIsDynTrait" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsDynTrait,
            "TypeComptimeArgumentTypeIsNamed" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsNamed,
            "TypeComptimeArgumentTypeIsStruct" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsStruct,
            "TypeComptimeArgumentTypeIsRecord" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsRecord,
            "TypeComptimeArgumentTypeIsEnum" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsEnum,
            "TypeComptimeArgumentTypeIsTrait" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsTrait,
            "TypeComptimeArgumentTypeIsDoctrine" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsDoctrine,
            "TypeComptimeArgumentTypeHasConcreteLayout" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeHasConcreteLayout,
            "TypeComptimeArgumentTypeDisplayName" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeDisplayName,
            "TypeComptimeArgumentTypeBaseName" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeBaseName,
            "TypeComptimeArgumentTypeModuleName" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeModuleName,
            "TypeComptimeArgumentTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsGenericInstantiation,
            "TypeComptimeArgumentTypeArgumentCount" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeArgumentCount,
            "TypeComptimeArgumentTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.TypeComptimeArgumentTypeComptimeArgumentCount,
            "TypeComptimeArgumentValueIs" => CompileTimeStructuralFactKind.TypeComptimeArgumentValueIs,
            "EnumVariantPayloadCount" => CompileTimeStructuralFactKind.EnumVariantPayloadCount,
            "EnumVariantTag" => CompileTimeStructuralFactKind.EnumVariantTag,
            "EnumTagOffset" => CompileTimeStructuralFactKind.EnumTagOffset,
            "EnumTagSize" => CompileTimeStructuralFactKind.EnumTagSize,
            "EnumTagAlign" => CompileTimeStructuralFactKind.EnumTagAlign,
            "EnumTagIsMisaligned" => CompileTimeStructuralFactKind.EnumTagIsMisaligned,
            "EnumVariantPayloadOffset" => CompileTimeStructuralFactKind.EnumVariantPayloadOffset,
            "EnumVariantPayloadSize" => CompileTimeStructuralFactKind.EnumVariantPayloadSize,
            "EnumVariantPayloadAlign" => CompileTimeStructuralFactKind.EnumVariantPayloadAlign,
            "EnumVariantPayloadIsMisaligned" => CompileTimeStructuralFactKind.EnumVariantPayloadIsMisaligned,
            "EnumVariantIsOk" => CompileTimeStructuralFactKind.EnumVariantIsOk,
            "EnumVariantIsErr" => CompileTimeStructuralFactKind.EnumVariantIsErr,
            "EnumVariantIsErrorFunnel" => CompileTimeStructuralFactKind.EnumVariantIsErrorFunnel,
            "Implements" => CompileTimeStructuralFactKind.Implements,
            "ImplementedTraitCount" => CompileTimeStructuralFactKind.ImplementedTraitCount,
            "ImplementedTraitTypeIs" => CompileTimeStructuralFactKind.ImplementedTraitTypeIs,
            "ImplementedTraitTypeIsBool" => CompileTimeStructuralFactKind.ImplementedTraitTypeIsBool,
            "ImplementedTraitTypeIsInteger" => CompileTimeStructuralFactKind.ImplementedTraitTypeIsInteger,
            "ImplementedTraitTypeIsFloat" => CompileTimeStructuralFactKind.ImplementedTraitTypeIsFloat,
            "ImplementedTraitTypeIsRawPointer" => CompileTimeStructuralFactKind.ImplementedTraitTypeIsRawPointer,
            "ImplementedTraitTypeIsFixedArray" => CompileTimeStructuralFactKind.ImplementedTraitTypeIsFixedArray,
            "ImplementedTraitTypeIsSlice" => CompileTimeStructuralFactKind.ImplementedTraitTypeIsSlice,
            "ImplementedTraitTypeIsDynamic" => CompileTimeStructuralFactKind.ImplementedTraitTypeIsDynamic,
            "ImplementedTraitTypeIsFunctionPointer" => CompileTimeStructuralFactKind.ImplementedTraitTypeIsFunctionPointer,
            "ImplementedTraitTypeIsClosure" => CompileTimeStructuralFactKind.ImplementedTraitTypeIsClosure,
            "ImplementedTraitTypeIsDynTrait" => CompileTimeStructuralFactKind.ImplementedTraitTypeIsDynTrait,
            "ImplementedTraitTypeIsNamed" => CompileTimeStructuralFactKind.ImplementedTraitTypeIsNamed,
            "ImplementedTraitTypeIsStruct" => CompileTimeStructuralFactKind.ImplementedTraitTypeIsStruct,
            "ImplementedTraitTypeIsRecord" => CompileTimeStructuralFactKind.ImplementedTraitTypeIsRecord,
            "ImplementedTraitTypeIsEnum" => CompileTimeStructuralFactKind.ImplementedTraitTypeIsEnum,
            "ImplementedTraitTypeIsTrait" => CompileTimeStructuralFactKind.ImplementedTraitTypeIsTrait,
            "ImplementedTraitTypeIsDoctrine" => CompileTimeStructuralFactKind.ImplementedTraitTypeIsDoctrine,
            "ImplementedTraitTypeHasConcreteLayout" => CompileTimeStructuralFactKind.ImplementedTraitTypeHasConcreteLayout,
            "ImplementedTraitTypeDisplayName" => CompileTimeStructuralFactKind.ImplementedTraitTypeDisplayName,
            "ImplementedTraitTypeBaseName" => CompileTimeStructuralFactKind.ImplementedTraitTypeBaseName,
            "ImplementedTraitTypeModuleName" => CompileTimeStructuralFactKind.ImplementedTraitTypeModuleName,
            "ImplementedTraitTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.ImplementedTraitTypeIsGenericInstantiation,
            "ImplementedTraitTypeArgumentCount" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentCount,
            "ImplementedTraitTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentCount,
            "ImplementedTraitTypeArgumentTypeIs" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIs,
            "ImplementedTraitTypeArgumentTypeIsBool" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsBool,
            "ImplementedTraitTypeArgumentTypeIsInteger" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsInteger,
            "ImplementedTraitTypeArgumentTypeIsFloat" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsFloat,
            "ImplementedTraitTypeArgumentTypeIsRawPointer" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsRawPointer,
            "ImplementedTraitTypeArgumentTypeIsFixedArray" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsFixedArray,
            "ImplementedTraitTypeArgumentTypeIsSlice" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsSlice,
            "ImplementedTraitTypeArgumentTypeIsDynamic" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsDynamic,
            "ImplementedTraitTypeArgumentTypeIsFunctionPointer" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsFunctionPointer,
            "ImplementedTraitTypeArgumentTypeIsClosure" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsClosure,
            "ImplementedTraitTypeArgumentTypeIsDynTrait" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsDynTrait,
            "ImplementedTraitTypeArgumentTypeIsNamed" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsNamed,
            "ImplementedTraitTypeArgumentTypeIsStruct" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsStruct,
            "ImplementedTraitTypeArgumentTypeIsRecord" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsRecord,
            "ImplementedTraitTypeArgumentTypeIsEnum" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsEnum,
            "ImplementedTraitTypeArgumentTypeIsTrait" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsTrait,
            "ImplementedTraitTypeArgumentTypeIsDoctrine" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsDoctrine,
            "ImplementedTraitTypeArgumentTypeHasConcreteLayout" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeHasConcreteLayout,
            "ImplementedTraitTypeArgumentTypeDisplayName" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeDisplayName,
            "ImplementedTraitTypeArgumentTypeBaseName" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeBaseName,
            "ImplementedTraitTypeArgumentTypeModuleName" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeModuleName,
            "ImplementedTraitTypeArgumentTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsGenericInstantiation,
            "ImplementedTraitTypeArgumentTypeArgumentCount" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeArgumentCount,
            "ImplementedTraitTypeArgumentTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeComptimeArgumentCount,
            "ImplementedTraitTypeComptimeArgumentName" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentName,
            "ImplementedTraitTypeComptimeArgumentTypeIs" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIs,
            "ImplementedTraitTypeComptimeArgumentTypeIsBool" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsBool,
            "ImplementedTraitTypeComptimeArgumentTypeIsInteger" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsInteger,
            "ImplementedTraitTypeComptimeArgumentTypeIsFloat" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsFloat,
            "ImplementedTraitTypeComptimeArgumentTypeIsRawPointer" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsRawPointer,
            "ImplementedTraitTypeComptimeArgumentTypeIsFixedArray" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsFixedArray,
            "ImplementedTraitTypeComptimeArgumentTypeIsSlice" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsSlice,
            "ImplementedTraitTypeComptimeArgumentTypeIsDynamic" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsDynamic,
            "ImplementedTraitTypeComptimeArgumentTypeIsFunctionPointer" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsFunctionPointer,
            "ImplementedTraitTypeComptimeArgumentTypeIsClosure" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsClosure,
            "ImplementedTraitTypeComptimeArgumentTypeIsDynTrait" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsDynTrait,
            "ImplementedTraitTypeComptimeArgumentTypeIsNamed" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsNamed,
            "ImplementedTraitTypeComptimeArgumentTypeIsStruct" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsStruct,
            "ImplementedTraitTypeComptimeArgumentTypeIsRecord" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsRecord,
            "ImplementedTraitTypeComptimeArgumentTypeIsEnum" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsEnum,
            "ImplementedTraitTypeComptimeArgumentTypeIsTrait" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsTrait,
            "ImplementedTraitTypeComptimeArgumentTypeIsDoctrine" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsDoctrine,
            "ImplementedTraitTypeComptimeArgumentTypeHasConcreteLayout" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeHasConcreteLayout,
            "ImplementedTraitTypeComptimeArgumentTypeDisplayName" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeDisplayName,
            "ImplementedTraitTypeComptimeArgumentTypeBaseName" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeBaseName,
            "ImplementedTraitTypeComptimeArgumentTypeModuleName" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeModuleName,
            "ImplementedTraitTypeComptimeArgumentTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsGenericInstantiation,
            "ImplementedTraitTypeComptimeArgumentTypeArgumentCount" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeArgumentCount,
            "ImplementedTraitTypeComptimeArgumentTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeComptimeArgumentCount,
            "ImplementedTraitTypeComptimeArgumentValueIs" => CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentValueIs,
            "AssociatedTypeCount" => CompileTimeStructuralFactKind.AssociatedTypeCount,
            "AssociatedTypeName" => CompileTimeStructuralFactKind.AssociatedTypeName,
            "AssociatedTypeHasTarget" => CompileTimeStructuralFactKind.AssociatedTypeHasTarget,
            "AssociatedTypeTargetTypeIs" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIs,
            "AssociatedTypeTargetTypeIsBool" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsBool,
            "AssociatedTypeTargetTypeIsInteger" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsInteger,
            "AssociatedTypeTargetTypeIsFloat" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsFloat,
            "AssociatedTypeTargetTypeIsRawPointer" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsRawPointer,
            "AssociatedTypeTargetTypeIsFixedArray" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsFixedArray,
            "AssociatedTypeTargetTypeIsSlice" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsSlice,
            "AssociatedTypeTargetTypeIsDynamic" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsDynamic,
            "AssociatedTypeTargetTypeIsFunctionPointer" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsFunctionPointer,
            "AssociatedTypeTargetTypeIsClosure" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsClosure,
            "AssociatedTypeTargetTypeIsDynTrait" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsDynTrait,
            "AssociatedTypeTargetTypeIsNamed" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsNamed,
            "AssociatedTypeTargetTypeIsStruct" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsStruct,
            "AssociatedTypeTargetTypeIsRecord" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsRecord,
            "AssociatedTypeTargetTypeIsEnum" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsEnum,
            "AssociatedTypeTargetTypeIsTrait" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsTrait,
            "AssociatedTypeTargetTypeIsDoctrine" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsDoctrine,
            "AssociatedTypeTargetTypeHasConcreteLayout" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeHasConcreteLayout,
            "AssociatedTypeTargetTypeDisplayName" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeDisplayName,
            "AssociatedTypeTargetTypeBaseName" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeBaseName,
            "AssociatedTypeTargetTypeModuleName" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeModuleName,
            "AssociatedTypeTargetTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsGenericInstantiation,
            "AssociatedTypeTargetTypeArgumentCount" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeArgumentCount,
            "AssociatedTypeTargetTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.AssociatedTypeTargetTypeComptimeArgumentCount,
            "FieldTypeIs" => CompileTimeStructuralFactKind.FieldTypeIs,
            "EnumVariantPayloadTypeIs" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeIs,
            "EnumVariantAbsorbsErrorTypeIs" => CompileTimeStructuralFactKind.EnumVariantAbsorbsErrorTypeIs,
            "EnumVariantPayloadTypeIsBool" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsBool,
            "EnumVariantPayloadTypeIsInteger" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsInteger,
            "EnumVariantPayloadTypeIsFloat" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsFloat,
            "EnumVariantPayloadTypeIsRawPointer" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsRawPointer,
            "EnumVariantPayloadTypeIsFixedArray" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsFixedArray,
            "EnumVariantPayloadTypeIsSlice" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsSlice,
            "EnumVariantPayloadTypeIsDynamic" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsDynamic,
            "EnumVariantPayloadTypeIsFunctionPointer" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsFunctionPointer,
            "EnumVariantPayloadTypeIsClosure" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsClosure,
            "EnumVariantPayloadTypeIsDynTrait" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsDynTrait,
            "EnumVariantPayloadTypeIsNamed" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsNamed,
            "EnumVariantPayloadTypeIsStruct" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsStruct,
            "EnumVariantPayloadTypeIsRecord" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsRecord,
            "EnumVariantPayloadTypeIsEnum" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsEnum,
            "EnumVariantPayloadTypeIsTrait" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsTrait,
            "EnumVariantPayloadTypeIsDoctrine" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsDoctrine,
            "EnumVariantPayloadTypeHasConcreteLayout" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeHasConcreteLayout,
            "EnumVariantPayloadTypeDisplayName" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeDisplayName,
            "EnumVariantPayloadTypeBaseName" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeBaseName,
            "EnumVariantPayloadTypeModuleName" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeModuleName,
            "EnumVariantPayloadTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsGenericInstantiation,
            "EnumVariantPayloadTypeArgumentCount" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeArgumentCount,
            "EnumVariantPayloadTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeComptimeArgumentCount,
            "EnumVariantPayloadTypeHasCSourceAlias" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeHasCSourceAlias,
            "EnumVariantPayloadTypeCSourceAliasName" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeCSourceAliasName,
            "EnumVariantPayloadTypeHasQualifiers" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeHasQualifiers,
            "EnumVariantPayloadTypeBorrowKindIsNone" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeBorrowKindIsNone,
            "EnumVariantPayloadTypeBorrowKindIsBorrow" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeBorrowKindIsBorrow,
            "EnumVariantPayloadTypeBorrowKindIsRetBorrow" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeBorrowKindIsRetBorrow,
            "EnumVariantPayloadTypeBorrowKindIsStoreBorrow" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeBorrowKindIsStoreBorrow,
            "EnumVariantPayloadTypeAccessKindIsNone" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeAccessKindIsNone,
            "EnumVariantPayloadTypeAccessKindIsShared" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeAccessKindIsShared,
            "EnumVariantPayloadTypeAccessKindIsFrozen" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeAccessKindIsFrozen,
            "EnumVariantPayloadTypeInitializationKindIsNone" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeInitializationKindIsNone,
            "EnumVariantPayloadTypeInitializationKindIsOut" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeInitializationKindIsOut,
            "EnumVariantPayloadTypeInitializationKindIsInit" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeInitializationKindIsInit,
            "EnumVariantPayloadTypeIsMutableView" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsMutableView,
            "EnumVariantPayloadTypeUnqualifiedTypeIs" => CompileTimeStructuralFactKind.EnumVariantPayloadTypeUnqualifiedTypeIs,
            "FieldName" => CompileTimeStructuralFactKind.FieldName,
            "FieldVisibilityIsModule" => CompileTimeStructuralFactKind.FieldVisibilityIsModule,
            "FieldVisibilityIsInternal" => CompileTimeStructuralFactKind.FieldVisibilityIsInternal,
            "FieldVisibilityIsPublic" => CompileTimeStructuralFactKind.FieldVisibilityIsPublic,
            "FieldVisibilityIsExport" => CompileTimeStructuralFactKind.FieldVisibilityIsExport,
            "EnumVariantName" => CompileTimeStructuralFactKind.EnumVariantName,
            "EnumVariantUsesNamedFields" => CompileTimeStructuralFactKind.EnumVariantUsesNamedFields,
            "EnumVariantPayloadHasName" => CompileTimeStructuralFactKind.EnumVariantPayloadHasName,
            "EnumVariantPayloadName" => CompileTimeStructuralFactKind.EnumVariantPayloadName,
            "TypeThreadSafetyLawAttributeCount" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeCount,
            "TypeThreadSafetyLawAttributeLawName" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeLawName,
            "TypeThreadSafetyLawAttributeIsGrant" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeIsGrant,
            "TypeThreadSafetyLawAttributeIsDeny" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeIsDeny,
            "TypeThreadSafetyLawAttributeHasCondition" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeHasCondition,
            "TypeThreadSafetyLawAttributeConditionLawName" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionLawName,
            "TypeThreadSafetyLawAttributeConditionTypeIs" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIs,
            "TypeThreadSafetyLawAttributeConditionTypeIsBool" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsBool,
            "TypeThreadSafetyLawAttributeConditionTypeIsInteger" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsInteger,
            "TypeThreadSafetyLawAttributeConditionTypeIsFloat" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsFloat,
            "TypeThreadSafetyLawAttributeConditionTypeIsRawPointer" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsRawPointer,
            "TypeThreadSafetyLawAttributeConditionTypeIsFixedArray" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsFixedArray,
            "TypeThreadSafetyLawAttributeConditionTypeIsSlice" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsSlice,
            "TypeThreadSafetyLawAttributeConditionTypeIsDynamic" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsDynamic,
            "TypeThreadSafetyLawAttributeConditionTypeIsFunctionPointer" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsFunctionPointer,
            "TypeThreadSafetyLawAttributeConditionTypeIsClosure" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsClosure,
            "TypeThreadSafetyLawAttributeConditionTypeIsDynTrait" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsDynTrait,
            "TypeThreadSafetyLawAttributeConditionTypeIsNamed" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsNamed,
            "TypeThreadSafetyLawAttributeConditionTypeIsStruct" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsStruct,
            "TypeThreadSafetyLawAttributeConditionTypeIsRecord" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsRecord,
            "TypeThreadSafetyLawAttributeConditionTypeIsEnum" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsEnum,
            "TypeThreadSafetyLawAttributeConditionTypeIsTrait" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsTrait,
            "TypeThreadSafetyLawAttributeConditionTypeIsDoctrine" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsDoctrine,
            "TypeThreadSafetyLawAttributeConditionTypeHasConcreteLayout" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeHasConcreteLayout,
            "TypeThreadSafetyLawAttributeConditionTypeDisplayName" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeDisplayName,
            "TypeThreadSafetyLawAttributeConditionTypeBaseName" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeBaseName,
            "TypeThreadSafetyLawAttributeConditionTypeModuleName" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeModuleName,
            "TypeThreadSafetyLawAttributeConditionTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsGenericInstantiation,
            "TypeThreadSafetyLawAttributeConditionTypeArgumentCount" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeArgumentCount,
            "TypeThreadSafetyLawAttributeConditionTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeComptimeArgumentCount,
            "FieldThreadSafetyLawAttributeCount" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeCount,
            "FieldThreadSafetyLawAttributeLawName" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeLawName,
            "FieldThreadSafetyLawAttributeIsGrant" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeIsGrant,
            "FieldThreadSafetyLawAttributeIsDeny" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeIsDeny,
            "FieldThreadSafetyLawAttributeHasCondition" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeHasCondition,
            "FieldThreadSafetyLawAttributeConditionLawName" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionLawName,
            "FieldThreadSafetyLawAttributeConditionTypeIs" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIs,
            "FieldThreadSafetyLawAttributeConditionTypeIsBool" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsBool,
            "FieldThreadSafetyLawAttributeConditionTypeIsInteger" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsInteger,
            "FieldThreadSafetyLawAttributeConditionTypeIsFloat" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsFloat,
            "FieldThreadSafetyLawAttributeConditionTypeIsRawPointer" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsRawPointer,
            "FieldThreadSafetyLawAttributeConditionTypeIsFixedArray" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsFixedArray,
            "FieldThreadSafetyLawAttributeConditionTypeIsSlice" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsSlice,
            "FieldThreadSafetyLawAttributeConditionTypeIsDynamic" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsDynamic,
            "FieldThreadSafetyLawAttributeConditionTypeIsFunctionPointer" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsFunctionPointer,
            "FieldThreadSafetyLawAttributeConditionTypeIsClosure" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsClosure,
            "FieldThreadSafetyLawAttributeConditionTypeIsDynTrait" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsDynTrait,
            "FieldThreadSafetyLawAttributeConditionTypeIsNamed" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsNamed,
            "FieldThreadSafetyLawAttributeConditionTypeIsStruct" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsStruct,
            "FieldThreadSafetyLawAttributeConditionTypeIsRecord" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsRecord,
            "FieldThreadSafetyLawAttributeConditionTypeIsEnum" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsEnum,
            "FieldThreadSafetyLawAttributeConditionTypeIsTrait" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsTrait,
            "FieldThreadSafetyLawAttributeConditionTypeIsDoctrine" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsDoctrine,
            "FieldThreadSafetyLawAttributeConditionTypeHasConcreteLayout" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeHasConcreteLayout,
            "FieldThreadSafetyLawAttributeConditionTypeDisplayName" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeDisplayName,
            "FieldThreadSafetyLawAttributeConditionTypeBaseName" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeBaseName,
            "FieldThreadSafetyLawAttributeConditionTypeModuleName" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeModuleName,
            "FieldThreadSafetyLawAttributeConditionTypeIsGenericInstantiation" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsGenericInstantiation,
            "FieldThreadSafetyLawAttributeConditionTypeArgumentCount" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeArgumentCount,
            "FieldThreadSafetyLawAttributeConditionTypeComptimeArgumentCount" => CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeComptimeArgumentCount,
            _ => default
        };
        return kind != default
            || string.Equals(localName, "IsBool", StringComparison.Ordinal)
            || localName is
            "IsBool"
            or "IsInteger"
            or "IsFloat"
            or "IsRawPointer"
            or "IsFixedArray"
            or "IsSlice"
            or "IsDynamic"
            or "IsFunctionPointer"
            or "IsClosure"
            or "IsDynTrait"
            or "IsNamed"
            or "IsStruct"
            or "IsRecord"
            or "IsEnum"
            or "IsTrait"
            or "IsDoctrine"
            or "HasConcreteLayout"
            or "TypeSize"
            or "TypeAlign"
            or "TypeIsZeroSized"
            or "TypeIntegerBitWidth"
            or "TypeFloatBitWidth"
            or "TypeIntegerIsSigned"
            or "TypeIntegerIsUnsigned"
            or "TypeIntegerIsFullRange"
            or "TypeIntegerMinIs"
            or "TypeIntegerMaxIs"
            or "RawPointerElementTypeIs"
            or "RawPointerElementTypeIsBool"
            or "RawPointerElementTypeIsInteger"
            or "RawPointerElementTypeIsFloat"
            or "RawPointerElementTypeIsRawPointer"
            or "RawPointerElementTypeIsFixedArray"
            or "RawPointerElementTypeIsSlice"
            or "RawPointerElementTypeIsDynamic"
            or "RawPointerElementTypeIsFunctionPointer"
            or "RawPointerElementTypeIsClosure"
            or "RawPointerElementTypeIsNamed"
            or "RawPointerElementTypeIsStruct"
            or "RawPointerElementTypeIsRecord"
            or "RawPointerElementTypeIsEnum"
            or "RawPointerElementTypeIsTrait"
            or "RawPointerElementTypeIsDoctrine"
            or "RawPointerElementTypeHasConcreteLayout"
            or "RawPointerElementTypeHasCSourceAlias"
            or "RawPointerElementTypeCSourceAliasName"
            or "RawPointerIsMutable"
            or "RawPointerIsReadOnly"
            or "TypeElementTypeIs"
            or "TypeElementTypeIsBool"
            or "TypeElementTypeIsInteger"
            or "TypeElementTypeIsFloat"
            or "TypeElementTypeIsRawPointer"
            or "TypeElementTypeIsFixedArray"
            or "TypeElementTypeIsSlice"
            or "TypeElementTypeIsDynamic"
            or "TypeElementTypeIsFunctionPointer"
            or "TypeElementTypeIsClosure"
            or "TypeElementTypeIsNamed"
            or "TypeElementTypeIsStruct"
            or "TypeElementTypeIsRecord"
            or "TypeElementTypeIsEnum"
            or "TypeElementTypeIsTrait"
            or "TypeElementTypeIsDoctrine"
            or "TypeElementTypeHasConcreteLayout"
            or "TypeElementTypeHasCSourceAlias"
            or "TypeElementTypeCSourceAliasName"
            or "TypeFixedArrayLength"
            or "TypeFixedArrayLengthIs"
            or "TypeHasQualifiers"
            or "TypeBorrowKindIsNone"
            or "TypeBorrowKindIsBorrow"
            or "TypeBorrowKindIsRetBorrow"
            or "TypeBorrowKindIsStoreBorrow"
            or "TypeAccessKindIsNone"
            or "TypeAccessKindIsShared"
            or "TypeAccessKindIsFrozen"
            or "TypeInitializationKindIsNone"
            or "TypeInitializationKindIsOut"
            or "TypeInitializationKindIsInit"
            or "TypeIsMutableView"
            or "TypeUnqualifiedTypeIs"
            or "FunctionPointerParameterCount"
            or "FunctionPointerReturnTypeIs"
            or "FunctionPointerParameterTypeIs"
            or "FunctionPointerReturnTypeIsBool"
            or "FunctionPointerReturnTypeIsInteger"
            or "FunctionPointerReturnTypeIsFloat"
            or "FunctionPointerReturnTypeIsRawPointer"
            or "FunctionPointerReturnTypeIsFixedArray"
            or "FunctionPointerReturnTypeIsSlice"
            or "FunctionPointerReturnTypeIsDynamic"
            or "FunctionPointerReturnTypeIsFunctionPointer"
            or "FunctionPointerReturnTypeIsClosure"
            or "FunctionPointerReturnTypeIsNamed"
            or "FunctionPointerReturnTypeIsStruct"
            or "FunctionPointerReturnTypeIsRecord"
            or "FunctionPointerReturnTypeIsEnum"
            or "FunctionPointerReturnTypeIsTrait"
            or "FunctionPointerReturnTypeIsDoctrine"
            or "FunctionPointerReturnTypeHasConcreteLayout"
            or "FunctionPointerParameterTypeIsBool"
            or "FunctionPointerParameterTypeIsInteger"
            or "FunctionPointerParameterTypeIsFloat"
            or "FunctionPointerParameterTypeIsRawPointer"
            or "FunctionPointerParameterTypeIsFixedArray"
            or "FunctionPointerParameterTypeIsSlice"
            or "FunctionPointerParameterTypeIsDynamic"
            or "FunctionPointerParameterTypeIsFunctionPointer"
            or "FunctionPointerParameterTypeIsClosure"
            or "FunctionPointerParameterTypeIsNamed"
            or "FunctionPointerParameterTypeIsStruct"
            or "FunctionPointerParameterTypeIsRecord"
            or "FunctionPointerParameterTypeIsEnum"
            or "FunctionPointerParameterTypeIsTrait"
            or "FunctionPointerParameterTypeIsDoctrine"
            or "FunctionPointerParameterTypeHasConcreteLayout"
            or "FunctionPointerReturnTypeDisplayName"
            or "FunctionPointerReturnTypeBaseName"
            or "FunctionPointerReturnTypeModuleName"
            or "FunctionPointerReturnTypeIsGenericInstantiation"
            or "FunctionPointerReturnTypeArgumentCount"
            or "FunctionPointerReturnTypeComptimeArgumentCount"
            or "FunctionPointerReturnTypeHasCSourceAlias"
            or "FunctionPointerReturnTypeCSourceAliasName"
            or "FunctionPointerReturnTypeHasQualifiers"
            or "FunctionPointerReturnTypeBorrowKindIsNone"
            or "FunctionPointerReturnTypeBorrowKindIsBorrow"
            or "FunctionPointerReturnTypeBorrowKindIsRetBorrow"
            or "FunctionPointerReturnTypeBorrowKindIsStoreBorrow"
            or "FunctionPointerReturnTypeAccessKindIsNone"
            or "FunctionPointerReturnTypeAccessKindIsShared"
            or "FunctionPointerReturnTypeAccessKindIsFrozen"
            or "FunctionPointerReturnTypeInitializationKindIsNone"
            or "FunctionPointerReturnTypeInitializationKindIsOut"
            or "FunctionPointerReturnTypeInitializationKindIsInit"
            or "FunctionPointerReturnTypeIsMutableView"
            or "FunctionPointerReturnTypeUnqualifiedTypeIs"
            or "FunctionPointerParameterTypeDisplayName"
            or "FunctionPointerParameterTypeBaseName"
            or "FunctionPointerParameterTypeModuleName"
            or "FunctionPointerParameterTypeIsGenericInstantiation"
            or "FunctionPointerParameterTypeArgumentCount"
            or "FunctionPointerParameterTypeComptimeArgumentCount"
            or "FunctionPointerParameterTypeHasCSourceAlias"
            or "FunctionPointerParameterTypeCSourceAliasName"
            or "FunctionPointerParameterTypeHasQualifiers"
            or "FunctionPointerParameterTypeBorrowKindIsNone"
            or "FunctionPointerParameterTypeBorrowKindIsBorrow"
            or "FunctionPointerParameterTypeBorrowKindIsRetBorrow"
            or "FunctionPointerParameterTypeBorrowKindIsStoreBorrow"
            or "FunctionPointerParameterTypeAccessKindIsNone"
            or "FunctionPointerParameterTypeAccessKindIsShared"
            or "FunctionPointerParameterTypeAccessKindIsFrozen"
            or "FunctionPointerParameterTypeInitializationKindIsNone"
            or "FunctionPointerParameterTypeInitializationKindIsOut"
            or "FunctionPointerParameterTypeInitializationKindIsInit"
            or "FunctionPointerParameterTypeIsMutableView"
            or "FunctionPointerParameterTypeUnqualifiedTypeIs"
            or "FunctionPointerParameterHasRawPointerElementCountExpression"
            or "FunctionPointerParameterRawPointerElementCountExpression"
            or "FunctionPointerKindIsFn"
            or "FunctionPointerKindIsFinite"
            or "FunctionPointerKindIsLaw"
            or "FunctionPointerKindIsFiniteLaw"
            or "FunctionPointerIsUnsafe"
            or "FunctionPointerHasFfiAbi"
            or "FunctionPointerAbiIsC"
            or "FunctionPointerAbiIsCDecl"
            or "FunctionPointerAbiIsStdCall"
            or "FunctionPointerAbiIsFastCall"
            or "FunctionPointerAbiIsThisCall"
            or "FunctionPointerAbiIsVectorCall"
            or "FunctionPointerAbiIsSysV"
            or "FunctionPointerAbiIsWin64"
            or "FunctionPointerAbiIsAapcs"
            or "FunctionPointerAbiIsAapcs64"
            or "FunctionPointerParametersAreDisjoint"
            or "FunctionPointerParametersOverlap"
            or "FunctionPointerParametersAreSame"
            or "ClosureParameterCount"
            or "ClosureReturnTypeIs"
            or "ClosureParameterTypeIs"
            or "ClosureReturnTypeIsBool"
            or "ClosureReturnTypeIsInteger"
            or "ClosureReturnTypeIsFloat"
            or "ClosureReturnTypeIsRawPointer"
            or "ClosureReturnTypeIsFixedArray"
            or "ClosureReturnTypeIsSlice"
            or "ClosureReturnTypeIsDynamic"
            or "ClosureReturnTypeIsFunctionPointer"
            or "ClosureReturnTypeIsClosure"
            or "ClosureReturnTypeIsNamed"
            or "ClosureReturnTypeIsStruct"
            or "ClosureReturnTypeIsRecord"
            or "ClosureReturnTypeIsEnum"
            or "ClosureReturnTypeIsTrait"
            or "ClosureReturnTypeIsDoctrine"
            or "ClosureReturnTypeHasConcreteLayout"
            or "ClosureParameterTypeIsBool"
            or "ClosureParameterTypeIsInteger"
            or "ClosureParameterTypeIsFloat"
            or "ClosureParameterTypeIsRawPointer"
            or "ClosureParameterTypeIsFixedArray"
            or "ClosureParameterTypeIsSlice"
            or "ClosureParameterTypeIsDynamic"
            or "ClosureParameterTypeIsFunctionPointer"
            or "ClosureParameterTypeIsClosure"
            or "ClosureParameterTypeIsNamed"
            or "ClosureParameterTypeIsStruct"
            or "ClosureParameterTypeIsRecord"
            or "ClosureParameterTypeIsEnum"
            or "ClosureParameterTypeIsTrait"
            or "ClosureParameterTypeIsDoctrine"
            or "ClosureParameterTypeHasConcreteLayout"
            or "ClosureReturnTypeDisplayName"
            or "ClosureReturnTypeBaseName"
            or "ClosureReturnTypeModuleName"
            or "ClosureReturnTypeIsGenericInstantiation"
            or "ClosureReturnTypeArgumentCount"
            or "ClosureReturnTypeComptimeArgumentCount"
            or "ClosureReturnTypeHasCSourceAlias"
            or "ClosureReturnTypeCSourceAliasName"
            or "ClosureReturnTypeHasQualifiers"
            or "ClosureReturnTypeBorrowKindIsNone"
            or "ClosureReturnTypeBorrowKindIsBorrow"
            or "ClosureReturnTypeBorrowKindIsRetBorrow"
            or "ClosureReturnTypeBorrowKindIsStoreBorrow"
            or "ClosureReturnTypeAccessKindIsNone"
            or "ClosureReturnTypeAccessKindIsShared"
            or "ClosureReturnTypeAccessKindIsFrozen"
            or "ClosureReturnTypeInitializationKindIsNone"
            or "ClosureReturnTypeInitializationKindIsOut"
            or "ClosureReturnTypeInitializationKindIsInit"
            or "ClosureReturnTypeIsMutableView"
            or "ClosureReturnTypeUnqualifiedTypeIs"
            or "ClosureParameterTypeDisplayName"
            or "ClosureParameterTypeBaseName"
            or "ClosureParameterTypeModuleName"
            or "ClosureParameterTypeIsGenericInstantiation"
            or "ClosureParameterTypeArgumentCount"
            or "ClosureParameterTypeComptimeArgumentCount"
            or "ClosureParameterTypeHasCSourceAlias"
            or "ClosureParameterTypeCSourceAliasName"
            or "ClosureParameterTypeHasQualifiers"
            or "ClosureParameterTypeBorrowKindIsNone"
            or "ClosureParameterTypeBorrowKindIsBorrow"
            or "ClosureParameterTypeBorrowKindIsRetBorrow"
            or "ClosureParameterTypeBorrowKindIsStoreBorrow"
            or "ClosureParameterTypeAccessKindIsNone"
            or "ClosureParameterTypeAccessKindIsShared"
            or "ClosureParameterTypeAccessKindIsFrozen"
            or "ClosureParameterTypeInitializationKindIsNone"
            or "ClosureParameterTypeInitializationKindIsOut"
            or "ClosureParameterTypeInitializationKindIsInit"
            or "ClosureParameterTypeIsMutableView"
            or "ClosureParameterTypeUnqualifiedTypeIs"
            or "ClosureParameterHasRawPointerElementCountExpression"
            or "ClosureParameterRawPointerElementCountExpression"
            or "ClosureKindIsFn"
            or "ClosureKindIsFinite"
            or "ClosureKindIsLaw"
            or "ClosureKindIsFiniteLaw"
            or "ClosureStorageIsBorrow"
            or "ClosureStorageIsHeap"
            or "ClosureStorageIsInline"
            or "ClosureCallCapabilityIsNormal"
            or "ClosureCallCapabilityIsMut"
            or "ClosureCallCapabilityIsOnce"
            or "ClosureParametersAreDisjoint"
            or "ClosureParametersOverlap"
            or "ClosureParametersAreSame"
            or "DynTraitIsView"
            or "DynTraitIsHeap"
            or "DynTraitTargetTypeIs"
            or "MethodCount"
            or "MethodName"
            or "MethodModuleName"
            or "MethodVisibilityIsModule"
            or "MethodVisibilityIsInternal"
            or "MethodVisibilityIsPublic"
            or "MethodVisibilityIsExport"
            or "MethodParameterCount"
            or "MethodParameterName"
            or "MethodReturnTypeIs"
            or "MethodParameterTypeIs"
            or "MethodReturnTypeIsBool"
            or "MethodReturnTypeIsInteger"
            or "MethodReturnTypeIsFloat"
            or "MethodReturnTypeIsRawPointer"
            or "MethodReturnTypeIsFixedArray"
            or "MethodReturnTypeIsSlice"
            or "MethodReturnTypeIsDynamic"
            or "MethodReturnTypeIsFunctionPointer"
            or "MethodReturnTypeIsClosure"
            or "MethodReturnTypeIsNamed"
            or "MethodReturnTypeIsStruct"
            or "MethodReturnTypeIsRecord"
            or "MethodReturnTypeIsEnum"
            or "MethodReturnTypeIsTrait"
            or "MethodReturnTypeIsDoctrine"
            or "MethodReturnTypeHasConcreteLayout"
            or "MethodParameterTypeIsBool"
            or "MethodParameterTypeIsInteger"
            or "MethodParameterTypeIsFloat"
            or "MethodParameterTypeIsRawPointer"
            or "MethodParameterTypeIsFixedArray"
            or "MethodParameterTypeIsSlice"
            or "MethodParameterTypeIsDynamic"
            or "MethodParameterTypeIsFunctionPointer"
            or "MethodParameterTypeIsClosure"
            or "MethodParameterTypeIsNamed"
            or "MethodParameterTypeIsStruct"
            or "MethodParameterTypeIsRecord"
            or "MethodParameterTypeIsEnum"
            or "MethodParameterTypeIsTrait"
            or "MethodParameterTypeIsDoctrine"
            or "MethodParameterTypeHasConcreteLayout"
            or "MethodKindIsFn"
            or "MethodKindIsFinite"
            or "MethodKindIsLaw"
            or "MethodKindIsFiniteLaw"
            or "MethodIsStatic"
            or "MethodHasBody"
            or "MethodIsUnsafe"
            or "MethodIsVarargs"
            or "MethodHasFfiAbi"
            or "MethodAbiIsC"
            or "MethodAbiIsCDecl"
            or "MethodAbiIsStdCall"
            or "MethodAbiIsFastCall"
            or "MethodAbiIsThisCall"
            or "MethodAbiIsVectorCall"
            or "MethodAbiIsSysV"
            or "MethodAbiIsWin64"
            or "MethodAbiIsAapcs"
            or "MethodAbiIsAapcs64"
            or "MethodParametersAreDisjoint"
            or "MethodParametersOverlap"
            or "MethodParametersAreSame"
            or "MethodGenericParameterCount"
            or "MethodGenericParameterName"
            or "MethodGenericParameterTraitBoundCount"
            or "MethodGenericParameterTraitBoundTypeIs"
            or "MethodGenericParameterTraitBoundTypeIsBool"
            or "MethodGenericParameterTraitBoundTypeIsInteger"
            or "MethodGenericParameterTraitBoundTypeIsFloat"
            or "MethodGenericParameterTraitBoundTypeIsRawPointer"
            or "MethodGenericParameterTraitBoundTypeIsFixedArray"
            or "MethodGenericParameterTraitBoundTypeIsSlice"
            or "MethodGenericParameterTraitBoundTypeIsDynamic"
            or "MethodGenericParameterTraitBoundTypeIsFunctionPointer"
            or "MethodGenericParameterTraitBoundTypeIsClosure"
            or "MethodGenericParameterTraitBoundTypeIsNamed"
            or "MethodGenericParameterTraitBoundTypeIsStruct"
            or "MethodGenericParameterTraitBoundTypeIsRecord"
            or "MethodGenericParameterTraitBoundTypeIsEnum"
            or "MethodGenericParameterTraitBoundTypeIsTrait"
            or "MethodGenericParameterTraitBoundTypeIsDoctrine"
            or "MethodGenericParameterTraitBoundTypeHasConcreteLayout"
            or "MethodGenericParameterTraitBoundTypeDisplayName"
            or "MethodGenericParameterTraitBoundTypeBaseName"
            or "MethodGenericParameterTraitBoundTypeModuleName"
            or "MethodGenericParameterTraitBoundTypeIsGenericInstantiation"
            or "MethodGenericParameterTraitBoundTypeArgumentCount"
            or "MethodGenericParameterTraitBoundTypeComptimeArgumentCount"
            or "MethodComptimeGenericParameterCount"
            or "MethodComptimeGenericParameterName"
            or "MethodComptimeGenericParameterTypeIs"
            or "MethodComptimeGenericParameterTypeIsBool"
            or "MethodComptimeGenericParameterTypeIsInteger"
            or "MethodComptimeGenericParameterTypeIsFloat"
            or "MethodComptimeGenericParameterTypeIsRawPointer"
            or "MethodComptimeGenericParameterTypeIsFixedArray"
            or "MethodComptimeGenericParameterTypeIsSlice"
            or "MethodComptimeGenericParameterTypeIsDynamic"
            or "MethodComptimeGenericParameterTypeIsFunctionPointer"
            or "MethodComptimeGenericParameterTypeIsClosure"
            or "MethodComptimeGenericParameterTypeIsNamed"
            or "MethodComptimeGenericParameterTypeIsStruct"
            or "MethodComptimeGenericParameterTypeIsRecord"
            or "MethodComptimeGenericParameterTypeIsEnum"
            or "MethodComptimeGenericParameterTypeIsTrait"
            or "MethodComptimeGenericParameterTypeIsDoctrine"
            or "MethodComptimeGenericParameterTypeHasConcreteLayout"
            or "MethodComptimeGenericParameterTypeDisplayName"
            or "MethodComptimeGenericParameterTypeBaseName"
            or "MethodComptimeGenericParameterTypeModuleName"
            or "MethodComptimeGenericParameterTypeIsGenericInstantiation"
            or "MethodComptimeGenericParameterTypeArgumentCount"
            or "MethodComptimeGenericParameterTypeComptimeArgumentCount"
            or "MethodThreadSafetyLawPredicateCount"
            or "MethodThreadSafetyLawPredicateLawName"
            or "MethodThreadSafetyLawPredicateTypeIs"
            or "MethodThreadSafetyLawPredicateTypeIsBool"
            or "MethodThreadSafetyLawPredicateTypeIsInteger"
            or "MethodThreadSafetyLawPredicateTypeIsFloat"
            or "MethodThreadSafetyLawPredicateTypeIsRawPointer"
            or "MethodThreadSafetyLawPredicateTypeIsFixedArray"
            or "MethodThreadSafetyLawPredicateTypeIsSlice"
            or "MethodThreadSafetyLawPredicateTypeIsDynamic"
            or "MethodThreadSafetyLawPredicateTypeIsFunctionPointer"
            or "MethodThreadSafetyLawPredicateTypeIsClosure"
            or "MethodThreadSafetyLawPredicateTypeIsNamed"
            or "MethodThreadSafetyLawPredicateTypeIsStruct"
            or "MethodThreadSafetyLawPredicateTypeIsRecord"
            or "MethodThreadSafetyLawPredicateTypeIsEnum"
            or "MethodThreadSafetyLawPredicateTypeIsTrait"
            or "MethodThreadSafetyLawPredicateTypeIsDoctrine"
            or "MethodThreadSafetyLawPredicateTypeHasConcreteLayout"
            or "MethodThreadSafetyLawPredicateTypeDisplayName"
            or "MethodThreadSafetyLawPredicateTypeBaseName"
            or "MethodThreadSafetyLawPredicateTypeModuleName"
            or "MethodThreadSafetyLawPredicateTypeIsGenericInstantiation"
            or "MethodThreadSafetyLawPredicateTypeArgumentCount"
            or "MethodThreadSafetyLawPredicateTypeComptimeArgumentCount"
            or "MethodReturnTypeDisplayName"
            or "MethodReturnTypeBaseName"
            or "MethodReturnTypeModuleName"
            or "MethodReturnTypeIsGenericInstantiation"
            or "MethodReturnTypeArgumentCount"
            or "MethodReturnTypeComptimeArgumentCount"
            or "MethodReturnTypeHasCSourceAlias"
            or "MethodReturnTypeCSourceAliasName"
            or "MethodReturnTypeHasQualifiers"
            or "MethodReturnTypeBorrowKindIsNone"
            or "MethodReturnTypeBorrowKindIsBorrow"
            or "MethodReturnTypeBorrowKindIsRetBorrow"
            or "MethodReturnTypeBorrowKindIsStoreBorrow"
            or "MethodReturnTypeAccessKindIsNone"
            or "MethodReturnTypeAccessKindIsShared"
            or "MethodReturnTypeAccessKindIsFrozen"
            or "MethodReturnTypeInitializationKindIsNone"
            or "MethodReturnTypeInitializationKindIsOut"
            or "MethodReturnTypeInitializationKindIsInit"
            or "MethodReturnTypeIsMutableView"
            or "MethodReturnTypeUnqualifiedTypeIs"
            or "MethodParameterTypeDisplayName"
            or "MethodParameterTypeBaseName"
            or "MethodParameterTypeModuleName"
            or "MethodParameterTypeIsGenericInstantiation"
            or "MethodParameterTypeArgumentCount"
            or "MethodParameterTypeComptimeArgumentCount"
            or "MethodParameterTypeHasCSourceAlias"
            or "MethodParameterTypeCSourceAliasName"
            or "MethodParameterTypeHasQualifiers"
            or "MethodParameterTypeBorrowKindIsNone"
            or "MethodParameterTypeBorrowKindIsBorrow"
            or "MethodParameterTypeBorrowKindIsRetBorrow"
            or "MethodParameterTypeBorrowKindIsStoreBorrow"
            or "MethodParameterTypeAccessKindIsNone"
            or "MethodParameterTypeAccessKindIsShared"
            or "MethodParameterTypeAccessKindIsFrozen"
            or "MethodParameterTypeInitializationKindIsNone"
            or "MethodParameterTypeInitializationKindIsOut"
            or "MethodParameterTypeInitializationKindIsInit"
            or "MethodParameterTypeIsMutableView"
            or "MethodParameterTypeUnqualifiedTypeIs"
            or "MethodParameterHasRawPointerElementCountExpression"
            or "MethodParameterRawPointerElementCountExpression"
            or "FieldCount"
            or "EnumVariantCount"
            or "FieldOffset"
            or "FieldSize"
            or "FieldAlign"
            or "FieldIsMisaligned"
            or "StructLayoutIsAuto"
            or "StructLayoutIsC"
            or "StructLayoutIsExplicit"
            or "StructHasPack"
            or "StructPack"
            or "StructHasAlign"
            or "StructAlign"
            or "FieldHasExplicitOffset"
            or "FieldExplicitOffset"
            or "FieldTypeIsBool"
            or "FieldTypeIsInteger"
            or "FieldTypeIsFloat"
            or "FieldTypeIsRawPointer"
            or "FieldTypeIsFixedArray"
            or "FieldTypeIsSlice"
            or "FieldTypeIsDynamic"
            or "FieldTypeIsFunctionPointer"
            or "FieldTypeIsClosure"
            or "FieldTypeIsNamed"
            or "FieldTypeIsStruct"
            or "FieldTypeIsRecord"
            or "FieldTypeIsEnum"
            or "FieldTypeIsTrait"
            or "FieldTypeIsDoctrine"
            or "FieldTypeHasConcreteLayout"
            or "FieldTypeDisplayName"
            or "FieldTypeBaseName"
            or "FieldTypeModuleName"
            or "FieldTypeIsGenericInstantiation"
            or "FieldTypeArgumentCount"
            or "FieldTypeComptimeArgumentCount"
            or "FieldTypeHasCSourceAlias"
            or "FieldTypeCSourceAliasName"
            or "FieldTypeHasQualifiers"
            or "FieldTypeBorrowKindIsNone"
            or "FieldTypeBorrowKindIsBorrow"
            or "FieldTypeBorrowKindIsRetBorrow"
            or "FieldTypeBorrowKindIsStoreBorrow"
            or "FieldTypeAccessKindIsNone"
            or "FieldTypeAccessKindIsShared"
            or "FieldTypeAccessKindIsFrozen"
            or "FieldTypeInitializationKindIsNone"
            or "FieldTypeInitializationKindIsOut"
            or "FieldTypeInitializationKindIsInit"
            or "FieldTypeIsMutableView"
            or "FieldTypeUnqualifiedTypeIs"
            or "TypeGenericParameterCount"
            or "TypeGenericParameterName"
            or "TypeComptimeGenericParameterCount"
            or "TypeComptimeGenericParameterName"
            or "TypeComptimeGenericParameterTypeIs"
            or "TypeComptimeGenericParameterTypeIsBool"
            or "TypeComptimeGenericParameterTypeIsInteger"
            or "TypeComptimeGenericParameterTypeIsFloat"
            or "TypeComptimeGenericParameterTypeIsRawPointer"
            or "TypeComptimeGenericParameterTypeIsFixedArray"
            or "TypeComptimeGenericParameterTypeIsSlice"
            or "TypeComptimeGenericParameterTypeIsDynamic"
            or "TypeComptimeGenericParameterTypeIsFunctionPointer"
            or "TypeComptimeGenericParameterTypeIsClosure"
            or "TypeComptimeGenericParameterTypeIsNamed"
            or "TypeComptimeGenericParameterTypeIsStruct"
            or "TypeComptimeGenericParameterTypeIsRecord"
            or "TypeComptimeGenericParameterTypeIsEnum"
            or "TypeComptimeGenericParameterTypeIsTrait"
            or "TypeComptimeGenericParameterTypeIsDoctrine"
            or "TypeComptimeGenericParameterTypeHasConcreteLayout"
            or "TypeComptimeGenericParameterTypeDisplayName"
            or "TypeComptimeGenericParameterTypeBaseName"
            or "TypeComptimeGenericParameterTypeModuleName"
            or "TypeComptimeGenericParameterTypeIsGenericInstantiation"
            or "TypeComptimeGenericParameterTypeArgumentCount"
            or "TypeComptimeGenericParameterTypeComptimeArgumentCount"
            or "TypeDisplayName"
            or "TypeBaseName"
            or "TypeModuleName"
            or "TypeVisibilityIsModule"
            or "TypeVisibilityIsInternal"
            or "TypeVisibilityIsPublic"
            or "TypeVisibilityIsExport"
            or "TypeHasCSourceAlias"
            or "TypeCSourceAliasName"
            or "TypeIsGenericInstantiation"
            or "TypeArgumentCount"
            or "TypeArgumentTypeIs"
            or "TypeArgumentTypeIsBool"
            or "TypeArgumentTypeIsInteger"
            or "TypeArgumentTypeIsFloat"
            or "TypeArgumentTypeIsRawPointer"
            or "TypeArgumentTypeIsFixedArray"
            or "TypeArgumentTypeIsSlice"
            or "TypeArgumentTypeIsDynamic"
            or "TypeArgumentTypeIsFunctionPointer"
            or "TypeArgumentTypeIsClosure"
            or "TypeArgumentTypeIsNamed"
            or "TypeArgumentTypeIsStruct"
            or "TypeArgumentTypeIsRecord"
            or "TypeArgumentTypeIsEnum"
            or "TypeArgumentTypeIsTrait"
            or "TypeArgumentTypeIsDoctrine"
            or "TypeArgumentTypeHasConcreteLayout"
            or "TypeArgumentTypeDisplayName"
            or "TypeArgumentTypeBaseName"
            or "TypeArgumentTypeModuleName"
            or "TypeArgumentTypeIsGenericInstantiation"
            or "TypeArgumentTypeArgumentCount"
            or "TypeArgumentTypeComptimeArgumentCount"
            or "TypeComptimeArgumentCount"
            or "TypeComptimeArgumentName"
            or "TypeComptimeArgumentTypeIs"
            or "TypeComptimeArgumentTypeIsBool"
            or "TypeComptimeArgumentTypeIsInteger"
            or "TypeComptimeArgumentTypeIsFloat"
            or "TypeComptimeArgumentTypeIsRawPointer"
            or "TypeComptimeArgumentTypeIsFixedArray"
            or "TypeComptimeArgumentTypeIsSlice"
            or "TypeComptimeArgumentTypeIsDynamic"
            or "TypeComptimeArgumentTypeIsFunctionPointer"
            or "TypeComptimeArgumentTypeIsClosure"
            or "TypeComptimeArgumentTypeIsNamed"
            or "TypeComptimeArgumentTypeIsStruct"
            or "TypeComptimeArgumentTypeIsRecord"
            or "TypeComptimeArgumentTypeIsEnum"
            or "TypeComptimeArgumentTypeIsTrait"
            or "TypeComptimeArgumentTypeIsDoctrine"
            or "TypeComptimeArgumentTypeHasConcreteLayout"
            or "TypeComptimeArgumentTypeDisplayName"
            or "TypeComptimeArgumentTypeBaseName"
            or "TypeComptimeArgumentTypeModuleName"
            or "TypeComptimeArgumentTypeIsGenericInstantiation"
            or "TypeComptimeArgumentTypeArgumentCount"
            or "TypeComptimeArgumentTypeComptimeArgumentCount"
            or "TypeComptimeArgumentValueIs"
            or "EnumVariantPayloadCount"
            or "EnumVariantTag"
            or "EnumTagOffset"
            or "EnumTagSize"
            or "EnumTagAlign"
            or "EnumTagIsMisaligned"
            or "EnumVariantPayloadOffset"
            or "EnumVariantPayloadSize"
            or "EnumVariantPayloadAlign"
            or "EnumVariantPayloadIsMisaligned"
            or "EnumVariantIsOk"
            or "EnumVariantIsErr"
            or "EnumVariantIsErrorFunnel"
            or "Implements"
            or "ImplementedTraitCount"
            or "ImplementedTraitTypeIs"
            or "ImplementedTraitTypeIsBool"
            or "ImplementedTraitTypeIsInteger"
            or "ImplementedTraitTypeIsFloat"
            or "ImplementedTraitTypeIsRawPointer"
            or "ImplementedTraitTypeIsFixedArray"
            or "ImplementedTraitTypeIsSlice"
            or "ImplementedTraitTypeIsDynamic"
            or "ImplementedTraitTypeIsFunctionPointer"
            or "ImplementedTraitTypeIsClosure"
            or "ImplementedTraitTypeIsNamed"
            or "ImplementedTraitTypeIsStruct"
            or "ImplementedTraitTypeIsRecord"
            or "ImplementedTraitTypeIsEnum"
            or "ImplementedTraitTypeIsTrait"
            or "ImplementedTraitTypeIsDoctrine"
            or "ImplementedTraitTypeHasConcreteLayout"
            or "ImplementedTraitTypeDisplayName"
            or "ImplementedTraitTypeBaseName"
            or "ImplementedTraitTypeModuleName"
            or "ImplementedTraitTypeIsGenericInstantiation"
            or "ImplementedTraitTypeArgumentCount"
            or "ImplementedTraitTypeComptimeArgumentCount"
            or "ImplementedTraitTypeArgumentTypeIs"
            or "ImplementedTraitTypeArgumentTypeIsBool"
            or "ImplementedTraitTypeArgumentTypeIsInteger"
            or "ImplementedTraitTypeArgumentTypeIsFloat"
            or "ImplementedTraitTypeArgumentTypeIsRawPointer"
            or "ImplementedTraitTypeArgumentTypeIsFixedArray"
            or "ImplementedTraitTypeArgumentTypeIsSlice"
            or "ImplementedTraitTypeArgumentTypeIsDynamic"
            or "ImplementedTraitTypeArgumentTypeIsFunctionPointer"
            or "ImplementedTraitTypeArgumentTypeIsClosure"
            or "ImplementedTraitTypeArgumentTypeIsNamed"
            or "ImplementedTraitTypeArgumentTypeIsStruct"
            or "ImplementedTraitTypeArgumentTypeIsRecord"
            or "ImplementedTraitTypeArgumentTypeIsEnum"
            or "ImplementedTraitTypeArgumentTypeIsTrait"
            or "ImplementedTraitTypeArgumentTypeIsDoctrine"
            or "ImplementedTraitTypeArgumentTypeHasConcreteLayout"
            or "ImplementedTraitTypeArgumentTypeDisplayName"
            or "ImplementedTraitTypeArgumentTypeBaseName"
            or "ImplementedTraitTypeArgumentTypeModuleName"
            or "ImplementedTraitTypeArgumentTypeIsGenericInstantiation"
            or "ImplementedTraitTypeArgumentTypeArgumentCount"
            or "ImplementedTraitTypeArgumentTypeComptimeArgumentCount"
            or "ImplementedTraitTypeComptimeArgumentName"
            or "ImplementedTraitTypeComptimeArgumentTypeIs"
            or "ImplementedTraitTypeComptimeArgumentTypeIsBool"
            or "ImplementedTraitTypeComptimeArgumentTypeIsInteger"
            or "ImplementedTraitTypeComptimeArgumentTypeIsFloat"
            or "ImplementedTraitTypeComptimeArgumentTypeIsRawPointer"
            or "ImplementedTraitTypeComptimeArgumentTypeIsFixedArray"
            or "ImplementedTraitTypeComptimeArgumentTypeIsSlice"
            or "ImplementedTraitTypeComptimeArgumentTypeIsDynamic"
            or "ImplementedTraitTypeComptimeArgumentTypeIsFunctionPointer"
            or "ImplementedTraitTypeComptimeArgumentTypeIsClosure"
            or "ImplementedTraitTypeComptimeArgumentTypeIsNamed"
            or "ImplementedTraitTypeComptimeArgumentTypeIsStruct"
            or "ImplementedTraitTypeComptimeArgumentTypeIsRecord"
            or "ImplementedTraitTypeComptimeArgumentTypeIsEnum"
            or "ImplementedTraitTypeComptimeArgumentTypeIsTrait"
            or "ImplementedTraitTypeComptimeArgumentTypeIsDoctrine"
            or "ImplementedTraitTypeComptimeArgumentTypeHasConcreteLayout"
            or "ImplementedTraitTypeComptimeArgumentTypeDisplayName"
            or "ImplementedTraitTypeComptimeArgumentTypeBaseName"
            or "ImplementedTraitTypeComptimeArgumentTypeModuleName"
            or "ImplementedTraitTypeComptimeArgumentTypeIsGenericInstantiation"
            or "ImplementedTraitTypeComptimeArgumentTypeArgumentCount"
            or "ImplementedTraitTypeComptimeArgumentTypeComptimeArgumentCount"
            or "ImplementedTraitTypeComptimeArgumentValueIs"
            or "AssociatedTypeCount"
            or "AssociatedTypeName"
            or "AssociatedTypeHasTarget"
            or "AssociatedTypeTargetTypeIs"
            or "AssociatedTypeTargetTypeIsBool"
            or "AssociatedTypeTargetTypeIsInteger"
            or "AssociatedTypeTargetTypeIsFloat"
            or "AssociatedTypeTargetTypeIsRawPointer"
            or "AssociatedTypeTargetTypeIsFixedArray"
            or "AssociatedTypeTargetTypeIsSlice"
            or "AssociatedTypeTargetTypeIsDynamic"
            or "AssociatedTypeTargetTypeIsFunctionPointer"
            or "AssociatedTypeTargetTypeIsClosure"
            or "AssociatedTypeTargetTypeIsNamed"
            or "AssociatedTypeTargetTypeIsStruct"
            or "AssociatedTypeTargetTypeIsRecord"
            or "AssociatedTypeTargetTypeIsEnum"
            or "AssociatedTypeTargetTypeIsTrait"
            or "AssociatedTypeTargetTypeIsDoctrine"
            or "AssociatedTypeTargetTypeHasConcreteLayout"
            or "AssociatedTypeTargetTypeDisplayName"
            or "AssociatedTypeTargetTypeBaseName"
            or "AssociatedTypeTargetTypeModuleName"
            or "AssociatedTypeTargetTypeIsGenericInstantiation"
            or "AssociatedTypeTargetTypeArgumentCount"
            or "AssociatedTypeTargetTypeComptimeArgumentCount"
            or "FieldTypeIs"
            or "EnumVariantPayloadTypeIs"
            or "EnumVariantAbsorbsErrorTypeIs"
            or "EnumVariantPayloadTypeIsBool"
            or "EnumVariantPayloadTypeIsInteger"
            or "EnumVariantPayloadTypeIsFloat"
            or "EnumVariantPayloadTypeIsRawPointer"
            or "EnumVariantPayloadTypeIsFixedArray"
            or "EnumVariantPayloadTypeIsSlice"
            or "EnumVariantPayloadTypeIsDynamic"
            or "EnumVariantPayloadTypeIsFunctionPointer"
            or "EnumVariantPayloadTypeIsClosure"
            or "EnumVariantPayloadTypeIsNamed"
            or "EnumVariantPayloadTypeIsStruct"
            or "EnumVariantPayloadTypeIsRecord"
            or "EnumVariantPayloadTypeIsEnum"
            or "EnumVariantPayloadTypeIsTrait"
            or "EnumVariantPayloadTypeIsDoctrine"
            or "EnumVariantPayloadTypeHasConcreteLayout"
            or "EnumVariantPayloadTypeDisplayName"
            or "EnumVariantPayloadTypeBaseName"
            or "EnumVariantPayloadTypeModuleName"
            or "EnumVariantPayloadTypeIsGenericInstantiation"
            or "EnumVariantPayloadTypeArgumentCount"
            or "EnumVariantPayloadTypeComptimeArgumentCount"
            or "EnumVariantPayloadTypeHasCSourceAlias"
            or "EnumVariantPayloadTypeCSourceAliasName"
            or "EnumVariantPayloadTypeHasQualifiers"
            or "EnumVariantPayloadTypeBorrowKindIsNone"
            or "EnumVariantPayloadTypeBorrowKindIsBorrow"
            or "EnumVariantPayloadTypeBorrowKindIsRetBorrow"
            or "EnumVariantPayloadTypeBorrowKindIsStoreBorrow"
            or "EnumVariantPayloadTypeAccessKindIsNone"
            or "EnumVariantPayloadTypeAccessKindIsShared"
            or "EnumVariantPayloadTypeAccessKindIsFrozen"
            or "EnumVariantPayloadTypeInitializationKindIsNone"
            or "EnumVariantPayloadTypeInitializationKindIsOut"
            or "EnumVariantPayloadTypeInitializationKindIsInit"
            or "EnumVariantPayloadTypeIsMutableView"
            or "EnumVariantPayloadTypeUnqualifiedTypeIs"
            or "FieldName"
            or "FieldVisibilityIsModule"
            or "FieldVisibilityIsInternal"
            or "FieldVisibilityIsPublic"
            or "FieldVisibilityIsExport"
            or "EnumVariantName"
            or "EnumVariantUsesNamedFields"
            or "EnumVariantPayloadHasName"
            or "EnumVariantPayloadName"
            or "TypeThreadSafetyLawAttributeCount"
            or "TypeThreadSafetyLawAttributeLawName"
            or "TypeThreadSafetyLawAttributeIsGrant"
            or "TypeThreadSafetyLawAttributeIsDeny"
            or "TypeThreadSafetyLawAttributeHasCondition"
            or "TypeThreadSafetyLawAttributeConditionLawName"
            or "TypeThreadSafetyLawAttributeConditionTypeIs"
            or "TypeThreadSafetyLawAttributeConditionTypeIsBool"
            or "TypeThreadSafetyLawAttributeConditionTypeIsInteger"
            or "TypeThreadSafetyLawAttributeConditionTypeIsFloat"
            or "TypeThreadSafetyLawAttributeConditionTypeIsRawPointer"
            or "TypeThreadSafetyLawAttributeConditionTypeIsFixedArray"
            or "TypeThreadSafetyLawAttributeConditionTypeIsSlice"
            or "TypeThreadSafetyLawAttributeConditionTypeIsDynamic"
            or "TypeThreadSafetyLawAttributeConditionTypeIsFunctionPointer"
            or "TypeThreadSafetyLawAttributeConditionTypeIsClosure"
            or "TypeThreadSafetyLawAttributeConditionTypeIsNamed"
            or "TypeThreadSafetyLawAttributeConditionTypeIsStruct"
            or "TypeThreadSafetyLawAttributeConditionTypeIsRecord"
            or "TypeThreadSafetyLawAttributeConditionTypeIsEnum"
            or "TypeThreadSafetyLawAttributeConditionTypeIsTrait"
            or "TypeThreadSafetyLawAttributeConditionTypeIsDoctrine"
            or "TypeThreadSafetyLawAttributeConditionTypeHasConcreteLayout"
            or "TypeThreadSafetyLawAttributeConditionTypeDisplayName"
            or "TypeThreadSafetyLawAttributeConditionTypeBaseName"
            or "TypeThreadSafetyLawAttributeConditionTypeModuleName"
            or "TypeThreadSafetyLawAttributeConditionTypeIsGenericInstantiation"
            or "TypeThreadSafetyLawAttributeConditionTypeArgumentCount"
            or "TypeThreadSafetyLawAttributeConditionTypeComptimeArgumentCount"
            or "FieldThreadSafetyLawAttributeCount"
            or "FieldThreadSafetyLawAttributeLawName"
            or "FieldThreadSafetyLawAttributeIsGrant"
            or "FieldThreadSafetyLawAttributeIsDeny"
            or "FieldThreadSafetyLawAttributeHasCondition"
            or "FieldThreadSafetyLawAttributeConditionLawName"
            or "FieldThreadSafetyLawAttributeConditionTypeIs"
            or "FieldThreadSafetyLawAttributeConditionTypeIsBool"
            or "FieldThreadSafetyLawAttributeConditionTypeIsInteger"
            or "FieldThreadSafetyLawAttributeConditionTypeIsFloat"
            or "FieldThreadSafetyLawAttributeConditionTypeIsRawPointer"
            or "FieldThreadSafetyLawAttributeConditionTypeIsFixedArray"
            or "FieldThreadSafetyLawAttributeConditionTypeIsSlice"
            or "FieldThreadSafetyLawAttributeConditionTypeIsDynamic"
            or "FieldThreadSafetyLawAttributeConditionTypeIsFunctionPointer"
            or "FieldThreadSafetyLawAttributeConditionTypeIsClosure"
            or "FieldThreadSafetyLawAttributeConditionTypeIsNamed"
            or "FieldThreadSafetyLawAttributeConditionTypeIsStruct"
            or "FieldThreadSafetyLawAttributeConditionTypeIsRecord"
            or "FieldThreadSafetyLawAttributeConditionTypeIsEnum"
            or "FieldThreadSafetyLawAttributeConditionTypeIsTrait"
            or "FieldThreadSafetyLawAttributeConditionTypeIsDoctrine"
            or "FieldThreadSafetyLawAttributeConditionTypeHasConcreteLayout"
            or "FieldThreadSafetyLawAttributeConditionTypeDisplayName"
            or "FieldThreadSafetyLawAttributeConditionTypeBaseName"
            or "FieldThreadSafetyLawAttributeConditionTypeModuleName"
            or "FieldThreadSafetyLawAttributeConditionTypeIsGenericInstantiation"
            or "FieldThreadSafetyLawAttributeConditionTypeArgumentCount"
            or "FieldThreadSafetyLawAttributeConditionTypeComptimeArgumentCount";
    }

    private const int CallableNestedTypeArgumentFactFamilySize = 24;
    private const int CallableNestedTypeArgumentPredicateStartOffset = 1;
    private const int CallableNestedTypeArgumentPredicateEndOffset = 17;
    private const int CallableNestedTypeArgumentDisplayNameOffset = 18;
    private const int CallableNestedTypeArgumentBaseNameOffset = 19;
    private const int CallableNestedTypeArgumentModuleNameOffset = 20;
    private const int CallableNestedTypeArgumentIsGenericInstantiationOffset = 21;
    private const int CallableNestedTypeArgumentArgumentCountOffset = 22;
    private const int CallableNestedTypeArgumentComptimeArgumentCountOffset = 23;

    private static bool TryGetCallableNestedTypeArgumentFactKind(
        string localName,
        out CompileTimeStructuralFactKind kind)
    {
        return TryGetCallableNestedTypeArgumentFactKind(
                localName,
                "FunctionPointerReturnTypeArgumentType",
                CompileTimeStructuralFactKind.FunctionPointerReturnTypeArgumentTypeIs,
                out kind)
            || TryGetCallableNestedTypeArgumentFactKind(
                localName,
                "FunctionPointerParameterTypeArgumentType",
                CompileTimeStructuralFactKind.FunctionPointerParameterTypeArgumentTypeIs,
                out kind)
            || TryGetCallableNestedTypeArgumentFactKind(
                localName,
                "ClosureReturnTypeArgumentType",
                CompileTimeStructuralFactKind.ClosureReturnTypeArgumentTypeIs,
                out kind)
            || TryGetCallableNestedTypeArgumentFactKind(
                localName,
                "ClosureParameterTypeArgumentType",
                CompileTimeStructuralFactKind.ClosureParameterTypeArgumentTypeIs,
                out kind)
            || TryGetCallableNestedTypeArgumentFactKind(
                localName,
                "MethodReturnTypeArgumentType",
                CompileTimeStructuralFactKind.MethodReturnTypeArgumentTypeIs,
                out kind)
            || TryGetCallableNestedTypeArgumentFactKind(
                localName,
                "MethodParameterTypeArgumentType",
                CompileTimeStructuralFactKind.MethodParameterTypeArgumentTypeIs,
                out kind);
    }

    private static bool TryGetCallableNestedTypeArgumentFactKind(
        string localName,
        string prefix,
        CompileTimeStructuralFactKind familyStart,
        out CompileTimeStructuralFactKind kind)
    {
        kind = default;
        if (!localName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var offset = localName[prefix.Length..] switch
        {
            "Is" => 0,
            "IsBool" => 1,
            "IsInteger" => 2,
            "IsFloat" => 3,
            "IsRawPointer" => 4,
            "IsFixedArray" => 5,
            "IsSlice" => 6,
            "IsDynamic" => 7,
            "IsFunctionPointer" => 8,
            "IsClosure" => 9,
            "IsDynTrait" => 10,
            "IsNamed" => 11,
            "IsStruct" => 12,
            "IsRecord" => 13,
            "IsEnum" => 14,
            "IsTrait" => 15,
            "IsDoctrine" => 16,
            "HasConcreteLayout" => 17,
            "DisplayName" => CallableNestedTypeArgumentDisplayNameOffset,
            "BaseName" => CallableNestedTypeArgumentBaseNameOffset,
            "ModuleName" => CallableNestedTypeArgumentModuleNameOffset,
            "IsGenericInstantiation" => CallableNestedTypeArgumentIsGenericInstantiationOffset,
            "ArgumentCount" => CallableNestedTypeArgumentArgumentCountOffset,
            "ComptimeArgumentCount" => CallableNestedTypeArgumentComptimeArgumentCountOffset,
            _ => -1
        };
        if (offset < 0)
        {
            return false;
        }

        kind = (CompileTimeStructuralFactKind)((int)familyStart + offset);
        return true;
    }

    public static bool HasValidGenericArgumentShape(StarkParser.GenericQualifiedNameContext genericQualifiedName)
    {
        if (!TryGetFactKind(genericQualifiedName.qualifiedName().GetText(), out var kind))
        {
            return false;
        }

        var arguments = genericQualifiedName.typeArgumentList().genericArgument();
        var typeParameterCount = GetTypeParameters(kind).Count;
        if (arguments.Length != typeParameterCount + GetComptimeValueParameters(kind).Count)
        {
            return false;
        }

        for (var index = 0; index < typeParameterCount; index++)
        {
            if (arguments[index].type_() is null)
            {
                return false;
            }
        }

        for (var index = typeParameterCount; index < arguments.Length; index++)
        {
            if (arguments[index].type_() is not null)
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryGetTypeArgument(
        StarkParser.GenericQualifiedNameContext genericQualifiedName,
        out StarkParser.Type_Context type)
    {
        type = null!;
        var arguments = genericQualifiedName.typeArgumentList().genericArgument();
        if (arguments.Length != 1 || arguments[0].type_() is not { } typeArgument)
        {
            return false;
        }

        type = typeArgument;
        return true;
    }

    public static bool TryResolveArguments(
        string name,
        StarkParser.GenericQualifiedNameContext genericQualifiedName,
        Func<StarkParser.Type_Context, StarkTypeSymbol> resolveType,
        Action<string, string, ParserRuleContext> reportError,
        CompileTimeEvaluationServices compileTimeServices,
        IReadOnlyDictionary<string, ComptimeGenericParameterSymbol>? visibleComptimeParameters,
        IReadOnlyDictionary<string, BigInteger>? comptimeValueSubstitution,
        out CompileTimeStructuralFactArguments arguments)
    {
        arguments = default!;
        if (!TryGetFactKind(name, out var kind))
        {
            return false;
        }

        var resolved = GenericArgumentSyntaxFacts.Resolve(
            genericQualifiedName.typeArgumentList(),
            GetTypeParameters(kind),
            GetComptimeValueParameters(kind),
            resolveType,
            reportError,
            compileTimeServices,
            visibleComptimeParameters);
        if (resolved.TypeArguments.Count != GetTypeParameters(kind).Count
            || resolved.TypeArguments[0].Kind == StarkTypeKind.Error
            || resolved.ComptimeValueArguments.Count != GetComptimeValueParameters(kind).Count)
        {
            return false;
        }

        var valueArguments = resolved.ComptimeValueArguments.Count == 0
            ? Array.Empty<ComptimeValueArgumentSymbol>()
            : ResolveSymbolicValues(resolved.ComptimeValueArguments, comptimeValueSubstitution);
        var additionalTypeArguments = resolved.TypeArguments.Count == 1
            ? Array.Empty<StarkTypeSymbol>()
            : resolved.TypeArguments.Skip(1).ToArray();
        arguments = new CompileTimeStructuralFactArguments(
            resolved.TypeArguments[0],
            additionalTypeArguments,
            valueArguments);
        return true;
    }

    public static bool TryCreateSignature(
        string name,
        StarkTypeSymbol targetType,
        out TypedFunctionSignature signature)
    {
        return TryCreateSignature(
            name,
            new CompileTimeStructuralFactArguments(targetType),
            out signature);
    }

    public static bool TryCreateSignature(
        string name,
        CompileTimeStructuralFactArguments arguments,
        out TypedFunctionSignature signature)
    {
        signature = null!;
        if (!TryGetFactKind(name, out var kind))
        {
            return false;
        }

        if (!TryGetReturnType(kind, out var returnType))
        {
            return false;
        }

        IReadOnlyList<StarkTypeSymbol> typeArguments = arguments.AdditionalTypeArguments.Count == 0
            ? [arguments.TargetType]
            : new[] { arguments.TargetType }.Concat(arguments.AdditionalTypeArguments).ToArray();

        signature = new TypedFunctionSignature(
            name,
            returnType,
            [],
            SourceName: name,
            TypeArguments: typeArguments,
            ComptimeValueArguments: arguments.ComptimeValueArguments,
            Kind: StarkFunctionKind.FiniteLaw,
            HasBody: false);
        return true;
    }

    public static bool IsSignature(TypedFunctionSignature signature)
    {
        return signature.Parameters.Count == 0
            && TryGetFactKind(signature.DisplaySourceName, out var kind)
            && TryGetReturnType(kind, out var returnType)
            && signature.ReturnType == returnType
            && signature.TypeArguments is { } typeArguments
            && typeArguments.Count == GetTypeParameters(kind).Count
            && signature.ComptimeValues.Count == GetComptimeValueParameters(kind).Count;
    }

    public static bool TryGetReturnType(CompileTimeStructuralFactKind kind, out StarkTypeSymbol returnType)
    {
        returnType = kind switch
        {
            _ when IsCallableNestedTypeArgumentCountFact(kind) => CountType,
            _ when IsCallableNestedTypeArgumentTextFact(kind) => StarkTypeSymbols.Ascii,
            CompileTimeStructuralFactKind.FieldCount
                or CompileTimeStructuralFactKind.MethodCount
                or CompileTimeStructuralFactKind.MethodParameterCount
                or CompileTimeStructuralFactKind.MethodGenericParameterCount
                or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundCount
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterCount
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateCount
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeArgumentCount
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.MethodReturnTypeArgumentCount
                or CompileTimeStructuralFactKind.MethodReturnTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.MethodParameterTypeArgumentCount
                or CompileTimeStructuralFactKind.MethodParameterTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeArgumentCount
                or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeArgumentCount
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.FunctionPointerParameterCount
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeArgumentCount
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeArgumentCount
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.ClosureReturnTypeArgumentCount
                or CompileTimeStructuralFactKind.ClosureReturnTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.ClosureParameterTypeArgumentCount
                or CompileTimeStructuralFactKind.ClosureParameterTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.EnumVariantCount
                or CompileTimeStructuralFactKind.FieldOffset
                or CompileTimeStructuralFactKind.FieldSize
                or CompileTimeStructuralFactKind.FieldAlign
                or CompileTimeStructuralFactKind.StructPack
                or CompileTimeStructuralFactKind.StructAlign
                or CompileTimeStructuralFactKind.FieldExplicitOffset
                or CompileTimeStructuralFactKind.FieldTypeArgumentCount
                or CompileTimeStructuralFactKind.FieldTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.AssociatedTypeCount
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeArgumentCount
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.TypeGenericParameterCount
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterCount
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeArgumentCount
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.TypeArgumentCount
                or CompileTimeStructuralFactKind.TypeArgumentTypeArgumentCount
                or CompileTimeStructuralFactKind.TypeArgumentTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.TypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeArgumentCount
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeArgumentCount
                or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeArgumentCount
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.TypeSize
                or CompileTimeStructuralFactKind.TypeAlign
                or CompileTimeStructuralFactKind.TypeFixedArrayLength
                or CompileTimeStructuralFactKind.TypeIntegerBitWidth
                or CompileTimeStructuralFactKind.TypeFloatBitWidth
                or CompileTimeStructuralFactKind.EnumVariantPayloadCount
                or CompileTimeStructuralFactKind.ImplementedTraitCount
                or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentCount
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeCount
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeCount
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeArgumentCount
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeArgumentCount
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.EnumVariantTag
                or CompileTimeStructuralFactKind.EnumTagOffset
                or CompileTimeStructuralFactKind.EnumTagSize
                or CompileTimeStructuralFactKind.EnumTagAlign
                or CompileTimeStructuralFactKind.EnumVariantPayloadOffset
                or CompileTimeStructuralFactKind.EnumVariantPayloadSize
                or CompileTimeStructuralFactKind.EnumVariantPayloadAlign
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeArgumentCount
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeComptimeArgumentCount => CountType,
            CompileTimeStructuralFactKind.FieldName
                or CompileTimeStructuralFactKind.FieldTypeDisplayName
                or CompileTimeStructuralFactKind.FieldTypeBaseName
                or CompileTimeStructuralFactKind.FieldTypeModuleName
                or CompileTimeStructuralFactKind.FieldTypeCSourceAliasName
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeDisplayName
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeBaseName
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeModuleName
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeCSourceAliasName
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeDisplayName
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeBaseName
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeModuleName
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeCSourceAliasName
                or CompileTimeStructuralFactKind.RawPointerElementTypeCSourceAliasName
                or CompileTimeStructuralFactKind.TypeElementTypeCSourceAliasName
                or CompileTimeStructuralFactKind.FunctionPointerParameterRawPointerElementCountExpression
                or CompileTimeStructuralFactKind.ClosureReturnTypeDisplayName
                or CompileTimeStructuralFactKind.ClosureReturnTypeBaseName
                or CompileTimeStructuralFactKind.ClosureReturnTypeModuleName
                or CompileTimeStructuralFactKind.ClosureReturnTypeCSourceAliasName
                or CompileTimeStructuralFactKind.ClosureParameterTypeDisplayName
                or CompileTimeStructuralFactKind.ClosureParameterTypeBaseName
                or CompileTimeStructuralFactKind.ClosureParameterTypeModuleName
                or CompileTimeStructuralFactKind.ClosureParameterTypeCSourceAliasName
                or CompileTimeStructuralFactKind.ClosureParameterRawPointerElementCountExpression
                or CompileTimeStructuralFactKind.MethodName
                or CompileTimeStructuralFactKind.MethodModuleName
                or CompileTimeStructuralFactKind.MethodParameterName
                or CompileTimeStructuralFactKind.MethodGenericParameterName
                or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeDisplayName
                or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeBaseName
                or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeModuleName
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterName
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeDisplayName
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeBaseName
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeModuleName
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateLawName
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeDisplayName
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeBaseName
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeModuleName
                or CompileTimeStructuralFactKind.MethodReturnTypeDisplayName
                or CompileTimeStructuralFactKind.MethodReturnTypeBaseName
                or CompileTimeStructuralFactKind.MethodReturnTypeModuleName
                or CompileTimeStructuralFactKind.MethodReturnTypeCSourceAliasName
                or CompileTimeStructuralFactKind.MethodParameterTypeDisplayName
                or CompileTimeStructuralFactKind.MethodParameterTypeBaseName
                or CompileTimeStructuralFactKind.MethodParameterTypeModuleName
                or CompileTimeStructuralFactKind.MethodParameterTypeCSourceAliasName
                or CompileTimeStructuralFactKind.MethodParameterRawPointerElementCountExpression
                or CompileTimeStructuralFactKind.AssociatedTypeName
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeDisplayName
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeBaseName
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeModuleName
                or CompileTimeStructuralFactKind.TypeGenericParameterName
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterName
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeDisplayName
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeBaseName
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeModuleName
                or CompileTimeStructuralFactKind.TypeArgumentTypeDisplayName
                or CompileTimeStructuralFactKind.TypeArgumentTypeBaseName
                or CompileTimeStructuralFactKind.TypeArgumentTypeModuleName
                or CompileTimeStructuralFactKind.TypeComptimeArgumentName
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeDisplayName
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeBaseName
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeModuleName
                or CompileTimeStructuralFactKind.TypeDisplayName
                or CompileTimeStructuralFactKind.TypeBaseName
                or CompileTimeStructuralFactKind.TypeModuleName
                or CompileTimeStructuralFactKind.TypeCSourceAliasName
                or CompileTimeStructuralFactKind.ImplementedTraitTypeDisplayName
                or CompileTimeStructuralFactKind.ImplementedTraitTypeBaseName
                or CompileTimeStructuralFactKind.ImplementedTraitTypeModuleName
                or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeDisplayName
                or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeBaseName
                or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeModuleName
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentName
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeDisplayName
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeBaseName
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeModuleName
                or CompileTimeStructuralFactKind.EnumVariantName
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeDisplayName
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeBaseName
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeModuleName
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeCSourceAliasName
                or CompileTimeStructuralFactKind.EnumVariantPayloadName
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeLawName
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionLawName
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeDisplayName
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeBaseName
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeModuleName
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeLawName
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionLawName
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeDisplayName
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeBaseName
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeModuleName => StarkTypeSymbols.Ascii,
            _ => StarkTypeSymbols.Bool
        };
        return true;
    }

    private static StarkTypeSymbol CountType { get; } =
        StarkTypeSymbols.Integer(64, BigInteger.Zero, (BigInteger.One << 64) - BigInteger.One, isUnsigned: true);

    public static bool IsFieldTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.FieldTypeIsBool
            or CompileTimeStructuralFactKind.FieldTypeIsInteger
            or CompileTimeStructuralFactKind.FieldTypeIsFloat
            or CompileTimeStructuralFactKind.FieldTypeIsRawPointer
            or CompileTimeStructuralFactKind.FieldTypeIsFixedArray
            or CompileTimeStructuralFactKind.FieldTypeIsSlice
            or CompileTimeStructuralFactKind.FieldTypeIsDynamic
            or CompileTimeStructuralFactKind.FieldTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.FieldTypeIsClosure
            or CompileTimeStructuralFactKind.FieldTypeIsDynTrait
            or CompileTimeStructuralFactKind.FieldTypeIsNamed
            or CompileTimeStructuralFactKind.FieldTypeIsStruct
            or CompileTimeStructuralFactKind.FieldTypeIsRecord
            or CompileTimeStructuralFactKind.FieldTypeIsEnum
            or CompileTimeStructuralFactKind.FieldTypeIsTrait
            or CompileTimeStructuralFactKind.FieldTypeIsDoctrine
            or CompileTimeStructuralFactKind.FieldTypeHasConcreteLayout;
    }

    public static bool IsFieldTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.FieldTypeDisplayName
            or CompileTimeStructuralFactKind.FieldTypeBaseName
            or CompileTimeStructuralFactKind.FieldTypeModuleName
            or CompileTimeStructuralFactKind.FieldTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.FieldTypeArgumentCount
            or CompileTimeStructuralFactKind.FieldTypeComptimeArgumentCount
            or CompileTimeStructuralFactKind.FieldTypeHasCSourceAlias
            or CompileTimeStructuralFactKind.FieldTypeCSourceAliasName
            or CompileTimeStructuralFactKind.FieldTypeHasQualifiers
            or CompileTimeStructuralFactKind.FieldTypeBorrowKindIsNone
            or CompileTimeStructuralFactKind.FieldTypeBorrowKindIsBorrow
            or CompileTimeStructuralFactKind.FieldTypeBorrowKindIsRetBorrow
            or CompileTimeStructuralFactKind.FieldTypeBorrowKindIsStoreBorrow
            or CompileTimeStructuralFactKind.FieldTypeAccessKindIsNone
            or CompileTimeStructuralFactKind.FieldTypeAccessKindIsShared
            or CompileTimeStructuralFactKind.FieldTypeAccessKindIsFrozen
            or CompileTimeStructuralFactKind.FieldTypeInitializationKindIsNone
            or CompileTimeStructuralFactKind.FieldTypeInitializationKindIsOut
            or CompileTimeStructuralFactKind.FieldTypeInitializationKindIsInit
            or CompileTimeStructuralFactKind.FieldTypeIsMutableView
            or CompileTimeStructuralFactKind.FieldTypeUnqualifiedTypeIs;
    }

    public static bool IsTypeVisibilityFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.TypeVisibilityIsModule
            or CompileTimeStructuralFactKind.TypeVisibilityIsInternal
            or CompileTimeStructuralFactKind.TypeVisibilityIsPublic
            or CompileTimeStructuralFactKind.TypeVisibilityIsExport;
    }

    public static bool IsFieldVisibilityFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.FieldVisibilityIsModule
            or CompileTimeStructuralFactKind.FieldVisibilityIsInternal
            or CompileTimeStructuralFactKind.FieldVisibilityIsPublic
            or CompileTimeStructuralFactKind.FieldVisibilityIsExport;
    }

    public static bool IsMethodVisibilityFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.MethodVisibilityIsModule
            or CompileTimeStructuralFactKind.MethodVisibilityIsInternal
            or CompileTimeStructuralFactKind.MethodVisibilityIsPublic
            or CompileTimeStructuralFactKind.MethodVisibilityIsExport;
    }

    public static bool IsTypeThreadSafetyLawAttributeIndexedFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeLawName
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeIsGrant
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeIsDeny
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeHasCondition
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionLawName
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIs
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsBool
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsInteger
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsFloat
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsRawPointer
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsFixedArray
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsSlice
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsDynamic
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsClosure
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsDynTrait
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsNamed
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsStruct
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsRecord
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsEnum
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsTrait
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsDoctrine
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeHasConcreteLayout
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeDisplayName
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeBaseName
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeModuleName
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeArgumentCount
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeComptimeArgumentCount;
    }

    public static bool IsFieldThreadSafetyLawAttributeFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeCount
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeLawName
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeIsGrant
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeIsDeny
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeHasCondition
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionLawName
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIs
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsBool
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsInteger
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsFloat
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsRawPointer
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsFixedArray
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsSlice
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsDynamic
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsClosure
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsDynTrait
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsNamed
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsStruct
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsRecord
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsEnum
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsTrait
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsDoctrine
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeHasConcreteLayout
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeDisplayName
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeBaseName
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeModuleName
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeArgumentCount
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeComptimeArgumentCount;
    }

    public static bool IsFieldThreadSafetyLawAttributeIndexedFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeLawName
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeIsGrant
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeIsDeny
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeHasCondition
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionLawName
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIs
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsBool
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsInteger
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsFloat
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsRawPointer
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsFixedArray
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsSlice
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsDynamic
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsClosure
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsDynTrait
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsNamed
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsStruct
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsRecord
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsEnum
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsTrait
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsDoctrine
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeHasConcreteLayout
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeDisplayName
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeBaseName
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeModuleName
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeArgumentCount
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeComptimeArgumentCount;
    }

    public static bool IsThreadSafetyLawAttributeConditionTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeDisplayName
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeBaseName
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeModuleName
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeArgumentCount
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeComptimeArgumentCount
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeDisplayName
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeBaseName
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeModuleName
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeArgumentCount
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeComptimeArgumentCount;
    }

    public static bool IsThreadSafetyLawAttributeConditionTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsBool
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsInteger
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsFloat
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsRawPointer
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsFixedArray
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsSlice
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsDynamic
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsClosure
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsDynTrait
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsNamed
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsStruct
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsRecord
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsEnum
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsTrait
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsDoctrine
            or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeHasConcreteLayout
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsBool
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsInteger
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsFloat
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsRawPointer
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsFixedArray
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsSlice
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsDynamic
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsClosure
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsDynTrait
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsNamed
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsStruct
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsRecord
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsEnum
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsTrait
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsDoctrine
            or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeHasConcreteLayout;
    }

    public static bool IsFunctionPointerReturnTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsBool
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsInteger
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsFloat
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsRawPointer
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsFixedArray
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsSlice
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsDynamic
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsClosure
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsDynTrait
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsNamed
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsStruct
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsRecord
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsEnum
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsTrait
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsDoctrine
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeHasConcreteLayout;
    }

    public static bool IsFunctionPointerReturnTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.FunctionPointerReturnTypeDisplayName
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeBaseName
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeModuleName
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeArgumentCount
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeComptimeArgumentCount
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeHasCSourceAlias
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeCSourceAliasName
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeHasQualifiers
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeBorrowKindIsNone
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeBorrowKindIsBorrow
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeBorrowKindIsRetBorrow
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeBorrowKindIsStoreBorrow
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeAccessKindIsNone
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeAccessKindIsShared
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeAccessKindIsFrozen
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeInitializationKindIsNone
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeInitializationKindIsOut
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeInitializationKindIsInit
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsMutableView
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeUnqualifiedTypeIs;
    }

    public static bool IsFunctionPointerParameterTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsBool
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsInteger
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsFloat
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsRawPointer
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsFixedArray
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsSlice
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsDynamic
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsClosure
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsDynTrait
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsNamed
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsStruct
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsRecord
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsEnum
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsTrait
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsDoctrine
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeHasConcreteLayout;
    }

    public static bool IsFunctionPointerParameterTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.FunctionPointerParameterTypeDisplayName
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeBaseName
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeModuleName
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeArgumentCount
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeComptimeArgumentCount
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeHasCSourceAlias
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeCSourceAliasName
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeHasQualifiers
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeBorrowKindIsNone
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeBorrowKindIsBorrow
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeBorrowKindIsRetBorrow
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeBorrowKindIsStoreBorrow
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeAccessKindIsNone
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeAccessKindIsShared
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeAccessKindIsFrozen
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeInitializationKindIsNone
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeInitializationKindIsOut
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeInitializationKindIsInit
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsMutableView
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeUnqualifiedTypeIs;
    }

    public static bool IsFunctionPointerParameterMemoryFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.FunctionPointerParametersAreDisjoint
            or CompileTimeStructuralFactKind.FunctionPointerParametersOverlap
            or CompileTimeStructuralFactKind.FunctionPointerParametersAreSame;
    }

    public static bool IsFunctionPointerParameterRawPointerElementCountExpressionFact(
        CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.FunctionPointerParameterHasRawPointerElementCountExpression
            or CompileTimeStructuralFactKind.FunctionPointerParameterRawPointerElementCountExpression;
    }

    public static bool IsClosureReturnTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.ClosureReturnTypeIsBool
            or CompileTimeStructuralFactKind.ClosureReturnTypeIsInteger
            or CompileTimeStructuralFactKind.ClosureReturnTypeIsFloat
            or CompileTimeStructuralFactKind.ClosureReturnTypeIsRawPointer
            or CompileTimeStructuralFactKind.ClosureReturnTypeIsFixedArray
            or CompileTimeStructuralFactKind.ClosureReturnTypeIsSlice
            or CompileTimeStructuralFactKind.ClosureReturnTypeIsDynamic
            or CompileTimeStructuralFactKind.ClosureReturnTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.ClosureReturnTypeIsClosure
            or CompileTimeStructuralFactKind.ClosureReturnTypeIsDynTrait
            or CompileTimeStructuralFactKind.ClosureReturnTypeIsNamed
            or CompileTimeStructuralFactKind.ClosureReturnTypeIsStruct
            or CompileTimeStructuralFactKind.ClosureReturnTypeIsRecord
            or CompileTimeStructuralFactKind.ClosureReturnTypeIsEnum
            or CompileTimeStructuralFactKind.ClosureReturnTypeIsTrait
            or CompileTimeStructuralFactKind.ClosureReturnTypeIsDoctrine
            or CompileTimeStructuralFactKind.ClosureReturnTypeHasConcreteLayout;
    }

    public static bool IsClosureReturnTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.ClosureReturnTypeDisplayName
            or CompileTimeStructuralFactKind.ClosureReturnTypeBaseName
            or CompileTimeStructuralFactKind.ClosureReturnTypeModuleName
            or CompileTimeStructuralFactKind.ClosureReturnTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.ClosureReturnTypeArgumentCount
            or CompileTimeStructuralFactKind.ClosureReturnTypeComptimeArgumentCount
            or CompileTimeStructuralFactKind.ClosureReturnTypeHasCSourceAlias
            or CompileTimeStructuralFactKind.ClosureReturnTypeCSourceAliasName
            or CompileTimeStructuralFactKind.ClosureReturnTypeHasQualifiers
            or CompileTimeStructuralFactKind.ClosureReturnTypeBorrowKindIsNone
            or CompileTimeStructuralFactKind.ClosureReturnTypeBorrowKindIsBorrow
            or CompileTimeStructuralFactKind.ClosureReturnTypeBorrowKindIsRetBorrow
            or CompileTimeStructuralFactKind.ClosureReturnTypeBorrowKindIsStoreBorrow
            or CompileTimeStructuralFactKind.ClosureReturnTypeAccessKindIsNone
            or CompileTimeStructuralFactKind.ClosureReturnTypeAccessKindIsShared
            or CompileTimeStructuralFactKind.ClosureReturnTypeAccessKindIsFrozen
            or CompileTimeStructuralFactKind.ClosureReturnTypeInitializationKindIsNone
            or CompileTimeStructuralFactKind.ClosureReturnTypeInitializationKindIsOut
            or CompileTimeStructuralFactKind.ClosureReturnTypeInitializationKindIsInit
            or CompileTimeStructuralFactKind.ClosureReturnTypeIsMutableView
            or CompileTimeStructuralFactKind.ClosureReturnTypeUnqualifiedTypeIs;
    }

    public static bool IsClosureParameterTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.ClosureParameterTypeIsBool
            or CompileTimeStructuralFactKind.ClosureParameterTypeIsInteger
            or CompileTimeStructuralFactKind.ClosureParameterTypeIsFloat
            or CompileTimeStructuralFactKind.ClosureParameterTypeIsRawPointer
            or CompileTimeStructuralFactKind.ClosureParameterTypeIsFixedArray
            or CompileTimeStructuralFactKind.ClosureParameterTypeIsSlice
            or CompileTimeStructuralFactKind.ClosureParameterTypeIsDynamic
            or CompileTimeStructuralFactKind.ClosureParameterTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.ClosureParameterTypeIsClosure
            or CompileTimeStructuralFactKind.ClosureParameterTypeIsDynTrait
            or CompileTimeStructuralFactKind.ClosureParameterTypeIsNamed
            or CompileTimeStructuralFactKind.ClosureParameterTypeIsStruct
            or CompileTimeStructuralFactKind.ClosureParameterTypeIsRecord
            or CompileTimeStructuralFactKind.ClosureParameterTypeIsEnum
            or CompileTimeStructuralFactKind.ClosureParameterTypeIsTrait
            or CompileTimeStructuralFactKind.ClosureParameterTypeIsDoctrine
            or CompileTimeStructuralFactKind.ClosureParameterTypeHasConcreteLayout;
    }

    public static bool IsClosureParameterTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.ClosureParameterTypeDisplayName
            or CompileTimeStructuralFactKind.ClosureParameterTypeBaseName
            or CompileTimeStructuralFactKind.ClosureParameterTypeModuleName
            or CompileTimeStructuralFactKind.ClosureParameterTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.ClosureParameterTypeArgumentCount
            or CompileTimeStructuralFactKind.ClosureParameterTypeComptimeArgumentCount
            or CompileTimeStructuralFactKind.ClosureParameterTypeHasCSourceAlias
            or CompileTimeStructuralFactKind.ClosureParameterTypeCSourceAliasName
            or CompileTimeStructuralFactKind.ClosureParameterTypeHasQualifiers
            or CompileTimeStructuralFactKind.ClosureParameterTypeBorrowKindIsNone
            or CompileTimeStructuralFactKind.ClosureParameterTypeBorrowKindIsBorrow
            or CompileTimeStructuralFactKind.ClosureParameterTypeBorrowKindIsRetBorrow
            or CompileTimeStructuralFactKind.ClosureParameterTypeBorrowKindIsStoreBorrow
            or CompileTimeStructuralFactKind.ClosureParameterTypeAccessKindIsNone
            or CompileTimeStructuralFactKind.ClosureParameterTypeAccessKindIsShared
            or CompileTimeStructuralFactKind.ClosureParameterTypeAccessKindIsFrozen
            or CompileTimeStructuralFactKind.ClosureParameterTypeInitializationKindIsNone
            or CompileTimeStructuralFactKind.ClosureParameterTypeInitializationKindIsOut
            or CompileTimeStructuralFactKind.ClosureParameterTypeInitializationKindIsInit
            or CompileTimeStructuralFactKind.ClosureParameterTypeIsMutableView
            or CompileTimeStructuralFactKind.ClosureParameterTypeUnqualifiedTypeIs;
    }

    public static bool IsClosureParameterMemoryFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.ClosureParametersAreDisjoint
            or CompileTimeStructuralFactKind.ClosureParametersOverlap
            or CompileTimeStructuralFactKind.ClosureParametersAreSame;
    }

    public static bool IsClosureParameterRawPointerElementCountExpressionFact(
        CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.ClosureParameterHasRawPointerElementCountExpression
            or CompileTimeStructuralFactKind.ClosureParameterRawPointerElementCountExpression;
    }

    public static bool IsMethodReturnTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.MethodReturnTypeIsBool
            or CompileTimeStructuralFactKind.MethodReturnTypeIsInteger
            or CompileTimeStructuralFactKind.MethodReturnTypeIsFloat
            or CompileTimeStructuralFactKind.MethodReturnTypeIsRawPointer
            or CompileTimeStructuralFactKind.MethodReturnTypeIsFixedArray
            or CompileTimeStructuralFactKind.MethodReturnTypeIsSlice
            or CompileTimeStructuralFactKind.MethodReturnTypeIsDynamic
            or CompileTimeStructuralFactKind.MethodReturnTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.MethodReturnTypeIsClosure
            or CompileTimeStructuralFactKind.MethodReturnTypeIsDynTrait
            or CompileTimeStructuralFactKind.MethodReturnTypeIsNamed
            or CompileTimeStructuralFactKind.MethodReturnTypeIsStruct
            or CompileTimeStructuralFactKind.MethodReturnTypeIsRecord
            or CompileTimeStructuralFactKind.MethodReturnTypeIsEnum
            or CompileTimeStructuralFactKind.MethodReturnTypeIsTrait
            or CompileTimeStructuralFactKind.MethodReturnTypeIsDoctrine
            or CompileTimeStructuralFactKind.MethodReturnTypeHasConcreteLayout;
    }

    public static bool IsMethodParameterTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.MethodParameterTypeIsBool
            or CompileTimeStructuralFactKind.MethodParameterTypeIsInteger
            or CompileTimeStructuralFactKind.MethodParameterTypeIsFloat
            or CompileTimeStructuralFactKind.MethodParameterTypeIsRawPointer
            or CompileTimeStructuralFactKind.MethodParameterTypeIsFixedArray
            or CompileTimeStructuralFactKind.MethodParameterTypeIsSlice
            or CompileTimeStructuralFactKind.MethodParameterTypeIsDynamic
            or CompileTimeStructuralFactKind.MethodParameterTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.MethodParameterTypeIsClosure
            or CompileTimeStructuralFactKind.MethodParameterTypeIsDynTrait
            or CompileTimeStructuralFactKind.MethodParameterTypeIsNamed
            or CompileTimeStructuralFactKind.MethodParameterTypeIsStruct
            or CompileTimeStructuralFactKind.MethodParameterTypeIsRecord
            or CompileTimeStructuralFactKind.MethodParameterTypeIsEnum
            or CompileTimeStructuralFactKind.MethodParameterTypeIsTrait
            or CompileTimeStructuralFactKind.MethodParameterTypeIsDoctrine
            or CompileTimeStructuralFactKind.MethodParameterTypeHasConcreteLayout;
    }

    public static bool IsMethodParameterMemoryFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.MethodParametersAreDisjoint
            or CompileTimeStructuralFactKind.MethodParametersOverlap
            or CompileTimeStructuralFactKind.MethodParametersAreSame;
    }

    public static bool IsMethodParameterRawPointerElementCountExpressionFact(
        CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.MethodParameterHasRawPointerElementCountExpression
            or CompileTimeStructuralFactKind.MethodParameterRawPointerElementCountExpression;
    }

    public static bool IsMethodReturnTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.MethodReturnTypeDisplayName
            or CompileTimeStructuralFactKind.MethodReturnTypeBaseName
            or CompileTimeStructuralFactKind.MethodReturnTypeModuleName
            or CompileTimeStructuralFactKind.MethodReturnTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.MethodReturnTypeArgumentCount
            or CompileTimeStructuralFactKind.MethodReturnTypeComptimeArgumentCount
            or CompileTimeStructuralFactKind.MethodReturnTypeHasCSourceAlias
            or CompileTimeStructuralFactKind.MethodReturnTypeCSourceAliasName
            or CompileTimeStructuralFactKind.MethodReturnTypeHasQualifiers
            or CompileTimeStructuralFactKind.MethodReturnTypeBorrowKindIsNone
            or CompileTimeStructuralFactKind.MethodReturnTypeBorrowKindIsBorrow
            or CompileTimeStructuralFactKind.MethodReturnTypeBorrowKindIsRetBorrow
            or CompileTimeStructuralFactKind.MethodReturnTypeBorrowKindIsStoreBorrow
            or CompileTimeStructuralFactKind.MethodReturnTypeAccessKindIsNone
            or CompileTimeStructuralFactKind.MethodReturnTypeAccessKindIsShared
            or CompileTimeStructuralFactKind.MethodReturnTypeAccessKindIsFrozen
            or CompileTimeStructuralFactKind.MethodReturnTypeInitializationKindIsNone
            or CompileTimeStructuralFactKind.MethodReturnTypeInitializationKindIsOut
            or CompileTimeStructuralFactKind.MethodReturnTypeInitializationKindIsInit
            or CompileTimeStructuralFactKind.MethodReturnTypeIsMutableView
            or CompileTimeStructuralFactKind.MethodReturnTypeUnqualifiedTypeIs;
    }

    public static bool IsMethodParameterTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.MethodParameterTypeDisplayName
            or CompileTimeStructuralFactKind.MethodParameterTypeBaseName
            or CompileTimeStructuralFactKind.MethodParameterTypeModuleName
            or CompileTimeStructuralFactKind.MethodParameterTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.MethodParameterTypeArgumentCount
            or CompileTimeStructuralFactKind.MethodParameterTypeComptimeArgumentCount
            or CompileTimeStructuralFactKind.MethodParameterTypeHasCSourceAlias
            or CompileTimeStructuralFactKind.MethodParameterTypeCSourceAliasName
            or CompileTimeStructuralFactKind.MethodParameterTypeHasQualifiers
            or CompileTimeStructuralFactKind.MethodParameterTypeBorrowKindIsNone
            or CompileTimeStructuralFactKind.MethodParameterTypeBorrowKindIsBorrow
            or CompileTimeStructuralFactKind.MethodParameterTypeBorrowKindIsRetBorrow
            or CompileTimeStructuralFactKind.MethodParameterTypeBorrowKindIsStoreBorrow
            or CompileTimeStructuralFactKind.MethodParameterTypeAccessKindIsNone
            or CompileTimeStructuralFactKind.MethodParameterTypeAccessKindIsShared
            or CompileTimeStructuralFactKind.MethodParameterTypeAccessKindIsFrozen
            or CompileTimeStructuralFactKind.MethodParameterTypeInitializationKindIsNone
            or CompileTimeStructuralFactKind.MethodParameterTypeInitializationKindIsOut
            or CompileTimeStructuralFactKind.MethodParameterTypeInitializationKindIsInit
            or CompileTimeStructuralFactKind.MethodParameterTypeIsMutableView
            or CompileTimeStructuralFactKind.MethodParameterTypeUnqualifiedTypeIs;
    }

    public static bool IsFunctionPointerReturnTypeArgumentFact(CompileTimeStructuralFactKind kind)
    {
        return IsCallableNestedTypeArgumentFamilyFact(
            kind,
            CompileTimeStructuralFactKind.FunctionPointerReturnTypeArgumentTypeIs);
    }

    public static bool IsFunctionPointerParameterTypeArgumentFact(CompileTimeStructuralFactKind kind)
    {
        return IsCallableNestedTypeArgumentFamilyFact(
            kind,
            CompileTimeStructuralFactKind.FunctionPointerParameterTypeArgumentTypeIs);
    }

    public static bool IsClosureReturnTypeArgumentFact(CompileTimeStructuralFactKind kind)
    {
        return IsCallableNestedTypeArgumentFamilyFact(
            kind,
            CompileTimeStructuralFactKind.ClosureReturnTypeArgumentTypeIs);
    }

    public static bool IsClosureParameterTypeArgumentFact(CompileTimeStructuralFactKind kind)
    {
        return IsCallableNestedTypeArgumentFamilyFact(
            kind,
            CompileTimeStructuralFactKind.ClosureParameterTypeArgumentTypeIs);
    }

    public static bool IsMethodReturnTypeArgumentFact(CompileTimeStructuralFactKind kind)
    {
        return IsCallableNestedTypeArgumentFamilyFact(
            kind,
            CompileTimeStructuralFactKind.MethodReturnTypeArgumentTypeIs);
    }

    public static bool IsMethodParameterTypeArgumentFact(CompileTimeStructuralFactKind kind)
    {
        return IsCallableNestedTypeArgumentFamilyFact(
            kind,
            CompileTimeStructuralFactKind.MethodParameterTypeArgumentTypeIs);
    }

    public static bool IsCallableNestedTypeArgumentExactFact(CompileTimeStructuralFactKind kind)
    {
        return GetCallableNestedTypeArgumentOffset(kind) == 0;
    }

    public static bool IsCallableNestedTypeArgumentTypePredicate(CompileTimeStructuralFactKind kind)
    {
        var offset = GetCallableNestedTypeArgumentOffset(kind);
        return offset is >= CallableNestedTypeArgumentPredicateStartOffset
            and <= CallableNestedTypeArgumentPredicateEndOffset;
    }

    public static bool IsCallableNestedTypeArgumentMetadataFact(CompileTimeStructuralFactKind kind)
    {
        var offset = GetCallableNestedTypeArgumentOffset(kind);
        return offset is >= CallableNestedTypeArgumentDisplayNameOffset
            and <= CallableNestedTypeArgumentComptimeArgumentCountOffset;
    }

    private static bool IsCallableNestedTypeArgumentFamilyFact(
        CompileTimeStructuralFactKind kind,
        CompileTimeStructuralFactKind familyStart)
    {
        var offset = (int)kind - (int)familyStart;
        return offset is >= 0 and < CallableNestedTypeArgumentFactFamilySize;
    }

    private static int GetCallableNestedTypeArgumentOffset(CompileTimeStructuralFactKind kind)
    {
        if (IsFunctionPointerReturnTypeArgumentFact(kind))
        {
            return (int)kind - (int)CompileTimeStructuralFactKind.FunctionPointerReturnTypeArgumentTypeIs;
        }

        if (IsFunctionPointerParameterTypeArgumentFact(kind))
        {
            return (int)kind - (int)CompileTimeStructuralFactKind.FunctionPointerParameterTypeArgumentTypeIs;
        }

        if (IsClosureReturnTypeArgumentFact(kind))
        {
            return (int)kind - (int)CompileTimeStructuralFactKind.ClosureReturnTypeArgumentTypeIs;
        }

        if (IsClosureParameterTypeArgumentFact(kind))
        {
            return (int)kind - (int)CompileTimeStructuralFactKind.ClosureParameterTypeArgumentTypeIs;
        }

        if (IsMethodReturnTypeArgumentFact(kind))
        {
            return (int)kind - (int)CompileTimeStructuralFactKind.MethodReturnTypeArgumentTypeIs;
        }

        if (IsMethodParameterTypeArgumentFact(kind))
        {
            return (int)kind - (int)CompileTimeStructuralFactKind.MethodParameterTypeArgumentTypeIs;
        }

        return -1;
    }

    private static bool IsCallableNestedTypeArgumentDisplayNameFact(CompileTimeStructuralFactKind kind)
    {
        return GetCallableNestedTypeArgumentOffset(kind) == CallableNestedTypeArgumentDisplayNameOffset;
    }

    private static bool IsCallableNestedTypeArgumentBaseNameFact(CompileTimeStructuralFactKind kind)
    {
        return GetCallableNestedTypeArgumentOffset(kind) == CallableNestedTypeArgumentBaseNameOffset;
    }

    private static bool IsCallableNestedTypeArgumentModuleNameFact(CompileTimeStructuralFactKind kind)
    {
        return GetCallableNestedTypeArgumentOffset(kind) == CallableNestedTypeArgumentModuleNameOffset;
    }

    private static bool IsCallableNestedTypeArgumentIsGenericInstantiationFact(CompileTimeStructuralFactKind kind)
    {
        return GetCallableNestedTypeArgumentOffset(kind) == CallableNestedTypeArgumentIsGenericInstantiationOffset;
    }

    private static bool IsCallableNestedTypeArgumentCountFact(CompileTimeStructuralFactKind kind)
    {
        var offset = GetCallableNestedTypeArgumentOffset(kind);
        return offset is CallableNestedTypeArgumentArgumentCountOffset
            or CallableNestedTypeArgumentComptimeArgumentCountOffset;
    }

    private static bool IsCallableNestedTypeArgumentTextFact(CompileTimeStructuralFactKind kind)
    {
        var offset = GetCallableNestedTypeArgumentOffset(kind);
        return offset is CallableNestedTypeArgumentDisplayNameOffset
            or CallableNestedTypeArgumentBaseNameOffset
            or CallableNestedTypeArgumentModuleNameOffset;
    }

    private static bool IsNestedTypeQualifierMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.FieldTypeHasQualifiers
            or CompileTimeStructuralFactKind.FieldTypeBorrowKindIsNone
            or CompileTimeStructuralFactKind.FieldTypeBorrowKindIsBorrow
            or CompileTimeStructuralFactKind.FieldTypeBorrowKindIsRetBorrow
            or CompileTimeStructuralFactKind.FieldTypeBorrowKindIsStoreBorrow
            or CompileTimeStructuralFactKind.FieldTypeAccessKindIsNone
            or CompileTimeStructuralFactKind.FieldTypeAccessKindIsShared
            or CompileTimeStructuralFactKind.FieldTypeAccessKindIsFrozen
            or CompileTimeStructuralFactKind.FieldTypeInitializationKindIsNone
            or CompileTimeStructuralFactKind.FieldTypeInitializationKindIsOut
            or CompileTimeStructuralFactKind.FieldTypeInitializationKindIsInit
            or CompileTimeStructuralFactKind.FieldTypeIsMutableView
            or CompileTimeStructuralFactKind.FieldTypeUnqualifiedTypeIs
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeHasQualifiers
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeBorrowKindIsNone
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeBorrowKindIsBorrow
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeBorrowKindIsRetBorrow
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeBorrowKindIsStoreBorrow
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeAccessKindIsNone
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeAccessKindIsShared
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeAccessKindIsFrozen
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeInitializationKindIsNone
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeInitializationKindIsOut
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeInitializationKindIsInit
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsMutableView
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeUnqualifiedTypeIs
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeHasQualifiers
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeBorrowKindIsNone
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeBorrowKindIsBorrow
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeBorrowKindIsRetBorrow
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeBorrowKindIsStoreBorrow
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeAccessKindIsNone
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeAccessKindIsShared
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeAccessKindIsFrozen
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeInitializationKindIsNone
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeInitializationKindIsOut
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeInitializationKindIsInit
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsMutableView
            or CompileTimeStructuralFactKind.FunctionPointerReturnTypeUnqualifiedTypeIs
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeHasQualifiers
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeBorrowKindIsNone
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeBorrowKindIsBorrow
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeBorrowKindIsRetBorrow
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeBorrowKindIsStoreBorrow
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeAccessKindIsNone
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeAccessKindIsShared
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeAccessKindIsFrozen
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeInitializationKindIsNone
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeInitializationKindIsOut
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeInitializationKindIsInit
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsMutableView
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeUnqualifiedTypeIs
            or CompileTimeStructuralFactKind.ClosureReturnTypeHasQualifiers
            or CompileTimeStructuralFactKind.ClosureReturnTypeBorrowKindIsNone
            or CompileTimeStructuralFactKind.ClosureReturnTypeBorrowKindIsBorrow
            or CompileTimeStructuralFactKind.ClosureReturnTypeBorrowKindIsRetBorrow
            or CompileTimeStructuralFactKind.ClosureReturnTypeBorrowKindIsStoreBorrow
            or CompileTimeStructuralFactKind.ClosureReturnTypeAccessKindIsNone
            or CompileTimeStructuralFactKind.ClosureReturnTypeAccessKindIsShared
            or CompileTimeStructuralFactKind.ClosureReturnTypeAccessKindIsFrozen
            or CompileTimeStructuralFactKind.ClosureReturnTypeInitializationKindIsNone
            or CompileTimeStructuralFactKind.ClosureReturnTypeInitializationKindIsOut
            or CompileTimeStructuralFactKind.ClosureReturnTypeInitializationKindIsInit
            or CompileTimeStructuralFactKind.ClosureReturnTypeIsMutableView
            or CompileTimeStructuralFactKind.ClosureReturnTypeUnqualifiedTypeIs
            or CompileTimeStructuralFactKind.ClosureParameterTypeHasQualifiers
            or CompileTimeStructuralFactKind.ClosureParameterTypeBorrowKindIsNone
            or CompileTimeStructuralFactKind.ClosureParameterTypeBorrowKindIsBorrow
            or CompileTimeStructuralFactKind.ClosureParameterTypeBorrowKindIsRetBorrow
            or CompileTimeStructuralFactKind.ClosureParameterTypeBorrowKindIsStoreBorrow
            or CompileTimeStructuralFactKind.ClosureParameterTypeAccessKindIsNone
            or CompileTimeStructuralFactKind.ClosureParameterTypeAccessKindIsShared
            or CompileTimeStructuralFactKind.ClosureParameterTypeAccessKindIsFrozen
            or CompileTimeStructuralFactKind.ClosureParameterTypeInitializationKindIsNone
            or CompileTimeStructuralFactKind.ClosureParameterTypeInitializationKindIsOut
            or CompileTimeStructuralFactKind.ClosureParameterTypeInitializationKindIsInit
            or CompileTimeStructuralFactKind.ClosureParameterTypeIsMutableView
            or CompileTimeStructuralFactKind.ClosureParameterTypeUnqualifiedTypeIs
            or CompileTimeStructuralFactKind.MethodReturnTypeHasQualifiers
            or CompileTimeStructuralFactKind.MethodReturnTypeBorrowKindIsNone
            or CompileTimeStructuralFactKind.MethodReturnTypeBorrowKindIsBorrow
            or CompileTimeStructuralFactKind.MethodReturnTypeBorrowKindIsRetBorrow
            or CompileTimeStructuralFactKind.MethodReturnTypeBorrowKindIsStoreBorrow
            or CompileTimeStructuralFactKind.MethodReturnTypeAccessKindIsNone
            or CompileTimeStructuralFactKind.MethodReturnTypeAccessKindIsShared
            or CompileTimeStructuralFactKind.MethodReturnTypeAccessKindIsFrozen
            or CompileTimeStructuralFactKind.MethodReturnTypeInitializationKindIsNone
            or CompileTimeStructuralFactKind.MethodReturnTypeInitializationKindIsOut
            or CompileTimeStructuralFactKind.MethodReturnTypeInitializationKindIsInit
            or CompileTimeStructuralFactKind.MethodReturnTypeIsMutableView
            or CompileTimeStructuralFactKind.MethodReturnTypeUnqualifiedTypeIs
            or CompileTimeStructuralFactKind.MethodParameterTypeHasQualifiers
            or CompileTimeStructuralFactKind.MethodParameterTypeBorrowKindIsNone
            or CompileTimeStructuralFactKind.MethodParameterTypeBorrowKindIsBorrow
            or CompileTimeStructuralFactKind.MethodParameterTypeBorrowKindIsRetBorrow
            or CompileTimeStructuralFactKind.MethodParameterTypeBorrowKindIsStoreBorrow
            or CompileTimeStructuralFactKind.MethodParameterTypeAccessKindIsNone
            or CompileTimeStructuralFactKind.MethodParameterTypeAccessKindIsShared
            or CompileTimeStructuralFactKind.MethodParameterTypeAccessKindIsFrozen
            or CompileTimeStructuralFactKind.MethodParameterTypeInitializationKindIsNone
            or CompileTimeStructuralFactKind.MethodParameterTypeInitializationKindIsOut
            or CompileTimeStructuralFactKind.MethodParameterTypeInitializationKindIsInit
            or CompileTimeStructuralFactKind.MethodParameterTypeIsMutableView
            or CompileTimeStructuralFactKind.MethodParameterTypeUnqualifiedTypeIs;
    }

    public static bool IsMethodThreadSafetyLawPredicateIndexedFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateLawName
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIs
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsBool
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsInteger
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsFloat
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsRawPointer
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsFixedArray
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsSlice
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsDynamic
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsClosure
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsDynTrait
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsNamed
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsStruct
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsRecord
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsEnum
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsTrait
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsDoctrine
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeHasConcreteLayout
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeDisplayName
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeBaseName
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeModuleName
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeArgumentCount
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeComptimeArgumentCount;
    }

    public static bool IsMethodThreadSafetyLawPredicateTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsBool
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsInteger
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsFloat
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsRawPointer
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsFixedArray
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsSlice
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsDynamic
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsClosure
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsDynTrait
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsNamed
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsStruct
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsRecord
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsEnum
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsTrait
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsDoctrine
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeHasConcreteLayout;
    }

    public static bool IsMethodThreadSafetyLawPredicateTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeDisplayName
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeBaseName
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeModuleName
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeArgumentCount
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeComptimeArgumentCount;
    }

    public static bool IsMethodGenericParameterTraitBoundIndexedFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIs
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsBool
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsInteger
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsFloat
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsRawPointer
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsFixedArray
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsSlice
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsDynamic
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsClosure
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsDynTrait
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsNamed
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsStruct
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsRecord
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsEnum
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsTrait
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsDoctrine
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeHasConcreteLayout
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeDisplayName
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeBaseName
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeModuleName
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeArgumentCount
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeComptimeArgumentCount;
    }

    public static bool IsMethodGenericParameterTraitBoundTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsBool
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsInteger
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsFloat
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsRawPointer
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsFixedArray
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsSlice
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsDynamic
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsClosure
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsDynTrait
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsNamed
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsStruct
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsRecord
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsEnum
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsTrait
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsDoctrine
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeHasConcreteLayout;
    }

    public static bool IsMethodGenericParameterTraitBoundTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeDisplayName
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeBaseName
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeModuleName
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeArgumentCount
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeComptimeArgumentCount;
    }

    public static bool IsImplementedTraitIndexedFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.ImplementedTraitTypeIs
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsBool
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsInteger
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsFloat
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsRawPointer
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsFixedArray
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsSlice
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsDynamic
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsClosure
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsDynTrait
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsNamed
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsStruct
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsRecord
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsEnum
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsTrait
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsDoctrine
            or CompileTimeStructuralFactKind.ImplementedTraitTypeHasConcreteLayout
            or CompileTimeStructuralFactKind.ImplementedTraitTypeDisplayName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeBaseName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeModuleName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentCount
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentCount
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIs
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsBool
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsInteger
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsFloat
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsRawPointer
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsFixedArray
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsSlice
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsDynamic
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsClosure
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsDynTrait
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsNamed
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsStruct
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsRecord
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsEnum
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsTrait
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsDoctrine
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeHasConcreteLayout
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeDisplayName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeBaseName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeModuleName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeArgumentCount
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeComptimeArgumentCount
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIs
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsBool
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsInteger
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsFloat
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsRawPointer
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsFixedArray
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsSlice
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsDynamic
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsClosure
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsDynTrait
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsNamed
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsStruct
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsRecord
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsEnum
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsTrait
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsDoctrine
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeHasConcreteLayout
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeDisplayName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeBaseName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeModuleName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeArgumentCount
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeComptimeArgumentCount
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentValueIs;
    }

    public static bool IsImplementedTraitTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.ImplementedTraitTypeIsBool
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsInteger
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsFloat
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsRawPointer
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsFixedArray
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsSlice
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsDynamic
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsClosure
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsDynTrait
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsNamed
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsStruct
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsRecord
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsEnum
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsTrait
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsDoctrine
            or CompileTimeStructuralFactKind.ImplementedTraitTypeHasConcreteLayout;
    }

    public static bool IsImplementedTraitTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.ImplementedTraitTypeDisplayName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeBaseName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeModuleName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentCount
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentCount;
    }

    public static bool IsImplementedTraitTypeArgumentIndexedFact(CompileTimeStructuralFactKind kind)
    {
        return kind == CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIs
            || IsImplementedTraitTypeArgumentTypePredicate(kind)
            || IsImplementedTraitTypeArgumentTypeMetadataFact(kind);
    }

    public static bool IsImplementedTraitTypeArgumentTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsBool
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsInteger
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsFloat
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsRawPointer
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsFixedArray
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsSlice
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsDynamic
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsClosure
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsDynTrait
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsNamed
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsStruct
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsRecord
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsEnum
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsTrait
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsDoctrine
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeHasConcreteLayout;
    }

    public static bool IsImplementedTraitTypeArgumentTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeDisplayName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeBaseName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeModuleName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeArgumentCount
            or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeComptimeArgumentCount;
    }

    public static bool IsImplementedTraitComptimeArgumentIndexedFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIs
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentValueIs
            || IsImplementedTraitComptimeArgumentTypePredicate(kind)
            || IsImplementedTraitComptimeArgumentTypeMetadataFact(kind);
    }

    public static bool IsImplementedTraitComptimeArgumentTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsBool
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsInteger
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsFloat
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsRawPointer
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsFixedArray
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsSlice
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsDynamic
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsClosure
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsDynTrait
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsNamed
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsStruct
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsRecord
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsEnum
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsTrait
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsDoctrine
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeHasConcreteLayout;
    }

    public static bool IsImplementedTraitComptimeArgumentTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeDisplayName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeBaseName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeModuleName
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeArgumentCount
            or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeComptimeArgumentCount;
    }

    public static bool IsMethodComptimeGenericParameterTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsBool
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsInteger
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsFloat
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsRawPointer
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsFixedArray
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsSlice
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsDynamic
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsClosure
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsDynTrait
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsNamed
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsStruct
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsRecord
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsEnum
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsTrait
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsDoctrine
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeHasConcreteLayout;
    }

    public static bool IsMethodComptimeGenericParameterTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeDisplayName
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeBaseName
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeModuleName
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeArgumentCount
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeComptimeArgumentCount;
    }

    public static bool IsMethodIndexedFact(CompileTimeStructuralFactKind kind)
    {
        return (kind is CompileTimeStructuralFactKind.MethodName
            or CompileTimeStructuralFactKind.MethodModuleName
            or CompileTimeStructuralFactKind.MethodVisibilityIsModule
            or CompileTimeStructuralFactKind.MethodVisibilityIsInternal
            or CompileTimeStructuralFactKind.MethodVisibilityIsPublic
            or CompileTimeStructuralFactKind.MethodVisibilityIsExport
            or CompileTimeStructuralFactKind.MethodParameterCount
            or CompileTimeStructuralFactKind.MethodParameterName
            or CompileTimeStructuralFactKind.MethodReturnTypeIs
            or CompileTimeStructuralFactKind.MethodParameterTypeIs
            or CompileTimeStructuralFactKind.MethodKindIsFn
            or CompileTimeStructuralFactKind.MethodKindIsFinite
            or CompileTimeStructuralFactKind.MethodKindIsLaw
            or CompileTimeStructuralFactKind.MethodKindIsFiniteLaw
            or CompileTimeStructuralFactKind.MethodIsStatic
            or CompileTimeStructuralFactKind.MethodHasBody
            or CompileTimeStructuralFactKind.MethodIsUnsafe
            or CompileTimeStructuralFactKind.MethodIsVarargs
            or CompileTimeStructuralFactKind.MethodHasFfiAbi
            or CompileTimeStructuralFactKind.MethodAbiIsC
            or CompileTimeStructuralFactKind.MethodAbiIsCDecl
            or CompileTimeStructuralFactKind.MethodAbiIsStdCall
            or CompileTimeStructuralFactKind.MethodAbiIsFastCall
            or CompileTimeStructuralFactKind.MethodAbiIsThisCall
            or CompileTimeStructuralFactKind.MethodAbiIsVectorCall
            or CompileTimeStructuralFactKind.MethodAbiIsSysV
            or CompileTimeStructuralFactKind.MethodAbiIsWin64
            or CompileTimeStructuralFactKind.MethodAbiIsAapcs
            or CompileTimeStructuralFactKind.MethodAbiIsAapcs64
            or CompileTimeStructuralFactKind.MethodGenericParameterCount
            or CompileTimeStructuralFactKind.MethodGenericParameterName
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundCount
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterCount
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterName
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIs
            or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateCount)
            || IsMethodReturnTypePredicate(kind)
            || IsMethodParameterTypePredicate(kind)
            || IsMethodParameterMemoryFact(kind)
            || IsMethodParameterRawPointerElementCountExpressionFact(kind)
            || IsMethodReturnTypeMetadataFact(kind)
            || IsMethodParameterTypeMetadataFact(kind)
            || IsMethodReturnTypeArgumentFact(kind)
            || IsMethodParameterTypeArgumentFact(kind)
            || IsMethodGenericParameterTraitBoundIndexedFact(kind)
            || IsMethodComptimeGenericParameterTypePredicate(kind)
            || IsMethodComptimeGenericParameterTypeMetadataFact(kind)
            || IsMethodThreadSafetyLawPredicateIndexedFact(kind);
    }

    public static bool IsEnumVariantPayloadTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsBool
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsInteger
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsFloat
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsRawPointer
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsFixedArray
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsSlice
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsDynamic
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsClosure
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsDynTrait
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsNamed
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsStruct
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsRecord
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsEnum
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsTrait
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsDoctrine
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeHasConcreteLayout;
    }

    public static bool IsEnumVariantPayloadTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.EnumVariantPayloadTypeDisplayName
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeBaseName
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeModuleName
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeArgumentCount
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeComptimeArgumentCount
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeHasCSourceAlias
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeCSourceAliasName
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeHasQualifiers
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeBorrowKindIsNone
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeBorrowKindIsBorrow
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeBorrowKindIsRetBorrow
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeBorrowKindIsStoreBorrow
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeAccessKindIsNone
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeAccessKindIsShared
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeAccessKindIsFrozen
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeInitializationKindIsNone
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeInitializationKindIsOut
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeInitializationKindIsInit
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsMutableView
            or CompileTimeStructuralFactKind.EnumVariantPayloadTypeUnqualifiedTypeIs;
    }

    public static bool IsEnumVariantPayloadLayoutFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.EnumVariantPayloadOffset
            or CompileTimeStructuralFactKind.EnumVariantPayloadSize
            or CompileTimeStructuralFactKind.EnumVariantPayloadAlign
            or CompileTimeStructuralFactKind.EnumVariantPayloadIsMisaligned;
    }

    public static bool IsEnumTagLayoutFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.EnumTagOffset
            or CompileTimeStructuralFactKind.EnumTagSize
            or CompileTimeStructuralFactKind.EnumTagAlign
            or CompileTimeStructuralFactKind.EnumTagIsMisaligned;
    }

    public static bool IsAssociatedTypeTargetTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsBool
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsInteger
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsFloat
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsRawPointer
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsFixedArray
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsSlice
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsDynamic
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsClosure
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsDynTrait
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsNamed
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsStruct
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsRecord
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsEnum
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsTrait
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsDoctrine
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeHasConcreteLayout;
    }

    public static bool IsAssociatedTypeTargetTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.AssociatedTypeTargetTypeDisplayName
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeBaseName
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeModuleName
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeArgumentCount
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeComptimeArgumentCount;
    }

    public static bool IsTypeArgumentTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.TypeArgumentTypeIsBool
            or CompileTimeStructuralFactKind.TypeArgumentTypeIsInteger
            or CompileTimeStructuralFactKind.TypeArgumentTypeIsFloat
            or CompileTimeStructuralFactKind.TypeArgumentTypeIsRawPointer
            or CompileTimeStructuralFactKind.TypeArgumentTypeIsFixedArray
            or CompileTimeStructuralFactKind.TypeArgumentTypeIsSlice
            or CompileTimeStructuralFactKind.TypeArgumentTypeIsDynamic
            or CompileTimeStructuralFactKind.TypeArgumentTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.TypeArgumentTypeIsClosure
            or CompileTimeStructuralFactKind.TypeArgumentTypeIsDynTrait
            or CompileTimeStructuralFactKind.TypeArgumentTypeIsNamed
            or CompileTimeStructuralFactKind.TypeArgumentTypeIsStruct
            or CompileTimeStructuralFactKind.TypeArgumentTypeIsRecord
            or CompileTimeStructuralFactKind.TypeArgumentTypeIsEnum
            or CompileTimeStructuralFactKind.TypeArgumentTypeIsTrait
            or CompileTimeStructuralFactKind.TypeArgumentTypeIsDoctrine
            or CompileTimeStructuralFactKind.TypeArgumentTypeHasConcreteLayout;
    }

    public static bool IsTypeArgumentTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.TypeArgumentTypeDisplayName
            or CompileTimeStructuralFactKind.TypeArgumentTypeBaseName
            or CompileTimeStructuralFactKind.TypeArgumentTypeModuleName
            or CompileTimeStructuralFactKind.TypeArgumentTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.TypeArgumentTypeArgumentCount
            or CompileTimeStructuralFactKind.TypeArgumentTypeComptimeArgumentCount;
    }

    public static bool IsTypeComptimeGenericParameterTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsBool
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsInteger
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsFloat
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsRawPointer
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsFixedArray
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsSlice
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsDynamic
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsClosure
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsDynTrait
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsNamed
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsStruct
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsRecord
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsEnum
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsTrait
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsDoctrine
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeHasConcreteLayout;
    }

    public static bool IsTypeComptimeGenericParameterTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeDisplayName
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeBaseName
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeModuleName
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeArgumentCount
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeComptimeArgumentCount;
    }

    public static bool IsTypeComptimeArgumentTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsBool
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsInteger
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsFloat
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsRawPointer
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsFixedArray
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsSlice
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsDynamic
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsClosure
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsDynTrait
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsNamed
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsStruct
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsRecord
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsEnum
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsTrait
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsDoctrine
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeHasConcreteLayout;
    }

    public static bool IsTypeComptimeArgumentTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.TypeComptimeArgumentTypeDisplayName
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeBaseName
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeModuleName
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeArgumentCount
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeComptimeArgumentCount;
    }

    public static bool IsRawPointerElementTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.RawPointerElementTypeIsBool
            or CompileTimeStructuralFactKind.RawPointerElementTypeIsInteger
            or CompileTimeStructuralFactKind.RawPointerElementTypeIsFloat
            or CompileTimeStructuralFactKind.RawPointerElementTypeIsRawPointer
            or CompileTimeStructuralFactKind.RawPointerElementTypeIsFixedArray
            or CompileTimeStructuralFactKind.RawPointerElementTypeIsSlice
            or CompileTimeStructuralFactKind.RawPointerElementTypeIsDynamic
            or CompileTimeStructuralFactKind.RawPointerElementTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.RawPointerElementTypeIsClosure
            or CompileTimeStructuralFactKind.RawPointerElementTypeIsDynTrait
            or CompileTimeStructuralFactKind.RawPointerElementTypeIsNamed
            or CompileTimeStructuralFactKind.RawPointerElementTypeIsStruct
            or CompileTimeStructuralFactKind.RawPointerElementTypeIsRecord
            or CompileTimeStructuralFactKind.RawPointerElementTypeIsEnum
            or CompileTimeStructuralFactKind.RawPointerElementTypeIsTrait
            or CompileTimeStructuralFactKind.RawPointerElementTypeIsDoctrine
            or CompileTimeStructuralFactKind.RawPointerElementTypeHasConcreteLayout;
    }

    public static bool IsTypeElementTypePredicate(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.TypeElementTypeIsBool
            or CompileTimeStructuralFactKind.TypeElementTypeIsInteger
            or CompileTimeStructuralFactKind.TypeElementTypeIsFloat
            or CompileTimeStructuralFactKind.TypeElementTypeIsRawPointer
            or CompileTimeStructuralFactKind.TypeElementTypeIsFixedArray
            or CompileTimeStructuralFactKind.TypeElementTypeIsSlice
            or CompileTimeStructuralFactKind.TypeElementTypeIsDynamic
            or CompileTimeStructuralFactKind.TypeElementTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.TypeElementTypeIsClosure
            or CompileTimeStructuralFactKind.TypeElementTypeIsDynTrait
            or CompileTimeStructuralFactKind.TypeElementTypeIsNamed
            or CompileTimeStructuralFactKind.TypeElementTypeIsStruct
            or CompileTimeStructuralFactKind.TypeElementTypeIsRecord
            or CompileTimeStructuralFactKind.TypeElementTypeIsEnum
            or CompileTimeStructuralFactKind.TypeElementTypeIsTrait
            or CompileTimeStructuralFactKind.TypeElementTypeIsDoctrine
            or CompileTimeStructuralFactKind.TypeElementTypeHasConcreteLayout;
    }

    public static bool IsScalarTypeMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.TypeIntegerBitWidth
            or CompileTimeStructuralFactKind.TypeFloatBitWidth
            or CompileTimeStructuralFactKind.TypeIntegerIsSigned
            or CompileTimeStructuralFactKind.TypeIntegerIsUnsigned
            or CompileTimeStructuralFactKind.TypeIntegerIsFullRange
            or CompileTimeStructuralFactKind.TypeIntegerMinIs
            or CompileTimeStructuralFactKind.TypeIntegerMaxIs;
    }

    public static bool IsTypeQualifierMetadataFact(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.TypeHasQualifiers
            or CompileTimeStructuralFactKind.TypeBorrowKindIsNone
            or CompileTimeStructuralFactKind.TypeBorrowKindIsBorrow
            or CompileTimeStructuralFactKind.TypeBorrowKindIsRetBorrow
            or CompileTimeStructuralFactKind.TypeBorrowKindIsStoreBorrow
            or CompileTimeStructuralFactKind.TypeAccessKindIsNone
            or CompileTimeStructuralFactKind.TypeAccessKindIsShared
            or CompileTimeStructuralFactKind.TypeAccessKindIsFrozen
            or CompileTimeStructuralFactKind.TypeInitializationKindIsNone
            or CompileTimeStructuralFactKind.TypeInitializationKindIsOut
            or CompileTimeStructuralFactKind.TypeInitializationKindIsInit
            or CompileTimeStructuralFactKind.TypeIsMutableView
            or CompileTimeStructuralFactKind.TypeUnqualifiedTypeIs;
    }

    public static bool RequiresRawPointerTarget(CompileTimeStructuralFactKind kind)
    {
        return (kind is CompileTimeStructuralFactKind.RawPointerElementTypeIs
            or CompileTimeStructuralFactKind.RawPointerElementTypeHasCSourceAlias
            or CompileTimeStructuralFactKind.RawPointerElementTypeCSourceAliasName
            or CompileTimeStructuralFactKind.RawPointerIsMutable
            or CompileTimeStructuralFactKind.RawPointerIsReadOnly)
            || IsRawPointerElementTypePredicate(kind);
    }

    public static bool RequiresElementTypeTarget(CompileTimeStructuralFactKind kind)
    {
        return (kind is CompileTimeStructuralFactKind.TypeElementTypeIs
            or CompileTimeStructuralFactKind.TypeElementTypeHasCSourceAlias
            or CompileTimeStructuralFactKind.TypeElementTypeCSourceAliasName)
            || IsTypeElementTypePredicate(kind);
    }

    public static bool RequiresFixedArrayTarget(CompileTimeStructuralFactKind kind)
    {
        return kind is CompileTimeStructuralFactKind.TypeFixedArrayLength
            or CompileTimeStructuralFactKind.TypeFixedArrayLengthIs;
    }

    public static bool RequiresIntegerTarget(CompileTimeStructuralFactKind kind)
    {
        return kind == CompileTimeStructuralFactKind.TypeIntegerBitWidth;
    }

    public static bool RequiresFloatTarget(CompileTimeStructuralFactKind kind)
    {
        return kind == CompileTimeStructuralFactKind.TypeFloatBitWidth;
    }

    public static bool TryEvaluate(
        CompileTimeStructuralFactKind kind,
        CompileTimeStructuralFactArguments arguments,
        Func<StarkTypeSymbol, NamedTypeSymbol?> resolveNamedType,
        Func<StarkTypeSymbol, ConcreteTypeLayout?>? resolveConcreteLayout,
        Func<StarkTypeSymbol, StarkTypeSymbol, bool?>? resolveTraitConformance,
        Func<StarkTypeSymbol, IReadOnlyList<TypedFunctionSignature>>? resolveMethods,
        out CompileTimeConstant constant)
    {
        constant = default;
        if (IsTypeQualifierMetadataFact(kind))
        {
            return TryEvaluateTypeQualifierMetadataFact(kind, arguments, out constant);
        }

        var coreType = StarkTypeSymbols.WithQualifiers(
            arguments.TargetType,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
        var namedType = coreType.Kind == StarkTypeKind.Named
            ? resolveNamedType(coreType)
            : null;
        var namedTypeDefinition = ResolveNamedTypeDefinition(coreType, namedType, resolveNamedType);

        if (kind is CompileTimeStructuralFactKind.TypeSize
            or CompileTimeStructuralFactKind.TypeAlign
            or CompileTimeStructuralFactKind.TypeIsZeroSized)
        {
            if (resolveConcreteLayout?.Invoke(coreType) is not { } layout)
            {
                return false;
            }

            constant = kind switch
            {
                CompileTimeStructuralFactKind.TypeSize =>
                    CompileTimeConstant.Integer(layout.SizeBytes, CountType),
                CompileTimeStructuralFactKind.TypeAlign =>
                    CompileTimeConstant.Integer(layout.AlignmentBytes, CountType),
                CompileTimeStructuralFactKind.TypeIsZeroSized =>
                    CompileTimeConstant.Bool(layout.SizeBytes == 0),
                _ => constant
            };
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.TypeIntegerBitWidth
            or CompileTimeStructuralFactKind.TypeFloatBitWidth
            or CompileTimeStructuralFactKind.TypeIntegerIsSigned
            or CompileTimeStructuralFactKind.TypeIntegerIsUnsigned
            or CompileTimeStructuralFactKind.TypeIntegerIsFullRange
            or CompileTimeStructuralFactKind.TypeIntegerMinIs
            or CompileTimeStructuralFactKind.TypeIntegerMaxIs)
        {
            return TryEvaluateScalarTypeMetadataFact(kind, coreType, arguments, out constant);
        }

        if (kind == CompileTimeStructuralFactKind.RawPointerElementTypeIs
            || kind == CompileTimeStructuralFactKind.RawPointerIsMutable
            || kind == CompileTimeStructuralFactKind.RawPointerIsReadOnly
            || kind == CompileTimeStructuralFactKind.RawPointerElementTypeHasCSourceAlias
            || kind == CompileTimeStructuralFactKind.RawPointerElementTypeCSourceAliasName
            || IsRawPointerElementTypePredicate(kind))
        {
            return TryEvaluateRawPointerMetadataFact(
                kind,
                coreType,
                arguments,
                resolveNamedType,
                resolveConcreteLayout,
                out constant);
        }

        if (kind == CompileTimeStructuralFactKind.TypeElementTypeIs
            || kind == CompileTimeStructuralFactKind.TypeElementTypeHasCSourceAlias
            || kind == CompileTimeStructuralFactKind.TypeElementTypeCSourceAliasName
            || IsTypeElementTypePredicate(kind))
        {
            return TryEvaluateTypeElementMetadataFact(
                kind,
                coreType,
                arguments,
                resolveNamedType,
                resolveConcreteLayout,
                out constant);
        }

        if (kind is CompileTimeStructuralFactKind.TypeFixedArrayLength
            or CompileTimeStructuralFactKind.TypeFixedArrayLengthIs)
        {
            return TryEvaluateFixedArrayMetadataFact(kind, coreType, arguments, out constant);
        }

        if (kind == CompileTimeStructuralFactKind.Implements)
        {
            var traitType = arguments.AdditionalTypeArguments.Count == 1
                ? StarkTypeSymbols.WithQualifiers(
                    arguments.AdditionalTypeArguments[0],
                    borrowKind: StarkBorrowKind.None,
                    accessKind: StarkAccessKind.None,
                    initializationKind: StarkInitializationKind.None,
                    isMutableView: false)
                : StarkTypeSymbols.Error;
            if (resolveTraitConformance?.Invoke(coreType, traitType) is { } implements)
            {
                constant = CompileTimeConstant.Bool(implements);
                return true;
            }

            var traitSymbol = traitType.Kind == StarkTypeKind.Named
                ? resolveNamedType(traitType)
                : null;
            constant = CompileTimeConstant.Bool(
                namedType is not null
                && traitSymbol?.Kind == DeclarationKind.Trait
                && ImplementsTrait(namedType, traitType, traitSymbol));
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.ImplementedTraitCount)
        {
            var implementedTraits = GetImplementedTraitNames(namedType, namedTypeDefinition);
            constant = CompileTimeConstant.Integer(implementedTraits.Count, CountType);
            return true;
        }

        if (IsImplementedTraitIndexedFact(kind))
        {
            var implementedTraits = GetImplementedTraitNames(namedType, namedTypeDefinition);
            if (!TryGetConcreteIndex(arguments, out var implementedTraitIndex)
                || implementedTraitIndex >= implementedTraits.Count)
            {
                return false;
            }

            var implementedTrait = implementedTraits[implementedTraitIndex];
            var implementedTraitTypes = GetImplementedTraitTypes(namedType, namedTypeDefinition);
            var implementedTraitType = implementedTraitIndex < implementedTraitTypes.Count
                ? implementedTraitTypes[implementedTraitIndex]
                : null;
            var implementedTraitSymbol = ResolveImplementedTraitSymbol(implementedTrait, resolveNamedType);
            if (kind == CompileTimeStructuralFactKind.ImplementedTraitTypeIs)
            {
                var traitType = arguments.AdditionalTypeArguments.Count == 1
                    ? StarkTypeSymbols.WithQualifiers(
                        arguments.AdditionalTypeArguments[0],
                        borrowKind: StarkBorrowKind.None,
                        accessKind: StarkAccessKind.None,
                        initializationKind: StarkInitializationKind.None,
                        isMutableView: false)
                    : StarkTypeSymbols.Error;
                var targetTraitSymbol = traitType.Kind == StarkTypeKind.Named
                    ? resolveNamedType(traitType)
                    : null;
                constant = CompileTimeConstant.Bool(
                    targetTraitSymbol?.Kind == DeclarationKind.Trait
                    && ImplementedTraitNameMatches(implementedTrait, traitType, targetTraitSymbol));
                return true;
            }

            if (kind == CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIs
                || IsImplementedTraitTypeArgumentTypePredicate(kind)
                || IsImplementedTraitTypeArgumentTypeMetadataFact(kind))
            {
                if (implementedTraitType?.TypeArguments is not { } traitTypeArguments
                    || !TryGetConcreteIndex(arguments, position: 1, out var typeArgumentIndex)
                    || typeArgumentIndex >= traitTypeArguments.Count)
                {
                    return false;
                }

                var typeArgument = traitTypeArguments[typeArgumentIndex];
                constant = kind switch
                {
                    CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIs =>
                        CompileTimeConstant.Bool(
                            arguments.AdditionalTypeArguments.Count == 1
                            && TypesEquivalent(typeArgument, arguments.AdditionalTypeArguments[0])),
                    _ when IsImplementedTraitTypeArgumentTypePredicate(kind) =>
                        CompileTimeConstant.Bool(
                            EvaluateTypePredicate(
                                GetTypePredicate(kind),
                                typeArgument,
                                resolveNamedType,
                                resolveConcreteLayout)),
                    _ when IsImplementedTraitTypeArgumentTypeMetadataFact(kind) =>
                        EvaluateNestedTypeMetadataFact(kind, typeArgument, arguments: null, resolveNamedType),
                    _ => constant
                };
                return true;
            }

            if (kind is CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentName
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIs
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentValueIs
                || IsImplementedTraitComptimeArgumentTypePredicate(kind)
                || IsImplementedTraitComptimeArgumentTypeMetadataFact(kind))
            {
                if (implementedTraitType?.ComptimeValueArguments is not { } valueArguments
                    || !TryGetConcreteIndex(arguments, position: 1, out var comptimeArgumentIndex)
                    || comptimeArgumentIndex >= valueArguments.Count)
                {
                    return false;
                }

                var valueArgument = valueArguments[comptimeArgumentIndex];
                constant = kind switch
                {
                    CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentName =>
                        TextConstant(valueArgument.ParameterName),
                    CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIs =>
                        CompileTimeConstant.Bool(
                            arguments.AdditionalTypeArguments.Count == 1
                            && TypesEquivalent(valueArgument.Type, arguments.AdditionalTypeArguments[0])),
                    _ when IsImplementedTraitComptimeArgumentTypePredicate(kind) =>
                        CompileTimeConstant.Bool(
                            EvaluateTypePredicate(
                                GetTypePredicate(kind),
                                valueArgument.Type,
                                resolveNamedType,
                                resolveConcreteLayout)),
                    _ when IsImplementedTraitComptimeArgumentTypeMetadataFact(kind) =>
                        EvaluateNestedTypeMetadataFact(kind, valueArgument.Type, arguments: null, resolveNamedType),
                    CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentValueIs =>
                        CompileTimeConstant.Bool(
                            !valueArgument.IsSymbolic
                            && arguments.ComptimeValueArguments.Count == 3
                            && !arguments.ComptimeValueArguments[2].IsSymbolic
                            && valueArgument.IntegerValue == arguments.ComptimeValueArguments[2].IntegerValue),
                    _ => constant
                };
                return true;
            }

            if (IsImplementedTraitTypePredicate(kind))
            {
                var predicateType = implementedTraitType ?? StarkTypeSymbols.Named(StarkTypeSymbols.GetGenericBaseName(implementedTrait));
                constant = CompileTimeConstant.Bool(
                    EvaluateTypePredicate(
                        GetTypePredicate(kind),
                        predicateType,
                        type => ResolveImplementedTraitPredicateType(type, implementedTraitSymbol, resolveNamedType),
                        resolveConcreteLayout));
                return true;
            }

            constant = EvaluateImplementedTraitMetadataFact(kind, implementedTrait, implementedTraitSymbol);
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.AssociatedTypeCount)
        {
            constant = CompileTimeConstant.Integer(namedTypeDefinition?.AssociatedTypes.Count ?? 0, CountType);
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.TypeGenericParameterCount
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterCount)
        {
            var count = kind == CompileTimeStructuralFactKind.TypeGenericParameterCount
                ? namedTypeDefinition?.GenericParams.Count ?? 0
                : namedTypeDefinition?.ComptimeGenericParams.Count ?? 0;
            constant = CompileTimeConstant.Integer(count, CountType);
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.TypeGenericParameterName
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterName
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIs
            || IsTypeComptimeGenericParameterTypePredicate(kind)
            || IsTypeComptimeGenericParameterTypeMetadataFact(kind))
        {
            if (namedTypeDefinition is null
                || !TryGetConcreteIndex(arguments, out var parameterIndex))
            {
                return false;
            }

            if (kind == CompileTimeStructuralFactKind.TypeGenericParameterName)
            {
                if (parameterIndex >= namedTypeDefinition.GenericParams.Count)
                {
                    return false;
                }

                constant = TextConstant(namedTypeDefinition.GenericParams[parameterIndex]);
                return true;
            }

            if (parameterIndex >= namedTypeDefinition.ComptimeGenericParams.Count)
            {
                return false;
            }

            var parameter = namedTypeDefinition.ComptimeGenericParams[parameterIndex];
            var parameterType = SubstituteOwnerGenericType(namedTypeDefinition, coreType, parameter.Type);
            constant = kind switch
            {
                CompileTimeStructuralFactKind.TypeComptimeGenericParameterName =>
                    TextConstant(parameter.Name),
                CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIs =>
                    CompileTimeConstant.Bool(
                        arguments.AdditionalTypeArguments.Count == 1
                        && TypesEquivalent(parameterType, arguments.AdditionalTypeArguments[0])),
                _ when IsTypeComptimeGenericParameterTypePredicate(kind) =>
                    CompileTimeConstant.Bool(
                        EvaluateTypePredicate(
                            GetTypePredicate(kind),
                            parameterType,
                            resolveNamedType,
                            resolveConcreteLayout)),
                _ when IsTypeComptimeGenericParameterTypeMetadataFact(kind) =>
                    EvaluateNestedTypeMetadataFact(kind, parameterType, arguments: null, resolveNamedType),
                _ => constant
            };
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.TypeDisplayName
            or CompileTimeStructuralFactKind.TypeBaseName
            or CompileTimeStructuralFactKind.TypeModuleName)
        {
            var text = kind switch
            {
                CompileTimeStructuralFactKind.TypeDisplayName => coreType.DisplayName,
                CompileTimeStructuralFactKind.TypeBaseName =>
                    coreType.Kind == StarkTypeKind.Named && coreType.NamedType is { } namedTypeName
                        ? StarkTypeSymbols.GetGenericBaseName(namedTypeName)
                        : string.Empty,
                CompileTimeStructuralFactKind.TypeModuleName =>
                    namedTypeDefinition?.DeclaringModuleName
                    ?? namedType?.DeclaringModuleName
                    ?? GetModuleName(namedTypeDefinition?.Name ?? namedType?.Name ?? coreType.NamedType),
                _ => string.Empty
            };
            constant = TextConstant(text);
            return true;
        }

        if (IsTypeVisibilityFact(kind))
        {
            constant = CompileTimeConstant.Bool(
                (namedTypeDefinition ?? namedType) is { } symbol
                && VisibilityMatchesFact(kind, symbol.Visibility));
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.TypeHasCSourceAlias
            or CompileTimeStructuralFactKind.TypeCSourceAliasName)
        {
            constant = EvaluateCSourceAliasMetadataFact(
                hasAliasFact: kind == CompileTimeStructuralFactKind.TypeHasCSourceAlias,
                coreType);
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.TypeIsGenericInstantiation)
        {
            constant = CompileTimeConstant.Bool(StarkTypeSymbols.IsGenericInstantiation(coreType));
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.TypeArgumentCount
            or CompileTimeStructuralFactKind.TypeComptimeArgumentCount)
        {
            var count = kind == CompileTimeStructuralFactKind.TypeArgumentCount
                ? coreType.TypeArguments?.Count ?? 0
                : coreType.ComptimeValueArguments?.Count ?? 0;
            constant = CompileTimeConstant.Integer(count, CountType);
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.TypeArgumentTypeIs
            || IsTypeArgumentTypePredicate(kind)
            || IsTypeArgumentTypeMetadataFact(kind))
        {
            if (coreType.Kind != StarkTypeKind.Named
                || coreType.TypeArguments is not { } typeArgumentsForTarget
                || !TryGetConcreteIndex(arguments, out var typeArgumentIndex)
                || typeArgumentIndex >= typeArgumentsForTarget.Count)
            {
                return false;
            }

            var typeArgument = typeArgumentsForTarget[typeArgumentIndex];
            constant = kind switch
            {
                CompileTimeStructuralFactKind.TypeArgumentTypeIs =>
                    CompileTimeConstant.Bool(
                        arguments.AdditionalTypeArguments.Count == 1
                        && TypesEquivalent(typeArgument, arguments.AdditionalTypeArguments[0])),
                _ when IsTypeArgumentTypePredicate(kind) =>
                    CompileTimeConstant.Bool(
                        EvaluateTypePredicate(
                            GetTypePredicate(kind),
                            typeArgument,
                            resolveNamedType,
                            resolveConcreteLayout)),
                _ when IsTypeArgumentTypeMetadataFact(kind) =>
                    EvaluateNestedTypeMetadataFact(kind, typeArgument, arguments: null, resolveNamedType),
                _ => constant
            };
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.TypeComptimeArgumentName
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIs
            or CompileTimeStructuralFactKind.TypeComptimeArgumentValueIs
            || IsTypeComptimeArgumentTypePredicate(kind)
            || IsTypeComptimeArgumentTypeMetadataFact(kind))
        {
            if (coreType.Kind != StarkTypeKind.Named
                || coreType.ComptimeValueArguments is not { } valueArgumentsForTarget
                || !TryGetConcreteIndex(arguments, out var comptimeArgumentIndex)
                || comptimeArgumentIndex >= valueArgumentsForTarget.Count)
            {
                return false;
            }

            var valueArgument = valueArgumentsForTarget[comptimeArgumentIndex];
            constant = kind switch
            {
                CompileTimeStructuralFactKind.TypeComptimeArgumentName =>
                    TextConstant(valueArgument.ParameterName),
                CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIs =>
                    CompileTimeConstant.Bool(
                        arguments.AdditionalTypeArguments.Count == 1
                        && TypesEquivalent(valueArgument.Type, arguments.AdditionalTypeArguments[0])),
                _ when IsTypeComptimeArgumentTypePredicate(kind) =>
                    CompileTimeConstant.Bool(
                        EvaluateTypePredicate(
                            GetTypePredicate(kind),
                            valueArgument.Type,
                            resolveNamedType,
                            resolveConcreteLayout)),
                _ when IsTypeComptimeArgumentTypeMetadataFact(kind) =>
                    EvaluateNestedTypeMetadataFact(kind, valueArgument.Type, arguments: null, resolveNamedType),
                CompileTimeStructuralFactKind.TypeComptimeArgumentValueIs =>
                    CompileTimeConstant.Bool(
                        !valueArgument.IsSymbolic
                        && arguments.ComptimeValueArguments.Count == 2
                        && !arguments.ComptimeValueArguments[1].IsSymbolic
                        && valueArgument.IntegerValue == arguments.ComptimeValueArguments[1].IntegerValue),
                _ => constant
            };
            return true;
        }

        if ((kind is CompileTimeStructuralFactKind.AssociatedTypeName
            or CompileTimeStructuralFactKind.AssociatedTypeHasTarget
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIs)
            || IsAssociatedTypeTargetTypePredicate(kind)
            || IsAssociatedTypeTargetTypeMetadataFact(kind))
        {
            if (namedTypeDefinition is null
                || !TryGetConcreteIndex(arguments, out var associatedTypeIndex))
            {
                return false;
            }

            var associatedTypes = GetOrderedAssociatedTypes(namedTypeDefinition);
            if (associatedTypeIndex >= associatedTypes.Count)
            {
                return false;
            }

            var associatedType = associatedTypes[associatedTypeIndex];
            var associatedTypeTarget = associatedType.TargetType is not null
                ? SubstituteOwnerGenericType(namedTypeDefinition, coreType, associatedType.TargetType)
                : null;
            if (IsAssociatedTypeTargetTypePredicate(kind))
            {
                constant = CompileTimeConstant.Bool(
                    associatedTypeTarget is not null
                    && EvaluateTypePredicate(
                        GetTypePredicate(kind),
                        associatedTypeTarget,
                        resolveNamedType,
                        resolveConcreteLayout));
                return true;
            }

            if (IsAssociatedTypeTargetTypeMetadataFact(kind))
            {
                if (associatedTypeTarget is null)
                {
                    return TryCreateDefaultConstant(kind, out constant);
                }

                constant = EvaluateNestedTypeMetadataFact(kind, associatedTypeTarget, arguments: null, resolveNamedType);
                return true;
            }

            constant = kind switch
            {
                CompileTimeStructuralFactKind.AssociatedTypeName =>
                    TextConstant(associatedType.Name),
                CompileTimeStructuralFactKind.AssociatedTypeHasTarget =>
                    CompileTimeConstant.Bool(associatedType.TargetType is not null),
                CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIs =>
                    CompileTimeConstant.Bool(
                        associatedTypeTarget is not null
                        && arguments.AdditionalTypeArguments.Count == 1
                        && TypesEquivalent(associatedTypeTarget, arguments.AdditionalTypeArguments[0])),
                _ => constant
            };
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.FieldCount or CompileTimeStructuralFactKind.EnumVariantCount)
        {
            var count = kind switch
            {
                CompileTimeStructuralFactKind.FieldCount
                    when namedType?.Kind is DeclarationKind.Struct or DeclarationKind.Record => namedType.OrderedFields.Count,
                CompileTimeStructuralFactKind.EnumVariantCount
                    when namedType?.Kind == DeclarationKind.Enum => namedType.Variants.Count,
                _ => 0
            };
            constant = CompileTimeConstant.Integer(count, CountType);
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.FunctionPointerParameterCount)
        {
            constant = CompileTimeConstant.Integer(
                coreType.Kind == StarkTypeKind.FunctionPointer
                    ? coreType.FunctionPointerParameterTypes?.Count ?? 0
                    : 0,
                CountType);
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.FunctionPointerReturnTypeIs)
        {
            constant = CompileTimeConstant.Bool(
                coreType.Kind == StarkTypeKind.FunctionPointer
                && coreType.FunctionPointerReturnType is { } returnType
                && arguments.AdditionalTypeArguments.Count == 1
                && TypesEquivalent(returnType, arguments.AdditionalTypeArguments[0]));
            return true;
        }

        if (IsFunctionPointerReturnTypePredicate(kind))
        {
            constant = CompileTimeConstant.Bool(
                coreType.Kind == StarkTypeKind.FunctionPointer
                && coreType.FunctionPointerReturnType is { } returnType
                && EvaluateTypePredicate(
                    GetTypePredicate(kind),
                    returnType,
                    resolveNamedType,
                    resolveConcreteLayout));
            return true;
        }

        if (IsFunctionPointerReturnTypeMetadataFact(kind))
        {
            if (coreType.Kind != StarkTypeKind.FunctionPointer
                || coreType.FunctionPointerReturnType is not { } returnType)
            {
                return false;
            }

            constant = EvaluateNestedTypeMetadataFact(kind, returnType, arguments, resolveNamedType);
            return true;
        }

        if (IsFunctionPointerReturnTypeArgumentFact(kind))
        {
            if (coreType.Kind != StarkTypeKind.FunctionPointer
                || coreType.FunctionPointerReturnType is not { } returnType
                || !TryGetConcreteIndex(arguments, position: 0, out var argumentIndex))
            {
                return false;
            }

            return TryEvaluateCallableNestedTypeArgumentFact(
                kind,
                returnType,
                argumentIndex,
                arguments,
                resolveNamedType,
                resolveConcreteLayout,
                out constant);
        }

        if (kind == CompileTimeStructuralFactKind.FunctionPointerParameterTypeIs)
        {
            if (coreType.Kind != StarkTypeKind.FunctionPointer
                || coreType.FunctionPointerParameterTypes is not { } parameterTypes
                || arguments.AdditionalTypeArguments.Count != 1
                || !TryGetConcreteIndex(arguments, out var parameterIndex)
                || parameterIndex >= parameterTypes.Count)
            {
                return false;
            }

            constant = CompileTimeConstant.Bool(
                TypesEquivalent(parameterTypes[parameterIndex], arguments.AdditionalTypeArguments[0]));
            return true;
        }

        if (IsFunctionPointerParameterTypePredicate(kind))
        {
            if (coreType.Kind != StarkTypeKind.FunctionPointer
                || coreType.FunctionPointerParameterTypes is not { } parameterTypes
                || !TryGetConcreteIndex(arguments, out var parameterIndex)
                || parameterIndex >= parameterTypes.Count)
            {
                return false;
            }

            constant = CompileTimeConstant.Bool(
                EvaluateTypePredicate(
                    GetTypePredicate(kind),
                    parameterTypes[parameterIndex],
                    resolveNamedType,
                    resolveConcreteLayout));
            return true;
        }

        if (IsFunctionPointerParameterTypeMetadataFact(kind))
        {
            if (coreType.Kind != StarkTypeKind.FunctionPointer
                || coreType.FunctionPointerParameterTypes is not { } parameterTypes
                || !TryGetConcreteIndex(arguments, out var parameterIndex)
                || parameterIndex >= parameterTypes.Count)
            {
                return false;
            }

            constant = EvaluateNestedTypeMetadataFact(kind, parameterTypes[parameterIndex], arguments, resolveNamedType);
            return true;
        }

        if (IsFunctionPointerParameterTypeArgumentFact(kind))
        {
            if (coreType.Kind != StarkTypeKind.FunctionPointer
                || coreType.FunctionPointerParameterTypes is not { } parameterTypes
                || !TryGetConcreteIndex(arguments, position: 0, out var parameterIndex)
                || !TryGetConcreteIndex(arguments, position: 1, out var argumentIndex)
                || parameterIndex >= parameterTypes.Count)
            {
                return false;
            }

            return TryEvaluateCallableNestedTypeArgumentFact(
                kind,
                parameterTypes[parameterIndex],
                argumentIndex,
                arguments,
                resolveNamedType,
                resolveConcreteLayout,
                out constant);
        }

        if (IsFunctionPointerParameterRawPointerElementCountExpressionFact(kind))
        {
            if (coreType.Kind != StarkTypeKind.FunctionPointer
                || coreType.FunctionPointerParameterTypes is not { } parameterTypes
                || !TryGetConcreteIndex(arguments, out var parameterIndex)
                || parameterIndex >= parameterTypes.Count)
            {
                return false;
            }

            constant = EvaluateRawPointerElementCountExpressionFact(
                kind,
                StarkTypeSymbols.GetFunctionPointerParameterRawPointerElementCountExpression(
                    coreType,
                    parameterIndex));
            return true;
        }

        if (IsFunctionPointerParameterMemoryFact(kind))
        {
            if (coreType.Kind != StarkTypeKind.FunctionPointer
                || coreType.FunctionPointerParameterTypes is not { } parameterTypes
                || !TryGetConcreteIndex(arguments, position: 0, out var leftIndex)
                || !TryGetConcreteIndex(arguments, position: 1, out var rightIndex)
                || leftIndex >= parameterTypes.Count
                || rightIndex >= parameterTypes.Count)
            {
                return false;
            }

            constant = CompileTimeConstant.Bool(
                EvaluateFunctionPointerParameterMemoryFact(kind, coreType, leftIndex, rightIndex));
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.ClosureParameterCount)
        {
            constant = CompileTimeConstant.Integer(
                coreType.Kind == StarkTypeKind.Closure
                    ? coreType.ClosureParameterTypes?.Count ?? 0
                    : 0,
                CountType);
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.ClosureReturnTypeIs)
        {
            constant = CompileTimeConstant.Bool(
                coreType.Kind == StarkTypeKind.Closure
                && coreType.ClosureReturnType is { } returnType
                && arguments.AdditionalTypeArguments.Count == 1
                && TypesEquivalent(returnType, arguments.AdditionalTypeArguments[0]));
            return true;
        }

        if (IsClosureReturnTypePredicate(kind))
        {
            constant = CompileTimeConstant.Bool(
                coreType.Kind == StarkTypeKind.Closure
                && coreType.ClosureReturnType is { } returnType
                && EvaluateTypePredicate(
                    GetTypePredicate(kind),
                    returnType,
                    resolveNamedType,
                    resolveConcreteLayout));
            return true;
        }

        if (IsClosureReturnTypeMetadataFact(kind))
        {
            if (coreType.Kind != StarkTypeKind.Closure
                || coreType.ClosureReturnType is not { } returnType)
            {
                return false;
            }

            constant = EvaluateNestedTypeMetadataFact(kind, returnType, arguments, resolveNamedType);
            return true;
        }

        if (IsClosureReturnTypeArgumentFact(kind))
        {
            if (coreType.Kind != StarkTypeKind.Closure
                || coreType.ClosureReturnType is not { } returnType
                || !TryGetConcreteIndex(arguments, position: 0, out var argumentIndex))
            {
                return false;
            }

            return TryEvaluateCallableNestedTypeArgumentFact(
                kind,
                returnType,
                argumentIndex,
                arguments,
                resolveNamedType,
                resolveConcreteLayout,
                out constant);
        }

        if (kind == CompileTimeStructuralFactKind.ClosureParameterTypeIs)
        {
            if (coreType.Kind != StarkTypeKind.Closure
                || coreType.ClosureParameterTypes is not { } parameterTypes
                || arguments.AdditionalTypeArguments.Count != 1
                || !TryGetConcreteIndex(arguments, out var parameterIndex)
                || parameterIndex >= parameterTypes.Count)
            {
                return false;
            }

            constant = CompileTimeConstant.Bool(
                TypesEquivalent(parameterTypes[parameterIndex], arguments.AdditionalTypeArguments[0]));
            return true;
        }

        if (IsClosureParameterTypePredicate(kind))
        {
            if (coreType.Kind != StarkTypeKind.Closure
                || coreType.ClosureParameterTypes is not { } parameterTypes
                || !TryGetConcreteIndex(arguments, out var parameterIndex)
                || parameterIndex >= parameterTypes.Count)
            {
                return false;
            }

            constant = CompileTimeConstant.Bool(
                EvaluateTypePredicate(
                    GetTypePredicate(kind),
                    parameterTypes[parameterIndex],
                    resolveNamedType,
                    resolveConcreteLayout));
            return true;
        }

        if (IsClosureParameterTypeMetadataFact(kind))
        {
            if (coreType.Kind != StarkTypeKind.Closure
                || coreType.ClosureParameterTypes is not { } parameterTypes
                || !TryGetConcreteIndex(arguments, out var parameterIndex)
                || parameterIndex >= parameterTypes.Count)
            {
                return false;
            }

            constant = EvaluateNestedTypeMetadataFact(kind, parameterTypes[parameterIndex], arguments, resolveNamedType);
            return true;
        }

        if (IsClosureParameterTypeArgumentFact(kind))
        {
            if (coreType.Kind != StarkTypeKind.Closure
                || coreType.ClosureParameterTypes is not { } parameterTypes
                || !TryGetConcreteIndex(arguments, position: 0, out var parameterIndex)
                || !TryGetConcreteIndex(arguments, position: 1, out var argumentIndex)
                || parameterIndex >= parameterTypes.Count)
            {
                return false;
            }

            return TryEvaluateCallableNestedTypeArgumentFact(
                kind,
                parameterTypes[parameterIndex],
                argumentIndex,
                arguments,
                resolveNamedType,
                resolveConcreteLayout,
                out constant);
        }

        if (IsClosureParameterRawPointerElementCountExpressionFact(kind))
        {
            if (coreType.Kind != StarkTypeKind.Closure
                || coreType.ClosureParameterTypes is not { } parameterTypes
                || !TryGetConcreteIndex(arguments, out var parameterIndex)
                || parameterIndex >= parameterTypes.Count)
            {
                return false;
            }

            constant = EvaluateRawPointerElementCountExpressionFact(
                kind,
                StarkTypeSymbols.GetClosureParameterRawPointerElementCountExpression(
                    coreType,
                    parameterIndex));
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.ClosureKindIsFn
            or CompileTimeStructuralFactKind.ClosureKindIsFinite
            or CompileTimeStructuralFactKind.ClosureKindIsLaw
            or CompileTimeStructuralFactKind.ClosureKindIsFiniteLaw)
        {
            var functionKind = coreType.Kind == StarkTypeKind.Closure
                ? coreType.ClosureFunctionKind
                : null;
            constant = CompileTimeConstant.Bool(kind switch
            {
                CompileTimeStructuralFactKind.ClosureKindIsFn => functionKind == StarkFunctionKind.Fn,
                CompileTimeStructuralFactKind.ClosureKindIsFinite => functionKind == StarkFunctionKind.Finite,
                CompileTimeStructuralFactKind.ClosureKindIsLaw => functionKind == StarkFunctionKind.Law,
                CompileTimeStructuralFactKind.ClosureKindIsFiniteLaw => functionKind == StarkFunctionKind.FiniteLaw,
                _ => false
            });
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.ClosureStorageIsBorrow
            or CompileTimeStructuralFactKind.ClosureStorageIsHeap
            or CompileTimeStructuralFactKind.ClosureStorageIsInline)
        {
            var storageKind = coreType.Kind == StarkTypeKind.Closure
                ? coreType.ClosureStorageKind
                : StarkClosureStorageKind.Unspecified;
            constant = CompileTimeConstant.Bool(
                coreType.Kind == StarkTypeKind.Closure
                && (kind switch
                {
                    CompileTimeStructuralFactKind.ClosureStorageIsBorrow =>
                        storageKind == StarkClosureStorageKind.Unspecified,
                    CompileTimeStructuralFactKind.ClosureStorageIsHeap =>
                        storageKind == StarkClosureStorageKind.Heap,
                    CompileTimeStructuralFactKind.ClosureStorageIsInline =>
                        storageKind == StarkClosureStorageKind.Inline,
                    _ => false
                }));
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.ClosureCallCapabilityIsNormal
            or CompileTimeStructuralFactKind.ClosureCallCapabilityIsMut
            or CompileTimeStructuralFactKind.ClosureCallCapabilityIsOnce)
        {
            var callCapability = coreType.Kind == StarkTypeKind.Closure
                ? coreType.ClosureCallCapability
                : StarkClosureCallCapability.None;
            constant = CompileTimeConstant.Bool(
                coreType.Kind == StarkTypeKind.Closure
                && (kind switch
                {
                    CompileTimeStructuralFactKind.ClosureCallCapabilityIsNormal =>
                        callCapability == StarkClosureCallCapability.None,
                    CompileTimeStructuralFactKind.ClosureCallCapabilityIsMut =>
                        callCapability == StarkClosureCallCapability.Mut,
                    CompileTimeStructuralFactKind.ClosureCallCapabilityIsOnce =>
                        callCapability == StarkClosureCallCapability.Once,
                    _ => false
                }));
            return true;
        }

        if (IsClosureParameterMemoryFact(kind))
        {
            if (coreType.Kind != StarkTypeKind.Closure
                || coreType.ClosureParameterTypes is not { } parameterTypes
                || !TryGetConcreteIndex(arguments, position: 0, out var leftIndex)
                || !TryGetConcreteIndex(arguments, position: 1, out var rightIndex)
                || leftIndex >= parameterTypes.Count
                || rightIndex >= parameterTypes.Count)
            {
                return false;
            }

            constant = CompileTimeConstant.Bool(
                EvaluateClosureParameterMemoryFact(kind, coreType, leftIndex, rightIndex));
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.DynTraitIsView
            or CompileTimeStructuralFactKind.DynTraitIsHeap)
        {
            constant = CompileTimeConstant.Bool(
                coreType.Kind == StarkTypeKind.DynTrait
                && kind switch
                {
                    CompileTimeStructuralFactKind.DynTraitIsView =>
                        coreType.DynTraitStorageKind == StarkDynTraitStorageKind.View,
                    CompileTimeStructuralFactKind.DynTraitIsHeap =>
                        coreType.DynTraitStorageKind == StarkDynTraitStorageKind.Heap,
                    _ => false
                });
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.DynTraitTargetTypeIs)
        {
            if (coreType.Kind != StarkTypeKind.DynTrait
                || coreType.DynTraitName is null
                || arguments.AdditionalTypeArguments.Count != 1)
            {
                return false;
            }

            constant = CompileTimeConstant.Bool(
                TypesEquivalent(
                    BuildDynTraitTargetType(coreType),
                    arguments.AdditionalTypeArguments[0]));
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.MethodCount)
        {
            constant = CompileTimeConstant.Integer(
                namedTypeDefinition is not null
                    ? GetOrderedMethodSignatures(coreType, namedTypeDefinition, resolveMethods?.Invoke(coreType) ?? []).Count
                    : 0,
                CountType);
            return true;
        }

        if (IsMethodIndexedFact(kind))
        {
            if (namedTypeDefinition is null
                || !TryGetConcreteIndex(arguments, out var methodIndex))
            {
                return false;
            }

            var methods = GetOrderedMethodSignatures(coreType, namedTypeDefinition, resolveMethods?.Invoke(coreType) ?? []);
            if (methodIndex >= methods.Count)
            {
                return false;
            }

            var method = methods[methodIndex];
            if (kind == CompileTimeStructuralFactKind.MethodName)
            {
                constant = TextConstant(GetMethodMemberName(namedTypeDefinition, method));
                return true;
            }

            if (kind == CompileTimeStructuralFactKind.MethodModuleName)
            {
                // Root-module owners are keyed without a module prefix; the
                // owner definition's declaring module is authoritative.
                constant = TextConstant(
                    namedTypeDefinition.DeclaringModuleName ?? GetMethodModuleName(method));
                return true;
            }

            if (IsMethodVisibilityFact(kind))
            {
                constant = CompileTimeConstant.Bool(VisibilityMatchesFact(kind, method.Visibility));
                return true;
            }

            if (kind == CompileTimeStructuralFactKind.MethodParameterCount)
            {
                constant = CompileTimeConstant.Integer(method.Parameters.Count, CountType);
                return true;
            }

            if (kind == CompileTimeStructuralFactKind.MethodParameterName)
            {
                if (!TryGetConcreteIndex(arguments, position: 1, out var parameterIndex)
                    || parameterIndex >= method.Parameters.Count)
                {
                    return false;
                }

                constant = TextConstant(method.Parameters[parameterIndex].Name);
                return true;
            }

            if (kind == CompileTimeStructuralFactKind.MethodReturnTypeIs)
            {
                constant = CompileTimeConstant.Bool(
                    arguments.AdditionalTypeArguments.Count == 1
                    && TypesEquivalent(
                        SubstituteOwnerGenericType(namedTypeDefinition, coreType, method.ReturnType),
                        arguments.AdditionalTypeArguments[0]));
                return true;
            }

            if (IsMethodReturnTypePredicate(kind))
            {
                constant = CompileTimeConstant.Bool(
                    EvaluateTypePredicate(
                        GetTypePredicate(kind),
                        SubstituteOwnerGenericType(namedTypeDefinition, coreType, method.ReturnType),
                        resolveNamedType,
                        resolveConcreteLayout));
                return true;
            }

            if (IsMethodReturnTypeMetadataFact(kind))
            {
                constant = EvaluateNestedTypeMetadataFact(
                    kind,
                    SubstituteOwnerGenericType(namedTypeDefinition, coreType, method.ReturnType),
                    arguments,
                    resolveNamedType);
                return true;
            }

            if (IsMethodReturnTypeArgumentFact(kind))
            {
                if (!TryGetConcreteIndex(arguments, position: 1, out var argumentIndex))
                {
                    return false;
                }

                return TryEvaluateCallableNestedTypeArgumentFact(
                    kind,
                    SubstituteOwnerGenericType(namedTypeDefinition, coreType, method.ReturnType),
                    argumentIndex,
                    arguments,
                    resolveNamedType,
                    resolveConcreteLayout,
                    out constant);
            }

            if (kind == CompileTimeStructuralFactKind.MethodParameterTypeIs
                || IsMethodParameterTypePredicate(kind))
            {
                if (!TryGetConcreteIndex(arguments, position: 1, out var parameterIndex)
                    || parameterIndex >= method.Parameters.Count)
                {
                    return false;
                }

                var parameterType = SubstituteOwnerGenericType(
                    namedTypeDefinition,
                    coreType,
                    method.Parameters[parameterIndex].Type);
                constant = kind == CompileTimeStructuralFactKind.MethodParameterTypeIs
                    ? CompileTimeConstant.Bool(
                        arguments.AdditionalTypeArguments.Count == 1
                        && TypesEquivalent(parameterType, arguments.AdditionalTypeArguments[0]))
                    : CompileTimeConstant.Bool(
                        EvaluateTypePredicate(
                            GetTypePredicate(kind),
                            parameterType,
                            resolveNamedType,
                            resolveConcreteLayout));
                return true;
            }

            if (IsMethodParameterTypeMetadataFact(kind))
            {
                if (!TryGetConcreteIndex(arguments, position: 1, out var parameterIndex)
                    || parameterIndex >= method.Parameters.Count)
                {
                    return false;
                }

                constant = EvaluateNestedTypeMetadataFact(
                    kind,
                    SubstituteOwnerGenericType(namedTypeDefinition, coreType, method.Parameters[parameterIndex].Type),
                    arguments,
                    resolveNamedType);
                return true;
            }

            if (IsMethodParameterTypeArgumentFact(kind))
            {
                if (!TryGetConcreteIndex(arguments, position: 1, out var parameterIndex)
                    || !TryGetConcreteIndex(arguments, position: 2, out var argumentIndex)
                    || parameterIndex >= method.Parameters.Count)
                {
                    return false;
                }

                return TryEvaluateCallableNestedTypeArgumentFact(
                    kind,
                    SubstituteOwnerGenericType(namedTypeDefinition, coreType, method.Parameters[parameterIndex].Type),
                    argumentIndex,
                    arguments,
                    resolveNamedType,
                    resolveConcreteLayout,
                    out constant);
            }

            if (IsMethodParameterRawPointerElementCountExpressionFact(kind))
            {
                if (!TryGetConcreteIndex(arguments, position: 1, out var parameterIndex)
                    || parameterIndex >= method.Parameters.Count)
                {
                    return false;
                }

                constant = EvaluateRawPointerElementCountExpressionFact(
                    kind,
                    method.Parameters[parameterIndex].RawPointerElementCountExpression);
                return true;
            }

            if (kind is CompileTimeStructuralFactKind.MethodKindIsFn
                or CompileTimeStructuralFactKind.MethodKindIsFinite
                or CompileTimeStructuralFactKind.MethodKindIsLaw
                or CompileTimeStructuralFactKind.MethodKindIsFiniteLaw)
            {
                constant = CompileTimeConstant.Bool(kind switch
                {
                    CompileTimeStructuralFactKind.MethodKindIsFn => method.Kind == StarkFunctionKind.Fn,
                    CompileTimeStructuralFactKind.MethodKindIsFinite => method.Kind == StarkFunctionKind.Finite,
                    CompileTimeStructuralFactKind.MethodKindIsLaw => method.Kind == StarkFunctionKind.Law,
                    CompileTimeStructuralFactKind.MethodKindIsFiniteLaw => method.Kind == StarkFunctionKind.FiniteLaw,
                    _ => false
                });
                return true;
            }

            if (kind is CompileTimeStructuralFactKind.MethodIsStatic
                or CompileTimeStructuralFactKind.MethodHasBody
                or CompileTimeStructuralFactKind.MethodIsUnsafe
                or CompileTimeStructuralFactKind.MethodIsVarargs)
            {
                constant = CompileTimeConstant.Bool(kind switch
                {
                    CompileTimeStructuralFactKind.MethodIsStatic => method.IsStatic,
                    CompileTimeStructuralFactKind.MethodHasBody => method.HasBody,
                    CompileTimeStructuralFactKind.MethodIsUnsafe => method.IsUnsafe,
                    CompileTimeStructuralFactKind.MethodIsVarargs => method.IsVarargs,
                    _ => false
                });
                return true;
            }

            if (kind is CompileTimeStructuralFactKind.MethodHasFfiAbi
                or CompileTimeStructuralFactKind.MethodAbiIsC
                or CompileTimeStructuralFactKind.MethodAbiIsCDecl
                or CompileTimeStructuralFactKind.MethodAbiIsStdCall
                or CompileTimeStructuralFactKind.MethodAbiIsFastCall
                or CompileTimeStructuralFactKind.MethodAbiIsThisCall
                or CompileTimeStructuralFactKind.MethodAbiIsVectorCall
                or CompileTimeStructuralFactKind.MethodAbiIsSysV
                or CompileTimeStructuralFactKind.MethodAbiIsWin64
                or CompileTimeStructuralFactKind.MethodAbiIsAapcs
                or CompileTimeStructuralFactKind.MethodAbiIsAapcs64)
            {
                constant = CompileTimeConstant.Bool(kind switch
                {
                    CompileTimeStructuralFactKind.MethodHasFfiAbi => method.FfiAbi is not null,
                    CompileTimeStructuralFactKind.MethodAbiIsC => method.FfiAbi == StarkFfiAbi.C,
                    CompileTimeStructuralFactKind.MethodAbiIsCDecl => method.FfiAbi == StarkFfiAbi.CDecl,
                    CompileTimeStructuralFactKind.MethodAbiIsStdCall => method.FfiAbi == StarkFfiAbi.StdCall,
                    CompileTimeStructuralFactKind.MethodAbiIsFastCall => method.FfiAbi == StarkFfiAbi.FastCall,
                    CompileTimeStructuralFactKind.MethodAbiIsThisCall => method.FfiAbi == StarkFfiAbi.ThisCall,
                    CompileTimeStructuralFactKind.MethodAbiIsVectorCall => method.FfiAbi == StarkFfiAbi.VectorCall,
                    CompileTimeStructuralFactKind.MethodAbiIsSysV => method.FfiAbi == StarkFfiAbi.SysV,
                    CompileTimeStructuralFactKind.MethodAbiIsWin64 => method.FfiAbi == StarkFfiAbi.Win64,
                    CompileTimeStructuralFactKind.MethodAbiIsAapcs => method.FfiAbi == StarkFfiAbi.Aapcs,
                    CompileTimeStructuralFactKind.MethodAbiIsAapcs64 => method.FfiAbi == StarkFfiAbi.Aapcs64,
                    _ => false
                });
                return true;
            }

            if (IsMethodParameterMemoryFact(kind))
            {
                if (!TryGetConcreteIndex(arguments, position: 1, out var leftIndex)
                    || !TryGetConcreteIndex(arguments, position: 2, out var rightIndex)
                    || leftIndex >= method.Parameters.Count
                    || rightIndex >= method.Parameters.Count)
                {
                    return false;
                }

                constant = CompileTimeConstant.Bool(EvaluateMethodParameterMemoryFact(kind, method, leftIndex, rightIndex));
                return true;
            }

            if (kind == CompileTimeStructuralFactKind.MethodGenericParameterCount)
            {
                constant = CompileTimeConstant.Integer(method.GenericParams.Count, CountType);
                return true;
            }

            if (kind == CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundCount)
            {
                if (!TryGetConcreteIndex(arguments, position: 1, out var genericParameterIndex)
                    || genericParameterIndex >= method.GenericParams.Count)
                {
                    return false;
                }

                var parameterName = method.GenericParams[genericParameterIndex];
                var bounds = GetMethodGenericParameterTraitBounds(method, parameterName);
                constant = CompileTimeConstant.Integer(bounds.Count, CountType);
                return true;
            }

            if (IsMethodGenericParameterTraitBoundIndexedFact(kind))
            {
                if (!TryGetConcreteIndex(arguments, position: 1, out var genericParameterIndex)
                    || genericParameterIndex >= method.GenericParams.Count
                    || !TryGetConcreteIndex(arguments, position: 2, out var boundIndex))
                {
                    return false;
                }

                var parameterName = method.GenericParams[genericParameterIndex];
                var bounds = GetMethodGenericParameterTraitBounds(method, parameterName);
                if (boundIndex >= bounds.Count)
                {
                    return false;
                }

                var boundType = SubstituteOwnerGenericType(namedTypeDefinition, coreType, bounds[boundIndex]);
                constant = kind switch
                {
                    CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIs =>
                        CompileTimeConstant.Bool(
                            arguments.AdditionalTypeArguments.Count == 1
                            && TypesEquivalent(boundType, arguments.AdditionalTypeArguments[0])),
                    _ when IsMethodGenericParameterTraitBoundTypePredicate(kind) =>
                        CompileTimeConstant.Bool(
                            EvaluateTypePredicate(
                                GetNestedTypePredicate(kind),
                                boundType,
                                resolveNamedType,
                                resolveConcreteLayout)),
                    _ when IsMethodGenericParameterTraitBoundTypeMetadataFact(kind) =>
                        EvaluateNestedTypeMetadataFact(kind, boundType, arguments: null, resolveNamedType),
                    _ => constant
                };
                return true;
            }

            if (kind == CompileTimeStructuralFactKind.MethodComptimeGenericParameterCount)
            {
                constant = CompileTimeConstant.Integer(method.ComptimeGenericParams.Count, CountType);
                return true;
            }

            if (kind is CompileTimeStructuralFactKind.MethodGenericParameterName
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterName
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIs
                || IsMethodComptimeGenericParameterTypePredicate(kind)
                || IsMethodComptimeGenericParameterTypeMetadataFact(kind))
            {
                if (!TryGetConcreteIndex(arguments, position: 1, out var genericParameterIndex))
                {
                    return false;
                }

                if (kind == CompileTimeStructuralFactKind.MethodGenericParameterName)
                {
                    if (genericParameterIndex >= method.GenericParams.Count)
                    {
                        return false;
                    }

                    constant = TextConstant(method.GenericParams[genericParameterIndex]);
                    return true;
                }

                if (genericParameterIndex >= method.ComptimeGenericParams.Count)
                {
                    return false;
                }

                var comptimeParameter = method.ComptimeGenericParams[genericParameterIndex];
                var comptimeParameterType = SubstituteOwnerGenericType(namedTypeDefinition, coreType, comptimeParameter.Type);
                constant = kind switch
                {
                    CompileTimeStructuralFactKind.MethodComptimeGenericParameterName =>
                        TextConstant(comptimeParameter.Name),
                    CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIs =>
                        CompileTimeConstant.Bool(
                            arguments.AdditionalTypeArguments.Count == 1
                            && TypesEquivalent(comptimeParameterType, arguments.AdditionalTypeArguments[0])),
                    _ when IsMethodComptimeGenericParameterTypePredicate(kind) =>
                        CompileTimeConstant.Bool(
                            EvaluateTypePredicate(
                                GetTypePredicate(kind),
                                comptimeParameterType,
                                resolveNamedType,
                                resolveConcreteLayout)),
                    _ when IsMethodComptimeGenericParameterTypeMetadataFact(kind) =>
                        EvaluateNestedTypeMetadataFact(kind, comptimeParameterType, arguments: null, resolveNamedType),
                    _ => constant
                };
                return true;
            }

            if (kind == CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateCount)
            {
                constant = CompileTimeConstant.Integer(method.ThreadSafetyLaws.Count, CountType);
                return true;
            }

            if (IsMethodThreadSafetyLawPredicateIndexedFact(kind))
            {
                if (!TryGetConcreteIndex(arguments, position: 1, out var predicateIndex)
                    || predicateIndex >= method.ThreadSafetyLaws.Count)
                {
                    return false;
                }

                var predicate = method.ThreadSafetyLaws[predicateIndex];
                var predicateType = SubstituteOwnerGenericType(namedTypeDefinition, coreType, predicate.Type);
                constant = kind switch
                {
                    CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateLawName =>
                        TextConstant(predicate.LawName),
                    CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIs =>
                        CompileTimeConstant.Bool(
                            arguments.AdditionalTypeArguments.Count == 1
                            && TypesEquivalent(predicateType, arguments.AdditionalTypeArguments[0])),
                    _ when IsMethodThreadSafetyLawPredicateTypePredicate(kind) =>
                        CompileTimeConstant.Bool(
                            EvaluateTypePredicate(
                                GetNestedTypePredicate(kind),
                                predicateType,
                                resolveNamedType,
                                resolveConcreteLayout)),
                    _ when IsMethodThreadSafetyLawPredicateTypeMetadataFact(kind) =>
                        EvaluateNestedTypeMetadataFact(kind, predicateType, arguments: null, resolveNamedType),
                    _ => constant
                };
                return true;
            }
        }

        if (kind is CompileTimeStructuralFactKind.FunctionPointerKindIsFn
            or CompileTimeStructuralFactKind.FunctionPointerKindIsFinite
            or CompileTimeStructuralFactKind.FunctionPointerKindIsLaw
            or CompileTimeStructuralFactKind.FunctionPointerKindIsFiniteLaw)
        {
            var functionKind = coreType.Kind == StarkTypeKind.FunctionPointer
                ? coreType.FunctionPointerKind
                : null;
            constant = CompileTimeConstant.Bool(kind switch
            {
                CompileTimeStructuralFactKind.FunctionPointerKindIsFn => functionKind == StarkFunctionKind.Fn,
                CompileTimeStructuralFactKind.FunctionPointerKindIsFinite => functionKind == StarkFunctionKind.Finite,
                CompileTimeStructuralFactKind.FunctionPointerKindIsLaw => functionKind == StarkFunctionKind.Law,
                CompileTimeStructuralFactKind.FunctionPointerKindIsFiniteLaw => functionKind == StarkFunctionKind.FiniteLaw,
                _ => false
            });
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.FunctionPointerIsUnsafe)
        {
            constant = CompileTimeConstant.Bool(
                coreType.Kind == StarkTypeKind.FunctionPointer
                && coreType.FunctionPointerIsUnsafe);
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.FunctionPointerHasFfiAbi
            or CompileTimeStructuralFactKind.FunctionPointerAbiIsC
            or CompileTimeStructuralFactKind.FunctionPointerAbiIsCDecl
            or CompileTimeStructuralFactKind.FunctionPointerAbiIsStdCall
            or CompileTimeStructuralFactKind.FunctionPointerAbiIsFastCall
            or CompileTimeStructuralFactKind.FunctionPointerAbiIsThisCall
            or CompileTimeStructuralFactKind.FunctionPointerAbiIsVectorCall
            or CompileTimeStructuralFactKind.FunctionPointerAbiIsSysV
            or CompileTimeStructuralFactKind.FunctionPointerAbiIsWin64
            or CompileTimeStructuralFactKind.FunctionPointerAbiIsAapcs
            or CompileTimeStructuralFactKind.FunctionPointerAbiIsAapcs64)
        {
            var abi = coreType.Kind == StarkTypeKind.FunctionPointer
                ? coreType.FunctionPointerAbi
                : null;
            constant = CompileTimeConstant.Bool(kind switch
            {
                CompileTimeStructuralFactKind.FunctionPointerHasFfiAbi => abi is not null,
                CompileTimeStructuralFactKind.FunctionPointerAbiIsC => abi == StarkFfiAbi.C,
                CompileTimeStructuralFactKind.FunctionPointerAbiIsCDecl => abi == StarkFfiAbi.CDecl,
                CompileTimeStructuralFactKind.FunctionPointerAbiIsStdCall => abi == StarkFfiAbi.StdCall,
                CompileTimeStructuralFactKind.FunctionPointerAbiIsFastCall => abi == StarkFfiAbi.FastCall,
                CompileTimeStructuralFactKind.FunctionPointerAbiIsThisCall => abi == StarkFfiAbi.ThisCall,
                CompileTimeStructuralFactKind.FunctionPointerAbiIsVectorCall => abi == StarkFfiAbi.VectorCall,
                CompileTimeStructuralFactKind.FunctionPointerAbiIsSysV => abi == StarkFfiAbi.SysV,
                CompileTimeStructuralFactKind.FunctionPointerAbiIsWin64 => abi == StarkFfiAbi.Win64,
                CompileTimeStructuralFactKind.FunctionPointerAbiIsAapcs => abi == StarkFfiAbi.Aapcs,
                CompileTimeStructuralFactKind.FunctionPointerAbiIsAapcs64 => abi == StarkFfiAbi.Aapcs64,
                _ => false
            });
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.FieldOffset
            or CompileTimeStructuralFactKind.FieldSize
            or CompileTimeStructuralFactKind.FieldAlign
            or CompileTimeStructuralFactKind.FieldIsMisaligned)
        {
            if (namedType?.Kind is not (DeclarationKind.Struct or DeclarationKind.Record)
                || !TryGetConcreteIndex(arguments, out var fieldIndex)
                || fieldIndex >= namedType.OrderedFields.Count
                || resolveConcreteLayout?.Invoke(coreType) is not { } layout
                || fieldIndex >= layout.Fields.Count)
            {
                return false;
            }

            var field = layout.Fields[fieldIndex];
            constant = kind switch
            {
                CompileTimeStructuralFactKind.FieldOffset => CompileTimeConstant.Integer(field.OffsetBytes, CountType),
                CompileTimeStructuralFactKind.FieldSize => CompileTimeConstant.Integer(field.SizeBytes, CountType),
                CompileTimeStructuralFactKind.FieldAlign => CompileTimeConstant.Integer(field.EffectiveAlignmentBytes, CountType),
                CompileTimeStructuralFactKind.FieldIsMisaligned => CompileTimeConstant.Bool(field.IsMisaligned),
                _ => constant
            };
            return true;
        }

        if (IsEnumTagLayoutFact(kind))
        {
            if (namedType?.Kind != DeclarationKind.Enum
                || resolveConcreteLayout?.Invoke(coreType) is not { } layout
                || layout.Fields.Count == 0
                || !string.Equals(layout.Fields[0].Name, "$tag", StringComparison.Ordinal))
            {
                return false;
            }

            var tagField = layout.Fields[0];
            constant = kind switch
            {
                CompileTimeStructuralFactKind.EnumTagOffset =>
                    CompileTimeConstant.Integer(tagField.OffsetBytes, CountType),
                CompileTimeStructuralFactKind.EnumTagSize =>
                    CompileTimeConstant.Integer(tagField.SizeBytes, CountType),
                CompileTimeStructuralFactKind.EnumTagAlign =>
                    CompileTimeConstant.Integer(tagField.EffectiveAlignmentBytes, CountType),
                CompileTimeStructuralFactKind.EnumTagIsMisaligned =>
                    CompileTimeConstant.Bool(tagField.IsMisaligned),
                _ => constant
            };
            return true;
        }

        if (IsEnumVariantPayloadLayoutFact(kind))
        {
            if (namedType?.Kind != DeclarationKind.Enum
                || !TryGetConcreteIndex(arguments, position: 0, out var variantIndex)
                || !TryGetConcreteIndex(arguments, position: 1, out var payloadIndex)
                || resolveConcreteLayout?.Invoke(coreType) is not { } layout
                || !TryGetEnumPayloadConcreteFieldLayout(
                    namedType,
                    layout,
                    variantIndex,
                    payloadIndex,
                    out var payloadField))
            {
                return false;
            }

            constant = kind switch
            {
                CompileTimeStructuralFactKind.EnumVariantPayloadOffset =>
                    CompileTimeConstant.Integer(payloadField.OffsetBytes, CountType),
                CompileTimeStructuralFactKind.EnumVariantPayloadSize =>
                    CompileTimeConstant.Integer(payloadField.SizeBytes, CountType),
                CompileTimeStructuralFactKind.EnumVariantPayloadAlign =>
                    CompileTimeConstant.Integer(payloadField.EffectiveAlignmentBytes, CountType),
                CompileTimeStructuralFactKind.EnumVariantPayloadIsMisaligned =>
                    CompileTimeConstant.Bool(payloadField.IsMisaligned),
                _ => constant
            };
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.EnumVariantTag)
        {
            if (namedType?.Kind != DeclarationKind.Enum
                || !TryGetConcreteIndex(arguments, out var variantIndex)
                || !TryGetEnumVariantTagValue(namedType, variantIndex, out var tagValue))
            {
                return false;
            }

            constant = CompileTimeConstant.Integer(tagValue, CountType);
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.StructLayoutIsAuto
            or CompileTimeStructuralFactKind.StructLayoutIsC
            or CompileTimeStructuralFactKind.StructLayoutIsExplicit
            or CompileTimeStructuralFactKind.StructHasPack
            or CompileTimeStructuralFactKind.StructPack
            or CompileTimeStructuralFactKind.StructHasAlign
            or CompileTimeStructuralFactKind.StructAlign)
        {
            var isStruct = namedType?.Kind == DeclarationKind.Struct;
            var layoutKind = namedType?.Layout?.Kind ?? StructLayoutKind.Auto;
            constant = kind switch
            {
                CompileTimeStructuralFactKind.StructLayoutIsAuto =>
                    CompileTimeConstant.Bool(isStruct && layoutKind == StructLayoutKind.Auto),
                CompileTimeStructuralFactKind.StructLayoutIsC =>
                    CompileTimeConstant.Bool(isStruct && layoutKind == StructLayoutKind.C),
                CompileTimeStructuralFactKind.StructLayoutIsExplicit =>
                    CompileTimeConstant.Bool(isStruct && layoutKind == StructLayoutKind.Explicit),
                CompileTimeStructuralFactKind.StructHasPack =>
                    CompileTimeConstant.Bool(isStruct && namedType?.Layout?.PackBytes is not null),
                CompileTimeStructuralFactKind.StructPack =>
                    CompileTimeConstant.Integer(isStruct ? namedType?.Layout?.PackBytes ?? 0 : 0, CountType),
                CompileTimeStructuralFactKind.StructHasAlign =>
                    CompileTimeConstant.Bool(isStruct && namedType?.Layout?.AlignBytes is not null),
                CompileTimeStructuralFactKind.StructAlign =>
                    CompileTimeConstant.Integer(isStruct ? namedType?.Layout?.AlignBytes ?? 0 : 0, CountType),
                _ => constant
            };
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.FieldTypeIs)
        {
            if (namedType?.Kind is not (DeclarationKind.Struct or DeclarationKind.Record)
                || arguments.AdditionalTypeArguments.Count != 1
                || !TryGetConcreteIndex(arguments, out var fieldIndex)
                || fieldIndex >= namedType.OrderedFields.Count)
            {
                return false;
            }

            constant = CompileTimeConstant.Bool(
                TypesEquivalent(
                    SubstituteOwnerGenericType(namedType, coreType, namedType.OrderedFields[fieldIndex].Type),
                    arguments.AdditionalTypeArguments[0]));
            return true;
        }

        if (IsFieldTypeMetadataFact(kind))
        {
            if (namedType?.Kind is not (DeclarationKind.Struct or DeclarationKind.Record)
                || !TryGetConcreteIndex(arguments, out var fieldIndex)
                || fieldIndex >= namedType.OrderedFields.Count)
            {
                return false;
            }

            constant = EvaluateNestedTypeMetadataFact(
                kind,
                SubstituteOwnerGenericType(namedType, coreType, namedType.OrderedFields[fieldIndex].Type),
                arguments,
                resolveNamedType);
            return true;
        }

        if (IsFieldTypePredicate(kind))
        {
            if (namedType?.Kind is not (DeclarationKind.Struct or DeclarationKind.Record)
                || !TryGetConcreteIndex(arguments, out var fieldIndex)
                || fieldIndex >= namedType.OrderedFields.Count)
            {
                return false;
            }

            constant = CompileTimeConstant.Bool(
                EvaluateTypePredicate(
                    GetTypePredicate(kind),
                    SubstituteOwnerGenericType(namedType, coreType, namedType.OrderedFields[fieldIndex].Type),
                    resolveNamedType,
                    resolveConcreteLayout));
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.FieldName)
        {
            if (namedType?.Kind is not (DeclarationKind.Struct or DeclarationKind.Record)
                || !TryGetConcreteIndex(arguments, out var fieldIndex)
                || fieldIndex >= namedType.OrderedFields.Count)
            {
                return false;
            }

            constant = TextConstant(namedType.OrderedFields[fieldIndex].Name);
            return true;
        }

        if (IsFieldVisibilityFact(kind))
        {
            if (namedType?.Kind is not (DeclarationKind.Struct or DeclarationKind.Record)
                || !TryGetConcreteIndex(arguments, out var fieldIndex)
                || fieldIndex >= namedType.OrderedFields.Count)
            {
                return false;
            }

            constant = CompileTimeConstant.Bool(VisibilityMatchesFact(kind, namedType.OrderedFields[fieldIndex].Visibility));
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.FieldHasExplicitOffset
            or CompileTimeStructuralFactKind.FieldExplicitOffset)
        {
            if (namedType?.Kind is not (DeclarationKind.Struct or DeclarationKind.Record)
                || !TryGetConcreteIndex(arguments, out var fieldIndex)
                || fieldIndex >= namedType.OrderedFields.Count)
            {
                return false;
            }

            var explicitOffsetBytes = namedType.OrderedFields[fieldIndex].ExplicitOffsetBytes;
            constant = kind == CompileTimeStructuralFactKind.FieldHasExplicitOffset
                ? CompileTimeConstant.Bool(explicitOffsetBytes is not null)
                : CompileTimeConstant.Integer(explicitOffsetBytes ?? 0, CountType);
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeCount)
        {
            constant = CompileTimeConstant.Integer(namedType?.ThreadSafetyLaws.Count ?? 0, CountType);
            return true;
        }

        if (IsTypeThreadSafetyLawAttributeIndexedFact(kind))
        {
            if (namedType is null
                || !TryGetConcreteIndex(arguments, out var attributeIndex)
                || attributeIndex >= namedType.ThreadSafetyLaws.Count)
            {
                return false;
            }

            constant = EvaluateThreadSafetyLawAttributeFact(
                kind,
                namedType.ThreadSafetyLaws[attributeIndex],
                namedTypeDefinition ?? namedType,
                coreType,
                arguments,
                resolveNamedType,
                resolveConcreteLayout);
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeCount)
        {
            if (namedType?.Kind is not (DeclarationKind.Struct or DeclarationKind.Record)
                || !TryGetConcreteIndex(arguments, out var fieldIndex)
                || fieldIndex >= namedType.OrderedFields.Count)
            {
                return false;
            }

            constant = CompileTimeConstant.Integer(namedType.OrderedFields[fieldIndex].ThreadSafetyLaws.Count, CountType);
            return true;
        }

        if (IsFieldThreadSafetyLawAttributeIndexedFact(kind))
        {
            if (namedType?.Kind is not (DeclarationKind.Struct or DeclarationKind.Record)
                || !TryGetConcreteIndex(arguments, position: 0, out var fieldIndex)
                || !TryGetConcreteIndex(arguments, position: 1, out var attributeIndex)
                || fieldIndex >= namedType.OrderedFields.Count)
            {
                return false;
            }

            var fieldAttributes = namedType.OrderedFields[fieldIndex].ThreadSafetyLaws;
            if (attributeIndex >= fieldAttributes.Count)
            {
                return false;
            }

            constant = EvaluateThreadSafetyLawAttributeFact(
                kind,
                fieldAttributes[attributeIndex],
                namedTypeDefinition ?? namedType,
                coreType,
                arguments,
                resolveNamedType,
                resolveConcreteLayout);
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.EnumVariantPayloadCount
            or CompileTimeStructuralFactKind.EnumVariantIsOk
            or CompileTimeStructuralFactKind.EnumVariantIsErr
            or CompileTimeStructuralFactKind.EnumVariantIsErrorFunnel)
        {
            if (namedType?.Kind != DeclarationKind.Enum
                || !TryGetConcreteIndex(arguments, out var variantIndex)
                || variantIndex >= namedType.Variants.Count)
            {
                return false;
            }

            var variant = namedType.Variants[variantIndex];
            constant = kind switch
            {
                CompileTimeStructuralFactKind.EnumVariantPayloadCount =>
                    CompileTimeConstant.Integer(variant.Fields.Count, CountType),
                CompileTimeStructuralFactKind.EnumVariantIsOk =>
                    CompileTimeConstant.Bool(variant.Role == EnumVariantRole.Ok),
                CompileTimeStructuralFactKind.EnumVariantIsErr =>
                    CompileTimeConstant.Bool(variant.Role == EnumVariantRole.Err),
                CompileTimeStructuralFactKind.EnumVariantIsErrorFunnel =>
                    CompileTimeConstant.Bool(variant.IsErrorFunnel),
                _ => constant
            };
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.EnumVariantAbsorbsErrorTypeIs)
        {
            if (namedType?.Kind != DeclarationKind.Enum
                || arguments.AdditionalTypeArguments.Count != 1
                || !TryGetConcreteIndex(arguments, out var variantIndex)
                || variantIndex >= namedType.Variants.Count)
            {
                return false;
            }

            var variant = namedType.Variants[variantIndex];
            constant = CompileTimeConstant.Bool(
                variant.AbsorbsErrorType is not null
                && TypesEquivalent(
                    SubstituteOwnerGenericType(namedType, coreType, variant.AbsorbsErrorType),
                    arguments.AdditionalTypeArguments[0]));
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.EnumVariantName)
        {
            if (namedType?.Kind != DeclarationKind.Enum
                || !TryGetConcreteIndex(arguments, out var variantIndex)
                || variantIndex >= namedType.Variants.Count)
            {
                return false;
            }

            constant = TextConstant(namedType.Variants[variantIndex].Name);
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.EnumVariantUsesNamedFields)
        {
            if (namedType?.Kind != DeclarationKind.Enum
                || !TryGetConcreteIndex(arguments, out var variantIndex)
                || variantIndex >= namedType.Variants.Count)
            {
                return false;
            }

            constant = CompileTimeConstant.Bool(namedType.Variants[variantIndex].UsesNamedFields);
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.EnumVariantPayloadTypeIs)
        {
            if (namedType?.Kind != DeclarationKind.Enum
                || arguments.AdditionalTypeArguments.Count != 1
                || !TryGetConcreteIndex(arguments, position: 0, out var variantIndex)
                || !TryGetConcreteIndex(arguments, position: 1, out var payloadIndex)
                || variantIndex >= namedType.Variants.Count)
            {
                return false;
            }

            var variant = namedType.Variants[variantIndex];
            if (payloadIndex >= variant.Fields.Count)
            {
                return false;
            }

            constant = CompileTimeConstant.Bool(
                TypesEquivalent(
                    SubstituteOwnerGenericType(namedType, coreType, variant.Fields[payloadIndex].Type),
                    arguments.AdditionalTypeArguments[0]));
            return true;
        }

        if (IsEnumVariantPayloadTypePredicate(kind))
        {
            if (namedType?.Kind != DeclarationKind.Enum
                || !TryGetConcreteIndex(arguments, position: 0, out var variantIndex)
                || !TryGetConcreteIndex(arguments, position: 1, out var payloadIndex)
                || variantIndex >= namedType.Variants.Count)
            {
                return false;
            }

            var variant = namedType.Variants[variantIndex];
            if (payloadIndex >= variant.Fields.Count)
            {
                return false;
            }

            constant = CompileTimeConstant.Bool(
                EvaluateTypePredicate(
                    GetTypePredicate(kind),
                    SubstituteOwnerGenericType(namedType, coreType, variant.Fields[payloadIndex].Type),
                    resolveNamedType,
                    resolveConcreteLayout));
            return true;
        }

        if (IsEnumVariantPayloadTypeMetadataFact(kind))
        {
            if (namedType?.Kind != DeclarationKind.Enum
                || !TryGetConcreteIndex(arguments, position: 0, out var variantIndex)
                || !TryGetConcreteIndex(arguments, position: 1, out var payloadIndex)
                || variantIndex >= namedType.Variants.Count)
            {
                return false;
            }

            var variant = namedType.Variants[variantIndex];
            if (payloadIndex >= variant.Fields.Count)
            {
                return false;
            }

            constant = EvaluateNestedTypeMetadataFact(
                kind,
                SubstituteOwnerGenericType(namedType, coreType, variant.Fields[payloadIndex].Type),
                arguments,
                resolveNamedType);
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.EnumVariantPayloadHasName
            or CompileTimeStructuralFactKind.EnumVariantPayloadName)
        {
            if (namedType?.Kind != DeclarationKind.Enum
                || !TryGetConcreteIndex(arguments, position: 0, out var variantIndex)
                || !TryGetConcreteIndex(arguments, position: 1, out var payloadIndex)
                || variantIndex >= namedType.Variants.Count)
            {
                return false;
            }

            var variant = namedType.Variants[variantIndex];
            if (payloadIndex >= variant.Fields.Count)
            {
                return false;
            }

            var payloadName = variant.Fields[payloadIndex].Name;
            constant = kind == CompileTimeStructuralFactKind.EnumVariantPayloadHasName
                ? CompileTimeConstant.Bool(payloadName is not null)
                : TextConstant(payloadName ?? string.Empty);
            return true;
        }

        constant = CompileTimeConstant.Bool(
            EvaluateTypePredicate(GetTypePredicate(kind), coreType, resolveNamedType, resolveConcreteLayout));
        return true;
    }

    public static bool TryCreateDefaultConstant(
        CompileTimeStructuralFactKind kind,
        out CompileTimeConstant constant)
    {
        constant = default;
        if (!TryGetReturnType(kind, out var returnType))
        {
            return false;
        }

        constant = returnType.Kind switch
        {
            StarkTypeKind.Bool => CompileTimeConstant.Bool(false),
            StarkTypeKind.Ascii or StarkTypeKind.Unicode => CompileTimeConstant.Text(
                TextLiteralDecoder.EncodeStringLiteral(string.Empty),
                returnType),
            _ => CompileTimeConstant.Integer(BigInteger.Zero, returnType)
        };
        return true;
    }

    private static bool TryEvaluateCallableNestedTypeArgumentFact(
        CompileTimeStructuralFactKind kind,
        StarkTypeSymbol parentType,
        int argumentIndex,
        CompileTimeStructuralFactArguments arguments,
        Func<StarkTypeSymbol, NamedTypeSymbol?> resolveNamedType,
        Func<StarkTypeSymbol, ConcreteTypeLayout?>? resolveConcreteLayout,
        out CompileTimeConstant constant)
    {
        constant = default;
        var coreParentType = NormalizeTypeForComparison(parentType);
        if (coreParentType.TypeArguments is not { } typeArguments
            || argumentIndex < 0
            || argumentIndex >= typeArguments.Count)
        {
            return false;
        }

        var typeArgument = typeArguments[argumentIndex];
        if (IsCallableNestedTypeArgumentExactFact(kind))
        {
            constant = CompileTimeConstant.Bool(
                arguments.AdditionalTypeArguments.Count == 1
                && TypesEquivalent(typeArgument, arguments.AdditionalTypeArguments[0]));
            return true;
        }

        if (IsCallableNestedTypeArgumentTypePredicate(kind))
        {
            constant = CompileTimeConstant.Bool(
                EvaluateTypePredicate(
                    GetTypePredicate(kind),
                    typeArgument,
                    resolveNamedType,
                    resolveConcreteLayout));
            return true;
        }

        if (IsCallableNestedTypeArgumentMetadataFact(kind))
        {
            constant = EvaluateNestedTypeMetadataFact(kind, typeArgument, arguments: null, resolveNamedType);
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> StructuralTypeParameters { get; } = ["T"];

    private static IReadOnlyList<string> ImplementsTypeParameters { get; } = ["T", "Trait"];

    private static IReadOnlyList<string> TypeComparisonTypeParameters { get; } = ["T", "U"];

    private static IReadOnlyList<ComptimeGenericParameterSymbol> NoComptimeValueParameters { get; } = [];

    private static StarkTypeSymbol SignedIntegerFactValueType { get; } = StarkTypeSymbols.Integer(1024);

    private static IReadOnlyList<ComptimeGenericParameterSymbol> SignedIntegerFactValueParameters { get; } =
        [new ComptimeGenericParameterSymbol("Value", SignedIntegerFactValueType)];

    private static IReadOnlyList<ComptimeGenericParameterSymbol> CountFactValueParameters { get; } =
        [new ComptimeGenericParameterSymbol("Value", CountType)];

    private static IReadOnlyList<ComptimeGenericParameterSymbol> ComptimeArgumentValueComparisonParameters { get; } =
    [
        new ComptimeGenericParameterSymbol("Index", CountType),
        new ComptimeGenericParameterSymbol("Value", SignedIntegerFactValueType)
    ];

    private static IReadOnlyList<ComptimeGenericParameterSymbol> IndexComptimeValueParameters { get; } =
        [new ComptimeGenericParameterSymbol("Index", CountType)];

    private static IReadOnlyList<ComptimeGenericParameterSymbol> ImplementedTraitArgumentComptimeValueParameters { get; } =
    [
        new ComptimeGenericParameterSymbol("TraitIndex", CountType),
        new ComptimeGenericParameterSymbol("ArgumentIndex", CountType)
    ];

    private static IReadOnlyList<ComptimeGenericParameterSymbol> ImplementedTraitComptimeArgumentValueComparisonParameters { get; } =
    [
        new ComptimeGenericParameterSymbol("TraitIndex", CountType),
        new ComptimeGenericParameterSymbol("ArgumentIndex", CountType),
        new ComptimeGenericParameterSymbol("Value", SignedIntegerFactValueType)
    ];

    private static IReadOnlyList<ComptimeGenericParameterSymbol> MethodIndexComptimeValueParameters { get; } =
        [new ComptimeGenericParameterSymbol("MethodIndex", CountType)];

    private static IReadOnlyList<ComptimeGenericParameterSymbol> MethodParameterComptimeValueParameters { get; } =
    [
        new ComptimeGenericParameterSymbol("MethodIndex", CountType),
        new ComptimeGenericParameterSymbol("ParameterIndex", CountType)
    ];

    private static IReadOnlyList<ComptimeGenericParameterSymbol> ParameterTypeArgumentComptimeValueParameters { get; } =
    [
        new ComptimeGenericParameterSymbol("ParameterIndex", CountType),
        new ComptimeGenericParameterSymbol("ArgumentIndex", CountType)
    ];

    private static IReadOnlyList<ComptimeGenericParameterSymbol> MethodReturnTypeArgumentComptimeValueParameters { get; } =
    [
        new ComptimeGenericParameterSymbol("MethodIndex", CountType),
        new ComptimeGenericParameterSymbol("ArgumentIndex", CountType)
    ];

    private static IReadOnlyList<ComptimeGenericParameterSymbol> MethodParameterTypeArgumentComptimeValueParameters { get; } =
    [
        new ComptimeGenericParameterSymbol("MethodIndex", CountType),
        new ComptimeGenericParameterSymbol("ParameterIndex", CountType),
        new ComptimeGenericParameterSymbol("ArgumentIndex", CountType)
    ];

    private static IReadOnlyList<ComptimeGenericParameterSymbol> MethodParameterPairComptimeValueParameters { get; } =
    [
        new ComptimeGenericParameterSymbol("MethodIndex", CountType),
        new ComptimeGenericParameterSymbol("LeftIndex", CountType),
        new ComptimeGenericParameterSymbol("RightIndex", CountType)
    ];

    private static IReadOnlyList<ComptimeGenericParameterSymbol> MethodGenericParameterComptimeValueParameters { get; } =
    [
        new ComptimeGenericParameterSymbol("MethodIndex", CountType),
        new ComptimeGenericParameterSymbol("GenericParameterIndex", CountType)
    ];

    private static IReadOnlyList<ComptimeGenericParameterSymbol> MethodGenericParameterTraitBoundComptimeValueParameters { get; } =
    [
        new ComptimeGenericParameterSymbol("MethodIndex", CountType),
        new ComptimeGenericParameterSymbol("GenericParameterIndex", CountType),
        new ComptimeGenericParameterSymbol("BoundIndex", CountType)
    ];

    private static IReadOnlyList<ComptimeGenericParameterSymbol> MethodThreadSafetyLawPredicateComptimeValueParameters { get; } =
    [
        new ComptimeGenericParameterSymbol("MethodIndex", CountType),
        new ComptimeGenericParameterSymbol("PredicateIndex", CountType)
    ];

    private static IReadOnlyList<ComptimeGenericParameterSymbol> VariantPayloadComptimeValueParameters { get; } =
    [
        new ComptimeGenericParameterSymbol("VariantIndex", CountType),
        new ComptimeGenericParameterSymbol("PayloadIndex", CountType)
    ];

    private static IReadOnlyList<ComptimeGenericParameterSymbol> FieldThreadSafetyLawAttributeComptimeValueParameters { get; } =
    [
        new ComptimeGenericParameterSymbol("FieldIndex", CountType),
        new ComptimeGenericParameterSymbol("AttributeIndex", CountType)
    ];

    private static IReadOnlyList<ComptimeGenericParameterSymbol> ParameterPairComptimeValueParameters { get; } =
    [
        new ComptimeGenericParameterSymbol("LeftIndex", CountType),
        new ComptimeGenericParameterSymbol("RightIndex", CountType)
    ];

    private static IReadOnlyList<string> GetTypeParameters(CompileTimeStructuralFactKind kind)
    {
        return kind switch
        {
            CompileTimeStructuralFactKind.Implements => ImplementsTypeParameters,
            _ when IsCallableNestedTypeArgumentExactFact(kind) => TypeComparisonTypeParameters,
            CompileTimeStructuralFactKind.FieldTypeIs
                or CompileTimeStructuralFactKind.ImplementedTraitTypeIs
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIs
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIs
                or CompileTimeStructuralFactKind.ClosureReturnTypeIs
                or CompileTimeStructuralFactKind.ClosureParameterTypeIs
                or CompileTimeStructuralFactKind.MethodReturnTypeIs
                or CompileTimeStructuralFactKind.MethodParameterTypeIs
                or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIs
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIs
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIs
                or CompileTimeStructuralFactKind.DynTraitTargetTypeIs
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIs
                or CompileTimeStructuralFactKind.TypeArgumentTypeIs
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIs
                or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIs
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIs
                or CompileTimeStructuralFactKind.RawPointerElementTypeIs
                or CompileTimeStructuralFactKind.TypeElementTypeIs
                or CompileTimeStructuralFactKind.TypeUnqualifiedTypeIs
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeUnqualifiedTypeIs
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeUnqualifiedTypeIs
                or CompileTimeStructuralFactKind.ClosureReturnTypeUnqualifiedTypeIs
                or CompileTimeStructuralFactKind.ClosureParameterTypeUnqualifiedTypeIs
                or CompileTimeStructuralFactKind.MethodReturnTypeUnqualifiedTypeIs
                or CompileTimeStructuralFactKind.MethodParameterTypeUnqualifiedTypeIs
                or CompileTimeStructuralFactKind.FieldTypeUnqualifiedTypeIs
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeUnqualifiedTypeIs
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIs
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIs
                or CompileTimeStructuralFactKind.EnumVariantAbsorbsErrorTypeIs
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIs
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIs => TypeComparisonTypeParameters,
            _ => StructuralTypeParameters
        };
    }

    private static IReadOnlyList<ComptimeGenericParameterSymbol> GetComptimeValueParameters(
        CompileTimeStructuralFactKind kind)
    {
        if (kind is CompileTimeStructuralFactKind.TypeIntegerMinIs
            or CompileTimeStructuralFactKind.TypeIntegerMaxIs)
        {
            return SignedIntegerFactValueParameters;
        }

        if (kind == CompileTimeStructuralFactKind.TypeFixedArrayLengthIs)
        {
            return CountFactValueParameters;
        }

        if (kind == CompileTimeStructuralFactKind.TypeComptimeArgumentValueIs)
        {
            return ComptimeArgumentValueComparisonParameters;
        }

        if (kind == CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentValueIs)
        {
            return ImplementedTraitComptimeArgumentValueComparisonParameters;
        }

        if (IsImplementedTraitTypeArgumentIndexedFact(kind)
            || IsImplementedTraitComptimeArgumentIndexedFact(kind))
        {
            return ImplementedTraitArgumentComptimeValueParameters;
        }

        if (IsTypeThreadSafetyLawAttributeIndexedFact(kind))
        {
            return IndexComptimeValueParameters;
        }

        if (IsImplementedTraitIndexedFact(kind))
        {
            return IndexComptimeValueParameters;
        }

        if (kind == CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeCount)
        {
            return IndexComptimeValueParameters;
        }

        if (IsFieldThreadSafetyLawAttributeIndexedFact(kind))
        {
            return FieldThreadSafetyLawAttributeComptimeValueParameters;
        }

        if (IsMethodParameterTypeArgumentFact(kind))
        {
            return MethodParameterTypeArgumentComptimeValueParameters;
        }

        if (IsMethodReturnTypeArgumentFact(kind))
        {
            return MethodReturnTypeArgumentComptimeValueParameters;
        }

        if (IsMethodParameterTypePredicate(kind)
            || IsMethodParameterTypeMetadataFact(kind)
            || IsMethodParameterRawPointerElementCountExpressionFact(kind)
            || kind == CompileTimeStructuralFactKind.MethodParameterName
            || kind == CompileTimeStructuralFactKind.MethodParameterTypeIs)
        {
            return MethodParameterComptimeValueParameters;
        }

        if (IsMethodParameterMemoryFact(kind))
        {
            return MethodParameterPairComptimeValueParameters;
        }

        if (kind is CompileTimeStructuralFactKind.MethodGenericParameterName
            or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundCount
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterName
            or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIs
            || IsMethodComptimeGenericParameterTypePredicate(kind)
            || IsMethodComptimeGenericParameterTypeMetadataFact(kind))
        {
            return MethodGenericParameterComptimeValueParameters;
        }

        if (IsMethodGenericParameterTraitBoundIndexedFact(kind))
        {
            return MethodGenericParameterTraitBoundComptimeValueParameters;
        }

        if (IsMethodThreadSafetyLawPredicateIndexedFact(kind))
        {
            return MethodThreadSafetyLawPredicateComptimeValueParameters;
        }

        if (IsMethodIndexedFact(kind))
        {
            return MethodIndexComptimeValueParameters;
        }

        if ((kind is CompileTimeStructuralFactKind.EnumVariantPayloadTypeIs
            or CompileTimeStructuralFactKind.EnumVariantPayloadHasName
            or CompileTimeStructuralFactKind.EnumVariantPayloadName)
            || IsEnumVariantPayloadLayoutFact(kind)
            || IsEnumVariantPayloadTypePredicate(kind)
            || IsEnumVariantPayloadTypeMetadataFact(kind))
        {
            return VariantPayloadComptimeValueParameters;
        }

        if (IsFunctionPointerParameterTypeArgumentFact(kind)
            || IsClosureParameterTypeArgumentFact(kind))
        {
            return ParameterTypeArgumentComptimeValueParameters;
        }

        if (IsFunctionPointerReturnTypeArgumentFact(kind)
            || IsClosureReturnTypeArgumentFact(kind))
        {
            return IndexComptimeValueParameters;
        }

        if (IsFunctionPointerParameterTypePredicate(kind)
            || IsFunctionPointerParameterTypeMetadataFact(kind)
            || IsFunctionPointerParameterRawPointerElementCountExpressionFact(kind))
        {
            return IndexComptimeValueParameters;
        }

        if (IsFunctionPointerParameterMemoryFact(kind))
        {
            return ParameterPairComptimeValueParameters;
        }

        if (IsClosureParameterTypePredicate(kind)
            || IsClosureParameterTypeMetadataFact(kind)
            || IsClosureParameterRawPointerElementCountExpressionFact(kind))
        {
            return IndexComptimeValueParameters;
        }

        if (IsClosureParameterMemoryFact(kind))
        {
            return ParameterPairComptimeValueParameters;
        }

        if (IsTypeArgumentTypePredicate(kind)
            || IsTypeArgumentTypeMetadataFact(kind)
            || IsTypeComptimeArgumentTypePredicate(kind)
            || IsTypeComptimeArgumentTypeMetadataFact(kind))
        {
            return IndexComptimeValueParameters;
        }

        if (kind is CompileTimeStructuralFactKind.TypeGenericParameterName
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterName
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIs
            || IsTypeComptimeGenericParameterTypePredicate(kind)
            || IsTypeComptimeGenericParameterTypeMetadataFact(kind))
        {
            return IndexComptimeValueParameters;
        }

        if (kind is CompileTimeStructuralFactKind.AssociatedTypeName
            or CompileTimeStructuralFactKind.AssociatedTypeHasTarget
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIs
            || IsAssociatedTypeTargetTypePredicate(kind)
            || IsAssociatedTypeTargetTypeMetadataFact(kind))
        {
            return IndexComptimeValueParameters;
        }

        if (IsFieldTypeMetadataFact(kind)
            || IsFieldVisibilityFact(kind))
        {
            return IndexComptimeValueParameters;
        }

        return kind is CompileTimeStructuralFactKind.FieldOffset
            or CompileTimeStructuralFactKind.FieldSize
            or CompileTimeStructuralFactKind.FieldAlign
            or CompileTimeStructuralFactKind.FieldIsMisaligned
            or CompileTimeStructuralFactKind.FieldHasExplicitOffset
            or CompileTimeStructuralFactKind.FieldExplicitOffset
            or CompileTimeStructuralFactKind.FieldTypeIsBool
            or CompileTimeStructuralFactKind.FieldTypeIsInteger
            or CompileTimeStructuralFactKind.FieldTypeIsFloat
            or CompileTimeStructuralFactKind.FieldTypeIsRawPointer
            or CompileTimeStructuralFactKind.FieldTypeIsFixedArray
            or CompileTimeStructuralFactKind.FieldTypeIsSlice
            or CompileTimeStructuralFactKind.FieldTypeIsDynamic
            or CompileTimeStructuralFactKind.FieldTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.FieldTypeIsClosure
            or CompileTimeStructuralFactKind.FieldTypeIsDynTrait
            or CompileTimeStructuralFactKind.FieldTypeIsNamed
            or CompileTimeStructuralFactKind.FieldTypeIsStruct
            or CompileTimeStructuralFactKind.FieldTypeIsRecord
            or CompileTimeStructuralFactKind.FieldTypeIsEnum
            or CompileTimeStructuralFactKind.FieldTypeIsTrait
            or CompileTimeStructuralFactKind.FieldTypeIsDoctrine
            or CompileTimeStructuralFactKind.FieldTypeHasConcreteLayout
            or CompileTimeStructuralFactKind.TypeGenericParameterName
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterName
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIs
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsBool
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsInteger
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsFloat
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsRawPointer
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsFixedArray
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsSlice
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsDynamic
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsClosure
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsDynTrait
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsNamed
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsStruct
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsRecord
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsEnum
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsTrait
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsDoctrine
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeHasConcreteLayout
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeDisplayName
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeBaseName
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeArgumentCount
            or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeComptimeArgumentCount
            or CompileTimeStructuralFactKind.TypeArgumentTypeIs
            or CompileTimeStructuralFactKind.TypeComptimeArgumentName
            or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIs
            or CompileTimeStructuralFactKind.AssociatedTypeName
            or CompileTimeStructuralFactKind.AssociatedTypeHasTarget
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIs
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsBool
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsInteger
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsFloat
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsRawPointer
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsFixedArray
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsSlice
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsDynamic
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsFunctionPointer
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsClosure
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsDynTrait
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsNamed
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsStruct
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsRecord
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsEnum
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsTrait
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsDoctrine
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeHasConcreteLayout
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeDisplayName
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeBaseName
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsGenericInstantiation
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeArgumentCount
            or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeComptimeArgumentCount
            or CompileTimeStructuralFactKind.EnumVariantPayloadCount
            or CompileTimeStructuralFactKind.EnumVariantTag
            or CompileTimeStructuralFactKind.EnumVariantIsOk
            or CompileTimeStructuralFactKind.EnumVariantIsErr
            or CompileTimeStructuralFactKind.EnumVariantIsErrorFunnel
            or CompileTimeStructuralFactKind.EnumVariantAbsorbsErrorTypeIs
            or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIs
            or CompileTimeStructuralFactKind.FunctionPointerParameterHasRawPointerElementCountExpression
            or CompileTimeStructuralFactKind.FunctionPointerParameterRawPointerElementCountExpression
            or CompileTimeStructuralFactKind.ClosureParameterTypeIs
            or CompileTimeStructuralFactKind.ClosureParameterHasRawPointerElementCountExpression
            or CompileTimeStructuralFactKind.ClosureParameterRawPointerElementCountExpression
            or CompileTimeStructuralFactKind.FieldTypeIs
            or CompileTimeStructuralFactKind.FieldName
            or CompileTimeStructuralFactKind.EnumVariantName
            or CompileTimeStructuralFactKind.EnumVariantUsesNamedFields
            ? IndexComptimeValueParameters
            : NoComptimeValueParameters;
    }

    private static IReadOnlyList<ComptimeValueArgumentSymbol> ResolveSymbolicValues(
        IReadOnlyList<ComptimeValueArgumentSymbol> arguments,
        IReadOnlyDictionary<string, BigInteger>? comptimeValueSubstitution)
    {
        if (comptimeValueSubstitution is not { Count: > 0 })
        {
            return arguments;
        }

        var result = new ComptimeValueArgumentSymbol[arguments.Count];
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            result[index] = argument.IsSymbolic
                && argument.SymbolicSourceName is { } name
                && comptimeValueSubstitution.TryGetValue(name, out var value)
                    ? argument with { IntegerValue = value, IsSymbolic = false, SymbolicSourceName = null }
                    : argument;
        }

        return result;
    }

    private static bool TryGetConcreteIndex(CompileTimeStructuralFactArguments arguments, out int index)
    {
        return TryGetConcreteIndex(arguments, position: 0, out index);
    }

    private static bool TryGetConcreteIndex(CompileTimeStructuralFactArguments arguments, int position, out int index)
    {
        index = -1;
        if (position < 0
            || position >= arguments.ComptimeValueArguments.Count
            || arguments.ComptimeValueArguments[position].IsSymbolic
            || arguments.ComptimeValueArguments[position].IntegerValue < BigInteger.Zero
            || arguments.ComptimeValueArguments[position].IntegerValue > int.MaxValue)
        {
            return false;
        }

        index = (int)arguments.ComptimeValueArguments[position].IntegerValue;
        return true;
    }

    private static CompileTimeConstant EvaluateThreadSafetyLawAttributeFact(
        CompileTimeStructuralFactKind kind,
        ThreadSafetyLawAttributeSymbol attribute,
        NamedTypeSymbol ownerSymbol,
        StarkTypeSymbol ownerType,
        CompileTimeStructuralFactArguments arguments,
        Func<StarkTypeSymbol, NamedTypeSymbol?> resolveNamedType,
        Func<StarkTypeSymbol, ConcreteTypeLayout?>? resolveConcreteLayout)
    {
        var conditionType = attribute.Condition is { } condition
            ? SubstituteOwnerGenericType(ownerSymbol, ownerType, condition.Type)
            : null;

        return kind switch
        {
            CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeLawName
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeLawName =>
                TextConstant(attribute.LawName),
            CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeIsGrant
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeIsGrant =>
                CompileTimeConstant.Bool(attribute.Kind == ThreadSafetyLawAttributeKind.Grant),
            CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeIsDeny
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeIsDeny =>
                CompileTimeConstant.Bool(attribute.Kind == ThreadSafetyLawAttributeKind.Deny),
            CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeHasCondition
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeHasCondition =>
                CompileTimeConstant.Bool(attribute.Condition is not null),
            CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionLawName
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionLawName =>
                TextConstant(attribute.Condition?.LawName ?? string.Empty),
            CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIs
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIs =>
                CompileTimeConstant.Bool(
                    conditionType is not null
                    && arguments.AdditionalTypeArguments.Count == 1
                    && TypesEquivalent(conditionType, arguments.AdditionalTypeArguments[0])),
            _ when IsThreadSafetyLawAttributeConditionTypePredicate(kind) =>
                CompileTimeConstant.Bool(
                    conditionType is not null
                    && EvaluateTypePredicate(
                        GetNestedTypePredicate(kind),
                        conditionType,
                        resolveNamedType,
                        resolveConcreteLayout)),
            _ when IsThreadSafetyLawAttributeConditionTypeMetadataFact(kind) =>
                conditionType is null
                    ? EvaluateAbsentThreadSafetyLawConditionTypeMetadataFact(kind)
                    : EvaluateNestedTypeMetadataFact(kind, conditionType, arguments: null, resolveNamedType),
            _ => CompileTimeConstant.Bool(false)
        };
    }

    private static CompileTimeConstant EvaluateAbsentThreadSafetyLawConditionTypeMetadataFact(
        CompileTimeStructuralFactKind kind)
    {
        return kind switch
        {
            CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeDisplayName
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeBaseName
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeDisplayName
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeBaseName =>
                TextConstant(string.Empty),
            CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeArgumentCount
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeArgumentCount
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeComptimeArgumentCount =>
                CompileTimeConstant.Integer(0, CountType),
            _ => CompileTimeConstant.Bool(false)
        };
    }

    private static bool TryEvaluateScalarTypeMetadataFact(
        CompileTimeStructuralFactKind kind,
        StarkTypeSymbol type,
        CompileTimeStructuralFactArguments arguments,
        out CompileTimeConstant constant)
    {
        constant = default;
        var coreType = NormalizeTypeForComparison(type);
        if (kind == CompileTimeStructuralFactKind.TypeIntegerBitWidth)
        {
            if (coreType.Kind != StarkTypeKind.Integer || coreType.BitWidth is not int bitWidth)
            {
                return false;
            }

            constant = CompileTimeConstant.Integer(bitWidth, CountType);
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.TypeFloatBitWidth)
        {
            if (coreType.Kind != StarkTypeKind.Float || coreType.BitWidth is not int bitWidth)
            {
                return false;
            }

            constant = CompileTimeConstant.Integer(bitWidth, CountType);
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.TypeIntegerIsSigned)
        {
            constant = CompileTimeConstant.Bool(coreType.Kind == StarkTypeKind.Integer && !coreType.IsUnsigned);
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.TypeIntegerIsUnsigned)
        {
            constant = CompileTimeConstant.Bool(coreType.Kind == StarkTypeKind.Integer && coreType.IsUnsigned);
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.TypeIntegerIsFullRange)
        {
            constant = CompileTimeConstant.Bool(
                StarkTypeSymbols.TryGetIntegerStorageBounds(coreType, out var storageMin, out var storageMax)
                && StarkTypeSymbols.TryGetEffectiveIntegerBounds(coreType, out var effectiveMin, out var effectiveMax)
                && storageMin == effectiveMin
                && storageMax == effectiveMax);
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.TypeIntegerMinIs
            or CompileTimeStructuralFactKind.TypeIntegerMaxIs)
        {
            if (!StarkTypeSymbols.TryGetEffectiveIntegerBounds(coreType, out var effectiveMin, out var effectiveMax)
                || arguments.ComptimeValueArguments.Count != 1
                || arguments.ComptimeValueArguments[0].IsSymbolic)
            {
                return false;
            }

            var expected = arguments.ComptimeValueArguments[0].IntegerValue;
            constant = CompileTimeConstant.Bool(
                kind == CompileTimeStructuralFactKind.TypeIntegerMinIs
                    ? effectiveMin == expected
                    : effectiveMax == expected);
            return true;
        }

        return false;
    }

    private static CompileTimeConstant EvaluateCSourceAliasMetadataFact(
        bool hasAliasFact,
        StarkTypeSymbol type)
    {
        var aliasName = NormalizeTypeForComparison(type).CSourceAliasName;
        return hasAliasFact
            ? CompileTimeConstant.Bool(!string.IsNullOrWhiteSpace(aliasName))
            : TextConstant(aliasName ?? string.Empty);
    }

    private static bool TryEvaluateRawPointerMetadataFact(
        CompileTimeStructuralFactKind kind,
        StarkTypeSymbol type,
        CompileTimeStructuralFactArguments arguments,
        Func<StarkTypeSymbol, NamedTypeSymbol?> resolveNamedType,
        Func<StarkTypeSymbol, ConcreteTypeLayout?>? resolveConcreteLayout,
        out CompileTimeConstant constant)
    {
        constant = default;
        var coreType = NormalizeTypeForComparison(type);
        if (coreType.Kind != StarkTypeKind.RawPointer || coreType.ElementType is not { } elementType)
        {
            return false;
        }

        if (kind == CompileTimeStructuralFactKind.RawPointerIsMutable)
        {
            constant = CompileTimeConstant.Bool(coreType.IsMutablePointer);
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.RawPointerIsReadOnly)
        {
            constant = CompileTimeConstant.Bool(!coreType.IsMutablePointer);
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.RawPointerElementTypeIs)
        {
            constant = CompileTimeConstant.Bool(
                arguments.AdditionalTypeArguments.Count == 1
                && TypesEquivalent(elementType, arguments.AdditionalTypeArguments[0]));
            return true;
        }

        if (IsRawPointerElementTypePredicate(kind))
        {
            constant = CompileTimeConstant.Bool(
                EvaluateTypePredicate(
                    GetTypePredicate(kind),
                    elementType,
                    resolveNamedType,
                    resolveConcreteLayout));
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.RawPointerElementTypeHasCSourceAlias
            or CompileTimeStructuralFactKind.RawPointerElementTypeCSourceAliasName)
        {
            constant = EvaluateCSourceAliasMetadataFact(
                hasAliasFact: kind == CompileTimeStructuralFactKind.RawPointerElementTypeHasCSourceAlias,
                elementType);
            return true;
        }

        return false;
    }

    private static bool TryEvaluateTypeElementMetadataFact(
        CompileTimeStructuralFactKind kind,
        StarkTypeSymbol type,
        CompileTimeStructuralFactArguments arguments,
        Func<StarkTypeSymbol, NamedTypeSymbol?> resolveNamedType,
        Func<StarkTypeSymbol, ConcreteTypeLayout?>? resolveConcreteLayout,
        out CompileTimeConstant constant)
    {
        constant = default;
        var coreType = NormalizeTypeForComparison(type);
        if (coreType.ElementType is not { } elementType)
        {
            return false;
        }

        if (kind == CompileTimeStructuralFactKind.TypeElementTypeIs)
        {
            constant = CompileTimeConstant.Bool(
                arguments.AdditionalTypeArguments.Count == 1
                && TypesEquivalent(elementType, arguments.AdditionalTypeArguments[0]));
            return true;
        }

        if (IsTypeElementTypePredicate(kind))
        {
            constant = CompileTimeConstant.Bool(
                EvaluateTypePredicate(
                    GetTypePredicate(kind),
                    elementType,
                    resolveNamedType,
                    resolveConcreteLayout));
            return true;
        }

        if (kind is CompileTimeStructuralFactKind.TypeElementTypeHasCSourceAlias
            or CompileTimeStructuralFactKind.TypeElementTypeCSourceAliasName)
        {
            constant = EvaluateCSourceAliasMetadataFact(
                hasAliasFact: kind == CompileTimeStructuralFactKind.TypeElementTypeHasCSourceAlias,
                elementType);
            return true;
        }

        return false;
    }

    private static bool TryEvaluateFixedArrayMetadataFact(
        CompileTimeStructuralFactKind kind,
        StarkTypeSymbol type,
        CompileTimeStructuralFactArguments arguments,
        out CompileTimeConstant constant)
    {
        constant = default;
        var coreType = NormalizeTypeForComparison(type);
        if (coreType.Kind != StarkTypeKind.FixedArray || coreType.FixedLength is not int fixedLength)
        {
            return false;
        }

        if (kind == CompileTimeStructuralFactKind.TypeFixedArrayLength)
        {
            constant = CompileTimeConstant.Integer(fixedLength, CountType);
            return true;
        }

        if (kind == CompileTimeStructuralFactKind.TypeFixedArrayLengthIs
            && arguments.ComptimeValueArguments.Count == 1
            && !arguments.ComptimeValueArguments[0].IsSymbolic)
        {
            constant = CompileTimeConstant.Bool(arguments.ComptimeValueArguments[0].IntegerValue == fixedLength);
            return true;
        }

        return false;
    }

    private static bool TryEvaluateTypeQualifierMetadataFact(
        CompileTimeStructuralFactKind kind,
        CompileTimeStructuralFactArguments arguments,
        out CompileTimeConstant constant)
    {
        constant = default;
        if (!IsTypeQualifierMetadataFact(kind))
        {
            return false;
        }

        constant = kind switch
        {
            CompileTimeStructuralFactKind.TypeHasQualifiers =>
                CompileTimeConstant.Bool(HasTopLevelQualifiers(arguments.TargetType)),
            CompileTimeStructuralFactKind.TypeBorrowKindIsNone =>
                CompileTimeConstant.Bool(arguments.TargetType.BorrowKind == StarkBorrowKind.None),
            CompileTimeStructuralFactKind.TypeBorrowKindIsBorrow =>
                CompileTimeConstant.Bool(arguments.TargetType.BorrowKind == StarkBorrowKind.Borrow),
            CompileTimeStructuralFactKind.TypeBorrowKindIsRetBorrow =>
                CompileTimeConstant.Bool(arguments.TargetType.BorrowKind == StarkBorrowKind.RetBorrow),
            CompileTimeStructuralFactKind.TypeBorrowKindIsStoreBorrow =>
                CompileTimeConstant.Bool(arguments.TargetType.BorrowKind == StarkBorrowKind.StoreBorrow),
            CompileTimeStructuralFactKind.TypeAccessKindIsNone =>
                CompileTimeConstant.Bool(arguments.TargetType.AccessKind == StarkAccessKind.None),
            CompileTimeStructuralFactKind.TypeAccessKindIsShared =>
                CompileTimeConstant.Bool(arguments.TargetType.AccessKind == StarkAccessKind.Shared),
            CompileTimeStructuralFactKind.TypeAccessKindIsFrozen =>
                CompileTimeConstant.Bool(arguments.TargetType.AccessKind == StarkAccessKind.Frozen),
            CompileTimeStructuralFactKind.TypeInitializationKindIsNone =>
                CompileTimeConstant.Bool(arguments.TargetType.InitializationKind == StarkInitializationKind.None),
            CompileTimeStructuralFactKind.TypeInitializationKindIsOut =>
                CompileTimeConstant.Bool(arguments.TargetType.InitializationKind == StarkInitializationKind.Out),
            CompileTimeStructuralFactKind.TypeInitializationKindIsInit =>
                CompileTimeConstant.Bool(arguments.TargetType.InitializationKind == StarkInitializationKind.Init),
            CompileTimeStructuralFactKind.TypeIsMutableView =>
                CompileTimeConstant.Bool(arguments.TargetType.IsMutableView),
            CompileTimeStructuralFactKind.TypeUnqualifiedTypeIs =>
                CompileTimeConstant.Bool(
                    arguments.AdditionalTypeArguments.Count == 1
                    && TypesEquivalent(arguments.TargetType, arguments.AdditionalTypeArguments[0])),
            _ => constant
        };
        return true;
    }

    private static bool HasTopLevelQualifiers(StarkTypeSymbol type)
    {
        return type.BorrowKind != StarkBorrowKind.None
            || type.AccessKind != StarkAccessKind.None
            || type.InitializationKind != StarkInitializationKind.None
            || type.IsMutableView;
    }

    private static bool TryGetEnumPayloadConcreteFieldLayout(
        NamedTypeSymbol enumType,
        ConcreteTypeLayout layout,
        int variantIndex,
        int payloadIndex,
        out ConcreteFieldLayout fieldLayout)
    {
        fieldLayout = null!;
        if (enumType.Kind != DeclarationKind.Enum
            || variantIndex < 0
            || variantIndex >= enumType.Variants.Count)
        {
            return false;
        }

        var variant = enumType.Variants[variantIndex];
        if (payloadIndex < 0 || payloadIndex >= variant.Fields.Count)
        {
            return false;
        }

        try
        {
            var storageFieldIndex = checked(1 + payloadIndex);
            for (var index = 0; index < variantIndex; index++)
            {
                storageFieldIndex = checked(storageFieldIndex + enumType.Variants[index].Fields.Count);
            }

            if (storageFieldIndex < 0 || storageFieldIndex >= layout.Fields.Count)
            {
                return false;
            }

            fieldLayout = layout.Fields[storageFieldIndex];
            return string.Equals(
                fieldLayout.Name,
                BuildEnumPayloadStorageFieldName(variant, variant.Fields[payloadIndex]),
                StringComparison.Ordinal);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static string BuildEnumPayloadStorageFieldName(
        EnumVariantSymbol variant,
        EnumVariantFieldSymbol field)
    {
        var suffix = field.Name ?? field.Position.ToString();
        return $"${variant.Name}_{suffix}";
    }

    private static bool TryGetEnumVariantTagValue(
        NamedTypeSymbol enumType,
        int sourceVariantIndex,
        out int tagValue)
    {
        tagValue = -1;
        if (enumType.Kind != DeclarationKind.Enum
            || sourceVariantIndex < 0
            || sourceVariantIndex >= enumType.Variants.Count)
        {
            return false;
        }

        var zeroTagVariantIndex = -1;
        for (var index = 0; index < enumType.Variants.Count; index++)
        {
            if (enumType.Variants[index].IsUnit)
            {
                zeroTagVariantIndex = index;
                break;
            }
        }

        if (zeroTagVariantIndex <= 0)
        {
            tagValue = sourceVariantIndex;
            return true;
        }

        tagValue = sourceVariantIndex == zeroTagVariantIndex
            ? 0
            : sourceVariantIndex < zeroTagVariantIndex
                ? sourceVariantIndex + 1
                : sourceVariantIndex;
        return true;
    }

    private static CompileTimeConstant EvaluateNestedTypeMetadataFact(
        CompileTimeStructuralFactKind kind,
        StarkTypeSymbol type,
        CompileTimeStructuralFactArguments? arguments = null,
        Func<StarkTypeSymbol, NamedTypeSymbol?>? resolveNamedType = null)
    {
        if (IsNestedTypeQualifierMetadataFact(kind))
        {
            return EvaluateNestedTypeQualifierMetadataFact(kind, type, arguments);
        }

        var coreType = NormalizeTypeForComparison(type);
        return kind switch
        {
            _ when IsCallableNestedTypeArgumentDisplayNameFact(kind) =>
                TextConstant(coreType.DisplayName),
            _ when IsCallableNestedTypeArgumentBaseNameFact(kind) =>
                TextConstant(coreType.Kind == StarkTypeKind.Named && coreType.NamedType is { } namedTypeName
                    ? StarkTypeSymbols.GetGenericBaseName(namedTypeName)
                    : string.Empty),
            _ when IsCallableNestedTypeArgumentModuleNameFact(kind) =>
                TextConstant(GetNestedTypeModuleName(coreType, resolveNamedType)),
            _ when IsCallableNestedTypeArgumentIsGenericInstantiationFact(kind) =>
                CompileTimeConstant.Bool(StarkTypeSymbols.IsGenericInstantiation(coreType)),
            _ when GetCallableNestedTypeArgumentOffset(kind) == CallableNestedTypeArgumentArgumentCountOffset =>
                CompileTimeConstant.Integer(coreType.TypeArguments?.Count ?? 0, CountType),
            _ when GetCallableNestedTypeArgumentOffset(kind) == CallableNestedTypeArgumentComptimeArgumentCountOffset =>
                CompileTimeConstant.Integer(coreType.ComptimeValueArguments?.Count ?? 0, CountType),
            CompileTimeStructuralFactKind.FieldTypeDisplayName
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeDisplayName
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeDisplayName
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeDisplayName
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeDisplayName
                or CompileTimeStructuralFactKind.ClosureReturnTypeDisplayName
                or CompileTimeStructuralFactKind.ClosureParameterTypeDisplayName
                or CompileTimeStructuralFactKind.MethodReturnTypeDisplayName
                or CompileTimeStructuralFactKind.MethodParameterTypeDisplayName
                or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeDisplayName
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeDisplayName
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeDisplayName
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeDisplayName
                or CompileTimeStructuralFactKind.TypeArgumentTypeDisplayName
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeDisplayName
                or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeDisplayName
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeDisplayName
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeDisplayName
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeDisplayName =>
                TextConstant(coreType.DisplayName),
            CompileTimeStructuralFactKind.FieldTypeBaseName
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeBaseName
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeBaseName
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeBaseName
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeBaseName
                or CompileTimeStructuralFactKind.ClosureReturnTypeBaseName
                or CompileTimeStructuralFactKind.ClosureParameterTypeBaseName
                or CompileTimeStructuralFactKind.MethodReturnTypeBaseName
                or CompileTimeStructuralFactKind.MethodParameterTypeBaseName
                or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeBaseName
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeBaseName
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeBaseName
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeBaseName
                or CompileTimeStructuralFactKind.TypeArgumentTypeBaseName
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeBaseName
                or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeBaseName
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeBaseName
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeBaseName
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeBaseName =>
                TextConstant(coreType.Kind == StarkTypeKind.Named && coreType.NamedType is { } namedTypeName
                    ? StarkTypeSymbols.GetGenericBaseName(namedTypeName)
                    : string.Empty),
            CompileTimeStructuralFactKind.FieldTypeModuleName
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeModuleName
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeModuleName
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeModuleName
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeModuleName
                or CompileTimeStructuralFactKind.ClosureReturnTypeModuleName
                or CompileTimeStructuralFactKind.ClosureParameterTypeModuleName
                or CompileTimeStructuralFactKind.MethodReturnTypeModuleName
                or CompileTimeStructuralFactKind.MethodParameterTypeModuleName
                or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeModuleName
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeModuleName
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeModuleName
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeModuleName
                or CompileTimeStructuralFactKind.TypeArgumentTypeModuleName
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeModuleName
                or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeModuleName
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeModuleName
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeModuleName
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeModuleName =>
                TextConstant(GetNestedTypeModuleName(coreType, resolveNamedType)),
            CompileTimeStructuralFactKind.FieldTypeHasCSourceAlias
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeHasCSourceAlias
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeHasCSourceAlias
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeHasCSourceAlias
                or CompileTimeStructuralFactKind.ClosureReturnTypeHasCSourceAlias
                or CompileTimeStructuralFactKind.ClosureParameterTypeHasCSourceAlias
                or CompileTimeStructuralFactKind.MethodReturnTypeHasCSourceAlias
                or CompileTimeStructuralFactKind.MethodParameterTypeHasCSourceAlias =>
                EvaluateCSourceAliasMetadataFact(hasAliasFact: true, coreType),
            CompileTimeStructuralFactKind.FieldTypeCSourceAliasName
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeCSourceAliasName
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeCSourceAliasName
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeCSourceAliasName
                or CompileTimeStructuralFactKind.ClosureReturnTypeCSourceAliasName
                or CompileTimeStructuralFactKind.ClosureParameterTypeCSourceAliasName
                or CompileTimeStructuralFactKind.MethodReturnTypeCSourceAliasName
                or CompileTimeStructuralFactKind.MethodParameterTypeCSourceAliasName =>
                EvaluateCSourceAliasMetadataFact(hasAliasFact: false, coreType),
            CompileTimeStructuralFactKind.FieldTypeIsGenericInstantiation
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsGenericInstantiation
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsGenericInstantiation
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsGenericInstantiation
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsGenericInstantiation
                or CompileTimeStructuralFactKind.ClosureReturnTypeIsGenericInstantiation
                or CompileTimeStructuralFactKind.ClosureParameterTypeIsGenericInstantiation
                or CompileTimeStructuralFactKind.MethodReturnTypeIsGenericInstantiation
                or CompileTimeStructuralFactKind.MethodParameterTypeIsGenericInstantiation
                or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsGenericInstantiation
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsGenericInstantiation
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsGenericInstantiation
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsGenericInstantiation
                or CompileTimeStructuralFactKind.TypeArgumentTypeIsGenericInstantiation
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsGenericInstantiation
                or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsGenericInstantiation
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsGenericInstantiation
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsGenericInstantiation
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsGenericInstantiation =>
                CompileTimeConstant.Bool(StarkTypeSymbols.IsGenericInstantiation(coreType)),
            CompileTimeStructuralFactKind.FieldTypeArgumentCount
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeArgumentCount
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeArgumentCount
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeArgumentCount
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeArgumentCount
                or CompileTimeStructuralFactKind.ClosureReturnTypeArgumentCount
                or CompileTimeStructuralFactKind.ClosureParameterTypeArgumentCount
                or CompileTimeStructuralFactKind.MethodReturnTypeArgumentCount
                or CompileTimeStructuralFactKind.MethodParameterTypeArgumentCount
                or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeArgumentCount
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeArgumentCount
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeArgumentCount
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeArgumentCount
                or CompileTimeStructuralFactKind.TypeArgumentTypeArgumentCount
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeArgumentCount
                or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeArgumentCount
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeArgumentCount
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeArgumentCount
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeArgumentCount =>
                CompileTimeConstant.Integer(coreType.TypeArguments?.Count ?? 0, CountType),
            CompileTimeStructuralFactKind.FieldTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.ClosureReturnTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.ClosureParameterTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.MethodReturnTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.MethodParameterTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.TypeArgumentTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeComptimeArgumentCount
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeComptimeArgumentCount =>
                CompileTimeConstant.Integer(coreType.ComptimeValueArguments?.Count ?? 0, CountType),
            _ => CompileTimeConstant.Bool(false)
        };
    }

    private static CompileTimeConstant EvaluateNestedTypeQualifierMetadataFact(
        CompileTimeStructuralFactKind kind,
        StarkTypeSymbol type,
        CompileTimeStructuralFactArguments? arguments)
    {
        return kind switch
        {
            CompileTimeStructuralFactKind.FieldTypeHasQualifiers
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeHasQualifiers
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeHasQualifiers
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeHasQualifiers
                or CompileTimeStructuralFactKind.ClosureReturnTypeHasQualifiers
                or CompileTimeStructuralFactKind.ClosureParameterTypeHasQualifiers
                or CompileTimeStructuralFactKind.MethodReturnTypeHasQualifiers
                or CompileTimeStructuralFactKind.MethodParameterTypeHasQualifiers =>
                CompileTimeConstant.Bool(HasTopLevelQualifiers(type)),
            CompileTimeStructuralFactKind.FieldTypeBorrowKindIsNone
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeBorrowKindIsNone
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeBorrowKindIsNone
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeBorrowKindIsNone
                or CompileTimeStructuralFactKind.ClosureReturnTypeBorrowKindIsNone
                or CompileTimeStructuralFactKind.ClosureParameterTypeBorrowKindIsNone
                or CompileTimeStructuralFactKind.MethodReturnTypeBorrowKindIsNone
                or CompileTimeStructuralFactKind.MethodParameterTypeBorrowKindIsNone =>
                CompileTimeConstant.Bool(type.BorrowKind == StarkBorrowKind.None),
            CompileTimeStructuralFactKind.FieldTypeBorrowKindIsBorrow
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeBorrowKindIsBorrow
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeBorrowKindIsBorrow
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeBorrowKindIsBorrow
                or CompileTimeStructuralFactKind.ClosureReturnTypeBorrowKindIsBorrow
                or CompileTimeStructuralFactKind.ClosureParameterTypeBorrowKindIsBorrow
                or CompileTimeStructuralFactKind.MethodReturnTypeBorrowKindIsBorrow
                or CompileTimeStructuralFactKind.MethodParameterTypeBorrowKindIsBorrow =>
                CompileTimeConstant.Bool(type.BorrowKind == StarkBorrowKind.Borrow),
            CompileTimeStructuralFactKind.FieldTypeBorrowKindIsRetBorrow
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeBorrowKindIsRetBorrow
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeBorrowKindIsRetBorrow
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeBorrowKindIsRetBorrow
                or CompileTimeStructuralFactKind.ClosureReturnTypeBorrowKindIsRetBorrow
                or CompileTimeStructuralFactKind.ClosureParameterTypeBorrowKindIsRetBorrow
                or CompileTimeStructuralFactKind.MethodReturnTypeBorrowKindIsRetBorrow
                or CompileTimeStructuralFactKind.MethodParameterTypeBorrowKindIsRetBorrow =>
                CompileTimeConstant.Bool(type.BorrowKind == StarkBorrowKind.RetBorrow),
            CompileTimeStructuralFactKind.FieldTypeBorrowKindIsStoreBorrow
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeBorrowKindIsStoreBorrow
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeBorrowKindIsStoreBorrow
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeBorrowKindIsStoreBorrow
                or CompileTimeStructuralFactKind.ClosureReturnTypeBorrowKindIsStoreBorrow
                or CompileTimeStructuralFactKind.ClosureParameterTypeBorrowKindIsStoreBorrow
                or CompileTimeStructuralFactKind.MethodReturnTypeBorrowKindIsStoreBorrow
                or CompileTimeStructuralFactKind.MethodParameterTypeBorrowKindIsStoreBorrow =>
                CompileTimeConstant.Bool(type.BorrowKind == StarkBorrowKind.StoreBorrow),
            CompileTimeStructuralFactKind.FieldTypeAccessKindIsNone
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeAccessKindIsNone
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeAccessKindIsNone
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeAccessKindIsNone
                or CompileTimeStructuralFactKind.ClosureReturnTypeAccessKindIsNone
                or CompileTimeStructuralFactKind.ClosureParameterTypeAccessKindIsNone
                or CompileTimeStructuralFactKind.MethodReturnTypeAccessKindIsNone
                or CompileTimeStructuralFactKind.MethodParameterTypeAccessKindIsNone =>
                CompileTimeConstant.Bool(type.AccessKind == StarkAccessKind.None),
            CompileTimeStructuralFactKind.FieldTypeAccessKindIsShared
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeAccessKindIsShared
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeAccessKindIsShared
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeAccessKindIsShared
                or CompileTimeStructuralFactKind.ClosureReturnTypeAccessKindIsShared
                or CompileTimeStructuralFactKind.ClosureParameterTypeAccessKindIsShared
                or CompileTimeStructuralFactKind.MethodReturnTypeAccessKindIsShared
                or CompileTimeStructuralFactKind.MethodParameterTypeAccessKindIsShared =>
                CompileTimeConstant.Bool(type.AccessKind == StarkAccessKind.Shared),
            CompileTimeStructuralFactKind.FieldTypeAccessKindIsFrozen
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeAccessKindIsFrozen
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeAccessKindIsFrozen
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeAccessKindIsFrozen
                or CompileTimeStructuralFactKind.ClosureReturnTypeAccessKindIsFrozen
                or CompileTimeStructuralFactKind.ClosureParameterTypeAccessKindIsFrozen
                or CompileTimeStructuralFactKind.MethodReturnTypeAccessKindIsFrozen
                or CompileTimeStructuralFactKind.MethodParameterTypeAccessKindIsFrozen =>
                CompileTimeConstant.Bool(type.AccessKind == StarkAccessKind.Frozen),
            CompileTimeStructuralFactKind.FieldTypeInitializationKindIsNone
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeInitializationKindIsNone
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeInitializationKindIsNone
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeInitializationKindIsNone
                or CompileTimeStructuralFactKind.ClosureReturnTypeInitializationKindIsNone
                or CompileTimeStructuralFactKind.ClosureParameterTypeInitializationKindIsNone
                or CompileTimeStructuralFactKind.MethodReturnTypeInitializationKindIsNone
                or CompileTimeStructuralFactKind.MethodParameterTypeInitializationKindIsNone =>
                CompileTimeConstant.Bool(type.InitializationKind == StarkInitializationKind.None),
            CompileTimeStructuralFactKind.FieldTypeInitializationKindIsOut
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeInitializationKindIsOut
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeInitializationKindIsOut
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeInitializationKindIsOut
                or CompileTimeStructuralFactKind.ClosureReturnTypeInitializationKindIsOut
                or CompileTimeStructuralFactKind.ClosureParameterTypeInitializationKindIsOut
                or CompileTimeStructuralFactKind.MethodReturnTypeInitializationKindIsOut
                or CompileTimeStructuralFactKind.MethodParameterTypeInitializationKindIsOut =>
                CompileTimeConstant.Bool(type.InitializationKind == StarkInitializationKind.Out),
            CompileTimeStructuralFactKind.FieldTypeInitializationKindIsInit
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeInitializationKindIsInit
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeInitializationKindIsInit
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeInitializationKindIsInit
                or CompileTimeStructuralFactKind.ClosureReturnTypeInitializationKindIsInit
                or CompileTimeStructuralFactKind.ClosureParameterTypeInitializationKindIsInit
                or CompileTimeStructuralFactKind.MethodReturnTypeInitializationKindIsInit
                or CompileTimeStructuralFactKind.MethodParameterTypeInitializationKindIsInit =>
                CompileTimeConstant.Bool(type.InitializationKind == StarkInitializationKind.Init),
            CompileTimeStructuralFactKind.FieldTypeIsMutableView
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsMutableView
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsMutableView
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsMutableView
                or CompileTimeStructuralFactKind.ClosureReturnTypeIsMutableView
                or CompileTimeStructuralFactKind.ClosureParameterTypeIsMutableView
                or CompileTimeStructuralFactKind.MethodReturnTypeIsMutableView
                or CompileTimeStructuralFactKind.MethodParameterTypeIsMutableView =>
                CompileTimeConstant.Bool(type.IsMutableView),
            CompileTimeStructuralFactKind.FieldTypeUnqualifiedTypeIs
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeUnqualifiedTypeIs
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeUnqualifiedTypeIs
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeUnqualifiedTypeIs
                or CompileTimeStructuralFactKind.ClosureReturnTypeUnqualifiedTypeIs
                or CompileTimeStructuralFactKind.ClosureParameterTypeUnqualifiedTypeIs
                or CompileTimeStructuralFactKind.MethodReturnTypeUnqualifiedTypeIs
                or CompileTimeStructuralFactKind.MethodParameterTypeUnqualifiedTypeIs =>
                CompileTimeConstant.Bool(
                    arguments is { AdditionalTypeArguments.Count: 1 }
                    && TypesEquivalent(type, arguments.AdditionalTypeArguments[0])),
            _ => CompileTimeConstant.Bool(false)
        };
    }

    private static string GetModuleName(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return string.Empty;
        }

        var baseName = StarkTypeSymbols.GetGenericBaseName(typeName);
        var separator = baseName.LastIndexOf('.');
        return separator > 0 ? baseName[..separator] : string.Empty;
    }

    private static string GetNestedTypeModuleName(
        StarkTypeSymbol type,
        Func<StarkTypeSymbol, NamedTypeSymbol?>? resolveNamedType = null)
    {
        var coreType = NormalizeTypeForComparison(type);
        if (coreType.Kind != StarkTypeKind.Named || coreType.NamedType is not { } namedTypeName)
        {
            return string.Empty;
        }

        // Root-module types are keyed without a module prefix, so the declaring
        // module recorded on the resolved symbol (or its generic template) is
        // authoritative.
        return resolveNamedType?.Invoke(coreType)?.DeclaringModuleName
            ?? resolveNamedType?.Invoke(StarkTypeSymbols.Named(StarkTypeSymbols.GetGenericBaseName(namedTypeName)))?.DeclaringModuleName
            ?? GetModuleName(namedTypeName);
    }

    private static CompileTimeStructuralTypePredicate GetNestedTypePredicate(
        CompileTimeStructuralFactKind kind)
    {
        return kind switch
        {
            CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsBool
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsBool
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsBool
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsBool =>
                CompileTimeStructuralTypePredicate.Bool,
            CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsInteger
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsInteger
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsInteger
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsInteger =>
                CompileTimeStructuralTypePredicate.Integer,
            CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsFloat
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsFloat
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsFloat
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsFloat =>
                CompileTimeStructuralTypePredicate.Float,
            CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsRawPointer
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsRawPointer
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsRawPointer
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsRawPointer =>
                CompileTimeStructuralTypePredicate.RawPointer,
            CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsFixedArray
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsFixedArray
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsFixedArray
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsFixedArray =>
                CompileTimeStructuralTypePredicate.FixedArray,
            CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsSlice
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsSlice
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsSlice
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsSlice =>
                CompileTimeStructuralTypePredicate.Slice,
            CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsDynamic
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsDynamic
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsDynamic
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsDynamic =>
                CompileTimeStructuralTypePredicate.Dynamic,
            CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsFunctionPointer
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsFunctionPointer
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsFunctionPointer
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsFunctionPointer =>
                CompileTimeStructuralTypePredicate.FunctionPointer,
            CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsClosure
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsClosure
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsClosure
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsClosure =>
                CompileTimeStructuralTypePredicate.Closure,
            CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsNamed
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsNamed
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsNamed
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsNamed =>
                CompileTimeStructuralTypePredicate.Named,
            CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsStruct
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsStruct
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsStruct
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsStruct =>
                CompileTimeStructuralTypePredicate.Struct,
            CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsRecord
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsRecord
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsRecord
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsRecord =>
                CompileTimeStructuralTypePredicate.Record,
            CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsEnum
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsEnum
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsEnum
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsEnum =>
                CompileTimeStructuralTypePredicate.Enum,
            CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsTrait
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsTrait
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsTrait
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsTrait =>
                CompileTimeStructuralTypePredicate.Trait,
            CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsDoctrine
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsDoctrine
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsDoctrine
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsDoctrine =>
                CompileTimeStructuralTypePredicate.Doctrine,
            CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeHasConcreteLayout
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeHasConcreteLayout
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeHasConcreteLayout
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeHasConcreteLayout =>
                CompileTimeStructuralTypePredicate.ConcreteLayout,
            _ => CompileTimeStructuralTypePredicate.None
        };
    }

    private static CompileTimeStructuralTypePredicate GetTypePredicate(CompileTimeStructuralFactKind kind)
    {
        var callableNestedArgumentPredicate = GetCallableNestedTypeArgumentPredicate(kind);
        if (callableNestedArgumentPredicate != CompileTimeStructuralTypePredicate.None)
        {
            return callableNestedArgumentPredicate;
        }

        var implementedTraitArgumentPredicate = kind switch
        {
            CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsBool
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsBool =>
                CompileTimeStructuralTypePredicate.Bool,
            CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsInteger
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsInteger =>
                CompileTimeStructuralTypePredicate.Integer,
            CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsFloat
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsFloat =>
                CompileTimeStructuralTypePredicate.Float,
            CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsRawPointer
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsRawPointer =>
                CompileTimeStructuralTypePredicate.RawPointer,
            CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsFixedArray
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsFixedArray =>
                CompileTimeStructuralTypePredicate.FixedArray,
            CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsSlice
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsSlice =>
                CompileTimeStructuralTypePredicate.Slice,
            CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsDynamic
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsDynamic =>
                CompileTimeStructuralTypePredicate.Dynamic,
            CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsFunctionPointer
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsFunctionPointer =>
                CompileTimeStructuralTypePredicate.FunctionPointer,
            CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsClosure
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsClosure =>
                CompileTimeStructuralTypePredicate.Closure,
            CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsNamed
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsNamed =>
                CompileTimeStructuralTypePredicate.Named,
            CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsStruct
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsStruct =>
                CompileTimeStructuralTypePredicate.Struct,
            CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsRecord
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsRecord =>
                CompileTimeStructuralTypePredicate.Record,
            CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsEnum
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsEnum =>
                CompileTimeStructuralTypePredicate.Enum,
            CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsTrait
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsTrait =>
                CompileTimeStructuralTypePredicate.Trait,
            CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeIsDoctrine
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeIsDoctrine =>
                CompileTimeStructuralTypePredicate.Doctrine,
            CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentTypeHasConcreteLayout
                or CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentTypeHasConcreteLayout =>
                CompileTimeStructuralTypePredicate.ConcreteLayout,
            _ => CompileTimeStructuralTypePredicate.None
        };
        if (implementedTraitArgumentPredicate != CompileTimeStructuralTypePredicate.None)
        {
            return implementedTraitArgumentPredicate;
        }

        return kind switch
        {
            CompileTimeStructuralFactKind.IsBool
                or CompileTimeStructuralFactKind.FieldTypeIsBool
                or CompileTimeStructuralFactKind.ImplementedTraitTypeIsBool
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsBool
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsBool
                or CompileTimeStructuralFactKind.ClosureReturnTypeIsBool
                or CompileTimeStructuralFactKind.ClosureParameterTypeIsBool
                or CompileTimeStructuralFactKind.MethodReturnTypeIsBool
                or CompileTimeStructuralFactKind.MethodParameterTypeIsBool
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsBool
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsBool
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsBool
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsBool
                or CompileTimeStructuralFactKind.TypeArgumentTypeIsBool
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsBool
                or CompileTimeStructuralFactKind.RawPointerElementTypeIsBool
                or CompileTimeStructuralFactKind.TypeElementTypeIsBool
                    => CompileTimeStructuralTypePredicate.Bool,
            CompileTimeStructuralFactKind.IsInteger
                or CompileTimeStructuralFactKind.FieldTypeIsInteger
                or CompileTimeStructuralFactKind.ImplementedTraitTypeIsInteger
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsInteger
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsInteger
                or CompileTimeStructuralFactKind.ClosureReturnTypeIsInteger
                or CompileTimeStructuralFactKind.ClosureParameterTypeIsInteger
                or CompileTimeStructuralFactKind.MethodReturnTypeIsInteger
                or CompileTimeStructuralFactKind.MethodParameterTypeIsInteger
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsInteger
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsInteger
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsInteger
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsInteger
                or CompileTimeStructuralFactKind.TypeArgumentTypeIsInteger
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsInteger
                or CompileTimeStructuralFactKind.RawPointerElementTypeIsInteger
                or CompileTimeStructuralFactKind.TypeElementTypeIsInteger
                    => CompileTimeStructuralTypePredicate.Integer,
            CompileTimeStructuralFactKind.IsFloat
                or CompileTimeStructuralFactKind.FieldTypeIsFloat
                or CompileTimeStructuralFactKind.ImplementedTraitTypeIsFloat
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsFloat
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsFloat
                or CompileTimeStructuralFactKind.ClosureReturnTypeIsFloat
                or CompileTimeStructuralFactKind.ClosureParameterTypeIsFloat
                or CompileTimeStructuralFactKind.MethodReturnTypeIsFloat
                or CompileTimeStructuralFactKind.MethodParameterTypeIsFloat
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsFloat
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsFloat
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsFloat
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsFloat
                or CompileTimeStructuralFactKind.TypeArgumentTypeIsFloat
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsFloat
                or CompileTimeStructuralFactKind.RawPointerElementTypeIsFloat
                or CompileTimeStructuralFactKind.TypeElementTypeIsFloat
                    => CompileTimeStructuralTypePredicate.Float,
            CompileTimeStructuralFactKind.IsRawPointer
                or CompileTimeStructuralFactKind.FieldTypeIsRawPointer
                or CompileTimeStructuralFactKind.ImplementedTraitTypeIsRawPointer
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsRawPointer
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsRawPointer
                or CompileTimeStructuralFactKind.ClosureReturnTypeIsRawPointer
                or CompileTimeStructuralFactKind.ClosureParameterTypeIsRawPointer
                or CompileTimeStructuralFactKind.MethodReturnTypeIsRawPointer
                or CompileTimeStructuralFactKind.MethodParameterTypeIsRawPointer
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsRawPointer
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsRawPointer
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsRawPointer
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsRawPointer
                or CompileTimeStructuralFactKind.TypeArgumentTypeIsRawPointer
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsRawPointer
                or CompileTimeStructuralFactKind.RawPointerElementTypeIsRawPointer
                or CompileTimeStructuralFactKind.TypeElementTypeIsRawPointer
                    => CompileTimeStructuralTypePredicate.RawPointer,
            CompileTimeStructuralFactKind.IsFixedArray
                or CompileTimeStructuralFactKind.FieldTypeIsFixedArray
                or CompileTimeStructuralFactKind.ImplementedTraitTypeIsFixedArray
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsFixedArray
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsFixedArray
                or CompileTimeStructuralFactKind.ClosureReturnTypeIsFixedArray
                or CompileTimeStructuralFactKind.ClosureParameterTypeIsFixedArray
                or CompileTimeStructuralFactKind.MethodReturnTypeIsFixedArray
                or CompileTimeStructuralFactKind.MethodParameterTypeIsFixedArray
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsFixedArray
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsFixedArray
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsFixedArray
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsFixedArray
                or CompileTimeStructuralFactKind.TypeArgumentTypeIsFixedArray
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsFixedArray
                or CompileTimeStructuralFactKind.RawPointerElementTypeIsFixedArray
                or CompileTimeStructuralFactKind.TypeElementTypeIsFixedArray
                    => CompileTimeStructuralTypePredicate.FixedArray,
            CompileTimeStructuralFactKind.IsSlice
                or CompileTimeStructuralFactKind.FieldTypeIsSlice
                or CompileTimeStructuralFactKind.ImplementedTraitTypeIsSlice
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsSlice
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsSlice
                or CompileTimeStructuralFactKind.ClosureReturnTypeIsSlice
                or CompileTimeStructuralFactKind.ClosureParameterTypeIsSlice
                or CompileTimeStructuralFactKind.MethodReturnTypeIsSlice
                or CompileTimeStructuralFactKind.MethodParameterTypeIsSlice
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsSlice
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsSlice
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsSlice
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsSlice
                or CompileTimeStructuralFactKind.TypeArgumentTypeIsSlice
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsSlice
                or CompileTimeStructuralFactKind.RawPointerElementTypeIsSlice
                or CompileTimeStructuralFactKind.TypeElementTypeIsSlice
                    => CompileTimeStructuralTypePredicate.Slice,
            CompileTimeStructuralFactKind.IsDynamic
                or CompileTimeStructuralFactKind.FieldTypeIsDynamic
                or CompileTimeStructuralFactKind.ImplementedTraitTypeIsDynamic
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsDynamic
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsDynamic
                or CompileTimeStructuralFactKind.ClosureReturnTypeIsDynamic
                or CompileTimeStructuralFactKind.ClosureParameterTypeIsDynamic
                or CompileTimeStructuralFactKind.MethodReturnTypeIsDynamic
                or CompileTimeStructuralFactKind.MethodParameterTypeIsDynamic
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsDynamic
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsDynamic
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsDynamic
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsDynamic
                or CompileTimeStructuralFactKind.TypeArgumentTypeIsDynamic
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsDynamic
                or CompileTimeStructuralFactKind.RawPointerElementTypeIsDynamic
                or CompileTimeStructuralFactKind.TypeElementTypeIsDynamic
                    => CompileTimeStructuralTypePredicate.Dynamic,
            CompileTimeStructuralFactKind.IsFunctionPointer
                or CompileTimeStructuralFactKind.FieldTypeIsFunctionPointer
                or CompileTimeStructuralFactKind.ImplementedTraitTypeIsFunctionPointer
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsFunctionPointer
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsFunctionPointer
                or CompileTimeStructuralFactKind.ClosureReturnTypeIsFunctionPointer
                or CompileTimeStructuralFactKind.ClosureParameterTypeIsFunctionPointer
                or CompileTimeStructuralFactKind.MethodReturnTypeIsFunctionPointer
                or CompileTimeStructuralFactKind.MethodParameterTypeIsFunctionPointer
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsFunctionPointer
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsFunctionPointer
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsFunctionPointer
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsFunctionPointer
                or CompileTimeStructuralFactKind.TypeArgumentTypeIsFunctionPointer
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsFunctionPointer
                or CompileTimeStructuralFactKind.RawPointerElementTypeIsFunctionPointer
                or CompileTimeStructuralFactKind.TypeElementTypeIsFunctionPointer
                    => CompileTimeStructuralTypePredicate.FunctionPointer,
            CompileTimeStructuralFactKind.IsClosure
                or CompileTimeStructuralFactKind.FieldTypeIsClosure
                or CompileTimeStructuralFactKind.ImplementedTraitTypeIsClosure
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsClosure
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsClosure
                or CompileTimeStructuralFactKind.ClosureReturnTypeIsClosure
                or CompileTimeStructuralFactKind.ClosureParameterTypeIsClosure
                or CompileTimeStructuralFactKind.MethodReturnTypeIsClosure
                or CompileTimeStructuralFactKind.MethodParameterTypeIsClosure
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsClosure
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsClosure
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsClosure
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsClosure
                or CompileTimeStructuralFactKind.TypeArgumentTypeIsClosure
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsClosure
                or CompileTimeStructuralFactKind.RawPointerElementTypeIsClosure
                or CompileTimeStructuralFactKind.TypeElementTypeIsClosure
                    => CompileTimeStructuralTypePredicate.Closure,
            CompileTimeStructuralFactKind.IsDynTrait
                or CompileTimeStructuralFactKind.FieldTypeIsDynTrait
                or CompileTimeStructuralFactKind.ImplementedTraitTypeIsDynTrait
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsDynTrait
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsDynTrait
                or CompileTimeStructuralFactKind.ClosureReturnTypeIsDynTrait
                or CompileTimeStructuralFactKind.ClosureParameterTypeIsDynTrait
                or CompileTimeStructuralFactKind.MethodReturnTypeIsDynTrait
                or CompileTimeStructuralFactKind.MethodParameterTypeIsDynTrait
                or CompileTimeStructuralFactKind.MethodGenericParameterTraitBoundTypeIsDynTrait
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsDynTrait
                or CompileTimeStructuralFactKind.MethodThreadSafetyLawPredicateTypeIsDynTrait
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsDynTrait
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsDynTrait
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsDynTrait
                or CompileTimeStructuralFactKind.TypeArgumentTypeIsDynTrait
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsDynTrait
                or CompileTimeStructuralFactKind.RawPointerElementTypeIsDynTrait
                or CompileTimeStructuralFactKind.TypeElementTypeIsDynTrait
                or CompileTimeStructuralFactKind.TypeThreadSafetyLawAttributeConditionTypeIsDynTrait
                or CompileTimeStructuralFactKind.FieldThreadSafetyLawAttributeConditionTypeIsDynTrait
                    => CompileTimeStructuralTypePredicate.DynTrait,
            CompileTimeStructuralFactKind.IsNamed
                or CompileTimeStructuralFactKind.FieldTypeIsNamed
                or CompileTimeStructuralFactKind.ImplementedTraitTypeIsNamed
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsNamed
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsNamed
                or CompileTimeStructuralFactKind.ClosureReturnTypeIsNamed
                or CompileTimeStructuralFactKind.ClosureParameterTypeIsNamed
                or CompileTimeStructuralFactKind.MethodReturnTypeIsNamed
                or CompileTimeStructuralFactKind.MethodParameterTypeIsNamed
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsNamed
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsNamed
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsNamed
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsNamed
                or CompileTimeStructuralFactKind.TypeArgumentTypeIsNamed
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsNamed
                or CompileTimeStructuralFactKind.RawPointerElementTypeIsNamed
                or CompileTimeStructuralFactKind.TypeElementTypeIsNamed
                    => CompileTimeStructuralTypePredicate.Named,
            CompileTimeStructuralFactKind.IsStruct
                or CompileTimeStructuralFactKind.FieldTypeIsStruct
                or CompileTimeStructuralFactKind.ImplementedTraitTypeIsStruct
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsStruct
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsStruct
                or CompileTimeStructuralFactKind.ClosureReturnTypeIsStruct
                or CompileTimeStructuralFactKind.ClosureParameterTypeIsStruct
                or CompileTimeStructuralFactKind.MethodReturnTypeIsStruct
                or CompileTimeStructuralFactKind.MethodParameterTypeIsStruct
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsStruct
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsStruct
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsStruct
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsStruct
                or CompileTimeStructuralFactKind.TypeArgumentTypeIsStruct
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsStruct
                or CompileTimeStructuralFactKind.RawPointerElementTypeIsStruct
                or CompileTimeStructuralFactKind.TypeElementTypeIsStruct
                    => CompileTimeStructuralTypePredicate.Struct,
            CompileTimeStructuralFactKind.IsRecord
                or CompileTimeStructuralFactKind.FieldTypeIsRecord
                or CompileTimeStructuralFactKind.ImplementedTraitTypeIsRecord
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsRecord
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsRecord
                or CompileTimeStructuralFactKind.ClosureReturnTypeIsRecord
                or CompileTimeStructuralFactKind.ClosureParameterTypeIsRecord
                or CompileTimeStructuralFactKind.MethodReturnTypeIsRecord
                or CompileTimeStructuralFactKind.MethodParameterTypeIsRecord
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsRecord
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsRecord
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsRecord
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsRecord
                or CompileTimeStructuralFactKind.TypeArgumentTypeIsRecord
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsRecord
                or CompileTimeStructuralFactKind.RawPointerElementTypeIsRecord
                or CompileTimeStructuralFactKind.TypeElementTypeIsRecord
                    => CompileTimeStructuralTypePredicate.Record,
            CompileTimeStructuralFactKind.IsEnum
                or CompileTimeStructuralFactKind.FieldTypeIsEnum
                or CompileTimeStructuralFactKind.ImplementedTraitTypeIsEnum
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsEnum
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsEnum
                or CompileTimeStructuralFactKind.ClosureReturnTypeIsEnum
                or CompileTimeStructuralFactKind.ClosureParameterTypeIsEnum
                or CompileTimeStructuralFactKind.MethodReturnTypeIsEnum
                or CompileTimeStructuralFactKind.MethodParameterTypeIsEnum
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsEnum
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsEnum
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsEnum
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsEnum
                or CompileTimeStructuralFactKind.TypeArgumentTypeIsEnum
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsEnum
                or CompileTimeStructuralFactKind.RawPointerElementTypeIsEnum
                or CompileTimeStructuralFactKind.TypeElementTypeIsEnum
                    => CompileTimeStructuralTypePredicate.Enum,
            CompileTimeStructuralFactKind.IsTrait
                or CompileTimeStructuralFactKind.FieldTypeIsTrait
                or CompileTimeStructuralFactKind.ImplementedTraitTypeIsTrait
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsTrait
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsTrait
                or CompileTimeStructuralFactKind.ClosureReturnTypeIsTrait
                or CompileTimeStructuralFactKind.ClosureParameterTypeIsTrait
                or CompileTimeStructuralFactKind.MethodReturnTypeIsTrait
                or CompileTimeStructuralFactKind.MethodParameterTypeIsTrait
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsTrait
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsTrait
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsTrait
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsTrait
                or CompileTimeStructuralFactKind.TypeArgumentTypeIsTrait
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsTrait
                or CompileTimeStructuralFactKind.RawPointerElementTypeIsTrait
                or CompileTimeStructuralFactKind.TypeElementTypeIsTrait
                    => CompileTimeStructuralTypePredicate.Trait,
            CompileTimeStructuralFactKind.IsDoctrine
                or CompileTimeStructuralFactKind.FieldTypeIsDoctrine
                or CompileTimeStructuralFactKind.ImplementedTraitTypeIsDoctrine
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeIsDoctrine
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeIsDoctrine
                or CompileTimeStructuralFactKind.ClosureReturnTypeIsDoctrine
                or CompileTimeStructuralFactKind.ClosureParameterTypeIsDoctrine
                or CompileTimeStructuralFactKind.MethodReturnTypeIsDoctrine
                or CompileTimeStructuralFactKind.MethodParameterTypeIsDoctrine
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeIsDoctrine
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeIsDoctrine
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeIsDoctrine
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeIsDoctrine
                or CompileTimeStructuralFactKind.TypeArgumentTypeIsDoctrine
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeIsDoctrine
                or CompileTimeStructuralFactKind.RawPointerElementTypeIsDoctrine
                or CompileTimeStructuralFactKind.TypeElementTypeIsDoctrine
                    => CompileTimeStructuralTypePredicate.Doctrine,
            CompileTimeStructuralFactKind.HasConcreteLayout
                or CompileTimeStructuralFactKind.FieldTypeHasConcreteLayout
                or CompileTimeStructuralFactKind.ImplementedTraitTypeHasConcreteLayout
                or CompileTimeStructuralFactKind.FunctionPointerReturnTypeHasConcreteLayout
                or CompileTimeStructuralFactKind.FunctionPointerParameterTypeHasConcreteLayout
                or CompileTimeStructuralFactKind.ClosureReturnTypeHasConcreteLayout
                or CompileTimeStructuralFactKind.ClosureParameterTypeHasConcreteLayout
                or CompileTimeStructuralFactKind.MethodReturnTypeHasConcreteLayout
                or CompileTimeStructuralFactKind.MethodParameterTypeHasConcreteLayout
                or CompileTimeStructuralFactKind.MethodComptimeGenericParameterTypeHasConcreteLayout
                or CompileTimeStructuralFactKind.EnumVariantPayloadTypeHasConcreteLayout
                or CompileTimeStructuralFactKind.AssociatedTypeTargetTypeHasConcreteLayout
                or CompileTimeStructuralFactKind.TypeComptimeGenericParameterTypeHasConcreteLayout
                or CompileTimeStructuralFactKind.TypeArgumentTypeHasConcreteLayout
                or CompileTimeStructuralFactKind.TypeComptimeArgumentTypeHasConcreteLayout
                or CompileTimeStructuralFactKind.RawPointerElementTypeHasConcreteLayout
                or CompileTimeStructuralFactKind.TypeElementTypeHasConcreteLayout
                    => CompileTimeStructuralTypePredicate.ConcreteLayout,
            _ => CompileTimeStructuralTypePredicate.None
        };
    }

    private static CompileTimeStructuralTypePredicate GetCallableNestedTypeArgumentPredicate(
        CompileTimeStructuralFactKind kind)
    {
        return GetCallableNestedTypeArgumentOffset(kind) switch
        {
            1 => CompileTimeStructuralTypePredicate.Bool,
            2 => CompileTimeStructuralTypePredicate.Integer,
            3 => CompileTimeStructuralTypePredicate.Float,
            4 => CompileTimeStructuralTypePredicate.RawPointer,
            5 => CompileTimeStructuralTypePredicate.FixedArray,
            6 => CompileTimeStructuralTypePredicate.Slice,
            7 => CompileTimeStructuralTypePredicate.Dynamic,
            8 => CompileTimeStructuralTypePredicate.FunctionPointer,
            9 => CompileTimeStructuralTypePredicate.Closure,
            10 => CompileTimeStructuralTypePredicate.DynTrait,
            11 => CompileTimeStructuralTypePredicate.Named,
            12 => CompileTimeStructuralTypePredicate.Struct,
            13 => CompileTimeStructuralTypePredicate.Record,
            14 => CompileTimeStructuralTypePredicate.Enum,
            15 => CompileTimeStructuralTypePredicate.Trait,
            16 => CompileTimeStructuralTypePredicate.Doctrine,
            17 => CompileTimeStructuralTypePredicate.ConcreteLayout,
            _ => CompileTimeStructuralTypePredicate.None
        };
    }

    private static bool EvaluateTypePredicate(
        CompileTimeStructuralTypePredicate predicate,
        StarkTypeSymbol type,
        Func<StarkTypeSymbol, NamedTypeSymbol?> resolveNamedType,
        Func<StarkTypeSymbol, ConcreteTypeLayout?>? resolveConcreteLayout)
    {
        var coreType = NormalizeTypeForComparison(type);
        var namedType = coreType.Kind == StarkTypeKind.Named
            ? resolveNamedType(coreType)
            : null;
        return predicate switch
        {
            CompileTimeStructuralTypePredicate.Bool => coreType.Kind == StarkTypeKind.Bool,
            CompileTimeStructuralTypePredicate.Integer => coreType.Kind == StarkTypeKind.Integer,
            CompileTimeStructuralTypePredicate.Float => coreType.Kind == StarkTypeKind.Float,
            CompileTimeStructuralTypePredicate.RawPointer => coreType.Kind == StarkTypeKind.RawPointer,
            CompileTimeStructuralTypePredicate.FixedArray => coreType.Kind == StarkTypeKind.FixedArray,
            CompileTimeStructuralTypePredicate.Slice => coreType.Kind == StarkTypeKind.Slice,
            CompileTimeStructuralTypePredicate.Dynamic => coreType.Kind == StarkTypeKind.Dynamic,
            CompileTimeStructuralTypePredicate.FunctionPointer => coreType.Kind == StarkTypeKind.FunctionPointer,
            CompileTimeStructuralTypePredicate.Closure => coreType.Kind == StarkTypeKind.Closure,
            CompileTimeStructuralTypePredicate.DynTrait => coreType.Kind == StarkTypeKind.DynTrait,
            CompileTimeStructuralTypePredicate.Named => coreType.Kind == StarkTypeKind.Named,
            CompileTimeStructuralTypePredicate.Struct => namedType?.Kind == DeclarationKind.Struct,
            CompileTimeStructuralTypePredicate.Record => namedType?.Kind == DeclarationKind.Record,
            CompileTimeStructuralTypePredicate.Enum => namedType?.Kind == DeclarationKind.Enum,
            CompileTimeStructuralTypePredicate.Trait => namedType?.Kind == DeclarationKind.Trait,
            CompileTimeStructuralTypePredicate.Doctrine => namedType?.Kind == DeclarationKind.Doctrine,
            CompileTimeStructuralTypePredicate.ConcreteLayout => resolveConcreteLayout?.Invoke(coreType) is not null,
            _ => false
        };
    }

    private static bool TypesEquivalent(StarkTypeSymbol left, StarkTypeSymbol right)
    {
        return TypesEquivalentCore(NormalizeTypeForComparison(left), NormalizeTypeForComparison(right));
    }

    private static bool TypesEquivalentCore(StarkTypeSymbol left, StarkTypeSymbol right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Kind != right.Kind)
        {
            return false;
        }

        return left.Kind switch
        {
            StarkTypeKind.Integer =>
                left.BitWidth == right.BitWidth
                && left.IsUnsigned == right.IsUnsigned
                && left.RangeMin == right.RangeMin
                && left.RangeMax == right.RangeMax,
            StarkTypeKind.Float =>
                left.BitWidth == right.BitWidth,
            StarkTypeKind.RawPointer =>
                left.IsMutablePointer == right.IsMutablePointer
                && TypeNullableEquivalent(left.ElementType, right.ElementType),
            StarkTypeKind.FixedArray =>
                left.FixedLength == right.FixedLength
                && string.Equals(left.FixedLengthParameterName, right.FixedLengthParameterName, StringComparison.Ordinal)
                && TypeNullableEquivalent(left.ElementType, right.ElementType),
            StarkTypeKind.Slice
                or StarkTypeKind.Dynamic =>
                TypeNullableEquivalent(left.ElementType, right.ElementType),
            StarkTypeKind.FunctionPointer =>
                left.FunctionPointerKind == right.FunctionPointerKind
                && left.FunctionPointerAbi == right.FunctionPointerAbi
                && left.FunctionPointerIsUnsafe == right.FunctionPointerIsUnsafe
                && TypeNullableEquivalent(left.FunctionPointerReturnType, right.FunctionPointerReturnType)
                && TypeListEquivalent(left.FunctionPointerParameterTypes, right.FunctionPointerParameterTypes)
                && StringListEquivalent(
                    left.FunctionPointerParameterRawPointerElementCountExpressions,
                    right.FunctionPointerParameterRawPointerElementCountExpressions)
                && ListEquivalent(left.FunctionPointerDisjointParameterGroups, right.FunctionPointerDisjointParameterGroups)
                && ListEquivalent(left.FunctionPointerOverlapParameterGroups, right.FunctionPointerOverlapParameterGroups)
                && ListEquivalent(left.FunctionPointerSameParameterGroups, right.FunctionPointerSameParameterGroups),
            StarkTypeKind.Closure =>
                left.ClosureStorageKind == right.ClosureStorageKind
                && left.ClosureCallCapability == right.ClosureCallCapability
                && left.ClosureFunctionKind == right.ClosureFunctionKind
                && TypeNullableEquivalent(left.ClosureReturnType, right.ClosureReturnType)
                && TypeListEquivalent(left.ClosureParameterTypes, right.ClosureParameterTypes)
                && StringListEquivalent(
                    left.ClosureParameterRawPointerElementCountExpressions,
                    right.ClosureParameterRawPointerElementCountExpressions)
                && ListEquivalent(left.ClosureDisjointParameterGroups, right.ClosureDisjointParameterGroups)
                && ListEquivalent(left.ClosureOverlapParameterGroups, right.ClosureOverlapParameterGroups)
                && ListEquivalent(left.ClosureSameParameterGroups, right.ClosureSameParameterGroups),
            StarkTypeKind.Named =>
                NamedTypesEquivalent(left, right),
            StarkTypeKind.DynTrait =>
                string.Equals(left.DynTraitName, right.DynTraitName, StringComparison.Ordinal)
                && left.DynTraitStorageKind == right.DynTraitStorageKind
                && TypeListEquivalent(left.TypeArguments, right.TypeArguments),
            StarkTypeKind.AssociatedType =>
                string.Equals(left.AssociatedTypeName, right.AssociatedTypeName, StringComparison.Ordinal)
                && TypeNullableEquivalent(left.AssociatedTypeOwner, right.AssociatedTypeOwner),
            _ =>
                string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal)
        };
    }

    private static bool NamedTypesEquivalent(StarkTypeSymbol left, StarkTypeSymbol right)
    {
        var leftIsInstantiation = StarkTypeSymbols.IsGenericInstantiation(left);
        var rightIsInstantiation = StarkTypeSymbols.IsGenericInstantiation(right);
        if (leftIsInstantiation || rightIsInstantiation)
        {
            return leftIsInstantiation
                && rightIsInstantiation
                && string.Equals(
                    StarkTypeSymbols.GetGenericBaseName(left.NamedType ?? left.DisplayName),
                    StarkTypeSymbols.GetGenericBaseName(right.NamedType ?? right.DisplayName),
                    StringComparison.Ordinal)
                && TypeListEquivalent(left.TypeArguments, right.TypeArguments)
                && ComptimeValueArgumentListEquivalent(left.ComptimeValueArguments, right.ComptimeValueArguments);
        }

        return string.Equals(left.NamedType, right.NamedType, StringComparison.Ordinal)
            || (left.NamedType is null
                && right.NamedType is null
                && string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal));
    }

    private static bool TypeNullableEquivalent(StarkTypeSymbol? left, StarkTypeSymbol? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return TypesEquivalentCore(NormalizeTypeForComparison(left), NormalizeTypeForComparison(right));
    }

    private static bool TypeListEquivalent(
        IReadOnlyList<StarkTypeSymbol>? left,
        IReadOnlyList<StarkTypeSymbol>? right)
    {
        var leftCount = left?.Count ?? 0;
        var rightCount = right?.Count ?? 0;
        if (leftCount != rightCount)
        {
            return false;
        }

        for (var index = 0; index < leftCount; index++)
        {
            if (!TypesEquivalentCore(
                NormalizeTypeForComparison(left![index]),
                NormalizeTypeForComparison(right![index])))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ComptimeValueArgumentListEquivalent(
        IReadOnlyList<ComptimeValueArgumentSymbol>? left,
        IReadOnlyList<ComptimeValueArgumentSymbol>? right)
    {
        var leftCount = left?.Count ?? 0;
        var rightCount = right?.Count ?? 0;
        if (leftCount != rightCount)
        {
            return false;
        }

        for (var index = 0; index < leftCount; index++)
        {
            if (!ComptimeValueArgumentsEquivalent(left![index], right![index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ComptimeValueArgumentsEquivalent(
        ComptimeValueArgumentSymbol left,
        ComptimeValueArgumentSymbol right)
    {
        if (left.IsSymbolic || right.IsSymbolic)
        {
            return left.IsSymbolic == right.IsSymbolic
                && string.Equals(left.SourceName, right.SourceName, StringComparison.Ordinal)
                && TypesEquivalentCore(
                    NormalizeTypeForComparison(left.Type),
                    NormalizeTypeForComparison(right.Type));
        }

        return left.IntegerValue == right.IntegerValue
            && TypesEquivalentCore(
                NormalizeTypeForComparison(left.Type),
                NormalizeTypeForComparison(right.Type));
    }

    private static bool StringListEquivalent(
        IReadOnlyList<string?>? left,
        IReadOnlyList<string?>? right)
    {
        var leftCount = left?.Count ?? 0;
        var rightCount = right?.Count ?? 0;
        if (leftCount != rightCount)
        {
            return false;
        }

        for (var index = 0; index < leftCount; index++)
        {
            if (!string.Equals(left![index], right![index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ListEquivalent<T>(
        IReadOnlyList<T>? left,
        IReadOnlyList<T>? right)
    {
        var leftCount = left?.Count ?? 0;
        var rightCount = right?.Count ?? 0;
        if (leftCount != rightCount)
        {
            return false;
        }

        var comparer = EqualityComparer<T>.Default;
        for (var index = 0; index < leftCount; index++)
        {
            if (!comparer.Equals(left![index], right![index]))
            {
                return false;
            }
        }

        return true;
    }

    public static IReadOnlyList<TypedFunctionSignature> GetOrderedMethodSignatures(
        StarkTypeSymbol ownerType,
        NamedTypeSymbol ownerDefinition,
        IEnumerable<TypedFunctionSignature> candidates)
    {
        var ownerCore = NormalizeTypeForComparison(ownerType);
        return candidates
            .Where(signature => IsMethodSignatureForOwner(ownerCore, ownerDefinition, signature))
            .OrderBy(signature => GetMethodMemberName(ownerDefinition, signature), StringComparer.Ordinal)
            .ThenBy(static signature => string.Join(",", signature.Parameters.Select(parameter => parameter.Type.DisplayName)), StringComparer.Ordinal)
            .ThenBy(static signature => signature.ReturnType.DisplayName, StringComparer.Ordinal)
            .ThenBy(static signature => signature.DisplaySourceName, StringComparer.Ordinal)
            .ThenBy(static signature => signature.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsMethodSignatureForOwner(
        StarkTypeSymbol ownerCore,
        NamedTypeSymbol ownerDefinition,
        TypedFunctionSignature signature)
    {
        if (!TryGetMethodOwnerSourceName(signature.DisplaySourceName, out var methodOwnerName))
        {
            return false;
        }

        foreach (var candidate in GetOwnerNameCandidates(ownerCore, ownerDefinition))
        {
            if (string.Equals(methodOwnerName, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetOwnerNameCandidates(StarkTypeSymbol ownerCore, NamedTypeSymbol ownerDefinition)
    {
        if (!string.IsNullOrWhiteSpace(ownerDefinition.Name))
        {
            yield return ownerDefinition.Name;
            var definitionBaseName = StarkTypeSymbols.GetGenericBaseName(ownerDefinition.Name);
            if (!string.Equals(definitionBaseName, ownerDefinition.Name, StringComparison.Ordinal))
            {
                yield return definitionBaseName;
            }
        }

        if (ownerCore.NamedType is { } ownerTypeName)
        {
            yield return ownerTypeName;
            var ownerBaseName = StarkTypeSymbols.GetGenericBaseName(ownerTypeName);
            if (!string.Equals(ownerBaseName, ownerTypeName, StringComparison.Ordinal))
            {
                yield return ownerBaseName;
            }
        }
    }

    private static bool TryGetMethodOwnerSourceName(string methodSourceName, out string ownerSourceName)
    {
        ownerSourceName = string.Empty;
        var separator = methodSourceName.LastIndexOf('.');
        if (separator <= 0)
        {
            return false;
        }

        ownerSourceName = methodSourceName[..separator];
        return true;
    }

    private static string GetMethodMemberName(NamedTypeSymbol ownerDefinition, TypedFunctionSignature method)
    {
        if (TryGetMethodOwnerSourceName(method.DisplaySourceName, out var ownerName)
            && (string.Equals(ownerName, ownerDefinition.Name, StringComparison.Ordinal)
                || string.Equals(ownerName, StarkTypeSymbols.GetGenericBaseName(ownerDefinition.Name), StringComparison.Ordinal)))
        {
            return method.DisplaySourceName[(ownerName.Length + 1)..];
        }

        var separator = method.DisplaySourceName.LastIndexOf('.');
        return separator < 0 ? method.DisplaySourceName : method.DisplaySourceName[(separator + 1)..];
    }

    private static string GetMethodModuleName(TypedFunctionSignature method)
    {
        if (!TryGetMethodOwnerSourceName(method.DisplaySourceName, out var ownerName))
        {
            return string.Empty;
        }

        return GetModuleName(ownerName);
    }

    private static IReadOnlyList<AssociatedTypeSymbol> GetOrderedAssociatedTypes(NamedTypeSymbol namedType)
    {
        return namedType.AssociatedTypes.Count == 0
            ? []
            : namedType.AssociatedTypes.Values
                .OrderBy(static associatedType => associatedType.Name, StringComparer.Ordinal)
                .ToArray();
    }

    private static NamedTypeSymbol? ResolveNamedTypeDefinition(
        StarkTypeSymbol coreType,
        NamedTypeSymbol? resolvedType,
        Func<StarkTypeSymbol, NamedTypeSymbol?> resolveNamedType)
    {
        if (coreType.Kind != StarkTypeKind.Named
            || coreType.NamedType is not { } typeName
            || !StarkTypeSymbols.IsGenericInstantiation(coreType))
        {
            return resolvedType;
        }

        var baseName = StarkTypeSymbols.GetGenericBaseName(typeName);
        if (string.Equals(baseName, typeName, StringComparison.Ordinal))
        {
            return resolvedType;
        }

        return resolveNamedType(StarkTypeSymbols.Named(baseName)) ?? resolvedType;
    }

    private static StarkTypeSymbol SubstituteOwnerGenericType(
        NamedTypeSymbol ownerSymbol,
        StarkTypeSymbol ownerType,
        StarkTypeSymbol targetType)
    {
        var ownerCore = NormalizeTypeForComparison(ownerType);
        var typeSubstitution = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
        if (ownerSymbol.GenericParams.Count > 0 && ownerCore.TypeArguments is { Count: > 0 } typeArguments)
        {
            for (var index = 0; index < ownerSymbol.GenericParams.Count && index < typeArguments.Count; index++)
            {
                typeSubstitution[ownerSymbol.GenericParams[index]] = typeArguments[index];
            }
        }

        Dictionary<string, BigInteger>? valueSubstitution = null;
        if (ownerSymbol.ComptimeGenericParams.Count > 0
            && ownerCore.ComptimeValueArguments is { Count: > 0 } valueArguments)
        {
            for (var index = 0; index < ownerSymbol.ComptimeGenericParams.Count && index < valueArguments.Count; index++)
            {
                var valueArgument = valueArguments[index];
                if (valueArgument.IsSymbolic)
                {
                    continue;
                }

                valueSubstitution ??= new Dictionary<string, BigInteger>(StringComparer.Ordinal);
                valueSubstitution[ownerSymbol.ComptimeGenericParams[index].Name] = valueArgument.IntegerValue;
            }
        }

        return typeSubstitution.Count == 0 && valueSubstitution is null
            ? targetType
            : FunctionOverloadFacts.SubstituteType(
                targetType,
                typeSubstitution,
                comptimeValueSubstitution: valueSubstitution);
    }

    private static StarkTypeSymbol NormalizeTypeForComparison(StarkTypeSymbol type)
    {
        return StarkTypeSymbols.WithQualifiers(
            type,
            borrowKind: StarkBorrowKind.None,
            accessKind: StarkAccessKind.None,
            initializationKind: StarkInitializationKind.None,
            isMutableView: false);
    }

    private static StarkTypeSymbol BuildDynTraitTargetType(StarkTypeSymbol dynTraitType)
    {
        if (dynTraitType.DynTraitName is not { } traitName)
        {
            return StarkTypeSymbols.Error;
        }

        return dynTraitType.TypeArguments is { Count: > 0 } typeArguments
            ? StarkTypeSymbols.GenericInstantiation(traitName, typeArguments)
            : StarkTypeSymbols.Named(traitName);
    }

    private static bool EvaluateFunctionPointerParameterMemoryFact(
        CompileTimeStructuralFactKind kind,
        StarkTypeSymbol functionPointerType,
        int leftIndex,
        int rightIndex)
    {
        if (leftIndex == rightIndex)
        {
            return kind is CompileTimeStructuralFactKind.FunctionPointerParametersAreSame
                or CompileTimeStructuralFactKind.FunctionPointerParametersOverlap;
        }

        var leftName = $"arg{leftIndex}";
        var rightName = $"arg{rightIndex}";
        return kind switch
        {
            CompileTimeStructuralFactKind.FunctionPointerParametersAreDisjoint =>
                ContainsParameterPair(functionPointerType.FunctionPointerDisjointParameterGroups, leftName, rightName),
            CompileTimeStructuralFactKind.FunctionPointerParametersOverlap =>
                ContainsParameterPair(functionPointerType.FunctionPointerOverlapParameterGroups, leftName, rightName)
                || ContainsParameterPair(functionPointerType.FunctionPointerSameParameterGroups, leftName, rightName),
            CompileTimeStructuralFactKind.FunctionPointerParametersAreSame =>
                ContainsParameterPair(functionPointerType.FunctionPointerSameParameterGroups, leftName, rightName),
            _ => false
        };
    }

    private static CompileTimeConstant EvaluateRawPointerElementCountExpressionFact(
        CompileTimeStructuralFactKind kind,
        string? expression)
    {
        return kind switch
        {
            CompileTimeStructuralFactKind.FunctionPointerParameterHasRawPointerElementCountExpression
                or CompileTimeStructuralFactKind.ClosureParameterHasRawPointerElementCountExpression
                or CompileTimeStructuralFactKind.MethodParameterHasRawPointerElementCountExpression =>
                CompileTimeConstant.Bool(!string.IsNullOrWhiteSpace(expression)),
            CompileTimeStructuralFactKind.FunctionPointerParameterRawPointerElementCountExpression
                or CompileTimeStructuralFactKind.ClosureParameterRawPointerElementCountExpression
                or CompileTimeStructuralFactKind.MethodParameterRawPointerElementCountExpression =>
                TextConstant(expression ?? string.Empty),
            _ => CompileTimeConstant.Bool(false)
        };
    }

    private static bool EvaluateClosureParameterMemoryFact(
        CompileTimeStructuralFactKind kind,
        StarkTypeSymbol closureType,
        int leftIndex,
        int rightIndex)
    {
        if (leftIndex == rightIndex)
        {
            return kind is CompileTimeStructuralFactKind.ClosureParametersAreSame
                or CompileTimeStructuralFactKind.ClosureParametersOverlap;
        }

        var leftName = $"arg{leftIndex}";
        var rightName = $"arg{rightIndex}";
        return kind switch
        {
            CompileTimeStructuralFactKind.ClosureParametersAreDisjoint =>
                ContainsParameterPair(closureType.ClosureDisjointParameterGroups, leftName, rightName),
            CompileTimeStructuralFactKind.ClosureParametersOverlap =>
                ContainsParameterPair(closureType.ClosureOverlapParameterGroups, leftName, rightName)
                || ContainsParameterPair(closureType.ClosureSameParameterGroups, leftName, rightName),
            CompileTimeStructuralFactKind.ClosureParametersAreSame =>
                ContainsParameterPair(closureType.ClosureSameParameterGroups, leftName, rightName),
            _ => false
        };
    }

    private static IReadOnlyList<StarkTypeSymbol> GetMethodGenericParameterTraitBounds(
        TypedFunctionSignature method,
        string parameterName)
    {
        foreach (var constraint in method.Constraints)
        {
            if (string.Equals(constraint.ParameterName, parameterName, StringComparison.Ordinal))
            {
                return constraint.BoundTraits;
            }
        }

        return [];
    }

    private static bool EvaluateMethodParameterMemoryFact(
        CompileTimeStructuralFactKind kind,
        TypedFunctionSignature method,
        int leftIndex,
        int rightIndex)
    {
        if (leftIndex == rightIndex)
        {
            return kind is CompileTimeStructuralFactKind.MethodParametersAreSame
                or CompileTimeStructuralFactKind.MethodParametersOverlap;
        }

        var leftName = GetParameterMemoryContractName(method, leftIndex);
        var rightName = GetParameterMemoryContractName(method, rightIndex);
        return kind switch
        {
            CompileTimeStructuralFactKind.MethodParametersAreDisjoint =>
                ContainsParameterPair(method.DisjointGroups, leftName, rightName),
            CompileTimeStructuralFactKind.MethodParametersOverlap =>
                ContainsParameterPair(method.OverlapGroups, leftName, rightName)
                || ContainsParameterPair(method.SameGroups, leftName, rightName),
            CompileTimeStructuralFactKind.MethodParametersAreSame =>
                ContainsParameterPair(method.SameGroups, leftName, rightName),
            _ => false
        };
    }

    private static string GetParameterMemoryContractName(TypedFunctionSignature method, int index)
    {
        if (index >= 0
            && index < method.Parameters.Count
            && !string.IsNullOrWhiteSpace(method.Parameters[index].Name))
        {
            return method.Parameters[index].Name;
        }

        return $"arg{index}";
    }

    private static bool ContainsParameterPair(
        IEnumerable<ParameterDisjointGroup>? groups,
        string leftName,
        string rightName)
    {
        return groups?.Any(group => !group.HasSubregions && GroupContainsParameterPair(group.ParameterNames, leftName, rightName)) == true;
    }

    private static bool ContainsParameterPair(
        IEnumerable<ParameterOverlapGroup>? groups,
        string leftName,
        string rightName)
    {
        return groups?.Any(group => GroupContainsParameterPair(group.ParameterNames, leftName, rightName)) == true;
    }

    private static bool ContainsParameterPair(
        IEnumerable<ParameterSameGroup>? groups,
        string leftName,
        string rightName)
    {
        return groups?.Any(group => GroupContainsParameterPair(group.ParameterNames, leftName, rightName)) == true;
    }

    private static bool GroupContainsParameterPair(
        IReadOnlyList<string> parameterNames,
        string leftName,
        string rightName)
    {
        var containsLeft = false;
        var containsRight = false;
        foreach (var parameterName in parameterNames)
        {
            containsLeft |= string.Equals(parameterName, leftName, StringComparison.Ordinal);
            containsRight |= string.Equals(parameterName, rightName, StringComparison.Ordinal);
            if (containsLeft && containsRight)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> GetImplementedTraitNames(
        NamedTypeSymbol? resolvedType,
        NamedTypeSymbol? resolvedTypeDefinition)
    {
        if (resolvedTypeDefinition?.ImplementedTraits is { Count: > 0 } definitionTraits)
        {
            return definitionTraits;
        }

        return resolvedType?.ImplementedTraits ?? [];
    }

    private static IReadOnlyList<StarkTypeSymbol> GetImplementedTraitTypes(
        NamedTypeSymbol? resolvedType,
        NamedTypeSymbol? resolvedTypeDefinition)
    {
        if (resolvedType?.ImplementedTraitTypes is { Count: > 0 } resolvedTraits)
        {
            return resolvedTraits;
        }

        return resolvedTypeDefinition?.ImplementedTraitTypes ?? [];
    }

    private static CompileTimeConstant EvaluateImplementedTraitMetadataFact(
        CompileTimeStructuralFactKind kind,
        string implementedTrait,
        NamedTypeSymbol? traitSymbol)
    {
        var hasInstantiation = TrySplitGenericArgumentText(implementedTrait, out var arguments);
        var typeArgumentCount = hasInstantiation
            ? traitSymbol?.GenericParams.Count ?? CountTypeLikeGenericArguments(arguments)
            : 0;
        var comptimeArgumentCount = hasInstantiation
            ? traitSymbol?.ComptimeGenericParams.Count ?? CountComptimeLikeGenericArguments(arguments)
            : 0;

        return kind switch
        {
            CompileTimeStructuralFactKind.ImplementedTraitTypeDisplayName =>
                TextConstant(FormatImplementedTraitDisplayName(implementedTrait, arguments)),
            CompileTimeStructuralFactKind.ImplementedTraitTypeBaseName =>
                TextConstant(StarkTypeSymbols.GetGenericBaseName(implementedTrait)),
            CompileTimeStructuralFactKind.ImplementedTraitTypeModuleName =>
                // Root-module traits are keyed without a module prefix; the
                // resolved trait symbol's declaring module is authoritative.
                TextConstant(traitSymbol?.DeclaringModuleName ?? GetModuleName(implementedTrait)),
            CompileTimeStructuralFactKind.ImplementedTraitTypeIsGenericInstantiation =>
                CompileTimeConstant.Bool(hasInstantiation),
            CompileTimeStructuralFactKind.ImplementedTraitTypeArgumentCount =>
                CompileTimeConstant.Integer(typeArgumentCount, CountType),
            CompileTimeStructuralFactKind.ImplementedTraitTypeComptimeArgumentCount =>
                CompileTimeConstant.Integer(comptimeArgumentCount, CountType),
            _ => CompileTimeConstant.Bool(false)
        };
    }

    private static NamedTypeSymbol? ResolveImplementedTraitSymbol(
        string implementedTrait,
        Func<StarkTypeSymbol, NamedTypeSymbol?> resolveNamedType)
    {
        var direct = resolveNamedType(StarkTypeSymbols.Named(implementedTrait));
        if (direct?.Kind == DeclarationKind.Trait)
        {
            return direct;
        }

        var baseName = StarkTypeSymbols.GetGenericBaseName(implementedTrait);
        if (!string.Equals(baseName, implementedTrait, StringComparison.Ordinal))
        {
            var template = resolveNamedType(StarkTypeSymbols.Named(baseName));
            if (template?.Kind == DeclarationKind.Trait)
            {
                return template;
            }
        }

        return direct;
    }

    private static NamedTypeSymbol? ResolveImplementedTraitPredicateType(
        StarkTypeSymbol type,
        NamedTypeSymbol? implementedTraitSymbol,
        Func<StarkTypeSymbol, NamedTypeSymbol?> resolveNamedType)
    {
        var resolved = resolveNamedType(type);
        if (resolved is not null)
        {
            return resolved;
        }

        if (implementedTraitSymbol is null || type.NamedType is not { } typeName)
        {
            return null;
        }

        return TraitNameEquivalent(typeName, implementedTraitSymbol.Name)
            ? implementedTraitSymbol
            : null;
    }

    private static bool ImplementedTraitNameMatches(
        string implementedTrait,
        StarkTypeSymbol traitType,
        NamedTypeSymbol traitSymbol)
    {
        var sourceName = traitType.NamedType;
        return TraitNameEquivalent(implementedTrait, traitSymbol.Name)
            || (sourceName is not null && TraitNameEquivalent(implementedTrait, sourceName));
    }

    private static bool TraitNameEquivalent(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        var leftBase = StarkTypeSymbols.GetGenericBaseName(left);
        var rightBase = StarkTypeSymbols.GetGenericBaseName(right);
        if (!string.Equals(LastSegment(leftBase), LastSegment(rightBase), StringComparison.Ordinal))
        {
            return false;
        }

        var leftHasArguments = TryGetGenericArgumentText(left, out var leftArguments);
        var rightHasArguments = TryGetGenericArgumentText(right, out var rightArguments);
        if (!leftHasArguments && !rightHasArguments)
        {
            return true;
        }

        return leftHasArguments
            && rightHasArguments
            && string.Equals(NormalizeGenericArgumentText(leftArguments), NormalizeGenericArgumentText(rightArguments), StringComparison.Ordinal);
    }

    private static string FormatImplementedTraitDisplayName(
        string implementedTrait,
        IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return implementedTrait;
        }

        var displayArguments = arguments.Select(FormatImplementedTraitArgumentDisplay);
        return $"{StarkTypeSymbols.GetGenericBaseName(implementedTrait)}<{string.Join(", ", displayArguments)}>";
    }

    private static string FormatImplementedTraitArgumentDisplay(string argument)
    {
        var equals = IndexOfTopLevelEquals(argument);
        return equals < 0 ? argument : argument[(equals + 1)..];
    }

    private static int CountTypeLikeGenericArguments(IReadOnlyList<string> arguments)
    {
        var count = 0;
        foreach (var argument in arguments)
        {
            if (IndexOfTopLevelEquals(argument) < 0)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountComptimeLikeGenericArguments(IReadOnlyList<string> arguments)
    {
        var count = 0;
        foreach (var argument in arguments)
        {
            if (IndexOfTopLevelEquals(argument) >= 0)
            {
                count++;
            }
        }

        return count;
    }

    private static bool TrySplitGenericArgumentText(string name, out IReadOnlyList<string> arguments)
    {
        arguments = [];
        if (!TryGetGenericArgumentText(name, out var argumentText))
        {
            return false;
        }

        arguments = SplitTopLevelGenericArguments(argumentText);
        return true;
    }

    private static bool TryGetGenericArgumentText(string name, out string argumentText)
    {
        argumentText = string.Empty;
        var start = name.IndexOf('<');
        if (start < 0)
        {
            return false;
        }

        var depth = 0;
        for (var index = start; index < name.Length; index++)
        {
            var ch = name[index];
            if (ch == '<')
            {
                depth++;
            }
            else if (ch == '>')
            {
                depth--;
                if (depth == 0)
                {
                    argumentText = name[(start + 1)..index];
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<string> SplitTopLevelGenericArguments(string argumentText)
    {
        if (string.IsNullOrWhiteSpace(argumentText))
        {
            return [];
        }

        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var index = 0; index < argumentText.Length; index++)
        {
            var ch = argumentText[index];
            if (ch == '<')
            {
                depth++;
            }
            else if (ch == '>')
            {
                depth--;
            }
            else if (ch == ',' && depth == 0)
            {
                parts.Add(argumentText[start..index].Trim());
                start = index + 1;
            }
        }

        parts.Add(argumentText[start..].Trim());
        return parts;
    }

    private static int IndexOfTopLevelEquals(string text)
    {
        var depth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch == '<')
            {
                depth++;
            }
            else if (ch == '>')
            {
                depth--;
            }
            else if (ch == '=' && depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static string NormalizeGenericArgumentText(string text)
    {
        return string.Concat(text.Where(static ch => !char.IsWhiteSpace(ch)));
    }

    private static CompileTimeConstant TextConstant(string value)
    {
        return CompileTimeConstant.Text(
            TextLiteralDecoder.EncodeStringLiteral(value),
            StarkTypeSymbols.Ascii);
    }

    private static bool VisibilityMatchesFact(CompileTimeStructuralFactKind kind, StarkVisibility visibility)
    {
        return kind switch
        {
            CompileTimeStructuralFactKind.TypeVisibilityIsModule
                or CompileTimeStructuralFactKind.FieldVisibilityIsModule
                or CompileTimeStructuralFactKind.MethodVisibilityIsModule => visibility == StarkVisibility.Module,
            CompileTimeStructuralFactKind.TypeVisibilityIsInternal
                or CompileTimeStructuralFactKind.FieldVisibilityIsInternal
                or CompileTimeStructuralFactKind.MethodVisibilityIsInternal => visibility == StarkVisibility.Internal,
            CompileTimeStructuralFactKind.TypeVisibilityIsPublic
                or CompileTimeStructuralFactKind.FieldVisibilityIsPublic
                or CompileTimeStructuralFactKind.MethodVisibilityIsPublic => visibility == StarkVisibility.Public,
            CompileTimeStructuralFactKind.TypeVisibilityIsExport
                or CompileTimeStructuralFactKind.FieldVisibilityIsExport
                or CompileTimeStructuralFactKind.MethodVisibilityIsExport => visibility == StarkVisibility.Export,
            _ => false
        };
    }

    private static bool ImplementsTrait(
        NamedTypeSymbol targetType,
        StarkTypeSymbol traitType,
        NamedTypeSymbol traitSymbol)
    {
        var sourceName = traitType.NamedType;
        foreach (var implementedTrait in targetType.ImplementedTraits)
        {
            if (string.Equals(implementedTrait, traitSymbol.Name, StringComparison.Ordinal)
                || (sourceName is not null && string.Equals(implementedTrait, sourceName, StringComparison.Ordinal)))
            {
                return true;
            }

            if (string.Equals(LastSegment(implementedTrait), LastSegment(traitSymbol.Name), StringComparison.Ordinal)
                || (sourceName is not null
                    && string.Equals(LastSegment(implementedTrait), LastSegment(sourceName), StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static string LastSegment(string name)
    {
        var baseName = StarkTypeSymbols.GetGenericBaseName(name);
        var separator = baseName.LastIndexOf('.');
        return separator < 0 ? baseName : baseName[(separator + 1)..];
    }
}
