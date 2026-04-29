using Stark.Parsing;

namespace Stark.Compiler;

internal sealed partial class MidLevelIrLowerer
{
    private sealed partial class FunctionMirBuilder
    {
        private bool TryResolveAssignmentTarget(StarkParser.UnaryExpressionContext expression, out PlaceTarget target)
        {
            return _placeLowerer.TryResolveAssignmentTarget(expression, out target);
        }

        private MidLevelIrOperand ReadPlace(PlaceTarget target)
        {
            return _placeLowerer.ReadPlace(target);
        }

        private LoweredAssignment BuildAssignment(PlaceTarget target, MidLevelIrOperand value, string text)
        {
            return _placeLowerer.BuildAssignment(target, value, text);
        }

        private MidLevelIrOperand? BuildAddress(PlaceTarget target)
        {
            return _placeLowerer.BuildAddress(target);
        }

        private bool TryBuildDynamicStorageLengthUpdate(PlaceTarget target, out DynamicStorageLengthUpdate update)
        {
            return _placeLowerer.TryBuildDynamicStorageLengthUpdate(target, out update);
        }

        private bool TryResolveAssignmentTargetCore(StarkParser.UnaryExpressionContext expression, out PlaceTarget target)
        {
            target = default!;

            if (expression.powerExpression() is not { } powerExpression
                || powerExpression.unaryExpression() is not null
                || powerExpression.postfixExpression() is not { } postfixExpression)
            {
                return false;
            }

            if (postfixExpression.primaryExpression().expression() is { } groupedExpression
                && TryExtractSimpleUnaryExpression(groupedExpression, out var groupedUnary))
            {
                if (TryResolvePointerBackedAssignmentTarget(postfixExpression, groupedUnary, out target))
                {
                    return true;
                }

                return TryResolveAssignmentTargetCore(groupedUnary, out target);
            }

            var hasCallPostfix = postfixExpression.postfixPart().Any(static part => part.argumentList() is not null);
            if (hasCallPostfix && TryResolveCallBackedAssignmentTarget(postfixExpression, out target))
            {
                return true;
            }

            if (!TryInitializePostfixState(postfixExpression.primaryExpression(), out var root, out var currentName))
            {
                return false;
            }

            var path = new List<PlacePathSegment>();
            var rootProjectionType = root is null ? null : ProjectRootType(root);
            var currentType = rootProjectionType;
            var supportsAddressModel = SupportsAddressModel(root);
            var usesAddressModel = false;

            foreach (var postfixPart in postfixExpression.postfixPart())
            {
                if (postfixPart.argumentList() is not null)
                {
                    return false;
                }

                if (currentType is null)
                {
                    var memberName = postfixPart.Identifier()?.GetText();
                    if (currentName is null || memberName is null)
                    {
                        return false;
                    }

                    var qualifiedName = $"{currentName}.{memberName}";
                    root = TryResolveNamedValueOperand(qualifiedName);
                    if (root is null)
                    {
                        currentName = qualifiedName;
                        continue;
                    }

                    currentName = null;
                    rootProjectionType = ProjectRootType(root);
                    currentType = rootProjectionType;
                    supportsAddressModel = SupportsAddressModel(root);
                    continue;
                }

                if (postfixPart.expressionList() is { } expressionList)
                {
                    foreach (var indexExpression in expressionList.expression())
                    {
                        if (currentType.Kind == StarkTypeKind.FixedArray
                            && TryResolveConstantArrayIndex(currentType, indexExpression, out var constantIndex, out var elementType))
                        {
                            elementType = ProjectAddressProjectionType(currentType, elementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.ConstantArrayIndex,
                                FieldName: null,
                                ConstantIndex: constantIndex,
                                IndexOperand: null,
                                ParentType: currentType,
                                SegmentType: elementType));
                            currentType = elementType;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.FixedArray && supportsAddressModel)
                        {
                            if (currentType.ElementType is null)
                            {
                                return false;
                            }

                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            var dynamicElementType = ProjectAddressProjectionType(currentType, currentType.ElementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.DynamicArrayIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: dynamicElementType));
                            currentType = dynamicElementType;
                            usesAddressModel = true;
                            supportsAddressModel = true;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.Slice && currentType.ElementType is not null)
                        {
                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            var sliceElementType = ProjectAddressProjectionType(currentType, currentType.ElementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.SliceIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: sliceElementType));
                            currentType = sliceElementType;
                            usesAddressModel = true;
                            supportsAddressModel = true;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.Dynamic && currentType.ElementType is not null)
                        {
                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            var dynamicStorageElementType = ProjectAddressProjectionType(currentType, currentType.ElementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.DynamicStorageIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: dynamicStorageElementType));
                            currentType = dynamicStorageElementType;
                            usesAddressModel = true;
                            supportsAddressModel = true;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.RawPointer && currentType.ElementType is not null)
                        {
                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            path.Add(new PlacePathSegment(
                                PlacePathKind.RawPointerIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: currentType.ElementType));
                            currentType = currentType.ElementType;
                            usesAddressModel = true;
                            supportsAddressModel = true;
                            continue;
                        }

                        return false;
                    }

                    continue;
                }

                if (!TryResolveField(currentType, postfixPart.Identifier().GetText(), out var field, out var fieldIndex))
                {
                    return false;
                }

                var projectedType = ProjectAddressProjectionType(currentType, field.Type);
                path.Add(new PlacePathSegment(
                    PlacePathKind.Field,
                    postfixPart.Identifier().GetText(),
                    fieldIndex,
                    IndexOperand: null,
                    ParentType: currentType,
                    SegmentType: projectedType));
                currentType = projectedType;
                supportsAddressModel = supportsAddressModel || usesAddressModel;
            }

            if (root is null)
            {
                if (currentName is null)
                {
                    return false;
                }

                root = ResolveNamedOperand(currentName);
                if (root is null)
                {
                    return false;
                }

                rootProjectionType = ProjectRootType(root);
                currentType = rootProjectionType;
            }

            if (IsBorrowParameterRoot(root)
                || ShouldUseHeapProjectionAddressModel(root, path))
            {
                usesAddressModel = true;
            }

            var targetType = currentType ?? rootProjectionType ?? root.Type;
            target = new PlaceTarget(
                root.Text,
                RootAddress: null,
                RootValue: null,
                rootProjectionType ?? root.Type,
                targetType,
                path,
                usesAddressModel,
                GetAddressMutability(root));
            return true;
        }

        private bool TryResolveCallBackedAssignmentTarget(
            StarkParser.PostfixExpressionContext postfixExpression,
            out PlaceTarget target)
        {
            target = default!;

            if (!TryLowerCallPrefix(
                    postfixExpression,
                    out var call,
                    out var callResult,
                    out var nextPostfixIndex)
                || call.SourceReturnType is not { } sourceReturnType
                || sourceReturnType.BorrowKind == StarkBorrowKind.None)
            {
                return false;
            }

            var sourceValueType = StarkTypeSymbols.BorrowReturnValueType(sourceReturnType);
            var path = new List<PlacePathSegment>();
            var currentType = StarkTypeSymbols.IsPointerBackedBorrowReturn(sourceReturnType)
                ? sourceValueType
                : callResult.Type;

            if (!TryAppendPostfixPlacePath(postfixExpression, nextPostfixIndex, path, ref currentType))
            {
                return false;
            }

            if (StarkTypeSymbols.IsPointerBackedBorrowReturn(sourceReturnType))
            {
                target = new PlaceTarget(
                    RootName: null,
                    RootAddress: callResult,
                    RootValue: null,
                    RootType: sourceValueType,
                    Type: currentType,
                    Path: path,
                    UsesAddressModel: true,
                    IsAddressMutable: sourceReturnType.IsMutableView);
                return true;
            }

            if (path.Count == 0)
            {
                return false;
            }

            target = new PlaceTarget(
                RootName: null,
                RootAddress: null,
                RootValue: callResult,
                RootType: callResult.Type,
                Type: currentType,
                Path: path,
                UsesAddressModel: true,
                IsAddressMutable: sourceReturnType.IsMutableView);
            return true;
        }

        private bool TryLowerCallPrefix(
            StarkParser.PostfixExpressionContext postfixExpression,
            out MidLevelIrCallRValue call,
            out MidLevelIrOperand callResult,
            out int nextPostfixIndex)
        {
            call = default!;
            callResult = default!;
            nextPostfixIndex = -1;

            if (!TryInitializePostfixState(postfixExpression.primaryExpression(), out var currentValue, out var currentName))
            {
                return false;
            }

            PlaceTarget? currentPlace = currentValue is null ? null : CreateRootPlaceTarget(currentValue);
            var postfixParts = postfixExpression.postfixPart();
            for (var index = 0; index < postfixParts.Length; index++)
            {
                var postfixPart = postfixParts[index];
                if (postfixPart.argumentList() is { } argumentList)
                {
                    if (currentName is null
                        || !TryBuildCall(currentName, argumentList, $"{currentName}{argumentList.GetText()}", out call))
                    {
                        return false;
                    }

                    callResult = EmitTemporary(call, "call")!;
                    if (callResult is null)
                    {
                        return false;
                    }

                    nextPostfixIndex = index + 1;
                    return true;
                }

                if (postfixPart.expressionList() is { } expressionList)
                {
                    if (currentValue is null)
                    {
                        return false;
                    }

                    currentValue = LowerIndexAccess(currentValue, expressionList);
                    currentPlace = null;
                    if (currentValue is null)
                    {
                        return false;
                    }

                    continue;
                }

                var memberName = postfixPart.Identifier()?.GetText();
                if (memberName is null)
                {
                    return false;
                }

                if (currentValue is not null
                    && index + 1 < postfixParts.Length
                    && postfixParts[index + 1].argumentList() is { } memberArguments)
                {
                    if (!(TryBuildPublishedMemberCall(currentValue, currentPlace, memberArguments, $"{currentValue.Text}.{memberName}{memberArguments.GetText()}", out call)
                          || TryBuildMemberCall(currentValue, currentPlace, memberName, memberArguments, $"{currentValue.Text}.{memberName}{memberArguments.GetText()}", out call)))
                    {
                        return false;
                    }

                    callResult = EmitTemporary(call, "call")!;
                    if (callResult is null)
                    {
                        return false;
                    }

                    nextPostfixIndex = index + 2;
                    return true;
                }

                if (currentValue is not null)
                {
                    currentPlace = currentPlace is not null && TryAppendFieldPlaceTarget(currentPlace, memberName, out var fieldPlace)
                        ? fieldPlace
                        : null;
                    currentValue = currentPlace is { UsesAddressModel: true }
                        ? ReadPlace(currentPlace)
                        : TryLowerPublishedFieldAccess(currentValue, postfixPart, out var publishedFieldAccess)
                            ? publishedFieldAccess
                            : LowerFieldAccess(currentValue, memberName);
                    if (currentValue is null)
                    {
                        return false;
                    }

                    continue;
                }

                currentName = currentName is null ? memberName : $"{currentName}.{memberName}";
            }

            return false;
        }

        private bool TryAppendPostfixPlacePath(
            StarkParser.PostfixExpressionContext postfixExpression,
            int startIndex,
            List<PlacePathSegment> path,
            ref StarkTypeSymbol currentType)
        {
            var postfixParts = postfixExpression.postfixPart();
            for (var index = startIndex; index < postfixParts.Length; index++)
            {
                var postfixPart = postfixParts[index];
                if (postfixPart.argumentList() is not null)
                {
                    return false;
                }

                if (postfixPart.expressionList() is { } expressionList)
                {
                    foreach (var indexExpression in expressionList.expression())
                    {
                        if (currentType.Kind == StarkTypeKind.FixedArray
                            && TryResolveConstantArrayIndex(currentType, indexExpression, out var constantIndex, out var elementType))
                        {
                            elementType = ProjectAddressProjectionType(currentType, elementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.ConstantArrayIndex,
                                FieldName: null,
                                ConstantIndex: constantIndex,
                                IndexOperand: null,
                                ParentType: currentType,
                                SegmentType: elementType));
                            currentType = elementType;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.FixedArray && currentType.ElementType is not null)
                        {
                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            var dynamicElementType = ProjectAddressProjectionType(currentType, currentType.ElementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.DynamicArrayIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: dynamicElementType));
                            currentType = dynamicElementType;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.Slice && currentType.ElementType is not null)
                        {
                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            var sliceElementType = ProjectAddressProjectionType(currentType, currentType.ElementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.SliceIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: sliceElementType));
                            currentType = sliceElementType;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.Dynamic && currentType.ElementType is not null)
                        {
                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            var dynamicStorageElementType = ProjectAddressProjectionType(currentType, currentType.ElementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.DynamicStorageIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: dynamicStorageElementType));
                            currentType = dynamicStorageElementType;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.RawPointer && currentType.ElementType is not null)
                        {
                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            path.Add(new PlacePathSegment(
                                PlacePathKind.RawPointerIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: currentType.ElementType));
                            currentType = currentType.ElementType;
                            continue;
                        }

                        return false;
                    }

                    continue;
                }

                var memberName = postfixPart.Identifier()?.GetText();
                if (memberName is null
                    || !TryResolveField(currentType, memberName, out var field, out var fieldIndex))
                {
                    return false;
                }

                var projectedType = ProjectAddressProjectionType(currentType, field.Type);
                path.Add(new PlacePathSegment(
                    PlacePathKind.Field,
                    memberName,
                    fieldIndex,
                    IndexOperand: null,
                    ParentType: currentType,
                    SegmentType: projectedType));
                currentType = projectedType;
            }

            return true;
        }

        private bool TryResolvePointerBackedAssignmentTarget(
            StarkParser.PostfixExpressionContext postfixExpression,
            StarkParser.UnaryExpressionContext groupedUnary,
            out PlaceTarget target)
        {
            target = default!;

            if (!TryInitializePointerPlaceRoot(groupedUnary, out var rootAddress, out var rootType, out var rootAddressIsMutable))
            {
                return false;
            }

            var path = new List<PlacePathSegment>();
            var currentType = rootType;

            foreach (var postfixPart in postfixExpression.postfixPart())
            {
                if (postfixPart.argumentList() is not null)
                {
                    return false;
                }

                if (postfixPart.expressionList() is { } expressionList)
                {
                    foreach (var indexExpression in expressionList.expression())
                    {
                        if (currentType.Kind == StarkTypeKind.FixedArray
                            && TryResolveConstantArrayIndex(currentType, indexExpression, out var constantIndex, out var elementType))
                        {
                            elementType = ProjectAddressProjectionType(currentType, elementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.ConstantArrayIndex,
                                FieldName: null,
                                ConstantIndex: constantIndex,
                                IndexOperand: null,
                                ParentType: currentType,
                                SegmentType: elementType));
                            currentType = elementType;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.FixedArray && currentType.ElementType is not null)
                        {
                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            var dynamicElementType = ProjectAddressProjectionType(currentType, currentType.ElementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.DynamicArrayIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: dynamicElementType));
                            currentType = dynamicElementType;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.Slice && currentType.ElementType is not null)
                        {
                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            var sliceElementType = ProjectAddressProjectionType(currentType, currentType.ElementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.SliceIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: sliceElementType));
                            currentType = sliceElementType;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.Dynamic && currentType.ElementType is not null)
                        {
                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            var dynamicStorageElementType = ProjectAddressProjectionType(currentType, currentType.ElementType);
                            path.Add(new PlacePathSegment(
                                PlacePathKind.DynamicStorageIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: dynamicStorageElementType));
                            currentType = dynamicStorageElementType;
                            continue;
                        }

                        if (currentType.Kind == StarkTypeKind.RawPointer && currentType.ElementType is not null)
                        {
                            var indexOperand = LowerExpressionToOperand(indexExpression);
                            if (indexOperand is null || indexOperand.Type.Kind != StarkTypeKind.Integer)
                            {
                                return false;
                            }

                            path.Add(new PlacePathSegment(
                                PlacePathKind.RawPointerIndex,
                                FieldName: null,
                                ConstantIndex: null,
                                IndexOperand: indexOperand,
                                ParentType: currentType,
                                SegmentType: currentType.ElementType));
                            currentType = currentType.ElementType;
                            continue;
                        }

                        return false;
                    }

                    continue;
                }

                var memberName = postfixPart.Identifier()?.GetText();
                if (memberName is null
                    || !TryResolveField(currentType, memberName, out var field, out var fieldIndex))
                {
                    return false;
                }

                var projectedType = ProjectAddressProjectionType(currentType, field.Type);
                path.Add(new PlacePathSegment(
                    PlacePathKind.Field,
                    memberName,
                    fieldIndex,
                    IndexOperand: null,
                    ParentType: currentType,
                    SegmentType: projectedType));
                currentType = projectedType;
            }

            target = new PlaceTarget(
                RootName: null,
                RootAddress: rootAddress,
                RootValue: null,
                RootType: rootType,
                Type: currentType,
                Path: path,
                UsesAddressModel: true,
                IsAddressMutable: rootAddressIsMutable);
            return true;
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.ExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;

            if (expression.assignmentExpression() is not { } assignmentExpression
                || assignmentExpression.unaryExpression() is not null
                || assignmentExpression.assignmentOperator() is not null
                || assignmentExpression.conditionalExpression() is not { } conditionalExpression
                || conditionalExpression.expression().Length != 0)
            {
                return false;
            }

            return TryExtractSimpleUnaryExpression(conditionalExpression.logicalOrExpression(), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.LogicalOrExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.logicalAndExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.logicalAndExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.LogicalAndExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.bitwiseOrExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.bitwiseOrExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.BitwiseOrExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.bitwiseXorExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.bitwiseXorExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.BitwiseXorExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.bitwiseAndExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.bitwiseAndExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.BitwiseAndExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.equalityExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.equalityExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.EqualityExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.relationalExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.relationalExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.RelationalExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.shiftExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.shiftExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.ShiftExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.additiveExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.additiveExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.AdditiveExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.multiplicativeExpression().Length == 1
                && TryExtractSimpleUnaryExpression(expression.multiplicativeExpression(0), out unaryExpression);
        }

        private static bool TryExtractSimpleUnaryExpression(
            StarkParser.MultiplicativeExpressionContext expression,
            out StarkParser.UnaryExpressionContext unaryExpression)
        {
            unaryExpression = default!;
            return expression.unaryExpression().Length == 1
                && (unaryExpression = expression.unaryExpression(0)) is not null;
        }

        private MidLevelIrOperand ReadPlaceCore(PlaceTarget target)
        {
            if (target.UsesAddressModel)
            {
                var address = BuildAddressCore(target);
                if (address is null)
                {
                    MarkUnsupported();
                    return target.RootName is not null
                        ? ResolveNamedOperand(target.RootName) ?? new MidLevelIrLocalOperand(target.RootName, target.RootType)
                        : new MidLevelIrZeroInitializerOperand(target.Type);
                }

                return EmitTemporary(
                           new MidLevelIrLoadIndirectRValue(address, target.Type, $"{target.RootName}:load"),
                           "load")
                       ?? address;
            }

            if (target.RootName is null)
            {
                MarkUnsupported();
                return new MidLevelIrZeroInitializerOperand(target.Type);
            }

            var current = ResolveNamedOperand(target.RootName) ?? new MidLevelIrLocalOperand(target.RootName, target.RootType);
            foreach (var segment in target.Path)
            {
                var extracted = segment.Kind == PlacePathKind.ConstantArrayIndex
                    ? LowerConstantIndexAccess(current, segment.ConstantIndex!.Value, segment.SegmentType)
                    : LowerFieldAccess(current, segment.FieldName!);
                if (extracted is null)
                {
                    MarkUnsupported();
                    return current;
                }

                current = extracted;
            }

            return current;
        }

        private LoweredAssignment BuildAssignmentCore(PlaceTarget target, MidLevelIrOperand value, string text)
        {
            var assignedValue = CoerceOperand(value, target.Type) ?? value;
            if (target.UsesAddressModel)
            {
                var address = BuildAddressCore(target);
                return new LoweredAssignment(
                    text,
                    TargetName: null,
                    target.Type,
                    DirectValue: null,
                    ResultValue: assignedValue,
                    Address: address,
                    ReplacesWholeValue: false);
            }

            if (target.Path.Count == 0)
            {
                if (target.RootName is null)
                {
                    MarkUnsupported();
                    return new LoweredAssignment(
                        text,
                        TargetName: null,
                        target.RootType,
                        DirectValue: null,
                        ResultValue: assignedValue,
                        Address: null,
                        ReplacesWholeValue: false);
                }

                return new LoweredAssignment(
                    text,
                    target.RootName,
                    target.RootType,
                    new MidLevelIrUseRValue(assignedValue),
                    assignedValue,
                    Address: null,
                    ReplacesWholeValue: true);
            }

            if (target.RootName is null)
            {
                MarkUnsupported();
                return new LoweredAssignment(
                    text,
                    TargetName: null,
                    target.RootType,
                    DirectValue: null,
                    ResultValue: assignedValue,
                    Address: null,
                    ReplacesWholeValue: false);
            }

            var root = ResolveNamedOperand(target.RootName) ?? new MidLevelIrLocalOperand(target.RootName, target.RootType);
            var updatedRoot = ApplyAggregatePathUpdate(root, target.Path, 0, assignedValue, text);
            return new LoweredAssignment(
                text,
                target.RootName,
                target.RootType,
                updatedRoot is null ? null : new MidLevelIrUseRValue(updatedRoot),
                assignedValue,
                Address: null,
                ReplacesWholeValue: false);
        }

        private MidLevelIrOperand? ApplyAggregatePathUpdate(
            MidLevelIrOperand aggregate,
            IReadOnlyList<PlacePathSegment> path,
            int depth,
            MidLevelIrOperand value,
            string text)
        {
            var segment = path[depth];
            if (depth == path.Count - 1)
            {
                var coercedValue = CoerceOperand(value, segment.SegmentType);
                if (coercedValue is null)
                {
                    return null;
                }

                return segment.Kind == PlacePathKind.ConstantArrayIndex
                    ? EmitTemporary(
                        new MidLevelIrInsertIndexRValue(
                            aggregate,
                            segment.ConstantIndex!.Value,
                            coercedValue,
                            aggregate.Type,
                            text),
                        "setindex")
                    : EmitTemporary(
                        new MidLevelIrInsertFieldRValue(
                            aggregate,
                            segment.FieldName!,
                            segment.ConstantIndex!.Value,
                            coercedValue,
                            aggregate.Type,
                            text),
                        "setfield");
            }

            var nested = segment.Kind == PlacePathKind.ConstantArrayIndex
                ? LowerConstantIndexAccess(aggregate, segment.ConstantIndex!.Value, segment.SegmentType)
                : LowerFieldAccess(aggregate, segment.FieldName!);
            if (nested is null)
            {
                return null;
            }

            var updatedNested = ApplyAggregatePathUpdate(nested, path, depth + 1, value, text);
            if (updatedNested is null)
            {
                return null;
            }

            return segment.Kind == PlacePathKind.ConstantArrayIndex
                ? EmitTemporary(
                    new MidLevelIrInsertIndexRValue(
                        aggregate,
                        segment.ConstantIndex!.Value,
                        updatedNested,
                        aggregate.Type,
                        text),
                    "setindex")
                : EmitTemporary(
                    new MidLevelIrInsertFieldRValue(
                        aggregate,
                        segment.FieldName!,
                        segment.ConstantIndex!.Value,
                        updatedNested,
                        aggregate.Type,
                        text),
                    "setfield");
        }

        private MidLevelIrOperand? BuildAddressCore(PlaceTarget target)
        {
            MidLevelIrOperand? currentValue = target.RootValue ?? (target.RootName is null ? null : ResolveNamedOperand(target.RootName));
            var currentAddressIsMutable = target.IsAddressMutable;
            MidLevelIrOperand? currentAddress = target.RootAddress
                ?? currentValue switch
                {
                    MidLevelIrLocalOperand local => CreateAddressOfLocal(local.Name, target.RootType),
                    MidLevelIrParameterOperand parameter => CreateAddressOfParameter(parameter.Name, target.RootType),
                    MidLevelIrGlobalOperand global => CreateAddressOfGlobal(global.Name, target.RootType),
                    _ => null
                };
            var currentType = target.RootType;

            foreach (var segment in target.Path)
            {
                switch (segment.Kind)
                {
                    case PlacePathKind.Field:
                        if (currentAddress is null)
                        {
                            return null;
                        }

                        var fieldAddressIsMutable = currentAddressIsMutable && CanMutateThroughType(segment.SegmentType);
                        currentAddress = EmitTemporary(
                            new MidLevelIrFieldAddressRValue(
                                currentAddress,
                                currentType,
                                segment.FieldName!,
                                segment.ConstantIndex!.Value,
                                AddressType(segment.SegmentType, fieldAddressIsMutable),
                                $"{currentAddress.Text}.{segment.FieldName}"),
                            "addr");
                        currentType = segment.SegmentType;
                        currentAddressIsMutable = fieldAddressIsMutable;
                        currentValue = null;
                        break;
                    case PlacePathKind.ConstantArrayIndex:
                    case PlacePathKind.DynamicArrayIndex:
                        if (currentAddress is null)
                        {
                            return null;
                        }

                        var elementAddressIsMutable = currentAddressIsMutable && CanMutateThroughType(segment.SegmentType);
                        currentAddress = EmitTemporary(
                            new MidLevelIrElementAddressRValue(
                                currentAddress,
                                currentType,
                                segment.IndexOperand,
                                segment.ConstantIndex,
                                AddressType(segment.SegmentType, elementAddressIsMutable),
                                $"{currentAddress.Text}[{segment.ConstantIndex?.ToString() ?? segment.IndexOperand?.Text ?? "?"}]"),
                            "addr");
                        currentType = segment.SegmentType;
                        currentAddressIsMutable = elementAddressIsMutable;
                        currentValue = null;
                        break;
                    case PlacePathKind.RawPointerIndex:
                        var pointerValue = currentValue;
                        if (pointerValue is null && currentAddress is not null)
                        {
                            pointerValue = EmitTemporary(
                                new MidLevelIrLoadIndirectRValue(currentAddress, currentType, $"{currentAddress.Text}:load"),
                                "load");
                        }

                        if (pointerValue is null
                            || pointerValue.Type.Kind != StarkTypeKind.RawPointer
                            || pointerValue.Type.ElementType is null
                            || segment.IndexOperand is null)
                        {
                            return null;
                        }

                        currentAddressIsMutable = pointerValue.Type.IsMutablePointer && CanMutateThroughType(segment.SegmentType);
                        currentAddress = EmitTemporary(
                            new MidLevelIrElementAddressRValue(
                                pointerValue,
                                segment.SegmentType,
                                segment.IndexOperand,
                                ConstantIndex: null,
                                AddressType(segment.SegmentType, currentAddressIsMutable),
                                $"{pointerValue.Text}[{segment.IndexOperand.Text}]"),
                            "addr");
                        currentType = segment.SegmentType;
                        currentValue = null;
                        break;
                    case PlacePathKind.DynamicStorageIndex:
                        var dynamicValue = currentValue;
                        if (dynamicValue is null && currentAddress is not null)
                        {
                            dynamicValue = EmitTemporary(
                                new MidLevelIrLoadIndirectRValue(currentAddress, currentType, $"{currentAddress.Text}:load"),
                                "load");
                        }

                        if (dynamicValue is null
                            || dynamicValue.Type.Kind != StarkTypeKind.Dynamic
                            || dynamicValue.Type.ElementType is null
                            || segment.IndexOperand is null)
                        {
                            return null;
                        }

                        var dataPointerType = StarkTypeSymbols.RawPointer(dynamicValue.Type.ElementType, isMutable: true);
                        var dataPointer = LowerKnownFieldAccess(dynamicValue, "Data", 0, dataPointerType, "Data");
                        currentAddressIsMutable = currentAddressIsMutable
                            && dataPointer.Type.IsMutablePointer
                            && CanMutateThroughType(segment.SegmentType);
                        currentAddress = EmitTemporary(
                            new MidLevelIrElementAddressRValue(
                                dataPointer,
                                segment.SegmentType,
                                segment.IndexOperand,
                                ConstantIndex: null,
                                AddressType(segment.SegmentType, currentAddressIsMutable),
                                $"{dynamicValue.Text}[{segment.IndexOperand.Text}]"),
                            "addr");
                        currentType = segment.SegmentType;
                        currentValue = null;
                        break;
                    case PlacePathKind.SliceIndex:
                        var sliceValue = currentValue;
                        if (sliceValue is null && currentAddress is not null)
                        {
                            sliceValue = EmitTemporary(
                                new MidLevelIrLoadIndirectRValue(currentAddress, currentType, $"{currentAddress.Text}:load"),
                                "load");
                        }

                        if (sliceValue is null || segment.IndexOperand is null)
                        {
                            return null;
                        }

                        var sliceElementAddressIsMutable = currentAddressIsMutable && CanMutateThroughType(segment.SegmentType);
                        currentAddress = EmitTemporary(
                            new MidLevelIrSliceElementAddressRValue(
                                sliceValue,
                                segment.IndexOperand,
                                AddressType(segment.SegmentType, sliceElementAddressIsMutable),
                                $"{sliceValue.Text}[{segment.IndexOperand.Text}]"),
                            "addr");
                        currentType = segment.SegmentType;
                        currentAddressIsMutable = sliceElementAddressIsMutable;
                        currentValue = null;
                        break;
                }

                if (currentAddress is null)
                {
                    return null;
                }
            }

            return currentAddress;
        }

        private bool TryResolveField(StarkTypeSymbol targetType, string memberName, out FieldSymbol field, out int fieldIndex)
        {
            field = default!;
            fieldIndex = -1;

            if (targetType.Kind != StarkTypeKind.Named
                || targetType.NamedType is null
                || !_typeModel.NamedTypes.TryGetValue(targetType.NamedType, out var namedType))
            {
                return false;
            }

            return namedType.TryGetField(memberName, out field, out fieldIndex);
        }

        private MidLevelIrOperand? LowerConstantIndexAccess(MidLevelIrOperand target, int constantIndex, StarkTypeSymbol elementType)
        {
            return EmitTemporary(
                new MidLevelIrExtractIndexRValue(
                    target,
                    constantIndex,
                    elementType,
                    $"{target.Text}[{constantIndex}]"),
                "index");
        }

        private bool TryResolveConstantArrayIndex(
            StarkTypeSymbol targetType,
            StarkParser.ExpressionContext expression,
            out int constantIndex,
            out StarkTypeSymbol elementType)
        {
            constantIndex = -1;
            elementType = StarkTypeSymbols.Error;

            if (targetType.Kind != StarkTypeKind.FixedArray
                || targetType.ElementType is null
                || targetType.FixedLength is not int fixedLength)
            {
                return false;
            }

            if (!_compileTimeEvaluator.TryEvaluateInteger(expression, CurrentModuleName, state: null, activeCalls: null, out var parsed))
            {
                return false;
            }
            if (parsed < 0 || parsed > int.MaxValue)
            {
                return false;
            }

            constantIndex = (int)parsed;
            if (constantIndex >= fixedLength)
            {
                return false;
            }

            elementType = targetType.ElementType;
            return true;
        }

        private static StarkTypeSymbol ProjectAddressProjectionType(StarkTypeSymbol sourceType, StarkTypeSymbol projectedType)
        {
            return sourceType.AccessKind == StarkAccessKind.Frozen
                ? StarkTypeSymbols.FreezeAddressPointeeType(projectedType)
                : projectedType;
        }

        private sealed class PlaceLowerer
        {
            private readonly FunctionMirBuilder _builder;

            public PlaceLowerer(FunctionMirBuilder builder)
            {
                _builder = builder;
            }

            public bool TryResolveAssignmentTarget(
                StarkParser.UnaryExpressionContext expression,
                out PlaceTarget target)
            {
                return _builder.TryResolveAssignmentTargetCore(expression, out target);
            }

            public bool TryResolveImportedTypedTemplateAssignmentTarget(
                ImportedTemplateTypedBodyExpressionSummary expression,
                out PlaceTarget target)
            {
                target = default!;

                if (!_builder.TryResolveImportedTypedTemplateAssignmentTargetCore(expression, out target, out var rootOperand))
                {
                    return false;
                }

                if (!target.UsesAddressModel
                    && rootOperand is not null
                    && _builder.IsBorrowParameterRoot(rootOperand))
                {
                    target = target with { UsesAddressModel = true };
                }

                return true;
            }

            public MidLevelIrOperand ReadPlace(PlaceTarget target)
            {
                return _builder.ReadPlaceCore(target);
            }

            public LoweredAssignment BuildAssignment(PlaceTarget target, MidLevelIrOperand value, string text)
            {
                return _builder.BuildAssignmentCore(target, value, text);
            }

            public MidLevelIrOperand? BuildAddress(PlaceTarget target)
            {
                return _builder.BuildAddressCore(target);
            }

            public bool TryBuildDynamicStorageLengthUpdate(PlaceTarget target, out DynamicStorageLengthUpdate update)
            {
                update = default!;

                if (target.Path.Count == 0)
                {
                    return false;
                }

                var lastSegment = target.Path[^1];
                if (lastSegment.Kind == PlacePathKind.SliceIndex
                    && lastSegment.IndexOperand is not null
                    && target.RootName is not null
                    && target.Path.Count == 1
                    && _builder._dynamicInitSliceProvenanceByLocal.TryGetValue(target.RootName, out var provenance))
                {
                    var start = _builder.CoerceOperand(provenance.StartIndex, NonNegativeI64Type) ?? provenance.StartIndex;
                    var index = _builder.CoerceOperand(lastSegment.IndexOperand, NonNegativeI64Type) ?? lastSegment.IndexOperand;
                    var initializedIndex = _builder.EmitTemporary(
                        new MidLevelIrBinaryRValue(
                            MidLevelIrBinaryOperator.Add,
                            start,
                            index,
                            NonNegativeI64Type,
                            $"{provenance.StartIndex.Text} + {lastSegment.IndexOperand.Text}"),
                        "dynamic_index");
                    if (initializedIndex is null)
                    {
                        return false;
                    }

                    update = new DynamicStorageLengthUpdate(
                        provenance.StorageAddress,
                        provenance.StorageType,
                        initializedIndex);
                    return true;
                }

                if (lastSegment.Kind != PlacePathKind.DynamicStorageIndex
                    || lastSegment.IndexOperand is null
                    || lastSegment.ParentType.Kind != StarkTypeKind.Dynamic)
                {
                    return false;
                }

                var parentTarget = target with
                {
                    Type = lastSegment.ParentType,
                    Path = target.Path.Take(target.Path.Count - 1).ToArray(),
                    UsesAddressModel = true
                };
                var storageAddress = _builder.BuildAddressCore(parentTarget);
                if (storageAddress is null)
                {
                    return false;
                }

                update = new DynamicStorageLengthUpdate(
                    storageAddress,
                    lastSegment.ParentType,
                    lastSegment.IndexOperand);
                return true;
            }
        }
    }
}
