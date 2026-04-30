const fs = require("fs");
const path = require("path");
const vscode = require("vscode");

const SYMBOL_DATA_PATH = path.join(__dirname, "data", "stdlib-symbols.json");

function activate(context) {
  const stdlib = loadStdlibIndex();
  const completionProvider = new StarkCompletionProvider(stdlib);

  context.subscriptions.push(
    vscode.languages.registerCompletionItemProvider(
      { language: "stark" },
      completionProvider,
      "."
    )
  );
}

function deactivate() {}

class StarkCompletionProvider {
  constructor(stdlib) {
    this.stdlib = stdlib;
    this.modules = stdlib.modules ?? [];
    this.symbols = stdlib.symbols ?? [];
    this.symbolsByModule = new Map();

    for (const symbol of this.symbols) {
      if (!this.symbolsByModule.has(symbol.module)) {
        this.symbolsByModule.set(symbol.module, []);
      }

      this.symbolsByModule.get(symbol.module).push(symbol);
    }
  }

  provideCompletionItems(document, position) {
    const text = document.lineAt(position).text.slice(0, position.character);
    const importMatch = text.match(/\b(?:export\s+)?import\s+([A-Za-z_][A-Za-z0-9_.]*)?$/);

    if (importMatch) {
      return this.moduleItems(importMatch[1] ?? "");
    }

    const qualifiedMatch = text.match(/([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\.$/);

    if (qualifiedMatch) {
      return [
        ...this.moduleChildren(qualifiedMatch[1]),
        ...this.moduleSymbolItems(qualifiedMatch[1])
      ];
    }

    return [
      ...this.moduleItems(""),
      ...this.globalSymbolItems()
    ];
  }

  moduleItems(prefix) {
    const normalizedPrefix = prefix.trim();
    return this.modules
      .filter((module) => !normalizedPrefix || module.name.startsWith(normalizedPrefix))
      .map((module) => {
        const item = new vscode.CompletionItem(module.name, vscode.CompletionItemKind.Module);
        item.detail = "Stark module";
        item.insertText = insertionTail(module.name, normalizedPrefix);
        item.sortText = `0:${module.name}`;
        return item;
      });
  }

  moduleChildren(moduleName) {
    const prefix = `${moduleName}.`;
    const childNames = new Set();

    for (const module of this.modules) {
      if (!module.name.startsWith(prefix)) {
        continue;
      }

      const remainder = module.name.slice(prefix.length);
      const child = remainder.split(".")[0];
      if (child) {
        childNames.add(child);
      }
    }

    return [...childNames].sort().map((name) => {
      const item = new vscode.CompletionItem(name, vscode.CompletionItemKind.Module);
      item.detail = `${moduleName}.${name}`;
      item.sortText = `0:${name}`;
      return item;
    });
  }

  moduleSymbolItems(moduleName) {
    return (this.symbolsByModule.get(moduleName) ?? [])
      .map((symbol) => this.symbolItem(symbol, `1:${symbol.name}`));
  }

  globalSymbolItems() {
    return this.symbols.map((symbol) => this.symbolItem(symbol, `2:${symbol.name}:${symbol.module}`));
  }

  symbolItem(symbol, sortText) {
    const item = new vscode.CompletionItem(symbol.name, completionKind(symbol.kind));
    item.detail = symbol.detail || `${symbol.kind} in ${symbol.module}`;
    item.documentation = symbolDocumentation(symbol);
    item.sortText = sortText;

    if (symbol.kind === "Function" || symbol.kind === "Law" || symbol.kind === "Method") {
      item.commitCharacters = ["("];
    }

    return item;
  }
}

function loadStdlibIndex() {
  try {
    return JSON.parse(fs.readFileSync(SYMBOL_DATA_PATH, "utf8"));
  } catch {
    return { modules: [], symbols: [] };
  }
}

function completionKind(kind) {
  switch (kind) {
    case "Constant":
      return vscode.CompletionItemKind.Constant;
    case "Doctrine":
    case "Trait":
      return vscode.CompletionItemKind.Interface;
    case "Enum":
      return vscode.CompletionItemKind.Enum;
    case "EnumMember":
      return vscode.CompletionItemKind.EnumMember;
    case "Function":
    case "Law":
      return vscode.CompletionItemKind.Function;
    case "Method":
      return vscode.CompletionItemKind.Method;
    case "Record":
    case "Struct":
      return vscode.CompletionItemKind.Struct;
    case "TypeAlias":
      return vscode.CompletionItemKind.TypeParameter;
    case "Variable":
      return vscode.CompletionItemKind.Variable;
    default:
      return vscode.CompletionItemKind.Value;
  }
}

function insertionTail(moduleName, prefix) {
  if (!prefix) {
    return moduleName;
  }

  if (moduleName === prefix) {
    const segments = moduleName.split(".");
    return segments[segments.length - 1];
  }

  if (moduleName.startsWith(`${prefix}.`)) {
    return moduleName.slice(prefix.length + 1);
  }

  const prefixSegments = prefix.split(".");
  const moduleSegments = moduleName.split(".");
  const typed = prefixSegments[prefixSegments.length - 1] ?? "";
  const last = moduleSegments[moduleSegments.length - 1] ?? moduleName;
  return last.startsWith(typed) ? last : moduleName;
}

function symbolDocumentation(symbol) {
  const lines = [];
  lines.push(`\`${symbol.detail || symbol.name}\``);
  lines.push("");
  lines.push(`Module: \`${symbol.module}\``);

  if (symbol.container) {
    lines.push(`Container: \`${symbol.container}\``);
  }

  if (symbol.source) {
    lines.push(`Source: \`${symbol.source}\``);
  }

  return new vscode.MarkdownString(lines.join("\n"));
}

module.exports = {
  activate,
  deactivate
};
