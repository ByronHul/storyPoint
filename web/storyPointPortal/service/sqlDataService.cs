/*
================================================================================
檔案名稱: sqlDataService.cs
用途說明: Azure 多人參與 Story Point 系統之資料存取服務。
          遵循規範：
          1. 不使用任何 OR Mapping 技術，全面採用 Microsoft.Data.SqlClient
          2. 商業邏輯均由 SQL Server 內之 storyPoint.sp* 預存程序完成
          3. 呼叫 SQL 時強制參數化，並確保連線字串包含明確 Application Name
          4. 內建自動重試 (Retry) 機制與雙軌 Log 支援
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
================================================================================
*/

using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace storyPointPortal.service;

/// <summary>
/// 封裝 SQL Server Stored Procedure 參數化呼叫、自動重試與雙軌 (DB + CSV 檔案/記憶體) Log 機制之核心資料服務
/// </summary>
public class sqlDataService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<sqlDataService> _logger;
    private static readonly ConcurrentQueue<Dictionary<string, object?>> _recentFallbackLogs = new();
    private static int _fallbackLogSeq = 900000;

    /// <summary>
    /// 伺服器啟動階段授權識別碼（確保每次服務重啟或重新部署時，舊瀏覽器殘留之 Cookie 自動失效，強制使用者重新透過 /login 帳號登入頁驗證）
    /// </summary>
    public static readonly string serverBootAuthToken = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 員工進入獨立盲投 Session 網頁之即時佔用鎖定紀錄（允許同一人同時以多個獨立網頁參與不同 Session，但嚴格禁止重複進入同一個 Session）
    /// </summary>
    public sealed class votingSessionPresenceLease
    {
        public int employeePk { get; set; }
        public string employeeName { get; set; } = string.Empty;
        public int votingSessionPk { get; set; }
        public string tabInstanceId { get; set; } = string.Empty;
        public DateTime enteredAtLocal { get; set; }
        public DateTime lastHeartbeatUtc { get; set; }
    }

    private static readonly ConcurrentDictionary<string, votingSessionPresenceLease> _activeVotingSessionLeases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _leaseSyncLock = new();
    private const double _leaseExpirationSeconds = 8.0;

    /// <summary>
    /// 嘗試進入或續約特定員工對特定盲投議程之獨立網頁佔用鎖定。
    /// 若同一位員工已在另一個活躍中的獨立網頁進入同一個 votingSessionPk，則拒絕重複進入；
    /// 若為不同 votingSessionPk 則互不衝突，可同時開啟多個獨立網頁參與不同盲投議程。
    /// </summary>
    public static (bool isAllowed, string assignedTabInstanceId, votingSessionPresenceLease? conflictLease) tryEnterOrRefreshVotingSessionLease(
        int employeePk,
        string employeeName,
        int votingSessionPk,
        string? requestedTabInstanceId,
        string? reloadTokenTabInstanceId = null)
    {
        if (employeePk <= 0 || votingSessionPk <= 0)
        {
            return (false, string.Empty, null);
        }

        string key = $"{employeePk}_{votingSessionPk}";
        DateTime nowUtc = DateTime.UtcNow;

        lock (_leaseSyncLock)
        {
            if (_activeVotingSessionLeases.TryGetValue(key, out var existing))
            {
                if ((nowUtc - existing.lastHeartbeatUtc).TotalSeconds > _leaseExpirationSeconds)
                {
                    _activeVotingSessionLeases.TryRemove(key, out _);
                    existing = null;
                }
            }

            string cleanReqId = (requestedTabInstanceId ?? string.Empty).Trim();
            string cleanReloadId = (reloadTokenTabInstanceId ?? string.Empty).Trim();

            if (existing != null)
            {
                bool isSameTabInstance = (!string.IsNullOrEmpty(cleanReqId) &&
                                          string.Equals(existing.tabInstanceId, cleanReqId, StringComparison.OrdinalIgnoreCase))
                                         || (!string.IsNullOrEmpty(cleanReloadId) &&
                                             string.Equals(existing.tabInstanceId, cleanReloadId, StringComparison.OrdinalIgnoreCase));

                if (isSameTabInstance)
                {
                    existing.lastHeartbeatUtc = nowUtc;
                    if (!string.IsNullOrWhiteSpace(employeeName))
                    {
                        existing.employeeName = employeeName;
                    }
                    return (true, existing.tabInstanceId, null);
                }

                // 同一位員工已在另一個活躍中的獨立網頁進入此 Session，禁止重複進入
                return (false, string.Empty, existing);
            }

            string assignedId = !string.IsNullOrEmpty(cleanReqId)
                ? cleanReqId
                : ("spTab_" + Guid.NewGuid().ToString("N")[..12]);

            var newLease = new votingSessionPresenceLease
            {
                employeePk = employeePk,
                employeeName = employeeName ?? string.Empty,
                votingSessionPk = votingSessionPk,
                tabInstanceId = assignedId,
                enteredAtLocal = DateTime.Now,
                lastHeartbeatUtc = nowUtc
            };
            _activeVotingSessionLeases[key] = newLease;
            return (true, assignedId, null);
        }
    }

    /// <summary>
    /// 獨立盲投 Session 網頁定時發送心跳續約鎖定。
    /// 回傳 (isStillOwner, ownerTabInstanceId)，若為 false 代表該分頁並非合法持有者。
    /// </summary>
    public static (bool isOwner, string currentOwnerTabInstanceId) refreshVotingSessionHeartbeat(
        int employeePk,
        string employeeName,
        int votingSessionPk,
        string? tabInstanceId)
    {
        if (employeePk <= 0 || votingSessionPk <= 0 || string.IsNullOrWhiteSpace(tabInstanceId))
        {
            return (false, string.Empty);
        }

        string key = $"{employeePk}_{votingSessionPk}";
        string cleanId = tabInstanceId.Trim();
        DateTime nowUtc = DateTime.UtcNow;

        lock (_leaseSyncLock)
        {
            if (_activeVotingSessionLeases.TryGetValue(key, out var existing))
            {
                if ((nowUtc - existing.lastHeartbeatUtc).TotalSeconds > _leaseExpirationSeconds)
                {
                    existing.tabInstanceId = cleanId;
                    existing.employeeName = employeeName ?? existing.employeeName;
                    existing.lastHeartbeatUtc = nowUtc;
                    return (true, cleanId);
                }

                if (string.Equals(existing.tabInstanceId, cleanId, StringComparison.OrdinalIgnoreCase))
                {
                    existing.lastHeartbeatUtc = nowUtc;
                    return (true, existing.tabInstanceId);
                }

                return (false, existing.tabInstanceId);
            }

            var recreated = new votingSessionPresenceLease
            {
                employeePk = employeePk,
                employeeName = employeeName ?? string.Empty,
                votingSessionPk = votingSessionPk,
                tabInstanceId = cleanId,
                enteredAtLocal = DateTime.Now,
                lastHeartbeatUtc = nowUtc
            };
            _activeVotingSessionLeases[key] = recreated;
            return (true, cleanId);
        }
    }

    /// <summary>
    /// 當使用者關閉或離開獨立盲投 Session 網頁時，立即釋放該 Session 之佔用鎖定
    /// </summary>
    public static bool releaseVotingSessionLease(int employeePk, int votingSessionPk, string? tabInstanceId)
    {
        if (employeePk <= 0 || votingSessionPk <= 0)
        {
            return false;
        }

        string key = $"{employeePk}_{votingSessionPk}";
        string cleanId = (tabInstanceId ?? string.Empty).Trim();

        lock (_leaseSyncLock)
        {
            if (_activeVotingSessionLeases.TryGetValue(key, out var existing))
            {
                if (string.IsNullOrEmpty(cleanId) ||
                    string.Equals(existing.tabInstanceId, cleanId, StringComparison.OrdinalIgnoreCase))
                {
                    _activeVotingSessionLeases.TryRemove(key, out _);
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// 取得指定員工目前正在獨立網頁中開啟進行的所有盲投 Session 主鍵清單
    /// </summary>
    public static List<int> getActiveLockedSessionPksForEmployee(int employeePk)
    {
        var result = new List<int>();
        if (employeePk <= 0)
        {
            return result;
        }

        DateTime nowUtc = DateTime.UtcNow;
        string prefix = $"{employeePk}_";

        lock (_leaseSyncLock)
        {
            foreach (var kv in _activeVotingSessionLeases)
            {
                if (!kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if ((nowUtc - kv.Value.lastHeartbeatUtc).TotalSeconds <= _leaseExpirationSeconds)
                {
                    result.Add(kv.Value.votingSessionPk);
                }
                else
                {
                    _activeVotingSessionLeases.TryRemove(kv.Key, out _);
                }
            }
        }
        result.Sort();
        return result;
    }

    public sqlDataService(IConfiguration configuration, ILogger<sqlDataService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// 取得具備明確 Application Name 之 SQL Server 連線字串
    /// </summary>
    public string getConnectionString()
    {
        string? connStr = _configuration["databaseSetting:connectionString"];
        bool isDefaultLocalhost = !string.IsNullOrWhiteSpace(connStr) &&
                                  connStr.Contains("Server=localhost;", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(connStr) || isDefaultLocalhost)
        {
            // 嘗試從專案根目錄之 config.json 讀取 (優先解密 encryptedConnectionString，再回退 plainConnectionStringForDev)
            string[] candidatePaths =
            {
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config.json")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "config.json")),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "config.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "config.json")
            };

            foreach (string rootConfigPath in candidatePaths)
            {
                if (!File.Exists(rootConfigPath)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(rootConfigPath, Encoding.UTF8));
                    if (doc.RootElement.TryGetProperty("databaseSetting", out var dbNode))
                    {
                        if (OperatingSystem.IsWindows() &&
                            dbNode.TryGetProperty("encryptedConnectionString", out var encNode))
                        {
                            string? encHex = encNode.GetString();
                            if (!string.IsNullOrWhiteSpace(encHex))
                            {
                                try
                                {
                                    byte[] encBytes = Convert.FromHexString(encHex.Trim());
                                    byte[] decBytes = ProtectedData.Unprotect(encBytes, null, DataProtectionScope.CurrentUser);
                                    string decrypted = Encoding.Unicode.GetString(decBytes);
                                    if (!string.IsNullOrWhiteSpace(decrypted))
                                    {
                                        connStr = decrypted;
                                        break;
                                    }
                                }
                                catch
                                {
                                    // 若非同帳號加密則略過並回退
                                }
                            }
                        }

                        if (string.IsNullOrWhiteSpace(connStr) &&
                            dbNode.TryGetProperty("plainConnectionStringForDev", out var devConn))
                        {
                            connStr = devConn.GetString();
                        }
                    }
                    break;
                }
                catch
                {
                    // 忽略解析例外
                }
            }
        }

        if (string.IsNullOrWhiteSpace(connStr))
        {
            connStr = "Server=localhost;Database=storyPointDb;Integrated Security=True;Application Name=azureStoryPointSystem;TrustServerCertificate=True;";
        }

        if (!connStr.Contains("Application Name", StringComparison.OrdinalIgnoreCase))
        {
            connStr = connStr.TrimEnd(';') + ";Application Name=azureStoryPointSystem;";
        }
        return connStr;
    }

    /// <summary>
    /// 計算字串之 SHA-256 小寫 Hex 雜湊值
    /// </summary>
    public static string computeSha256(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return string.Empty;
        }
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plainText));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 具備自動重試機制之 Stored Procedure 呼叫，回傳 DataSet
    /// </summary>
    public async Task<DataSet> executeStoredProcedureDataSetAsync(string procedureName, Dictionary<string, object?>? parameters = null)
    {
        int maxRetry = 3;
        int delayMs = 1200;
        int attempt = 0;
        var sw = Stopwatch.StartNew();

        while (true)
        {
            attempt++;
            try
            {
                string connStr = getConnectionString();
                await using var conn = new SqlConnection(connStr);
                await conn.OpenAsync();

                await using var cmd = conn.CreateCommand();
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandText = procedureName;
                cmd.CommandTimeout = 60;

                if (parameters != null)
                {
                    foreach (var kvp in parameters)
                    {
                        string pName = kvp.Key.StartsWith('@') ? kvp.Key : "@" + kvp.Key;
                        cmd.Parameters.AddWithValue(pName, kvp.Value ?? DBNull.Value);
                    }
                }

                using var adapter = new SqlDataAdapter(cmd);
                var ds = new DataSet();
                adapter.Fill(ds);
                return ds;
            }
            catch (Exception ex)
            {
                if (attempt <= maxRetry)
                {
                    _logger.LogWarning(ex, "執行預存程序 {Proc} 第 {Attempt} 次失敗，準備重試...", procedureName, attempt);
                    await Task.Delay(delayMs);
                }
                else
                {
                    sw.Stop();
                    _logger.LogError(ex, "執行預存程序 {Proc} 重試後仍失敗。", procedureName);
                    if (!string.Equals(procedureName, "storyPoint.spWriteLog", StringComparison.OrdinalIgnoreCase))
                    {
                        int errCode = (ex is SqlException sqlEx) ? sqlEx.Number : ex.HResult;
                        int errLine = (ex is SqlException sqlEx2 && sqlEx2.LineNumber > 0)
                            ? sqlEx2.LineNumber
                            : extractErrorLineFromStackTrace(ex);
                        string opAcc = parameters != null && parameters.TryGetValue("operatorAccount", out var opObj) && opObj != null
                            ? opObj.ToString()!
                            : "system";
                        await writeSystemLogAsync(
                            logLevel: 1,
                            sourceName: "webPortal.sql",
                            objectName: procedureName,
                            durationMillisecond: (int)sw.ElapsedMilliseconds,
                            executionResult: "呼叫 Stored Procedure 發生例外錯誤",
                            errorCode: errCode,
                            errorLine: errLine,
                            errorMessage: ex.Message,
                            operatorAccount: opAcc
                        );
                    }
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// 從 Exception StackTrace 擷取發生錯誤的程式行號
    /// </summary>
    public static int extractErrorLineFromStackTrace(Exception? ex)
    {
        if (ex == null || string.IsNullOrEmpty(ex.StackTrace)) return 0;
        var match = Regex.Match(ex.StackTrace, @":line\s+(\d+)", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int lineNum))
        {
            return lineNum;
        }
        return 0;
    }

    /// <summary>
    /// 雙軌寫入系統與錯誤 Log（1. 寫入本機 logs 目錄 CSV 檔案與記憶體緩衝；2. 寫入資料庫 storyPoint.tbLog）
    /// 保證不會因寫入 Log 失敗而拋出例外導致系統當機
    /// </summary>
    public async Task writeSystemLogAsync(
        byte logLevel,
        string sourceName,
        string objectName,
        int durationMillisecond,
        string executionResult,
        int? errorCode = null,
        int? errorLine = null,
        string? errorMessage = null,
        string operatorAccount = "system")
    {
        DateTime now = DateTime.Now;
        string levelText = logLevel switch
        {
            1 => "1-錯誤 (Error)",
            2 => "2-警告 (Warning)",
            3 => "3-資訊 (Info)",
            4 => "4-除錯 (Debug)",
            _ => "0-未分類"
        };

        // 1. 先寫入記憶體環狀佇列與本機 CSV Log 檔案（確保即使 DB 斷線也能查到錯誤 Log）
        try
        {
            int seq = Interlocked.Increment(ref _fallbackLogSeq);
            _recentFallbackLogs.Enqueue(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["logPk"] = seq,
                ["logTime"] = now,
                ["logLevel"] = (int)logLevel,
                ["logLevelText"] = levelText,
                ["sourceName"] = sourceName ?? "webPortal",
                ["objectName"] = objectName ?? "unknown",
                ["durationMillisecond"] = durationMillisecond,
                ["executionResult"] = executionResult ?? "",
                ["errorCode"] = errorCode,
                ["errorLine"] = errorLine,
                ["errorMessage"] = errorMessage ?? "",
                ["operatorAccount"] = operatorAccount ?? "system"
            });
            while (_recentFallbackLogs.Count > 200)
            {
                _recentFallbackLogs.TryDequeue(out _);
            }

            string logDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            string csvFile = Path.Combine(logDir, $"webPortal_{now:yyyyMMdd}.csv");
            bool isNew = !File.Exists(csvFile);
            string cleanMsg = (errorMessage ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\"", "\"\"");
            string cleanRes = (executionResult ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\"", "\"\"");
            var sb = new StringBuilder();
            if (isNew)
            {
                sb.AppendLine("logTime,logLevel,sourceName,objectName,durationMillisecond,executionResult,errorCode,errorLine,errorMessage,operatorAccount");
            }
            sb.AppendLine($"{now:yyyy-MM-dd HH:mm:ss.fff},{logLevel},\"{sourceName}\",\"{objectName}\",{durationMillisecond},\"{cleanRes}\",{errorCode},{errorLine},\"{cleanMsg}\",\"{operatorAccount}\"");
            await File.AppendAllTextAsync(csvFile, sb.ToString(), new UTF8Encoding(true));
        }
        catch
        {
            // 忽略檔案 IO 例外，確保絕不影響主流程
        }

        // 2. 寫入 SQL Server storyPoint.tbLog (呼叫 storyPoint.spWriteLog)
        try
        {
            string connStr = getConnectionString();
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.CommandText = "storyPoint.spWriteLog";
            cmd.CommandTimeout = 15;
            cmd.Parameters.AddWithValue("@logLevel", logLevel);
            cmd.Parameters.AddWithValue("@sourceName", sourceName ?? "webPortal");
            cmd.Parameters.AddWithValue("@objectName", objectName ?? "unknown");
            cmd.Parameters.AddWithValue("@durationMillisecond", durationMillisecond);
            cmd.Parameters.AddWithValue("@executionResult", executionResult ?? "完成");
            cmd.Parameters.AddWithValue("@errorCode", (object?)errorCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@errorLine", (object?)errorLine ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@errorMessage", (object?)errorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@operatorAccount", operatorAccount ?? "system");
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception dbEx)
        {
            _logger.LogWarning(dbEx, "寫入資料庫 storyPoint.tbLog 失敗，已保存於本機與記憶體 Log。");
        }
    }

    /// <summary>
    /// 取得記憶體備援 Log DataTable（當資料庫查詢失敗時仍可讓管理者於介面檢視最新錯誤 Log）
    /// </summary>
    public DataTable getFallbackLogTable()
    {
        var dt = new DataTable();
        dt.Columns.Add("logPk", typeof(int));
        dt.Columns.Add("logTime", typeof(DateTime));
        dt.Columns.Add("logLevel", typeof(int));
        dt.Columns.Add("logLevelText", typeof(string));
        dt.Columns.Add("sourceName", typeof(string));
        dt.Columns.Add("objectName", typeof(string));
        dt.Columns.Add("durationMillisecond", typeof(int));
        dt.Columns.Add("executionResult", typeof(string));
        dt.Columns.Add("errorCode", typeof(object));
        dt.Columns.Add("errorLine", typeof(object));
        dt.Columns.Add("errorMessage", typeof(string));
        dt.Columns.Add("operatorAccount", typeof(string));

        foreach (var item in _recentFallbackLogs.Reverse())
        {
            var r = dt.NewRow();
            foreach (DataColumn col in dt.Columns)
            {
                if (item.TryGetValue(col.ColumnName, out var val) && val != null)
                {
                    r[col.ColumnName] = val;
                }
                else
                {
                    r[col.ColumnName] = DBNull.Value;
                }
            }
            dt.Rows.Add(r);
        }
        return dt;
    }

    /// <summary>
    /// 將 DataTable 轉換為 Dictionary 清單供 JSON 序列化或頁面渲染使用
    /// </summary>
    public static List<Dictionary<string, object?>> convertTableToList(DataTable? table)
    {
        var list = new List<Dictionary<string, object?>>();
        if (table == null)
        {
            return list;
        }
        foreach (DataRow row in table.Rows)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (DataColumn col in table.Columns)
            {
                object val = row[col];
                dict[col.ColumnName] = val == DBNull.Value ? null : val;
            }
            list.Add(dict);
        }
        return list;
    }
}
