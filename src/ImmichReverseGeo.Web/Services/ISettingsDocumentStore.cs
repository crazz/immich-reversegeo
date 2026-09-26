using System.Threading;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Web.Services;

internal interface ISettingsDocumentStore
{
    Task<byte[]?> ReadAsync(CancellationToken cancellationToken);
    Task PublishAsync(byte[] document, CancellationToken cancellationToken);
}
