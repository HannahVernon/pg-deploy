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

    // ── Extension Diff ──────────────────────────────────────────────

    [Fact]
    public void NewExtension_DetectedAsAdd()
    {
        WriteSource("extensions", "uuid-ossp.sql",
            "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\" SCHEMA \"public\";\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Equal(ChangeAction.Add, changes[0].Action);
        Assert.Equal("EXTENSION", changes[0].ObjectType);
    }

    [Fact]
    public void DroppedExtension_OnlyWithAllowDrops()
    {
        WriteTarget("extensions", "pgcrypto.sql",
            "CREATE EXTENSION IF NOT EXISTS \"pgcrypto\" SCHEMA \"public\";\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);

        Assert.Empty(new SchemaDiffer(source, target, allowDrops: false).ComputeChanges());

        var changes = new SchemaDiffer(source, target, allowDrops: true).ComputeChanges();
        Assert.Single(changes);
        Assert.Equal(ChangeAction.Drop, changes[0].Action);
        Assert.True(changes[0].IsDestructive);
        Assert.Contains("DROP EXTENSION", changes[0].Sql);
    }

    // ── Materialized View Diff ──────────────────────────────────────

    [Fact]
    public void NewMaterializedView_DetectedAsAdd()
    {
        WriteSource("materialized_views", "s.mv1.sql",
            "CREATE MATERIALIZED VIEW \"s\".\"mv1\" AS\n SELECT 1 AS id\nWITH NO DATA;\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Equal(ChangeAction.Add, changes[0].Action);
        Assert.Equal("MATERIALIZED VIEW", changes[0].ObjectType);
    }

    [Fact]
    public void ModifiedMaterializedView_UsesDropCreate()
    {
        WriteSource("materialized_views", "s.mv1.sql",
            "CREATE MATERIALIZED VIEW \"s\".\"mv1\" AS\n SELECT 2 AS id\nWITH NO DATA;\n");
        WriteTarget("materialized_views", "s.mv1.sql",
            "CREATE MATERIALIZED VIEW \"s\".\"mv1\" AS\n SELECT 1 AS id\nWITH NO DATA;\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Equal(ChangeAction.Modify, changes[0].Action);
        Assert.Contains("DROP MATERIALIZED VIEW", changes[0].Sql);
        Assert.Contains("CREATE MATERIALIZED VIEW", changes[0].Sql);
        Assert.NotNull(changes[0].Warning);
    }

    [Fact]
    public void DroppedMaterializedView_OnlyWithAllowDrops()
    {
        WriteTarget("materialized_views", "s.mv1.sql",
            "CREATE MATERIALIZED VIEW \"s\".\"mv1\" AS\n SELECT 1 AS id\nWITH NO DATA;\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);

        Assert.Empty(new SchemaDiffer(source, target, allowDrops: false).ComputeChanges());

        var changes = new SchemaDiffer(source, target, allowDrops: true).ComputeChanges();
        Assert.Single(changes);
        Assert.True(changes[0].IsDestructive);
    }

    // ── Foreign Key Diff ────────────────────────────────────────────

    [Fact]
    public void NewForeignKey_DetectedAsAdd()
    {
        WriteSource("foreign_keys", "s.fk_new.sql",
            "ALTER TABLE \"s\".\"child\"\n    ADD CONSTRAINT \"fk_new\" FOREIGN KEY (parent_id) REFERENCES s.parent(id);\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Equal(ChangeAction.Add, changes[0].Action);
        Assert.Equal("FOREIGN KEY", changes[0].ObjectType);
    }

    [Fact]
    public void ModifiedForeignKey_EmitsDropAndAdd()
    {
        WriteSource("foreign_keys", "s.fk1.sql",
            "ALTER TABLE \"s\".\"child\"\n    ADD CONSTRAINT \"fk1\" FOREIGN KEY (parent_id) REFERENCES s.parent(id) ON DELETE CASCADE;\n");
        WriteTarget("foreign_keys", "s.fk1.sql",
            "ALTER TABLE \"s\".\"child\"\n    ADD CONSTRAINT \"fk1\" FOREIGN KEY (parent_id) REFERENCES s.parent(id);\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Equal(ChangeAction.Modify, changes[0].Action);
        Assert.Contains("DROP CONSTRAINT", changes[0].Sql);
        Assert.Contains("ADD CONSTRAINT", changes[0].Sql);
    }

    [Fact]
    public void DroppedForeignKey_OnlyWithAllowDrops()
    {
        WriteTarget("foreign_keys", "s.fk_old.sql",
            "ALTER TABLE \"s\".\"child\"\n    ADD CONSTRAINT \"fk_old\" FOREIGN KEY (parent_id) REFERENCES s.parent(id);\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);

        Assert.Empty(new SchemaDiffer(source, target, allowDrops: false).ComputeChanges());

        var changes = new SchemaDiffer(source, target, allowDrops: true).ComputeChanges();
        Assert.Single(changes);
        Assert.True(changes[0].IsDestructive);
        Assert.Contains("DROP CONSTRAINT", changes[0].Sql);
    }

    // ── Trigger Diff ────────────────────────────────────────────────

    [Fact]
    public void NewTrigger_DetectedAsAdd()
    {
        WriteSource("triggers", "s.trg_new.sql",
            "CREATE TRIGGER trg_new BEFORE INSERT ON s.t FOR EACH ROW EXECUTE FUNCTION s.my_func();\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Equal(ChangeAction.Add, changes[0].Action);
        Assert.Equal("TRIGGER", changes[0].ObjectType);
    }

    [Fact]
    public void ModifiedTrigger_EmitsDropAndCreate()
    {
        WriteSource("triggers", "s.trg1.sql",
            "CREATE TRIGGER trg1 AFTER INSERT ON s.t FOR EACH ROW EXECUTE FUNCTION s.my_func();\n");
        WriteTarget("triggers", "s.trg1.sql",
            "CREATE TRIGGER trg1 BEFORE INSERT ON s.t FOR EACH ROW EXECUTE FUNCTION s.my_func();\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Equal(ChangeAction.Modify, changes[0].Action);
        Assert.Contains("DROP TRIGGER", changes[0].Sql);
        Assert.Contains("CREATE TRIGGER", changes[0].Sql);
    }

    [Fact]
    public void DroppedTrigger_OnlyWithAllowDrops()
    {
        WriteTarget("triggers", "s.trg_old.sql",
            "CREATE TRIGGER trg_old BEFORE INSERT ON s.t FOR EACH ROW EXECUTE FUNCTION s.my_func();\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);

        Assert.Empty(new SchemaDiffer(source, target, allowDrops: false).ComputeChanges());

        var changes = new SchemaDiffer(source, target, allowDrops: true).ComputeChanges();
        Assert.Single(changes);
        Assert.True(changes[0].IsDestructive);
    }

    // ── Function/Procedure Diff ─────────────────────────────────────

    [Fact]
    public void NewFunction_DetectedAsAdd()
    {
        var ddl = "CREATE OR REPLACE FUNCTION s.new_func()\n RETURNS void\n LANGUAGE plpgsql\nAS $function$\nBEGIN NULL; END;\n$function$\n;\n";
        WriteSource("functions", "s.new_func.sql", ddl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Equal(ChangeAction.Add, changes[0].Action);
        Assert.Equal("FUNCTION", changes[0].ObjectType);
    }

    [Fact]
    public void ModifiedFunction_UsesCreateOrReplace()
    {
        var srcDdl = "CREATE OR REPLACE FUNCTION s.f1()\n RETURNS integer\n LANGUAGE plpgsql\nAS $function$\nBEGIN RETURN 2; END;\n$function$\n;\n";
        var tgtDdl = "CREATE OR REPLACE FUNCTION s.f1()\n RETURNS integer\n LANGUAGE plpgsql\nAS $function$\nBEGIN RETURN 1; END;\n$function$\n;\n";
        WriteSource("functions", "s.f1.sql", srcDdl);
        WriteTarget("functions", "s.f1.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Equal(ChangeAction.Modify, changes[0].Action);
        Assert.Contains("CREATE OR REPLACE FUNCTION", changes[0].Sql);
    }

    [Fact]
    public void DroppedFunction_OnlyWithAllowDrops()
    {
        var ddl = "CREATE OR REPLACE FUNCTION s.old_func()\n RETURNS void\n LANGUAGE plpgsql\nAS $function$\nBEGIN NULL; END;\n$function$\n;\n";
        WriteTarget("functions", "s.old_func.sql", ddl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);

        Assert.Empty(new SchemaDiffer(source, target, allowDrops: false).ComputeChanges());

        var changes = new SchemaDiffer(source, target, allowDrops: true).ComputeChanges();
        Assert.Single(changes);
        Assert.Equal("FUNCTION", changes[0].ObjectType);
        Assert.True(changes[0].IsDestructive);
        Assert.Contains("DROP FUNCTION", changes[0].Sql);
    }

    [Fact]
    public void DroppedProcedure_UsesDropProcedure()
    {
        var ddl = "CREATE OR REPLACE PROCEDURE s.old_proc(IN p_id integer)\n LANGUAGE plpgsql\nAS $procedure$\nBEGIN NULL; END;\n$procedure$\n;\n";
        WriteTarget("procedures", "s.old_proc.sql", ddl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var changes = new SchemaDiffer(source, target, allowDrops: true).ComputeChanges();

        Assert.Single(changes);
        Assert.Equal("PROCEDURE", changes[0].ObjectType);
        Assert.Contains("DROP PROCEDURE", changes[0].Sql);
    }

    // ── Table Constraint Diff ───────────────────────────────────────

    [Fact]
    public void PrimaryKeyChange_EmitsDropAndAdd()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL,\n    \"code\" text NOT NULL,\n    CONSTRAINT \"t_pkey\" PRIMARY KEY (\"id\", \"code\")\n);\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL,\n    \"code\" text NOT NULL,\n    CONSTRAINT \"t_pkey\" PRIMARY KEY (\"id\")\n);\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        var pkChange = changes.Single(c => c.ObjectType == "PRIMARY KEY");
        Assert.Equal(ChangeAction.Modify, pkChange.Action);
        Assert.Contains("DROP CONSTRAINT", pkChange.Sql);
        Assert.Contains("ADD CONSTRAINT", pkChange.Sql);
        Assert.NotNull(pkChange.Warning);
    }

    [Fact]
    public void NewPrimaryKey_DetectedAsAdd()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL,\n    CONSTRAINT \"t_pkey\" PRIMARY KEY (\"id\")\n);\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL\n);\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Contains(changes, c => c.ObjectType == "PRIMARY KEY" && c.Action == ChangeAction.Add);
    }

    [Fact]
    public void DroppedPrimaryKey_OnlyWithAllowDrops()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL\n);\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL,\n    CONSTRAINT \"t_pkey\" PRIMARY KEY (\"id\")\n);\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);

        Assert.DoesNotContain(
            new SchemaDiffer(source, target, allowDrops: false).ComputeChanges(),
            c => c.ObjectType == "PRIMARY KEY");

        var changes = new SchemaDiffer(source, target, allowDrops: true).ComputeChanges();
        Assert.Contains(changes, c => c.ObjectType == "PRIMARY KEY" && c.Action == ChangeAction.Drop);
    }

    [Fact]
    public void NewUniqueConstraint_DetectedAsAdd()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"email\" text,\n    CONSTRAINT \"t_email_uq\" UNIQUE (\"email\")\n);\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"email\" text\n);\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Contains(changes, c => c.ObjectType == "UNIQUE CONSTRAINT" && c.Action == ChangeAction.Add);
    }

    [Fact]
    public void ModifiedUniqueConstraint_EmitsDropAndAdd()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"a\" text,\n    \"b\" text,\n    CONSTRAINT \"t_uq\" UNIQUE (\"a\", \"b\")\n);\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"a\" text,\n    \"b\" text,\n    CONSTRAINT \"t_uq\" UNIQUE (\"a\")\n);\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        var uqChange = changes.Single(c => c.ObjectType == "UNIQUE CONSTRAINT");
        Assert.Equal(ChangeAction.Modify, uqChange.Action);
        Assert.Contains("DROP CONSTRAINT", uqChange.Sql);
    }

    [Fact]
    public void DroppedUniqueConstraint_OnlyWithAllowDrops()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"email\" text\n);\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"email\" text,\n    CONSTRAINT \"t_email_uq\" UNIQUE (\"email\")\n);\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);

        Assert.DoesNotContain(
            new SchemaDiffer(source, target, allowDrops: false).ComputeChanges(),
            c => c.ObjectType == "UNIQUE CONSTRAINT");

        var changes = new SchemaDiffer(source, target, allowDrops: true).ComputeChanges();
        Assert.Contains(changes, c => c.ObjectType == "UNIQUE CONSTRAINT" && c.Action == ChangeAction.Drop);
    }

    [Fact]
    public void NewCheckConstraint_DetectedAsAdd()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"val\" integer,\n    CONSTRAINT \"t_val_ck\" CHECK ((val > 0))\n);\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"val\" integer\n);\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Contains(changes, c => c.ObjectType == "CHECK CONSTRAINT" && c.Action == ChangeAction.Add);
    }

    [Fact]
    public void DroppedCheckConstraint_OnlyWithAllowDrops()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"val\" integer\n);\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"val\" integer,\n    CONSTRAINT \"t_val_ck\" CHECK ((val > 0))\n);\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);

        Assert.DoesNotContain(
            new SchemaDiffer(source, target, allowDrops: false).ComputeChanges(),
            c => c.ObjectType == "CHECK CONSTRAINT");

        var changes = new SchemaDiffer(source, target, allowDrops: true).ComputeChanges();
        Assert.Contains(changes, c => c.ObjectType == "CHECK CONSTRAINT" && c.Action == ChangeAction.Drop);
    }

    // ── Table Comment Diff ──────────────────────────────────────────

    [Fact]
    public void TableCommentAdded_DetectedAsModify()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL\n);\n\nCOMMENT ON TABLE \"s\".\"t\" IS 'New comment';\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL\n);\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Contains(changes, c => c.ObjectType == "TABLE COMMENT");
        Assert.Contains(changes, c => c.Sql.Contains("COMMENT ON TABLE") && c.Sql.Contains("New comment"));
    }

    [Fact]
    public void TableCommentRemoved_SetsToNull()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL\n);\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL\n);\n\nCOMMENT ON TABLE \"s\".\"t\" IS 'Old comment';\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Contains(changes, c => c.Sql.Contains("IS NULL"));
    }

    [Fact]
    public void ColumnCommentChanged_DetectedAsModify()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL\n);\n\nCOMMENT ON COLUMN \"s\".\"t\".\"id\" IS 'Updated comment';\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL\n);\n\nCOMMENT ON COLUMN \"s\".\"t\".\"id\" IS 'Old comment';\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Contains(changes, c => c.ObjectType == "COLUMN COMMENT" && c.Sql.Contains("Updated comment"));
    }

    [Fact]
    public void ColumnCommentRemoved_SetsToNull()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL\n);\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL\n);\n\nCOMMENT ON COLUMN \"s\".\"t\".\"id\" IS 'Was here';\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Contains(changes, c => c.ObjectType == "COLUMN COMMENT" && c.Sql.Contains("IS NULL"));
    }

    // ── Index Diff ──────────────────────────────────────────────────

    [Fact]
    public void ModifiedIndex_EmitsDropAndCreate()
    {
        WriteSource("indexes", "s.idx1.sql",
            "CREATE INDEX idx1 ON s.t USING btree (col1, col2);\n");
        WriteTarget("indexes", "s.idx1.sql",
            "CREATE INDEX idx1 ON s.t USING btree (col1);\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Equal(ChangeAction.Modify, changes[0].Action);
        Assert.Contains("DROP INDEX", changes[0].Sql);
        Assert.Contains("CREATE INDEX", changes[0].Sql);
    }

    [Fact]
    public void DroppedIndex_OnlyWithAllowDrops()
    {
        WriteTarget("indexes", "s.idx_old.sql",
            "CREATE INDEX idx_old ON s.t USING btree (col1);\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);

        Assert.Empty(new SchemaDiffer(source, target, allowDrops: false).ComputeChanges());

        var changes = new SchemaDiffer(source, target, allowDrops: true).ComputeChanges();
        Assert.Single(changes);
        Assert.True(changes[0].IsDestructive);
        Assert.Contains("DROP INDEX", changes[0].Sql);
    }

    // ── View Diff ───────────────────────────────────────────────────

    [Fact]
    public void NewView_DetectedAsAdd()
    {
        WriteSource("views", "s.v_new.sql",
            "CREATE OR REPLACE VIEW \"s\".\"v_new\" AS\n SELECT 1 AS id;\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Equal(ChangeAction.Add, changes[0].Action);
        Assert.Equal("VIEW", changes[0].ObjectType);
    }

    [Fact]
    public void DroppedView_OnlyWithAllowDrops()
    {
        WriteTarget("views", "s.v_old.sql",
            "CREATE OR REPLACE VIEW \"s\".\"v_old\" AS\n SELECT 1 AS id;\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);

        Assert.Empty(new SchemaDiffer(source, target, allowDrops: false).ComputeChanges());

        var changes = new SchemaDiffer(source, target, allowDrops: true).ComputeChanges();
        Assert.Single(changes);
        Assert.True(changes[0].IsDestructive);
    }

    // ── Schema Diff ─────────────────────────────────────────────────

    [Fact]
    public void NewSchema_DetectedAsAdd()
    {
        WriteSource("schemas", "new_schema.sql",
            "CREATE SCHEMA IF NOT EXISTS \"new_schema\";\n\nALTER SCHEMA \"new_schema\" OWNER TO \"admin\";\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Equal(ChangeAction.Add, changes[0].Action);
        Assert.Contains("CREATE SCHEMA", changes[0].Sql);
        Assert.Contains("OWNER TO", changes[0].Sql);
    }

    [Fact]
    public void DroppedSchema_OnlyWithAllowDrops_HasCascadeWarning()
    {
        WriteTarget("schemas", "old_schema.sql",
            "CREATE SCHEMA IF NOT EXISTS \"old_schema\";\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);

        Assert.Empty(new SchemaDiffer(source, target, allowDrops: false).ComputeChanges());

        var changes = new SchemaDiffer(source, target, allowDrops: true).ComputeChanges();
        Assert.Single(changes);
        Assert.True(changes[0].IsDestructive);
        Assert.Contains("CASCADE", changes[0].Sql);
        Assert.NotNull(changes[0].Warning);
    }

    // ── Sequence Diff ───────────────────────────────────────────────

    [Fact]
    public void NewSequence_DetectedAsAdd()
    {
        WriteSource("sequences", "s.seq_new.sql",
            "CREATE SEQUENCE \"s\".\"seq_new\"\n    AS bigint\n    INCREMENT BY 1\n    MINVALUE 1\n    MAXVALUE 999\n    START WITH 1\n    NO CYCLE;\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Equal(ChangeAction.Add, changes[0].Action);
        Assert.Equal("SEQUENCE", changes[0].ObjectType);
    }

    [Fact]
    public void DroppedSequence_OnlyWithAllowDrops()
    {
        WriteTarget("sequences", "s.seq_old.sql",
            "CREATE SEQUENCE \"s\".\"seq_old\"\n    AS bigint\n    INCREMENT BY 1\n    MINVALUE 1\n    MAXVALUE 999\n    START WITH 1\n    NO CYCLE;\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);

        Assert.Empty(new SchemaDiffer(source, target, allowDrops: false).ComputeChanges());

        var changes = new SchemaDiffer(source, target, allowDrops: true).ComputeChanges();
        Assert.Single(changes);
        Assert.True(changes[0].IsDestructive);
        Assert.Contains("DROP SEQUENCE", changes[0].Sql);
    }

    [Fact]
    public void SequenceCycleChange_EmitsAlter()
    {
        WriteSource("sequences", "s.seq1.sql",
            "CREATE SEQUENCE \"s\".\"seq1\"\n    AS bigint\n    INCREMENT BY 1\n    MINVALUE 1\n    MAXVALUE 999\n    START WITH 1\n    CYCLE;\n");
        WriteTarget("sequences", "s.seq1.sql",
            "CREATE SEQUENCE \"s\".\"seq1\"\n    AS bigint\n    INCREMENT BY 1\n    MINVALUE 1\n    MAXVALUE 999\n    START WITH 1\n    NO CYCLE;\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Contains("CYCLE", changes[0].Sql);
    }

    // ── Enum Diff — removed labels ──────────────────────────────────

    [Fact]
    public void EnumType_RemovedLabel_FlagsWarning()
    {
        WriteSource("types", "s.my_enum.sql",
            "CREATE TYPE \"s\".\"my_enum\" AS ENUM ('a');\n");
        WriteTarget("types", "s.my_enum.sql",
            "CREATE TYPE \"s\".\"my_enum\" AS ENUM ('a', 'b');\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Contains(changes, c => c.Warning != null && c.Warning.Contains("Cannot remove enum labels"));
    }

    // ── Type Diff — composite type changed ──────────────────────────

    [Fact]
    public void CompositeType_Changed_EmitsDropCreate()
    {
        WriteSource("types", "s.my_type.sql",
            "CREATE TYPE \"s\".\"my_type\" AS (\n    \"field1\" bigint,\n    \"field2\" text\n);\n");
        WriteTarget("types", "s.my_type.sql",
            "CREATE TYPE \"s\".\"my_type\" AS (\n    \"field1\" bigint\n);\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Contains("DROP TYPE", changes[0].Sql);
        Assert.Contains("CREATE TYPE", changes[0].Sql);
        Assert.NotNull(changes[0].Warning);
    }

    // ── New type ────────────────────────────────────────────────────

    [Fact]
    public void NewType_DetectedAsAdd()
    {
        WriteSource("types", "s.new_type.sql",
            "CREATE TYPE \"s\".\"new_type\" AS ENUM ('x', 'y');\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Single(changes);
        Assert.Equal(ChangeAction.Add, changes[0].Action);
        Assert.Equal("TYPE", changes[0].ObjectType);
    }

    [Fact]
    public void DroppedType_OnlyWithAllowDrops()
    {
        WriteTarget("types", "s.old_type.sql",
            "CREATE TYPE \"s\".\"old_type\" AS ENUM ('a');\n");

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);

        Assert.Empty(new SchemaDiffer(source, target, allowDrops: false).ComputeChanges());

        var changes = new SchemaDiffer(source, target, allowDrops: true).ComputeChanges();
        Assert.Single(changes);
        Assert.True(changes[0].IsDestructive);
        Assert.Contains("DROP TYPE", changes[0].Sql);
    }

    // ── Default dropped ─────────────────────────────────────────────

    [Fact]
    public void DefaultDropped_EmitsDropDefault()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"val\" integer\n);\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"val\" integer DEFAULT 42\n);\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Contains(changes, c => c.Sql.Contains("DROP DEFAULT"));
    }

    // ── NOT NULL dropped ────────────────────────────────────────────

    [Fact]
    public void NotNullDropped_EmitsDropNotNull()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"name\" text\n);\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"name\" text NOT NULL\n);\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Contains(changes, c => c.Sql.Contains("DROP NOT NULL"));
    }

    // ── No changes across all object types ──────────────────────────

    [Fact]
    public void CompletelyIdentical_NoChanges()
    {
        var tableDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL,\n    CONSTRAINT \"t_pkey\" PRIMARY KEY (\"id\")\n);\n";
        WriteSource("tables", "s.t.sql", tableDdl);
        WriteTarget("tables", "s.t.sql", tableDdl);

        var viewDdl = "CREATE OR REPLACE VIEW \"s\".\"v\" AS\n SELECT 1 AS id;\n";
        WriteSource("views", "s.v.sql", viewDdl);
        WriteTarget("views", "s.v.sql", viewDdl);

        var funcDdl = "CREATE OR REPLACE FUNCTION s.f()\n RETURNS void\n LANGUAGE plpgsql\nAS $function$\nBEGIN NULL; END;\n$function$\n;\n";
        WriteSource("functions", "s.f.sql", funcDdl);
        WriteTarget("functions", "s.f.sql", funcDdl);

        var idxDdl = "CREATE INDEX idx1 ON s.t USING btree (id);\n";
        WriteSource("indexes", "s.idx1.sql", idxDdl);
        WriteTarget("indexes", "s.idx1.sql", idxDdl);

        var trgDdl = "CREATE TRIGGER trg1 BEFORE INSERT ON s.t FOR EACH ROW EXECUTE FUNCTION s.f();\n";
        WriteSource("triggers", "s.trg1.sql", trgDdl);
        WriteTarget("triggers", "s.trg1.sql", trgDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: true);
        var changes = differ.ComputeChanges();

        Assert.Empty(changes);
    }

    // ── Multiple simultaneous column changes ────────────────────────

    [Fact]
    public void MultipleColumnChanges_AllDetected()
    {
        var srcDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL,\n    \"name\" character varying(200) NOT NULL,\n    \"status\" text DEFAULT 'active'\n);\n";
        var tgtDdl = "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL,\n    \"name\" character varying(100),\n    \"status\" text DEFAULT 'pending'\n);\n";
        WriteSource("tables", "s.t.sql", srcDdl);
        WriteTarget("tables", "s.t.sql", tgtDdl);

        var source = DdlLoader.Load(_sourceDir);
        var target = DdlLoader.Load(_targetDir);
        var differ = new SchemaDiffer(source, target, allowDrops: false);
        var changes = differ.ComputeChanges();

        Assert.Contains(changes, c => c.Sql.Contains("TYPE character varying(200)"));
        Assert.Contains(changes, c => c.Sql.Contains("SET NOT NULL"));
        Assert.Contains(changes, c => c.Sql.Contains("SET DEFAULT 'active'"));
    }
}
