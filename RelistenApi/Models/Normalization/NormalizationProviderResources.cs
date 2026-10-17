using Newtonsoft.Json;

namespace Relisten.Api.Models.Normalization;

/// <summary>Constructs retained resource keys whose exact encoding is part of their durable identity.</summary>
public static class NormalizationProviderResources
{
    /// <summary>
    /// Preserves the original Local capture tuple encoding, including its Unicode and punctuation
    /// escaping. Changing the serializer would create different resources for existing directories.
    /// </summary>
    public static NormalizationResourceKey LocalDirectory(string metadataRoot, string directory) =>
        new(
            "local",
            "directory_metadata",
            JsonConvert.SerializeObject(new[] { metadataRoot, directory })
        );
}
