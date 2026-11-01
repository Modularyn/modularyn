using System.Buffers.Text;
using System.Security.Cryptography;
using Modularyn.Framework.Events.Tests;

namespace Modularyn.Framework.Audit.Tests;

/// <summary>Helpers over the durable fixture's real audit store, which these tests read and break directly, as tests may.</summary>
internal static class AuditFixture
{
    internal static CancellationToken Token => DurableDatabase.Token;

    /// <summary>Makes every append fail, as an unavailable audit store would, until <see cref="RepairAsync"/>.</summary>
    internal static Task BreakAsync(DurableDatabase database) =>
        database.ExecuteAsync("ALTER TABLE \"" + database.AuditSchema + "\".\"AuditRecords\" RENAME TO \"AuditRecordsUnavailable\"");

    internal static Task RepairAsync(DurableDatabase database) =>
        database.ExecuteAsync("ALTER TABLE \"" + database.AuditSchema + "\".\"AuditRecordsUnavailable\" RENAME TO \"AuditRecords\"");

    /// <summary>Every text the store holds, for searching it for a canary.</summary>
    internal static async Task<string> EverythingAsync(DurableDatabase database) =>
        string.Join("\n", (await database.AuditAsync()).Select(row => row.ToString()));

    /// <summary>Fresh canaries of secret forms, generated at run time.</summary>
    internal static string[] Canaries() =>
    [
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)),
        "Bearer " + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24)),
        "Password=" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)),
        "mfg1_" + Guid.NewGuid().ToString("N") + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32))
    ];
}
