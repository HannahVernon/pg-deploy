namespace pg_deploy.Models;

/// <summary>
/// Represents a single change to be emitted in the deployment script.
/// </summary>
public sealed class SchemaChange
{
    public required ChangeCategory Category { get; init; }
    public required ChangeAction Action { get; init; }
    public required string ObjectType { get; init; }
    public required string ObjectName { get; init; }
    public required string Sql { get; init; }
    public bool IsDestructive { get; init; }
    public string? Warning { get; init; }
    public int LineNumber { get; set; }
}

public enum ChangeCategory
{
    Extension,
    Schema,
    Type,
    Sequence,
    Table,
    Index,
    ForeignKey,
    View,
    MaterializedView,
    Function,
    Trigger
}

public enum ChangeAction
{
    Add,
    Modify,
    Drop
}
