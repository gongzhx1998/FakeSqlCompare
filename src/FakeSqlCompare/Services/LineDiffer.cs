namespace FakeSqlCompare;

public static class LineDiffer
{
    private const int MaxLines = 1200;

    public static (IReadOnlyList<SqlLine> Source, IReadOnlyList<SqlLine> Target) Build(string? sourceSql, string? targetSql)
    {
        var a = Split(sourceSql);
        var b = Split(targetSql);
        var aKey = a.Select(CompareKey).ToArray();
        var bKey = b.Select(CompareKey).ToArray();
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
