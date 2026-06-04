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
            IReadOnlyList<LowerableSwitchLabel> labels,
            StarkTypeSymbol switchType,
            out IReadOnlyList<LowerableSwitchLabel> registeredLabels)
        {
            return _switchPatternLowerer.TryRegisterSwitchCaptureLocals(labels, switchType, out registeredLabels);
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
                throw LoweringInvariantViolation(
                    switchStatement.expression(),
                    "Switch expression was accepted but could not be lowered to a MIR operand.");
            }

            var hasBoundSwitch = TryResolveBoundSwitchDispatch(switchStatement, out var boundSwitch);
            if (hasBoundSwitch
                && !HasSameStorageType(ApplyGenericSubstitution(boundSwitch.SwitchType), switchValue.Type))
            {
                throw LoweringInvariantViolation(
                    switchStatement,
                    $"Bound switch dispatch type '{ApplyGenericSubstitution(boundSwitch.SwitchType).DisplayName}' does not match lowered switch value type '{switchValue.Type.DisplayName}'.");
            }

            var lowered = hasBoundSwitch
                ? boundSwitch.Family switch
                {
                    SwitchLoweringFamilies.Native => TryLowerNativeSwitch(switchStatement, switchValue),
                    SwitchLoweringFamilies.PartitionedText => TryLowerPartitionedTextSwitch(switchStatement, switchValue),
                    SwitchLoweringFamilies.Guarded => TryLowerGuardedSwitch(switchStatement, switchValue),
                    _ => throw LoweringInvariantViolation(
                        switchStatement,
                        $"Bound switch dispatch family '{boundSwitch.Family}' has no MIR lowering case.")
                }
                : switchValue.Type.Kind switch
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

            throw LoweringInvariantViolation(
                switchStatement,
                "Accepted switch shape could not be lowered by native, partitioned text, or guarded switch lowering.");
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
                DefaultTarget: resolvedDefaultTarget,
                BranchWeights: CreateSwitchBranchWeights(switchStatement.weightSpecifier(), switchCases.Count));

            var switchEntryDropStates = SnapshotRuntimeDropStates();
            var exitDropStates = new List<IReadOnlyDictionary<string, bool>>();
            if (resolvedDefaultTarget == exitBlock.Id)
            {
                exitDropStates.Add(switchEntryDropStates);
            }

            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            try
            {
                foreach (var section in sections)
                {
                    RestoreRuntimeDropStates(switchEntryDropStates);
                    CurrentBlock = section.Block;
                    LowerSwitchSectionStatements(section.Section, section.Labels, switchValue.Type);
                    var sectionFallsThrough = !CurrentBlock.HasTerminator;
                    var sectionDropStates = SnapshotRuntimeDropStates();

                    EnsureGoto(exitBlock.Id);
                    if (sectionFallsThrough)
                    {
                        exitDropStates.Add(sectionDropStates);
                    }
                }
            }
            finally
            {
                _breakTargets.Pop();
            }

            RestoreRuntimeDropStates(MergeRuntimeDropStates(exitDropStates, switchEntryDropStates));
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

            var switchEntryDropStates = SnapshotRuntimeDropStates();
            var exitDropStates = new List<IReadOnlyDictionary<string, bool>>();
            if (defaultTarget == exitBlock.Id)
            {
                exitDropStates.Add(switchEntryDropStates);
            }

            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            try
            {
                foreach (var section in sections)
                {
                    RestoreRuntimeDropStates(switchEntryDropStates);
                    CurrentBlock = section.Block;
                    LowerSwitchSectionStatements(section.Section, [], switchValue.Type);
                    var sectionFallsThrough = !CurrentBlock.HasTerminator;
                    var sectionDropStates = SnapshotRuntimeDropStates();

                    EnsureGoto(exitBlock.Id);
                    if (sectionFallsThrough)
                    {
                        exitDropStates.Add(sectionDropStates);
                    }
                }
            }
            finally
            {
                _breakTargets.Pop();
            }

            RestoreRuntimeDropStates(MergeRuntimeDropStates(exitDropStates, switchEntryDropStates));
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

            if (!TryRegisterSwitchCaptureLocalsCore(parsedSections, switchValue.Type, out var registeredSections))
            {
                return false;
            }

            var sections = registeredSections
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

            var switchEntryDropStates = SnapshotRuntimeDropStates();
            var bodyEntryDropStates = new Dictionary<int, Dictionary<string, bool>>();
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
                    RestoreRuntimeDropStates(switchEntryDropStates);
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

                    bodyEntryDropStates[sections[index].BodyBlock.Id] = SnapshotRuntimeDropStates();
                }
            }

            var exitDropStates = new List<IReadOnlyDictionary<string, bool>>();
            if (defaultTarget == exitBlock.Id)
            {
                exitDropStates.Add(switchEntryDropStates);
            }

            _breakTargets.Push(new BreakTargets(exitBlock.Id, _scopes.Count));
            try
            {
                foreach (var section in sections)
                {
                    RestoreRuntimeDropStates(
                        bodyEntryDropStates.TryGetValue(section.BodyBlock.Id, out var bodyEntryDropState)
                            ? bodyEntryDropState
                            : switchEntryDropStates);
                    CurrentBlock = section.BodyBlock;
                    LowerSwitchSectionStatements(section.Section, section.Labels, switchValue.Type);
                    var sectionFallsThrough = !CurrentBlock.HasTerminator;
                    var sectionDropStates = SnapshotRuntimeDropStates();

                    EnsureGoto(exitBlock.Id);
                    if (sectionFallsThrough)
                    {
                        exitDropStates.Add(sectionDropStates);
                    }
                }
            }
            finally
            {
                _breakTargets.Pop();
            }

            RestoreRuntimeDropStates(MergeRuntimeDropStates(exitDropStates, switchEntryDropStates));
            CurrentBlock = exitBlock;
            return true;
        }

        private void LowerSwitchSectionStatements(
            StarkParser.SwitchSectionContext section,
            IReadOnlyList<LowerableSwitchLabel> labels,
            StarkTypeSymbol switchType)
        {
            _scopes.Push(new ScopeFrame());
            TrackSwitchSectionCaptureLocals(labels, switchType);
            _compileTimeConstantState.PushScope();

            try
            {
                foreach (var nested in section.statement())
                {
                    LowerStatement(nested);
                }
            }
            finally
            {
                _compileTimeConstantState.PopScope();
            }

            var scope = _scopes.Pop();
            EmitStorageDead(scope);
            RestoreScopedNameAliases(scope);
        }

        private void TrackSwitchSectionCaptureLocals(IReadOnlyList<LowerableSwitchLabel> labels, StarkTypeSymbol switchType)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var label in labels)
            {
                if (label.CaptureName is { } captureName)
                {
                    TrackSwitchSectionCaptureLocal(
                        captureName,
                        label.CaptureStorageName ?? captureName,
                        switchType,
                        seen);
                }

                if (label.AggregatePattern is { } aggregatePattern)
                {
                    TrackAggregatePatternCaptureLocals(aggregatePattern, switchType, seen);
                }
            }
        }

        private void TrackAggregatePatternCaptureLocals(
            LowerableAggregatePattern aggregatePattern,
            StarkTypeSymbol aggregateValueType,
            HashSet<string> seen)
        {
            if (aggregatePattern.WholeCaptureName is { } wholeCaptureName)
            {
                TrackSwitchSectionCaptureLocal(
                    wholeCaptureName,
                    aggregatePattern.WholeCaptureStorageName ?? wholeCaptureName,
                    aggregateValueType,
                    seen);
            }

            foreach (var fieldPattern in aggregatePattern.FieldPatterns)
            {
                if (fieldPattern.Kind == AggregatePatternFieldKind.Capture && fieldPattern.CaptureName is not null)
                {
                    TrackSwitchSectionCaptureLocal(
                        fieldPattern.CaptureName,
                        fieldPattern.CaptureStorageName ?? fieldPattern.CaptureName,
                        fieldPattern.FieldType,
                        seen);
                    continue;
                }

                if (fieldPattern.Kind == AggregatePatternFieldKind.Nested && fieldPattern.NestedPattern is not null)
                {
                    TrackAggregatePatternCaptureLocals(fieldPattern.NestedPattern, fieldPattern.FieldType, seen);
                }
            }
        }

        private void TrackSwitchSectionCaptureLocal(
            string sourceName,
            string storageName,
            StarkTypeSymbol type,
            HashSet<string> seen)
        {
            if (!seen.Add(sourceName))
            {
                return;
            }

            var trackedType = _localsByName.TryGetValue(storageName, out var local)
                ? local.Type
                : type;
            TrackDeclaredLocal(storageName, trackedType);
            if (!string.Equals(sourceName, storageName, StringComparison.Ordinal))
            {
                PushScopedNameAlias(sourceName, storageName);
            }
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

                    var patterns = label.pattern();
                    if (patterns.Length == 0)
                    {
                        return false;
                    }

                    foreach (var pattern in patterns)
                    {
                        if (!TryBuildSwitchLabelFromPattern(pattern, label.whenClause()?.expression(), out var builtLabel)
                            || builtLabel is null)
                        {
                            return false;
                        }

                        if (builtLabel.IsDefault)
                        {
                            defaultSectionCount++;
                        }

                        labels.Add(builtLabel);
                    }
                }

                sections.Add(new LowerableSwitchSection(section, labels));
            }

            return true;
        }

        // Builds a single lowerable label from one `pattern` (with an optional `when` guard).
        // Shared by `switch case` sections and by `if (expr is pattern)` / `while (expr is pattern)`
        // so all three get identical pattern coverage (discard, var-capture, enum/struct/nested
        // aggregate, named-field enum, literal).
        private bool TryBuildSwitchLabelFromPattern(
            StarkParser.PatternContext pattern,
            StarkParser.ExpressionContext? guardExpression,
            out LowerableSwitchLabel? label)
        {
            label = null;

            if (pattern.DISCARD() is not null)
            {
                label = guardExpression is null
                    ? new LowerableSwitchLabel(pattern.GetText(), null, null, IsDefault: true, IsMatchAll: true, CaptureName: null, AggregatePattern: null)
                    : new LowerableSwitchLabel(
                        pattern.GetText(),
                        Literal: null,
                        GuardExpression: guardExpression,
                        IsDefault: false,
                        IsMatchAll: true,
                        CaptureName: null,
                        AggregatePattern: null);
                return true;
            }

            if (pattern.VAR() is not null)
            {
                label = new LowerableSwitchLabel(
                    pattern.GetText(),
                    Literal: null,
                    GuardExpression: guardExpression,
                    IsDefault: false,
                    IsMatchAll: true,
                    CaptureName: pattern.Identifier()?.GetText(),
                    AggregatePattern: null);
                return true;
            }

            if (pattern.enumNamedFieldPattern() is { } enumNamedFieldPattern)
            {
                if (!TryParseEnumNamedFieldPattern(enumNamedFieldPattern, out var parsedEnumNamedFieldPattern)
                    || parsedEnumNamedFieldPattern is null)
                {
                    return false;
                }

                label = new LowerableSwitchLabel(
                    enumNamedFieldPattern.GetText(),
                    Literal: null,
                    GuardExpression: guardExpression,
                    IsDefault: false,
                    IsMatchAll: false,
                    CaptureName: null,
                    AggregatePattern: parsedEnumNamedFieldPattern);
                return true;
            }

            if (pattern.aggregatePattern() is { } aggregatePattern)
            {
                if (!TryParseAggregatePattern(aggregatePattern, out var parsedAggregatePattern)
                    || parsedAggregatePattern is null)
                {
                    return false;
                }

                label = new LowerableSwitchLabel(
                    aggregatePattern.GetText(),
                    Literal: null,
                    GuardExpression: guardExpression,
                    IsDefault: false,
                    IsMatchAll: false,
                    CaptureName: null,
                    AggregatePattern: parsedAggregatePattern);
                return true;
            }

            if (pattern.genericEnumAggregatePattern() is { } genericEnumAggregatePattern)
            {
                if (!TryParseAggregatePattern(genericEnumAggregatePattern, out var parsedAggregatePattern)
                    || parsedAggregatePattern is null)
                {
                    return false;
                }

                label = new LowerableSwitchLabel(
                    genericEnumAggregatePattern.GetText(),
                    Literal: null,
                    GuardExpression: guardExpression,
                    IsDefault: false,
                    IsMatchAll: false,
                    CaptureName: null,
                    AggregatePattern: parsedAggregatePattern);
                return true;
            }

            if (pattern.literal() is not { } literal)
            {
                return false;
            }

            label = new LowerableSwitchLabel(
                literal.GetText(),
                literal,
                guardExpression,
                IsDefault: false,
                IsMatchAll: false,
                CaptureName: null,
                AggregatePattern: null);
            return true;
        }

        private bool TryRegisterSwitchCaptureLocalsCore(
            IReadOnlyList<LowerableSwitchSection> sections,
            StarkTypeSymbol switchType,
            out IReadOnlyList<LowerableSwitchSection> registeredSections)
        {
            var rewrittenSections = new List<LowerableSwitchSection>(sections.Count);
            foreach (var section in sections)
            {
                if (!TryRegisterSwitchCaptureLocalsCore(section.Labels, switchType, out var registeredLabels))
                {
                    registeredSections = [];
                    return false;
                }

                rewrittenSections.Add(section with { Labels = registeredLabels });
            }

            registeredSections = rewrittenSections;
            return true;
        }

        private bool TryRegisterSwitchCaptureLocalsCore(
            IReadOnlyList<LowerableSwitchLabel> labels,
            StarkTypeSymbol switchType,
            out IReadOnlyList<LowerableSwitchLabel> registeredLabels)
        {
            registeredLabels = [];
            var expectedCaptures = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
            var hasExpectedCaptures = false;

            foreach (var label in labels)
            {
                if (!TryCollectLowerableLabelCaptures(label, switchType, out var captures))
                {
                    return false;
                }

                if (!hasExpectedCaptures)
                {
                    expectedCaptures = captures;
                    hasExpectedCaptures = true;
                    continue;
                }

                if (!HaveSameLowerableCaptures(expectedCaptures, captures))
                {
                    return false;
                }
            }

            if (!hasExpectedCaptures || expectedCaptures.Count == 0)
            {
                registeredLabels = labels;
                return true;
            }

            var seenCaptures = new HashSet<string>(StringComparer.Ordinal);
            var storageNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var capture in expectedCaptures.OrderBy(static capture => capture.Key, StringComparer.Ordinal))
            {
                if (!TryRegisterSwitchCaptureLocal(capture.Key, capture.Value, seenCaptures, out var storageName))
                {
                    return false;
                }

                storageNames[capture.Key] = storageName;
            }

            var rewrittenLabels = new LowerableSwitchLabel[labels.Count];
            for (var index = 0; index < labels.Count; index++)
            {
                if (!TryApplySwitchCaptureStorageNames(labels[index], storageNames, out rewrittenLabels[index]))
                {
                    return false;
                }
            }

            registeredLabels = rewrittenLabels;
            return true;
        }

        private bool TryCollectLowerableLabelCaptures(
            LowerableSwitchLabel label,
            StarkTypeSymbol switchType,
            out Dictionary<string, StarkTypeSymbol> captures)
        {
            captures = new Dictionary<string, StarkTypeSymbol>(StringComparer.Ordinal);
            if (label.CaptureName is { } captureName
                && !TryAddLowerableCapture(captures, captureName, switchType))
            {
                return false;
            }

            return label.AggregatePattern is null
                || TryCollectLowerableAggregateCaptures(label.AggregatePattern, switchType, captures);
        }

        private bool TryCollectLowerableAggregateCaptures(
            LowerableAggregatePattern aggregatePattern,
            StarkTypeSymbol aggregateValueType,
            Dictionary<string, StarkTypeSymbol> captures)
        {
            if (aggregatePattern.WholeCaptureName is { } wholeCaptureName
                && !TryAddLowerableCapture(captures, wholeCaptureName, aggregateValueType))
            {
                return false;
            }

            foreach (var fieldPattern in aggregatePattern.FieldPatterns)
            {
                if (fieldPattern.Kind == AggregatePatternFieldKind.Capture)
                {
                    if (fieldPattern.CaptureName is null
                        || !TryAddLowerableCapture(captures, fieldPattern.CaptureName, fieldPattern.FieldType))
                    {
                        return false;
                    }
                }

                if (fieldPattern.Kind == AggregatePatternFieldKind.Nested
                    && (fieldPattern.NestedPattern is null
                        || !TryCollectLowerableAggregateCaptures(fieldPattern.NestedPattern, fieldPattern.FieldType, captures)))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryAddLowerableCapture(
            Dictionary<string, StarkTypeSymbol> captures,
            string name,
            StarkTypeSymbol type)
        {
            if (captures.ContainsKey(name))
            {
                return false;
            }

            captures.Add(name, type);
            return true;
        }

        private static bool HaveSameLowerableCaptures(
            IReadOnlyDictionary<string, StarkTypeSymbol> expected,
            IReadOnlyDictionary<string, StarkTypeSymbol> actual)
        {
            if (expected.Count != actual.Count)
            {
                return false;
            }

            foreach (var (name, expectedType) in expected)
            {
                if (!actual.TryGetValue(name, out var actualType)
                    || !HasSameLowerableCaptureType(expectedType, actualType))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasSameLowerableCaptureType(StarkTypeSymbol left, StarkTypeSymbol right)
        {
            return left == right
                || string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal)
                && string.Equals(left.NamedType, right.NamedType, StringComparison.Ordinal);
        }

        private bool TryApplySwitchCaptureStorageNames(
            LowerableSwitchLabel label,
            IReadOnlyDictionary<string, string> storageNames,
            out LowerableSwitchLabel rewrittenLabel)
        {
            rewrittenLabel = label;
            string? captureStorageName = null;
            if (label.CaptureName is { } captureName
                && !storageNames.TryGetValue(captureName, out captureStorageName))
            {
                return false;
            }

            LowerableAggregatePattern? aggregatePattern = null;
            if (label.AggregatePattern is { } originalAggregatePattern
                && !TryApplyAggregateCaptureStorageNames(originalAggregatePattern, storageNames, out aggregatePattern))
            {
                return false;
            }

            rewrittenLabel = label with
            {
                CaptureStorageName = captureStorageName,
                AggregatePattern = aggregatePattern ?? label.AggregatePattern
            };
            return true;
        }

        private bool TryApplyAggregateCaptureStorageNames(
            LowerableAggregatePattern aggregatePattern,
            IReadOnlyDictionary<string, string> storageNames,
            out LowerableAggregatePattern rewrittenPattern)
        {
            rewrittenPattern = aggregatePattern;
            string? wholeCaptureStorageName = null;
            if (aggregatePattern.WholeCaptureName is { } wholeCaptureName
                && !storageNames.TryGetValue(wholeCaptureName, out wholeCaptureStorageName))
            {
                return false;
            }

            var rewrittenFields = aggregatePattern.FieldPatterns.ToArray();
            for (var index = 0; index < rewrittenFields.Length; index++)
            {
                var fieldPattern = rewrittenFields[index];
                if (fieldPattern.Kind == AggregatePatternFieldKind.Capture)
                {
                    if (fieldPattern.CaptureName is null
                        || !storageNames.TryGetValue(fieldPattern.CaptureName, out var fieldStorageName))
                    {
                        return false;
                    }

                    rewrittenFields[index] = fieldPattern with { CaptureStorageName = fieldStorageName };
                    continue;
                }

                if (fieldPattern.Kind == AggregatePatternFieldKind.Nested)
                {
                    if (fieldPattern.NestedPattern is null
                        || !TryApplyAggregateCaptureStorageNames(fieldPattern.NestedPattern, storageNames, out var nestedPattern))
                    {
                        return false;
                    }

                    rewrittenFields[index] = fieldPattern with { NestedPattern = nestedPattern };
                }
            }

            rewrittenPattern = aggregatePattern with
            {
                FieldPatterns = rewrittenFields,
                WholeCaptureStorageName = wholeCaptureStorageName
            };
            return true;
        }

        private bool TryRegisterSwitchCaptureLocal(
            string sourceName,
            StarkTypeSymbol type,
            HashSet<string> seenCaptures,
            out string storageName)
        {
            storageName = string.Empty;
            if (!seenCaptures.Add(sourceName))
            {
                return false;
            }

            storageName = AllocateLocalStorageName(sourceName);
            RegisterLocal(storageName, type, storageClass: "match", isMutable: false, isConstant: false);
            InitializeRuntimeDropState(storageName, type, isActive: false);
            return true;
        }

        private bool EmitSwitchMatchTransition(LowerableSwitchLabel label, MidLevelIrOperand switchValue, int targetBlockId, int nextTarget)
        {
            IReadOnlyList<PendingSwitchBinding> bindings = label.CaptureName is null
                ? []
                : [new PendingSwitchBinding(label.CaptureName, label.CaptureStorageName ?? label.CaptureName, switchValue)];

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
                bindings.Add(new PendingSwitchBinding(
                    wholeCaptureName,
                    aggregatePattern.WholeCaptureStorageName ?? wholeCaptureName,
                    switchValue));
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
                    bindings.Add(new PendingSwitchBinding(
                        fieldPattern.CaptureName!,
                        fieldPattern.CaptureStorageName ?? fieldPattern.CaptureName!,
                        fieldValue,
                        RequiresRuntimeDrop(fieldPattern.FieldType) ? switchValue : null));
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
                var capture = new MidLevelIrLocalOperand(binding.StorageName, binding.Source.Type);
                Emit(MidLevelIrStatementKind.StorageLive, binding.StorageName, binding.SourceName, binding.Source.Type);
                EmitOperandAssignment(capture, binding.Source, binding.Source.Text);
                SetRuntimeDropState(binding.StorageName, isActive: true);
                if (binding.RuntimeMoveSource is not null)
                {
                    RecordMoveFromOperand(binding.RuntimeMoveSource, binding.RuntimeMoveSource.Type);
                }
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
                IReadOnlyList<LowerableSwitchLabel> labels,
                StarkTypeSymbol switchType,
                out IReadOnlyList<LowerableSwitchLabel> registeredLabels)
            {
                return _builder.TryRegisterSwitchCaptureLocalsCore(labels, switchType, out registeredLabels);
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
