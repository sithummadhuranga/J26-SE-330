using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;

namespace Sync.Common.Web;

/// <summary>Keeps data-protection keys in memory since bearer-only services never actually protect data with them.</summary>
public static class InMemoryDataProtection
{
    public static IServiceCollection AddInMemoryDataProtection(this IServiceCollection services)
    {
        services.AddDataProtection();
        services.Configure<KeyManagementOptions>(o =>
        {
            o.XmlRepository = new MemoryXmlRepository();
            o.XmlEncryptor = new NullXmlEncryptor();
        });
        return services;
    }

    private sealed class MemoryXmlRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];
        private readonly Lock _lock = new();

        public IReadOnlyCollection<XElement> GetAllElements()
        {
            lock (_lock) return _elements.Select(e => new XElement(e)).ToList();
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            lock (_lock) _elements.Add(new XElement(element));
        }
    }
}
