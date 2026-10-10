using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WSLCManager.Helpers;

/// <summary>
/// 虚拟磁盘压缩（回收 wslc 会话的 storage.vhdx 空间）。
///
/// 背景（2026-10-10 实测）：删除镜像后，即使 wslc 输出里有「已删除」行，
/// storage.vhdx 的文件大小也**纹丝不动**（拉取 163MB 的 nginx 再删掉，磁盘 +0.0 MB）。
/// 原因是动态 VHDX 只增不减，必须手动压缩。
///
/// 为什么不能用 `wsl --manage &lt;发行版&gt; --compact`：`wsl -l -v` 里**看不到 wslc 的会话**
/// （它不是发行版），该命令管不到这个 vhdx。
///
/// 官方/通用做法（微软文档与社区一致）：
///   1) wsl --shutdown                    —— vhdx 被占用时无法压缩
///   2) diskpart:
///        select vdisk file="&lt;vhdx&gt;"
///        attach vdisk readonly
///        compact vdisk
///        detach vdisk
///   ⚠ diskpart 的 vdisk 操作**必须管理员权限**，所以要提权（UAC 由用户确认）。
/// </summary>
public static class DiskCompactor
{
    /// <summary>要压缩的 vhdx 路径；取不到返回 null。</summary>
    public static string FindStorageVhdx()
    {
        var dir = WSLCManager.Services.WslcService.GetStorageDir();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

        string best = null;
        long bestSize = -1;
        foreach (var f in Directory.EnumerateFiles(dir, "*.vhdx", SearchOption.AllDirectories))
        {
            try
            {
                var fi = new FileInfo(f);
                // storage.vhdx 优先（swap.vhdx 一般不需要压）
                var score = fi.Name.StartsWith("storage", StringComparison.OrdinalIgnoreCase) ? fi.Length + long.MaxValue : fi.Length;
                if (score > bestSize) { bestSize = score; best = f; }
            }
            catch { }
        }
        return best;
    }

    /// <summary>当前 vhdx 大小（字节）；-1 表示取不到。</summary>
    public static long GetVhdxBytes(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return -1; }
    }

    /// <summary>
    /// 提权运行 diskpart 压缩。会弹 UAC，由用户确认。
    /// 返回 (成功, 输出文本, 错误信息)。**调用前必须已执行 wsl --shutdown**。
    /// </summary>
    public static (bool Ok, string Output, string Error) Compact(string vhdxPath)
    {
        if (string.IsNullOrEmpty(vhdxPath) || !File.Exists(vhdxPath))
            return (false, "", "找不到虚拟磁盘文件：" + vhdxPath);

        var work = Path.Combine(Path.GetTempPath(), "wslc_compact");
        Directory.CreateDirectory(work);
        var script = Path.Combine(work, "compact.txt");
        var log = Path.Combine(work, "compact.log");

        // diskpart 脚本：注意路径里有中文（张萌），脚本文件必须 UTF-16（Unicode）编码，
        // 否则 diskpart 按 ANSI 读会乱码导致 select vdisk 失败。
        var sb = new StringBuilder();
        sb.AppendLine("select vdisk file=\"" + vhdxPath + "\"");
        sb.AppendLine("attach vdisk readonly");
        sb.AppendLine("compact vdisk");
        sb.AppendLine("detach vdisk");
        sb.AppendLine("exit");
        File.WriteAllText(script, sb.ToString(), Encoding.Unicode);

        try { File.Delete(log); } catch { }

        // 用 cmd 包装以拿到输出（提权进程的 stdout 无法直接重定向）
        var cmd = "/c chcp 65001 >nul & diskpart /s \"" + script + "\" > \"" + log + "\" 2>&1";
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = cmd,
            Verb = "runas",              // ← UAC 弹窗
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Process p;
        try { p = Process.Start(psi); }
        catch (Exception ex)
        {
            return (false, "", ex.Message.Contains("被取消") || ex.Message.Contains("canceled")
                ? "UAC 授权被取消"
                : "提权失败：" + ex.Message);
        }

        // 等待完成（最长 20 分钟；compact 大盘可能较慢）
        if (!p.WaitForExit(20 * 60 * 1000))
        {
            try { p.Kill(); } catch { }
            return (false, "", "压缩超时（20 分钟）");
        }

        var output = "";
        try { output = File.ReadAllText(log, Encoding.UTF8); } catch { }
        var ok = p.ExitCode == 0 && output.Contains("successfully", StringComparison.OrdinalIgnoreCase)
                                     || output.Contains("成功");
        return (ok, output, ok ? "" : "diskpart 返回码 " + p.ExitCode);
    }

    /// <summary>关闭 WSL（压缩前必须）。wslc 的会话也由它停掉。</summary>
    public static (bool Ok, string Output) ShutdownWsl()
    {
        var wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        if (!File.Exists(wsl)) return (false, "找不到 wsl.exe");

        var psi = new ProcessStartInfo
        {
            FileName = wsl,
            Arguments = "--shutdown",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Unicode,
            StandardErrorEncoding = Encoding.Unicode,
        };
        try
        {
            using var p = Process.Start(psi);
            var outText = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            return (p.ExitCode == 0, outText);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
