using System.Text;
using pg_deploy.Models;

namespace pg_deploy.ScriptGeneration;

/// <summary>
/// Generates the deployment SQL script from a list of schema changes.
/// </summary>
public sealed class ScriptGenerator
{
    private readonly List<SchemaChange> _changes;
    private readonly string _sourcePath;
    private readonly string _targetPath;
    private readonly bool _allowDrops;

    public ScriptGenerator(List<SchemaChange> changes, string sourcePath, string targetPath, bool allowDrops)
    {
        _changes = changes;
        _sourcePath = sourcePath;
        _targetPath = targetPath;
        _allowDrops = allowDrops;
    }

    public string Generate()
    {
        var nonDestructive = _changes.Where(c => !c.IsDestructive).ToList();
        var destructive = _changes.Where(c => c.IsDestructive).ToList();

        var bodyBuilder = new StringBuilder();
        GenerateBody(bodyBuilder, nonDestructive, destructive);
        var body = bodyBuilder.ToString();

        // Assign line numbers (header placeholder lines + body lines)
        AssignLineNumbers(body, nonDestructive, destructive);

        var headerBuilder = new StringBuilder();
        GenerateHeader(headerBuilder, nonDestructive, destructive);
        var header = headerBuilder.ToString();

        // Recalculate line numbers now that we know the header size
        var headerLineCount = header.Split('\n').Length;
        foreach (var change in _changes)
        {
            if (change.LineNumber > 0)
                change.LineNumber += headerLineCount;
        }

        // Rebuild header with corrected line numbers
        headerBuilder.Clear();
        GenerateHeader(headerBuilder, nonDestructive, destructive);
        header = headerBuilder.ToString();

        return header + body;
    }

    private void GenerateHeader(StringBuilder sb, List<SchemaChange> nonDestructive, List<SchemaChange> destructive)
    {
        sb.AppendLine("/*");
        sb.AppendLine("================================================================================");
        sb.AppendLine("  PostgreSQL Deployment Script");
        sb.AppendLine($"  Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine("  Generator: pg_deploy");
        sb.AppendLine("================================================================================");

        // Git info
        var sourceGit = GitInfo.Detect(_sourcePath);
        var targetGit = GitInfo.Detect(_targetPath);

        if (sourceGit.IsGitRepo || targetGit.IsGitRepo)
        {
            sb.AppendLine();
            sb.AppendLine("  Source & Target Details:");
        }

        sb.AppendLine();
        sb.AppendLine($"  Source DDL: {Path.GetFullPath(_sourcePath)}");
        if (sourceGit.IsGitRepo)
        {
            sb.AppendLine($"    Git Branch: {sourceGit.Branch}");
            foreach (var remote in sourceGit.Remotes)
                sb.AppendLine($"    Remote: {remote}");
        }

        sb.AppendLine($"  Target DDL: {Path.GetFullPath(_targetPath)}");
        if (targetGit.IsGitRepo)
        {
            sb.AppendLine($"    Git Branch: {targetGit.Branch}");
            foreach (var remote in targetGit.Remotes)
                sb.AppendLine($"    Remote: {remote}");
        }

        // Change summary
        sb.AppendLine();
        sb.AppendLine("  Change Summary:");
        WriteSummaryLine(sb, nonDestructive, destructive);

        // Warnings
        var warnings = _changes.Where(c => c.Warning != null).ToList();
        if (warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  ============================================================");
            sb.AppendLine("  WARNINGS — Review carefully before executing:");
            sb.AppendLine("  ============================================================");
            foreach (var w in warnings)
            {
                var lineRef = w.LineNumber > 0 ? $" (line {w.LineNumber})" : "";
                sb.AppendLine($"  - [{w.ObjectType}] {w.Warning}{lineRef}");
            }
        }

        if (destructive.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  ╔══════════════════════════════════════════════════════════════╗");
            sb.AppendLine("  ║  ⚠  THIS SCRIPT CONTAINS DESTRUCTIVE CHANGES (DROPS)  ⚠   ║");
            sb.AppendLine("  ║  Review the DESTRUCTIVE CHANGES section at the bottom       ║");
            sb.AppendLine("  ║  of this script before executing.                           ║");
            sb.AppendLine("  ╚══════════════════════════════════════════════════════════════╝");

            sb.AppendLine();
            sb.AppendLine("  Destructive Changes:");
            foreach (var d in destructive)
            {
                var lineRef = d.LineNumber > 0 ? $" (line {d.LineNumber})" : "";
                sb.AppendLine($"  - DROP {d.ObjectType}: {d.ObjectName}{lineRef}");
            }
        }

        // Materialized view drop+creates
        var matViewChanges = nonDestructive
            .Where(c => c.Category == ChangeCategory.MaterializedView && c.Action == ChangeAction.Modify)
            .ToList();
        if (matViewChanges.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  Materialized View DROP+CREATE (required — no CREATE OR REPLACE support):");
            foreach (var mv in matViewChanges)
            {
                var lineRef = mv.LineNumber > 0 ? $" (line {mv.LineNumber})" : "";
                sb.AppendLine($"  - {mv.ObjectName}{lineRef}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("================================================================================");
        sb.AppendLine("*/");
        sb.AppendLine();
    }

    private static void WriteSummaryLine(StringBuilder sb, List<SchemaChange> nonDestructive, List<SchemaChange> destructive)
    {
        var all = nonDestructive.Concat(destructive).ToList();
        var categories = all.GroupBy(c => c.Category).OrderBy(g => g.Key);

        foreach (var group in categories)
        {
            var adds = group.Count(c => c.Action == ChangeAction.Add);
            var modifies = group.Count(c => c.Action == ChangeAction.Modify);
            var drops = group.Count(c => c.Action == ChangeAction.Drop);

            var parts = new List<string>();
            if (adds > 0) parts.Add($"{adds} added");
            if (modifies > 0) parts.Add($"{modifies} modified");
            if (drops > 0) parts.Add($"{drops} dropped");

            sb.AppendLine($"  - {group.Key}: {string.Join(", ", parts)}");
        }

        if (all.Count == 0)
            sb.AppendLine("  - No changes detected.");
    }

    private void GenerateBody(StringBuilder sb, List<SchemaChange> nonDestructive, List<SchemaChange> destructive)
    {
        sb.AppendLine("BEGIN;");
        sb.AppendLine();

        // Non-destructive changes in dependency order
        var orderedCategories = new[]
        {
            ChangeCategory.Extension,
            ChangeCategory.Schema,
            ChangeCategory.Type,
            ChangeCategory.Sequence,
            ChangeCategory.Table,
            ChangeCategory.Index,
            ChangeCategory.ForeignKey,
            ChangeCategory.View,
            ChangeCategory.MaterializedView,
            ChangeCategory.Function,
            ChangeCategory.Trigger
        };

        foreach (var category in orderedCategories)
        {
            var catChanges = nonDestructive.Where(c => c.Category == category).ToList();
            if (catChanges.Count == 0) continue;

            sb.AppendLine($"-- ══════════════════════════════════════════════════════════════");
            sb.AppendLine($"-- {category} Changes");
            sb.AppendLine($"-- ══════════════════════════════════════════════════════════════");
            sb.AppendLine();

            foreach (var change in catChanges)
            {
                sb.AppendLine($"-- {change.Action.ToString().ToUpperInvariant()}: {change.ObjectType} {change.ObjectName}");
                // Record line number (1-indexed, for the SQL statement start)
                change.LineNumber = sb.ToString().Split('\n').Length;
                sb.AppendLine(change.Sql);
                sb.AppendLine();
            }
        }

        // Destructive changes section
        if (destructive.Count > 0 && _allowDrops)
        {
            sb.AppendLine("-- ╔══════════════════════════════════════════════════════════════╗");
            sb.AppendLine("-- ║           ⚠  DESTRUCTIVE CHANGES — REVIEW CAREFULLY  ⚠     ║");
            sb.AppendLine("-- ╚══════════════════════════════════════════════════════════════╝");
            sb.AppendLine();

            foreach (var category in orderedCategories)
            {
                var catDrops = destructive.Where(c => c.Category == category).ToList();
                if (catDrops.Count == 0) continue;

                sb.AppendLine($"-- DROP {category}:");
                foreach (var drop in catDrops)
                {
                    if (drop.Warning != null)
                        sb.AppendLine($"-- WARNING: {drop.Warning}");
                    drop.LineNumber = sb.ToString().Split('\n').Length;
                    sb.AppendLine(drop.Sql);
                    sb.AppendLine();
                }
            }
        }

        sb.AppendLine("COMMIT;");
    }

    private static void AssignLineNumbers(string body, List<SchemaChange> nonDestructive, List<SchemaChange> destructive)
    {
        // Line numbers are already assigned during body generation
        // This method is kept for the two-pass header line number adjustment
    }
}
