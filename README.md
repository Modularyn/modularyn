# Modularyn

Modularyn is a modular .NET application platform and content management system (CMS) designed for .NET 10 and C# 14.

Modularyn is in development. Nothing has been released, and no package is published. **G-01: the product and contract baseline** is complete. **G-02: the foundation and secure application slice** is in progress. The repository holds the framework's foundation, the Identity, Background Jobs and Files modules, and a sample multi-tenant application, the team workspace, all tested against PostgreSQL in CI. It is not ready for production use, and the CMS is not built yet. See the [roadmap](ROADMAP.md) for the planned sequence.

## Editions

**Community** lives in this repository under the [Apache License 2.0](LICENSE). It is free, including for commercial use, and is intended to be complete without the paid editions. Community does not depend on paid modules.

**Standard** and **Pro** license the same paid modules: Standard for one person, Pro for teams and organizations. Their source belongs in [Modularyn/modularyn-pro](https://github.com/Modularyn/modularyn-pro) under PolyForm Noncommercial 1.0.0 for review and noncommercial use. Commercial use requires a separate license from Jason Furr. These modules are not open source and do not accept outside contributions. No commercial offers or prices are available yet.

## Repositories

| Repository | Purpose |
| --- | --- |
| [modularyn](https://github.com/Modularyn/modularyn) | Community code, tests, and product documentation |
| [modularyn-pro](https://github.com/Modularyn/modularyn-pro) | Shared paid modules for Standard and Pro |
| [modularyn-com](https://github.com/Modularyn/modularyn-com) | Websites |
| [.github](https://github.com/Modularyn/.github) | Organization profile |
| modularyn-estate (private) | Governance records and the operator's master design |

## Development

Install Git and the .NET SDK specified in [global.json](global.json), then run these commands from the repository root:

```sh
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build --filter "Requires!=PostgreSQL"
```

Tests that need real providers carry the trait `Requires=PostgreSQL`. They need PostgreSQL 18 and the pinned Node.js, and run through `bash .github/scripts/postgresql-test-database.sh run bash .github/scripts/real-provider-tests.sh`.

Package versions are centralized in [Directory.Packages.props](Directory.Packages.props), and dependencies are locked alongside each project. The [CI guide](docs/ci.md) describes the required checks, dependency updates, and verification process.

## License, security, and contact

Community is licensed under [Apache-2.0](LICENSE); see [NOTICE](NOTICE) for attribution. Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md). For other correspondence, email [modularyn@jasonfurr.com](mailto:modularyn@jasonfurr.com).
