# pg_deploy

A cross-platform .NET 9 CLI tool that generates incremental PostgreSQL deployment scripts by comparing DDL folders.

## Overview

`pg_deploy` compares a **source** folder (containing desired/new DDL) against a **target** folder (containing existing DDL extracted from a database) and produces a single SQL deployment script with only the changes needed to bring the target in line with the source.

Both folders should be in the format produced by [`pg-extract-schema`](https://github.com/HannahVernon/pg-extract-schema), with subdirectories per object type (`tables/`, `views/`, `functions/`, etc.).

### Key Features

- **Incremental changes only** — uses `ALTER` where possible instead of `DROP`+`CREATE`
- **Smart column diffing** — detects added/removed columns, type changes, default changes, nullability changes
- **Potential rename detection** — flags tables with both added and dropped columns as possible renames
- **Destructive change control** — drops require `--allow-drops` flag and are placed in a clearly-marked section
- **Transaction-wrapped** — output script uses `BEGIN`/`COMMIT` for atomic deployment
- **Type change warnings** — flags potentially problematic data type changes with line numbers
- **Git-aware** — includes git branch and remote info in the script header when available
- **Change summary** — header lists counts of adds, modifies, and drops per object type

## Supported Object Types

| Object Type | Add | Modify | Drop | Strategy |
|---|---|---|---|---|
| Extensions | ✅ | — | ✅ | `CREATE EXTENSION IF NOT EXISTS` / `DROP EXTENSION` |
| Schemas | ✅ | ✅ (owner) | ✅ | `CREATE SCHEMA` / `ALTER SCHEMA OWNER TO` |
| Types (enum) | ✅ | ✅ | ✅ | `ALTER TYPE ADD VALUE` for new labels |
| Types (composite/domain) | ✅ | ✅ | ✅ | `DROP`+`CREATE` (no ALTER support) |
| Sequences | ✅ | ✅ | ✅ | `ALTER SEQUENCE` for property changes |
| Tables | ✅ | ✅ | ✅ | `ALTER TABLE` for columns/constraints |
| Indexes | ✅ | ✅ | ✅ | `DROP`+`CREATE` (indexes are always recreated) |
| Foreign Keys | ✅ | ✅ | ✅ | `DROP`+`ADD CONSTRAINT` |
| Views | ✅ | ✅ | ✅ | `CREATE OR REPLACE VIEW` |
| Materialized Views | ✅ | ✅ | ✅ | `DROP`+`CREATE` (no REPLACE support) |
| Functions/Procedures | ✅ | ✅ | ✅ | `CREATE OR REPLACE FUNCTION/PROCEDURE` |
| Triggers | ✅ | ✅ | ✅ | `DROP`+`CREATE TRIGGER` |

## Installation

```bash
dotnet build -c Release
```

The executable will be at `bin/Release/net9.0/pg_deploy.exe` (Windows) or `bin/Release/net9.0/pg_deploy` (Linux/macOS).

## Usage

```bash
pg_deploy --source <source-ddl-folder> --target <target-ddl-folder> --output <output-script.sql> [options]
```

### Parameters

| Parameter | Alias | Required | Description |
|---|---|---|---|
| `--source` | `-s` | Yes | Folder containing new/desired DDL files |
| `--target` | `-t` | Yes | Folder containing existing DDL (extracted from DB) |
| `--output` | `-o` | Yes | Output path for the generated SQL script |
| `--allow-drops` | | No | Enable destructive changes (default: off) |
| `--trust-source-folder` | | No | Skip untrusted-source warning prompt (required for non-interactive use) |
| `--verbose` | `-v` | No | Verbose console output |
| `--quiet` | `-q` | No | Suppress all console output |

### Examples

Generate a deployment script (safe mode — no drops):
```bash
pg_deploy -s ./new-ddl -t ./current-ddl -o ./deploy.sql
```

Generate with destructive changes enabled:
```bash
pg_deploy -s ./new-ddl -t ./current-ddl -o ./deploy.sql --allow-drops
```

Non-interactive / CI usage (skip source trust prompt):
```bash
pg_deploy -s ./new-ddl -t ./current-ddl -o ./deploy.sql --trust-source-folder
```

Verbose output for debugging:
```bash
pg_deploy -s ./new-ddl -t ./current-ddl -o ./deploy.sql -v
```

### Typical Workflow

1. Extract current DDL from the database:
   ```bash
   pg-extract-schema -h myhost -d mydb -o ./current-ddl -U myuser
   ```

2. Make your DDL changes in a separate folder (or use the DDL from a dev branch).

3. Generate the deployment script:
   ```bash
   pg_deploy -s ./updated-ddl -t ./current-ddl -o ./deploy.sql --allow-drops
   ```

4. Review the generated script, paying attention to:
   - Warnings in the header (type changes, potential renames)
   - The destructive changes section at the bottom

5. Execute the script against your database:
   ```bash
   psql -h myhost -d mydb -U myuser -f ./deploy.sql
   ```

## Script Output Format

The generated script includes:

- **Header comment** with:
  - Generation timestamp
  - Source and target folder paths
  - Git branch and remote info (if available)
  - Change summary (counts per object type)
  - Warnings for risky changes (with line numbers)
  - Destructive change listing (with line numbers)

- **Body** wrapped in `BEGIN`/`COMMIT` with changes ordered by dependency:
  1. Extensions
  2. Schemas
  3. Types
  4. Sequences
  5. Tables (columns, constraints, comments)
  6. Indexes
  7. Foreign Keys
  8. Views
  9. Materialized Views
  10. Functions/Procedures
  11. Triggers

- **Destructive changes section** (when `--allow-drops` is used) — clearly marked at the bottom

## Security

- **Source folder trust**: DDL files from the source folder are embedded into the generated SQL script. Without `--trust-source-folder`, the tool displays a warning and prompts for confirmation before proceeding. Always verify the provenance of your source DDL files.
- **CHECK constraint validation**: CHECK constraint expressions are validated to reject semicolons outside string literals, preventing SQL injection via crafted constraint definitions.
- **Review before executing**: Always review the generated deployment script before running it against a production database.

## Building & Testing

```bash
dotnet build
dotnet test
```

## License

See [LICENSE](LICENSE) for details.
