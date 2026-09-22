using System.IO;
using Microsoft.Data.SqlClient;

namespace FakeSqlCompare;

public static class DeployService
{
    public static async Task RunAsync(
        string connectionString,
        string database,
        string script,
        bool backup,
        IProgress<string> log,
        CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        if (backup)
        {
            try
            {
                var path = await BackupAsync(conn, database, ct);
                log.Report($"✓ BACKUP DATABASE [{database}] → {path}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("备份失败，已中止更新：" + ConnectionService.Describe(ex));
            }
        }
        else
        {
            log.Report("✓ 已跳过备份");
        }

        var batches = SqlBatches.SplitExecutable(script, database).ToList();
        if (batches.Count == 0)
            throw new InvalidOperationException("脚本为空，没有可执行的语句。");
        log.Report($"✓ 已去掉 SQLCMD 指令（:setvar 等），共 {batches.Count} 批");

        var index = 0;
        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = batch;
                cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync(ct);
                log.Report($"✓ 批次 {index}/{batches.Count} 成功");
            }
            catch (Exception ex)
            {
                var first = batch.Split('\n')[0].Trim();
                if (first.Length > 88) first = first[..88] + "…";
                log.Report($"✗ 批次 {index}/{batches.Count} 失败：{ConnectionService.Describe(ex)}");
                log.Report($"    {first}");
                throw new InvalidOperationException($"执行到第 {index} 批失败，后续未继续。失败不等于全部撤销。");
            }
        }

        log.Report($"✓ 完成：{batches.Count} 个批次已应用到 {database}");
    }

    private static async Task<string> BackupAsync(SqlConnection conn, string database, CancellationToken ct)
    {
        await using var dirCmd = conn.CreateCommand();
        dirCmd.CommandText = "SELECT CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultBackupPath'));";
        var dir = (await dirCmd.ExecuteScalarAsync(ct)) as string;
        if (string.IsNullOrWhiteSpace(dir))
            throw new InvalidOperationException("无法取得实例默认备份目录（需要 SQL Server 2019+ 的 InstanceDefaultBackupPath）。");

        var file = Path.Combine(dir.TrimEnd('\\', '/'), $"{database}_{DateTime.Now:yyyyMMdd_HHmmss}.bak");
        await using var bak = conn.CreateCommand();
        bak.CommandTimeout = 0;
        bak.CommandText = $"BACKUP DATABASE [{database.Replace("]", "]]")}] TO DISK = @path WITH COPY_ONLY, INIT, STATS = 10";
        bak.Parameters.AddWithValue("@path", file);
        await bak.ExecuteNonQueryAsync(ct);
        return file;
    }
}
