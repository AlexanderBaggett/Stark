#!/usr/bin/env node
"use strict";

const assert = require("assert");
const {
    createDefinitionIndex,
    extractDefinitionTarget,
    resolveDefinition,
} = require("../definition-index");

const documents = [
    {
        uri: "file:///workspace/stdlib/src/System/Console.stark",
        text: `module System.Console

public fn System.Text.OwnedUnicode ReadLine()
{
}
`,
    },
    {
        uri: "file:///workspace/stdlib/src/System/IO.stark",
        text: `module System.IO

public enum IOStatus
{
    Ok,
    Err
}

public enum IOResult<T>
{
    Ok(T Value),
    Err(IOStatus Status)
}
`,
    },
    {
        uri: "file:///workspace/stdlib/src/System/IO/File.stark",
        text: `module System.IO.File

public struct File
{
    File()
    {
    }
}
`,
    },
    {
        uri: "file:///workspace/stdlib/src/System/Memory.stark",
        text: `module System.Memory

public enum MemoryResult<T>
{
    Ok(T Value),
    Err
}
`,
    },
    {
        uri: "file:///workspace/stdlib/src/System/Text.stark",
        text: `module System.Text

public struct OwnedUnicode
{
}
`,
    },
    {
        uri: "file:///workspace/src/One.stark",
        text: `module Ambiguous.One

public fn void Duplicate()
{
}
`,
    },
    {
        uri: "file:///workspace/src/Two.stark",
        text: `module Ambiguous.Two

public fn void Duplicate()
{
}
`,
    },
    {
        uri: "file:///workspace/src/Fields.stark",
        text: `module Example.Fields

public struct Container
{
    i32[0 max] Value;

    fn void Touch(mut borrow Container self)
    {
        stack i32[0 max] localValue = 0;
        localValue = localValue + 1;
    }
}
`,
    },
];

const consumer = {
    uri: "file:///workspace/benchmarks/ConsoleReadSurface.stark",
    text: `import System.Console
import System.IO
module Benchmarks.Console.ConsoleReadSurface

fn System.Memory.MemoryResult<System.Text.OwnedUnicode> ReadUnicodeLineExperimental()
{
    return System.Console.ReadLine();
}

fn System.Text.OwnedUnicode ReadUnqualified()
{
    return ReadLine();
}

fn bool IOOk(System.IO.IOStatus status)
{
    switch (status)
    {
        case System.IO.IOStatus.Ok:
            return true;
        case System.IO.IOResult<System.IO.File.File>.Err(var openError):
            return false;
    }
}

fn bool MultiLineParams(
    System.IO.IOStatus multiStatus,
    Example.Fields.Container multiContainer)
{
    multiContainer.Value = 2;
    switch (multiStatus)
    {
        case System.IO.IOStatus.Ok:
            return true;
        default:
            return false;
    }
}

fn i32[0 max] ShortParameterName(
    i32[0 max] i)
{
    return i;
}

fn System.IO.IOStatus ExtractResultStatus(System.IO.IOResult<System.IO.File.File> result)
{
    switch (result)
    {
        case System.IO.IOResult<System.IO.File.File>.Ok(var fileValue):
            fileValue.Value = fileValue.Value;
            return System.IO.IOStatus.Ok;
        case System.IO.IOResult<System.IO.File.File>.Err(var openError):
            return openError;
    }
}

fn bool GuardedPattern(System.IO.IOStatus status)
{
    switch (status)
    {
        case var whole when whole == System.IO.IOStatus.Ok:
            return true;
        default:
            return false;
    }
}

fn void AmbiguousCall()
{
    Duplicate();
}

fn void FieldAndMissing()
{
    stack Example.Fields.Container container = new Example.Fields.Container();
    container.Value = 1;
    DoesNotExist();
}
`,
};

const index = createDefinitionIndex([...documents, consumer]);

assertResolves("imported module", "System.Console", "System.Console");
assertResolves("qualified type", "MemoryResult", "System.Memory.MemoryResult");
assertResolves("generic type argument", "OwnedUnicode", "System.Text.OwnedUnicode");
assertResolves("qualified function call", "System.Console.ReadLine", "System.Console.ReadLine");
assertResolves("unqualified imported function", "return ReadLine();", "System.Console.ReadLine", "ReadLine");
assertResolves("qualified enum member", "IOStatus.Ok", "System.IO.IOStatus.Ok");
assertResolves("generic qualified enum member", "IOResult<System.IO.File.File>.Err", "System.IO.IOResult.Err");
assertResolves("direct field", "Value", "Example.Fields.Container.Value");
assertLocalResolves("parameter", "switch (status)", "status", "parameter", "status");
assertLocalResolves("multi-line parameter", "switch (multiStatus)", "multiStatus", "parameter", "multiStatus");
assertLocalResolves("multi-line parameter member base", "multiContainer.Value = 2;", "multiContainer", "parameter", "multiContainer");
assertLocalResolves("short multi-line parameter", "return i;", "i", "parameter", "i");
assertLocalResolves("enum case pattern variable", "return openError;", "openError", "pattern", "openError");
assertLocalResolves("enum case pattern member base", "fileValue.Value = fileValue.Value;", "fileValue", "pattern", "fileValue");
assertLocalResolves("case var guard pattern", "whole == System.IO.IOStatus.Ok", "whole", "pattern", "whole");
assertLocalResolves("local variable", "container.Value = 1;", "container", "local", "container");
assertLocalResolvesInDocument(
    "method local variable",
    documents.find(document => document.uri.endsWith("/Fields.stark")),
    "localValue = localValue + 1;",
    "localValue",
    "local",
    "localValue",
);

const ambiguous = resolveAt("Duplicate();");
assert.deepStrictEqual(
    ambiguous.map(declaration => declaration.qualifiedName),
    ["Ambiguous.One.Duplicate", "Ambiguous.Two.Duplicate"],
);

assert.deepStrictEqual(resolveAt("DoesNotExist();"), []);
assert.deepStrictEqual(index.byName.get("localValue"), undefined);
assert.deepStrictEqual(index.byName.get("openError"), undefined);

const target = extractAt("IOResult<System.IO.File.File>.Err");
assert.strictEqual(target.normalizedText, "System.IO.IOResult.Err");

const importTarget = extractAt("System.Console");
assert.deepStrictEqual(importTarget.range.start, { line: 0, character: 7 });
assert.deepStrictEqual(importTarget.range.end, { line: 0, character: 21 });

const shortParameter = index.documents.get(consumer.uri).localDeclarations.find(declaration =>
    declaration.kind === "parameter"
    && declaration.name === "i"
    && consumer.text.split(/\r?\n/)[declaration.line].includes("i32[0 max] i)"));
assert(shortParameter, "Expected short parameter to be indexed");
assert.strictEqual(
    shortParameter.character,
    consumer.text.split(/\r?\n/)[shortParameter.line].lastIndexOf("i"),
    "Short parameter range should point to the parameter name, not the type",
);

console.log("Definition index tests passed.");

function assertResolves(label, needle, expectedQualifiedName, targetNeedle = needle) {
    const matches = resolveAt(needle, targetNeedle);
    assert(
        matches.some(declaration => declaration.qualifiedName === expectedQualifiedName),
        `${label} did not resolve to ${expectedQualifiedName}; saw ${matches.map(declaration => declaration.qualifiedName).join(", ")}`,
    );
}

function assertLocalResolves(label, needle, targetNeedle, expectedKind, expectedName) {
    assertLocalResolvesInDocument(label, consumer, needle, targetNeedle, expectedKind, expectedName);
}

function assertLocalResolvesInDocument(label, document, needle, targetNeedle, expectedKind, expectedName) {
    const position = positionOf(document.text, needle, targetNeedle);
    const matches = resolveDefinition(index, {
        uri: document.uri,
        text: document.text,
        line: position.line,
        character: position.character,
    });
    assert.strictEqual(matches.length, 1, `${label} expected one local match; saw ${matches.map(declaration => declaration.qualifiedName).join(", ")}`);
    assert.strictEqual(matches[0].kind, expectedKind, `${label} resolved wrong kind`);
    assert.strictEqual(matches[0].name, expectedName, `${label} resolved wrong name`);
}

function resolveAt(needle, targetNeedle = needle) {
    const position = positionOf(consumer.text, needle, targetNeedle);
    return resolveDefinition(index, {
        uri: consumer.uri,
        text: consumer.text,
        line: position.line,
        character: position.character,
    });
}

function extractAt(needle) {
    const position = positionOf(consumer.text, needle);
    return extractDefinitionTarget(consumer.text, position.line, position.character);
}

function positionOf(text, needle, targetNeedle = needle) {
    const offset = text.indexOf(needle);
    assert.notStrictEqual(offset, -1, `Could not find ${needle}`);
    const relativeTargetOffset = needle.indexOf(targetNeedle);
    assert.notStrictEqual(relativeTargetOffset, -1, `Could not find ${targetNeedle} inside ${needle}`);
    const targetOffset = offset + relativeTargetOffset + Math.max(0, targetNeedle.lastIndexOf(".") + 1);
    const prefix = text.slice(0, targetOffset);
    const lines = prefix.split(/\r?\n/);

    return {
        line: lines.length - 1,
        character: lines[lines.length - 1].length,
    };
}
