using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

// TM-G02-027 for the seal tenancy registers: it seals with the key ring that SEC-02's AddModularynDataProtection declared
// and with no other, so a host without a declared ring stores no reference and opens none, and a reference sealed by
// another ring, or over other content, does not verify.
public sealed class PropagationSealTests
{
    [Fact]
    public void TM_G02_027_References_are_storedwith_the_declared_key_ring_alone()
    {
        var content = Encoding.UTF8.GetBytes("{\"reference\":\"" + Guid.NewGuid().ToString("D") + "\"}");

        // Without a declared ring the seal is unavailable: nothing is sealed, and nothing verifies.
        using (var undeclared = Services(declare: false, new MemoryRepository()))
        {
            var seal = undeclared.GetRequiredService<IPropagationSeal>();
            Assert.False(seal.IsAvailable);
            Assert.Throws<InvalidOperationException>(() => seal.Seal(content));
            Assert.False(seal.Verify(content, "CfDJ8" + new string('A', 40)));
        }

        // With one, a seal verifies over exactly its content, in every host over the same ring and in no other.
        var ring = new MemoryRepository();
        string stored;
        using (var first = Services(declare: true, ring))
        {
            var seal = first.GetRequiredService<IPropagationSeal>();
            Assert.True(seal.IsAvailable);
            stored = seal.Seal(content);
            Assert.True(seal.Verify(content, stored));
            Assert.False(seal.Verify([.. content, (byte)' '], stored));
            Assert.False(seal.Verify(content, stored[..^2]));
            Assert.False(seal.Verify(content, "not a seal"));
        }
        using (var restarted = Services(declare: true, ring))
            Assert.True(restarted.GetRequiredService<IPropagationSeal>().Verify(content, stored));
        using (var foreign = Services(declare: true, new MemoryRepository()))
            Assert.False(foreign.GetRequiredService<IPropagationSeal>().Verify(content, stored));
    }

    private static ServiceProvider Services(bool declare, IXmlRepository ring)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Modularyn:DataProtection:ApplicationName"] = "Modularyn.SealTests"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        if (declare) services.AddModularynDataProtection(configuration, keyStore: _ => ring);
        // No connection is opened: the directory module only completes the registration.
        services.AddModularynTenancy(TenantDirectoryModule.Create(new PostgreSqlPersistenceOptions("Host=localhost;Database=unused")));
        return services.BuildServiceProvider();
    }

    private sealed class MemoryRepository : IXmlRepository
    {
        private readonly List<XElement> elements = [];
        public IReadOnlyCollection<XElement> GetAllElements() { lock (elements) return elements.Select(element => new XElement(element)).ToArray(); }
        public void StoreElement(XElement element, string friendlyName) { lock (elements) elements.Add(new XElement(element)); }
    }
}
