namespace pg_deploy.Models;

/// <summary>
/// Represents the entire DDL schema loaded from a folder.
/// </summary>
public sealed class DatabaseSchema
{
    public Dictionary<string, ExtensionDef> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SchemaDef> Schemas { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TypeDef> Types { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SequenceDef> Sequences { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TableDef> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, IndexDef> Indexes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ForeignKeyDef> ForeignKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ViewDef> Views { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MaterializedViewDef> MaterializedViews { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FunctionDef> Functions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TriggerDef> Triggers { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ExtensionDef
{
    public required string Name { get; init; }
    public required string Schema { get; init; }
    public required string RawDdl { get; init; }
    public string FileName { get; init; } = "";
}

public sealed class SchemaDef
{
    public required string Name { get; init; }
    public string? Owner { get; init; }
    public required string RawDdl { get; init; }
    public string FileName { get; init; } = "";
}

public sealed class TypeDef
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required TypeKind Kind { get; init; }
    public List<string>? EnumLabels { get; init; }
    public string? CompositeBody { get; init; }
    public string? DomainBaseType { get; init; }
    public string? DomainDefault { get; init; }
    public bool DomainNotNull { get; init; }
    public string? DomainChecks { get; init; }
    public required string RawDdl { get; init; }
    public string FileName { get; init; } = "";

    public string QualifiedName => $"\"{Schema}\".\"{Name}\"";
}

public enum TypeKind { Enum, Composite, Domain }

public sealed class SequenceDef
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public string? DataType { get; init; }
    public string? IncrementBy { get; init; }
    public string? MinValue { get; init; }
    public string? MaxValue { get; init; }
    public string? StartWith { get; init; }
    public bool Cycle { get; init; }
    public required string RawDdl { get; init; }
    public string FileName { get; init; } = "";

    public string QualifiedName => $"\"{Schema}\".\"{Name}\"";
}

public sealed class TableDef
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public List<ColumnDef> Columns { get; init; } = [];
    public PrimaryKeyDef? PrimaryKey { get; init; }
    public List<UniqueConstraintDef> UniqueConstraints { get; init; } = [];
    public List<CheckConstraintDef> CheckConstraints { get; init; } = [];
    public List<CommentDef> Comments { get; init; } = [];
    public string? TableComment { get; init; }
    public required string RawDdl { get; init; }
    public string FileName { get; init; } = "";

    public string QualifiedName => $"\"{Schema}\".\"{Name}\"";
}

public sealed class ColumnDef
{
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public bool NotNull { get; init; }
    public string? Default { get; init; }
    public string? IdentityType { get; init; }
    public string? GeneratedExpr { get; init; }
}

public sealed class PrimaryKeyDef
{
    public required string Name { get; init; }
    public required List<string> Columns { get; init; }
}

public sealed class UniqueConstraintDef
{
    public required string Name { get; init; }
    public required List<string> Columns { get; init; }
}

public sealed class CheckConstraintDef
{
    public required string Name { get; init; }
    public required string Expression { get; init; }
}

public sealed class CommentDef
{
    public required string ColumnName { get; init; }
    public required string Comment { get; init; }
}

public sealed class IndexDef
{
    public required string Schema { get; init; }
    public required string IndexName { get; init; }
    public required string TableName { get; init; }
    public required string RawDdl { get; init; }
    public string FileName { get; init; } = "";

    public string QualifiedName => $"\"{Schema}\".\"{IndexName}\"";
}

public sealed class ForeignKeyDef
{
    public required string Schema { get; init; }
    public required string TableName { get; init; }
    public required string ConstraintName { get; init; }
    public required string Definition { get; init; }
    public required string RawDdl { get; init; }
    public string FileName { get; init; } = "";

    public string QualifiedName => $"\"{Schema}\".\"{ConstraintName}\"";
}

public sealed class ViewDef
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required string RawDdl { get; init; }
    public string FileName { get; init; } = "";

    public string QualifiedName => $"\"{Schema}\".\"{Name}\"";
}

public sealed class MaterializedViewDef
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required string RawDdl { get; init; }
    public string FileName { get; init; } = "";

    public string QualifiedName => $"\"{Schema}\".\"{Name}\"";
}

public sealed class FunctionDef
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }  // "function" or "procedure"
    public required string RawDdl { get; init; }
    public string FileName { get; init; } = "";

    public string QualifiedName => $"\"{Schema}\".\"{Name}\"";
}

public sealed class TriggerDef
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required string TableName { get; init; }
    public required string RawDdl { get; init; }
    public string FileName { get; init; } = "";
}
