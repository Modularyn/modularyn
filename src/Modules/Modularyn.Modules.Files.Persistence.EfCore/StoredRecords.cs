using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Modules.Files.Domain;

namespace Modularyn.Modules.Files.Persistence.EfCore;

// Persistence marker interfaces stay at the adapter boundary. All mapped roots inherit ITenantScoped from the domain.
public sealed class StoredFileAsset : FileAsset, IConcurrencyTracked { }
public sealed class StoredFileRevision : FileRevision, ISoftDeleted, IConcurrencyTracked { }
public sealed class StoredFileUpload : FileUpload, IConcurrencyTracked { }
