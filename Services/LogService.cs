using System.IO;

namespace KRBWeekly.Services;

/// <summary>
/// 程序日志服务 —— 把运行日志持久化写入 Data/Logs/日志_时间戳.txt，
/// 供「设置 → 数据文件 → 运行日志」只读查看。线程安全，写入失败静默不阻断主流程。
/// </summary>
public static class LogService
{
    private static readonly object Lock = new();
    private static string? _currentFile;

    /// <summary>日志目录（Data/Logs）</summary>
    public static string LogDir { get; private set; } = "";

    /// <summary>初始化日志目录（App 与各页面启动时调用）</summary>
    public static void Init(string dataDir)
    {
        LogDir = Path.Combine(dataDir, "Logs");
    }

    /// <summary>开始一次新的日志会话（每次统计开始时调用），返回本次日志文件路径</summary>
    public static string StartSession()
    {
        lock (Lock)
        {
            EnsureDir();
            _currentFile = NewSessionPath();
            AppendLocked($"日志会话开始: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            return _currentFile;
        }
    }

    /// <summary>生成唯一会话文件路径（同一秒内多次会话时加 _2/_3 后缀）</summary>
    private static string NewSessionPath()
    {
        var baseName = $"日志_{DateTime.Now:yyyyMMdd_HHmmss}";
        var path = Path.Combine(LogDir, baseName + ".txt");
        int i = 2;
        while (File.Exists(path))
            path = Path.Combine(LogDir, $"{baseName}_{i++}.txt");
        return path;
    }

    /// <summary>追加一行日志（自动补时间戳；未开始会话时自动新建日志文件）</summary>
    public static void Append(string message)
    {
        lock (Lock)
        {
            if (LogDir.Length == 0) return;
            if (_currentFile == null) StartSession();
            AppendLocked($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
    }

    private static void EnsureDir()
    {
        try { Directory.CreateDirectory(LogDir); } catch { }
    }

    private static void AppendLocked(string line)
    {
        try
        {
            File.AppendAllText(_currentFile!, line + Environment.NewLine, System.Text.Encoding.UTF8);
        }
        catch { }
    }
}
