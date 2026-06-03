using System.Diagnostics;

namespace ClaudeNotch.Core;

/// <summary>
/// 尽力探测运行中的 claude 进程（仅用于设置页「运行中的 claude」计数）。
/// Windows 上拿不到进程 cwd（需读 PEB，脆弱），故会话「活跃」改以 transcript 近期写入判定（见 SessionScanner）。
/// </summary>
public static class ProcessProbe
{
    public static int LiveClaudeCount()
    {
        int count = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var name = p.ProcessName;   // 不含 .exe
                if (name.Contains("claude", StringComparison.OrdinalIgnoreCase)) { count++; continue; }
                // node 宿主的 claude CLI 较难判定，这里仅按可执行路径含 \claude\ 兜底
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { /* 访问受限，跳过 */ }
                if (path is not null && path.Replace('/', '\\').Contains("\\claude\\", StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            catch { /* 进程已退出/无权限 */ }
            finally { p.Dispose(); }
        }
        return count;
    }
}
