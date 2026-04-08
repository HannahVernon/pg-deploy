# pg_deploy — Architecture

## Overview

pg_deploy is a cross-platform .NET 9 CLI tool that generates incremental PostgreSQL deployment scripts by comparing two DDL folder trees produced by [pg-extract-schema](https://github.com/HannahVernon/pg-extract-schema). It uses `ALTER` where possible instead of `DROP`+`CREATE` to preserve permissions and avoid cascading side-effects.

**Key design goals:**

- Incremental, minimal-change deployments
- Safety by default (destructive changes require opt-in)
- Auditable output (git metadata, warnings with line numbers, change summaries)
- Human-reviewable SQL scripts (single file, transaction-wrapped, dependency-ordered)

---

## Project Structure

```
pg_deploy/
├── pg_deploy/                              Main application
│   ├── Program.cs                          CLI entry point (System.CommandLine)
│   ├── Models/
│   │   ├── DdlModels.cs                    Schema object model (11 DDL types)
│   │   └── SchemaChange.cs                 Change representation (category, action, SQL)
│   ├── Parsing/
│   │   └── DdlLoader.cs                    Regex-based DDL parser
│   ├── Diff/
│   │   └── SchemaDiffer.cs                 Structural diff engine
│   └── ScriptGeneration/
│       ├── ScriptGenerator.cs              SQL script emitter (header, body, footer)
│       └── GitInfo.cs                      Git branch/remote detection
│
├── pg_deploy.Tests/                        xUnit test suite
│   ├── DdlLoaderTests.cs                   Parser tests
│   └── SchemaDifferTests.cs                Differ + security tests
│
├── README.md
├── architecture.md                         (this file)
└── pg_deploy.slnx                          Solution file
```

### External Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| System.CommandLine | 2.0.0-beta4.22272.1 | CLI argument parsing, help generation |
| xunit | 2.9.2 | Unit test framework (test project only) |
| coverlet.collector | 6.0.2 | Code coverage (test project only) |

**Runtime:** .NET 9.0 with nullable reference types and implicit usings enabled.

---

## Data Flow Pipeline

```
┌──────────────────────────────────────────────────────────────────┐
│ 1. CLI INVOCATION & VALIDATION (Program.cs)                      │
│    Parse --source, --target, --output + optional flags           │
│    Validate folder existence                                     │
│    [SECURITY] Display trust prompt unless --trust-source-folder  │
└────────────────────────┬─────────────────────────────────────────┘
                         │
                         ▼
┌──────────────────────────────────────────────────────────────────┐
│ 2. PARSE SOURCE & TARGET (DdlLoader.Load)                        │
│    Read *.sql files from 12 subdirectories                       │
│    Regex-parse each file into typed model objects                 │
│    Populate two DatabaseSchema instances                         │
└────────────────────────┬─────────────────────────────────────────┘
                         │
                         ▼
┌──────────────────────────────────────────────────────────────────┐
│ 3. COMPUTE DIFFERENCES (SchemaDiffer.ComputeChanges)             │
│    Compare source vs target for all 11 object categories         │
│    Detect ADDs, MODIFYs, DROPs                                  │
│    [SECURITY] Validate CHECK constraint expressions              │
│    Generate SQL for each change; attach warnings                 │
│    Return List<SchemaChange>                                     │
└────────────────────────┬─────────────────────────────────────────┘
                         │
                         ▼
┌──────────────────────────────────────────────────────────────────┐
│ 4. GENERATE SCRIPT (ScriptGenerator.Generate)                    │
│    Build metadata header (timestamps, git info, change summary)  │
│    Emit SQL body in dependency order, wrapped in BEGIN/COMMIT    │
│    Two-pass line numbering for accurate warning cross-references │
│    Write to --output file                                        │
└──────────────────────────────────────────────────────────────────┘
```

---

## Component Architecture

### Models (`pg_deploy.Models`)

**DatabaseSchema** is the root container, holding 11 `Dictionary<string, T>` collections keyed by qualified name:

| Collection | Model Type | Represents |
|------------|-----------|------------|
| `Extensions` | `ExtensionDef` | `CREATE EXTENSION` |
| `Schemas` | `SchemaDef` | `CREATE SCHEMA` with optional `OWNER` |
| `Types` | `TypeDef` | Enum, Composite, or Domain types |
| `Sequences` | `SequenceDef` | `CREATE SEQUENCE` with all properties |
| `Tables` | `TableDef` | Tables with columns, PK, unique/check constraints, comments |
| `Indexes` | `IndexDef` | `CREATE INDEX` (unique and non-unique) |
| `ForeignKeys` | `ForeignKeyDef` | `ALTER TABLE ... ADD CONSTRAINT ... FOREIGN KEY` |
| `Views` | `ViewDef` | `CREATE VIEW` |
| `MaterializedViews` | `MaterializedViewDef` | `CREATE MATERIALIZED VIEW` |
| `Functions` | `FunctionDef` | `CREATE FUNCTION` and `CREATE PROCEDURE` (distinguished by `Kind`) |
| `Triggers` | `TriggerDef` | `CREATE TRIGGER` |

Every model stores `RawDdl` (the original file content) and `FileName` for traceability.

**SchemaChange** represents a single diff result:

- `ChangeCategory` — which object category (11 values: Extension through Trigger)
- `ChangeAction` — Add, Modify, or Drop
- `ObjectType` — human-readable label (e.g., `"COLUMN"`, `"CHECK CONSTRAINT"`, `"TYPE (ENUM)"`)
- `ObjectName` — qualified name of the affected object
- `Sql` — the SQL statement(s) to execute
- `IsDestructive` — true for DROP operations
- `Warning` — optional warning message (risky type changes, cascading drops, etc.)
- `LineNumber` — line in the generated script (assigned post-generation)

### Parser (`pg_deploy.Parsing.DdlLoader`)

Static class that reads a pg-extract-schema output folder and returns a `DatabaseSchema`.

**Input structure expected:**

```
folder/
├── extensions/    {name}.sql
├── schemas/       {name}.sql
├── types/         {schema}.{name}.sql
├── sequences/     {schema}.{name}.sql
├── tables/        {schema}.{name}.sql
├── indexes/       {schema}.{name}.sql
├── foreign_keys/  {schema}.{name}.sql
├── views/         {schema}.{name}.sql
├── materialized_views/  {schema}.{name}.sql
├── functions/     {schema}.{name}[_{hash}].sql
├── procedures/    {schema}.{name}[_{hash}].sql
└── triggers/      {schema}.{name}.sql
```

**Parsing approach:**

- 15+ `[GeneratedRegex]` source-generated patterns (compile-time optimized)
- `ExtractParenthesizedBody()` for nested paren handling (depth-tracked)
- `SplitTableBody()` splits table column/constraint definitions by commas (paren-aware)
- `ParseColumn()` extracts name, type, nullability, default, identity, generated expressions
- `ParseDomainDetails()` extracts base type, default, NOT NULL, CHECK for domain types

### Differ (`pg_deploy.Diff.SchemaDiffer`)

Sealed class that compares source (desired) and target (existing) schemas.

**Algorithm per object category:**

1. Build dictionaries keyed by qualified name for both source and target
2. **Additions:** keys in source not in target → generate `CREATE` or `ALTER ... ADD`
3. **Modifications:** keys in both → compare properties → generate `ALTER` where possible
4. **Drops:** keys in target not in source → generate `DROP` (only if `--allow-drops`)

**Category-specific diff strategies:**

| Category | Add | Modify | Drop |
|----------|-----|--------|------|
| Extension | `CREATE EXTENSION` | Version change warning | `DROP EXTENSION` |
| Schema | `CREATE SCHEMA` | `ALTER SCHEMA ... OWNER TO` | `DROP SCHEMA CASCADE` |
| Type (Enum) | `CREATE TYPE ... AS ENUM` | `ALTER TYPE ... ADD VALUE` per label | Warning (requires `DROP CASCADE`) |
| Type (Composite/Domain) | Raw DDL | `DROP TYPE` + recreate | `DROP TYPE` |
| Sequence | `CREATE SEQUENCE` | `ALTER SEQUENCE` per property | `DROP SEQUENCE` |
| Table | Raw DDL | Per-column/constraint diffs | `DROP TABLE CASCADE` |
| Column | `ALTER TABLE ... ADD COLUMN` | `ALTER COLUMN TYPE/SET/DROP` | `ALTER TABLE ... DROP COLUMN` |
| PK/Unique/Check | `ALTER TABLE ... ADD CONSTRAINT` | Drop + re-add | `DROP CONSTRAINT` |
| Index | Raw DDL | Drop + recreate | `DROP INDEX` |
| Foreign Key | Raw DDL | Drop + recreate | `DROP CONSTRAINT` |
| View | `CREATE OR REPLACE VIEW` | `CREATE OR REPLACE VIEW` | `DROP VIEW` |
| Materialized View | Raw DDL | Drop + recreate (warned) | `DROP MATERIALIZED VIEW` |
| Function/Procedure | `CREATE OR REPLACE` | `CREATE OR REPLACE` | `DROP FUNCTION/PROCEDURE` |
| Trigger | Raw DDL | Drop + recreate | `DROP TRIGGER` |

**Potential rename detection:** When a table has both added and dropped columns, the diff engine flags these as potential renames with a comment in the script.

### Script Generator (`pg_deploy.ScriptGeneration.ScriptGenerator`)

Produces a single `.sql` file with this structure:

```sql
/* ================================================================
   pg_deploy — Deployment Script
   Generated: 2026-04-08T18:22:00Z
   Source: /path/to/source (branch: main, remote: origin/...)
   Target: /path/to/target (branch: main, remote: origin/...)

   Summary: +3 tables, ~2 columns, +1 index, ...

   WARNINGS:
     Line 45: Column type change on "billing"."invoice"."amount" ...
     Line 89: Materialized view "public"."mv_summary" requires DROP+CREATE

   DESTRUCTIVE CHANGES:
     Line 120: DROP TABLE "legacy"."old_data" CASCADE
   ================================================================ */

BEGIN;

-- Non-destructive changes (dependency-ordered)
CREATE EXTENSION IF NOT EXISTS "pgcrypto" SCHEMA "public";
ALTER TABLE "billing"."invoice" ALTER COLUMN "amount" TYPE numeric(12,2);
...

-- ╔══════════════════════════════════════════════════════════════╗
-- ║  ⚠  DESTRUCTIVE CHANGES BELOW — REVIEW CAREFULLY           ║
-- ╚══════════════════════════════════════════════════════════════╝
DROP TABLE "legacy"."old_data" CASCADE;

COMMIT;
```

**Two-pass line numbering:**

1. Generate body SQL and assign preliminary line numbers
2. Generate header (which references those line numbers)
3. Offset all line numbers by the header's line count

### Git Integration (`pg_deploy.ScriptGeneration.GitInfo`)

Static helper that shells out to `git rev-parse` and `git remote -v` to extract branch name and remote URLs for both source and target folders. Fails gracefully (returns empty details) if git is not available or the folder is not a git repo.

---

## Dependency Ordering

The script generator emits changes in a fixed dependency order that respects PostgreSQL's object dependency graph:

```
 1. Extensions        (provide types/functions to all downstream objects)
 2. Schemas            (namespaces — must exist before objects within them)
 3. Types              (referenced by table columns, function signatures)
 4. Sequences          (referenced by column DEFAULT expressions)
 5. Tables             (core data structures; depend on types and sequences)
 6. Indexes            (depend on tables)
 7. Foreign Keys       (depend on both referencing and referenced tables)
 8. Views              (depend on tables; may depend on other views)
 9. Materialized Views (depend on tables)
10. Functions          (depend on types; referenced by triggers)
11. Triggers           (depend on both tables and functions)
```

**Limitation:** Within a category (e.g., views that depend on other views), no topological sort is performed. The tool relies on the source folder's file ordering being correct.

---

## CLI Options

| Flag | Alias | Required | Default | Description |
|------|-------|----------|---------|-------------|
| `--source` | `-s` | Yes | — | Folder containing desired (new) DDL files |
| `--target` | `-t` | Yes | — | Folder containing existing DDL (extracted from DB) |
| `--output` | `-o` | Yes | — | Output path for the generated SQL script |
| `--allow-drops` | — | No | `false` | Enable destructive changes (drops placed in marked section) |
| `--trust-source-folder` | — | No | `false` | Skip untrusted-source warning prompt |
| `--verbose` | `-v` | No | `false` | Verbose console output |
| `--quiet` | `-q` | No | `false` | Suppress all console output |

**Exit codes:** `0` = success, `1` = error or user abort.

---

## Security Architecture

### Threat Model

pg_deploy reads DDL text files and embeds portions of their content into a SQL script that will be executed against a production database. The primary attack surface is **malicious content in source DDL files** being passed through to the output script.

```
 Untrusted Input                    Trust Boundary                 Sensitive Output
┌─────────────────┐            ┌───────────────────┐           ┌──────────────────┐
│ Source DDL files │ ──parse──▶ │ DatabaseSchema    │ ──diff──▶ │ deployment.sql   │
│ (*.sql files)   │            │ (typed model)     │           │ (executed on DB) │
└─────────────────┘            └───────────────────┘           └──────────────────┘
        ▲                              ▲                               ▲
   Attack vector              Validation layer                  Target of attack
```

**Key insight:** Unlike Microsoft's sqlpackage (which works with compiled DACPAC models and never handles raw SQL text), pg_deploy works directly with SQL text files. Raw DDL from views, functions, materialized views, and triggers is embedded verbatim into the output script. This is an inherent architectural tradeoff of working with text-based DDL rather than a compiled schema model.

### Security Controls (Implemented)

#### 1. Source Folder Trust Prompt

**Threat:** Malicious DDL files injecting arbitrary SQL.

**Control:** When `--trust-source-folder` is not set, the tool displays a prominent yellow warning box and requires explicit `y`/`yes` confirmation before proceeding. This ensures interactive users are aware that source DDL content will be embedded directly into the deployment script.

**Behavior in non-interactive contexts:** CI/CD pipelines must explicitly pass `--trust-source-folder`, making the trust decision visible in the pipeline configuration.

#### 2. CHECK Constraint Expression Validation

**Threat:** A crafted CHECK constraint expression containing semicolons to inject additional SQL statements (e.g., `CHECK ((col > 0); DROP TABLE users; SELECT (1))`).

**Control:** The `IsCheckExpressionSafe()` method implements a character-by-character state machine that:

- Tracks whether the scanner is inside a single-quoted string literal (handling `''` escapes)
- Tracks parenthesis nesting depth
- **Rejects** any expression containing a semicolon outside a string literal
- **Rejects** expressions with unbalanced parentheses or unterminated strings

Unsafe expressions are replaced with a `/* SKIPPED */` comment and a warning in the script header. This is a **fail-closed** design — suspicious expressions are blocked rather than allowed.

#### 3. Enum Label Bounds Checking

**Threat:** `IndexOutOfRangeException` crash when an enum type has only one label, causing a denial-of-service or unexpected behavior.

**Control:** The enum diff logic checks `srcLabels.Count > 1` before accessing `srcLabels[1]` for the `BEFORE` clause. When there is only one label, a plain `ADD VALUE` without positional clause is emitted.

#### 4. Identifier Quoting

All PostgreSQL identifiers (schema, table, column, constraint names) are quoted with double quotes (`"schema"."table"`), preventing SQL injection via identifier names and avoiding keyword collisions.

#### 5. String Literal Escaping

Single quotes in enum labels and other string values are escaped by doubling (`'` → `''`), per PostgreSQL convention, before interpolation into generated SQL.

#### 6. Transactional Wrapping

All generated scripts are wrapped in `BEGIN;` / `COMMIT;`, ensuring atomic all-or-nothing deployment. If any statement fails, the entire transaction rolls back.

### Known Vulnerabilities (Unfixed)

The following issues were identified during a red-team security review and have **not yet been addressed**:

#### V1: Raw DDL Injection (Severity: High — Accepted Risk)

**Description:** Views, functions, procedures, materialized views, and triggers use `RawDdl` content from source files, embedded directly into the output script via `CREATE OR REPLACE` or drop+recreate patterns. A malicious `.sql` file in any of these directories can inject arbitrary SQL.

**Example attack:** A file `functions/public.innocent_func.sql` containing:

```sql
CREATE OR REPLACE FUNCTION "public"."innocent_func"()
RETURNS void AS $$
BEGIN
  DROP TABLE "billing"."transactions";
END;
$$ LANGUAGE plpgsql;

DROP DATABASE production; -- injected
```

The `DROP DATABASE` statement would appear verbatim in the deployment script.

**Mitigation status:** The `--trust-source-folder` prompt warns users, but does not prevent the injection. This is an **accepted architectural limitation** — the tool trusts that DDL files from the source folder are legitimate, as they typically come from a version-controlled repository.

**Recommendations:**
- Always review generated scripts before execution
- Use source folders from trusted, version-controlled repositories only
- Consider adding a `--dry-run` mode that only shows the change summary without writing the script
- Future: Parse raw DDL through a PostgreSQL syntax validator to detect multi-statement injection

#### V2: ReDoS in Domain Type Parsing (Severity: Medium)

**Description:** The domain type CHECK constraint regex (`\bCHECK\s*\(.+\)` with `RegexOptions.Singleline`) uses a greedy `.+` pattern that can exhibit catastrophic backtracking on pathologically crafted CHECK expressions with deeply nested or repeated patterns.

**Example attack:** A domain definition with a CHECK expression containing thousands of nested parentheses could cause the regex engine to consume excessive CPU time, effectively hanging the tool.

**Impact:** Denial of service (tool hangs during parsing). No data corruption or injection risk.

**Recommendations:**
- Replace `.+` with a paren-balanced extraction using `ExtractParenthesizedBody()` (already used for table bodies)
- Add a timeout to regex operations via `RegexOptions.NonBacktracking` (.NET 7+) or `Regex.MatchTimeout`
- Add input size limits on individual DDL files

#### V3: Path Traversal on Output File (Severity: Medium)

**Description:** The `--output` parameter is passed directly to `File.WriteAllText()` without path validation. An attacker who controls the `--output` argument (e.g., in a misconfigured CI pipeline) could write to arbitrary filesystem locations.

**Example:** `--output ../../etc/cron.d/malicious` (on Linux) or `--output C:\Windows\System32\evil.sql` (on Windows).

**Impact:** Arbitrary file write. Severity depends on the privileges of the user running the tool.

**Recommendations:**
- Validate that the output path is within the current working directory or an explicitly allowed location
- Resolve the path with `Path.GetFullPath()` and check it doesn't escape intended boundaries
- On Unix, check for symlink traversal

#### V4: Information Disclosure via Error Messages (Severity: Low)

**Description:** Exception messages and error output may include full filesystem paths (e.g., `Error: Source folder not found: C:\Users\admin\sensitive\project\ddl`). In shared CI/CD logs, this could leak directory structure or username information.

**Impact:** Minor information disclosure. No direct exploitation path.

**Recommendations:**
- In `--quiet` mode, suppress all path information in error output
- Consider showing only relative paths or folder names in error messages
- Sanitize exception messages before writing to stderr

### Security Architecture Summary

| Control | Threat | Status | Severity |
|---------|--------|--------|----------|
| Source trust prompt | Raw DDL injection | ✅ Implemented | High |
| CHECK expression validator | SQL injection via constraints | ✅ Implemented | High |
| Enum bounds check | DoS via crash | ✅ Fixed | Medium |
| Identifier quoting | Injection via identifiers | ✅ Implemented | Medium |
| String literal escaping | Injection via string values | ✅ Implemented | Medium |
| Transaction wrapping | Partial deployment corruption | ✅ Implemented | Medium |
| Raw DDL passthrough | Arbitrary SQL injection | ⚠️ Accepted risk | High |
| ReDoS in domain parsing | CPU denial of service | ❌ Not fixed | Medium |
| Output path traversal | Arbitrary file write | ❌ Not fixed | Medium |
| Error message paths | Information disclosure | ❌ Not fixed | Low |

---

## Test Architecture

**Framework:** xUnit 2.9.2 with coverlet for coverage collection.

**Test organization:**

| File | Tests | Focus |
|------|-------|-------|
| `DdlLoaderTests.cs` | ~34 | Parsing all 11 DDL object types, column parsing, constraint extraction, comments, edge cases |
| `SchemaDifferTests.cs` | ~64 | Diff detection for all categories, security validation (CHECK injection, enum bounds), potential rename flagging |

**Test pattern:** Each test creates temporary source/target directory trees, writes DDL files using `WriteSource()`/`WriteTarget()` helpers, loads schemas via `DdlLoader.Load()`, runs `SchemaDiffer.ComputeChanges()`, and asserts on the resulting `SchemaChange` list. Temporary directories are cleaned up via `IDisposable`.

**Coverage gaps:**

- No end-to-end CLI integration tests (Program.cs is not tested)
- No tests for `ScriptGenerator` output formatting
- No tests for `GitInfo` (depends on git CLI availability)
- No performance/stress tests with large schemas
- No tests for circular foreign key references or view dependency ordering
- No tests for concurrent execution
