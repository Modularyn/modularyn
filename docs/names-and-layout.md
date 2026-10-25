# Names and solution layout

Modularyn is a modular .NET application platform and CMS. This guide fixes the
repository naming contract; [the companion](names-and-layout.json) carries the
same identifiers and layout in machine-readable form.

The front-door NuGet package is `Modularyn`. It brings the essentials for a
typical application. The .NET tool package is `dotnet-modularyn`, invoked as
`dotnet modularyn` with either a global or local installation. Commands use
readable subcommands within that tool. These are exact identifiers.

## Package families and product homes

| Family | NuGet prefix | Editions | Source repository | Source directory |
|---|---|---|---|---|
| Framework | `Modularyn.Framework.*` | Community | `modularyn` | `src/Framework/` |
| Modules | `Modularyn.Modules.*` | Community | `modularyn` | `src/Modules/` |
| CMS | `Modularyn.Cms.*` | Community | `modularyn` | `src/Cms/` |
| UI | `Modularyn.UI.*` | Community | `modularyn` | `src/UI/` |
| Connectors | `Modularyn.Connectors.*` | Community | `modularyn` | `src/Connectors/` |
| Paid modules | `Modularyn.Pro.*` | Standard, Pro | `modularyn-pro` | `src/Pro/` |
| Tooling | `Modularyn.Tooling.*` | Community | `modularyn` | `src/Tooling/` |

The first five families retain the design's spelling. `Modularyn.Tooling.*` is
the proposed single addition: it groups command implementation and generation
without putting developer executables in runtime infrastructure. Admin fits
inside UI, and SDK extension contracts fit inside Framework. This proposal
defines this repository's layout; changes to the master design require a
recorded decision.

Each product name has one home. More specific prefixes take precedence: Admin
owns `Modularyn.UI.Admin.*`, SDK owns `Modularyn.Framework.Sdk.*`, and the parent
product owns the rest of its family. A product home is an ownership boundary,
not another implementation of its contracts.

| Product name | Home | Responsibility |
|---|---|---|
| Framework | `Modularyn.Framework.*` | Composition and application infrastructure outside the SDK subtree |
| Modules | `Modularyn.Modules.*` | Community business modules |
| CMS | `Modularyn.Cms.*` | Content, editorial work and delivery |
| Admin | `Modularyn.UI.Admin.*` | Administrative shell and module screens |
| UI | `Modularyn.UI.*` | Design system, themes and adapters outside the Admin subtree |
| Studio | `Modularyn.Pro.Studio.*` | Paid developer workspace |
| CLI | `dotnet-modularyn` | Command entry point, implemented by `Modularyn.Tooling.Cli` |
| Generator | `Modularyn.Tooling.Generator.*` | Reviewed source generation |
| SDK | `Modularyn.Framework.Sdk.*` | Public extension contracts |
| Connectors | `Modularyn.Connectors.*` | Provider integrations |

Community sources live in `modularyn` under Apache-2.0. Standard and Pro share
the paid sources in `modularyn-pro` under PolyForm Noncommercial 1.0.0;
commercial use requires a separate license. Standard is for one person and
Pro is for teams. Studio and all other paid modules use `Modularyn.Pro.*`.
Paid modules depend on Community only through published packages pinned to
exact versions. Community does not reference paid modules. An edition changes
distribution and licensing, not the implementation of a domain.

## Identifier grammar

Package identifiers are case-sensitive in this contract. Each dotted segment
is PascalCase, composed of words that start with an ASCII uppercase letter and
continue with lowercase ASCII letters or digits. The complete segment `UI` is
the sole acronym exception. Use `Cms`, `Cli`, `Sdk`, `HttpApi` and `EfCore`;
never `CMS`, `CLI` or `SDK` in package identifiers. No underscores, spaces,
hyphens or empty segments occur in dotted identifiers. A family prefix needs
at least one segment after its final dot. The companion's `packageIdPattern`
is the complete grammar, including the two exact package identifiers.

For every library package, package id = assembly name = root namespace =
project filename without `.csproj`. The front-door package follows this rule
as `Modularyn`. The sole packaging exception is the .NET tool:
`dotnet-modularyn` has assembly and root namespace `Modularyn.Tooling.Cli`,
because its package identifier is not a C# namespace. Its project is
`Modularyn.Tooling.Cli.csproj`; its command remains `dotnet modularyn`.

A module starts with `Modularyn.Modules.`, `Modularyn.Cms.` or `Modularyn.Pro.`,
followed by one PascalCase module segment and exactly one role suffix:

| Role | Suffix | Dependency boundary |
|---|---|---|
| Domain | `.Domain` | Domain logic; references no UI framework or UI adapter (ARC-001) |
| Application contracts | `.Application.Contracts` | Shared request, response and service contracts |
| Application services | `.Application` | Use cases over domain and application contracts |
| Persistence adapter | `.Persistence.{Provider}` | One named persistence implementation |
| HTTP endpoints | `.HttpApi` | Transport adapter over application contracts |
| Client | `.Client` | Client contract implementation |
| UI adapter | `.UI.{Adapter}` | One named UI implementation |

`{Provider}` and `{Adapter}` are grammar metavariables for one PascalCase
segment each, not literal identifier text. For example,
`Modularyn.Modules.Files.Persistence.EfCore` and
`Modularyn.Cms.Content.UI.Razor` have unambiguous roles. UI and persistence
adapters stay outside domain packages. Product subtrees such as Studio are
not forced into a business module's role grammar.

## Repository layout

| Path | Purpose | Present at this baseline |
|---|---|---|
| `global.json` | Exact SDK selection | Yes |
| `Directory.Build.props` | Shared build properties | Yes |
| `Directory.Packages.props` | Central package versions | Yes |
| `.editorconfig` | Text conventions | Yes |
| `.gitattributes` | Repository text handling | Yes |
| `Modularyn.slnx` | Solution listing every project once | Yes |
| `src/` | Product projects grouped by the family directories above | No |
| `tests/` | One project per tested package, plus repository contract tests | Yes |
| `tools/` | Repository development utilities | No |
| `docs/` | Public product contracts and guides | Yes |
| `schemas/` | Versioned machine-readable contracts | Yes |
| `brand/` | Product identity assets | No |

The front-door project belongs at `src/Modularyn/Modularyn.csproj`. Other
projects use their family directory, then their assembly name as both the
directory and project filename. The tool therefore belongs at
`src/Tooling/Modularyn.Tooling.Cli/Modularyn.Tooling.Cli.csproj`.

A package's tests use `tests/` followed by its assembly name plus `.Tests`,
with a matching project filename, assembly and root namespace. Exactly one
test project accompanies each tested package. The existing
`tests/Modularyn.Repository.Tests/Modularyn.Repository.Tests.csproj` is the
repository-wide exception and already appears in `Modularyn.slnx`.

The root configuration, solution, repository tests, `docs/coverage/`,
`docs/profiles/`, `docs/content-packages/` and `schemas/content-package/` already
follow the layout. Absent product directories are introduced when they gain
real content. This guide creates no product projects or empty directories.
