using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace PLC_Dispatcher_SXM_Rework
{
 public static class FileLogger
 {
  private static string baseDir = "Logs";    // 紀錄APP和 PLC或WS交握資訊儲存位置
  private static bool isEnabled = true;      // LOG啟用
  private static int retentionDays = 30;     // 保存天數
  private static Timer dailyCleanTimer;
  /// <summary>
  /// 保護例外日誌檔寫入的鎖 (多執行緒同時 AppendAllText 同一個檔案會互撞)
  /// </summary>
  private static readonly object exceptionLogLock = new object();
  private static readonly BlockingCollection<LogEntry> logQueue = new BlockingCollection<LogEntry>(new ConcurrentQueue<LogEntry>());
  /// <summary>
  /// Log 佇列的資料結構，用來儲存單筆日誌的目標檔案路徑與整行內容
  /// </summary>
  private class LogEntry
  {
   /// <summary>
   /// 完整檔案路徑 (入佇列當下就決定，跨日不會寫錯檔)
   /// </summary>
   public string FilePath;
   /// <summary>
   /// 整行內容 (包含時間戳與訊息)
   /// </summary>
   public string Line;
  }
  /// <summary>
  /// 背景寫入執行緒：程式執行期間持續從佇列取資料寫檔 (FIFO，先進先寫)
  /// </summary>
  static FileLogger()
  {
   Thread writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "FileLoggerWriter" };
   writerThread.Start();
  }

  private static void WriterLoop()
  {
   foreach (LogEntry entry in logQueue.GetConsumingEnumerable())
   {
    try
    {
     string folder = Path.GetDirectoryName(entry.FilePath);
     if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
     {
      Directory.CreateDirectory(folder);
     }
     File.AppendAllText(entry.FilePath, entry.Line, Encoding.UTF8);
    }
    catch (Exception ex)
    {
     // 當寫檔發生異常時，寫入當月例外日誌
     WriteExceptionLog($"[WriterError] {ex.Message} | Target: {entry.FilePath}");
    }
   }
  }

  /// <summary>
  /// 記錄被 catch 住、不中斷程式的例外到當月例外日誌 (yyyyMM_exceptionlog.txt)，
  /// 讓原本靜默吞掉的 catch 也能事後追查
  /// </summary>
  /// <param name="source">發生位置 (例：SocketClientClass.Receive)</param>
  /// <param name="ex">捕捉到的例外</param>
  public static void WriteException(string source, Exception ex)
  {
   if (ex == null) return;
   WriteExceptionLog($"[{source}] {ex.GetType().Name}: {ex.Message} | Stack: {ex.StackTrace}");
  }

  /// <summary>
  /// 共用的例外日誌寫入方法：寫入當月月份資料夾內的 yyyyMM_exceptionlog.txt
  /// </summary>
  private static void WriteExceptionLog(string errorDetails)
  {
   try
   {
    lock (exceptionLogLock)
    {
     DateTime now = DateTime.Now;
     string yearMonthFolder = now.ToString("yyyyMM");
     string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, baseDir, yearMonthFolder);
     if (!Directory.Exists(folder))
     {
      Directory.CreateDirectory(folder);
     }

     string emergencyPath = Path.Combine(folder, $"{yearMonthFolder}_exceptionlog.txt");
     string errorLog = $"{now:yyyy-MM-dd HH:mm:ss.fff} | {errorDetails}\r\n";
     File.AppendAllText(emergencyPath, errorLog, Encoding.UTF8);
    }
   }
   catch
   {
    // 若連備用檔都寫不進去則靜默吞掉，保護執行緒不崩潰
   }
  }

  /// <summary>
  /// 初始化 Log 系統：設定保留天數，並立即清除一次過期 log 後排程每天午夜清除
  /// </summary>
  /// <param name="retention">Log 檔保留天數 (appsetting.json 的 logRetentionDays)；0 以下用預設 30 天</param>
  public static void Initialize(int retention)
  {
   if (retention > 0)
   {
    retentionDays = retention;
   }

   CleanOldLogs();
   ScheduleNextMidnightClean();
  }

  /// <summary>
  /// 排程下一次清除時間：每天本機午夜 00:00:05 觸發，觸發後自動排下一天
  /// </summary>
  private static void ScheduleNextMidnightClean()
  {
   DateTime now = DateTime.Now;
   DateTime nextMidnight = now.Date.AddDays(1).AddSeconds(5);
   TimeSpan timeToMidnight = nextMidnight - now;

   if (dailyCleanTimer == null)
   {
    dailyCleanTimer = new Timer(_ =>
    {
     CleanOldLogs();
     ScheduleNextMidnightClean();
    }, null, timeToMidnight, TimeSpan.FromMilliseconds(-1));
   }
   else
   {
    dailyCleanTimer.Change(timeToMidnight, TimeSpan.FromMilliseconds(-1));
   }
  }

  /// <summary>
  /// 清除過期 Log：刪除超過保留天數的檔案，並移除清空的 yyyyMM 資料夾
  /// </summary>
  public static void CleanOldLogs()
  {
   if (!isEnabled || retentionDays <= 0) return;

   ThreadPool.QueueUserWorkItem(_ =>
   {
    try
    {
     string rootDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, baseDir);
     if (!Directory.Exists(rootDir)) return;

     string[] yearMonthDirs = Directory.GetDirectories(rootDir);
     DateTime cutoffDate = DateTime.Now.Date.AddDays(-retentionDays);

     foreach (string ymDir in yearMonthDirs)
     {
      DirectoryInfo ymDirInfo = new DirectoryInfo(ymDir);
      FileInfo[] logFiles = ymDirInfo.GetFiles("*.txt");
      bool hasValidFile = false;

      foreach (FileInfo file in logFiles)
      {
       string fileName = file.Name;
       // 檢查是否為月度例外日誌 (格式：yyyyMM_exceptionlog.txt)
       if (fileName.EndsWith("_exceptionlog.txt", StringComparison.OrdinalIgnoreCase) && fileName.Length >= 6)
       {
        if (DateTime.TryParseExact(fileName.Substring(0, 6), "yyyyMM", null, System.Globalization.DateTimeStyles.None, out DateTime fileMonthDate))
        {
         // 以該月份最後一天作為過期判定基準
         if (fileMonthDate.AddMonths(1).AddDays(-1) < cutoffDate)
         {
          try
          {
           file.Delete();
          }
          catch (Exception ex)
          {
           WriteExceptionLog($"[DeleteFileError] 刪除檔案失敗: {ex.Message} | File: {file.FullName}");
          }
         }
         else
         {
          hasValidFile = true;
         }
        }
       }
       // 一般日誌檢查 (格式：yyyyMMdd_*.txt)
       else if (fileName.Length >= 8 && DateTime.TryParseExact(fileName.Substring(0, 8), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out DateTime fileDate))
       {
        if (fileDate < cutoffDate)
        {
         try
         {
          file.Delete();
         }
         catch (Exception ex)
         {
          WriteExceptionLog($"[DeleteFileError] 刪除檔案失敗: {ex.Message} | File: {file.FullName}");
         }
        }
        else
        {
         hasValidFile = true;
        }
       }
      }
      // 如果該月份資料夾已經沒有任何有效檔案且空了，則刪除資料夾
      if (!hasValidFile && ymDirInfo.GetFiles().Length == 0 && ymDirInfo.GetDirectories().Length == 0)
      {
       try
       {
        ymDirInfo.Delete(true);
       }
       catch (Exception ex)
       {
        WriteExceptionLog($"[DeleteDirError] 刪除資料夾失敗: {ex.Message} | Dir: {ymDirInfo.FullName}");
       }
      }
     }
    }
    catch (Exception ex)
    {
     WriteExceptionLog($"[CleanRootError] 清理過期日誌發生例外: {ex.Message}");
    }
   });
  }

  /// <summary>
  /// 寫入系統 Log (檔名：yyyyMMdd_System.txt)
  /// </summary>
  /// <param name="message">要寫入的訊息內容</param>
  public static void WriteSystem(string message)
  {
   WriteLogInternal("System", message);
  }

  /// <summary>
  /// 寫入 PLC Log (檔名：yyyyMMdd_PLC{id}.txt，id 為 appsetting.json 的 plcs.id)
  /// </summary>
  /// <param name="plcId">PLC 編號 (appsetting.json 的 plcs.id)</param>
  /// <param name="message">要寫入的訊息內容</param>
  public static void Write(int plcId, string message)
  {
   WriteLogInternal("PLC" + plcId, message);
  }

  /// <summary>
  /// 寫入帶有完整 Hex 封包的 PLC Log
  /// </summary>
  /// <param name="plcId">PLC 編號 (appsetting.json 的 plcs.id)</param>
  /// <param name="message">要寫入的訊息內容</param>
  /// <param name="data">原始封包，會轉成 Hex 字串附加在訊息後面</param>
  public static void Write(int plcId, string message, byte[] data)
  {
   Write(plcId, message, data, DateTime.Now);
  }

  /// <summary>
  /// 寫入 PLC Log 並使用指定的時間戳 (例：補寫輪詢 M7700 詢問時，用原本的發送時間)
  /// </summary>
  /// <param name="plcId">PLC 編號 (appsetting.json 的 plcs.id)</param>
  /// <param name="message">要寫入的訊息內容</param>
  /// <param name="data">原始封包，會轉成 Hex 字串附加在訊息後面</param>
  /// <param name="timestamp">這筆 log 的時間戳 (事件發生時間，非寫檔時間)</param>
  public static void Write(int plcId, string message, byte[] data, DateTime timestamp)
  {
   string hexStr = "";
   if (data != null && data.Length > 0)
   {
    var sbHex = new StringBuilder();
    foreach (byte b in data)
    {
     sbHex.Append(b.ToString("X2")).Append(' ');
    }
    hexStr = " | Hex: " + sbHex.ToString().TrimEnd();
   }
   WriteLogInternal("PLC" + plcId, message + hexStr, timestamp);
  }

  /// <summary>
  /// 組出 log 行並丟入佇列 (使用現在時間)；fileKey = 檔名中日期底線後面的部分 (例：PLC1 / System)
  /// </summary>
  /// <param name="fileKey">檔名中日期底線後面的部分 (例：PLC1 / System)</param>
  /// <param name="content">要寫入的整行內容</param>
  private static void WriteLogInternal(string fileKey, string content)
  {
   WriteLogInternal(fileKey, content, DateTime.Now);
  }

  /// <summary>
  /// 組出 log 行並丟入佇列；timestamp 在「呼叫當下」就決定 (不是寫檔當下)，
  /// 時間才會跟事件發生順序一致；整行組好後丟進佇列，由單一寫入執行緒依序寫入
  /// </summary>
  /// <param name="fileKey">檔名中日期底線後面的部分 (例：PLC1 / System)</param>
  /// <param name="content">要寫入的整行內容</param>
  /// <param name="now">這筆 log 的時間戳</param>
  private static void WriteLogInternal(string fileKey, string content, DateTime now)
  {
   if (!isEnabled) return;

   string yearMonthFolder = now.ToString("yyyyMM");
   string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, baseDir, yearMonthFolder);
   string fileName = $"{now:yyyyMMdd}_{fileKey}.txt"; // 檔名格式：yyyyMMdd_PLC{id}.txt (例：20261028_PLC1.txt)；系統訊息為 yyyyMMdd_System.txt
   string filePath = Path.Combine(folder, fileName);
   string logLine = $"{now:yyyy-MM-dd HH:mm:ss.fff} | {content}\r\n";
   logQueue.Add(new LogEntry { FilePath = filePath, Line = logLine });
  }
 }
}