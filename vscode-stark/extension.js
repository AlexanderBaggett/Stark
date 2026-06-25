"use strict";

const vscode = require("vscode");
const {
    createDefinitionIndex,
    resolveDefinition,
} = require("./definition-index");

const languageCompletions = require("./data/language-completions.json");
const stdlibCompletions = require("./data/stdlib-completions.json");

const kindMap = new Map([
    ["alias", vscode.CompletionItemKind.TypeParameter],
    ["class", vscode.CompletionItemKind.Class],
    ["constructor", vscode.CompletionItemKind.Constructor],
    ["doctrine", vscode.CompletionItemKind.Interface],
    ["enum", vscode.CompletionItemKind.Enum],
    ["enumMember", vscode.CompletionItemKind.EnumMember],
    ["field", vscode.CompletionItemKind.Field],
    ["function", vscode.CompletionItemKind.Function],
    ["keyword", vscode.CompletionItemKind.Keyword],
    ["method", vscode.CompletionItemKind.Method],
    ["module", vscode.CompletionItemKind.Module],
    ["record", vscode.CompletionItemKind.Struct],
    ["snippet", vscode.CompletionItemKind.Snippet],
    ["struct", vscode.CompletionItemKind.Struct],
    ["trait", vscode.CompletionItemKind.Interface],
]);

function completionKind(kind) {
    return kindMap.get(kind) ?? vscode.CompletionItemKind.Text;
}

function markdown(text) {
    const value = new vscode.MarkdownString(text ?? "");
    value.supportHtml = false;
    value.isTrusted = false;
    return value;
}

function usesSnippetSyntax(text) {
    return typeof text === "string" && (text.includes("$0") || /\$\{\d+/.test(text));
}

function createCompletionItem(entry, options = {}) {
    const item = new vscode.CompletionItem(entry.label, completionKind(entry.kind));
    item.detail = entry.detail;
    item.documentation = markdown(entry.documentation);

    if (entry.insertText) {
        item.insertText = usesSnippetSyntax(entry.insertText)
            ? new vscode.SnippetString(entry.insertText)
            : entry.insertText;
    }

    item.filterText = entry.filterText ?? entry.qualifiedName ?? entry.label;
    item.sortText = options.sortText ?? entry.sortText;

    if (options.range) {
        item.range = options.range;
    }

    if (entry.kind === "module") {
        item.commitCharacters = ["."];
    } else if (entry.kind === "function" || entry.kind === "method" || entry.kind === "constructor") {
        item.commitCharacters = ["("];
    }

    return item;
}

function wordRangeFrom(document, position, startColumn) {
    return new vscode.Range(
        new vscode.Position(position.line, startColumn),
        position,
    );
}

function currentLinePrefix(document, position) {
    return document.lineAt(position.line).text.slice(0, position.character);
}

function importedModules(document) {
    const modules = new Set();
    const importPattern = /^\s*(?:export\s+)?import\s+([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)*)\s*$/;

    for (let index = 0; index < document.lineCount; index++) {
        const match = document.lineAt(index).text.match(importPattern);

        if (match) {
            modules.add(match[1]);
        }
    }

    return modules;
}

function moduleTail(moduleName) {
    const parts = moduleName.split(".");
    return parts[parts.length - 1];
}

function moduleCompletionsForImport(document, position, linePrefix) {
    const match = linePrefix.match(/^\s*(?:export\s+)?import\s+([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)*\.?)?$/);
    if (!match) {
        return null;
    }

    const startColumn = linePrefix.length - (match[1]?.length ?? 0);
    const replacementRange = wordRangeFrom(document, position, startColumn);

    return stdlibCompletions
        .filter(entry => entry.kind === "module" && entry.source !== "generated")
        .map(entry => createCompletionItem(
            {
                ...entry,
                label: entry.qualifiedName,
                insertText: entry.qualifiedName,
                filterText: entry.qualifiedName,
            },
            { range: replacementRange, sortText: `0:${entry.qualifiedName}` },
        ));
}

function qualifiedCompletions(prefix) {
    const items = [];
    const seen = new Set();
    const childPrefix = `${prefix}.`;

    for (const entry of stdlibCompletions) {
        if (entry.kind === "module" && entry.qualifiedName?.startsWith(childPrefix)) {
            const rest = entry.qualifiedName.slice(childPrefix.length);
            const child = rest.split(".")[0];
            const key = `module:${child}`;

            if (!seen.has(key)) {
                seen.add(key);
                items.push({
                    label: child,
                    insertText: child,
                    kind: "module",
                    detail: `standard library module namespace ${prefix}.${child}`,
                    documentation: `Standard library module or namespace \`${prefix}.${child}\`.`,
                    qualifiedName: `${prefix}.${child}`,
                });
            }

            continue;
        }

        if (entry.module === prefix && entry.kind !== "module") {
            const key = `${entry.kind}:${entry.owner ?? ""}:${entry.label}:${entry.signature ?? ""}`;

            if (!seen.has(key)) {
                seen.add(key);
                items.push(entry);
            }
        }

        if (entry.owner && `${entry.module}.${entry.owner}` === prefix) {
            const key = `${entry.kind}:${entry.label}:${entry.signature ?? ""}`;

            if (!seen.has(key)) {
                seen.add(key);
                items.push(entry);
            }
        }

        if (entry.owner && entry.owner === prefix) {
            const key = `owner:${entry.kind}:${entry.label}:${entry.signature ?? ""}`;

            if (!seen.has(key)) {
                seen.add(key);
                items.push(entry);
            }
        }
    }

    return items;
}

function qualifiedAccess(linePrefix) {
    const match = linePrefix.match(/([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)*)\.([A-Za-z][A-Za-z0-9_]*)?$/);

    if (!match) {
        return null;
    }

    return {
        prefix: match[1],
        partial: match[2] ?? "",
    };
}

function rootCompletions(document) {
    const imports = importedModules(document);
    const items = [];

    for (const entry of languageCompletions) {
        items.push(createCompletionItem(entry, { sortText: `0:${entry.label}` }));
    }

    for (const entry of stdlibCompletions) {
        if (entry.kind === "module") {
            items.push(createCompletionItem(
                {
                    ...entry,
                    label: entry.qualifiedName,
                    insertText: entry.qualifiedName,
                    filterText: entry.qualifiedName,
                },
                { sortText: `1:${entry.qualifiedName}` },
            ));
            continue;
        }

        if (entry.kind === "field") {
            continue;
        }

        const imported = imports.has(entry.module);
        const sameTailImported = [...imports].some(importName => moduleTail(importName) === moduleTail(entry.module));
        const sortGroup = imported || sameTailImported ? "1" : "3";
        items.push(createCompletionItem(entry, { sortText: `${sortGroup}:${entry.label}:${entry.signature ?? ""}` }));
    }

    return items;
}

function isStarkDocument(document) {
    return document.languageId === "stark" || document.uri.path.endsWith(".stark");
}

async function starkWorkspaceDocuments() {
    const documents = new Map();
    const uris = await vscode.workspace.findFiles("**/*.stark", "**/{.git,node_modules}/**");

    await Promise.all(uris.map(async uri => {
        const bytes = await vscode.workspace.fs.readFile(uri);
        documents.set(uri.toString(), {
            uri: uri.toString(),
            text: Buffer.from(bytes).toString("utf8"),
        });
    }));

    for (const document of vscode.workspace.textDocuments) {
        if (isStarkDocument(document)) {
            documents.set(document.uri.toString(), {
                uri: document.uri.toString(),
                text: document.getText(),
            });
        }
    }

    return [...documents.values()];
}

function createStarkDefinitionProvider(context) {
    let index = createDefinitionIndex([]);
    let dirty = true;
    let rebuildPromise = null;
    let rebuildTimer = null;

    async function rebuildIndex() {
        const documents = await starkWorkspaceDocuments();
        index = createDefinitionIndex(documents);
        dirty = false;
        rebuildPromise = null;
        return index;
    }

    function ensureIndex() {
        if (!dirty) {
            return Promise.resolve(index);
        }

        if (!rebuildPromise) {
            rebuildPromise = rebuildIndex();
        }

        return rebuildPromise;
    }

    function scheduleRebuild() {
        dirty = true;
        if (rebuildTimer) {
            clearTimeout(rebuildTimer);
        }

        rebuildTimer = setTimeout(() => {
            rebuildPromise = rebuildIndex().catch(error => {
                dirty = true;
                rebuildPromise = null;
                console.error("Failed to rebuild Stark definition index", error);
            });
        }, 250);
    }

    const watcher = vscode.workspace.createFileSystemWatcher("**/*.stark");
    context.subscriptions.push(
        watcher,
        watcher.onDidCreate(scheduleRebuild),
        watcher.onDidChange(scheduleRebuild),
        watcher.onDidDelete(scheduleRebuild),
        vscode.workspace.onDidOpenTextDocument(document => {
            if (isStarkDocument(document)) {
                scheduleRebuild();
            }
        }),
        vscode.workspace.onDidSaveTextDocument(document => {
            if (isStarkDocument(document)) {
                scheduleRebuild();
            }
        }),
        vscode.workspace.onDidChangeTextDocument(event => {
            if (isStarkDocument(event.document)) {
                scheduleRebuild();
            }
        }),
        vscode.workspace.onDidChangeWorkspaceFolders(scheduleRebuild),
    );

    rebuildPromise = rebuildIndex().catch(error => {
        dirty = true;
        rebuildPromise = null;
        console.error("Failed to build Stark definition index", error);
    });

    return {
        async provideDefinition(document, position) {
            const currentIndex = await ensureIndex();
            const definitions = resolveDefinition(currentIndex, {
                uri: document.uri.toString(),
                text: document.getText(),
                line: position.line,
                character: position.character,
            });

            return definitions.map(definition => new vscode.Location(
                vscode.Uri.parse(definition.uri),
                new vscode.Range(
                    new vscode.Position(definition.range.start.line, definition.range.start.character),
                    new vscode.Position(definition.range.end.line, definition.range.end.character),
                ),
            ));
        },
    };
}

function activate(context) {
    const provider = {
        provideCompletionItems(document, position) {
            const linePrefix = currentLinePrefix(document, position);
            const importItems = moduleCompletionsForImport(document, position, linePrefix);

            if (importItems) {
                return importItems;
            }

            const access = qualifiedAccess(linePrefix);

            if (access) {
                const replacementRange = wordRangeFrom(document, position, position.character - access.partial.length);

                return qualifiedCompletions(access.prefix).map((entry, index) => createCompletionItem(entry, {
                    range: replacementRange,
                    sortText: `${index.toString().padStart(4, "0")}:${entry.label}`,
                }));
            }

            return rootCompletions(document);
        },
    };

    context.subscriptions.push(vscode.languages.registerCompletionItemProvider(
        { language: "stark" },
        provider,
        ".",
    ));

    context.subscriptions.push(vscode.languages.registerDefinitionProvider(
        { language: "stark" },
        createStarkDefinitionProvider(context),
    ));
}

function deactivate() {
}

module.exports = {
    activate,
    deactivate,
};
