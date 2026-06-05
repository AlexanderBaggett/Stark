grammar Stark;

compilationUnit
    : importDeclaration* moduleDeclaration topLevelDeclaration* EOF
    ;

importDeclaration
    : EXPORT? IMPORT qualifiedName
    ;

moduleDeclaration
    : attributeList* MODULE qualifiedName
    ;

attributeList
    : LBRACK attribute (COMMA attribute)* COMMA? RBRACK
    ;

attribute
    : qualifiedName (LPAREN (attributeArgument (COMMA attributeArgument)* COMMA?)? RPAREN)?
    ;

attributeArgument
    : qualifiedName
    | StringLiteral
    | IntegerLiteral
    ;

topLevelDeclaration
    : attributeList* visibilityModifier? (
          functionDeclaration
        | structDeclaration
        | recordDeclaration
        | enumDeclaration
        | traitDeclaration
        | doctrineDeclaration
        | typeAliasDeclaration
        | globalConstantDeclaration
        | globalVariableDeclaration
      )
    ;

visibilityModifier
    : INTERNAL
    | PUBLIC
    | EXPORT
    ;

functionDeclaration
    : functionModifier* asmSpecifier? functionKind returnType Identifier typeParameterList? parameterList typeParameterConstraints* parameterMemoryContractClause* asmClauseList? functionBody
    ;

functionKind
    : FINITE LAW
    | FINITE
    | LAW
    | FN
    ;

functionModifier
    : INLINE
    | NOINLINE
    | INLINEHINT
    | HOT
    | COLD
    | ffiModifier
    | VARARGS
    | UNSAFE
    | STRICTFP
    | STATIC
    ;

ffiModifier
    : FFI ffiAbiSpecifier?
    ;

ffiAbiSpecifier
    : LPAREN ffiAbi RPAREN
    ;

ffiAbi
    : Identifier
    | Identifier LPAREN ffiPlatformAbiEntry (COMMA ffiPlatformAbiEntry)* COMMA? RPAREN
    ;

ffiPlatformAbiEntry
    : ffiPlatformKey COLON Identifier
    ;

ffiPlatformKey
    : DEFAULT
    | qualifiedName
    ;

returnType
    : type_
    | VOID
    ;

parameterList
    : LPAREN (parameter (COMMA parameter)*)? COMMA? RPAREN
    ;

parameter
    : parameterContractPrefix* type_ Identifier
    ;

parameterContractPrefix
    : DISJOINT
    | CONST
    ;

parameterMemoryContractClause
    : WHERE parameterMemoryContract (COMMA parameterMemoryContract)* COMMA?
    ;

parameterMemoryContract
    : disjointContract
    | overlapContract
    | sameContract
    ;

disjointContract
    : DISJOINT LPAREN expressionList RPAREN
    ;

overlapContract
    : OVERLAP LPAREN expressionList RPAREN
    ;

sameContract
    : SAME LPAREN expressionList RPAREN
    ;

typeParameterList
    : LT typeParameter (COMMA typeParameter)* GT
    ;

typeParameter
    : Identifier
    ;

typeParameterConstraints
    : WHERE Identifier COLON type_ (COMMA type_)*
    ;

functionBody
    : block
    | SEMI
    | asmFunctionBody
    ;

asmSpecifier
    : ASM LPAREN Identifier RPAREN
    ;

asmClauseList
    : asmClause (COMMA asmClause)* COMMA?
    ;

asmClause
    : asmInputClause
    | asmOutputClause
    | asmClobberClause
    ;

asmInputClause
    : IN LPAREN StringLiteral RPAREN Identifier
    ;

asmOutputClause
    : OUT LPAREN StringLiteral RPAREN (Identifier | RETURN)
    ;

asmClobberClause
    : CLOBBER LPAREN StringLiteral (COMMA StringLiteral)* COMMA? RPAREN
    ;

asmFunctionBody
    : LBRACE StringLiteral RBRACE
    ;

structDeclaration
    : STRUCT Identifier typeParameterList? baseTraitList? structBody
    ;

recordDeclaration
    : RECORD Identifier typeParameterList? primaryConstructorParameters? baseTraitList? recordBody
    ;

enumDeclaration
    : ENUM Identifier typeParameterList? enumBody
    ;

traitDeclaration
    : DYN? TRAIT Identifier typeParameterList? traitBody
    ;

doctrineDeclaration
    : DOCTRINE Identifier typeParameterList? doctrineBody
    ;

typeAliasDeclaration
    : ALIAS Identifier typeParameterList? ASSIGN type_ SEMI
    ;

primaryConstructorParameters
    : parameterList
    ;

baseTraitList
    : COLON type_ (COMMA type_)*
    ;

structBody
    : LBRACE structMember* RBRACE
    ;

recordBody
    : LBRACE recordMember* RBRACE
    ;

enumBody
    : LBRACE (enumVariantDeclaration (COMMA enumVariantDeclaration)* COMMA?)? RBRACE
    ;

traitBody
    : LBRACE traitMember* RBRACE
    ;

doctrineBody
    : LBRACE doctrineMember* RBRACE
    ;

structMember
    : attributeList* (
          fieldDeclaration
    | methodDeclaration
    | constructorDeclaration
    | destructorDeclaration
    | associatedTypeDeclaration
      )
    ;

recordMember
    : attributeList* (
          fieldDeclaration
    | methodDeclaration
    | constructorDeclaration
    | destructorDeclaration
    | associatedTypeDeclaration
      )
    ;

enumVariantDeclaration
    : attributeList* Identifier enumVariantPayload?
    ;

enumVariantPayload
    : LPAREN (type_ (COMMA type_)*)? COMMA? RPAREN
    | LBRACE (enumVariantFieldDeclaration (COMMA enumVariantFieldDeclaration)* COMMA?)? RBRACE
    | FROM type_
    ;

enumVariantFieldDeclaration
    : Identifier COLON type_
    ;

traitMember
    : attributeList* (
          traitMethodDeclaration
    | associatedTypeDeclaration
      )
    ;

doctrineMember
    : attributeList* (
          doctrineMethodDeclaration
    | associatedTypeDeclaration
      )
    ;

associatedTypeDeclaration
    : ALIAS Identifier (ASSIGN type_)? SEMI
    ;

fieldDeclaration
    : visibilityModifier? MUT? type_ variableDeclarators SEMI
    ;

methodDeclaration
    : visibilityModifier? functionModifier* functionKind returnType Identifier typeParameterList? parameterList typeParameterConstraints* parameterMemoryContractClause* functionBody
    ;

traitMethodDeclaration
    : functionModifier* functionKind returnType Identifier typeParameterList? parameterList typeParameterConstraints* parameterMemoryContractClause* functionBody
    ;

doctrineMethodDeclaration
    : functionModifier* doctrineFunctionKind returnType Identifier typeParameterList? parameterList typeParameterConstraints* parameterMemoryContractClause* functionBody
    ;

doctrineFunctionKind
    : FINITE LAW
    | LAW
    ;

constructorDeclaration
    : Identifier parameterList block
    ;

destructorDeclaration
    : MUT? DROP block
    ;

globalConstantDeclaration
    : CONST constantDeclarators SEMI
    | CONST INTEGER_TYPE constantDeclarators SEMI
    | CONST type_ constantDeclarators SEMI
    ;

globalVariableDeclaration
    : storageClass MUT? type_ variableDeclarators SEMI
    ;

constantDeclarators
    : constantDeclarator (COMMA constantDeclarator)*
    ;

constantDeclarator
    : Identifier ASSIGN variableInitializer
    ;

variableDeclarators
    : variableDeclarator (COMMA variableDeclarator)*
    ;

variableDeclarator
    : Identifier variableStorageCapacity? (ASSIGN variableInitializer)?
    ;

variableStorageCapacity
    : LBRACK expression RBRACK
    ;

variableInitializer
    : expression
    | objectInitializer
    | arrayInitializer
    ;

storageClass
    : STACK
    | HEAP
    | REGISTER
    | STATIC
    | ARENA
    ;

type_
    : typeQualifier* nonArrayType arraySuffix*
    ;

typeQualifier
    : BORROW
    | RETBORROW
    | STOREBORROW
    | FROZEN
    | SHARED
    | OUT
    | INIT
    | MUT
    ;

nonArrayType
    : dynamicType
    | rawPointerType
    | functionPointerType
    | closureType
    | dynTraitType
    | integerType
    | simpleType
    ;

dynamicType
    : DYNAMIC type_
    ;

dynTraitType
    : dynStoragePrefix? DYN simpleType
    ;

dynStoragePrefix
    : HEAP
    ;

rawPointerType
    : RAWPTR LT type_ GT
    | RAWMUTPTR LT type_ GT
    ;

functionPointerType
    : FNPTR LT functionPointerSignature GT
    ;

functionPointerSignature
    : functionPointerAbiModifier? functionKind returnType functionPointerParameterList parameterMemoryContractClause*
    ;

functionPointerAbiModifier
    : FFI ffiAbiSpecifier?
    ;

closureType
    : closureStoragePrefix? CLOSURE LT closureSignature GT
    ;

closureStoragePrefix
    : INLINE
    | HEAP
    ;

closureSignature
    : closureCallCapability? functionKind returnType functionPointerParameterList parameterMemoryContractClause*
    ;

closureCallCapability
    : MUT
    | ONCE
    ;

functionPointerParameterList
    : LPAREN (type_ (COMMA type_)*)? COMMA? RPAREN
    ;

simpleType
    : builtinType
    | qualifiedName typeArgumentList?
    ;

integerType
    : INTEGER_TYPE rangeConstraint
    ;

builtinType
    : BOOL
    | ASCII
    | UNICODE
    | ASCIISTRING
    | UNICODESTRING
    | FLOAT_TYPE
    ;

arraySuffix
    : LBRACK expression? RBRACK
    ;

rangeConstraint
    : LBRACK rangeEndpointToken+ RBRACK
    ;

rangeEndpointToken
    : IntegerLiteral
    | Identifier
    | PLUS
    | MINUS
    | STAR
    | DIV
    | MOD
    | POW
    | LPAREN
    | RPAREN
    ;

typeArgumentList
    : LT type_ (COMMA type_)* GT
    ;

block
    : LBRACE statement* RBRACE
    ;

statement
    : block
    | unsafeStatement
    | assumeStatement
    | localConstantDeclaration
    | localVariableDeclaration
    | ifStatement
    | switchStatement
    | whileStatement
    | forStatement
    | returnStatement
    | breakStatement
    | continueStatement
    | expressionStatement
    | emptyStatement
    ;

unsafeStatement
    : UNSAFE (block | assumeStatement)
    ;

assumeStatement
    : ASSUME disjointRuntimeCondition statement
    ;

localConstantDeclaration
    : CONST constantDeclarators SEMI
    | CONST INTEGER_TYPE constantDeclarators SEMI
    | CONST type_ constantDeclarators SEMI
    ;

localVariableDeclaration
    : storageClass MUT? type_ variableDeclarators SEMI
    ;

ifStatement
    : IF weightSpecifier? (LPAREN expression (IS pattern)? RPAREN | disjointRuntimeCondition) statement (ELSE statement)?
    ;

disjointRuntimeCondition
    : DISJOINT LPAREN expressionList RPAREN
    ;

switchStatement
    : SWITCH weightSpecifier? LPAREN expression RPAREN LBRACE switchSection* RBRACE
    ;

switchSection
    : switchLabel+ statement*
    ;

switchLabel
    : CASE pattern (OR pattern)* whenClause? COLON
    | DEFAULT COLON
    ;

whenClause
    : WHEN expression
    ;

whileStatement
    : WHILE loopBehavior loopContract* LPAREN expression (IS pattern)? RPAREN statement
    ;

forStatement
    : FOR loopBehavior loopContract* LPAREN forInitializer? SEMI forCondition? SEMI forIterator? RPAREN statement
    ;

forInitializer
    : localForVariableDeclaration
    | expressionList
    ;

localForVariableDeclaration
    : storageClass MUT? type_ variableDeclarators
    ;

forCondition
    : expression
    ;

forIterator
    : expressionList
    ;

loopBehavior
    : INFINITE
    | NONDETERMINISTIC
    | WILLEXIT
    ;

loopContract
    : INDEPENDENT
    ;

weightSpecifier
    : WEIGHT_LITERAL
    ;

returnStatement
    : RETURN expression? SEMI
    ;

breakStatement
    : BREAK SEMI
    ;

continueStatement
    : CONTINUE SEMI
    ;

expressionStatement
    : expression SEMI
    ;

emptyStatement
    : SEMI
    ;

pattern
    : DISCARD
    | rangePattern
    | literal
    | VAR Identifier
    | enumNamedFieldPattern
    | genericEnumAggregatePattern
    | aggregatePattern
    ;

rangePattern
    : signedIntegerLiteral RANGE signedIntegerLiteral
    ;

aggregatePattern
    : simpleType aggregatePatternSuffix?
    ;

genericEnumAggregatePattern
    : genericEnumCaseReference aggregatePatternSuffix?
    ;

aggregatePatternSuffix
    : Identifier
    | LPAREN (pattern (COMMA pattern)*)? COMMA? RPAREN
    ;

enumNamedFieldPattern
    : enumCaseTarget enumNamedFieldPatternPayload
    ;

enumNamedFieldPatternPayload
    : LBRACE (namedPatternMember (COMMA namedPatternMember)*)? COMMA? RBRACE
    ;

namedPatternMember
    : Identifier COLON pattern
    ;

expressionList
    : expression (COMMA expression)*
    ;

expression
    : assignmentExpression
    ;

assignmentExpression
    : INIT unaryExpression ASSIGN assignmentExpression
    | conditionalExpression
    | unaryExpression assignmentOperator assignmentExpression
    ;

assignmentOperator
    : ASSIGN
    | ADD_ASSIGN
    | SUB_ASSIGN
    | MUL_ASSIGN
    | WRAP_ADD_ASSIGN
    | WRAP_SUB_ASSIGN
    | WRAP_MUL_ASSIGN
    | SAT_ADD_ASSIGN
    | SAT_SUB_ASSIGN
    | SAT_MUL_ASSIGN
    | DIV_ASSIGN
    | MOD_ASSIGN
    | AND_ASSIGN
    | OR_ASSIGN
    | XOR_ASSIGN
    ;

conditionalExpression
    : logicalOrExpression (QUESTION expression COLON expression)?
    ;

logicalOrExpression
    : logicalAndExpression (OR_OR logicalAndExpression)*
    ;

logicalAndExpression
    : bitwiseOrExpression (AND_AND bitwiseOrExpression)*
    ;

bitwiseOrExpression
    : bitwiseXorExpression (OR bitwiseXorExpression)*
    ;

bitwiseXorExpression
    : bitwiseAndExpression (CARET bitwiseAndExpression)*
    ;

bitwiseAndExpression
    : equalityExpression (AND equalityExpression)*
    ;

equalityExpression
    : relationalExpression ((EQ | NEQ) relationalExpression)*
    ;

relationalExpression
    : shiftExpression ((LT | GT | LTE | GTE) shiftExpression)*
    ;

shiftExpression
    : additiveExpression (((LT LT) | (GT GT)) additiveExpression)*
    ;

additiveExpression
    : multiplicativeExpression ((PLUS | MINUS | WRAP_ADD | WRAP_SUB | SAT_ADD | SAT_SUB) multiplicativeExpression)*
    ;

multiplicativeExpression
    : unaryExpression ((STAR | DIV | MOD | WRAP_MUL | SAT_MUL) unaryExpression)*
    ;

unaryExpression
    : powerExpression
    | INIT unaryExpression
    | TRY unaryExpression
    | LPAREN conversionType RPAREN unaryExpression
    | unaryOperator unaryExpression
    ;

unaryOperator
    : PLUS
    | MINUS
    | WRAP_SUB
    | BANG
    | TILDE
    | AND
    | STAR
    ;

conversionType
    : typeQualifier* conversionNonArrayType arraySuffix*
    ;

conversionNonArrayType
    : rawPointerType
    | integerType
    | builtinType
    ;

powerExpression
    : postfixExpression (POW unaryExpression)?
    ;

postfixExpression
    : primaryExpression postfixPart*
    ;

postfixPart
    : argumentList
    | LBRACK expressionList? RBRACK
    | DOT Identifier
    ;

primaryExpression
    : lambdaExpression
    | literal
    | SIZEOF LPAREN type_ RPAREN
    | ALIGNOF LPAREN type_ RPAREN
    | Identifier
    | enumConstructorExpression
    | genericEnumCaseReference
    | qualifiedName
    | objectCreationExpression
    | LPAREN expression RPAREN
    ;

lambdaExpression
    : lambdaStoragePrefix? captureClause? lambdaParameterList ARROW (expression | block)
    ;

lambdaStoragePrefix
    : HEAP
    ;

captureClause
    : Identifier LPAREN (captureBinding (COMMA captureBinding)*)? COMMA? RPAREN
    ;

captureBinding
    : UNSAFE? captureMode Identifier
    ;

captureMode
    : Identifier
    | MUT
    | OUT
    | INIT
    | SHARED
    ;

lambdaParameterList
    : LPAREN (parameter (COMMA parameter)*)? COMMA? RPAREN
    ;

enumConstructorExpression
    : enumCaseTarget enumConstructorInitializer
    ;

objectCreationExpression
    : NEW type_ argumentList? objectInitializer?
    | NEW argumentList objectInitializer?
    ;

enumConstructorInitializer
    : LBRACE (enumConstructorMember (COMMA enumConstructorMember)*)? COMMA? RBRACE
    ;

enumConstructorMember
    : Identifier COLON expression
    ;

argumentList
    : LPAREN (argument (COMMA argument)*)? COMMA? RPAREN
    ;

argument
    : expression
    ;

objectInitializer
    : LBRACE memberInitializer (COMMA memberInitializer)* COMMA? RBRACE
    ;

memberInitializer
    : Identifier ASSIGN variableInitializer
    ;

arrayInitializer
    : LBRACE variableInitializer (COMMA variableInitializer)* COMMA? RBRACE
    ;

literal
    : signedIntegerLiteral
    | FloatLiteral
    | DOLLAR StringLiteral
    | StringLiteral
    | CharacterLiteral
    | TRUE
    | FALSE
    | NULL
    ;

signedIntegerLiteral
    : MINUS? IntegerLiteral
    ;

genericQualifiedName
    : qualifiedName typeArgumentList
    ;

genericEnumCaseReference
    : genericQualifiedName DOT Identifier
    ;

qualifiedName
    : Identifier (DOT Identifier)*
    ;

dottedName
    : Identifier (DOT Identifier)+
    ;

enumCaseTarget
    : dottedName
    | genericEnumCaseReference
    ;

IMPORT      : 'import';
MODULE      : 'module';
INTERNAL    : 'internal';
PUBLIC      : 'public';
EXPORT      : 'export';

FN          : 'fn';
FINITE      : 'finite';
LAW         : 'law';

INLINE      : 'inline';
NOINLINE    : 'noinline';
INLINEHINT  : 'inlinehint';
HOT         : 'hot';
COLD        : 'cold';
FFI         : 'ffi';
VARARGS     : 'varargs';
UNSAFE      : 'unsafe';
STRICTFP    : 'strictfp';
ASM         : 'asm';
CLOBBER     : 'clobber';
IN          : 'in';

STRUCT      : 'struct';
RECORD      : 'record';
ENUM        : 'enum';
TRAIT       : 'trait';
DOCTRINE    : 'doctrine';
ALIAS       : 'alias';
DROP        : 'drop';
FROM        : 'from';

STACK       : 'stack';
HEAP        : 'heap';
REGISTER    : 'register';
STATIC      : 'static';
ARENA       : 'arena';
SIZEOF      : 'sizeof';
ALIGNOF     : 'alignof';

BORROW      : 'borrow';
RETBORROW   : 'retborrow';
STOREBORROW : 'storeborrow';
FROZEN      : 'frozen';
SHARED      : 'shared';
OUT         : 'out';
INIT        : 'init';
DYN         : 'dyn';
DYNAMIC     : 'dynamic';
RAWPTR      : 'rawptr';
RAWMUTPTR   : 'rawmutptr';
FNPTR       : 'fnptr';
CLOSURE     : 'closure';
MUT         : 'mut';
ONCE        : 'once';
DISJOINT    : 'disjoint';
OVERLAP     : 'overlap';
SAME        : 'same';
ASSUME      : 'assume';

IF          : 'if';
IS          : 'is';
ELSE        : 'else';
SWITCH      : 'switch';
CASE        : 'case';
DEFAULT     : 'default';
WHEN        : 'when';
WHILE       : 'while';
FOR         : 'for';
RETURN      : 'return';
BREAK       : 'break';
CONTINUE    : 'continue';
TRY         : 'try';
NEW         : 'new';
CONST       : 'const';
WHERE       : 'where';
VAR         : 'var';
INFINITE         : 'infinite';
NONDETERMINISTIC : 'non-deterministic';
WILLEXIT         : 'willexit';
INDEPENDENT      : 'independent';

VOID        : 'void';
BOOL        : 'bool';
ASCII       : 'ascii';
UNICODE     : 'unicode';
ASCIISTRING : 'Ascii';
UNICODESTRING : 'Unicode';
TRUE        : 'true';
FALSE       : 'false';
NULL        : 'null';

WEIGHT_LITERAL
    : 'w' DIGIT+
    ;

INTEGER_TYPE
    : 'i8'
    | 'u8'
    | 'i16'
    | 'u16'
    | 'i24'
    | 'u24'
    | 'i32'
    | 'u32'
    | 'i48'
    | 'u48'
    | 'i64'
    | 'u64'
    | 'i96'
    | 'u96'
    | 'i128'
    | 'u128'
    | 'i192'
    | 'u192'
    | 'i256'
    | 'u256'
    | 'i384'
    | 'u384'
    | 'i512'
    | 'u512'
    | 'i768'
    | 'u768'
    | 'i1024'
    | 'u1024'
    ;

FLOAT_TYPE
    : 'f16'
    | 'f32'
    | 'f64'
    | 'f80'
    | 'f128'
    ;

INVALID_INTEGER_TYPE
    : [iu] DIGIT+
    ;

INVALID_FLOAT_TYPE
    : 'f' DIGIT+
    ;

WRAP_ADD_ASSIGN : '+%=';
WRAP_SUB_ASSIGN : '-%=';
WRAP_MUL_ASSIGN : '*%=';
SAT_ADD_ASSIGN  : '+|=';
SAT_SUB_ASSIGN  : '-|=';
SAT_MUL_ASSIGN  : '*|=';
ADD_ASSIGN      : '+=';
SUB_ASSIGN      : '-=';
MUL_ASSIGN      : '*=';
DIV_ASSIGN  : '/=';
MOD_ASSIGN  : '%=';
AND_ASSIGN  : '&=';
OR_ASSIGN   : '|=';
XOR_ASSIGN  : '^=';
EQ          : '==';
NEQ         : '!=';
LTE         : '<=';
GTE         : '>=';
AND_AND     : '&&';
OR_OR       : '||';
POW         : '**';
WRAP_ADD    : '+%';
WRAP_SUB    : '-%';
WRAP_MUL    : '*%';
SAT_ADD     : '+|';
SAT_SUB     : '-|';
SAT_MUL     : '*|';

ASSIGN      : '=';
LT          : '<';
GT          : '>';
PLUS        : '+';
MINUS       : '-';
STAR        : '*';
DIV         : '/';
MOD         : '%';
AND         : '&';
OR          : '|';
CARET       : '^';
BANG        : '!';
TILDE       : '~';
QUESTION    : '?';
ARROW       : '=>';
COLON       : ':';
SEMI        : ';';
COMMA       : ',';
RANGE       : '..';
DOT         : '.';
DOLLAR      : '$';
LPAREN      : '(';
RPAREN      : ')';
LBRACE      : '{';
RBRACE      : '}';
LBRACK      : '[';
RBRACK      : ']';
DISCARD     : '_';

Identifier
    : IdentifierStart IdentifierPart*
    ;

IntegerLiteral
    : DIGIT+
    ;

FloatLiteral
    : DIGIT+ DOT DIGIT+ ExponentPart? FloatSuffix?
    | DIGIT+ ExponentPart FloatSuffix?
    ;

CharacterLiteral
    : '\'' (LiteralEscapeSequence | ~['\\\r\n])* '\''
    ;

StringLiteral
    : 'raw"""' .*? '"""'
    | 'raw"' ~["\r\n]* '"'
    | '"' (LiteralEscapeSequence | ~["\\\r\n])* '"'
    ;

fragment ExponentPart
    : [eE] [+\-]? DIGIT+
    ;

fragment FloatSuffix
    : [fF]
    ;

fragment LiteralEscapeSequence
    : '\\' .
    ;

fragment IdentifierStart
    : [a-zA-Z]
    ;

fragment IdentifierPart
    : [a-zA-Z0-9_]
    ;

fragment DIGIT
    : [0-9]
    ;

fragment HexDigit
    : [0-9a-fA-F]
    ;

// Line comments are trivia, including C#-style XML documentation comments
// written with three leading slashes.
LINE_COMMENT
    : '//' ~[\r\n]* -> skip
    ;

// Block comments are trivia, including C#-style XML documentation comments
// written with a doubled leading asterisk. Block comments do not nest.
BLOCK_COMMENT
    : '/*' .*? '*/' -> skip
    ;

WS
    : [ \t\r\n\u000C]+ -> skip
    ;
