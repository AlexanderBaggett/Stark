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
        bool isGeneric)
    {
        return HasPublishedApiVisibility(visibility)
            && hasBody
            && isGeneric;
    }

    public static bool ShouldPublishTypedTemplateBody(
        DeclaredFunctionSyntax declaration,
        TypedFunctionSignature function)
    {
        return ShouldPublishTypedTemplateBody(
            declaration.Visibility,
            declaration.HasBody,
            function.IsGeneric);
    }

    public static bool ShouldPublishTypedTemplateBody(
        StarkVisibility visibility,
        FunctionDeclarationModel declaration,
        TypedFunctionSignature function)
    {
        return ShouldPublishTypedTemplateBody(
            visibility,
            declaration.HasBody,
            function.IsGeneric);
    }
}
