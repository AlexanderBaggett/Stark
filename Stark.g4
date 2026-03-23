grammar Stark;

compilationUnit
    : importDeclaration* moduleDeclaration topLevelDeclaration* EOF
    ;

importDeclaration
    : IMPORT qualifiedName SEMI?
    ;

moduleDeclaration
    : MODULE qualifiedName SEMI?
    ;

topLevelDeclaration
    : visibilityModifier? (
          functionDeclaration
        | structDeclaration
        | recordDeclaration
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
    : STRUCT Identifier typeParameterList? inheritanceClause? structBody
    ;

recordDeclaration
    : RECORD Identifier typeParameterList? primaryConstructorParameters? inheritanceClause? recordBody
    ;

traitDeclaration
    : TRAIT Identifier typeParameterList? inheritanceClause? traitBody
    ;

doctrineDeclaration
    : DOCTRINE Identifier typeParameterList? inheritanceClause? doctrineBody
    ;

inheritanceClause
    : COLON typeList
    ;

typeList
    : type_ (COMMA type_)*
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
    : Identifier parameterList constructorInitializer? block
    ;

constructorInitializer
    : COLON (BASE | THIS) argumentList
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
    : Identifier ASSIGN expression
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
    | sliceOrArrayType
    | simpleType rangeConstraint?
    ;

rawPointerType
    : RAWPTR LT type_ GT
    | RAWMUTPTR LT type_ GT
    ;

sliceOrArrayType
    : LBRACK type_ (SEMI expression)? RBRACK
    ;

simpleType
    : builtinType
    | qualifiedName typeArgumentList?
    ;

builtinType
    : BOOL
    | ASCII
    | UNICODE
    | INTEGER_TYPE
    | FLOAT_TYPE
    ;

arraySuffix
    : LBRACK RBRACK
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
    | type_ Identifier?
    | qualifiedName
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
    | DIV_ASSIGN
    | MOD_ASSIGN
    | AND_ASSIGN
    | OR_ASSIGN
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
    : bitwiseAndExpression (OR bitwiseAndExpression)*
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
    : multiplicativeExpression ((PLUS | MINUS | CARET) multiplicativeExpression)*
    ;

multiplicativeExpression
    : unaryExpression ((STAR | DIV | MOD) unaryExpression)*
    ;

unaryExpression
    : postfixExpression
    | (PLUS | MINUS | BANG | TILDE) unaryExpression
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
    | qualifiedName
    | objectCreationExpression
    | LPAREN expression RPAREN
    ;

objectCreationExpression
    : NEW type_ argumentList? objectInitializer?
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
    : Identifier ASSIGN expression
    ;

arrayInitializer
    : LBRACE expression (COMMA expression)* COMMA? RBRACE
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

STRUCT      : 'struct';
RECORD      : 'record';
TRAIT       : 'trait';
DOCTRINE    : 'doctrine' | 'doctorine';

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
BASE        : 'base';
THIS        : 'this';
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

ADD_ASSIGN  : '+=';
SUB_ASSIGN  : '-=';
MUL_ASSIGN  : '*=';
DIV_ASSIGN  : '/=';
MOD_ASSIGN  : '%=';
AND_ASSIGN  : '&=';
OR_ASSIGN   : '|=';
EQ          : '==';
NEQ         : '!=';
LTE         : '<=';
GTE         : '>=';
AND_AND     : '&&';
OR_OR       : '||';

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
    : '\'' (EscapeSequence | ~['\\\r\n]) '\''
    ;

StringLiteral
    : '"' (EscapeSequence | ~["\\\r\n])* '"'
    ;

fragment ExponentPart
    : [eE] [+\-]? DIGIT+
    ;

fragment EscapeSequence
    : '\\' [btnfr"'\\]
    | '\\u' HexDigit HexDigit HexDigit HexDigit
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
