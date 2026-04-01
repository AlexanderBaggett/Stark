grammar Stark;

compilationUnit
    : importDeclaration* moduleDeclaration topLevelDeclaration* EOF
    ;

importDeclaration
    : EXPORT? IMPORT qualifiedName SEMI?
    ;

moduleDeclaration
    : MODULE qualifiedName SEMI?
    ;

topLevelDeclaration
    : visibilityModifier? (
          functionDeclaration
        | structDeclaration
        | recordDeclaration
        | enumDeclaration
        | traitDeclaration
        | doctrineDeclaration
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
    : functionModifier* functionKind returnType Identifier typeParameterList? parameterList typeParameterConstraints* functionBody
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
    | FFI
    | STRICTFP
    ;

returnType
    : type_
    | VOID
    ;

parameterList
    : LPAREN (parameter (COMMA parameter)*)? COMMA? RPAREN
    ;

parameter
    : type_ Identifier (ASSIGN expression)?
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
    ;

structDeclaration
    : STRUCT Identifier typeParameterList? structBody
    ;

recordDeclaration
    : RECORD Identifier typeParameterList? primaryConstructorParameters? recordBody
    ;

enumDeclaration
    : ENUM Identifier typeParameterList? enumBody
    ;

traitDeclaration
    : TRAIT Identifier typeParameterList? traitBody
    ;

doctrineDeclaration
    : DOCTRINE Identifier typeParameterList? doctrineBody
    ;

primaryConstructorParameters
    : parameterList
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
    : fieldDeclaration
    | methodDeclaration
    | constructorDeclaration
    ;

recordMember
    : fieldDeclaration
    | methodDeclaration
    | constructorDeclaration
    ;

enumVariantDeclaration
    : Identifier enumVariantPayload?
    ;

enumVariantPayload
    : LPAREN (type_ (COMMA type_)*)? COMMA? RPAREN
    | LBRACE (enumVariantFieldDeclaration (COMMA enumVariantFieldDeclaration)* COMMA?)? RBRACE
    ;

enumVariantFieldDeclaration
    : Identifier COLON type_
    ;

traitMember
    : traitMethodDeclaration
    ;

doctrineMember
    : doctrineMethodDeclaration
    ;

fieldDeclaration
    : MUT? type_ variableDeclarators SEMI
    ;

methodDeclaration
    : functionModifier* functionKind returnType Identifier typeParameterList? parameterList typeParameterConstraints* functionBody
    ;

traitMethodDeclaration
    : functionModifier* functionKind returnType Identifier typeParameterList? parameterList typeParameterConstraints* functionBody
    ;

doctrineMethodDeclaration
    : functionModifier* doctrineFunctionKind returnType Identifier typeParameterList? parameterList typeParameterConstraints* functionBody
    ;

doctrineFunctionKind
    : FINITE LAW
    | LAW
    ;

constructorDeclaration
    : Identifier parameterList block
    ;

globalConstantDeclaration
    : CONST type_ constantDeclarators SEMI
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
    : Identifier (ASSIGN variableInitializer)?
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
    : rawPointerType
    | simpleType rangeConstraint?
    ;

rawPointerType
    : RAWPTR LT type_ GT
    | RAWMUTPTR LT type_ GT
    ;

simpleType
    : builtinType
    | qualifiedName typeArgumentList?
    ;

builtinType
    : BOOL
    | ASCII
    | UNICODE
    | ASCIISTRING
    | UNICODESTRING
    | INTEGER_TYPE
    | FLOAT_TYPE
    ;

arraySuffix
    : LBRACK expression? RBRACK
    ;

rangeConstraint
    : LBRACK signedIntegerLiteral signedIntegerLiteral RBRACK
    ;

typeArgumentList
    : LT type_ (COMMA type_)* GT
    ;

block
    : LBRACE statement* RBRACE
    ;

statement
    : block
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

localConstantDeclaration
    : CONST type_ constantDeclarators SEMI
    ;

localVariableDeclaration
    : storageClass MUT? type_ variableDeclarators SEMI
    ;

ifStatement
    : IF weightSpecifier? LPAREN expression RPAREN statement (ELSE statement)?
    ;

switchStatement
    : SWITCH weightSpecifier? LPAREN expression RPAREN LBRACE switchSection* RBRACE
    ;

switchSection
    : switchLabel+ statement*
    ;

switchLabel
    : CASE pattern whenClause? COLON
    | DEFAULT COLON
    ;

whenClause
    : WHEN expression
    ;

whileStatement
    : WHILE loopBehavior LPAREN expression RPAREN statement
    ;

forStatement
    : FOR loopBehavior LPAREN forInitializer? SEMI forCondition? SEMI forIterator? RPAREN statement
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
    | literal
    | VAR Identifier
    | enumNamedFieldPattern
    | aggregatePattern
    ;

aggregatePattern
    : simpleType aggregatePatternSuffix?
    ;

aggregatePatternSuffix
    : Identifier
    | LPAREN (pattern (COMMA pattern)*)? COMMA? RPAREN
    ;

enumNamedFieldPattern
    : dottedName enumNamedFieldPatternPayload
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
    : conditionalExpression
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
    | builtinType rangeConstraint?
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
    : literal
    | Identifier
    | enumConstructorExpression
    | qualifiedName
    | objectCreationExpression
    | LPAREN expression RPAREN
    ;

enumConstructorExpression
    : dottedName enumConstructorInitializer
    ;

objectCreationExpression
    : NEW type_ argumentList? objectInitializer?
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
    | StringLiteral
    | CharacterLiteral
    | TRUE
    | FALSE
    | NULL
    ;

signedIntegerLiteral
    : MINUS? IntegerLiteral
    ;

qualifiedName
    : Identifier (DOT Identifier)*
    ;

dottedName
    : Identifier (DOT Identifier)+
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
STRICTFP    : 'strictfp';

STRUCT      : 'struct';
RECORD      : 'record';
ENUM        : 'enum';
TRAIT       : 'trait';
DOCTRINE    : 'doctrine';

STACK       : 'stack';
HEAP        : 'heap';
REGISTER    : 'register';
STATIC      : 'static';
ARENA       : 'arena';

BORROW      : 'borrow';
RETBORROW   : 'retborrow';
STOREBORROW : 'storeborrow';
FROZEN      : 'frozen';
SHARED      : 'shared';
OUT         : 'out';
INIT        : 'init';
RAWPTR      : 'rawptr';
RAWMUTPTR   : 'rawmutptr';
MUT         : 'mut';

IF          : 'if';
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
NEW         : 'new';
CONST       : 'const';
WHERE       : 'where';
VAR         : 'var';

INFINITE         : 'infinite';
NONDETERMINISTIC : 'non-deterministic' | 'nondeterministic';
WILLEXIT         : 'willexit';

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
    : 'i' DIGIT+
    ;

FLOAT_TYPE
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
COLON       : ':';
SEMI        : ';';
COMMA       : ',';
DOT         : '.';
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
    : DIGIT+ DOT DIGIT+ ExponentPart?
    | DIGIT+ ExponentPart
    ;

CharacterLiteral
    : '\'' (LiteralEscapeSequence | ~['\\\r\n])* '\''
    ;

StringLiteral
    : '"' (LiteralEscapeSequence | ~["\\\r\n])* '"'
    ;

fragment ExponentPart
    : [eE] [+\-]? DIGIT+
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

LINE_COMMENT
    : '//' ~[\r\n]* -> skip
    ;

BLOCK_COMMENT
    : '/*' .*? '*/' -> skip
    ;

WS
    : [ \t\r\n\u000C]+ -> skip
    ;
