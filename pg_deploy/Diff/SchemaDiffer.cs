using pg_deploy.Models;

namespace pg_deploy.Diff;

/// <summary>
/// Compares source (desired) and target (existing) schemas and produces a list of changes.
/// </summary>
public sealed class SchemaDiffer
{
    private readonly DatabaseSchema _source;
    private readonly DatabaseSchema _target;
    private readonly bool _allowDrops;

    /// <summary>
    /// Validates that a CHECK constraint expression doesn't contain SQL injection vectors.
    /// Semicolons outside of string literals or parenthesized blocks are rejected.
    /// </summary>
    private static bool IsCheckExpressionSafe(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        bool inString = false;
        int parenDepth = 0;

        for (int i = 0; i < expression.Length; i++)
        {
            char c = expression[i];

            if (inString)
            {
                if (c == '\'' && i + 1 < expression.Length && expression[i + 1] == '\'')
                    i++; /* escaped quote — skip */
                else if (c == '\'')
                    inString = false;
                continue;
            }

            switch (c)
            {
                case '\'':
                    inString = true;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    parenDepth--;
                    if (parenDepth < 0) return false;
                    break;
                case ';':
                    return false;
            }
        }

        return !inString && parenDepth == 0;
    }

    public SchemaDiffer(DatabaseSchema source, DatabaseSchema target, bool allowDrops)
    {
        _source = source;
        _target = target;
        _allowDrops = allowDrops;
    }

    public List<SchemaChange> ComputeChanges()
    {
        var changes = new List<SchemaChange>();

        DiffExtensions(changes);
        DiffSchemas(changes);
        DiffTypes(changes);
        DiffSequences(changes);
        DiffTables(changes);
        DiffIndexes(changes);
        DiffForeignKeys(changes);
        DiffViews(changes);
        DiffMaterializedViews(changes);
        DiffFunctions(changes);
        DiffTriggers(changes);

        return changes;
    }

    // ── Extensions ──────────────────────────────────────────────────

    private void DiffExtensions(List<SchemaChange> changes)
    {
        foreach (var (key, src) in _source.Extensions)
        {
            if (!_target.Extensions.ContainsKey(key))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Extension,
                    Action = ChangeAction.Add,
                    ObjectType = "EXTENSION",
                    ObjectName = src.Name,
                    Sql = src.RawDdl
                });
            }
        }

        if (_allowDrops)
        {
            foreach (var (key, tgt) in _target.Extensions)
            {
                if (!_source.Extensions.ContainsKey(key))
                {
                    changes.Add(new SchemaChange
                    {
                        Category = ChangeCategory.Extension,
                        Action = ChangeAction.Drop,
                        ObjectType = "EXTENSION",
                        ObjectName = tgt.Name,
                        Sql = $"DROP EXTENSION IF EXISTS \"{tgt.Name}\";",
                        IsDestructive = true
                    });
                }
            }
        }
    }

    // ── Schemas ─────────────────────────────────────────────────────

    private void DiffSchemas(List<SchemaChange> changes)
    {
        foreach (var (key, src) in _source.Schemas)
        {
            if (!_target.Schemas.TryGetValue(key, out var tgt))
            {
                var sql = $"CREATE SCHEMA IF NOT EXISTS \"{src.Name}\";";
                if (src.Owner != null)
                    sql += $"\n\nALTER SCHEMA \"{src.Name}\" OWNER TO \"{src.Owner}\";";

                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Schema,
                    Action = ChangeAction.Add,
                    ObjectType = "SCHEMA",
                    ObjectName = src.Name,
                    Sql = sql
                });
            }
            else if (src.Owner != null && !string.Equals(src.Owner, tgt.Owner, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Schema,
                    Action = ChangeAction.Modify,
                    ObjectType = "SCHEMA",
                    ObjectName = src.Name,
                    Sql = $"ALTER SCHEMA \"{src.Name}\" OWNER TO \"{src.Owner}\";"
                });
            }
        }

        if (_allowDrops)
        {
            foreach (var (key, tgt) in _target.Schemas)
            {
                if (!_source.Schemas.ContainsKey(key))
                {
                    changes.Add(new SchemaChange
                    {
                        Category = ChangeCategory.Schema,
                        Action = ChangeAction.Drop,
                        ObjectType = "SCHEMA",
                        ObjectName = tgt.Name,
                        Sql = $"DROP SCHEMA IF EXISTS \"{tgt.Name}\" CASCADE;",
                        IsDestructive = true,
                        Warning = "CASCADE will drop all objects in this schema!"
                    });
                }
            }
        }
    }

    // ── Types ───────────────────────────────────────────────────────

    private void DiffTypes(List<SchemaChange> changes)
    {
        foreach (var (key, src) in _source.Types)
        {
            if (!_target.Types.TryGetValue(key, out var tgt))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Type,
                    Action = ChangeAction.Add,
                    ObjectType = "TYPE",
                    ObjectName = src.QualifiedName,
                    Sql = src.RawDdl
                });
            }
            else if (!string.Equals(src.RawDdl, tgt.RawDdl, StringComparison.Ordinal))
            {
                DiffTypeModify(changes, src, tgt);
            }
        }

        if (_allowDrops)
        {
            foreach (var (key, tgt) in _target.Types)
            {
                if (!_source.Types.ContainsKey(key))
                {
                    changes.Add(new SchemaChange
                    {
                        Category = ChangeCategory.Type,
                        Action = ChangeAction.Drop,
                        ObjectType = "TYPE",
                        ObjectName = tgt.QualifiedName,
                        Sql = $"DROP TYPE IF EXISTS {tgt.QualifiedName} CASCADE;",
                        IsDestructive = true
                    });
                }
            }
        }
    }

    private static void DiffTypeModify(List<SchemaChange> changes, TypeDef src, TypeDef tgt)
    {
        if (src.Kind == TypeKind.Enum && tgt.Kind == TypeKind.Enum)
        {
            var srcLabels = src.EnumLabels ?? [];
            var tgtLabels = tgt.EnumLabels ?? [];

            var newLabels = srcLabels.Except(tgtLabels).ToList();
            var removedLabels = tgtLabels.Except(srcLabels).ToList();

            if (newLabels.Count > 0)
            {
                var sqlParts = new List<string>();
                foreach (var label in newLabels)
                {
                    var idx = srcLabels.IndexOf(label);
                    if (idx > 0)
                        sqlParts.Add($"ALTER TYPE {src.QualifiedName} ADD VALUE '{label.Replace("'", "''")}' AFTER '{srcLabels[idx - 1].Replace("'", "''")}';");
                    else if (srcLabels.Count > 1)
                        sqlParts.Add($"ALTER TYPE {src.QualifiedName} ADD VALUE '{label.Replace("'", "''")}' BEFORE '{srcLabels[1].Replace("'", "''")}';");
                    else
                        sqlParts.Add($"ALTER TYPE {src.QualifiedName} ADD VALUE '{label.Replace("'", "''")}';");
                }

                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Type,
                    Action = ChangeAction.Modify,
                    ObjectType = "TYPE (ENUM)",
                    ObjectName = src.QualifiedName,
                    Sql = string.Join("\n", sqlParts)
                });
            }

            if (removedLabels.Count > 0)
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Type,
                    Action = ChangeAction.Modify,
                    ObjectType = "TYPE (ENUM)",
                    ObjectName = src.QualifiedName,
                    Sql = $"/* WARNING: Enum labels removed: {string.Join(", ", removedLabels)} */\n" +
                          $"/* PostgreSQL does not support removing enum values. Manual intervention required. */\n" +
                          $"/* Consider: DROP TYPE {src.QualifiedName} CASCADE; then recreate. */",
                    Warning = $"Cannot remove enum labels ({string.Join(", ", removedLabels)}) — requires DROP+CREATE which cascades to dependent columns."
                });
            }
        }
        else
        {
            changes.Add(new SchemaChange
            {
                Category = ChangeCategory.Type,
                Action = ChangeAction.Modify,
                ObjectType = "TYPE",
                ObjectName = src.QualifiedName,
                Sql = $"DROP TYPE IF EXISTS {tgt.QualifiedName} CASCADE;\n\n{src.RawDdl}",
                Warning = "Type definition changed — requires DROP+CREATE which may cascade."
            });
        }
    }

    // ── Sequences ───────────────────────────────────────────────────

    private void DiffSequences(List<SchemaChange> changes)
    {
        foreach (var (key, src) in _source.Sequences)
        {
            if (!_target.Sequences.TryGetValue(key, out var tgt))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Sequence,
                    Action = ChangeAction.Add,
                    ObjectType = "SEQUENCE",
                    ObjectName = src.QualifiedName,
                    Sql = src.RawDdl
                });
            }
            else
            {
                var alterParts = new List<string>();
                var qn = src.QualifiedName;

                if (!string.Equals(src.DataType, tgt.DataType, StringComparison.OrdinalIgnoreCase) && src.DataType != null)
                    alterParts.Add($"ALTER SEQUENCE {qn} AS {src.DataType};");
                if (src.IncrementBy != tgt.IncrementBy && src.IncrementBy != null)
                    alterParts.Add($"ALTER SEQUENCE {qn} INCREMENT BY {src.IncrementBy};");
                if (src.MinValue != tgt.MinValue && src.MinValue != null)
                    alterParts.Add($"ALTER SEQUENCE {qn} MINVALUE {src.MinValue};");
                if (src.MaxValue != tgt.MaxValue && src.MaxValue != null)
                    alterParts.Add($"ALTER SEQUENCE {qn} MAXVALUE {src.MaxValue};");
                if (src.Cycle != tgt.Cycle)
                    alterParts.Add($"ALTER SEQUENCE {qn} {(src.Cycle ? "CYCLE" : "NO CYCLE")};");

                if (alterParts.Count > 0)
                {
                    changes.Add(new SchemaChange
                    {
                        Category = ChangeCategory.Sequence,
                        Action = ChangeAction.Modify,
                        ObjectType = "SEQUENCE",
                        ObjectName = qn,
                        Sql = string.Join("\n", alterParts)
                    });
                }
            }
        }

        if (_allowDrops)
        {
            foreach (var (key, tgt) in _target.Sequences)
            {
                if (!_source.Sequences.ContainsKey(key))
                {
                    changes.Add(new SchemaChange
                    {
                        Category = ChangeCategory.Sequence,
                        Action = ChangeAction.Drop,
                        ObjectType = "SEQUENCE",
                        ObjectName = tgt.QualifiedName,
                        Sql = $"DROP SEQUENCE IF EXISTS {tgt.QualifiedName};",
                        IsDestructive = true
                    });
                }
            }
        }
    }

    // ── Tables ──────────────────────────────────────────────────────

    private void DiffTables(List<SchemaChange> changes)
    {
        foreach (var (key, src) in _source.Tables)
        {
            if (!_target.Tables.TryGetValue(key, out var tgt))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Table,
                    Action = ChangeAction.Add,
                    ObjectType = "TABLE",
                    ObjectName = src.QualifiedName,
                    Sql = src.RawDdl
                });
            }
            else
            {
                DiffTableColumns(changes, src, tgt);
                DiffTableConstraints(changes, src, tgt);
                DiffTableComments(changes, src, tgt);
            }
        }

        if (_allowDrops)
        {
            foreach (var (key, tgt) in _target.Tables)
            {
                if (!_source.Tables.ContainsKey(key))
                {
                    changes.Add(new SchemaChange
                    {
                        Category = ChangeCategory.Table,
                        Action = ChangeAction.Drop,
                        ObjectType = "TABLE",
                        ObjectName = tgt.QualifiedName,
                        Sql = $"DROP TABLE IF EXISTS {tgt.QualifiedName} CASCADE;",
                        IsDestructive = true,
                        Warning = "CASCADE will drop dependent objects (FKs, views, etc.)"
                    });
                }
            }
        }
    }

    private void DiffTableColumns(List<SchemaChange> changes, TableDef src, TableDef tgt)
    {
        var srcCols = src.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var tgtCols = tgt.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        var addedCols = new List<string>();
        var droppedCols = new List<string>();

        // New columns
        foreach (var (name, col) in srcCols)
        {
            if (!tgtCols.ContainsKey(name))
            {
                var colDef = $"\"{col.Name}\" {col.DataType}";
                if (col.IdentityType != null)
                    colDef += $" GENERATED {col.IdentityType} AS IDENTITY";
                else if (col.GeneratedExpr != null)
                    colDef += $" GENERATED ALWAYS AS ({col.GeneratedExpr}) STORED";
                else if (col.Default != null)
                    colDef += $" DEFAULT {col.Default}";
                if (col.NotNull)
                    colDef += " NOT NULL";

                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Table,
                    Action = ChangeAction.Add,
                    ObjectType = "COLUMN",
                    ObjectName = $"{src.QualifiedName}.\"{col.Name}\"",
                    Sql = $"ALTER TABLE {src.QualifiedName} ADD COLUMN {colDef};"
                });
                addedCols.Add(name);
            }
        }

        // Dropped columns
        if (_allowDrops)
        {
            foreach (var (name, col) in tgtCols)
            {
                if (!srcCols.ContainsKey(name))
                {
                    changes.Add(new SchemaChange
                    {
                        Category = ChangeCategory.Table,
                        Action = ChangeAction.Drop,
                        ObjectType = "COLUMN",
                        ObjectName = $"{src.QualifiedName}.\"{col.Name}\"",
                        Sql = $"ALTER TABLE {src.QualifiedName} DROP COLUMN IF EXISTS \"{col.Name}\";",
                        IsDestructive = true
                    });
                    droppedCols.Add(name);
                }
            }
        }
        else
        {
            droppedCols = tgtCols.Keys.Where(k => !srcCols.ContainsKey(k)).ToList();
        }

        // Flag potential renames
        if (addedCols.Count > 0 && droppedCols.Count > 0)
        {
            var warning = $"Table {src.QualifiedName} has both added columns ({string.Join(", ", addedCols)}) and " +
                          $"dropped columns ({string.Join(", ", droppedCols)}) — these may be renames.";
            changes.Add(new SchemaChange
            {
                Category = ChangeCategory.Table,
                Action = ChangeAction.Modify,
                ObjectType = "COLUMN (POTENTIAL RENAME)",
                ObjectName = src.QualifiedName,
                Sql = $"/* {warning} */\n/* Use: ALTER TABLE {src.QualifiedName} RENAME COLUMN \"old_name\" TO \"new_name\"; */",
                Warning = warning
            });
        }

        // Modified columns (type, default, nullability)
        foreach (var (name, srcCol) in srcCols)
        {
            if (!tgtCols.TryGetValue(name, out var tgtCol)) continue;

            var alterParts = new List<string>();
            string? warning = null;

            if (!string.Equals(srcCol.DataType, tgtCol.DataType, StringComparison.OrdinalIgnoreCase))
            {
                alterParts.Add($"ALTER TABLE {src.QualifiedName} ALTER COLUMN \"{name}\" TYPE {srcCol.DataType};");
                warning = $"Column {src.QualifiedName}.\"{name}\" type change: {tgtCol.DataType} -> {srcCol.DataType}";
            }

            if (!string.Equals(srcCol.Default, tgtCol.Default, StringComparison.Ordinal))
            {
                if (srcCol.Default != null)
                    alterParts.Add($"ALTER TABLE {src.QualifiedName} ALTER COLUMN \"{name}\" SET DEFAULT {srcCol.Default};");
                else
                    alterParts.Add($"ALTER TABLE {src.QualifiedName} ALTER COLUMN \"{name}\" DROP DEFAULT;");
            }

            if (srcCol.NotNull != tgtCol.NotNull)
            {
                if (srcCol.NotNull)
                    alterParts.Add($"ALTER TABLE {src.QualifiedName} ALTER COLUMN \"{name}\" SET NOT NULL;");
                else
                    alterParts.Add($"ALTER TABLE {src.QualifiedName} ALTER COLUMN \"{name}\" DROP NOT NULL;");
            }

            if (alterParts.Count > 0)
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Table,
                    Action = ChangeAction.Modify,
                    ObjectType = "COLUMN",
                    ObjectName = $"{src.QualifiedName}.\"{name}\"",
                    Sql = string.Join("\n", alterParts),
                    Warning = warning
                });
            }
        }
    }

    private void DiffTableConstraints(List<SchemaChange> changes, TableDef src, TableDef tgt)
    {
        // Primary key changes
        if (src.PrimaryKey != null && tgt.PrimaryKey != null)
        {
            if (!src.PrimaryKey.Columns.SequenceEqual(tgt.PrimaryKey.Columns, StringComparer.OrdinalIgnoreCase) ||
                !string.Equals(src.PrimaryKey.Name, tgt.PrimaryKey.Name, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Table,
                    Action = ChangeAction.Modify,
                    ObjectType = "PRIMARY KEY",
                    ObjectName = $"{src.QualifiedName} ({src.PrimaryKey.Name})",
                    Sql = $"ALTER TABLE {src.QualifiedName} DROP CONSTRAINT IF EXISTS \"{tgt.PrimaryKey.Name}\";\n" +
                          $"ALTER TABLE {src.QualifiedName} ADD CONSTRAINT \"{src.PrimaryKey.Name}\" PRIMARY KEY ({string.Join(", ", src.PrimaryKey.Columns.Select(c => $"\"{c}\""))});",
                    Warning = "Primary key change — this may affect foreign key references."
                });
            }
        }
        else if (src.PrimaryKey != null && tgt.PrimaryKey == null)
        {
            changes.Add(new SchemaChange
            {
                Category = ChangeCategory.Table,
                Action = ChangeAction.Add,
                ObjectType = "PRIMARY KEY",
                ObjectName = $"{src.QualifiedName} ({src.PrimaryKey.Name})",
                Sql = $"ALTER TABLE {src.QualifiedName} ADD CONSTRAINT \"{src.PrimaryKey.Name}\" PRIMARY KEY ({string.Join(", ", src.PrimaryKey.Columns.Select(c => $"\"{c}\""))});"
            });
        }
        else if (src.PrimaryKey == null && tgt.PrimaryKey != null && _allowDrops)
        {
            changes.Add(new SchemaChange
            {
                Category = ChangeCategory.Table,
                Action = ChangeAction.Drop,
                ObjectType = "PRIMARY KEY",
                ObjectName = $"{src.QualifiedName} ({tgt.PrimaryKey.Name})",
                Sql = $"ALTER TABLE {src.QualifiedName} DROP CONSTRAINT IF EXISTS \"{tgt.PrimaryKey.Name}\";",
                IsDestructive = true
            });
        }

        // Unique constraints
        var srcUqs = src.UniqueConstraints.ToDictionary(u => u.Name, StringComparer.OrdinalIgnoreCase);
        var tgtUqs = tgt.UniqueConstraints.ToDictionary(u => u.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, srcUq) in srcUqs)
        {
            if (!tgtUqs.TryGetValue(name, out var tgtUq))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Table,
                    Action = ChangeAction.Add,
                    ObjectType = "UNIQUE CONSTRAINT",
                    ObjectName = $"{src.QualifiedName} ({name})",
                    Sql = $"ALTER TABLE {src.QualifiedName} ADD CONSTRAINT \"{name}\" UNIQUE ({string.Join(", ", srcUq.Columns.Select(c => $"\"{c}\""))});"
                });
            }
            else if (!srcUq.Columns.SequenceEqual(tgtUq.Columns, StringComparer.OrdinalIgnoreCase))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Table,
                    Action = ChangeAction.Modify,
                    ObjectType = "UNIQUE CONSTRAINT",
                    ObjectName = $"{src.QualifiedName} ({name})",
                    Sql = $"ALTER TABLE {src.QualifiedName} DROP CONSTRAINT IF EXISTS \"{name}\";\n" +
                          $"ALTER TABLE {src.QualifiedName} ADD CONSTRAINT \"{name}\" UNIQUE ({string.Join(", ", srcUq.Columns.Select(c => $"\"{c}\""))});"
                });
            }
        }

        if (_allowDrops)
        {
            foreach (var (name, _) in tgtUqs)
            {
                if (!srcUqs.ContainsKey(name))
                {
                    changes.Add(new SchemaChange
                    {
                        Category = ChangeCategory.Table,
                        Action = ChangeAction.Drop,
                        ObjectType = "UNIQUE CONSTRAINT",
                        ObjectName = $"{src.QualifiedName} ({name})",
                        Sql = $"ALTER TABLE {src.QualifiedName} DROP CONSTRAINT IF EXISTS \"{name}\";",
                        IsDestructive = true
                    });
                }
            }
        }

        // Check constraints
        var srcCks = src.CheckConstraints.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var tgtCks = tgt.CheckConstraints.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, srcCk) in srcCks)
        {
            if (!IsCheckExpressionSafe(srcCk.Expression))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Table,
                    Action = ChangeAction.Add,
                    ObjectType = "CHECK CONSTRAINT",
                    ObjectName = $"{src.QualifiedName} ({name})",
                    Sql = $"/* SKIPPED: CHECK constraint \"{name}\" has a potentially unsafe expression */",
                    Warning = $"CHECK constraint \"{name}\" on {src.QualifiedName} was skipped — expression contains suspicious characters (possible SQL injection)"
                });
            }
            else if (!tgtCks.TryGetValue(name, out var tgtCk))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Table,
                    Action = ChangeAction.Add,
                    ObjectType = "CHECK CONSTRAINT",
                    ObjectName = $"{src.QualifiedName} ({name})",
                    Sql = $"ALTER TABLE {src.QualifiedName} ADD CONSTRAINT \"{name}\" {srcCk.Expression};"
                });
            }
            else if (!string.Equals(srcCk.Expression, tgtCk.Expression, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Table,
                    Action = ChangeAction.Modify,
                    ObjectType = "CHECK CONSTRAINT",
                    ObjectName = $"{src.QualifiedName} ({name})",
                    Sql = $"ALTER TABLE {src.QualifiedName} DROP CONSTRAINT IF EXISTS \"{name}\";\n" +
                          $"ALTER TABLE {src.QualifiedName} ADD CONSTRAINT \"{name}\" {srcCk.Expression};"
                });
            }
        }

        if (_allowDrops)
        {
            foreach (var (name, _) in tgtCks)
            {
                if (!srcCks.ContainsKey(name))
                {
                    changes.Add(new SchemaChange
                    {
                        Category = ChangeCategory.Table,
                        Action = ChangeAction.Drop,
                        ObjectType = "CHECK CONSTRAINT",
                        ObjectName = $"{src.QualifiedName} ({name})",
                        Sql = $"ALTER TABLE {src.QualifiedName} DROP CONSTRAINT IF EXISTS \"{name}\";",
                        IsDestructive = true
                    });
                }
            }
        }
    }

    private static void DiffTableComments(List<SchemaChange> changes, TableDef src, TableDef tgt)
    {
        // Table comment
        if (!string.Equals(src.TableComment, tgt.TableComment, StringComparison.Ordinal))
        {
            if (src.TableComment != null)
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Table,
                    Action = ChangeAction.Modify,
                    ObjectType = "TABLE COMMENT",
                    ObjectName = src.QualifiedName,
                    Sql = $"COMMENT ON TABLE {src.QualifiedName} IS '{src.TableComment.Replace("'", "''")}';",
                });
            }
            else
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Table,
                    Action = ChangeAction.Modify,
                    ObjectType = "TABLE COMMENT",
                    ObjectName = src.QualifiedName,
                    Sql = $"COMMENT ON TABLE {src.QualifiedName} IS NULL;",
                });
            }
        }

        // Column comments
        var srcComments = src.Comments.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
        var tgtComments = tgt.Comments.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);

        foreach (var (colName, srcComment) in srcComments)
        {
            if (!tgtComments.TryGetValue(colName, out var tgtComment) ||
                !string.Equals(srcComment.Comment, tgtComment.Comment, StringComparison.Ordinal))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Table,
                    Action = ChangeAction.Modify,
                    ObjectType = "COLUMN COMMENT",
                    ObjectName = $"{src.QualifiedName}.\"{colName}\"",
                    Sql = $"COMMENT ON COLUMN {src.QualifiedName}.\"{colName}\" IS '{srcComment.Comment.Replace("'", "''")}';",
                });
            }
        }

        foreach (var (colName, _) in tgtComments)
        {
            if (!srcComments.ContainsKey(colName))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Table,
                    Action = ChangeAction.Modify,
                    ObjectType = "COLUMN COMMENT",
                    ObjectName = $"{src.QualifiedName}.\"{colName}\"",
                    Sql = $"COMMENT ON COLUMN {src.QualifiedName}.\"{colName}\" IS NULL;",
                });
            }
        }
    }

    // ── Indexes ─────────────────────────────────────────────────────

    private void DiffIndexes(List<SchemaChange> changes)
    {
        foreach (var (key, src) in _source.Indexes)
        {
            if (!_target.Indexes.TryGetValue(key, out var tgt))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Index,
                    Action = ChangeAction.Add,
                    ObjectType = "INDEX",
                    ObjectName = src.QualifiedName,
                    Sql = src.RawDdl
                });
            }
            else if (!string.Equals(src.RawDdl, tgt.RawDdl, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Index,
                    Action = ChangeAction.Modify,
                    ObjectType = "INDEX",
                    ObjectName = src.QualifiedName,
                    Sql = $"DROP INDEX IF EXISTS \"{src.Schema}\".\"{src.IndexName}\";\n\n{src.RawDdl}"
                });
            }
        }

        if (_allowDrops)
        {
            foreach (var (key, tgt) in _target.Indexes)
            {
                if (!_source.Indexes.ContainsKey(key))
                {
                    changes.Add(new SchemaChange
                    {
                        Category = ChangeCategory.Index,
                        Action = ChangeAction.Drop,
                        ObjectType = "INDEX",
                        ObjectName = tgt.QualifiedName,
                        Sql = $"DROP INDEX IF EXISTS \"{tgt.Schema}\".\"{tgt.IndexName}\";",
                        IsDestructive = true
                    });
                }
            }
        }
    }

    // ── Foreign Keys ────────────────────────────────────────────────

    private void DiffForeignKeys(List<SchemaChange> changes)
    {
        foreach (var (key, src) in _source.ForeignKeys)
        {
            if (!_target.ForeignKeys.TryGetValue(key, out var tgt))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.ForeignKey,
                    Action = ChangeAction.Add,
                    ObjectType = "FOREIGN KEY",
                    ObjectName = src.QualifiedName,
                    Sql = src.RawDdl
                });
            }
            else if (!string.Equals(src.Definition, tgt.Definition, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.ForeignKey,
                    Action = ChangeAction.Modify,
                    ObjectType = "FOREIGN KEY",
                    ObjectName = src.QualifiedName,
                    Sql = $"ALTER TABLE \"{src.Schema}\".\"{src.TableName}\" DROP CONSTRAINT IF EXISTS \"{src.ConstraintName}\";\n\n{src.RawDdl}"
                });
            }
        }

        if (_allowDrops)
        {
            foreach (var (key, tgt) in _target.ForeignKeys)
            {
                if (!_source.ForeignKeys.ContainsKey(key))
                {
                    changes.Add(new SchemaChange
                    {
                        Category = ChangeCategory.ForeignKey,
                        Action = ChangeAction.Drop,
                        ObjectType = "FOREIGN KEY",
                        ObjectName = tgt.QualifiedName,
                        Sql = $"ALTER TABLE \"{tgt.Schema}\".\"{tgt.TableName}\" DROP CONSTRAINT IF EXISTS \"{tgt.ConstraintName}\";",
                        IsDestructive = true
                    });
                }
            }
        }
    }

    // ── Views ───────────────────────────────────────────────────────

    private void DiffViews(List<SchemaChange> changes)
    {
        foreach (var (key, src) in _source.Views)
        {
            if (!_target.Views.TryGetValue(key, out var tgt))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.View,
                    Action = ChangeAction.Add,
                    ObjectType = "VIEW",
                    ObjectName = src.QualifiedName,
                    Sql = src.RawDdl
                });
            }
            else if (!string.Equals(src.RawDdl, tgt.RawDdl, StringComparison.Ordinal))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.View,
                    Action = ChangeAction.Modify,
                    ObjectType = "VIEW",
                    ObjectName = src.QualifiedName,
                    Sql = src.RawDdl
                });
            }
        }

        if (_allowDrops)
        {
            foreach (var (key, tgt) in _target.Views)
            {
                if (!_source.Views.ContainsKey(key))
                {
                    changes.Add(new SchemaChange
                    {
                        Category = ChangeCategory.View,
                        Action = ChangeAction.Drop,
                        ObjectType = "VIEW",
                        ObjectName = tgt.QualifiedName,
                        Sql = $"DROP VIEW IF EXISTS {tgt.QualifiedName};",
                        IsDestructive = true
                    });
                }
            }
        }
    }

    // ── Materialized Views ──────────────────────────────────────────

    private void DiffMaterializedViews(List<SchemaChange> changes)
    {
        foreach (var (key, src) in _source.MaterializedViews)
        {
            if (!_target.MaterializedViews.TryGetValue(key, out var tgt))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.MaterializedView,
                    Action = ChangeAction.Add,
                    ObjectType = "MATERIALIZED VIEW",
                    ObjectName = src.QualifiedName,
                    Sql = src.RawDdl
                });
            }
            else if (!string.Equals(src.RawDdl, tgt.RawDdl, StringComparison.Ordinal))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.MaterializedView,
                    Action = ChangeAction.Modify,
                    ObjectType = "MATERIALIZED VIEW",
                    ObjectName = src.QualifiedName,
                    Sql = $"DROP MATERIALIZED VIEW IF EXISTS {tgt.QualifiedName};\n\n{src.RawDdl}",
                    Warning = $"Materialized view {src.QualifiedName} requires DROP+CREATE (CREATE OR REPLACE not supported)."
                });
            }
        }

        if (_allowDrops)
        {
            foreach (var (key, tgt) in _target.MaterializedViews)
            {
                if (!_source.MaterializedViews.ContainsKey(key))
                {
                    changes.Add(new SchemaChange
                    {
                        Category = ChangeCategory.MaterializedView,
                        Action = ChangeAction.Drop,
                        ObjectType = "MATERIALIZED VIEW",
                        ObjectName = tgt.QualifiedName,
                        Sql = $"DROP MATERIALIZED VIEW IF EXISTS {tgt.QualifiedName};",
                        IsDestructive = true
                    });
                }
            }
        }
    }

    // ── Functions & Procedures ──────────────────────────────────────

    private void DiffFunctions(List<SchemaChange> changes)
    {
        foreach (var (key, src) in _source.Functions)
        {
            if (!_target.Functions.TryGetValue(key, out var tgt))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Function,
                    Action = ChangeAction.Add,
                    ObjectType = src.Kind.ToUpperInvariant(),
                    ObjectName = src.QualifiedName,
                    Sql = src.RawDdl
                });
            }
            else if (!string.Equals(src.RawDdl, tgt.RawDdl, StringComparison.Ordinal))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Function,
                    Action = ChangeAction.Modify,
                    ObjectType = src.Kind.ToUpperInvariant(),
                    ObjectName = src.QualifiedName,
                    Sql = src.RawDdl
                });
            }
        }

        if (_allowDrops)
        {
            foreach (var (key, tgt) in _target.Functions)
            {
                if (!_source.Functions.ContainsKey(key))
                {
                    var dropKw = tgt.Kind.Equals("procedure", StringComparison.OrdinalIgnoreCase) ? "PROCEDURE" : "FUNCTION";
                    changes.Add(new SchemaChange
                    {
                        Category = ChangeCategory.Function,
                        Action = ChangeAction.Drop,
                        ObjectType = dropKw,
                        ObjectName = tgt.QualifiedName,
                        Sql = $"DROP {dropKw} IF EXISTS {tgt.QualifiedName};",
                        IsDestructive = true
                    });
                }
            }
        }
    }

    // ── Triggers ────────────────────────────────────────────────────

    private void DiffTriggers(List<SchemaChange> changes)
    {
        foreach (var (key, src) in _source.Triggers)
        {
            if (!_target.Triggers.TryGetValue(key, out var tgt))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Trigger,
                    Action = ChangeAction.Add,
                    ObjectType = "TRIGGER",
                    ObjectName = $"\"{src.Schema}\".\"{src.Name}\"",
                    Sql = src.RawDdl
                });
            }
            else if (!string.Equals(src.RawDdl, tgt.RawDdl, StringComparison.Ordinal))
            {
                changes.Add(new SchemaChange
                {
                    Category = ChangeCategory.Trigger,
                    Action = ChangeAction.Modify,
                    ObjectType = "TRIGGER",
                    ObjectName = $"\"{src.Schema}\".\"{src.Name}\"",
                    Sql = $"DROP TRIGGER IF EXISTS \"{tgt.Name}\" ON \"{tgt.Schema}\".\"{tgt.TableName}\";\n\n{src.RawDdl}"
                });
            }
        }

        if (_allowDrops)
        {
            foreach (var (key, tgt) in _target.Triggers)
            {
                if (!_source.Triggers.ContainsKey(key))
                {
                    changes.Add(new SchemaChange
                    {
                        Category = ChangeCategory.Trigger,
                        Action = ChangeAction.Drop,
                        ObjectType = "TRIGGER",
                        ObjectName = $"\"{tgt.Schema}\".\"{tgt.Name}\"",
                        Sql = $"DROP TRIGGER IF EXISTS \"{tgt.Name}\" ON \"{tgt.Schema}\".\"{tgt.TableName}\";",
                        IsDestructive = true
                    });
                }
            }
        }
    }
}
