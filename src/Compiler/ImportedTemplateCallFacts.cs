namespace Stark.Compiler;

internal static class ImportedTemplateCallFacts
{
    public static IReadOnlyList<TypedFunctionSignature> GetReachableCallSignatures(
        ImportedFunctionTemplateSummary importedTemplate)
    {
        var boundCalls = importedTemplate.BoundOperations
            .Select(static summary => summary.Operation)
            .Select(static operation => operation switch
            {
                BoundDirectCallOperation directCall => directCall.Signature,
                BoundMemberCallOperation memberCall => memberCall.Signature,
                _ => null
            })
            .Where(static signature => signature is not null)
            .Cast<TypedFunctionSignature>()
            .ToArray();
        var functionAddressSignatures = importedTemplate.FunctionAddresses
            .Select(static functionAddress => functionAddress.Signature)
            .ToArray();
        if (boundCalls.Length != 0)
        {
            return boundCalls
                .Concat(functionAddressSignatures)
                .ToArray();
        }

        return importedTemplate.DirectCalls.Select(static call => call.Signature)
            .Concat(importedTemplate.MemberCalls.Select(static call => call.Signature))
            .Concat(functionAddressSignatures)
            .ToArray();
    }

    public static bool CanMaterializeClosedBody(string functionName, LoadedPackageImageFacts facts)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        return Visit(functionName);

        bool Visit(string name)
        {
            if (!visited.Add(name)) return true;
            if (!facts.FunctionTemplates.TryGetValue(name, out var template) || template.TypedBody is null) return false;
            foreach (var call in GetReachableCallSignatures(template))
            {
                var callee = call.TemplateName ?? call.Name;
                if (facts.FunctionSignatures.ContainsKey(callee)) continue;
                if (!facts.FunctionEffects.TryGetValue(callee, out var effects)) continue;
                if (effects.IsFfi) continue;
                // A private definition cannot be called through the library ABI.
                // Keep an unsupported dependency inside its original native wrapper.
                if (effects.IsCold || effects.BackendOptimizationMode == ModuleBackendOptimizationMode.Opaque
                    || !Visit(callee)) return false;
            }
            return true;
        }
    }

}
