# pg-deploy-test — Automated Deployment Validation Tool

## Problem Statement

After pg-deploy generates a deployment script, there is no automated way to verify the script actually applies cleanly against a real PostgreSQL instance. Manual testing requires a dedicated database, careful setup/teardown, and is error-prone. We need a companion CLI tool that:

1. Spins up an ephemeral PostgreSQL instance (no pre-existing install required)
2. Deploys the target DDL (baseline — representing the current database state)
3. Applies the pg-deploy-generated deployment script
4. Reports success/failure with detailed error output
5. Tears down everything — no persistent state left behind

## Option 1: MysticMind.PostgresEmbed (PRIMARY — Recommended)

### Overview

[MysticMind.PostgresEmbed](https://github.com/mysticmind/mysticmind-postgresembed) (v4.0.0, MIT license) provides an embedded PostgreSQL server for .NET. It downloads minimal PostgreSQL binaries (~10MB) from [zonky.io](https://github.com/zonkyio/embedded-postgres-binaries) on first run, caches them locally, and runs `initdb` + `pg_ctl` to start a fully functional PostgreSQL instance on an ephemeral port.

### Architecture

```
pg-deploy-test (new CLI project in pg-deploy solution)
├── References pg-deploy (shared models, DdlLoader, ScriptGenerator)
├── NuGet: MysticMind.PostgresEmbed (embedded PG server)
├── NuGet: Npgsql (PostgreSQL .NET driver)
└── NuGet: System.CommandLine (CLI parsing)
```

### CLI Interface

```bash
pg-deploy-test --source <source-ddl> --output <script.sql> [--target <target-ddl>] [--pg-version <ver>] [options]
```

| Parameter | Alias | Required | Description |
|-----------|-------|----------|-------------|
| `--source` | `-s` | Yes | Folder containing new/desired DDL files |
| `--output` | `-o` | Yes | The pg-deploy-generated deployment script to test |
| `--target` | `-t` | No | Folder containing existing DDL (baseline). If omitted, tests full-creation mode against an empty DB. |
| `--pg-version` | | No | PostgreSQL version to use (default: `16.8.0`). Must be available in zonky.io binaries. |
| `--allow-drops` | | No | Pass through to pg-deploy when generating the script internally |
| `--trust-source-folder` | | No | Trust source DDL without prompting |
| `--verbose` | `-v` | No | Show detailed output including SQL execution |
| `--quiet` | `-q` | No | Only output pass/fail |
| `--keep-on-failure` | | No | Don't tear down the PG instance on failure (for debugging) |

### Execution Flow

```
Step 1: Start embedded PostgreSQL
  └── PgServer("16.8.0") → picks free port, runs initdb + pg_ctl
  └── Connection string: Server=localhost;Port={auto};User Id=postgres;Database=postgres;Pooling=false

Step 2: Create test database
  └── CREATE DATABASE pg_deploy_test;
  └── Switch connection to pg_deploy_test

Step 3: Deploy target DDL (baseline)
  └── If --target provided:
  │     Load DDL via DdlLoader
  │     Execute each object's RawDdl in dependency order:
  │       Extensions → Schemas → Types → Sequences → Tables → Indexes →
  │       ForeignKeys → Views → MaterializedViews → Functions → Triggers
  └── If --target omitted:
        Database stays empty (testing full-creation script)

Step 4: Apply the deployment script
  └── Read --output file
  └── Execute the full script against the test database
  └── Capture any errors with line numbers and context

Step 5: Validation (optional future enhancement)
  └── Re-extract schema from the test DB and compare with source DDL
  └── Flag any discrepancies

Step 6: Report results
  └── SUCCESS: "Deployment script applied successfully against PostgreSQL {version}"
  └── FAILURE: Error message, line number, SQL context, suggestion

Step 7: Teardown
  └── DROP DATABASE pg_deploy_test
  └── PgServer.Stop() / Dispose()
  └── Instance directory cleaned up automatically
```

### Project Structure

```
pg-deploy-test/
├── pg-deploy-test.csproj      (Exe, net9.0, references pg-deploy)
├── Program.cs                  (CLI entry point)
├── TestRunner.cs               (orchestrates the full test flow)
├── PostgresServer.cs           (wraps MysticMind.PostgresEmbed lifecycle)
├── BinaryVerifier.cs           (SHA-256 checksum verification for downloaded PG binaries)
└── SqlExecutor.cs              (executes SQL files via Npgsql, error reporting)
```

### Dependencies

| Package | Version | License | Purpose |
|---------|---------|---------|---------|
| MysticMind.PostgresEmbed | 4.0.0 | MIT | Embedded PostgreSQL server |
| Npgsql | 9.0.x | PostgreSQL License | .NET PostgreSQL driver |
| System.CommandLine | 2.0.0-beta4 | MIT | CLI argument parsing |
| pg-deploy (ProjectReference) | — | MIT | Shared models, DdlLoader |

### Advantages

- **Zero infrastructure prerequisites** — no Docker, no pre-installed PostgreSQL
- **Fast startup** — ~2s once binaries are cached (first run downloads ~10MB)
- **Cross-platform** — Windows, Linux, macOS (including Apple Silicon)
- **Self-contained** — single executable with embedded everything
- **CI-friendly** — no Docker-in-Docker complexity, works on GitHub Actions runners natively
- **Deterministic port allocation** — uses free port, no conflicts
- **Lightweight** — binary cache is ~10MB, instance folders are cleaned automatically

### Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| PG version lag — zonky.io may not publish latest PG versions immediately | Low | Default to a well-tested version (16.8.0). User can override. |
| `initdb` permission issues on Windows | Medium | Set `addLocalUserAccessPermission: true` in PgServer constructor |
| Npgsql "connection forcibly closed" on teardown | Low | Use `Pooling=false` in connection string per MysticMind docs |
| Binary download fails (network flaky) | Low | MysticMind has built-in 3-retry logic with Polly |
| MysticMind targets net6.0-net8.0, not net9.0 explicitly | Low | net8.0 TFM is compatible with net9.0 projects (TFM compatibility) |
| PG extensions not available (e.g., PostGIS) | Medium | Document limitation. Target DDL that requires extensions may fail. Add `--skip-extensions` flag. |

### Gotchas from MysticMind Documentation

- Default user is `postgres` with trust authentication (no password needed)
- Default database is `postgres` — must create a separate test database
- Port is auto-assigned from 5500+ range if not specified
- `clearInstanceDirOnStop: true` ensures cleanup on normal exit
- On Windows, may need Visual C++ Redistributable 2013 for older PG versions
- Use `Pooling=false` in Npgsql connection string to avoid sporadic connection errors

### Security Hardening: Binary Trust Model

MysticMind.PostgresEmbed downloads PostgreSQL binaries from Maven Central (`repo1.maven.org`) but performs **no integrity verification** — no SHA-256 check, no GPG signature validation. The binaries are repackaged by a third party ([zonky.io](https://github.com/zonkyio/embedded-postgres-binaries)), not by the PostgreSQL Global Development Group. We must add our own verification layer.

#### Trust Chain Analysis

The fundamental question: **whose binaries are we running?**

| Source | Trust chain | Verification available |
|--------|-------------|----------------------|
| zonky.io Maven artifacts | Third-party repackaging of PG source → compiled → uploaded to Maven Central | `.jar.sha256` and `.jar.asc` on Maven Central |
| Official PG source tarball | `postgresql.org` → signed by PG Global Dev Group GPG key | `.sha256` + GPG signatures with well-known public key |
| Build from source ourselves | We download official source, verify GPG sig, compile locally | **Strongest** — no binary trust needed |

**Key insight**: If Maven Central is compromised, the attacker controls both the `.jar` binary AND the `.jar.sha256` checksum file. Verifying a hash from the same server as the binary only proves transport integrity (file wasn't corrupted in transit), not provenance. This is a **low-value security check**.

#### Verification Strategy (Revised)

**Tier 1 — Pinned hash manifest (PRIMARY — real security):**
1. Ship a `pg-binary-hashes.json` manifest in our repo with known-good SHA-256 hashes:
   ```json
   {
     "16.8.0": {
       "windows-amd64": "e894f9cd89971a5c92174a3c71b08eb2d06bbdae6e6a489a59f7cadb43682e29",
       "linux-amd64": "...",
       "darwin-amd64": "...",
       "darwin-arm64v8": "..."
     }
   }
   ```
2. Before first use of a PG version, a maintainer independently verifies the binary (ideally by building from official PG source and comparing, or by downloading from multiple mirrors and comparing hashes)
3. The pinned hash is committed to the repo — an attacker would need to compromise **both** Maven Central AND our GitHub repo
4. On download, the pinned hash is checked first — **no network dependency**, works offline
5. If the version/platform isn't in the manifest (user specified a different `--pg-version`), refuse with a clear error unless `--skip-hash-verification` is used
6. Also verify cached binaries on every run (not just first download) to detect local tampering

**Tier 2 — Maven Central .sha256 (TRANSPORT INTEGRITY ONLY — low security value):**
1. If `--allow-unpinned-versions` is specified for a version not in the manifest, download the `.jar.sha256` from Maven Central as a transport integrity check
2. This only proves the file wasn't corrupted during download — it does NOT prove the binary is legitimate
3. Display a prominent warning: `⚠ Using unpinned PG version. Binary integrity verified against Maven Central only (transport check). For full security, use a pinned version or --build-from-source.`

**Tier 3 — Build from source (STRONGEST — optional, future):**

Building PostgreSQL from the official source tarball (`postgresql.org`) eliminates all binary trust concerns entirely:

| Platform | Feasibility | Build time | Required toolchain |
|----------|-------------|------------|-------------------|
| **Linux** | ✅ Easy | ~2-3 min | gcc, make, flex, bison, zlib-dev, readline-dev, libicu-dev |
| **macOS** | ✅ Easy | ~2-3 min | Xcode CLI tools (includes everything needed) |
| **Windows** | ⚠️ Hard | ~5-10 min | Meson + Ninja + Visual Studio (or MSYS2) + Perl + flex + bison |

**Build-from-source flow:**
1. Download official tarball from `https://ftp.postgresql.org/pub/source/v{version}/postgresql-{version}.tar.bz2` (~24MB)
2. Verify GPG signature against the PostgreSQL Global Development Group public key (well-known, verifiable through multiple trust paths)
3. Extract, configure with minimal options (`--without-readline --without-zlib --without-icu`), compile
4. Use the resulting binaries instead of MysticMind's download
5. Cache the compiled binaries for subsequent runs

**Why this is deferred, not eliminated:**
- Windows builds require Visual Studio or MSYS2 — kills the "zero prerequisites" advantage
- Build time (~2-5 min) vs cached binary startup (~2 sec) — poor first-run UX
- But on Linux/macOS where build tools are typically available, this could be offered as `--build-from-source` for security-conscious users or CI environments

**Tier 4 — GPG signature verification of zonky.io artifacts (LOW VALUE — not planned):**
- Maven Central publishes `.asc` GPG signatures signed by the zonky.io maintainer
- This only proves **zonky.io** signed it, not that the binary faithfully represents official PG source
- Adds BouncyCastle or GnuPG dependency for marginal security benefit
- Not recommended unless we also independently verify zonky.io's build process

#### Security Summary

| Tier | What it proves | Security value | Implementation |
|------|---------------|---------------|----------------|
| Pinned hashes | Binary matches what we independently verified | **HIGH** | Phase 2 (initial release) |
| Maven Central SHA-256 | Download wasn't corrupted in transit | **LOW** | Phase 2 (for unpinned versions only) |
| Build from source | Binary IS official PostgreSQL | **HIGHEST** | Future (Linux/macOS only) |
| zonky.io GPG | zonky.io signed it | **MARGINAL** | Not planned |

#### Implementation: `BinaryVerifier` Class

```csharp
internal static class BinaryVerifier
{
    public static async Task VerifyAsync(string jarPath, string pgVersion,
        string platform, string architecture, string mavenRepo,
        bool allowUnpinned = false)
    {
        /* 1. Try pinned manifest first (real security) */
        if (TryGetPinnedHash(pgVersion, platform, architecture, out var pinnedHash))
        {
            var localHash = ComputeSha256(jarPath);
            if (!string.Equals(localHash, pinnedHash, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException(
                    $"Binary hash mismatch! Expected (pinned): {pinnedHash}, Got: {localHash}. " +
                    "The downloaded binary does not match our verified hash. " +
                    "This could indicate a compromised download source.");
            return;
        }

        /* 2. Version not in manifest */
        if (!allowUnpinned)
            throw new SecurityException(
                $"PG version {pgVersion}/{platform}-{architecture} is not in the pinned hash manifest. " +
                "Use --allow-unpinned-versions to proceed with transport-only verification, " +
                "or use a pinned version. Run --list-pinned-versions to see available versions.");

        /* 3. Fall back to Maven Central .sha256 (transport integrity only) */
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("⚠  WARNING: Using unpinned PG version. Binary verified against Maven Central only.");
        Console.WriteLine("   This is a transport integrity check, NOT a provenance check.");
        Console.ResetColor();

        var sha256Url = BuildSha256Url(mavenRepo, platform, architecture, pgVersion);
        var expectedHash = await DownloadStringAsync(sha256Url);
        var actualHash = ComputeSha256(jarPath);

        if (!string.Equals(actualHash, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new SecurityException(
                $"Binary hash mismatch (transport check). Expected: {expectedHash.Trim()}, Got: {actualHash}");
    }

    private static string ComputeSha256(string filePath) { /* SHA256.HashData */ }
    private static bool TryGetPinnedHash(...) { /* read embedded pg-binary-hashes.json */ }
}
```

#### Integration Point

MysticMind.PostgresEmbed doesn't expose a hook between download and extraction. Two approaches:

**Approach A (Preferred): Pre-download ourselves, then point MysticMind at the cache.**
1. Download the `.jar` ourselves using `HttpClient`
2. Verify SHA-256 (pinned manifest → refuse or Maven Central fallback with warning)
3. Place verified file in MysticMind's expected cache path (`pg_embed/binaries/`)
4. MysticMind sees the cached file and skips its own download
5. This gives us full control over the download+verify pipeline

**Approach B: Post-download verification.**
1. Let MysticMind download as normal
2. Before calling `server.Start()`, locate the cached `.jar` and verify it
3. If verification fails, delete the cached file and abort
4. Simpler but there's a TOCTOU window between download and verification

#### CLI Flags

| Parameter | Description |
|-----------|-------------|
| `--skip-hash-verification` | Bypass all SHA-256 checks (for air-gapped environments with pre-cached, pre-verified binaries) |
| `--allow-unpinned-versions` | Allow PG versions not in the pinned manifest (uses Maven Central transport check with warning) |
| `--list-pinned-versions` | Display all PG versions and platforms in the pinned hash manifest |
| `--update-hash-manifest` | Download and print SHA-256 hashes for a given PG version/platform (maintainer tool for updating the manifest) |

---

## Option 2: Testcontainers.PostgreSql (SECONDARY — Docker-based)

### Overview

[Testcontainers for .NET](https://dotnet.testcontainers.org/) (MIT license) uses Docker to spin up real PostgreSQL containers from official Docker Hub images. Mature, well-maintained, and widely used in the .NET ecosystem.

### Architecture

```
pg-deploy-test (new CLI project in pg-deploy solution)
├── References pg-deploy (shared models, DdlLoader, ScriptGenerator)
├── NuGet: Testcontainers.PostgreSql (Docker-based PG server)
├── NuGet: Npgsql (PostgreSQL .NET driver)
└── NuGet: System.CommandLine (CLI parsing)
```

### CLI Interface

Same as Option 1, but `--pg-version` maps to Docker image tags:

```bash
pg-deploy-test --source <source-ddl> --output <script.sql> [--target <target-ddl>] [--pg-version 16] [options]
```

### Execution Flow

Same 7-step flow as Option 1, except:
- Step 1: `new PostgreSqlBuilder("postgres:16").Build()` → `container.StartAsync()`
- Step 7: `container.DisposeAsync()` (stops and removes container + volume)

### Dependencies

| Package | Version | License | Purpose |
|---------|---------|---------|---------|
| Testcontainers.PostgreSql | 4.x | MIT | Docker-based PostgreSQL container |
| Npgsql | 9.0.x | PostgreSQL License | .NET PostgreSQL driver |
| System.CommandLine | 2.0.0-beta4 | MIT | CLI argument parsing |
| pg-deploy (ProjectReference) | — | MIT | Shared models, DdlLoader |

### Advantages

- **Exact version matching** — use any PG version available on Docker Hub (9.6 through 17)
- **Extensions available** — PostGIS, pgvector, etc. via specialized images
- **Battle-tested** — Testcontainers is a mature, widely-adopted project
- **Richer API** — `ExecScriptAsync()` can run scripts directly inside the container
- **Network isolation** — container runs in its own network namespace

### Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| **Requires Docker** — user must have Docker Desktop or Docker Engine | **High** | Document prerequisite clearly. Provide clear error message if Docker not found. |
| Slower startup — ~5-10s vs ~2s for embedded | Medium | Acceptable for a validation tool. Cache Docker image layers. |
| Docker-in-Docker for CI | Medium | GitHub Actions has Docker preinstalled. Other CI may need config. |
| Docker licensing — Docker Desktop requires paid license for large orgs | Medium | Document that Docker Engine (free) on Linux works fine. |
| Container cleanup on crash | Low | Testcontainers has built-in resource reaper (Ryuk container) |
| Image pull on first run — ~100-150MB | Medium | One-time cost, Docker caches layers |

---

## Comparison Matrix

| Criterion | Option 1: MysticMind.PostgresEmbed | Option 2: Testcontainers |
|-----------|-----------------------------------|--------------------------|
| **Prerequisites** | None (auto-downloads ~10MB) | Docker Desktop/Engine required |
| **First-run download** | ~10MB (PG binaries) | ~100-150MB (Docker image) |
| **Startup time** | ~2s (cached) | ~5-10s |
| **PG version flexibility** | Limited to zonky.io published versions | Any version on Docker Hub |
| **PG extensions** | Not easily supported | Full support via Docker images |
| **Cross-platform** | ✅ Win/Linux/macOS/ARM | ✅ Win/Linux/macOS (needs Docker) |
| **CI friendliness** | ✅ No special config needed | ⚠️ Docker must be available |
| **Corporate environments** | ✅ No Docker licensing concerns | ⚠️ Docker Desktop licensing for large orgs |
| **Cleanup reliability** | Good (Dispose + clearInstanceDirOnStop) | Excellent (Ryuk reaper container) |
| **Library maturity** | Moderate (550K downloads, single maintainer) | High (millions of downloads, org-maintained) |
| **Package license** | MIT | MIT |
| **API complexity** | Simple (PgServer start/stop) | Simple (builder pattern) |
| **Network isolation** | Localhost only | Container network namespace |
| **Debugging** | Can inspect data dir on disk | Can exec into container |

### Recommendation

**Option 1 (MysticMind.PostgresEmbed) is recommended as primary** because:
1. Zero prerequisites — users don't need Docker installed
2. Faster startup and smaller download
3. No corporate Docker licensing concerns
4. Simpler for CI/CD pipelines
5. Perfectly adequate for validating DDL scripts (extensions not typically needed for schema-only testing)

**Option 2 could be added later** as an alternative backend behind an interface (e.g., `IPostgresServer`), activated via `--use-docker` flag. This would allow users who need exact version matching or extension support to opt in.

---

## Implementation Plan (Option 1 — Primary)

### Phase 1: Project Setup
- **project-scaffold**: Create `pg-deploy-test` console project in the solution
  - net9.0, System.CommandLine, MysticMind.PostgresEmbed, Npgsql
  - ProjectReference to pg-deploy
  - Update pg-deploy.slnx
  - Update licence.md (if tracking NuGet licenses)

### Phase 2: Security — Binary Trust Verification
- **binary-verifier**: Create `BinaryVerifier` class
  - `ComputeSha256()` using `System.Security.Cryptography.SHA256`
  - `TryGetPinnedHash()` reads embedded `pg-binary-hashes.json` manifest
  - `VerifyAsync()` checks pinned manifest first (HIGH security); for unpinned versions, refuses unless `--allow-unpinned-versions` is set, then falls back to Maven Central `.sha256` (transport integrity only, with prominent warning)
  - Throws `SecurityException` with clear, actionable messages on mismatch
  - Also verifies cached binaries on every run (detect local tampering)
- **hash-manifest**: Create `pg-binary-hashes.json` with independently verified SHA-256 hashes
  - Cover default PG version (16.8.0) across all platforms: windows-amd64, linux-amd64, darwin-amd64, darwin-arm64v8
  - Embed as assembly resource
  - Document the verification process for maintainers adding new versions
  - Include `--list-pinned-versions` CLI flag to display manifest contents
  - Include `--update-hash-manifest` maintainer tool to download and display hashes for new versions
- **pre-download**: Implement Approach A — download `.jar` ourselves via `HttpClient`, verify, then place in MysticMind's cache path so it skips its own unverified download

### Phase 3: Core Infrastructure
- **postgres-server-wrapper**: Create `PostgresServer` class wrapping MysticMind.PostgresEmbed lifecycle
  - Start/Stop/Dispose with proper error handling
  - Connection string generation
  - Database creation/drop helper
  - Configurable PG version
  - Call `BinaryVerifier.VerifyAsync()` before starting PG server
  - Support `--skip-hash-verification` and `--allow-unpinned-versions` flags
- **sql-executor**: Create `SqlExecutor` class for running SQL against Npgsql
  - Execute single statements and full scripts
  - Error capture with line number, position, and SQL context
  - Timeout handling
  - Transaction awareness (scripts already have BEGIN/COMMIT)

### Phase 4: Test Runner
- **test-runner**: Create `TestRunner` orchestrating the full flow
  - Accept source/target/output paths + options
  - Call PostgresServer to start instance
  - Deploy target DDL in dependency order (reuse DdlLoader + ordered iteration)
  - Execute the deployment script
  - Report results with clear pass/fail output
  - Teardown with --keep-on-failure support

### Phase 5: CLI Integration
- **cli-program**: Wire up Program.cs with System.CommandLine
  - All parameters from the CLI table above
  - Security flags: `--skip-hash-verification`, `--allow-unpinned-versions`, `--list-pinned-versions`, `--update-hash-manifest`
  - Consistent output formatting with pg-deploy
  - Exit codes: 0 = success, 1 = script failed, 2 = infrastructure error, 3 = security verification failed

### Phase 6: Testing & Documentation
- **unit-tests**: Test BinaryVerifier (pinned match, pinned mismatch, unpinned refused, unpinned allowed with Maven fallback, cached binary re-verification), SqlExecutor error parsing, TestRunner flow with mocked server
- **integration-test**: End-to-end test with a simple schema (can use MysticMind in xUnit fixture)
- **readme-update**: Update pg-deploy README with pg-deploy-test section, document security model and trust chain analysis
- **build-release**: Add pg-deploy-test to build-release.yml for cross-platform publishing

### Future Enhancements (not in initial scope)
- `--build-from-source` flag for Linux/macOS — download official PG tarball from postgresql.org, verify GPG signature against PG Global Dev Group key, compile locally (eliminates all binary trust concerns)
- `--use-docker` flag to switch to Testcontainers backend
- Post-deployment schema re-extraction and comparison against source
- `--generate-and-test` mode that runs pg-deploy + pg-deploy-test in one step
- Support for seed data scripts (run after baseline DDL, before deployment)
- Parallel test runs with different PG versions
