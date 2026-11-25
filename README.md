# NKeeper Oracle plugin

The official `oracle` plugin for [NKeeper](https://github.com/OpenCraft-Solutions/NKeeper) 2.0: Oracle Database (11g and later), with a query window, object explorer and blocking manager. It lives in a repository of its own and is released on its own, so releasing it isn't a release of NKeeper (NKeeper's `docs/architecture.md` §9). Its design is §7.34 there.

## What it does

It's a database engine for `db-core` (§7.32), which it depends on: db-core runs the session and draws the tab, and this plugin brings Oracle's driver (ODP.NET) and SQL. It needs SDK 1.9 (`>=1.9.0 <2.0.0`), whose database failures carry the driver's inner messages, and db-core 1.x.

It contributes the **`oracle` service type** ("Oracle", port 1521, health by TCP), whose one setting is the database's service name or SID, and its database tab (`oracle.query`), ported from 1.x:
- **Signing in.** Core opens the login with ODP.NET and the credential's password, which the plugin never sees (SDK 1.7):
  - by service name, and by SID when the listener doesn't know the name;
  - Oracle's native network encryption and integrity checks are requested, so a server that offers them encrypts the connection;
  - a login named SYS connects AS SYSDBA; a credential with a domain is refused.
- **Query windows**, each on a connection of its own: statements split as SQL*Plus splits them (PL/SQL blocks end at `/`), results, row counts, SQL*Plus's messages, DBMS_OUTPUT under Messages, compilation errors listed, and errors at their line. The toolbar chooses the window's schema (`CURRENT_SCHEMA`).
- **The object explorer:** schemas (Oracle-maintained ones in their own folder), tables, views, synonyms, sequences, procedures, functions, packages, columns, constraints, indexes, triggers, arguments, users and roles, through the `ALL_` views, so any login sees what it may use.
- **Scripts:** Select Top 1000 Rows, Script as CREATE (from DBMS_METADATA) and Script as EXECUTE.
- **The blocking manager**, under Management, from `V$SESSION` (it needs `SELECT_CATALOG_ROLE` or similar), and kill (`ALTER SYSTEM KILL SESSION 'sid,serial#' IMMEDIATE`), audited as `oracle.kill`.

**Management reports** (1.1.0, on db-core 1.1), 1.x's, under Management in the explorer. Each reads only, and offers scripts it never runs: SQL opens in a query window, RMAN commands are shown to copy.
- **Storage:** tablespaces (used of their size and of their maximum), temporary tablespaces and datafiles, warning at 85% of a tablespace's maximum, with scripts to let one grow.
- **Backups (RMAN):** the database's log mode and last backup, RMAN's job history, running jobs, datafiles' last backups, the fast recovery area and its usage, and RMAN's configuration, warning when a backup is old or failed, with RMAN scripts.
- **Health:** the instance, its size, resource limits, alerts, the alert log, log switches, invalid objects (with scripts to recompile them), locked and expiring accounts, and long operations.

They read the DBA_ and V$ views, so the login needs SELECT_CATALOG_ROLE (or SELECT ANY DICTIONARY); a section it can't read says why.

**Not yet:** the insurance survey. Oracle's native encryption doesn't prove the server's identity, so there's no certificate to trust, as in 1.x.

The plugin's ID is `oracle`, and it is built in: it is signed with NKeeper's official key and installed with NKeeper.

## Layout

```text
backend/        the plugin: OraclePlugin.cs, the engine (OracleEngine.cs), the dialect and script splitter ported from 1.x (OracleDialect.cs, OracleScript.cs), nkeeper-plugin.json (the manifest), the project
frontend/       its tab: db-core's database tab, told it's Oracle
tests/          the engine and db-core's session against a real Oracle Database Free in Docker
.github/        CI and the release workflow
```

It builds against NKeeper's published SDK and db-core's package, never core's source:

| Package | Registry | Used for |
| --- | --- | --- |
| `NKeeper.Abstractions` | GitHub Packages (NuGet) | the backend SDK |
| `NKeeper.Plugins.Databases` | GitHub Packages (NuGet) | db-core's assembly: the engine contract and the session |
| `NKeeper.Plugin.Build` | GitHub Packages (NuGet) | builds, packs and signs the plugin |
| `Oracle.ManagedDataAccess.Core` | nuget.org | the driver, which the package carries |
| `@opencraft-solutions/nkeeper-sdk` | GitHub Packages (npm) | the frontend SDK, imported as `@nkeeper/sdk` |

The plugin is built for `linux-x64`, where NKeeper runs: some of ODP.NET's dependencies are per operating system, and the package carries the Linux ones.

## Building

You need the .NET 10 SDK (see `global.json`), Node.js 24 (see `.nvmrc`), and Docker for the tests.

GitHub Packages needs a token even for public packages. Make a GitHub personal access token that can read packages (`read:packages`) and give it to the build in the environment. It is never written to the repository:

```bash
export NUGET_USERNAME=<your GitHub user name>
export NUGET_AUTH_TOKEN=<token>
export NODE_AUTH_TOKEN=<token>
```

Then, from the repository root:

```bash
(cd frontend && npm ci && npm run lint && npm run typecheck)
dotnet restore NKeeper.Plugins.Oracle.slnx
dotnet build NKeeper.Plugins.Oracle.slnx --configuration Release
```

Building the plugin builds its frontend, packs `oracle-<version>.nkp` into `artifacts/plugins/` and signs it with a development key, which the build creates under `.dev/keys/` on first use. Both are ignored by git. To load it in a development NKeeper, put db-core's package beside it.

Pinned versions: NuGet packages are locked in `packages.lock.json` (CI restores with `--locked-mode`), and npm packages in `frontend/package-lock.json`. Run `dotnet restore --force-evaluate` after changing a version.

## Testing

```bash
dotnet test NKeeper.Plugins.Oracle.slnx --configuration Release
```

The tests start Oracle Database Free in Docker (Testcontainers, `gvenzl/oracle-free`), and are skipped when Docker isn't there. A stand-in for core opens the login, as core does. They cover:
- signing in by service name, without the password left in the driver's connection string;
- a wrong password, an unknown service and a domain, refused in words;
- statements with results, row counts, SQL*Plus's messages, DBMS_OUTPUT and an error at its line;
- the schema as the window's database, and a cancelled query;
- the explorer and scripts;
- the blocking manager's chain, and kill with its serial and its audit.

## Continuous integration

`.github/workflows/ci.yml` runs on every push to `main` and every pull request: it restores from GitHub Packages with the workflow's token, lints and type-checks the frontend, checks formatting, builds, runs the tests and packs the plugin signed with a development key, which it keeps as a build artifact.

## Releasing

1. Raise `version` in `backend/nkeeper-plugin.json` (and keep `frontend/package.json` in step), and merge it to `main`.
2. Tag the merge commit `v<version>`, for example `v1.1.0`, and push the tag.
3. `.github/workflows/release.yml` runs. It checks that the tag matches the manifest's version and is on `main`, builds and tests, and then, in the protected `release` environment, which needs an owner's approval, packs `oracle-<version>.nkp` signed with the official key and publishes a GitHub release with the package and its SHA-256.

The official key's private half, `NKEEPER_RELEASE_KEY` (a PEM), is a secret of the `release` environment. It is available only to the signing job, goes to memory-backed storage for the packing step alone, and is removed when that step ends. It is never in the repository or in a log.

NKeeper's installer picks the release up when its `deploy/plugins.lock` is updated with the new URL and SHA-256.

## License

MIT, as NKeeper. See [LICENSE](LICENSE).
