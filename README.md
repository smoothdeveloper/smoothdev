# smoothdev

Tooling for smooth development: small, sharp tools that take the friction out of the daily loop, in F#
(and Rust where it fits).

| tool | what it is |
|---|---|
| [`tools/smoothdev.web`](tools/smoothdev.web/README.md) | `smoothdev-web`: run a Vite + Fable + .NET web app in dev and prod from one place (free ports, process groups, logs, static bundle, TUI and web GUI) |

Requirements: the .NET SDK pinned in `global.json`; `dotnet tool restore` brings paket.

The documentation site is [Nacara](https://mangelmaxime.github.io/Nacara/), in `docs/`. From the repository root: `dotnet run --project docs -- watch`.
Dependencies are managed with paket (`paket.dependencies` at the root).

Licence: [GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0-only).
