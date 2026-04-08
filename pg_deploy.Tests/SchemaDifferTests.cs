using pg_deploy.Diff;
using pg_deploy.Models;
using pg_deploy.Parsing;

namespace pg_deploy.Tests;

public class SchemaDifferTests : IDisposable
{
    private readonly string _sourceDir;
    private readonly string _targetDir;

    public SchemaDifferTests()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "pg_deploy_diff_" + Guid.NewGuid().ToString("N")[..8]);
        _sourceDir = Path.Combine(basePath, "source");
        _targetDir = Path.Combine(basePath, "target");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_targetDir);
    }

    public void Dispose()
    {
        var basePath = Path.GetDirectoryName(_sourceDir)!;
        if (Directory.Exists(basePath))
            Directory.Delete(basePath, true);
    }

    private void WriteSource(string subdir, string fileName, string content)
    {
        var dir = Path.Combine(_sourceDir, subdir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    private void WriteTarget(string subdir, string fileName, string content)
    {
        var dir = Path.Combine(_targetDir, subdir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    [Fact]
    public void IdenticalSchemas_NoChanges()
    {
        var ddl = "CREATE SCHEMA IF NOT EXISTS \"test\";\n\nALTER SCHEMA \"test\" OWNER TO \"admin\";\n";
        WriteSource("schemas", "test.sql", ddl);
        WriteTarget("schemas", "test.sql", ddl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Empty(changes);
    }

    [Fact]
    public void NewTable_DetectedAsAdd()
    {
        var ddl = "CREATE TABLE \"s\".\"new_table\" (\n    \"id\" bigint NOT NULL\n);\n";
        WriteSource("tables", "s.new_table.sql", ddl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Equal(ChangeAction.Add, changes[0].Action);
        Assert.Equal("TABLE", changes[0].ObjectType);
    }

    [Fact]
    public void DroppedTable_OnlyWithAllowDrops()
    {
        var ddl = "CREATE TABLE \"s\".\"old_table\" (\n    \"id\" bigint NOT NULL\n);\n";
        WriteTarget("tables", "s.old_table.sql", ddl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);

        var differNoDrop = new SchemaDiffer(source, target, allowDrops: false);
        Assert.Empty(differNoDrop.ComputeChanges());

        var differDrop = new SchemaDiffer(source, target, allowDrops: true);
        var changes = differDrop.ComputeChanges();
        Assert.Single(changes);
        Assert.Equal(ChangeAction.Drop, changes[0].Action);
        Assert.True(changes[0].IsDestructive);
    }

    [Fact]
    public void NewColumn_DetectedAsAddColumn()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL,\n    \"new_col\" text\n);\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL\n);\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Contains(changes, c => c.Action == ChangeAction.Add && c.ObjectType == "COLUMN");
    }

    [Fact]
    public void ColumnTypeChange_EmitsAlterAndWarning()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"name\" character varying(200)\n);\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"name\" character varying(100)\n);\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        var typeChange = changes.Single(c => c.ObjectType == "COLUMN" && c.Action == ChangeAction.Modify);
        Assert.Contains("ALTER COLUMN", typeChange.Sql);
        Assert.Contains("TYPE character varying(200)", typeChange.Sql);
        Assert.NotNull(typeChange.Warning);
    }

    [Fact]
    public void PotentialRename_Flagged()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL,\n    \"new_name\" text\n);\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL,\n    \"old_name\" text\n);\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: true);
        var changes = differ.ComputeChanges();

        Assert.Contains(changes, c => c.ObjectType.Contains("POTENTIAL RENAME"));
    }

    [Fact]
    public void NullabilityChange_DetectedAsModify()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"name\" text NOT NULL\n);\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"name\" text\n);\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Contains(changes, c => c.Sql.Contains("SET NOT NULL"));
    }

    [Fact]
    public void DefaultChange_DetectedAsModify()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"status\" text DEFAULT 'active'\n);\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"status\" text DEFAULT 'pending'\n);\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Contains(changes, c => c.Sql.Contains("SET DEFAULT 'active'"));
    }

    [Fact]
    public void SchemaOwnerChange_DetectedAsModify()
    {
        WriteSource("schemas", "test.sql", "CREATE SCHEMA IF NOT EXISTS \"test\";\n\nALTER SCHEMA \"test\" OWNER TO \"new_owner\";\n");
        WriteTarget("schemas", "test.sql", "CREATE SCHEMA IF NOT EXISTS \"test\";\n\nALTER SCHEMA \"test\" OWNER TO \"old_owner\";\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Contains("OWNER TO \"new_owner\"", changes[0].Sql);
    }

    [Fact]
    public void SequencePropertyChange_EmitsAlter()
    {
        WriteSource("sequences", "s.seq1.sql",
            "CREATE SEQUENCE \"s\".\"seq1\"\n    AS bigint\n    INCREMENT BY 2\n    MINVALUE 1\n    MAXVALUE 999\n    START WITH 1\n    NO CYCLE;\n");
        WriteTarget("sequences", "s.seq1.sql",
            "CREATE SEQUENCE \"s\".\"seq1\"\n    AS bigint\n    INCREMENT BY 1\n    MINVALUE 1\n    MAXVALUE 999\n    START WITH 1\n    NO CYCLE;\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Contains("INCREMENT BY 2", changes[0].Sql);
    }

    [Fact]
    public void NewIndex_DetectedAsAdd()
    {
        WriteSource("indexes", "s.idx_new.sql",
            "CREATE INDEX idx_new ON s.table1 USING btree (col1);\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Equal(ChangeAction.Add, changes[0].Action);
        Assert.Equal("INDEX", changes[0].ObjectType);
    }

    [Fact]
    public void ModifiedView_UsesCreateOrReplace()
    {
        WriteSource("views", "s.v1.sql",
            "CREATE OR REPLACE VIEW \"s\".\"v1\" AS\n SELECT 2 AS id;\n");
        WriteTarget("views", "s.v1.sql",
            "CREATE OR REPLACE VIEW \"s\".\"v1\" AS\n SELECT 1 AS id;\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Equal(ChangeAction.Modify, changes[0].Action);
        Assert.Contains("CREATE OR REPLACE VIEW", changes[0].Sql);
    }

    [Fact]
    public void EnumType_NewLabelAdded()
    {
        WriteSource("types", "s.my_enum.sql",
            "CREATE TYPE \"s\".\"my_enum\" AS ENUM ('a', 'b', 'c');\n");
        WriteTarget("types", "s.my_enum.sql",
            "CREATE TYPE \"s\".\"my_enum\" AS ENUM ('a', 'b');\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Contains("ADD VALUE 'c'", changes[0].Sql);
    }
}
