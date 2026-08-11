using System.Text.Json.Nodes;

namespace Stark.ReleaseTools;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var command = CommandLine.Parse(args);
            JsonNode? result = command.Command switch
            {
                "create-archive" => ArchiveCreator.Run(command),
                "extract-archive" => ArchiveExtractor.Extract(command),
                "inventory-tree" => ArchiveExtractor.Inventory(command),
                "validate-config" => ReleaseConfiguration.Run(command),
                "prepare-managed-licenses" => ManagedLicenseEvidence.Run(command),
                "prepare-release" => ReleasePlanPreparer.Run(command),
                "inspect-candidate" => CandidateIdentity.Run(command),
                "candidate-evidence" => CandidateEvidenceBinder.Run(command),
                "validate-managed-restore" => ManagedRestoreValidator.Run(command),
                "validate-stage" => ReleaseStageValidator.Run(command),
                "qualify-private-backend" => PrivateBackendQualifier.Run(command),
                "verify-private-backend-bundle" => PrivateBackendBundleVerifier.Run(command),
                "compare-candidates" => CandidateComparer.Run(command),
                "reconcile-github-release" => await GitHubReleaseReconciler.RunAsync(command),
                _ => throw new ReleaseToolException($"Unknown release-tool command '{command.Command}'."),
            };

            if (result is not null)
            {
                Console.Out.WriteLine(JsonIO.Compact(result));
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"release tool failed: {exception.Message}");
            return 1;
        }
    }
}
