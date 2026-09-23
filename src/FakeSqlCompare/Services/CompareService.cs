using System.Text;
using System.Text.RegularExpressions;
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
        IncludeLooseExtendedProperties(diff);
    }

    private void IncludeLooseExtendedProperties(SchemaDifference owner)
    {
        var host = owner.SourceObject ?? owner.TargetObject;
        if (host is null) return;
        var hostName = host.Name?.ToString();
        if (string.IsNullOrWhiteSpace(hostName)) return;
        foreach (var d in Flatten(Result.Differences))
        {
            var type = (d.SourceObject ?? d.TargetObject)?.ObjectType?.Name;
            if (!string.Equals(type, "ExtendedProperty", StringComparison.OrdinalIgnoreCase)) continue;
            var epHost = CompareService.ResolveOwnerObject(d.SourceObject) ?? CompareService.ResolveOwnerObject(d.TargetObject);
            if (epHost is null) continue;
            if (string.Equals(epHost.Name?.ToString(), hostName, StringComparison.OrdinalIgnoreCase))
            {
                try { Result.Include(d); }
                catch { /* already included */ }
            }
        }
    }

    private static IEnumerable<SchemaDifference> Flatten(IEnumerable<SchemaDifference> diffs)
    {
        foreach (var d in diffs)
        {
            yield return d;
            foreach (var c in Flatten(d.Children))
                yield return c;
        }
    }
}

public static class CompareService
{
    private static readonly Regex TrailingGo = new(@"(\s*GO\s*;?\s*)+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> SkipTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "DatabaseOptions", "Filegroup", "File", "Permission",
        "RoleMembership", "User", "Login", "Role", "Certificate", "AsymmetricKey",
        "SymmetricKey", "Endpoint", "ServerRole", "DatabaseAuditSpecification",
        "ServerAudit", "ServerAuditSpecification", "Statistic"
    };

    private static readonly HashSet<string> FoldIntoOwner = new(StringComparer.OrdinalIgnoreCase)
    {
        "Index", "PrimaryKeyConstraint", "ForeignKeyConstraint",
        "DefaultConstraint", "CheckConstraint", "UniqueConstraint",
        "ExtendedProperty", "Column"
    };

    private static readonly HashSet<string> OwnerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Table", "View", "Procedure", "ScalarFunction", "TableValuedFunction",
        "InlineTableValuedFunction", "MultiStatementTableValuedFunction", "DmlTrigger"
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
        objects.Sort(CompareListOrder);

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
        o.IgnoreExtendedProperties = false;
        o.DropObjectsNotInSource = true;
        o.BlockOnPossibleDataLoss = false;
    }

    private static void Collect(SchemaDifference diff, Dictionary<string, SchemaDifference> map, List<SchemaObject> list)
    {
        if (diff.DifferenceType == SchemaDifferenceType.Object)
        {
            var typeName = (diff.SourceObject ?? diff.TargetObject)?.ObjectType?.Name;
            if (typeName is not null && !SkipTypes.Contains(typeName))
            {
                if (FoldIntoOwner.Contains(typeName))
                {
                    var owner = FindOwner(diff);
                    if (owner is not null)
                    {
                        TryAdd(owner, map, list, forceDifferent: owner.UpdateAction != SchemaUpdateAction.Add
                            && owner.UpdateAction != SchemaUpdateAction.Delete);
                    }
                    else if (string.Equals(typeName, "ExtendedProperty", StringComparison.OrdinalIgnoreCase))
                    {
                        TryAddFromExtendedProperty(diff, map, list);
                    }
                }
                else if (diff.Parent is null || string.Equals(typeName, "DmlTrigger", StringComparison.OrdinalIgnoreCase))
                {
                    TryAdd(diff, map, list, forceDifferent: false);
                }
            }
        }
        foreach (var child in diff.Children)
            Collect(child, map, list);
    }

    private static SchemaDifference? FindOwner(SchemaDifference diff)
    {
        for (var p = diff.Parent; p is not null; p = p.Parent)
        {
            var type = (p.SourceObject ?? p.TargetObject)?.ObjectType?.Name;
            if (type is not null && OwnerTypes.Contains(type))
                return p;
        }
        return null;
    }

    private static void TryAddFromExtendedProperty(SchemaDifference diff, Dictionary<string, SchemaDifference> map, List<SchemaObject> list)
    {
        var srcHost = ResolveOwnerObject(diff.SourceObject);
        var tgtHost = ResolveOwnerObject(diff.TargetObject);
        var host = srcHost ?? tgtHost;
        if (host is null) return;

        var typeName = host.ObjectType?.Name;
        var (schema, name) = SplitName(host, null);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(typeName)) return;
        var id = $"{typeName}:{schema}.{name}";
        if (map.ContainsKey(id)) return;

        var sourceSql = ScriptObject(srcHost);
        var targetSql = ScriptObject(tgtHost);
        var (srcLines, tgtLines) = LineDiffer.Build(sourceSql, targetSql);
        list.Add(new SchemaObject
        {
            Id = id,
            Schema = schema,
            Name = name,
            Type = MapType(typeName),
            Status = DiffStatus.Different,
            Action = "更新目标",
            Script = sourceSql ?? "-- 无单独脚本，见完整部署脚本",
            SourceLines = srcLines,
            TargetLines = tgtLines
        });
        map[id] = diff;
    }

    internal static TSqlObject? ResolveOwnerObject(TSqlObject? obj)
    {
        if (obj is null) return null;
        var host = FirstReferenced(obj) ?? obj;
        var type = host.ObjectType?.Name;
        if (type is not null && OwnerTypes.Contains(type))
            return host;
        try
        {
            var parent = host.GetParent(DacQueryScopes.UserDefined) ?? host.GetParent();
            var pt = parent?.ObjectType?.Name;
            if (pt is not null && OwnerTypes.Contains(pt))
                return parent;
        }
        catch { /* no parent */ }
        return type is not null && OwnerTypes.Contains(type) ? host : null;
    }

    private static TSqlObject? FirstReferenced(TSqlObject obj)
    {
        try
        {
            return obj.GetReferenced(DacQueryScopes.UserDefined).FirstOrDefault()
                ?? obj.GetReferenced().FirstOrDefault();
        }
        catch
        {
            try { return obj.GetReferenced().FirstOrDefault(); }
            catch { return null; }
        }
    }

    private static void TryAdd(SchemaDifference diff, Dictionary<string, SchemaDifference> map, List<SchemaObject> list, bool forceDifferent)
    {
        var typeName = (diff.SourceObject ?? diff.TargetObject)?.ObjectType?.Name;
        var obj = ToSchemaObject(diff, typeName, forceDifferent);
        if (obj is null) return;
        if (!map.TryAdd(obj.Id, diff)) return;
        list.Add(obj);
    }

    private static int CompareListOrder(SchemaObject a, SchemaObject b)
    {
        var rank = TypeRank(a.Type).CompareTo(TypeRank(b.Type));
        return rank != 0 ? rank : string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase);
    }

    private static int TypeRank(string type) => type switch
    {
        "表" => 0,
        "视图" => 1,
        "存储过程" => 2,
        "函数" => 3,
        "触发器" => 4,
        _ => 20
    };

    internal static string? MakeKey(SchemaDifference diff)
    {
        var typeName = (diff.SourceObject ?? diff.TargetObject)?.ObjectType?.Name;
        if (string.IsNullOrEmpty(typeName)) return null;
        var (schema, name) = SplitName(diff.SourceObject ?? diff.TargetObject, diff.Name?.ToString());
        return string.IsNullOrWhiteSpace(name) ? null : $"{typeName}:{schema}.{name}";
    }

    private static SchemaObject? ToSchemaObject(SchemaDifference diff, string? typeName, bool forceDifferent)
    {
        var (schema, name) = SplitName(diff.SourceObject ?? diff.TargetObject, diff.Name?.ToString());
        if (string.IsNullOrWhiteSpace(name)) return null;

        var status = diff.UpdateAction switch
        {
            SchemaUpdateAction.Add => DiffStatus.SourceOnly,
            SchemaUpdateAction.Delete => DiffStatus.TargetOnly,
            SchemaUpdateAction.Change => DiffStatus.Different,
            _ => DiffStatus.Equal
        };
        if (forceDifferent && status == DiffStatus.Equal)
            status = DiffStatus.Different;

        var type = MapType(typeName);
        var sourceSql = ScriptObject(diff.SourceObject);
        var targetSql = ScriptObject(diff.TargetObject);
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

    private static (string Schema, string Name) SplitName(TSqlObject? obj, string? fallback)
    {
        var parts = obj?.Name?.Parts;
        if (parts is { Count: >= 2 })
            return (parts[0], parts[1]);
        if (parts is { Count: 1 })
            return ("dbo", parts[0]);

        var raw = fallback ?? "object";
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

    private static string? ScriptObject(TSqlObject? obj)
    {
        if (obj is null) return null;
        var type = obj.ObjectType?.Name;
        if (type is "Table" or "View")
            return ScriptTableLike(obj);

        var batches = new List<string>();
        var core = StripGo(TryScript(obj));
        if (!string.IsNullOrWhiteSpace(core))
            batches.Add(core);
        batches.AddRange(ScriptExtendedProperties(obj));
        return batches.Count == 0 ? null : string.Join("\r\nGO\r\n", batches);
    }

    private static string? ScriptTableLike(TSqlObject table)
    {
        var batches = new List<string>();
        var create = StripGo(TryScript(table));
        if (!string.IsNullOrWhiteSpace(create))
            batches.Add(create);

        var quoted = QuoteName(table);
        var children = GetChildren(table).ToList();
        var reservedIndexNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in children)
        {
            var t = child.ObjectType?.Name;
            if (t is "PrimaryKeyConstraint" or "UniqueConstraint")
            {
                var n = LastPart(child);
                if (!string.IsNullOrEmpty(n)) reservedIndexNames.Add(n);
            }
        }

        foreach (var fk in children.Where(c => c.ObjectType?.Name == "ForeignKeyConstraint"))
        {
            if (!IsDisabled(fk)) continue;
            var fkName = LastPart(fk);
            if (string.IsNullOrEmpty(fkName) || quoted is null) continue;
            batches.Add($"ALTER TABLE {quoted} NOCHECK CONSTRAINT [{fkName}];");
        }

        foreach (var index in children.Where(c => c.ObjectType?.Name == "Index"))
        {
            var n = LastPart(index);
            if (!string.IsNullOrEmpty(n) && reservedIndexNames.Contains(n)) continue;
            var script = StripGo(TryScript(index));
            if (!string.IsNullOrWhiteSpace(script))
                batches.Add(script);
        }

        batches.AddRange(ScriptExtendedProperties(table));

        if (batches.Count == 0) return null;
        return string.Join("\r\nGO\r\n", batches);
    }

    private static IEnumerable<string> ScriptExtendedProperties(TSqlObject owner)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (host, ep) in EnumerateExtendedProperties(owner))
        {
            var sql = FormatExtendedProperty(owner, host, ep);
            if (string.IsNullOrWhiteSpace(sql) || !seen.Add(sql)) continue;
            yield return sql;
        }
    }

    private static IEnumerable<(TSqlObject Host, TSqlObject Ep)> EnumerateExtendedProperties(TSqlObject owner)
    {
        foreach (var ep in ReferencingEps(owner))
            yield return (owner, ep);

        foreach (var child in GetChildren(owner))
        {
            var t = child.ObjectType?.Name;
            if (t is not ("Column" or "Index" or "PrimaryKeyConstraint" or "ForeignKeyConstraint"
                or "DefaultConstraint" or "CheckConstraint" or "UniqueConstraint" or "DmlTrigger"))
                continue;
            foreach (var ep in ReferencingEps(child))
                yield return (child, ep);
        }
    }

    private static IEnumerable<TSqlObject> ReferencingEps(TSqlObject obj)
    {
        var list = new List<TSqlObject>();
        try
        {
            list.AddRange(obj.GetReferencing(DacQueryScopes.UserDefined)
                .Where(x => string.Equals(x.ObjectType?.Name, "ExtendedProperty", StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            try
            {
                list.AddRange(obj.GetReferencing()
                    .Where(x => string.Equals(x.ObjectType?.Name, "ExtendedProperty", StringComparison.OrdinalIgnoreCase)));
            }
            catch { /* ignore */ }
        }
        try
        {
            list.AddRange(obj.GetReferenced(DacQueryScopes.UserDefined)
                .Where(x => string.Equals(x.ObjectType?.Name, "ExtendedProperty", StringComparison.OrdinalIgnoreCase)));
        }
        catch { /* ignore */ }
        return list;
    }

    private static string? FormatExtendedProperty(TSqlObject owner, TSqlObject host, TSqlObject ep)
    {
        var propName = LastPart(ep);
        if (string.IsNullOrWhiteSpace(propName))
            propName = ep.Name?.ToString()?.Trim('[', ']') ?? "";
        if (string.IsNullOrWhiteSpace(propName)) return null;

        var value = ReadExtendedPropertyValue(ep);
        var ownerParts = owner.Name?.Parts;
        var schema = ownerParts is { Count: >= 2 } ? ownerParts[0] : "dbo";
        var ownerName = ownerParts is { Count: >= 2 } ? ownerParts[1] : ownerParts is { Count: 1 } ? ownerParts[0] : LastPart(owner);
        if (string.IsNullOrWhiteSpace(ownerName)) return null;

        var sb = new StringBuilder();
        sb.Append("EXECUTE sp_addextendedproperty @name = N'").Append(SqlLiteral(propName)).Append("'");
        sb.Append(", @value = N'").Append(SqlLiteral(value)).Append("'");
        sb.Append(", @level0type = N'SCHEMA', @level0name = N'").Append(SqlLiteral(schema)).Append("'");
        sb.Append(", @level1type = N'").Append(Level1Type(owner.ObjectType?.Name)).Append("'");
        sb.Append(", @level1name = N'").Append(SqlLiteral(ownerName)).Append("'");
        if (!IsSameObject(owner, host))
        {
            var level2 = Level2Type(host.ObjectType?.Name);
            var hostName = LastPart(host);
            if (!string.IsNullOrEmpty(level2) && !string.IsNullOrEmpty(hostName))
            {
                sb.Append(", @level2type = N'").Append(level2).Append("'");
                sb.Append(", @level2name = N'").Append(SqlLiteral(hostName)).Append("'");
            }
        }
        sb.Append(';');
        return sb.ToString();
    }

    private static string ReadExtendedPropertyValue(TSqlObject ep)
    {
        try
        {
            var typed = ep.GetProperty<string>(ExtendedProperty.Value);
            if (typed is not null) return typed;
        }
        catch { /* 属性类型可能不是 string */ }
        try
        {
            var raw = ep.GetProperty<object>(ExtendedProperty.Value);
            if (raw is not null) return raw.ToString() ?? "";
        }
        catch { /* ignore */ }
        var prop = ep.ObjectType.Properties.FirstOrDefault(p =>
            string.Equals(p.Name, "Value", StringComparison.OrdinalIgnoreCase));
        if (prop is not null)
        {
            try { return ep.GetProperty<object>(prop)?.ToString() ?? ""; }
            catch { /* ignore */ }
        }
        return "";
    }

    private static string Level1Type(string? dac) => dac switch
    {
        "Table" => "TABLE",
        "View" => "VIEW",
        "Procedure" => "PROCEDURE",
        "ScalarFunction" or "TableValuedFunction" or "InlineTableValuedFunction"
            or "MultiStatementTableValuedFunction" => "FUNCTION",
        "DmlTrigger" => "TRIGGER",
        "Synonym" => "SYNONYM",
        "Sequence" => "SEQUENCE",
        _ => "TABLE"
    };

    private static string? Level2Type(string? dac) => dac switch
    {
        "Column" => "COLUMN",
        "Index" => "INDEX",
        "PrimaryKeyConstraint" or "ForeignKeyConstraint" or "DefaultConstraint"
            or "CheckConstraint" or "UniqueConstraint" => "CONSTRAINT",
        "DmlTrigger" => "TRIGGER",
        "Parameter" => "PARAMETER",
        _ => null
    };

    private static bool IsSameObject(TSqlObject a, TSqlObject b)
    {
        if (ReferenceEquals(a, b)) return true;
        return string.Equals(a.Name?.ToString(), b.Name?.ToString(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.ObjectType?.Name, b.ObjectType?.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static string SqlLiteral(string value) => value.Replace("'", "''");

    private static IEnumerable<TSqlObject> GetChildren(TSqlObject obj)
    {
        try { return obj.GetChildren(DacQueryScopes.UserDefined); }
        catch
        {
            try { return obj.GetChildren(); }
            catch { return []; }
        }
    }

    private static bool IsDisabled(TSqlObject obj)
    {
        foreach (var name in new[] { "Disabled", "IsDisabled" })
        {
            var prop = obj.ObjectType.Properties.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (prop is null) continue;
            try
            {
                var value = obj.GetProperty<object>(prop);
                if (value is bool b) return b;
            }
            catch { /* ignore */ }
        }
        return false;
    }

    private static string? QuoteName(TSqlObject obj)
    {
        var parts = obj.Name?.Parts;
        if (parts is { Count: >= 2 })
            return $"[{parts[0]}].[{parts[1]}]";
        if (parts is { Count: 1 })
            return $"[dbo].[{parts[0]}]";
        return null;
    }

    private static string LastPart(TSqlObject obj)
        => obj.Name?.Parts is { Count: > 0 } parts ? parts[^1] : "";

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

    private static string? StripGo(string? script)
    {
        if (string.IsNullOrWhiteSpace(script)) return null;
        return TrailingGo.Replace(script.Trim(), "").Trim();
    }
}
