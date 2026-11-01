# Background Jobs PostgreSQL store

`BackgroundJobsPersistence.Create` registers one owned schema and context.
`BackgroundJobsInitial` creates the framework job tables through the module
migration coordinator. Include its manifest in the same activation plan and run
the coordinator before activation. No database or role creation is required.

Every root is tenant-scoped. Keys, indexes and foreign keys carry the tenant;
job, lease and schedule writes use concurrency tokens. The adapter's filters and
host-operation reach guard reads and writes. The application takes this module
registration as an explicit dependency and references no persistence assembly.
