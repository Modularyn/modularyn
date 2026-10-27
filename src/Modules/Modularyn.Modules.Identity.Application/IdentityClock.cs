using System.Runtime.CompilerServices;
using Modularyn.Modules.Identity.Domain;

[assembly: InternalsVisibleTo("Modularyn.Modules.Identity.Application.Tests")]

namespace Modularyn.Modules.Identity.Application;

/// <summary>
/// The identity module's one clock: the host's <see cref="TimeProvider"/>, floored to whole microseconds
/// (<see cref="StoredTime"/>). Every time the module stores or compares is read here or derived from a time read here,
/// so what it holds in memory is exactly what the database keeps, and a limit decides the same way before and after a
/// round trip.
/// </summary>
internal sealed class IdentityClock(TimeProvider time)
{
    internal DateTimeOffset Now => StoredTime.Floor(time.GetUtcNow());
}
