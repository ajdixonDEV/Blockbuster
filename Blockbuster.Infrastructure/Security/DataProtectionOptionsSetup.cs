using Blockbuster.Infrastructure.Configuration;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blockbuster.Infrastructure.Security;

public sealed class DataProtectionOptionsSetup(IStoragePathResolver paths, ILoggerFactory loggerFactory)
    : IConfigureOptions<KeyManagementOptions>
{
    public void Configure(KeyManagementOptions options)
    {
        Directory.CreateDirectory(paths.DataProtectionKeysPath);
        options.XmlRepository = new FileSystemXmlRepository(
            new DirectoryInfo(paths.DataProtectionKeysPath),
            loggerFactory);
    }
}
