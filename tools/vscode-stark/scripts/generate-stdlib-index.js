const fs = require("fs");
const path = require("path");

const extensionRoot = path.resolve(__dirname, "..");
const repoRoot = path.resolve(extensionRoot, "..", "..");
const stdlibRoot = path.join(repoRoot, "stdlib", "src");
const outputPath = path.join(extensionRoot, "data", "stdlib-symbols.json");

const FUNCTION_KINDS = new Set(["fn", "law", "finite", "finite law"]);
const TYPE_KINDS = new Map([
  ["struct", "Struct"],
  ["record", "Record"],
  ["enum", "Enum"],
  ["trait", "Trait"],
  ["doctrine", "Doctrine"]
]);

function main() {
  const files = listFiles(stdlibRoot)
    .filter((file) => file.endsWith(".stark"))
    .sort((left, right) => left.localeCompare(right));

  const modules = [];
  const symbols = [];

  for (const file of files) {
    const parsed = parseModule(file);

    if (!parsed.module) {
      continue;
    }

    modules.push({
      name: parsed.module,
      exports: parsed.exports
    });
    symbols.push(...parsed.symbols);
  }

  modules.sort((left, right) => left.name.localeCompare(right.name));
  symbols.sort((left, right) =>
    left.module.localeCompare(right.module) ||
    (left.container ?? "").localeCompare(right.container ?? "") ||
    left.name.localeCompare(right.name) ||
    left.detail.localeCompare(right.detail));

  const data = {
    schemaVersion: 1,
    source: "stdlib/src",
    modules,
    symbols
  };

  fs.mkdirSync(path.dirname(outputPath), { recursive: true });
  fs.writeFileSync(outputPath, `${JSON.stringify(data, null, 2)}\n`);
  console.log(`Generated ${symbols.length} symbols from ${modules.length} modules.`);
}

function parseModule(file) {
  const source = fs.readFileSync(file, "utf8");
  const lines = source.split(/\r?\n/);
  const relative = toRepoPath(file);
  const module = findModule(lines);
  const exports = findExportImports(lines);
  const symbols = [];
  const containers = [];
  let braceDepth = 0;

  for (let index = 0; index < lines.length; index += 1) {
    const rawLine = lines[index];
    const cleanLine = stripLine(rawLine);
    const trimmed = cleanLine.trim();

    while (containers.length > 0 && braceDepth < containers[containers.length - 1].bodyDepth) {
      containers.pop();
    }

    if (!trimmed || trimmed.startsWith("[") || trimmed.startsWith("module ") || trimmed.startsWith("import ") || trimmed.startsWith("export import ")) {
      braceDepth += countBraces(cleanLine);
      continue;
    }

    const container = containers[containers.length - 1];

    if (container && container.kind === "Enum") {
      const enumCase = parseEnumCase(trimmed);
      if (enumCase) {
        symbols.push({
          name: enumCase,
          kind: "EnumMember",
          module,
          container: container.name,
          detail: `${container.name}.${enumCase}`,
          source: `${relative}:${index + 1}`
        });
      }

      braceDepth += countBraces(cleanLine);
      continue;
    }

    if (!looksLikeDeclaration(trimmed, container)) {
      braceDepth += countBraces(cleanLine);
      continue;
    }

    const declaration = collectDeclaration(lines, index);
    const parsedType = parseTypeDeclaration(declaration.text);

    if (parsedType) {
      if (parsedType.visibility === "public" || parsedType.visibility === "export") {
        symbols.push(createSymbol(parsedType, module, relative, index + 1));
      }

      const opensBody = declaration.text.includes("{");
      if (opensBody && (parsedType.visibility === "public" || parsedType.visibility === "export")) {
        containers.push({
          name: parsedType.displayName,
          kind: parsedType.kind,
          bodyDepth: braceDepth + countBracesBeforeBody(cleanLine)
        });
      }
    } else {
      const parsedCallable = parseCallableDeclaration(declaration.text, container);
      if (parsedCallable && isVisibleCallable(parsedCallable, container)) {
        symbols.push(createSymbol(parsedCallable, module, relative, index + 1));
      } else {
        const parsedAlias = parseAliasDeclaration(declaration.text);
        if (parsedAlias) {
          symbols.push(createSymbol(parsedAlias, module, relative, index + 1));
        } else {
          const parsedStorage = parseStorageDeclaration(declaration.text);
          if (parsedStorage) {
            symbols.push(createSymbol(parsedStorage, module, relative, index + 1));
          }
        }
      }
    }

    if (declaration.endIndex > index) {
      for (let consumed = index; consumed <= declaration.endIndex; consumed += 1) {
        braceDepth += countBraces(stripLine(lines[consumed]));
      }
      index = declaration.endIndex;
    } else {
      braceDepth += countBraces(cleanLine);
    }
  }

  return {
    module,
    exports,
    symbols
  };
}

function looksLikeDeclaration(trimmed, container) {
  const topLevelPrefix = "(?:public|export|internal)";
  const modifiers = "(?:(?:inline|noinline|inlinehint|hot|cold|ffi|varargs|unsafe|strictfp|static)\\s+)*";
  const functionKind = "(?:(?:finite\\s+law)|finite|law|fn)";
  const typeKind = "(?:struct|record|enum|trait|doctrine)";

  if (new RegExp(`^${topLevelPrefix}\\s+${typeKind}\\b`).test(trimmed)) {
    return true;
  }

  if (new RegExp(`^${topLevelPrefix}\\s+(?:alias|const|var)\\b`).test(trimmed)) {
    return true;
  }

  if (new RegExp(`^${topLevelPrefix}\\s+${modifiers}(?:asm\\s*\\([^)]*\\)\\s+)?${functionKind}\\b`).test(trimmed)) {
    return true;
  }

  return Boolean(container) && new RegExp(`^${modifiers}${functionKind}\\b`).test(trimmed);
}

function findModule(lines) {
  for (const line of lines) {
    const match = stripLine(line).match(/^\s*module\s+([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)*)\s*$/);
    if (match) {
      return match[1];
    }
  }

  return null;
}

function findExportImports(lines) {
  const exports = [];

  for (const line of lines) {
    const match = stripLine(line).match(/^\s*export\s+import\s+([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)*)\s*$/);
    if (match) {
      exports.push(match[1]);
    }
  }

  return exports.sort();
}

function parseTypeDeclaration(text) {
  const match = text.match(/^(?:(public|export|internal)\s+)?(struct|record|enum|trait|doctrine)\s+([A-Za-z][A-Za-z0-9_]*(?:<[^>{}()]*>)?)/);
  if (!match) {
    return null;
  }

  return {
    name: stripTypeParameters(match[3]),
    displayName: match[3],
    kind: TYPE_KINDS.get(match[2]),
    visibility: match[1] ?? "module",
    detail: normalizeSignature(text)
  };
}

function parseCallableDeclaration(text, container) {
  const signature = normalizeSignature(text);
  const match = signature.match(/^(?:(public|export|internal)\s+)?(?:(?:inline|noinline|inlinehint|hot|cold|ffi|varargs|unsafe|strictfp|static)\s+)*(?:(asm\s*\([^)]*\)\s+)?)((?:finite\s+law)|finite|law|fn)\s+(.+?)\s*(?:where\s+.+)?$/);

  if (!match) {
    return null;
  }

  const openParen = signature.indexOf("(");
  if (openParen < 0) {
    return null;
  }

  const beforeParen = signature.slice(0, openParen).trim();
  const nameMatch = beforeParen.match(/([A-Za-z][A-Za-z0-9_]*)\s*(?:<[^<>]*>)?$/);
  if (!nameMatch) {
    return null;
  }

  const functionKind = match[3];
  return {
    name: nameMatch[1],
    kind: container ? "Method" : functionKind.includes("law") ? "Law" : "Function",
    visibility: match[1] ?? (container ? "public" : "module"),
    container: container?.name,
    detail: signature
  };
}

function parseAliasDeclaration(text) {
  const signature = normalizeSignature(text);
  const match = signature.match(/^(public|export)\s+alias\s+([A-Za-z][A-Za-z0-9_]*(?:<[^>{}()]*>)?)\s*=/);
  if (!match) {
    return null;
  }

  return {
    name: stripTypeParameters(match[2]),
    kind: "TypeAlias",
    visibility: match[1],
    detail: signature
  };
}

function parseStorageDeclaration(text) {
  const signature = normalizeSignature(text);
  const match = signature.match(/^(public|export)\s+(const|var)\s+(?:.+\s+)?([A-Za-z][A-Za-z0-9_]*)\s*(?:=|$)/);
  if (!match) {
    return null;
  }

  return {
    name: match[3],
    kind: match[2] === "const" ? "Constant" : "Variable",
    visibility: match[1],
    detail: signature
  };
}

function parseEnumCase(trimmed) {
  if (/^(case|default|if|else|for|while|switch|return|break|continue)\b/.test(trimmed)) {
    return null;
  }

  const match = trimmed.match(/^([A-Za-z][A-Za-z0-9_]*)(?:\s*\([^)]*\))?\s*,?$/);
  return match ? match[1] : null;
}

function isVisibleCallable(callable, container) {
  if (callable.visibility === "public" || callable.visibility === "export") {
    return true;
  }

  return Boolean(container);
}

function createSymbol(parsed, module, relative, line) {
  const symbol = {
    name: parsed.name,
    kind: parsed.kind,
    module,
    detail: parsed.detail,
    source: `${relative}:${line}`
  };

  if (parsed.container) {
    symbol.container = parsed.container;
  }

  return symbol;
}

function collectDeclaration(lines, startIndex) {
  let text = "";
  let parenDepth = 0;
  let angleDepth = 0;

  for (let index = startIndex; index < lines.length; index += 1) {
    const line = stripLine(lines[index]).trim();
    if (!line || line.startsWith("[")) {
      continue;
    }

    text = `${text} ${line}`.trim();

    for (const ch of line) {
      if (ch === "(") {
        parenDepth += 1;
      } else if (ch === ")") {
        parenDepth -= 1;
      } else if (ch === "<") {
        angleDepth += 1;
      } else if (ch === ">") {
        angleDepth -= 1;
      }
    }

    if ((line.includes("{") || line.includes(";")) && parenDepth <= 0 && angleDepth <= 0) {
      return { text, endIndex: index };
    }
  }

  return { text, endIndex: startIndex };
}

function stripTypeParameters(name) {
  return name.replace(/<.*$/, "");
}

function normalizeSignature(text) {
  return text
    .replace(/\s+/g, " ")
    .replace(/\s+\{/g, "")
    .replace(/\s*;\s*$/, "")
    .trim();
}

function stripLine(line) {
  let result = "";
  let inString = false;
  let inChar = false;
  let escaped = false;

  for (let index = 0; index < line.length; index += 1) {
    const ch = line[index];
    const next = line[index + 1];

    if (!inString && !inChar && ch === "/" && next === "/") {
      break;
    }

    if (!inString && !inChar && ch === "/" && next === "*") {
      result += "  ";
      index += 1;
      while (index + 1 < line.length && !(line[index] === "*" && line[index + 1] === "/")) {
        result += " ";
        index += 1;
      }
      result += "  ";
      index += 1;
      continue;
    }

    if (escaped) {
      result += " ";
      escaped = false;
      continue;
    }

    if (ch === "\\") {
      result += " ";
      escaped = inString || inChar;
      continue;
    }

    if (!inChar && ch === "\"") {
      inString = !inString;
      result += " ";
      continue;
    }

    if (!inString && ch === "'") {
      inChar = !inChar;
      result += " ";
      continue;
    }

    result += inString || inChar ? " " : ch;
  }

  return result;
}

function countBraces(line) {
  let count = 0;

  for (const ch of line) {
    if (ch === "{") {
      count += 1;
    } else if (ch === "}") {
      count -= 1;
    }
  }

  return count;
}

function countBracesBeforeBody(line) {
  const bodyIndex = line.indexOf("{");
  if (bodyIndex < 0) {
    return 1;
  }

  return countBraces(line.slice(0, bodyIndex + 1));
}

function listFiles(root) {
  const result = [];

  for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
    const fullPath = path.join(root, entry.name);
    if (entry.isDirectory()) {
      result.push(...listFiles(fullPath));
    } else if (entry.isFile()) {
      result.push(fullPath);
    }
  }

  return result;
}

function toRepoPath(file) {
  return path.relative(repoRoot, file).replace(/\\/g, "/");
}

main();
