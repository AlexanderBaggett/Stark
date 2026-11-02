namespace Stark.Compiler;

internal static class GenericTemplatePublicationPolicy
{
    public static bool HasPublishedApiVisibility(StarkVisibility visibility)
    {
        return visibility is StarkVisibility.Public or StarkVisibility.Export;
    }

    public static bool ShouldPublishTypedTemplateBody(
        StarkVisibility visibility,
        bool hasBody,
        bool isGeneric,
        bool isExplicitInline = false)
    {
        return hasBody
            && ((HasPublishedApiVisibility(visibility) && isGeneric)
                || ((HasPublishedApiVisibility(visibility) || visibility == StarkVisibility.Internal)
                    && isExplicitInline));
    }

    public static bool ShouldPublishTypedTemplateBody(
        DeclaredFunctionSyntax declaration,
        TypedFunctionSignature function)
    {
        return ShouldPublishTypedTemplateBody(
            declaration.Visibility,
            declaration.HasBody,
            function.IsGeneric,
            declaration.Modifiers.Any(static modifier => modifier.INLINE() is not null));
    }

    public static bool ShouldPublishTypedTemplateBody(
        StarkVisibility visibility,
        FunctionDeclarationModel declaration,
        TypedFunctionSignature function)
    {
        return ShouldPublishTypedTemplateBody(
            visibility,
            declaration.HasBody,
            function.IsGeneric,
            declaration.Modifiers.HasExplicitInlinePreference
                && declaration.Modifiers.InlinePreference == InlinePreference.Inline);
    }
}
