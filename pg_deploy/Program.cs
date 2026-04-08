using System.CommandLine;
using pg_deploy.Diff;
using pg_deploy.Models;
using pg_deploy.Parsing;
using pg_deploy.ScriptGeneration;

var sourceOption = new Option<string>("--source", "Folder containing new/desired DDL files") { IsRequired = true };
sourceOption.AddAlias("-s");

var targetOption = new Option<string>("--target", "Folder containing existing DDL (extracted from DB)") { IsRequired = true };
targetOption.AddAlias("-t");

var outputOption = new Option<string>("--output", "Output path for the generated SQL deployment script") { IsRequired = true };
outputOption.AddAlias("-o");

var allowDropsOption = new Option<bool>("--allow-drops", getDefaultValue: () => false,
    description: "Enable destructive changes (drops). Drops are placed in a clearly-marked section.");

var trustSourceOption = new Option<bool>("--trust-source-folder", getDefaultValue: () => false,
    description: "Trust all DDL files in the source folder without prompting. Required for non-interactive use.");

var includeSystemOption = new Option<bool>("--include-postgres-system-objects", getDefaultValue: () => false,
    description: "Include PostgreSQL system objects (pg_catalog, pg_toast, pg_temp, information_schema, plpgsql) in the diff. By default these are excluded.");

var verboseOption = new Option<bool>("--verbose", getDefaultValue: () => false, description: "Verbose console output");
verboseOption.AddAlias("-v");

var quietOption = new Option<bool>("--quiet", getDefaultValue: () => false, description: "Suppress all console output");
quietOption.AddAlias("-q");

var rootCommand = new RootCommand("pg_deploy — Generate incremental PostgreSQL deployment scripts by comparing DDL folders")
{
    sourceOption, targetOption, outputOption, allowDropsOption, trustSourceOption, includeSystemOption, verboseOption, quietOption
};

rootCommand.SetHandler((source, target, output, allowDrops, trustSource, includeSystem, verbose, quiet) =>
{
    try
    {
        if (!Directory.Exists(source))
        {
            if (!quiet) Console.Error.WriteLine($"Error: Source folder not found: {source}");
            Environment.ExitCode = 1;
            return;
        }

        if (!Directory.Exists(target))
        {
            if (!quiet) Console.Error.WriteLine($"Error: Target folder not found: {target}");
            Environment.ExitCode = 1;
            return;
        }

        // Source folder trust check
        if (!trustSource)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  ⚠  WARNING: Source folder contents have not been verified  ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine($"  Source: {Path.GetFullPath(source)}");
            Console.WriteLine();
            Console.WriteLine("  DDL files from the source folder will be embedded directly");
            Console.WriteLine("  into the generated deployment script. Malicious DDL files");
            Console.WriteLine("  could inject arbitrary SQL that executes against your database.");
            Console.WriteLine();
            Console.WriteLine("  Only proceed if you trust the source of these files.");
            Console.WriteLine("  Use --trust-source-folder to skip this prompt.");
            Console.WriteLine();
            Console.Write("  Continue? [y/N] ");

            var response = Console.ReadLine()?.Trim();
            if (!string.Equals(response, "y", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Aborted.");
                Environment.ExitCode = 1;
                return;
            }
            Console.WriteLine();
        }

        if (!quiet) Console.WriteLine("pg_deploy — PostgreSQL Deployment Script Generator");
        if (!quiet) Console.WriteLine();

        // Load schemas
        if (verbose) Console.WriteLine($"Loading source DDL from: {Path.GetFullPath(source)}");
        var sourceSchema = DdlLoader.Load(source, includeSystem);
        if (verbose) PrintSchemaStats("Source", sourceSchema);

        if (verbose) Console.WriteLine($"Loading target DDL from: {Path.GetFullPath(target)}");
        var targetSchema = DdlLoader.Load(target, includeSystem);
        if (verbose) PrintSchemaStats("Target", targetSchema);

        // Compute diff
        if (verbose) Console.WriteLine("\nComputing differences...");
        var differ = new SchemaDiffer(sourceSchema, targetSchema, allowDrops);
        var changes = differ.ComputeChanges();

        if (!quiet)
        {
            var adds = changes.Count(c => c.Action == ChangeAction.Add);
            var modifies = changes.Count(c => c.Action == ChangeAction.Modify);
            var drops = changes.Count(c => c.Action == ChangeAction.Drop);
            Console.WriteLine($"Changes detected: {adds} additions, {modifies} modifications, {drops} drops");
        }

        if (changes.Count == 0)
        {
            if (!quiet) Console.WriteLine("No changes detected. No script generated.");
            return;
        }

        // Generate script
        if (verbose) Console.WriteLine("\nGenerating deployment script...");
        var generator = new ScriptGenerator(changes, source, target, allowDrops);
        var script = generator.Generate();

        // Write output
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(output));
        if (outputDir != null)
            Directory.CreateDirectory(outputDir);

        File.WriteAllText(output, script);

        if (!quiet)
        {
            Console.WriteLine($"\nDeployment script written to: {Path.GetFullPath(output)}");
            Console.WriteLine($"Script size: {script.Length:N0} characters, {script.Split('\n').Length:N0} lines");

            var destructiveCount = changes.Count(c => c.IsDestructive);
            if (destructiveCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n⚠  WARNING: Script contains {destructiveCount} destructive change(s). Review carefully!");
                Console.ResetColor();
            }

            var warnings = changes.Where(c => c.Warning != null).ToList();
            if (warnings.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n⚠  {warnings.Count} warning(s) — see script header for details.");
                Console.ResetColor();
            }
        }
    }
    catch (Exception ex)
    {
        if (!quiet)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (verbose) Console.Error.WriteLine(ex.StackTrace);
        }
        Environment.ExitCode = 1;
    }
}, sourceOption, targetOption, outputOption, allowDropsOption, trustSourceOption, includeSystemOption, verboseOption, quietOption);

return await rootCommand.InvokeAsync(args);

static void PrintSchemaStats(string label, pg_deploy.Models.DatabaseSchema schema)
{
    Console.WriteLine($"  {label}: {schema.Extensions.Count} extensions, {schema.Schemas.Count} schemas, " +
                      $"{schema.Types.Count} types, {schema.Sequences.Count} sequences, " +
                      $"{schema.Tables.Count} tables, {schema.Indexes.Count} indexes, " +
                      $"{schema.ForeignKeys.Count} foreign keys, {schema.Views.Count} views, " +
                      $"{schema.MaterializedViews.Count} mat views, {schema.Functions.Count} functions, " +
                      $"{schema.Triggers.Count} triggers");
}
