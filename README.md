# smoothdev

Tooling for smooth development: small, sharp tools that take the friction out of the daily loop, in F#
(and Rust where it fits).

| tool | what it is |
|---|---|
| [`tools/smoothdev-web`](tools/smoothdev-web/README.md) | `smoothdev-web`: run a Vite + Fable + .NET web app in dev and prod from one place (free ports, process groups, logs, static bundle, TUI and web GUI) |

| library | what it is |
|---|---|
| [`ui/smoothdev.ui`](docs/content/guide/ui.md) | models of common controls (scrubber, colour sets, row editor, sortable list, tree, tabs, editor host) and theme tokens, for any target |
| [`ui/smoothdev.ui.fable.webcomponents`](docs/content/guide/ui.md) | those controls as Lit custom elements in F# (Firelight); `ui/gallery` shows them all |

Local packs: `dotnet fsi scripts/pack_local.fsx` packs the libraries at `HEAD` as `0.0.0.1-alpha.g<short sha>` into `~/.nuget/local-feed`; pin that exact version.

Requirements: the .NET SDK pinned in `global.json`; `dotnet tool restore` brings paket.

The documentation site is [Nacara](https://mangelmaxime.github.io/Nacara/), in `docs/`. From the repository root: `dotnet run --project docs -- watch`, or `smoothdev-web site dev` to also run the Vite apps the docs host behind one port (see [Docs sites and Vite apps](docs/content/tools/smoothdev-web/sites.md)).
Dependencies are managed with paket (`paket.dependencies` at the root).

Licence: [GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0-only).
