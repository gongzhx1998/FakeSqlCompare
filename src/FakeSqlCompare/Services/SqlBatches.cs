using System.Text;
using System.Text.RegularExpressions;

namespace FakeSqlCompare;

public static class SqlBatches
{
    private static readonly Regex Go = new(@"^\s*GO(?:\s+\d+)?\s*(?:--.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SetVar = new(@"^\s*:setvar\s+(\S+)\s+(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SqlCmd = new(@"^\s*:",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex VarRef = new(@"\$\(([^)]+)\)", RegexOptions.Compiled);

    public static IEnumerable<string> Split(string script)
    {
        var sb = new StringBuilder();
        foreach (var line in script.Replace("\r\n", "\n").Split('\n'))
        {
            if (Go.IsMatch(line))
            {
                var batch = sb.ToString().Trim();
                if (batch.Length > 0) yield return batch;
                sb.Clear();
            }
            else
            {
                sb.AppendLine(line);
            }
        }
        var last = sb.ToString().Trim();
        if (last.Length > 0) yield return last;
    }

    /// <summary>
    /// DacFx 脚本带 :setvar / :on error 等 SQLCMD 指令，SqlCommand 不能执行。
    /// 去掉这些行，并把 $(Var) 替换成 :setvar 的值（DatabaseName 默认用目标库名）。
    /// </summary>
    public static IReadOnlyList<string> SplitExecutable(string script, string database)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DatabaseName"] = database
        };
        var list = new List<string>();
        foreach (var batch in Split(script))
        {
            var cleaned = Rewrite(batch, vars);
            if (!string.IsNullOrWhiteSpace(cleaned))
                list.Add(cleaned);
        }
        return list;
    }

    private static string Rewrite(string batch, Dictionary<string, string> vars)
    {
        var sb = new StringBuilder();
        foreach (var raw in batch.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var set = SetVar.Match(line);
            if (set.Success)
            {
                vars[set.Groups[1].Value] = Unquote(set.Groups[2].Value.Trim());
                continue;
            }
            if (SqlCmd.IsMatch(line))
                continue;
            sb.AppendLine(line);
        }

        var text = sb.ToString();
        text = VarRef.Replace(text, m =>
            vars.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);
        return text.Trim();
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
            return value[1..^1];
        return value;
    }
}
