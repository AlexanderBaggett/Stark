#!/usr/bin/env node
"use strict";

const fs = require("fs");
const path = require("path");

const repoRoot = path.resolve(__dirname, "..", "..");
const stdlibRoot = path.join(repoRoot, "stdlib", "src");
const outputPath = path.join(__dirname, "..", "data", "stdlib-completions.json");

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

function walkStarkFiles(directory) {
    const results = [];

    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
        const fullPath = path.join(directory, entry.name);

        if (entry.isDirectory()) {
            results.push(...walkStarkFiles(fullPath));
        } else if (entry.isFile() && entry.name.endsWith(".stark")) {
            results.push(fullPath);
        }
    }

    return results.sort((left, right) => left.localeCompare(right));
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

    for (const current of stripLineComment(line)) {
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

function collectSignature(lines, startIndex) {
    let signature = "";
    let index = startIndex;
    let parenDepth = 0;
    let angleDepth = 0;
    let bracketDepth = 0;

    for (; index < lines.length; index++) {
        const raw = stripLineComment(lines[index]).trim();

        if (raw.length === 0) {
            continue;
        }

        signature = signature.length === 0 ? raw : `${signature} ${raw}`;

        for (const character of raw) {
            switch (character) {
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
            }
        }

        if (parenDepth === 0 && angleDepth === 0 && bracketDepth === 0 && /[;{]\s*$/.test(raw)) {
            break;
        }
    }

    signature = signature.replace(/\s*[;{]\s*$/, "").trim();
    return { signature, endIndex: index };
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

function parameterNames(signature, name) {
    const nameIndex = signature.indexOf(name);
    if (nameIndex < 0) {
        return [];
    }

    const openIndex = signature.indexOf("(", nameIndex + name.length);
    if (openIndex < 0) {
        return [];
    }

    let depth = 0;
    let closeIndex = -1;

    for (let index = openIndex; index < signature.length; index++) {
        const character = signature[index];

        if (character === "(") {
            depth++;
        } else if (character === ")") {
            depth--;

            if (depth === 0) {
                closeIndex = index;
                break;
            }
        }
    }

    if (closeIndex < 0) {
        return [];
    }

    const parameters = signature.slice(openIndex + 1, closeIndex).trim();
    if (parameters.length === 0) {
        return [];
    }

    return splitTopLevelComma(parameters)
        .map(parameter => {
            const withoutContracts = parameter.replace(/\b(disjoint|const)\s+/g, "").trim();
            const match = withoutContracts.match(/([A-Za-z][A-Za-z0-9_]*)\s*$/);
            return match ? match[1] : "value";
        })
        .filter(Boolean);
}

function snippetForCall(name, signature) {
    const names = parameterNames(signature, name);
    const placeholders = names.map((parameter, index) => `\${${index + 1}:${parameter}}`);
    return `${name}(${placeholders.join(", ")})`;
}

function parseFunction(signature) {
    const tokens = signature.split(/\s+/);
    let index = 0;
    let visibility = null;

    if (["public", "internal", "export"].includes(tokens[index])) {
        visibility = tokens[index++];
    }

    while (declarationModifiers.has(tokens[index])) {
        index++;
    }

    let functionKind = null;
    if (tokens[index] === "finite" && tokens[index + 1] === "law") {
        functionKind = "finite law";
        index += 2;
    } else if (["finite", "law", "fn"].includes(tokens[index])) {
        functionKind = tokens[index++];
    }

    if (!functionKind) {
        return null;
    }

    const parenIndex = signature.indexOf("(");
    if (parenIndex < 0) {
        return null;
    }

    const beforeParameters = signature.slice(0, parenIndex).trim();
    const nameMatch = beforeParameters.match(/([A-Za-z][A-Za-z0-9_]*)\s*(?:<[^>\n]+>)?$/);
    if (!nameMatch) {
        return null;
    }

    const name = nameMatch[1];
    const returnType = beforeParameters.slice(0, nameMatch.index).trim().split(/\s+/).slice(index).join(" ");

    return {
        visibility,
        functionKind,
        name,
        returnType,
        signature,
    };
}

function parseTypeDeclaration(line) {
    return line.match(/^(?:(public|internal|export)\s+)?(struct|record|enum|trait|doctrine)\s+([A-Za-z][A-Za-z0-9_]*)(?:<[^>\n]+>)?/);
}

function parseAlias(line) {
    return line.match(/^(?:(public|internal|export)\s+)?alias\s+([A-Za-z][A-Za-z0-9_]*)(?:<[^>\n]+>)?\s*=\s*([^;]+)/);
}

function parseConstructor(line, owner) {
    if (!owner) {
        return null;
    }

    const escaped = owner.name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    const match = line.match(new RegExp(`^${escaped}\\s*\\(`));
    if (!match) {
        return null;
    }

    const { signature } = collectSignature([line], 0);
    return {
        name: owner.name,
        signature,
    };
}

function parseField(line) {
    if (!line.endsWith(";")) {
        return null;
    }

    const normalized = line.replace(/;$/, "").trim();
    const match = normalized.match(/^(?:(public|internal|export)\s+)?(?:mut\s+)?(.+?)\s+([A-Za-z][A-Za-z0-9_]*)(?:\s*=.*)?$/);

    if (!match) {
        return null;
    }

    const [, visibility, type, name] = match;
    if (["return", "break", "continue"].includes(type)) {
        return null;
    }

    return {
        visibility,
        type: type.trim(),
        name,
        signature: line,
    };
}

function makeDocumentation(entry) {
    const blocks = [];

    if (entry.signature) {
        const signature = entry.signature.endsWith(";") ? entry.signature : `${entry.signature};`;
        blocks.push(`\`\`\`stark\n${signature}\n\`\`\``);
    }

    blocks.push(`${entry.module}${entry.owner ? `.${entry.owner}` : ""}`);

    if (entry.visibility) {
        blocks.push(`Visibility: ${entry.visibility}`);
    }

    return blocks.join("\n\n");
}

function addEntry(entries, entry) {
    entries.push({
        ...entry,
        qualifiedName: entry.kind === "module"
            ? entry.module
            : [entry.module, entry.owner, entry.label].filter(Boolean).join("."),
        documentation: makeDocumentation(entry),
    });
}

function parseFile(filePath) {
    const text = fs.readFileSync(filePath, "utf8");
    const lines = text.split(/\r?\n/);
    const moduleMatch = text.match(/^\s*module\s+([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)*)/m);

    if (!moduleMatch) {
        return [];
    }

    const moduleName = moduleMatch[1];
    const source = path.relative(repoRoot, filePath).replace(/\\/g, "/");
    const entries = [];
    let braceDepth = 0;
    let currentType = null;
    let pendingType = null;

    addEntry(entries, {
        label: moduleName,
        insertText: moduleName,
        kind: "module",
        module: moduleName,
        source,
        detail: "standard library module",
        visibility: "public",
    });

    for (let index = 0; index < lines.length; index++) {
        const rawLine = stripLineComment(lines[index]);
        const trimmed = rawLine.trim();
        const depthBeforeLine = braceDepth;

        if (trimmed.length === 0) {
            braceDepth += countBraceDelta(rawLine);
            continue;
        }

        if (currentType && depthBeforeLine < currentType.depth) {
            currentType = null;
        }

        const typeDeclaration = parseTypeDeclaration(trimmed);
        if (typeDeclaration) {
            const [, visibility, kind, name] = typeDeclaration;
            const publicLike = visibility === "public" || visibility === "export";

            if (publicLike) {
                addEntry(entries, {
                    label: name,
                    insertText: name,
                    kind,
                    module: moduleName,
                    source,
                    detail: `${visibility} ${kind} in ${moduleName}`,
                    signature: trimmed,
                    visibility,
                });
            }

            pendingType = {
                name,
                kind,
                visibility: visibility ?? "private",
                publicLike,
            };
        }

        const alias = parseAlias(trimmed);
        if (alias && (alias[1] === "public" || alias[1] === "export")) {
            const [, visibility, name, target] = alias;

            addEntry(entries, {
                label: name,
                insertText: name,
                kind: "alias",
                module: moduleName,
                source,
                detail: `${visibility} alias for ${target.trim()}`,
                signature: trimmed,
                visibility,
            });
        }

        const { signature } = collectSignature(lines, index);
        const functionDeclaration = parseFunction(signature);
        if (functionDeclaration) {
            const inheritedPublic = currentType?.publicLike && !functionDeclaration.visibility;
            const publicLike = functionDeclaration.visibility === "public"
                || functionDeclaration.visibility === "export"
                || inheritedPublic;

            if (publicLike) {
                const kind = currentType ? "method" : "function";
                const visibility = functionDeclaration.visibility ?? currentType.visibility;

                addEntry(entries, {
                    label: functionDeclaration.name,
                    insertText: snippetForCall(functionDeclaration.name, functionDeclaration.signature),
                    kind,
                    module: moduleName,
                    owner: currentType?.name,
                    source,
                    detail: functionDeclaration.signature,
                    signature: functionDeclaration.signature,
                    visibility,
                });
            }
        } else if (currentType?.publicLike && depthBeforeLine === currentType.depth) {
            const constructor = parseConstructor(trimmed, currentType);

            if (constructor) {
                addEntry(entries, {
                    label: constructor.name,
                    insertText: snippetForCall(constructor.name, constructor.signature),
                    kind: "constructor",
                    module: moduleName,
                    owner: currentType.name,
                    source,
                    detail: constructor.signature,
                    signature: constructor.signature,
                    visibility: currentType.visibility,
                });
            } else if (currentType.kind === "enum") {
                const variant = trimmed.match(/^([A-Za-z][A-Za-z0-9_]*)(?:\s*(?:\(|\{|,|$))/);

                if (variant && !["public", "internal", "export"].includes(variant[1])) {
                    addEntry(entries, {
                        label: variant[1],
                        insertText: variant[1],
                        kind: "enumMember",
                        module: moduleName,
                        owner: currentType.name,
                        source,
                        detail: trimmed.replace(/,$/, ""),
                        signature: trimmed.replace(/,$/, ""),
                        visibility: currentType.visibility,
                    });
                }
            } else {
                const field = parseField(trimmed);

                const fieldPublicLike = field
                    && (field.visibility === "public" || field.visibility === "export" || (!field.visibility && currentType.publicLike));

                if (fieldPublicLike) {
                    addEntry(entries, {
                        label: field.name,
                        insertText: field.name,
                        kind: "field",
                        module: moduleName,
                        owner: currentType.name,
                        source,
                        detail: field.signature,
                        signature: field.signature,
                        visibility: field.visibility ?? currentType.visibility,
                    });
                }
            }
        }

        const delta = countBraceDelta(rawLine);
        braceDepth += delta;

        if (pendingType && rawLine.includes("{")) {
            currentType = {
                ...pendingType,
                depth: depthBeforeLine + Math.max(1, countBraceDelta(rawLine)),
            };
            pendingType = null;
        }
    }

    return entries;
}

function modulePrefixEntries(entries) {
    const modules = new Set(entries.filter(entry => entry.kind === "module").map(entry => entry.module));
    const prefixes = new Map();

    for (const moduleName of modules) {
        const parts = moduleName.split(".");

        for (let index = 1; index < parts.length; index++) {
            const prefix = parts.slice(0, index).join(".");
            if (!modules.has(prefix) && !prefixes.has(prefix)) {
                prefixes.set(prefix, {
                    label: prefix,
                    insertText: prefix,
                    kind: "module",
                    module: prefix,
                    source: "generated",
                    detail: "standard library module namespace",
                    visibility: "public",
                    qualifiedName: prefix,
                    documentation: `Standard library module namespace \`${prefix}\`.`,
                });
            }
        }
    }

    return [...prefixes.values()];
}

const entries = walkStarkFiles(stdlibRoot).flatMap(parseFile);
const allEntries = [...modulePrefixEntries(entries), ...entries]
    .sort((left, right) => {
        const moduleCompare = left.qualifiedName.localeCompare(right.qualifiedName);
        if (moduleCompare !== 0) {
            return moduleCompare;
        }

        return (left.signature ?? "").localeCompare(right.signature ?? "");
    });

fs.writeFileSync(outputPath, `${JSON.stringify(allEntries, null, 2)}\n`);
console.log(`Wrote ${allEntries.length} standard library completion entries to ${path.relative(repoRoot, outputPath)}.`);
