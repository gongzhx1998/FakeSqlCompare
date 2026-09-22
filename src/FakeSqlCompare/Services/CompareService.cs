using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Compare;
using Microsoft.SqlServer.Dac.Model;

namespace FakeSqlCompare;

public sealed class CompareSession
{
    public required SchemaComparison Comparison { get; init; }
    public required SchemaComparisonResult Result { get; init; }
    public required string TargetDatabase { get; init; }
    public required string TargetConnectionString { get; init; }
    public required List<SchemaObject> Objects { get; init; }
    public required Dictionary<string, SchemaDifference> Diffs { get; init; }

    public string GenerateScript(IEnumerable<string> selectedKeys, bool includeDrops)
    {
        var selected = selectedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Result.ExcludeAll();
        foreach (var diff in Result.Differences)
            IncludeSelected(diff, selected, includeDrops);

        Comparison.Options.DropObjectsNotInSource = includeDrops;
        var gen = Result.GenerateScript(TargetDatabase);
        if (!gen.Success)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(gen.Message)
                ? "未能生成部署脚本。"
                : gen.Message);
        return gen.Script ?? "";
    }

    private void IncludeSelected(SchemaDifference diff, HashSet<string> selected, bool includeDrops)
    {
        var key = CompareService.MakeKey(diff);
        var drop = diff.UpdateAction == SchemaUpdateAction.Delete;
        if (key is not null && selected.Contains(key) && (!drop || includeDrops))
            IncludeTree(diff);
        else
            foreach (var child in diff.Children)
                IncludeSelected(child, selected, includeDrops);
    }

    private void IncludeTree(SchemaDifference diff)
    {
        Result.Include(diff);
        try
        {
            foreach (var dep in Result.GetIncludeDependencies(diff))
                Result.Include(dep);
        }
        catch { /* 无依赖或 API 不可用 */ }
        foreach (var child in diff.Children)
            IncludeTree(child);
    }
}

public static class CompareService
{
    private static readonly HashSet<string> SkipTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "DatabaseOptions", "Filegroup", "File", "ExtendedProperty", "Permission",
        "RoleMembership", "User", "Login", "Role", "Certificate", "AsymmetricKey",
        "SymmetricKey", "Endpoint", "ServerRole", "DatabaseAuditSpecification",
        "ServerAudit", "ServerAuditSpecification"
    };

    public static CompareSession Compare(ConnectionProfile source, ConnectionProfile target, CancellationToken ct)
    {
        var sourceCs = ConnectionService.Build(source, timeoutSeconds: 30);
        var targetCs = ConnectionService.Build(target, timeoutSeconds: 30);
        var sourceEp = new SchemaCompareDatabaseEndpoint(sourceCs);
        var targetEp = new SchemaCompareDatabaseEndpoint(targetCs);
        var comparison = new SchemaComparison(sourceEp, targetEp);
        ApplyOptions(comparison.Options);

        var result = comparison.Compare(ct);
        if (result is null)
            throw new InvalidOperationException("对比失败：DacFx 未返回结果。");
        if (!result.IsValid)
        {
            var errors = string.Join("；", result.GetErrors().Select(e => e.ToString()));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errors)
                ? "对比结果无效。"
                : "对比失败：" + errors);
        }

        var diffs = new Dictionary<string, SchemaDifference>(StringComparer.OrdinalIgnoreCase);
        var objects = new List<SchemaObject>();
        foreach (var diff in result.Differences)
            Collect(diff, diffs, objects);

        return new CompareSession
        {
            Comparison = comparison,
            Result = result,
            TargetDatabase = target.Database,
            TargetConnectionString = targetCs,
            Objects = objects,
            Diffs = diffs
        };
    }

    private static void ApplyOptions(DacDeployOptions o)
    {
        o.IgnoreWhitespace = true;
        o.IgnoreComments = true;
        o.IgnoreKeywordCasing = true;
        o.IgnoreSemicolonBetweenStatements = true;
        o.IgnoreColumnOrder = true;
        o.IgnoreFilegroupPlacement = true;
        o.IgnorePermissions = true;
        o.IgnoreExtendedProperties = true;
        o.DropObjectsNotInSource = true;
        o.BlockOnPossibleDataLoss = false;
    }

    private static void Collect(SchemaDifference diff, Dictionary<string, SchemaDifference> map, List<SchemaObject> list)
    {
        if (diff.DifferenceType == SchemaDifferenceType.Object && diff.Parent is null)
        {
            var typeName = (diff.SourceObject ?? diff.TargetObject)?.ObjectType?.Name;
            if (typeName is null || !SkipTypes.Contains(typeName))
            {
                var obj = ToSchemaObject(diff, typeName);
                if (obj is not null)
                {
                    map[obj.Id] = diff;
                    list.Add(obj);
                }
            }
        }
        foreach (var child in diff.Children)
            Collect(child, map, list);
    }

    internal static string? MakeKey(SchemaDifference diff)
    {
        var typeName = (diff.SourceObject ?? diff.TargetObject)?.ObjectType?.Name;
        if (string.IsNullOrEmpty(typeName)) return null;
        var (schema, name) = SplitName(diff);
        return string.IsNullOrWhiteSpace(name) ? null : $"{typeName}:{schema}.{name}";
    }

    private static SchemaObject? ToSchemaObject(SchemaDifference diff, string? typeName)
    {
        var (schema, name) = SplitName(diff);
        if (string.IsNullOrWhiteSpace(name)) return null;

        var status = diff.UpdateAction switch
        {
            SchemaUpdateAction.Add => DiffStatus.SourceOnly,
            SchemaUpdateAction.Delete => DiffStatus.TargetOnly,
            SchemaUpdateAction.Change => DiffStatus.Different,
            _ => DiffStatus.Equal
        };
        var type = MapType(typeName);
        var sourceSql = TryScript(diff.SourceObject);
        var targetSql = TryScript(diff.TargetObject);
        var (srcLines, tgtLines) = LineDiffer.Build(sourceSql, targetSql);
        var script = status switch
        {
            DiffStatus.TargetOnly => $"DROP {DropKeyword(type)} {Quote(schema, name)};",
            _ => sourceSql ?? "-- 无单独脚本，见完整部署脚本"
        };

        return new SchemaObject
        {
            Id = $"{typeName}:{schema}.{name}",
            Schema = schema,
            Name = name,
            Type = type,
            Status = status,
            Action = status switch
            {
                DiffStatus.SourceOnly => "在目标创建",
                DiffStatus.TargetOnly => "从目标删除",
                DiffStatus.Different => "更新目标",
                _ => "无需变更"
            },
            Script = script,
            SourceLines = srcLines,
            TargetLines = tgtLines
        };
    }

    private static (string Schema, string Name) SplitName(SchemaDifference diff)
    {
        var obj = diff.SourceObject ?? diff.TargetObject;
        var parts = obj?.Name?.Parts;
        if (parts is { Count: >= 2 })
            return (parts[^2], parts[^1]);
        if (parts is { Count: 1 })
            return ("dbo", parts[0]);

        var raw = diff.Name?.ToString() ?? "object";
        raw = raw.Replace("[", "").Replace("]", "");
        var idx = raw.LastIndexOf('.');
        return idx > 0 ? (raw[..idx], raw[(idx + 1)..]) : ("dbo", raw);
    }

    private static string MapType(string? dac) => dac switch
    {
        "Table" => "表",
        "View" => "视图",
        "Procedure" => "存储过程",
        "ScalarFunction" or "TableValuedFunction" or "InlineTableValuedFunction"
            or "MultiStatementTableValuedFunction" => "函数",
        "DmlTrigger" => "触发器",
        "Index" => "索引",
        "PrimaryKeyConstraint" => "主键",
        "ForeignKeyConstraint" => "外键",
        "DefaultConstraint" => "默认约束",
        "CheckConstraint" => "检查约束",
        "UniqueConstraint" => "唯一约束",
        "Sequence" => "序列",
        "Synonym" => "同义词",
        _ => string.IsNullOrEmpty(dac) ? "对象" : dac
    };

    private static string DropKeyword(string type) => type switch
    {
        "表" => "TABLE",
        "视图" => "VIEW",
        "存储过程" => "PROCEDURE",
        "函数" => "FUNCTION",
        "触发器" => "TRIGGER",
        "序列" => "SEQUENCE",
        "同义词" => "SYNONYM",
        _ => "OBJECT"
    };

    private static string Quote(string schema, string name) => $"[{schema}].[{name}]";

    private static string? TryScript(TSqlObject? obj)
    {
        if (obj is null) return null;
        try
        {
            if (obj.TryGetScript(out var script) && !string.IsNullOrWhiteSpace(script))
                return script;
        }
        catch { /* some types cannot script */ }
        return null;
    }
}
