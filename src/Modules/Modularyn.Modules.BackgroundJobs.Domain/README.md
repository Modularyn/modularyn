# Background Jobs definitions

`BackgroundJobsDefinition` declares the module identity, initial migration,
current-profile execute, schedule and cancel permissions, and the dispatch host
operation. Dispatch reaches only this module's jobs, attempts and schedules.
The domain references framework abstractions; runtime and provider concerns live
in the application and persistence projects.
