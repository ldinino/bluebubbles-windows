namespace BlueBubbles.Core.Services;

/// <summary>
/// Keeps the log folder bounded: deletes files older than a retention window, then drops the
/// oldest files until the folder fits under a total-size cap. The newest file is always kept.
/// Extracted from <see cref="AppLog"/> so the policy is unit-testable against a temp directory.
/// </summary>
public static class LogRetention
{
    public static void Prune(string dir, int retentionDays, long maxTotalBytes)
    {
        try
        {
            var d = new DirectoryInfo(dir);
            if (!d.Exists) return;

            // 1) Age-based: drop anything past the retention window.
            var cutoff = DateTime.Now.AddDays(-retentionDays);
            foreach (var f in d.GetFiles("*.log"))
            {
                if (f.LastWriteTime < cutoff)
                {
                    try { f.Delete(); } catch { /* file may be locked; skip */ }
                }
            }

            // 2) Size-cap: drop oldest-first until under the cap, never the newest file.
            var remaining = d.GetFiles("*.log")
                .OrderBy(f => f.LastWriteTime)
                .ToList();
            long total = remaining.Sum(f => f.Length);
            for (var i = 0; total > maxTotalBytes && i < remaining.Count - 1; i++)
            {
                total -= remaining[i].Length;
                try { remaining[i].Delete(); } catch { /* skip locked files */ }
            }
        }
        catch { /* best-effort */ }
    }
}
