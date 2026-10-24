/*
================================================================================
檔案名稱: emailNotificationService.cs
用途說明: 工項達到迄時自動轉換狀態、扣除 Story Point 並透過 Email 寄信通知部門管理層與 HR 之服務。
          包含：
          1. emailNotificationService : 整合 Azure Communication Services (ACS) Email REST API (HMAC-SHA256)
             寄發繁體中文通知信，並呼叫 storyPoint.spProcessExpiredWorkItem 與 storyPoint.spMarkWorkItemExpiryNotified
          2. workItemExpiryBackgroundService : 背景定時排程服務 (IHostedService)，自動偵測達到迄時之工項並執行轉態、扣點與寄信
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
================================================================================
*/

using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace storyPointPortal.service;

/// <summary>
/// 到期工項處理與 Email 寄信通知結果摘要模型
/// </summary>
public sealed class expiredWorkItemProcessResult
{
    public int processedCount { get; set; }
    public int emailSentCount { get; set; }
    public List<string> itemSummaryList { get; set; } = new();
    public List<string> recipientSummaryList { get; set; } = new();
}

/// <summary>
/// 負責發送部門管理層與 HR 通知信、以及執行到期工項轉態與扣點之核心服務
/// </summary>
public class emailNotificationService
{
    private readonly IConfiguration _configuration;
    private readonly sqlDataService _sqlService;
    private readonly ILogger<emailNotificationService> _logger;
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly SemaphoreSlim _processLock = new(1, 1);

    public emailNotificationService(
        IConfiguration configuration,
        sqlDataService sqlService,
        ILogger<emailNotificationService> logger)
    {
        _configuration = configuration;
        _sqlService = sqlService;
        _logger = logger;
    }

    /// <summary>
    /// 取得台灣時間 (UTC+8)
    /// </summary>
    public static DateTime getTaiwanNow()
    {
        return DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).DateTime;
    }

    /// <summary>
    /// 讀取郵件設定 (優先讀取環境變數/AppSettings，其次讀取根目錄 config.json 並支援 Windows DPAPI 解密)
    /// </summary>
    private (string acsConnectionString, string senderEmail, string senderDisplayName) resolveMailSetting()
    {
        string acsConn = (_configuration["mailProviderSetting:acsConnectionString"] ?? string.Empty).Trim();
        string encAcsHex = (_configuration["mailProviderSetting:encryptedAcsConnectionString"] ?? string.Empty).Trim();
        string senderEmail = (_configuration["mailProviderSetting:defaultSenderEmail"] ?? string.Empty).Trim();
        string senderName = (_configuration["mailProviderSetting:defaultSenderDisplayName"] ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(acsConn) && !string.IsNullOrWhiteSpace(encAcsHex) && OperatingSystem.IsWindows())
        {
            acsConn = tryDecryptDpapiHex(encAcsHex);
        }

        if (string.IsNullOrWhiteSpace(acsConn) || string.IsNullOrWhiteSpace(senderEmail))
        {
            string[] candidatePaths =
            {
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config.json")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "config.json")),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "config.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "config.json")
            };

            foreach (string path in candidatePaths)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                    if (doc.RootElement.TryGetProperty("mailProviderSetting", out var mailNode))
                    {
                        if (string.IsNullOrWhiteSpace(senderEmail) &&
                            mailNode.TryGetProperty("defaultSenderEmail", out var seNode))
                        {
                            senderEmail = (seNode.GetString() ?? string.Empty).Trim();
                        }

                        if (string.IsNullOrWhiteSpace(senderName) &&
                            mailNode.TryGetProperty("defaultSenderDisplayName", out var snNode))
                        {
                            senderName = (snNode.GetString() ?? string.Empty).Trim();
                        }

                        if (string.IsNullOrWhiteSpace(acsConn) &&
                            mailNode.TryGetProperty("acsConnectionString", out var plainNode))
                        {
                            acsConn = (plainNode.GetString() ?? string.Empty).Trim();
                        }

                        if (string.IsNullOrWhiteSpace(acsConn) &&
                            OperatingSystem.IsWindows() &&
                            mailNode.TryGetProperty("encryptedAcsConnectionString", out var encNode))
                        {
                            string hex = (encNode.GetString() ?? string.Empty).Trim();
                            if (!string.IsNullOrWhiteSpace(hex))
                            {
                                acsConn = tryDecryptDpapiHex(hex);
                            }
                        }
                    }
                    break;
                }
                catch
                {
                    // 忽略解析錯誤
                }
            }
        }

        if (string.IsNullOrWhiteSpace(senderEmail))
        {
            senderEmail = "DoNotReply@2116ef18-0b69-4917-9e2b-b1adaa14af1b.azurecomm.net";
        }
        if (string.IsNullOrWhiteSpace(senderName))
        {
            senderName = "Story Point 工項與權重管理系統";
        }

        return (acsConn, senderEmail, senderName);
    }

    private static string tryDecryptDpapiHex(string hexString)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(hexString))
        {
            return string.Empty;
        }
        try
        {
            byte[] encryptedBytes = Convert.FromHexString(hexString.Trim());
            byte[] plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.Unicode.GetString(plainBytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 解析 ACS 連線字串 (endpoint=...;accesskey=...)
    /// </summary>
    private static (string endpoint, string accessKey) parseAcsConnectionString(string connectionString)
    {
        string endpoint = string.Empty;
        string accessKey = string.Empty;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return (endpoint, accessKey);
        }

        foreach (string segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int idx = segment.IndexOf('=');
            if (idx <= 0) continue;
            string key = segment[..idx].Trim();
            string val = segment[(idx + 1)..].Trim();
            if (key.Equals("endpoint", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = val.TrimEnd('/');
            }
            else if (key.Equals("accesskey", StringComparison.OrdinalIgnoreCase))
            {
                accessKey = val;
            }
        }
        return (endpoint, accessKey);
    }

    /// <summary>
    /// 透過 Azure Communication Services (ACS) Email REST API 寄送通知信（具備自動 Retry 機制）
    /// </summary>
    public async Task<(bool isSuccess, string statusMessage)> sendEmailAsync(
        List<(string email, string displayName)> recipients,
        string subject,
        string htmlBody,
        string plainTextBody,
        string operatorAccount = "system")
    {
        var cleanRecipients = recipients
            .Where(r => !string.IsNullOrWhiteSpace(r.email) && r.email.Contains('@'))
            .GroupBy(r => r.email.Trim().ToLowerInvariant())
            .Select(g => g.First())
            .ToList();

        if (cleanRecipients.Count == 0)
        {
            return (false, "無有效收件人 Email 地址");
        }

        var (acsConn, senderEmail, _) = resolveMailSetting();
        var (endpoint, accessKey) = parseAcsConnectionString(acsConn);

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(accessKey))
        {
            string fallbackMsg = $"郵件服務未配置 ACS 金鑰，已記錄模擬寄信通知至系統 Log (收件人: {string.Join(", ", cleanRecipients.Select(r => r.email))})";
            await _sqlService.writeSystemLogAsync(
                logLevel: 2,
                sourceName: "emailService",
                objectName: "sendEmailAsync",
                durationMillisecond: 0,
                executionResult: fallbackMsg,
                operatorAccount: operatorAccount);
            return (true, fallbackMsg);
        }

        string pathAndQuery = "/emails:send?api-version=2023-03-31";
        string requestUriString = endpoint + pathAndQuery;
        var requestUri = new Uri(requestUriString);

        var payloadObj = new
        {
            senderAddress = senderEmail,
            content = new
            {
                subject = subject,
                plainText = plainTextBody,
                html = htmlBody
            },
            recipients = new
            {
                to = cleanRecipients.Select(r => new
                {
                    address = r.email.Trim(),
                    displayName = string.IsNullOrWhiteSpace(r.displayName) ? r.email.Trim() : r.displayName.Trim()
                }).ToArray()
            }
        };

        string jsonPayload = JsonSerializer.Serialize(payloadObj);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonPayload);
        string contentHash = Convert.ToBase64String(SHA256.HashData(bodyBytes));
        byte[] keyBytes = Convert.FromBase64String(accessKey);

        int maxRetry = 3;
        string lastError = string.Empty;

        for (int attempt = 1; attempt <= maxRetry; attempt++)
        {
            try
            {
                string rfc1123Date = DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture);
                string hostAuthority = requestUri.Authority;
                string stringToSign = $"POST\n{pathAndQuery}\n{rfc1123Date};{hostAuthority};{contentHash}";

                using var hmac = new HMACSHA256(keyBytes);
                string signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
                string authHeader = $"HMAC-SHA256 SignedHeaders=x-ms-date;host;x-ms-content-sha256&Signature={signature}";

                using var req = new HttpRequestMessage(HttpMethod.Post, requestUri);
                req.Headers.TryAddWithoutValidation("x-ms-date", rfc1123Date);
                req.Headers.TryAddWithoutValidation("x-ms-content-sha256", contentHash);
                req.Headers.TryAddWithoutValidation("Authorization", authHeader);
                req.Content = new ByteArrayContent(bodyBytes);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

                using var resp = await _httpClient.SendAsync(req);
                string respBody = await resp.Content.ReadAsStringAsync();

                if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.Accepted)
                {
                    return (true, $"ACS Email 寄送成功 (HTTP {(int)resp.StatusCode})");
                }

                lastError = $"HTTP {(int)resp.StatusCode}: {respBody}";
                _logger.LogWarning("ACS Email 寄送第 {Attempt} 次失敗: {Err}", attempt, lastError);
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                _logger.LogWarning(ex, "ACS Email 寄送第 {Attempt} 次發生例外", attempt);
            }

            if (attempt < maxRetry)
            {
                await Task.Delay(1200);
            }
        }

        return (false, lastError);
    }

    /// <summary>
    /// 檢核所有達到迄時 (endDate &lt;= 台灣現在時間) 之工項，自動轉換狀態為「已完成」、扣掉 Story Point，並寄發 Email 給部門管理層與 HR
    /// </summary>
    public async Task<expiredWorkItemProcessResult> processExpiredWorkItemsAndNotifyAsync(
        int? workItemPk = null,
        string operatorAccount = "system")
    {
        var summary = new expiredWorkItemProcessResult();
        if (!await _processLock.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            return summary;
        }

        try
        {
            var ds = await _sqlService.executeStoredProcedureDataSetAsync(
                "storyPoint.spProcessExpiredWorkItem",
                new Dictionary<string, object?>
                {
                    ["workItemPk"] = workItemPk,
                    ["operatorAccount"] = operatorAccount
                });

            if (ds.Tables.Count == 0 || ds.Tables[0].Rows.Count == 0)
            {
                return summary;
            }

            DataTable dt = ds.Tables[0];
            summary.processedCount = dt.Rows.Count;

            foreach (DataRow row in dt.Rows)
            {
                int wiPk = Convert.ToInt32(row["workItemPk"]);
                string itemCode = Convert.ToString(row["itemCode"]) ?? "";
                string itemTitle = Convert.ToString(row["itemTitle"]) ?? "";
                string workItemKindText = Convert.ToString(row["workItemKindText"]) ?? "專案";
                string typeName = Convert.ToString(row["typeName"]) ?? "未分類";
                string oldStatus = Convert.ToString(row["oldExecutionStatus"]) ?? "inProgress";
                string oldStatusText = oldStatus switch
                {
                    "todo" => "待處理",
                    "inProgress" => "進行中",
                    "done" => "已完成",
                    _ => oldStatus
                };
                decimal originalPt = row["originalStoryPoint"] == DBNull.Value ? 0m : Convert.ToDecimal(row["originalStoryPoint"]);
                decimal deductedPt = row["deductedStoryPoint"] == DBNull.Value ? 0m : Convert.ToDecimal(row["deductedStoryPoint"]);
                decimal remainingPt = row["remainingStoryPoint"] == DBNull.Value ? 0m : Convert.ToDecimal(row["remainingStoryPoint"]);

                string startDateStr = row["startDate"] == DBNull.Value
                    ? "未設定"
                    : Convert.ToDateTime(row["startDate"]).ToString("yyyy-MM-dd HH:mm");
                string endDateStr = row["endDate"] == DBNull.Value
                    ? "未設定"
                    : Convert.ToDateTime(row["endDate"]).ToString("yyyy-MM-dd HH:mm");

                string assigneeSummary = Convert.ToString(row["assigneeSummary"]) ?? "尚未指派";
                string departmentSummary = Convert.ToString(row["departmentSummary"]) ?? "-";
                string mgrEmailCsv = Convert.ToString(row["departmentManagerEmailCsv"]) ?? "";
                string mgrNameCsv = Convert.ToString(row["departmentManagerNameCsv"]) ?? "該同仁的部門主管";
                string hrEmailCsv = Convert.ToString(row["hrEmailCsv"]) ?? "";
                string hrNameCsv = Convert.ToString(row["hrNameCsv"]) ?? "人資主管";
                string allEmailCsv = Convert.ToString(row["allRecipientEmailCsv"]) ?? "";

                var recipientList = new List<(string email, string displayName)>();
                foreach (string rawMail in allEmailCsv.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string cleanMail = rawMail.Trim();
                    if (!string.IsNullOrWhiteSpace(cleanMail) && cleanMail.Contains('@'))
                    {
                        recipientList.Add((cleanMail, cleanMail));
                    }
                }

                string subject = $"【工項到期自動轉態與扣點通知】[{workItemKindText}] {itemCode} {itemTitle} 已達迄時並扣除 {deductedPt:0.##} Story Point";

                string htmlBody = $@"
<!DOCTYPE html>
<html lang=""zh-Hant"">
<head><meta charset=""utf-8""></head>
<body style=""font-family:'Microsoft JhengHei',sans-serif;color:#1e293b;line-height:1.6;background:#f8fafc;padding:18px;"">
  <div style=""max-width:680px;margin:0 auto;background:#ffffff;border:1px solid #cbd5e1;border-radius:10px;overflow:hidden;"">
    <div style=""background:#1e3a8a;color:#ffffff;padding:16px 22px;"">
      <h2 style=""margin:0;font-size:18px;"">⏰ Story Point 工項達到迄時自動轉態與扣點通知</h2>
      <div style=""font-size:13px;color:#bfdbfe;margin-top:4px;"">通知對象：該同仁的部門主管 ({WebUtility.HtmlEncode(mgrNameCsv)}) 、 人資主管 ({WebUtility.HtmlEncode(hrNameCsv)})</div>
    </div>
    <div style=""padding:20px 22px;"">
      <p style=""margin-top:0;"">部門主管與人資主管 您好，以下工項已達到設定之<strong>迄時 (結束時間)</strong>，系統已自動轉換該工項狀態為「<strong>已完成 (done)</strong>」，並自動扣掉該工項之 Story Point：</p>
      <table style=""width:100%;border-collapse:collapse;font-size:14px;margin:14px 0;"">
        <tr>
          <td style=""padding:8px 12px;border:1px solid #e2e8f0;background:#f1f5f9;font-weight:bold;width:150px;"">工項代碼 / 標題</td>
          <td style=""padding:8px 12px;border:1px solid #e2e8f0;""><strong>{WebUtility.HtmlEncode(itemCode)}</strong> - {WebUtility.HtmlEncode(itemTitle)}</td>
        </tr>
        <tr>
          <td style=""padding:8px 12px;border:1px solid #e2e8f0;background:#f1f5f9;font-weight:bold;"">工項類型 / 代表類型</td>
          <td style=""padding:8px 12px;border:1px solid #e2e8f0;""><span style=""background:#dbeafe;color:#1e40af;padding:2px 8px;border-radius:4px;font-weight:bold;"">{WebUtility.HtmlEncode(workItemKindText)}</span>（代表類型：{WebUtility.HtmlEncode(typeName)}）</td>
        </tr>
        <tr>
          <td style=""padding:8px 12px;border:1px solid #e2e8f0;background:#f1f5f9;font-weight:bold;"">起訖時間 (起時 ~ 迄時)</td>
          <td style=""padding:8px 12px;border:1px solid #e2e8f0;"">{WebUtility.HtmlEncode(startDateStr)} ～ <strong style=""color:#b91c1c;"">{WebUtility.HtmlEncode(endDateStr)}</strong></td>
        </tr>
        <tr>
          <td style=""padding:8px 12px;border:1px solid #e2e8f0;background:#f1f5f9;font-weight:bold;"">工項狀態自動轉換</td>
          <td style=""padding:8px 12px;border:1px solid #e2e8f0;"">{WebUtility.HtmlEncode(oldStatusText)} ➜ <strong style=""color:#15803d;"">已完成 (done)</strong></td>
        </tr>
        <tr>
          <td style=""padding:8px 12px;border:1px solid #e2e8f0;background:#f1f5f9;font-weight:bold;"">Story Point 自動扣點</td>
          <td style=""padding:8px 12px;border:1px solid #e2e8f0;"">原點數 <strong>{originalPt:0.##} pt</strong> ➜ <strong style=""color:#dc2626;"">已扣掉 -{deductedPt:0.##} pt</strong>（剩餘有效點數：<strong>{remainingPt:0.##} pt</strong>）</td>
        </tr>
        <tr>
          <td style=""padding:8px 12px;border:1px solid #e2e8f0;background:#f1f5f9;font-weight:bold;"">負責員工與所屬部門</td>
          <td style=""padding:8px 12px;border:1px solid #e2e8f0;"">{WebUtility.HtmlEncode(assigneeSummary)}（部門：{WebUtility.HtmlEncode(departmentSummary)}）</td>
        </tr>
        <tr>
          <td style=""padding:8px 12px;border:1px solid #e2e8f0;background:#f1f5f9;font-weight:bold;"">通知收件對象 (樹狀遞迴階層)</td>
          <td style=""padding:8px 12px;border:1px solid #e2e8f0;font-size:12px;color:#475569;"">
            <div><strong>該同仁的部門主管：</strong>{WebUtility.HtmlEncode(mgrNameCsv)} ({WebUtility.HtmlEncode(mgrEmailCsv)})</div>
            <div><strong>人資主管 (HR)：</strong>{WebUtility.HtmlEncode(hrNameCsv)} ({WebUtility.HtmlEncode(hrEmailCsv)})</div>
          </td>
        </tr>
      </table>
      <p style=""font-size:12px;color:#64748b;margin-bottom:0;"">此信件由 Azure Story Point 估算與工項權重管理系統自動發送（台灣時間：{getTaiwanNow():yyyy-MM-dd HH:mm:ss}）。</p>
    </div>
  </div>
</body>
</html>";

                string plainBody =
                    $"【Story Point 工項達到迄時自動轉態與扣點通知】\n" +
                    $"工項代碼/標題：{itemCode} - {itemTitle}\n" +
                    $"工項類型：{workItemKindText}（代表類型：{typeName}）\n" +
                    $"起訖時間：{startDateStr} ~ {endDateStr}\n" +
                    $"狀態自動轉換：{oldStatusText} -> 已完成 (done)\n" +
                    $"Story Point 扣點：原 {originalPt:0.##} pt -> 扣除 -{deductedPt:0.##} pt -> 剩餘 {remainingPt:0.##} pt\n" +
                    $"負責員工與部門：{assigneeSummary}（部門：{departmentSummary}）\n" +
                    $"通知該同仁的部門主管：{mgrNameCsv} ({mgrEmailCsv})\n" +
                    $"通知人資主管：{hrNameCsv} ({hrEmailCsv})\n";

                var (sendOk, sendStatus) = await sendEmailAsync(recipientList, subject, htmlBody, plainBody, operatorAccount);

                string recipientDisplay = $"該同仁部門主管[{mgrNameCsv}] + 人資主管[{hrNameCsv}] ({allEmailCsv})";
                await _sqlService.executeStoredProcedureDataSetAsync(
                    "storyPoint.spMarkWorkItemExpiryNotified",
                    new Dictionary<string, object?>
                    {
                        ["workItemPk"] = wiPk,
                        ["recipientCsv"] = recipientDisplay.Length > 1000 ? recipientDisplay[..1000] : recipientDisplay,
                        ["isSuccess"] = sendOk,
                        ["errorMessage"] = sendOk ? null : sendStatus,
                        ["operatorAccount"] = operatorAccount
                    });

                if (sendOk)
                {
                    summary.emailSentCount++;
                }
                summary.itemSummaryList.Add($"{itemCode} {itemTitle} ({workItemKindText}，扣除 {deductedPt:0.##} pt)");
                summary.recipientSummaryList.Add(recipientDisplay);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "執行到期工項自動轉態、扣點與寄信時發生例外");
        }
        finally
        {
            _processLock.Release();
        }

        return summary;
    }
}

/// <summary>
/// 背景定時檢核達到迄時工項之常駐服務 (每 30 秒自動檢核一次)
/// </summary>
public class workItemExpiryBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<workItemExpiryBackgroundService> _logger;

    public workItemExpiryBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<workItemExpiryBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var emailService = scope.ServiceProvider.GetRequiredService<emailNotificationService>();
                await emailService.processExpiredWorkItemsAndNotifyAsync(null, "autoExpiryScheduler");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "背景定時檢核到期工項發生非預期例外");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
