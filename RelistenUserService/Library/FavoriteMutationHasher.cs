using System.Security.Cryptography;
using System.Text;

namespace RelistenUserService.Library;

public static class FavoriteMutationHasher
{
    private const string Domain = "relisten-favorite-mutation-typed-v2";

    public static byte[] Hash(FavoriteMutationCommand mutation)
    {
        var semanticValue = string.Join(
            '\n',
            Domain,
            FavoriteMutationRequestValidator.ContractVersion,
            mutation.CatalogType,
            mutation.CatalogId,
            mutation.DesiredState,
            mutation.FavoriteId?.ToString() ?? "-"
        );
        return SHA256.HashData(Encoding.UTF8.GetBytes(semanticValue));
    }
}
