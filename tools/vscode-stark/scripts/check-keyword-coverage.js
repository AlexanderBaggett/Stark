const fs = require("fs");
const path = require("path");

const extensionRoot = path.resolve(__dirname, "..");
const repoRoot = path.resolve(extensionRoot, "..", "..");
const grammarPath = path.join(repoRoot, "Stark.g4");
const syntaxPath = path.join(extensionRoot, "syntaxes", "stark.tmLanguage.json");

const ignoredTokenRules = new Set([
  "INVALID_FLOAT_TYPE",
  "INVALID_INTEGER_TYPE",
  "WEIGHT_LITERAL"
]);

function main() {
  const grammar = fs.readFileSync(grammarPath, "utf8");
  const syntax = JSON.parse(fs.readFileSync(syntaxPath, "utf8"));
  const patternSources = collectPatternSources(syntax);
  const grammarKeywords = collectGrammarKeywords(grammar);

  const missing = grammarKeywords.filter((keyword) =>
    !patternSources.some((source) => regexSourceContainsLiteral(source, keyword)));

  if (missing.length > 0) {
    console.error("Stark TextMate grammar is missing grammar keyword coverage:");
    for (const keyword of missing) {
      console.error(`  - ${keyword}`);
    }
    process.exitCode = 1;
    return;
  }

  console.log(`Verified ${grammarKeywords.length} Stark grammar keyword literals in TextMate grammar.`);
}

function collectGrammarKeywords(grammar) {
  const keywords = new Set();
  const tokenRule = /^([A-Z][A-Z_]*)\s*(?::|\r?\n\s*:)([\s\S]*?);/gm;
  let match;

  while ((match = tokenRule.exec(grammar)) !== null) {
    const tokenName = match[1];
    const tokenBody = match[2];
    if (ignoredTokenRules.has(tokenName)) {
      continue;
    }

    for (const literalMatch of tokenBody.matchAll(/'([^']+)'/g)) {
      const literal = literalMatch[1];
      if (/^[A-Za-z][A-Za-z0-9-]*$/.test(literal)) {
        keywords.add(literal);
      }
    }
  }

  return [...keywords].sort((left, right) => left.localeCompare(right));
}

function collectPatternSources(value, result = []) {
  if (Array.isArray(value)) {
    for (const item of value) {
      collectPatternSources(item, result);
    }
    return result;
  }

  if (!value || typeof value !== "object") {
    return result;
  }

  for (const key of ["match", "begin", "end"]) {
    if (typeof value[key] === "string") {
      result.push(value[key]);
    }
  }

  for (const child of Object.values(value)) {
    collectPatternSources(child, result);
  }

  return result;
}

function regexSourceContainsLiteral(source, literal) {
  const normalizedSource = source.replace(/\\b/g, " ");
  const escapedLiteral = escapeRegExp(literal);
  const literalBoundary = new RegExp(`(?<![A-Za-z0-9_-])${escapedLiteral}(?![A-Za-z0-9_-])`);
  return literalBoundary.test(normalizedSource);
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

main();
