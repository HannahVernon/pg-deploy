using System.Text.RegularExpressions;
using pg_deploy.Models;

namespace pg_deploy.Parsing;

/// <summary>
/// Loads DDL files from a pg-extract-schema output folder into a DatabaseSchema model.
/// </summary>
public static partial class DdlLoader
{
    public static DatabaseSchema Load(string folderPath)
    {
        var schema = new DatabaseSchema();

        LoadExtensions(schema, folderPath);
        LoadSchemas(schema, folderPath);
        LoadTypes(schema, folderPath);
        LoadSequences(schema, folderPath);
        LoadTables(schema, folderPath);
        LoadIndexes(schema, folderPath);
        LoadForeignKeys(schema, folderPath);
        LoadViews(schema, folderPath);
        LoadMaterializedViews(schema, folderPath);
        LoadFunctions(schema, folderPath, "functions", "function");
        LoadFunctions(schema, folderPath, "procedures", "procedure");
        LoadTriggers(schema, folderPath);

        return schema;
    }

    private static IEnumerable<(string fileName, string content)> ReadSqlFiles(string folderPath, string subdir)
    {
        var dir = Path.Combine(folderPath, subdir);
        if (!Directory.Exists(dir))
            yield break;

        foreach (var file in Directory.GetFiles(dir, "*.sql").OrderBy(f => f))
            yield return (Path.GetFileName(file), File.ReadAllText(file));
    }

    // ── Extensions ──────────────────────────────────────────────────

    [GeneratedRegex(@"CREATE\s+EXTENSION\s+IF\s+NOT\s+EXISTS\s+""([^""]+)""\s+SCHEMA\s+""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ExtensionRegex();

    private static void LoadExtensions(DatabaseSchema schema, string folderPath)
    {
        foreach (var (fileName, content) in ReadSqlFiles(folderPath, "extensions"))
        {
            var m = ExtensionRegex().Match(content);
            if (m.Success)
            {
                var ext = new ExtensionDef
                {
                    Name = m.Groups[1].Value,
                    Schema = m.Groups[2].Value,
                    RawDdl = content.Trim(),
                    FileName = fileName
                };
                schema.Extensions[ext.Name] = ext;
            }
        }
    }

    // ── Schemas ─────────────────────────────────────────────────────

    [GeneratedRegex(@"CREATE\s+SCHEMA\s+IF\s+NOT\s+EXISTS\s+""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SchemaCreateRegex();

    [GeneratedRegex(@"ALTER\s+SCHEMA\s+""([^""]+)""\s+OWNER\s+TO\s+""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SchemaOwnerRegex();

    private static void LoadSchemas(DatabaseSchema schema, string folderPath)
    {
        foreach (var (fileName, content) in ReadSqlFiles(folderPath, "schemas"))
        {
            var cm = SchemaCreateRegex().Match(content);
            if (cm.Success)
            {
                var name = cm.Groups[1].Value;
                var om = SchemaOwnerRegex().Match(content);
                var def = new SchemaDef
                {
                    Name = name,
                    Owner = om.Success ? om.Groups[2].Value : null,
                    RawDdl = content.Trim(),
                    FileName = fileName
                };
                schema.Schemas[def.Name] = def;
            }
        }
    }

    // ── Types ───────────────────────────────────────────────────────

    [GeneratedRegex(@"CREATE\s+TYPE\s+""([^""]+)""\.\""([^""]+)""\s+AS\s+ENUM\s*\(([^)]+)\)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex EnumTypeRegex();

    [GeneratedRegex(@"CREATE\s+TYPE\s+""([^""]+)""\.\""([^""]+)""\s+AS\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex CompositeTypeRegex();

    [GeneratedRegex(@"CREATE\s+DOMAIN\s+""([^""]+)""\.\""([^""]+)""\s+AS\s+(.+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DomainTypeRegex();

    private static void LoadTypes(DatabaseSchema schema, string folderPath)
    {
        foreach (var (fileName, content) in ReadSqlFiles(folderPath, "types"))
        {
            var key = Path.GetFileNameWithoutExtension(fileName);

            var enumMatch = EnumTypeRegex().Match(content);
            if (enumMatch.Success)
            {
                var labels = ParseEnumLabels(enumMatch.Groups[3].Value);
                var def = new TypeDef
                {
                    Schema = enumMatch.Groups[1].Value,
                    Name = enumMatch.Groups[2].Value,
                    Kind = TypeKind.Enum,
                    EnumLabels = labels,
                    RawDdl = content.Trim(),
                    FileName = fileName
                };
                schema.Types[key] = def;
                continue;
            }

            var compMatch = CompositeTypeRegex().Match(content);
            if (compMatch.Success)
            {
                var bodyStart = content.IndexOf('(', compMatch.Index + compMatch.Length - 1);
                var body = ExtractParenthesizedBody(content, bodyStart);
                var def = new TypeDef
                {
                    Schema = compMatch.Groups[1].Value,
                    Name = compMatch.Groups[2].Value,
                    Kind = TypeKind.Composite,
                    CompositeBody = body,
                    RawDdl = content.Trim(),
                    FileName = fileName
                };
                schema.Types[key] = def;
                continue;
            }

            var domMatch = DomainTypeRegex().Match(content);
            if (domMatch.Success)
            {
                var remainder = domMatch.Groups[3].Value.Trim().TrimEnd(';');
                ParseDomainDetails(remainder, out var baseType, out var defaultVal, out var notNull, out var checks);
                var def = new TypeDef
                {
                    Schema = domMatch.Groups[1].Value,
                    Name = domMatch.Groups[2].Value,
                    Kind = TypeKind.Domain,
                    DomainBaseType = baseType,
                    DomainDefault = defaultVal,
                    DomainNotNull = notNull,
                    DomainChecks = checks,
                    RawDdl = content.Trim(),
                    FileName = fileName
                };
                schema.Types[key] = def;
            }
        }
    }

    private static List<string> ParseEnumLabels(string labelsText)
    {
        return Regex.Matches(labelsText, @"'([^']*(?:''[^']*)*)'")
            .Select(m => m.Groups[1].Value.Replace("''", "'"))
            .ToList();
    }

    private static void ParseDomainDetails(string remainder, out string baseType, out string? defaultVal, out bool notNull, out string? checks)
    {
        defaultVal = null;
        notNull = false;
        checks = null;

        var defaultMatch = Regex.Match(remainder, @"\bDEFAULT\s+(.+?)(?=\s+NOT\s+NULL|\s+CHECK|\s*$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var notNullMatch = Regex.Match(remainder, @"\bNOT\s+NULL\b", RegexOptions.IgnoreCase);
        var checkMatch = Regex.Match(remainder, @"\bCHECK\s*\(.+\)", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var baseEnd = remainder.Length;
        if (defaultMatch.Success) baseEnd = Math.Min(baseEnd, defaultMatch.Index);
        if (notNullMatch.Success) baseEnd = Math.Min(baseEnd, notNullMatch.Index);
        if (checkMatch.Success) baseEnd = Math.Min(baseEnd, checkMatch.Index);

        baseType = remainder[..baseEnd].Trim();
        if (defaultMatch.Success) defaultVal = defaultMatch.Groups[1].Value.Trim();
        notNull = notNullMatch.Success;
        if (checkMatch.Success) checks = checkMatch.Value.Trim();
    }

    private static string ExtractParenthesizedBody(string text, int openParenIndex)
    {
        var depth = 0;
        for (var i = openParenIndex; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')') { depth--; if (depth == 0) return text[(openParenIndex + 1)..i].Trim(); }
        }
        return text[(openParenIndex + 1)..].Trim();
    }

    // ── Sequences ───────────────────────────────────────────────────

    [GeneratedRegex(@"CREATE\s+SEQUENCE\s+""([^""]+)""\.\""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SequenceRegex();

    private static void LoadSequences(DatabaseSchema schema, string folderPath)
    {
        foreach (var (fileName, content) in ReadSqlFiles(folderPath, "sequences"))
        {
            var m = SequenceRegex().Match(content);
            if (!m.Success) continue;

            var key = Path.GetFileNameWithoutExtension(fileName);
            var def = new SequenceDef
            {
                Schema = m.Groups[1].Value,
                Name = m.Groups[2].Value,
                DataType = ExtractClause(content, @"AS\s+(\w+)"),
                IncrementBy = ExtractClause(content, @"INCREMENT\s+BY\s+([\d-]+)"),
                MinValue = ExtractClause(content, @"MINVALUE\s+([\d-]+)"),
                MaxValue = ExtractClause(content, @"MAXVALUE\s+([\d-]+)"),
                StartWith = ExtractClause(content, @"START\s+WITH\s+([\d-]+)"),
                Cycle = Regex.IsMatch(content, @"\bCYCLE\b", RegexOptions.IgnoreCase)
                        && !Regex.IsMatch(content, @"\bNO\s+CYCLE\b", RegexOptions.IgnoreCase),
                RawDdl = content.Trim(),
                FileName = fileName
            };
            schema.Sequences[key] = def;
        }
    }

    private static string? ExtractClause(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    // ── Tables ──────────────────────────────────────────────────────

    [GeneratedRegex(@"CREATE\s+TABLE\s+""([^""]+)""\.\""([^""]+)""\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex TableCreateRegex();

    private static void LoadTables(DatabaseSchema schema, string folderPath)
    {
        foreach (var (fileName, content) in ReadSqlFiles(folderPath, "tables"))
        {
            var m = TableCreateRegex().Match(content);
            if (!m.Success) continue;

            var schemaName = m.Groups[1].Value;
            var tableName = m.Groups[2].Value;
            var body = ExtractParenthesizedBody(content, content.IndexOf('(', m.Index));

            var columns = new List<ColumnDef>();
            PrimaryKeyDef? pk = null;
            var uniqueConstraints = new List<UniqueConstraintDef>();
            var checkConstraints = new List<CheckConstraintDef>();

            foreach (var line in SplitTableBody(body))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                var constraintMatch = Regex.Match(trimmed, @"^CONSTRAINT\s+""([^""]+)""\s+PRIMARY\s+KEY\s*\(([^)]+)\)", RegexOptions.IgnoreCase);
                if (constraintMatch.Success)
                {
                    pk = new PrimaryKeyDef
                    {
                        Name = constraintMatch.Groups[1].Value,
                        Columns = ParseColumnList(constraintMatch.Groups[2].Value)
                    };
                    continue;
                }

                var uqMatch = Regex.Match(trimmed, @"^CONSTRAINT\s+""([^""]+)""\s+UNIQUE\s*\(([^)]+)\)", RegexOptions.IgnoreCase);
                if (uqMatch.Success)
                {
                    uniqueConstraints.Add(new UniqueConstraintDef
                    {
                        Name = uqMatch.Groups[1].Value,
                        Columns = ParseColumnList(uqMatch.Groups[2].Value)
                    });
                    continue;
                }

                var ckMatch = Regex.Match(trimmed, @"^CONSTRAINT\s+""([^""]+)""\s+(CHECK\s*\(.+\))", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (ckMatch.Success)
                {
                    checkConstraints.Add(new CheckConstraintDef
                    {
                        Name = ckMatch.Groups[1].Value,
                        Expression = ckMatch.Groups[2].Value.Trim()
                    });
                    continue;
                }

                var col = ParseColumn(trimmed);
                if (col != null) columns.Add(col);
            }

            var comments = new List<CommentDef>();
            string? tableComment = null;

            foreach (var commentMatch in Regex.Matches(content,
                @"COMMENT\s+ON\s+COLUMN\s+""[^""]+""\.""[^""]+""\.""([^""]+)""\s+IS\s+'((?:[^']|'')*)'",
                RegexOptions.IgnoreCase).Cast<Match>())
            {
                comments.Add(new CommentDef
                {
                    ColumnName = commentMatch.Groups[1].Value,
                    Comment = commentMatch.Groups[2].Value.Replace("''", "'")
                });
            }

            var tblCommentMatch = Regex.Match(content,
                @"COMMENT\s+ON\s+TABLE\s+""[^""]+""\.""[^""]+?""\s+IS\s+'((?:[^']|'')*)'",
                RegexOptions.IgnoreCase);
            if (tblCommentMatch.Success)
                tableComment = tblCommentMatch.Groups[1].Value.Replace("''", "'");

            var key = Path.GetFileNameWithoutExtension(fileName);
            var def = new TableDef
            {
                Schema = schemaName,
                Name = tableName,
                Columns = columns,
                PrimaryKey = pk,
                UniqueConstraints = uniqueConstraints,
                CheckConstraints = checkConstraints,
                Comments = comments,
                TableComment = tableComment,
                RawDdl = content.Trim(),
                FileName = fileName
            };
            schema.Tables[key] = def;
        }
    }

    private static ColumnDef? ParseColumn(string line)
    {
        var m = Regex.Match(line, @"^""([^""]+)""\s+(.+)$", RegexOptions.Singleline);
        if (!m.Success) return null;

        var name = m.Groups[1].Value;
        var rest = m.Groups[2].Value.Trim();

        string? identityType = null;
        string? generatedExpr = null;
        string? defaultVal = null;
        var notNull = false;

        var identityMatch = Regex.Match(rest, @"\bGENERATED\s+(ALWAYS|BY\s+DEFAULT)\s+AS\s+IDENTITY\b", RegexOptions.IgnoreCase);
        if (identityMatch.Success)
        {
            identityType = identityMatch.Groups[1].Value.ToUpperInvariant();
            rest = rest[..identityMatch.Index].Trim();
        }

        var genMatch = Regex.Match(rest, @"\bGENERATED\s+ALWAYS\s+AS\s+\((.+?)\)\s+STORED\b", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (genMatch.Success)
        {
            generatedExpr = genMatch.Groups[1].Value.Trim();
            rest = rest[..genMatch.Index].Trim();
        }

        if (Regex.IsMatch(rest, @"\bNOT\s+NULL\b", RegexOptions.IgnoreCase))
        {
            notNull = true;
            rest = Regex.Replace(rest, @"\s*NOT\s+NULL\b", "", RegexOptions.IgnoreCase).Trim();
        }

        var defMatch = Regex.Match(rest, @"\bDEFAULT\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (defMatch.Success)
        {
            defaultVal = defMatch.Groups[1].Value.Trim();
            rest = rest[..defMatch.Index].Trim();
        }

        return new ColumnDef
        {
            Name = name,
            DataType = rest,
            NotNull = notNull,
            Default = defaultVal,
            IdentityType = identityType,
            GeneratedExpr = generatedExpr
        };
    }

    private static List<string> ParseColumnList(string text) =>
        text.Split(',').Select(c => c.Trim().Trim('"')).ToList();

    /// <summary>
    /// Splits the table body on top-level commas, respecting parenthesized expressions.
    /// </summary>
    private static List<string> SplitTableBody(string body)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < body.Length; i++)
        {
            switch (body[i])
            {
                case '(': depth++; break;
                case ')': depth--; break;
                case ',' when depth == 0:
                    parts.Add(body[start..i]);
                    start = i + 1;
                    break;
            }
        }
        if (start < body.Length)
            parts.Add(body[start..]);
        return parts;
    }

    // ── Indexes ─────────────────────────────────────────────────────

    [GeneratedRegex(@"CREATE\s+(?:UNIQUE\s+)?INDEX\s+(\S+)\s+ON\s+(\S+)\.(\S+)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex IndexRegex();

    private static void LoadIndexes(DatabaseSchema schema, string folderPath)
    {
        foreach (var (fileName, content) in ReadSqlFiles(folderPath, "indexes"))
        {
            var m = IndexRegex().Match(content);
            if (!m.Success) continue;

            var key = Path.GetFileNameWithoutExtension(fileName);
            var indexName = m.Groups[1].Value.Trim('"');
            var schemaName = m.Groups[2].Value.Trim('"');
            var tableName = m.Groups[3].Value.Trim('"');

            var def = new IndexDef
            {
                Schema = schemaName,
                IndexName = indexName,
                TableName = tableName,
                RawDdl = content.Trim(),
                FileName = fileName
            };
            schema.Indexes[key] = def;
        }
    }

    // ── Foreign Keys ────────────────────────────────────────────────

    [GeneratedRegex(@"ALTER\s+TABLE\s+""([^""]+)""\.\""([^""]+)""\s+ADD\s+CONSTRAINT\s+""([^""]+)""\s+(FOREIGN\s+KEY.+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ForeignKeyRegex();

    private static void LoadForeignKeys(DatabaseSchema schema, string folderPath)
    {
        foreach (var (fileName, content) in ReadSqlFiles(folderPath, "foreign_keys"))
        {
            var m = ForeignKeyRegex().Match(content);
            if (!m.Success) continue;

            var key = Path.GetFileNameWithoutExtension(fileName);
            var def = new ForeignKeyDef
            {
                Schema = m.Groups[1].Value,
                TableName = m.Groups[2].Value,
                ConstraintName = m.Groups[3].Value,
                Definition = m.Groups[4].Value.Trim().TrimEnd(';'),
                RawDdl = content.Trim(),
                FileName = fileName
            };
            schema.ForeignKeys[key] = def;
        }
    }

    // ── Views ───────────────────────────────────────────────────────

    [GeneratedRegex(@"CREATE\s+OR\s+REPLACE\s+VIEW\s+""([^""]+)""\.\""([^""]+)""\s+AS\b", RegexOptions.IgnoreCase)]
    private static partial Regex ViewRegex();

    private static void LoadViews(DatabaseSchema schema, string folderPath)
    {
        foreach (var (fileName, content) in ReadSqlFiles(folderPath, "views"))
        {
            var m = ViewRegex().Match(content);
            if (!m.Success) continue;

            var key = Path.GetFileNameWithoutExtension(fileName);
            var def = new ViewDef
            {
                Schema = m.Groups[1].Value,
                Name = m.Groups[2].Value,
                RawDdl = content.Trim(),
                FileName = fileName
            };
            schema.Views[key] = def;
        }
    }

    // ── Materialized Views ──────────────────────────────────────────

    [GeneratedRegex(@"CREATE\s+MATERIALIZED\s+VIEW\s+""([^""]+)""\.\""([^""]+)""\s+AS\b", RegexOptions.IgnoreCase)]
    private static partial Regex MatViewRegex();

    private static void LoadMaterializedViews(DatabaseSchema schema, string folderPath)
    {
        foreach (var (fileName, content) in ReadSqlFiles(folderPath, "materialized_views"))
        {
            var m = MatViewRegex().Match(content);
            if (!m.Success) continue;

            var key = Path.GetFileNameWithoutExtension(fileName);
            var def = new MaterializedViewDef
            {
                Schema = m.Groups[1].Value,
                Name = m.Groups[2].Value,
                RawDdl = content.Trim(),
                FileName = fileName
            };
            schema.MaterializedViews[key] = def;
        }
    }

    // ── Functions & Procedures ──────────────────────────────────────

    [GeneratedRegex(@"CREATE\s+OR\s+REPLACE\s+(FUNCTION|PROCEDURE)\s+(\S+)\.(\S+)\(", RegexOptions.IgnoreCase)]
    private static partial Regex FunctionRegex();

    private static void LoadFunctions(DatabaseSchema schema, string folderPath, string subdir, string kind)
    {
        foreach (var (fileName, content) in ReadSqlFiles(folderPath, subdir))
        {
            var m = FunctionRegex().Match(content);
            if (!m.Success) continue;

            var key = Path.GetFileNameWithoutExtension(fileName);
            var schemaName = m.Groups[2].Value.Trim('"');
            var funcName = m.Groups[3].Value.Trim('"');

            var def = new FunctionDef
            {
                Schema = schemaName,
                Name = funcName,
                Kind = kind,
                RawDdl = content.Trim(),
                FileName = fileName
            };
            schema.Functions[key] = def;
        }
    }

    // ── Triggers ────────────────────────────────────────────────────

    [GeneratedRegex(@"CREATE\s+TRIGGER\s+(\S+)\s+.+?\s+ON\s+(\S+)\.(\S+)\s+", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TriggerRegex();

    private static void LoadTriggers(DatabaseSchema schema, string folderPath)
    {
        foreach (var (fileName, content) in ReadSqlFiles(folderPath, "triggers"))
        {
            var m = TriggerRegex().Match(content);
            if (!m.Success) continue;

            var key = Path.GetFileNameWithoutExtension(fileName);
            var triggerName = m.Groups[1].Value.Trim('"');
            var schemaName = m.Groups[2].Value.Trim('"');
            var tableName = m.Groups[3].Value.Trim('"');

            var def = new TriggerDef
            {
                Schema = schemaName,
                Name = triggerName,
                TableName = tableName,
                RawDdl = content.Trim(),
                FileName = fileName
            };
            schema.Triggers[key] = def;
        }
    }
}
