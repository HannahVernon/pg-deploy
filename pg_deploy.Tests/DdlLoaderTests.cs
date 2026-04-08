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

    // ── Extensions ──────────────────────────────────────────────────

    [Fact]
    public void LoadExtensions_ParsesNameAndSchema()
    {
        WriteFile("extensions", "uuid-ossp.sql",
            "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\" SCHEMA \"public\";\n");

        var schema = DdlLoader.Load(_testDir);

        Assert.Single(schema.Extensions);
        var ext = schema.Extensions["uuid-ossp"];
        Assert.Equal("uuid-ossp", ext.Name);
        Assert.Equal("public", ext.Schema);
    }

    [Fact]
    public void LoadExtensions_MultipleExtensions()
    {
        WriteFile("extensions", "uuid-ossp.sql",
            "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\" SCHEMA \"public\";\n");
        WriteFile("extensions", "pgcrypto.sql",
            "CREATE EXTENSION IF NOT EXISTS \"pgcrypto\" SCHEMA \"public\";\n");

        var schema = DdlLoader.Load(_testDir);
        Assert.Equal(2, schema.Extensions.Count);
    }

    // ── Domain Types ────────────────────────────────────────────────

    [Fact]
    public void LoadTypes_ParsesDomainType()
    {
        WriteFile("types", "s.email_domain.sql",
            "CREATE DOMAIN \"s\".\"email_domain\" AS character varying(255)\n    NOT NULL\n    CHECK (VALUE ~ '^.+@.+$');\n");

        var schema = DdlLoader.Load(_testDir);

        Assert.Single(schema.Types);
        var typ = schema.Types["s.email_domain"];
        Assert.Equal(TypeKind.Domain, typ.Kind);
        Assert.Equal("s", typ.Schema);
        Assert.Equal("email_domain", typ.Name);
        Assert.Equal("character varying(255)", typ.DomainBaseType);
        Assert.True(typ.DomainNotNull);
        Assert.NotNull(typ.DomainChecks);
    }

    [Fact]
    public void LoadTypes_ParsesDomainWithDefault()
    {
        WriteFile("types", "s.positive_int.sql",
            "CREATE DOMAIN \"s\".\"positive_int\" AS integer\n    DEFAULT 0\n    CHECK (VALUE >= 0);\n");

        var schema = DdlLoader.Load(_testDir);

        var typ = schema.Types["s.positive_int"];
        Assert.Equal(TypeKind.Domain, typ.Kind);
        Assert.Equal("integer", typ.DomainBaseType);
        Assert.Equal("0", typ.DomainDefault);
    }

    // ── Procedures ──────────────────────────────────────────────────

    [Fact]
    public void LoadProcedures_ParsedAsProcedureKind()
    {
        var ddl = """
            CREATE OR REPLACE PROCEDURE billing_own.my_proc(IN p_id integer)
             LANGUAGE plpgsql
            AS $procedure$
            BEGIN
                NULL;
            END;
            $procedure$
            ;
            """;
        WriteFile("procedures", "billing_own.my_proc.sql", ddl);

        var schema = DdlLoader.Load(_testDir);

        Assert.Single(schema.Functions);
        var proc = schema.Functions["billing_own.my_proc"];
        Assert.Equal("billing_own", proc.Schema);
        Assert.Equal("my_proc", proc.Name);
        Assert.Equal("procedure", proc.Kind);
    }

    [Fact]
    public void LoadFunctionsAndProcedures_BothLoaded()
    {
        var funcDdl = """
            CREATE OR REPLACE FUNCTION billing_own.my_func()
             RETURNS void
             LANGUAGE plpgsql
            AS $function$
            BEGIN NULL;
            END;
            $function$
            ;
            """;
        var procDdl = """
            CREATE OR REPLACE PROCEDURE billing_own.my_proc(IN p_id integer)
             LANGUAGE plpgsql
            AS $procedure$
            BEGIN NULL;
            END;
            $procedure$
            ;
            """;
        WriteFile("functions", "billing_own.my_func.sql", funcDdl);
        WriteFile("procedures", "billing_own.my_proc.sql", procDdl);

        var schema = DdlLoader.Load(_testDir);

        Assert.Equal(2, schema.Functions.Count);
        Assert.Equal("function", schema.Functions["billing_own.my_func"].Kind);
        Assert.Equal("procedure", schema.Functions["billing_own.my_proc"].Kind);
    }

    // ── Materialized Views ──────────────────────────────────────────

    [Fact]
    public void LoadMaterializedViews_Parsed()
    {
        WriteFile("materialized_views", "s.mv1.sql",
            "CREATE MATERIALIZED VIEW \"s\".\"mv1\" AS\n SELECT 1 AS id\nWITH NO DATA;\n");

        var schema = DdlLoader.Load(_testDir);

        Assert.Single(schema.MaterializedViews);
        var mv = schema.MaterializedViews["s.mv1"];
        Assert.Equal("s", mv.Schema);
        Assert.Equal("mv1", mv.Name);
    }

    // ── Table Comment ───────────────────────────────────────────────

    [Fact]
    public void LoadTables_ParsesTableComment()
    {
        var ddl = """
            CREATE TABLE "s"."t" (
                "id" bigint NOT NULL
            );

            COMMENT ON TABLE "s"."t" IS 'This is the main table';
            """;
        WriteFile("tables", "s.t.sql", ddl);

        var schema = DdlLoader.Load(_testDir);
        var table = schema.Tables["s.t"];
        Assert.Equal("This is the main table", table.TableComment);
    }

    [Fact]
    public void LoadTables_ParsesMultipleColumnComments()
    {
        var ddl = """
            CREATE TABLE "s"."t" (
                "id" bigint NOT NULL,
                "name" text,
                "status" character varying(3)
            );

            COMMENT ON COLUMN "s"."t"."id" IS 'Primary identifier';
            COMMENT ON COLUMN "s"."t"."status" IS 'Active or inactive';
            """;
        WriteFile("tables", "s.t.sql", ddl);

        var schema = DdlLoader.Load(_testDir);
        var table = schema.Tables["s.t"];
        Assert.Equal(2, table.Comments.Count);
        Assert.Contains(table.Comments, c => c.ColumnName == "id");
        Assert.Contains(table.Comments, c => c.ColumnName == "status");
    }

    // ── Column with Generated Expression ────────────────────────────

    [Fact]
    public void LoadTables_ParsesGeneratedColumn()
    {
        var ddl = """
            CREATE TABLE "s"."t" (
                "price" numeric(10,2),
                "tax" numeric(10,2),
                "total" numeric(10,2) GENERATED ALWAYS AS (price + tax) STORED
            );
            """;
        WriteFile("tables", "s.t.sql", ddl);

        var schema = DdlLoader.Load(_testDir);
        var table = schema.Tables["s.t"];
        var totalCol = table.Columns.Single(c => c.Name == "total");
        Assert.Equal("price + tax", totalCol.GeneratedExpr);
    }

    // ── BY DEFAULT identity ─────────────────────────────────────────

    [Fact]
    public void LoadTables_ParsesByDefaultIdentity()
    {
        var ddl = """
            CREATE TABLE "s"."t" (
                "id" bigint GENERATED BY DEFAULT AS IDENTITY
            );
            """;
        WriteFile("tables", "s.t.sql", ddl);

        var schema = DdlLoader.Load(_testDir);
        var table = schema.Tables["s.t"];
        Assert.Equal("BY DEFAULT", table.Columns[0].IdentityType);
    }

    // ── Composite PK ────────────────────────────────────────────────

    [Fact]
    public void LoadTables_ParsesCompositePrimaryKey()
    {
        var ddl = """
            CREATE TABLE "s"."t" (
                "col_a" integer NOT NULL,
                "col_b" integer NOT NULL,
                CONSTRAINT "t_pkey" PRIMARY KEY ("col_a", "col_b")
            );
            """;
        WriteFile("tables", "s.t.sql", ddl);

        var schema = DdlLoader.Load(_testDir);
        var table = schema.Tables["s.t"];
        Assert.NotNull(table.PrimaryKey);
        Assert.Equal(2, table.PrimaryKey.Columns.Count);
        Assert.Equal("col_a", table.PrimaryKey.Columns[0]);
        Assert.Equal("col_b", table.PrimaryKey.Columns[1]);
    }

    // ── Multiple unique constraints ─────────────────────────────────

    [Fact]
    public void LoadTables_ParsesMultipleUniqueConstraints()
    {
        var ddl = """
            CREATE TABLE "s"."t" (
                "id" bigint NOT NULL,
                "email" text NOT NULL,
                "code" text NOT NULL,
                CONSTRAINT "t_pkey" PRIMARY KEY ("id"),
                CONSTRAINT "t_email_uq" UNIQUE ("email"),
                CONSTRAINT "t_code_uq" UNIQUE ("code")
            );
            """;
        WriteFile("tables", "s.t.sql", ddl);

        var schema = DdlLoader.Load(_testDir);
        var table = schema.Tables["s.t"];
        Assert.Equal(2, table.UniqueConstraints.Count);
    }

    // ── Sequence with CYCLE ─────────────────────────────────────────

    [Fact]
    public void LoadSequences_ParsesCycle()
    {
        WriteFile("sequences", "s.seq_cycle.sql",
            "CREATE SEQUENCE \"s\".\"seq_cycle\"\n    AS integer\n    INCREMENT BY 1\n    MINVALUE 1\n    MAXVALUE 100\n    START WITH 1\n    CYCLE;\n");

        var schema = DdlLoader.Load(_testDir);
        var seq = schema.Sequences["s.seq_cycle"];
        Assert.True(seq.Cycle);
    }

    // ── Unique index ────────────────────────────────────────────────

    [Fact]
    public void LoadIndexes_ParsesUniqueIndex()
    {
        WriteFile("indexes", "s.idx_unique.sql",
            "CREATE UNIQUE INDEX idx_unique ON s.table1 USING btree (email);\n");

        var schema = DdlLoader.Load(_testDir);
        Assert.Single(schema.Indexes);
        Assert.Equal("idx_unique", schema.Indexes["s.idx_unique"].IndexName);
    }

    // ── Nullable column (no NOT NULL, no DEFAULT) ───────────────────

    [Fact]
    public void LoadTables_NullableColumnNoDefault()
    {
        var ddl = """
            CREATE TABLE "s"."t" (
                "notes" text
            );
            """;
        WriteFile("tables", "s.t.sql", ddl);

        var schema = DdlLoader.Load(_testDir);
        var col = schema.Tables["s.t"].Columns[0];
        Assert.Equal("notes", col.Name);
        Assert.Equal("text", col.DataType);
        Assert.False(col.NotNull);
        Assert.Null(col.Default);
        Assert.Null(col.IdentityType);
        Assert.Null(col.GeneratedExpr);
    }

    // ── Column with nextval default ─────────────────────────────────

    [Fact]
    public void LoadTables_ParsesNextvalDefault()
    {
        var ddl = """
            CREATE TABLE "s"."t" (
                "id" bigint DEFAULT nextval('s.seq_t'::regclass) NOT NULL
            );
            """;
        WriteFile("tables", "s.t.sql", ddl);

        var schema = DdlLoader.Load(_testDir);
        var col = schema.Tables["s.t"].Columns[0];
        Assert.Equal("nextval('s.seq_t'::regclass)", col.Default);
        Assert.True(col.NotNull);
    }

    // ── Enum with escaped quotes ────────────────────────────────────

    [Fact]
    public void LoadTypes_ParsesEnumWithEscapedQuotes()
    {
        WriteFile("types", "s.quoted_enum.sql",
            "CREATE TYPE \"s\".\"quoted_enum\" AS ENUM ('it''s', 'they''re');\n");

        var schema = DdlLoader.Load(_testDir);
        var typ = schema.Types["s.quoted_enum"];
        Assert.Equal(TypeKind.Enum, typ.Kind);
        Assert.Equal(2, typ.EnumLabels!.Count);
        Assert.Contains("it's", typ.EnumLabels);
        Assert.Contains("they're", typ.EnumLabels);
    }

    // ── Missing subdirectories ──────────────────────────────────────

    [Fact]
    public void LoadPartialFolder_OnlyParsesExistingSubdirs()
    {
        WriteFile("tables", "s.t.sql",
            "CREATE TABLE \"s\".\"t\" (\n    \"id\" bigint NOT NULL\n);\n");

        var schema = DdlLoader.Load(_testDir);

        Assert.Single(schema.Tables);
        Assert.Empty(schema.Schemas);
        Assert.Empty(schema.Views);
        Assert.Empty(schema.Functions);
        Assert.Empty(schema.Triggers);
        Assert.Empty(schema.Extensions);
    }

    // ── FK with CASCADE actions ─────────────────────────────────────

    [Fact]
    public void LoadForeignKeys_PreservesCascadeActions()
    {
        WriteFile("foreign_keys", "s.fk_cascade.sql",
            "ALTER TABLE \"s\".\"child\"\n    ADD CONSTRAINT \"fk_cascade\" FOREIGN KEY (parent_id) REFERENCES s.parent(id) ON DELETE CASCADE ON UPDATE SET NULL;\n");

        var schema = DdlLoader.Load(_testDir);
        var fk = schema.ForeignKeys["s.fk_cascade"];
        Assert.Contains("ON DELETE CASCADE", fk.Definition);
        Assert.Contains("ON UPDATE SET NULL", fk.Definition);
    }
}
