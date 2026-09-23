using System.Text.RegularExpressions;

namespace FakeSqlCompare;

public static class LineDiffer
{
    private const int MaxLines = 1200;

    private static readonly Regex IndexNameRx = new(
        @"CREATE\s+(?:UNIQUE\s+)?(?:NONCLUSTERED\s+|CLUSTERED\s+)?INDEX\s+(?:\[(?<n>[^\]]+)\]|(?<n>\S+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ConstraintNameRx = new(
        @"\bCONSTRAINT\s+(?:\[(?<n>[^\]]+)\]|(?<n>\S+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static (IReadOnlyList<SqlLine> Source, IReadOnlyList<SqlLine> Target) Build(string? sourceSql, string? targetSql)
    {
        var a = Split(sourceSql);
        var b = Split(targetSql);
        if (a.Length == 0 && b.Length == 0)
        {
            var empty = new SqlLine { Kind = LineKind.Eq, Text = "-- （无脚本）" };
            return ([empty], [empty]);
        }
        if (a.Length == 0)
            return ([new SqlLine { Kind = LineKind.Del, Text = "-- （源不存在）" }], b.Select(t => new SqlLine { Kind = LineKind.Add, Text = t }).ToList());
        if (b.Length == 0)
            return (a.Select(t => new SqlLine { Kind = LineKind.Add, Text = t }).ToList(), [new SqlLine { Kind = LineKind.Del, Text = "-- （目标不存在）" }]);

        if (a.Length > MaxLines || b.Length > MaxLines)
        {
            static IReadOnlyList<SqlLine> Plain(string[] lines) =>
                lines.Take(MaxLines).Select(t => new SqlLine { Kind = LineKind.Eq, Text = t })
                    .Concat(lines.Length > MaxLines ? [new SqlLine { Kind = LineKind.Eq, Text = $"-- … 已截断，共 {lines.Length} 行" }] : Array.Empty<SqlLine>())
                    .ToList();
            return (Plain(a), Plain(b));
        }

        var aBatches = SplitBatches(a);
        var bBatches = SplitBatches(b);
        if (aBatches.Count <= 1 && bBatches.Count <= 1)
            return AlignLines(a, b);

        return AlignBatches(aBatches, bBatches);
    }

    private static (IReadOnlyList<SqlLine> Source, IReadOnlyList<SqlLine> Target) AlignBatches(
        List<string[]> aBatches, List<string[]> bBatches)
    {
        var used = new bool[bBatches.Count];
        var pairs = new List<(string[]? Src, string[]? Tgt)>(aBatches.Count + bBatches.Count);
        for (var i = 0; i < aBatches.Count; i++)
        {
            var id = BatchIdentity(aBatches[i]);
            var j = -1;
            for (var k = 0; k < bBatches.Count; k++)
            {
                if (used[k]) continue;
                if (string.Equals(BatchIdentity(bBatches[k]), id, StringComparison.OrdinalIgnoreCase))
                {
                    j = k;
                    break;
                }
            }
            if (j >= 0)
            {
                used[j] = true;
                pairs.Add((aBatches[i], bBatches[j]));
            }
            else
            {
                pairs.Add((aBatches[i], null));
            }
        }
        for (var k = 0; k < bBatches.Count; k++)
        {
            if (!used[k])
                pairs.Add((null, bBatches[k]));
        }

        var src = new List<SqlLine>();
        var tgt = new List<SqlLine>();
        for (var p = 0; p < pairs.Count; p++)
        {
            if (p > 0)
            {
                src.Add(new SqlLine { Kind = LineKind.Eq, Text = "GO" });
                tgt.Add(new SqlLine { Kind = LineKind.Eq, Text = "GO" });
            }

            var (sa, sb) = pairs[p];
            if (sa is not null && sb is not null)
            {
                var aligned = AlignLines(sa, sb);
                src.AddRange(aligned.Source);
                tgt.AddRange(aligned.Target);
            }
            else if (sa is not null)
            {
                foreach (var line in sa)
                {
                    src.Add(new SqlLine { Kind = LineKind.Add, Text = line });
                    tgt.Add(new SqlLine { Kind = LineKind.Del, Text = "（无对应行）" });
                }
            }
            else if (sb is not null)
            {
                foreach (var line in sb)
                {
                    src.Add(new SqlLine { Kind = LineKind.Del, Text = "（无对应行）" });
                    tgt.Add(new SqlLine { Kind = LineKind.Add, Text = line });
                }
            }
        }
        return (src, tgt);
    }

    private static (IReadOnlyList<SqlLine> Source, IReadOnlyList<SqlLine> Target) AlignLines(string[] a, string[] b)
    {
        var aKey = a.Select(CompareKey).ToArray();
        var bKey = b.Select(CompareKey).ToArray();
        var lcs = Lcs(aKey, bKey);
        var src = new List<SqlLine>();
        var tgt = new List<SqlLine>();
        int i = 0, j = 0, k = 0;
        while (i < a.Length || j < b.Length)
        {
            var next = k < lcs.Count ? lcs[k] : (-1, -1);
            var srcUnmatched = i < a.Length && i != next.Item1;
            var tgtUnmatched = j < b.Length && j != next.Item2;
            if (srcUnmatched && tgtUnmatched)
            {
                src.Add(new SqlLine { Kind = LineKind.Chg, Text = a[i++] });
                tgt.Add(new SqlLine { Kind = LineKind.Chg, Text = b[j++] });
            }
            else if (srcUnmatched)
            {
                src.Add(new SqlLine { Kind = LineKind.Add, Text = a[i++] });
                tgt.Add(new SqlLine { Kind = LineKind.Del, Text = "（无对应行）" });
            }
            else if (tgtUnmatched)
            {
                src.Add(new SqlLine { Kind = LineKind.Del, Text = "（无对应行）" });
                tgt.Add(new SqlLine { Kind = LineKind.Add, Text = b[j++] });
            }
            else if (k < lcs.Count)
            {
                src.Add(new SqlLine { Kind = LineKind.Eq, Text = a[i++] });
                tgt.Add(new SqlLine { Kind = LineKind.Eq, Text = b[j++] });
                k++;
            }
            else break;
        }
        return (src, tgt);
    }

    private static string[] Split(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return [];
        return sql.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    private static List<string[]> SplitBatches(string[] lines)
    {
        var batches = new List<string[]>();
        var cur = new List<string>();
        foreach (var line in lines)
        {
            if (IsGo(line))
            {
                if (cur.Count > 0)
                {
                    batches.Add(cur.ToArray());
                    cur.Clear();
                }
                continue;
            }
            cur.Add(line);
        }
        if (cur.Count > 0)
            batches.Add(cur.ToArray());
        return batches;
    }

    private static bool IsGo(string line)
    {
        var t = line.Trim();
        if (t.EndsWith(';')) t = t[..^1].TrimEnd();
        return t.Equals("GO", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 索引/约束按名称配对，避免 DacFx 子对象顺序不同时把同一条索引对到别的行上。
    /// </summary>
    private static string BatchIdentity(string[] lines)
    {
        var text = string.Join('\n', lines.Select(CompareKey));
        var index = IndexNameRx.Match(text);
        if (index.Success)
            return "INDEX:" + index.Groups["n"].Value;
        var constraint = ConstraintNameRx.Match(text);
        if (constraint.Success)
            return "CONSTRAINT:" + constraint.Groups["n"].Value;
        if (text.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
            return "TABLE";
        if (text.Contains("CREATE VIEW", StringComparison.OrdinalIgnoreCase))
            return "VIEW";
        return "SQL:" + text;
    }

    /// <summary>
    /// CREATE TABLE 最后一列没有逗号，中间列有逗号。只差句尾逗号时不算变更。
    /// </summary>
    private static string CompareKey(string line)
    {
        var t = line.TrimEnd();
        if (t.EndsWith(',')) t = t[..^1].TrimEnd();
        return t;
    }

    private static List<(int, int)> Lcs(string[] a, string[] b)
    {
        var n = a.Length;
        var m = b.Length;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        for (var j = m - 1; j >= 0; j--)
            dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var path = new List<(int, int)>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                path.Add((x, y));
                x++; y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1]) x++;
            else y++;
        }
        return path;
    }
}
