using pg_deploy.Models;
using pg_deploy.Parsing;

namespace pg_deploy.Tests;

public class DdlLoaderTests : IDisposable
{
    private readonly string _testDir;

    public DdlLoaderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "pg_deploy_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    private void WriteFile(string subdir, string fileName, string content)
    {
        var dir = Path.Combine(_testDir, subdir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    [Fact]
    public void LoadSchemas_ParsesNameAndOwner()
    {
        WriteFile("schemas", "billing_own.sql",
            "CREATE SCHEMA IF NOT EXISTS \"billing_own\";\n\nALTER SCHEMA \"billing_own\" OWNER TO \"admin_user\";\n");

        var schema = DdlLoader.Load(_testDir);

        Assert.Single(schema.Schemas);
        var s = schema.Schemas["billing_own"];
        Assert.Equal("billing_own", s.Name);
        Assert.Equal("admin_user", s.Owner);
    }

    [Fact]
    public void LoadSchemas_NoOwner()
    {
        WriteFile("schemas", "public.sql",
            "CREATE SCHEMA IF NOT EXISTS \"public\";\n");

        var schema = DdlLoader.Load(_testDir);

        Assert.Single(schema.Schemas);
        Assert.Null(schema.Schemas["public"].Owner);
    }

    [Fact]
    public void LoadSequences_ParsesAllProperties()
    {
        WriteFile("sequences", "billing_own.seq_test.sql",
            "CREATE SEQUENCE \"billing_own\".\"seq_test\"\n    AS bigint\n    INCREMENT BY 1\n    MINVALUE 1\n    MAXVALUE 999999999999999999\n    START WITH 100\n    NO CYCLE;\n");

        var schema = DdlLoader.Load(_testDir);

        Assert.Single(schema.Sequences);
        var seq = schema.Sequences["billing_own.seq_test"];
        Assert.Equal("billing_own", seq.Schema);
        Assert.Equal("seq_test", seq.Name);
        Assert.Equal("bigint", seq.DataType);
        Assert.Equal("1", seq.IncrementBy);
        Assert.Equal("1", seq.MinValue);
        Assert.Equal("999999999999999999", seq.MaxValue);
        Assert.Equal("100", seq.StartWith);
        Assert.False(seq.Cycle);
    }

    [Fact]
    public void LoadTables_ParsesColumnsAndConstraints()
    {
        var ddl = """
            CREATE TABLE "billing_own"."test_table" (
                "id" bigint NOT NULL,
                "name" character varying(100) DEFAULT 'unknown' NOT NULL,
                "amount" numeric(13,4),
                "created_at" timestamp without time zone,
                CONSTRAINT "test_table_pkey" PRIMARY KEY ("id"),
                CONSTRAINT "test_table_name_unique" UNIQUE ("name")
            );
            """;
        WriteFile("tables", "billing_own.test_table.sql", ddl);

        var schema = DdlLoader.Load(_testDir);

        Assert.Single(schema.Tables);
        var table = schema.Tables["billing_own.test_table"];
        Assert.Equal("billing_own", table.Schema);
        Assert.Equal("test_table", table.Name);
        Assert.Equal(4, table.Columns.Count);

        var idCol = table.Columns[0];
        Assert.Equal("id", idCol.Name);
        Assert.Equal("bigint", idCol.DataType);
        Assert.True(idCol.NotNull);

        var nameCol = table.Columns[1];
        Assert.Equal("name", nameCol.Name);
        Assert.Equal("character varying(100)", nameCol.DataType);
        Assert.True(nameCol.NotNull);
        Assert.Equal("'unknown'", nameCol.Default);

        Assert.NotNull(table.PrimaryKey);
        Assert.Equal("test_table_pkey", table.PrimaryKey.Name);
        Assert.Single(table.PrimaryKey.Columns);
        Assert.Equal("id", table.PrimaryKey.Columns[0]);

        Assert.Single(table.UniqueConstraints);
        Assert.Equal("test_table_name_unique", table.UniqueConstraints[0].Name);
    }

    [Fact]
    public void LoadTables_ParsesIdentityColumn()
    {
        var ddl = """
            CREATE TABLE "test_schema"."auto_id" (
                "id" bigint GENERATED ALWAYS AS IDENTITY,
                "data" text
            );
            """;
        WriteFile("tables", "test_schema.auto_id.sql", ddl);

        var schema = DdlLoader.Load(_testDir);
        var table = schema.Tables["test_schema.auto_id"];
        Assert.Equal("ALWAYS", table.Columns[0].IdentityType);
    }

    [Fact]
    public void LoadTables_ParsesCheckConstraint()
    {
        var ddl = """
            CREATE TABLE "s"."t" (
                "status" character varying(3) NOT NULL,
                CONSTRAINT "status_check" CHECK ((status)::text = ANY (ARRAY['A'::text, 'B'::text]))
            );
            """;
        WriteFile("tables", "s.t.sql", ddl);

        var schema = DdlLoader.Load(_testDir);
        var table = schema.Tables["s.t"];
        Assert.Single(table.CheckConstraints);
        Assert.Equal("status_check", table.CheckConstraints[0].Name);
    }

    [Fact]
    public void LoadIndexes_ParsesIndexDef()
    {
        WriteFile("indexes", "billing_own.idx_test.sql",
            "CREATE INDEX idx_test ON billing_own.test_table USING btree (name);\n");

        var schema = DdlLoader.Load(_testDir);

        Assert.Single(schema.Indexes);
        var idx = schema.Indexes["billing_own.idx_test"];
        Assert.Equal("billing_own", idx.Schema);
        Assert.Equal("idx_test", idx.IndexName);
        Assert.Equal("test_table", idx.TableName);
    }

    [Fact]
    public void LoadForeignKeys_ParsesFkDef()
    {
        WriteFile("foreign_keys", "billing_own.fk_test.sql",
            "ALTER TABLE \"billing_own\".\"child_table\"\n    ADD CONSTRAINT \"fk_test\" FOREIGN KEY (parent_id) REFERENCES billing_own.parent_table(id);\n");

        var schema = DdlLoader.Load(_testDir);

        Assert.Single(schema.ForeignKeys);
        var fk = schema.ForeignKeys["billing_own.fk_test"];
        Assert.Equal("billing_own", fk.Schema);
        Assert.Equal("child_table", fk.TableName);
        Assert.Equal("fk_test", fk.ConstraintName);
    }

    [Fact]
    public void LoadViews_ParsesViewDef()
    {
        WriteFile("views", "billing_own.test_view.sql",
            "CREATE OR REPLACE VIEW \"billing_own\".\"test_view\" AS\n SELECT 1 AS id;\n");

        var schema = DdlLoader.Load(_testDir);

        Assert.Single(schema.Views);
        var view = schema.Views["billing_own.test_view"];
        Assert.Equal("billing_own", view.Schema);
        Assert.Equal("test_view", view.Name);
    }

    [Fact]
    public void LoadFunctions_ParsesFunctionDef()
    {
        var ddl = """
            CREATE OR REPLACE FUNCTION billing_own.my_func()
             RETURNS void
             LANGUAGE plpgsql
            AS $function$
            BEGIN
                NULL;
            END;
            $function$
            ;
            """;
        WriteFile("functions", "billing_own.my_func.sql", ddl);

        var schema = DdlLoader.Load(_testDir);

        Assert.Single(schema.Functions);
        var func = schema.Functions["billing_own.my_func"];
        Assert.Equal("billing_own", func.Schema);
        Assert.Equal("my_func", func.Name);
        Assert.Equal("function", func.Kind);
    }

    [Fact]
    public void LoadTriggers_ParsesTriggerDef()
    {
        WriteFile("triggers", "billing_own.trg_test.sql",
            "CREATE TRIGGER trg_test BEFORE INSERT ON billing_own.test_table FOR EACH ROW EXECUTE FUNCTION billing_own.my_func();\n");

        var schema = DdlLoader.Load(_testDir);

        Assert.Single(schema.Triggers);
        var trg = schema.Triggers["billing_own.trg_test"];
        Assert.Equal("billing_own", trg.Schema);
        Assert.Equal("trg_test", trg.Name);
        Assert.Equal("test_table", trg.TableName);
    }

    [Fact]
    public void LoadTypes_ParsesCompositeType()
    {
        var ddl = """
            CREATE TYPE "billing_own"."my_type" AS (
                "field1" bigint,
                "field2" character varying
            );
            """;
        WriteFile("types", "billing_own.my_type.sql", ddl);

        var schema = DdlLoader.Load(_testDir);

        Assert.Single(schema.Types);
        var typ = schema.Types["billing_own.my_type"];
        Assert.Equal(TypeKind.Composite, typ.Kind);
    }

    [Fact]
    public void LoadTypes_ParsesEnumType()
    {
        WriteFile("types", "s.status_enum.sql",
            "CREATE TYPE \"s\".\"status_enum\" AS ENUM ('active', 'inactive', 'pending');\n");

        var schema = DdlLoader.Load(_testDir);

        Assert.Single(schema.Types);
        var typ = schema.Types["s.status_enum"];
        Assert.Equal(TypeKind.Enum, typ.Kind);
        Assert.Equal(3, typ.EnumLabels!.Count);
        Assert.Contains("active", typ.EnumLabels);
        Assert.Contains("inactive", typ.EnumLabels);
        Assert.Contains("pending", typ.EnumLabels);
    }

    [Fact]
    public void LoadEmptyFolder_ReturnsEmptySchema()
    {
        var schema = DdlLoader.Load(_testDir);

        Assert.Empty(schema.Extensions);
        Assert.Empty(schema.Schemas);
        Assert.Empty(schema.Tables);
        Assert.Empty(schema.Functions);
    }

    [Fact]
    public void LoadTables_ParsesColumnComment()
    {
        var ddl = """
            CREATE TABLE "s"."t" (
                "id" bigint NOT NULL
            );

            COMMENT ON COLUMN "s"."t"."id" IS 'Primary key';
            """;
        WriteFile("tables", "s.t.sql", ddl);

        var schema = DdlLoader.Load(_testDir);
        var table = schema.Tables["s.t"];
        Assert.Single(table.Comments);
        Assert.Equal("id", table.Comments[0].ColumnName);
        Assert.Equal("Primary key", table.Comments[0].Comment);
    }
}
