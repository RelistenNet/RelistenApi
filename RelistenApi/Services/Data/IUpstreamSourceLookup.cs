using System.Threading.Tasks;
using Relisten.Api.Models;

namespace Relisten.Data;

public interface IUpstreamSourceLookup
{
    Task<UpstreamSource?> FindUpstreamSourceByName(string name);
}
