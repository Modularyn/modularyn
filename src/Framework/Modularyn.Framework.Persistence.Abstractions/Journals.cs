namespace Modularyn.Framework.Persistence.Abstractions;

/// <summary>
/// A record written once and never changed or removed, such as an audit record. The persistence adapter inserts it only
/// through its journal append, in the transaction of the change it records, and refuses every tracked, direct or bulk
/// write that would add it any other way, change it or delete it.
/// </summary>
public interface IAppendOnly;
