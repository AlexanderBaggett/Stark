"use strict";

const identifierPattern = /[A-Za-z_][A-Za-z0-9_]*/y;
const identifierCharacterPattern = /[A-Za-z0-9_]/;

const declarationModifiers = new Set([
    "inline",
    "noinline",
    "inlinehint",
    "hot",
    "cold",
    "unsafe",
    "ffi",
    "varargs",
    "strictfp",
    "static",
]);

function createDefinitionIndex(documents) {
    const index = {
        documents: new Map(),
        declarations: [],
        byQualifiedName: new Map(),
        byName: new Map(),
    };

    for (const document of documents) {
        const parsed = parseDocument(document.uri, document.text);
        index.documents.set(document.uri, parsed);

        for (const declaration of parsed.declarations) {
            addDeclaration(index, declaration);
        }
    }

    return index;
}

function parseDocument(uri, text) {
    const lines = text.split(/\r?\n/);
    const document = {
        uri,
        text,
        lines,
        moduleName: null,
        imports: [],
        declarations: [],
        localDeclarations: [],
        scopes: [],
    };

    let braceDepth = 0;
    let currentType = null;
    let pendingType = null;
    let currentScope = null;
    let pendingScope = null;
    let pendingParameterScope = null;
    let nextScopeId = 1;

    for (let lineIndex = 0; lineIndex < lines.length; lineIndex++) {
        const rawLine = lines[lineIndex];
        const cleanLine = stripLineComment(rawLine);
        const trimmed = cleanLine.trim();
        const depthBeforeLine = braceDepth;

        if (currentType && depthBeforeLine < currentType.bodyDepth) {
            currentType = null;
        }

        if (currentScope && depthBeforeLine < currentScope.bodyDepth) {
            currentScope.endLine = Math.max(currentScope.startLine, lineIndex - 1);
            currentScope = null;
        }

        if (pendingParameterScope && lineIndex > pendingParameterScope.startLine) {
            const parametersComplete = addParameterDeclarationsFromLine(document, pendingParameterScope, rawLine, lineIndex);
            if (parametersComplete) {
                pendingParameterScope = null;
            }
        }

        const moduleMatch = trimmed.match(/^module\s+([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\b/);
        if (moduleMatch) {
            document.moduleName = moduleMatch[1];
            addDocumentDeclaration(document, {
                kind: "module",
                name: tailName(moduleMatch[1]),
                qualifiedName: moduleMatch[1],
                moduleName: moduleMatch[1],
                lineText: rawLine,
                line: lineIndex,
                character: rawLine.indexOf(moduleMatch[1]),
                rangeLength: moduleMatch[1].length,
            });
        }

        const importMatch = trimmed.match(/^(?:export\s+)?import\s+([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\b/);
        if (importMatch) {
            document.imports.push(importMatch[1]);
        }

        const typeDeclaration = parseTypeDeclaration(trimmed);
        if (typeDeclaration) {
            addDocumentDeclaration(document, {
                ...typeDeclaration,
                moduleName: document.moduleName,
                qualifiedName: qualify(document.moduleName, typeDeclaration.name),
                lineText: rawLine,
                line: lineIndex,
                character: rawLine.indexOf(typeDeclaration.name),
            });

            pendingType = {
                name: typeDeclaration.name,
                kind: typeDeclaration.kind,
            };
        }

        const aliasDeclaration = parseAliasDeclaration(trimmed);
        if (aliasDeclaration) {
            addDocumentDeclaration(document, {
                ...aliasDeclaration,
                moduleName: document.moduleName,
                qualifiedName: qualify(document.moduleName, aliasDeclaration.name),
                lineText: rawLine,
                line: lineIndex,
                character: rawLine.indexOf(aliasDeclaration.name),
            });
        }

        const callableDeclaration = parseCallableDeclaration(trimmed, currentType);
        if (callableDeclaration) {
            addDocumentDeclaration(document, {
                ...callableDeclaration,
                moduleName: document.moduleName,
                qualifiedName: qualify(document.moduleName, currentType?.name, callableDeclaration.name),
                lineText: rawLine,
                line: lineIndex,
                character: rawLine.indexOf(callableDeclaration.name),
            });

            pendingScope = {
                id: nextScopeId++,
                name: callableDeclaration.name,
                startLine: lineIndex,
                endLine: lines.length - 1,
                bodyDepth: null,
            };
            const parametersComplete = addParameterDeclarations(document, pendingScope, rawLine, lineIndex);
            if (!parametersComplete) {
                pendingParameterScope = pendingScope;
            }
        }

        if (currentType && depthBeforeLine === currentType.bodyDepth) {
            if (currentType.kind === "enum") {
                const enumMember = parseEnumMember(trimmed);
                if (enumMember) {
                    addDocumentDeclaration(document, {
                        kind: "enumMember",
                        name: enumMember.name,
                        moduleName: document.moduleName,
                        owner: currentType.name,
                        qualifiedName: qualify(document.moduleName, currentType.name, enumMember.name),
                        lineText: rawLine,
                        line: lineIndex,
                        character: rawLine.indexOf(enumMember.name),
                    });
                }
            }

            const constructorDeclaration = parseConstructorDeclaration(trimmed, currentType.name);
            if (constructorDeclaration) {
                addDocumentDeclaration(document, {
                    ...constructorDeclaration,
                    moduleName: document.moduleName,
                    owner: currentType.name,
                    qualifiedName: qualify(document.moduleName, currentType.name, constructorDeclaration.name),
                    lineText: rawLine,
                    line: lineIndex,
                    character: rawLine.indexOf(constructorDeclaration.name),
                });
            }

            const fieldDeclaration = parseFieldDeclaration(trimmed);
            if (fieldDeclaration) {
                addDocumentDeclaration(document, {
                    ...fieldDeclaration,
                    moduleName: document.moduleName,
                    owner: currentType.name,
                    qualifiedName: qualify(document.moduleName, currentType.name, fieldDeclaration.name),
                    lineText: rawLine,
                    line: lineIndex,
                    character: rawLine.indexOf(fieldDeclaration.name),
                });
            }
        }

        if (currentScope && depthBeforeLine >= currentScope.bodyDepth) {
            const localDeclaration = parseLocalDeclaration(trimmed);
            if (localDeclaration) {
                addLocalDeclaration(document, {
                    ...localDeclaration,
                    scopeId: currentScope.id,
                    line: lineIndex,
                    character: rawLine.lastIndexOf(localDeclaration.name),
                });
            }

            for (const patternDeclaration of parsePatternLocalDeclarations(cleanLine)) {
                addLocalDeclaration(document, {
                    ...patternDeclaration,
                    scopeId: currentScope.id,
                    line: lineIndex,
                });
            }
        }

        const delta = countBraceDelta(cleanLine);
        braceDepth += delta;

        if (pendingType && cleanLine.includes("{")) {
            currentType = {
                ...pendingType,
                bodyDepth: depthBeforeLine + Math.max(1, delta),
            };
            pendingType = null;
        }

        if (pendingScope && cleanLine.includes("{")) {
            currentScope = {
                ...pendingScope,
                bodyDepth: depthBeforeLine + Math.max(1, delta),
            };
            document.scopes.push(currentScope);
            pendingScope = null;
        }
    }

    return document;
}

function resolveDefinition(index, request) {
    const target = extractDefinitionTarget(request.text, request.line, request.character);
    if (!target) {
        return [];
    }

    const currentDocument = index.documents.get(request.uri) ?? parseDocument(request.uri, request.text);
    return uniqueDeclarations(resolveTarget(index, currentDocument, target));
}

function extractDefinitionTarget(text, line, character) {
    const lines = text.split(/\r?\n/);
    const lineText = lines[line] ?? "";
    const importOrModuleTarget = extractImportOrModuleTarget(line, lineText, character);
    if (importOrModuleTarget) {
        return importOrModuleTarget;
    }

    const identifier = identifierAt(lineText, character);
    if (!identifier) {
        return null;
    }

    const qualified = qualifiedTargetAt(lineText, identifier);
    return {
        kind: "symbol",
        text: qualified.text,
        normalizedText: stripTypeArguments(qualified.text),
        identifierText: identifier.text,
        identifierStart: identifier.start,
        line,
        range: {
            start: { line, character: qualified.start },
            end: { line, character: qualified.end },
        },
    };
}

function resolveTarget(index, currentDocument, target) {
    const normalized = target.normalizedText ?? stripTypeArguments(target.text);

    if (target.kind === "module") {
        return lookupQualified(index, normalized).filter(declaration => declaration.kind === "module");
    }

    const localMatches = resolveLocalTarget(currentDocument, target);
    if (localMatches.length > 0) {
        return localMatches;
    }

    if (normalized.includes(".")) {
        const exact = lookupQualified(index, normalized);
        if (exact.length > 0) {
            return exact;
        }

        const container = lookupQualified(index, removeTrailingMember(normalized));
        if (container.length > 0) {
            return container;
        }

        return index.byName.get(tailName(normalized)) ?? [];
    }

    const currentModuleMatches = lookupQualified(index, qualify(currentDocument.moduleName, normalized));
    if (currentModuleMatches.length > 0) {
        return currentModuleMatches;
    }

    const importedMatches = [];
    for (const importName of currentDocument.imports) {
        importedMatches.push(...lookupQualified(index, qualify(importName, normalized)));
        if (tailName(importName) === normalized) {
            importedMatches.push(...lookupQualified(index, importName));
        }
    }

    if (importedMatches.length > 0) {
        return importedMatches;
    }

    return index.byName.get(normalized) ?? [];
}

function addDeclaration(index, declaration) {
    index.declarations.push(declaration);
    pushMap(index.byQualifiedName, declaration.qualifiedName, declaration);
    pushMap(index.byName, declaration.name, declaration);
}

function addDocumentDeclaration(document, declaration) {
    if (!declaration.moduleName && declaration.kind !== "module") {
        return null;
    }

    const character = Math.max(0, declaration.character ?? 0);
    const nameLength = declaration.rangeLength ?? declaration.name.length;
    const range = {
        start: { line: declaration.line, character },
        end: { line: declaration.line, character: character + nameLength },
    };

    const fullDeclaration = {
        uri: document.uri,
        kind: declaration.kind,
        name: declaration.name,
        qualifiedName: declaration.qualifiedName,
        moduleName: declaration.moduleName,
        owner: declaration.owner,
        line: declaration.line,
        character,
        range,
    };

    document.declarations.push(fullDeclaration);
    return fullDeclaration;
}

function addLocalDeclaration(document, declaration) {
    const character = Math.max(0, declaration.character ?? 0);
    const range = {
        start: { line: declaration.line, character },
        end: { line: declaration.line, character: character + declaration.name.length },
    };

    document.localDeclarations.push({
        uri: document.uri,
        kind: declaration.kind,
        name: declaration.name,
        qualifiedName: `local:${declaration.scopeId}:${declaration.name}`,
        scopeId: declaration.scopeId,
        line: declaration.line,
        character,
        range,
    });
}

function addParameterDeclarations(document, scope, rawLine, lineIndex) {
    const openIndex = rawLine.indexOf("(");
    if (openIndex < 0) {
        return true;
    }

    const closeIndex = matchingCloseParen(rawLine, openIndex);
    if (closeIndex < 0) {
        addParameterDeclarationsFromText(document, scope, rawLine.slice(openIndex + 1), lineIndex, openIndex + 1);
        return false;
    }

    const parameterText = rawLine.slice(openIndex + 1, closeIndex);
    addParameterDeclarationsFromText(document, scope, parameterText, lineIndex, openIndex + 1);
    return true;
}

function addParameterDeclarationsFromLine(document, scope, rawLine, lineIndex) {
    const cleanLine = stripLineComment(rawLine);
    const closeIndex = cleanLine.indexOf(")");
    const parameterText = closeIndex < 0
        ? cleanLine
        : cleanLine.slice(0, closeIndex);

    addParameterDeclarationsFromText(document, scope, parameterText, lineIndex, 0);
    return closeIndex >= 0;
}

function addParameterDeclarationsFromText(document, scope, parameterText, lineIndex, characterOffset) {
    let searchStart = 0;
    for (const parameter of splitTopLevelComma(parameterText)) {
        const name = parameterNameFrom(parameter);
        if (!name) {
            continue;
        }

        const parameterStart = Math.max(0, parameterText.indexOf(parameter, searchStart));
        const nameStart = parameter.lastIndexOf(name);
        searchStart = parameterStart + parameter.length;

        addLocalDeclaration(document, {
            kind: "parameter",
            name,
            scopeId: scope.id,
            line: lineIndex,
            character: characterOffset + parameterStart + Math.max(0, nameStart),
        });
    }
}

function resolveLocalTarget(document, target) {
    if (!target.identifierText) {
        return [];
    }

    const normalized = target.normalizedText ?? target.text;
    if (normalized.includes(".") && target.identifierStart !== target.range.start.character) {
        return [];
    }

    const name = normalized.includes(".") ? target.identifierText : normalized;
    const scope = scopeAt(document, target.line);
    if (!scope) {
        return [];
    }

    return document.localDeclarations
        .filter(declaration =>
            declaration.scopeId === scope.id
            && declaration.name === name
            && isBeforeOrAt(declaration, target))
        .sort((left, right) =>
            right.line - left.line
            || right.character - left.character)
        .slice(0, 1);
}

function parseTypeDeclaration(line) {
    const match = line.match(/^(?:(?:public|internal|export)\s+)?(struct|record|enum|trait|doctrine)\s+([A-Za-z_][A-Za-z0-9_]*)(?:<[^>{}()]*>)?/);
    if (!match) {
        return null;
    }

    return {
        kind: match[1],
        name: match[2],
    };
}

function parseAliasDeclaration(line) {
    const match = line.match(/^(?:(?:public|internal|export)\s+)?alias\s+([A-Za-z_][A-Za-z0-9_]*)(?:<[^>{}()]*>)?\s*=/);
    if (!match) {
        return null;
    }

    return {
        kind: "alias",
        name: match[1],
    };
}

function parseCallableDeclaration(line, currentType) {
    const normalized = line.replace(/\s+/g, " ").trim();
    const tokens = normalized.split(/\s+/);
    let index = 0;

    if (["public", "internal", "export"].includes(tokens[index])) {
        index++;
    }

    while (declarationModifiers.has(tokens[index]) || /^asm\b/.test(tokens[index] ?? "")) {
        index++;
    }

    let functionKind = null;
    if (tokens[index] === "finite" && tokens[index + 1] === "law") {
        functionKind = "law";
        index += 2;
    } else if (tokens[index] === "law") {
        functionKind = "law";
        index++;
    } else if (tokens[index] === "finite" || tokens[index] === "fn") {
        functionKind = "function";
        index++;
    }

    if (!functionKind) {
        return null;
    }

    const openParen = normalized.indexOf("(");
    if (openParen < 0) {
        return null;
    }

    const beforeParameters = normalized.slice(0, openParen).trim();
    const nameMatch = beforeParameters.match(/([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>]*>)?$/);
    if (!nameMatch) {
        return null;
    }

    return {
        kind: currentType ? "method" : functionKind,
        name: nameMatch[1],
        owner: currentType?.name,
    };
}

function parseConstructorDeclaration(line, ownerName) {
    const match = line.match(new RegExp(`^${escapeRegExp(ownerName)}\\s*\\(`));
    if (!match) {
        return null;
    }

    return {
        kind: "constructor",
        name: ownerName,
    };
}

function parseFieldDeclaration(line) {
    if (!line.endsWith(";") || line.includes("(")) {
        return null;
    }

    const normalized = line.replace(/;$/, "").replace(/=.*/, "").trim();
    const match = normalized.match(/(?:^|\s)([A-Za-z_][A-Za-z0-9_]*)$/);
    if (!match) {
        return null;
    }

    const name = match[1];
    if (["break", "case", "continue", "return"].includes(name)) {
        return null;
    }

    return {
        kind: "field",
        name,
    };
}

function parseLocalDeclaration(line) {
    const storageMatch = line.match(/^(?:stack|heap|arena|register)\s+(?:mut\s+)?(.+?)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:=|;|\{)/);
    if (storageMatch) {
        return {
            kind: "local",
            name: storageMatch[2],
        };
    }

    const inferredMatch = line.match(/^(?:const|var)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:=|;)/);
    if (inferredMatch) {
        return {
            kind: "local",
            name: inferredMatch[1],
        };
    }

    return null;
}

function parsePatternLocalDeclarations(line) {
    const caseMatch = line.match(/^\s*case\b/);
    if (!caseMatch) {
        return [];
    }

    const patternStart = caseMatch[0].length;
    const patternEnd = findTopLevelCaseColon(line, patternStart);
    const patternText = line.slice(patternStart, patternEnd < 0 ? line.length : patternEnd);
    const declarations = [];
    const variablePattern = /\bvar\s+([A-Za-z_][A-Za-z0-9_]*)\b/g;
    let match = null;

    while ((match = variablePattern.exec(patternText)) !== null) {
        const name = match[1];
        declarations.push({
            kind: "pattern",
            name,
            character: patternStart + match.index + match[0].lastIndexOf(name),
        });
    }

    return declarations;
}

function findTopLevelCaseColon(line, startIndex) {
    let parenDepth = 0;
    let angleDepth = 0;
    let bracketDepth = 0;
    let braceDepth = 0;
    let inString = false;
    let inCharacter = false;
    let escaped = false;

    for (let index = startIndex; index < line.length; index++) {
        const current = line[index];

        if (escaped) {
            escaped = false;
            continue;
        }

        if ((inString || inCharacter) && current === "\\") {
            escaped = true;
            continue;
        }

        if (!inCharacter && current === "\"") {
            inString = !inString;
            continue;
        }

        if (!inString && current === "'") {
            inCharacter = !inCharacter;
            continue;
        }

        if (inString || inCharacter) {
            continue;
        }

        switch (current) {
            case "(":
                parenDepth++;
                break;
            case ")":
                parenDepth = Math.max(0, parenDepth - 1);
                break;
            case "<":
                angleDepth++;
                break;
            case ">":
                angleDepth = Math.max(0, angleDepth - 1);
                break;
            case "[":
                bracketDepth++;
                break;
            case "]":
                bracketDepth = Math.max(0, bracketDepth - 1);
                break;
            case "{":
                braceDepth++;
                break;
            case "}":
                braceDepth = Math.max(0, braceDepth - 1);
                break;
            case ":":
                if (parenDepth === 0 && angleDepth === 0 && bracketDepth === 0 && braceDepth === 0) {
                    return index;
                }
                break;
        }
    }

    return -1;
}

function parseEnumMember(line) {
    if (/^(case|default|if|else|for|while|switch|return|break|continue)\b/.test(line)) {
        return null;
    }

    const match = line.match(/^([A-Za-z_][A-Za-z0-9_]*)(?:\s*(?:\(|\{|,|$))/);
    if (!match) {
        return null;
    }

    return {
        name: match[1],
    };
}

function matchingCloseParen(text, openIndex) {
    if (openIndex < 0) {
        return -1;
    }

    let depth = 0;
    let angleDepth = 0;
    let bracketDepth = 0;

    for (let index = openIndex; index < text.length; index++) {
        const character = text[index];

        switch (character) {
            case "(":
                depth++;
                break;
            case ")":
                depth--;
                if (depth === 0) {
                    return index;
                }
                break;
            case "<":
                angleDepth++;
                break;
            case ">":
                angleDepth = Math.max(0, angleDepth - 1);
                break;
            case "[":
                bracketDepth++;
                break;
            case "]":
                bracketDepth = Math.max(0, bracketDepth - 1);
                break;
        }

        if (depth < 0 || angleDepth < 0 || bracketDepth < 0) {
            return -1;
        }
    }

    return -1;
}

function splitTopLevelComma(text) {
    const parts = [];
    let start = 0;
    let parenDepth = 0;
    let angleDepth = 0;
    let bracketDepth = 0;

    for (let index = 0; index < text.length; index++) {
        const character = text[index];

        switch (character) {
            case "(":
                parenDepth++;
                break;
            case ")":
                parenDepth--;
                break;
            case "<":
                angleDepth++;
                break;
            case ">":
                angleDepth--;
                break;
            case "[":
                bracketDepth++;
                break;
            case "]":
                bracketDepth--;
                break;
            case ",":
                if (parenDepth === 0 && angleDepth === 0 && bracketDepth === 0) {
                    parts.push(text.slice(start, index).trim());
                    start = index + 1;
                }
                break;
        }
    }

    const tail = text.slice(start).trim();
    if (tail.length > 0) {
        parts.push(tail);
    }

    return parts;
}

function parameterNameFrom(parameter) {
    const normalized = parameter
        .replace(/\b(?:borrow|mut|out|init|frozen|shared|disjoint|overlap|same|const)\b/g, " ")
        .trim();
    const match = normalized.match(/([A-Za-z_][A-Za-z0-9_]*)\s*$/);
    return match ? match[1] : null;
}

function scopeAt(document, line) {
    return document.scopes
        .filter(scope => scope.startLine <= line && line <= scope.endLine)
        .sort((left, right) =>
            right.startLine - left.startLine
            || right.bodyDepth - left.bodyDepth)[0] ?? null;
}

function isBeforeOrAt(declaration, target) {
    if (declaration.line < target.line) {
        return true;
    }

    return declaration.line === target.line
        && declaration.character <= target.identifierStart;
}

function extractImportOrModuleTarget(line, lineText, character) {
    const match = lineText.match(/^\s*(?:export\s+)?(import|module)\s+([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)/);
    if (!match) {
        return null;
    }

    const start = lineText.indexOf(match[2]);
    const end = start + match[2].length;
    if (character < start || character > end) {
        return null;
    }

    return {
        kind: "module",
        text: match[2],
        normalizedText: match[2],
        range: {
            start: { line, character: start },
            end: { line, character: end },
        },
    };
}

function identifierAt(lineText, character) {
    let start = Math.min(character, lineText.length);
    if (start > 0 && !identifierCharacterPattern.test(lineText[start] ?? "") && identifierCharacterPattern.test(lineText[start - 1] ?? "")) {
        start--;
    }

    while (start > 0 && identifierCharacterPattern.test(lineText[start - 1])) {
        start--;
    }

    identifierPattern.lastIndex = start;
    const match = identifierPattern.exec(lineText);
    if (!match || character < start || character > start + match[0].length) {
        return null;
    }

    return {
        text: match[0],
        start,
        end: start + match[0].length,
    };
}

function qualifiedTargetAt(lineText, identifier) {
    let start = identifier.start;
    let end = identifier.end;

    while (lineText[start - 1] === ".") {
        const previousStart = previousQualifiedSegmentStart(lineText, start - 1);
        if (previousStart === null) {
            break;
        }

        start = previousStart;
    }

    while (lineText[end] === ".") {
        const next = nextIdentifierRange(lineText, end + 1);
        if (!next) {
            break;
        }

        end = next.end;
    }

    return {
        text: lineText.slice(start, end),
        start,
        end,
    };
}

function previousQualifiedSegmentStart(lineText, dotIndex) {
    let end = dotIndex;
    if (end <= 0) {
        return null;
    }

    if (lineText[end - 1] === ">") {
        const genericStart = findMatchingGenericStart(lineText, end - 1);
        if (genericStart === null) {
            return null;
        }

        end = genericStart;
    }

    let start = end;
    while (start > 0 && identifierCharacterPattern.test(lineText[start - 1])) {
        start--;
    }

    if (start === end) {
        return null;
    }

    while (lineText[start - 1] === ".") {
        const previousStart = previousQualifiedSegmentStart(lineText, start - 1);
        if (previousStart === null) {
            break;
        }

        start = previousStart;
    }

    return start;
}

function nextIdentifierRange(lineText, start) {
    identifierPattern.lastIndex = start;
    const match = identifierPattern.exec(lineText);
    if (!match || match.index !== start) {
        return null;
    }

    return {
        start,
        end: start + match[0].length,
    };
}

function findMatchingGenericStart(lineText, endIndex) {
    let depth = 0;
    for (let index = endIndex; index >= 0; index--) {
        const character = lineText[index];
        if (character === ">") {
            depth++;
        } else if (character === "<") {
            depth--;
            if (depth === 0) {
                return index;
            }
        }
    }

    return null;
}

function lookupQualified(index, qualifiedName) {
    if (!qualifiedName) {
        return [];
    }

    return index.byQualifiedName.get(qualifiedName) ?? [];
}

function removeTrailingMember(value) {
    const dotIndex = value.lastIndexOf(".");
    return dotIndex < 0 ? value : value.slice(0, dotIndex);
}

function uniqueDeclarations(declarations) {
    const seen = new Set();
    const result = [];

    for (const declaration of declarations) {
        const key = `${declaration.uri}:${declaration.kind}:${declaration.qualifiedName}:${declaration.line}:${declaration.character}`;
        if (seen.has(key)) {
            continue;
        }

        seen.add(key);
        result.push(declaration);
    }

    return result.sort((left, right) =>
        left.uri.localeCompare(right.uri)
        || left.line - right.line
        || left.character - right.character
        || left.qualifiedName.localeCompare(right.qualifiedName));
}

function pushMap(map, key, value) {
    if (!key) {
        return;
    }

    if (!map.has(key)) {
        map.set(key, []);
    }

    map.get(key).push(value);
}

function qualify(...parts) {
    return parts.filter(Boolean).join(".");
}

function tailName(value) {
    const parts = value.split(".");
    return parts[parts.length - 1];
}

function stripTypeArguments(value) {
    let result = "";
    let depth = 0;

    for (const character of value) {
        if (character === "<") {
            depth++;
            continue;
        }

        if (character === ">") {
            depth = Math.max(0, depth - 1);
            continue;
        }

        if (depth === 0) {
            result += character;
        }
    }

    return result;
}

function stripLineComment(line) {
    let inString = false;
    let inCharacter = false;
    let escaped = false;

    for (let index = 0; index < line.length - 1; index++) {
        const current = line[index];
        const next = line[index + 1];

        if (escaped) {
            escaped = false;
            continue;
        }

        if ((inString || inCharacter) && current === "\\") {
            escaped = true;
            continue;
        }

        if (!inCharacter && current === "\"") {
            inString = !inString;
            continue;
        }

        if (!inString && current === "'") {
            inCharacter = !inCharacter;
            continue;
        }

        if (!inString && !inCharacter && current === "/" && next === "/") {
            return line.slice(0, index);
        }
    }

    return line;
}

function countBraceDelta(line) {
    let delta = 0;
    let inString = false;
    let inCharacter = false;
    let escaped = false;

    for (const current of line) {
        if (escaped) {
            escaped = false;
            continue;
        }

        if ((inString || inCharacter) && current === "\\") {
            escaped = true;
            continue;
        }

        if (!inCharacter && current === "\"") {
            inString = !inString;
            continue;
        }

        if (!inString && current === "'") {
            inCharacter = !inCharacter;
            continue;
        }

        if (inString || inCharacter) {
            continue;
        }

        if (current === "{") {
            delta++;
        } else if (current === "}") {
            delta--;
        }
    }

    return delta;
}

function escapeRegExp(value) {
    return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

module.exports = {
    createDefinitionIndex,
    extractDefinitionTarget,
    parseDocument,
    resolveDefinition,
    stripTypeArguments,
};
