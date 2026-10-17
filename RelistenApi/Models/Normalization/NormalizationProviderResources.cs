using System;
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

    /// <summary>
    /// Resolves a canonical Local directory's required root identity. It does not select a
    /// retained revision or assert that the root currently contains that directory account.
    /// </summary>
    public static NormalizationResourceKey LocalRootForDirectory(NormalizationResourceKey directory)
    {
        if (directory.Provider != "local" || directory.ResourceType != "directory_metadata")
        {
            throw new ArgumentException(
                "A Local directory resource is required.",
                nameof(directory)
            );
        }
        string[]? parts;
        try
        {
            parts = JsonConvert.DeserializeObject<string[]>(directory.ProviderKey);
        }
        catch (JsonException)
        {
            throw new ArgumentException(
                "The Local directory identity is invalid.",
                nameof(directory)
            );
        }
        if (
            parts is not { Length: 2 }
            || string.IsNullOrWhiteSpace(parts[0])
            || string.IsNullOrWhiteSpace(parts[1])
            || LocalDirectory(parts[0], parts[1]) != directory
        )
        {
            throw new ArgumentException(
                "The Local directory identity is invalid.",
                nameof(directory)
            );
        }
        return new("local", "metadata_root", parts[0]);
    }
}
