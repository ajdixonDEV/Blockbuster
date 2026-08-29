using System.Data.Common;

namespace Blockbuster.Core.Persistence;

public interface IDbConnectionFactory
{
    Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
