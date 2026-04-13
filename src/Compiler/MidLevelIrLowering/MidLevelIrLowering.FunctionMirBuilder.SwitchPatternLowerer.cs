using System.Numerics;
using Stark.Parsing;

namespace Stark.Compiler;

internal sealed partial class MidLevelIrLowerer
{
    private sealed partial class FunctionMirBuilder
    {
        private void LowerSwitch(StarkParser.SwitchStatementContext switchStatement)
        {
            _switchPatternLowerer.LowerSwitch(switchStatement);
        }

        private bool TryRegisterSwitchCaptureLocals(
            IEnumerable<IReadOnlyList<LowerableSwitchLabel>> sectionLabels,
            StarkTypeSymbol switchType)
        {
            return _switchPatternLowerer.TryRegisterSwitchCaptureLocals(sectionLabels, switchType);
        }

        private bool EmitSwitchSectionDecision(
            IReadOnlyList<LowerableSwitchLabel> labels,
            MidLevelIrOperand switchValue,
            int targetBlockId,
            int nextSectionTarget,
            string switchText,
            int sectionIndex)
        {
            return _switchPatternLowerer.EmitSwitchSectionDecision(
                labels,
                switchValue,
                targetBlockId,
                nextSectionTarget,
                switchText,
                sectionIndex);
        }

        private void LowerSwitchCore(StarkParser.SwitchStatementContext switchStatement)
        {
            var switchValue = LowerExpressionToOperand(switchStatement.expression());
            if (switchValue is null)
            {
                MarkUnsupported(switchStatement, "Switch expression could not be lowered.");
                return;
            }

            var lowered = switchValue.Type.Kind switch
            {
                StarkTypeKind.Integer or StarkTypeKind.Bool =>
                    TryLowerNativeSwitch(switchStatement, switchValue)
                    || TryLowerGuardedSwitch(switchStatement, switchValue),
                StarkTypeKind.Ascii or StarkTypeKind.Unicode =>
                    TryLowerPartitionedTextSwitch(switchStatement, switchValue)
                    || TryLowerGuardedSwitch(switchStatement, switchValue),
                _ => TryLowerGuardedSwitch(switchStatement, switchValue)
            };

            if (lowered)
            {
                return;
            }

            MarkUnsupported(switchStatement, "Switch shape is outside the current direct MIR lowering subset.");

            var exitBlock = CreateBlock("switch_exit");
            var sectionBlocks = switchStatement.switchSection()
                .Select((section, index) => (Section: section, Block: CreateBlock($"switch_case_{index}")))
                .ToArray();

            var cases = new List<MidLevelIrSwitchCase>();
            foreach (var (section, block) in sectionBlocks)
            {
                foreach (var label in section.switchLabel())
                {
                    var labelText = label.DEFAULT() is not null ? "default" : label.GetText();
                    cases.Add(new MidLevelIrSwitchCase(labelText, block.Id));
                }
            }

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Switch,
                sectionBlocks.Select(static item => item.Block.Id).Append(exitBlock.Id).ToArray(),
                ConditionText: switchStatement.expression().GetText(),
                SwitchCases: cases);

            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            try
            {
                foreach (var (section, block) in sectionBlocks)
                {
                    CurrentBlock = block;
                    foreach (var nested in section.statement())
                    {
                        LowerStatement(nested);
                    }

                    EnsureGoto(exitBlock.Id);
                }
            }
            finally
            {
                _breakTargets.Pop();
            }

            CurrentBlock = exitBlock;
        }

        private bool TryLowerNativeSwitch(
            StarkParser.SwitchStatementContext switchStatement,
            MidLevelIrOperand switchValue)
        {
            if (!TryParseLowerableSwitchSections(switchStatement, out var parsedSections, out var defaultSectionCount))
            {
                return false;
            }

            if (!CanUseNativeSwitchType(switchValue.Type) || defaultSectionCount > 1)
            {
                return false;
            }

            var allLabels = parsedSections
                .SelectMany(static section => section.Labels)
                .ToArray();

            if (allLabels.Any(static label => label.IsMatchAll && !label.IsDefault))
            {
                return false;
            }

            var nativeLabels = allLabels
                .Where(static label => !label.IsMatchAll)
                .ToArray();

            if (nativeLabels.Length == 0
                || nativeLabels.Any(static label => label.GuardExpression is not null || label.Literal is null || label.CaptureName is not null))
            {
                return false;
            }

            var sections = parsedSections
                .Select((section, index) => (section.Section, section.Labels, Block: CreateBlock($"switch_case_{index}")))
                .ToArray();
            var exitBlock = CreateBlock("switch_exit");
            var switchCases = new List<MidLevelIrSwitchCase>();
            int? defaultTarget = null;

            foreach (var section in sections)
            {
                foreach (var label in section.Labels)
                {
                    if (label.IsDefault)
                    {
                        defaultTarget ??= section.Block.Id;
                        continue;
                    }

                    if (label.GuardExpression is not null || label.Literal is null)
                    {
                        return false;
                    }

                    var matchValue = LowerSwitchCaseLiteral(label.Literal, switchValue.Type);
                    if (matchValue is null || !CanUseNativeSwitchCase(matchValue.Type, switchValue.Type))
                    {
                        return false;
                    }

                    switchCases.Add(new MidLevelIrSwitchCase(label.LabelText, section.Block.Id, matchValue));
                }
            }

            var resolvedDefaultTarget = defaultTarget ?? exitBlock.Id;
            if (switchCases.Count == 0)
            {
                return false;
            }

            var targets = switchCases
                .Select(static item => item.TargetBlockId)
                .Append(resolvedDefaultTarget)
                .Distinct()
                .ToArray();

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Switch,
                targets,
                ConditionText: switchStatement.expression().GetText(),
                Condition: switchValue,
                SwitchCases: switchCases,
                DefaultTarget: resolvedDefaultTarget);

            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            try
            {
                foreach (var section in sections)
                {
                    CurrentBlock = section.Block;
                    foreach (var nested in section.Section.statement())
                    {
                        LowerStatement(nested);
                    }

                    EnsureGoto(exitBlock.Id);
                }
            }
            finally
            {
                _breakTargets.Pop();
            }

            CurrentBlock = exitBlock;
            return true;
        }

        private bool TryLowerPartitionedTextSwitch(
            StarkParser.SwitchStatementContext switchStatement,
            MidLevelIrOperand switchValue)
        {
            if (!TryParseLowerableSwitchSections(switchStatement, out var parsedSections, out var defaultSectionCount))
            {
                return false;
            }

            if (!CanUsePartitionedTextSwitchType(switchValue.Type)
                || defaultSectionCount > 1)
            {
                return false;
            }

            var allLabels = parsedSections
                .SelectMany(static section => section.Labels)
                .ToArray();
            if (allLabels.Any(static label => label.IsMatchAll && !label.IsDefault))
            {
                return false;
            }

            var textLabels = allLabels
                .Where(static label => !label.IsDefault)
                .ToArray();
            if (textLabels.Length == 0
                || textLabels.Any(static label => label.GuardExpression is not null || label.CaptureName is not null || label.Literal is null))
            {
                return false;
            }

            var sections = parsedSections
                .Select((section, index) => (section.Section, section.Labels, Block: CreateBlock($"switch_case_{index}")))
                .ToArray();
            var exitBlock = CreateBlock("switch_exit");
            var defaultTarget = sections
                .Where(static section => section.Labels.Any(static label => label.IsDefault))
                .Select(static section => section.Block.Id)
                .FirstOrDefault(exitBlock.Id);

            if (!TryExtractTextSwitchComponents(switchValue, out var dataPointer, out var length))
            {
                return false;
            }

            var flattenedLabels = new List<PartitionedTextSwitchLabel>();
            var order = 0;
            foreach (var section in sections)
            {
                foreach (var label in section.Labels)
                {
                    if (label.IsDefault || label.Literal is null)
                    {
                        continue;
                    }

                    flattenedLabels.Add(new PartitionedTextSwitchLabel(
                        label,
                        section.Block.Id,
                        DecodeTextLiteralUnits(label.Literal.GetText(), switchValue.Type),
                        order++));
                }
            }

            if (flattenedLabels.Count == 0)
            {
                return false;
            }

            var lengthType = StarkTypeSymbols.Integer(64);
            var lengthGroups = flattenedLabels
                .GroupBy(static label => label.Units.Length)
                .OrderBy(static group => group.Key)
                .Select(group => (
                    Length: group.Key,
                    Labels: group.OrderBy(static label => label.Order).ToArray(),
                    Block: CreateBlock($"switch_len_{group.Key}")))
                .ToArray();

            var switchCases = lengthGroups
                .Select(group => new MidLevelIrSwitchCase(
                    group.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    group.Block.Id,
                    new MidLevelIrIntegerConstantOperand(new BigInteger(group.Length), lengthType)))
                .ToList();
            var targets = switchCases
                .Select(static item => item.TargetBlockId)
                .Append(defaultTarget)
                .Distinct()
                .ToArray();

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Switch,
                targets,
                ConditionText: $"{switchStatement.expression().GetText()}.length",
                Condition: length,
                SwitchCases: switchCases,
                DefaultTarget: defaultTarget);

            foreach (var group in lengthGroups)
            {
                CurrentBlock = group.Block;
                if (!EmitPartitionedTextLengthDecision(dataPointer, group.Labels, defaultTarget, switchStatement.expression().GetText()))
                {
                    return false;
                }
            }

            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            try
            {
                foreach (var section in sections)
                {
                    CurrentBlock = section.Block;
                    foreach (var nested in section.Section.statement())
                    {
                        LowerStatement(nested);
                    }

                    EnsureGoto(exitBlock.Id);
                }
            }
            finally
            {
                _breakTargets.Pop();
            }

            CurrentBlock = exitBlock;
            return true;
        }

        private bool TryLowerGuardedSwitch(
            StarkParser.SwitchStatementContext switchStatement,
            MidLevelIrOperand switchValue)
        {
            if (!TryParseLowerableSwitchSections(switchStatement, out var parsedSections, out var defaultSectionCount))
            {
                return false;
            }

            if (!CanLowerSwitchType(switchValue.Type) || defaultSectionCount > 1)
            {
                return false;
            }

            var sections = parsedSections
                .Select((section, index) => (
                    section.Section,
                    section.Labels,
                    EntryBlock: CreateBlock($"switch_test_{index}"),
                    BodyBlock: CreateBlock($"switch_case_{index}")))
                .ToArray();
            var exitBlock = CreateBlock("switch_exit");
            var defaultTarget = sections
                .Where(static section => section.Labels.Any(static label => label.IsDefault && label.GuardExpression is null && label.ImportedGuardExpression is null && label.CaptureName is null))
                .Select(static section => section.BodyBlock.Id)
                .FirstOrDefault(exitBlock.Id);

            if (!TryRegisterSwitchCaptureLocals(sections.Select(static section => section.Labels), switchValue.Type))
            {
                return false;
            }

            if (sections.Length == 0)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [defaultTarget]);
            }
            else
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Goto,
                    [sections[0].EntryBlock.Id]);

                for (var index = 0; index < sections.Length; index++)
                {
                    CurrentBlock = sections[index].EntryBlock;
                    var nextSectionTarget = index + 1 < sections.Length ? sections[index + 1].EntryBlock.Id : defaultTarget;

                    if (!EmitSwitchSectionDecision(
                        sections[index].Labels,
                        switchValue,
                        sections[index].BodyBlock.Id,
                        nextSectionTarget,
                        switchStatement.expression().GetText(),
                        index))
                    {
                        return false;
                    }
                }
            }

            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            try
            {
                foreach (var section in sections)
                {
                    CurrentBlock = section.BodyBlock;
                    foreach (var nested in section.Section.statement())
                    {
                        LowerStatement(nested);
                    }

                    EnsureGoto(exitBlock.Id);
                }
            }
            finally
            {
                _breakTargets.Pop();
            }

            CurrentBlock = exitBlock;
            return true;
        }

        private bool EmitSwitchSectionDecisionCore(
            IReadOnlyList<LowerableSwitchLabel> labels,
            MidLevelIrOperand switchValue,
            int targetBlockId,
            int nextSectionTarget,
            string switchText,
            int sectionIndex)
        {
            if (labels.Count == 0)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [nextSectionTarget]);
                return true;
            }

            var decisionBlocks = new BasicBlockBuilder[labels.Count];
            decisionBlocks[0] = CurrentBlock;
            for (var index = 1; index < labels.Count; index++)
            {
                decisionBlocks[index] = CreateBlock($"switch_test_{sectionIndex}_{index}");
            }

            for (var index = 0; index < labels.Count; index++)
            {
                CurrentBlock = decisionBlocks[index];
                var label = labels[index];
                var nextTarget = index + 1 < labels.Count ? decisionBlocks[index + 1].Id : nextSectionTarget;

                if (label.IsMatchAll)
                {
                    if (!EmitSwitchMatchTransition(label, switchValue, targetBlockId, nextTarget))
                    {
                        return false;
                    }

                    continue;
                }

                if (label.AggregatePattern is { } aggregatePattern)
                {
                    if (!EmitAggregateSwitchPatternTransition(label, aggregatePattern, switchValue, targetBlockId, nextTarget, sectionIndex, index))
                    {
                        return false;
                    }

                    continue;
                }

                MidLevelIrOperand? condition;
                if (label.Literal is not null)
                {
                    condition = EmitSwitchLiteralComparison(
                        switchValue,
                        label.Literal,
                        $"switch {switchText} == {label.LabelText}");
                }
                else if (label.ImportedLiteralExpression is not null)
                {
                    condition = EmitImportedTypedTemplateSwitchLiteralComparison(
                        switchValue,
                        label.ImportedLiteralExpression,
                        $"switch {switchText} == {label.LabelText}");
                }
                else
                {
                    return false;
                }

                if (condition is null)
                {
                    return false;
                }

                if (label.GuardExpression is null && label.ImportedGuardExpression is null && label.CaptureName is null)
                {
                    CurrentBlock.Terminator = new MidLevelIrTerminator(
                        MidLevelIrTerminatorKind.Branch,
                        [targetBlockId, nextTarget],
                        ConditionText: label.LabelText,
                        Condition: condition);
                    continue;
                }

                var matchBlock = CreateBlock($"switch_match_{sectionIndex}_{index}");
                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [matchBlock.Id, nextTarget],
                    ConditionText: label.LabelText,
                    Condition: condition);

                CurrentBlock = matchBlock;
                if (!EmitSwitchMatchTransition(label, switchValue, targetBlockId, nextTarget))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryParseAggregatePattern(StarkParser.AggregatePatternContext aggregatePattern, out LowerableAggregatePattern? parsedAggregatePattern)
        {
            parsedAggregatePattern = null;

            if (TryResolvePublishedEnumPatternSummary(aggregatePattern, out var publishedEnumPattern))
            {
                return TryParsePublishedEnumPattern(
                    aggregatePattern.aggregatePatternSuffix(),
                    publishedEnumPattern,
                    out parsedAggregatePattern);
            }

            if (TryResolvePublishedAggregatePatternSummary(aggregatePattern, out var publishedAggregatePattern))
            {
                var publishedPatternType = ApplyGenericSubstitution(publishedAggregatePattern.Type);
                if (publishedPatternType.Kind != StarkTypeKind.Named
                    || publishedPatternType.NamedType is null
                    || !_typeModel.NamedTypes.TryGetValue(publishedPatternType.NamedType, out var publishedNamedType))
                {
                    return false;
                }

                return TryParseResolvedAggregatePattern(
                    publishedPatternType,
                    publishedNamedType,
                    aggregatePattern.aggregatePatternSuffix(),
                    out parsedAggregatePattern);
            }

            var patternName = aggregatePattern.simpleType().GetText();
            if (TryResolveEnumCaseReference(patternName, out var enumType, out _, out var enumVariant))
            {
                if (enumVariant.UsesNamedFields)
                {
                    return false;
                }

                var enumSuffix = aggregatePattern.aggregatePatternSuffix();
                if (enumVariant.Fields.Count == 0)
                {
                    if (enumSuffix is not null)
                    {
                        return false;
                    }

                    parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, [], WholeCaptureName: null);
                    return true;
                }

                if (enumSuffix is null)
                {
                    return false;
                }

                if (enumSuffix.Identifier() is { } enumCapture)
                {
                    parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, [], enumCapture.GetText());
                    return true;
                }

                var enumFieldPatterns = enumSuffix.pattern();
                if (enumFieldPatterns.Length != enumVariant.Fields.Count)
                {
                    return false;
                }

                var parsedEnumFieldPatterns = new LowerableAggregateFieldPattern[enumFieldPatterns.Length];
                for (var index = 0; index < enumFieldPatterns.Length; index++)
                {
                    var field = enumVariant.Fields[index];
                    if (!TryParseStructuredFieldPattern(
                            enumFieldPatterns[index],
                            field.SourceFieldName ?? field.SourcePosition.ToString(),
                            field.StorageFieldName,
                            field.StorageFieldIndex,
                            field.Type,
                            out var parsedFieldPattern))
                    {
                        return false;
                    }

                    parsedEnumFieldPatterns[index] = parsedFieldPattern;
                }

                parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, parsedEnumFieldPatterns, WholeCaptureName: null);
                return true;
            }

            var patternType = _typeResolver.ResolveSimpleType(aggregatePattern.simpleType(), currentModuleName: CurrentModuleName);
            if (patternType.Kind != StarkTypeKind.Named
                || patternType.NamedType is null
                || !_typeModel.NamedTypes.TryGetValue(patternType.NamedType, out var namedType))
            {
                return false;
            }

            return TryParseResolvedAggregatePattern(
                patternType,
                namedType,
                aggregatePattern.aggregatePatternSuffix(),
                out parsedAggregatePattern);
        }

        private bool TryParseResolvedAggregatePattern(
            StarkTypeSymbol patternType,
            NamedTypeSymbol namedType,
            StarkParser.AggregatePatternSuffixContext? suffix,
            out LowerableAggregatePattern? parsedAggregatePattern)
        {
            parsedAggregatePattern = null;

            if (suffix is null)
            {
                parsedAggregatePattern = new LowerableAggregatePattern(patternType.NamedType!, EnumVariantName: null, [], WholeCaptureName: null);
                return true;
            }

            if (suffix.Identifier() is { } capture)
            {
                parsedAggregatePattern = new LowerableAggregatePattern(patternType.NamedType!, EnumVariantName: null, [], capture.GetText());
                return true;
            }

            var fieldPatterns = suffix.pattern();
            if (fieldPatterns.Length != namedType.OrderedFields.Count)
            {
                return false;
            }

            var parsedFieldPatterns = new LowerableAggregateFieldPattern[fieldPatterns.Length];
            for (var index = 0; index < fieldPatterns.Length; index++)
            {
                var field = namedType.OrderedFields[index];
                if (!TryParseStructuredFieldPattern(fieldPatterns[index], field.Name, field.Name, index, field.Type, out var parsedFieldPattern))
                {
                    return false;
                }

                parsedFieldPatterns[index] = parsedFieldPattern;
            }

            parsedAggregatePattern = new LowerableAggregatePattern(patternType.NamedType!, EnumVariantName: null, parsedFieldPatterns, WholeCaptureName: null);
            return true;
        }

        private bool TryParseAggregatePattern(StarkParser.GenericEnumAggregatePatternContext genericEnumAggregatePattern, out LowerableAggregatePattern? parsedAggregatePattern)
        {
            parsedAggregatePattern = null;

            if (TryResolvePublishedEnumPatternSummary(genericEnumAggregatePattern, out var publishedEnumPattern))
            {
                return TryParsePublishedEnumPattern(
                    genericEnumAggregatePattern.aggregatePatternSuffix(),
                    publishedEnumPattern,
                    out parsedAggregatePattern);
            }

            if (!TryResolveEnumCaseReference(genericEnumAggregatePattern.genericEnumCaseReference(), out var enumType, out _, out var enumVariant)
                || enumVariant.UsesNamedFields)
            {
                return false;
            }

            var enumSuffix = genericEnumAggregatePattern.aggregatePatternSuffix();
            if (enumVariant.Fields.Count == 0)
            {
                if (enumSuffix is not null)
                {
                    return false;
                }

                parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, [], WholeCaptureName: null);
                return true;
            }

            if (enumSuffix is null)
            {
                return false;
            }

            if (enumSuffix.Identifier() is { } enumCapture)
            {
                parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, [], enumCapture.GetText());
                return true;
            }

            var enumFieldPatterns = enumSuffix.pattern();
            if (enumFieldPatterns.Length != enumVariant.Fields.Count)
            {
                return false;
            }

            var parsedEnumFieldPatterns = new LowerableAggregateFieldPattern[enumFieldPatterns.Length];
            for (var index = 0; index < enumFieldPatterns.Length; index++)
            {
                var field = enumVariant.Fields[index];
                if (!TryParseStructuredFieldPattern(
                        enumFieldPatterns[index],
                        field.SourceFieldName ?? field.SourcePosition.ToString(),
                        field.StorageFieldName,
                        field.StorageFieldIndex,
                        field.Type,
                        out var parsedFieldPattern))
                {
                    return false;
                }

                parsedEnumFieldPatterns[index] = parsedFieldPattern;
            }

            parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, parsedEnumFieldPatterns, WholeCaptureName: null);
            return true;
        }

        private bool TryParseEnumNamedFieldPattern(StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern, out LowerableAggregatePattern? parsedAggregatePattern)
        {
            parsedAggregatePattern = null;

            if (TryResolvePublishedEnumPatternSummary(enumNamedFieldPattern, out var publishedEnumPattern))
            {
                return TryParsePublishedEnumNamedFieldPattern(enumNamedFieldPattern, publishedEnumPattern, out parsedAggregatePattern);
            }

            if (!TryResolveEnumCaseTarget(enumNamedFieldPattern.enumCaseTarget(), out _, out var enumType, out _, out var enumVariant)
                || !enumVariant.UsesNamedFields)
            {
                return false;
            }

            var members = enumNamedFieldPattern.enumNamedFieldPatternPayload().namedPatternMember();
            if (members.Length != enumVariant.Fields.Count)
            {
                return false;
            }

            var parsedFieldPatterns = new LowerableAggregateFieldPattern[enumVariant.Fields.Count];
            var seenMembers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in members)
            {
                var memberName = member.Identifier().GetText();
                var field = enumVariant.Fields.FirstOrDefault(candidate => string.Equals(candidate.SourceFieldName, memberName, StringComparison.Ordinal));
                if (field is null
                    || field.SourceFieldName is null
                    || !seenMembers.Add(memberName)
                    || !TryParseStructuredFieldPattern(
                        member.pattern(),
                        field.SourceFieldName,
                        field.StorageFieldName,
                        field.StorageFieldIndex,
                        field.Type,
                        out var parsedFieldPattern))
                {
                    return false;
                }

                parsedFieldPatterns[field.SourcePosition] = parsedFieldPattern;
            }

            if (seenMembers.Count != enumVariant.Fields.Count)
            {
                return false;
            }

            parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, parsedFieldPatterns, WholeCaptureName: null);
            return true;
        }

        private bool TryParsePublishedEnumPattern(
            StarkParser.AggregatePatternSuffixContext? enumSuffix,
            ImportedTemplateEnumPatternSummary publishedEnumPattern,
            out LowerableAggregatePattern? parsedAggregatePattern)
        {
            parsedAggregatePattern = null;

            var publishedEnumType = ApplyGenericSubstitution(publishedEnumPattern.EnumType);
            var publishedCaseName = $"{publishedEnumType.DisplayName}.{publishedEnumPattern.VariantName}";
            if (!TryResolveEnumCaseReference(publishedCaseName, out var enumType, out _, out var enumVariant)
                || enumVariant.UsesNamedFields)
            {
                return false;
            }

            if (enumVariant.Fields.Count == 0)
            {
                if (enumSuffix is not null)
                {
                    return false;
                }

                parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, [], WholeCaptureName: null);
                return true;
            }

            if (enumSuffix is null)
            {
                return false;
            }

            if (enumSuffix.Identifier() is { } enumCapture)
            {
                parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, [], enumCapture.GetText());
                return true;
            }

            var enumFieldPatterns = enumSuffix.pattern();
            if (enumFieldPatterns.Length != enumVariant.Fields.Count)
            {
                return false;
            }

            var parsedEnumFieldPatterns = new LowerableAggregateFieldPattern[enumFieldPatterns.Length];
            for (var index = 0; index < enumFieldPatterns.Length; index++)
            {
                var field = enumVariant.Fields[index];
                if (!TryParseStructuredFieldPattern(
                        enumFieldPatterns[index],
                        field.SourceFieldName ?? field.SourcePosition.ToString(),
                        field.StorageFieldName,
                        field.StorageFieldIndex,
                        field.Type,
                        out var parsedFieldPattern))
                {
                    return false;
                }

                parsedEnumFieldPatterns[index] = parsedFieldPattern;
            }

            parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, parsedEnumFieldPatterns, WholeCaptureName: null);
            return true;
        }

        private bool TryParsePublishedEnumNamedFieldPattern(
            StarkParser.EnumNamedFieldPatternContext enumNamedFieldPattern,
            ImportedTemplateEnumPatternSummary publishedEnumPattern,
            out LowerableAggregatePattern? parsedAggregatePattern)
        {
            parsedAggregatePattern = null;

            var publishedEnumType = ApplyGenericSubstitution(publishedEnumPattern.EnumType);
            var publishedCaseName = $"{publishedEnumType.DisplayName}.{publishedEnumPattern.VariantName}";
            if (!TryResolveEnumCaseReference(publishedCaseName, out var enumType, out _, out var enumVariant)
                || !enumVariant.UsesNamedFields)
            {
                return false;
            }

            var members = enumNamedFieldPattern.enumNamedFieldPatternPayload().namedPatternMember();
            if (members.Length != enumVariant.Fields.Count
                || publishedEnumPattern.Members.Count > 0 && members.Length != publishedEnumPattern.Members.Count)
            {
                return false;
            }

            var parsedFieldPatterns = new LowerableAggregateFieldPattern[enumVariant.Fields.Count];
            var seenMembers = new HashSet<int>();
            for (var memberOrdinal = 0; memberOrdinal < members.Length; memberOrdinal++)
            {
                var member = members[memberOrdinal];
                var memberName = member.Identifier().GetText();
                EnumVariantLayoutFieldSymbol? field;

                if (publishedEnumPattern.Members.Count > 0 && memberOrdinal < publishedEnumPattern.Members.Count)
                {
                    var publishedMember = publishedEnumPattern.Members[memberOrdinal];
                    memberName = publishedMember.FieldName;
                    field = publishedMember.FieldIndex >= 0 && publishedMember.FieldIndex < enumVariant.Fields.Count
                        ? enumVariant.Fields[publishedMember.FieldIndex]
                        : null;
                }
                else
                {
                    field = enumVariant.Fields.FirstOrDefault(candidate => string.Equals(candidate.SourceFieldName, memberName, StringComparison.Ordinal));
                }

                if (field is null
                    || field.SourceFieldName is null
                    || !seenMembers.Add(field.SourcePosition)
                    || !TryParseStructuredFieldPattern(
                        member.pattern(),
                        field.SourceFieldName,
                        field.StorageFieldName,
                        field.StorageFieldIndex,
                        field.Type,
                        out var parsedFieldPattern))
                {
                    return false;
                }

                parsedFieldPatterns[field.SourcePosition] = parsedFieldPattern;
            }

            if (seenMembers.Count != enumVariant.Fields.Count)
            {
                return false;
            }

            parsedAggregatePattern = new LowerableAggregatePattern(enumType.NamedType!, enumVariant.Name, parsedFieldPatterns, WholeCaptureName: null);
            return true;
        }

        private bool TryParseStructuredFieldPattern(
            StarkParser.PatternContext pattern,
            string fieldName,
            string storageFieldName,
            int fieldIndex,
            StarkTypeSymbol fieldType,
            out LowerableAggregateFieldPattern parsedFieldPattern)
        {
            if (pattern.DISCARD() is not null)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Discard,
                    pattern.GetText(),
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: null,
                    ImportedLiteralExpression: null);
                return true;
            }

            if (pattern.VAR() is not null)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Capture,
                    pattern.GetText(),
                    Literal: null,
                    CaptureName: pattern.Identifier()?.GetText(),
                    NestedPattern: null,
                    ImportedLiteralExpression: null);
                return true;
            }

            if (pattern.literal() is { } literal)
            {
                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Literal,
                    literal.GetText(),
                    literal,
                    CaptureName: null,
                    NestedPattern: null,
                    ImportedLiteralExpression: null);
                return true;
            }

            if (pattern.enumNamedFieldPattern() is { } nestedEnumNamedFieldPattern)
            {
                if (!TryParseEnumNamedFieldPattern(nestedEnumNamedFieldPattern, out var parsedNestedPattern)
                    || parsedNestedPattern is null)
                {
                    parsedFieldPattern = default!;
                    return false;
                }

                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Nested,
                    nestedEnumNamedFieldPattern.GetText(),
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: parsedNestedPattern,
                    ImportedLiteralExpression: null);
                return true;
            }

            if (pattern.aggregatePattern() is { } nestedAggregatePattern)
            {
                if (!TryParseAggregatePattern(nestedAggregatePattern, out var parsedNestedPattern)
                    || parsedNestedPattern is null)
                {
                    parsedFieldPattern = default!;
                    return false;
                }

                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Nested,
                    nestedAggregatePattern.GetText(),
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: parsedNestedPattern,
                    ImportedLiteralExpression: null);
                return true;
            }

            if (pattern.genericEnumAggregatePattern() is { } nestedGenericEnumAggregatePattern)
            {
                if (!TryParseAggregatePattern(nestedGenericEnumAggregatePattern, out var parsedNestedPattern)
                    || parsedNestedPattern is null)
                {
                    parsedFieldPattern = default!;
                    return false;
                }

                parsedFieldPattern = new LowerableAggregateFieldPattern(
                    fieldName,
                    storageFieldName,
                    fieldIndex,
                    fieldType,
                    AggregatePatternFieldKind.Nested,
                    nestedGenericEnumAggregatePattern.GetText(),
                    Literal: null,
                    CaptureName: null,
                    NestedPattern: parsedNestedPattern,
                    ImportedLiteralExpression: null);
                return true;
            }

            parsedFieldPattern = default!;
            return false;
        }

        private bool TryParseLowerableSwitchSections(
            StarkParser.SwitchStatementContext switchStatement,
            out List<LowerableSwitchSection> sections,
            out int defaultSectionCount)
        {
            sections = [];
            defaultSectionCount = 0;

            foreach (var section in switchStatement.switchSection())
            {
                var labels = new List<LowerableSwitchLabel>();

                foreach (var label in section.switchLabel())
                {
                    if (label.DEFAULT() is not null)
                    {
                        labels.Add(new LowerableSwitchLabel("default", null, null, IsDefault: true, IsMatchAll: true, CaptureName: null, AggregatePattern: null));
                        defaultSectionCount++;
                        continue;
                    }

                    var pattern = label.pattern();
                    if (pattern is null)
                    {
                        return false;
                    }

                    if (pattern.DISCARD() is not null)
                    {
                        if (label.whenClause() is null)
                        {
                            labels.Add(new LowerableSwitchLabel(pattern.GetText(), null, null, IsDefault: true, IsMatchAll: true, CaptureName: null, AggregatePattern: null));
                            defaultSectionCount++;
                            continue;
                        }

                        labels.Add(new LowerableSwitchLabel(
                            pattern.GetText(),
                            Literal: null,
                            GuardExpression: label.whenClause()?.expression(),
                            IsDefault: false,
                            IsMatchAll: true,
                            CaptureName: null,
                            AggregatePattern: null));
                        continue;
                    }

                    if (pattern.VAR() is not null)
                    {
                        labels.Add(new LowerableSwitchLabel(
                            pattern.GetText(),
                            Literal: null,
                            GuardExpression: label.whenClause()?.expression(),
                            IsDefault: false,
                            IsMatchAll: true,
                            CaptureName: pattern.Identifier()?.GetText(),
                            AggregatePattern: null));
                        continue;
                    }

                    if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
                    {
                        if (!TryParseEnumNamedFieldPattern(enumNamedFieldPattern, out var parsedEnumNamedFieldPattern)
                            || parsedEnumNamedFieldPattern is null)
                        {
                            return false;
                        }

                        labels.Add(new LowerableSwitchLabel(
                            enumNamedFieldPattern.GetText(),
                            Literal: null,
                            GuardExpression: label.whenClause()?.expression(),
                            IsDefault: false,
                            IsMatchAll: false,
                            CaptureName: null,
                            AggregatePattern: parsedEnumNamedFieldPattern));
                        continue;
                    }

                    if (pattern.aggregatePattern() is { } aggregatePattern)
                    {
                        if (!TryParseAggregatePattern(aggregatePattern, out var parsedAggregatePattern)
                            || parsedAggregatePattern is null)
                        {
                            return false;
                        }

                        labels.Add(new LowerableSwitchLabel(
                            aggregatePattern.GetText(),
                            Literal: null,
                            GuardExpression: label.whenClause()?.expression(),
                            IsDefault: false,
                            IsMatchAll: false,
                            CaptureName: null,
                            AggregatePattern: parsedAggregatePattern));
                        continue;
                    }

                    if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern)
                    {
                        if (!TryParseAggregatePattern(genericEnumAggregatePattern, out var parsedAggregatePattern)
                            || parsedAggregatePattern is null)
                        {
                            return false;
                        }

                        labels.Add(new LowerableSwitchLabel(
                            genericEnumAggregatePattern.GetText(),
                            Literal: null,
                            GuardExpression: label.whenClause()?.expression(),
                            IsDefault: false,
                            IsMatchAll: false,
                            CaptureName: null,
                            AggregatePattern: parsedAggregatePattern));
                        continue;
                    }

                    if (pattern.literal() is not { } literal)
                    {
                        return false;
                    }

                    labels.Add(new LowerableSwitchLabel(
                        literal.GetText(),
                        literal,
                        label.whenClause()?.expression(),
                        IsDefault: false,
                        IsMatchAll: false,
                        CaptureName: null,
                        AggregatePattern: null));
                }

                sections.Add(new LowerableSwitchSection(section, labels));
            }

            return true;
        }

        private bool TryRegisterSwitchCaptureLocalsCore(
            IEnumerable<IReadOnlyList<LowerableSwitchLabel>> sectionLabels,
            StarkTypeSymbol switchType)
        {
            foreach (var labels in sectionLabels)
            {
                var aggregateLabels = labels.Where(static label => label.AggregatePattern is not null).ToArray();
                if (aggregateLabels.Length != 0)
                {
                    if (aggregateLabels.Length != 1 || labels.Count != 1)
                    {
                        return false;
                    }

                    var aggregatePattern = aggregateLabels[0].AggregatePattern!;
                    if (!TryRegisterAggregatePatternCaptureLocals(aggregatePattern, switchType))
                    {
                        return false;
                    }

                    continue;
                }

                var captureLabels = labels.Where(static label => label.CaptureName is not null).ToArray();
                if (captureLabels.Length == 0)
                {
                    continue;
                }

                if (captureLabels.Length != 1 || labels.Count != 1)
                {
                    return false;
                }

                var captureName = captureLabels[0].CaptureName!;
                if (_localsByName.ContainsKey(captureName) || _parametersByName.ContainsKey(captureName))
                {
                    return false;
                }

                RegisterLocal(captureName, switchType, storageClass: "match", isMutable: false, isConstant: false);
            }

            return true;
        }

        private bool TryRegisterAggregatePatternCaptureLocals(LowerableAggregatePattern aggregatePattern, StarkTypeSymbol aggregateValueType)
        {
            if (aggregatePattern.WholeCaptureName is { } wholeCaptureName)
            {
                if (_localsByName.ContainsKey(wholeCaptureName) || _parametersByName.ContainsKey(wholeCaptureName))
                {
                    return false;
                }

                RegisterLocal(wholeCaptureName, aggregateValueType, storageClass: "match", isMutable: false, isConstant: false);
            }

            foreach (var fieldPattern in aggregatePattern.FieldPatterns)
            {
                if (fieldPattern.Kind == AggregatePatternFieldKind.Capture)
                {
                    if (fieldPattern.CaptureName is null
                        || _localsByName.ContainsKey(fieldPattern.CaptureName)
                        || _parametersByName.ContainsKey(fieldPattern.CaptureName))
                    {
                        return false;
                    }

                    RegisterLocal(fieldPattern.CaptureName, fieldPattern.FieldType, storageClass: "match", isMutable: false, isConstant: false);
                    continue;
                }

                if (fieldPattern.Kind == AggregatePatternFieldKind.Nested)
                {
                    if (fieldPattern.NestedPattern is null
                        || !TryRegisterAggregatePatternCaptureLocals(fieldPattern.NestedPattern, fieldPattern.FieldType))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private bool EmitSwitchMatchTransition(LowerableSwitchLabel label, MidLevelIrOperand switchValue, int targetBlockId, int nextTarget)
        {
            IReadOnlyList<PendingSwitchBinding> bindings = label.CaptureName is null
                ? []
                : [new PendingSwitchBinding(label.CaptureName, switchValue)];

            return EmitSwitchBindingsAndGuard(label.GuardExpression, label.ImportedGuardExpression, bindings, targetBlockId, nextTarget);
        }

        private bool EmitAggregateSwitchPatternTransition(
            LowerableSwitchLabel label,
            LowerableAggregatePattern aggregatePattern,
            MidLevelIrOperand switchValue,
            int targetBlockId,
            int nextTarget,
            int sectionIndex,
            int labelIndex)
        {
            if (switchValue.Type.Kind != StarkTypeKind.Named
                || switchValue.Type.NamedType is null
                || !string.Equals(switchValue.Type.NamedType, aggregatePattern.TypeName, StringComparison.Ordinal))
            {
                return false;
            }

            var bindings = new List<PendingSwitchBinding>();
            var matchBlock = CreateBlock($"switch_agg_match_{sectionIndex}_{labelIndex}");
            if (!EmitAggregatePatternDecision(
                aggregatePattern,
                switchValue,
                matchBlock.Id,
                nextTarget,
                bindings,
                $"{sectionIndex}_{labelIndex}"))
            {
                return false;
            }

            CurrentBlock = matchBlock;
            return EmitSwitchBindingsAndGuard(label.GuardExpression, label.ImportedGuardExpression, bindings, targetBlockId, nextTarget);
        }

        private bool EmitAggregatePatternDecision(
            LowerableAggregatePattern aggregatePattern,
            MidLevelIrOperand switchValue,
            int successTarget,
            int failureTarget,
            List<PendingSwitchBinding> bindings,
            string pathTag)
        {
            if (aggregatePattern.WholeCaptureName is { } wholeCaptureName)
            {
                bindings.Add(new PendingSwitchBinding(wholeCaptureName, switchValue));
            }

            var fieldPatterns = aggregatePattern.FieldPatterns;
            if (aggregatePattern.EnumVariantName is { } enumVariantName)
            {
                if (!_enumLayoutModel.Layouts.TryGetValue(aggregatePattern.TypeName, out var enumLayout)
                    || !enumLayout.TryGetVariant(enumVariantName, out var enumVariant))
                {
                    return false;
                }

                BasicBlockBuilder? payloadEntryBlock = null;
                var successAfterTag = successTarget;
                if (fieldPatterns.Count != 0)
                {
                    payloadEntryBlock = CreateBlock($"switch_enum_match_{pathTag}");
                    successAfterTag = payloadEntryBlock.Id;
                }

                var tagValue = LowerKnownFieldAccess(switchValue, enumLayout.TagField.Name, fieldIndex: 0, enumLayout.TagField.Type, "$tag");
                var expectedTag = new MidLevelIrIntegerConstantOperand(new BigInteger(enumVariant.TagValue), enumLayout.TagField.Type);
                var condition = EmitResolvedEqualityComparison(tagValue, expectedTag, $"switch {switchValue.Text} is {aggregatePattern.TypeName}.{enumVariantName}");

                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [successAfterTag, failureTarget],
                    ConditionText: $"{aggregatePattern.TypeName}.{enumVariantName}",
                    Condition: condition);

                if (fieldPatterns.Count == 0)
                {
                    return true;
                }

                CurrentBlock = payloadEntryBlock!;
            }

            if (fieldPatterns.Count == 0)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [successTarget]);
                return true;
            }

            var decisionBlocks = new BasicBlockBuilder[fieldPatterns.Count];
            decisionBlocks[0] = CurrentBlock;
            for (var index = 1; index < fieldPatterns.Count; index++)
            {
                decisionBlocks[index] = CreateBlock($"switch_agg_test_{pathTag}_{index}");
            }

            for (var index = 0; index < fieldPatterns.Count; index++)
            {
                CurrentBlock = decisionBlocks[index];
                var fieldPattern = fieldPatterns[index];
                var nextTarget = index + 1 < fieldPatterns.Count ? decisionBlocks[index + 1].Id : successTarget;

                if (fieldPattern.Kind == AggregatePatternFieldKind.Discard)
                {
                    CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [nextTarget]);
                    continue;
                }

                var fieldValue = LowerKnownFieldAccess(switchValue, fieldPattern.StorageFieldName, fieldPattern.FieldIndex, fieldPattern.FieldType, fieldPattern.FieldName);
                if (fieldPattern.Kind == AggregatePatternFieldKind.Capture)
                {
                    bindings.Add(new PendingSwitchBinding(fieldPattern.CaptureName!, fieldValue));
                    CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [nextTarget]);
                    continue;
                }

                if (fieldPattern.Kind == AggregatePatternFieldKind.Nested)
                {
                    if (fieldPattern.NestedPattern is null
                        || !EmitAggregatePatternDecision(
                            fieldPattern.NestedPattern,
                            fieldValue,
                            nextTarget,
                            failureTarget,
                            bindings,
                            $"{pathTag}_{index}"))
                    {
                        return false;
                    }

                    continue;
                }

                var condition = fieldPattern.ImportedLiteralExpression is { } importedLiteralExpression
                    ? EmitImportedTypedTemplateSwitchLiteralComparison(
                        fieldValue,
                        importedLiteralExpression,
                        $"switch {switchValue.Text}.{fieldPattern.FieldName} == {fieldPattern.Text}")
                    : EmitSwitchLiteralComparison(
                        fieldValue,
                        fieldPattern.Literal!,
                        $"switch {switchValue.Text}.{fieldPattern.FieldName} == {fieldPattern.Text}");
                if (condition is null)
                {
                    return false;
                }

                CurrentBlock.Terminator = new MidLevelIrTerminator(
                    MidLevelIrTerminatorKind.Branch,
                    [nextTarget, failureTarget],
                    ConditionText: fieldPattern.Text,
                    Condition: condition);
            }

            return true;
        }

        private bool EmitSwitchBindingsAndGuard(
            StarkParser.ExpressionContext? guardExpression,
            ImportedTemplateTypedBodyExpressionSummary? importedGuardExpression,
            IReadOnlyList<PendingSwitchBinding> bindings,
            int targetBlockId,
            int nextTarget)
        {
            if (bindings.Count != 0 && (guardExpression is not null || importedGuardExpression is not null))
            {
                var bindBlock = CreateBlock("switch_bind");
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [bindBlock.Id]);
                CurrentBlock = bindBlock;
            }

            foreach (var binding in bindings)
            {
                var capture = new MidLevelIrLocalOperand(binding.Name, binding.Source.Type);
                EmitOperandAssignment(capture, binding.Source, binding.Source.Text);
            }

            if (guardExpression is null && importedGuardExpression is null)
            {
                CurrentBlock.Terminator = new MidLevelIrTerminator(MidLevelIrTerminatorKind.Goto, [targetBlockId]);
                return true;
            }

            MidLevelIrOperand? guard;
            string conditionText;
            if (guardExpression is not null)
            {
                guard = LowerExpressionToOperand(guardExpression, StarkTypeSymbols.Bool);
                conditionText = guardExpression.GetText();
            }
            else
            {
                guard = LowerImportedTypedTemplateExpression(importedGuardExpression!, StarkTypeSymbols.Bool);
                conditionText = RenderImportedTypedTemplateExpression(importedGuardExpression!);
            }

            if (guard is null)
            {
                return false;
            }

            CurrentBlock.Terminator = new MidLevelIrTerminator(
                MidLevelIrTerminatorKind.Branch,
                [targetBlockId, nextTarget],
                ConditionText: conditionText,
                Condition: guard);
            return true;
        }

        private sealed class SwitchPatternLowerer
        {
            private readonly FunctionMirBuilder _builder;

            public SwitchPatternLowerer(FunctionMirBuilder builder)
            {
                _builder = builder;
            }

            public void LowerSwitch(StarkParser.SwitchStatementContext switchStatement)
            {
                _builder.LowerSwitchCore(switchStatement);
            }

            public bool TryRegisterSwitchCaptureLocals(
                IEnumerable<IReadOnlyList<LowerableSwitchLabel>> sectionLabels,
                StarkTypeSymbol switchType)
            {
                return _builder.TryRegisterSwitchCaptureLocalsCore(sectionLabels, switchType);
            }

            public bool EmitSwitchSectionDecision(
                IReadOnlyList<LowerableSwitchLabel> labels,
                MidLevelIrOperand switchValue,
                int targetBlockId,
                int nextSectionTarget,
                string switchText,
                int sectionIndex)
            {
                return _builder.EmitSwitchSectionDecisionCore(
                    labels,
                    switchValue,
                    targetBlockId,
                    nextSectionTarget,
                    switchText,
                    sectionIndex);
            }
        }
    }
}
