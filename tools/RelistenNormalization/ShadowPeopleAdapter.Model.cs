using System.Security.Cryptography;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Offline trained linear model for diagnostic ranking. The original trainer's
/// feature order and duplicate runtime mapping must agree. No threshold or
/// evaluation attestation is manufactured from these development parameters.
/// </summary>
internal sealed class ShadowPeopleIdentityModel
{
    private static readonly string[] FeatureOrder =
    [
        "name_token_similarity",
        "exact_name",
        "artist_overlap",
        "contact_agreement",
        "profile_agreement",
    ];
    internal string Identity { get; }
    internal PeopleAcceptanceLinearScorer Scorer { get; }

    internal ShadowPeopleIdentityModel(string pinnedModel)
    {
        var separator = pinnedModel.LastIndexOf("#sha256=", StringComparison.Ordinal);
        if (separator <= 0 || pinnedModel.Length - separator != 72)
        {
            throw new ArgumentException(
                "People model requires <models-path>#sha256=<64 hexadecimal digits>."
            );
        }
        var bytes = File.ReadAllBytes(Path.GetFullPath(pinnedModel[..separator]));
        Identity = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!Identity.Equals(pinnedModel[(separator + 8)..], StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("People model differs from the supplied digest.");
        }
        using var document = JsonDocument.Parse(bytes);
        var model = document.RootElement.GetProperty("logistic").GetProperty("final");
        var order = model
            .GetProperty("featureOrder")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();
        var classes = model
            .GetProperty("classes")
            .EnumerateArray()
            .Select(value => value.GetInt32())
            .ToArray();
        var weights = model
            .GetProperty("coefficients")
            .EnumerateArray()
            .Select(value => value.GetDouble())
            .ToArray();
        var intercept = model.GetProperty("intercept").GetDouble();
        var runtime = model.GetProperty("runtimeScorerParameters");
        if (
            model.GetProperty("version").GetString() != "people-five-feature-logistic-v1"
            || model.GetProperty("projectionVersion").GetString()
                != "people-mentions-source-context-five-v1"
            || model.GetProperty("scoreKind").GetString() != "raw_logit"
            || !order.SequenceEqual(FeatureOrder)
            || !classes.SequenceEqual(new[] { 0, 1 })
            || weights.Length != 5
            || !weights.Append(intercept).All(double.IsFinite)
        )
        {
            throw new InvalidDataException("Unsupported people model feature or score contract.");
        }
        var runtimeValues = new[]
        {
            "nameSimilarityWeight",
            "exactNameWeight",
            "artistOverlapWeight",
            "contactAgreementWeight",
            "profileAgreementWeight",
        }.Select(name => runtime.GetProperty(name).GetDouble());
        if (
            !weights.SequenceEqual(runtimeValues)
            || runtime.GetProperty("intercept").GetDouble() != intercept
        )
        {
            throw new InvalidDataException(
                "Trained coefficients and public runtime mapping disagree."
            );
        }
        // These fields are unused by ScorePair. The model supplies ranking only;
        // zero must never be interpreted as a publication threshold or margin.
        Scorer = new(intercept, weights[0], weights[1], weights[2], 0, 0, weights[3], weights[4]);
    }
}
