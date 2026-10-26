/*
================================================================================
檔案名稱: portalController.cs
用途說明: Azure 多人參與 Story Point 估算與工項權重管理系統之全功能 Web 控制器。
          實作功能：
          1. 員工登入驗證 (/login) 與角色權限識別 (admin 管理者 / participant 參與者)
          2. 多人即時 Planning Poker 投票大廳 (針對「工項代表類型」或「個別工項」盲投、翻牌、開啟下一回合、拍板定案，並透過 SignalR 即時推播)
          3. 事後查詢訂出 Story Point 的完整過程 (每回合每位員工的出牌點數、平均值、最高/最低值、討論備註、核定說明)
          4. 建立工項自動帶入「工項代表類型」Story Point 或手動調整點數，以及管理者修改員工賦予的工項與分攤比例
          5. 查詢各工項 Story Point、員工身上的總 Story Point 數、以及員工所屬部門的總 Story Point 數
          6. 存放與管理員工列表、每個人的登入帳密、部門清單，並支援 CSV 批次匯入與匯出
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
================================================================================
*/

using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using storyPointPortal.hub;
using storyPointPortal.service;

namespace storyPointPortal.controller;

[Route("")]
[Authorize]
public class portalController : Controller
{
    private readonly sqlDataService _sqlService;
    private readonly IHubContext<votingHub> _hubContext;
    private readonly emailNotificationService _emailService;

    public portalController(sqlDataService sqlService, IHubContext<votingHub> hubContext, emailNotificationService emailService)
    {
        _sqlService = sqlService;
        _hubContext = hubContext;
        _emailService = emailService;
    }

    private int getCurrentEmployeePk()
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            return 0;
        }
        string? val = User.FindFirstValue("employeePk");
        return int.TryParse(val, out int pk) && pk > 0 ? pk : 0;
    }

    private string getCurrentLoginAccount()
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            return string.Empty;
        }
        return User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    }

    private string getCurrentEmployeeName()
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            return "未登入訪客";
        }
        return User.FindFirstValue(ClaimTypes.Name) ?? "未登入訪客";
    }

    private string getCurrentRoleName()
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            return string.Empty;
        }
        return User.FindFirstValue(ClaimTypes.Role) ?? "participant";
    }

    private string getCurrentDepartmentName()
    {
        return User.FindFirstValue("departmentName") ?? "";
    }

    private string getCurrentEmployeeCode()
    {
        return User.FindFirstValue("employeeCode") ?? "";
    }

    private bool isAdminUser()
    {
        return string.Equals(getCurrentRoleName(), "admin", StringComparison.OrdinalIgnoreCase);
    }

    private static string h(object? val)
    {
        return WebUtility.HtmlEncode(val?.ToString() ?? string.Empty);
    }

    /// <summary>
    /// 統一攔截控制器例外錯誤並雙軌寫入系統 Log (storyPoint.tbLog + 本機 CSV 檔案)，絕不讓網站當掉
    /// </summary>
    private async Task<string> logPortalExceptionAsync(string actionName, Exception ex)
    {
        int errLine = sqlDataService.extractErrorLineFromStackTrace(ex);
        string opAcc = getCurrentLoginAccount();
        string cleanAction = (actionName ?? "unknown").Trim();
        string objName = cleanAction.StartsWith("portalController.", StringComparison.OrdinalIgnoreCase)
            ? cleanAction
            : $"portalController.{cleanAction}";
        await _sqlService.writeSystemLogAsync(
            logLevel: 1,
            sourceName: "webPortal.controller",
            objectName: objName,
            durationMillisecond: 0,
            executionResult: $"執行 {cleanAction} 時發生例外錯誤（已自動攔截防當機）",
            errorCode: ex.HResult,
            errorLine: errLine,
            errorMessage: $"{ex.GetType().Name}: {ex.Message} | Stack: {ex.StackTrace}",
            operatorAccount: opAcc
        );
        return $"操作發生例外狀況（系統已自動攔截防止當機，並已記錄至系統 Log 供管理者查閱）：{ex.Message}";
    }

    // =========================================================================
    // 1. 員工帳號登入驗證與登出端點 (/login, /logout)
    // =========================================================================

    [HttpGet("login")]
    [AllowAnonymous]
    public async Task<IActionResult> loginPage([FromQuery] string? err = null, [FromQuery] string? msg = null, [FromQuery] string? ReturnUrl = null)
    {
        // 進入帳號登入頁時，強制清除任何舊瀏覽器殘留之登入 Cookie，確保使用者必須輸入帳號與密碼驗證後方可登入
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        Response.Cookies.Delete("storyPointAuthSession");
        Response.Cookies.Delete("storyPointStrictAuthCookie");
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";

        string alertHtml = "";
        if (!string.IsNullOrWhiteSpace(err))
        {
            alertHtml = $"<div class='alert alert-err'>⚠️ {h(err)}</div>";
        }
        else if (!string.IsNullOrWhiteSpace(msg))
        {
            alertHtml = $"<div class='alert alert-ok'>✅ {h(msg)}</div>";
        }

        string html = $@"<!DOCTYPE html>
<html lang=""zh-Hant"">
<head>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />
    <title>員工帳號登入 - Azure 多人參與 Story Point 估算與工項管理系統</title>
    <style>
        * {{ box-sizing:border-box; }}
        body {{ margin:0; font-family:'Microsoft JhengHei UI','Segoe UI',sans-serif; background:linear-gradient(135deg,#0f172a 0%,#1e3a8a 60%,#1d4ed8 100%); min-height:100vh; display:flex; align-items:center; justify-content:center; color:#1e293b; padding:16px; }}
        .login-card {{ background:#ffffff; width:460px; max-width:96vw; border-radius:16px; box-shadow:0 24px 60px rgba(0,0,0,0.42); padding:36px 38px; border:1px solid rgba(255,255,255,0.2); }}
        .brand {{ display:flex; align-items:center; gap:14px; margin-bottom:14px; }}
        .brand-icon {{ width:50px; height:50px; border-radius:12px; background:linear-gradient(135deg,#2563eb,#1e40af); color:#fff; display:flex; align-items:center; justify-content:center; font-size:26px; font-weight:bold; box-shadow:0 6px 16px rgba(37,99,235,0.3); flex-shrink:0; }}
        h1 {{ font-size:21px; margin:0; color:#0f172a; font-weight:800; }}
        .sub {{ font-size:13px; color:#475569; margin:0 0 22px 0; line-height:1.6; padding-bottom:14px; border-bottom:1px solid #e2e8f0; }}
        .form-group {{ margin-bottom:18px; }}
        label {{ display:block; font-size:13.5px; font-weight:700; color:#1e293b; margin-bottom:7px; }}
        .input-wrap {{ position:relative; display:flex; align-items:center; }}
        input[type=text], input[type=password] {{ width:100%; padding:12px 14px; border:1.5px solid #cbd5e1; border-radius:9px; font-size:14.5px; font-family:inherit; transition:all .2s; background:#f8fafc; color:#0f172a; }}
        input:focus {{ outline:none; border-color:#2563eb; background:#ffffff; box-shadow:0 0 0 3px rgba(37,99,235,0.16); }}
        .pwd-toggle-btn {{ position:absolute; right:10px; background:#e2e8f0; border:none; border-radius:6px; padding:4px 9px; font-size:12px; font-weight:700; color:#334155; cursor:pointer; }}
        .pwd-toggle-btn:hover {{ background:#cbd5e1; color:#0f172a; }}
        .btn-primary {{ width:100%; background:linear-gradient(135deg,#2563eb,#1d4ed8); color:#fff; border:none; padding:13px; border-radius:9px; font-size:15.5px; font-weight:800; cursor:pointer; transition:all .2s; box-shadow:0 6px 16px rgba(37,99,235,0.28); margin-top:4px; }}
        .btn-primary:hover {{ background:linear-gradient(135deg,#1d4ed8,#1e40af); transform:translateY(-1px); }}
        .security-footer {{ margin-top:22px; padding:12px 14px; background:#f8fafc; border:1px solid #e2e8f0; border-radius:10px; font-size:12px; color:#475569; line-height:1.55; }}
        .alert {{ padding:11px 14px; border-radius:8px; margin-bottom:16px; font-size:13px; font-weight:700; line-height:1.45; }}
        .alert-err {{ background:#fef2f2; color:#b91c1c; border:1px solid #fecaca; }}
        .alert-ok {{ background:#f0fdf4; color:#15803d; border:1px solid #bbf7d0; }}
    </style>
</head>
<body>
    <div class=""login-card"">
        <div class=""brand"">
            <div class=""brand-icon"">🔐</div>
            <div>
                <h1>員工帳號登入驗證</h1>
                <div style=""font-size:12.5px;color:#2563eb;font-weight:700;margin-top:2px;"">Azure 多人參與 Story Point 估算與工項權重管理系統</div>
            </div>
        </div>
        <p class=""sub"">
            本系統採嚴格帳號身分驗證與角色資料隔離機制。<br/>
            請輸入您的<strong>員工登入帳號</strong>與<strong>登入密碼</strong>進行驗證，未經登入授權無法直接進入系統。
        </p>
        {alertHtml}
        <form method=""post"" action=""/login"" autocomplete=""off"" onsubmit=""return validateLoginForm()"">
            <input type=""hidden"" name=""returnUrl"" value=""{h(ReturnUrl)}"" />
            <div class=""form-group"">
                <label for=""loginAccount"">👤 員工登入帳號 (Login Account)</label>
                <div class=""input-wrap"">
                    <input type=""text"" id=""loginAccount"" name=""loginAccount"" value="""" placeholder=""請輸入員工登入帳號"" autocomplete=""off"" autofocus required />
                </div>
            </div>
            <div class=""form-group"">
                <label for=""plainPassword"">🔑 登入密碼 (Password)</label>
                <div class=""input-wrap"">
                    <input type=""password"" id=""plainPassword"" name=""plainPassword"" value="""" placeholder=""請輸入登入密碼"" autocomplete=""new-password"" style=""padding-right:76px;"" required />
                    <button type=""button"" class=""pwd-toggle-btn"" id=""btnTogglePwd"" onclick=""togglePasswordVisibility()"">👁️ 顯示</button>
                </div>
            </div>
            <button type=""submit"" class=""btn-primary"">🔐 驗證身分並登入系統</button>
        </form>
        <div style=""margin-top:14px;text-align:center;"">
            <a href=""/help"" target=""_blank"" style=""display:inline-flex;align-items:center;justify-content:center;gap:6px;width:100%;padding:10px 14px;border-radius:9px;background:#eff6ff;border:1.5px solid #93c5fd;color:#1d4ed8;font-size:13.5px;font-weight:800;text-decoration:none;transition:all .15s;"">
                📖 開啟系統線上說明與操作畫面手冊 (支援關鍵字搜尋)
            </a>
        </div>
        <div class=""security-footer"">
            🛡️ <strong>安全存取與權限隔離規範：</strong><br/>
            • 系統依登入帳號自動識別「<strong>👑 管理者 (Admin)</strong>」或「<strong>🙋 參與者 (Participant)</strong>」權限。<br/>
            • 一般員工登入後僅可參與投票及查看屬於自己的工項與個人 Story Point 分數。<br/>
            • <strong>每個人皆可修改自己的帳/密：</strong>登入後可隨時點擊上方「<strong>🔑 修改我的帳密</strong>」自助修改個人的登入帳號與登入密碼。
        </div>
    </div>
    <script>
        try {{
            for (let i = localStorage.length - 1; i >= 0; i--) {{
                const k = localStorage.key(i);
                if (k && k.indexOf('sp_active_session_lock_') === 0) {{
                    localStorage.removeItem(k);
                }}
            }}
            if (typeof BroadcastChannel !== 'undefined') {{
                const bc = new BroadcastChannel('sp_voting_session_guard');
                bc.postMessage({{ type: 'USER_LOGGED_OUT' }});
            }}
        }} catch (e) {{ }}

        function togglePasswordVisibility() {{
            const pwdInput = document.getElementById('plainPassword');
            const btn = document.getElementById('btnTogglePwd');
            if (!pwdInput || !btn) return;
            if (pwdInput.type === 'password') {{
                pwdInput.type = 'text';
                btn.textContent = '🙈 隱藏';
            }} else {{
                pwdInput.type = 'password';
                btn.textContent = '👁️ 顯示';
            }}
        }}

        function validateLoginForm() {{
            const acc = (document.getElementById('loginAccount')?.value || '').trim();
            const pwd = document.getElementById('plainPassword')?.value || '';
            if (!acc || !pwd) {{
                alert('⚠️ 請輸入完整的員工登入帳號與登入密碼！');
                return false;
            }}
            return true;
        }}
    </script>
</body>
</html>";
        return Content(html, "text/html; charset=utf-8");
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> loginSubmit([FromForm] string? loginAccount, [FromForm] string? plainPassword, [FromForm] string? returnUrl = null)
    {
        try
        {
            string cleanAcc = (loginAccount ?? "").Trim();
            string rawPwd = plainPassword ?? "";
            string trimmedPwd = rawPwd.Trim();

            if (string.IsNullOrWhiteSpace(cleanAcc) || string.IsNullOrWhiteSpace(trimmedPwd))
            {
                return Redirect($"/login?err={Uri.EscapeDataString("請輸入完整的員工登入帳號與登入密碼，不可空白。")}");
            }

            string pwdHash = sqlDataService.computeSha256(trimmedPwd);

            var ds = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spVerifyEmployeeLogin", new Dictionary<string, object?>
            {
                ["loginAccount"] = cleanAcc,
                ["passwordHash"] = pwdHash
            });

            if ((ds.Tables.Count == 0 || ds.Tables[0].Rows.Count == 0) && rawPwd != trimmedPwd)
            {
                string rawHash = sqlDataService.computeSha256(rawPwd);
                ds = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spVerifyEmployeeLogin", new Dictionary<string, object?>
                {
                    ["loginAccount"] = cleanAcc,
                    ["passwordHash"] = rawHash
                });
            }

            if (ds.Tables.Count == 0 || ds.Tables[0].Rows.Count == 0)
            {
                return Redirect($"/login?err={Uri.EscapeDataString("員工登入帳號或密碼錯誤，或該帳號已停用，請重新輸入。")}");
            }

            DataRow row = ds.Tables[0].Rows[0];
            var claims = new List<Claim>
            {
                new Claim("employeePk", row["employeePk"].ToString() ?? "0"),
                new Claim("employeeCode", row["employeeCode"].ToString() ?? ""),
                new Claim(ClaimTypes.NameIdentifier, row["loginAccount"].ToString() ?? ""),
                new Claim(ClaimTypes.Name, row["employeeName"].ToString() ?? ""),
                new Claim(ClaimTypes.Role, row["roleName"].ToString() ?? "participant"),
                new Claim("departmentPk", row["departmentPk"].ToString() ?? "1"),
                new Claim("departmentName", row["departmentName"].ToString() ?? ""),
                new Claim("jobTitle", row["jobTitle"].ToString() ?? ""),
                new Claim("authBootToken", sqlDataService.serverBootAuthToken)
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);
            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    AllowRefresh = true
                });

            if (!string.IsNullOrWhiteSpace(returnUrl) &&
                Url.IsLocalUrl(returnUrl) &&
                returnUrl.StartsWith("/?tab=", StringComparison.OrdinalIgnoreCase))
            {
                return Redirect(returnUrl);
            }

            return Redirect("/?tab=voting");
        }
        catch (Exception ex)
        {
            string friendly = await logPortalExceptionAsync(nameof(loginSubmit), ex);
            return Redirect($"/login?err={Uri.EscapeDataString(friendly)}");
        }
    }

    [HttpGet("logout")]
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> logout()
    {
        int empPk = getCurrentEmployeePk();
        string empAcc = getCurrentLoginAccount();
        string empName = getCurrentEmployeeName();
        int releasedLocks = 0;

        if (empPk > 0)
        {
            releasedLocks = sqlDataService.releaseAllVotingSessionLeasesForEmployee(empPk);
        }

        if (!string.IsNullOrWhiteSpace(empAcc) && !string.Equals(empAcc, "anonymous", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await _sqlService.writeSystemLogAsync(
                    logLevel: 3,
                    sourceName: "webPortal.auth",
                    objectName: "portalController.logout",
                    durationMillisecond: 1,
                    executionResult: $"員工帳號執行安全登出系統：{empName} ({empAcc})，已同步釋放 {releasedLocks} 個盲投議程獨立網頁佔用鎖定",
                    operatorAccount: empAcc
                );
            }
            catch
            {
                // 登出過程若記錄 Log 發生異常不阻斷登出動作
            }
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        Response.Cookies.Delete("storyPointAuthSession");
        Response.Cookies.Delete("storyPointStrictAuthCookie");
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        return Redirect("/login?msg=" + Uri.EscapeDataString("您已安全登出系統，請重新輸入員工帳號與密碼登入。"));
    }

    // =========================================================================
    // 1-2. 系統線上說明與操作畫面搜尋專頁 (/help)
    // =========================================================================

    [HttpGet("help")]
    [AllowAnonymous]
    public IActionResult onlineHelpPage([FromQuery] string? q = null, [FromQuery] string? cat = null, [FromQuery] string? topic = null)
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        bool isAuth = User?.Identity?.IsAuthenticated == true && getCurrentEmployeePk() > 0;
        bool isAdmin = isAuth && isAdminUser();
        string html = buildStandaloneOnlineHelpPageHtml(isAuth, isAdmin, q, cat, topic);
        return Content(html, "text/html; charset=utf-8");
    }

    // =========================================================================
    // 2. 主系統多頁籤儀表板頁面 (/)
    // =========================================================================

    [HttpGet("")]
    [Authorize]
    public async Task<IActionResult> index([FromQuery] string? tab = null, [FromQuery] int? sessionPk = null, [FromQuery] string? msg = null, [FromQuery] string? err = null)
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        // 當使用者直接開啟網站根網址 (/) 而未帶入頁籤參數，或尚未通過嚴格登入驗證時，一律強制導向 /login 帳號登入頁
        int currentEmpPk = getCurrentEmployeePk();
        string currentAcc = getCurrentLoginAccount();
        if (currentEmpPk <= 0 ||
            string.IsNullOrWhiteSpace(currentAcc) ||
            (string.IsNullOrWhiteSpace(tab) && !sessionPk.HasValue))
        {
            return Redirect("/login");
        }

        bool isAdmin = isAdminUser();
        string currentTab = string.IsNullOrWhiteSpace(tab) ? "voting" : tab.Trim();

        // 一般參與者不可進入系統 Log 頁籤；但每個人皆可進入 myAccount 或 employees 修改自己的登入帳號與密碼
        if (!isAdmin && string.Equals(currentTab, "logs", StringComparison.OrdinalIgnoreCase))
        {
            currentTab = "voting";
        }

        try
        {
            // 先自動檢核是否有已達到迄時 (endDate) 的工項：若有則自動轉換狀態為已完成、扣掉該 Story Point，並寄發 Email 給部門管理層與 HR
            var autoProcessedExpiredResult = await _emailService.processExpiredWorkItemsAndNotifyAsync(null, currentAcc);
            if (autoProcessedExpiredResult.processedCount > 0 && string.IsNullOrWhiteSpace(msg))
            {
                string joinedItems = string.Join("、", autoProcessedExpiredResult.itemSummaryList);
                msg = $"⏰ 系統已偵測到 {autoProcessedExpiredResult.processedCount} 筆工項達到迄時，已自動轉換狀態為「已完成 (已到期扣點)」、扣除其 Story Point，並寄發 Email 通知部門管理層與 HR：{joinedItems}";
            }

            // 載入儀表板完整統計資料（依角色自動隔離：一般員工僅能看到自己的工項與自己的分數，不可看到其他人）
            var dashDs = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spGetStoryPointDashboard", new Dictionary<string, object?>
            {
                ["viewerEmployeePk"] = currentEmpPk,
                ["isViewerAdmin"] = isAdmin
            });
            DataTable deptTable = dashDs.Tables[0];
            DataTable empTable = dashDs.Tables[1];
            DataTable typeTable = dashDs.Tables[2];
            DataTable itemTable = dashDs.Tables[3];
            DataTable assignTable = dashDs.Tables[4];
            DataTable historyTable = dashDs.Tables[5];
            DataTable cardDeckTable = dashDs.Tables.Count > 6 ? dashDs.Tables[6] : new DataTable();
            DataTable logTable = dashDs.Tables.Count > 7 ? dashDs.Tables[7] : _sqlService.getFallbackLogTable();
            DataTable kindTable = dashDs.Tables.Count > 8 ? dashDs.Tables[8] : new DataTable();

            // 載入投票議程與過程資料（依角色自動隔離：一般員工僅能看到自己的投票出牌紀錄，不可看到其他人的出牌與分數）
            var voteDs = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spGetVotingProcessHistory", new Dictionary<string, object?>
            {
                ["viewerEmployeePk"] = currentEmpPk,
                ["isViewerAdmin"] = isAdmin
            });
            DataTable sessionTable = voteDs.Tables[0];
            DataTable roundTable = voteDs.Tables[1];
            DataTable voteRecordTable = voteDs.Tables[2];
            DataTable participantTable = voteDs.Tables[3];

            // 選擇當前聚焦的投票議程 (預設挑第一筆進行中或最新一筆；若傳入之 sessionPk 不屬於該成員則自動聚焦第一筆)
            int activeSessionPk = sessionPk ?? 0;
            bool sessionExistsForMember = false;
            if (activeSessionPk > 0 && sessionTable.Rows.Count > 0)
            {
                foreach (DataRow sr in sessionTable.Rows)
                {
                    if (Convert.ToInt32(sr["votingSessionPk"]) == activeSessionPk)
                    {
                        sessionExistsForMember = true;
                        break;
                    }
                }
            }
            if ((!sessionExistsForMember || activeSessionPk <= 0) && sessionTable.Rows.Count > 0)
            {
                activeSessionPk = Convert.ToInt32(sessionTable.Rows[0]["votingSessionPk"]);
            }

            string html = buildPortalHtml(
                currentTab, activeSessionPk, isAdmin, msg, err,
                deptTable, empTable, typeTable, itemTable, assignTable, historyTable, cardDeckTable, logTable, kindTable,
                sessionTable, roundTable, voteRecordTable, participantTable
            );

            return Content(html, "text/html; charset=utf-8");
        }
        catch (Exception ex)
        {
            string friendly = await logPortalExceptionAsync(nameof(index), ex);
            DataTable fallbackLogs = _sqlService.getFallbackLogTable();
            try
            {
                var logDs = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spGetSystemLog", new Dictionary<string, object?>
                {
                    ["topCount"] = 200,
                    ["operatorAccount"] = getCurrentLoginAccount()
                });
                if (logDs.Tables.Count > 0) fallbackLogs = logDs.Tables[0];
            }
            catch
            {
                // 若資料庫也暫時無法連線，使用記憶體備援 Log
            }

            var sbErr = new StringBuilder();
            sbErr.Append($@"<!DOCTYPE html><html lang='zh-Hant'><head><meta charset='utf-8'/><title>系統防當機保護與錯誤 Log 檢視</title>
            <style>body{{font-family:'Microsoft JhengHei UI',sans-serif;background:#f8fafc;padding:24px;color:#0f172a;}} .card{{background:#fff;border:1px solid #cbd5e1;border-radius:12px;padding:20px;margin-bottom:20px;}} table{{width:100%;border-collapse:collapse;font-size:13px;}} th,td{{padding:9px 11px;border-bottom:1px solid #e2e8f0;text-align:left;}} th{{background:#f1f5f9;}} .btn{{display:inline-block;padding:8px 14px;background:#2563eb;color:#fff;border-radius:8px;text-decoration:none;font-weight:700;}}</style></head><body>
            <div class='card' style='border-color:#fca5a5;background:#fef2f2;'>
                <h2 style='margin-top:0;color:#b91c1c;'>🛡️ 系統防當機保護：已自動攔截頁面例外並寫入系統 Log</h2>
                <p><strong>錯誤原因：</strong>{h(friendly)}</p>
                <a class='btn' href='/?tab=voting'>🔄 重新載入首頁</a>
            </div>");
            if (isAdmin)
            {
                sbErr.Append("<div class='card'><h3>📜 最新系統與錯誤 Log 紀錄 (storyPoint.tbLog)</h3><table><thead><tr><th>時間</th><th>層級</th><th>來源</th><th>執行物件</th><th>執行結果</th><th>錯誤行號</th><th>錯誤訊息</th><th>操作人員</th></tr></thead><tbody>");
                foreach (DataRow lr in fallbackLogs.Rows)
                {
                    sbErr.Append($"<tr><td>{h(lr["logTime"])}</td><td>{h(lr["logLevelText"])}</td><td>{h(lr["sourceName"])}</td><td>{h(lr["objectName"])}</td><td>{h(lr["executionResult"])}</td><td>{h(lr["errorLine"])}</td><td style='color:#b91c1c;'>{h(lr["errorMessage"])}</td><td>{h(lr["operatorAccount"])}</td></tr>");
                }
                sbErr.Append("</tbody></table></div>");
            }
            sbErr.Append("</body></html>");
            return Content(sbErr.ToString(), "text/html; charset=utf-8");
        }
    }

    private static string calculateSuggestedPoint(
        DataTable voteRecordTable,
        int sessionPk,
        int roundNum,
        string consensusRule,
        string rawCardCsv,
        decimal minPoint,
        decimal maxPoint)
    {
        var numericVotes = new List<decimal>();
        string numCol = voteRecordTable.Columns.Contains("numericPointValue") ? "numericPointValue" : "numericPoint";
        foreach (DataRow vr in voteRecordTable.Rows)
        {
            if (Convert.ToInt32(vr["votingSessionPk"]) == sessionPk &&
                Convert.ToInt32(vr["roundNumber"]) == roundNum &&
                voteRecordTable.Columns.Contains(numCol) &&
                vr[numCol] != DBNull.Value)
            {
                numericVotes.Add(Convert.ToDecimal(vr[numCol]));
            }
        }
        string[] deckCards = (rawCardCsv ?? "0,0.5,1,2,3,5,8,13,21,?,☕").Split(',', StringSplitOptions.RemoveEmptyEntries);
        decimal suggested = calculateSuggestedPoint(numericVotes, consensusRule, deckCards, minPoint, maxPoint);
        return suggested.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static decimal calculateSuggestedPoint(
        List<decimal> numericVotes,
        string consensusRule,
        string[] deckCards,
        decimal minPoint,
        decimal maxPoint)
    {
        if (numericVotes.Count == 0)
        {
            return Math.Max(minPoint, Math.Min(5m, maxPoint));
        }

        var sorted = numericVotes.OrderBy(x => x).ToList();
        decimal rawVal;

        switch (consensusRule)
        {
            case "median":
            {
                int mid = sorted.Count / 2;
                rawVal = (sorted.Count % 2 == 1)
                    ? sorted[mid]
                    : (sorted[mid - 1] + sorted[mid]) / 2m;
                break;
            }
            case "mode":
            {
                rawVal = sorted
                    .GroupBy(x => x)
                    .OrderByDescending(g => g.Count())
                    .ThenByDescending(g => g.Key)
                    .First().Key;
                break;
            }
            case "trimmedMean":
            {
                if (sorted.Count >= 3)
                {
                    rawVal = sorted.Skip(1).Take(sorted.Count - 2).Average();
                }
                else
                {
                    rawVal = sorted.Average();
                }
                break;
            }
            case "maxConservative":
            {
                rawVal = sorted.Max();
                break;
            }
            case "average":
            case "nearestFibonacci":
            default:
            {
                rawVal = sorted.Average();
                break;
            }
        }

        rawVal = Math.Max(minPoint, Math.Min(maxPoint, rawVal));

        if (string.Equals(consensusRule, "nearestFibonacci", StringComparison.OrdinalIgnoreCase))
        {
            var validDeckNumbers = new List<decimal>();
            foreach (string c in deckCards)
            {
                if (decimal.TryParse(c.Trim(), out decimal dVal) && dVal >= minPoint && dVal <= maxPoint)
                {
                    validDeckNumbers.Add(dVal);
                }
            }
            if (validDeckNumbers.Count > 0)
            {
                return validDeckNumbers
                    .OrderBy(d => Math.Abs(d - rawVal))
                    .ThenByDescending(d => d)
                    .First();
            }
        }

        return Math.Round(rawVal * 2m, MidpointRounding.AwayFromZero) / 2m;
    }

    // =========================================================================
    // 3. 多人投票操作端點 (出牌、建立議程與設定盲投回合、翻牌揭曉、下一回合、拍板定案)
    // =========================================================================

    [HttpPost("voting/create")]
    [HttpPost("voting/session/create")]
    [Authorize]
    public async Task<IActionResult> createVotingSession(
        [FromForm] int? votingSessionPk,
        [FromForm] string sessionTitle,
        [FromForm] string targetKind,
        [FromForm] int? workItemTypePk,
        [FromForm] int? workItemPk,
        [FromForm] int? cardDeckPk,
        [FromForm] int[]? participantPks,
        [FromForm] string? participantPkCsv,
        [FromForm] decimal? minStoryPoint,
        [FromForm] decimal? maxStoryPoint,
        [FromForm] decimal? maxAllowedSpread,
        [FromForm] string? consensusRule,
        [FromForm] bool autoRevealWhenAllVoted = true,
        [FromForm] int? roundTimeLimitSecond = 120,
        [FromForm] bool allowQuestionCard = true,
        [FromForm] bool allowCoffeeCard = true,
        [FromForm] string? voteCommentPolicy = "optional",
        [FromForm] bool restrictToParticipant = true,
        [FromForm] string? roundGoalNote = null,
        [FromForm] bool resetCurrentRoundVotes = false,
        [FromForm] string? submitMode = "saveAndGoVoting",
        [FromForm] bool fromStandalone = false,
        [FromForm] string? tabInstanceId = null)
    {
        if (!isAdminUser())
        {
            if (fromStandalone && votingSessionPk.HasValue && votingSessionPk.Value > 0)
            {
                return Redirect($"/voting/session?sessionPk={votingSessionPk.Value}&tabInstanceId={Uri.EscapeDataString(tabInstanceId ?? "")}&err=" + Uri.EscapeDataString("僅管理者可以發起新的多人投票議程或設定盲投回合。"));
            }
            return Redirect("/?tab=voting&err=" + Uri.EscapeDataString("僅管理者可以發起新的多人投票議程或設定盲投回合。"));
        }

        try
        {
            string resolvedParticipantCsv = participantPkCsv ?? "";
            if (participantPks != null && participantPks.Length > 0)
            {
                resolvedParticipantCsv = string.Join(",", participantPks.Where(id => id > 0).Distinct());
            }

            bool isUpdatingExisting = votingSessionPk.HasValue && votingSessionPk.Value > 0;
            string sessionCode = "VOTE-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string cleanTitle = string.IsNullOrWhiteSpace(sessionTitle)
                ? $"Story Point 估算議程 ({sessionCode})"
                : sessionTitle.Trim();

            var ds = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spCreateVotingSession", new Dictionary<string, object?>
            {
                ["votingSessionPk"] = isUpdatingExisting ? votingSessionPk!.Value : null,
                ["sessionCode"] = sessionCode,
                ["sessionTitle"] = cleanTitle,
                ["targetKind"] = targetKind == "workItem" ? "workItem" : "workItemType",
                ["workItemTypePk"] = (workItemTypePk.HasValue && workItemTypePk.Value > 0) ? workItemTypePk.Value : null,
                ["workItemPk"] = (workItemPk.HasValue && workItemPk.Value > 0) ? workItemPk.Value : null,
                ["cardDeckPk"] = cardDeckPk ?? 1,
                ["hostEmployeePk"] = getCurrentEmployeePk(),
                ["participantPkCsv"] = resolvedParticipantCsv,
                ["minStoryPoint"] = minStoryPoint ?? 0.00m,
                ["maxStoryPoint"] = maxStoryPoint ?? 21.00m,
                ["maxAllowedSpread"] = maxAllowedSpread ?? 5.00m,
                ["consensusRule"] = string.IsNullOrWhiteSpace(consensusRule) ? "nearestFibonacci" : consensusRule.Trim(),
                ["autoRevealWhenAllVoted"] = autoRevealWhenAllVoted,
                ["roundTimeLimitSecond"] = roundTimeLimitSecond ?? 120,
                ["allowQuestionCard"] = allowQuestionCard,
                ["allowCoffeeCard"] = allowCoffeeCard,
                ["voteCommentPolicy"] = string.IsNullOrWhiteSpace(voteCommentPolicy) ? "optional" : voteCommentPolicy.Trim(),
                ["restrictToParticipant"] = restrictToParticipant,
                ["roundGoalNote"] = roundGoalNote,
                ["resetCurrentRoundVotes"] = resetCurrentRoundVotes,
                ["operatorAccount"] = getCurrentLoginAccount()
            });

            int newPk = ds.Tables[0].Rows.Count > 0 ? Convert.ToInt32(ds.Tables[0].Rows[0]["votingSessionPk"]) : (votingSessionPk ?? 0);
            string eventMsg = isUpdatingExisting
                ? $"管理者 {getCurrentEmployeeName()} 更新了盲投回合設定：{cleanTitle} (範圍 {minStoryPoint ?? 0}~{maxStoryPoint ?? 21} pt)"
                : $"管理者 {getCurrentEmployeeName()} 發起了新盲投議程：{cleanTitle} (範圍 {minStoryPoint ?? 0}~{maxStoryPoint ?? 21} pt)";

            await _hubContext.Clients.All.SendAsync("onVotingSessionUpdated", new
            {
                votingSessionPk = newPk,
                eventName = isUpdatingExisting ? "roundSettingUpdated" : "sessionCreated",
                messageText = eventMsg,
                timestamp = DateTime.Now.ToString("HH:mm:ss")
            });

            string okText = isUpdatingExisting
                ? $"已成功儲存議程「{cleanTitle}」的盲投回合設定（允許點數：{minStoryPoint ?? 0:0.##} ~ {maxStoryPoint ?? 21:0.##} pt）！"
                : $"已成功啟動多人盲投議程「{cleanTitle}」（允許點數：{minStoryPoint ?? 0:0.##} ~ {maxStoryPoint ?? 21:0.##} pt）！議程已列入「📂 待參與」，請從「待參與」點選該議程以進入「🟢 參與中」的獨立網頁。";

            if (fromStandalone)
            {
                var (_, assignedTabId, _) = sqlDataService.tryEnterOrRefreshVotingSessionLease(
                    getCurrentEmployeePk(),
                    getCurrentEmployeeName(),
                    newPk,
                    tabInstanceId);
                string tokenParam = !string.IsNullOrEmpty(assignedTabId) ? $"&tabInstanceId={Uri.EscapeDataString(assignedTabId)}" : "";
                return Redirect($"/voting/session?sessionPk={newPk}{tokenParam}&msg=" + Uri.EscapeDataString(okText));
            }

            string targetTab = string.Equals(submitMode, "saveStaySetup", StringComparison.OrdinalIgnoreCase) ? "roundSetup" : "voting";
            return Redirect($"/?tab={targetTab}&sessionPk={newPk}&msg=" + Uri.EscapeDataString(okText));
        }
        catch (Exception ex)
        {
            await logPortalExceptionAsync("portalController.createVotingSession", ex);
            if (fromStandalone && votingSessionPk.HasValue && votingSessionPk.Value > 0)
            {
                return Redirect($"/voting/session?sessionPk={votingSessionPk.Value}&tabInstanceId={Uri.EscapeDataString(tabInstanceId ?? "")}&err=" + Uri.EscapeDataString("儲存盲投議程設定時發生異常（已自動記錄至系統 Log）：" + ex.Message));
            }
            return Redirect("/?tab=roundSetup&err=" + Uri.EscapeDataString("儲存盲投議程設定時發生異常（已自動記錄至系統 Log）：" + ex.Message));
        }
    }

    [HttpPost("voting/cast")]
    [Authorize]
    public async Task<IActionResult> castVote(
        [FromForm] int votingSessionPk,
        [FromForm] string cardValue,
        [FromForm] string? voteComment,
        [FromForm] int? actAsEmployeePk = null,
        [FromForm] bool fromStandalone = false,
        [FromForm] string? tabInstanceId = null)
    {
        int voterPk = getCurrentEmployeePk();
        if (isAdminUser() && actAsEmployeePk.HasValue && actAsEmployeePk.Value > 0)
        {
            voterPk = actAsEmployeePk.Value;
        }
        if (voterPk <= 0)
        {
            return Redirect("/login?err=" + Uri.EscapeDataString("請先輸入員工帳號與密碼登入系統。"));
        }

        try
        {
            string cleanVal = (cardValue ?? "").Trim();
            var (_, fullCardMeaning) = getPokerCardMeaning(cleanVal);

            var ds = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spCastVote", new Dictionary<string, object?>
            {
                ["votingSessionPk"] = votingSessionPk,
                ["employeePk"] = voterPk,
                ["cardValue"] = cleanVal,
                ["voteComment"] = voteComment,
                ["operatorAccount"] = getCurrentLoginAccount()
            });

            bool isAutoRevealed = false;
            string updatedSessionStatus = "voting";
            int votedCnt = 0;
            int reqCnt = 0;
            if (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0)
            {
                var r0 = ds.Tables[0].Rows[0];
                if (r0.Table.Columns.Contains("isAutoRevealed") && r0["isAutoRevealed"] != DBNull.Value)
                    isAutoRevealed = Convert.ToBoolean(r0["isAutoRevealed"]);
                if (r0.Table.Columns.Contains("sessionStatus") && r0["sessionStatus"] != DBNull.Value)
                    updatedSessionStatus = r0["sessionStatus"].ToString() ?? "voting";
                if (r0.Table.Columns.Contains("votedParticipantCount") && r0["votedParticipantCount"] != DBNull.Value)
                    votedCnt = Convert.ToInt32(r0["votedParticipantCount"]);
                if (r0.Table.Columns.Contains("requiredVoterCount") && r0["requiredVoterCount"] != DBNull.Value)
                    reqCnt = Convert.ToInt32(r0["requiredVoterCount"]);
            }

            string broadcastMsg = isAutoRevealed
                ? $"🎉 所有受邀參與者 ({votedCnt}/{reqCnt} 人) 皆已完成投票，系統已自動翻牌揭曉所有人的點數！"
                : $"有一位參與者已完成盲投出牌（目前已出牌 {votedCnt}/{reqCnt} 人，待全員投票後自動翻牌）！";

            await _hubContext.Clients.All.SendAsync("onVotingSessionUpdated", new
            {
                votingSessionPk = votingSessionPk,
                eventName = isAutoRevealed ? "autoRevealed" : "voteCast",
                sessionStatus = updatedSessionStatus,
                isAutoRevealed = isAutoRevealed,
                votedParticipantCount = votedCnt,
                requiredVoterCount = reqCnt,
                messageText = broadcastMsg,
                timestamp = DateTime.Now.ToString("HH:mm:ss")
            });

            string redirectMsg = isAutoRevealed
                ? $"已投出撲克牌「{fullCardMeaning}」！🎉 全體受邀參與者 ({votedCnt}/{reqCnt} 人) 皆已完成投票，所有人的畫面已自動翻牌揭曉！"
                : $"已成功投出撲克牌「{fullCardMeaning}」（目前已出牌 {votedCnt}/{reqCnt} 人，待全員完成投票後將自動翻牌揭曉）！";

            if (fromStandalone || !string.IsNullOrWhiteSpace(tabInstanceId))
            {
                var (_, assignedTabId, _) = sqlDataService.tryEnterOrRefreshVotingSessionLease(
                    getCurrentEmployeePk(),
                    getCurrentEmployeeName(),
                    votingSessionPk,
                    tabInstanceId);
                string tokenParam = !string.IsNullOrEmpty(assignedTabId) ? $"&tabInstanceId={Uri.EscapeDataString(assignedTabId)}" : "";
                return Redirect($"/voting/session?sessionPk={votingSessionPk}{tokenParam}&msg=" + Uri.EscapeDataString(redirectMsg));
            }

            return Redirect($"/voting/session?sessionPk={votingSessionPk}&msg=" + Uri.EscapeDataString(redirectMsg));
        }
        catch (Exception ex)
        {
            await logPortalExceptionAsync("portalController.castVote", ex);
            if (fromStandalone || !string.IsNullOrWhiteSpace(tabInstanceId))
            {
                return Redirect($"/voting/session?sessionPk={votingSessionPk}&tabInstanceId={Uri.EscapeDataString(tabInstanceId ?? "")}&err=" + Uri.EscapeDataString(ex.Message));
            }
            return Redirect($"/voting/session?sessionPk={votingSessionPk}&err=" + Uri.EscapeDataString(ex.Message));
        }
    }

    [HttpPost("voting/control")]
    [HttpPost("voting/revote")]
    [Authorize]
    public async Task<IActionResult> controlVotingSession(
        [FromForm] int votingSessionPk = 0,
        [FromForm] string? controlAction = null,
        [FromForm] string? submitAction = null,
        [FromForm] string? actionType = null,
        [FromForm] string? roundDiscussionNote = null,
        [FromForm] decimal? finalStoryPoint = null,
        [FromForm] string? finalDecisionNote = null,
        [FromForm] bool syncExistingOpenWorkItem = false,
        [FromForm] bool fromStandalone = false,
        [FromForm] string? tabInstanceId = null,
        [FromForm] string? targetKind = null,
        [FromForm] int? workItemTypePk = null,
        [FromForm] int? workItemPk = null,
        [FromForm] string? returnTab = null)
    {
        if (!isAdminUser())
        {
            if (fromStandalone && votingSessionPk > 0)
            {
                return Redirect($"/voting/session?sessionPk={votingSessionPk}&tabInstanceId={Uri.EscapeDataString(tabInstanceId ?? "")}&err=" + Uri.EscapeDataString("僅管理者可執行翻牌揭曉、重新投票、開啟下一回合或拍板定案。"));
            }
            return Redirect($"/?tab={Uri.EscapeDataString(string.IsNullOrWhiteSpace(returnTab) ? "voting" : returnTab)}&err=" + Uri.EscapeDataString("僅管理者可執行翻牌揭曉、重新投票、開啟下一回合或拍板定案。"));
        }

        string resolvedAction = !string.IsNullOrWhiteSpace(submitAction)
            ? submitAction.Trim()
            : (!string.IsNullOrWhiteSpace(controlAction)
                ? controlAction.Trim()
                : (!string.IsNullOrWhiteSpace(actionType) ? actionType.Trim() : "revote"));

        string? cleanDiscussionNote = string.IsNullOrWhiteSpace(roundDiscussionNote)
            ? null
            : roundDiscussionNote.Trim();
        string? cleanFinalDecisionNote = string.IsNullOrWhiteSpace(finalDecisionNote)
            ? null
            : finalDecisionNote.Trim();
        string? effectiveDecisionNote = cleanFinalDecisionNote ?? cleanDiscussionNote;

        try
        {
            var ds = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spControlVotingSession", new Dictionary<string, object?>
            {
                ["votingSessionPk"] = votingSessionPk > 0 ? votingSessionPk : null,
                ["controlAction"] = resolvedAction,
                ["roundDiscussionNote"] = cleanDiscussionNote,
                ["finalStoryPoint"] = finalStoryPoint,
                ["finalDecisionNote"] = effectiveDecisionNote,
                ["syncExistingOpenWorkItem"] = syncExistingOpenWorkItem,
                ["operatorEmployeePk"] = getCurrentEmployeePk(),
                ["operatorAccount"] = getCurrentLoginAccount(),
                ["targetKindParam"] = string.IsNullOrWhiteSpace(targetKind) ? null : targetKind.Trim(),
                ["workItemTypePkParam"] = (workItemTypePk.HasValue && workItemTypePk.Value > 0) ? workItemTypePk.Value : null,
                ["workItemPkParam"] = (workItemPk.HasValue && workItemPk.Value > 0) ? workItemPk.Value : null
            });

            int effectiveSessionPk = votingSessionPk;
            string effectiveSessionTitle = "";
            int effectiveRoundNum = 1;
            if (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0)
            {
                var r0 = ds.Tables[0].Rows[0];
                if (r0.Table.Columns.Contains("votingSessionPk") && r0["votingSessionPk"] != DBNull.Value)
                    effectiveSessionPk = Convert.ToInt32(r0["votingSessionPk"]);
                if (r0.Table.Columns.Contains("sessionTitle") && r0["sessionTitle"] != DBNull.Value)
                    effectiveSessionTitle = r0["sessionTitle"]?.ToString() ?? "";
                if (r0.Table.Columns.Contains("currentRoundNumber") && r0["currentRoundNumber"] != DBNull.Value)
                    effectiveRoundNum = Convert.ToInt32(r0["currentRoundNumber"]);
            }

            string actionText = resolvedAction switch
            {
                "reveal" => !string.IsNullOrEmpty(cleanDiscussionNote)
                    ? "已翻牌揭曉本回合點數，並儲存本回合討論紀錄！"
                    : "已翻牌揭曉本回合所有人的出牌點數！",
                "nextRound" => !string.IsNullOrEmpty(cleanDiscussionNote)
                    ? $"已將本回合討論紀錄（「{cleanDiscussionNote}」）寫入回合紀錄，並成功開啟下一回合重新盲投！"
                    : "已開啟下一回合重新盲投！",
                "revote" => !string.IsNullOrEmpty(effectiveSessionTitle)
                    ? $"🔄 已針對「{effectiveSessionTitle}」啟動重新投票（目前為第 {effectiveRoundNum} 回合盲投進行中），估算狀態已重設為投票估算中！"
                    : $"🔄 已成功啟動重新投票（第 {effectiveRoundNum} 回合盲投進行中）！",
                "resetRound" => !string.IsNullOrEmpty(effectiveSessionTitle)
                    ? $"🔁 已清空「{effectiveSessionTitle}」本回合（第 {effectiveRoundNum} 回合）既有出牌紀錄並重啟重新投票！"
                    : "🔁 已清空本回合既有出牌紀錄並重啟重新投票！",
                "finalize" => !string.IsNullOrEmpty(effectiveDecisionNote)
                    ? $"已拍板定案最終 Story Point 為 {finalStoryPoint:0.##} 點（定案決議說明：「{effectiveDecisionNote}」），並自動更新目標點數與歷程！"
                    : $"已拍板定案最終 Story Point 為 {finalStoryPoint:0.##} 點，並自動更新目標點數與歷程！",
                _ => "投票議程狀態已更新"
            };

            await _hubContext.Clients.All.SendAsync("onVotingSessionUpdated", new
            {
                votingSessionPk = effectiveSessionPk,
                eventName = resolvedAction,
                sessionStatus = (resolvedAction == "revote" || resolvedAction == "resetRound" || resolvedAction == "nextRound") ? "voting" : null,
                messageText = actionText,
                timestamp = DateTime.Now.ToString("HH:mm:ss")
            });

            if (fromStandalone || !string.IsNullOrWhiteSpace(tabInstanceId))
            {
                var (_, assignedTabId, _) = sqlDataService.tryEnterOrRefreshVotingSessionLease(
                    getCurrentEmployeePk(),
                    getCurrentEmployeeName(),
                    effectiveSessionPk,
                    tabInstanceId);
                string tokenParam = !string.IsNullOrEmpty(assignedTabId) ? $"&tabInstanceId={Uri.EscapeDataString(assignedTabId)}" : "";
                return Redirect($"/voting/session?sessionPk={effectiveSessionPk}{tokenParam}&msg=" + Uri.EscapeDataString(actionText));
            }

            if (!string.IsNullOrWhiteSpace(returnTab))
            {
                return Redirect($"/?tab={Uri.EscapeDataString(returnTab.Trim())}&sessionPk={effectiveSessionPk}&msg=" + Uri.EscapeDataString(actionText));
            }

            return Redirect($"/voting/session?sessionPk={effectiveSessionPk}&msg=" + Uri.EscapeDataString(actionText));
        }
        catch (Exception ex)
        {
            await logPortalExceptionAsync("portalController.controlVotingSession", ex);
            if (!fromStandalone && string.IsNullOrWhiteSpace(tabInstanceId) && !string.IsNullOrWhiteSpace(returnTab))
            {
                return Redirect($"/?tab={Uri.EscapeDataString(returnTab.Trim())}&err=" + Uri.EscapeDataString("控制投票議程時發生異常（已自動記錄至系統 Log）：" + ex.Message));
            }
            return Redirect($"/voting/session?sessionPk={votingSessionPk}&tabInstanceId={Uri.EscapeDataString(tabInstanceId ?? "")}&err=" + Uri.EscapeDataString("控制投票議程時發生異常（已自動記錄至系統 Log）：" + ex.Message));
        }
    }

    [HttpGet("voting/liveStatus")]
    [Authorize]
    public async Task<IActionResult> getVotingLiveStatus([FromQuery] int? sessionPk = null, [FromQuery] string? tabInstanceId = null)
    {
        try
        {
            int currentEmpPk = getCurrentEmployeePk();
            bool isAdmin = isAdminUser();

            bool isSessionLeaseOwner = true;
            string currentOwnerTabId = "";
            if (sessionPk.HasValue && sessionPk.Value > 0 && !string.IsNullOrWhiteSpace(tabInstanceId))
            {
                var hb = sqlDataService.refreshVotingSessionHeartbeat(
                    currentEmpPk,
                    getCurrentEmployeeName(),
                    sessionPk.Value,
                    tabInstanceId);
                isSessionLeaseOwner = hb.isOwner;
                currentOwnerTabId = hb.currentOwnerTabInstanceId;
            }

            List<int> myActiveOpenSessionPks = sqlDataService.getActiveLockedSessionPksForEmployee(currentEmpPk);

            // 查詢屬於該成員的所有投票議程 (votingSessionPk 傳 null)，以便同時偵測是否有新議程出現
            var votingDs = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spGetVotingProcessHistory", new Dictionary<string, object?>
            {
                ["votingSessionPk"] = null,
                ["viewerEmployeePk"] = currentEmpPk,
                ["isViewerAdmin"] = isAdmin
            });

            if (votingDs.Tables.Count == 0 || votingDs.Tables[0].Rows.Count == 0)
            {
                return Json(new
                {
                    found = true,
                    votingSessionPk = 0,
                    sessionStatus = "",
                    currentRoundNumber = 0,
                    voteCount = 0,
                    participantCount = 0,
                    isRevealed = false,
                    allSessionPks = Array.Empty<int>(),
                    ongoingSessionPks = Array.Empty<int>(),
                    latestOngoingSessionPk = 0,
                    newestSessionPk = 0,
                    sessionListSignature = "",
                    isSessionLeaseOwner = isSessionLeaseOwner,
                    currentOwnerTabInstanceId = currentOwnerTabId,
                    myActiveOpenSessionPks = myActiveOpenSessionPks
                });
            }

            DataTable sTable = votingDs.Tables[0];
            var allSessionPks = new List<int>();
            var ongoingSessionPks = new List<int>();
            var pendingJoinSessionPks = new List<int>();
            var participatingSessionPks = new List<int>();
            var sigParts = new List<string>();
            int latestVotingPk = 0;
            int latestOngoingPk = 0;
            int newestPk = 0;
            DataRow? targetRow = null;

            foreach (DataRow r in sTable.Rows)
            {
                int pk = Convert.ToInt32(r["votingSessionPk"]);
                string st = r["sessionStatus"]?.ToString() ?? "voting";
                int rn = r["currentRoundNumber"] != DBNull.Value ? Convert.ToInt32(r["currentRoundNumber"]) : 1;
                int vc = r.Table.Columns.Contains("currentRoundVoteCount") && r["currentRoundVoteCount"] != DBNull.Value
                    ? Convert.ToInt32(r["currentRoundVoteCount"])
                    : 0;
                int pc = r.Table.Columns.Contains("participantCount") && r["participantCount"] != DBNull.Value
                    ? Convert.ToInt32(r["participantCount"])
                    : 0;

                allSessionPks.Add(pk);
                if (pk > newestPk) newestPk = pk;

                if (st == "voting" || st == "revealed")
                {
                    ongoingSessionPks.Add(pk);
                    if (myActiveOpenSessionPks.Contains(pk))
                    {
                        participatingSessionPks.Add(pk);
                    }
                    else
                    {
                        pendingJoinSessionPks.Add(pk);
                    }
                    if (pk > latestOngoingPk) latestOngoingPk = pk;
                    if (st == "voting" && pk > latestVotingPk) latestVotingPk = pk;
                }

                sigParts.Add($"{pk}:{st}:{rn}:{vc}:{pc}");

                if (sessionPk.HasValue && sessionPk.Value > 0 && pk == sessionPk.Value)
                {
                    targetRow = r;
                }
            }

            if (targetRow == null)
            {
                targetRow = sTable.Rows[0];
            }

            int vsPk = Convert.ToInt32(targetRow["votingSessionPk"]);
            string sStatus = targetRow["sessionStatus"]?.ToString() ?? "voting";
            int curRound = targetRow["currentRoundNumber"] != DBNull.Value ? Convert.ToInt32(targetRow["currentRoundNumber"]) : 1;
            int voteCnt = targetRow.Table.Columns.Contains("currentRoundVoteCount") && targetRow["currentRoundVoteCount"] != DBNull.Value
                ? Convert.ToInt32(targetRow["currentRoundVoteCount"])
                : 0;
            int partCnt = targetRow.Table.Columns.Contains("participantCount") && targetRow["participantCount"] != DBNull.Value
                ? Convert.ToInt32(targetRow["participantCount"])
                : 0;

            return Json(new
            {
                found = true,
                votingSessionPk = vsPk,
                sessionStatus = sStatus,
                currentRoundNumber = curRound,
                voteCount = voteCnt,
                participantCount = partCnt,
                isRevealed = sStatus == "revealed" || sStatus == "finalized",
                allSessionPks = allSessionPks,
                ongoingSessionPks = ongoingSessionPks,
                pendingJoinSessionPks = pendingJoinSessionPks,
                participatingSessionPks = participatingSessionPks,
                latestOngoingSessionPk = latestVotingPk > 0 ? latestVotingPk : (latestOngoingPk > 0 ? latestOngoingPk : newestPk),
                newestSessionPk = newestPk,
                sessionListSignature = string.Join("|", sigParts),
                isSessionLeaseOwner = isSessionLeaseOwner,
                currentOwnerTabInstanceId = currentOwnerTabId,
                myActiveOpenSessionPks = myActiveOpenSessionPks
            });
        }
        catch
        {
            return Json(new { found = false });
        }
    }

    /// <summary>
    /// 獨立盲投 Session 網頁心跳續約端點（確保開啟中的獨立網頁持續保有進入鎖定，防止同一人重複開啟相同 Session）
    /// </summary>
    [HttpPost("voting/session/heartbeat")]
    [Authorize]
    public IActionResult votingSessionHeartbeat([FromForm] int votingSessionPk, [FromForm] string? tabInstanceId)
    {
        int empPk = getCurrentEmployeePk();
        var (isOwner, ownerId) = sqlDataService.refreshVotingSessionHeartbeat(
            empPk,
            getCurrentEmployeeName(),
            votingSessionPk,
            tabInstanceId);
        return Json(new
        {
            ok = true,
            isSessionLeaseOwner = isOwner,
            currentOwnerTabInstanceId = ownerId,
            myActiveOpenSessionPks = sqlDataService.getActiveLockedSessionPksForEmployee(empPk)
        });
    }

    /// <summary>
    /// 離開或關閉獨立盲投 Session 網頁時釋放佔用鎖定（支援 navigator.sendBeacon 與按鈕主動離開）
    /// </summary>
    [HttpPost("voting/session/leave")]
    [HttpGet("voting/session/leave")]
    [Authorize]
    public IActionResult votingSessionLeave(
        [FromQuery] int? sessionPk = null,
        [FromForm] int? votingSessionPk = null,
        [FromQuery] string? tabInstanceId = null,
        [FromForm] string? formTabInstanceId = null,
        [FromQuery] bool redirectLobby = false)
    {
        int empPk = getCurrentEmployeePk();
        int targetSessionPk = votingSessionPk ?? sessionPk ?? 0;
        string? resolvedTabId = !string.IsNullOrWhiteSpace(tabInstanceId) ? tabInstanceId : formTabInstanceId;

        if (Request.HasFormContentType)
        {
            if (targetSessionPk <= 0 &&
                int.TryParse(Request.Form["sessionPk"].FirstOrDefault() ?? Request.Form["votingSessionPk"].FirstOrDefault(), out int formSpk) &&
                formSpk > 0)
            {
                targetSessionPk = formSpk;
            }
            if (string.IsNullOrWhiteSpace(resolvedTabId))
            {
                resolvedTabId = Request.Form["tabInstanceId"].FirstOrDefault() ?? Request.Form["formTabInstanceId"].FirstOrDefault();
            }
        }

        if (targetSessionPk > 0 && empPk > 0)
        {
            sqlDataService.releaseVotingSessionLease(empPk, targetSessionPk, resolvedTabId);
        }

        if (redirectLobby)
        {
            return Redirect("/?tab=voting&msg=" + Uri.EscapeDataString($"您已結束盲投議程 (#{targetSessionPk}) 的「參與中」獨立網頁狀態，該議程已移回「📂 待參與」清單。"));
        }

        return Json(new
        {
            released = true,
            votingSessionPk = targetSessionPk,
            myActiveOpenSessionPks = sqlDataService.getActiveLockedSessionPksForEmployee(empPk)
        });
    }

    /// <summary>
    /// 盲投 Session 獨立專屬網頁 (/voting/session?sessionPk=X 或 /voting/session/X)
    /// 遵循需求規範：
    /// 1. 當一個人進入一個盲投 session 時，使用獨立的網頁進行這整個 session（非 Modal 彈窗）
    /// 2. 讓人可以同時以多個獨立網頁參與「不同」的 session
    /// 3. 但「同一個」session 不能重複進入（若已在另一個獨立網頁開啟同一場 session，立即攔截並顯示禁止重複進入畫面）
    /// </summary>
    [HttpGet("voting/session")]
    [HttpGet("voting/session/{routeSessionPk:int}")]
    [Authorize]
    public async Task<IActionResult> votingSessionStandalonePage(
        [FromQuery] int? sessionPk = null,
        [FromRoute] int? routeSessionPk = null,
        [FromQuery] string? tabInstanceId = null,
        [FromQuery] string? msg = null,
        [FromQuery] string? err = null)
    {
        int currentEmpPk = getCurrentEmployeePk();
        string currentAcc = getCurrentLoginAccount();
        if (currentEmpPk <= 0 || string.IsNullOrWhiteSpace(currentAcc))
        {
            return Redirect("/login");
        }

        int targetSessionPk = sessionPk ?? routeSessionPk ?? 0;
        if (targetSessionPk <= 0)
        {
            return Redirect("/?tab=voting&err=" + Uri.EscapeDataString("請選擇要進入的盲投議程 (Voting Session)。"));
        }

        bool isAdmin = isAdminUser();

        try
        {
            // 讀取該員工可存取的投票議程資料
            var voteDs = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spGetVotingProcessHistory", new Dictionary<string, object?>
            {
                ["viewerEmployeePk"] = currentEmpPk,
                ["isViewerAdmin"] = isAdmin
            });

            DataTable sessionTable = voteDs.Tables[0];
            DataTable roundTable = voteDs.Tables[1];
            DataTable voteRecordTable = voteDs.Tables[2];
            DataTable participantTable = voteDs.Tables[3];

            DataRow? targetSessionRow = null;
            foreach (DataRow sr in sessionTable.Rows)
            {
                if (Convert.ToInt32(sr["votingSessionPk"]) == targetSessionPk)
                {
                    targetSessionRow = sr;
                    break;
                }
            }

            if (targetSessionRow == null)
            {
                return Redirect("/?tab=voting&err=" + Uri.EscapeDataString($"找不到指定的盲投議程 (#{targetSessionPk})，或您不屬於本場議程的受邀參與者名單。"));
            }

            // 檢查是否有同分頁 F5 重新整理暫存 Cookie
            string reloadCookieName = $"sp_reload_inst_{targetSessionPk}";
            string? reloadCookieTabId = Request.Cookies[reloadCookieName];
            if (!string.IsNullOrEmpty(reloadCookieTabId))
            {
                Response.Cookies.Delete(reloadCookieName);
            }

            // 檢核同一員工是否已在另一個獨立網頁進入同一個盲投 Session
            var (isAllowed, assignedTabInstanceId, conflictLease) = sqlDataService.tryEnterOrRefreshVotingSessionLease(
                currentEmpPk,
                getCurrentEmployeeName(),
                targetSessionPk,
                tabInstanceId,
                reloadCookieTabId);

            if (!isAllowed && conflictLease != null)
            {
                await _sqlService.writeSystemLogAsync(
                    logLevel: 2,
                    sourceName: "webPortal.votingSessionGuard",
                    objectName: "portalController.votingSessionStandalonePage",
                    durationMillisecond: 0,
                    executionResult: $"攔截重複進入同一盲投議程：員工 {getCurrentEmployeeName()} ({currentAcc}) 嘗試重複開啟議程 #{targetSessionPk} ({targetSessionRow["sessionCode"]})",
                    operatorAccount: currentAcc);

                string blockedHtml = buildDuplicateVotingSessionBlockedHtml(
                    targetSessionPk,
                    targetSessionRow,
                    conflictLease,
                    sessionTable);
                return Content(blockedHtml, "text/html; charset=utf-8");
            }

            // 載入輔助資料表（供管理者在獨立網頁內也可調整牌組/受邀參與者設定）
            var dashDs = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spGetStoryPointDashboard", new Dictionary<string, object?>
            {
                ["viewerEmployeePk"] = currentEmpPk,
                ["isViewerAdmin"] = isAdmin
            });
            DataTable deptTable = dashDs.Tables[0];
            DataTable empTable = dashDs.Tables[1];
            DataTable cardDeckTable = dashDs.Tables.Count > 6 ? dashDs.Tables[6] : new DataTable();

            string standaloneHtml = buildStandaloneVotingSessionHtml(
                targetSessionPk,
                assignedTabInstanceId,
                isAdmin,
                msg,
                err,
                targetSessionRow,
                sessionTable,
                roundTable,
                voteRecordTable,
                participantTable,
                deptTable,
                empTable,
                cardDeckTable);

            return Content(standaloneHtml, "text/html; charset=utf-8");
        }
        catch (Exception ex)
        {
            string friendly = await logPortalExceptionAsync(nameof(votingSessionStandalonePage), ex);
            return Redirect("/?tab=voting&err=" + Uri.EscapeDataString(friendly));
        }
    }

    // =========================================================================
    // 4. 工項類型 (tbWorkItemKind)、工項代表類型 (tbWorkItemType) 與實際工項 (tbWorkItem) 增刪修端點
    // =========================================================================

    [HttpPost("workitem/kind/save")]
    [Authorize]
    public async Task<IActionResult> saveWorkItemKind(
        [FromForm] int? workItemKindPk,
        [FromForm] string kindCode,
        [FromForm] string kindName,
        [FromForm] string? badgeIcon = "📌",
        [FromForm] string? badgeColor = "blue",
        [FromForm] int sortOrder = 100,
        [FromForm] string? description = null,
        [FromForm] string? returnTab = "workitems")
    {
        string targetTab = string.IsNullOrWhiteSpace(returnTab) ? "workitems" : returnTab.Trim();
        if (!isAdminUser())
        {
            return Redirect($"/?tab={targetTab}&err=" + Uri.EscapeDataString("僅管理者可新增或修改工項類型。"));
        }

        try
        {
            var ds = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spUpsertWorkItemKind", new Dictionary<string, object?>
            {
                ["workItemKindPk"] = workItemKindPk,
                ["kindCode"] = (kindCode ?? "").Trim(),
                ["kindName"] = (kindName ?? "").Trim(),
                ["badgeIcon"] = string.IsNullOrWhiteSpace(badgeIcon) ? "📌" : badgeIcon.Trim(),
                ["badgeColor"] = string.IsNullOrWhiteSpace(badgeColor) ? "blue" : badgeColor.Trim(),
                ["sortOrder"] = sortOrder,
                ["description"] = description,
                ["isActive"] = true,
                ["operatorAccount"] = getCurrentLoginAccount()
            });

            string savedCode = (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0) ? (ds.Tables[0].Rows[0]["kindCode"]?.ToString() ?? kindCode) : kindCode;
            string savedName = (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0) ? (ds.Tables[0].Rows[0]["kindName"]?.ToString() ?? kindName) : kindName;
            return Redirect($"/?tab={targetTab}&msg=" + Uri.EscapeDataString($"工項類型「{savedCode} - {savedName}」已成功儲存！"));
        }
        catch (Exception ex)
        {
            await logPortalExceptionAsync("portalController.saveWorkItemKind", ex);
            return Redirect($"/?tab={targetTab}&err=" + Uri.EscapeDataString("儲存工項類型失敗（已記錄至系統 Log）：" + ex.Message));
        }
    }

    [HttpPost("workitem/kind/delete")]
    [Authorize]
    public async Task<IActionResult> deleteWorkItemKind(
        [FromForm] int? workItemKindPk,
        [FromForm] string? kindCode = null,
        [FromForm] string? fallbackKindCode = "project",
        [FromForm] string? returnTab = "workitems")
    {
        string targetTab = string.IsNullOrWhiteSpace(returnTab) ? "workitems" : returnTab.Trim();
        if (!isAdminUser())
        {
            return Redirect($"/?tab={targetTab}&err=" + Uri.EscapeDataString("僅管理者可刪除工項類型。"));
        }

        try
        {
            var ds = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spDeleteWorkItemKind", new Dictionary<string, object?>
            {
                ["workItemKindPk"] = workItemKindPk,
                ["kindCode"] = string.IsNullOrWhiteSpace(kindCode) ? null : kindCode.Trim(),
                ["fallbackKindCode"] = string.IsNullOrWhiteSpace(fallbackKindCode) ? "project" : fallbackKindCode.Trim(),
                ["operatorAccount"] = getCurrentLoginAccount()
            });

            string delCode = (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0) ? (ds.Tables[0].Rows[0]["deletedKindCode"]?.ToString() ?? "") : "";
            string delName = (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0) ? (ds.Tables[0].Rows[0]["deletedKindName"]?.ToString() ?? "") : "";
            return Redirect($"/?tab={targetTab}&msg=" + Uri.EscapeDataString($"🗑️ 已成功刪除工項類型「{delCode} - {delName}」！"));
        }
        catch (Exception ex)
        {
            await logPortalExceptionAsync("portalController.deleteWorkItemKind", ex);
            return Redirect($"/?tab={targetTab}&err=" + Uri.EscapeDataString("刪除工項類型失敗（已記錄至系統 Log）：" + ex.Message));
        }
    }

    [HttpPost("workitem/type/save")]
    [Authorize]
    public async Task<IActionResult> saveWorkItemType(
        [FromForm] int? workItemTypePk,
        [FromForm] string typeCode,
        [FromForm] string typeName,
        [FromForm] string categoryName,
        [FromForm] string? defaultWorkItemKind = "project",
        [FromForm] string? description = null,
        [FromForm] decimal? standardStoryPoint = null,
        [FromForm] bool syncExistingOpenWorkItem = false)
    {
        if (!isAdminUser())
        {
            return Redirect("/?tab=types&err=" + Uri.EscapeDataString("僅管理者可新增或修改工項代表類型。"));
        }

        try
        {
            string cleanKind = string.IsNullOrWhiteSpace(defaultWorkItemKind) ? "project" : defaultWorkItemKind.Trim();

            await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spUpsertWorkItemType", new Dictionary<string, object?>
            {
                ["workItemTypePk"] = workItemTypePk,
                ["typeCode"] = (typeCode ?? "").Trim(),
                ["typeName"] = (typeName ?? "").Trim(),
                ["categoryName"] = string.IsNullOrWhiteSpace(categoryName) ? "一般分類" : categoryName.Trim(),
                ["defaultWorkItemKind"] = cleanKind,
                ["description"] = description,
                ["standardStoryPoint"] = standardStoryPoint,
                ["syncExistingOpenWorkItem"] = syncExistingOpenWorkItem,
                ["changedByEmployeePk"] = getCurrentEmployeePk(),
                ["operatorAccount"] = getCurrentLoginAccount()
            });

            return Redirect("/?tab=types&msg=" + Uri.EscapeDataString($"工項代表類型「{typeCode} - {typeName}」已成功儲存！"));
        }
        catch (Exception ex)
        {
            await logPortalExceptionAsync("portalController.saveWorkItemType", ex);
            return Redirect("/?tab=types&err=" + Uri.EscapeDataString("儲存工項代表類型失敗（已記錄至系統 Log）：" + ex.Message));
        }
    }

    [HttpPost("workitem/type/delete")]
    [Authorize]
    public async Task<IActionResult> deleteWorkItemType(
        [FromForm] int? workItemTypePk,
        [FromForm] string? typeCode = null)
    {
        if (!isAdminUser())
        {
            return Redirect("/?tab=types&err=" + Uri.EscapeDataString("僅管理者可刪除工項代表類型。"));
        }

        try
        {
            var ds = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spDeleteWorkItemType", new Dictionary<string, object?>
            {
                ["workItemTypePk"] = workItemTypePk,
                ["typeCode"] = string.IsNullOrWhiteSpace(typeCode) ? null : typeCode.Trim(),
                ["operatorAccount"] = getCurrentLoginAccount()
            });

            string delCode = (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0) ? (ds.Tables[0].Rows[0]["deletedTypeCode"]?.ToString() ?? "") : "";
            string delName = (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0) ? (ds.Tables[0].Rows[0]["deletedTypeName"]?.ToString() ?? "") : "";
            return Redirect("/?tab=types&msg=" + Uri.EscapeDataString($"🗑️ 已成功刪除工項代表類型「{delCode} - {delName}」！"));
        }
        catch (Exception ex)
        {
            await logPortalExceptionAsync("portalController.deleteWorkItemType", ex);
            return Redirect("/?tab=types&err=" + Uri.EscapeDataString("刪除工項代表類型失敗（已記錄至系統 Log）：" + ex.Message));
        }
    }

    [HttpPost("workitem/save")]
    [Authorize]
    public async Task<IActionResult> saveWorkItem(
        [FromForm] int? workItemPk,
        [FromForm] string itemCode,
        [FromForm] string itemTitle,
        [FromForm] string? itemDescription,
        [FromForm] int? workItemTypePk,
        [FromForm] string? workItemKind = "project",
        [FromForm] DateTime? startDate = null,
        [FromForm] DateTime? endDate = null,
        [FromForm] string? sprintName = null,
        [FromForm] string priorityLevel = "medium",
        [FromForm] decimal? storyPoint = null,
        [FromForm] bool forceManualAdjust = false,
        [FromForm] string? adjustmentReason = null,
        [FromForm] string executionStatus = "todo",
        [FromForm] DateTime? dueDate = null,
        [FromForm] int? primaryEmployeePk = null,
        [FromForm] decimal? primaryAllocationPercent = null)
    {
        if (!isAdminUser())
        {
            return Redirect("/?tab=workitems&err=" + Uri.EscapeDataString("僅管理者可新增或修改工項及點數。"));
        }

        try
        {
            string cleanKind = string.IsNullOrWhiteSpace(workItemKind) ? "project" : workItemKind.Trim();

            DateTime resolvedStart = startDate ?? DateTime.Now;
            DateTime resolvedEnd = endDate ?? dueDate ?? resolvedStart.AddDays(14);
            if (resolvedEnd < resolvedStart)
            {
                resolvedEnd = resolvedStart;
            }

            var ds = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spUpsertWorkItem", new Dictionary<string, object?>
            {
                ["workItemPk"] = workItemPk,
                ["itemCode"] = (itemCode ?? "").Trim(),
                ["itemTitle"] = (itemTitle ?? "").Trim(),
                ["itemDescription"] = itemDescription,
                ["workItemTypePk"] = (workItemTypePk.HasValue && workItemTypePk.Value > 0) ? workItemTypePk.Value : null,
                ["workItemKind"] = cleanKind,
                ["startDate"] = resolvedStart,
                ["endDate"] = resolvedEnd,
                ["sprintName"] = sprintName,
                ["priorityLevel"] = string.IsNullOrWhiteSpace(priorityLevel) ? "medium" : priorityLevel,
                ["storyPoint"] = storyPoint,
                ["forceManualAdjust"] = forceManualAdjust,
                ["adjustmentReason"] = adjustmentReason,
                ["executionStatus"] = string.IsNullOrWhiteSpace(executionStatus) ? "todo" : executionStatus,
                ["dueDate"] = resolvedEnd.Date,
                ["createdByEmployeePk"] = getCurrentEmployeePk(),
                ["operatorAccount"] = getCurrentLoginAccount()
            });

            int savedWiPk = (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0)
                ? Convert.ToInt32(ds.Tables[0].Rows[0]["pk"])
                : (workItemPk ?? 0);
            string kindText = (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0 && ds.Tables[0].Columns.Contains("workItemKindText"))
                ? (ds.Tables[0].Rows[0]["workItemKindText"]?.ToString() ?? cleanKind)
                : cleanKind;

            if (primaryEmployeePk.HasValue && primaryEmployeePk.Value > 0 && savedWiPk > 0)
            {
                await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spAssignWorkItemToEmployee", new Dictionary<string, object?>
                {
                    ["actionType"] = "upsert",
                    ["workItemPk"] = savedWiPk,
                    ["employeePk"] = primaryEmployeePk.Value,
                    ["assignmentRole"] = "主要負責",
                    ["allocationPercent"] = primaryAllocationPercent ?? 100.00m,
                    ["assignmentNote"] = "建立/修改工項時直接指派",
                    ["assignedByEmployeePk"] = getCurrentEmployeePk(),
                    ["operatorAccount"] = getCurrentLoginAccount()
                });
            }

            // 儲存後立即檢核：若該工項（或其他工項）已達到迄時 (endDate)，自動轉換狀態為已完成、扣掉該 Story Point，並寄發 Email 給部門管理層與 HR
            var expiredResult = await _emailService.processExpiredWorkItemsAndNotifyAsync(null, getCurrentLoginAccount());
            if (expiredResult.processedCount > 0)
            {
                var expDetail = string.Join("；", expiredResult.itemSummaryList);
                return Redirect("/?tab=workitems&msg=" + Uri.EscapeDataString($"工項「{itemCode} - {itemTitle}」已儲存！⏰ 因達到迄時，系統已自動轉換狀態、扣減 Story Point 並寄信給部門管理層與 HR：{expDetail}"));
            }

            return Redirect("/?tab=workitems&msg=" + Uri.EscapeDataString($"工項「{itemCode} - {itemTitle}」（類型：{kindText}｜起訖：{resolvedStart:yyyy-MM-dd HH:mm} ~ {resolvedEnd:yyyy-MM-dd HH:mm}）已成功儲存，並已自動計算員工與部門 Story Point！"));
        }
        catch (Exception ex)
        {
            await logPortalExceptionAsync("portalController.saveWorkItem", ex);
            return Redirect("/?tab=workitems&err=" + Uri.EscapeDataString("儲存工項失敗（已記錄至系統 Log）：" + ex.Message));
        }
    }

    [HttpPost("workitem/delete")]
    [Authorize]
    public async Task<IActionResult> deleteWorkItem(
        [FromForm] int? workItemPk,
        [FromForm] string? itemCode = null)
    {
        if (!isAdminUser())
        {
            return Redirect("/?tab=workitems&err=" + Uri.EscapeDataString("僅管理者可刪除實際工項。"));
        }

        try
        {
            var ds = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spDeleteWorkItem", new Dictionary<string, object?>
            {
                ["workItemPk"] = workItemPk,
                ["itemCode"] = string.IsNullOrWhiteSpace(itemCode) ? null : itemCode.Trim(),
                ["operatorAccount"] = getCurrentLoginAccount()
            });

            string delCode = (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0) ? (ds.Tables[0].Rows[0]["deletedItemCode"]?.ToString() ?? "") : "";
            string delTitle = (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0) ? (ds.Tables[0].Rows[0]["deletedItemTitle"]?.ToString() ?? "") : "";
            return Redirect("/?tab=workitems&msg=" + Uri.EscapeDataString($"🗑️ 已成功刪除實際工項「{delCode} - {delTitle}」，並同步扣除重算員工與部門 Story Point！"));
        }
        catch (Exception ex)
        {
            await logPortalExceptionAsync("portalController.deleteWorkItem", ex);
            return Redirect("/?tab=workitems&err=" + Uri.EscapeDataString("刪除實際工項失敗（已記錄至系統 Log）：" + ex.Message));
        }
    }

    /// <summary>
    /// 手動或排程觸發：檢核所有已達到迄時 (endDate) 的工項，自動轉換狀態為「已完成」、扣掉該 Story Point，並透過 Email 寄信給部門管理層與 HR
    /// </summary>
    [HttpPost("workitem/checkExpired")]
    [Authorize]
    public async Task<IActionResult> checkExpiredWorkItemsNow([FromForm] int? workItemPk = null)
    {
        if (!isAdminUser())
        {
            return Redirect("/?tab=workitems&err=" + Uri.EscapeDataString("僅管理者可手動觸發到期工項檢核與通知。"));
        }

        try
        {
            var results = await _emailService.processExpiredWorkItemsAndNotifyAsync(workItemPk, getCurrentLoginAccount());
            if (results.processedCount == 0)
            {
                return Redirect("/?tab=workitems&msg=" + Uri.EscapeDataString("✅ 已完成到期工項檢核：目前沒有新的已達迄時待處理工項（所有到期工項皆已完成轉態、扣點與寄信）。"));
            }

            string summary = string.Join("；", results.itemSummaryList);

            return Redirect("/?tab=workitems&msg=" + Uri.EscapeDataString($"⏰ 已成功處理 {results.processedCount} 筆到期工項並寄發 Email 通知：{summary}"));
        }
        catch (Exception ex)
        {
            await logPortalExceptionAsync("portalController.checkExpiredWorkItemsNow", ex);
            return Redirect("/?tab=workitems&err=" + Uri.EscapeDataString("執行到期工項檢核時發生異常：" + ex.Message));
        }
    }

    [HttpPost("workitem/assign")]
    [Authorize]
    public async Task<IActionResult> assignWorkItem(
        [FromForm] string actionType,
        [FromForm] int workItemPk,
        [FromForm] int employeePk,
        [FromForm] string? assignmentRole,
        [FromForm] decimal? allocationPercent,
        [FromForm] string? assignmentNote)
    {
        if (!isAdminUser())
        {
            return Redirect("/?tab=workitems&err=" + Uri.EscapeDataString("僅管理者可修改員工賦予的工項。"));
        }

        try
        {
            await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spAssignWorkItemToEmployee", new Dictionary<string, object?>
            {
                ["actionType"] = string.IsNullOrWhiteSpace(actionType) ? "upsert" : actionType,
                ["workItemPk"] = workItemPk,
                ["employeePk"] = employeePk,
                ["assignmentRole"] = string.IsNullOrWhiteSpace(assignmentRole) ? "主要負責" : assignmentRole,
                ["allocationPercent"] = allocationPercent ?? 100.00m,
                ["assignmentNote"] = assignmentNote,
                ["assignedByEmployeePk"] = getCurrentEmployeePk(),
                ["operatorAccount"] = getCurrentLoginAccount()
            });

            return Redirect("/?tab=workitems&msg=" + Uri.EscapeDataString("已成功更新員工賦予的工項與 Story Point 分攤配額！"));
        }
        catch (Exception ex)
        {
            await logPortalExceptionAsync("portalController.assignWorkItem", ex);
            return Redirect("/?tab=workitems&err=" + Uri.EscapeDataString("更新員工指派失敗（已記錄至系統 Log）：" + ex.Message));
        }
    }

    // =========================================================================
    // 5. 部門與員工帳密管理端點 (僅管理者可修改)
    // =========================================================================

    [HttpPost("employee/department/save")]
    [Authorize]
    public async Task<IActionResult> saveDepartment(
        [FromForm] int? departmentPk,
        [FromForm] string departmentCode,
        [FromForm] string departmentName,
        [FromForm] string? managerName,
        [FromForm] string? departmentNote)
    {
        if (!isAdminUser())
        {
            return Redirect("/?tab=employees&err=" + Uri.EscapeDataString("僅管理者可新增或修改部門資料。"));
        }

        try
        {
            await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spUpsertDepartment", new Dictionary<string, object?>
            {
                ["departmentPk"] = departmentPk,
                ["departmentCode"] = (departmentCode ?? "").Trim(),
                ["departmentName"] = (departmentName ?? "").Trim(),
                ["managerName"] = managerName,
                ["departmentNote"] = departmentNote,
                ["isActive"] = true,
                ["operatorAccount"] = getCurrentLoginAccount()
            });

            return Redirect("/?tab=employees&msg=" + Uri.EscapeDataString($"部門「{departmentCode} - {departmentName}」儲存成功！"));
        }
        catch (Exception ex)
        {
            await logPortalExceptionAsync("portalController.saveDepartment", ex);
            return Redirect("/?tab=employees&err=" + Uri.EscapeDataString("儲存部門失敗（已記錄至系統 Log）：" + ex.Message));
        }
    }

    /// <summary>
    /// 當員工修改自己的登入帳號或資料後，立即同步刷新當前瀏覽器的登入憑證 Cookie，確保無須重新登入即可繼續操作
    /// </summary>
    private async Task refreshCurrentAuthCookieAsync(DataRow row)
    {
        var claims = new List<Claim>
        {
            new Claim("employeePk", row["employeePk"].ToString() ?? getCurrentEmployeePk().ToString()),
            new Claim("employeeCode", row["employeeCode"].ToString() ?? (User.FindFirstValue("employeeCode") ?? "")),
            new Claim(ClaimTypes.NameIdentifier, row["loginAccount"].ToString() ?? getCurrentLoginAccount()),
            new Claim(ClaimTypes.Name, row["employeeName"].ToString() ?? getCurrentEmployeeName()),
            new Claim(ClaimTypes.Role, row["roleName"].ToString() ?? getCurrentRoleName()),
            new Claim("departmentPk", row["departmentPk"].ToString() ?? (User.FindFirstValue("departmentPk") ?? "1")),
            new Claim("departmentName", row["departmentName"].ToString() ?? getCurrentDepartmentName()),
            new Claim("jobTitle", row["jobTitle"].ToString() ?? (User.FindFirstValue("jobTitle") ?? "")),
            new Claim("authBootToken", sqlDataService.serverBootAuthToken)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = true
            });
    }

    [HttpPost("account/updateSelf")]
    [HttpPost("employee/updateSelf")]
    [Authorize]
    public async Task<IActionResult> updateSelfCredential(
        [FromForm] string? newLoginAccount,
        [FromForm] string? loginAccount,
        [FromForm] string? newPlainPassword,
        [FromForm] string? plainPassword,
        [FromForm] string? confirmPlainPassword,
        [FromForm] string? currentPlainPassword,
        [FromForm] string? returnTab = "myAccount")
    {
        string targetTab = string.IsNullOrWhiteSpace(returnTab) ? "myAccount" : returnTab.Trim();
        int selfEmpPk = getCurrentEmployeePk();
        string oldAcc = getCurrentLoginAccount();

        if (selfEmpPk <= 0)
        {
            return Redirect("/login?err=" + Uri.EscapeDataString("請先登入系統後再修改個人帳號與密碼。"));
        }

        try
        {
            string resolvedNewAcc = !string.IsNullOrWhiteSpace(newLoginAccount)
                ? newLoginAccount.Trim()
                : (!string.IsNullOrWhiteSpace(loginAccount) ? loginAccount.Trim() : oldAcc);

            string resolvedNewPwd = !string.IsNullOrWhiteSpace(newPlainPassword)
                ? newPlainPassword.Trim()
                : (!string.IsNullOrWhiteSpace(plainPassword) ? plainPassword.Trim() : "");

            string resolvedConfirmPwd = (confirmPlainPassword ?? "").Trim();

            if (string.IsNullOrWhiteSpace(resolvedNewAcc))
            {
                return Redirect($"/?tab={targetTab}&err=" + Uri.EscapeDataString("個人登入帳號不可為空白，請輸入有效的登入帳號。"));
            }

            if (!string.IsNullOrWhiteSpace(resolvedConfirmPwd) && resolvedNewPwd != resolvedConfirmPwd)
            {
                return Redirect($"/?tab={targetTab}&err=" + Uri.EscapeDataString("兩次輸入的新登入密碼不一致，請重新確認！"));
            }

            if (string.Equals(resolvedNewAcc, oldAcc, StringComparison.Ordinal) && string.IsNullOrWhiteSpace(resolvedNewPwd))
            {
                return Redirect($"/?tab={targetTab}&err=" + Uri.EscapeDataString("請輸入欲變更的新登入帳號，或輸入新的登入密碼！"));
            }

            string? newPwdHash = string.IsNullOrWhiteSpace(resolvedNewPwd)
                ? null
                : sqlDataService.computeSha256(resolvedNewPwd);

            string? curPwdHash = string.IsNullOrWhiteSpace(currentPlainPassword)
                ? null
                : sqlDataService.computeSha256(currentPlainPassword.Trim());

            var ds = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spUpdateEmployeeSelfCredential", new Dictionary<string, object?>
            {
                ["employeePk"] = selfEmpPk,
                ["newLoginAccount"] = resolvedNewAcc,
                ["newPasswordHash"] = newPwdHash,
                ["currentPasswordHash"] = curPwdHash,
                ["operatorAccount"] = oldAcc
            });

            if (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0)
            {
                await refreshCurrentAuthCookieAsync(ds.Tables[0].Rows[0]);
            }

            string changeDetail = (!string.Equals(resolvedNewAcc, oldAcc, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(resolvedNewPwd))
                ? $"登入帳號已更新為「{resolvedNewAcc}」，且登入密碼已同步更新"
                : (!string.Equals(resolvedNewAcc, oldAcc, StringComparison.Ordinal)
                    ? $"登入帳號已成功更新為「{resolvedNewAcc}」（原密碼維持不變）"
                    : $"帳號「{resolvedNewAcc}」的登入密碼已成功更新");

            return Redirect($"/?tab={targetTab}&msg=" + Uri.EscapeDataString($"✅ 個人帳密修改成功！{changeDetail}。下次登入請使用最新帳密。"));
        }
        catch (Exception ex)
        {
            await logPortalExceptionAsync("portalController.updateSelfCredential", ex);
            return Redirect($"/?tab={targetTab}&err=" + Uri.EscapeDataString("修改個人帳號密碼失敗：" + ex.Message));
        }
    }

    [HttpPost("employee/save")]
    [Authorize]
    public async Task<IActionResult> saveEmployee(
        [FromForm] int? employeePk,
        [FromForm] string employeeCode,
        [FromForm] string employeeName,
        [FromForm] string loginAccount,
        [FromForm] string? plainPassword,
        [FromForm] string emailAddress,
        [FromForm] string jobTitle,
        [FromForm] int departmentPk,
        [FromForm] bool isDepartmentManager = false,
        [FromForm] int? managerEmployeePk = null,
        [FromForm] string roleName = "participant",
        [FromForm] bool isActive = true)
    {
        if (!isAdminUser())
        {
            // 一般參與者若呼叫 /employee/save，自動導向修改其本人的登入帳號與密碼，確保每個人都可以修改自己的帳/密
            return await updateSelfCredential(
                newLoginAccount: loginAccount,
                loginAccount: loginAccount,
                newPlainPassword: plainPassword,
                plainPassword: plainPassword,
                confirmPlainPassword: null,
                currentPlainPassword: null,
                returnTab: "myAccount"
            );
        }

        try
        {
            string? pwdHash = string.IsNullOrWhiteSpace(plainPassword)
                ? null
                : sqlDataService.computeSha256(plainPassword.Trim());

            int? cleanMgrPk = (managerEmployeePk.HasValue && managerEmployeePk.Value > 0)
                ? managerEmployeePk.Value
                : null;

            var ds = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spUpsertEmployee", new Dictionary<string, object?>
            {
                ["employeePk"] = employeePk,
                ["employeeCode"] = (employeeCode ?? "").Trim(),
                ["employeeName"] = (employeeName ?? "").Trim(),
                ["loginAccount"] = (loginAccount ?? "").Trim(),
                ["passwordHash"] = pwdHash,
                ["emailAddress"] = (emailAddress ?? "").Trim(),
                ["jobTitle"] = (jobTitle ?? "").Trim(),
                ["departmentPk"] = departmentPk,
                ["isDepartmentManager"] = isDepartmentManager,
                ["managerEmployeePk"] = cleanMgrPk,
                ["roleName"] = roleName == "admin" ? "admin" : "participant",
                ["isActive"] = isActive,
                ["operatorAccount"] = getCurrentLoginAccount()
            });

            if (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0)
            {
                DataRow savedRow = ds.Tables[0].Rows[0];
                if (Convert.ToInt32(savedRow["employeePk"]) == getCurrentEmployeePk())
                {
                    await refreshCurrentAuthCookieAsync(savedRow);
                }
            }

            string mgrLabel = isDepartmentManager ? "（已設為部門主管）" : "";
            return Redirect("/?tab=employees&msg=" + Uri.EscapeDataString($"員工「{employeeCode} - {employeeName} ({loginAccount})」{mgrLabel}資料、樹狀主管階層與帳密儲存成功！"));
        }
        catch (Exception ex)
        {
            await logPortalExceptionAsync("portalController.saveEmployee", ex);
            return Redirect("/?tab=employees&err=" + Uri.EscapeDataString("儲存員工資料失敗（已記錄至系統 Log）：" + ex.Message));
        }
    }

    [HttpPost("employee/setActive")]
    [Authorize]
    public async Task<IActionResult> setEmployeeActiveStatus(
        [FromForm] int employeePk,
        [FromForm] bool isActive)
    {
        if (!isAdminUser())
        {
            return Redirect("/?tab=employees&err=" + Uri.EscapeDataString("僅管理者可停用或啟用員工帳號。"));
        }

        if (!isActive && employeePk == getCurrentEmployeePk())
        {
            return Redirect("/?tab=employees&err=" + Uri.EscapeDataString("不可停用目前正在登入操作中的管理者本人帳號。"));
        }

        try
        {
            var ds = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spSetEmployeeActiveStatus", new Dictionary<string, object?>
            {
                ["employeePk"] = employeePk,
                ["isActive"] = isActive,
                ["operatorAccount"] = getCurrentLoginAccount()
            });

            string empName = ds.Tables[0].Rows.Count > 0 ? (ds.Tables[0].Rows[0]["employeeName"]?.ToString() ?? "") : "";
            string statusText = isActive ? "已恢復啟用" : "已停用（禁止登入）";
            return Redirect("/?tab=employees&msg=" + Uri.EscapeDataString($"員工帳號「{empName}」{statusText}！"));
        }
        catch (Exception ex)
        {
            await logPortalExceptionAsync("portalController.setEmployeeActiveStatus", ex);
            return Redirect("/?tab=employees&err=" + Uri.EscapeDataString("變更員工狀態失敗（已記錄至系統 Log）：" + ex.Message));
        }
    }

    [HttpPost("employee/delete")]
    [Authorize]
    public async Task<IActionResult> deleteEmployee([FromForm] int employeePk)
    {
        if (!isAdminUser())
        {
            return Redirect("/?tab=employees&err=" + Uri.EscapeDataString("僅管理者可刪除員工帳號。"));
        }

        if (employeePk == getCurrentEmployeePk())
        {
            return Redirect("/?tab=employees&err=" + Uri.EscapeDataString("不可刪除目前正在登入操作中的管理者本人帳號。"));
        }

        try
        {
            var ds = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spDeleteEmployee", new Dictionary<string, object?>
            {
                ["employeePk"] = employeePk,
                ["operatorAccount"] = getCurrentLoginAccount()
            });

            string empName = ds.Tables[0].Rows.Count > 0 ? (ds.Tables[0].Rows[0]["deletedEmployeeName"]?.ToString() ?? "") : "";
            return Redirect("/?tab=employees&msg=" + Uri.EscapeDataString($"已成功刪除員工帳號「{empName}」！"));
        }
        catch (Exception ex)
        {
            await logPortalExceptionAsync("portalController.deleteEmployee", ex);
            return Redirect("/?tab=employees&err=" + Uri.EscapeDataString(ex.Message));
        }
    }

    [HttpGet("employee/import/template")]
    [Authorize]
    public IActionResult downloadEmployeeImportTemplate([FromQuery] string format = "csv")
    {
        string[] headers = { "員工編號", "員工姓名", "登入帳號", "登入密碼", "電子郵件", "職稱", "所屬部門", "系統角色", "是否啟用", "是否為部門主管(1/0)", "上層主管(員工編號或帳號或姓名)" };
        var rows = new List<string[]>
        {
            new[] { "EMP009", "林志豪 (Leo)", "leo.lin", "pass123", "leo.lin@company.com", "部門經理", "開發", "admin", "1", "1", "byron.hu" },
            new[] { "EMP010", "許雅筑 (Chloe)", "chloe.hsu", "pass123", "chloe.hsu@company.com", "資深工程師", "開發", "participant", "1", "0", "leo.lin" },
            new[] { "EMP011", "鄭家銘 (Mark)", "mark.cheng", "admin123", "mark.cheng@company.com", "部門經理 / 敏捷教練", "BI", "admin", "1", "1", "byron.hu" }
        };

        if (string.Equals(format, "excel", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
        {
            byte[] xlsxBytes = generateExcelXlsxBytes("員工批次匯入範本", headers, rows);
            return File(xlsxBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "employeeBatchImportTemplate.xlsx");
        }

        byte[] csvBytes = generateCsvBytes(headers, rows);
        return File(csvBytes, "text/csv; charset=utf-8", "employeeBatchImportTemplate.csv");
    }

    [HttpPost("employee/import")]
    [Authorize]
    public async Task<IActionResult> batchImportEmployees(
        [FromForm] IFormFile? csvFile,
        [FromForm] IFormFile? importFile,
        [FromForm] string? importFormat,
        [FromForm] string? csvRawText,
        [FromForm] int defaultDepartmentPk = 1,
        [FromForm] string defaultPassword = "pass123")
    {
        if (!isAdminUser())
        {
            return Redirect("/?tab=employees&err=" + Uri.EscapeDataString("僅管理者可執行批次載入員工資料。"));
        }

        IFormFile? activeFile = importFile ?? csvFile;
        var parsedRows = await parseUploadedRowsAsync(activeFile, csvRawText);

        if (parsedRows.Count == 0)
        {
            return Redirect("/?tab=employees&err=" + Uri.EscapeDataString("請選擇要上傳的 CSV 或 Excel (.xlsx) 檔案，或在文字框中貼上要批次載入的員工資料。"));
        }

        try
        {
            // 先載入現有部門與員工對照表 (支援部門代碼或部門名稱自動對應，以及上層主管樹狀遞迴關聯對應)
            var dashDs = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spGetStoryPointDashboard");
            var deptMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (DataRow dRow in dashDs.Tables[0].Rows)
            {
                int dPk = Convert.ToInt32(dRow["departmentPk"]);
                string dCode = dRow["departmentCode"]?.ToString()?.Trim() ?? "";
                string dName = dRow["departmentName"]?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(dCode)) deptMap[dCode] = dPk;
                if (!string.IsNullOrEmpty(dName)) deptMap[dName] = dPk;
            }

            var empLookupMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (dashDs.Tables.Count > 1)
            {
                foreach (DataRow eRow in dashDs.Tables[1].Rows)
                {
                    int ePk = Convert.ToInt32(eRow["employeePk"]);
                    string eCode = eRow["employeeCode"]?.ToString()?.Trim() ?? "";
                    string eAcc = eRow["loginAccount"]?.ToString()?.Trim() ?? "";
                    string eName = eRow["employeeName"]?.ToString()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(eCode)) empLookupMap[eCode] = ePk;
                    if (!string.IsNullOrEmpty(eAcc)) empLookupMap[eAcc] = ePk;
                    if (!string.IsNullOrEmpty(eName)) empLookupMap[eName] = ePk;
                }
            }

            int importedCount = 0;

            foreach (string[] cols in parsedRows)
            {
                if (cols.Length < 2) continue;

                string firstCol = cols[0].Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(firstCol) || firstCol.StartsWith("#")) continue;

                // 略過標題列
                if (firstCol.Equals("employeeCode", StringComparison.OrdinalIgnoreCase) ||
                    firstCol.Contains("員工編號") ||
                    firstCol.Equals("編號", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string empCode = firstCol;
                string empName = cols.Length > 1 && !string.IsNullOrWhiteSpace(cols[1]) ? cols[1].Trim().Trim('"') : empCode;
                string loginAcc = cols.Length > 2 && !string.IsNullOrWhiteSpace(cols[2]) ? cols[2].Trim().Trim('"') : empCode.ToLowerInvariant();
                string rawPwd = cols.Length > 3 && !string.IsNullOrWhiteSpace(cols[3]) ? cols[3].Trim().Trim('"') : defaultPassword;
                string email = cols.Length > 4 && !string.IsNullOrWhiteSpace(cols[4]) ? cols[4].Trim().Trim('"') : $"{loginAcc}@company.com";
                string jobTitle = cols.Length > 5 && !string.IsNullOrWhiteSpace(cols[5]) ? cols[5].Trim().Trim('"') : "工程師";
                string deptToken = cols.Length > 6 ? cols[6].Trim().Trim('"') : "";
                string roleToken = cols.Length > 7 ? cols[7].Trim().Trim('"').ToLowerInvariant() : "participant";
                string activeToken = cols.Length > 8 ? cols[8].Trim().Trim('"').ToLowerInvariant() : "1";
                string isDeptMgrToken = cols.Length > 9 ? cols[9].Trim().Trim('"').ToLowerInvariant() : "";
                string mgrToken = cols.Length > 10 ? cols[10].Trim().Trim('"') : "";

                int resolvedDeptPk = defaultDepartmentPk > 0 ? defaultDepartmentPk : 1;
                if (!string.IsNullOrWhiteSpace(deptToken))
                {
                    if (deptMap.TryGetValue(deptToken, out int foundDeptPk))
                    {
                        resolvedDeptPk = foundDeptPk;
                    }
                    else if (int.TryParse(deptToken, out int numericDeptPk) && numericDeptPk > 0)
                    {
                        resolvedDeptPk = numericDeptPk;
                    }
                    else
                    {
                        // 自動建立新部門
                        string newDeptCode = "DEPT_" + Math.Abs(deptToken.GetHashCode() % 10000).ToString("D4");
                        var newDeptDs = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spUpsertDepartment", new Dictionary<string, object?>
                        {
                            ["departmentCode"] = newDeptCode,
                            ["departmentName"] = deptToken,
                            ["managerName"] = "待指派",
                            ["departmentNote"] = "由員工批次載入自動建立",
                            ["isActive"] = true,
                            ["operatorAccount"] = getCurrentLoginAccount()
                        });
                        if (newDeptDs.Tables.Count > 0 && newDeptDs.Tables[0].Rows.Count > 0)
                        {
                            resolvedDeptPk = Convert.ToInt32(newDeptDs.Tables[0].Rows[0]["pk"]);
                            deptMap[deptToken] = resolvedDeptPk;
                            deptMap[newDeptCode] = resolvedDeptPk;
                        }
                    }
                }

                string resolvedRole = (roleToken == "admin" || roleToken.Contains("管理")) ? "admin" : "participant";
                bool resolvedActive = !(activeToken == "0" || activeToken == "false" || activeToken.Contains("停用") || activeToken == "no");
                bool resolvedIsDeptMgr = !string.IsNullOrWhiteSpace(isDeptMgrToken)
                    ? (isDeptMgrToken == "1" || isDeptMgrToken == "true" || isDeptMgrToken == "yes" || isDeptMgrToken == "是" || isDeptMgrToken.Contains("主管"))
                    : jobTitle.Contains("部門經理");

                int? resolvedMgrPk = null;
                if (!string.IsNullOrWhiteSpace(mgrToken))
                {
                    if (empLookupMap.TryGetValue(mgrToken, out int foundMgrPk))
                    {
                        resolvedMgrPk = foundMgrPk;
                    }
                    else if (int.TryParse(mgrToken, out int numericMgrPk) && numericMgrPk > 0)
                    {
                        resolvedMgrPk = numericMgrPk;
                    }
                }

                string? pwdHash = string.IsNullOrWhiteSpace(rawPwd) ? null : sqlDataService.computeSha256(rawPwd);

                var savedEmpDs = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spUpsertEmployee", new Dictionary<string, object?>
                {
                    ["employeeCode"] = empCode,
                    ["employeeName"] = empName,
                    ["loginAccount"] = loginAcc,
                    ["passwordHash"] = pwdHash,
                    ["emailAddress"] = email,
                    ["jobTitle"] = jobTitle,
                    ["departmentPk"] = resolvedDeptPk,
                    ["isDepartmentManager"] = resolvedIsDeptMgr,
                    ["managerEmployeePk"] = resolvedMgrPk,
                    ["roleName"] = resolvedRole,
                    ["isActive"] = resolvedActive,
                    ["operatorAccount"] = getCurrentLoginAccount()
                });
                if (savedEmpDs.Tables.Count > 0 && savedEmpDs.Tables[0].Rows.Count > 0)
                {
                    int savedPk = Convert.ToInt32(savedEmpDs.Tables[0].Rows[0]["employeePk"]);
                    empLookupMap[empCode] = savedPk;
                    empLookupMap[loginAcc] = savedPk;
                    empLookupMap[empName] = savedPk;
                }
                importedCount++;
            }

            string fmtLabel = (activeFile != null && activeFile.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) ||
                              string.Equals(importFormat, "excel", StringComparison.OrdinalIgnoreCase)
                ? "Excel"
                : "CSV";
            return Redirect("/?tab=employees&msg=" + Uri.EscapeDataString($"已透過 {fmtLabel} 格式成功批次載入並更新 {importedCount} 筆員工資料、部門主管註記與樹狀階層帳密！"));
        }
        catch (Exception ex)
        {
            await logPortalExceptionAsync("portalController.batchImportEmployees", ex);
            return Redirect("/?tab=employees&err=" + Uri.EscapeDataString("批次載入員工失敗（已記錄至系統 Log）：" + ex.Message));
        }
    }

    [HttpGet("workitem/import/template")]
    [Authorize]
    public IActionResult downloadWorkItemImportTemplate([FromQuery] string format = "csv")
    {
        string[] headers = { "工項代碼", "工項標題", "工項說明", "代表類型代碼或名稱", "StoryPoint", "所屬Sprint", "執行狀態", "負責員工編號或帳號", "分攤比例", "工項類型(專案/維運/顧問時數/教育訓練/開發產品)", "開始時間(起時)", "結束時間(迄時)" };
        var rows = new List<string[]>
        {
            new[] { "WI-2026-101", "實作部門月度報表批次匯出 API", "支援 CSV、Excel 與互動式 HTML 報表下載", "TYPE_API_CRUD", "5", "2026-Q4 Sprint 2", "inProgress", "EMP003", "100", "專案", "2026-10-01 09:00", "2026-11-15 18:00" },
            new[] { "WI-2026-102", "核心資料庫例行維運與備份監控", "整合資料庫健康巡檢與異常告警", "TYPE_DB_TUNING", "3", "2026-Q4 Sprint 2", "inProgress", "EMP002", "100", "維運", "2026-10-01 09:00", "2026-12-31 18:00" },
            new[] { "WI-2026-103", "企業客戶技術架構顧問時數支援", "提供高併發架構與效能調校顧問服務", "TYPE_SECURITY_AUTH", "8", "2026-Q4 Sprint 2", "todo", "EMP004", "100", "顧問時數", "2026-10-15 09:00", "2026-11-30 18:00" },
            new[] { "WI-2026-104", "內部技術傳承與新人教育訓練課程", "規劃並講授資料庫架構與安全實務訓練", "TYPE_TRAINING", "3", "2026-Q4 Sprint 2", "todo", "EMP005", "100", "教育訓練", "2026-10-20 09:00", "2026-11-20 18:00" },
            new[] { "WI-2026-105", "自有產品核心模組研發與版本迭代", "開發產品自動化檢測引擎與儀表板", "TYPE_PRODUCT_DEV", "8", "2026-Q4 Sprint 2", "inProgress", "EMP006", "100", "開發產品", "2026-10-01 09:00", "2026-12-15 18:00" }
        };

        if (string.Equals(format, "excel", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
        {
            byte[] xlsxBytes = generateExcelXlsxBytes("工項批次匯入範本", headers, rows);
            return File(xlsxBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "workItemBatchImportTemplate.xlsx");
        }

        byte[] csvBytes = generateCsvBytes(headers, rows);
        return File(csvBytes, "text/csv; charset=utf-8", "workItemBatchImportTemplate.csv");
    }

    [HttpPost("workitem/import")]
    [Authorize]
    public async Task<IActionResult> batchImportWorkItems(
        [FromForm] IFormFile? importFile,
        [FromForm] string? importFormat,
        [FromForm] string? rawText)
    {
        if (!isAdminUser())
        {
            return Redirect("/?tab=workitems&err=" + Uri.EscapeDataString("僅管理者可執行批次匯入工項資料。"));
        }

        var parsedRows = await parseUploadedRowsAsync(importFile, rawText);
        if (parsedRows.Count == 0)
        {
            return Redirect("/?tab=workitems&err=" + Uri.EscapeDataString("請選擇要上傳的 CSV 或 Excel (.xlsx) 檔案，或在文字框中貼上要批次匯入的工項資料。"));
        }

        try
        {
            var dashDs = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spGetStoryPointDashboard", new Dictionary<string, object?>
            {
                ["viewerEmployeePk"] = getCurrentEmployeePk(),
                ["isViewerAdmin"] = true
            });

            var typeMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (DataRow tRow in dashDs.Tables[2].Rows)
            {
                int tPk = Convert.ToInt32(tRow["workItemTypePk"]);
                string tCode = tRow["typeCode"]?.ToString()?.Trim() ?? "";
                string tName = tRow["typeName"]?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(tCode)) typeMap[tCode] = tPk;
                if (!string.IsNullOrEmpty(tName)) typeMap[tName] = tPk;
            }

            var empMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (DataRow eRow in dashDs.Tables[1].Rows)
            {
                int ePk = Convert.ToInt32(eRow["employeePk"]);
                string eCode = eRow["employeeCode"]?.ToString()?.Trim() ?? "";
                string eAcc = eRow["loginAccount"]?.ToString()?.Trim() ?? "";
                string eName = eRow["employeeName"]?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(eCode)) empMap[eCode] = ePk;
                if (!string.IsNullOrEmpty(eAcc)) empMap[eAcc] = ePk;
                if (!string.IsNullOrEmpty(eName)) empMap[eName] = ePk;
            }

            int importedCount = 0;
            foreach (string[] cols in parsedRows)
            {
                if (cols.Length < 2) continue;
                string firstCol = cols[0].Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(firstCol) || firstCol.StartsWith("#")) continue;

                if (firstCol.Equals("itemCode", StringComparison.OrdinalIgnoreCase) ||
                    firstCol.Contains("工項代碼") ||
                    firstCol.Equals("代碼", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string itemCode = firstCol;
                string itemTitle = cols.Length > 1 && !string.IsNullOrWhiteSpace(cols[1]) ? cols[1].Trim().Trim('"') : itemCode;
                string itemDesc = cols.Length > 2 ? cols[2].Trim().Trim('"') : "";
                string typeToken = cols.Length > 3 ? cols[3].Trim().Trim('"') : "";
                string ptToken = cols.Length > 4 ? cols[4].Trim().Trim('"') : "";
                string sprintName = cols.Length > 5 && !string.IsNullOrWhiteSpace(cols[5]) ? cols[5].Trim().Trim('"') : "2026-Q4 Sprint 2";
                string statusToken = cols.Length > 6 ? cols[6].Trim().Trim('"') : "todo";
                string empToken = cols.Length > 7 ? cols[7].Trim().Trim('"') : "";
                string allocToken = cols.Length > 8 ? cols[8].Trim().Trim('"') : "100";
                string kindToken = cols.Length > 9 ? cols[9].Trim().Trim('"') : "";
                string startToken = cols.Length > 10 ? cols[10].Trim().Trim('"') : "";
                string endToken = cols.Length > 11 ? cols[11].Trim().Trim('"') : "";

                int? resolvedTypePk = null;
                if (!string.IsNullOrWhiteSpace(typeToken) && typeMap.TryGetValue(typeToken, out int foundTypePk))
                {
                    resolvedTypePk = foundTypePk;
                }

                decimal? parsedPt = null;
                if (decimal.TryParse(ptToken.Replace("pt", "", StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal ptVal))
                {
                    parsedPt = ptVal;
                }

                string cleanStatus = "todo";
                if (statusToken.Equals("done", StringComparison.OrdinalIgnoreCase) || statusToken.Contains("完成"))
                {
                    cleanStatus = "done";
                }
                else if (statusToken.Equals("inProgress", StringComparison.OrdinalIgnoreCase) || statusToken.Contains("進行"))
                {
                    cleanStatus = "inProgress";
                }

                string cleanKind = string.IsNullOrWhiteSpace(kindToken) ? "project" : kindToken;
                if (kindToken.Equals("maintenance", StringComparison.OrdinalIgnoreCase) || kindToken.Contains("維運"))
                {
                    cleanKind = "maintenance";
                }
                else if (kindToken.Equals("consultingHour", StringComparison.OrdinalIgnoreCase) || kindToken.Contains("顧問"))
                {
                    cleanKind = "consultingHour";
                }
                else if (kindToken.Equals("training", StringComparison.OrdinalIgnoreCase) || kindToken.Contains("教育") || kindToken.Contains("訓練"))
                {
                    cleanKind = "training";
                }
                else if (kindToken.Equals("productDevelopment", StringComparison.OrdinalIgnoreCase) || kindToken.Equals("productDev", StringComparison.OrdinalIgnoreCase) || kindToken.Contains("開發產品") || kindToken.Contains("產品開發"))
                {
                    cleanKind = "productDevelopment";
                }
                else if (kindToken.Equals("project", StringComparison.OrdinalIgnoreCase) || kindToken.Contains("專案"))
                {
                    cleanKind = "project";
                }

                DateTime? parsedStart = DateTime.TryParse(startToken, out DateTime dtStart) ? dtStart : null;
                DateTime? parsedEnd = DateTime.TryParse(endToken, out DateTime dtEnd) ? dtEnd : null;

                var wiDs = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spUpsertWorkItem", new Dictionary<string, object?>
                {
                    ["workItemPk"] = null,
                    ["itemCode"] = itemCode,
                    ["itemTitle"] = itemTitle,
                    ["itemDescription"] = itemDesc,
                    ["workItemTypePk"] = resolvedTypePk,
                    ["workItemKind"] = cleanKind,
                    ["startDate"] = parsedStart,
                    ["endDate"] = parsedEnd,
                    ["sprintName"] = sprintName,
                    ["priorityLevel"] = "medium",
                    ["storyPoint"] = parsedPt,
                    ["forceManualAdjust"] = parsedPt.HasValue && !resolvedTypePk.HasValue,
                    ["adjustmentReason"] = "由批次匯入建立或更新",
                    ["executionStatus"] = cleanStatus,
                    ["dueDate"] = parsedEnd?.Date,
                    ["createdByEmployeePk"] = getCurrentEmployeePk(),
                    ["operatorAccount"] = getCurrentLoginAccount()
                });

                if (!string.IsNullOrWhiteSpace(empToken) &&
                    empMap.TryGetValue(empToken, out int targetEmpPk) &&
                    wiDs.Tables.Count > 0 && wiDs.Tables[0].Rows.Count > 0)
                {
                    int savedWiPk = Convert.ToInt32(wiDs.Tables[0].Rows[0]["pk"]);
                    decimal allocPct = decimal.TryParse(allocToken.Replace("%", "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal aVal) ? aVal : 100m;
                    await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spAssignWorkItemToEmployee", new Dictionary<string, object?>
                    {
                        ["actionType"] = "upsert",
                        ["workItemPk"] = savedWiPk,
                        ["employeePk"] = targetEmpPk,
                        ["assignmentRole"] = "主要負責",
                        ["allocationPercent"] = allocPct,
                        ["assignmentNote"] = "批次匯入工項自動指派",
                        ["assignedByEmployeePk"] = getCurrentEmployeePk(),
                        ["operatorAccount"] = getCurrentLoginAccount()
                    });
                }

                importedCount++;
            }

            // 匯入後立即檢核是否有已達到迄時之工項，自動轉態扣點並寄信
            var expiredList = await _emailService.processExpiredWorkItemsAndNotifyAsync(null, getCurrentLoginAccount());

            string fmtLabel = (importFile != null && importFile.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) ||
                              string.Equals(importFormat, "excel", StringComparison.OrdinalIgnoreCase)
                ? "Excel"
                : "CSV";
            string expSuffix = expiredList.processedCount > 0 ? $"（其中 {expiredList.processedCount} 筆已達迄時，已自動轉為已完成、扣除點數並寄信通知部門管理層與 HR）" : "";
            return Redirect("/?tab=workitems&msg=" + Uri.EscapeDataString($"已透過 {fmtLabel} 格式成功批次匯入並更新 {importedCount} 筆工項與 Story Point 資料！{expSuffix}"));
        }
        catch (Exception ex)
        {
            await logPortalExceptionAsync("portalController.batchImportWorkItems", ex);
            return Redirect("/?tab=workitems&err=" + Uri.EscapeDataString("批次匯入工項失敗（已記錄至系統 Log）：" + ex.Message));
        }
    }

    // =========================================================================
    // 5-1. 系統防崩潰保護與管理者錯誤 Log 模擬測試端點
    // =========================================================================

    [HttpPost("admin/log/testError")]
    [Authorize]
    public async Task<IActionResult> simulateTestErrorLog(
        [FromForm] string? testScenario = "controllerException",
        [FromForm] string? customNote = null)
    {
        if (!isAdminUser())
        {
            return Redirect("/?tab=voting&err=" + Uri.EscapeDataString("僅管理者可執行錯誤攔截與 Log 測試。"));
        }

        string scenario = (testScenario ?? "controllerException").Trim();
        string note = string.IsNullOrWhiteSpace(customNote) ? "管理者手動觸發防崩潰與錯誤 Log 攔截測試" : customNote.Trim();

        try
        {
            if (string.Equals(scenario, "middlewareException", StringComparison.OrdinalIgnoreCase))
            {
                // 模擬未預期拋出至全域 Middleware 的嚴重例外，驗證全域防崩潰保護不會造成 HTTP 500 當機
                throw new InvalidOperationException($"[全域防崩潰測試] 模擬未預期執行期例外（{note}）");
            }
            if (string.Equals(scenario, "sqlValidationError", StringComparison.OrdinalIgnoreCase))
            {
                // 模擬呼叫 Stored Procedure 觸發業務規則攔截與 SQL CATCH 記錄
                await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spCastVote", new Dictionary<string, object?>
                {
                    ["votingSessionPk"] = -999,
                    ["employeePk"] = getCurrentEmployeePk(),
                    ["cardValue"] = "999",
                    ["voteComment"] = note,
                    ["operatorAccount"] = getCurrentLoginAccount()
                });
            }

            // 預設：模擬 Controller 內部捕捉算術或資料轉型例外並寫入雙軌 Log
            throw new ApplicationException($"[Controller 錯誤攔截測試] 模擬商業邏輯異常：{note}（系統自動捕捉並保持穩定運行，絕不當機）");
        }
        catch (Exception ex) when (!string.Equals(scenario, "middlewareException", StringComparison.OrdinalIgnoreCase))
        {
            await logPortalExceptionAsync($"portalController.simulateTestErrorLog({scenario})", ex);
            return Redirect("/?tab=logs&msg=" + Uri.EscapeDataString($"✅ 防崩潰驗證成功！系統已安全攔截例外且未當機，並已將錯誤細節寫入 Log：{ex.Message}"));
        }
    }

    // =========================================================================
    // 6. 報表多格式匯出端點 (/export/report 與 /export/csv：支援 CSV、Excel、互動式 HTML)
    // =========================================================================

    [HttpGet("export/csv")]
    [HttpGet("export/report")]
    [Authorize]
    public async Task<IActionResult> exportReport([FromQuery] string type = "employee", [FromQuery] string format = "csv")
    {
        bool isAdmin = isAdminUser();
        int currentEmpPk = getCurrentEmployeePk();

        if (!isAdmin && (string.Equals(type, "department", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(type, "log", StringComparison.OrdinalIgnoreCase)))
        {
            return Redirect("/?tab=stats&err=" + Uri.EscapeDataString("個別員工僅可匯出自己的工項與分數，不可匯出部門報表或系統管理 Log。"));
        }

        try
        {
            var dashDs = await _sqlService.executeStoredProcedureDataSetAsync("storyPoint.spGetStoryPointDashboard", new Dictionary<string, object?>
            {
                ["viewerEmployeePk"] = currentEmpPk,
                ["isViewerAdmin"] = isAdmin
            });

            string reportTitle;
            string[] headers;
            var rows = new List<string[]>();
            int defaultGroupColIndex = 0;
            int primaryPointColIndex = 5;

            if (string.Equals(type, "log", StringComparison.OrdinalIgnoreCase))
            {
                reportTitle = "系統執行與錯誤異常 Log 稽核報表 (tbLog)";
                headers = new[] { "Log編號", "發生時間", "嚴重層級", "來源模組", "執行物件或端點", "耗時(ms)", "執行結果", "錯誤碼", "錯誤行號", "錯誤訊息與異常細節", "操作帳號" };
                defaultGroupColIndex = 2; // 預設依「嚴重層級」群組彙總
                primaryPointColIndex = 5; // 數值彙總欄位：耗時(ms)

                DataTable logDt = dashDs.Tables.Count > 7 ? dashDs.Tables[7] : _sqlService.getFallbackLogTable();
                foreach (DataRow r in logDt.Rows)
                {
                    rows.Add(new[]
                    {
                        r["logPk"]?.ToString() ?? "",
                        r["logTime"] != DBNull.Value ? Convert.ToDateTime(r["logTime"]).ToString("yyyy-MM-dd HH:mm:ss") : "",
                        r["logLevelText"]?.ToString() ?? "",
                        r["sourceName"]?.ToString() ?? "",
                        r["objectName"]?.ToString() ?? "",
                        r["durationMillisecond"]?.ToString() ?? "0",
                        r["executionResult"]?.ToString() ?? "",
                        r["errorCode"]?.ToString() ?? "",
                        r["errorLine"]?.ToString() ?? "",
                        r["errorMessage"]?.ToString() ?? "",
                        r["operatorAccount"]?.ToString() ?? ""
                    });
                }
            }
            else if (string.Equals(type, "department", StringComparison.OrdinalIgnoreCase))
            {
                reportTitle = "各部門總 Story Point 與負載統計報表";
                headers = new[] { "部門代碼", "部門名稱", "部門主管", "在職人數", "參與工項數", "部門總StoryPoint", "到期已扣除StoryPoint", "待處理點數", "進行中點數", "已完成點數", "人均StoryPoint" };
                defaultGroupColIndex = 1;
                primaryPointColIndex = 5;
                foreach (DataRow r in dashDs.Tables[0].Rows)
                {
                    rows.Add(new[]
                    {
                        r["departmentCode"]?.ToString() ?? "",
                        r["departmentName"]?.ToString() ?? "",
                        r["managerName"]?.ToString() ?? "",
                        r["activeEmployeeCount"]?.ToString() ?? "0",
                        r["distinctWorkItemCount"]?.ToString() ?? "0",
                        r["totalDepartmentStoryPoint"]?.ToString() ?? "0",
                        r.Table.Columns.Contains("totalDeductedDepartmentStoryPoint") ? (r["totalDeductedDepartmentStoryPoint"]?.ToString() ?? "0") : "0",
                        r["todoDepartmentStoryPoint"]?.ToString() ?? "0",
                        r["inProgressDepartmentStoryPoint"]?.ToString() ?? "0",
                        r["doneDepartmentStoryPoint"]?.ToString() ?? "0",
                        r["averageStoryPointPerEmployee"]?.ToString() ?? "0"
                    });
                }
            }
            else if (string.Equals(type, "workitem", StringComparison.OrdinalIgnoreCase))
            {
                reportTitle = isAdmin ? "各工項 Story Point 與員工賦予明細報表" : "我的工項與個人 Story Point 配額報表";
                headers = new[] { "工項代碼", "工項標題", "工項類型", "開始時間(起時)", "結束時間(迄時)", "代表類型代碼", "代表類型名稱", "代表類型預設點數", "工項目前StoryPoint", "原核定StoryPoint", "到期已扣除點數", "點數來源", "執行狀態", "到期通知信箱", "所屬Sprint", "負責員工與配額明細" };
                defaultGroupColIndex = 2; // 預設依「工項類型」群組彙總
                primaryPointColIndex = 8;
                foreach (DataRow r in dashDs.Tables[3].Rows)
                {
                    string startStr = r.Table.Columns.Contains("startDate") && r["startDate"] != DBNull.Value
                        ? Convert.ToDateTime(r["startDate"]).ToString("yyyy-MM-dd HH:mm")
                        : "";
                    string endStr = r.Table.Columns.Contains("endDate") && r["endDate"] != DBNull.Value
                        ? Convert.ToDateTime(r["endDate"]).ToString("yyyy-MM-dd HH:mm")
                        : "";
                    rows.Add(new[]
                    {
                        r["itemCode"]?.ToString() ?? "",
                        r["itemTitle"]?.ToString() ?? "",
                        r.Table.Columns.Contains("workItemKindText") ? (r["workItemKindText"]?.ToString() ?? "專案") : "專案",
                        startStr,
                        endStr,
                        r["typeCode"]?.ToString() ?? "",
                        r["typeName"]?.ToString() ?? "未分類",
                        r["defaultTypePoint"]?.ToString() ?? "0",
                        r["storyPoint"]?.ToString() ?? "0",
                        r.Table.Columns.Contains("originalStoryPoint") ? (r["originalStoryPoint"]?.ToString() ?? "0") : (r["storyPoint"]?.ToString() ?? "0"),
                        r.Table.Columns.Contains("deductedStoryPoint") ? (r["deductedStoryPoint"]?.ToString() ?? "0") : "0",
                        r["pointSourceText"]?.ToString() ?? "",
                        r["executionStatusText"]?.ToString() ?? "",
                        r.Table.Columns.Contains("expiryNotificationRecipient") ? (r["expiryNotificationRecipient"]?.ToString() ?? "") : "",
                        r["sprintName"]?.ToString() ?? "",
                        (r["assigneeSummary"]?.ToString() ?? "").Replace(",", "；")
                    });
                }
            }
            else if (string.Equals(type, "history", StringComparison.OrdinalIgnoreCase))
            {
                reportTitle = "Story Point 訂定與異動稽核歷程報表";
                headers = new[] { "異動時間", "標的類別", "標的名稱", "原點數", "新StoryPoint", "異動來源", "原因說明", "操作人員" };
                defaultGroupColIndex = 5; // 預設依「異動來源」群組彙總
                primaryPointColIndex = 4;
                foreach (DataRow r in dashDs.Tables[5].Rows)
                {
                    string targetName = r["targetKind"]?.ToString() == "workItemType"
                        ? $"[代表類型] {r["typeCode"]} - {r["typeName"]}"
                        : $"[工項] {r["itemCode"]} - {r["itemTitle"]}";
                    rows.Add(new[]
                    {
                        r["changedAt"] != DBNull.Value ? Convert.ToDateTime(r["changedAt"]).ToString("yyyy-MM-dd HH:mm:ss") : "",
                        r["targetKindText"]?.ToString() ?? "",
                        targetName,
                        r["oldStoryPoint"]?.ToString() ?? "0",
                        r["newStoryPoint"]?.ToString() ?? "0",
                        r["changeSourceText"]?.ToString() ?? "",
                        r["changeReason"]?.ToString() ?? "",
                        r["changedByEmployeeName"]?.ToString() ?? ""
                    });
                }
            }
            else
            {
                reportTitle = isAdmin ? "各員工身上的工項、部門主管註記、樹狀遞迴階層與總 Story Point 統計報表" : "我的個人工項與總 Story Point 分數統計報表";
                headers = new[] { "員工編號", "員工姓名", "登入帳號", "所屬部門", "職稱", "是否為部門主管", "上層直屬主管", "該同仁的部門主管", "人資主管", "樹狀遞迴階層路徑", "系統角色", "賦予工項數", "員工總StoryPoint", "待處理點數", "進行中點數", "已完成點數", "賦予工項清單" };
                defaultGroupColIndex = 3; // 預設依「所屬部門」群組彙總
                primaryPointColIndex = 12;
                foreach (DataRow r in dashDs.Tables[1].Rows)
                {
                    bool isMgr = r.Table.Columns.Contains("isDepartmentManager") && r["isDepartmentManager"] != DBNull.Value && Convert.ToBoolean(r["isDepartmentManager"]);
                    string mgrName = r.Table.Columns.Contains("managerEmployeeName") ? (r["managerEmployeeName"]?.ToString() ?? "") : "";
                    string deptMgrName = r.Table.Columns.Contains("departmentManagerName") ? (r["departmentManagerName"]?.ToString() ?? "") : "";
                    string hrMgrName = r.Table.Columns.Contains("hrManagerName") ? (r["hrManagerName"]?.ToString() ?? "") : "";
                    string treePath = r.Table.Columns.Contains("hierarchyPath") ? (r["hierarchyPath"]?.ToString() ?? "") : "";
                    rows.Add(new[]
                    {
                        r["employeeCode"]?.ToString() ?? "",
                        r["employeeName"]?.ToString() ?? "",
                        r["loginAccount"]?.ToString() ?? "",
                        r["departmentName"]?.ToString() ?? "",
                        r["jobTitle"]?.ToString() ?? "",
                        isMgr ? "是 (部門主管)" : "否",
                        string.IsNullOrWhiteSpace(mgrName) ? "頂層 Root" : mgrName,
                        deptMgrName,
                        hrMgrName,
                        treePath,
                        r["roleDisplayName"]?.ToString() ?? "",
                        r["assignedItemCount"]?.ToString() ?? "0",
                        r["totalAllocatedStoryPoint"]?.ToString() ?? "0",
                        r["todoAllocatedStoryPoint"]?.ToString() ?? "0",
                        r["inProgressAllocatedStoryPoint"]?.ToString() ?? "0",
                        r["doneAllocatedStoryPoint"]?.ToString() ?? "0",
                        (r["workItemListSummary"]?.ToString() ?? "").Replace(",", " ")
                    });
                }
            }

            string ts = DateTime.Now.ToString("yyyyMMddHHmmss");
            string cleanFormat = (format ?? "csv").Trim().ToLowerInvariant();

            if (cleanFormat == "excel" || cleanFormat == "xlsx")
            {
                byte[] xlsxBytes = generateExcelXlsxBytes(reportTitle, headers, rows);
                return File(xlsxBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"storyPoint_{type}_{ts}.xlsx");
            }

            if (cleanFormat == "html")
            {
                string htmlReport = generateInteractiveHtmlReport(reportTitle, type, headers, rows, defaultGroupColIndex, primaryPointColIndex);
                byte[] htmlBytes = Encoding.UTF8.GetBytes(htmlReport);
                return File(htmlBytes, "text/html; charset=utf-8", $"storyPoint_{type}_{ts}.html");
            }

            byte[] csvBytes = generateCsvBytes(headers, rows);
            return File(csvBytes, "text/csv; charset=utf-8", $"storyPoint_{type}_{ts}.csv");
        }
        catch (Exception ex)
        {
            await logPortalExceptionAsync($"portalController.exportReport({type},{format})", ex);
            return Redirect("/?tab=stats&err=" + Uri.EscapeDataString("匯出報表時發生異常（已記錄至系統 Log）：" + ex.Message));
        }
    }

    // =========================================================================
    // 6-1. CSV / Excel (.xlsx) 解析與產生、互動式 HTML 報表 (排序、篩選、群組彙總) 輔助方法
    // =========================================================================

    private static byte[] generateCsvBytes(string[] headers, List<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(escapeCsvCell)));
        foreach (string[] row in rows)
        {
            sb.AppendLine(string.Join(",", row.Select(escapeCsvCell)));
        }

        byte[] preamble = Encoding.UTF8.GetPreamble();
        byte[] body = Encoding.UTF8.GetBytes(sb.ToString());
        byte[] fullBytes = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, fullBytes, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, fullBytes, preamble.Length, body.Length);
        return fullBytes;
    }

    private static string escapeCsvCell(string? val)
    {
        string s = val ?? "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
        {
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        return s;
    }

    private static async Task<List<string[]>> parseUploadedRowsAsync(IFormFile? uploadedFile, string? rawText)
    {
        var result = new List<string[]>();

        if (uploadedFile != null && uploadedFile.Length > 0)
        {
            string fileName = uploadedFile.FileName ?? "";
            using var ms = new MemoryStream();
            await uploadedFile.CopyToAsync(ms);
            byte[] fileBytes = ms.ToArray();

            // 檢查是否為標準 OpenXML Excel (.xlsx) ZIP 標頭 (PK\x03\x04)
            bool isZipXlsx = fileBytes.Length > 4 &&
                             fileBytes[0] == 0x50 &&
                             fileBytes[1] == 0x4B &&
                             fileBytes[2] == 0x03 &&
                             fileBytes[3] == 0x04;

            if (isZipXlsx || fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                ms.Position = 0;
                result.AddRange(parseExcelXlsxStream(ms));
            }
            else
            {
                string fileText = Encoding.UTF8.GetString(fileBytes).TrimStart('\uFEFF');
                if (fileText.Contains("<Workbook", StringComparison.OrdinalIgnoreCase) ||
                    fileText.Contains("<Worksheet", StringComparison.OrdinalIgnoreCase))
                {
                    result.AddRange(parseSpreadsheetXmlText(fileText));
                }
                else
                {
                    result.AddRange(parseDelimitedTextRows(fileText));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(rawText))
        {
            result.AddRange(parseDelimitedTextRows(rawText));
        }

        return result;
    }

    private static List<string[]> parseDelimitedTextRows(string text)
    {
        var rows = new List<string[]>();
        string[] lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

            if (line.Contains('\t') && !line.Contains(','))
            {
                rows.Add(line.Split('\t').Select(s => s.Trim().Trim('"')).ToArray());
            }
            else
            {
                rows.Add(parseCsvLine(line));
            }
        }
        return rows;
    }

    private static string[] parseCsvLine(string line)
    {
        var cols = new List<string>();
        var cur = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cur.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                cols.Add(cur.ToString().Trim());
                cur.Clear();
            }
            else
            {
                cur.Append(c);
            }
        }
        cols.Add(cur.ToString().Trim());
        return cols.ToArray();
    }

    private static List<string[]> parseSpreadsheetXmlText(string xmlText)
    {
        var rows = new List<string[]>();
        try
        {
            var doc = XDocument.Parse(xmlText);
            foreach (var rowEl in doc.Descendants().Where(e => e.Name.LocalName == "Row"))
            {
                var cells = rowEl.Elements().Where(e => e.Name.LocalName == "Cell")
                    .Select(c => c.Value.Trim())
                    .ToArray();
                if (cells.Length > 0 && cells.Any(v => !string.IsNullOrWhiteSpace(v)))
                {
                    rows.Add(cells);
                }
            }
        }
        catch
        {
            // Ignore malformed XML
        }
        return rows;
    }

    private static List<string[]> parseExcelXlsxStream(Stream stream)
    {
        var rows = new List<string[]>();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        // 1. 讀取 xl/sharedStrings.xml (若存在)
        var sharedStrings = new List<string>();
        var sstEntry = archive.Entries.FirstOrDefault(e =>
            e.FullName.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase));
        if (sstEntry != null)
        {
            using var sstStream = sstEntry.Open();
            var sstDoc = XDocument.Load(sstStream);
            foreach (var si in sstDoc.Descendants().Where(e => e.Name.LocalName == "si"))
            {
                string val = string.Concat(si.Descendants().Where(e => e.Name.LocalName == "t").Select(t => t.Value));
                sharedStrings.Add(val);
            }
        }

        // 2. 讀取第一個工作表 xl/worksheets/sheet1.xml
        var sheetEntry = archive.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) &&
                        e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (sheetEntry == null) return rows;

        using var sheetStream = sheetEntry.Open();
        var sheetDoc = XDocument.Load(sheetStream);
        foreach (var rowEl in sheetDoc.Descendants().Where(e => e.Name.LocalName == "row"))
        {
            var cellList = new List<string>();
            int expectedColIndex = 0;

            foreach (var cellEl in rowEl.Elements().Where(e => e.Name.LocalName == "c"))
            {
                string? cellRef = cellEl.Attribute("r")?.Value;
                if (!string.IsNullOrEmpty(cellRef))
                {
                    int colIdx = parseExcelColumnIndex(cellRef);
                    while (expectedColIndex < colIdx)
                    {
                        cellList.Add("");
                        expectedColIndex++;
                    }
                }

                string cellType = cellEl.Attribute("t")?.Value ?? "";
                string cellVal = "";

                if (cellType == "s")
                {
                    string vStr = cellEl.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? "";
                    if (int.TryParse(vStr, out int sstIdx) && sstIdx >= 0 && sstIdx < sharedStrings.Count)
                    {
                        cellVal = sharedStrings[sstIdx];
                    }
                }
                else if (cellType == "inlineStr")
                {
                    cellVal = string.Concat(cellEl.Descendants().Where(e => e.Name.LocalName == "t").Select(t => t.Value));
                }
                else
                {
                    cellVal = cellEl.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? "";
                }

                cellList.Add(cellVal.Trim());
                expectedColIndex++;
            }

            if (cellList.Count > 0 && cellList.Any(v => !string.IsNullOrWhiteSpace(v)))
            {
                rows.Add(cellList.ToArray());
            }
        }

        return rows;
    }

    private static int parseExcelColumnIndex(string cellRef)
    {
        int col = 0;
        foreach (char c in cellRef)
        {
            if (char.IsLetter(c))
            {
                col = col * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
            }
            else
            {
                break;
            }
        }
        return Math.Max(0, col - 1);
    }

    private static string getExcelColumnName(int colIndexZeroBased)
    {
        int dividend = colIndexZeroBased + 1;
        string colName = string.Empty;
        while (dividend > 0)
        {
            int modulo = (dividend - 1) % 26;
            colName = Convert.ToChar('A' + modulo) + colName;
            dividend = (dividend - modulo) / 26;
        }
        return colName;
    }

    private static byte[] generateExcelXlsxBytes(string sheetTitle, string[] headers, List<string[]> rows)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void writeEntry(string path, string xmlContent)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(xmlContent);
            }

            writeEntry("[Content_Types].xml", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
  <Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>
  <Default Extension=""xml"" ContentType=""application/xml""/>
  <Override PartName=""/xl/workbook.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml""/>
  <Override PartName=""/xl/worksheets/sheet1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml""/>
  <Override PartName=""/xl/styles.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml""/>
</Types>");

            writeEntry("_rels/.rels", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""xl/workbook.xml""/>
</Relationships>");

            string safeSheetName = new string((sheetTitle ?? "Report").Where(c => !"[]:*?/\\".Contains(c)).Take(28).ToArray());
            if (string.IsNullOrWhiteSpace(safeSheetName)) safeSheetName = "StoryPointReport";

            writeEntry("xl/workbook.xml", $@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<workbook xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"">
  <sheets>
    <sheet name=""{SecurityElement.Escape(safeSheetName)}"" sheetId=""1"" r:id=""rId1""/>
  </sheets>
</workbook>");

            writeEntry("xl/_rels/workbook.xml.rels", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"" Target=""worksheets/sheet1.xml""/>
  <Relationship Id=""rId2"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"" Target=""styles.xml""/>
</Relationships>");

            writeEntry("xl/styles.xml", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<styleSheet xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">
  <fonts count=""2"">
    <font><sz val=""11""/><name val=""Microsoft JhengHei""/></font>
    <font><b/><sz val=""11""/><color rgb=""FFFFFFFF""/><name val=""Microsoft JhengHei""/></font>
  </fonts>
  <fills count=""3"">
    <fill><patternFill patternType=""none""/></fill>
    <fill><patternFill patternType=""gray125""/></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FF1E3A8A""/><bgColor indexed=""64""/></patternFill></fill>
  </fills>
  <borders count=""1""><border><left/><right/><top/><bottom/><diagonal/></border></borders>
  <cellStyleXfs count=""1""><xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0""/></cellStyleXfs>
  <cellXfs count=""2"">
    <xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0"" xfId=""0""/>
    <xf numFmtId=""0"" fontId=""1"" fillId=""2"" borderId=""0"" xfId=""0"" applyFont=""1"" applyFill=""1""/>
  </cellXfs>
</styleSheet>");

            var sheetSb = new StringBuilder();
            sheetSb.Append(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<worksheet xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">
  <sheetData>");

            // 寫入第 1 列標題列
            sheetSb.Append(@"<row r=""1"">");
            for (int c = 0; c < headers.Length; c++)
            {
                string colRef = getExcelColumnName(c) + "1";
                string escaped = SecurityElement.Escape(headers[c] ?? "") ?? "";
                sheetSb.Append($@"<c r=""{colRef}"" s=""1"" t=""inlineStr""><is><t>{escaped}</t></is></c>");
            }
            sheetSb.Append("</row>");

            // 寫入資料列
            for (int rIdx = 0; rIdx < rows.Count; rIdx++)
            {
                int excelRowNum = rIdx + 2;
                sheetSb.Append($@"<row r=""{excelRowNum}"">");
                string[] row = rows[rIdx];
                for (int c = 0; c < row.Length; c++)
                {
                    string colRef = getExcelColumnName(c) + excelRowNum;
                    string val = row[c] ?? "";
                    // 判斷是否為純數值且非以 0 開頭的編號
                    bool isNumeric = decimal.TryParse(val, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out decimal numVal) &&
                                     !(val.Length > 1 && val.StartsWith("0") && !val.StartsWith("0."));
                    if (isNumeric)
                    {
                        sheetSb.Append($@"<c r=""{colRef}""><v>{numVal.ToString(CultureInfo.InvariantCulture)}</v></c>");
                    }
                    else
                    {
                        string escaped = SecurityElement.Escape(val) ?? "";
                        sheetSb.Append($@"<c r=""{colRef}"" t=""inlineStr""><is><t>{escaped}</t></is></c>");
                    }
                }
                sheetSb.Append("</row>");
            }

            sheetSb.Append(@"  </sheetData>
</worksheet>");
            writeEntry("xl/worksheets/sheet1.xml", sheetSb.ToString());
        }

        return ms.ToArray();
    }

    private static string generateInteractiveHtmlReport(
        string reportTitle,
        string reportType,
        string[] headers,
        List<string[]> rows,
        int defaultGroupColIndex,
        int primaryPointColIndex)
    {
        var headersJson = "[" + string.Join(",", headers.Select(x => "\"" + escapeJsString(x) + "\"")) + "]";
        var rowsJsonList = new List<string>();
        foreach (string[] r in rows)
        {
            rowsJsonList.Add("[" + string.Join(",", r.Select(x => "\"" + escapeJsString(x) + "\"")) + "]");
        }
        string rowsJson = "[" + string.Join(",", rowsJsonList) + "]";

        return $@"<!DOCTYPE html>
<html lang=""zh-Hant"">
<head>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />
    <title>{h(reportTitle)} - 互動式 HTML 分析報表 (排序・篩選・群組彙總)</title>
    <style>
        :root {{ --primary:#2563eb; --bg:#f8fafc; --card:#ffffff; --border:#e2e8f0; --text:#0f172a; --muted:#64748b; }}
        * {{ box-sizing:border-box; }}
        body {{ margin:0; font-family:'Microsoft JhengHei UI','Segoe UI',sans-serif; background:var(--bg); color:var(--text); padding:24px; }}
        .wrap {{ max-width:1440px; margin:0 auto; }}
        .header-banner {{ background:linear-gradient(135deg,#0f172a 0%,#1e3a8a 100%); color:#fff; padding:22px 26px; border-radius:14px; margin-bottom:20px; display:flex; justify-content:space-between; align-items:center; flex-wrap:wrap; gap:12px; box-shadow:0 10px 25px rgba(15,23,42,0.15); }}
        .header-banner h1 {{ margin:0; font-size:21px; }}
        .header-sub {{ font-size:13px; color:#bfdbfe; margin-top:4px; }}
        .kpi-row {{ display:grid; grid-template-columns:repeat(auto-fit,minmax(200px,1fr)); gap:14px; margin-bottom:18px; }}
        .kpi-box {{ background:#fff; border:1px solid var(--border); border-radius:12px; padding:14px 18px; box-shadow:0 1px 3px rgba(0,0,0,0.04); }}
        .kpi-lbl {{ font-size:12px; color:var(--muted); font-weight:700; }}
        .kpi-num {{ font-size:24px; font-weight:800; color:#1e3a8a; margin-top:4px; }}
        .toolbar-card {{ background:#fff; border:1px solid var(--border); border-radius:12px; padding:16px 20px; margin-bottom:18px; box-shadow:0 1px 3px rgba(0,0,0,0.04); }}
        .toolbar-grid {{ display:grid; grid-template-columns:repeat(auto-fit,minmax(180px,1fr)); gap:12px; align-items:end; }}
        .toolbar-grid > div {{ min-width:0; display:flex; flex-direction:column; justify-content:flex-end; }}
        label {{ display:flex; align-items:center; gap:5px; font-size:12.5px; font-weight:700; color:#334155; margin-bottom:5px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; line-height:1.35; min-height:20px; }}
        label[data-tip] {{ cursor:help; }}
        .sp-tip-icon {{ display:inline-flex; align-items:center; justify-content:center; width:15px; height:15px; border-radius:50%; background:#e2e8f0; color:#475569; font-size:10.5px; font-weight:700; flex-shrink:0; cursor:help; }}
        label[data-tip]:hover .sp-tip-icon {{ background:#2563eb; color:#fff; }}
        input, select {{ width:100%; padding:8px 11px; border:1px solid #cbd5e1; border-radius:8px; font-size:13.5px; font-family:inherit; }}
        .btn {{ display:inline-flex; align-items:center; justify-content:center; gap:6px; padding:8px 14px; border-radius:8px; font-size:13px; font-weight:700; cursor:pointer; border:1px solid #cbd5e1; background:#fff; color:#1e293b; }}
        .btn:hover {{ background:#f1f5f9; }}
        .btn-primary {{ background:#2563eb; color:#fff; border-color:#2563eb; }}
        .btn-primary:hover {{ background:#1d4ed8; }}
        .section-card {{ background:#fff; border:1px solid var(--border); border-radius:12px; padding:18px 20px; margin-bottom:20px; box-shadow:0 1px 3px rgba(0,0,0,0.04); }}
        .section-title {{ font-size:16px; font-weight:800; color:#0f172a; margin:0 0 12px 0; display:flex; justify-content:space-between; align-items:center; flex-wrap:wrap; gap:8px; border-bottom:1px solid #f1f5f9; padding-bottom:10px; }}
        .sp-table-toolbar {{ display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:8px; padding:7px 12px; background:#f8fafc; border:1px solid #e2e8f0; border-bottom:none; border-radius:8px 8px 0 0; font-size:12px; color:#475569; }}
        .sp-table-scroll-container {{ overflow-x:auto; max-height:72vh; overflow-y:auto; border:1px solid #e2e8f0; border-radius:0 0 8px 8px; position:relative; background:#fff; }}
        table {{ width:100%; border-collapse:separate; border-spacing:0; font-size:13px; }}
        th, td {{ padding:10px 12px; border-bottom:1px solid var(--border); text-align:left; vertical-align:middle; background:#ffffff; }}
        th {{ background:#f1f5f9; font-weight:800; color:#1e293b; user-select:none; white-space:nowrap; transition:background .15s; }}
        table.sp-sticky-header-enabled thead th {{ position:sticky; top:0; z-index:25; box-shadow:0 1px 0 #cbd5e1; }}
        th.th-sorted-asc, th.th-sorted-desc {{ background:#eff6ff !important; color:#1d4ed8 !important; }}
        th.sp-col-locked {{ background:#dbeafe !important; color:#1e3a8a !important; z-index:35 !important; }}
        td.sp-col-locked {{ background:#f0f7ff !important; z-index:15 !important; }}
        .sp-col-locked-last {{ border-right:2px solid #3b82f6 !important; box-shadow:3px 0 8px rgba(37,99,235,0.14); }}
        .sp-th-box {{ display:flex; align-items:center; justify-content:space-between; gap:6px; width:100%; }}
        .sp-th-sort-btn {{ display:inline-flex; align-items:center; gap:5px; cursor:pointer; flex:1; padding:2px 0; color:inherit; font-weight:800; }}
        .sp-th-sort-btn:hover {{ color:#2563eb; }}
        .sp-sort-indicator {{ display:inline-flex; align-items:center; justify-content:center; min-width:18px; height:18px; padding:0 4px; border-radius:4px; font-size:11px; color:#64748b; background:rgba(148,163,184,0.16); }}
        th.th-sorted-asc .sp-sort-indicator, th.th-sorted-desc .sp-sort-indicator {{ background:#2563eb; color:#ffffff; font-weight:800; }}
        .sp-th-lock-btn {{ display:inline-flex; align-items:center; justify-content:center; width:24px; height:22px; border-radius:5px; border:1px solid #cbd5e1; background:#ffffff; color:#64748b; font-size:11px; cursor:pointer; padding:0; transition:all .15s; flex-shrink:0; }}
        .sp-th-lock-btn:hover {{ border-color:#2563eb; color:#2563eb; background:#eff6ff; }}
        .sp-th-lock-btn.active {{ background:#1e40af; color:#ffffff; border-color:#1e3a8a; box-shadow:0 1px 3px rgba(30,64,175,0.3); }}
        .sp-toolbar-btn {{ display:inline-flex; align-items:center; gap:4px; padding:3px 9px; border-radius:6px; border:1px solid #cbd5e1; background:#ffffff; color:#334155; font-size:11.5px; font-weight:700; cursor:pointer; }}
        .sp-toolbar-btn:hover {{ background:#eff6ff; border-color:#93c5fd; color:#1d4ed8; }}
        .sp-toolbar-btn.active {{ background:#dbeafe; border-color:#3b82f6; color:#1e3a8a; }}
        tr:hover td {{ background:#f8fafc; }}
        tr:hover td.sp-col-locked {{ background:#e0f2fe !important; }}
        .group-row td {{ background:#eff6ff !important; font-weight:800; color:#1e3a8a; cursor:pointer; }}
        .group-row:hover td {{ background:#dbeafe !important; }}
        .badge {{ display:inline-block; padding:3px 9px; border-radius:999px; font-size:12px; font-weight:700; background:#dbeafe; color:#1d4ed8; }}
        .tfoot-row td {{ background:#0f172a !important; color:#fff; font-weight:800; border-bottom:none; }}
        @media print {{ .no-print {{ display:none !important; }} body {{ padding:0; background:#fff; }} .sp-table-scroll-container {{ max-height:none; overflow:visible; }} }}
    </style>
</head>
<body>
<div class=""wrap"">
    <div class=""header-banner"">
        <div>
            <h1>📊 {h(reportTitle)}</h1>
            <div class=""header-sub"">匯出時間：{DateTime.Now:yyyy-MM-dd HH:mm:ss} ｜ 支援點擊欄位排序、欄位鎖定與表頭列鎖定、多條件即時篩選、動態群組彙總分析</div>
        </div>
        <div class=""no-print"" style=""display:flex;gap:8px;"">
            <button type=""button"" class=""btn"" onclick=""resetAllFilters()"">🔄 重設全部篩選與鎖定</button>
            <button type=""button"" class=""btn btn-primary"" onclick=""window.print()"">🖨️ 列印 / 另存 PDF</button>
        </div>
    </div>

    <div class=""kpi-row"">
        <div class=""kpi-box"">
            <div class=""kpi-lbl"">📋 篩選後資料筆數 / 總筆數</div>
            <div class=""kpi-num"" id=""kpiRowCount"">0 / 0</div>
        </div>
        <div class=""kpi-box"">
            <div class=""kpi-lbl"">🎯 篩選後總 Story Point 合計</div>
            <div class=""kpi-num"" id=""kpiTotalPoints"">0 pt</div>
        </div>
        <div class=""kpi-box"">
            <div class=""kpi-lbl"">⚖️ 平均每筆 Story Point</div>
            <div class=""kpi-num"" id=""kpiAvgPoints"">0 pt</div>
        </div>
        <div class=""kpi-box"">
            <div class=""kpi-lbl"">📂 目前群組彙總組數</div>
            <div class=""kpi-num"" id=""kpiGroupCount"">0 組</div>
        </div>
    </div>

    <div class=""toolbar-card no-print"">
        <div class=""toolbar-grid"">
            <div>
                <label for=""kwInput"" data-tip=""輸入關鍵字、代碼、姓名或標題，即時搜尋所有欄位內容"" title=""輸入關鍵字、代碼、姓名或標題，即時搜尋所有欄位內容"">🔍 關鍵字篩選 <span class=""sp-tip-icon"">ⓘ</span></label>
                <input type=""text"" id=""kwInput"" oninput=""renderAll()"" placeholder=""輸入關鍵字、代碼、姓名或標題..."" />
            </div>
            <div>
                <label for=""filterColSelect"" data-tip=""選擇要進行精確值過濾的特定報表欄位"" title=""選擇要進行精確值過濾的特定報表欄位"">🏷️ 指定篩選欄位 <span class=""sp-tip-icon"">ⓘ</span></label>
                <select id=""filterColSelect"" onchange=""populateFilterValues()""></select>
            </div>
            <div>
                <label for=""filterValSelect"" data-tip=""選擇指定欄位的特定欄位值進行過濾"" title=""選擇指定欄位的特定欄位值進行過濾"">🎯 篩選欄位值 <span class=""sp-tip-icon"">ⓘ</span></label>
                <select id=""filterValSelect"" onchange=""renderAll()"">
                    <option value="""">全部資料</option>
                </select>
            </div>
            <div>
                <label for=""minPtInput"" data-tip=""設定 Story Point 點數或主要數值欄位的最小篩選下限"" title=""設定 Story Point 點數或主要數值欄位的最小篩選下限"">📉 最小點數下限 <span class=""sp-tip-icon"">ⓘ</span></label>
                <input type=""number"" step=""0.5"" id=""minPtInput"" oninput=""renderAll()"" placeholder=""不限下限"" />
            </div>
            <div>
                <label for=""maxPtInput"" data-tip=""設定 Story Point 點數或主要數值欄位的最大篩選上限"" title=""設定 Story Point 點數或主要數值欄位的最大篩選上限"">📈 最大點數上限 <span class=""sp-tip-icon"">ⓘ</span></label>
                <input type=""number"" step=""0.5"" id=""maxPtInput"" oninput=""renderAll()"" placeholder=""不限上限"" />
            </div>
            <div>
                <label for=""groupBySelect"" data-tip=""選擇動態群組彙總維度（Group By），自動計算各群組筆數、佔比與點數合計"" title=""選擇動態群組彙總維度（Group By），自動計算各群組筆數、佔比與點數合計"">📊 群組彙總維度 <span class=""sp-tip-icon"">ⓘ</span></label>
                <select id=""groupBySelect"" onchange=""renderAll()""></select>
            </div>
        </div>
    </div>

    <!-- 群組彙總統計表 -->
    <div class=""section-card"" id=""groupSummarySection"">
        <div class=""section-title"">
            <span>📑 群組彙總統計表 (點擊表頭可排序與鎖定欄位；點擊群組列可快速篩選該群組)</span>
            <span class=""badge"" id=""groupSummaryBadge"">依維度自動計算筆數、佔比、總點數、平均、最小與最大點數</span>
        </div>
        <div class=""sp-table-toolbar no-print"">
            <span id=""groupTableStatusText"">⇅ 點擊表頭文字可升冪/降冪排序 ｜ 📌 點擊表頭右側圖釘可鎖定(凍結)欄位</span>
            <div style=""display:flex;gap:6px;"">
                <button type=""button"" class=""sp-toolbar-btn active"" id=""btnGroupStickyHeader"" onclick=""toggleReportStickyHeader('groupSummaryTable', this)"">🔒 表頭列已鎖定 (Sticky)</button>
                <button type=""button"" class=""sp-toolbar-btn"" onclick=""resetGroupSortAndLocks()"">🔄 重設群組表排序與解鎖</button>
            </div>
        </div>
        <div class=""sp-table-scroll-container"">
            <table id=""groupSummaryTable"" class=""sp-sticky-header-enabled"">
                <thead>
                    <tr id=""groupHeaderRow""></tr>
                </thead>
                <tbody></tbody>
            </table>
        </div>
    </div>

    <!-- 互動式排序與分組明細表 -->
    <div class=""section-card"">
        <div class=""section-title"">
            <span>📋 報表明細資料表 (凡表頭皆支援點擊 ▲升冪 / ▼降冪 排序，以及 📌 鎖定欄位 / 表頭列鎖定)</span>
            <span class=""badge"" id=""sortStatusBadge"">預設排序</span>
        </div>
        <div class=""sp-table-toolbar no-print"">
            <span id=""detailTableStatusText"">⇅ 點擊表頭文字可升冪/降冪排序 ｜ 📌 點擊表頭右側圖釘可鎖定(凍結)欄位</span>
            <div style=""display:flex;gap:6px;"">
                <button type=""button"" class=""sp-toolbar-btn active"" id=""btnDetailStickyHeader"" onclick=""toggleReportStickyHeader('detailTable', this)"">🔒 表頭列已鎖定 (Sticky)</button>
                <button type=""button"" class=""sp-toolbar-btn"" onclick=""resetDetailSortAndLocks()"">🔄 重設明細表排序與解鎖</button>
            </div>
        </div>
        <div class=""sp-table-scroll-container"">
            <table id=""detailTable"" class=""sp-sticky-header-enabled"">
                <thead><tr id=""detailHeaderRow""></tr></thead>
                <tbody id=""detailTbody""></tbody>
                <tfoot id=""detailTfoot""></tfoot>
            </table>
        </div>
    </div>
</div>

<script>
    const HEADERS = {headersJson};
    const GROUP_HEADERS = ['群組名稱', '資料筆數', '筆數佔比', 'Story Point 合計', '點數佔比', '平均 Story Point', '最小點數', '最大點數'];
    const RAW_ROWS = {rowsJson};
    const DEFAULT_GROUP_COL = {defaultGroupColIndex};
    const PRIMARY_POINT_COL = {primaryPointColIndex};

    let sortColIndex = -1;
    let sortAsc = true;
    const lockedDetailCols = new Set();

    let groupSortColIndex = 3; // 預設依 Story Point 合計排序
    let groupSortAsc = false;
    const lockedGroupCols = new Set();

    const collapsedGroups = {{}};

    function parseNum(val) {{
        if (val === null || val === undefined) return NaN;
        const cleaned = String(val).replace(/,/g, '').replace(/pt/gi, '').replace(/筆/g, '').replace(/%/g, '').trim();
        if (!cleaned || cleaned === '-') return NaN;
        const m = cleaned.match(/^([+-]?\d+(?:\.\d+)?)/);
        if (m) return Number(m[1]);
        const n = Number(cleaned);
        return isNaN(n) ? NaN : n;
    }}

    function compareValues(va, vb, asc) {{
        const na = parseNum(va);
        const nb = parseNum(vb);
        let cmp = 0;
        if (!isNaN(na) && !isNaN(nb)) {{
            cmp = na - nb;
        }} else {{
            cmp = String(va ?? '').localeCompare(String(vb ?? ''), 'zh-Hant', {{ numeric: true, sensitivity: 'base' }});
        }}
        return asc ? cmp : -cmp;
    }}

    function escapeHtml(str) {{
        return String(str ?? '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/""/g, '&quot;');
    }}

    function initControls() {{
        const filterColSel = document.getElementById('filterColSelect');
        const groupBySel = document.getElementById('groupBySelect');

        filterColSel.innerHTML = '';
        groupBySel.innerHTML = '<option value=""-1"">-- 不分群組 (平面明細檢視) --</option>';

        HEADERS.forEach((h, idx) => {{
            const opt1 = document.createElement('option');
            opt1.value = String(idx);
            opt1.textContent = h;
            if (idx === DEFAULT_GROUP_COL) opt1.selected = true;
            filterColSel.appendChild(opt1);

            const opt2 = document.createElement('option');
            opt2.value = String(idx);
            opt2.textContent = '依【' + h + '】群組彙總';
            if (idx === DEFAULT_GROUP_COL) opt2.selected = true;
            groupBySel.appendChild(opt2);
        }});

        populateFilterValues();
        renderAll();
    }}

    function populateFilterValues() {{
        const colIdx = parseInt(document.getElementById('filterColSelect').value || '0', 10);
        const valSel = document.getElementById('filterValSelect');
        const uniqueVals = Array.from(new Set(RAW_ROWS.map(r => (r[colIdx] || '').trim()).filter(Boolean))).sort();
        valSel.innerHTML = '<option value="""">全部 (' + uniqueVals.length + ' 種)</option>';
        uniqueVals.forEach(v => {{
            const opt = document.createElement('option');
            opt.value = v;
            opt.textContent = v;
            valSel.appendChild(opt);
        }});
        renderAll();
    }}

    function resetAllFilters() {{
        document.getElementById('kwInput').value = '';
        document.getElementById('filterValSelect').value = '';
        document.getElementById('minPtInput').value = '';
        document.getElementById('maxPtInput').value = '';
        document.getElementById('groupBySelect').value = String(DEFAULT_GROUP_COL);
        sortColIndex = -1;
        sortAsc = true;
        lockedDetailCols.clear();
        groupSortColIndex = 3;
        groupSortAsc = false;
        lockedGroupCols.clear();
        renderAll();
    }}

    function resetGroupSortAndLocks() {{
        groupSortColIndex = 3;
        groupSortAsc = false;
        lockedGroupCols.clear();
        renderAll();
    }}

    function resetDetailSortAndLocks() {{
        sortColIndex = -1;
        sortAsc = true;
        lockedDetailCols.clear();
        renderAll();
    }}

    function toggleReportStickyHeader(tableId, btnEl) {{
        const tbl = document.getElementById(tableId);
        if (!tbl) return;
        const enabled = tbl.classList.toggle('sp-sticky-header-enabled');
        if (btnEl) {{
            btnEl.classList.toggle('active', enabled);
            btnEl.innerHTML = enabled ? '🔒 表頭列已鎖定 (Sticky)' : '🔓 表頭列未鎖定';
        }}
    }}

    function toggleSort(colIdx) {{
        if (sortColIndex === colIdx) {{
            if (sortAsc) {{
                sortAsc = false;
            }} else {{
                sortColIndex = -1;
                sortAsc = true;
            }}
        }} else {{
            sortColIndex = colIdx;
            sortAsc = true;
        }}
        renderAll();
    }}

    function toggleDetailColLock(evt, colIdx) {{
        evt.stopPropagation();
        if (lockedDetailCols.has(colIdx)) lockedDetailCols.delete(colIdx);
        else lockedDetailCols.add(colIdx);
        renderAll();
    }}

    function toggleGroupSort(colIdx) {{
        if (groupSortColIndex === colIdx) {{
            groupSortAsc = !groupSortAsc;
        }} else {{
            groupSortColIndex = colIdx;
            groupSortAsc = true;
        }}
        renderAll();
    }}

    function toggleGroupColLock(evt, colIdx) {{
        evt.stopPropagation();
        if (lockedGroupCols.has(colIdx)) lockedGroupCols.delete(colIdx);
        else lockedGroupCols.add(colIdx);
        renderAll();
    }}

    function applyLockedColumnPositions(tableId, lockedSet) {{
        const table = document.getElementById(tableId);
        if (!table) return;
        const sortedLocked = Array.from(lockedSet).sort((a, b) => a - b);
        const headerCells = table.querySelectorAll('thead tr th');
        headerCells.forEach(th => {{
            th.classList.remove('sp-col-locked', 'sp-col-locked-last');
            th.style.left = '';
        }});
        table.querySelectorAll('tbody tr td, tfoot tr td').forEach(td => {{
            td.classList.remove('sp-col-locked', 'sp-col-locked-last');
            td.style.left = '';
        }});
        if (sortedLocked.length === 0) return;

        let currentLeft = 0;
        sortedLocked.forEach((colIdx, idx) => {{
            const isLast = (idx === sortedLocked.length - 1);
            const th = headerCells[colIdx];
            if (!th) return;
            const width = th.offsetWidth || 100;
            th.classList.add('sp-col-locked');
            if (isLast) th.classList.add('sp-col-locked-last');
            th.style.left = currentLeft + 'px';

            table.querySelectorAll('tbody tr').forEach(tr => {{
                if (tr.classList.contains('group-row')) return;
                const td = tr.children[colIdx];
                if (td) {{
                    td.classList.add('sp-col-locked');
                    if (isLast) td.classList.add('sp-col-locked-last');
                    td.style.left = currentLeft + 'px';
                    td.style.position = 'sticky';
                }}
            }});
            currentLeft += width;
        }});
    }}

    function quickFilterGroup(groupKey) {{
        const groupCol = parseInt(document.getElementById('groupBySelect').value || '-1', 10);
        if (groupCol < 0) return;
        document.getElementById('filterColSelect').value = String(groupCol);
        populateFilterValues();
        document.getElementById('filterValSelect').value = groupKey;
        renderAll();
    }}

    function toggleGroupCollapse(groupKey) {{
        collapsedGroups[groupKey] = !collapsedGroups[groupKey];
        renderAll();
    }}

    function renderAll() {{
        const kw = (document.getElementById('kwInput').value || '').trim().toLowerCase();
        const filterCol = parseInt(document.getElementById('filterColSelect').value || '0', 10);
        const filterVal = document.getElementById('filterValSelect').value || '';
        const minPtRaw = document.getElementById('minPtInput').value;
        const maxPtRaw = document.getElementById('maxPtInput').value;
        const minPt = minPtRaw !== '' ? parseFloat(minPtRaw) : null;
        const maxPt = maxPtRaw !== '' ? parseFloat(maxPtRaw) : null;
        const groupCol = parseInt(document.getElementById('groupBySelect').value || '-1', 10);

        // 1. 篩選資料
        let filtered = RAW_ROWS.filter(r => {{
            if (kw) {{
                const joined = r.join(' ').toLowerCase();
                if (joined.indexOf(kw) === -1) return false;
            }}
            if (filterVal && (r[filterCol] || '').trim() !== filterVal) {{
                return false;
            }}
            const pt = parseNum(r[PRIMARY_POINT_COL]);
            if (minPt !== null && (isNaN(pt) || pt < minPt)) return false;
            if (maxPt !== null && (isNaN(pt) || pt > maxPt)) return false;
            return true;
        }});

        // 2. 排序資料
        if (sortColIndex >= 0) {{
            filtered.sort((a, b) => compareValues(a[sortColIndex], b[sortColIndex], sortAsc));
            document.getElementById('sortStatusBadge').textContent =
                '目前排序：' + HEADERS[sortColIndex] + (sortAsc ? ' (▲ 升冪)' : ' (▼ 降冪)');
        }} else {{
            document.getElementById('sortStatusBadge').textContent = '預設順序 (點擊表頭可排序)';
        }}

        const lockedDetailNames = Array.from(lockedDetailCols).sort((a, b) => a - b).map(i => HEADERS[i]);
        document.getElementById('detailTableStatusText').textContent =
            (sortColIndex >= 0 ? '📊 排序欄位：' + HEADERS[sortColIndex] + (sortAsc ? ' (▲ 升冪)' : ' (▼ 降冪)') : '⇅ 點擊表頭文字可升冪/降冪排序') +
            ' ｜ ' +
            (lockedDetailNames.length > 0 ? '🔒 已鎖定欄位：' + lockedDetailNames.join('、') : '📌 點擊表頭右側圖釘可鎖定(凍結)欄位');

        // 3. 計算總計 KPI
        let totalPt = 0;
        filtered.forEach(r => {{
            const pt = parseNum(r[PRIMARY_POINT_COL]);
            if (!isNaN(pt)) totalPt += pt;
        }});
        const avgPt = filtered.length > 0 ? (totalPt / filtered.length) : 0;

        document.getElementById('kpiRowCount').textContent = filtered.length + ' / ' + RAW_ROWS.length + ' 筆';
        document.getElementById('kpiTotalPoints').textContent = totalPt.toFixed(2).replace(/\.00$/, '') + ' pt';
        document.getElementById('kpiAvgPoints').textContent = avgPt.toFixed(2).replace(/\.00$/, '') + ' pt';

        // 4. 群組彙總計算與渲染 (包含表頭排序與鎖定)
        const groupSection = document.getElementById('groupSummarySection');
        const groupHeaderRow = document.getElementById('groupHeaderRow');
        const groupTbody = document.querySelector('#groupSummaryTable tbody');
        groupHeaderRow.innerHTML = '';
        groupTbody.innerHTML = '';

        GROUP_HEADERS.forEach((gh, gIdx) => {{
            const th = document.createElement('th');
            const isSorted = (groupSortColIndex === gIdx);
            const isLocked = lockedGroupCols.has(gIdx);
            if (isSorted) th.classList.add(groupSortAsc ? 'th-sorted-asc' : 'th-sorted-desc');
            const arrow = isSorted ? (groupSortAsc ? '▲' : '▼') : '⇅';
            th.innerHTML = `<div class=""sp-th-box"">
                <span class=""sp-th-sort-btn"" title=""點擊依【${{escapeHtml(gh)}}】排序"">
                    <span>${{escapeHtml(gh)}}</span>
                    <span class=""sp-sort-indicator"">${{arrow}}</span>
                </span>
                <button type=""button"" class=""sp-th-lock-btn ${{isLocked ? 'active' : ''}}"" title=""${{isLocked ? '點擊解除鎖定此欄位' : '點擊鎖定(凍結)此欄位'}}"" >${{isLocked ? '🔒' : '📌'}}</button>
            </div>`;
            th.querySelector('.sp-th-sort-btn').onclick = () => toggleGroupSort(gIdx);
            th.querySelector('.sp-th-lock-btn').onclick = (e) => toggleGroupColLock(e, gIdx);
            groupHeaderRow.appendChild(th);
        }});

        const groupsMap = new Map();
        if (groupCol >= 0) {{
            groupSection.style.display = 'block';
            filtered.forEach(r => {{
                const key = (r[groupCol] || '未分類').trim() || '未分類';
                if (!groupsMap.has(key)) {{
                    groupsMap.set(key, {{ key, rows: [], sumPt: 0, minPt: null, maxPt: null }});
                }}
                const g = groupsMap.get(key);
                g.rows.push(r);
                const pt = parseNum(r[PRIMARY_POINT_COL]);
                if (!isNaN(pt)) {{
                    g.sumPt += pt;
                    if (g.minPt === null || pt < g.minPt) g.minPt = pt;
                    if (g.maxPt === null || pt > g.maxPt) g.maxPt = pt;
                }}
            }});

            const groupList = Array.from(groupsMap.values()).map(g => {{
                const cntPctVal = filtered.length > 0 ? (g.rows.length * 100) / filtered.length : 0;
                const ptPctVal = totalPt > 0 ? (g.sumPt * 100) / totalPt : 0;
                const gAvgVal = g.rows.length > 0 ? (g.sumPt / g.rows.length) : 0;
                return {{
                    ...g,
                    cntPctVal,
                    ptPctVal,
                    gAvgVal,
                    sortVals: [
                        g.key,
                        g.rows.length,
                        cntPctVal,
                        g.sumPt,
                        ptPctVal,
                        gAvgVal,
                        g.minPt ?? -Infinity,
                        g.maxPt ?? -Infinity
                    ]
                }};
            }});

            if (groupSortColIndex >= 0) {{
                groupList.sort((a, b) => compareValues(a.sortVals[groupSortColIndex], b.sortVals[groupSortColIndex], groupSortAsc));
            }}

            const lockedGroupNames = Array.from(lockedGroupCols).sort((a, b) => a - b).map(i => GROUP_HEADERS[i]);
            document.getElementById('groupTableStatusText').textContent =
                (groupSortColIndex >= 0 ? '📊 排序欄位：' + GROUP_HEADERS[groupSortColIndex] + (groupSortAsc ? ' (▲ 升冪)' : ' (▼ 降冪)') : '⇅ 點擊表頭文字可排序') +
                ' ｜ ' +
                (lockedGroupNames.length > 0 ? '🔒 已鎖定欄位：' + lockedGroupNames.join('、') : '📌 點擊表頭右側圖釘可鎖定(凍結)欄位');

            document.getElementById('kpiGroupCount').textContent = groupList.length + ' 組';
            document.getElementById('groupSummaryBadge').textContent =
                '群組維度：【' + HEADERS[groupCol] + '】｜ 共 ' + groupList.length + ' 個群組';

            groupList.forEach(g => {{
                const cntPct = g.cntPctVal.toFixed(1) + '%';
                const ptPct = g.ptPctVal.toFixed(1) + '%';
                const gAvg = g.gAvgVal.toFixed(2).replace(/\.00$/, '');
                const tr = document.createElement('tr');
                tr.style.cursor = 'pointer';
                tr.title = '點擊只篩選此群組：' + g.key;
                tr.onclick = () => quickFilterGroup(g.key);
                tr.innerHTML = `
                    <td><strong>${{escapeHtml(g.key)}}</strong></td>
                    <td><span class=""badge"">${{g.rows.length}} 筆</span></td>
                    <td>${{cntPct}}</td>
                    <td><strong style=""color:#2563eb;font-size:14.5px;"">${{g.sumPt.toFixed(2).replace(/\.00$/, '')}} pt</strong></td>
                    <td>${{ptPct}}</td>
                    <td>${{gAvg}} pt</td>
                    <td>${{g.minPt !== null ? g.minPt : '-'}} pt</td>
                    <td>${{g.maxPt !== null ? g.maxPt : '-'}} pt</td>
                `;
                groupTbody.appendChild(tr);
            }});
            applyLockedColumnPositions('groupSummaryTable', lockedGroupCols);
        }} else {{
            groupSection.style.display = 'none';
            document.getElementById('kpiGroupCount').textContent = '未分組';
        }}

        // 5. 渲染明細表頭 (含排序與欄位鎖定按鈕)
        const headerRow = document.getElementById('detailHeaderRow');
        headerRow.innerHTML = '';
        HEADERS.forEach((h, idx) => {{
            const th = document.createElement('th');
            const isSorted = (sortColIndex === idx);
            const isLocked = lockedDetailCols.has(idx);
            if (isSorted) th.classList.add(sortAsc ? 'th-sorted-asc' : 'th-sorted-desc');
            const arrow = isSorted ? (sortAsc ? '▲' : '▼') : '⇅';
            th.innerHTML = `<div class=""sp-th-box"">
                <span class=""sp-th-sort-btn"" title=""點擊依【${{escapeHtml(h)}}】切換升冪/降冪排序"">
                    <span>${{escapeHtml(h)}}</span>
                    <span class=""sp-sort-indicator"">${{arrow}}</span>
                </span>
                <button type=""button"" class=""sp-th-lock-btn ${{isLocked ? 'active' : ''}}"" title=""${{isLocked ? '點擊解除鎖定此欄位' : '點擊鎖定(凍結)此欄位'}}"">${{isLocked ? '🔒' : '📌'}}</button>
            </div>`;
            th.querySelector('.sp-th-sort-btn').onclick = () => toggleSort(idx);
            th.querySelector('.sp-th-lock-btn').onclick = (e) => toggleDetailColLock(e, idx);
            headerRow.appendChild(th);
        }});

        // 6. 渲染明細內容 (若啟用群組彙總則顯示可摺疊群組小計標題列)
        const detailTbody = document.getElementById('detailTbody');
        detailTbody.innerHTML = '';

        if (groupCol >= 0 && groupsMap.size > 0) {{
            Array.from(groupsMap.values()).forEach(g => {{
                const isCollapsed = !!collapsedGroups[g.key];
                const grpTr = document.createElement('tr');
                grpTr.className = 'group-row';
                grpTr.onclick = () => toggleGroupCollapse(g.key);
                grpTr.innerHTML = `<td colspan=""${{HEADERS.length}}"">
                    <span>${{isCollapsed ? '▶' : '▼'}} 📂 群組【${{escapeHtml(HEADERS[groupCol])}}：${{escapeHtml(g.key)}}】— 共 ${{g.rows.length}} 筆 ｜ 小計 Story Point：${{g.sumPt.toFixed(2).replace(/\.00$/, '')}} pt（點擊展開/收合群組明細）</span>
                </td>`;
                detailTbody.appendChild(grpTr);

                if (!isCollapsed) {{
                    g.rows.forEach(r => {{
                        const tr = document.createElement('tr');
                        tr.innerHTML = r.map((cell, cIdx) =>
                            cIdx === PRIMARY_POINT_COL
                                ? `<td><strong style=""color:#2563eb;"">${{escapeHtml(cell)}}</strong></td>`
                                : `<td>${{escapeHtml(cell)}}</td>`
                        ).join('');
                        detailTbody.appendChild(tr);
                    }});
                }}
            }});
        }} else {{
            filtered.forEach(r => {{
                const tr = document.createElement('tr');
                tr.innerHTML = r.map((cell, cIdx) =>
                    cIdx === PRIMARY_POINT_COL
                        ? `<td><strong style=""color:#2563eb;"">${{escapeHtml(cell)}}</strong></td>`
                        : `<td>${{escapeHtml(cell)}}</td>`
                ).join('');
                detailTbody.appendChild(tr);
            }});
        }}

        // 7. 渲染頁尾總計列
        const detailTfoot = document.getElementById('detailTfoot');
        detailTfoot.innerHTML = `<tr class=""tfoot-row"">
            <td colspan=""${{Math.max(1, PRIMARY_POINT_COL)}}"">Σ 篩選後總計彙總（共 ${{filtered.length}} 筆）</td>
            <td>${{totalPt.toFixed(2).replace(/\.00$/, '')}} pt (平均 ${{avgPt.toFixed(2).replace(/\.00$/, '')}} pt)</td>
            <td colspan=""${{Math.max(1, HEADERS.length - PRIMARY_POINT_COL - 1)}}""></td>
        </tr>`;

        applyLockedColumnPositions('detailTable', lockedDetailCols);
    }}

    window.addEventListener('resize', () => {{
        applyLockedColumnPositions('groupSummaryTable', lockedGroupCols);
        applyLockedColumnPositions('detailTable', lockedDetailCols);
    }});
    document.addEventListener('DOMContentLoaded', initControls);
</script>
</body>
</html>";
    }

    private static string escapeJsString(string? input)
    {
        return (input ?? "")
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "")
            .Replace("\n", " ");
    }

    /// <summary>
    /// 正規化撲克牌組 CSV 字串：若舊資料庫因 varchar 編碼導致第二個特殊牌「☕」變成「?」（出現兩個問號 ?,?），自動將第二個問號還原為「☕」。
    /// </summary>
    private static string normalizeCardDeckCsv(string? rawCsv)
    {
        string src = string.IsNullOrWhiteSpace(rawCsv) ? "0,0.5,1,2,3,5,8,13,21,?,☕" : rawCsv.Trim();
        string[] parts = src.Split(',', StringSplitOptions.RemoveEmptyEntries);
        int questionCount = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = parts[i].Trim();
            if (parts[i] == "?")
            {
                questionCount++;
                if (questionCount >= 2)
                {
                    parts[i] = "☕";
                }
            }
        }
        return string.Join(",", parts);
    }

    /// <summary>
    /// 取得每張撲克牌面的「牌面簡短意義標籤」與「完整中文意義說明」，讓牌面除了顯示符號/數字外也直接標示意義。
    /// </summary>
    private static (string shortMeaning, string fullDescription) getPokerCardMeaning(string? cardValue)
    {
        string c = (cardValue ?? "").Trim();
        return c switch
        {
            "?" => ("需求不明", "? (疑問牌：需求不明需討論)"),
            "☕" => ("暫停休息", "☕ (休息牌：暫停休息)"),
            "0" => ("無需工時", "0 點 (無需額外工時)"),
            "0.5" => ("極小微調", "0.5 點 (極小微調)"),
            "1" => ("極簡變更", "1 點 (極簡變更)"),
            "2" => ("小型工項", "2 點 (小型工項)"),
            "3" => ("標準一般", "3 點 (標準一般工項)"),
            "5" => ("中等複雜", "5 點 (中等複雜度)"),
            "8" => ("高複雜度", "8 點 (高複雜度工項)"),
            "13" => ("大型跨模組", "13 點 (大型跨模組工項)"),
            "20" => ("極大工項", "20 點 (極大工項)"),
            "21" => ("極大工項", "21 點 (極大工項)"),
            "40" => ("巨型需拆解", "40 點 (巨型需拆解)"),
            "100" => ("史詩級拆解", "100 點 (史詩級需拆解)"),
            _ => ($"{c} 點", $"{c} 點")
        };
    }

    /// <summary>
    /// 將牌組 CSV 轉為下拉選單易讀文字（將 ? 與 ☕ 附加上中文意義）。
    /// </summary>
    private static string formatCardDeckCsvForOption(string? rawCsv)
    {
        string normalized = normalizeCardDeckCsv(rawCsv);
        string[] parts = normalized.Split(',', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "?") parts[i] = "?(需求不明)";
            else if (parts[i] == "☕") parts[i] = "☕(暫停休息)";
        }
        return string.Join(", ", parts);
    }

    // =========================================================================
    // 7. 建構繁體中文多頁籤 HTML 介面
    // =========================================================================

    private string buildPortalHtml(
        string currentTab, int activeSessionPk, bool isAdmin, string? msg, string? err,
        DataTable deptTable, DataTable empTable, DataTable typeTable, DataTable itemTable,
        DataTable assignTable, DataTable historyTable, DataTable cardDeckTable, DataTable logTable, DataTable kindTable,
        DataTable sessionTable, DataTable roundTable, DataTable voteRecordTable, DataTable participantTable)
    {
        var sb = new StringBuilder();

        // 計算頂部 KPI 卡片指標
        decimal totalCompanyPoints = 0m;
        foreach (DataRow r in deptTable.Rows)
        {
            if (r["totalDepartmentStoryPoint"] != DBNull.Value)
            {
                totalCompanyPoints += Convert.ToDecimal(r["totalDepartmentStoryPoint"]);
            }
        }

        decimal myPoints = 0m;
        decimal myTodoPoints = 0m;
        decimal myInProgressPoints = 0m;
        decimal myDonePoints = 0m;
        int myItems = 0;
        int curEmpPk = getCurrentEmployeePk();
        foreach (DataRow r in empTable.Rows)
        {
            if (Convert.ToInt32(r["employeePk"]) == curEmpPk)
            {
                myPoints = Convert.ToDecimal(r["totalAllocatedStoryPoint"]);
                myTodoPoints = Convert.ToDecimal(r["todoAllocatedStoryPoint"]);
                myInProgressPoints = Convert.ToDecimal(r["inProgressAllocatedStoryPoint"]);
                myDonePoints = Convert.ToDecimal(r["doneAllocatedStoryPoint"]);
                myItems = Convert.ToInt32(r["assignedItemCount"]);
                break;
            }
        }

        string roleBadge = isAdmin
            ? "<span class='badge badge-admin'>👑 管理者 (Admin - 全域檢視與管理)</span>"
            : "<span class='badge badge-user'>🙋 個別員工 (僅檢視自己的投票、工項與分數)</span>";

        string menu2MainLabel = isAdmin ? "📋 工項管理" : "📋 我的工項";
        string menu2MainTip = isAdmin
            ? "工項與代表類型管理：查詢各工項點數、指派負責員工、管理標準代表類型及匯出 CSV / Excel / 互動 HTML 報表"
            : "我的工項與分數：查詢賦予給我的工項、查閱代表類型標準點數及匯出個人工項報表";
        string menu2Sub1Title = isAdmin ? "2-1. 工項與員工指派" : "2-1. 我的工項查詢";
        string menu2Sub1Desc = isAdmin
            ? "2-1. 工項點數查詢與賦予員工設定：查詢各工項點數、選擇代表類型自動帶入或修改員工指派"
            : "2-1. 我的工項與個人分數查詢：僅顯示賦予給我本人的工項與個人配額分數（不顯示其他人）";
        string menu3MainLabel = isAdmin ? "📊 統計報表" : "📊 我的報表";
        string menu3MainTip = isAdmin
            ? "員工與部門統計報表：查詢員工個人與部門總 Story Point 負載統計，並支援匯出 CSV / Excel / 互動 HTML"
            : "我的分數統計報表：查看我個人身上的工項數量與各狀態 Story Point 分數，並匯出三種格式報表";
        string menu3Sub1Title = isAdmin ? "3-1. 員工與部門統計" : "3-1. 我的分數統計";
        string menu3Sub1Desc = isAdmin
            ? "3-1. 員工與部門總 Story Point 統計：查詢員工身上的點數與所屬部門的總 Story Point 數"
            : "3-1. 我的個人總 Story Point 分數統計：查看我個人身上的工項數量與各狀態 Story Point 分數";
        string menu3Sub3Title = isAdmin ? "3-3A. 匯出員工 (CSV)" : "3-2A. 匯出個人 (CSV)";
        string menu3Sub3Desc = isAdmin
            ? "匯出員工個人總點數 CSV 報表：下載每位員工賦予工項與點數統計表 (UTF-8 BOM)"
            : "匯出我的個人工項與分數 CSV 報表：僅下載我個人專屬的工項與分數統計表";

        sb.Append($@"<!DOCTYPE html>
<html lang=""zh-Hant"">
<head>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />
    <title>Azure 多人參與 Story Point 估算與工項管理系統</title>
    <script src=""https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.0/signalr.min.js""></script>
    <style>
        :root {{ --primary:#2563eb; --primary-dark:#1d4ed8; --bg:#f8fafc; --card:#ffffff; --border:#e2e8f0; --text:#0f172a; --muted:#64748b; }}
        * {{ box-sizing:border-box; }}
        body {{ margin:0; font-family:'Microsoft JhengHei UI','Segoe UI',sans-serif; background:var(--bg); color:var(--text); }}
        .topbar {{ background:#0f172a; color:#fff; padding:12px 24px; display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:12px; }}
        .topbar-title {{ display:flex; align-items:center; gap:12px; font-size:18px; font-weight:700; }}
        .topbar-user {{ display:flex; align-items:center; gap:12px; font-size:13px; }}
        .badge {{ padding:4px 10px; border-radius:999px; font-size:12px; font-weight:700; display:inline-block; }}
        .badge-admin {{ background:#fef3c7; color:#b45309; }}
        .badge-user {{ background:#dbeafe; color:#1d4ed8; }}
        .badge-voting {{ background:#fef9c3; color:#854d0e; border:1px solid #fde047; }}
        .badge-revealed {{ background:#e0e7ff; color:#3730a3; border:1px solid #a5b4fc; }}
        .badge-finalized {{ background:#dcfce7; color:#166534; border:1px solid #86efac; }}
        .container {{ max-width:1440px; margin:0 auto; padding:20px 24px; }}
        .kpi-grid {{ display:grid; grid-template-columns:repeat(auto-fit,minmax(220px,1fr)); gap:14px; margin-bottom:20px; }}
        .kpi-card {{ background:#fff; border:1px solid var(--border); border-radius:12px; padding:16px 18px; box-shadow:0 1px 3px rgba(0,0,0,0.04); }}
        .kpi-label {{ font-size:12.5px; color:var(--muted); font-weight:600; }}
        .kpi-val {{ font-size:26px; font-weight:800; color:#0f172a; margin-top:4px; }}
        .kpi-sub {{ font-size:12px; color:#2563eb; margin-top:4px; }}
        /* 網頁上方主選單與子選單下拉導覽列（強制單排不換行，搭配浮動 tips 說明） */
        .top-menubar {{ background:#1e293b; border-bottom:1px solid #334155; padding:0 18px; position:sticky; top:0; z-index:999; box-shadow:0 4px 12px rgba(15,23,42,0.12); }}
        .top-menubar-inner {{ max-width:1440px; margin:0 auto; display:flex; align-items:center; justify-content:space-between; flex-wrap:nowrap; gap:8px; }}
        .menu-group-list {{ display:flex; align-items:center; gap:4px; flex-wrap:nowrap; white-space:nowrap; }}
        .menu-dropdown {{ position:relative; display:inline-block; flex-shrink:0; }}
        .menu-main-btn {{ background:transparent; color:#e2e8f0; border:none; padding:10px 12px; font-size:13.5px; font-weight:700; font-family:inherit; cursor:pointer; display:inline-flex; align-items:center; gap:5px; border-bottom:3px solid transparent; transition:all .15s; white-space:nowrap; }}
        .menu-main-btn:hover, .menu-dropdown:hover .menu-main-btn {{ background:#334155; color:#ffffff; }}
        .menu-main-btn.active-group {{ color:#ffffff; background:rgba(37,99,235,0.28); border-bottom-color:#38bdf8; }}
        .menu-caret {{ font-size:10px; opacity:0.8; transition:transform .15s; }}
        .menu-dropdown:hover .menu-caret {{ transform:rotate(180deg); }}
        .menu-sub-list {{ display:none; position:absolute; top:100%; left:0; min-width:230px; background:#ffffff; border:1px solid #cbd5e1; border-radius:0 0 12px 12px; box-shadow:0 14px 30px rgba(15,23,42,0.18); padding:6px; z-index:1000; }}
        .menu-dropdown:hover .menu-sub-list, .menu-dropdown.open .menu-sub-list {{ display:block; }}
        .menu-sub-header {{ font-size:11px; font-weight:700; color:#64748b; padding:5px 10px 4px 10px; letter-spacing:0.4px; border-bottom:1px dashed #e2e8f0; margin-bottom:3px; white-space:nowrap; }}
        .menu-sub-item {{ display:flex; align-items:center; gap:8px; padding:8px 11px; border-radius:8px; color:#1e293b; text-decoration:none; cursor:pointer; transition:background .15s; white-space:nowrap; }}
        .menu-sub-item:hover {{ background:#eff6ff; color:#1d4ed8; }}
        .menu-sub-item.active {{ background:#2563eb; color:#ffffff; }}
        .menu-sub-icon {{ font-size:15px; line-height:1; flex-shrink:0; }}
        .menu-sub-title {{ font-size:13px; font-weight:700; line-height:1.3; white-space:nowrap; }}
        .current-location-bar {{ display:flex; align-items:center; gap:6px; color:#cbd5e1; font-size:12px; padding:6px 0; white-space:nowrap; flex-shrink:0; }}
        .current-location-pill {{ background:#2563eb; color:#fff; padding:3px 10px; border-radius:999px; font-weight:700; font-size:12px; white-space:nowrap; }}
        .tab-pane {{ display:none; }}
        .tab-pane.active {{ display:block; }}
        .card {{ background:#fff; border:1px solid var(--border); border-radius:12px; padding:20px; margin-bottom:20px; box-shadow:0 1px 3px rgba(0,0,0,0.04); }}
        .card-title {{ font-size:16px; font-weight:700; margin:0 0 14px 0; display:flex; align-items:center; justify-content:space-between; border-bottom:1px solid #f1f5f9; padding-bottom:10px; flex-wrap:wrap; gap:8px; }}
        .grid-2 {{ display:grid; grid-template-columns:1fr 1fr; gap:20px; }}
        .grid-voting {{ display:grid; grid-template-columns:385px 1fr; gap:20px; transition:all .2s ease; align-items:start; }}
        .grid-voting.sidebar-collapsed {{ grid-template-columns:56px 1fr; }}
        .voting-sidebar-card {{ transition:all .2s ease; overflow:hidden; }}
        .grid-voting.sidebar-collapsed .voting-sidebar-card {{ padding:12px 8px; }}
        .voting-sidebar-collapsed-strip {{ display:none; flex-direction:column; align-items:center; gap:12px; cursor:pointer; padding:8px 0; color:#1e3a8a; font-weight:700; font-size:13px; writing-mode:vertical-rl; text-orientation:mixed; letter-spacing:2px; user-select:none; }}
        .grid-voting.sidebar-collapsed .voting-sidebar-collapsed-strip {{ display:flex; }}
        .grid-voting.sidebar-collapsed .voting-sidebar-full-content {{ display:none; }}
        .voting-sidebar-body.collapsed {{ display:none; }}
        /* 投票議程清單 Folder 與邊項展開收合樣式 */
        .agenda-folder {{ border:1px solid #cbd5e1; border-radius:10px; margin-bottom:12px; background:#f8fafc; overflow:hidden; }}
        .agenda-folder-header {{ display:flex; align-items:center; justify-content:space-between; padding:10px 12px; background:#f1f5f9; cursor:pointer; user-select:none; font-weight:700; font-size:13.5px; color:#1e293b; border-bottom:1px solid #e2e8f0; transition:background .15s; }}
        .agenda-folder-header:hover {{ background:#e2e8f0; }}
        .agenda-folder.collapsed .agenda-folder-header {{ border-bottom:none; }}
        .agenda-folder-caret {{ display:inline-block; transition:transform .18s ease; font-size:12px; color:#475569; margin-right:6px; }}
        .agenda-folder.collapsed .agenda-folder-caret {{ transform:rotate(-90deg); }}
        .agenda-folder-body {{ padding:8px; display:flex; flex-direction:column; gap:8px; background:#ffffff; }}
        .agenda-folder.collapsed .agenda-folder-body {{ display:none; }}
        .session-item-card {{ border-radius:8px; padding:10px 12px; transition:all .15s; position:relative; }}
        .session-item-detail {{ margin-top:8px; padding-top:8px; border-top:1px dashed #cbd5e1; font-size:12px; color:#334155; display:flex; flex-direction:column; gap:4px; background:rgba(248,250,252,0.8); border-radius:6px; padding:8px; }}
        .session-item-detail.collapsed {{ display:none; }}
        .btn-item-toggle {{ background:#f1f5f9; border:1px solid #cbd5e1; color:#334155; border-radius:6px; padding:2px 7px; font-size:11px; font-weight:700; cursor:pointer; }}
        .btn-item-toggle:hover {{ background:#e2e8f0; color:#0f172a; }}
        /* 設定盲投回合專用樣式 */
        .setup-section {{ border:1px solid #e2e8f0; border-radius:10px; padding:16px; margin-bottom:16px; background:#f8fafc; }}
        .setup-section-title {{ font-size:15px; font-weight:700; color:#1e3a8a; margin:0 0 12px 0; display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:8px; border-bottom:1px dashed #cbd5e1; padding-bottom:8px; }}
        .preset-bar {{ display:flex; gap:10px; flex-wrap:wrap; margin-bottom:16px; padding:12px 14px; background:#eff6ff; border:1px solid #bfdbfe; border-radius:10px; align-items:center; }}
        .participant-grid {{ display:grid; grid-template-columns:repeat(auto-fill,minmax(235px,1fr)); gap:10px; max-height:310px; overflow-y:auto; padding:4px; }}
        .participant-chip {{ display:flex; align-items:flex-start; gap:8px; padding:10px 12px; border:1px solid #cbd5e1; border-radius:8px; background:#fff; cursor:pointer; transition:all .15s; margin:0; font-weight:normal; }}
        .participant-chip:hover {{ border-color:#2563eb; background:#eff6ff; }}
        .participant-chip input[type=checkbox] {{ margin-top:3px; width:16px; height:16px; accent-color:#2563eb; }}
        .rule-pill {{ display:inline-flex; align-items:center; gap:4px; padding:4px 10px; border-radius:999px; font-size:12px; font-weight:600; background:#f1f5f9; color:#334155; border:1px solid #cbd5e1; }}
        .poker-card.disabled-card {{ opacity:0.32; cursor:not-allowed; background:#f1f5f9; border-style:dashed; transform:none !important; box-shadow:none !important; }}
        @media (max-width:1024px) {{ .grid-2, .grid-voting, .grid-voting.sidebar-collapsed {{ grid-template-columns:1fr; }} }}
        table {{ width:100%; border-collapse:separate; border-spacing:0; font-size:13px; }}
        th, td {{ padding:10px 12px; border-bottom:1px solid var(--border); text-align:left; vertical-align:top; background:#ffffff; }}
        th {{ background:#f8fafc; font-weight:700; color:#334155; white-space:nowrap; user-select:none; transition:background .15s; vertical-align:middle; }}
        tr:hover td {{ background:#f8fafc; }}
        tr.syslog-row-err td {{ background:#fff1f2; }}
        tr.syslog-row-warn td {{ background:#fffbeb; }}
        tr.syslog-row-err:hover td {{ background:#ffe4e6; }}
        tr.syslog-row-warn:hover td {{ background:#fef3c7; }}
        /* 通用表格表頭排序 (Sorting)、鎖定 (Locking / Pinning / Sticky Header)、篩選 (Filtering) 與滑鼠快捷選單 (Context Menu) 樣式 */
        .sp-table-wrapper {{ width:100%; margin-top:4px; }}
        .sp-table-toolbar {{ display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:8px; padding:8px 12px; background:#f1f5f9; border:1px solid #cbd5e1; border-bottom:none; border-radius:8px 8px 0 0; font-size:12px; color:#334155; }}
        .sp-table-toolbar-status {{ display:flex; align-items:center; gap:8px; flex-wrap:wrap; font-weight:600; }}
        .sp-table-toolbar-filter {{ display:flex; align-items:center; gap:6px; flex-wrap:wrap; flex:1; min-width:280px; justify-content:flex-end; }}
        .sp-tbl-filter-input {{ width:185px !important; padding:4px 9px !important; font-size:12px !important; border-radius:6px !important; border:1px solid #93c5fd !important; background:#ffffff !important; color:#0f172a !important; }}
        .sp-tbl-filter-input:focus {{ outline:2px solid #2563eb; border-color:#2563eb !important; }}
        .sp-tbl-filter-select {{ width:auto !important; max-width:165px; padding:4px 8px !important; font-size:12px !important; border-radius:6px !important; border:1px solid #cbd5e1 !important; background:#ffffff !important; color:#1e293b !important; font-weight:600; }}
        .sp-tbl-count-badge {{ display:inline-flex; align-items:center; padding:3px 8px; border-radius:999px; background:#dbeafe; color:#1e40af; font-size:11.5px; font-weight:800; white-space:nowrap; border:1px solid #93c5fd; }}
        .sp-table-toolbar-actions {{ display:flex; align-items:center; gap:6px; flex-wrap:wrap; }}
        .sp-toolbar-btn {{ display:inline-flex; align-items:center; gap:4px; padding:4px 9px; border-radius:6px; border:1px solid #cbd5e1; background:#ffffff; color:#334155; font-size:11.5px; font-weight:700; cursor:pointer; transition:all .15s; white-space:nowrap; }}
        .sp-toolbar-btn:hover {{ background:#eff6ff; border-color:#93c5fd; color:#1d4ed8; }}
        .sp-toolbar-btn.active {{ background:#dbeafe; border-color:#3b82f6; color:#1e3a8a; }}
        .sp-table-scroll-container {{ width:100%; overflow-x:auto; max-height:70vh; overflow-y:auto; border:1px solid #cbd5e1; border-radius:0 0 8px 8px; position:relative; background:#ffffff; }}
        table.sp-sticky-header-enabled thead th {{ position:sticky; top:0; z-index:25; box-shadow:0 1px 0 #cbd5e1; }}
        th.th-sorted-asc, th.th-sorted-desc {{ background:#eff6ff !important; color:#1d4ed8 !important; }}
        th.sp-col-locked {{ background:#dbeafe !important; color:#1e3a8a !important; z-index:35 !important; position:sticky; }}
        td.sp-col-locked {{ background:#f0f7ff !important; z-index:15 !important; position:sticky; }}
        tr:hover td.sp-col-locked {{ background:#e0f2fe !important; }}
        .sp-col-locked-last {{ border-right:2px solid #3b82f6 !important; box-shadow:3px 0 8px rgba(37,99,235,0.14); }}
        .sp-th-box {{ display:flex; align-items:center; justify-content:space-between; gap:6px; width:100%; }}
        .sp-th-sort-btn {{ display:inline-flex; align-items:center; gap:5px; cursor:pointer; flex:1; padding:2px 0; color:inherit; font-weight:700; }}
        .sp-th-sort-btn:hover {{ color:#2563eb; }}
        .sp-sort-indicator {{ display:inline-flex; align-items:center; justify-content:center; min-width:18px; height:18px; padding:0 4px; border-radius:4px; font-size:11px; color:#64748b; background:rgba(148,163,184,0.16); transition:all .15s; }}
        th.th-sorted-asc .sp-sort-indicator, th.th-sorted-desc .sp-sort-indicator {{ background:#2563eb; color:#ffffff; font-weight:800; }}
        .sp-th-lock-btn {{ display:inline-flex; align-items:center; justify-content:center; width:24px; height:22px; border-radius:5px; border:1px solid #cbd5e1; background:#ffffff; color:#64748b; font-size:11px; cursor:pointer; padding:0; transition:all .15s; flex-shrink:0; }}
        .sp-th-lock-btn:hover {{ border-color:#2563eb; color:#2563eb; background:#eff6ff; }}
        .sp-th-lock-btn.active {{ background:#1e40af; color:#ffffff; border-color:#1e3a8a; box-shadow:0 1px 3px rgba(30,64,175,0.3); }}
        /* 表格資料列滑鼠右鍵快捷選單 (Row Context Menu) 與單筆編輯 Modal 視窗樣式 */
        table tbody tr {{ cursor:context-menu; transition:background .12s; }}
        table tbody tr:hover td {{ background:#f0f7ff; }}
        table tbody tr.sp-context-active-row td {{ background:#dbeafe !important; box-shadow:inset 0 1px 0 #3b82f6, inset 0 -1px 0 #3b82f6; }}
        .sp-context-menu {{ position:fixed; z-index:100000; min-width:265px; max-width:360px; background:#ffffff; border:1.5px solid #3b82f6; border-radius:12px; box-shadow:0 18px 38px rgba(15,23,42,0.28), 0 4px 12px rgba(37,99,235,0.14); padding:6px; display:none; user-select:none; animation:spCtxFadeIn .12s ease-out; }}
        .sp-context-menu.visible, .sp-context-menu.open {{ display:block !important; }}
        @keyframes spCtxFadeIn {{ from {{ opacity:0; transform:scale(0.96) translateY(-4px); }} to {{ opacity:1; transform:scale(1) translateY(0); }} }}
        .sp-ctx-header {{ display:flex; align-items:center; justify-content:space-between; gap:8px; padding:8px 11px; margin-bottom:4px; background:linear-gradient(135deg,#0f172a 0%,#1e3a8a 100%); color:#ffffff; border-radius:8px; font-size:12px; font-weight:700; }}
        .sp-ctx-header-title {{ overflow:hidden; text-overflow:ellipsis; white-space:nowrap; max-width:265px; }}
        .sp-ctx-close {{ background:transparent; border:none; color:#cbd5e1; font-size:13px; cursor:pointer; padding:0 4px; line-height:1; border-radius:4px; }}
        .sp-ctx-close:hover {{ color:#ffffff; background:rgba(255,255,255,0.15); }}
        .sp-ctx-item {{ width:100%; display:flex; align-items:center; justify-content:space-between; gap:9px; padding:8px 11px; border:none; background:transparent; color:#1e293b; font-size:13px; font-weight:600; font-family:inherit; text-align:left; border-radius:7px; cursor:pointer; transition:background .12s, color .12s; }}
        .sp-ctx-item:hover {{ background:#eff6ff; color:#1d4ed8; }}
        .sp-ctx-item.primary-action {{ color:#1d4ed8; font-weight:700; }}
        .sp-ctx-item.primary-action:hover {{ background:#dbeafe; color:#1e3a8a; }}
        .sp-ctx-item.warn {{ color:#b45309; }}
        .sp-ctx-item.warn:hover {{ background:#fef3c7; color:#92400e; }}
        .sp-ctx-item.danger {{ color:#dc2626; }}
        .sp-ctx-item.danger:hover {{ background:#fef2f2; color:#991b1b; }}
        .sp-ctx-item:disabled, .sp-ctx-item.disabled {{ opacity:0.45; cursor:not-allowed; }}
        .sp-ctx-icon {{ display:inline-block; width:20px; text-align:center; font-size:14px; margin-right:6px; }}
        .sp-ctx-divider {{ height:1px; background:#e2e8f0; margin:5px 4px; }}
        /* 單筆紀錄編輯 Modal 彈出視窗樣式（不捲動網頁，直接原地彈出 Modal 編輯） */
        .sp-modal-overlay {{ position:fixed; inset:0; z-index:100005; background:rgba(15,23,42,0.58); backdrop-filter:blur(3px); display:none; align-items:center; justify-content:center; padding:18px; overflow-y:auto; }}
        .sp-modal-overlay.visible, .sp-modal-overlay.open {{ display:flex !important; }}
        .sp-modal-dialog {{ width:100%; max-width:860px; max-height:90vh; background:#ffffff; border-radius:14px; border:2px solid #2563eb; box-shadow:0 24px 60px rgba(15,23,42,0.42); display:flex; flex-direction:column; overflow:hidden; animation:spCtxFadeIn .15s ease-out; }}
        .sp-modal-header {{ display:flex; align-items:center; justify-content:space-between; gap:12px; padding:14px 20px; background:linear-gradient(135deg,#0f172a 0%,#1e3a8a 100%); color:#ffffff; }}
        .sp-modal-title {{ font-size:16px; font-weight:800; display:flex; align-items:center; gap:8px; }}
        .sp-modal-close-btn {{ background:rgba(255,255,255,0.14); border:1px solid rgba(255,255,255,0.28); color:#ffffff; border-radius:8px; padding:5px 12px; font-size:13px; font-weight:700; cursor:pointer; transition:all .15s; }}
        .sp-modal-close-btn:hover {{ background:#dc2626; border-color:#f87171; }}
        .sp-modal-body {{ padding:20px; overflow-y:auto; flex:1; background:#ffffff; }}
        .sp-modal-footer {{ display:flex; align-items:center; justify-content:space-between; gap:10px; padding:12px 20px; background:#f8fafc; border-top:1px solid #cbd5e1; flex-wrap:wrap; }}
        .sp-page-action-bar {{ display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:10px; background:linear-gradient(135deg,#0f172a 0%,#1e3a8a 100%); color:#ffffff; padding:11px 16px; border-radius:11px; margin-bottom:14px; box-shadow:0 6px 18px rgba(15,23,42,0.16); border:1px solid #3b82f6; }}
        .sp-page-action-bar .sp-action-title {{ font-size:14.5px; font-weight:800; display:flex; align-items:center; gap:8px; color:#f8fafc; }}
        .sp-page-action-bar .sp-action-group {{ display:flex; align-items:center; flex-wrap:wrap; gap:8px; }}
        .sp-drawer-toggle-btn {{ background:rgba(255,255,255,0.13); color:#f8fafc; border:1px solid rgba(255,255,255,0.32); padding:6px 12px; border-radius:7px; font-size:12.5px; font-weight:700; cursor:pointer; display:inline-flex; align-items:center; gap:6px; transition:all .15s; }}
        .sp-drawer-toggle-btn:hover, .sp-drawer-toggle-btn.active-drawer {{ background:#2563eb; border-color:#93c5fd; color:#ffffff; }}
        .sp-collapsible-drawer {{ transition:all .2s ease; margin-bottom:14px; }}
        .sp-collapsible-drawer.collapsed {{ display:none !important; }}
        .sp-form-flash {{ animation:spFormPulse 1.3s ease; }}
        @keyframes spFormPulse {{ 0% {{ box-shadow:0 0 0 0 rgba(37,99,235,0.55); border-color:#2563eb; }} 50% {{ box-shadow:0 0 0 6px rgba(37,99,235,0.22); border-color:#1d4ed8; }} 100% {{ box-shadow:0 1px 3px rgba(0,0,0,0.04); }} }}
        .sp-toast-notice {{ position:fixed; bottom:24px; right:24px; z-index:100010; background:#0f172a; color:#ffffff; border-left:4px solid #3b82f6; padding:11px 16px; border-radius:10px; font-size:13px; font-weight:700; box-shadow:0 12px 28px rgba(15,23,42,0.3); opacity:0; transform:translateY(12px); transition:all .2s ease; pointer-events:none; }}
        .sp-toast-notice.show {{ opacity:1; transform:translateY(0); }}
        input[type=text], input[type=number], input[type=password], input[type=date], input[type=datetime-local], select, textarea {{ width:100%; padding:8px 11px; border:1px solid #cbd5e1; border-radius:7px; font-size:13.5px; font-family:inherit; }}
        .form-row {{ display:grid; grid-template-columns:repeat(auto-fit,minmax(180px,1fr)); gap:12px; margin-bottom:12px; align-items:end; }}
        .form-row > div {{ min-width:0; }}
        label {{ display:block; font-size:12.5px; font-weight:600; color:#334155; margin-bottom:4px; }}
        .form-row > div > label {{ display:flex; align-items:center; gap:5px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; height:20px; line-height:20px; margin-bottom:5px; }}
        [data-tip] {{ cursor:help; }}
        .sp-tip-icon {{ display:inline-flex; align-items:center; justify-content:center; width:15px; height:15px; border-radius:50%; background:#e2e8f0; color:#475569; font-size:10.5px; font-weight:700; flex-shrink:0; cursor:help; transition:all .15s; }}
        [data-tip]:hover .sp-tip-icon, .sp-tip-icon:hover {{ background:#2563eb; color:#ffffff; }}
        .sp-floating-tooltip {{ position:fixed; z-index:99999; max-width:380px; padding:9px 13px; border-radius:8px; background:#0f172a; color:#f8fafc; font-size:12.5px; font-weight:500; line-height:1.55; box-shadow:0 10px 26px rgba(15,23,42,0.32); border:1px solid #3b82f6; pointer-events:none; opacity:0; transform:translateY(4px); transition:opacity .12s ease, transform .12s ease; white-space:normal; word-break:break-word; }}
        .sp-floating-tooltip.visible {{ opacity:1; transform:translateY(0); }}
        .btn {{ display:inline-flex; align-items:center; justify-content:center; gap:6px; padding:8px 15px; border-radius:7px; font-size:13.5px; font-weight:700; border:1px solid transparent; cursor:pointer; text-decoration:none; }}
        .btn-primary {{ background:#2563eb; color:#fff; }}
        .btn-primary:hover {{ background:#1d4ed8; }}
        .btn-success {{ background:#16a34a; color:#fff; }}
        .btn-warning {{ background:#d97706; color:#fff; }}
        .btn-outline {{ background:#fff; border-color:#cbd5e1; color:#334155; }}
        .btn-danger {{ background:#dc2626; color:#fff; }}
        .poker-deck {{ display:flex; flex-wrap:wrap; gap:10px; margin:14px 0; }}
        .poker-card {{ min-width:78px; height:96px; padding:6px 8px; border-radius:10px; border:2px solid #cbd5e1; background:#fff; display:flex; flex-direction:column; align-items:center; justify-content:center; gap:3px; font-size:22px; font-weight:800; color:#1e293b; cursor:pointer; transition:all .15s; box-shadow:0 2px 5px rgba(0,0,0,0.06); user-select:none; text-align:center; }}
        .poker-card-meaning {{ font-size:11px; font-weight:700; color:#475569; line-height:1.2; white-space:nowrap; }}
        .poker-card:hover {{ transform:translateY(-4px); border-color:#2563eb; box-shadow:0 6px 14px rgba(37,99,235,0.2); }}
        .poker-card.selected {{ background:linear-gradient(135deg,#2563eb,#1d4ed8); color:#fff; border-color:#1e40af; transform:translateY(-5px); }}
        .poker-card.selected .poker-card-meaning {{ color:#dbeafe; }}
        .voter-grid {{ display:grid; grid-template-columns:repeat(auto-fill,minmax(155px,1fr)); gap:12px; margin-top:12px; }}
        .voter-box {{ border:1px solid var(--border); border-radius:10px; padding:12px; text-align:center; background:#f8fafc; }}
        .voter-card-val {{ font-size:24px; font-weight:800; margin:8px 0; color:#2563eb; min-height:34px; display:flex; align-items:center; justify-content:center; }}
        .alert {{ padding:12px 16px; border-radius:8px; margin-bottom:16px; font-size:13.5px; font-weight:600; }}
        .alert-ok {{ background:#f0fdf4; color:#15803d; border:1px solid #bbf7d0; }}
        .alert-err {{ background:#fef2f2; color:#b91c1c; border:1px solid #fecaca; }}
        .alert-live {{ background:#eff6ff; color:#1e40af; border:1px solid #bfdbfe; display:none; }}
        /* 線上說明與搜尋中心 (Online Help Center with Search & Operation Screens) 樣式 */
        .help-hero-banner {{ background:linear-gradient(135deg,#0f172a 0%,#1e3a8a 65%,#1d4ed8 100%); color:#ffffff; border-radius:14px; padding:22px 24px; margin-bottom:18px; box-shadow:0 10px 28px rgba(15,23,42,0.22); border:1px solid #3b82f6; }}
        .help-search-grid {{ display:grid; grid-template-columns:1.6fr 0.7fr 0.7fr auto; gap:10px; margin-top:14px; align-items:center; }}
        @media (max-width:960px) {{ .help-search-grid {{ grid-template-columns:1fr; }} }}
        .help-search-input-wrap {{ position:relative; display:flex; align-items:center; }}
        .help-search-input {{ width:100% !important; padding:11px 14px 11px 38px !important; border-radius:10px !important; border:2px solid #60a5fa !important; background:#ffffff !important; color:#0f172a !important; font-size:14.5px !important; font-weight:700 !important; box-shadow:0 4px 14px rgba(0,0,0,0.18); }}
        .help-search-input:focus {{ outline:none; border-color:#f59e0b !important; box-shadow:0 0 0 3px rgba(245,158,11,0.35); }}
        .help-quick-tag {{ display:inline-flex; align-items:center; gap:4px; padding:4px 11px; border-radius:999px; background:rgba(255,255,255,0.14); border:1px solid rgba(255,255,255,0.3); color:#f8fafc; font-size:12px; font-weight:700; cursor:pointer; transition:all .15s; user-select:none; }}
        .help-quick-tag:hover, .help-quick-tag.active-tag {{ background:#f59e0b; border-color:#fbbf24; color:#0f172a; transform:translateY(-1px); }}
        .help-layout-grid {{ display:grid; grid-template-columns:280px 1fr; gap:18px; align-items:start; }}
        @media (max-width:1080px) {{ .help-layout-grid {{ grid-template-columns:1fr; }} }}
        .help-toc-sidebar {{ position:sticky; top:62px; background:#ffffff; border:1px solid #cbd5e1; border-radius:12px; padding:14px; box-shadow:0 2px 8px rgba(15,23,42,0.05); max-height:calc(100vh - 84px); overflow-y:auto; }}
        .help-toc-item {{ display:flex; align-items:center; justify-content:space-between; gap:8px; padding:8px 10px; border-radius:8px; color:#1e293b; font-size:12.5px; font-weight:700; text-decoration:none; cursor:pointer; transition:all .15s; margin-bottom:3px; border-left:3px solid transparent; }}
        .help-toc-item:hover {{ background:#eff6ff; color:#1d4ed8; border-left-color:#3b82f6; }}
        .help-toc-item.active-toc {{ background:#dbeafe; color:#1e3a8a; border-left-color:#1d4ed8; font-weight:800; }}
        .help-toc-item.toc-filtered-out {{ opacity:0.35; }}
        .help-topic-card {{ background:#ffffff; border:1.5px solid #cbd5e1; border-radius:14px; padding:20px; margin-bottom:20px; box-shadow:0 4px 12px rgba(15,23,42,0.05); transition:border-color .2s, box-shadow .2s; scroll-margin-top:70px; }}
        .help-topic-card:hover {{ border-color:#60a5fa; box-shadow:0 8px 22px rgba(37,99,235,0.1); }}
        .help-topic-header {{ display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:10px; border-bottom:2px solid #f1f5f9; padding-bottom:12px; margin-bottom:16px; }}
        .help-topic-body-grid {{ display:grid; grid-template-columns:1fr 1.18fr; gap:18px; align-items:start; }}
        @media (max-width:980px) {{ .help-topic-body-grid {{ grid-template-columns:1fr; }} }}
        .help-step-list {{ display:flex; flex-direction:column; gap:10px; margin:10px 0; }}
        .help-step-item {{ display:flex; align-items:flex-start; gap:10px; background:#f8fafc; border:1px solid #e2e8f0; border-left:4px solid #2563eb; border-radius:9px; padding:10px 12px; font-size:13px; line-height:1.55; color:#1e293b; }}
        .help-step-num {{ display:inline-flex; align-items:center; justify-content:center; width:24px; height:24px; border-radius:50%; background:#2563eb; color:#ffffff; font-size:12.5px; font-weight:800; flex-shrink:0; margin-top:1px; box-shadow:0 2px 6px rgba(37,99,235,0.3); }}
        .help-browser-frame {{ border:2px solid #334155; border-radius:12px; overflow:hidden; background:#0f172a; box-shadow:0 10px 26px rgba(15,23,42,0.22); }}
        .help-browser-bar {{ background:#1e293b; padding:7px 12px; display:flex; align-items:center; justify-content:space-between; gap:8px; border-bottom:1px solid #334155; color:#cbd5e1; font-size:11.5px; }}
        .help-browser-dots {{ display:flex; gap:5px; align-items:center; }}
        .help-browser-dot {{ width:10px; height:10px; border-radius:50%; display:inline-block; }}
        .help-browser-url {{ flex:1; background:#0f172a; color:#93c5fd; padding:3px 10px; border-radius:6px; font-family:Consolas,monospace; font-size:11.5px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; border:1px solid #334155; }}
        .help-screenshot-box {{ position:relative; background:#0f172a; cursor:zoom-in; overflow:hidden; border-bottom:1px solid #334155; }}
        .help-screenshot-box img {{ width:100%; height:auto; display:block; transition:transform .25s ease; }}
        .help-screenshot-box:hover img {{ transform:scale(1.015); }}
        .help-screenshot-zoom-hint {{ position:absolute; right:10px; bottom:10px; background:rgba(15,23,42,0.88); color:#ffffff; border:1px solid #60a5fa; border-radius:8px; padding:5px 10px; font-size:11.5px; font-weight:700; pointer-events:none; box-shadow:0 4px 12px rgba(0,0,0,0.4); }}
        .help-callout-legend {{ background:#f8fafc; padding:10px 12px; display:grid; grid-template-columns:repeat(auto-fit,minmax(195px,1fr)); gap:7px; border-top:1px solid #cbd5e1; }}
        .help-callout-chip {{ display:flex; align-items:center; gap:6px; font-size:12px; font-weight:700; color:#1e293b; background:#ffffff; border:1px solid #cbd5e1; border-radius:7px; padding:5px 8px; }}
        .help-callout-pin {{ display:inline-flex; align-items:center; justify-content:center; width:20px; height:20px; border-radius:50%; background:#dc2626; color:#ffffff; font-size:11px; font-weight:800; flex-shrink:0; }}
        .help-ui-diagram {{ background:#f1f5f9; padding:10px 12px; border-top:1px dashed #cbd5e1; font-size:12px; color:#334155; }}
        mark.help-highlight {{ background:#fef08a; color:#854d0e; padding:1px 4px; border-radius:4px; font-weight:800; box-shadow:0 0 0 1px #facc15; }}
        .help-faq-item {{ border:1px solid #cbd5e1; border-radius:10px; padding:12px 16px; margin-bottom:10px; background:#f8fafc; }}
        .help-faq-q {{ font-size:14px; font-weight:800; color:#1e3a8a; cursor:pointer; display:flex; align-items:center; justify-content:space-between; gap:8px; }}
        .help-faq-a {{ margin-top:8px; padding-top:8px; border-top:1px dashed #cbd5e1; font-size:13px; color:#334155; line-height:1.6; }}
    </style>
</head>
<body>
    <div class=""topbar"">
        <div class=""topbar-title"">
            <span>🃏 Azure 多人參與 Story Point 估算與工項權重管理系統</span>
            <span style=""font-size:12px;background:#1e293b;padding:4px 10px;border-radius:6px;color:#93c5fd;"">Azure RG: storyPoint | Schema: storyPoint</span>
        </div>
        <div class=""topbar-user"">
            <span>👤 {h(getCurrentEmployeeName())} ({h(getCurrentLoginAccount())}) ｜ 部門：{h(getCurrentDepartmentName())}</span>
            {roleBadge}
            <button type=""button"" class=""btn btn-outline"" style=""padding:5px 12px;font-size:12px;background:#0d9488;color:#fff;border-color:#5eead4;font-weight:800;"" onclick=""switchTab('help')"" data-tip=""開啟線上說明與操作畫面搜尋手冊：提供全站關鍵字搜尋與實機操作畫面步驟圖解"">📖 線上說明</button>
            <button type=""button"" class=""btn btn-outline"" style=""padding:5px 12px;font-size:12px;background:#2563eb;color:#fff;border-color:#60a5fa;"" onclick=""switchTab('myAccount')"" data-tip=""修改您個人的登入帳號與登入密碼"">🔑 修改我的帳密</button>
            <a href=""/logout"" onclick=""return performSystemLogout(event)"" class=""btn btn-danger"" style=""padding:5px 12px;font-size:12.5px;background:#dc2626;color:#fff;border-color:#f87171;font-weight:800;"" data-tip=""安全登出系統：清除目前登入憑證、釋放盲投議程鎖定並返回員工登入頁面"">🚪 登出系統</a>
        </div>
    </div>

    <div class=""top-menubar"">
        <div class=""top-menubar-inner"">
            <div class=""menu-group-list"">
                <!-- 主選單 1: 多人估算 -->
                <div class=""menu-dropdown"" id=""menu-group-voting"">
                    <button type=""button"" class=""menu-main-btn {(currentTab == "voting" || currentTab == "roundSetup" || currentTab == "history" ? "active-group" : "")}"" data-tip=""多人估算與過程：Story Point 即時盲投大廳、回合設定與決策歷程查詢"" onclick=""toggleDropdownMenu(event, 'menu-group-voting')"">
                        <span>🃏 多人估算</span>
                        <span class=""menu-caret"">▼</span>
                    </button>
                    <div class=""menu-sub-list"">
                        <div class=""menu-sub-header"">STORY POINT 估算與歷程</div>
                        <a class=""menu-sub-item {(currentTab == "voting" ? "active" : "")}"" data-tab=""voting"" data-tip=""{(isAdmin ? "1-1. 多人即時盲投大廳 (Planning Poker)：為工項代表類型或工項投出點數、翻牌與定案" : "1-1. 多人即時盲投大廳 (Planning Poker)：參與投票並檢視自己的出牌與定案點數（不顯示他人分數）")}"" onclick=""switchTab('voting')"">
                            <span class=""menu-sub-icon"">🃏</span>
                            <span class=""menu-sub-title"">1-1. 即時盲投大廳</span>
                        </a>
                        <a class=""menu-sub-item {(currentTab == "roundSetup" ? "active" : "")}"" data-tab=""roundSetup"" data-tip=""{(isAdmin ? "1-2. 設定盲投回合：盲投開始前設定標的、參與者名單、高低點數上下限、共識規則與計時" : "1-2. 設定盲投回合：檢視各盲投議程的參與名單、點數範圍與共識規則設定")}"" onclick=""switchTab('roundSetup')"">
                            <span class=""menu-sub-icon"">⚙️</span>
                            <span class=""menu-sub-title"">1-2. 設定盲投回合</span>
                        </a>
                        <a class=""menu-sub-item {(currentTab == "history" ? "active" : "")}"" data-tab=""history"" data-tip=""{(isAdmin ? "1-3. 查詢訂出 Story Point 的過程：事後檢視每回合出牌明細、討論備註與點數異動紀錄" : "1-3. 查詢我的投票與訂定點數過程：事後檢視我個人的投票紀錄與我的工項點數異動歷程")}"" onclick=""switchTab('history')"">
                            <span class=""menu-sub-icon"">📜</span>
                            <span class=""menu-sub-title"">1-3. 估算過程查詢</span>
                        </a>
                    </div>
                </div>

                <!-- 主選單直接快速入口：設定回合 -->
                <div class=""menu-dropdown"" id=""menu-group-roundsetup"">
                    <button type=""button"" class=""menu-main-btn {(currentTab == "roundSetup" ? "active-group" : "")}"" data-tip=""設定盲投回合 (盲投開始前設定)：設定本回合要投什麼標的、誰可參與、點數上下限範圍與共識規則"" onclick=""switchTab('roundSetup')"">
                        <span>⚙️ 設定回合</span>
                    </button>
                </div>

                <!-- 主選單 2: 工項管理 -->
                <div class=""menu-dropdown"" id=""menu-group-workitems"">
                    <button type=""button"" class=""menu-main-btn {(currentTab == "workitems" || currentTab == "types" ? "active-group" : "")}"" data-tip=""{menu2MainTip}"" onclick=""toggleDropdownMenu(event, 'menu-group-workitems')"">
                        <span>{menu2MainLabel}</span>
                        <span class=""menu-caret"">▼</span>
                    </button>
                    <div class=""menu-sub-list"">
                        <div class=""menu-sub-header"">{(isAdmin ? "工項點數與代表類型" : "我的工項與標準代表類型")}</div>
                        <a class=""menu-sub-item {(currentTab == "workitems" ? "active" : "")}"" data-tab=""workitems"" data-tip=""{menu2Sub1Desc}"" onclick=""switchTab('workitems')"">
                            <span class=""menu-sub-icon"">📋</span>
                            <span class=""menu-sub-title"">{menu2Sub1Title}</span>
                        </a>
                        <a class=""menu-sub-item {(currentTab == "types" ? "active" : "")}"" data-tab=""types"" data-tip=""{(isAdmin ? "2-2. 工項代表類型管理：管理已賦予標準 Story Point 的工項代表類型" : "2-2. 工項代表類型標準點數參考：查閱各工項代表類型的標準 Story Point 定義")}"" onclick=""switchTab('types')"">
                            <span class=""menu-sub-icon"">🏷️</span>
                            <span class=""menu-sub-title"">2-2. {(isAdmin ? "工項代表類型" : "代表類型參考")}</span>
                        </a>
                        <div class=""menu-sub-header"" style=""margin-top:6px;"">工項報表匯出</div>
                        <a class=""menu-sub-item"" href=""/export/report?type=workitem&format=csv"" data-tip=""2-3. 匯出工項報表 (CSV 格式)：下載 UTF-8 BOM CSV 工項點數清單"">
                            <span class=""menu-sub-icon"">📥</span>
                            <span class=""menu-sub-title"">2-3. 匯出工項 (CSV)</span>
                        </a>
                        <a class=""menu-sub-item"" href=""/export/report?type=workitem&format=excel"" data-tip=""2-4. 匯出工項報表 (Excel .xlsx 格式)：下載標準 OpenXML Excel 活頁簿"">
                            <span class=""menu-sub-icon"">📊</span>
                            <span class=""menu-sub-title"">2-4. 匯出工項 (Excel)</span>
                        </a>
                        <a class=""menu-sub-item"" href=""/export/report?type=workitem&format=html"" data-tip=""2-5. 匯出工項互動 HTML 報表：支援點擊表頭排序、多條件篩選與動態群組彙總統計"">
                            <span class=""menu-sub-icon"">🌐</span>
                            <span class=""menu-sub-title"">2-5. 匯出工項 (HTML)</span>
                        </a>
                    </div>
                </div>

                <!-- 主選單 3: 統計報表 -->
                <div class=""menu-dropdown"" id=""menu-group-stats"">
                    <button type=""button"" class=""menu-main-btn {(currentTab == "stats" ? "active-group" : "")}"" data-tip=""{menu3MainTip}"" onclick=""toggleDropdownMenu(event, 'menu-group-stats')"">
                        <span>{menu3MainLabel}</span>
                        <span class=""menu-caret"">▼</span>
                    </button>
                    <div class=""menu-sub-list"">
                        <div class=""menu-sub-header"">{(isAdmin ? "員工與部門點數統計" : "個人工項與分數查詢")}</div>
                        <a class=""menu-sub-item {(currentTab == "stats" ? "active" : "")}"" data-tab=""stats"" data-tip=""{menu3Sub1Desc}"" onclick=""switchTab('stats')"">
                            <span class=""menu-sub-icon"">📊</span>
                            <span class=""menu-sub-title"">{menu3Sub1Title}</span>
                        </a>");
        if (isAdmin)
        {
            sb.Append(@"
                        <div class=""menu-sub-header"" style=""margin-top:6px;"">部門總點數匯出</div>
                        <a class=""menu-sub-item"" href=""/export/report?type=department&format=csv"" data-tip=""3-2A. 匯出部門總點數 (CSV 格式)：下載各部門 Story Point 負載 CSV 報表"">
                            <span class=""menu-sub-icon"">📥</span>
                            <span class=""menu-sub-title"">3-2A. 匯出部門 (CSV)</span>
                        </a>
                        <a class=""menu-sub-item"" href=""/export/report?type=department&format=excel"" data-tip=""3-2B. 匯出部門總點數 (Excel .xlsx 格式)：下載各部門 Story Point 負載 Excel 活頁簿"">
                            <span class=""menu-sub-icon"">📊</span>
                            <span class=""menu-sub-title"">3-2B. 匯出部門 (Excel)</span>
                        </a>
                        <a class=""menu-sub-item"" href=""/export/report?type=department&format=html"" data-tip=""3-2C. 匯出部門互動 HTML (排序・篩選・群組彙總)：可於瀏覽器即時排序、篩選與群組彙總分析"">
                            <span class=""menu-sub-icon"">🌐</span>
                            <span class=""menu-sub-title"">3-2C. 匯出部門 (HTML)</span>
                        </a>");
        }
        sb.Append($@"
                        <div class=""menu-sub-header"" style=""margin-top:6px;"">員工點數匯出</div>
                        <a class=""menu-sub-item"" href=""/export/report?type=employee&format=csv"" data-tip=""{menu3Sub3Desc}"">
                            <span class=""menu-sub-icon"">📥</span>
                            <span class=""menu-sub-title"">{menu3Sub3Title}</span>
                        </a>
                        <a class=""menu-sub-item"" href=""/export/report?type=employee&format=excel"" data-tip=""{(isAdmin ? "3-3B. 匯出員工個人總點數 Excel (.xlsx)：下載標準 OpenXML Excel 報表格式" : "3-2B. 匯出我的個人工項與分數 Excel (.xlsx)：下載標準 OpenXML Excel 報表格式")}"">
                            <span class=""menu-sub-icon"">📊</span>
                            <span class=""menu-sub-title"">{(isAdmin ? "3-3B. 匯出員工 (Excel)" : "3-2B. 匯出個人 (Excel)")}</span>
                        </a>
                        <a class=""menu-sub-item"" href=""/export/report?type=employee&format=html"" data-tip=""{(isAdmin ? "3-3C. 匯出員工總點數互動 HTML：支援欄位排序、條件篩選與依部門/角色群組彙總" : "3-2C. 匯出我的個人分數互動 HTML：支援欄位排序、條件篩選與群組彙總")}"">
                            <span class=""menu-sub-icon"">🌐</span>
                            <span class=""menu-sub-title"">{(isAdmin ? "3-3C. 匯出員工 (HTML)" : "3-2C. 匯出個人 (HTML)")}</span>
                        </a>
                    </div>
                </div>

                <!-- 主選單直接快速入口：修改帳密 -->
                <div class=""menu-dropdown"" id=""menu-group-myaccount"">
                    <button type=""button"" class=""menu-main-btn {(currentTab == "myAccount" ? "active-group" : "")}"" data-tip=""修改我的帳密：每個人皆可隨時修改自己的個人登入帳號 (loginAccount) 與登入密碼"" onclick=""switchTab('myAccount')"">
                        <span>🔑 修改帳密</span>
                    </button>
                </div>");

        if (isAdmin)
        {
            int errLogCount = 0;
            foreach (DataRow lr in logTable.Rows)
            {
                if (lr["logLevel"] != DBNull.Value && Convert.ToInt32(lr["logLevel"]) == 1)
                {
                    errLogCount++;
                }
            }
            string errCountBadge = errLogCount > 0
                ? $"<span style='background:#dc2626;color:#fff;padding:1px 7px;border-radius:999px;font-size:11px;font-weight:800;'>{errLogCount}</span>"
                : "<span style='background:#16a34a;color:#fff;padding:1px 7px;border-radius:999px;font-size:11px;font-weight:800;'>0</span>";

            sb.Append($@"
                <!-- 主選單 4: 組織與員工 (管理者可見全域管理 + 個人帳密修改) -->
                <div class=""menu-dropdown"" id=""menu-group-employees"">
                    <button type=""button"" class=""menu-main-btn {(currentTab == "employees" || currentTab == "myAccount" ? "active-group" : "")}"" data-tip=""組織與員工帳密管理：管理部門主管註記、上下層樹狀階層、員工列表與 CSV / Excel 批次匯入"" onclick=""toggleDropdownMenu(event, 'menu-group-employees')"">
                        <span>👥 組織與員工</span>
                        <span class=""menu-caret"">▼</span>
                    </button>
                    <div class=""menu-sub-list"">
                        <div class=""menu-sub-header"">帳密修改、員工管理與批次匯入</div>
                        <a class=""menu-sub-item {(currentTab == "myAccount" ? "active" : "")}"" data-tab=""myAccount"" data-tip=""4-0. 修改我的個人登入帳號與密碼：每個人皆可隨時修改自己的登入帳號 (loginAccount) 與登入密碼"" onclick=""switchTab('myAccount')"">
                            <span class=""menu-sub-icon"">🔑</span>
                            <span class=""menu-sub-title"">4-0. 修改我的帳密</span>
                        </a>
                        <a class=""menu-sub-item {(currentTab == "employees" ? "active" : "")}"" data-tab=""employees"" data-tip=""4-1. 員工列表與登入帳密管理：增刪修查、部門主管設定、樹狀階層、CSV 與 Excel 兩種批次匯入與三種匯出"" onclick=""switchTab('employees')"">
                            <span class=""menu-sub-icon"">👥</span>
                            <span class=""menu-sub-title"">4-1. 員工與組織管理</span>
                        </a>
                        <a class=""menu-sub-item"" href=""/employee/import/template?format=csv"" data-tip=""4-2A. 下載員工批次匯入 CSV 範本：取得標準 UTF-8 BOM CSV 欄位格式範本"">
                            <span class=""menu-sub-icon"">📄</span>
                            <span class=""menu-sub-title"">4-2A. 員工 CSV 範本</span>
                        </a>
                        <a class=""menu-sub-item"" href=""/employee/import/template?format=excel"" data-tip=""4-2B. 下載員工批次匯入 Excel (.xlsx) 範本：取得標準 Excel .xlsx 欄位格式範本"">
                            <span class=""menu-sub-icon"">📊</span>
                            <span class=""menu-sub-title"">4-2B. 員工 Excel 範本</span>
                        </a>
                        <div class=""menu-sub-header"" style=""margin-top:6px;"">工作階段與安全登出</div>
                        <a class=""menu-sub-item"" href=""/logout"" onclick=""return performSystemLogout(event)"" data-tip=""4-3. 安全登出系統：清除目前登入狀態、釋放盲投議程鎖定並返回登入畫面"" style=""color:#dc2626;font-weight:800;"">
                            <span class=""menu-sub-icon"">🚪</span>
                            <span class=""menu-sub-title"">4-3. 登出系統 (切換帳號)</span>
                        </a>
                    </div>
                </div>

                <!-- 主選單 5: 系統 Log (僅管理者可見) -->
                <div class=""menu-dropdown"" id=""menu-group-logs"">
                    <button type=""button"" class=""menu-main-btn {(currentTab == "logs" ? "active-group" : "")}"" data-tip=""系統與錯誤 Log 查詢中心：系統防崩潰保護、錯誤異常追蹤與 Log 三格式匯出 (目前錯誤數：{errLogCount} 筆)"" onclick=""toggleDropdownMenu(event, 'menu-group-logs')"">
                        <span>🛡️ 系統 Log</span>
                        {errCountBadge}
                        <span class=""menu-caret"">▼</span>
                    </button>
                    <div class=""menu-sub-list"">
                        <div class=""menu-sub-header"">防崩潰保護與 Log 查詢匯出</div>
                        <a class=""menu-sub-item {(currentTab == "logs" ? "active" : "")}"" data-tab=""logs"" data-tip=""5-1. 系統與錯誤 Log 查詢中心 (tbLog / 本地雙軌備援)：查詢錯誤碼、行號、例外訊息、執行耗時與防崩潰攔截測試"" onclick=""switchTab('logs')"">
                            <span class=""menu-sub-icon"">🛡️</span>
                            <span class=""menu-sub-title"">5-1. 系統 Log 查詢</span>
                        </a>
                        <a class=""menu-sub-item"" href=""/export/report?type=log&format=csv"" data-tip=""5-2A. 匯出系統與錯誤 Log (CSV 格式)：下載 UTF-8 BOM CSV 格式系統 Log"">
                            <span class=""menu-sub-icon"">📥</span>
                            <span class=""menu-sub-title"">5-2A. 匯出 Log (CSV)</span>
                        </a>
                        <a class=""menu-sub-item"" href=""/export/report?type=log&format=excel"" data-tip=""5-2B. 匯出系統與錯誤 Log (Excel .xlsx 格式)：下載標準 OpenXML Excel 系統 Log 報表"">
                            <span class=""menu-sub-icon"">📊</span>
                            <span class=""menu-sub-title"">5-2B. 匯出 Log (Excel)</span>
                        </a>
                        <a class=""menu-sub-item"" href=""/export/report?type=log&format=html"" data-tip=""5-2C. 匯出系統 Log 互動 HTML：依錯誤層級、來源模組或物件群組彙總分析與欄位鎖定"">
                            <span class=""menu-sub-icon"">🌐</span>
                            <span class=""menu-sub-title"">5-2C. 匯出 Log (HTML)</span>
                        </a>
                    </div>
                </div>");
        }
        else
        {
            sb.Append($@"
                <!-- 主選單 4: 帳號設定 (參與者可見) -->
                <div class=""menu-dropdown"" id=""menu-group-employees"">
                    <button type=""button"" class=""menu-main-btn {(currentTab == "employees" || currentTab == "myAccount" ? "active-group" : "")}"" data-tip=""個人帳密設定：檢視個人基本資料並隨時修改自己的登入帳號與密碼"" onclick=""toggleDropdownMenu(event, 'menu-group-employees')"">
                        <span>🔑 帳號設定</span>
                        <span class=""menu-caret"">▼</span>
                    </button>
                    <div class=""menu-sub-list"">
                        <div class=""menu-sub-header"">個人帳密設定</div>
                        <a class=""menu-sub-item {(currentTab == "myAccount" ? "active" : "")}"" data-tab=""myAccount"" data-tip=""4-1. 修改我的個人登入帳號與密碼：每個人皆可隨時修改自己的登入帳號 (loginAccount) 與登入密碼"" onclick=""switchTab('myAccount')"">
                            <span class=""menu-sub-icon"">🔑</span>
                            <span class=""menu-sub-title"">4-1. 修改我的帳密</span>
                        </a>
                        <a class=""menu-sub-item {(currentTab == "employees" ? "active" : "")}"" data-tab=""employees"" data-tip=""4-2. 個人帳號資訊與修改帳密專頁：檢視個人基本資料並修改自己的登入帳號與密碼"" onclick=""switchTab('employees')"">
                            <span class=""menu-sub-icon"">👤</span>
                            <span class=""menu-sub-title"">4-2. 個人帳號資訊</span>
                        </a>
                        <div class=""menu-sub-header"" style=""margin-top:6px;"">工作階段與安全登出</div>
                        <a class=""menu-sub-item"" href=""/logout"" onclick=""return performSystemLogout(event)"" data-tip=""4-3. 安全登出系統：清除目前登入狀態、釋放盲投議程鎖定並返回登入畫面"" style=""color:#dc2626;font-weight:800;"">
                            <span class=""menu-sub-icon"">🚪</span>
                            <span class=""menu-sub-title"">4-3. 登出系統 (切換帳號)</span>
                        </a>
                    </div>
                </div>");
        }

        sb.Append($@"
                <!-- 主選單 6: 線上說明 (含搜尋與搭配操作畫面) -->
                <div class=""menu-dropdown"" id=""menu-group-help"">
                    <button type=""button"" class=""menu-main-btn {(currentTab == "help" ? "active-group" : "")}"" data-tip=""系統線上說明與互動操作手冊：提供全站關鍵字搜尋、12 大章節搭配實機操作畫面與常見問題 FAQ"" onclick=""toggleDropdownMenu(event, 'menu-group-help')"">
                        <span>📖 線上說明</span>
                        <span class=""menu-caret"">▼</span>
                    </button>
                    <div class=""menu-sub-list"">
                        <div class=""menu-sub-header"">線上說明搜尋與操作畫面圖解</div>
                        <a class=""menu-sub-item {(currentTab == "help" ? "active" : "")}"" data-tab=""help"" data-tip=""6-1. 線上說明與全站搜尋中心：關鍵字即時搜尋、高亮反白與 12 幅實機操作畫面詳解"" onclick=""openHelpTopic('')"">
                            <span class=""menu-sub-icon"">📖</span>
                            <span class=""menu-sub-title"">6-1. 線上說明搜尋中心</span>
                        </a>
                        <a class=""menu-sub-item"" data-tip=""6-2. 登入驗證與個人帳密修改操作畫面圖解"" onclick=""openHelpTopic('topic-auth')"">
                            <span class=""menu-sub-icon"">🔐</span>
                            <span class=""menu-sub-title"">6-2. 登入與帳密操作畫面</span>
                        </a>
                        <a class=""menu-sub-item"" data-tip=""6-3. 多人即時盲投大廳 (待參與/參與中) 與獨立盲投網頁操作畫面圖解"" onclick=""openHelpTopic('topic-voting-lobby')"">
                            <span class=""menu-sub-icon"">🃏</span>
                            <span class=""menu-sub-title"">6-3. 多人盲投與獨立網頁畫面</span>
                        </a>
                        <a class=""menu-sub-item"" data-tip=""6-4. 盲投回合設定、撲克牌意義與共識規則操作畫面圖解"" onclick=""openHelpTopic('topic-round-setup')"">
                            <span class=""menu-sub-icon"">⚙️</span>
                            <span class=""menu-sub-title"">6-4. 回合設定與牌組畫面</span>
                        </a>
                        <a class=""menu-sub-item"" data-tip=""6-5. 工項管理、代表類型自動帶點、到期自動扣點寄信與右鍵 Modal 單筆編輯操作畫面圖解"" onclick=""openHelpTopic('topic-workitem')"">
                            <span class=""menu-sub-icon"">📋</span>
                            <span class=""menu-sub-title"">6-5. 工項與右鍵 Modal 畫面</span>
                        </a>
                        <a class=""menu-sub-item"" data-tip=""6-6. 員工/部門點數統計、表頭排序與欄位鎖定、CSV/Excel/HTML 報表匯出操作畫面圖解"" onclick=""openHelpTopic('topic-stats-export')"">
                            <span class=""menu-sub-icon"">📊</span>
                            <span class=""menu-sub-title"">6-6. 統計報表與表頭鎖定畫面</span>
                        </a>
                        <a class=""menu-sub-item"" data-tip=""6-7. 組織與部門主管樹狀階層、批次匯入、系統 Log 與 PowerShell GUI 操作畫面圖解"" onclick=""openHelpTopic('topic-org-employees')"">
                            <span class=""menu-sub-icon"">👥</span>
                            <span class=""menu-sub-title"">6-7. 組織主管與 Log 畫面</span>
                        </a>
                        <div class=""menu-sub-header"" style=""margin-top:6px;"">獨立視窗檢視</div>
                        <a class=""menu-sub-item"" href=""/help"" target=""_blank"" data-tip=""6-8. 於瀏覽器新分頁開啟獨立線上說明與搜尋專頁 (/help)"">
                            <span class=""menu-sub-icon"">🗗</span>
                            <span class=""menu-sub-title"">6-8. 獨立視窗開啟說明 (/help)</span>
                        </a>
                    </div>
                </div>
            </div>

            <div class=""current-location-bar"">
                <span>位置：</span>
                <span class=""current-location-pill"" id=""currentMenuTitlePill"" data-tip=""{(currentTab switch
                {
                    "roundSetup" => "1-2. 設定盲投回合：設定估算標的、參與者名單、高低點數上下限、共識規則與計時",
                    "history" => isAdmin ? "1-3. 查詢訂出 Story Point 的過程：檢視每回合出牌明細、討論備註與點數異動紀錄" : "1-3. 查詢我的投票與訂定點數過程：檢視個人投票紀錄與工項點數異動歷程",
                    "workitems" => isAdmin ? "2-1. 工項點數查詢與賦予員工設定：查詢各工項點數、選擇代表類型自動帶入或修改員工指派" : "2-1. 我的工項與個人分數查詢：顯示賦予給我本人的工項與個人配額分數",
                    "types" => isAdmin ? "2-2. 工項代表類型管理：管理已賦予標準 Story Point 的工項代表類型" : "2-2. 工項代表類型標準點數參考：查閱各工項代表類型的標準 Story Point 定義",
                    "stats" => isAdmin ? "3-1. 員工與部門總 Story Point 統計：查詢員工身上的點數與所屬部門的總 Story Point 數" : "3-1. 我的個人總 Story Point 分數統計：查看個人工項數量與各狀態 Story Point 分數",
                    "myAccount" => "修改我的個人登入帳號與密碼：每個人皆可隨時修改自己的登入帳號與密碼",
                    "employees" => isAdmin ? "4-1. 員工列表與登入帳密管理：增刪修查、部門主管設定、樹狀階層與批次匯入" : "4-1. 修改我的個人登入帳號與密碼：檢視個人基本資料並修改帳密",
                    "logs" => "5-1. 系統與錯誤 Log 查詢中心 (tbLog)：查詢錯誤碼、行號、例外訊息與防崩潰保護紀錄",
                    "help" => "6-1. 系統線上說明與操作畫面搜尋手冊：支援關鍵字全文搜尋與 12 幅實機操作畫面詳解",
                    _ => "1-1. 多人即時盲投大廳 (Planning Poker)：參與盲投出牌、同步翻牌揭曉與拍板定案"
                })}"">
                    {(currentTab switch
                    {
                        "roundSetup" => "⚙️ 1-2. 設定盲投回合",
                        "history" => "📜 1-3. 估算過程查詢",
                        "workitems" => isAdmin ? "📋 2-1. 工項與員工指派" : "📋 2-1. 我的工項查詢",
                        "types" => isAdmin ? "🏷️ 2-2. 工項代表類型" : "🏷️ 2-2. 代表類型參考",
                        "stats" => isAdmin ? "📊 3-1. 員工與部門統計" : "📊 3-1. 我的分數統計",
                        "myAccount" => "🔑 修改我的帳密",
                        "employees" => isAdmin ? "👥 4-1. 員工與組織管理" : "🔑 4-1. 修改我的帳密",
                        "logs" => "🛡️ 5-1. 系統 Log 查詢",
                        "help" => "📖 6-1. 線上說明與搜尋",
                        _ => "🃏 1-1. 即時盲投大廳"
                    })}
                </span>
                <button type=""button"" onclick=""switchTab('help')"" class=""btn btn-outline"" style=""padding:4px 9px;font-size:12px;background:#0d9488;color:#fff;border-color:#5eead4;font-weight:800;margin-left:4px;white-space:nowrap;"" data-tip=""開啟線上說明與操作畫面搜尋手冊"">📖 說明</button>
                <a href=""/logout"" onclick=""return performSystemLogout(event)"" class=""btn btn-danger"" style=""padding:4px 10px;font-size:12px;background:#dc2626;color:#fff;border-color:#f87171;font-weight:800;margin-left:4px;white-space:nowrap;"" data-tip=""安全登出系統：清除目前登入狀態、釋放盲投議程鎖定並返回員工帳號登入頁面"">🚪 登出</a>
            </div>
        </div>
    </div>

    <div class=""container"">
        <div id=""liveSignalRBanner"" class=""alert alert-live""></div>");

        if (!string.IsNullOrWhiteSpace(msg))
        {
            sb.Append($"<div class='alert alert-ok'>✅ {h(msg)}</div>");
        }
        if (!string.IsNullOrWhiteSpace(err))
        {
            string adminLogJumpBtn = isAdmin
                ? @" <button type='button' class='btn btn-danger' style='padding:4px 10px;font-size:12px;margin-left:10px;' onclick=""switchTab('logs')"">🔍 前往系統與錯誤 Log 查詢中心檢視詳情</button>"
                : "";
            sb.Append($"<div class='alert alert-err' style='display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:8px;'><span>⚠️ {h(err)}</span>{adminLogJumpBtn}</div>");
        }

        if (!isAdmin)
        {
            sb.Append(@"<div class='alert' style='background:#eff6ff;border:1px solid #bfdbfe;color:#1e3a8a;margin-bottom:16px;'>
                🔒 <strong>個人資料隱私保護模式：</strong>您目前以「個別員工 (參與者)」身分登入，可參與投票並查看<strong>您本人的投票紀錄、賦予給您的工項與個人 Story Point 分數</strong>；系統已自動隱藏其他員工的工項、分數與出牌紀錄。
            </div>");
        }

        // KPI Cards
        sb.Append(@"<div class=""kpi-grid"">");
        if (isAdmin)
        {
            sb.Append($@"
            <div class=""kpi-card"">
                <div class=""kpi-label"">🏢 全公司各部門總 Story Point</div>
                <div class=""kpi-val"">{totalCompanyPoints:0.##} <span style=""font-size:14px;font-weight:600;"">pt</span></div>
                <div class=""kpi-sub"">涵蓋 {deptTable.Rows.Count} 個部門、{itemTable.Rows.Count} 個實際工項</div>
            </div>");
        }
        sb.Append($@"
            <div class=""kpi-card"">
                <div class=""kpi-label"">🙋 我身上的總 Story Point 分數</div>
                <div class=""kpi-val"">{myPoints:0.##} <span style=""font-size:14px;font-weight:600;"">pt</span></div>
                <div class=""kpi-sub"">目前賦予給我本人共 {myItems} 個工項</div>
            </div>");
        if (!isAdmin)
        {
            sb.Append($@"
            <div class=""kpi-card"">
                <div class=""kpi-label"">📋 我的各狀態工項分數明細</div>
                <div class=""kpi-val"" style=""font-size:19px;margin-top:8px;"">待處理 {myTodoPoints:0.##} ｜ 進行中 {myInProgressPoints:0.##} ｜ 完成 {myDonePoints:0.##}</div>
                <div class=""kpi-sub"">僅統計我個人負責的工項配額點數</div>
            </div>");
        }
        sb.Append($@"
            <div class=""kpi-card"">
                <div class=""kpi-label"">🏷️ 已定義工項代表類型</div>
                <div class=""kpi-val"">{typeTable.Rows.Count} <span style=""font-size:14px;font-weight:600;"">種代表類型</span></div>
                <div class=""kpi-sub"">建立工項選取後自動帶入標準點數</div>
            </div>
            <div class=""kpi-card"">
                <div class=""kpi-label"">🃏 多人投票議程與過程紀錄</div>
                <div class=""kpi-val"">{sessionTable.Rows.Count} <span style=""font-size:14px;font-weight:600;"">場議程</span></div>
                <div class=""kpi-sub"">{(isAdmin ? "完整保留每回合出牌與共識收斂過程" : "可參與盲投並檢視自己的出牌紀錄")}</div>
            </div>
        </div>");

        // =====================================================================
        // TAB 1: 多人即時盲投大廳 (分成「待參與」與「參與中」：由個人從「待參與」點選，才會進入「參與中」的獨立網頁)
        // =====================================================================
        DataRow? activeSessionRow = null;
        var ongoingSessions = new List<DataRow>();
        var finalizedSessions = new List<DataRow>();
        foreach (DataRow r in sessionTable.Rows)
        {
            int sPk = Convert.ToInt32(r["votingSessionPk"]);
            if (sPk == activeSessionPk)
            {
                activeSessionRow = r;
            }
            string st = r["sessionStatus"]?.ToString() ?? "";
            if (st == "finalized")
            {
                finalizedSessions.Add(r);
            }
            else
            {
                ongoingSessions.Add(r);
            }
        }

        var myLockedSessionSet = sqlDataService.getActiveLockedSessionPksForEmployee(curEmpPk).ToHashSet();
        var pendingToJoinSessions = new List<DataRow>();
        var participatingSessions = new List<DataRow>();
        foreach (DataRow r in ongoingSessions)
        {
            int sPk = Convert.ToInt32(r["votingSessionPk"]);
            if (myLockedSessionSet.Contains(sPk))
            {
                participatingSessions.Add(r);
            }
            else
            {
                pendingToJoinSessions.Add(r);
            }
        }

        sb.Append($"<div id='pane-voting' class='tab-pane {(currentTab == "voting" ? "active" : "")}'>");
        sb.Append("<div class='grid-voting' id='votingGridContainer'>");

        // 左側：投票議程清單 (分成「📂 待參與」、「🟢 參與中」與「📁 已拍板定案議程」三個可展開收合 Folder；個人從「待參與」點選後才會進入「參與中」的獨立網頁)
        sb.Append("<div class='card voting-sidebar-card' id='votingSidebarCard'>");
        sb.Append(@"
            <div class='voting-sidebar-collapsed-strip' onclick='toggleVotingSidebarWidth()' title='點擊展開投票議程清單邊欄'>
                <span>▶ 展開投票議程清單 (" + sessionTable.Rows.Count + @")</span>
            </div>
            <div class='voting-sidebar-full-content'>
                <div class='card-title' style='margin-bottom:10px;'>
                    <span>📌 投票議程清單</span>
                    <div style='display:flex;gap:4px;align-items:center;flex-wrap:wrap;'>
                        <button type='button' class='btn-item-toggle' style='background:#eff6ff;border-color:#93c5fd;color:#1d4ed8;' onclick='refreshVotingSessionsNow(this)' title='立即重新整理並載入屬於您的最新投票議程'>🔄 重新整理</button>
                        <button type='button' class='btn-item-toggle' onclick='expandAllSessionFolders(true)' title='展開全部資料夾與邊項'>➕ 全展開</button>
                        <button type='button' class='btn-item-toggle' onclick='expandAllSessionFolders(false)' title='收合全部資料夾與邊項'>➖ 全收合</button>
                        <button type='button' class='btn-item-toggle' id='btnToggleSidebarBody' onclick='toggleVotingSidebarBody()' title='垂直收合或展開議程清單內容'>▴ 收合清單</button>
                        <button type='button' class='btn-item-toggle' onclick='toggleVotingSidebarWidth()' title='將左側投票議程邊欄水平收合，放大右側投票區'>◀ 縮起邊欄</button>
                    </div>
                </div>
                <div class='voting-sidebar-body' id='votingSidebarBody'>");

        // 輔助函式：渲染單一投票議程邊項（個人從「待參與」點選，才會進入「參與中」的獨立網頁 + 邊項本身展開/收合詳情 + 防重複進入標示）
        void renderSessionAgendaItem(DataRow sRow)
        {
            int sPk = Convert.ToInt32(sRow["votingSessionPk"]);
            string sStatus = sRow["sessionStatus"].ToString() ?? "";
            bool isFinalized = sStatus == "finalized";
            string badgeCls = sStatus == "voting" ? "badge-voting" : (sStatus == "revealed" ? "badge-revealed" : "badge-finalized");
            bool isAlreadyOpen = !isFinalized && myLockedSessionSet.Contains(sPk);
            string activeBorder = isAlreadyOpen
                ? "border:2px solid #16a34a;background:#f0fdf4;"
                : "border:1.5px solid #cbd5e1;background:#ffffff;";
            string finalPtStr = sRow["finalStoryPoint"] != DBNull.Value ? $"｜定案: <strong>{sRow["finalStoryPoint"]} pt</strong>" : $"｜第 {sRow["currentRoundNumber"]} 回合";
            decimal minPt = sRow.Table.Columns.Contains("minStoryPoint") && sRow["minStoryPoint"] != DBNull.Value ? Convert.ToDecimal(sRow["minStoryPoint"]) : 0m;
            decimal maxPt = sRow.Table.Columns.Contains("maxStoryPoint") && sRow["maxStoryPoint"] != DBNull.Value ? Convert.ToDecimal(sRow["maxStoryPoint"]) : 21m;
            bool restrictPart = sRow.Table.Columns.Contains("restrictToParticipant") && sRow["restrictToParticipant"] != DBNull.Value && Convert.ToBoolean(sRow["restrictToParticipant"]);
            int partCnt = sRow.Table.Columns.Contains("participantCount") && sRow["participantCount"] != DBNull.Value ? Convert.ToInt32(sRow["participantCount"]) : 0;
            string consensusText = sRow.Table.Columns.Contains("consensusRuleText") ? (sRow["consensusRuleText"]?.ToString() ?? "") : "";
            string goalNote = sRow.Table.Columns.Contains("roundGoalNote") ? (sRow["roundGoalNote"]?.ToString() ?? "") : "";
            string decisionNote = sRow.Table.Columns.Contains("finalDecisionNote") && sRow["finalDecisionNote"] != DBNull.Value
                ? (sRow["finalDecisionNote"]?.ToString() ?? "").Trim()
                : "";
            string itemDetailCollapsed = isFinalized ? "collapsed" : "";
            string toggleBtnText = isFinalized ? "▾ 邊項詳情" : "▴ 收合詳情";
            string jsSafeTitle = escapeJsString($"{sRow["sessionCode"]} - {sRow["sessionTitle"]}");

            string stateBadgeHtml = isFinalized
                ? ""
                : $@"<span data-session-open-badge=""{sPk}"" class=""badge"" style=""display:inline-block;{(isAlreadyOpen ? "background:#dcfce7;color:#15803d;border:1px solid #86efac;" : "background:#eff6ff;color:#1d4ed8;border:1px solid #93c5fd;")}"">{(isAlreadyOpen ? "🟢 參與中" : "⏳ 待參與")}</span>";

            string titleHref = isFinalized ? $"/?tab=voting&sessionPk={sPk}" : $"/voting/session?sessionPk={sPk}";
            string titleTargetAttr = isFinalized ? "" : @"target=""_blank"" rel=""noopener""";
            string titleOnClickAttr = isFinalized ? "" : $@"onclick=""return openIndependentVotingSession(event, {sPk}, '{jsSafeTitle}')"" title=""點選從「待參與」進入「參與中」的獨立盲投網頁""";

            sb.Append($@"
            <div class=""session-item-card"" id=""sidebarSessionCard_{sPk}"" data-session-pk=""{sPk}"" data-session-status=""{h(sStatus)}"" style=""{activeBorder}"">
                <div style=""display:flex;justify-content:space-between;align-items:center;margin-bottom:4px;gap:6px;flex-wrap:wrap;"">
                    <a href=""{titleHref}"" {titleTargetAttr} data-session-title-link=""{sPk}"" {titleOnClickAttr} style=""text-decoration:none;color:#1e293b;font-weight:700;font-size:13px;"">
                        {h(sRow["sessionCode"])}
                    </a>
                    <div style=""display:flex;align-items:center;gap:4px;flex-wrap:wrap;"">
                        {stateBadgeHtml}
                        <span class=""badge {badgeCls}"">{h(sRow["sessionStatusText"])}</span>
                        <button type=""button"" class=""btn-item-toggle"" onclick=""toggleSessionItemDetail(event, {sPk}, this)"">{toggleBtnText}</button>
                    </div>
                </div>
                <a href=""{titleHref}"" {titleTargetAttr} data-session-title-link=""{sPk}"" {titleOnClickAttr} style=""text-decoration:none;color:inherit;display:block;"">
                    <div style=""font-size:13px;font-weight:700;margin-bottom:3px;color:#0f172a;"">{h(sRow["sessionTitle"])}</div>
                    <div style=""font-size:12px;color:#64748b;"">{h(sRow["targetKindText"])}{finalPtStr}</div>
                    {(!string.IsNullOrWhiteSpace(decisionNote) ? $@"<div style=""font-size:12px;color:#15803d;font-weight:700;margin-top:3px;"">🏆 定案決議說明：{h(decisionNote)}</div>" : "")}
                </a>
                <div id=""session-detail-{sPk}"" class=""session-item-detail {itemDetailCollapsed}"">
                    <div>🎯 <strong>估算標的：</strong>{h(sRow["targetDisplayName"])}</div>
                    <div>📏 <strong>點數範圍：</strong>{minPt:0.##} ~ {maxPt:0.##} pt ｜ <strong>共識規則：</strong>{h(consensusText)}</div>
                    <div>👥 <strong>誰可參與：</strong>{(restrictPart ? $"僅限指定名單 ({partCnt} 人)" : $"全體啟用員工 (目前 {partCnt} 人)")}</div>
                    {(!string.IsNullOrWhiteSpace(goalNote) ? $"<div>📝 <strong>回合重點：</strong>{h(goalNote)}</div>" : "")}
                    {(!string.IsNullOrWhiteSpace(decisionNote) ? $@"<div style=""color:#166534;background:#dcfce7;border:1px solid #86efac;padding:4px 8px;border-radius:6px;margin-top:2px;"">🏆 <strong>定案決議說明：</strong>{h(decisionNote)}</div>" : "")}
                    <div style=""display:flex;gap:6px;margin-top:4px;flex-wrap:wrap;"">
                        <a href=""/voting/session?sessionPk={sPk}"" target=""_blank"" rel=""noopener"" data-session-enter-btn=""{sPk}"" data-session-title=""{h(sRow["sessionCode"] + " - " + sRow["sessionTitle"])}"" onclick=""return openIndependentVotingSession(event, {sPk}, '{jsSafeTitle}')"" class=""btn {(isAlreadyOpen ? "btn-outline" : "btn-primary")}"" style=""padding:4px 9px;font-size:11.5px;{(isAlreadyOpen ? "background:#dcfce7;color:#15803d;border-color:#86efac;" : "")}"">
                            {(isFinalized ? "🔍 檢視此定案 Session 獨立頁" : (isAlreadyOpen ? "🟢 參與中 (獨立網頁進行中・禁重複進入)" : "🚀 點選進入「參與中」獨立網頁"))}
                        </a>
                        {(!isFinalized ? $"<button type='button' data-session-leave-btn='{sPk}' onclick='leaveParticipatingSessionFromLobby(event, {sPk})' class='btn btn-outline' style='display:{(isAlreadyOpen ? "inline-block" : "none")};padding:4px 8px;font-size:11.5px;color:#dc2626;border-color:#fca5a5;background:#fff;' title='結束本場議程的獨立網頁參與狀態並移回「待參與」'>🚪 結束參與</button>" : "")}
                        {(isAdmin ? $"<button type='button' class='btn btn-outline' style='padding:4px 8px;font-size:11.5px;color:#1d4ed8;border-color:#93c5fd;background:#eff6ff;' onclick=\"triggerRevoteFromTarget('session', 0, {sPk}, '{jsSafeTitle}', false, 'voting')\" title='針對此議程啟動重新投票（開啟新一輪盲投）'>🔄 重新投票</button>" : "")}
                        {(isAdmin && !isFinalized ? $"<button type='button' class='btn btn-outline' style='padding:4px 8px;font-size:11.5px;' onclick='openRoundSetupForSession({sPk})'>⚙️ 修改回合設定</button>" : "")}
                    </div>
                </div>
            </div>");
        }

        // Folder 1: 待參與 (個人尚未點選進入獨立網頁的進行中議程；從此處點選才會進入「參與中」的獨立網頁)
        sb.Append($@"
                    <div class=""agenda-folder"" id=""folder-pending-join"">
                        <div class=""agenda-folder-header"" onclick=""toggleSessionFolder('folder-pending-join')"">
                            <div>
                                <span class=""agenda-folder-caret"">▼</span>
                                <span>📂 待參與</span>
                            </div>
                            <span id=""sidebarPendingCountBadge"" class=""badge badge-voting"">{pendingToJoinSessions.Count} 場</span>
                        </div>
                        <div class=""agenda-folder-body"" id=""sidebarFolderBodyPending"">
                            <div id=""sidebarEmptyPendingMsg"" style=""display:{(pendingToJoinSessions.Count == 0 ? "block" : "none")};font-size:12.5px;color:#64748b;padding:8px;text-align:center;"">
                                {(ongoingSessions.Count == 0 ? "目前無進行中盲投議程，可點選上方「⚙️ 設定回合」發起新回合。" : "目前無「待參與」議程（您已點選進入下方「🟢 參與中」獨立網頁）。")}
                            </div>");
        foreach (DataRow sRow in pendingToJoinSessions)
        {
            renderSessionAgendaItem(sRow);
        }
        sb.Append(@"
                        </div>
                    </div>");

        // Folder 2: 參與中 (個人已從「待參與」點選、目前正在獨立網頁進行中的議程)
        sb.Append($@"
                    <div class=""agenda-folder"" id=""folder-participating"">
                        <div class=""agenda-folder-header"" onclick=""toggleSessionFolder('folder-participating')"" style=""background:#f0fdf4;border-bottom-color:#bbf7d0;"">
                            <div>
                                <span class=""agenda-folder-caret"">▼</span>
                                <span style=""color:#15803d;font-weight:800;"">🟢 參與中</span>
                            </div>
                            <span id=""sidebarParticipatingCountBadge"" class=""badge"" style=""background:#dcfce7;color:#15803d;border:1px solid #86efac;"">{participatingSessions.Count} 場</span>
                        </div>
                        <div class=""agenda-folder-body"" id=""sidebarFolderBodyParticipating"">
                            <div id=""sidebarEmptyParticipatingMsg"" style=""display:{(participatingSessions.Count == 0 ? "block" : "none")};font-size:12.5px;color:#64748b;padding:8px;text-align:center;"">
                                目前尚無「參與中」議程；請個人從上方「📂 待參與」點選議程，才會進入「🟢 參與中」的獨立網頁。
                            </div>");
        foreach (DataRow sRow in participatingSessions)
        {
            renderSessionAgendaItem(sRow);
        }
        sb.Append(@"
                        </div>
                    </div>");

        // Folder 3: 已拍板定案議程 (finalized) — 預設一律收合
        string finalizedFolderClass = "collapsed";
        sb.Append($@"
                    <div class=""agenda-folder {finalizedFolderClass}"" id=""folder-finalized"">
                        <div class=""agenda-folder-header"" onclick=""toggleSessionFolder('folder-finalized')"">
                            <div>
                                <span class=""agenda-folder-caret"">▼</span>
                                <span>📁 已拍板定案議程</span>
                            </div>
                            <span class=""badge badge-finalized"">{finalizedSessions.Count} 場</span>
                        </div>
                        <div class=""agenda-folder-body"">");
        if (finalizedSessions.Count == 0)
        {
            sb.Append("<div style='font-size:12.5px;color:#64748b;padding:8px;text-align:center;'>尚無已拍板定案的議程。</div>");
        }
        else
        {
            foreach (DataRow sRow in finalizedSessions)
            {
                renderSessionAgendaItem(sRow);
            }
        }
        sb.Append(@"
                        </div>
                    </div>");

        if (isAdmin)
        {
            sb.Append(@"
                    <div style='border-top:1px solid #e2e8f0;padding-top:12px;margin-top:6px;display:flex;flex-direction:column;gap:6px;'>
                        <button type='button' class='btn btn-primary' style='width:100%;background:#1d4ed8;' onclick=""openSessionSetupEditModal(0)"">
                            ⚡ 快速發起盲投議程 (彈出 Modal)
                        </button>
                        <button type='button' class='btn btn-outline' style='width:100%;font-size:12px;padding:6px 10px;' onclick=""switchTab('roundSetup')"">
                            ⚙️ 開啟「設定盲投回合」完整設定精靈
                        </button>
                        <div style='font-size:12px;color:#64748b;text-align:center;'>可設定投什麼、誰可參與、最高/最低 Story Point 與共識規則</div>
                    </div>");
        }
        sb.Append("</div></div></div>"); // end votingSidebarBody, voting-sidebar-full-content, votingSidebarCard

        // 右側：獨立網頁盲投 Session 入口大廳（分成「📂 待參與」與「🟢 參與中」，由個人從待參與點選才進入參與中的獨立網頁）
        sb.Append("<div class='card'>");
        sb.Append($@"
            <div class=""card-title"">
                <div>
                    <span style=""font-size:18px;"">🌐 多人盲投 Session 獨立網頁入口中心（分成「待參與」與「參與中」）</span>
                    <div style=""font-size:12.5px;color:#64748b;font-weight:normal;margin-top:3px;"">
                        進行中的議程依個人參與狀態分為「<strong>📂 待參與</strong>」與「<strong>🟢 參與中</strong>」｜<strong>由個人從「待參與」點選，才會進入到「參與中」的獨立專屬網頁 (<code>/voting/session?sessionPk=...</code>)</strong>
                    </div>
                </div>
                <div style=""display:flex;align-items:center;gap:8px;flex-wrap:wrap;"">
                    <button type=""button"" class=""btn btn-outline"" style=""padding:6px 12px;font-size:12.5px;"" onclick=""refreshVotingSessionsNow(this)"" title=""重新整理並檢查是否有屬於您的新投票議程"">🔄 重新整理議程清單</button>
                    {(isAdmin ? "<button type='button' class='btn btn-primary' style='padding:6px 12px;font-size:12.5px;background:#1d4ed8;' onclick=\"openSessionSetupEditModal(0)\">⚡ 快速發起盲投 (Modal)</button><button type='button' class='btn btn-outline' style='padding:6px 12px;font-size:12.5px;' onclick=\"switchTab('roundSetup')\">⚙️ 回合設定精靈</button>" : "")}
                </div>
            </div>

            <!-- 待參與 / 參與中 獨立網頁運作規範與即時統計橫幅 -->
            <div style=""background:linear-gradient(135deg,#eff6ff 0%,#f0fdf4 100%);border:1px solid #93c5fd;border-radius:10px;padding:12px 16px;margin-bottom:18px;display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:12px;"">
                <div style=""font-size:13px;color:#1e3a8a;line-height:1.65;"">
                    <div>1️⃣ <strong>個人從「📂 待參與」點選進入：</strong>所有新發起或尚未進入的盲投議程皆位於「<strong>📂 待參與</strong>」；當您個人從「待參與」點選某場議程時，系統才會在新的瀏覽器分頁開啟專屬獨立網頁，並將該議程移入您的「<strong>🟢 參與中</strong>」。</div>
                    <div>2️⃣ <strong>多開參與不同議程 ＆ 同議程防重複進入：</strong>您可從「待參與」點選多個<strong>不同</strong>議程同時以多個獨立網頁參與；已位於「<strong>🟢 參與中</strong>」的議程代表已在獨立網頁開啟中，系統自動鎖定禁止重複進入（離開或關閉獨立網頁後即自動移回「待參與」）。</div>
                </div>
                <div style=""display:flex;gap:10px;flex-wrap:wrap;"">
                    <div style=""background:#ffffff;border:1.5px solid #f59e0b;border-radius:10px;padding:8px 14px;text-align:center;min-width:135px;"">
                        <div style=""font-size:11.5px;color:#92400e;font-weight:700;"">📂 我的待參與議程</div>
                        <div id=""lobbyPendingSessionCountVal"" style=""font-size:19px;font-weight:800;color:#b45309;margin-top:2px;"">{pendingToJoinSessions.Count} 場待參與</div>
                    </div>
                    <div id=""lobbyActiveSessionCountBox"" style=""background:#ffffff;border:1.5px solid #16a34a;border-radius:10px;padding:8px 14px;text-align:center;min-width:155px;"">
                        <div style=""font-size:11.5px;color:#166534;font-weight:700;"">🟢 我的參與中獨立網頁</div>
                        <div id=""lobbyActiveSessionCountVal"" style=""font-size:19px;font-weight:800;color:#15803d;margin-top:2px;"">{participatingSessions.Count} 場參與中</div>
                    </div>
                </div>
            </div>");

        // 輔助函式：渲染右側大廳的單一議程卡片
        void renderLobbySessionCard(DataRow sRow)
        {
            int sPk = Convert.ToInt32(sRow["votingSessionPk"]);
            string sStatus = sRow["sessionStatus"]?.ToString() ?? "voting";
            string badgeCls = sStatus == "voting" ? "badge-voting" : "badge-revealed";
            int curRound = sRow["currentRoundNumber"] != DBNull.Value ? Convert.ToInt32(sRow["currentRoundNumber"]) : 1;
            decimal minPt = sRow.Table.Columns.Contains("minStoryPoint") && sRow["minStoryPoint"] != DBNull.Value ? Convert.ToDecimal(sRow["minStoryPoint"]) : 0m;
            decimal maxPt = sRow.Table.Columns.Contains("maxStoryPoint") && sRow["maxStoryPoint"] != DBNull.Value ? Convert.ToDecimal(sRow["maxStoryPoint"]) : 21m;
            int partCnt = sRow.Table.Columns.Contains("participantCount") && sRow["participantCount"] != DBNull.Value ? Convert.ToInt32(sRow["participantCount"]) : 0;
            int voteCnt = sRow.Table.Columns.Contains("currentRoundVoteCount") && sRow["currentRoundVoteCount"] != DBNull.Value ? Convert.ToInt32(sRow["currentRoundVoteCount"]) : 0;
            string consensusText = sRow.Table.Columns.Contains("consensusRuleText") ? (sRow["consensusRuleText"]?.ToString() ?? "對齊最接近費氏牌面") : "對齊最接近費氏牌面";
            bool isOpenNow = myLockedSessionSet.Contains(sPk);
            string jsCardTitle = escapeJsString($"{sRow["sessionCode"]} - {sRow["sessionTitle"]}");

            sb.Append($@"
                <div id=""lobbySessionCard_{sPk}"" data-session-pk=""{sPk}"" data-session-status=""{h(sStatus)}"" style=""border:{(isOpenNow ? "2px solid #16a34a" : "1.5px solid #cbd5e1")};border-radius:10px;padding:14px;background:{(isOpenNow ? "#f0fdf4" : "#f8fafc")};display:flex;flex-direction:column;justify-content:space-between;gap:10px;"">
                    <div>
                        <div style=""display:flex;justify-content:space-between;align-items:center;gap:6px;flex-wrap:wrap;margin-bottom:6px;"">
                            <a href=""/voting/session?sessionPk={sPk}"" target=""_blank"" rel=""noopener"" data-session-title-link=""{sPk}"" onclick=""return openIndependentVotingSession(event, {sPk}, '{jsCardTitle}')"" style=""text-decoration:none;font-size:13.5px;font-weight:800;color:#1e3a8a;"">{h(sRow["sessionCode"])}</a>
                            <div style=""display:flex;gap:4px;align-items:center;flex-wrap:wrap;"">
                                <span data-session-open-badge=""{sPk}"" class=""badge"" style=""display:inline-block;{(isOpenNow ? "background:#dcfce7;color:#15803d;border:1px solid #86efac;" : "background:#eff6ff;color:#1d4ed8;border:1px solid #93c5fd;")}"">{(isOpenNow ? "🟢 參與中" : "⏳ 待參與")}</span>
                                <span class=""badge {badgeCls}"">第 {curRound} 回合・{h(sRow["sessionStatusText"])}</span>
                            </div>
                        </div>
                        <a href=""/voting/session?sessionPk={sPk}"" target=""_blank"" rel=""noopener"" data-session-title-link=""{sPk}"" onclick=""return openIndependentVotingSession(event, {sPk}, '{jsCardTitle}')"" style=""text-decoration:none;color:inherit;display:block;"">
                            <div style=""font-size:15px;font-weight:800;color:#0f172a;margin-bottom:4px;"">{h(sRow["sessionTitle"])}</div>
                        </a>
                        <div style=""font-size:12.5px;color:#334155;margin-bottom:4px;"">🎯 <strong>估算標的：</strong>{h(sRow["targetDisplayName"])} ({h(sRow["targetKindText"])})</div>
                        <div style=""font-size:12px;color:#64748b;"">
                            📏 點數範圍：{minPt:0.##}~{maxPt:0.##} pt ｜ 🧮 共識：{h(consensusText)}<br/>
                            👥 本回合已出牌：<strong>{voteCnt} / {partCnt} 人</strong> ｜ 主持：{h(sRow["hostEmployeeName"])}
                        </div>
                    </div>
                    <div style=""display:flex;gap:8px;flex-wrap:wrap;padding-top:8px;border-top:1px dashed #cbd5e1;"">
                        <a href=""/voting/session?sessionPk={sPk}"" target=""_blank"" rel=""noopener"" data-session-enter-btn=""{sPk}"" data-session-title=""{h(sRow["sessionCode"] + " - " + sRow["sessionTitle"])}"" onclick=""return openIndependentVotingSession(event, {sPk}, '{jsCardTitle}')"" class=""btn {(isOpenNow ? "btn-outline" : "btn-primary")}"" style=""flex:1;padding:8px 12px;font-size:13px;{(isOpenNow ? "background:#dcfce7;color:#15803d;border-color:#86efac;" : "")}"">
                            {(isOpenNow ? "🟢 參與中 (獨立網頁進行中・禁重複進入)" : "🚀 點選進入「參與中」獨立網頁")}
                        </a>
                        <button type=""button"" data-session-leave-btn=""{sPk}"" onclick=""leaveParticipatingSessionFromLobby(event, {sPk})"" class=""btn btn-outline"" style=""display:{(isOpenNow ? "inline-block" : "none")};padding:8px 10px;font-size:12px;color:#dc2626;border-color:#fca5a5;background:#fff;"" title=""結束本場議程的獨立網頁參與狀態並移回「待參與」"">🚪 結束參與</button>
                        {(isAdmin ? $"<button type='button' class='btn btn-outline' style='padding:8px 10px;font-size:12px;color:#1d4ed8;border-color:#93c5fd;background:#eff6ff;' onclick=\"triggerRevoteFromTarget('session', 0, {sPk}, '{jsCardTitle}', false, 'voting')\" title='針對此議程啟動重新投票'>🔄 重新投票</button>" : "")}
                        {(isAdmin ? $"<button type='button' class='btn btn-outline' style='padding:8px 10px;font-size:12px;' onclick='openRoundSetupForSession({sPk})'>⚙️ 設定</button>" : "")}
                    </div>
                </div>");
        }

        // 區塊 1：📂 待參與議程（個人從此處點選，才會進入「參與中」的獨立網頁）
        sb.Append($@"
            <div style=""border:1.5px solid #bfdbfe;border-radius:12px;padding:16px;background:#ffffff;margin-bottom:20px;"">
                <div style=""font-weight:800;font-size:15.5px;color:#1e3a8a;margin-bottom:12px;display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:8px;border-bottom:1px solid #e2e8f0;padding-bottom:8px;"">
                    <span>📂 1. 待參與議程（請由您個人從下方點選議程，才會進入「參與中」的獨立網頁）</span>
                    <span id=""lobbyPendingBadgeCount"" class=""badge badge-voting"">{pendingToJoinSessions.Count} 場待參與</span>
                </div>
                <div id=""lobbyEmptyPendingMsg"" style=""display:{(pendingToJoinSessions.Count == 0 ? "block" : "none")};padding:22px 16px;text-align:center;color:#475569;background:#f8fafc;border:1px dashed #cbd5e1;border-radius:10px;"">
                    <div style=""font-size:14.5px;font-weight:700;color:#0f172a;margin-bottom:4px;"">{(ongoingSessions.Count == 0 ? "🃏 目前尚無進行中的盲投議程" : "✅ 您目前已無「待參與」的盲投議程")}</div>
                    <div style=""font-size:12.5px;color:#64748b;"">{(ongoingSessions.Count == 0 ? "當管理者發起包含您的新盲投議程時，將自動出現在「📂 待參與」清單中供您點選進入。" : "您可參與的進行中議程皆已由您點選並移入下方「🟢 參與中」獨立網頁。")}</div>
                </div>
                <div id=""lobbyGridPending"" style=""display:grid;grid-template-columns:repeat(auto-fill,minmax(340px,1fr));gap:14px;"">");
        foreach (DataRow sRow in pendingToJoinSessions)
        {
            renderLobbySessionCard(sRow);
        }
        sb.Append(@"
                </div>
            </div>");

        // 區塊 2：🟢 參與中議程（個人已從「待參與」點選、目前正在獨立網頁進行中的 Session）
        sb.Append($@"
            <div style=""border:2px solid #86efac;border-radius:12px;padding:16px;background:#f0fdf4;margin-bottom:16px;"">
                <div style=""font-weight:800;font-size:15.5px;color:#15803d;margin-bottom:12px;display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:8px;border-bottom:1px solid #bbf7d0;padding-bottom:8px;"">
                    <span>🟢 2. 參與中議程（您個人已從「待參與」點選並開啟獨立網頁進行中的 Session — 同一場禁止重複進入）</span>
                    <span id=""lobbyParticipatingBadgeCount"" class=""badge"" style=""background:#dcfce7;color:#15803d;border:1px solid #86efac;"">{participatingSessions.Count} 場參與中</span>
                </div>
                <div id=""lobbyEmptyParticipatingMsg"" style=""display:{(participatingSessions.Count == 0 ? "block" : "none")};padding:22px 16px;text-align:center;color:#166534;background:rgba(255,255,255,0.75);border:1px dashed #86efac;border-radius:10px;"">
                    <div style=""font-size:14.5px;font-weight:700;color:#15803d;margin-bottom:4px;"">💡 您目前尚無「參與中」的獨立網頁議程</div>
                    <div style=""font-size:12.5px;color:#166534;"">請從上方或左側「<strong>📂 待參與</strong>」點選任一議程，才會開啟專屬獨立網頁並進入「<strong>🟢 參與中</strong>」！</div>
                </div>
                <div id=""lobbyGridParticipating"" style=""display:grid;grid-template-columns:repeat(auto-fill,minmax(340px,1fr));gap:14px;"">");
        foreach (DataRow sRow in participatingSessions)
        {
            renderLobbySessionCard(sRow);
        }
        sb.Append(@"
                </div>
            </div>");

        sb.Append("</div>"); // end right column
        sb.Append("</div></div>"); // end Tab 1

        // =====================================================================
        // TAB 1-2 (roundSetup): 設定盲投回合 (多人盲投開始前設定：投什麼、誰可以參與、最高最低 Story Point 與建議回合設定)
        // =====================================================================
        sb.Append($"<div id='pane-roundSetup' class='tab-pane {(currentTab == "roundSetup" ? "active" : "")}'>");
        sb.Append("<div class='card'>");
        sb.Append(@"
            <div class='card-title'>
                <div>
                    <span style='font-size:18px;'>⚙️ 多人盲投回合開始前設定 (Pre-Round Configuration Wizard)</span>
                    <div style='font-size:12.5px;color:#64748b;font-weight:normal;margin-top:3px;'>
                        設定本回合「投什麼」、「誰可以參與」、「最低與最高 Story Point 範圍」，以及敏捷估算共識規則、自動翻牌與計時選項
                    </div>
                </div>
                <button type='button' class='btn btn-outline' onclick=""switchTab('voting')"">🔙 返回多人即時盲投大廳</button>
            </div>");

        if (isAdmin)
        {
            sb.Append(@"
            <!-- 敏捷估算快速範本列 -->
            <div class='preset-bar'>
                <span style='font-weight:700;font-size:13.5px;color:#1e3a8a;'>⚡ 一鍵套用推薦回合設定範本：</span>
                <button type='button' class='btn btn-outline' style='padding:6px 12px;font-size:12.5px;' onclick=""applyRoundPreset('standard')"">
                    📐 標準 Scrum 範本 (0.5~21 pt ｜ 費氏牌面對齊 ｜ 分歧門檻 5 pt ｜ 180 秒)
                </button>
                <button type='button' class='btn btn-outline' style='padding:6px 12px;font-size:12.5px;' onclick=""applyRoundPreset('blitz')"">
                    🚀 快速閃電估算 (1~13 pt ｜ 全員出牌自動翻牌 ｜ 中位數 ｜ 60 秒)
                </button>
                <button type='button' class='btn btn-outline' style='padding:6px 12px;font-size:12.5px;' onclick=""applyRoundPreset('strict')"">
                    🏛️ 嚴謹架構評審 (1~40 pt ｜ 去頭去尾平均 ｜ 門檻 3 pt ｜ 極端值必填理由 ｜ 300 秒)
                </button>
            </div>

            <form method='post' action='/voting/create' id='formRoundSetup'>
                <!-- 模式切換：發起新盲投議程 vs 修改既有進行中議程設定 -->
                <div class='setup-section' style='background:#eff6ff;border-color:#bfdbfe;'>
                    <div class='setup-section-title'>
                        <span>🔄 設定對象模式（可發起全新盲投回合，或載入既有議程調整回合規則與參與名單）</span>
                    </div>
                    <div class='form-row' style='margin-bottom:0;'>
                        <div>
                            <label data-tip='選擇設定作業模式：可建立並啟動全新的多人盲投議程，或載入既有進行中議程調整回合規則與參與名單' title='選擇設定作業模式：可建立並啟動全新的多人盲投議程，或載入既有進行中議程調整回合規則與參與名單'>設定作業模式 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select name='votingSessionPk' id='setupSessionSelector' onchange='onSetupSessionSelectChange()'>
                                <option value=''>➕ 建立並啟動全新的多人盲投議程</option>");
            foreach (DataRow sRow in ongoingSessions)
            {
                int sPk = Convert.ToInt32(sRow["votingSessionPk"]);
                sb.Append($"<option value='{sPk}'>✏️ 修改進行中議程：{h(sRow["sessionCode"])} - {h(sRow["sessionTitle"])}</option>");
            }
            foreach (DataRow sRow in finalizedSessions)
            {
                int sPk = Convert.ToInt32(sRow["votingSessionPk"]);
                sb.Append($"<option value='{sPk}'>🔄 重新投票已定案議程：{h(sRow["sessionCode"])} - {h(sRow["sessionTitle"])}</option>");
            }
            sb.Append(@"
                            </select>
                        </div>
                        <div style='display:flex;align-items:end;'>
                            <label style='display:flex;align-items:center;gap:8px;cursor:pointer;padding:8px 12px;background:#fff;border:1px solid #cbd5e1;border-radius:7px;width:100%;margin:0;' data-tip='若為修改既有或已定案議程，勾選此項將同時清空出牌紀錄並重啟盲投回合，讓全體參與者重新投票' title='若為修改既有或已定案議程，勾選此項將同時清空出牌紀錄並重啟盲投回合，讓全體參與者重新投票'>
                                <input type='checkbox' name='resetCurrentRoundVotes' id='setupResetVotes' value='true' />
                                <span>🔄 重新投票：修改既有／已定案議程時，一併重啟盲投重新投票 <span class='sp-tip-icon'>ⓘ</span></span>
                            </label>
                        </div>
                    </div>
                </div>

                <!-- 區塊 1：投什麼 (What to vote on) -->
                <div class='setup-section'>
                    <div class='setup-section-title'>
                        <span>1️⃣ 投什麼：選擇估算標的類型、目標項目與回合驗收重點</span>
                        <span class='badge badge-user'>必填項目</span>
                    </div>
                    <div class='form-row'>
                        <div>
                            <label data-tip='選擇投票估算標的類型：投「工項的代表類型」（定案後建立工項自動帶入標準點數）或投「個別特定工項」' title='選擇投票估算標的類型：投「工項的代表類型」（定案後建立工項自動帶入標準點數）或投「個別特定工項」'>估算標的類型 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select name='targetKind' id='setupTargetKind' onchange='toggleSetupTargetKind()'>
                                <option value='workItemType'>🏷️ 投「工項的代表類型」(定案後建立工項自動帶入標準點數)</option>
                                <option value='workItem'>📋 投「個別特定工項」(針對單一特殊複雜工項估算點數)</option>
                            </select>
                        </div>
                        <div id='setupBoxType'>
                            <label data-tip='選擇本回合要進行多人盲投估算的「工項代表類型」；若尚無代表類型或欲建立新類型，可選擇「依盲投議程標題自動建立」' title='選擇本回合要進行多人盲投估算的「工項代表類型」；若尚無代表類型或欲建立新類型，可選擇「依盲投議程標題自動建立」'>目標工項代表類型 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select name='workItemTypePk' id='setupWorkItemTypePk' onchange='autoFillSetupTitle()'>");
            foreach (DataRow tRow in typeTable.Rows)
            {
                string curPt = tRow["standardStoryPoint"] != DBNull.Value ? $" (目前標準: {tRow["standardStoryPoint"]} pt)" : " (待估算)";
                sb.Append($"<option value='{tRow["workItemTypePk"]}' data-name='{h(tRow["typeName"])}'>{h(tRow["typeCode"])} - {h(tRow["typeName"])}{h(curPt)}</option>");
            }
            sb.Append(@"<option value='0' data-name=''>➕ 依下方「盲投議程標題」自動建立新工項代表類型</option>
                            </select>
                        </div>
                        <div id='setupBoxItem' style='display:none;'>
                            <label data-tip='選擇本回合要單獨進行多人盲投估算的「個別特定工項」；若尚無工項或欲建立新工項，可選擇「依盲投議程標題自動建立」' title='選擇本回合要單獨進行多人盲投估算的「個別特定工項」；若尚無工項或欲建立新工項，可選擇「依盲投議程標題自動建立」'>目標特定工項 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select name='workItemPk' id='setupWorkItemPk' onchange='autoFillSetupTitle()'>");
            foreach (DataRow wRow in itemTable.Rows)
            {
                sb.Append($"<option value='{wRow["workItemPk"]}' data-name='{h(wRow["itemTitle"])}'>{h(wRow["itemCode"])} - {h(wRow["itemTitle"])} (目前: {wRow["storyPoint"]} pt)</option>");
            }
            sb.Append(@"<option value='0' data-name=''>➕ 依下方「盲投議程標題」自動建立新實際工項</option>
                            </select>
                        </div>
                    </div>
                    <div class='form-row' style='margin-bottom:0;'>
                        <div>
                            <label data-tip='設定本次多人盲投議程的顯示標題（切換估算標的時會自動帶入建議標題）' title='設定本次多人盲投議程的顯示標題（切換估算標的時會自動帶入建議標題）'>盲投議程標題 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='text' name='sessionTitle' id='setupSessionTitle' required placeholder='例如：【代表類型估算】複合交易 API 模組標準點數訂定' />
                        </div>
                        <div>
                            <label data-tip='本回合估算範圍與驗收重點說明（DoD / 估算邊界提示），會直接顯示於所有參與者的出牌桌面供評估參考' title='本回合估算範圍與驗收重點說明（DoD / 估算邊界提示），會直接顯示於所有參與者的出牌桌面供評估參考'>估算範圍與驗收重點說明 (DoD) <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='text' name='roundGoalNote' id='setupRoundGoalNote' placeholder='例如：包含 DB SP、API 權限驗證、單元測試與例外 Log，不含舊資料移轉' />
                        </div>
                    </div>
                </div>

                <!-- 區塊 2：最高與最低的 Story Point 及撲克牌組設定 -->
                <div class='setup-section'>
                    <div class='setup-section-title'>
                        <span>2️⃣ 最高與最低的 Story Point：設定本回合可出牌點數上下限與牌組</span>
                        <span class='badge badge-voting' id='setupCardCountBadge'>即時預覽可用撲克牌面</span>
                    </div>
                    <div class='form-row'>
                        <div>
                            <label data-tip='選擇本回合使用的 Planning Poker 撲克牌組樣式（對應資料表 storyPoint.tbCardDeck）' title='選擇本回合使用的 Planning Poker 撲克牌組樣式（對應資料表 storyPoint.tbCardDeck）'>撲克牌組樣式 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select name='cardDeckPk' id='setupCardDeckPk' onchange='updateSetupCardPreview()'>");
            if (cardDeckTable.Rows.Count > 0)
            {
                foreach (DataRow cdRow in cardDeckTable.Rows)
                {
                    object deckPkVal = cdRow.Table.Columns.Contains("cardDeckPk") ? cdRow["cardDeckPk"] : cdRow["pk"];
                    string normCsv = normalizeCardDeckCsv(cdRow["cardValueCsv"]?.ToString());
                    string displayCsv = formatCardDeckCsvForOption(normCsv);
                    sb.Append($"<option value='{deckPkVal}' data-csv='{h(normCsv)}'>{h(cdRow["deckName"])} ({h(displayCsv)})</option>");
                }
            }
            else
            {
                sb.Append("<option value='1' data-csv='0,0.5,1,2,3,5,8,13,21,?,☕'>標準敏捷費氏牌組 (0, 0.5, 1, 2, 3, 5, 8, 13, 21, ?(需求不明), ☕(暫停休息))</option>");
            }
            sb.Append(@"
                            </select>
                        </div>
                        <div>
                            <label data-tip='設定本回合允許參與者投出的最低 Story Point 下限（Min Story Point），低於此點數的牌面將自動鎖定' title='設定本回合允許參與者投出的最低 Story Point 下限（Min Story Point），低於此點數的牌面將自動鎖定'>最低允許點數 (Min) <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='number' step='0.5' min='0' max='100' name='minStoryPoint' id='setupMinStoryPoint' value='0.5' oninput='updateSetupCardPreview()' required />
                        </div>
                        <div>
                            <label data-tip='設定本回合允許參與者投出的最高 Story Point 上限（Max Story Point），高於此點數的牌面將自動鎖定' title='設定本回合允許參與者投出的最高 Story Point 上限（Max Story Point），高於此點數的牌面將自動鎖定'>最高允許點數 (Max) <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='number' step='0.5' min='0.5' max='200' name='maxStoryPoint' id='setupMaxStoryPoint' value='21' oninput='updateSetupCardPreview()' required />
                        </div>
                    </div>
                    <div class='form-row' style='margin-bottom:8px;'>
                        <label style='display:flex;align-items:center;gap:8px;cursor:pointer;padding:8px 12px;background:#fff;border:1px solid #cbd5e1;border-radius:7px;margin:0;' data-tip='開放參與者於需求不明確或需要補充討論時投出「?」疑問牌' title='開放參與者於需求不明確或需要補充討論時投出「?」疑問牌'>
                            <input type='checkbox' name='allowQuestionCard' id='setupAllowQuestionCard' value='true' checked onchange='updateSetupCardPreview()' />
                            <span>允許投出「<strong>?</strong> (需求不明需補充討論)」疑問牌</span>
                        </label>
                        <label style='display:flex;align-items:center;gap:8px;cursor:pointer;padding:8px 12px;background:#fff;border:1px solid #cbd5e1;border-radius:7px;margin:0;' data-tip='開放參與者於會議疲勞或需中場休息時投出「☕」休息牌' title='開放參與者於會議疲勞或需中場休息時投出「☕」休息牌'>
                            <input type='checkbox' name='allowCoffeeCard' id='setupAllowCoffeeCard' value='true' checked onchange='updateSetupCardPreview()' />
                            <span>允許投出「<strong>☕</strong> (暫停休息)」休息牌</span>
                        </label>
                    </div>
                    <div style='background:#fff;border:1px dashed #93c5fd;border-radius:8px;padding:10px 12px;'>
                        <div style='display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:6px;margin-bottom:8px;'>
                            <div style='font-size:12px;font-weight:700;color:#1e3a8a;'>🃏 本回合實際開放給參與者點選的撲克牌面即時預覽（牌面同時顯示符號/點數與代表意義）：</div>
                            <div style='font-size:11.5px;color:#475569;'>💡 特殊牌說明：<strong>? = 需求不明需討論</strong> ｜ <strong>☕ = 暫停休息</strong></div>
                        </div>
                        <div id='setupCardPreviewContainer' style='display:flex;flex-wrap:wrap;gap:8px;'></div>
                    </div>
                </div>

                <!-- 區塊 3：誰可以參與 (Who can participate) -->
                <div class='setup-section'>
                    <div class='setup-section-title'>
                        <span>3️⃣ 誰可以參與：設定參與權限模式與勾選受邀參與投票員工名單</span>
                        <span id='setupSelectedParticipantCount' class='badge badge-finalized'>已勾選參與者</span>
                    </div>
                    <div class='form-row' style='align-items:end;'>
                        <div>
                            <label data-tip='設定參與權限控管模式：僅限下方勾選的受邀參與者可出牌（嚴格名單控管），或開放全體啟用中員工皆可出牌' title='設定參與權限控管模式：僅限下方勾選的受邀參與者可出牌（嚴格名單控管），或開放全體啟用中員工皆可出牌'>參與權限控管模式 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select name='restrictToParticipant' id='setupRestrictToParticipant'>
                                <option value='true'>🔒 僅限下方勾選的受邀參與者可以出牌投票 (嚴格名單控管)</option>
                                <option value='false'>🌐 開放全體啟用中員工皆可出牌 (下方勾選名單作為預設桌面名單)</option>
                            </select>
                        </div>
                        <div>
                            <label data-tip='選擇部門後，可一鍵將該部門全體啟用中成員加入下方受邀參與者勾選名單' title='選擇部門後，可一鍵將該部門全體啟用中成員加入下方受邀參與者勾選名單'>依部門快速批次勾選 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select id='setupQuickDeptSelect' onchange='selectParticipantsByDept(this.value)'>
                                <option value=''>-- 選擇部門一鍵加入勾選 --</option>");
            foreach (DataRow dRow in deptTable.Rows)
            {
                sb.Append($"<option value='{h(dRow["departmentName"])}'>🏢 加入【{h(dRow["departmentName"])}】全體成員</option>");
            }
            sb.Append(@"
                            </select>
                        </div>
                        <div style='display:flex;gap:6px;flex-wrap:wrap;'>
                            <button type='button' class='btn btn-outline' style='padding:7px 10px;font-size:12px;' onclick='toggleAllParticipants(true)'>✅ 全選所有啟用員工</button>
                            <button type='button' class='btn btn-outline' style='padding:7px 10px;font-size:12px;' onclick='toggleAllParticipants(false)'>⬜ 清空全部勾選</button>
                            <button type='button' class='btn btn-outline' style='padding:7px 10px;font-size:12px;' onclick='selectAssignedWorkItemEmployees()'>🎯 僅勾選該工項已指派員工</button>
                        </div>
                    </div>
                    <div class='participant-grid' id='setupParticipantGrid'>");
            foreach (DataRow eRow in empTable.Rows)
            {
                bool empActive = Convert.ToBoolean(eRow["isActive"]);
                if (!empActive) continue;
                int ePk = Convert.ToInt32(eRow["employeePk"]);
                string rTag = eRow["roleName"].ToString() == "admin" ? "👑 管理者" : "🙋 參與者";
                sb.Append($@"
                        <label class='participant-chip' data-dept='{h(eRow["departmentName"])}' data-emp-pk='{ePk}'>
                            <input type='checkbox' name='participantPks' class='setup-participant-cb' value='{ePk}' checked onchange='updateSetupParticipantCount()' />
                            <div>
                                <div style='font-weight:700;font-size:13px;color:#0f172a;'>{h(eRow["employeeName"])} <span style='font-size:11px;color:#64748b;'>({h(eRow["employeeCode"])})</span></div>
                                <div style='font-size:11.5px;color:#475569;'>{h(eRow["departmentName"])} ｜ {h(eRow["jobTitle"])}</div>
                                <div style='font-size:11px;color:#2563eb;margin-top:2px;'>{rTag} ｜ 目前負載 {eRow["totalAllocatedStoryPoint"]} pt</div>
                            </div>
                        </label>");
            }
            sb.Append(@"
                    </div>
                </div>

                <!-- 區塊 4：建議的敏捷盲投回合進階設定選項 (Recommended Round Settings) -->
                <div class='setup-section'>
                    <div class='setup-section-title'>
                        <span>4️⃣ 建議的敏捷盲投回合相關設定選項（共識收斂算式、分歧警示門檻、自動翻牌、限時與理由規範）</span>
                        <span class='badge badge-admin'>敏捷教練推薦設定</span>
                    </div>
                    <div class='form-row'>
                        <div>
                            <label data-tip='翻牌揭曉後，系統自動根據全體有效出牌計算「建議定案 Story Point」的演算法規則（Consensus Rule）' title='翻牌揭曉後，系統自動根據全體有效出牌計算「建議定案 Story Point」的演算法規則（Consensus Rule）'>🧮 1. 共識點數計算規則 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select name='consensusRule' id='setupConsensusRule'>
                                <option value='nearestFibonacci' selected>🔢 對齊最接近的費氏牌面值 (Recommended 標準敏捷作法)</option>
                                <option value='average'>📊 算術平均數 (四捨五入至 0.5 pt)</option>
                                <option value='median'>⚖️ 中位數 (避免極端高低分拉偏)</option>
                                <option value='mode'>🏆 眾數 (最多人投出的點數，同票取較高值)</option>
                                <option value='trimmedMean'>✂️ 去頭去尾平均數 (4人以上扣除最高與最低分後平均)</option>
                                <option value='maxConservative'>🛡️ 保守取較高值 (風險導向，取最高有效點數)</option>
                            </select>
                        </div>
                        <div>
                            <label data-tip='設定最高分減去最低分的最大允許落差（Max Allowed Spread）；翻牌後若 (最高分 - 最低分) 超過此門檻，系統自動標示紅字建議重投' title='設定最高分減去最低分的最大允許落差（Max Allowed Spread）；翻牌後若 (最高分 - 最低分) 超過此門檻，系統自動標示紅字建議重投'>⚠️ 2. 共識分歧警示門檻 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='number' step='0.5' min='0' max='100' name='maxAllowedSpread' id='setupMaxAllowedSpread' value='5' required />
                        </div>
                        <div>
                            <label data-tip='設定單一回合的思考與出牌倒數計時限制（Timebox Countdown），於盲投桌面顯示即時倒數計時器' title='設定單一回合的思考與出牌倒數計時限制（Timebox Countdown），於盲投桌面顯示即時倒數計時器'>⏱️ 3. 單回合出牌限時 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select name='roundTimeLimitSecond' id='setupRoundTimeLimitSecond'>
                                <option value='60'>⏱️ 60 秒 (1 分鐘快速直覺估算)</option>
                                <option value='120'>⏱️ 120 秒 (2 分鐘標準估算)</option>
                                <option value='180' selected>⏱️ 180 秒 (3 分鐘含需求提問，推薦)</option>
                                <option value='300'>⏱️ 300 秒 (5 分鐘複雜架構評估)</option>
                                <option value='0'>♾️ 不限時間 (0 秒)</option>
                            </select>
                        </div>
                    </div>
                    <div class='form-row' style='margin-bottom:0;align-items:end;'>
                        <div>
                            <label data-tip='設定參與者出牌時填寫估算理由的規範（選填、投出極端高低點數或疑問牌必填、或全體參與者必填）' title='設定參與者出牌時填寫估算理由的規範（選填、投出極端高低點數或疑問牌必填、或全體參與者必填）'>💬 4. 估算理由填寫規範 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select name='voteCommentPolicy' id='setupVoteCommentPolicy'>
                                <option value='optional' selected>📝 選填 (鼓勵填寫估算假設與理由)</option>
                                <option value='requiredExtreme'>🔍 投出最低/最高極端點數或「?」疑問牌時必填理由</option>
                                <option value='requiredAll'>✍️ 全體參與者出牌時皆必須填寫估算理由</option>
                            </select>
                        </div>
                        <div style='display:flex;align-items:end;'>
                            <label style='display:flex;align-items:center;gap:8px;cursor:pointer;padding:9px 12px;background:#fff;border:1px solid #cbd5e1;border-radius:7px;width:100%;margin:0;' data-tip='勾選後，當所有受邀參與者皆已完成出牌時，系統將自動翻牌切換為揭曉狀態並計算統計指標' title='勾選後，當所有受邀參與者皆已完成出牌時，系統將自動翻牌切換為揭曉狀態並計算統計指標'>
                                <input type='checkbox' name='autoRevealWhenAllVoted' id='setupAutoRevealWhenAllVoted' value='true' checked />
                                <span>⚡ <strong>5. 全員出牌後自動翻牌揭曉</strong> <span class='sp-tip-icon'>ⓘ</span></span>
                            </label>
                        </div>
                        <div style='display:flex;align-items:end;'>
                            <button type='submit' class='btn btn-primary' style='width:100%;padding:10px;font-size:14px;' id='setupSubmitBtn'>
                                🚀 儲存設定並啟動盲投議程
                            </button>
                        </div>
                    </div>
                </div>
            </form>");
        }
        else
        {
            sb.Append(@"
            <div class='alert' style='background:#eff6ff;border:1px solid #bfdbfe;color:#1e3a8a;'>
                ℹ️ <strong>回合規則檢視模式：</strong>發起新盲投回合與修改回合規則由「👑 管理者 (Admin)」操作；您可於下方檢視目前已建立議程的回合設定（投什麼、誰可以參與、最高最低 Story Point 與共識規則）。
            </div>");
        }

        // 下方列出各議程目前已生效的回合設定總覽表（支援表格篩選、滑鼠右鍵選擇紀錄與彈出 Modal 單筆編輯，無須下拉選單或捲動網頁）
        sb.Append(@"
            <div style='margin-top:18px;border-top:1px solid #e2e8f0;padding-top:14px;'>
                <div style='display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:8px;margin-bottom:10px;'>
                    <div style='font-weight:700;font-size:15px;color:#0f172a;'>📋 各投票議程的回合設定總覽（投什麼 ／ 誰可參與 ／ 點數上下限 ／ 共識與計時規則）</div>
                    <span class='badge badge-revealed'>🖱️ 直接於任一筆紀錄按「滑鼠右鍵」即可選擇該筆紀錄並開啟操作選單（彈出 Modal 單筆編輯設定、進入盲投網頁等）</span>
                </div>
                <table id='roundSetupSessionTable'>
                    <thead>
                        <tr>
                            <th>議程代碼與標題</th>
                            <th>狀態</th>
                            <th>投什麼 (估算標的)</th>
                            <th>點數範圍 (Min~Max)</th>
                            <th>誰可以參與</th>
                            <th>共識計算規則與分歧門檻</th>
                            <th>自動翻牌 / 計時 / 理由規範</th>
                        </tr>
                    </thead>
                    <tbody>");
        foreach (DataRow sRow in sessionTable.Rows)
        {
            int sPk = Convert.ToInt32(sRow["votingSessionPk"]);
            string sCodeStr = sRow["sessionCode"]?.ToString() ?? "";
            string sTitleStr = sRow["sessionTitle"]?.ToString() ?? "";
            string sStatus = sRow["sessionStatus"].ToString() ?? "";
            string badgeCls = sStatus == "voting" ? "badge-voting" : (sStatus == "revealed" ? "badge-revealed" : "badge-finalized");
            decimal minPt = sRow.Table.Columns.Contains("minStoryPoint") && sRow["minStoryPoint"] != DBNull.Value ? Convert.ToDecimal(sRow["minStoryPoint"]) : 0m;
            decimal maxPt = sRow.Table.Columns.Contains("maxStoryPoint") && sRow["maxStoryPoint"] != DBNull.Value ? Convert.ToDecimal(sRow["maxStoryPoint"]) : 21m;
            decimal maxSpread = sRow.Table.Columns.Contains("maxAllowedSpread") && sRow["maxAllowedSpread"] != DBNull.Value ? Convert.ToDecimal(sRow["maxAllowedSpread"]) : 5m;
            string cRuleText = sRow.Table.Columns.Contains("consensusRuleText") ? (sRow["consensusRuleText"]?.ToString() ?? "") : "";
            bool autoRev = sRow.Table.Columns.Contains("autoRevealWhenAllVoted") && sRow["autoRevealWhenAllVoted"] != DBNull.Value && Convert.ToBoolean(sRow["autoRevealWhenAllVoted"]);
            int tSec = sRow.Table.Columns.Contains("roundTimeLimitSecond") && sRow["roundTimeLimitSecond"] != DBNull.Value ? Convert.ToInt32(sRow["roundTimeLimitSecond"]) : 180;
            string cPolText = sRow.Table.Columns.Contains("voteCommentPolicyText") ? (sRow["voteCommentPolicyText"]?.ToString() ?? "") : "";
            bool restPart = sRow.Table.Columns.Contains("restrictToParticipant") && sRow["restrictToParticipant"] != DBNull.Value && Convert.ToBoolean(sRow["restrictToParticipant"]);
            int pCnt = sRow.Table.Columns.Contains("participantCount") && sRow["participantCount"] != DBNull.Value ? Convert.ToInt32(sRow["participantCount"]) : 0;
            string rowDecisionNote = sRow.Table.Columns.Contains("finalDecisionNote") && sRow["finalDecisionNote"] != DBNull.Value
                ? (sRow["finalDecisionNote"]?.ToString() ?? "").Trim()
                : "";

            sb.Append($@"
                        <tr class='sp-context-row' data-row-kind='session' data-row-title='{h(sCodeStr)} - {h(sTitleStr)}' data-session-pk='{sPk}' data-session-code='{h(sCodeStr)}' data-session-title='{h(sTitleStr)}' data-session-status='{h(sStatus)}' title='💡 按滑鼠右鍵選擇此筆議程紀錄並開啟操作選單（彈出 Modal 單筆編輯、進入獨立盲投網頁）'>
                            <td data-sort-value='{h(sCodeStr)}'>
                                <div><strong>{h(sCodeStr)}</strong></div>
                                <span style='font-size:12.5px;color:#334155;'>{h(sTitleStr)}</span>
                                {(!string.IsNullOrWhiteSpace(rowDecisionNote) ? $"<br/><span style='font-size:12px;color:#15803d;font-weight:700;'>🏆 定案決議說明：{h(rowDecisionNote)}</span>" : "")}
                            </td>
                            <td>
                                <span class='badge {badgeCls}'>{h(sRow["sessionStatusText"])}</span>
                                {(sRow["finalStoryPoint"] != DBNull.Value ? $"<div style='font-size:12px;color:#15803d;font-weight:700;margin-top:4px;'>定案：{sRow["finalStoryPoint"]} pt</div>" : "")}
                            </td>
                            <td>
                                <span style='font-size:12px;color:#2563eb;font-weight:700;'>{h(sRow["targetKindText"])}</span><br/>
                                <strong>{h(sRow["targetDisplayName"])}</strong>
                            </td>
                            <td><strong>{minPt:0.##} ~ {maxPt:0.##} pt</strong></td>
                            <td>{(restPart ? $"🔒 指定名單 ({pCnt} 人)" : $"🌐 全體員工 ({pCnt} 人)")}</td>
                            <td>{h(cRuleText)}<br/><span style='font-size:11.5px;color:#64748b;'>分歧警示門檻：{maxSpread:0.##} pt</span></td>
                            <td>
                                自動翻牌：{(autoRev ? "✅ 是" : "手動")} ｜ 計時：{(tSec > 0 ? $"{tSec}秒" : "不限")}<br/>
                                <span style='font-size:11.5px;color:#64748b;'>出牌理由：{h(cPolText)}</span>
                            </td>
                        </tr>");
        }
        sb.Append("</tbody></table></div>");
        sb.Append("</div></div>"); // end Tab roundSetup

        // =====================================================================
        // TAB 2: 事後查詢訂出 Story Point 的完整過程
        // =====================================================================
        sb.Append($"<div id='pane-history' class='tab-pane {(currentTab == "history" ? "active" : "")}'>");
        sb.Append($@"
        <div class='card'>
            <div class='card-title'>
                <span>📜 {(isAdmin ? "事後查詢訂出 Story Point 的完整過程（各議程、各回合統計、具名出牌明細與討論紀錄）" : "查詢我的投票紀錄與工項 Story Point 訂定歷程（僅顯示您本人的出牌與工項，不顯示其他人）")}</span>
            </div>");

        foreach (DataRow sRow in sessionTable.Rows)
        {
            int sPk = Convert.ToInt32(sRow["votingSessionPk"]);
            string sStatus = sRow["sessionStatus"].ToString() ?? "";
            string badgeCls = sStatus == "voting" ? "badge-voting" : (sStatus == "revealed" ? "badge-revealed" : "badge-finalized");
            string finalPt = sRow["finalStoryPoint"] != DBNull.Value ? $"{sRow["finalStoryPoint"]} pt" : "尚未定案";
            string jsHistTitle = escapeJsString($"{sRow["sessionCode"]} - {sRow["sessionTitle"]}");

            sb.Append($@"
            <div style=""border:1px solid #cbd5e1;border-radius:10px;padding:16px;margin-bottom:18px;background:#f8fafc;"">
                <div style=""display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:8px;margin-bottom:8px;"">
                    <div>
                        <span class=""badge {badgeCls}"">{h(sRow["sessionStatusText"])}</span>
                        <strong style=""font-size:16px;margin-left:6px;"">{h(sRow["sessionCode"])} - {h(sRow["sessionTitle"])}</strong>
                    </div>
                    <div style=""font-size:14px;display:flex;align-items:center;gap:10px;flex-wrap:wrap;"">
                        <span>最終核定 Story Point：<strong style=""color:#16a34a;font-size:18px;"">{finalPt}</strong> ｜ 總回合數：<strong>{sRow["totalRoundCount"]} 回合</strong> ｜ 主持管理者：{h(sRow["hostEmployeeName"])}</span>
                        {(isAdmin ? $"<button type='button' class='btn btn-outline' style='padding:4px 10px;font-size:12px;color:#1d4ed8;border-color:#93c5fd;background:#eff6ff;' onclick=\"triggerRevoteFromTarget('session', 0, {sPk}, '{jsHistTitle}', false, 'voting')\">🔄 重新投票</button>" : "")}
                    </div>
                </div>
                <div style=""font-size:13px;color:#334155;margin-bottom:10px;"">
                    <strong>估算標的：</strong>{h(sRow["targetDisplayName"])}
                    {(sRow["finalDecisionNote"] != DBNull.Value && !string.IsNullOrWhiteSpace(sRow["finalDecisionNote"]?.ToString())
                        ? $@" ｜ 🏆 <strong style=""color:#15803d;"">定案決議說明：{h(sRow["finalDecisionNote"])}</strong>"
                        : (sStatus == "finalized" ? "" : " ｜ <strong>狀態：</strong>進行中"))}
                </div>");

            // 列出每一回合的討論摘要與投票紀錄
            foreach (DataRow rRow in roundTable.Rows)
            {
                if (Convert.ToInt32(rRow["votingSessionPk"]) != sPk) continue;
                int rNum = Convert.ToInt32(rRow["roundNumber"]);
                bool isRoundRevealed = rRow["averagePoint"] != DBNull.Value;
                string roundStatSummary = isRoundRevealed
                    ? $"（出牌數：{rRow["voteCount"]} 人 ｜ 平均：{rRow["averagePoint"]} pt ｜ 最低：{rRow["minPoint"]} pt ｜ 最高：{rRow["maxPoint"]} pt）"
                    : $"（出牌數：{rRow["voteCount"]} 人 ｜ 盲投進行中，待全員投票後自動翻牌揭曉）";
                sb.Append($@"
                <div style=""background:#ffffff;border:1px solid #e2e8f0;border-radius:8px;padding:12px;margin-top:10px;"">
                    <div style=""font-weight:700;color:#1e3a8a;margin-bottom:6px;"">
                        🔹 第 {rNum} 回合 {roundStatSummary}
                    </div>
                    <div style=""font-size:12.5px;color:#475569;margin-bottom:8px;"">💬 回合討論紀錄：{h(rRow["roundDiscussionNote"])}</div>
                    <table>
                        <thead><tr><th>員工編號</th><th>投票員工</th><th>所屬部門</th><th>職稱</th><th>投出撲克牌</th><th>估算理由備註</th><th>出牌時間</th></tr></thead>
                        <tbody>");
                foreach (DataRow vRow in voteRecordTable.Rows)
                {
                    if (Convert.ToInt32(vRow["votingSessionPk"]) != sPk || Convert.ToInt32(vRow["roundNumber"]) != rNum) continue;
                    sb.Append($@"<tr class='sp-context-row' data-row-kind='readonlyRecord' data-row-title='第 {rNum} 回合投票：{h(vRow["employeeCode"])} - {h(vRow["employeeName"])}' title='💡 按滑鼠右鍵選擇此筆紀錄並開啟操作選單'>
                        <td>{h(vRow["employeeCode"])}</td>
                        <td><strong>{h(vRow["employeeName"])}</strong></td>
                        <td>{h(vRow["departmentName"])}</td>
                        <td>{h(vRow["jobTitle"])}</td>
                        <td><strong style='color:#2563eb;font-size:14px;'>{h(vRow["displayCardValue"])}</strong></td>
                        <td>{h(vRow["voteComment"])}</td>
                        <td>{Convert.ToDateTime(vRow["votedAt"]):yyyy-MM-dd HH:mm:ss}</td>
                    </tr>");
                }
                sb.Append("</tbody></table></div>");
            }
            sb.Append("</div>");
        }

        // 點數異動稽核歷程表
        sb.Append($@"
        <div class='card-title' style='margin-top:24px;'>
            <span>🕒 {(isAdmin ? "Story Point 訂定與異動稽核歷程 (tbPointHistory)" : "我的工項與代表類型 Story Point 異動歷程")}</span>
            <div style='display:flex;gap:8px;flex-wrap:wrap;'>
                <a href='/export/report?type=history&format=csv' class='btn btn-outline' style='padding:5px 12px;font-size:12px;'>📥 匯出 CSV</a>
                <a href='/export/report?type=history&format=excel' class='btn btn-outline' style='padding:5px 12px;font-size:12px;'>📊 匯出 Excel (.xlsx)</a>
                <a href='/export/report?type=history&format=html' class='btn btn-primary' style='padding:5px 12px;font-size:12px;'>🌐 匯出互動 HTML (排序・篩選・群組彙總)</a>
            </div>
        </div>
        <table>
            <thead><tr><th>時間</th><th>類別</th><th>標的名稱</th><th>原點數</th><th>新 Story Point</th><th>異動來源</th><th>原因說明</th><th>操作人員</th></tr></thead>
            <tbody>");
        foreach (DataRow hRow in historyTable.Rows)
        {
            string targetName = hRow["targetKind"].ToString() == "workItemType"
                ? $"[代表類型] {hRow["typeCode"]} - {hRow["typeName"]}"
                : $"[工項] {hRow["itemCode"]} - {hRow["itemTitle"]}";
            sb.Append($@"<tr class='sp-context-row' data-row-kind='readonlyRecord' data-row-title='點數異動：{h(targetName)}' title='💡 按滑鼠右鍵選擇此筆紀錄並開啟操作選單'>
                <td>{Convert.ToDateTime(hRow["changedAt"]):yyyy-MM-dd HH:mm:ss}</td>
                <td>{h(hRow["targetKindText"])}</td>
                <td><strong>{h(targetName)}</strong></td>
                <td>{h(hRow["oldStoryPoint"] ?? "-")}</td>
                <td><strong style='color:#2563eb;'>{hRow["newStoryPoint"]} pt</strong></td>
                <td><span class='badge badge-user'>{h(hRow["changeSourceText"])}</span></td>
                <td>{h(hRow["changeReason"])}</td>
                <td>{h(hRow["changedByEmployeeName"])}</td>
            </tr>");
        }
        sb.Append("</tbody></table></div></div>"); // end Tab 2

        // =====================================================================
        // TAB 3: 工項點數查詢、工項類型增刪修、建立工項自動帶入代表類型點數、修改員工賦予的工項
        // =====================================================================
        sb.Append($"<div id='pane-workitems' class='tab-pane {(currentTab == "workitems" ? "active" : "")}'>");

        // 動態建構工項類型下拉選單選項 HTML (來自 storyPoint.tbWorkItemKind，支援增刪修)
        var kindOptionsBuilder = new StringBuilder();
        if (kindTable.Rows.Count > 0)
        {
            foreach (DataRow kRow in kindTable.Rows)
            {
                string kCode = kRow["kindCode"]?.ToString() ?? "project";
                string kName = kRow["kindName"]?.ToString() ?? kCode;
                string kIcon = kRow.Table.Columns.Contains("badgeIcon") && kRow["badgeIcon"] != DBNull.Value ? (kRow["badgeIcon"]?.ToString() ?? "📌") : "📌";
                kindOptionsBuilder.Append($"<option value='{h(kCode)}'>{h(kIcon)} {h(kName)} ({h(kCode)})</option>");
            }
        }
        else
        {
            kindOptionsBuilder.Append("<option value='project'>🏗️ 專案 (project)</option>");
            kindOptionsBuilder.Append("<option value='maintenance'>🛠️ 維運 (maintenance)</option>");
            kindOptionsBuilder.Append("<option value='consultingHour'>⏳ 顧問時數 (consultingHour)</option>");
            kindOptionsBuilder.Append("<option value='training'>🎓 教育訓練 (training)</option>");
            kindOptionsBuilder.Append("<option value='productDevelopment'>🚀 開發產品 (productDevelopment)</option>");
        }
        string dynamicKindOptionsHtml = kindOptionsBuilder.ToString();

        // 共用輔助局部函數：輸出「工項類型 (tbWorkItemKind) 增、刪、修管理」卡片與清單
        void renderWorkItemKindCrudCard(string tabSuffix)
        {
            sb.Append($@"
            <div class='card' style='border:1.5px solid #bfdbfe;background:linear-gradient(180deg,#f8fafc 0%,#ffffff 100%);margin-bottom:18px;'>
                <div class='card-title'>
                    <span>🗂️ 工項類型增、刪、修管理（預設含：專案 / 維運 / 顧問時數 / 教育訓練 / 開發產品，支援自由新增、修改與刪除工項類型）</span>
                    <button type='button' class='btn btn-outline' style='padding:4px 10px;font-size:12px;' onclick=""resetWorkItemKindForm('{tabSuffix}')"">🔄 清空切換為新增工項類型</button>
                </div>
                <form method='post' action='/workitem/kind/save' style='margin-bottom:14px;'>
                    <input type='hidden' name='workItemKindPk' id='wikPk_{tabSuffix}' value='' />
                    <input type='hidden' name='returnTab' value='{tabSuffix}' />
                    <div class='form-row' style='align-items:end;'>
                        <div>
                            <label data-tip='設定工項類型唯一識別代碼（例如：project、maintenance、consultingHour、training、productDevelopment 或自訂 supportCase）' title='設定工項類型唯一識別代碼（例如：project、maintenance、consultingHour、training、productDevelopment 或自訂 supportCase）'>類型代碼 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='text' name='kindCode' id='wikCode_{tabSuffix}' placeholder='例如：supportCase' required />
                        </div>
                        <div>
                            <label data-tip='設定工項類型中文顯示名稱（例如：專案、維運、顧問時數、教育訓練、開發產品、駐點支援）' title='設定工項類型中文顯示名稱（例如：專案、維運、顧問時數、教育訓練、開發產品、駐點支援）'>工項類型名稱 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='text' name='kindName' id='wikName_{tabSuffix}' placeholder='例如：駐點技術支援' required />
                        </div>
                        <div>
                            <label data-tip='選擇或輸入代表此工項類型的圖示符號（Emoji）' title='選擇或輸入代表此工項類型的圖示符號（Emoji）'>代表圖示 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='text' name='badgeIcon' id='wikIcon_{tabSuffix}' value='📌' required />
                        </div>
                        <div>
                            <label data-tip='選擇此工項類型在報表與工項列表中的標籤顏色樣式' title='選擇此工項類型在報表與工項列表中的標籤顏色樣式'>標籤顏色 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select name='badgeColor' id='wikColor_{tabSuffix}'>
                                <option value='blue'>🔵 藍色 (Blue - 專案)</option>
                                <option value='amber'>🟡 琥珀橘 (Amber - 維運)</option>
                                <option value='purple'>🟣 紫色 (Purple - 顧問時數)</option>
                                <option value='green'>🟢 綠色 (Green - 教育訓練)</option>
                                <option value='red'>🔴 紅色 (Red - 開發產品)</option>
                            </select>
                        </div>
                        <div>
                            <label data-tip='設定下拉選單與列表的顯示排序數字（越小越前面）' title='設定下拉選單與列表的顯示排序數字（越小越前面）'>排序 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='number' name='sortOrder' id='wikSort_{tabSuffix}' value='60' required />
                        </div>
                    </div>
                    <div class='form-row' style='align-items:end;margin-bottom:0;'>
                        <div style='grid-column: span 3;'>
                            <label data-tip='描述此工項類型的適用範圍與管理規則（所有類型工項皆有起訖時間，達迄時自動轉態扣點並寄信給部門管理層與 HR）' title='描述此工項類型的適用範圍與管理規則（所有類型工項皆有起訖時間，達迄時自動轉態扣點並寄信給部門管理層與 HR）'>工項類型說明 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='text' name='description' id='wikDesc_{tabSuffix}' placeholder='說明此工項類型的適用範圍（皆支援起訖時間設定與到期自動轉態扣點寄信）' />
                        </div>
                        <div>
                            <button type='submit' class='btn btn-primary' id='wikSubmitBtn_{tabSuffix}' style='width:100%;'>➕ 新增 / 儲存工項類型</button>
                        </div>
                    </div>
                </form>
                <table class='wik-data-table'>
                    <thead>
                        <tr>
                            <th>排序</th>
                            <th>類型代碼</th>
                            <th>工項類型名稱与標籤預覽</th>
                            <th>類型說明</th>
                            <th>套用中實際工項數</th>
                            <th>套用中代表類型數</th>
                        </tr>
                    </thead>
                    <tbody>");
            foreach (DataRow kRow in kindTable.Rows)
            {
                int kPk = Convert.ToInt32(kRow["workItemKindPk"]);
                string kCode = kRow["kindCode"]?.ToString() ?? "";
                string kName = kRow["kindName"]?.ToString() ?? "";
                string kIcon = kRow.Table.Columns.Contains("badgeIcon") && kRow["badgeIcon"] != DBNull.Value ? (kRow["badgeIcon"]?.ToString() ?? "📌") : "📌";
                string kColor = kRow.Table.Columns.Contains("badgeColor") && kRow["badgeColor"] != DBNull.Value ? (kRow["badgeColor"]?.ToString() ?? "blue") : "blue";
                int kSort = kRow.Table.Columns.Contains("sortOrder") && kRow["sortOrder"] != DBNull.Value ? Convert.ToInt32(kRow["sortOrder"]) : 100;
                string kDesc = kRow["description"]?.ToString() ?? "";
                int linkedWiCnt = kRow.Table.Columns.Contains("linkedWorkItemCount") && kRow["linkedWorkItemCount"] != DBNull.Value ? Convert.ToInt32(kRow["linkedWorkItemCount"]) : 0;
                int linkedTypeCnt = kRow.Table.Columns.Contains("linkedTypeCount") && kRow["linkedTypeCount"] != DBNull.Value ? Convert.ToInt32(kRow["linkedTypeCount"]) : 0;

                string badgeStyle = kColor switch
                {
                    "amber" => "background:#fef3c7;color:#b45309;border:1px solid #fde68a;",
                    "purple" => "background:#f3e8ff;color:#6b21a8;border:1px solid #d8b4fe;",
                    "green" => "background:#dcfce7;color:#15803d;border:1px solid #86efac;",
                    "red" => "background:#fee2e2;color:#b91c1c;border:1px solid #fca5a5;",
                    _ => "background:#dbeafe;color:#1e40af;border:1px solid #93c5fd;"
                };

                sb.Append($@"
                        <tr class='sp-context-row' data-row-kind='workitemKind' data-kind-pk='{kPk}' data-kind-code='{h(kCode)}' data-kind-name='{h(kName)}' data-kind-icon='{h(kIcon)}' data-kind-color='{h(kColor)}' data-kind-sort='{kSort}' data-kind-desc='{h(kDesc)}' data-tab-suffix='{h(tabSuffix)}' title='💡 按滑鼠右鍵選擇此筆工項類型並開啟操作選單（彈出 Modal 單筆編輯、刪除、篩選）'>
                            <td data-sort-value='{kSort}'>
                                <strong>{kSort}</strong>
                            </td>
                            <td><code>{h(kCode)}</code></td>
                            <td><span class='badge' style='{badgeStyle}'>{h(kIcon)} {h(kName)}</span></td>
                            <td style='font-size:12.5px;color:#334155;'>{h(kDesc)}</td>
                            <td><strong>{linkedWiCnt}</strong> 件工項</td>
                            <td><strong>{linkedTypeCnt}</strong> 個代表類型</td>
                        </tr>");
            }
            sb.Append(@"
                    </tbody>
                </table>
            </div>");
        }

        if (isAdmin)
        {
            string defaultStartIso = DateTime.Now.ToString("yyyy-MM-ddTHH:mm");
            string defaultEndIso = DateTime.Now.AddDays(1).ToString("yyyy-MM-ddTHH:mm");
            string nextWiCode = "WI-2026-" + (itemTable.Rows.Count + 1).ToString("D3");

            sb.Append($@"
            <div class='sp-page-action-bar'>
                <div class='sp-action-title'>
                    <span>⚡ 工項管理快捷指令列</span>
                    <span style='font-size:12px;font-weight:600;color:#93c5fd;'>（表格直接置頂呈現｜支援左鍵單擊選取列、雙擊列彈出 Modal 編輯、右鍵快捷選單）</span>
                </div>
                <div class='sp-action-group'>
                    <button type='button' class='btn btn-primary' style='padding:6px 12px;font-size:12.5px;background:#2563eb;border-color:#60a5fa;' onclick=""openWorkItemEditModal('','{nextWiCode}','','0','','todo','','project','{defaultStartIso}','{defaultEndIso}','2026-Q4 Sprint 2',false,'')"">➕ 新增工項與指派 (彈出 Modal)</button>
                    <button type='button' class='btn btn-success' style='padding:6px 12px;font-size:12.5px;' onclick=""openWorkItemAssignModal('','','','','')"">👥 指派員工與分攤配額 (Modal)</button>
                    <button type='button' class='sp-drawer-toggle-btn' onclick=""openWorkItemKindEditModal('','','','📌','blue',60,'','workitems')"">🗂️ 新增工項類型 (Modal)</button>
                    <button type='button' class='sp-drawer-toggle-btn' onclick=""toggleAdminDrawer('drawer-wi-import', this)"">📤 批次匯入工項面板 ▾</button>
                    <button type='button' class='sp-drawer-toggle-btn' onclick=""toggleAdminDrawer('drawer-wi-full', this)"">🛠️ 完整管理與類型面板 ▾</button>
                    <form method='post' action='/workitem/checkExpired' style='margin:0;display:inline;'>
                        <button type='submit' class='btn btn-warning' style='padding:6px 12px;font-size:12.5px;white-space:nowrap;' title='立即執行到期工項自動轉態、扣減 Story Point 並寄信給部門主管與 HR'>⚡ 立即檢核到期工項</button>
                    </form>
                </div>
            </div>

            <div id='drawer-wi-full' class='sp-collapsible-drawer collapsed'>
            <div class='card' style='border:2px solid #93c5fd;background:linear-gradient(135deg,#eff6ff 0%,#ffffff 100%);margin-bottom:18px;'>
                <div style='display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:12px;'>
                    <div style='flex:1;min-width:280px;'>
                        <div style='font-size:15px;font-weight:800;color:#1e3a8a;margin-bottom:4px;'>⏰ 工項類型（專案 / 維運 / 顧問時數 / 教育訓練 / 開發產品 / 自訂類型）與起訖時間到期自動轉態、扣減 Story Point 及 Email 寄信通知機制</div>
                        <div style='font-size:13px;color:#334155;line-height:1.55;'>
                            每一項工項皆支援設定類型（預設含 <strong>🏗️ 專案</strong>、<strong>🛠️ 維運</strong>、<strong>⏳ 顧問時數</strong>、<strong>🎓 教育訓練</strong>、<strong>🚀 開發產品</strong>，且工項類型支援<strong>增、刪、修</strong>）與<strong>起訖時間 (起時 ~ 迄時)</strong>。當工項達到<strong>迄時 (endDate)</strong> 時，系統背景排程與即時檢核會自動將該工項狀態轉換為「<strong>已完成 (已到期扣點)</strong>」、自動扣掉該工項與負責員工/部門身上的 <strong>Story Point</strong>，並透過 <strong>Email 寄信通知所屬部門管理層（部門經理）與 HR</strong>。
                        </div>
                    </div>
                    <form method='post' action='/workitem/checkExpired' style='margin:0;'>
                        <button type='submit' class='btn btn-primary' style='background:#1d4ed8;box-shadow:0 4px 12px rgba(29,78,216,0.22);white-space:nowrap;'>⚡ 立即檢核到期工項（自動轉態 + 扣減 Story Point + 寄信給部門管理層與 HR）</button>
                    </form>
                </div>
            </div>");

            renderWorkItemKindCrudCard("workitems");

            sb.Append($@"
            <div class='grid-2'>
                <div class='card'>
                    <div class='card-title'>
                        <span id='wiFormTitle'>➕ 建立/修改工項（設定工項類型、起訖時間與 Story Point）</span>
                        <button type='button' class='btn btn-outline' style='padding:4px 10px;font-size:12px;' onclick=""resetWorkItemForm('{nextWiCode}')"">🔄 清空切換為新增工項</button>
                    </div>
                    <form method='post' action='/workitem/save'>
                        <input type='hidden' name='workItemPk' id='wiPk' value='' />
                        <div class='form-row'>
                            <div>
                                <label data-tip='工項唯一識別代碼（例如：WI-2026-001）' title='工項唯一識別代碼（例如：WI-2026-001）'>工項代碼 <span class='sp-tip-icon'>ⓘ</span></label>
                                <input type='text' name='itemCode' id='wiCode' value='{nextWiCode}' required />
                            </div>
                            <div>
                                <label data-tip='選擇工項類型（如：專案、維運、顧問時數，或於上方工項類型管理新增的自訂類型）' title='選擇工項類型（如：專案、維運、顧問時數，或於上方工項類型管理新增的自訂類型）'>工項類型 <span class='sp-tip-icon'>ⓘ</span></label>
                                <select name='workItemKind' id='wiKindSelect' required style='border-color:#2563eb;font-weight:700;background:#eff6ff;'>
                                    {dynamicKindOptionsHtml}
                                </select>
                            </div>
                            <div>
                                <label data-tip='輸入工項標題與功能簡述' title='輸入工項標題與功能簡述'>工項標題 <span class='sp-tip-icon'>ⓘ</span></label>
                                <input type='text' name='itemTitle' id='wiTitle' placeholder='請輸入工項名稱' required />
                            </div>
                        </div>
                        <div class='form-row'>
                            <div>
                                <label data-tip='設定工項開始時間（起時 startDate）' title='設定工項開始時間（起時 startDate）'>開始時間 (起時) <span class='sp-tip-icon'>ⓘ</span></label>
                                <input type='datetime-local' name='startDate' id='wiStartDate' value='{defaultStartIso}' onchange='applyWorkItemDurationPreset()' required />
                            </div>
                            <div>
                                <label data-tip='設定工項結束時間（迄時 endDate）：當達到迄時，系統自動轉換該工項狀態為已完成、扣掉該 Story Point，並寄信給部門管理層與 HR' title='設定工項結束時間（迄時 endDate）：當達到迄時，系統自動轉換該工項狀態為已完成、扣掉該 Story Point，並寄信給部門管理層與 HR'>結束時間 (迄時 - 到期自動扣點寄信) <span class='sp-tip-icon'>ⓘ</span></label>
                                <input type='datetime-local' name='endDate' id='wiEndDate' value='{defaultEndIso}' required style='border-color:#dc2626;font-weight:700;' />
                            </div>
                            <div>
                                <label data-tip='快速設定工項起訖時間範圍：手動輸入數量（預設 1）並選擇單位（天、周、月、季、年），自動依據起時計算迄時' title='快速設定工項起訖時間範圍：手動輸入數量（預設 1）並選擇單位（天、周、月、季、年），自動依據起時計算迄時'>起訖時間快速設定 <span class='sp-tip-icon'>ⓘ</span></label>
                                <div style='display:grid;grid-template-columns:1fr 1.15fr;gap:6px;align-items:center;'>
                                    <div style='display:flex;align-items:center;gap:4px;'>
                                        <span style='font-size:12px;color:#475569;white-space:nowrap;font-weight:700;'>數量</span>
                                        <input type='number' id='wiDurationQty' name='durationQuantity' value='1' min='1' step='1' placeholder='1' oninput='applyWorkItemDurationPreset()' onchange='applyWorkItemDurationPreset()' title='手動輸入數量（預設 1）' style='text-align:center;font-weight:700;padding:8px 6px;' />
                                    </div>
                                    <div style='display:flex;align-items:center;gap:4px;'>
                                        <span style='font-size:12px;color:#475569;white-space:nowrap;font-weight:700;'>單位</span>
                                        <select id='wiDurationUnit' name='durationUnit' onchange='applyWorkItemDurationPreset()' title='選擇單位：天、周、月、季、年' style='font-weight:700;color:#1e40af;background:#eff6ff;border-color:#93c5fd;padding:8px 8px;'>
                                            <option value='day' selected>天</option>
                                            <option value='week'>周</option>
                                            <option value='month'>月</option>
                                            <option value='quarter'>季</option>
                                            <option value='year'>年</option>
                                        </select>
                                    </div>
                                </div>
                            </div>
                        </div>
                        <div class='form-row'>
                            <div style='grid-column: span 2;'>
                                <label data-tip='選擇已賦予標準 Story Point 的「工項代表類型」，選擇後系統會自動帶入標準點數與預設工項類型' title='選擇已賦予標準 Story Point 的「工項代表類型」，選擇後系統會自動帶入標準點數與預設工項類型'>工項代表類型 <span class='sp-tip-icon'>ⓘ</span></label>
                                <select name='workItemTypePk' id='wiTypeSelect' onchange='onWorkItemTypeChange()'>
                                    <option value='0' data-pt='' data-kind=''>-- 不使用代表類型（直接手動輸入點數） --</option>");
            foreach (DataRow tRow in typeTable.Rows)
            {
                string ptVal = tRow["standardStoryPoint"] != DBNull.Value ? tRow["standardStoryPoint"].ToString()! : "";
                string dKind = tRow.Table.Columns.Contains("defaultWorkItemKind") ? (tRow["defaultWorkItemKind"]?.ToString() ?? "project") : "project";
                string dKindText = tRow.Table.Columns.Contains("defaultWorkItemKindText") ? (tRow["defaultWorkItemKindText"]?.ToString() ?? "專案") : "專案";
                sb.Append($"<option value='{tRow["workItemTypePk"]}' data-pt='{ptVal}' data-kind='{h(dKind)}'>[{h(dKindText)}] {h(tRow["typeCode"])} - {h(tRow["typeName"])} (標準: {(string.IsNullOrEmpty(ptVal) ? "待估算" : ptVal + " pt")})</option>");
            }
            sb.Append(@"
                                </select>
                            </div>
                            <div>
                                <label data-tip='Story Point 點數：選擇代表類型後會自動帶入，管理者亦可手動修改微調（留空即自動帶入代表類型點數）' title='Story Point 點數：選擇代表類型後會自動帶入，管理者亦可手動修改微調（留空即自動帶入代表類型點數）'>Story Point 點數 <span class='sp-tip-icon'>ⓘ</span></label>
                                <input type='number' step='0.5' name='storyPoint' id='wiStoryPoint' placeholder='留空即自動帶入代表類型點數' />
                            </div>
                        </div>
                        <div class='form-row'>
                            <div>
                                <label data-tip='設定此工項歸屬的開發衝刺週期（Sprint）或專案階段名稱' title='設定此工項歸屬的開發衝刺週期（Sprint）或專案階段名稱'>所屬 Sprint / 階段 <span class='sp-tip-icon'>ⓘ</span></label>
                                <input type='text' name='sprintName' id='wiSprint' value='2026-Q4 Sprint 2' />
                            </div>
                            <div>
                                <label data-tip='設定工項目前的執行狀態：待處理 (To Do)、進行中 (In Progress) 或已完成 (Done)；當達到迄時將自動轉為已完成並扣點' title='設定工項目前的執行狀態：待處理 (To Do)、進行中 (In Progress) 或已完成 (Done)；當達到迄時將自動轉為已完成並扣點'>執行狀態 <span class='sp-tip-icon'>ⓘ</span></label>
                                <select name='executionStatus' id='wiStatus'>
                                    <option value='todo'>待處理 (To Do)</option>
                                    <option value='inProgress'>進行中 (In Progress)</option>
                                    <option value='done'>已完成 (Done)</option>
                                </select>
                            </div>
                            <div>
                                <label data-tip='若有手動調整 Story Point（與代表類型標準點數不同），請填寫微調原因以記錄於點數變更歷程' title='若有手動調整 Story Point（與代表類型標準點數不同），請填寫微調原因以記錄於點數變更歷程'>點數微調原因 <span class='sp-tip-icon'>ⓘ</span></label>
                                <input type='text' name='adjustmentReason' id='wiReason' placeholder='選填：說明為何與代表類型不同' />
                            </div>
                        </div>
                        <div class='form-row'>
                            <div style='grid-column: span 2;'>
                                <label data-tip='建立工項時可同時賦予給一位負責員工；儲存後亦可於右側面板新增多位共同分攤員工' title='建立工項時可同時賦予給一位負責員工；儲存後亦可於右側面板新增多位共同分攤員工'>指派主要負責員工 <span class='sp-tip-icon'>ⓘ</span></label>
                                <select name='primaryEmployeePk'>
                                    <option value='0'>-- 暫不指派（或稍後於右側指派） --</option>");
            foreach (DataRow eRow in empTable.Rows)
            {
                sb.Append($"<option value='{eRow["employeePk"]}'>{h(eRow["employeeCode"])} - {h(eRow["employeeName"])} ({h(eRow["departmentName"])}｜{h(eRow["jobTitle"])}｜目前 {eRow["totalAllocatedStoryPoint"]} pt)</option>");
            }
            sb.Append(@"
                                </select>
                            </div>
                            <div>
                                <label data-tip='設定該負責員工在此工項的 Story Point 分攤比例（1~100%，預設單人 100%）' title='設定該負責員工在此工項的 Story Point 分攤比例（1~100%，預設單人 100%）'>分攤比例 (%) <span class='sp-tip-icon'>ⓘ</span></label>
                                <input type='number' step='5' name='primaryAllocationPercent' value='100' />
                            </div>
                        </div>
                        <button type='submit' class='btn btn-primary'>💾 儲存工項設定（若已達迄時將自動轉態扣點並寄信）</button>
                    </form>
                </div>

                <div class='card' id='assignWorkItemCard'>
                    <div class='card-title'><span>🔄 管理者修改員工賦予的工項與點數分攤配額</span></div>
                    <form method='post' action='/workitem/assign'>
                        <div class='form-row'>
                            <div style='grid-column: span 2;'>
                                <label data-tip='選擇要新增、調整或移除負責員工配額的目標工項' title='選擇要新增、調整或移除負責員工配額的目標工項'>選擇目標工項 <span class='sp-tip-icon'>ⓘ</span></label>
                                <select name='workItemPk' id='assignWorkItemSelect' required>");
            if (itemTable.Rows.Count == 0)
            {
                sb.Append("<option value=''>-- 目前尚無工項，請先於左側建立或匯入工項 --</option>");
            }
            foreach (DataRow wRow in itemTable.Rows)
            {
                string wKindLabel = wRow.Table.Columns.Contains("workItemKindText") ? (wRow["workItemKindText"]?.ToString() ?? "專案") : "專案";
                sb.Append($"<option value='{wRow["workItemPk"]}'>[{h(wKindLabel)}] {h(wRow["itemCode"])} - {h(wRow["itemTitle"])} (目前點數: {wRow["storyPoint"]} pt)</option>");
            }
            sb.Append(@"
                                </select>
                            </div>
                        </div>
                        <div class='form-row'>
                            <div style='grid-column: span 2;'>
                                <label data-tip='選擇要被賦予此工項或調整點數分攤配額的員工' title='選擇要被賦予此工項或調整點數分攤配額的員工'>賦予給哪位員工 <span class='sp-tip-icon'>ⓘ</span></label>
                                <select name='employeePk' id='assignEmployeeSelect' required>");
            foreach (DataRow eRow in empTable.Rows)
            {
                sb.Append($"<option value='{eRow["employeePk"]}'>{h(eRow["employeeCode"])} - {h(eRow["employeeName"])} （部門：{h(eRow["departmentName"])}｜職級：{h(eRow["jobTitle"])}｜目前身上 {eRow["totalAllocatedStoryPoint"]} pt）</option>");
            }
            sb.Append(@"
                                </select>
                            </div>
                        </div>
                        <div class='form-row'>
                            <div>
                                <label data-tip='選擇指派動作：新增/更新該員工分攤比例、改由該員工單獨 100% 負責、或移除該員工指派' title='選擇指派動作：新增/更新該員工分攤比例、改由該員工單獨 100% 負責、或移除該員工指派'>指派動作 <span class='sp-tip-icon'>ⓘ</span></label>
                                <select name='actionType'>
                                    <option value='upsert'>➕ 新增或更新該員工分攤比例</option>
                                    <option value='replaceSingle'>🎯 改由該員工單獨 100% 負責</option>
                                    <option value='remove'>🗑️ 移除該員工在此工項的指派</option>
                                </select>
                            </div>
                            <div>
                                <label data-tip='設定該員工在此工項的擔任角色（如：主要負責、協同開發、Code Review 等）' title='設定該員工在此工項的擔任角色（如：主要負責、協同開發、Code Review 等）'>負責角色 <span class='sp-tip-icon'>ⓘ</span></label>
                                <input type='text' name='assignmentRole' value='主要負責' />
                            </div>
                            <div>
                                <label data-tip='設定該員工分攤此工項的 Story Point 百分比（系統會自動依比例計算 allocatedStoryPoint）' title='設定該員工分攤此工項的 Story Point 百分比（系統會自動依比例計算 allocatedStoryPoint）'>分攤比例 (%) <span class='sp-tip-icon'>ⓘ</span></label>
                                <input type='number' step='5' name='allocationPercent' value='100' />
                            </div>
                        </div>
                        <div class='form-row'>
                            <div style='grid-column: span 3;'>
                                <label data-tip='填寫此筆工項指派或分攤配額的補充說明備註' title='填寫此筆工項指派或分攤配額的補充說明備註'>指派備註 <span class='sp-tip-icon'>ⓘ</span></label>
                                <input type='text' name='assignmentNote' placeholder='例如：負責後端 Stored Procedure 與單元測試' />
                            </div>
                        </div>
                        <button type='submit' class='btn btn-success'>✅ 更新員工賦予的工項</button>
                    </form>
                </div>
            </div>
            </div>

            <!-- 批次匯入工項與 Story Point 面板 (提供 CSV 與 Excel 兩種匯入選項) -->
            <div id='drawer-wi-import' class='sp-collapsible-drawer collapsed'>
            <div class='card' style='border:1px solid #93c5fd;background:linear-gradient(180deg,#ffffff 0%,#f8fafc 100%);'>
                <div class='card-title'>
                    <span>📤 批次匯入工項與 Story Point（支援工項類型「專案/維運/顧問時數/自訂類型」與起訖時間，提供 CSV、Excel 兩種匯入選項）</span>
                    <div style='display:flex;gap:8px;flex-wrap:wrap;'>
                        <a href='/workitem/import/template?format=csv' class='btn btn-outline' style='padding:5px 12px;font-size:12px;'>📄 下載工項 CSV 範本</a>
                        <a href='/workitem/import/template?format=excel' class='btn btn-outline' style='padding:5px 12px;font-size:12px;'>📊 下載工項 Excel (.xlsx) 範本</a>
                    </div>
                </div>
                <form method='post' action='/workitem/import' enctype='multipart/form-data'>
                    <div class='form-row' style='align-items:end;'>
                        <div>
                            <label data-tip='選擇批次匯入工項的檔案格式選項：CSV (.csv) 或 Excel (.xlsx / .xls)' title='選擇批次匯入工項的檔案格式選項：CSV (.csv) 或 Excel (.xlsx / .xls)'>匯入檔案格式 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select name='importFormat' id='wiImportFormatSelect' onchange=""const f=document.getElementById('wiImportFileInput'); if(f) f.accept=(this.value==='excel'?'.xlsx,.xls,.xml':'.csv,.txt,.xlsx');"">
                                <option value='csv'>📄 選項 1：匯入 CSV 檔案 (.csv)</option>
                                <option value='excel'>📊 選項 2：匯入 Excel 檔案 (.xlsx / .xls)</option>
                            </select>
                        </div>
                        <div style='grid-column: span 2;'>
                            <label data-tip='選擇本機要上傳批次匯入的 CSV 或 Excel (.xlsx) 工項檔案' title='選擇本機要上傳批次匯入的 CSV 或 Excel (.xlsx) 工項檔案'>上傳工項匯入檔案 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='file' name='importFile' id='wiImportFileInput' accept='.csv,.xlsx,.xls,.xml,.txt' style='padding:6px;background:#fff;border:1px solid #cbd5e1;border-radius:7px;width:100%;' />
                        </div>
                        <div>
                            <button type='submit' class='btn btn-primary' style='width:100%;'>🚀 執行批次匯入工項</button>
                        </div>
                    </div>
                </form>
            </div>
            </div>");
        }

        // 各工項的 Story Point 清單表格（管理者檢視全部工項，一般員工僅檢視賦予給自己的工項與個人分數）
        sb.Append($@"
        <div class='card'>
            <div class='card-title'>
                <span>📋 {(isAdmin ? "各工項的類型（專案/維運/顧問時數/自訂類型）、起訖時間、Story Point 與到期自動扣點寄信一覽表" : "我的工項與個人獲得分數一覽表（僅顯示賦予給我本人的工項與分數，不顯示其他人）")}</span>
                <div style='display:flex;gap:8px;flex-wrap:wrap;align-items:center;'>
                    <span class='badge badge-user' id='wiFilterCountLabel'>共 {itemTable.Rows.Count} 筆工項</span>
                    <a href='/export/report?type=workitem&format=csv' class='btn btn-outline' style='padding:6px 12px;font-size:12.5px;'>📥 匯出 CSV</a>
                    <a href='/export/report?type=workitem&format=excel' class='btn btn-outline' style='padding:6px 12px;font-size:12.5px;'>📊 匯出 Excel (.xlsx)</a>
                    <a href='/export/report?type=workitem&format=html' class='btn btn-primary' style='padding:6px 12px;font-size:12.5px;'>🌐 匯出互動 HTML (排序・篩選・群組彙總)</a>
                </div>
            </div>

            <!-- 工項清單多維度即時篩選列 + 滑鼠右鍵快捷選單操作說明 -->
            <div style='background:#f8fafc;border:1px solid #e2e8f0;border-radius:10px;padding:12px 14px;margin-bottom:14px;'>
                <div style='display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:8px;margin-bottom:10px;'>
                    <div style='font-size:12.5px;color:#1e3a8a;font-weight:700;'>
                        💡 <strong>操作提示：</strong>於表格內任一筆紀錄<strong>滑鼠雙擊 (Double-Click)</strong> 直接彈出單筆編輯 Modal（含負責員工與配額指派）；<strong>按滑鼠右鍵</strong>開啟快捷選單執行<strong>單筆編輯、修改指派員工、發起盲投、刪除、以此欄值篩選</strong>（選取列後亦可按 <code>Enter</code> / <code>F2</code> 編輯）！
                    </div>
                    <button type='button' class='btn btn-outline' style='padding:4px 10px;font-size:12px;' onclick='resetWorkItemFilter()'>🔄 清除全部篩選條件</button>
                </div>
                <div class='form-row' style='margin-bottom:0;align-items:end;grid-template-columns:repeat(auto-fit,minmax(175px,1fr));'>
                    <div>
                        <label data-tip='輸入工項代碼、標題、說明、Sprint、代表類型、負責員工或部門關鍵字即時篩選表格' title='輸入工項代碼、標題、說明、Sprint、代表類型、負責員工或部門關鍵字即時篩選表格'>🔍 關鍵字快速篩選 <span class='sp-tip-icon'>ⓘ</span></label>
                        <input type='text' id='filterWiKeyword' oninput='filterWorkItemTable()' placeholder='輸入代碼 / 標題 / 員工 / 部門 / 說明...' />
                    </div>
                    <div>
                        <label data-tip='依工項類型（專案 / 維運 / 顧問時數 / 自訂類型）過濾工項列表' title='依工項類型（專案 / 維運 / 顧問時數 / 自訂類型）過濾工項列表'>🏷️ 工項類型篩選 <span class='sp-tip-icon'>ⓘ</span></label>
                        <select id='filterWiKind' onchange='filterWorkItemTable()'>
                            <option value=''>全部工項類型</option>");
        foreach (DataRow kRow in kindTable.Rows)
        {
            string kCode = kRow["kindCode"]?.ToString() ?? "";
            string kName = kRow["kindName"]?.ToString() ?? "";
            string kIcon = kRow.Table.Columns.Contains("badgeIcon") && kRow["badgeIcon"] != DBNull.Value ? (kRow["badgeIcon"]?.ToString() ?? "📌") : "📌";
            sb.Append($"<option value='{h(kCode)}'>{h(kIcon)} {h(kName)} ({h(kCode)})</option>");
        }
        sb.Append(@"
                        </select>
                    </div>
                    <div>
                        <label data-tip='依所屬工項代表類型過濾工項列表' title='依所屬工項代表類型過濾工項列表'>📐 所屬代表類型篩選 <span class='sp-tip-icon'>ⓘ</span></label>
                        <select id='filterWiType' onchange='filterWorkItemTable()'>
                            <option value=''>全部代表類型</option>");
        foreach (DataRow tRow in typeTable.Rows)
        {
            sb.Append($"<option value='{h(tRow["typeName"])}'>{h(tRow["typeCode"])} - {h(tRow["typeName"])}</option>");
        }
        sb.Append(@"
                        </select>
                    </div>
                    <div>
                        <label data-tip='依工項執行狀態或是否已達迄時自動扣點過濾工項列表' title='依工項執行狀態或是否已達迄時自動扣點過濾工項列表'>⚡ 執行與到期狀態篩選 <span class='sp-tip-icon'>ⓘ</span></label>
                        <select id='filterWiStatus' onchange='filterWorkItemTable()'>
                            <option value=''>全部狀態</option>
                            <option value='todo'>📝 待處理 (To Do)</option>
                            <option value='inProgress'>🚀 進行中 (In Progress)</option>
                            <option value='done'>✅ 已完成 (Done)</option>
                            <option value='expired'>⏰ 已達迄時自動扣點寄信</option>
                            <option value='activePt'>💎 有效計點中 (尚未到期扣點)</option>
                        </select>
                    </div>
                    <div>
                        <label data-tip='依負責員工姓名或所屬部門過濾工項列表' title='依負責員工姓名或所屬部門過濾工項列表'>👤 負責員工 / 部門篩選 <span class='sp-tip-icon'>ⓘ</span></label>
                        <select id='filterWiAssignee' onchange='filterWorkItemTable()'>
                            <option value=''>全部負責員工與部門</option>
                            <option value='尚未指派'>⚪ 尚未指派員工</option>");
        foreach (DataRow dRow in deptTable.Rows)
        {
            sb.Append($"<option value='{h(dRow["departmentName"])}'>🏢 部門：{h(dRow["departmentName"])}</option>");
        }
        foreach (DataRow eRow in empTable.Rows)
        {
            sb.Append($"<option value='{h(eRow["employeeName"])}'>👤 {h(eRow["employeeCode"])} - {h(eRow["employeeName"])} ({h(eRow["departmentName"])})</option>");
        }
        sb.Append($@"
                        </select>
                    </div>
                </div>
            </div>

            <table id='workItemDataTable'>
                <thead>
                    <tr>
                        <th>工項代碼</th>
                        <th>工項類型</th>
                        <th>工項標題</th>
                        <th>起訖時間 (起時 ~ 迄時)</th>
                        <th>所屬代表類型</th>
                        <th>工項 Story Point</th>
                        <th>點數來源與到期扣點紀錄</th>
                        <th>執行狀態與到期寄信通知</th>
                        <th>{(isAdmin ? "賦予員工與部門配額明細" : "我的分攤比例與個人獲得分數")}</th>
                    </tr>
                </thead>
                <tbody>");

        foreach (DataRow wRow in itemTable.Rows)
        {
            int wiPkVal = Convert.ToInt32(wRow["workItemPk"]);
            bool isManual = Convert.ToBoolean(wRow["isManualAdjusted"]);
            bool isExpiredProc = wRow.Table.Columns.Contains("isExpiredProcessed") && wRow["isExpiredProcessed"] != DBNull.Value && Convert.ToBoolean(wRow["isExpiredProcessed"]);
            bool notifSent = wRow.Table.Columns.Contains("expiryNotificationSent") && wRow["expiryNotificationSent"] != DBNull.Value && Convert.ToBoolean(wRow["expiryNotificationSent"]);
            string notifRecipients = wRow.Table.Columns.Contains("expiryNotificationRecipient") ? (wRow["expiryNotificationRecipient"]?.ToString() ?? "") : "";
            decimal curPt = wRow["storyPoint"] != DBNull.Value ? Convert.ToDecimal(wRow["storyPoint"]) : 0m;
            decimal origPt = wRow.Table.Columns.Contains("originalStoryPoint") && wRow["originalStoryPoint"] != DBNull.Value ? Convert.ToDecimal(wRow["originalStoryPoint"]) : curPt;
            decimal dedPt = wRow.Table.Columns.Contains("deductedStoryPoint") && wRow["deductedStoryPoint"] != DBNull.Value ? Convert.ToDecimal(wRow["deductedStoryPoint"]) : 0m;
            decimal editPt = isExpiredProc ? origPt : curPt;

            string wKind = wRow.Table.Columns.Contains("workItemKind") ? (wRow["workItemKind"]?.ToString() ?? "project") : "project";
            string wKindText = wRow.Table.Columns.Contains("workItemKindText") ? (wRow["workItemKindText"]?.ToString() ?? "專案") : "專案";
            string wKindIcon = wRow.Table.Columns.Contains("workItemKindIcon") && wRow["workItemKindIcon"] != DBNull.Value ? (wRow["workItemKindIcon"]?.ToString() ?? "🏗️") : "🏗️";
            string wKindColor = wRow.Table.Columns.Contains("workItemKindColor") && wRow["workItemKindColor"] != DBNull.Value ? (wRow["workItemKindColor"]?.ToString() ?? "blue") : "blue";
            string wBadgeStyle = wKindColor switch
            {
                "amber" => "background:#fef3c7;color:#b45309;border:1px solid #fde68a;",
                "purple" => "background:#f3e8ff;color:#6b21a8;border:1px solid #d8b4fe;",
                "green" => "background:#dcfce7;color:#15803d;border:1px solid #86efac;",
                "red" => "background:#fee2e2;color:#b91c1c;border:1px solid #fca5a5;",
                _ => "background:#dbeafe;color:#1e40af;border:1px solid #93c5fd;"
            };
            string kindBadge = $"<span class='badge' style='{wBadgeStyle}'>{h(wKindIcon)} {h(wKindText)}</span>";

            string startDisplay = wRow.Table.Columns.Contains("startDate") && wRow["startDate"] != DBNull.Value
                ? Convert.ToDateTime(wRow["startDate"]).ToString("yyyy-MM-dd HH:mm")
                : "-";
            string endDisplay = wRow.Table.Columns.Contains("endDate") && wRow["endDate"] != DBNull.Value
                ? Convert.ToDateTime(wRow["endDate"]).ToString("yyyy-MM-dd HH:mm")
                : "-";
            string startIsoJs = wRow.Table.Columns.Contains("startDate") && wRow["startDate"] != DBNull.Value
                ? Convert.ToDateTime(wRow["startDate"]).ToString("yyyy-MM-ddTHH:mm")
                : DateTime.Now.ToString("yyyy-MM-ddTHH:mm");
            string endIsoJs = wRow.Table.Columns.Contains("endDate") && wRow["endDate"] != DBNull.Value
                ? Convert.ToDateTime(wRow["endDate"]).ToString("yyyy-MM-ddTHH:mm")
                : DateTime.Now.AddDays(14).ToString("yyyy-MM-ddTHH:mm");

            string pointCellHtml = isExpiredProc
                ? $"<div><strong style='font-size:16px;color:#dc2626;'>0.00 pt</strong> <span class='badge' style='background:#fee2e2;color:#b91c1c;border:1px solid #fca5a5;'>已扣 -{dedPt:0.##} pt</span></div><div style='font-size:11.5px;color:#64748b;margin-top:2px;'>原核定：{origPt:0.##} pt</div>"
                : $"<strong style='font-size:17px;color:#2563eb;'>{curPt:0.##} pt</strong><div style='font-size:11px;color:#64748b;'>標準：{(wRow["defaultTypePoint"] != DBNull.Value ? wRow["defaultTypePoint"] + " pt" : "-")}</div>";

            string srcBadge = isExpiredProc
                ? $"<span class='badge' style='background:#fee2e2;color:#991b1b;border:1px solid #fca5a5;'>⏰ 到期自動扣點</span><div style='font-size:11.5px;color:#334155;margin-top:3px;'>{h(wRow["adjustmentReason"])}</div>"
                : (isManual
                    ? $"<span class='badge badge-voting'>手動調整</span> <div style='font-size:11.5px;color:#64748b;'>{h(wRow["adjustmentReason"])}</div>"
                    : "<span class='badge badge-finalized'>代表類型自動帶入</span>");

            string statusAndMailHtml = isExpiredProc
                ? $"<div><span class='badge badge-finalized'>✅ {h(wRow["executionStatusText"])}</span></div>" +
                  (notifSent
                      ? $"<div style='font-size:11px;color:#15803d;margin-top:4px;word-break:break-all;'>📧 已寄信通知部門管理層與 HR：<br/><code>{h(notifRecipients)}</code></div>"
                      : "<div style='font-size:11px;color:#d97706;margin-top:4px;'>📧 待寄發到期通知信</div>")
                : $"<div><span class='badge badge-user'>{h(wRow["executionStatusText"])}</span></div><div style='font-size:11px;color:#64748b;margin-top:3px;'>⏳ 達迄時將自動轉態扣點並寄信</div>";

            string wCodeStr = wRow["itemCode"]?.ToString() ?? "";
            string wTitleStr = wRow["itemTitle"]?.ToString() ?? "";
            string wDescStr = wRow["itemDescription"]?.ToString() ?? "";
            string wSprintStr = wRow["sprintName"]?.ToString() ?? "";
            string wTypePkStr = wRow["workItemTypePk"] != DBNull.Value ? wRow["workItemTypePk"].ToString()! : "0";
            string wTypeNameStr = wRow["typeName"]?.ToString() ?? "";
            string wStatusStr = wRow["executionStatus"]?.ToString() ?? "todo";
            string wReasonStr = wRow["adjustmentReason"]?.ToString() ?? "";
            string wAssigneeStr = wRow["assigneeSummary"]?.ToString() ?? "";
            int wiLastSessPk = wRow.Table.Columns.Contains("lastVotingSessionPk") && wRow["lastVotingSessionPk"] != DBNull.Value
                ? Convert.ToInt32(wRow["lastVotingSessionPk"])
                : 0;
            string wiSearchBlob = $"{wCodeStr} {wTitleStr} {wDescStr} {wSprintStr} {wKind} {wKindText} {wRow["typeCode"]} {wTypeNameStr} {wAssigneeStr} {wReasonStr} {notifRecipients}".ToLowerInvariant();

            sb.Append($@"<tr class='wi-row sp-context-row'
                data-row-kind='workitem'
                data-search='{h(wiSearchBlob)}'
                data-wi-pk='{wiPkVal}'
                data-wi-code='{h(wCodeStr)}'
                data-wi-title='{h(wTitleStr)}'
                data-wi-desc='{h(wDescStr)}'
                data-wi-sprint='{h(wSprintStr)}'
                data-wi-type-pk='{h(wTypePkStr)}'
                data-wi-type-name='{h(wTypeNameStr)}'
                data-wi-pt='{editPt.ToString(CultureInfo.InvariantCulture)}'
                data-wi-status='{h(wStatusStr)}'
                data-wi-reason='{h(wReasonStr)}'
                data-wi-kind='{h(wKind)}'
                data-wi-kind-text='{h(wKindText)}'
                data-wi-start='{h(startIsoJs)}'
                data-wi-end='{h(endIsoJs)}'
                data-wi-expired='{(isExpiredProc ? "1" : "0")}'
                data-wi-assignee='{h(wAssigneeStr)}'
                data-wi-last-session-pk='{wiLastSessPk}'
                title='💡 按滑鼠右鍵選擇此筆工項並開啟操作選單（彈出 Modal 單筆編輯、修改指派員工、重新投票、發起盲投、刪除）'>
                <td data-sort-value='{h(wCodeStr)}'>
                    <div><strong>{h(wCodeStr)}</strong></div>
                    <div style='font-size:11px;color:#64748b;margin-top:2px;'>{h(wSprintStr)}</div>
                </td>
                <td style='white-space:nowrap;'>{kindBadge}</td>
                <td><strong>{h(wTitleStr)}</strong><div style='font-size:12px;color:#64748b;'>{h(wRow["itemDescription"])}</div></td>
                <td style='white-space:nowrap;font-size:12px;'>
                    <div>🟢 起：{h(startDisplay)}</div>
                    <div style='color:{(isExpiredProc ? "#dc2626;font-weight:700" : "#1e293b")};'>🔴 迄：{h(endDisplay)}</div>
                </td>
                <td>{h(wRow["typeCode"])}<br/><strong>{h(wTypeNameStr)}</strong></td>
                <td data-sort-value='{curPt}'>{pointCellHtml}</td>
                <td>{srcBadge}</td>
                <td>{statusAndMailHtml}</td>
                <td><strong>{h(wAssigneeStr)}</strong></td>
            </tr>");
        }
        sb.Append("</tbody></table></div></div>"); // end Tab 3

        // =====================================================================
        // TAB 4: 工項代表類型與工項類型增刪修管理
        // =====================================================================
        sb.Append($"<div id='pane-types' class='tab-pane {(currentTab == "types" ? "active" : "")}'>");
        if (isAdmin)
        {
            sb.Append(@"
            <div class='sp-page-action-bar'>
                <div class='sp-action-title'>
                    <span>⚡ 工項代表類型與工項類型快捷指令列</span>
                    <span style='font-size:12px;font-weight:600;color:#93c5fd;'>（表格直接置頂呈現｜支援左鍵單擊選取、雙擊列彈出 Modal 編輯、右鍵選單發起盲投）</span>
                </div>
                <div class='sp-action-group'>
                    <button type='button' class='btn btn-primary' style='padding:6px 12px;font-size:12.5px;background:#2563eb;border-color:#60a5fa;' onclick=""openWorkItemTypeEditModal('','','','後端與資料庫','','','project')"">➕ 新增工項代表類型 (彈出 Modal)</button>
                    <button type='button' class='sp-drawer-toggle-btn' onclick=""openWorkItemKindEditModal('','','','📌','blue',60,'','types')"">🗂️ 新增工項類型 (Modal)</button>
                    <button type='button' class='sp-drawer-toggle-btn' onclick=""toggleAdminDrawer('drawer-types-full', this)"">🛠️ 展開/收合工項類型與代表類型管理面板 ▾</button>
                </div>
            </div>
            <div id='drawer-types-full' class='sp-collapsible-drawer collapsed'>");

            renderWorkItemKindCrudCard("types");

            sb.Append($@"
            <div class='card'>
                <div class='card-title'>
                    <span id='typeFormTitle'>🏷️ 新增或修改「工項代表類型」（可設定預設工項類型與標準 Story Point）</span>
                    <button type='button' class='btn btn-outline' style='padding:4px 10px;font-size:12px;' onclick=""resetTypeForm()"">🔄 清空切換為新增代表類型</button>
                </div>
                <form method='post' action='/workitem/type/save'>
                    <input type='hidden' name='workItemTypePk' id='typePk' value='' />
                    <div class='form-row'>
                        <div>
                            <label data-tip='工項代表類型的唯一識別代碼（例如：TYPE_BATCH_JOB）' title='工項代表類型的唯一識別代碼（例如：TYPE_BATCH_JOB）'>代表類型代碼 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='text' name='typeCode' id='typeCode' placeholder='例如：TYPE_BATCH_JOB' required />
                        </div>
                        <div>
                            <label data-tip='工項代表類型的顯示名稱（例如：每日批次資料同步排程）' title='工項代表類型的顯示名稱（例如：每日批次資料同步排程）'>代表類型名稱 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='text' name='typeName' id='typeName' placeholder='例如：每日批次資料同步排程' required />
                        </div>
                        <div>
                            <label data-tip='設定此代表類型預設對應的工項類型（如：專案、維運、顧問時數或自訂工項類型）' title='設定此代表類型預設對應的工項類型（如：專案、維運、顧問時數或自訂工項類型）'>預設工項類型 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select name='defaultWorkItemKind' id='typeKind'>
                                {dynamicKindOptionsHtml}
                            </select>
                        </div>
                        <div>
                            <label data-tip='此工項代表類型所屬的技術或業務分類名稱' title='此工項代表類型所屬的技術或業務分類名稱'>分類名稱 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='text' name='categoryName' id='typeCat' value='後端與資料庫' required />
                        </div>
                        <div>
                            <label data-tip='標準 Story Point：可由多人盲投拍板定案自動寫入，或由管理者手動輸入設定' title='標準 Story Point：可由多人盲投拍板定案自動寫入，或由管理者手動輸入設定'>標準 Story Point <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='number' step='0.5' name='standardStoryPoint' id='typePt' placeholder='例如：5' />
                        </div>
                    </div>
                    <div class='form-row'>
                        <div style='grid-column: span 3;'>
                            <label data-tip='描述此代表類型包含的開發複雜度、技術範圍與驗收標準（DoD），作為團隊盲投與工項分類依據' title='描述此代表類型包含的開發複雜度、技術範圍與驗收標準（DoD），作為團隊盲投與工項分類依據'>複雜度與驗收標準說明 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='text' name='description' id='typeDesc' placeholder='描述此代表類型包含的開發與測試範圍，作為團隊投票與工項分類依據' />
                        </div>
                        <div style='display:flex;align-items:end;gap:10px;'>
                            <label style='display:flex;align-items:center;gap:6px;cursor:pointer;margin-bottom:8px;' data-tip='勾選後，儲存時將一併同步更新所有套用此代表類型、尚未完成且未曾手動微調過點數的既有工項' title='勾選後，儲存時將一併同步更新所有套用此代表類型、尚未完成且未曾手動微調過點數的既有工項'>
                                <input type='checkbox' name='syncExistingOpenWorkItem' value='true' checked />
                                <span>同步更新未完成且未微調之工項 <span class='sp-tip-icon'>ⓘ</span></span>
                            </label>
                            <button type='submit' class='btn btn-primary'>💾 儲存代表類型</button>
                        </div>
                    </div>
                </form>
            </div>
            </div>");
        }

        sb.Append($@"
        <div class='card'>
            <div class='card-title'>
                <span>🏷️ 工項代表類型與標準 Story Point 一覽表</span>
                <span class='badge badge-user' id='typeFilterCountLabel'>共 {typeTable.Rows.Count} 筆代表類型</span>
            </div>
            <!-- 工項代表類型即時篩選列 -->
            <div style='background:#f8fafc;border:1px solid #e2e8f0;border-radius:10px;padding:12px 14px;margin-bottom:14px;'>
                <div style='display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:8px;margin-bottom:10px;'>
                    <div style='font-size:12.5px;color:#1e3a8a;font-weight:700;'>
                        💡 <strong>操作提示：</strong>於代表類型列<strong>滑鼠雙擊 (Double-Click)</strong> 直接彈出單筆編輯 Modal；<strong>按滑鼠右鍵</strong>開啟快捷選單執行<strong>單筆編輯、重新投票、發起盲投、刪除、以此欄值篩選</strong>（選取列後亦可按 <code>Enter</code> / <code>F2</code> 編輯）！
                    </div>
                    <button type='button' class='btn btn-outline' style='padding:4px 10px;font-size:12px;' onclick='resetWorkItemTypeFilter()'>🔄 清除全部篩選條件</button>
                </div>
                <div class='form-row' style='margin-bottom:0;align-items:end;'>
                    <div>
                        <label data-tip='輸入代表類型代碼、名稱、分類或複雜度說明關鍵字即時篩選表格' title='輸入代表類型代碼、名稱、分類或複雜度說明關鍵字即時篩選表格'>🔍 關鍵字快速篩選 <span class='sp-tip-icon'>ⓘ</span></label>
                        <input type='text' id='filterTypeKeyword' oninput='filterWorkItemTypeTable()' placeholder='輸入類型代碼 / 名稱 / 分類 / 說明...' />
                    </div>
                    <div>
                        <label data-tip='依預設工項類型過濾代表類型列表' title='依預設工項類型過濾代表類型列表'>🏷️ 預設工項類型篩選 <span class='sp-tip-icon'>ⓘ</span></label>
                        <select id='filterTypeKind' onchange='filterWorkItemTypeTable()'>
                            <option value=''>全部預設工項類型</option>");
        foreach (DataRow kRow in kindTable.Rows)
        {
            string kCode = kRow["kindCode"]?.ToString() ?? "";
            string kName = kRow["kindName"]?.ToString() ?? "";
            string kIcon = kRow.Table.Columns.Contains("badgeIcon") && kRow["badgeIcon"] != DBNull.Value ? (kRow["badgeIcon"]?.ToString() ?? "📌") : "📌";
            sb.Append($"<option value='{h(kCode)}'>{h(kIcon)} {h(kName)}</option>");
        }
        sb.Append(@"
                        </select>
                    </div>
                    <div>
                        <label data-tip='依是否已完成標準點數核定過濾代表類型列表' title='依是否已完成標準點數核定過濾代表類型列表'>📊 估算狀態篩選 <span class='sp-tip-icon'>ⓘ</span></label>
                        <select id='filterTypeStatus' onchange='filterWorkItemTypeTable()'>
                            <option value=''>全部估算狀態</option>
                            <option value='finalized'>✅ 已核定標準 Story Point</option>
                            <option value='voting'>🃏 投票估算中</option>
                            <option value='pending'>⏳ 待投票定案</option>
                        </select>
                    </div>
                </div>
            </div>
            <table id='workItemTypeDataTable'>
                <thead><tr><th>類型代碼</th><th>代表類型名稱</th><th>預設工項類型</th><th>分類</th><th>複雜度與驗收說明</th><th>標準 Story Point</th><th>估算狀態</th><th>套用工項數</th></tr></thead><tbody>");
        foreach (DataRow tRow in typeTable.Rows)
        {
            int tPkVal = Convert.ToInt32(tRow["workItemTypePk"]);
            bool hasStdPt = tRow["standardStoryPoint"] != DBNull.Value;
            string ptJs = hasStdPt ? tRow["standardStoryPoint"].ToString()! : "";
            string stdPt = hasStdPt ? $"<strong style='font-size:16px;color:#2563eb;'>{tRow["standardStoryPoint"]} pt</strong>" : "<span style='color:#d97706;'>待投票定案</span>";
            string dKind = tRow.Table.Columns.Contains("defaultWorkItemKind") ? (tRow["defaultWorkItemKind"]?.ToString() ?? "project") : "project";
            string dKindText = tRow.Table.Columns.Contains("defaultWorkItemKindText") ? (tRow["defaultWorkItemKindText"]?.ToString() ?? "專案") : "專案";
            string dKindIcon = tRow.Table.Columns.Contains("defaultWorkItemKindIcon") && tRow["defaultWorkItemKindIcon"] != DBNull.Value ? (tRow["defaultWorkItemKindIcon"]?.ToString() ?? "🏗️") : "🏗️";
            string tCodeStr = tRow["typeCode"]?.ToString() ?? "";
            string tNameStr = tRow["typeName"]?.ToString() ?? "";
            string tCatStr = tRow["categoryName"]?.ToString() ?? "";
            string tDescStr = tRow["description"]?.ToString() ?? "";
            string tEstStatus = tRow.Table.Columns.Contains("estimationStatus") ? (tRow["estimationStatus"]?.ToString() ?? "pending") : "pending";
            int tLastSessPk = tRow.Table.Columns.Contains("lastVotingSessionPk") && tRow["lastVotingSessionPk"] != DBNull.Value
                ? Convert.ToInt32(tRow["lastVotingSessionPk"])
                : 0;
            string tEstBadgeCls = tEstStatus == "finalized" ? "badge-finalized" : (tEstStatus == "voting" ? "badge-voting" : "badge-revealed");
            string tSearchBlob = $"{tCodeStr} {tNameStr} {dKind} {dKindText} {tCatStr} {tDescStr} {ptJs}".ToLowerInvariant();

            sb.Append($@"<tr class='type-row sp-context-row'
                data-row-kind='workitemType'
                data-search='{h(tSearchBlob)}'
                data-type-pk='{tPkVal}'
                data-type-code='{h(tCodeStr)}'
                data-type-name='{h(tNameStr)}'
                data-type-cat='{h(tCatStr)}'
                data-type-desc='{h(tDescStr)}'
                data-type-pt='{h(ptJs)}'
                data-type-kind='{h(dKind)}'
                data-type-status='{(hasStdPt ? "finalized" : "pending")}'
                data-type-est-status='{h(tEstStatus)}'
                data-type-last-session-pk='{tLastSessPk}'
                title='💡 按滑鼠右鍵選擇此筆代表類型並開啟操作選單（彈出 Modal 單筆編輯、重新投票、發起盲投、刪除）'>
                <td data-sort-value='{h(tCodeStr)}'>
                    <strong>{h(tCodeStr)}</strong>
                </td>
                <td><strong>{h(tNameStr)}</strong></td>
                <td><span class='badge badge-user'>{h(dKindIcon)} {h(dKindText)}</span></td>
                <td>{h(tCatStr)}</td>
                <td>{h(tDescStr)}</td>
                <td data-sort-value='{(hasStdPt ? ptJs : "-1")}'>{stdPt}</td>
                <td><span class='badge {tEstBadgeCls}'>{h(tRow["estimationStatusText"])}</span></td>
                <td>{tRow["linkedWorkItemCount"]} 個工項</td>
            </tr>");
        }
        sb.Append("</tbody></table></div></div>"); // end Tab 4

        // =====================================================================
        // TAB 5: 查詢員工身上有多少 Story Point，以及員工所屬部門的總 Story Point 數
        // =====================================================================
        sb.Append($"<div id='pane-stats' class='tab-pane {(currentTab == "stats" ? "active" : "")}'>");
        if (isAdmin)
        {
            sb.Append(@"
            <div class='card'>
                <div class='card-title'>
                    <span>🏢 一、各部門總 Story Point 數與負載統計表 (vwDepartmentPointSummary)</span>
                    <div style='display:flex;gap:8px;flex-wrap:wrap;'>
                        <a href='/export/report?type=department&format=csv' class='btn btn-outline' style='padding:6px 12px;font-size:12.5px;'>📥 匯出部門 CSV</a>
                        <a href='/export/report?type=department&format=excel' class='btn btn-outline' style='padding:6px 12px;font-size:12.5px;'>📊 匯出部門 Excel (.xlsx)</a>
                        <a href='/export/report?type=department&format=html' class='btn btn-primary' style='padding:6px 12px;font-size:12.5px;'>🌐 匯出部門互動 HTML (排序・篩選・群組彙總)</a>
                    </div>
                </div>
                <table>
                    <thead>
                        <tr>
                            <th>部門代碼</th>
                            <th>部門名稱</th>
                            <th>部門主管</th>
                            <th>在職員工數</th>
                            <th>參與工項數</th>
                            <th>部門總 Story Point (分配加總)</th>
                            <th>到期已扣除點數</th>
                            <th>待處理點數</th>
                            <th>進行中點數</th>
                            <th>已完成點數</th>
                            <th>人均 Story Point 負載</th>
                        </tr>
                    </thead>
                    <tbody>");
            foreach (DataRow dRow in deptTable.Rows)
            {
                int dPkVal = dRow.Table.Columns.Contains("departmentPk") && dRow["departmentPk"] != DBNull.Value ? Convert.ToInt32(dRow["departmentPk"]) : 0;
                string dCodeVal = dRow["departmentCode"]?.ToString() ?? "";
                string dNameVal = dRow["departmentName"]?.ToString() ?? "";
                string dMgrVal = dRow["managerName"]?.ToString() ?? "";
                string dNoteVal = dRow["departmentNote"]?.ToString() ?? "";
                decimal deptDed = dRow.Table.Columns.Contains("totalDeductedDepartmentStoryPoint") && dRow["totalDeductedDepartmentStoryPoint"] != DBNull.Value
                    ? Convert.ToDecimal(dRow["totalDeductedDepartmentStoryPoint"])
                    : 0m;
                sb.Append($@"<tr class='sp-context-row' data-row-kind='department' data-dept-pk='{dPkVal}' data-dept-code='{h(dCodeVal)}' data-dept-name='{h(dNameVal)}' data-dept-mgr='{h(dMgrVal)}' data-dept-note='{h(dNoteVal)}' title='💡 按滑鼠右鍵選擇此筆部門紀錄並開啟操作選單（彈出 Modal 單筆編輯部門、篩選部門工項與員工）'>
                    <td><strong>{h(dCodeVal)}</strong></td>
                    <td><strong style='font-size:14.5px;'>{h(dNameVal)}</strong><div style='font-size:11.5px;color:#64748b;'>{h(dNoteVal)}</div></td>
                    <td>{h(dMgrVal)}</td>
                    <td>{dRow["activeEmployeeCount"]} 人</td>
                    <td>{dRow["distinctWorkItemCount"]} 件</td>
                    <td><strong style='font-size:18px;color:#2563eb;'>{dRow["totalDepartmentStoryPoint"]} pt</strong></td>
                    <td>{(deptDed > 0 ? $"<span class='badge' style='background:#fee2e2;color:#b91c1c;border:1px solid #fca5a5;'>-{deptDed:0.##} pt</span>" : "<span style='color:#94a3b8;'>0 pt</span>")}</td>
                    <td>{dRow["todoDepartmentStoryPoint"]} pt</td>
                    <td><strong style='color:#d97706;'>{dRow["inProgressDepartmentStoryPoint"]} pt</strong></td>
                    <td><strong style='color:#16a34a;'>{dRow["doneDepartmentStoryPoint"]} pt</strong></td>
                    <td><strong>{dRow["averageStoryPointPerEmployee"]} pt / 人</strong></td>
                </tr>");
            }
            sb.Append(@"
                    </tbody>
                </table>
            </div>");
        }

        sb.Append($@"
        <div class='card'>
            <div class='card-title'>
                <span>{(isAdmin ? "👩‍💻 二、各員工身上的工項與個人總 Story Point 數統計表 (vwEmployeePointSummary)" : "🙋 我的個人工項與總 Story Point 分數統計表（僅顯示您本人的工項與分數，不顯示其他員工）")}</span>
                <div style='display:flex;gap:8px;flex-wrap:wrap;'>
                    <a href='/export/report?type=employee&format=csv' class='btn btn-outline' style='padding:6px 12px;font-size:12.5px;'>📥 {(isAdmin ? "匯出員工 CSV" : "匯出我的分數 CSV")}</a>
                    <a href='/export/report?type=employee&format=excel' class='btn btn-outline' style='padding:6px 12px;font-size:12.5px;'>📊 {(isAdmin ? "匯出員工 Excel (.xlsx)" : "匯出我的分數 Excel (.xlsx)")}</a>
                    <a href='/export/report?type=employee&format=html' class='btn btn-primary' style='padding:6px 12px;font-size:12.5px;'>🌐 {(isAdmin ? "匯出員工互動 HTML (排序・篩選・群組彙總)" : "匯出我的分數互動 HTML (排序・篩選・群組彙總)")}</a>
                </div>
            </div>
            <table>
                <thead>
                    <tr>
                        <th>員工編號</th>
                        <th>員工姓名 (樹狀階層)</th>
                        <th>所屬部門</th>
                        <th>部門主管註記 / 職級</th>
                        <th>上一層主管 / 通知對象 (部門主管 + 人資主管)</th>
                        <th>角色</th>
                        <th>賦予工項數</th>
                        <th>{(isAdmin ? "員工身上總 Story Point" : "我身上的總 Story Point 分數")}</th>
                        <th>到期已扣除點數</th>
                        <th>待處理 / 進行中 / 已完成點數</th>
                        <th>{(isAdmin ? "賦予給該員工的工項與點數明細" : "賦予給我本人的工項與分數明細")}</th>
                    </tr>
                </thead>
                <tbody>");
        foreach (DataRow eRow in empTable.Rows)
        {
            int stEmpPk = Convert.ToInt32(eRow["employeePk"]);
            bool stEmpActive = Convert.ToBoolean(eRow["isActive"]);
            string empJobTitle = eRow["jobTitle"]?.ToString() ?? "";
            bool isDeptMgr = (eRow.Table.Columns.Contains("isDepartmentManager") && eRow["isDepartmentManager"] != DBNull.Value)
                ? Convert.ToBoolean(eRow["isDepartmentManager"])
                : empJobTitle.Contains("部門經理");
            string deptCodeStr = eRow.Table.Columns.Contains("departmentCode") ? (eRow["departmentCode"]?.ToString() ?? "") : "";
            bool isHrMgr = isDeptMgr && string.Equals(deptCodeStr, "HR", StringComparison.OrdinalIgnoreCase);
            int hLevel = eRow.Table.Columns.Contains("hierarchyLevel") && eRow["hierarchyLevel"] != DBNull.Value
                ? Convert.ToInt32(eRow["hierarchyLevel"])
                : 1;
            bool hasParentMgr = eRow.Table.Columns.Contains("managerEmployeePk") && eRow["managerEmployeePk"] != DBNull.Value;
            string stMgrPkStr = hasParentMgr ? eRow["managerEmployeePk"].ToString()! : "";
            string mgrNameStr = (hasParentMgr && eRow.Table.Columns.Contains("managerEmployeeName") && eRow["managerEmployeeName"] != DBNull.Value)
                ? (eRow["managerEmployeeName"]?.ToString() ?? "")
                : "";
            string parentMgrDisplay = (!string.IsNullOrWhiteSpace(mgrNameStr) && mgrNameStr != "頂層根節點(無上層)")
                ? $"👤 上一層主管：{h(mgrNameStr)}"
                : "🌳 上一層主管：無 (頂層根節點)";
            string deptMgrName = eRow.Table.Columns.Contains("departmentManagerName") ? (eRow["departmentManagerName"]?.ToString() ?? "") : "";
            string hrMgrName = eRow.Table.Columns.Contains("hrManagerName") ? (eRow["hrManagerName"]?.ToString() ?? "") : "";

            string mgrFlagBadge = isHrMgr
                ? "<span class='badge' style='background:#fce7f3;color:#9d174d;border:1px solid #f472b6;margin-right:4px;'>🤝 人資主管 (isDepartmentManager=1)</span>"
                : (isDeptMgr
                    ? "<span class='badge' style='background:#fef3c7;color:#92400e;border:1px solid #f59e0b;margin-right:4px;'>👑 部門主管 (isDepartmentManager=1)</span>"
                    : "<span class='badge' style='background:#f1f5f9;color:#475569;border:1px solid #cbd5e1;margin-right:4px;'>👨‍💻 一般同仁</span>");
            string titleHtml = $"{mgrFlagBadge}<strong>{h(empJobTitle)}</strong>";
            string levelPrefix = hLevel <= 1 ? "🌳 L1 Root" : $"↳ L{hLevel}";
            decimal empDed = eRow.Table.Columns.Contains("totalDeductedStoryPoint") && eRow["totalDeductedStoryPoint"] != DBNull.Value
                ? Convert.ToDecimal(eRow["totalDeductedStoryPoint"])
                : 0m;
            bool stIsSelf = (stEmpPk == curEmpPk);
            sb.Append($@"<tr class='sp-context-row'
                data-row-kind='employee'
                data-emp-pk='{stEmpPk}'
                data-emp-code='{h(eRow["employeeCode"])}'
                data-emp-name='{h(eRow["employeeName"])}'
                data-emp-acc='{h(eRow["loginAccount"])}'
                data-emp-mail='{h(eRow["emailAddress"])}'
                data-emp-title='{h(empJobTitle)}'
                data-emp-dept-pk='{eRow["departmentPk"]}'
                data-emp-role='{h(eRow["roleName"])}'
                data-emp-active='{(stEmpActive ? "true" : "false")}'
                data-emp-ismgr='{(isDeptMgr ? "true" : "false")}'
                data-emp-mgrpk='{h(stMgrPkStr)}'
                data-emp-self='{(stIsSelf ? "1" : "0")}'
                title='💡 按滑鼠右鍵選擇此筆員工紀錄並開啟操作選單（彈出 Modal 單筆編輯員工、檢視指派工項）'>
                <td><strong>{h(eRow["employeeCode"])}</strong></td>
                <td><span class='badge' style='background:#eff6ff;color:#1d4ed8;border:1px solid #bfdbfe;margin-right:4px;'>{levelPrefix}</span><strong>{h(eRow["employeeName"])}</strong> ({h(eRow["loginAccount"])})</td>
                <td><span class='badge badge-user'>{h(eRow["departmentName"])}</span></td>
                <td>{titleHtml}</td>
                <td style='font-size:12px;'>
                    <div style='color:#0f172a;font-weight:700;'>{parentMgrDisplay}</div>
                    <div style='color:#475569;margin-top:2px;'>📩 到期通知：部門主管[<strong>{h(deptMgrName)}</strong>] + 人資主管[<strong>{h(hrMgrName)}</strong>]</div>
                </td>
                <td>{h(eRow["roleDisplayName"])}</td>
                <td><strong>{eRow["assignedItemCount"]} 件</strong></td>
                <td><strong style='font-size:18px;color:#2563eb;'>{eRow["totalAllocatedStoryPoint"]} pt</strong></td>
                <td>{(empDed > 0 ? $"<span class='badge' style='background:#fee2e2;color:#b91c1c;border:1px solid #fca5a5;'>-{empDed:0.##} pt</span>" : "<span style='color:#94a3b8;'>0 pt</span>")}</td>
                <td>待處理: {eRow["todoAllocatedStoryPoint"]} ｜ 進行中: {eRow["inProgressAllocatedStoryPoint"]} ｜ 完成: {eRow["doneAllocatedStoryPoint"]}</td>
                <td style='font-size:12.5px;color:#334155;'>{h(eRow["workItemListSummary"])}</td>
            </tr>");
        }
        sb.Append("</tbody></table></div></div>"); // end Tab 5

        // =====================================================================
        // TAB 6: 員工列表、每個人的登入帳密與部門管理 (僅管理者可檢視與操作)
        // =====================================================================
        sb.Append($"<div id='pane-employees' class='tab-pane {(currentTab == "employees" ? "active" : "")}'>");
        if (isAdmin)
        {
            int maxEmpNum = empTable.Rows.Count;
            foreach (DataRow er in empTable.Rows)
            {
                string ec = er["employeeCode"]?.ToString()?.Trim() ?? "";
                if (ec.StartsWith("EMP", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(ec[3..], out int parsedNum) &&
                    parsedNum > maxEmpNum)
                {
                    maxEmpNum = parsedNum;
                }
            }
            string nextEmpCode = "EMP" + (maxEmpNum + 1).ToString("D3");
            sb.Append($@"
            <div class='sp-page-action-bar'>
                <div class='sp-action-title'>
                    <span>⚡ 組織、部門與員工帳號管理快捷指令列</span>
                    <span style='font-size:12px;font-weight:600;color:#93c5fd;'>（表格直接置頂呈現｜支援左鍵單擊選取、雙擊列彈出 Modal 編輯、右鍵快捷選單）</span>
                </div>
                <div class='sp-action-group'>
                    <button type='button' class='btn btn-primary' style='padding:6px 12px;font-size:12.5px;background:#2563eb;border-color:#60a5fa;' onclick=""openEmployeeEditModal('','{nextEmpCode}','','','user@company.com','資深工程師','1','participant','true','false','')"">➕ 新增員工帳號 (彈出 Modal)</button>
                    <button type='button' class='btn btn-success' style='padding:6px 12px;font-size:12.5px;' onclick=""openDepartmentEditModal('','','','','')"">🏢 新增/修改部門 (Modal)</button>
                    <button type='button' class='sp-drawer-toggle-btn' onclick=""switchTab('myAccount')"">🔑 修改我本人的帳密</button>
                    <button type='button' class='sp-drawer-toggle-btn' onclick=""toggleAdminDrawer('drawer-emp-tree', this)"">🌳 組織與部門主管樹狀階層檢視 ▾</button>
                    <button type='button' class='sp-drawer-toggle-btn' onclick=""toggleAdminDrawer('drawer-emp-import', this)"">📤 批次載入員工面板 ▾</button>
                    <button type='button' class='sp-drawer-toggle-btn' onclick=""toggleAdminDrawer('drawer-emp-full', this)"">🛠️ 完整新增員工/部門面板 ▾</button>
                </div>
            </div>

            <div id='drawer-emp-full' class='sp-collapsible-drawer collapsed'>
            <div class='grid-2'>
                <div class='card'>
                    <div class='card-title'>
                        <span id='empFormTitle'>➕ 新增 / 修改員工資料、部門主管註記與樹狀階層 (tbEmployee)</span>
                        <div style='display:flex;gap:6px;align-items:center;flex-wrap:wrap;'>
                            <button type='button' class='btn btn-primary' style='padding:4px 10px;font-size:12px;' onclick=""switchTab('myAccount')"">🔑 修改我本人的帳密</button>
                            <select id='empQuickEditSelector' onchange='onQuickSelectEmployeeToEdit(this.value)' style='padding:4px 8px;font-size:12px;width:auto;min-width:190px;border-radius:6px;border:1px solid #93c5fd;background:#eff6ff;color:#1e3a8a;font-weight:700;'>
                                <option value=''>⚡ 快速選擇既有帳號修改...</option>");
            foreach (DataRow er in empTable.Rows)
            {
                bool ea = Convert.ToBoolean(er["isActive"]);
                bool erIsMgr = er.Table.Columns.Contains("isDepartmentManager") && er["isDepartmentManager"] != DBNull.Value && Convert.ToBoolean(er["isDepartmentManager"]);
                string erMgrPk = er.Table.Columns.Contains("managerEmployeePk") && er["managerEmployeePk"] != DBNull.Value ? er["managerEmployeePk"].ToString()! : "";
                sb.Append($"<option value='{er["employeePk"]}' data-code='{h(er["employeeCode"])}' data-name='{h(er["employeeName"])}' data-acc='{h(er["loginAccount"])}' data-mail='{h(er["emailAddress"])}' data-title='{h(er["jobTitle"])}' data-dept='{er["departmentPk"]}' data-ismgr='{(erIsMgr ? "true" : "false")}' data-mgrpk='{erMgrPk}' data-role='{h(er["roleName"])}' data-active='{(ea ? "true" : "false")}'>{(erIsMgr ? "👑 " : "")}{h(er["loginAccount"])} - {h(er["employeeName"])} ({h(er["employeeCode"])})</option>");
            }
            sb.Append($@"
                            </select>
                            <button type='button' class='btn btn-outline' style='padding:4px 10px;font-size:12px;' onclick=""resetEmpForm('{nextEmpCode}')"">🔄 清空切換為新增模式</button>
                        </div>
                    </div>
                    <form method='post' action='/employee/save'>
                        <input type='hidden' name='employeePk' id='empPk' value='' />
                        <div class='form-row'>
                            <div>
                                <label data-tip='員工唯一識別編號（例如：EMP001 或 200701）' title='員工唯一識別編號（例如：EMP001 或 200701）'>員工編號 <span class='sp-tip-icon'>ⓘ</span></label>
                                <input type='text' name='employeeCode' id='empCode' value='{nextEmpCode}' required />
                            </div>
                            <div>
                                <label data-tip='員工真實姓名與英文別名' title='員工真實姓名與英文別名'>員工姓名 <span class='sp-tip-icon'>ⓘ</span></label>
                                <input type='text' name='employeeName' id='empName' placeholder='例如：林志豪 (Leo)' required />
                            </div>
                            <div>
                                <label data-tip='選擇該員工歸屬的組織部門（對應 storyPoint.tbDepartment）' title='選擇該員工歸屬的組織部門（對應 storyPoint.tbDepartment）'>所屬部門 <span class='sp-tip-icon'>ⓘ</span></label>
                                <select name='departmentPk' id='empDept'>");
            foreach (DataRow dRow in deptTable.Rows)
            {
                sb.Append($"<option value='{dRow["departmentPk"]}'>{h(dRow["departmentCode"])} - {h(dRow["departmentName"])}</option>");
            }
            sb.Append(@"
                                </select>
                            </div>
                        </div>
                        <div class='form-row'>
                            <div>
                                <label data-tip='員工登入系統使用的唯一帳號（loginAccount，例如：admin 或 leo.lin；輸入既有帳號將自動切換為修改該帳號模式）' title='員工登入系統使用的唯一帳號（loginAccount，例如：admin 或 leo.lin；輸入既有帳號將自動切換為修改該帳號模式）'>登入帳號 <span class='sp-tip-icon'>ⓘ</span></label>
                                <input type='text' name='loginAccount' id='empAcc' placeholder='例如：leo.lin' oninput='autoMatchExistingEmpByAcc(this.value)' required />
                            </div>
                            <div>
                                <label data-tip='設定或重設登入密碼：新增時未填預設 pass123；修改既有員工時留空代表不更改密碼' title='設定或重設登入密碼：新增時未填預設 pass123；修改既有員工時留空代表不更改密碼'>登入密碼 <span class='sp-tip-icon'>ⓘ</span></label>
                                <div style='position:relative;display:flex;align-items:center;'>
                                    <input type='password' name='plainPassword' id='empPwd' placeholder='修改時留空代表不改密碼' style='padding-right:64px;' />
                                    <button type='button' id='btnToggleEmpPwd' onclick='toggleEmpPasswordVisibility()' style='position:absolute;right:6px;background:#e2e8f0;border:none;border-radius:5px;padding:3px 7px;font-size:11.5px;font-weight:700;color:#334155;cursor:pointer;'>👁️ 顯示</button>
                                </div>
                            </div>
                            <div>
                                <label data-tip='設定系統角色權限：參與者（僅可投票與查看個人工項/分數）或管理者（可管理全站與主持定案）' title='設定系統角色權限：參與者（僅可投票與查看個人工項/分數）或管理者（可管理全站與主持定案）'>系統角色權限 <span class='sp-tip-icon'>ⓘ</span></label>
                                <select name='roleName' id='empRole'>
                                    <option value='participant'>🙋 參與者 (僅可投票、僅可看自己的工項與分數)</option>
                                    <option value='admin'>👑 管理者 (可修改工項指派、員工帳密與主持定案)</option>
                                </select>
                            </div>
                        </div>
                        <div class='form-row'>
                            <div>
                                <label data-tip='員工公司電子郵件信箱（當同仁工項到期扣點時，系統會透過樹狀遞迴階層寄信給該同仁的部門主管與人資主管信箱）' title='員工公司電子郵件信箱（當同仁工項到期扣點時，系統會透過樹狀遞迴階層寄信給該同仁的部門主管與人資主管信箱）'>電子郵件 <span class='sp-tip-icon'>ⓘ</span></label>
                                <input type='text' name='emailAddress' id='empMail' value='user@company.com' required />
                            </div>
                            <div>
                                <label data-tip='員工職級與職務名稱（若選擇「部門經理」相關職級，下方「是否為部門主管」將自動切換為「是」）' title='員工職級與職務名稱（若選擇「部門經理」相關職級，下方「是否為部門主管」將自動切換為「是」）'>職級 / 職稱 (含部門經理) <span class='sp-tip-icon'>ⓘ</span></label>
                                <div style='display:flex;gap:6px;'>
                                    <select id='empTitleQuickSelect' onchange=""if(this.value){ document.getElementById('empTitle').value=this.value; if(this.value.indexOf('部門經理')!==-1) document.getElementById('empIsDeptMgr').value='true'; }"" style='width:138px;flex-shrink:0;background:#eff6ff;border-color:#93c5fd;font-weight:700;color:#1e3a8a;padding:6px 8px;font-size:12.5px;'>
                                        <option value=''>⚡ 選擇職級...</option>
                                        <option value='部門經理'>👔 部門經理</option>
                                        <option value='董事長兼部門經理'>👑 董事長兼部門經理</option>
                                        <option value='部門經理 / 資深架構師'>👔 部門經理 / 資深架構師</option>
                                        <option value='部門經理 / 商業智慧資深顧問'>👔 部門經理 / BI 資深顧問</option>
                                        <option value='部門經理 / 資料庫資深顧問'>👔 部門經理 / DB 資深顧問</option>
                                        <option value='部門經理 / 人資'>👔 部門經理 / 人資 (HR)</option>
                                        <option value='專案經理 (PM)'>📋 專案經理 (PM)</option>
                                        <option value='資深架構師'>🏗️ 資深架構師</option>
                                        <option value='資深工程師'>💻 資深工程師</option>
                                        <option value='工程師'>👨‍💻 工程師</option>
                                        <option value='人資專員 (HR)'>🤝 人資專員 (HR)</option>
                                    </select>
                                    <input type='text' name='jobTitle' id='empTitle' list='empJobTitleDatalist' value='資深工程師' placeholder='請選擇或輸入職級（如：部門經理）' required />
                                    <datalist id='empJobTitleDatalist'>
                                        <option value='部門經理'></option>
                                        <option value='董事長兼部門經理'></option>
                                        <option value='部門經理 / 資深架構師'></option>
                                        <option value='部門經理 / 商業智慧資深顧問'></option>
                                        <option value='部門經理 / 資料庫資深顧問'></option>
                                        <option value='部門經理 / 人資'></option>
                                        <option value='專案經理 (PM)'></option>
                                        <option value='資深架構師'></option>
                                        <option value='資深工程師'></option>
                                        <option value='工程師'></option>
                                        <option value='人資專員 (HR)'></option>
                                    </datalist>
                                </div>
                            </div>
                            <div>
                                <label data-tip='設定帳號啟用或停用狀態：停用後該帳號將無法登入系統與參與盲投' title='設定帳號啟用或停用狀態：停用後該帳號將無法登入系統與參與盲投'>帳號狀態 <span class='sp-tip-icon'>ⓘ</span></label>
                                <select name='isActive' id='empActive'>
                                    <option value='true'>🟢 啟用中 (允許登入與參與投票)</option>
                                    <option value='false'>🚫 已停用 (禁止登入系統)</option>
                                </select>
                            </div>
                        </div>
                        <div class='form-row' style='background:#f8fafc;border:1px solid #bfdbfe;border-radius:8px;padding:10px 12px;'>
                            <div>
                                <label data-tip='記載該員工是否為部門主管（tbEmployee.isDepartmentManager）：同一個部門可設定多位主管；若所屬部門有多位主管，同仁工項到期時多位主管皆會收到通知信' title='記載該員工是否為部門主管（tbEmployee.isDepartmentManager）：同一個部門可設定多位主管；若所屬部門有多位主管，同仁工項到期時多位主管皆會收到通知信'>是否為部門主管 (支援同部門多位主管) <span class='sp-tip-icon'>ⓘ</span></label>
                                <select name='isDepartmentManager' id='empIsDeptMgr' onchange=""const mSel=document.getElementById('empManagerPk'); if(mSel) mSel.value='';"" style='font-weight:700;border-color:#2563eb;'>
                                    <option value='false'>👨‍💻 否 (一般部門同仁)</option>
                                    <option value='true'>👑 是 (部門主管 / 人資主管，同部門可多位主管)</option>
                                </select>
                            </div>
                            <div style='grid-column: span 2;'>
                                <label data-tip='記載該員工的上一層主管（tbEmployee.managerEmployeePk 自我參照外鍵）：畫面僅呈現上一層主管；若所屬部門有多位主管，工項到期時該部門多位主管與人資主管皆會收到通知信' title='記載該員工的上一層主管（tbEmployee.managerEmployeePk 自我參照外鍵）：畫面僅呈現上一層主管；若所屬部門有多位主管，工項到期時該部門多位主管與人資主管皆會收到通知信'>上一層主管 (樹狀階層) <span class='sp-tip-icon'>ⓘ</span></label>
                                <select name='managerEmployeePk' id='empManagerPk' style='font-weight:700;border-color:#2563eb;'>
                                    <option value=''>🌳 自動依所屬部門之部門主管掛載 (或頂層 Root 節點)</option>");
            foreach (DataRow mgrRow in empTable.Rows)
            {
                bool mIsMgr = mgrRow.Table.Columns.Contains("isDepartmentManager") && mgrRow["isDepartmentManager"] != DBNull.Value && Convert.ToBoolean(mgrRow["isDepartmentManager"]);
                int mLvl = mgrRow.Table.Columns.Contains("hierarchyLevel") && mgrRow["hierarchyLevel"] != DBNull.Value ? Convert.ToInt32(mgrRow["hierarchyLevel"]) : 1;
                string mPrefix = mIsMgr ? $"👑 [L{mLvl} 部門主管]" : $"👤 [L{mLvl}]";
                sb.Append($"<option value='{mgrRow["employeePk"]}'>{mPrefix} {h(mgrRow["employeeName"])} ({h(mgrRow["employeeCode"])} ｜ {h(mgrRow["departmentName"])} - {h(mgrRow["jobTitle"])})</option>");
            }
            sb.Append(@"
                                </select>
                            </div>
                        </div>
                        <div style='margin-top:8px;display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:8px;'>
                            <div style='font-size:12px;color:#1e3a8a;'>📩 <strong>到期通知信規則：</strong>同一個部門可有多位主管，若部門有多位主管則多位都通知（並通知人資主管），畫面僅呈現上一層主管。</div>
                            <button type='submit' class='btn btn-primary' id='empSubmitBtn' style='min-width:220px;'>💾 新增儲存員工與登入帳密</button>
                        </div>
                    </form>
                </div>

                <div class='card'>
                    <div class='card-title'><span>🏢 新增或修改部門資料 (tbDepartment)</span></div>
                    <form method='post' action='/employee/department/save'>
                        <div class='form-row'>
                            <div>
                                <label data-tip='部門唯一識別代碼（例如：DEPT_DATA_AI）' title='部門唯一識別代碼（例如：DEPT_DATA_AI）'>部門代碼 <span class='sp-tip-icon'>ⓘ</span></label>
                                <input type='text' name='departmentCode' placeholder='例如：DEPT_DATA_AI' required />
                            </div>
                            <div>
                                <label data-tip='部門完整顯示名稱（例如：數據科學與 AI 研發部）' title='部門完整顯示名稱（例如：數據科學與 AI 研發部）'>部門名稱 <span class='sp-tip-icon'>ⓘ</span></label>
                                <input type='text' name='departmentName' placeholder='例如：數據科學與 AI 研發部' required />
                            </div>
                            <div>
                                <label data-tip='該部門負責主管姓名' title='該部門負責主管姓名'>部門主管姓名 <span class='sp-tip-icon'>ⓘ</span></label>
                                <input type='text' name='managerName' placeholder='例如：吳經理' />
                            </div>
                        </div>
                        <div class='form-row'>
                            <div style='grid-column: span 2;'>
                                <label data-tip='描述該部門負責之系統模組或業務職掌範圍' title='描述該部門負責之系統模組或業務職掌範圍'>部門職掌備註 <span class='sp-tip-icon'>ⓘ</span></label>
                                <input type='text' name='departmentNote' placeholder='描述部門負責業務範圍' />
                            </div>
                            <div style='display:flex;align-items:end;'>
                                <button type='submit' class='btn btn-success' style='width:100%;'>➕ 儲存部門</button>
                            </div>
                        </div>
                    </form>
                </div>
            </div>
            </div>

            <!-- 批次載入員工資料與登入帳密面板 (提供 CSV、Excel 兩種匯入選項，以及 CSV、Excel、互動 HTML 三種匯出選項) -->
            <div id='drawer-emp-import' class='sp-collapsible-drawer collapsed'>
            <div class='card' style='border:1px solid #93c5fd;background:linear-gradient(180deg,#ffffff 0%,#f8fafc 100%);'>
                <div class='card-title'>
                    <span>📤 批次載入員工資料與登入帳密（匯入支援 CSV、Excel 兩種選項；匯出支援 CSV、Excel、互動 HTML 三種選項）</span>
                    <div style='display:flex;gap:8px;flex-wrap:wrap;'>
                        <button type='button' class='btn btn-outline' style='padding:5px 12px;font-size:12px;' onclick='fillSampleBatchEmployees()'>⚡ 一鍵帶入範例名單</button>
                        <a href='/employee/import/template?format=csv' class='btn btn-outline' style='padding:5px 12px;font-size:12px;'>📄 下載 CSV 範本</a>
                        <a href='/employee/import/template?format=excel' class='btn btn-outline' style='padding:5px 12px;font-size:12px;'>📊 下載 Excel (.xlsx) 範本</a>
                        <a href='/export/report?type=employee&format=csv' class='btn btn-outline' style='padding:5px 12px;font-size:12px;'>📥 匯出 CSV</a>
                        <a href='/export/report?type=employee&format=excel' class='btn btn-outline' style='padding:5px 12px;font-size:12px;'>📊 匯出 Excel</a>
                        <a href='/export/report?type=employee&format=html' class='btn btn-primary' style='padding:5px 12px;font-size:12px;'>🌐 匯出互動 HTML (排序・篩選・群組彙總)</a>
                    </div>
                </div>
                <form method='post' action='/employee/import' enctype='multipart/form-data'>
                    <div class='form-row'>
                        <div>
                            <label data-tip='選擇批次匯入員工的檔案格式選項：CSV (.csv) 或 Excel (.xlsx / .xls)' title='選擇批次匯入員工的檔案格式選項：CSV (.csv) 或 Excel (.xlsx / .xls)'>匯入檔案格式 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select name='importFormat' id='empImportFormatSelect' onchange=""const f=document.getElementById('empImportFileInput'); if(f) f.accept=(this.value==='excel'?'.xlsx,.xls,.xml':'.csv,.txt,.xlsx');"">
                                <option value='csv'>📄 選項 1：匯入 CSV 檔案 (.csv)</option>
                                <option value='excel'>📊 選項 2：匯入 Excel 檔案 (.xlsx / .xls)</option>
                            </select>
                        </div>
                        <div>
                            <label data-tip='選擇本機要上傳批次匯入的 CSV 或 Excel (.xlsx) 員工帳號檔案' title='選擇本機要上傳批次匯入的 CSV 或 Excel (.xlsx) 員工帳號檔案'>上傳員工匯入檔案 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='file' name='importFile' id='empImportFileInput' accept='.csv,.xlsx,.xls,.xml,.txt' style='padding:6px;background:#fff;border:1px solid #cbd5e1;border-radius:7px;width:100%;' />
                        </div>
                        <div>
                            <label data-tip='當匯入資料列未填寫部門名稱或代碼時，系統自動將該員工歸屬至此預設部門' title='當匯入資料列未填寫部門名稱或代碼時，系統自動將該員工歸屬至此預設部門'>預設歸屬部門 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select name='defaultDepartmentPk'>");
            foreach (DataRow dRow in deptTable.Rows)
            {
                sb.Append($"<option value='{dRow["departmentPk"]}'>{h(dRow["departmentCode"])} - {h(dRow["departmentName"])}</option>");
            }
            sb.Append(@"
                            </select>
                        </div>
                        <div>
                            <label data-tip='當匯入資料列未填寫登入密碼時，系統自動為新員工帳號設定此預設登入密碼' title='當匯入資料列未填寫登入密碼時，系統自動為新員工帳號設定此預設登入密碼'>預設登入密碼 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='text' name='defaultPassword' value='pass123' />
                        </div>
                    </div>
                    <div class='form-row'>
                        <div style='grid-column: 1 / -1;'>
                            <label data-tip='直接貼上多筆員工 CSV 或 Excel 複製內容，每行一筆，欄位順序：員工編號,員工姓名,登入帳號,登入密碼,電子郵件,職稱,所屬部門名稱或代碼,角色(participant/admin),是否啟用(1/0),是否為部門主管(1/0),上層主管(帳號或編號)' title='直接貼上多筆員工 CSV 或 Excel 複製內容，每行一筆，欄位順序：員工編號,員工姓名,登入帳號,登入密碼,電子郵件,職稱,所屬部門名稱或代碼,角色(participant/admin),是否啟用(1/0),是否為部門主管(1/0),上層主管(帳號或編號)'>直接貼上多筆員工 CSV / Excel 文字內容 <span class='sp-tip-icon'>ⓘ</span></label>
                            <textarea name='csvRawText' id='batchEmpTextarea' rows='5' placeholder='EMP009,林志豪 (Leo),leo.lin,pass123,leo.lin@company.com,部門經理,開發,admin,1,1,byron.hu&#10;EMP010,許雅筑 (Chloe),chloe.hsu,pass123,chloe.hsu@company.com,資深工程師,開發,participant,1,0,leo.lin' style='font-family:Consolas,monospace;font-size:13px;'></textarea>
                        </div>
                    </div>
                    <div style='display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:10px;'>
                        <div style='font-size:12.5px;color:#475569;'>💡 提示：若員工編號或登入帳號已存在，系統會自動更新其部門、職稱、是否為部門主管、上層主管樹狀階層、信箱與密碼。</div>
                        <button type='submit' class='btn btn-primary' style='min-width:240px;'>🚀 執行批次載入員工資料</button>
                    </div>
                </form>
            </div>
            </div>");

            // 建立視覺化「🌳 員工與部門主管樹狀遞迴階層檢視 (Recursive Tree Hierarchy & 通知信路由)」卡片
            var treeOrderedRows = empTable.Rows.Cast<DataRow>()
                .OrderBy(r => r.Table.Columns.Contains("hierarchySortKey") && r["hierarchySortKey"] != DBNull.Value ? r["hierarchySortKey"].ToString() : r["employeeCode"].ToString())
                .ToList();
            int deptMgrTotalCount = treeOrderedRows.Count(r => r.Table.Columns.Contains("isDepartmentManager") && r["isDepartmentManager"] != DBNull.Value && Convert.ToBoolean(r["isDepartmentManager"]));

            sb.Append($@"
            <div id='drawer-emp-tree' class='sp-collapsible-drawer collapsed'>
            <div class='card' style='border:2px solid #86efac;background:linear-gradient(180deg,#f0fdf4 0%,#ffffff 100%);'>
                <div class='card-title'>
                    <span>🌳 組織與部門主管樹狀遞迴階層檢視（支援同部門多位主管 ＆ 到期通知信路由）</span>
                    <div style='display:flex;gap:8px;align-items:center;flex-wrap:wrap;'>
                        <span class='badge badge-finalized'>👑 部門主管共 {deptMgrTotalCount} 位 (含人資主管)</span>
                        <span class='badge badge-revealed'>📩 通知規則：同仁工項達迄時 ➔ 若部門有多位主管，多位部門主管皆通知 + 人資主管</span>
                    </div>
                </div>
                <div style='font-size:13px;color:#334155;margin-bottom:12px;line-height:1.6;'>
                    系統於 <code>storyPoint.tbEmployee</code> 以 <strong><code>isDepartmentManager</code> (是否為部門主管，同一個部門可設定多位主管)</strong> 與 <strong><code>managerEmployeePk</code> (上一層主管自我參照外鍵)</strong> 建立樹狀遞迴階層。當任何同仁負責之工項達到迄時，<code>storyPoint.spProcessExpiredWorkItem</code> 會通知<strong>該同仁所屬部門之所有部門主管</strong>與<strong>人資主管</strong>（點擊任一節點可直接彈出單筆編輯 Modal）：
                </div>
                <div style='display:grid;grid-template-columns:repeat(auto-fit,minmax(270px,1fr));gap:12px;'>");

            // 依各部門主管分組顯示樹狀結構卡片
            var deptManagers = treeOrderedRows
                .Where(r => r.Table.Columns.Contains("isDepartmentManager") && r["isDepartmentManager"] != DBNull.Value && Convert.ToBoolean(r["isDepartmentManager"]))
                .ToList();

            foreach (DataRow mgrRow in deptManagers)
            {
                int mPk = Convert.ToInt32(mgrRow["employeePk"]);
                string mCode = mgrRow["employeeCode"]?.ToString() ?? "";
                string mName = mgrRow["employeeName"]?.ToString() ?? "";
                string mAcc = mgrRow["loginAccount"]?.ToString() ?? "";
                string mMail = mgrRow["emailAddress"]?.ToString() ?? "";
                string mTitle = mgrRow["jobTitle"]?.ToString() ?? "";
                int mDeptPk = Convert.ToInt32(mgrRow["departmentPk"]);
                string mDeptName = mgrRow["departmentName"]?.ToString() ?? "";
                string mDeptCode = mgrRow.Table.Columns.Contains("departmentCode") ? (mgrRow["departmentCode"]?.ToString() ?? "") : "";
                string mRole = mgrRow["roleName"]?.ToString() ?? "participant";
                bool mActive = Convert.ToBoolean(mgrRow["isActive"]);
                string mParentPk = mgrRow.Table.Columns.Contains("managerEmployeePk") && mgrRow["managerEmployeePk"] != DBNull.Value ? mgrRow["managerEmployeePk"].ToString()! : "";
                string mParentName = (mgrRow.Table.Columns.Contains("managerEmployeePk") && mgrRow["managerEmployeePk"] != DBNull.Value && mgrRow.Table.Columns.Contains("managerEmployeeName") && mgrRow["managerEmployeeName"] != DBNull.Value)
                    ? (mgrRow["managerEmployeeName"]?.ToString() ?? "")
                    : "";
                int mLvl = mgrRow.Table.Columns.Contains("hierarchyLevel") && mgrRow["hierarchyLevel"] != DBNull.Value ? Convert.ToInt32(mgrRow["hierarchyLevel"]) : 1;
                string allDeptMgrNames = mgrRow.Table.Columns.Contains("departmentManagerName") && mgrRow["departmentManagerName"] != DBNull.Value
                    ? (mgrRow["departmentManagerName"]?.ToString() ?? mName)
                    : mName;
                string allDeptMgrMails = mgrRow.Table.Columns.Contains("departmentManagerEmail") && mgrRow["departmentManagerEmail"] != DBNull.Value
                    ? (mgrRow["departmentManagerEmail"]?.ToString() ?? mMail)
                    : mMail;
                string hrMgrNameForDept = mgrRow.Table.Columns.Contains("hrManagerName") ? (mgrRow["hrManagerName"]?.ToString() ?? "許立慧") : "許立慧";
                string hrMgrMailForDept = mgrRow.Table.Columns.Contains("hrManagerEmail") ? (mgrRow["hrManagerEmail"]?.ToString() ?? "raya.hsu@mentortrust.com") : "raya.hsu@mentortrust.com";

                var directSubordinates = treeOrderedRows
                    .Where(r => r.Table.Columns.Contains("managerEmployeePk") && r["managerEmployeePk"] != DBNull.Value
                             && Convert.ToInt32(r["managerEmployeePk"]) == mPk
                             && (!(r.Table.Columns.Contains("isDepartmentManager") && r["isDepartmentManager"] != DBNull.Value && Convert.ToBoolean(r["isDepartmentManager"]))
                                 || Convert.ToInt32(r["departmentPk"]) == mDeptPk))
                    .ToList();

                bool isHrDept = string.Equals(mDeptCode, "HR", StringComparison.OrdinalIgnoreCase);
                string cardBorderColor = mLvl == 1 ? "#f59e0b" : (isHrDept ? "#ec4899" : "#3b82f6");
                string headerBg = mLvl == 1 ? "#fef3c7" : (isHrDept ? "#fce7f3" : "#eff6ff");
                string roleTag = mLvl == 1 ? "🌳 L1 頂層 Root / 部門主管" : (isHrDept ? $"🤝 L{mLvl} 人資主管 (HR)" : $"👑 L{mLvl} 部門主管");

                sb.Append($@"
                    <div style='background:#ffffff;border:1.5px solid {cardBorderColor};border-radius:10px;overflow:hidden;box-shadow:0 2px 5px rgba(15,23,42,0.04);'>
                        <div style='background:{headerBg};padding:10px 12px;border-bottom:1px solid #e2e8f0;cursor:pointer;' onclick=""loadEmpToForm({mPk},'{escapeJsString(mCode)}','{escapeJsString(mName)}','{escapeJsString(mAcc)}','{escapeJsString(mMail)}','{escapeJsString(mTitle)}',{mDeptPk},'{escapeJsString(mRole)}','{(mActive ? "true" : "false")}','true','{mParentPk}')"">
                            <div style='display:flex;justify-content:space-between;align-items:center;gap:6px;'>
                                <span style='font-size:11.5px;font-weight:800;color:#1e293b;'>{roleTag} ｜ {h(mDeptName)}</span>
                                <button type='button' class='btn btn-outline' style='padding:2px 7px;font-size:11px;' onclick=""event.stopPropagation(); openDepartmentEditModal('{mDeptPk}','{escapeJsString(mDeptCode)}','{escapeJsString(mDeptName)}','{escapeJsString(mName)}','');"">🏢 修改部門</button>
                            </div>
                            <div style='font-size:11px;color:#475569;margin-top:2px;'>{(string.IsNullOrEmpty(mParentName) || mParentName == "頂層根節點(無上層)" ? "上一層主管：無 (頂層)" : $"上一層主管：{h(mParentName)}")}</div>
                            <div style='font-size:15px;font-weight:800;color:#0f172a;margin-top:3px;'>👑 {h(mName)} <span style='font-size:12px;font-weight:600;color:#475569;'>({h(mCode)} / {h(mAcc)})</span></div>
                            <div style='font-size:11.5px;color:#334155;margin-top:2px;'>📧 {h(mMail)} ｜ {h(mTitle)}</div>
                        </div>
                        <div style='padding:10px 12px;'>
                            <div style='font-size:11.5px;color:#1e40af;background:#f8fafc;border:1px solid #dbeafe;border-radius:6px;padding:6px 8px;margin-bottom:8px;'>
                                📩 <strong>本部門同仁工項到期通知收件人（多位主管皆通知）：</strong><br/>
                                1️⃣ 部門主管：<strong>{h(allDeptMgrNames)}</strong> (<code>{h(allDeptMgrMails)}</code>)<br/>
                                2️⃣ 人資主管：<strong>{h(hrMgrNameForDept)}</strong> (<code>{h(hrMgrMailForDept)}</code>)
                            </div>
                            <div style='font-size:12px;font-weight:700;color:#475569;margin-bottom:6px;'>↳ 直屬下層同仁（共 {directSubordinates.Count} 人）：</div>
                            <div style='display:flex;flex-wrap:wrap;gap:5px;'>");
                foreach (DataRow subRow in directSubordinates)
                {
                    int sPk = Convert.ToInt32(subRow["employeePk"]);
                    string sCode = subRow["employeeCode"]?.ToString() ?? "";
                    string sName = subRow["employeeName"]?.ToString() ?? "";
                    string sAcc = subRow["loginAccount"]?.ToString() ?? "";
                    string sMail = subRow["emailAddress"]?.ToString() ?? "";
                    string sTitle = subRow["jobTitle"]?.ToString() ?? "";
                    int sDeptPk = Convert.ToInt32(subRow["departmentPk"]);
                    string sRole = subRow["roleName"]?.ToString() ?? "participant";
                    bool sActive = Convert.ToBoolean(subRow["isActive"]);
                    bool sIsMgr = subRow.Table.Columns.Contains("isDepartmentManager") && subRow["isDepartmentManager"] != DBNull.Value && Convert.ToBoolean(subRow["isDepartmentManager"]);
                    int sLvl = subRow.Table.Columns.Contains("hierarchyLevel") && subRow["hierarchyLevel"] != DBNull.Value ? Convert.ToInt32(subRow["hierarchyLevel"]) : (mLvl + 1);
                    string sIcon = sIsMgr ? "👑" : "↳";
                    sb.Append($@"<button type='button' onclick=""loadEmpToForm({sPk},'{escapeJsString(sCode)}','{escapeJsString(sName)}','{escapeJsString(sAcc)}','{escapeJsString(sMail)}','{escapeJsString(sTitle)}',{sDeptPk},'{escapeJsString(sRole)}','{(sActive ? "true" : "false")}','{(sIsMgr ? "true" : "false")}','{mPk}')"" style='border:1px solid {(sIsMgr ? "#f59e0b" : "#cbd5e1")};background:{(sIsMgr ? "#fef3c7" : "#f8fafc")};color:#1e293b;border-radius:6px;padding:3px 8px;font-size:11.5px;cursor:pointer;font-weight:600;' title='[L{sLvl}] {h(sName)} ({h(sCode)} - {h(sTitle)}) ｜ 上一層主管：{h(mName)} ｜ 點擊修改'>{sIcon} L{sLvl} {h(sName)} <span style='color:#64748b;font-size:10.5px;'>({h(sCode)})</span></button>");
                }
                sb.Append(@"
                            </div>
                        </div>
                    </div>");
            }

            sb.Append(@"
                </div>
            </div>
            </div>

            <div class='card'>
                <div class='card-title'>
                    <div style='display:flex;align-items:center;gap:8px;flex-wrap:wrap;'>
                        <span>👥 員工列表、部門主管註記、上一層主管、登入帳號與權限角色一覽表（支援增、刪、修、查與停用）</span>
                    </div>
                    <div style='display:flex;align-items:center;gap:8px;flex-wrap:wrap;'>
                        <span id='empFilterCountLabel' style='font-size:13px;color:#2563eb;font-weight:700;'>共 " + empTable.Rows.Count + @" 位員工</span>
                        <a href='/export/report?type=employee&format=csv' class='btn btn-outline' style='padding:5px 10px;font-size:12px;'>📥 匯出 CSV</a>
                        <a href='/export/report?type=employee&format=excel' class='btn btn-outline' style='padding:5px 10px;font-size:12px;'>📊 匯出 Excel</a>
                        <a href='/export/report?type=employee&format=html' class='btn btn-primary' style='padding:5px 10px;font-size:12px;'>🌐 匯出互動 HTML</a>
                    </div>
                </div>

                <!-- 員工帳號多維度查詢與篩選列 -->
                <div style='background:#f8fafc;border:1px solid #e2e8f0;border-radius:10px;padding:12px 14px;margin-bottom:14px;'>
                    <div style='display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:8px;margin-bottom:10px;'>
                        <div style='font-size:12.5px;color:#1e3a8a;font-weight:700;'>
                            💡 <strong>操作提示：</strong>於員工列表任一筆紀錄<strong>滑鼠雙擊 (Double-Click)</strong> 直接彈出單筆編輯 Modal；<strong>按滑鼠右鍵</strong>開啟快捷選單執行<strong>單筆編輯資料/密碼、啟用/停用、刪除、檢視指派工項</strong>（選取列後亦可按 <code>Enter</code> / <code>F2</code> 編輯）！
                        </div>
                        <button type='button' class='btn btn-outline' style='padding:4px 10px;font-size:12px;' onclick='resetEmployeeFilter()'>🔄 清除全部篩選條件</button>
                    </div>
                    <div class='form-row' style='margin-bottom:0;align-items:end;'>
                        <div>
                            <label data-tip='輸入員工編號、姓名、登入帳號、電子郵件、上層主管或職級關鍵字，即時篩選下方員工列表' title='輸入員工編號、姓名、登入帳號、電子郵件、上層主管或職級關鍵字，即時篩選下方員工列表'>🔍 關鍵字搜尋 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='text' id='filterEmpKeyword' oninput='filterEmployeeTable()' placeholder='輸入員工編號 / 姓名 / 帳號 / 上層主管 / 信箱...' />
                        </div>
                        <div>
                            <label data-tip='依員工所屬組織部門過濾員工列表' title='依員工所屬組織部門過濾員工列表'>🏢 所屬部門篩選 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select id='filterEmpDept' onchange='filterEmployeeTable()'>
                                <option value=''>全部部門</option>");
            foreach (DataRow dRow in deptTable.Rows)
            {
                sb.Append($"<option value='{h(dRow["departmentName"])}'>{h(dRow["departmentName"])}</option>");
            }
            sb.Append(@"
                            </select>
                        </div>
                        <div>
                            <label data-tip='依「是否為部門主管 (isDepartmentManager)」過濾員工列表' title='依「是否為部門主管 (isDepartmentManager)」過濾員工列表'>👑 部門主管篩選 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select id='filterEmpRank' onchange='filterEmployeeTable()'>
                                <option value=''>全部人員 (部門主管 + 一般同仁)</option>
                                <option value='部門經理'>👑 部門主管 (isDepartmentManager = 1，含人資主管)</option>
                                <option value='一般職級'>👨‍💻 一般部門同仁 (isDepartmentManager = 0)</option>
                            </select>
                        </div>
                        <div>
                            <label data-tip='依系統角色權限（管理者 Admin / 參與者 Participant）過濾員工列表' title='依系統角色權限（管理者 Admin / 參與者 Participant）過濾員工列表'>👑 系統角色篩選 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select id='filterEmpRole' onchange='filterEmployeeTable()'>
                                <option value=''>全部角色</option>
                                <option value='admin'>👑 管理者 (Admin)</option>
                                <option value='participant'>🙋 參與者 (Participant)</option>
                            </select>
                        </div>
                        <div>
                            <label data-tip='依帳號啟用中或已停用狀態過濾員工列表' title='依帳號啟用中或已停用狀態過濾員工列表'>⚡ 帳號狀態篩選 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select id='filterEmpStatus' onchange='filterEmployeeTable()'>
                                <option value=''>全部狀態 (啟用 + 停用)</option>
                                <option value='1'>🟢 啟用中</option>
                                <option value='0'>🚫 已停用</option>
                            </select>
                        </div>
                    </div>
                </div>

                <table id='empDataTable'>
                    <thead><tr><th>員工編號</th><th>員工姓名</th><th>登入帳號</th><th>所屬部門</th><th>是否為部門主管 / 職稱</th><th>上一層主管 / 到期通知對象</th><th>電子郵件</th><th>系統角色</th><th>帳號狀態</th><th>目前賦予 Story Point</th></tr></thead><tbody>");
            foreach (DataRow eRow in empTable.Rows)
            {
                int rowEmpPk = Convert.ToInt32(eRow["employeePk"]);
                bool empActive = Convert.ToBoolean(eRow["isActive"]);
                string empTitleStr = eRow["jobTitle"]?.ToString() ?? "";
                bool isDeptMgrRow = (eRow.Table.Columns.Contains("isDepartmentManager") && eRow["isDepartmentManager"] != DBNull.Value)
                    ? Convert.ToBoolean(eRow["isDepartmentManager"])
                    : empTitleStr.Contains("部門經理");
                string deptCodeStr = eRow.Table.Columns.Contains("departmentCode") ? (eRow["departmentCode"]?.ToString() ?? "") : "";
                bool isHrMgrRow = isDeptMgrRow && string.Equals(deptCodeStr, "HR", StringComparison.OrdinalIgnoreCase);
                string mgrPkStr = eRow.Table.Columns.Contains("managerEmployeePk") && eRow["managerEmployeePk"] != DBNull.Value
                    ? eRow["managerEmployeePk"].ToString()!
                    : "";
                string mgrNameStr = (!string.IsNullOrEmpty(mgrPkStr) && eRow.Table.Columns.Contains("managerEmployeeName") && eRow["managerEmployeeName"] != DBNull.Value)
                    ? (eRow["managerEmployeeName"]?.ToString() ?? "")
                    : "";
                string hPath = eRow.Table.Columns.Contains("hierarchyPath") && eRow["hierarchyPath"] != DBNull.Value
                    ? (eRow["hierarchyPath"]?.ToString() ?? "")
                    : (eRow["employeeName"]?.ToString() ?? "");
                string deptMgrName = eRow.Table.Columns.Contains("departmentManagerName") && eRow["departmentManagerName"] != DBNull.Value
                    ? (eRow["departmentManagerName"]?.ToString() ?? "")
                    : "";
                string hrMgrName = eRow.Table.Columns.Contains("hrManagerName") && eRow["hrManagerName"] != DBNull.Value
                    ? (eRow["hrManagerName"]?.ToString() ?? "")
                    : "";

                string mgrBadgeHtml = isHrMgrRow
                    ? "<span class='badge' style='background:#fce7f3;color:#9d174d;border:1px solid #f472b6;margin-right:4px;'>🤝 人資主管 (是)</span>"
                    : (isDeptMgrRow
                        ? "<span class='badge' style='background:#fef3c7;color:#92400e;border:1px solid #f59e0b;margin-right:4px;'>👑 部門主管 (是)</span>"
                        : "<span class='badge' style='background:#f1f5f9;color:#475569;border:1px solid #cbd5e1;margin-right:4px;'>否 (一般同仁)</span>");
                string titleCellHtml = $"{mgrBadgeHtml}<strong>{h(empTitleStr)}</strong>";

                string parentMgrCellText = (!string.IsNullOrWhiteSpace(mgrNameStr) && mgrNameStr != "頂層根節點(無上層)")
                    ? $"👤 上一層主管：<strong>{h(mgrNameStr)}</strong>"
                    : "🌳 上一層主管：<strong>無 (頂層根節點)</strong>";

                string treeHierarchyCellHtml = $@"
                    <div style='font-size:12.5px;color:#0f172a;'>
                        {parentMgrCellText}
                    </div>
                    <div style='font-size:11.5px;color:#475569;margin-top:3px;'>
                        📩 到期通知：部門主管[<strong>{h(deptMgrName)}</strong>] + 人資主管[<strong>{h(hrMgrName)}</strong>]
                    </div>";

                string rBadge = eRow["roleName"].ToString() == "admin"
                    ? "<span class='badge badge-admin'>👑 管理者</span>"
                    : "<span class='badge badge-user'>🙋 參與者</span>";
                string statusBadge = empActive
                    ? "<span class='badge badge-finalized'>🟢 啟用中</span>"
                    : "<span class='badge' style='background:#fee2e2;color:#b91c1c;border:1px solid #fca5a5;'>🚫 已停用</span>";
                string rowStyle = empActive ? "" : "opacity:0.68;background:#fef2f2;";
                string searchBlob = $"{eRow["employeeCode"]} {eRow["employeeName"]} {eRow["loginAccount"]} {eRow["emailAddress"]} {empTitleStr} {hPath} {deptMgrName} {hrMgrName}".ToLowerInvariant();
                bool isSelf = (rowEmpPk == curEmpPk);
                string empCodeStr = eRow["employeeCode"]?.ToString() ?? "";
                string empNameStr = eRow["employeeName"]?.ToString() ?? "";
                string empAccStr = eRow["loginAccount"]?.ToString() ?? "";
                string empMailStr = eRow["emailAddress"]?.ToString() ?? "";
                string empRoleStr = eRow["roleName"]?.ToString() ?? "participant";

                sb.Append($@"<tr class='emp-row sp-context-row'
                    style='{rowStyle}'
                    data-row-kind='employee'
                    data-search='{h(searchBlob)}'
                    data-dept='{h(eRow["departmentName"])}'
                    data-rank='{(isDeptMgrRow ? "部門經理" : "一般職級")}'
                    data-role='{h(empRoleStr)}'
                    data-active='{(empActive ? "1" : "0")}'
                    data-emp-pk='{rowEmpPk}'
                    data-emp-code='{h(empCodeStr)}'
                    data-emp-name='{h(empNameStr)}'
                    data-emp-acc='{h(empAccStr)}'
                    data-emp-mail='{h(empMailStr)}'
                    data-emp-title='{h(empTitleStr)}'
                    data-emp-dept-pk='{eRow["departmentPk"]}'
                    data-emp-role='{h(empRoleStr)}'
                    data-emp-active='{(empActive ? "true" : "false")}'
                    data-emp-ismgr='{(isDeptMgrRow ? "true" : "false")}'
                    data-emp-mgrpk='{h(mgrPkStr)}'
                    data-emp-self='{(isSelf ? "1" : "0")}'
                    title='💡 按滑鼠右鍵選擇此筆員工紀錄並開啟操作選單（彈出 Modal 單筆編輯員工、啟用/停用、刪除）'>
                    <td data-sort-value='{h(empCodeStr)}'>
                        <strong>{h(empCodeStr)}</strong>
                    </td>
                    <td><strong>{h(empNameStr)}</strong></td>
                    <td><code>{h(empAccStr)}</code></td>
                    <td>{h(eRow["departmentName"])}</td>
                    <td>{titleCellHtml}</td>
                    <td>{treeHierarchyCellHtml}</td>
                    <td>{h(empMailStr)}</td>
                    <td>{rBadge}</td>
                    <td>{statusBadge}</td>
                    <td data-sort-value='{eRow["totalAllocatedStoryPoint"]}'><strong style='color:#2563eb;'>{eRow["totalAllocatedStoryPoint"]} pt</strong> ({eRow["assignedItemCount"]} 件)</td>
                </tr>");
            }
            sb.Append("</tbody></table></div>");
        }
        else
        {
            sb.Append($@"
            <div class='card' style='max-width:860px;margin:0 auto 18px auto;border:2px solid #93c5fd;'>
                <div class='card-title'>
                    <span>🔑 修改我的個人登入帳號與密碼（每個人皆可修改自己的帳/密）</span>
                    <div style='display:flex;gap:8px;align-items:center;'>
                        <span class='badge badge-user'>👤 員工編號：{h(getCurrentEmployeeCode())}</span>
                        <a href='/logout' onclick='return performSystemLogout(event)' class='btn btn-danger' style='padding:4px 10px;font-size:12px;' data-tip='安全登出系統並返回員工登入頁面'>🚪 登出系統</a>
                    </div>
                </div>
                <form method='post' action='/account/updateSelf' onsubmit='return validateSelfCredentialForm(this)'>
                    <input type='hidden' name='returnTab' value='employees' />
                    <div class='form-row'>
                        <div>
                            <label data-tip='您目前的員工識別編號與真實姓名（唯讀顯示）' title='您目前的員工識別編號與真實姓名（唯讀顯示）'>員工姓名與編號 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='text' value='{h(getCurrentEmployeeName())} ({h(getCurrentEmployeeCode())})' readonly style='background:#f1f5f9;color:#475569;font-weight:700;' />
                        </div>
                        <div>
                            <label data-tip='您目前所屬的組織部門與系統角色權限（唯讀顯示）' title='您目前所屬的組織部門與系統角色權限（唯讀顯示）'>所屬部門與角色 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='text' value='{h(getCurrentDepartmentName())} ｜ 參與者 (Participant)' readonly style='background:#f1f5f9;color:#475569;font-weight:700;' />
                        </div>
                        <div>
                            <label data-tip='設定您登入系統使用的唯一帳號（loginAccount，不可與其他員工重複；可隨時自行修改）' title='設定您登入系統使用的唯一帳號（loginAccount，不可與其他員工重複；可隨時自行修改）'>我的登入帳號 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='text' name='newLoginAccount' value='{h(getCurrentLoginAccount())}' placeholder='請輸入新的登入帳號' required style='border-color:#2563eb;font-weight:700;' />
                        </div>
                    </div>
                    <div class='form-row'>
                        <div>
                            <label data-tip='輸入您要設定的新登入密碼（若僅修改登入帳號而不修改密碼，此欄可留空）' title='輸入您要設定的新登入密碼（若僅修改登入帳號而不修改密碼，此欄可留空）'>新登入密碼 <span class='sp-tip-icon'>ⓘ</span></label>
                            <div style='position:relative;display:flex;align-items:center;'>
                                <input type='password' name='newPlainPassword' id='empSelfNewPwd1' placeholder='輸入新密碼（留空代表不改密碼）' style='padding-right:64px;' />
                                <button type='button' id='btnToggleEmpSelfNewPwd1' onclick=""togglePasswordVisibilityById('empSelfNewPwd1','btnToggleEmpSelfNewPwd1')"" style='position:absolute;right:6px;background:#e2e8f0;border:none;border-radius:5px;padding:3px 7px;font-size:11.5px;font-weight:700;color:#334155;cursor:pointer;'>👁️ 顯示</button>
                            </div>
                        </div>
                        <div>
                            <label data-tip='請再次輸入相同的新登入密碼以確認無誤（若上方新密碼留空，此欄亦留空）' title='請再次輸入相同的新登入密碼以確認無誤（若上方新密碼留空，此欄亦留空）'>確認新登入密碼 <span class='sp-tip-icon'>ⓘ</span></label>
                            <div style='position:relative;display:flex;align-items:center;'>
                                <input type='password' name='confirmPlainPassword' id='empSelfConfirmPwd1' placeholder='再次輸入新密碼確認' style='padding-right:64px;' />
                                <button type='button' id='btnToggleEmpSelfConfirmPwd1' onclick=""togglePasswordVisibilityById('empSelfConfirmPwd1','btnToggleEmpSelfConfirmPwd1')"" style='position:absolute;right:6px;background:#e2e8f0;border:none;border-radius:5px;padding:3px 7px;font-size:11.5px;font-weight:700;color:#334155;cursor:pointer;'>👁️ 顯示</button>
                            </div>
                        </div>
                        <div>
                            <label data-tip='選填：若輸入目前舊密碼，系統將一併驗證舊密碼正確性；留空亦可直接更新本人帳密' title='選填：若輸入目前舊密碼，系統將一併驗證舊密碼正確性；留空亦可直接更新本人帳密'>目前舊密碼 (選填驗證) <span class='sp-tip-icon'>ⓘ</span></label>
                            <div style='position:relative;display:flex;align-items:center;'>
                                <input type='password' name='currentPlainPassword' id='empSelfCurPwd1' placeholder='選填：驗證目前舊密碼' style='padding-right:64px;' />
                                <button type='button' id='btnToggleEmpSelfCurPwd1' onclick=""togglePasswordVisibilityById('empSelfCurPwd1','btnToggleEmpSelfCurPwd1')"" style='position:absolute;right:6px;background:#e2e8f0;border:none;border-radius:5px;padding:3px 7px;font-size:11.5px;font-weight:700;color:#334155;cursor:pointer;'>👁️ 顯示</button>
                            </div>
                        </div>
                    </div>
                    <div style='display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:10px;margin-top:8px;'>
                        <div style='font-size:12.5px;color:#475569;'>💡 提示：修改後系統將立即更新資料庫（<code>storyPoint.spUpdateEmployeeSelfCredential</code>）並自動刷新您的登入憑證，無須重新登入即可繼續操作。</div>
                        <button type='submit' class='btn btn-primary' style='min-width:240px;'>💾 儲存修改我的登入帳號與密碼</button>
                    </div>
                </form>
            </div>
            <div class='card' style='max-width:860px;margin:0 auto;'>
                <div class='card-title'><span>🔒 權限隔離保護提示</span></div>
                <p style='font-size:14px;color:#334155;margin:0;'>每個人都可以隨時修改<strong>自己的登入帳號與密碼</strong>；同時基於資料隱私保護，個別員工（參與者角色）僅可查看自己的工項與分數，不可查看或修改其他員工的帳密設定。</p>
            </div>");
        }
        sb.Append("</div>"); // end Tab 6

        // =========================================================================
        // Tab 6B: 修改我的個人登入帳號與密碼專頁 (每個人皆可修改自己的帳/密)
        // =========================================================================
        sb.Append($@"
        <div id='pane-myAccount' class='tab-pane {(currentTab == "myAccount" ? "active" : "")}'>
            <div class='card' style='max-width:880px;margin:0 auto 18px auto;border:2px solid #2563eb;box-shadow:0 8px 24px rgba(37,99,235,0.08);'>
                <div class='card-title'>
                    <span>🔑 修改我的個人登入帳號與密碼（每個人皆可修改自己的帳/密）</span>
                    <div style='display:flex;gap:8px;align-items:center;'>
                        <span class='badge {(isAdmin ? "badge-admin" : "badge-user")}'>{(isAdmin ? "👑 管理者 (Admin)" : "🙋 參與者 (Participant)")}</span>
                        <span class='badge badge-user'>員工編號：{h(getCurrentEmployeeCode())}</span>
                        <a href='/logout' onclick='return performSystemLogout(event)' class='btn btn-danger' style='padding:4px 10px;font-size:12px;' data-tip='安全登出系統並返回員工登入頁面'>🚪 登出系統</a>
                    </div>
                </div>
                <div style='background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;padding:10px 14px;margin-bottom:16px;font-size:13px;color:#1e3a8a;'>
                    🔒 <strong>個人帳密自主管理：</strong>不論您是「管理者」或「個別員工（參與者）」，每個人都可以隨時在此修改自己的<strong>登入帳號 (loginAccount)</strong> 與<strong>登入密碼</strong>。儲存後系統將自動更新您的登入工作階段並記錄稽核 Log。
                </div>
                <form method='post' action='/account/updateSelf' onsubmit='return validateSelfCredentialForm(this)'>
                    <input type='hidden' name='returnTab' value='myAccount' />
                    <div class='form-row'>
                        <div>
                            <label data-tip='您目前的員工識別編號與真實姓名（唯讀顯示）' title='您目前的員工識別編號與真實姓名（唯讀顯示）'>員工姓名與編號 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='text' value='{h(getCurrentEmployeeName())} ({h(getCurrentEmployeeCode())})' readonly style='background:#f1f5f9;color:#475569;font-weight:700;' />
                        </div>
                        <div>
                            <label data-tip='您目前所屬的組織部門與職稱（唯讀顯示）' title='您目前所屬的組織部門與職稱（唯讀顯示）'>所屬部門與職稱 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='text' value='{h(getCurrentDepartmentName())} ｜ {h(User.FindFirstValue("jobTitle") ?? "工程師")}' readonly style='background:#f1f5f9;color:#475569;font-weight:700;' />
                        </div>
                        <div>
                            <label data-tip='設定您登入系統使用的唯一帳號（loginAccount，不可與其他員工重複；可隨時自行修改）' title='設定您登入系統使用的唯一帳號（loginAccount，不可與其他員工重複；可隨時自行修改）'>我的登入帳號 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='text' name='newLoginAccount' id='myAccLoginInput' value='{h(getCurrentLoginAccount())}' placeholder='請輸入您的登入帳號' required style='border-color:#2563eb;font-weight:700;background:#fff;' />
                        </div>
                    </div>
                    <div class='form-row'>
                        <div>
                            <label data-tip='輸入您要設定的新登入密碼（若僅修改登入帳號而不修改密碼，此欄可留空）' title='輸入您要設定的新登入密碼（若僅修改登入帳號而不修改密碼，此欄可留空）'>新登入密碼 <span class='sp-tip-icon'>ⓘ</span></label>
                            <div style='position:relative;display:flex;align-items:center;'>
                                <input type='password' name='newPlainPassword' id='myAccNewPwd' placeholder='輸入新密碼（留空代表不改密碼）' style='padding-right:64px;' />
                                <button type='button' id='btnToggleMyAccNewPwd' onclick=""togglePasswordVisibilityById('myAccNewPwd','btnToggleMyAccNewPwd')"" style='position:absolute;right:6px;background:#e2e8f0;border:none;border-radius:5px;padding:3px 7px;font-size:11.5px;font-weight:700;color:#334155;cursor:pointer;'>👁️ 顯示</button>
                            </div>
                        </div>
                        <div>
                            <label data-tip='請再次輸入相同的新登入密碼以確認無誤（若上方新密碼留空，此欄亦留空）' title='請再次輸入相同的新登入密碼以確認無誤（若上方新密碼留空，此欄亦留空）'>確認新登入密碼 <span class='sp-tip-icon'>ⓘ</span></label>
                            <div style='position:relative;display:flex;align-items:center;'>
                                <input type='password' name='confirmPlainPassword' id='myAccConfirmPwd' placeholder='再次輸入新密碼確認' style='padding-right:64px;' />
                                <button type='button' id='btnToggleMyAccConfirmPwd' onclick=""togglePasswordVisibilityById('myAccConfirmPwd','btnToggleMyAccConfirmPwd')"" style='position:absolute;right:6px;background:#e2e8f0;border:none;border-radius:5px;padding:3px 7px;font-size:11.5px;font-weight:700;color:#334155;cursor:pointer;'>👁️ 顯示</button>
                            </div>
                        </div>
                        <div>
                            <label data-tip='選填：若輸入目前舊密碼，系統將一併驗證舊密碼正確性；留空亦可直接更新本人帳密' title='選填：若輸入目前舊密碼，系統將一併驗證舊密碼正確性；留空亦可直接更新本人帳密'>目前舊密碼 (選填驗證) <span class='sp-tip-icon'>ⓘ</span></label>
                            <div style='position:relative;display:flex;align-items:center;'>
                                <input type='password' name='currentPlainPassword' id='myAccCurPwd' placeholder='選填：驗證目前舊密碼' style='padding-right:64px;' />
                                <button type='button' id='btnToggleMyAccCurPwd' onclick=""togglePasswordVisibilityById('myAccCurPwd','btnToggleMyAccCurPwd')"" style='position:absolute;right:6px;background:#e2e8f0;border:none;border-radius:5px;padding:3px 7px;font-size:11.5px;font-weight:700;color:#334155;cursor:pointer;'>👁️ 顯示</button>
                            </div>
                        </div>
                    </div>
                    <div style='display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:10px;margin-top:10px;'>
                        <div style='font-size:12.5px;color:#475569;'>💡 提示：可同時修改「登入帳號」與「登入密碼」，或只修改其中一項。</div>
                        <div style='display:flex;gap:10px;'>
                            {(isAdmin ? @"<button type='button' class='btn btn-outline' onclick=""switchTab('employees')"">👥 前往全體員工帳密管理</button>" : "")}
                            <button type='submit' class='btn btn-primary' style='min-width:240px;'>💾 儲存修改我的登入帳號與密碼</button>
                        </div>
                    </div>
                </form>
            </div>
        </div>");

        // =========================================================================
        // Tab 7: 系統與錯誤 Log 查詢中心 (tbLog / 本地雙軌備援，僅管理者可見)
        // =========================================================================
        sb.Append($"<div id='pane-logs' class='tab-pane {(currentTab == "logs" ? "active" : "")}'>");
        if (isAdmin)
        {
            int totalLogs = logTable.Rows.Count;
            int level1ErrorCount = 0;
            int level2WarnCount = 0;
            int level3InfoCount = 0;
            long totalDurationMs = 0;
            var sourceSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (DataRow lRow in logTable.Rows)
            {
                int lvl = lRow["logLevel"] != DBNull.Value ? Convert.ToInt32(lRow["logLevel"]) : 3;
                if (lvl == 1) level1ErrorCount++;
                else if (lvl == 2) level2WarnCount++;
                else level3InfoCount++;

                if (lRow["durationMillisecond"] != DBNull.Value)
                {
                    totalDurationMs += Convert.ToInt64(lRow["durationMillisecond"]);
                }

                string src = lRow["sourceName"]?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(src)) sourceSet.Add(src);
            }
            long avgDurationMs = totalLogs > 0 ? totalDurationMs / totalLogs : 0;

            sb.Append($@"
            <div class='kpi-grid' style='margin-bottom:16px;'>
                <div class='kpi-card' style='border-left:4px solid #2563eb;'>
                    <div class='kpi-label'>📜 系統與資料庫 Log 總筆數 (近期)</div>
                    <div class='kpi-val'>{totalLogs} <span style='font-size:14px;font-weight:600;'>筆</span></div>
                    <div class='kpi-sub'>自動整合 SQL Server tbLog 與本地備援 CSV</div>
                </div>
                <div class='kpi-card' style='border-left:4px solid #dc2626;'>
                    <div class='kpi-label'>❌ 錯誤異常筆數 (Level 1 - Error)</div>
                    <div class='kpi-val' style='color:#dc2626;'>{level1ErrorCount} <span style='font-size:14px;font-weight:600;'>筆</span></div>
                    <div class='kpi-sub'>所有異常皆已安全攔截，系統保持 100% 不當機</div>
                </div>
                <div class='kpi-card' style='border-left:4px solid #d97706;'>
                    <div class='kpi-label'>⚠️ 警告與提示筆數 (Level 2 / 3)</div>
                    <div class='kpi-val' style='color:#0f172a;'>{level2WarnCount} / {level3InfoCount} <span style='font-size:14px;font-weight:600;'>筆</span></div>
                    <div class='kpi-sub'>警告 {level2WarnCount} 筆 ｜ 一般資訊 {level3InfoCount} 筆</div>
                </div>
                <div class='kpi-card' style='border-left:4px solid #16a34a;'>
                    <div class='kpi-label'>⏱️ 平均處理耗時 (Duration)</div>
                    <div class='kpi-val' style='color:#15803d;'>{avgDurationMs} <span style='font-size:14px;font-weight:600;'>ms</span></div>
                    <div class='kpi-sub'>含 Stored Procedure 與 Web Action 執行時間</div>
                </div>
            </div>

            <div class='card' style='border:2px solid #bfdbfe;background:#f8fafc;'>
                <div class='card-title'>
                    <span>🛡️ 系統防崩潰保護機制說明與管理者一鍵錯誤攔截測試</span>
                    <span class='badge badge-finalized'>三層防護：SQL TRY-CATCH + Controller 攔截 + 全域 Middleware 防崩潰</span>
                </div>
                <p style='font-size:13px;color:#334155;margin-top:0;line-height:1.6;'>
                    本系統已啟用<strong>全站防崩潰與雙軌 Log 記錄機制</strong>：不論發生 T-SQL Stored Procedure 錯誤、資料格式異常、或未預期的執行期例外，系統<strong>絕不會當掉或出現空白 HTTP 500 錯誤畫面</strong>，並會自動將「發生時間、來源模組、執行物件/端點、錯誤碼、行號、詳細錯誤訊息與操作人員」同步寫入 SQL Server <code>storyPoint.tbLog</code> 與本地備援 <code>logs/webPortal_yyyyMMdd.csv</code>。
                </p>
                <form method='post' action='/admin/log/testError' style='display:flex;gap:10px;flex-wrap:wrap;align-items:flex-end;background:#fff;padding:12px 14px;border-radius:10px;border:1px solid #cbd5e1;'>
                    <div style='min-width:260px;flex:1;'>
                        <label data-tip='選擇要模擬觸發的防崩潰與 Log 記錄測試情境（Controller 例外、SQL Stored Procedure 參數異常、或全域 Middleware 例外）' title='選擇要模擬觸發的防崩潰與 Log 記錄測試情境（Controller 例外、SQL Stored Procedure 參數異常、或全域 Middleware 例外）'>🧪 測試錯誤情境 <span class='sp-tip-icon'>ⓘ</span></label>
                        <select name='testScenario'>
                            <option value='controllerException'>情境 A：模擬 Controller 執行期例外（測試自動攔截並寫入 Error Log）</option>
                            <option value='sqlValidationError'>情境 B：模擬 SQL Stored Procedure 參數異常（測試 SQL 錯誤碼與行號記錄）</option>
                            <option value='middlewareException'>情境 C：模擬全域未預期嚴重例外（測試 Global Middleware 防崩潰保護）</option>
                        </select>
                    </div>
                    <div style='min-width:240px;flex:1;'>
                        <label data-tip='輸入本次手動觸發錯誤測試的備註說明，將一併寫入 storyPoint.tbLog 供查核驗證' title='輸入本次手動觸發錯誤測試的備註說明，將一併寫入 storyPoint.tbLog 供查核驗證'>📝 測試備註說明 <span class='sp-tip-icon'>ⓘ</span></label>
                        <input type='text' name='customNote' value='管理者手動驗證系統防崩潰與錯誤 Log 查詢功能' />
                    </div>
                    <div>
                        <button type='submit' class='btn btn-danger'>⚡ 立即觸發測試錯誤 (驗證不當機並寫入 Log)</button>
                    </div>
                </form>
            </div>

            <div class='card'>
                <div class='card-title'>
                    <span>📜 系統執行與錯誤異常 Log 查詢清單 (storyPoint.tbLog)</span>
                    <div style='display:flex;gap:8px;flex-wrap:wrap;align-items:center;'>
                        <span id='sysLogFilterCountLabel' class='badge badge-revealed'>顯示全部 {totalLogs} 筆 Log</span>
                        <a href='/?tab=logs' class='btn btn-primary' style='padding:5px 11px;font-size:12px;'>🔄 重新整理最新 Log</a>
                        <a href='/export/report?type=log&format=csv' class='btn btn-outline' style='padding:5px 10px;font-size:12px;'>📥 匯出 CSV</a>
                        <a href='/export/report?type=log&format=excel' class='btn btn-outline' style='padding:5px 10px;font-size:12px;border-color:#86efac;color:#166534;background:#f0fdf4;'>📊 匯出 Excel (.xlsx)</a>
                        <a href='/export/report?type=log&format=html' class='btn btn-outline' style='padding:5px 10px;font-size:12px;border-color:#93c5fd;color:#1e40af;background:#eff6ff;'>🌐 匯出互動 HTML (排序・篩選・群組彙總)</a>
                    </div>
                </div>

                <!-- 即時多條件篩選工具列 -->
                <div style='background:#f8fafc;border:1px solid #cbd5e1;border-radius:10px;padding:12px 14px;margin-bottom:14px;'>
                    <div class='form-row' style='margin-bottom:0;'>
                        <div>
                            <label data-tip='輸入執行物件名稱、端點路徑、錯誤訊息、錯誤碼或操作帳號關鍵字，即時過濾下方 Log 列表' title='輸入執行物件名稱、端點路徑、錯誤訊息、錯誤碼或操作帳號關鍵字，即時過濾下方 Log 列表'>🔍 關鍵字搜尋 <span class='sp-tip-icon'>ⓘ</span></label>
                            <input type='text' id='filterLogKeyword' placeholder='輸入物件名稱 / 錯誤訊息 / 錯誤碼 / 帳號...' oninput='filterSystemLogTable()' />
                        </div>
                        <div>
                            <label data-tip='依 Log 嚴重層級（1-錯誤、2-警告、3-資訊、4-除錯）即時篩選清單' title='依 Log 嚴重層級（1-錯誤、2-警告、3-資訊、4-除錯）即時篩選清單'>🚨 Log 嚴重層級 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select id='filterLogLevel' onchange='filterSystemLogTable()'>
                                <option value=''>全部層級 (錯誤 + 警告 + 資訊)</option>
                                <option value='1'>❌ 1 - 錯誤 (Error - 僅看異常紀錄)</option>
                                <option value='2'>⚠️ 2 - 警告 (Warning)</option>
                                <option value='3'>ℹ️ 3 - 資訊 (Info - 正常執行紀錄)</option>
                                <option value='4'>🐛 4 - 除錯 (Debug)</option>
                            </select>
                        </div>
                        <div>
                            <label data-tip='依 Log 記錄的來源模組（sourceName，如 webPortal、sqlServerSP 等）篩選清單' title='依 Log 記錄的來源模組（sourceName，如 webPortal、sqlServerSP 等）篩選清單'>📦 來源模組篩選 <span class='sp-tip-icon'>ⓘ</span></label>
                            <select id='filterLogSource' onchange='filterSystemLogTable()'>
                                <option value=''>全部來源模組</option>");
            foreach (string srcName in sourceSet.OrderBy(s => s))
            {
                sb.Append($"<option value='{h(srcName)}'>{h(srcName)}</option>");
            }
            sb.Append(@"
                            </select>
                        </div>
                        <div style='display:flex;align-items:flex-end;gap:8px;'>
                            <button type='button' class='btn btn-danger' style='padding:8px 12px;font-size:12.5px;' onclick=""document.getElementById('filterLogLevel').value='1';filterSystemLogTable();"">❌ 只看錯誤異常</button>
                            <button type='button' class='btn btn-outline' style='padding:8px 12px;font-size:12.5px;' onclick=""document.getElementById('filterLogKeyword').value='';document.getElementById('filterLogLevel').value='';document.getElementById('filterLogSource').value='';filterSystemLogTable();"">🔄 重設篩選</button>
                        </div>
                    </div>
                </div>

                <table id='sysLogDataTable'>
                    <thead>
                        <tr>
                            <th style='white-space:nowrap;'>Log 編號</th>
                            <th style='white-space:nowrap;'>發生時間</th>
                            <th style='white-space:nowrap;'>嚴重層級</th>
                            <th style='white-space:nowrap;'>來源模組</th>
                            <th style='white-space:nowrap;'>執行物件 / 端點</th>
                            <th style='white-space:nowrap;'>耗時</th>
                            <th style='white-space:nowrap;'>執行結果</th>
                            <th style='white-space:nowrap;'>錯誤碼 / 行號</th>
                            <th style='min-width:240px;'>錯誤訊息與異常細節</th>
                            <th style='white-space:nowrap;'>操作帳號</th>
                        </tr>
                    </thead>
                    <tbody>");

            foreach (DataRow lRow in logTable.Rows)
            {
                int lvl = lRow["logLevel"] != DBNull.Value ? Convert.ToInt32(lRow["logLevel"]) : 3;
                string timeStr = lRow["logTime"] != DBNull.Value
                    ? Convert.ToDateTime(lRow["logTime"]).ToString("yyyy-MM-dd HH:mm:ss")
                    : "";
                string srcName = lRow["sourceName"]?.ToString() ?? "";
                string objName = lRow["objectName"]?.ToString() ?? "";
                if (objName.StartsWith("portalController.portalController.", StringComparison.OrdinalIgnoreCase))
                {
                    objName = "portalController." + objName["portalController.portalController.".Length..];
                }
                string durStr = lRow["durationMillisecond"]?.ToString() ?? "0";
                string execRes = lRow["executionResult"]?.ToString() ?? "";
                string errCode = lRow["errorCode"]?.ToString() ?? "";
                string errLine = lRow["errorLine"]?.ToString() ?? "";
                string errMsg = lRow["errorMessage"]?.ToString() ?? "";
                string opAcc = lRow["operatorAccount"]?.ToString() ?? "";

                string levelBadge = lvl switch
                {
                    1 => "<span class='badge' style='background:#fee2e2;color:#b91c1c;border:1px solid #fca5a5;'>❌ 錯誤 (Error)</span>",
                    2 => "<span class='badge' style='background:#fef3c7;color:#b45309;border:1px solid #fde68a;'>⚠️ 警告 (Warn)</span>",
                    4 => "<span class='badge' style='background:#f1f5f9;color:#475569;border:1px solid #cbd5e1;'>🐛 除錯 (Debug)</span>",
                    _ => "<span class='badge badge-finalized'>ℹ️ 資訊 (Info)</span>"
                };

                string trLevelClass = lvl == 1
                    ? "syslog-row-err"
                    : (lvl == 2 ? "syslog-row-warn" : "");

                string codeLineDisplay = (!string.IsNullOrWhiteSpace(errCode) || !string.IsNullOrWhiteSpace(errLine))
                    ? $"<code style='color:#b91c1c;font-weight:700;'>Code:{h(string.IsNullOrWhiteSpace(errCode) ? "-" : errCode)} / Line:{h(string.IsNullOrWhiteSpace(errLine) ? "-" : errLine)}</code>"
                    : "<span style='color:#94a3b8;'>-</span>";

                string errMsgDisplay;
                if (string.IsNullOrWhiteSpace(errMsg))
                {
                    errMsgDisplay = "<span style='color:#64748b;'>正常無異常</span>";
                }
                else
                {
                    int stackIdx = errMsg.IndexOf("| Stack:", StringComparison.OrdinalIgnoreCase);
                    if (stackIdx > 0)
                    {
                        string summaryPart = errMsg[..stackIdx].Trim();
                        string stackPart = errMsg[(stackIdx + "| Stack:".Length)..].Trim();
                        errMsgDisplay = $@"<div style='color:#b91c1c;font-weight:700;word-break:break-word;line-height:1.4;'>{h(summaryPart)}</div>
                            <details style='margin-top:4px;'>
                                <summary style='cursor:pointer;font-size:11.5px;color:#1d4ed8;font-weight:700;user-select:none;'>🔍 點擊展開 StackTrace 錯誤堆疊明細</summary>
                                <pre style='margin:4px 0 0 0;padding:8px;background:#0f172a;color:#f8fafc;border-radius:6px;font-size:11px;max-height:180px;overflow:auto;white-space:pre-wrap;word-break:break-all;'>{h(stackPart)}</pre>
                            </details>";
                    }
                    else if (errMsg.Length > 160)
                    {
                        string shortPreview = errMsg[..150].Trim() + "...";
                        errMsgDisplay = $@"<div style='color:#b91c1c;font-weight:700;word-break:break-word;line-height:1.4;'>{h(shortPreview)}</div>
                            <details style='margin-top:4px;'>
                                <summary style='cursor:pointer;font-size:11.5px;color:#1d4ed8;font-weight:700;user-select:none;'>🔍 點擊展開完整錯誤訊息</summary>
                                <div style='margin-top:4px;padding:8px;background:#fff;border:1px solid #fca5a5;border-radius:6px;font-size:12px;color:#991b1b;word-break:break-word;'>{h(errMsg)}</div>
                            </details>";
                    }
                    else
                    {
                        errMsgDisplay = $"<div style='color:#b91c1c;font-weight:600;word-break:break-word;line-height:1.45;'>{h(errMsg)}</div>";
                    }
                }

                string searchBlob = $"{lRow["logPk"]} {timeStr} {srcName} {objName} {execRes} {errCode} {errLine} {errMsg} {opAcc}".ToLowerInvariant();

                sb.Append($@"<tr class='syslog-row sp-context-row {trLevelClass}' data-row-kind='readonlyRecord' data-row-title='系統 Log #{h(lRow["logPk"])} ({h(objName)})' data-level='{lvl}' data-source='{h(srcName)}' data-search='{h(searchBlob)}' title='💡 按滑鼠右鍵選擇此筆 Log 紀錄並開啟操作選單'>
                    <td style='white-space:nowrap;'><code>#{h(lRow["logPk"])}</code></td>
                    <td style='white-space:nowrap;font-size:12.5px;'>{h(timeStr)}</td>
                    <td style='white-space:nowrap;'>{levelBadge}</td>
                    <td style='white-space:nowrap;'><strong>{h(srcName)}</strong></td>
                    <td style='max-width:210px;word-break:break-all;'><code>{h(objName)}</code></td>
                    <td style='white-space:nowrap;'>{h(durStr)} ms</td>
                    <td style='max-width:220px;word-break:break-word;'>{h(execRes)}</td>
                    <td style='white-space:nowrap;'>{codeLineDisplay}</td>
                    <td style='max-width:360px;word-break:break-word;'>{errMsgDisplay}</td>
                    <td style='white-space:nowrap;'><code>{h(opAcc)}</code></td>
                </tr>");
            }

            sb.Append(@"
                    </tbody>
                </table>
            </div>");
        }
        else
        {
            sb.Append(@"
            <div class='card'>
                <div class='card-title'><span>🔒 系統與錯誤 Log 僅限管理者查閱</span></div>
                <p style='font-size:14px;color:#334155;'>系統執行與錯誤異常 Log 包含內部技術診斷資訊，僅限管理者（Admin）帳號查閱與匯出。</p>
            </div>");
        }
        sb.Append("</div>"); // end Tab 7 (logs)

        // =========================================================================
        // Tab 8: 系統線上說明與操作畫面搜尋手冊 (#pane-help)
        // =========================================================================
        sb.Append($"<div id='pane-help' class='tab-pane {(currentTab == "help" ? "active" : "")}'>");
        sb.Append(buildOnlineHelpCenterInnerHtml(
            isAdmin,
            isStandalonePage: false,
            initialSearchQuery: Request.Query["q"].ToString(),
            initialCategory: Request.Query["cat"].ToString(),
            initialTopic: Request.Query["topic"].ToString()));
        sb.Append("</div>"); // end Tab 8 (help)

        // 建立前端設定回合所需的既有議程設定與工項指派對應 JSON
        var sessionMetaJsonList = new List<string>();
        foreach (DataRow sRow in sessionTable.Rows)
        {
            int sPk = Convert.ToInt32(sRow["votingSessionPk"]);
            var pList = new List<int>();
            foreach (DataRow pRow in participantTable.Rows)
            {
                if (Convert.ToInt32(pRow["votingSessionPk"]) == sPk)
                {
                    pList.Add(Convert.ToInt32(pRow["employeePk"]));
                }
            }
            string tKind = sRow["targetKind"]?.ToString() ?? "workItemType";
            int tTypePk = sRow["workItemTypePk"] != DBNull.Value ? Convert.ToInt32(sRow["workItemTypePk"]) : 0;
            int tItemPk = sRow["workItemPk"] != DBNull.Value ? Convert.ToInt32(sRow["workItemPk"]) : 0;
            int deckPk = sRow.Table.Columns.Contains("cardDeckPk") && sRow["cardDeckPk"] != DBNull.Value ? Convert.ToInt32(sRow["cardDeckPk"]) : 1;
            decimal minPt = sRow.Table.Columns.Contains("minStoryPoint") && sRow["minStoryPoint"] != DBNull.Value ? Convert.ToDecimal(sRow["minStoryPoint"]) : 0.5m;
            decimal maxPt = sRow.Table.Columns.Contains("maxStoryPoint") && sRow["maxStoryPoint"] != DBNull.Value ? Convert.ToDecimal(sRow["maxStoryPoint"]) : 21m;
            decimal maxSpread = sRow.Table.Columns.Contains("maxAllowedSpread") && sRow["maxAllowedSpread"] != DBNull.Value ? Convert.ToDecimal(sRow["maxAllowedSpread"]) : 5m;
            string cRule = sRow.Table.Columns.Contains("consensusRule") ? (sRow["consensusRule"]?.ToString() ?? "nearestFibonacci") : "nearestFibonacci";
            bool autoRev = sRow.Table.Columns.Contains("autoRevealWhenAllVoted") && sRow["autoRevealWhenAllVoted"] != DBNull.Value && Convert.ToBoolean(sRow["autoRevealWhenAllVoted"]);
            int tSec = sRow.Table.Columns.Contains("roundTimeLimitSecond") && sRow["roundTimeLimitSecond"] != DBNull.Value ? Convert.ToInt32(sRow["roundTimeLimitSecond"]) : 180;
            bool allowQ = !sRow.Table.Columns.Contains("allowQuestionCard") || sRow["allowQuestionCard"] == DBNull.Value || Convert.ToBoolean(sRow["allowQuestionCard"]);
            bool allowC = !sRow.Table.Columns.Contains("allowCoffeeCard") || sRow["allowCoffeeCard"] == DBNull.Value || Convert.ToBoolean(sRow["allowCoffeeCard"]);
            string cPol = sRow.Table.Columns.Contains("voteCommentPolicy") ? (sRow["voteCommentPolicy"]?.ToString() ?? "optional") : "optional";
            bool restP = sRow.Table.Columns.Contains("restrictToParticipant") && sRow["restrictToParticipant"] != DBNull.Value && Convert.ToBoolean(sRow["restrictToParticipant"]);
            string gNote = (sRow.Table.Columns.Contains("roundGoalNote") ? (sRow["roundGoalNote"]?.ToString() ?? "") : "").Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", " ");
            string sTitle = (sRow["sessionTitle"]?.ToString() ?? "").Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", " ");
            string sStatusVal = (sRow["sessionStatus"]?.ToString() ?? "voting").Replace("'", "");

            sessionMetaJsonList.Add(
                $"'{sPk}':{{pk:{sPk},title:'{sTitle}',sessionStatus:'{sStatusVal}',targetKind:'{tKind}',workItemTypePk:{tTypePk},workItemPk:{tItemPk},cardDeckPk:{deckPk}," +
                $"minStoryPoint:{minPt.ToString(CultureInfo.InvariantCulture)},maxStoryPoint:{maxPt.ToString(CultureInfo.InvariantCulture)}," +
                $"maxAllowedSpread:{maxSpread.ToString(CultureInfo.InvariantCulture)},consensusRule:'{cRule}'," +
                $"autoRevealWhenAllVoted:{(autoRev ? "true" : "false")},roundTimeLimitSecond:{tSec}," +
                $"allowQuestionCard:{(allowQ ? "true" : "false")},allowCoffeeCard:{(allowC ? "true" : "false")}," +
                $"voteCommentPolicy:'{cPol}',restrictToParticipant:'{(restP ? "true" : "false")}',roundGoalNote:'{gNote}'," +
                $"participants:[{string.Join(",", pList)}]}}");
        }

        var itemAssigneeMap = new Dictionary<int, List<int>>();
        foreach (DataRow aRow in assignTable.Rows)
        {
            int wPk = Convert.ToInt32(aRow["workItemPk"]);
            int ePk = Convert.ToInt32(aRow["employeePk"]);
            if (!itemAssigneeMap.ContainsKey(wPk)) itemAssigneeMap[wPk] = new List<int>();
            itemAssigneeMap[wPk].Add(ePk);
        }
        var itemAssigneeJsonList = new List<string>();
        foreach (var kv in itemAssigneeMap)
        {
            itemAssigneeJsonList.Add($"'{kv.Key}':[{string.Join(",", kv.Value)}]");
        }

        // 前端互動與 SignalR 即時同步腳本
        sb.Append($"</div><script>const isViewerAdmin = {(isAdmin ? "true" : "false")};");
        sb.Append($"const sessionConfigMap = {{{string.Join(",", sessionMetaJsonList)}}};");
        sb.Append($"const workItemAssigneeMap = {{{string.Join(",", itemAssigneeJsonList)}}};");
        sb.Append(@"
        function toggleDropdownMenu(evt, groupId) {
            evt.stopPropagation();
            const target = document.getElementById(groupId);
            const wasOpen = target && target.classList.contains('open');
            document.querySelectorAll('.menu-dropdown').forEach(d => d.classList.remove('open'));
            if (target && !wasOpen) {
                target.classList.add('open');
            }
        }

        document.addEventListener('click', function () {
            document.querySelectorAll('.menu-dropdown').forEach(d => d.classList.remove('open'));
        });

        function switchTab(tabName) {
            document.querySelectorAll('.tab-pane').forEach(el => el.classList.remove('active'));
            document.querySelectorAll('.menu-sub-item').forEach(el => el.classList.remove('active'));
            document.querySelectorAll('.menu-main-btn').forEach(el => el.classList.remove('active-group'));
            document.querySelectorAll('.menu-dropdown').forEach(d => d.classList.remove('open'));

            const pane = document.getElementById('pane-' + tabName);
            if (pane) pane.classList.add('active');

            const subItem = document.querySelector('.menu-sub-item[data-tab=""' + tabName + '""]');
            if (subItem) {
                subItem.classList.add('active');
                const parentDropdown = subItem.closest('.menu-dropdown');
                if (parentDropdown) {
                    const mainBtn = parentDropdown.querySelector('.menu-main-btn');
                    if (mainBtn) mainBtn.classList.add('active-group');
                }
            }
            if (tabName === 'roundSetup') {
                const directBtn = document.querySelector('#menu-group-roundsetup .menu-main-btn');
                if (directBtn) directBtn.classList.add('active-group');
            }
            if (tabName === 'myAccount') {
                const directMyAccBtn = document.querySelector('#menu-group-myaccount .menu-main-btn');
                if (directMyAccBtn) directMyAccBtn.classList.add('active-group');
            }
            if (tabName === 'help') {
                const directHelpBtn = document.querySelector('#menu-group-help .menu-main-btn');
                if (directHelpBtn) directHelpBtn.classList.add('active-group');
            }

            const titleMap = {
                'voting': '🃏 1-1. 即時盲投大廳',
                'roundSetup': '⚙️ 1-2. 設定盲投回合',
                'history': '📜 1-3. 估算過程查詢',
                'workitems': isViewerAdmin ? '📋 2-1. 工項與員工指派' : '📋 2-1. 我的工項查詢',
                'types': isViewerAdmin ? '🏷️ 2-2. 工項代表類型' : '🏷️ 2-2. 代表類型參考',
                'stats': isViewerAdmin ? '📊 3-1. 員工與部門統計' : '📊 3-1. 我的分數統計',
                'myAccount': '🔑 修改我的帳密',
                'employees': isViewerAdmin ? '👥 4-1. 員工與組織管理' : '🔑 4-1. 修改我的帳密',
                'logs': '🛡️ 5-1. 系統 Log 查詢',
                'help': '📖 6-1. 線上說明與搜尋'
            };
            const tipMap = {
                'voting': '1-1. 多人即時盲投大廳 (Planning Poker)：參與盲投出牌、同步翻牌揭曉與拍板定案',
                'roundSetup': '1-2. 設定盲投回合：設定估算標的、參與者名單、高低點數上下限、共識規則與計時',
                'history': isViewerAdmin ? '1-3. 查詢訂出 Story Point 的過程：檢視每回合出牌明細、討論備註與點數異動紀錄' : '1-3. 查詢我的投票與訂定點數過程：檢視個人投票紀錄與工項點數異動歷程',
                'workitems': isViewerAdmin ? '2-1. 工項點數查詢與賦予員工設定：查詢各工項點數、選擇代表類型自動帶入或修改員工指派' : '2-1. 我的工項與個人分數查詢：顯示賦予給我本人的工項與個人配額分數',
                'types': isViewerAdmin ? '2-2. 工項代表類型管理：管理已賦予標準 Story Point 的工項代表類型' : '2-2. 工項代表類型標準點數參考：查閱各工項代表類型的標準 Story Point 定義',
                'stats': isViewerAdmin ? '3-1. 員工與部門總 Story Point 統計：查詢員工身上的點數與所屬部門的總 Story Point 數' : '3-1. 我的個人總 Story Point 分數統計：查看個人工項數量與各狀態 Story Point 分數',
                'myAccount': '修改我的個人登入帳號與密碼：每個人皆可隨時修改自己的登入帳號與密碼',
                'employees': isViewerAdmin ? '4-1. 員工列表與登入帳密管理：增刪修查、部門主管設定、樹狀階層與批次匯入' : '4-1. 修改我的個人登入帳號與密碼：檢視個人基本資料並修改帳密',
                'logs': '5-1. 系統與錯誤 Log 查詢中心 (tbLog)：查詢錯誤碼、行號、例外訊息與防崩潰保護紀錄',
                'help': '6-1. 系統線上說明與操作畫面搜尋手冊：支援關鍵字全文搜尋與 12 幅實機操作畫面詳解'
            };
            const pill = document.getElementById('currentMenuTitlePill');
            if (pill && titleMap[tabName]) {
                pill.textContent = titleMap[tabName];
                if (tipMap[tabName]) {
                    pill.setAttribute('data-tip', tipMap[tabName]);
                }
            }
            document.getElementById('spFloatingTooltip')?.classList.remove('visible');

            history.replaceState(null, '', '/?tab=' + tabName);
            setTimeout(refreshAllTableStickyOffsets, 30);
        }

        function openHelpTopic(topicId, searchKw) {
            switchTab('help');
            if (typeof resetOnlineHelpFilter === 'function' && !searchKw) {
                resetOnlineHelpFilter();
            }
            if (searchKw && typeof applyHelpQuickKeyword === 'function') {
                applyHelpQuickKeyword(searchKw);
            }
            if (topicId && typeof jumpToHelpTopic === 'function') {
                setTimeout(function () { jumpToHelpTopic(topicId); }, 60);
            }
        }

        // 投票議程邊欄水平收合/展開（放大右側出牌桌面）
        function toggleVotingSidebarWidth() {
            const grid = document.getElementById('votingGridContainer');
            if (grid) {
                grid.classList.toggle('sidebar-collapsed');
            }
        }

        // 投票議程邊欄垂直收合/展開清單內容
        function toggleVotingSidebarBody() {
            const body = document.getElementById('votingSidebarBody');
            const btn = document.getElementById('btnToggleSidebarBody');
            if (!body) return;
            const isCollapsed = body.classList.toggle('collapsed');
            if (btn) {
                btn.textContent = isCollapsed ? '▾ 展開清單' : '▴ 收合清單';
            }
        }

        // 投票中 / 已拍板 Folder 展開收合
        function toggleSessionFolder(folderId) {
            const folder = document.getElementById(folderId);
            if (folder) {
                folder.classList.toggle('collapsed');
            }
        }

        // 一鍵全展開或全收合所有議程 Folder 與邊項詳情
        function expandAllSessionFolders(expand) {
            document.querySelectorAll('.agenda-folder').forEach(f => {
                if (expand) f.classList.remove('collapsed');
                else f.classList.add('collapsed');
            });
            document.querySelectorAll('.session-item-detail').forEach(d => {
                if (expand) d.classList.remove('collapsed');
                else d.classList.add('collapsed');
            });
        }

        // 個別投票議程邊項本身展開/收合詳情
        function toggleSessionItemDetail(evt, sessionPk, btnEl) {
            evt.stopPropagation();
            evt.preventDefault();
            const detailEl = document.getElementById('session-detail-' + sessionPk);
            if (!detailEl) return;
            const isCollapsed = detailEl.classList.toggle('collapsed');
            if (btnEl) {
                btnEl.textContent = isCollapsed ? '▾ 邊項詳情' : '▴ 收合詳情';
            }
        }

        function selectPokerCard(cardEl, val) {
            document.querySelectorAll('.poker-card').forEach(c => c.classList.remove('selected'));
            cardEl.classList.add('selected');
            document.getElementById('inputCardValue').value = val;
        }

        function validateVoteForm(policy, minPt, maxPt) {
            const cardVal = (document.getElementById('inputCardValue')?.value || '').trim();
            const comment = (document.getElementById('inputVoteComment')?.value || '').trim();
            if (policy === 'requiredAll' && !comment) {
                alert('⚠️ 本回合設定要求「全體參與者出牌時必填估算理由」，請於下方輸入框填寫您的評估想法後再送出！');
                document.getElementById('inputVoteComment')?.focus();
                return false;
            }
            if (policy === 'requiredExtreme' && !comment) {
                const num = parseFloat(cardVal);
                if (cardVal === '?' || (!isNaN(num) && (num <= minPt || num >= maxPt))) {
                    alert('⚠️ 本回合設定要求投出「最低/最高極端點數或 ? 疑問牌」時必須填寫估算理由，請補充說明！');
                    document.getElementById('inputVoteComment')?.focus();
                    return false;
                }
            }
            return true;
        }

        // 設定盲投回合相關互動腳本
        function toggleSetupTargetKind() {
            const kind = document.getElementById('setupTargetKind')?.value || 'workItemType';
            const boxType = document.getElementById('setupBoxType');
            const boxItem = document.getElementById('setupBoxItem');
            if (boxType) boxType.style.display = (kind === 'workItemType') ? 'block' : 'none';
            if (boxItem) boxItem.style.display = (kind === 'workItem') ? 'block' : 'none';
            autoFillSetupTitle();
        }

        function autoFillSetupTitle() {
            const selSession = document.getElementById('setupSessionSelector');
            if (selSession && selSession.value !== '') return; // 修改既有議程時不自動覆蓋標題
            const kind = document.getElementById('setupTargetKind')?.value || 'workItemType';
            const titleInput = document.getElementById('setupSessionTitle');
            if (!titleInput) return;
            if (kind === 'workItemType') {
                const sel = document.getElementById('setupWorkItemTypePk');
                const opt = sel?.options[sel.selectedIndex];
                const name = opt?.getAttribute('data-name') || '';
                if (name) {
                    titleInput.value = '【代表類型估算】' + name + ' 標準點數訂定';
                } else {
                    titleInput.placeholder = '請輸入新工項代表類型名稱（例如：【代表類型估算】複合交易 API 模組標準點數訂定）';
                }
            } else {
                const sel = document.getElementById('setupWorkItemPk');
                const opt = sel?.options[sel.selectedIndex];
                const name = opt?.getAttribute('data-name') || '';
                if (name) {
                    titleInput.value = '【特定工項估算】' + name;
                } else {
                    titleInput.placeholder = '請輸入新實際工項名稱（例如：【特定工項估算】核心交易引擎批次結算優化）';
                }
            }
        }

        function getPokerCardMeaningMeta(c) {
            const map = {
                '?': { shortText: '需求不明', fullText: '? (疑問牌：需求不明需討論)' },
                '☕': { shortText: '暫停休息', fullText: '☕ (休息牌：暫停休息)' },
                '0': { shortText: '無需工時', fullText: '0 點 (無需額外工時)' },
                '0.5': { shortText: '極小微調', fullText: '0.5 點 (極小微調)' },
                '1': { shortText: '極簡變更', fullText: '1 點 (極簡變更)' },
                '2': { shortText: '小型工項', fullText: '2 點 (小型工項)' },
                '3': { shortText: '標準一般', fullText: '3 點 (標準一般工項)' },
                '5': { shortText: '中等複雜', fullText: '5 點 (中等複雜度)' },
                '8': { shortText: '高複雜度', fullText: '8 點 (高複雜度工項)' },
                '13': { shortText: '大型跨模組', fullText: '13 點 (大型跨模組工項)' },
                '20': { shortText: '極大工項', fullText: '20 點 (極大工項)' },
                '21': { shortText: '極大工項', fullText: '21 點 (極大工項)' },
                '40': { shortText: '巨型需拆解', fullText: '40 點 (巨型需拆解)' },
                '100': { shortText: '史詩級拆解', fullText: '100 點 (史詩級需拆解)' }
            };
            return map[c] || { shortText: c + ' 點', fullText: c + ' 點' };
        }

        function updateSetupCardPreview() {
            const deckSel = document.getElementById('setupCardDeckPk');
            const container = document.getElementById('setupCardPreviewContainer');
            const badge = document.getElementById('setupCardCountBadge');
            if (!deckSel || !container) return;

            const opt = deckSel.options[deckSel.selectedIndex];
            const csv = opt?.getAttribute('data-csv') || '0,0.5,1,2,3,5,8,13,21,?,☕';
            const minPt = parseFloat(document.getElementById('setupMinStoryPoint')?.value || '0');
            const maxPt = parseFloat(document.getElementById('setupMaxStoryPoint')?.value || '21');
            const allowQ = document.getElementById('setupAllowQuestionCard')?.checked ?? true;
            const allowC = document.getElementById('setupAllowCoffeeCard')?.checked ?? true;

            // 若牌組字串出現第 2 個問號 (舊版編碼將 ☕ 轉成 ?)，自動還原為 ☕
            let qSeen = 0;
            const cards = csv.split(',').map(s => s.trim()).filter(Boolean).map(tok => {
                if (tok === '?') {
                    qSeen++;
                    if (qSeen >= 2) return '☕';
                }
                return tok;
            });

            let activeCount = 0;
            let html = '';
            cards.forEach(c => {
                const meta = getPokerCardMeaningMeta(c);
                let enabled = true;
                let disableTag = '範圍外';
                if (c === '?') {
                    enabled = allowQ;
                    if (!enabled) disableTag = '未開放';
                } else if (c === '☕') {
                    enabled = allowC;
                    if (!enabled) disableTag = '未開放';
                } else {
                    const v = parseFloat(c);
                    if (!isNaN(v) && (v < minPt || v > maxPt)) enabled = false;
                }
                if (enabled) {
                    activeCount++;
                    html += `<span style=""display:inline-flex;flex-direction:column;align-items:center;justify-content:center;gap:2px;min-width:66px;height:64px;padding:4px 8px;border-radius:8px;border:2px solid #2563eb;background:#eff6ff;color:#1e3a8a;box-shadow:0 1px 3px rgba(37,99,235,0.12);"" title=""可投出牌面：${meta.fullText}"">
                        <span style=""font-weight:800;font-size:17px;line-height:1.1;"">${c}</span>
                        <span style=""font-weight:700;font-size:11px;color:#1d4ed8;line-height:1.1;white-space:nowrap;"">${meta.shortText}</span>
                    </span>`;
                } else {
                    html += `<span style=""display:inline-flex;flex-direction:column;align-items:center;justify-content:center;gap:1px;min-width:66px;height:64px;padding:4px 8px;border-radius:8px;border:1px dashed #cbd5e1;background:#f1f5f9;color:#94a3b8;"" title=""已排除於本回合 (${disableTag})：${meta.fullText}"">
                        <span style=""font-weight:700;font-size:15px;line-height:1.1;text-decoration:line-through;"">${c}</span>
                        <span style=""font-weight:600;font-size:10.5px;color:#94a3b8;line-height:1.1;white-space:nowrap;"">${meta.shortText}</span>
                        <span style=""font-weight:700;font-size:9.5px;color:#dc2626;line-height:1;"">(${disableTag})</span>
                    </span>`;
                }
            });
            container.innerHTML = html;
            if (badge) {
                badge.textContent = `本回合開放 ${activeCount} 張可投牌面（範圍 ${minPt} ~ ${maxPt} pt）`;
            }
        }

        function updateSetupParticipantCount() {
            const cbs = document.querySelectorAll('.setup-participant-cb');
            let checkedCount = 0;
            cbs.forEach(cb => { if (cb.checked) checkedCount++; });
            const lbl = document.getElementById('setupSelectedParticipantCount');
            if (lbl) {
                lbl.textContent = `已勾選 ${checkedCount} / ${cbs.length} 位參與者`;
            }
        }

        function toggleAllParticipants(checkedState) {
            document.querySelectorAll('.setup-participant-cb').forEach(cb => {
                cb.checked = checkedState;
            });
            updateSetupParticipantCount();
        }

        function selectParticipantsByDept(deptName) {
            if (!deptName) return;
            document.querySelectorAll('#setupParticipantGrid .participant-chip').forEach(chip => {
                if (chip.getAttribute('data-dept') === deptName) {
                    const cb = chip.querySelector('.setup-participant-cb');
                    if (cb) cb.checked = true;
                }
            });
            document.getElementById('setupQuickDeptSelect').value = '';
            updateSetupParticipantCount();
        }

        function selectAssignedWorkItemEmployees() {
            const wPk = document.getElementById('setupWorkItemPk')?.value || '';
            const assignedList = workItemAssigneeMap[wPk] || [];
            if (assignedList.length === 0) {
                alert('ℹ️ 目前選取的工項尚無預先指派員工，將保持現有勾選名單。');
                return;
            }
            document.querySelectorAll('.setup-participant-cb').forEach(cb => {
                const empPk = parseInt(cb.value, 10);
                cb.checked = assignedList.indexOf(empPk) !== -1;
            });
            updateSetupParticipantCount();
        }

        function applyRoundPreset(presetName) {
            if (presetName === 'standard') {
                document.getElementById('setupMinStoryPoint').value = '0.5';
                document.getElementById('setupMaxStoryPoint').value = '21';
                document.getElementById('setupConsensusRule').value = 'nearestFibonacci';
                document.getElementById('setupMaxAllowedSpread').value = '5';
                document.getElementById('setupRoundTimeLimitSecond').value = '180';
                document.getElementById('setupVoteCommentPolicy').value = 'optional';
                document.getElementById('setupAutoRevealWhenAllVoted').checked = true;
                document.getElementById('setupAllowQuestionCard').checked = true;
                document.getElementById('setupAllowCoffeeCard').checked = true;
            } else if (presetName === 'blitz') {
                document.getElementById('setupMinStoryPoint').value = '1';
                document.getElementById('setupMaxStoryPoint').value = '13';
                document.getElementById('setupConsensusRule').value = 'median';
                document.getElementById('setupMaxAllowedSpread').value = '3';
                document.getElementById('setupRoundTimeLimitSecond').value = '60';
                document.getElementById('setupVoteCommentPolicy').value = 'optional';
                document.getElementById('setupAutoRevealWhenAllVoted').checked = true;
                document.getElementById('setupAllowQuestionCard').checked = false;
                document.getElementById('setupAllowCoffeeCard').checked = false;
            } else if (presetName === 'strict') {
                document.getElementById('setupMinStoryPoint').value = '1';
                document.getElementById('setupMaxStoryPoint').value = '40';
                document.getElementById('setupConsensusRule').value = 'trimmedMean';
                document.getElementById('setupMaxAllowedSpread').value = '3';
                document.getElementById('setupRoundTimeLimitSecond').value = '300';
                document.getElementById('setupVoteCommentPolicy').value = 'requiredExtreme';
                document.getElementById('setupAutoRevealWhenAllVoted').checked = true;
                document.getElementById('setupAllowQuestionCard').checked = true;
                document.getElementById('setupAllowCoffeeCard').checked = true;
            }
            updateSetupCardPreview();
        }

        function onSetupSessionSelectChange() {
            const sel = document.getElementById('setupSessionSelector');
            const submitBtn = document.getElementById('setupSubmitBtn');
            if (!sel) return;
            const sPk = sel.value;
            if (!sPk || !sessionConfigMap[sPk]) {
                if (submitBtn) submitBtn.innerHTML = '🚀 儲存回合設定並啟動多人盲投議程';
                autoFillSetupTitle();
                return;
            }
            const cfg = sessionConfigMap[sPk];
            document.getElementById('setupTargetKind').value = cfg.targetKind;
            toggleSetupTargetKind();
            if (cfg.targetKind === 'workItemType' && cfg.workItemTypePk > 0) {
                document.getElementById('setupWorkItemTypePk').value = cfg.workItemTypePk;
            } else if (cfg.targetKind === 'workItem' && cfg.workItemPk > 0) {
                document.getElementById('setupWorkItemPk').value = cfg.workItemPk;
            }
            document.getElementById('setupSessionTitle').value = cfg.title || '';
            document.getElementById('setupRoundGoalNote').value = cfg.roundGoalNote || '';
            document.getElementById('setupCardDeckPk').value = cfg.cardDeckPk || 1;
            document.getElementById('setupMinStoryPoint').value = cfg.minStoryPoint;
            document.getElementById('setupMaxStoryPoint').value = cfg.maxStoryPoint;
            document.getElementById('setupAllowQuestionCard').checked = !!cfg.allowQuestionCard;
            document.getElementById('setupAllowCoffeeCard').checked = !!cfg.allowCoffeeCard;
            document.getElementById('setupRestrictToParticipant').value = cfg.restrictToParticipant;
            document.getElementById('setupConsensusRule').value = cfg.consensusRule || 'nearestFibonacci';
            document.getElementById('setupMaxAllowedSpread').value = cfg.maxAllowedSpread;
            document.getElementById('setupRoundTimeLimitSecond').value = String(cfg.roundTimeLimitSecond);
            document.getElementById('setupVoteCommentPolicy').value = cfg.voteCommentPolicy || 'optional';
            document.getElementById('setupAutoRevealWhenAllVoted').checked = !!cfg.autoRevealWhenAllVoted;

            document.querySelectorAll('.setup-participant-cb').forEach(cb => {
                const empPk = parseInt(cb.value, 10);
                cb.checked = (cfg.participants || []).indexOf(empPk) !== -1;
            });
            const resetCb = document.getElementById('setupResetVotes');
            if (resetCb && cfg.sessionStatus === 'finalized') {
                resetCb.checked = true;
            }
            updateSetupCardPreview();
            updateSetupParticipantCount();
            if (submitBtn) {
                submitBtn.innerHTML = cfg.sessionStatus === 'finalized'
                    ? '🔄 重啟此議程重新投票並返回盲投桌面'
                    : '💾 更新本議程回合設定並返回盲投桌面';
            }
        }

        function openRoundSetupForSession(sessionPk) {
            openSessionSetupEditModal(sessionPk);
        }

        function initFloatingTooltips() {
            let tipBox = document.getElementById('spFloatingTooltip');
            if (!tipBox) {
                tipBox = document.createElement('div');
                tipBox.id = 'spFloatingTooltip';
                tipBox.className = 'sp-floating-tooltip';
                document.body.appendChild(tipBox);
            }

            let activeTarget = null;

            function positionTip(el) {
                if (!el || !tipBox) return;
                const rect = el.getBoundingClientRect();
                const tipRect = tipBox.getBoundingClientRect();
                const margin = 8;
                let top = rect.top - tipRect.height - margin;
                let left = rect.left;

                if (el.classList && el.classList.contains('menu-sub-item')) {
                    // 子選單項目：浮動 tips 優先顯示於該選項右側（若右側空間不足則顯示於左側），避免遮擋上下選項
                    top = Math.max(8, Math.min(window.innerHeight - tipRect.height - 8, rect.top));
                    left = rect.right + 10;
                    if (left + tipRect.width > window.innerWidth - 12) {
                        left = Math.max(8, rect.left - tipRect.width - 10);
                    }
                } else if (el.classList && el.classList.contains('menu-main-btn')) {
                    // 頂部主選單按鈕：優先顯示於按鈕上方；若頁面捲動至頂部且含有下拉子選單，則顯示於按鈕右上方避免遮擋下拉選單
                    const hasSubMenu = !!(el.closest('.menu-dropdown') && el.closest('.menu-dropdown').querySelector('.menu-sub-list'));
                    if (rect.top - tipRect.height - 6 >= 4) {
                        top = rect.top - tipRect.height - 6;
                    } else if (hasSubMenu) {
                        top = Math.max(4, rect.top + 4);
                        left = rect.right + 8;
                    } else {
                        top = rect.bottom + margin;
                    }
                    if (left + tipRect.width > window.innerWidth - 12) {
                        left = Math.max(8, window.innerWidth - tipRect.width - 12);
                    }
                } else {
                    if (top < 8) {
                        top = rect.bottom + margin;
                    }
                    if (left + tipRect.width > window.innerWidth - 12) {
                        left = Math.max(8, window.innerWidth - tipRect.width - 12);
                    }
                }

                tipBox.style.top = Math.round(top) + 'px';
                tipBox.style.left = Math.round(left) + 'px';
            }

            document.addEventListener('mouseover', function (evt) {
                const target = evt.target && evt.target.closest ? evt.target.closest('[data-tip]') : null;
                if (!target) return;
                const tipText = target.getAttribute('data-tip');
                if (!tipText) return;
                if (target.hasAttribute('title')) {
                    target.setAttribute('data-native-title', target.getAttribute('title') || '');
                    target.removeAttribute('title');
                }
                activeTarget = target;
                tipBox.textContent = tipText;
                tipBox.classList.add('visible');
                positionTip(target);
            });

            document.addEventListener('mouseout', function (evt) {
                if (!activeTarget) return;
                const related = evt.relatedTarget;
                if (related && activeTarget.contains(related)) return;
                if (activeTarget.hasAttribute('data-native-title')) {
                    activeTarget.setAttribute('title', activeTarget.getAttribute('data-native-title') || '');
                }
                activeTarget = null;
                tipBox.classList.remove('visible');
            });

            window.addEventListener('scroll', function () {
                if (activeTarget) tipBox.classList.remove('visible');
            }, true);
        }

        const isCurrentUserAdmin = " + (isAdmin ? "true" : "false") + @";

        document.addEventListener('DOMContentLoaded', function () {
            initFloatingTooltips();
            initAllSortableLockableTables();
            initTableRowContextMenu();
            if (document.getElementById('setupSessionTitle') && !document.getElementById('setupSessionTitle').value) {
                autoFillSetupTitle();
            }
            updateSetupCardPreview();
            updateSetupParticipantCount();

            // 啟動盲投桌面思考倒數計時器
            const timerEl = document.getElementById('roundCountdownTimer');
            if (timerEl) {
                let remain = parseInt(timerEl.getAttribute('data-seconds') || '0', 10);
                if (remain > 0) {
                    const iv = setInterval(function () {
                        remain--;
                        if (remain <= 0) {
                            clearInterval(iv);
                            timerEl.textContent = '⏰ 時間到！請儘速出牌或翻牌';
                            timerEl.style.color = '#dc2626';
                        } else {
                            const m = Math.floor(remain / 60);
                            const s = remain % 60;
                            timerEl.textContent = (m > 0 ? m + ' 分 ' : '') + s + ' 秒';
                        }
                    }, 1000);
                }
            }
        });

        // =====================================================================
        // 全域 Table Header 排序 (Sorting)、欄位鎖定 (Locking) 與即時表格篩選 (Filtering) 引擎
        // 凡是頁面上的 <table> 只要包含 <thead>，皆自動賦予：
        //   1. 點擊表頭文字切換 ▲升冪 / ▼降冪 / ⇅預設 排序（支援鎖定欄位優先排序與 Shift 多欄排序）
        //   2. 點擊表頭右側 📌/🔒 按鈕鎖定(凍結)或解鎖該欄位（水平捲動時固定於左側 + 鎖定排序優先權）
        //   3. 表格上方工具列支援「🔍 關鍵字快速篩選」、「🏷️ 指定欄位篩選 + 欄位值下拉選單」、「📊 筆數統計徽章」、「🔒 表頭列鎖定 (Sticky Header)」與「🔄 重設排序/篩選/解鎖」
        // =====================================================================
        const spTableStateMap = new WeakMap();

        function extractCellCleanText(cell) {
            if (!cell) return '';
            const attrVal = cell.getAttribute('data-sort-value');
            if (attrVal !== null && attrVal !== undefined && attrVal !== '') return attrVal.trim();
            return (cell.innerText || cell.textContent || '').replace(/⚡\s*操作\s*▾/g, '').replace(/\s+/g, ' ').trim();
        }

        function extractCellFilterDisplayText(cell) {
            if (!cell) return '';
            return (cell.innerText || cell.textContent || '').replace(/⚡\s*操作\s*▾/g, '').replace(/\s+/g, ' ').trim();
        }

        function syncRowDisplayState(tr) {
            if (!tr) return false;
            const customHidden = tr.getAttribute('data-custom-hidden') === '1';
            const toolbarHidden = tr.getAttribute('data-sp-toolbar-hidden') === '1';
            const shouldHide = customHidden || toolbarHidden;
            tr.style.display = shouldHide ? 'none' : '';
            return !shouldHide;
        }

        function parseSmartSortToken(rawText) {
            if (!rawText) return { type: 'empty', num: -Infinity, str: '' };
            const s = rawText.trim();
            if (s === '-' || s === '尚未定案' || s === '待投票定案' || s === '待估算') {
                return { type: 'empty', num: -Infinity, str: s };
            }
            // 日期時間格式 YYYY-MM-DD HH:mm:ss
            if (/^\d{4}-\d{2}-\d{2}(?:\s+\d{2}:\d{2}(?::\d{2})?)?$/.test(s)) {
                const ts = Date.parse(s.replace(' ', 'T'));
                if (!isNaN(ts)) return { type: 'number', num: ts, str: s };
            }
            // 第 N 回合
            const roundMatch = s.match(/^第\s*(\d+(?:\.\d+)?)\s*回合/);
            if (roundMatch) {
                return { type: 'number', num: parseFloat(roundMatch[1]), str: s };
            }
            // 數值開頭搭配單位：例如 '7.50 pt (2 件)', '15 pt', '3 件', '5 人', '0.5 ~ 21 pt', '18.5 pt / 人'
            const unitMatch = s.match(/^([+-]?\d+(?:\.\d+)?)\s*(?:pt|點|件|人|個工項|個|%|秒|筆|組|~|$)/i);
            if (unitMatch) {
                return { type: 'number', num: parseFloat(unitMatch[1]), str: s };
            }
            // 純數字
            if (/^[+-]?\d+(?:\.\d+)?$/.test(s)) {
                return { type: 'number', num: parseFloat(s), str: s };
            }
            return { type: 'string', num: NaN, str: s };
        }

        function compareTableCells(cellA, cellB, asc) {
            const tokA = parseSmartSortToken(extractCellCleanText(cellA));
            const tokB = parseSmartSortToken(extractCellCleanText(cellB));
            let cmp = 0;

            if (tokA.type === 'number' && tokB.type === 'number') {
                cmp = tokA.num - tokB.num;
                if (cmp === 0) {
                    cmp = tokA.str.localeCompare(tokB.str, 'zh-Hant', { numeric: true, sensitivity: 'base' });
                }
            } else if (tokA.type === 'number' && tokB.type === 'empty') {
                cmp = 1;
            } else if (tokA.type === 'empty' && tokB.type === 'number') {
                cmp = -1;
            } else {
                cmp = tokA.str.localeCompare(tokB.str, 'zh-Hant', { numeric: true, sensitivity: 'base' });
            }
            return asc ? cmp : -cmp;
        }

        function initAllSortableLockableTables() {
            const tables = document.querySelectorAll('table');
            tables.forEach((table, tIdx) => {
                if (spTableStateMap.has(table)) return;
                const thead = table.querySelector('thead');
                const tbody = table.querySelector('tbody');
                if (!thead || !tbody) return;
                const headerRow = thead.querySelector('tr');
                if (!headerRow) return;
                const thList = Array.from(headerRow.querySelectorAll('th'));
                if (thList.length === 0) return;

                // 預設啟用表頭列垂直捲動鎖定 (Sticky Header)
                table.classList.add('sp-sticky-header-enabled');

                // 記錄原始資料列順序以便隨時重設恢復
                Array.from(tbody.querySelectorAll('tr')).forEach((tr, rIdx) => {
                    tr.setAttribute('data-sp-orig-idx', String(rIdx));
                });

                const colNames = thList.map(th => (th.innerText || th.textContent || '').replace(/\s+/g, ' ').trim());

                // 建立外層包裝與表頭狀態工具列 (.sp-table-wrapper > .sp-table-toolbar + .sp-table-scroll-container)
                const wrapper = document.createElement('div');
                wrapper.className = 'sp-table-wrapper';

                const toolbar = document.createElement('div');
                toolbar.className = 'sp-table-toolbar';

                const statusSpan = document.createElement('div');
                statusSpan.className = 'sp-table-toolbar-status';
                statusSpan.innerHTML = '<span>⇅ 點擊表頭排序</span><span>｜</span><span>📌 圖釘鎖定欄位</span><span>｜</span><span>🖱️ 滑鼠右鍵選擇紀錄並開啟操作選單（編輯彈出 Modal 視窗）</span>';

                // 建立即時篩選控制區 (.sp-table-toolbar-filter)
                const filterDiv = document.createElement('div');
                filterDiv.className = 'sp-table-toolbar-filter';

                const colSelect = document.createElement('select');
                colSelect.className = 'sp-tbl-filter-select';
                colSelect.title = '選擇要篩選的指定欄位（預設為全部欄位）';
                const allColOpt = document.createElement('option');
                allColOpt.value = '-1';
                allColOpt.textContent = '🏷️ 全部欄位篩選';
                colSelect.appendChild(allColOpt);
                colNames.forEach((cName, cIdx) => {
                    const opt = document.createElement('option');
                    opt.value = String(cIdx);
                    opt.textContent = '欄位：' + cName;
                    colSelect.appendChild(opt);
                });

                const valSelect = document.createElement('select');
                valSelect.className = 'sp-tbl-filter-select';
                valSelect.title = '快速選擇該欄位的既有值進行過濾';
                valSelect.style.display = 'none';

                const filterInput = document.createElement('input');
                filterInput.type = 'text';
                filterInput.className = 'sp-tbl-filter-input';
                filterInput.placeholder = '🔍 輸入關鍵字即時篩選本表格...';
                filterInput.title = '輸入任意文字即時過濾此表格紀錄（亦可於儲存格按右鍵直接以此值篩選）';

                const clearFilterBtn = document.createElement('button');
                clearFilterBtn.type = 'button';
                clearFilterBtn.className = 'sp-toolbar-btn';
                clearFilterBtn.innerHTML = '✖ 清除篩選';
                clearFilterBtn.title = '清除此表格工具列的篩選條件';
                clearFilterBtn.style.display = 'none';

                const countBadge = document.createElement('span');
                countBadge.className = 'sp-tbl-count-badge';
                const totalRowsInit = tbody.querySelectorAll('tr').length;
                countBadge.textContent = '顯示 ' + totalRowsInit + ' / ' + totalRowsInit + ' 筆';

                filterDiv.appendChild(colSelect);
                filterDiv.appendChild(valSelect);
                filterDiv.appendChild(filterInput);
                filterDiv.appendChild(clearFilterBtn);
                filterDiv.appendChild(countBadge);

                const actionsDiv = document.createElement('div');
                actionsDiv.className = 'sp-table-toolbar-actions';

                const stickyBtn = document.createElement('button');
                stickyBtn.type = 'button';
                stickyBtn.className = 'sp-toolbar-btn active';
                stickyBtn.innerHTML = '🔒 表頭已鎖定';
                stickyBtn.title = '切換垂直捲動時是否固定鎖定頂部表頭列';

                const resetBtn = document.createElement('button');
                resetBtn.type = 'button';
                resetBtn.className = 'sp-toolbar-btn';
                resetBtn.innerHTML = '🔄 重設表格';
                resetBtn.title = '恢復預設資料列順序、清除篩選並解除所有鎖定欄位';

                actionsDiv.appendChild(stickyBtn);
                actionsDiv.appendChild(resetBtn);
                toolbar.appendChild(statusSpan);
                toolbar.appendChild(filterDiv);
                toolbar.appendChild(actionsDiv);

                const scrollContainer = document.createElement('div');
                scrollContainer.className = 'sp-table-scroll-container';

                const parent = table.parentNode;
                parent.insertBefore(wrapper, table);
                wrapper.appendChild(toolbar);
                wrapper.appendChild(scrollContainer);
                scrollContainer.appendChild(table);

                const state = {
                    table,
                    tbody,
                    thList,
                    colNames,
                    // sortList: [{ colIndex: number, asc: boolean }]
                    sortList: [],
                    // lockedCols: Set<number>
                    lockedCols: new Set(),
                    statusSpan,
                    stickyBtn,
                    resetBtn,
                    colSelect,
                    valSelect,
                    filterInput,
                    clearFilterBtn,
                    countBadge
                };
                spTableStateMap.set(table, state);

                // 改造每一個 <th> 表頭，加入「排序觸發區 (⇅/▲/▼)」與「鎖定欄位按鈕 (📌/🔒)」
                thList.forEach((th, colIdx) => {
                    const rawTitle = colNames[colIdx] || ('欄位 ' + (colIdx + 1));
                    th.innerHTML = '';
                    const box = document.createElement('div');
                    box.className = 'sp-th-box';

                    const sortBtn = document.createElement('span');
                    sortBtn.className = 'sp-th-sort-btn';
                    sortBtn.title = '點擊依【' + rawTitle + '】切換升冪(▲) / 降冪(▼) 排序（按住 Shift 點擊可疊加多欄排序）';
                    const titleSpan = document.createElement('span');
                    titleSpan.className = 'sp-th-title-text';
                    titleSpan.textContent = rawTitle;
                    const indicator = document.createElement('span');
                    indicator.className = 'sp-sort-indicator';
                    indicator.textContent = '⇅';

                    sortBtn.appendChild(titleSpan);
                    sortBtn.appendChild(indicator);

                    const lockBtn = document.createElement('button');
                    lockBtn.type = 'button';
                    lockBtn.className = 'sp-th-lock-btn';
                    lockBtn.textContent = '📌';
                    lockBtn.title = '鎖定此欄位【' + rawTitle + '】（水平捲動時固定於左側 + 鎖定排序優先權）';

                    sortBtn.addEventListener('click', function (evt) {
                        evt.stopPropagation();
                        handleTableHeaderSortClick(state, colIdx, evt.shiftKey);
                    });

                    lockBtn.addEventListener('click', function (evt) {
                        evt.stopPropagation();
                        handleTableHeaderLockClick(state, colIdx);
                    });

                    box.appendChild(sortBtn);
                    box.appendChild(lockBtn);
                    th.appendChild(box);
                });

                colSelect.addEventListener('change', function () {
                    populateToolbarColumnDistinctValues(state);
                    applyToolbarTableFilter(state);
                });

                valSelect.addEventListener('change', function () {
                    filterInput.value = valSelect.value || '';
                    applyToolbarTableFilter(state);
                });

                filterInput.addEventListener('input', function () {
                    applyToolbarTableFilter(state);
                });

                clearFilterBtn.addEventListener('click', function () {
                    colSelect.value = '-1';
                    valSelect.style.display = 'none';
                    valSelect.innerHTML = '';
                    filterInput.value = '';
                    applyToolbarTableFilter(state);
                });

                stickyBtn.addEventListener('click', function () {
                    const enabled = table.classList.toggle('sp-sticky-header-enabled');
                    stickyBtn.classList.toggle('active', enabled);
                    stickyBtn.innerHTML = enabled ? '🔒 表頭已鎖定' : '🔓 表頭未鎖定';
                });

                resetBtn.addEventListener('click', function () {
                    state.sortList = [];
                    state.lockedCols.clear();
                    colSelect.value = '-1';
                    valSelect.style.display = 'none';
                    valSelect.innerHTML = '';
                    filterInput.value = '';
                    applyToolbarTableFilter(state);
                    applyTableSortAndLockState(state);
                });
            });
        }

        function populateToolbarColumnDistinctValues(state) {
            const { tbody, colSelect, valSelect } = state;
            const colIdx = parseInt(colSelect.value || '-1', 10);
            valSelect.innerHTML = '';
            if (colIdx < 0) {
                valSelect.style.display = 'none';
                return;
            }
            const distinctSet = new Set();
            Array.from(tbody.querySelectorAll('tr')).forEach(tr => {
                const cell = tr.children[colIdx];
                if (!cell) return;
                const txt = extractCellFilterDisplayText(cell);
                if (txt && txt.length <= 65) {
                    distinctSet.add(txt);
                }
            });
            const vals = Array.from(distinctSet).sort((a, b) => a.localeCompare(b, 'zh-Hant', { numeric: true }));
            if (vals.length === 0) {
                valSelect.style.display = 'none';
                return;
            }
            const defOpt = document.createElement('option');
            defOpt.value = '';
            defOpt.textContent = '-- 選擇欄位值 (' + vals.length + ' 種) --';
            valSelect.appendChild(defOpt);
            vals.slice(0, 60).forEach(v => {
                const o = document.createElement('option');
                o.value = v;
                o.textContent = v;
                valSelect.appendChild(o);
            });
            valSelect.style.display = '';
        }

        function applyToolbarTableFilter(state) {
            if (!state) return;
            const { tbody, colSelect, filterInput, clearFilterBtn, countBadge } = state;
            const colIdx = parseInt(colSelect.value || '-1', 10);
            const kw = (filterInput.value || '').trim().toLowerCase();
            const rows = Array.from(tbody.querySelectorAll('tr'));
            let visibleCount = 0;

            rows.forEach(tr => {
                let match = true;
                if (kw) {
                    if (colIdx >= 0 && tr.children[colIdx]) {
                        const cellTxt = extractCellFilterDisplayText(tr.children[colIdx]).toLowerCase();
                        match = cellTxt.indexOf(kw) !== -1;
                    } else {
                        const rowTxt = (tr.innerText || tr.textContent || '').replace(/⚡\s*操作\s*▾/g, '').toLowerCase();
                        const dataSearch = (tr.getAttribute('data-search') || '').toLowerCase();
                        match = rowTxt.indexOf(kw) !== -1 || dataSearch.indexOf(kw) !== -1;
                    }
                }
                tr.setAttribute('data-sp-toolbar-hidden', match ? '0' : '1');
                if (syncRowDisplayState(tr)) {
                    visibleCount++;
                }
            });

            if (clearFilterBtn) {
                clearFilterBtn.style.display = (kw || colIdx >= 0) ? '' : 'none';
            }
            if (countBadge) {
                countBadge.textContent = '顯示 ' + visibleCount + ' / ' + rows.length + ' 筆';
            }
        }

        function refreshToolbarCountBadgeForTable(tableId) {
            const tbl = document.getElementById(tableId);
            if (!tbl) return;
            const st = spTableStateMap.get(tbl);
            if (!st || !st.countBadge) return;
            const rows = Array.from(st.tbody.querySelectorAll('tr'));
            let vis = 0;
            rows.forEach(r => {
                if (r.style.display !== 'none') vis++;
            });
            st.countBadge.textContent = '顯示 ' + vis + ' / ' + rows.length + ' 筆';
        }

        function handleTableHeaderSortClick(state, colIdx, isShiftKey) {
            const existingIdx = state.sortList.findIndex(s => s.colIndex === colIdx);
            if (existingIdx !== -1) {
                const cur = state.sortList[existingIdx];
                if (cur.asc) {
                    cur.asc = false;
                } else {
                    state.sortList.splice(existingIdx, 1);
                }
            } else {
                if (isShiftKey) {
                    state.sortList.push({ colIndex: colIdx, asc: true });
                } else {
                    // 保留已被鎖定(🔒)欄位的排序設定作為第一優先序，並加入當前點擊欄位排序
                    const lockedSorts = state.sortList.filter(s => state.lockedCols.has(s.colIndex));
                    state.sortList = [...lockedSorts, { colIndex: colIdx, asc: true }];
                }
            }
            applyTableSortAndLockState(state);
        }

        function handleTableHeaderLockClick(state, colIdx) {
            if (state.lockedCols.has(colIdx)) {
                state.lockedCols.delete(colIdx);
            } else {
                state.lockedCols.add(colIdx);
            }
            applyTableSortAndLockState(state);
        }

        function applyTableSortAndLockState(state) {
            const { table, tbody, thList, colNames, sortList, lockedCols, statusSpan } = state;

            // 1. 更新表頭 <th> 排序與鎖定視覺狀態
            thList.forEach((th, idx) => {
                th.classList.remove('th-sorted-asc', 'th-sorted-desc', 'sp-col-locked', 'sp-col-locked-last');
                th.style.left = '';
                const indicator = th.querySelector('.sp-sort-indicator');
                const lockBtn = th.querySelector('.sp-th-lock-btn');

                const sortEntryIdx = sortList.findIndex(s => s.colIndex === idx);
                if (sortEntryIdx !== -1) {
                    const sEntry = sortList[sortEntryIdx];
                    th.classList.add(sEntry.asc ? 'th-sorted-asc' : 'th-sorted-desc');
                    if (indicator) {
                        const orderPrefix = sortList.length > 1 ? String(sortEntryIdx + 1) : '';
                        indicator.textContent = orderPrefix + (sEntry.asc ? '▲' : '▼');
                    }
                } else if (indicator) {
                    indicator.textContent = '⇅';
                }

                const isLocked = lockedCols.has(idx);
                if (lockBtn) {
                    lockBtn.classList.toggle('active', isLocked);
                    lockBtn.textContent = isLocked ? '🔒' : '📌';
                    lockBtn.title = isLocked
                        ? '已鎖定欄位【' + colNames[idx] + '】（點擊解除鎖定）'
                        : '鎖定此欄位【' + colNames[idx] + '】（水平捲動時固定於左側 + 鎖定排序優先權）';
                }
            });

            // 2. 執行資料列排序
            const rows = Array.from(tbody.querySelectorAll('tr'));
            if (sortList.length === 0) {
                rows.sort((rA, rB) => {
                    const iA = parseInt(rA.getAttribute('data-sp-orig-idx') || '0', 10);
                    const iB = parseInt(rB.getAttribute('data-sp-orig-idx') || '0', 10);
                    return iA - iB;
                });
            } else {
                // 被鎖定(🔒)且有排序的欄位優先，其餘依排序順位
                const orderedSorts = [
                    ...sortList.filter(s => lockedCols.has(s.colIndex)),
                    ...sortList.filter(s => !lockedCols.has(s.colIndex))
                ];
                rows.sort((rA, rB) => {
                    for (let i = 0; i < orderedSorts.length; i++) {
                        const { colIndex, asc } = orderedSorts[i];
                        const cA = rA.children[colIndex];
                        const cB = rB.children[colIndex];
                        const cmp = compareTableCells(cA, cB, asc);
                        if (cmp !== 0) return cmp;
                    }
                    const iA = parseInt(rA.getAttribute('data-sp-orig-idx') || '0', 10);
                    const iB = parseInt(rB.getAttribute('data-sp-orig-idx') || '0', 10);
                    return iA - iB;
                });
            }
            rows.forEach(tr => tbody.appendChild(tr));

            // 3. 更新欄位水平凍結鎖定 (Sticky Left Offset)
            updateSingleTableStickyOffsets(state);

            // 4. 更新工具列狀態文字
            const sortText = sortList.length > 0
                ? '📊 排序中：' + sortList.map(s => colNames[s.colIndex] + (s.asc ? '(▲升冪)' : '(▼降冪)')).join(' ➔ ')
                : '⇅ 點擊表頭排序';
            const lockedArr = Array.from(lockedCols).sort((a, b) => a - b).map(i => colNames[i]);
            const lockText = lockedArr.length > 0
                ? '<span style=""color:#1e40af;font-weight:700;"">🔒 已鎖定：' + lockedArr.join('、') + '</span>'
                : '📌 圖釘鎖定欄位';
            statusSpan.innerHTML = `<span>${sortText}</span><span>｜</span><span>${lockText}</span><span>｜</span><span>🖱️ 滑鼠右鍵選擇紀錄並開啟操作選單（編輯彈出 Modal 視窗）</span>`;
        }

        function updateSingleTableStickyOffsets(state) {
            const { table, tbody, thList, lockedCols } = state;
            tbody.querySelectorAll('tr td').forEach(td => {
                td.classList.remove('sp-col-locked', 'sp-col-locked-last');
                td.style.left = '';
            });
            thList.forEach(th => {
                th.classList.remove('sp-col-locked', 'sp-col-locked-last');
                th.style.left = '';
            });

            const sortedLocked = Array.from(lockedCols).sort((a, b) => a - b);
            if (sortedLocked.length === 0) return;

            let leftOffset = 0;
            sortedLocked.forEach((colIdx, i) => {
                const isLast = (i === sortedLocked.length - 1);
                const th = thList[colIdx];
                if (!th) return;
                const colWidth = th.offsetWidth || 110;
                th.classList.add('sp-col-locked');
                if (isLast) th.classList.add('sp-col-locked-last');
                th.style.left = leftOffset + 'px';

                tbody.querySelectorAll('tr').forEach(tr => {
                    const td = tr.children[colIdx];
                    if (td) {
                        td.classList.add('sp-col-locked');
                        if (isLast) td.classList.add('sp-col-locked-last');
                        td.style.left = leftOffset + 'px';
                    }
                });
                leftOffset += colWidth;
            });
        }

        function refreshAllTableStickyOffsets() {
            document.querySelectorAll('table').forEach(tbl => {
                const st = spTableStateMap.get(tbl);
                if (st && st.lockedCols.size > 0) {
                    updateSingleTableStickyOffsets(st);
                }
            });
        }

        window.addEventListener('resize', refreshAllTableStickyOffsets);

        // 系統安全登出處理函數：清除前端盲投 Session 鎖定、廣播登出通知並導向 /logout
        function performSystemLogout(evt) {
            try {
                const keysToRemove = [];
                for (let i = 0; i < localStorage.length; i++) {
                    const k = localStorage.key(i);
                    if (k && k.indexOf('sp_active_session_lock_') === 0) {
                        keysToRemove.push(k);
                    }
                }
                keysToRemove.forEach(k => localStorage.removeItem(k));
                if (typeof BroadcastChannel !== 'undefined') {
                    const ch = new BroadcastChannel('sp_voting_session_guard');
                    ch.postMessage({ type: 'USER_LOGGED_OUT', timestamp: new Date().toLocaleTimeString() });
                    ch.close();
                }
            } catch (e) { }
            return true;
        }

        // =====================================================================
        // 全域表格紀錄「滑鼠右鍵選單 (Context Menu)」與「單筆紀錄編輯 Modal 視窗」引擎
        // 支援：
        //   1. 全站所有 <table> 的任何資料列 (tbody tr) 按下滑鼠右鍵 (contextmenu)，立即反白選取該筆紀錄並彈出右鍵選單（不使用下拉選單）
        //   2. 自動判斷該列資料類型 (workitem / workitemKind / workitemType / employee / department / session / readonlyRecord / 通用表格列)
        //   3. 點選「✏️ 編輯 / 修改」時，直接原地彈出置中 Modal 對話視窗進行單筆編輯，絕不捲動網頁至其他位置！
        // =====================================================================
        let spContextMenuEl = null;
        let spActiveContextRow = null;
        let spRecordEditModalOverlayEl = null;

        function escapeModalHtml(str) {
            return String(str ?? '')
                .replace(/&/g, '&amp;')
                .replace(/</g, '&lt;')
                .replace(/>/g, '&gt;')
                .replace(/""/g, '&quot;')
                .replace(/'/g, '&#39;');
        }

        function showToastNotice(msg) {
            let toast = document.getElementById('spToastNotice');
            if (!toast) {
                toast = document.createElement('div');
                toast.id = 'spToastNotice';
                toast.className = 'sp-toast-notice';
                document.body.appendChild(toast);
            }
            toast.textContent = msg;
            toast.classList.add('show');
            clearTimeout(toast._hideTimer);
            toast._hideTimer = setTimeout(function () {
                toast.classList.remove('show');
            }, 2600);
        }

        function submitDynamicPostAction(actionUrl, fieldsObj, confirmMsg) {
            if (confirmMsg && !window.confirm(confirmMsg)) {
                return;
            }
            const form = document.createElement('form');
            form.method = 'post';
            form.action = actionUrl;
            form.style.display = 'none';
            Object.keys(fieldsObj || {}).forEach(k => {
                const inp = document.createElement('input');
                inp.type = 'hidden';
                inp.name = k;
                inp.value = String(fieldsObj[k] ?? '');
                form.appendChild(inp);
            });
            document.body.appendChild(form);
            form.submit();
        }

        function closeRowContextMenu() {
            if (spContextMenuEl) {
                spContextMenuEl.classList.remove('visible', 'open');
            }
            if (spActiveContextRow) {
                spActiveContextRow.classList.remove('sp-context-active-row');
                spActiveContextRow = null;
            }
        }

        function ensureRecordEditModalOverlay() {
            if (!spRecordEditModalOverlayEl) {
                spRecordEditModalOverlayEl = document.createElement('div');
                spRecordEditModalOverlayEl.id = 'spRecordEditModalOverlay';
                spRecordEditModalOverlayEl.className = 'sp-modal-overlay';
                spRecordEditModalOverlayEl.addEventListener('click', function (evt) {
                    if (evt.target === spRecordEditModalOverlayEl) {
                        closeRecordEditModal();
                    }
                });
                document.body.appendChild(spRecordEditModalOverlayEl);
            }
            return spRecordEditModalOverlayEl;
        }

        function closeRecordEditModal() {
            if (spRecordEditModalOverlayEl) {
                spRecordEditModalOverlayEl.classList.remove('visible', 'open');
                spRecordEditModalOverlayEl.innerHTML = '';
            }
        }

        function openRecordModalShell(titleHtml, badgeHtml, bodyHtml) {
            closeRowContextMenu();
            const overlay = ensureRecordEditModalOverlay();
            overlay.innerHTML = `
                <div class=""sp-modal-dialog"" id=""spRecordEditModal"" role=""dialog"" aria-modal=""true"">
                    <div class=""sp-modal-header"">
                        <div class=""sp-modal-title"">
                            <span>${titleHtml}</span>
                            ${badgeHtml || ''}
                        </div>
                        <button type=""button"" class=""sp-modal-close-btn"" onclick=""closeRecordEditModal()"" title=""關閉視窗 (Esc)"">✖</button>
                    </div>
                    <div class=""sp-modal-body"">
                        ${bodyHtml}
                    </div>
                </div>
            `;
            overlay.classList.add('visible', 'open');
            const firstInput = overlay.querySelector('input:not([type=""hidden""]):not([readonly]), select, textarea');
            if (firstInput) {
                setTimeout(() => firstInput.focus(), 40);
            }
        }

        function toggleAdminDrawer(drawerId, btnEl) {
            const drawer = document.getElementById(drawerId);
            if (!drawer) return;
            const isNowCollapsed = drawer.classList.toggle('collapsed');
            if (btnEl) {
                btnEl.classList.toggle('active-drawer', !isNowCollapsed);
            }
            if (!isNowCollapsed) {
                showToastNotice('📂 已展開管理面板');
            }
        }

        function openWorkItemEditModal(pk, code, title, typePk, pt, status, reason, kind, startIso, endIso, sprintName, forceExpired, assigneeSummary) {
            const isCreate = !pk || String(pk).trim() === '' || String(pk) === '0';
            const kindOptionsHtml = document.getElementById('wiKindSelect') ? document.getElementById('wiKindSelect').innerHTML : '<option value=""project"">🚀 專案 (project)</option><option value=""maintenance"">🛠️ 維運 (maintenance)</option><option value=""consulting"">💼 顧問時數 (consulting)</option>';
            const typeOptionsHtml = document.getElementById('wiTypeSelect') ? document.getElementById('wiTypeSelect').innerHTML : '<option value=""0"">-- 不使用代表類型 --</option>';
            const empOptionsHtml = document.getElementById('assignEmployeeSelect') ? document.getElementById('assignEmployeeSelect').innerHTML : '';
            const curSprint = sprintName || (document.getElementById('wiSprint') ? document.getElementById('wiSprint').value : '2026-Q4 Sprint 2');
            let resolvedStart = startIso || formatLocalIsoMinute(new Date());
            let resolvedEnd = endIso || formatLocalIsoMinute(new Date(Date.now() + 24 * 3600 * 1000));
            if (forceExpired) {
                const now = new Date();
                resolvedStart = formatLocalIsoMinute(new Date(now.getTime() - 7 * 24 * 3600 * 1000));
                resolvedEnd = formatLocalIsoMinute(new Date(now.getTime() - 10 * 60 * 1000));
            }

            let initQty = 1;
            let initUnit = 'day';
            try {
                const sDt = new Date(resolvedStart);
                const eDt = new Date(resolvedEnd);
                if (!isNaN(sDt.getTime()) && !isNaN(eDt.getTime()) && eDt > sDt) {
                    const diffDays = Math.max(1, Math.round((eDt.getTime() - sDt.getTime()) / (24 * 3600 * 1000)));
                    if (diffDays % 365 === 0) {
                        initQty = diffDays / 365;
                        initUnit = 'year';
                    } else if (diffDays % 90 === 0) {
                        initQty = diffDays / 90;
                        initUnit = 'quarter';
                    } else if (diffDays % 30 === 0) {
                        initQty = diffDays / 30;
                        initUnit = 'month';
                    } else if (diffDays % 7 === 0) {
                        initQty = diffDays / 7;
                        initUnit = 'week';
                    } else {
                        initQty = diffDays;
                        initUnit = 'day';
                    }
                }
            } catch (e) { }

            const bodyHtml = `
                <form method=""post"" action=""/workitem/save"">
                    <input type=""hidden"" name=""workItemPk"" value=""${escapeModalHtml(pk)}"" />
                    <div class=""form-row"">
                        <div>
                            <label>工項代碼</label>
                            <input type=""text"" name=""itemCode"" id=""modalWiCode"" value=""${escapeModalHtml(code)}"" required />
                        </div>
                        <div>
                            <label>工項類型</label>
                            <select name=""workItemKind"" id=""modalWiKind"" required style=""border-color:#2563eb;font-weight:700;background:#eff6ff;"">
                                ${kindOptionsHtml}
                            </select>
                        </div>
                        <div>
                            <label>工項標題</label>
                            <input type=""text"" name=""itemTitle"" id=""modalWiTitle"" value=""${escapeModalHtml(title)}"" placeholder=""請輸入工項標題"" required />
                        </div>
                    </div>
                    <div class=""form-row"">
                        <div>
                            <label data-tip=""設定工項開始時間（起時 startDate）"" title=""設定工項開始時間（起時 startDate）"">開始時間 (起時) <span class=""sp-tip-icon"">ⓘ</span></label>
                            <input type=""datetime-local"" name=""startDate"" id=""modalWiStartDate"" value=""${escapeModalHtml(resolvedStart)}"" onchange=""applyModalWorkItemDurationPreset()"" required />
                        </div>
                        <div>
                            <label data-tip=""設定工項結束時間（迄時 endDate）：當達到迄時，系統自動轉換該工項狀態為已完成、扣掉該 Story Point，並寄信給部門管理層與 HR"" title=""設定工項結束時間（迄時 endDate）：當達到迄時，系統自動轉換該工項狀態為已完成、扣掉該 Story Point，並寄信給部門管理層與 HR"">結束時間 (迄時 - 到期自動扣點寄信) <span class=""sp-tip-icon"">ⓘ</span></label>
                            <input type=""datetime-local"" name=""endDate"" id=""modalWiEndDate"" value=""${escapeModalHtml(resolvedEnd)}"" required style=""border-color:#dc2626;font-weight:700;"" />
                        </div>
                        <div>
                            <label data-tip=""快速設定工項起訖時間範圍：手動輸入數量（預設 1）並選擇單位（天、周、月、季、年），自動依據起時計算迄時"" title=""快速設定工項起訖時間範圍：手動輸入數量（預設 1）並選擇單位（天、周、月、季、年），自動依據起時計算迄時"">起訖時間快速設定 <span class=""sp-tip-icon"">ⓘ</span></label>
                            <div style=""display:grid;grid-template-columns:1fr 1.15fr;gap:6px;align-items:center;"">
                                <div style=""display:flex;align-items:center;gap:4px;"">
                                    <span style=""font-size:12px;color:#475569;white-space:nowrap;font-weight:700;"">數量</span>
                                    <input type=""number"" id=""modalWiDurationQty"" name=""durationQuantity"" value=""${initQty}"" min=""1"" step=""1"" placeholder=""1"" oninput=""applyModalWorkItemDurationPreset()"" onchange=""applyModalWorkItemDurationPreset()"" title=""手動輸入數量（預設 1）"" style=""text-align:center;font-weight:700;padding:8px 6px;"" />
                                </div>
                                <div style=""display:flex;align-items:center;gap:4px;"">
                                    <span style=""font-size:12px;color:#475569;white-space:nowrap;font-weight:700;"">單位</span>
                                    <select id=""modalWiDurationUnit"" name=""durationUnit"" onchange=""applyModalWorkItemDurationPreset()"" title=""選擇單位：天、周、月、季、年"" style=""font-weight:700;color:#1e40af;background:#eff6ff;border-color:#93c5fd;padding:8px 8px;"">
                                        <option value=""day"" ${initUnit === 'day' ? 'selected' : ''}>天</option>
                                        <option value=""week"" ${initUnit === 'week' ? 'selected' : ''}>周</option>
                                        <option value=""month"" ${initUnit === 'month' ? 'selected' : ''}>月</option>
                                        <option value=""quarter"" ${initUnit === 'quarter' ? 'selected' : ''}>季</option>
                                        <option value=""year"" ${initUnit === 'year' ? 'selected' : ''}>年</option>
                                    </select>
                                </div>
                            </div>
                        </div>
                    </div>
                    <div class=""form-row"">
                        <div style=""grid-column: span 2;"">
                            <label>工項代表類型（選擇後自動帶入標準點數）</label>
                            <select name=""workItemTypePk"" id=""modalWiTypeSelect"" onchange=""onModalWorkItemTypeChange()"">
                                ${typeOptionsHtml}
                            </select>
                        </div>
                        <div>
                            <label>Story Point 點數</label>
                            <input type=""number"" step=""0.5"" name=""storyPoint"" id=""modalWiStoryPoint"" value=""${escapeModalHtml(pt)}"" placeholder=""留空即自動帶入代表類型點數"" />
                        </div>
                    </div>
                    <div class=""form-row"">
                        <div>
                            <label>所屬 Sprint / 階段</label>
                            <input type=""text"" name=""sprintName"" id=""modalWiSprint"" value=""${escapeModalHtml(curSprint)}"" />
                        </div>
                        <div>
                            <label>執行狀態</label>
                            <select name=""executionStatus"" id=""modalWiStatus"">
                                <option value=""todo"">待處理 (To Do)</option>
                                <option value=""inProgress"">進行中 (In Progress)</option>
                                <option value=""done"">已完成 (Done)</option>
                            </select>
                        </div>
                        <div>
                            <label>點數微調原因</label>
                            <input type=""text"" name=""adjustmentReason"" id=""modalWiReason"" value=""${escapeModalHtml(reason)}"" placeholder=""選填：說明為何與代表類型不同"" />
                        </div>
                    </div>
                    <div style=""background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;padding:10px 12px;margin-bottom:12px;"">
                        <div style=""display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:6px;margin-bottom:8px;"">
                            <span style=""font-size:12.5px;font-weight:800;color:#1e3a8a;"">👥 負責員工與點數分攤配額指派（可於儲存工項時一次完成指派）</span>
                            ${!isCreate ? `<button type=""button"" class=""btn btn-outline"" style=""padding:3px 9px;font-size:11.5px;"" onclick=""openWorkItemAssignModal('${escapeModalHtml(pk)}','${escapeModalHtml(code)}','${escapeModalHtml(title)}','${escapeModalHtml(pt)}','${escapeModalHtml(assigneeSummary || '')}')"">🔄 切換至多人分攤配額設定 Modal</button>` : ''}
                        </div>
                        ${assigneeSummary ? `<div style=""font-size:12px;color:#334155;margin-bottom:8px;"">📌 目前已指派負責員工：<strong>${escapeModalHtml(assigneeSummary)}</strong></div>` : ''}
                        <div class=""form-row"" style=""margin-bottom:0;"">
                            <div style=""grid-column: span 2;"">
                                <label>${isCreate ? '指派主要負責員工（選填）' : '新增或更新主要負責員工（選填，留空則保持原指派）'}</label>
                                <select name=""primaryEmployeePk"" id=""modalWiPrimaryEmp"">
                                    <option value=""0"">${isCreate ? '-- 暫不指派（或稍後指派） --' : '-- 保持原指派不變 --'}</option>
                                    ${empOptionsHtml}
                                </select>
                            </div>
                            <div>
                                <label>分攤比例 (%)</label>
                                <input type=""number"" step=""5"" min=""5"" max=""100"" name=""primaryAllocationPercent"" value=""100"" />
                            </div>
                        </div>
                    </div>
                    <div class=""sp-modal-footer"">
                        <button type=""button"" class=""btn btn-outline"" onclick=""closeRecordEditModal()"">✖ 取消關閉</button>
                        <div style=""display:flex;gap:8px;flex-wrap:wrap;"">
                            ${!isCreate ? `<button type=""button"" class=""btn btn-outline"" style=""color:#1d4ed8;border-color:#93c5fd;background:#eff6ff;"" onclick=""closeRecordEditModal(); triggerRevoteFromTarget('workItem', '${escapeModalHtml(pk)}', 0, '${escapeModalHtml(code)} - ${escapeModalHtml(title)}', false, 'voting');"">🔄 重新投票</button>` : ''}
                            <button type=""submit"" class=""btn btn-primary"">${isCreate ? '➕ 建立工項並完成指派' : '💾 儲存此筆工項與指派修改'}</button>
                        </div>
                    </div>
                </form>
            `;
            openRecordModalShell(
                isCreate ? '➕ 新增工項與指派負責員工' : '✏️ 單筆編輯工項紀錄与負責員工指派',
                `<span class=""badge badge-user"">${escapeModalHtml(code)}${title ? ' - ' + escapeModalHtml(title) : ' (新增工項)'}</span>`,
                bodyHtml
            );
            const kSel = document.getElementById('modalWiKind');
            if (kSel && kind) kSel.value = kind;
            const tSel = document.getElementById('modalWiTypeSelect');
            if (tSel && typePk !== undefined && typePk !== null) tSel.value = String(typePk);
            const sSel = document.getElementById('modalWiStatus');
            if (sSel && status) sSel.value = status;
        }

        function applyModalWorkItemDurationPreset() {
            const startEl = document.getElementById('modalWiStartDate');
            const endEl = document.getElementById('modalWiEndDate');
            const qtyEl = document.getElementById('modalWiDurationQty');
            const unitEl = document.getElementById('modalWiDurationUnit');
            if (!startEl || !endEl) return;

            let baseDate = startEl.value ? new Date(startEl.value) : new Date();
            if (isNaN(baseDate.getTime())) {
                baseDate = new Date();
                startEl.value = formatLocalIsoMinute(baseDate);
            } else if (!startEl.value) {
                startEl.value = formatLocalIsoMinute(baseDate);
            }

            let qty = qtyEl ? parseFloat(qtyEl.value) : 1;
            if (isNaN(qty) || qty <= 0) {
                qty = 1;
            }
            const unit = unitEl ? unitEl.value : 'day';
            const endDate = new Date(baseDate.getTime());

            if (unit === 'day') {
                endDate.setDate(endDate.getDate() + qty);
            } else if (unit === 'week') {
                endDate.setDate(endDate.getDate() + (qty * 7));
            } else if (unit === 'month') {
                endDate.setMonth(endDate.getMonth() + qty);
            } else if (unit === 'quarter') {
                endDate.setMonth(endDate.getMonth() + (qty * 3));
            } else if (unit === 'year') {
                endDate.setFullYear(endDate.getFullYear() + qty);
            } else {
                endDate.setDate(endDate.getDate() + qty);
            }

            endEl.value = formatLocalIsoMinute(endDate);
        }

        function onModalWorkItemTypeChange() {
            const sel = document.getElementById('modalWiTypeSelect');
            if (!sel) return;
            const opt = sel.options[sel.selectedIndex];
            const pt = opt ? opt.getAttribute('data-pt') : '';
            const kind = opt ? opt.getAttribute('data-kind') : '';
            if (pt && document.getElementById('modalWiStoryPoint')) {
                document.getElementById('modalWiStoryPoint').value = pt;
            }
            if (kind && document.getElementById('modalWiKind')) {
                document.getElementById('modalWiKind').value = kind;
            }
        }

        function openWorkItemAssignModal(wiPk, wiCode, wiTitle, wiPt, currentAssigneeText) {
            const empOptionsHtml = document.getElementById('assignEmployeeSelect') ? document.getElementById('assignEmployeeSelect').innerHTML : '';
            const wiSelectOptionsHtml = document.getElementById('assignWorkItemSelect') ? document.getElementById('assignWorkItemSelect').innerHTML : '';
            const hasFixedWi = wiPk && String(wiPk).trim() !== '' && String(wiPk) !== '0';
            const targetWiSectionHtml = hasFixedWi
                ? `<input type=""hidden"" name=""workItemPk"" value=""${escapeModalHtml(wiPk)}"" />
                   <div style=""background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;padding:10px 14px;margin-bottom:14px;font-size:13px;color:#1e3a8a;"">
                       <div><strong>📌 目標工項：</strong>${escapeModalHtml(wiCode)} - ${escapeModalHtml(wiTitle)} （總點數：<strong>${escapeModalHtml(wiPt || '0')} pt</strong>）</div>
                       <div style=""margin-top:4px;""><strong>👥 目前負責員工與配額：</strong>${escapeModalHtml(currentAssigneeText || '尚未指派')}</div>
                   </div>`
                : `<div class=""form-row"">
                       <div style=""grid-column: span 3;"">
                           <label>選擇要指派或調整分攤配額的目標工項</label>
                           <select name=""workItemPk"" id=""modalAssignWiSelect"" required style=""border-color:#2563eb;font-weight:700;background:#eff6ff;"">
                               ${wiSelectOptionsHtml}
                           </select>
                       </div>
                   </div>`;
            const bodyHtml = `
                <form method=""post"" action=""/workitem/assign"">
                    ${targetWiSectionHtml}
                    <div class=""form-row"">
                        <div style=""grid-column: span 3;"">
                            <label>選擇要賦予或調整配額的員工</label>
                            <select name=""employeePk"" id=""modalAssignEmpSelect"" required>
                                ${empOptionsHtml}
                            </select>
                        </div>
                    </div>
                    <div class=""form-row"">
                        <div>
                            <label>指派動作</label>
                            <select name=""actionType"">
                                <option value=""upsert"">➕ 新增或更新該員工分攤比例</option>
                                <option value=""replaceSingle"">🎯 改由該員工單獨 100% 負責</option>
                                <option value=""remove"">🗑️ 移除該員工在此工項的指派</option>
                            </select>
                        </div>
                        <div>
                            <label>負責角色</label>
                            <input type=""text"" name=""assignmentRole"" value=""主要負責"" />
                        </div>
                        <div>
                            <label>分攤比例 (%)</label>
                            <input type=""number"" step=""5"" name=""allocationPercent"" value=""100"" />
                        </div>
                    </div>
                    <div class=""form-row"">
                        <div style=""grid-column: span 3;"">
                            <label>指派備註</label>
                            <input type=""text"" name=""assignmentNote"" placeholder=""例如：負責後端 Stored Procedure 與單元測試"" />
                        </div>
                    </div>
                    <div class=""sp-modal-footer"">
                        <button type=""button"" class=""btn btn-outline"" onclick=""closeRecordEditModal()"">✖ 取消關閉</button>
                        <button type=""submit"" class=""btn btn-success"">✅ 儲存員工指派與分攤配額</button>
                    </div>
                </form>
            `;
            openRecordModalShell(
                '👥 修改工項之賦予員工與分攤配額',
                `<span class=""badge badge-user"">${hasFixedWi ? escapeModalHtml(wiCode) : '選擇工項指派'}</span>`,
                bodyHtml
            );
        }

        function openWorkItemKindEditModal(pk, code, name, icon, color, sortOrder, desc, tabSuffix) {
            const isCreate = !pk || String(pk).trim() === '' || String(pk) === '0';
            const sfx = tabSuffix || 'workitems';
            const bodyHtml = `
                <form method=""post"" action=""/workitem/kind/save"">
                    <input type=""hidden"" name=""workItemKindPk"" value=""${escapeModalHtml(pk)}"" />
                    <input type=""hidden"" name=""returnTab"" value=""${escapeModalHtml(sfx)}"" />
                    <div class=""form-row"">
                        <div>
                            <label>類型代碼 (kindCode)</label>
                            <input type=""text"" name=""kindCode"" value=""${escapeModalHtml(code)}"" placeholder=""例如：research"" required />
                        </div>
                        <div>
                            <label>類型顯示名稱 (kindName)</label>
                            <input type=""text"" name=""kindName"" value=""${escapeModalHtml(name)}"" placeholder=""例如：研發創新"" required />
                        </div>
                        <div>
                            <label>圖示 Emoji</label>
                            <input type=""text"" name=""badgeIcon"" value=""${escapeModalHtml(icon || '📌')}"" required />
                        </div>
                    </div>
                    <div class=""form-row"">
                        <div>
                            <label>徽章主題色 (badgeColor)</label>
                            <select name=""badgeColor"" id=""modalWikColor"">
                                <option value=""blue"">🔵 藍色 (blue - 專案)</option>
                                <option value=""green"">🟢 綠色 (green - 維運)</option>
                                <option value=""amber"">🟡 琥珀金 (amber - 顧問時數)</option>
                                <option value=""purple"">🟣 紫色 (purple - 研發創新)</option>
                                <option value=""rose"">🔴 玫瑰紅 (rose - 緊急支援)</option>
                                <option value=""teal"">🩵 藍綠色 (teal - 客戶導入)</option>
                                <option value=""slate"">⚪ 灰藍色 (slate - 一般行政)</option>
                            </select>
                        </div>
                        <div>
                            <label>排序順序 (sortOrder)</label>
                            <input type=""number"" name=""sortOrder"" value=""${escapeModalHtml(sortOrder || 40)}"" step=""10"" />
                        </div>
                        <div>
                            <label>類型適用範圍與說明</label>
                            <input type=""text"" name=""description"" value=""${escapeModalHtml(desc || '')}"" placeholder=""說明此工項類型的定義與使用情境"" />
                        </div>
                    </div>
                    <div class=""sp-modal-footer"">
                        <button type=""button"" class=""btn btn-outline"" onclick=""closeRecordEditModal()"">✖ 取消關閉</button>
                        <button type=""submit"" class=""btn btn-primary"">💾 儲存此筆工項類型</button>
                    </div>
                </form>
            `;
            openRecordModalShell(
                isCreate ? '➕ 新增工項類型 (tbWorkItemKind)' : '✏️ 單筆編輯工項類型 (tbWorkItemKind)',
                `<span class=""badge badge-user"">${escapeModalHtml(icon || '📌')} ${name ? escapeModalHtml(name) + ' (' + escapeModalHtml(code) + ')' : '新工項類型'}</span>`,
                bodyHtml
            );
            const cSel = document.getElementById('modalWikColor');
            if (cSel && color) cSel.value = color;
        }

        function openWorkItemTypeEditModal(pk, code, name, cat, desc, pt, kind) {
            const isCreate = !pk || String(pk).trim() === '' || String(pk) === '0';
            const kindOptionsHtml = document.getElementById('typeKind')
                ? document.getElementById('typeKind').innerHTML
                : (document.getElementById('wiKindSelect') ? document.getElementById('wiKindSelect').innerHTML : '<option value=""project"">🚀 專案 (project)</option>');
            const bodyHtml = `
                <form method=""post"" action=""/workitem/type/save"">
                    <input type=""hidden"" name=""workItemTypePk"" value=""${escapeModalHtml(pk)}"" />
                    <div class=""form-row"">
                        <div>
                            <label>代表類型代碼</label>
                            <input type=""text"" name=""typeCode"" value=""${escapeModalHtml(code)}"" placeholder=""例如：TYPE_API_DEV"" required />
                        </div>
                        <div>
                            <label>預設工項類型</label>
                            <select name=""defaultWorkItemKind"" id=""modalTypeKind"" required style=""border-color:#2563eb;font-weight:700;background:#eff6ff;"">
                                ${kindOptionsHtml}
                            </select>
                        </div>
                        <div>
                            <label>代表類型名稱</label>
                            <input type=""text"" name=""typeName"" value=""${escapeModalHtml(name)}"" placeholder=""例如：標準 RESTful API 開發"" required />
                        </div>
                    </div>
                    <div class=""form-row"">
                        <div>
                            <label>分類群組</label>
                            <input type=""text"" name=""categoryName"" value=""${escapeModalHtml(cat || '一般分類')}"" required />
                        </div>
                        <div>
                            <label>標準 Story Point 點數</label>
                            <input type=""number"" step=""0.5"" name=""standardStoryPoint"" value=""${escapeModalHtml(pt)}"" placeholder=""留空代表待盲投估算"" />
                        </div>
                        <div>
                            <label>類型規模與複雜度說明</label>
                            <input type=""text"" name=""description"" value=""${escapeModalHtml(desc || '')}"" placeholder=""描述開發複雜度與驗收標準"" />
                        </div>
                    </div>
                    <div style=""margin:8px 0 14px 0;"">
                        <label style=""display:inline-flex;align-items:center;gap:6px;cursor:pointer;font-size:13px;"">
                            <input type=""checkbox"" name=""syncExistingOpenWorkItem"" value=""true"" checked style=""width:auto;"" />
                            <span>儲存後同步將標準點數與預設工項類型套用至所有未結案工項</span>
                        </label>
                    </div>
                    <div class=""sp-modal-footer"">
                        <button type=""button"" class=""btn btn-outline"" onclick=""closeRecordEditModal()"">✖ 取消關閉</button>
                        <div style=""display:flex;gap:8px;flex-wrap:wrap;"">
                            ${!isCreate ? `<button type=""button"" class=""btn btn-outline"" style=""color:#1d4ed8;border-color:#93c5fd;background:#eff6ff;"" onclick=""closeRecordEditModal(); triggerRevoteFromTarget('workItemType', '${escapeModalHtml(pk)}', 0, '${escapeModalHtml(code)} - ${escapeModalHtml(name)}', false, 'voting');"">🔄 重新投票</button>` : ''}
                            <button type=""submit"" class=""btn btn-primary"">💾 儲存此筆代表類型</button>
                        </div>
                    </div>
                </form>
            `;
            openRecordModalShell(
                isCreate ? '➕ 新增工項代表類型 (tbWorkItemType)' : '✏️ 單筆編輯工項代表類型 (tbWorkItemType)',
                `<span class=""badge badge-user"">${code ? escapeModalHtml(code) + ' - ' + escapeModalHtml(name) : '新代表類型'}</span>`,
                bodyHtml
            );
            const kSel = document.getElementById('modalTypeKind');
            if (kSel && kind) kSel.value = kind;
        }

        function openEmployeeEditModal(pk, code, name, acc, mail, title, deptPk, role, isActiveStr, isDeptMgrStr, mgrPkStr) {
            const isCreate = !pk || String(pk).trim() === '' || String(pk) === '0';
            const deptOptionsHtml = document.getElementById('empDept') ? document.getElementById('empDept').innerHTML : '<option value=""1"">預設部門</option>';
            const mgrOptionsHtml = document.getElementById('empManagerPk') ? document.getElementById('empManagerPk').innerHTML : '<option value="""">🌳 自動依所屬部門之部門主管掛載</option>';
            const bodyHtml = `
                <form method=""post"" action=""/employee/save"">
                    <input type=""hidden"" name=""employeePk"" value=""${escapeModalHtml(pk)}"" />
                    <div class=""form-row"">
                        <div>
                            <label>員工編號</label>
                            <input type=""text"" name=""employeeCode"" value=""${escapeModalHtml(code)}"" required />
                        </div>
                        <div>
                            <label>員工姓名</label>
                            <input type=""text"" name=""employeeName"" value=""${escapeModalHtml(name)}"" placeholder=""例如：林志豪 (Leo)"" required />
                        </div>
                        <div>
                            <label>所屬部門</label>
                            <select name=""departmentPk"" id=""modalEmpDept"">
                                ${deptOptionsHtml}
                            </select>
                        </div>
                    </div>
                    <div class=""form-row"">
                        <div>
                            <label>登入帳號</label>
                            <input type=""text"" name=""loginAccount"" value=""${escapeModalHtml(acc)}"" placeholder=""例如：leo.lin"" required />
                        </div>
                        <div>
                            <label>${isCreate ? '登入密碼（未填預設 pass123）' : '重設登入密碼（留空代表不更改密碼）'}</label>
                            <div style=""position:relative;display:flex;align-items:center;"">
                                <input type=""password"" name=""plainPassword"" id=""modalEmpPwd"" placeholder=""${isCreate ? '未填預設 pass123' : '留空代表不更改密碼'}"" style=""padding-right:64px;"" />
                                <button type=""button"" id=""btnToggleModalEmpPwd"" onclick=""togglePasswordVisibilityById('modalEmpPwd','btnToggleModalEmpPwd')"" style=""position:absolute;right:6px;background:#e2e8f0;border:none;border-radius:5px;padding:3px 7px;font-size:11.5px;font-weight:700;color:#334155;cursor:pointer;"">👁️ 顯示</button>
                            </div>
                        </div>
                        <div>
                            <label>系統角色權限</label>
                            <select name=""roleName"" id=""modalEmpRole"">
                                <option value=""participant"">🙋 參與者 (僅可投票、僅可看自己的工項與分數)</option>
                                <option value=""admin"">👑 管理者 (可修改工項指派、員工帳密與主持定案)</option>
                            </select>
                        </div>
                    </div>
                    <div class=""form-row"">
                        <div>
                            <label>電子郵件</label>
                            <input type=""text"" name=""emailAddress"" value=""${escapeModalHtml(mail || 'user@company.com')}"" required />
                        </div>
                        <div>
                            <label>職級 / 職稱</label>
                            <input type=""text"" name=""jobTitle"" value=""${escapeModalHtml(title || '資深工程師')}"" list=""empJobTitleDatalist"" required />
                        </div>
                        <div>
                            <label>帳號狀態</label>
                            <select name=""isActive"" id=""modalEmpActive"">
                                <option value=""true"">🟢 啟用中 (允許登入與參與投票)</option>
                                <option value=""false"">🚫 已停用 (禁止登入系統)</option>
                            </select>
                        </div>
                    </div>
                    <div class=""form-row"" style=""background:#f8fafc;border:1px solid #bfdbfe;border-radius:8px;padding:10px 12px;"">
                        <div>
                            <label>是否為部門主管 (同部門可多位主管)</label>
                            <select name=""isDepartmentManager"" id=""modalEmpIsDeptMgr"">
                                <option value=""false"">👨‍💻 否 (一般部門同仁)</option>
                                <option value=""true"">👑 是 (部門主管 / 人資主管)</option>
                            </select>
                        </div>
                        <div style=""grid-column: span 2;"">
                            <label>上一層主管 (樹狀階層)</label>
                            <select name=""managerEmployeePk"" id=""modalEmpManagerPk"">
                                ${mgrOptionsHtml}
                            </select>
                        </div>
                    </div>
                    <div class=""sp-modal-footer"">
                        <button type=""button"" class=""btn btn-outline"" onclick=""closeRecordEditModal()"">✖ 取消關閉</button>
                        <button type=""submit"" class=""btn btn-primary"">${isCreate ? '➕ 新增儲存員工與登入帳密' : '💾 儲存此筆員工與登入帳密修改'}</button>
                    </div>
                </form>
            `;
            openRecordModalShell(
                isCreate ? '➕ 新增員工帳號、部門主管與樹狀階層' : '✏️ 單筆編輯員工資料、部門主管與樹狀階層',
                `<span class=""badge badge-user"">${escapeModalHtml(code)}${name ? ' - ' + escapeModalHtml(name) + ' (' + escapeModalHtml(acc) + ')' : ' (新員工帳號)'}</span>`,
                bodyHtml
            );
            if (document.getElementById('modalEmpDept') && deptPk) document.getElementById('modalEmpDept').value = String(deptPk);
            if (document.getElementById('modalEmpRole') && role) document.getElementById('modalEmpRole').value = role;
            if (document.getElementById('modalEmpActive')) document.getElementById('modalEmpActive').value = (isActiveStr === 'false' || isActiveStr === false) ? 'false' : 'true';
            if (document.getElementById('modalEmpIsDeptMgr')) document.getElementById('modalEmpIsDeptMgr').value = (isDeptMgrStr === 'true' || isDeptMgrStr === true) ? 'true' : 'false';
            if (document.getElementById('modalEmpManagerPk')) document.getElementById('modalEmpManagerPk').value = mgrPkStr ? String(mgrPkStr) : '';
        }

        function openDepartmentEditModal(deptPk, deptCode, deptName, mgrName, deptNote) {
            const isCreate = !deptPk || String(deptPk).trim() === '' || String(deptPk) === '0';
            const bodyHtml = `
                <form method=""post"" action=""/employee/department/save"">
                    <input type=""hidden"" name=""departmentPk"" value=""${escapeModalHtml(deptPk)}"" />
                    <div class=""form-row"">
                        <div>
                            <label>部門代碼 (departmentCode)</label>
                            <input type=""text"" name=""departmentCode"" value=""${escapeModalHtml(deptCode)}"" placeholder=""例如：DEPT_DATA_AI"" required />
                        </div>
                        <div>
                            <label>部門名稱 (departmentName)</label>
                            <input type=""text"" name=""departmentName"" value=""${escapeModalHtml(deptName)}"" placeholder=""例如：數據科學與 AI 研發部"" required />
                        </div>
                        <div>
                            <label>部門主管姓名</label>
                            <input type=""text"" name=""managerName"" value=""${escapeModalHtml(mgrName || '')}"" placeholder=""例如：吳經理"" />
                        </div>
                    </div>
                    <div class=""form-row"">
                        <div style=""grid-column: span 3;"">
                            <label>部門職掌備註</label>
                            <input type=""text"" name=""departmentNote"" value=""${escapeModalHtml(deptNote || '')}"" placeholder=""描述部門負責業務範圍"" />
                        </div>
                    </div>
                    <div class=""sp-modal-footer"">
                        <button type=""button"" class=""btn btn-outline"" onclick=""closeRecordEditModal()"">✖ 取消關閉</button>
                        <button type=""submit"" class=""btn btn-primary"">💾 儲存此筆部門設定</button>
                    </div>
                </form>
            `;
            openRecordModalShell(
                isCreate ? '🏢 新增組織部門 (tbDepartment)' : '🏢 單筆編輯部門資料 (tbDepartment)',
                `<span class=""badge badge-user"">${deptCode ? escapeModalHtml(deptCode) + ' - ' + escapeModalHtml(deptName) : '新部門'}</span>`,
                bodyHtml
            );
        }

        function applyModalSessionPreset(minPt, maxPt, spread, sec, rule) {
            if (document.getElementById('modalSetupMinPt')) document.getElementById('modalSetupMinPt').value = minPt;
            if (document.getElementById('modalSetupMaxPt')) document.getElementById('modalSetupMaxPt').value = maxPt;
            if (document.getElementById('modalSetupSpread')) document.getElementById('modalSetupSpread').value = spread;
            if (document.getElementById('modalSetupTimeSec')) document.getElementById('modalSetupTimeSec').value = sec;
            if (document.getElementById('modalSetupConsensusRule') && rule) document.getElementById('modalSetupConsensusRule').value = rule;
        }

        function toggleAllModalSessionParticipants(checkedState) {
            document.querySelectorAll('#spRecordEditModal input[name=""participantPks""]').forEach(cb => {
                cb.checked = checkedState;
            });
        }

        function onModalSessionTargetOptionChange() {
            const tKind = document.getElementById('modalSetupTargetKind')?.value || 'workItemType';
            const sel = document.getElementById(tKind === 'workItem' ? 'modalSetupWorkItemPk' : 'modalSetupWorkItemTypePk');
            const titleInput = document.getElementById('modalSetupTitleInput');
            if (!sel || !titleInput) return;
            const opt = sel.options[sel.selectedIndex];
            if (opt && (!titleInput.value || titleInput.value.indexOf('【') === 0)) {
                titleInput.value = (tKind === 'workItem' ? '【工項點數盲投】' : '【代表類型點數盲投】') + opt.text.split('(')[0].trim();
            }
        }

        function openSessionSetupEditModal(sessionPk, targetKindOverride, targetPkOverride, isRevoteMode) {
            let sPk = sessionPk ? Number(sessionPk) : 0;
            if (sPk <= 0 && targetPkOverride && typeof sessionConfigMap !== 'undefined') {
                const matchKey = Object.keys(sessionConfigMap).find(k => {
                    const item = sessionConfigMap[k];
                    if (!item) return false;
                    if (targetKindOverride === 'workItem') return Number(item.workItemPk) === Number(targetPkOverride);
                    return Number(item.workItemTypePk) === Number(targetPkOverride);
                });
                if (matchKey && isRevoteMode) {
                    sPk = Number(matchKey);
                }
            }
            const cfg = (sPk > 0 && typeof sessionConfigMap !== 'undefined' && sessionConfigMap[sPk]) ? sessionConfigMap[sPk] : null;
            const tKind = targetKindOverride || (cfg ? cfg.targetKind : 'workItemType');
            const wiTypePk = (tKind === 'workItemType' && targetPkOverride) ? targetPkOverride : (cfg ? cfg.workItemTypePk : 0);
            const wiPk = (tKind === 'workItem' && targetPkOverride) ? targetPkOverride : (cfg ? cfg.workItemPk : 0);
            const typeOptionsHtml = document.getElementById('setupWorkItemTypePk') ? document.getElementById('setupWorkItemTypePk').innerHTML : '';
            const wiOptionsHtml = document.getElementById('setupWorkItemPk') ? document.getElementById('setupWorkItemPk').innerHTML : '';
            const deckOptionsHtml = document.getElementById('setupCardDeckPk') ? document.getElementById('setupCardDeckPk').innerHTML : '<option value=""1"">標準 Fibonacci 牌組</option>';

            let defaultTitle = cfg ? (cfg.title || '') : '';
            if (!defaultTitle) {
                const srcSel = document.getElementById(tKind === 'workItem' ? 'setupWorkItemPk' : 'setupWorkItemTypePk');
                if (srcSel) {
                    const matchOpt = targetPkOverride
                        ? Array.from(srcSel.options).find(o => String(o.value) === String(targetPkOverride))
                        : srcSel.options[0];
                    if (matchOpt) {
                        defaultTitle = (tKind === 'workItem' ? '【工項點數盲投】' : '【代表類型點數盲投】') + matchOpt.text.split('(')[0].trim();
                    }
                }
            }

            const participantPks = cfg && Array.isArray(cfg.participants) ? cfg.participants : [];
            const allParticipantCheckboxes = Array.from(document.querySelectorAll('.setup-participant-cb'));
            let participantHtml = '';
            if (allParticipantCheckboxes.length > 0) {
                participantHtml = allParticipantCheckboxes.map(cb => {
                    const empVal = parseInt(cb.value, 10);
                    const isChecked = cfg ? (participantPks.indexOf(empVal) !== -1) : cb.checked;
                    const labelEl = cb.closest('label');
                    const text = labelEl ? (labelEl.innerText || labelEl.textContent || '').trim() : ('員工 #' + empVal);
                    return `<label style=""display:inline-flex;align-items:center;gap:6px;padding:5px 10px;border:1px solid #cbd5e1;border-radius:6px;background:#fff;font-size:12.5px;cursor:pointer;"">
                        <input type=""checkbox"" name=""participantPks"" value=""${empVal}"" ${isChecked ? 'checked' : ''} style=""width:auto;"" />
                        <span>${escapeModalHtml(text)}</span>
                    </label>`;
                }).join('');
            }

            const bodyHtml = `
                <form method=""post"" action=""/voting/create"">
                    <input type=""hidden"" name=""votingSessionPk"" value=""${sPk > 0 ? sPk : ''}"" />
                    <input type=""hidden"" name=""submitMode"" id=""modalSetupSubmitMode"" value=""saveAndGoVoting"" />
                    <div style=""display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:6px;background:#eff6ff;border:1px solid #bfdbfe;border-radius:8px;padding:8px 12px;margin-bottom:10px;"">
                        <span style=""font-size:12.5px;font-weight:800;color:#1e3a8a;"">⚡ 一鍵套用盲投規則範本：</span>
                        <div style=""display:flex;gap:6px;flex-wrap:wrap;"">
                            <button type=""button"" class=""btn btn-outline"" style=""padding:4px 9px;font-size:12px;"" onclick=""applyModalSessionPreset(0.5, 21, 5, 180, 'nearestFibonacci')"">📐 標準 Scrum (0.5~21pt / 180秒)</button>
                            <button type=""button"" class=""btn btn-outline"" style=""padding:4px 9px;font-size:12px;"" onclick=""applyModalSessionPreset(1, 13, 3, 60, 'nearestFibonacci')"">🚀 快速閃電 (1~13pt / 60秒)</button>
                            <button type=""button"" class=""btn btn-outline"" style=""padding:4px 9px;font-size:12px;"" onclick=""applyModalSessionPreset(0.5, 34, 3, 300, 'trimmedMean')"">🏛️ 嚴謹評審 (0.5~34pt / 300秒)</button>
                        </div>
                    </div>
                    <div style=""margin-bottom:12px;padding:8px 12px;background:#fffbeb;border:1px solid #fde68a;border-radius:8px;display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:8px;"">
                        <label style=""display:inline-flex;align-items:center;gap:8px;cursor:pointer;margin:0;font-weight:700;color:#92400e;font-size:13px;"">
                            <input type=""checkbox"" name=""resetCurrentRoundVotes"" id=""modalSetupResetVotes"" value=""true"" ${(isRevoteMode || sPk > 0) ? 'checked' : ''} style=""width:auto;"" />
                            <span>🔄 重新投票模式：儲存時一併重啟盲投回合並清空既有出牌紀錄（讓全體成員重新投票）</span>
                        </label>
                    </div>
                    <div class=""form-row"">
                        <div>
                            <label>估算目標類型</label>
                            <select name=""targetKind"" id=""modalSetupTargetKind"" onchange=""const isWi=this.value==='workItem'; document.getElementById('modalSetupTypeBox').style.display=isWi?'none':''; document.getElementById('modalSetupWiBox').style.display=isWi?'':'none'; onModalSessionTargetOptionChange();"">
                                <option value=""workItemType"" ${tKind === 'workItemType' ? 'selected' : ''}>🏷️ 工項代表類型 (tbWorkItemType)</option>
                                <option value=""workItem"" ${tKind === 'workItem' ? 'selected' : ''}>📋 實際工項 (tbWorkItem)</option>
                            </select>
                        </div>
                        <div id=""modalSetupTypeBox"" style=""grid-column: span 2; ${tKind === 'workItem' ? 'display:none;' : ''}"">
                            <label>選擇工項代表類型</label>
                            <select name=""workItemTypePk"" id=""modalSetupWorkItemTypePk"" onchange=""onModalSessionTargetOptionChange()"">${typeOptionsHtml}</select>
                        </div>
                        <div id=""modalSetupWiBox"" style=""grid-column: span 2; ${tKind === 'workItem' ? '' : 'display:none;'}"">
                            <label>選擇實際工項</label>
                            <select name=""workItemPk"" id=""modalSetupWorkItemPk"" onchange=""onModalSessionTargetOptionChange()"">${wiOptionsHtml}</select>
                        </div>
                    </div>
                    <div class=""form-row"">
                        <div style=""grid-column: span 2;"">
                            <label>盲投議程標題</label>
                            <input type=""text"" name=""sessionTitle"" id=""modalSetupTitleInput"" value=""${escapeModalHtml(defaultTitle)}"" placeholder=""請輸入議程標題"" required />
                        </div>
                        <div>
                            <label>使用撲克牌組</label>
                            <select name=""cardDeckPk"" id=""modalSetupDeckPk"">${deckOptionsHtml}</select>
                        </div>
                    </div>
                    <div class=""form-row"">
                        <div>
                            <label>最小點數下限 (minStoryPoint)</label>
                            <input type=""number"" step=""0.5"" name=""minStoryPoint"" id=""modalSetupMinPt"" value=""${cfg ? cfg.minStoryPoint : 0.5}"" />
                        </div>
                        <div>
                            <label>最大點數上限 (maxStoryPoint)</label>
                            <input type=""number"" step=""0.5"" name=""maxStoryPoint"" id=""modalSetupMaxPt"" value=""${cfg ? cfg.maxStoryPoint : 21}"" />
                        </div>
                        <div>
                            <label>分歧容許差距 (maxAllowedSpread)</label>
                            <input type=""number"" step=""0.5"" name=""maxAllowedSpread"" id=""modalSetupSpread"" value=""${cfg ? cfg.maxAllowedSpread : 5}"" />
                        </div>
                    </div>
                    <div class=""form-row"">
                        <div>
                            <label>共識收斂演算法</label>
                            <select name=""consensusRule"" id=""modalSetupConsensusRule"">
                                <option value=""nearestFibonacci"">📐 最接近費氏牌值 (Nearest Fibonacci)</option>
                                <option value=""average"">📊 算術平均數 (Arithmetic Mean)</option>
                                <option value=""median"">🎯 中位數 (Median)</option>
                                <option value=""trimmedMean"">✂️ 去頭尾平均數 (Trimmed Mean)</option>
                            </select>
                        </div>
                        <div>
                            <label>回合思考時限 (秒)</label>
                            <input type=""number"" step=""30"" name=""roundTimeLimitSecond"" id=""modalSetupTimeSec"" value=""${cfg ? cfg.roundTimeLimitSecond : 180}"" />
                        </div>
                        <div>
                            <label>備註/目標提示</label>
                            <input type=""text"" name=""roundGoalNote"" value=""${escapeModalHtml(cfg ? cfg.roundGoalNote : '')}"" placeholder=""選填：本回合估算重點提示"" />
                        </div>
                    </div>
                    <div style=""margin:8px 0;"">
                        <div style=""display:flex;align-items:center;justify-content:space-between;margin-bottom:6px;"">
                            <label style=""font-weight:700;margin:0;"">👥 受邀參與盲投成員</label>
                            <div style=""display:flex;gap:6px;"">
                                <button type=""button"" class=""btn btn-outline"" style=""padding:2px 8px;font-size:11.5px;"" onclick=""toggleAllModalSessionParticipants(true)"">✅ 全選成員</button>
                                <button type=""button"" class=""btn btn-outline"" style=""padding:2px 8px;font-size:11.5px;"" onclick=""toggleAllModalSessionParticipants(false)"">⬜ 全不選</button>
                            </div>
                        </div>
                        <div style=""display:flex;flex-wrap:wrap;gap:6px;max-height:140px;overflow-y:auto;padding:8px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:8px;"">
                            ${participantHtml}
                        </div>
                    </div>
                    <div class=""sp-modal-footer"">
                        <button type=""button"" class=""btn btn-outline"" onclick=""closeRecordEditModal()"">✖ 取消關閉</button>
                        <div style=""display:flex;gap:8px;flex-wrap:wrap;"">
                            <button type=""submit"" class=""btn btn-outline"" onclick=""document.getElementById('modalSetupSubmitMode').value='saveStaySetup'"">💾 僅儲存於回合設定</button>
                            <button type=""submit"" class=""btn btn-primary"" onclick=""document.getElementById('modalSetupSubmitMode').value='saveAndGoVoting'"">${isRevoteMode ? '🔄 儲存設定並啟動重新投票' : (sPk > 0 ? '🚀 儲存更新並前往盲投大廳' : '🚀 建立議程並立即前往盲投大廳')}</button>
                        </div>
                    </div>
                </form>
            `;
            openRecordModalShell(
                isRevoteMode ? '🔄 重新投票回合設定' : (sPk > 0 ? '🃏 單筆編輯盲投議程回合設定' : '🃏 快速發起多人盲投估算議程'),
                `<span class=""badge badge-user"">${sPk > 0 ? 'Session #' + sPk : '新盲投議程'}</span>`,
                bodyHtml
            );
            if (document.getElementById('modalSetupWorkItemTypePk') && wiTypePk) document.getElementById('modalSetupWorkItemTypePk').value = String(wiTypePk);
            if (document.getElementById('modalSetupWorkItemPk') && wiPk) document.getElementById('modalSetupWorkItemPk').value = String(wiPk);
            if (cfg && document.getElementById('modalSetupConsensusRule')) document.getElementById('modalSetupConsensusRule').value = cfg.consensusRule || 'nearestFibonacci';
        }

        function triggerRevoteFromTarget(targetKind, targetPk, sessionPk, displayTitle, resetCurrentRoundOnly, returnTab) {
            const sPk = sessionPk ? Number(sessionPk) : 0;
            const tPk = targetPk ? Number(targetPk) : 0;
            const actionName = resetCurrentRoundOnly ? 'resetRound' : 'revote';
            const rTab = returnTab || 'voting';
            const payload = {
                votingSessionPk: sPk > 0 ? String(sPk) : '0',
                controlAction: actionName,
                submitAction: actionName,
                targetKind: targetKind === 'workItem' ? 'workItem' : (targetKind === 'workItemType' ? 'workItemType' : ''),
                workItemTypePk: targetKind === 'workItemType' && tPk > 0 ? String(tPk) : '',
                workItemPk: targetKind === 'workItem' && tPk > 0 ? String(tPk) : '',
                roundDiscussionNote: resetCurrentRoundOnly
                    ? ('管理者針對「' + (displayTitle || '此議程') + '」清空本回合出牌紀錄並重新投票')
                    : ('管理者針對「' + (displayTitle || '此項目') + '」發起重新投票（開啟新一輪盲投）'),
                returnTab: rTab
            };
            submitDynamicPostAction('/voting/revote', payload);
        }

        function openRowDetailReadonlyModal(tr) {
            if (!tr) return;
            const table = tr.closest('table');
            const state = table ? spTableStateMap.get(table) : null;
            const headers = state ? state.colNames : Array.from(table?.querySelectorAll('thead th') || []).map(th => (th.innerText || th.textContent || '').trim());
            const cells = Array.from(tr.children);
            const rowsHtml = cells.map((td, idx) => {
                const colName = headers[idx] || ('欄位 ' + (idx + 1));
                const val = extractCellFilterDisplayText(td) || '-';
                return `<div style=""display:grid;grid-template-columns:160px 1fr;gap:10px;padding:8px 10px;border-bottom:1px solid #e2e8f0;font-size:13px;"">
                    <div style=""font-weight:700;color:#1e3a8a;"">${escapeModalHtml(colName)}</div>
                    <div style=""color:#0f172a;word-break:break-word;"">${escapeModalHtml(val)}</div>
                </div>`;
            }).join('');

            const bodyHtml = `
                <div style=""border:1px solid #cbd5e1;border-radius:8px;background:#f8fafc;overflow:hidden;"">
                    ${rowsHtml}
                </div>
                <div class=""sp-modal-footer"">
                    <button type=""button"" class=""btn btn-outline"" onclick=""closeRecordEditModal()"">✖ 關閉視窗</button>
                </div>
            `;
            openRecordModalShell('🔍 單筆紀錄完整明細檢視', '<span class=""badge badge-user"">唯讀檢視</span>', bodyHtml);
        }

        function triggerDefaultRowEditOrView(tr) {
            if (!tr) return;
            const rowKind = tr.getAttribute('data-row-kind') || 'generic';
            if (isCurrentUserAdmin) {
                if (rowKind === 'workitem') {
                    openWorkItemEditModal(
                        tr.getAttribute('data-wi-pk') || '',
                        tr.getAttribute('data-wi-code') || '',
                        tr.getAttribute('data-wi-title') || '',
                        tr.getAttribute('data-wi-type-pk') || '0',
                        tr.getAttribute('data-wi-pt') || '',
                        tr.getAttribute('data-wi-status') || 'todo',
                        tr.getAttribute('data-wi-reason') || '',
                        tr.getAttribute('data-wi-kind') || 'project',
                        tr.getAttribute('data-wi-start') || '',
                        tr.getAttribute('data-wi-end') || '',
                        tr.getAttribute('data-wi-sprint') || '',
                        false,
                        tr.getAttribute('data-wi-assignee') || ''
                    );
                    return;
                } else if (rowKind === 'workitemKind') {
                    openWorkItemKindEditModal(
                        tr.getAttribute('data-kind-pk') || '',
                        tr.getAttribute('data-kind-code') || '',
                        tr.getAttribute('data-kind-name') || '',
                        tr.getAttribute('data-kind-icon') || '📌',
                        tr.getAttribute('data-kind-color') || 'blue',
                        tr.getAttribute('data-kind-sort') || '40',
                        tr.getAttribute('data-kind-desc') || '',
                        tr.getAttribute('data-tab-suffix') || 'workitems'
                    );
                    return;
                } else if (rowKind === 'workitemType') {
                    openWorkItemTypeEditModal(
                        tr.getAttribute('data-type-pk') || '',
                        tr.getAttribute('data-type-code') || '',
                        tr.getAttribute('data-type-name') || '',
                        tr.getAttribute('data-type-cat') || '',
                        tr.getAttribute('data-type-desc') || '',
                        tr.getAttribute('data-type-pt') || '',
                        tr.getAttribute('data-type-kind') || 'project'
                    );
                    return;
                } else if (rowKind === 'employee') {
                    openEmployeeEditModal(
                        tr.getAttribute('data-emp-pk') || '',
                        tr.getAttribute('data-emp-code') || '',
                        tr.getAttribute('data-emp-name') || '',
                        tr.getAttribute('data-emp-acc') || '',
                        tr.getAttribute('data-emp-mail') || '',
                        tr.getAttribute('data-emp-title') || '',
                        tr.getAttribute('data-emp-dept-pk') || '1',
                        tr.getAttribute('data-emp-role') || 'participant',
                        tr.getAttribute('data-emp-active') || 'true',
                        tr.getAttribute('data-emp-ismgr') || 'false',
                        tr.getAttribute('data-emp-mgrpk') || ''
                    );
                    return;
                } else if (rowKind === 'department') {
                    openDepartmentEditModal(
                        tr.getAttribute('data-dept-pk') || '',
                        tr.getAttribute('data-dept-code') || '',
                        tr.getAttribute('data-dept-name') || '',
                        tr.getAttribute('data-dept-mgr') || '',
                        tr.getAttribute('data-dept-note') || ''
                    );
                    return;
                } else if (rowKind === 'session') {
                    const sPk = tr.getAttribute('data-session-pk') || '';
                    if (sPk) {
                        openSessionSetupEditModal(sPk);
                        return;
                    }
                }
            }
            openRowDetailReadonlyModal(tr);
        }

        function initTableRowContextMenu() {
            if (!spContextMenuEl) {
                spContextMenuEl = document.createElement('div');
                spContextMenuEl.id = 'spRowContextMenu';
                spContextMenuEl.className = 'sp-context-menu';
                document.body.appendChild(spContextMenuEl);
            }

            // 在全站任何 <table> 的資料列 (tbody tr) 按滑鼠右鍵即選取該筆紀錄並彈出右鍵操作選單
            document.addEventListener('contextmenu', function (evt) {
                const rawTarget = evt.target;
                const target = (rawTarget && rawTarget.nodeType === 1) ? rawTarget : (rawTarget ? rawTarget.parentElement : null);
                if (!target || !target.closest) return;
                // 若在輸入框、下拉選單或文字區按右鍵則保留瀏覽器原生選單
                if (target.closest('input, textarea, select')) return;
                const tr = target.closest('table tbody tr');
                if (!tr) return;
                evt.preventDefault();
                const td = target.closest('td');
                openRowContextMenuForRow(tr, td, evt.clientX, evt.clientY);
            });

            // 滑鼠左鍵單擊選取列，並關閉已開啟的右鍵選單
            document.addEventListener('click', function (evt) {
                const rawTarget = evt.target;
                const target = (rawTarget && rawTarget.nodeType === 1) ? rawTarget : (rawTarget ? rawTarget.parentElement : null);
                if (spContextMenuEl && spContextMenuEl.classList.contains('visible')) {
                    if (target && target.closest && target.closest('#spRowContextMenu')) {
                        return;
                    }
                    closeRowContextMenu();
                }
                if (target && target.closest && !target.closest('input, textarea, select, button, a, #spRecordEditModal')) {
                    const tr = target.closest('table tbody tr');
                    if (tr) {
                        if (spActiveContextRow && spActiveContextRow !== tr) {
                            spActiveContextRow.classList.remove('sp-context-active-row');
                        }
                        spActiveContextRow = tr;
                        tr.classList.add('sp-context-active-row');
                    }
                }
            });

            // 滑鼠左鍵雙擊 (Double-Click) 表格列直接彈出單筆編輯或明細 Modal 視窗
            document.addEventListener('dblclick', function (evt) {
                const rawTarget = evt.target;
                const target = (rawTarget && rawTarget.nodeType === 1) ? rawTarget : (rawTarget ? rawTarget.parentElement : null);
                if (!target || !target.closest) return;
                if (target.closest('input, textarea, select, button, a, #spRecordEditModal, #spRowContextMenu')) return;
                const tr = target.closest('table tbody tr');
                if (!tr) return;
                if (spActiveContextRow && spActiveContextRow !== tr) {
                    spActiveContextRow.classList.remove('sp-context-active-row');
                }
                spActiveContextRow = tr;
                tr.classList.add('sp-context-active-row');
                triggerDefaultRowEditOrView(tr);
            });

            document.addEventListener('keydown', function (evt) {
                if (evt.key === 'Escape') {
                    closeRowContextMenu();
                    closeRecordEditModal();
                    return;
                }
                const activeEl = document.activeElement;
                const isEditingInput = activeEl && (activeEl.tagName === 'INPUT' || activeEl.tagName === 'TEXTAREA' || activeEl.tagName === 'SELECT' || activeEl.isContentEditable);
                const isModalOpen = spRecordEditModalOverlayEl && spRecordEditModalOverlayEl.classList.contains('visible');
                if (!isEditingInput && !isModalOpen && spActiveContextRow && document.body.contains(spActiveContextRow)) {
                    if (evt.key === 'Enter' || evt.key === 'F2') {
                        evt.preventDefault();
                        triggerDefaultRowEditOrView(spActiveContextRow);
                    }
                }
            });

            window.addEventListener('scroll', function () {
                if (spContextMenuEl && spContextMenuEl.classList.contains('visible')) {
                    closeRowContextMenu();
                }
            }, true);
        }

        function createCtxMenuItem(icon, label, subText, cssClass, onClickFn, disabled) {
            const btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'sp-ctx-item' + (cssClass ? ' ' + cssClass : '');
            if (disabled) {
                btn.disabled = true;
            }
            const leftSpan = document.createElement('span');
            leftSpan.innerHTML = `<span class='sp-ctx-icon'>${icon}</span><span>${label}</span>`;
            btn.appendChild(leftSpan);
            if (subText) {
                const subSpan = document.createElement('span');
                subSpan.style.fontSize = '11px';
                subSpan.style.color = '#64748b';
                subSpan.style.marginLeft = 'auto';
                subSpan.textContent = subText;
                btn.appendChild(subSpan);
            }
            btn.addEventListener('click', function (evt) {
                evt.stopPropagation();
                if (disabled) return;
                closeRowContextMenu();
                onClickFn();
            });
            return btn;
        }

        function createCtxDivider() {
            const div = document.createElement('div');
            div.className = 'sp-ctx-divider';
            return div;
        }

        function openRowContextMenuForRow(tr, clickedTd, clientX, clientY) {
            if (!spContextMenuEl || !tr) return;
            if (spActiveContextRow) {
                spActiveContextRow.classList.remove('sp-context-active-row');
            }
            spActiveContextRow = tr;
            tr.classList.add('sp-context-active-row');

            spContextMenuEl.innerHTML = '';
            const rowKind = tr.getAttribute('data-row-kind') || 'generic';
            const table = tr.closest('table');
            const state = table ? spTableStateMap.get(table) : null;

            // 標題區
            const headerDiv = document.createElement('div');
            headerDiv.className = 'sp-ctx-header';
            let headerTitle = '📋 表格紀錄滑鼠右鍵操作選單';

            if (rowKind === 'workitem') {
                const wiPk = tr.getAttribute('data-wi-pk') || '';
                const wiCode = tr.getAttribute('data-wi-code') || '';
                const wiTitle = tr.getAttribute('data-wi-title') || '';
                const wiTypePk = tr.getAttribute('data-wi-type-pk') || '0';
                const wiPt = tr.getAttribute('data-wi-pt') || '';
                const wiStatus = tr.getAttribute('data-wi-status') || 'todo';
                const wiReason = tr.getAttribute('data-wi-reason') || '';
                const wiKind = tr.getAttribute('data-wi-kind') || 'project';
                const wiKindText = tr.getAttribute('data-wi-kind-text') || '';
                const wiStart = tr.getAttribute('data-wi-start') || '';
                const wiEnd = tr.getAttribute('data-wi-end') || '';
                const wiSprint = tr.getAttribute('data-wi-sprint') || '';
                const wiAssignee = tr.getAttribute('data-wi-assignee') || '';
                const wiLastSessionPk = parseInt(tr.getAttribute('data-wi-last-session-pk') || '0', 10);

                headerTitle = `📋 工項右鍵操作：${wiCode} - ${wiTitle}`;
                headerDiv.innerHTML = `<span>${headerTitle}</span><span class='badge badge-user' style='font-size:11px;'>${wiKindText}｜${wiPt} pt</span>`;
                spContextMenuEl.appendChild(headerDiv);

                if (isCurrentUserAdmin) {
                    spContextMenuEl.appendChild(createCtxMenuItem('✏️', '單筆編輯此工項設定與負責員工指派（彈出 Modal 視窗）', 'Modal 編輯', 'primary-action', function () {
                        openWorkItemEditModal(wiPk, wiCode, wiTitle, wiTypePk, wiPt, wiStatus, wiReason, wiKind, wiStart, wiEnd, wiSprint, false, wiAssignee);
                    }));

                    spContextMenuEl.appendChild(createCtxMenuItem('👥', '修改此工項之多人分攤配額（彈出 Modal 視窗）', 'Modal 指派', '', function () {
                        openWorkItemAssignModal(wiPk, wiCode, wiTitle, wiPt, wiAssignee);
                    }));

                    spContextMenuEl.appendChild(createCtxMenuItem('🔄', '重新投票此工項點數（重啟新一輪多人盲投議程）', '重新投票', 'primary-action', function () {
                        triggerRevoteFromTarget('workItem', wiPk, wiLastSessionPk, wiCode + ' - ' + wiTitle, false, 'voting');
                    }));

                    spContextMenuEl.appendChild(createCtxMenuItem('🃏', '針對此工項發起「多人盲投估算」（彈出 Modal 視窗）', 'Modal 盲投', '', function () {
                        openSessionSetupEditModal(wiLastSessionPk, 'workItem', wiPk, true);
                    }));

                    spContextMenuEl.appendChild(createCtxMenuItem('⏰', '設為已到期並彈出 Modal 編輯（測試自動扣點寄信）', '到期測試', '', function () {
                        openWorkItemEditModal(wiPk, wiCode, wiTitle, wiTypePk, wiPt, wiStatus, wiReason, wiKind, wiStart, wiEnd, wiSprint, true, wiAssignee);
                    }));

                    spContextMenuEl.appendChild(createCtxDivider());

                    spContextMenuEl.appendChild(createCtxMenuItem('🗑️', '刪除此工項紀錄 (' + wiCode + ')', '永久刪除', 'danger', function () {
                        submitDynamicPostAction('/workitem/delete', {
                            workItemPk: wiPk,
                            itemCode: wiCode
                        }, '確定要刪除工項「' + wiCode + ' - ' + wiTitle + '」嗎？');
                    }));

                    spContextMenuEl.appendChild(createCtxDivider());
                }
            } else if (rowKind === 'workitemKind') {
                const kPk = tr.getAttribute('data-kind-pk') || '';
                const kCode = tr.getAttribute('data-kind-code') || '';
                const kName = tr.getAttribute('data-kind-name') || '';
                const kIcon = tr.getAttribute('data-kind-icon') || '📌';
                const kColor = tr.getAttribute('data-kind-color') || 'blue';
                const kSort = tr.getAttribute('data-kind-sort') || '40';
                const kDesc = tr.getAttribute('data-kind-desc') || '';
                const tabSuffix = tr.getAttribute('data-tab-suffix') || 'workitems';

                headerTitle = `🗂️ 工項類型右鍵操作：${kIcon} ${kName} (${kCode})`;
                headerDiv.innerHTML = `<span>${headerTitle}</span>`;
                spContextMenuEl.appendChild(headerDiv);

                if (isCurrentUserAdmin) {
                    spContextMenuEl.appendChild(createCtxMenuItem('✏️', '單筆編輯此工項類型（彈出 Modal 視窗）', 'Modal 編輯', 'primary-action', function () {
                        openWorkItemKindEditModal(kPk, kCode, kName, kIcon, kColor, kSort, kDesc, tabSuffix);
                    }));

                    spContextMenuEl.appendChild(createCtxMenuItem('🔍', '篩選套用此類型 (' + kName + ') 的所有工項', '工項篩選', '', function () {
                        switchTab('workitems');
                        const fKind = document.getElementById('filterWiKind');
                        if (fKind) {
                            fKind.value = kCode;
                            filterWorkItemTable();
                        }
                        showToastNotice('🔍 已篩選工項類型為「' + kName + '」的工項紀錄');
                    }));

                    spContextMenuEl.appendChild(createCtxDivider());

                    spContextMenuEl.appendChild(createCtxMenuItem('🗑️', '刪除此工項類型 (' + kCode + ')', '移轉備援', 'danger', function () {
                        submitDynamicPostAction('/workitem/kind/delete', {
                            workItemKindPk: kPk,
                            kindCode: kCode,
                            returnTab: tabSuffix
                        }, '確定要刪除工項類型「' + kCode + ' - ' + kName + '」嗎？（若有工項使用此類型將自動移轉至備援類型）');
                    }));

                    spContextMenuEl.appendChild(createCtxDivider());
                }
            } else if (rowKind === 'workitemType') {
                const tPk = tr.getAttribute('data-type-pk') || '';
                const tCode = tr.getAttribute('data-type-code') || '';
                const tName = tr.getAttribute('data-type-name') || '';
                const tCat = tr.getAttribute('data-type-cat') || '';
                const tDesc = tr.getAttribute('data-type-desc') || '';
                const tPt = tr.getAttribute('data-type-pt') || '';
                const tKind = tr.getAttribute('data-type-kind') || 'project';
                const tLastSessionPk = parseInt(tr.getAttribute('data-type-last-session-pk') || '0', 10);

                headerTitle = `🏷️ 代表類型右鍵操作：${tCode} - ${tName}`;
                headerDiv.innerHTML = `<span>${headerTitle}</span><span class='badge badge-user' style='font-size:11px;'>${tPt ? tPt + ' pt' : '待估算'}</span>`;
                spContextMenuEl.appendChild(headerDiv);

                if (isCurrentUserAdmin) {
                    spContextMenuEl.appendChild(createCtxMenuItem('✏️', '單筆編輯此工項代表類型（彈出 Modal 視窗）', 'Modal 編輯', 'primary-action', function () {
                        openWorkItemTypeEditModal(tPk, tCode, tName, tCat, tDesc, tPt, tKind);
                    }));

                    spContextMenuEl.appendChild(createCtxMenuItem('🔄', '重新投票此代表類型標準點數（重啟新一輪多人盲投議程）', '重新投票', 'primary-action', function () {
                        triggerRevoteFromTarget('workItemType', tPk, tLastSessionPk, tCode + ' - ' + tName, false, 'voting');
                    }));

                    spContextMenuEl.appendChild(createCtxMenuItem('🃏', '針對此代表類型發起「多人盲投估算」（彈出 Modal 視窗）', 'Modal 盲投', '', function () {
                        openSessionSetupEditModal(tLastSessionPk, 'workItemType', tPk, true);
                    }));

                    spContextMenuEl.appendChild(createCtxMenuItem('🔍', '檢視套用此代表類型的所有工項', '工項篩選', '', function () {
                        switchTab('workitems');
                        const fType = document.getElementById('filterWiType');
                        if (fType) {
                            fType.value = tName;
                            filterWorkItemTable();
                        }
                        showToastNotice('🔍 已篩選代表類型為「' + tName + '」的工項紀錄');
                    }));

                    spContextMenuEl.appendChild(createCtxDivider());

                    spContextMenuEl.appendChild(createCtxMenuItem('🗑️', '刪除此工項代表類型 (' + tCode + ')', '永久刪除', 'danger', function () {
                        submitDynamicPostAction('/workitem/type/delete', {
                            workItemTypePk: tPk,
                            typeCode: tCode
                        }, '確定要刪除工項代表類型「' + tCode + ' - ' + tName + '」嗎？');
                    }));

                    spContextMenuEl.appendChild(createCtxDivider());
                }
            } else if (rowKind === 'employee') {
                const empPk = tr.getAttribute('data-emp-pk') || '';
                const empCode = tr.getAttribute('data-emp-code') || '';
                const empName = tr.getAttribute('data-emp-name') || '';
                const empAcc = tr.getAttribute('data-emp-acc') || '';
                const empMail = tr.getAttribute('data-emp-mail') || '';
                const empTitle = tr.getAttribute('data-emp-title') || '';
                const empDeptPk = tr.getAttribute('data-emp-dept-pk') || '1';
                const empRole = tr.getAttribute('data-emp-role') || 'participant';
                const empActive = tr.getAttribute('data-emp-active') || 'true';
                const empIsMgr = tr.getAttribute('data-emp-ismgr') || 'false';
                const empMgrPk = tr.getAttribute('data-emp-mgrpk') || '';
                const isSelf = tr.getAttribute('data-emp-self') === '1';

                headerTitle = `👤 員工右鍵操作：${empCode} - ${empName}`;
                headerDiv.innerHTML = `<span>${headerTitle}</span><span class='badge badge-user' style='font-size:11px;'>${empAcc}</span>`;
                spContextMenuEl.appendChild(headerDiv);

                if (isCurrentUserAdmin) {
                    spContextMenuEl.appendChild(createCtxMenuItem('✏️', '單筆編輯員工資料、部門主管與登入帳密（彈出 Modal 視窗）', 'Modal 編輯', 'primary-action', function () {
                        openEmployeeEditModal(empPk, empCode, empName, empAcc, empMail, empTitle, empDeptPk, empRole, empActive, empIsMgr, empMgrPk);
                    }));

                    spContextMenuEl.appendChild(createCtxMenuItem('📋', '篩選檢視此員工負責的所有工項', '工項清單', '', function () {
                        switchTab('workitems');
                        const fEmp = document.getElementById('filterWiAssignee');
                        if (fEmp) {
                            fEmp.value = empName;
                            filterWorkItemTable();
                        }
                        showToastNotice('📋 已篩選由「' + empName + '」負責的工項');
                    }));

                    spContextMenuEl.appendChild(createCtxDivider());

                    if (empActive === 'true') {
                        spContextMenuEl.appendChild(createCtxMenuItem('🚫', '停用此員工帳號 (' + empAcc + ')', isSelf ? '本人不可停用' : '停用帳號', 'warn', function () {
                            submitDynamicPostAction('/employee/setActive', {
                                employeePk: empPk,
                                isActive: 'false'
                            }, '確定要停用員工帳號【' + empCode + ' - ' + empName + '】嗎？');
                        }, isSelf));
                    } else {
                        spContextMenuEl.appendChild(createCtxMenuItem('✅', '啟用此員工帳號 (' + empAcc + ')', '恢復啟用', 'primary-action', function () {
                            submitDynamicPostAction('/employee/setActive', {
                                employeePk: empPk,
                                isActive: 'true'
                            });
                        }));
                    }

                    spContextMenuEl.appendChild(createCtxMenuItem('🗑️', '永久刪除此員工帳號 (' + empCode + ')', isSelf ? '本人不可刪除' : '永久刪除', 'danger', function () {
                        submitDynamicPostAction('/employee/delete', {
                            employeePk: empPk
                        }, '確定要永久刪除員工帳號【' + empCode + ' - ' + empName + ' (' + empAcc + ')】嗎？');
                    }, isSelf));

                    spContextMenuEl.appendChild(createCtxDivider());
                }
            } else if (rowKind === 'department') {
                const deptPk = tr.getAttribute('data-dept-pk') || '';
                const deptCode = tr.getAttribute('data-dept-code') || '';
                const deptName = tr.getAttribute('data-dept-name') || '';
                const deptMgr = tr.getAttribute('data-dept-mgr') || '';
                const deptNote = tr.getAttribute('data-dept-note') || '';

                headerTitle = `🏢 部門右鍵操作：${deptCode} - ${deptName}`;
                headerDiv.innerHTML = `<span>${headerTitle}</span>`;
                spContextMenuEl.appendChild(headerDiv);

                if (isCurrentUserAdmin) {
                    spContextMenuEl.appendChild(createCtxMenuItem('✏️', '單筆編輯此部門資料與主管設定（彈出 Modal 視窗）', 'Modal 編輯', 'primary-action', function () {
                        openDepartmentEditModal(deptPk, deptCode, deptName, deptMgr, deptNote);
                    }));

                    spContextMenuEl.appendChild(createCtxMenuItem('👥', '篩選檢視此部門 (' + deptName + ') 的所有員工', '員工清單', '', function () {
                        switchTab('employees');
                        const fDept = document.getElementById('filterEmpDept');
                        if (fDept) {
                            fDept.value = deptName;
                            filterEmployeeTable();
                        }
                        showToastNotice('👥 已篩選「' + deptName + '」部門員工');
                    }));

                    spContextMenuEl.appendChild(createCtxDivider());
                }
            } else if (rowKind === 'session') {
                const sPk = tr.getAttribute('data-session-pk') || '';
                const sCode = tr.getAttribute('data-session-code') || '';
                const sTitle = tr.getAttribute('data-session-title') || '';
                const sUrl = tr.getAttribute('data-session-url') || ('/voting/session?sessionPk=' + sPk);
                const sLocked = tr.getAttribute('data-session-locked') === '1';

                headerTitle = `🃏 盲投議程右鍵操作：${sCode}`;
                headerDiv.innerHTML = `<span>${headerTitle}</span>`;
                spContextMenuEl.appendChild(headerDiv);

                if (isCurrentUserAdmin && sPk) {
                    spContextMenuEl.appendChild(createCtxMenuItem('🔄', '重新投票此盲投議程（開啟新一輪盲投並重設已定案狀態）', '重新投票', 'primary-action', function () {
                        triggerRevoteFromTarget('session', 0, sPk, sCode + ' - ' + sTitle, false, 'voting');
                    }));

                    spContextMenuEl.appendChild(createCtxMenuItem('🔁', '清空本回合已出牌紀錄並重新投票（保持同一回合號）', '重設本輪', 'warn', function () {
                        triggerRevoteFromTarget('session', 0, sPk, sCode + ' - ' + sTitle, true, 'voting');
                    }));

                    spContextMenuEl.appendChild(createCtxMenuItem('✏️', '單筆編輯此盲投議程回合設定（彈出 Modal 視窗）', 'Modal 編輯', 'primary-action', function () {
                        openSessionSetupEditModal(sPk);
                    }));
                }

                spContextMenuEl.appendChild(createCtxMenuItem('🚀', sLocked ? '此盲投 Session 已在獨立網頁開啟中' : '開啟此盲投 Session 獨立網頁', '#' + sPk, 'primary-action', function () {
                    window.open(sUrl, '_blank', 'noopener');
                }, sLocked));

                spContextMenuEl.appendChild(createCtxDivider());
            } else {
                headerDiv.innerHTML = `<span>${headerTitle}</span>`;
                spContextMenuEl.appendChild(headerDiv);
            }

            // 任何表格列皆可彈出 Modal 檢視完整欄位明細
            spContextMenuEl.appendChild(createCtxMenuItem('🪟', '彈出 Modal 視窗檢視此筆紀錄完整明細', 'Modal 檢視', '', function () {
                openRowDetailReadonlyModal(tr);
            }));

            // 通用表格篩選與複製快捷動作（任何表格皆適用）
            if (clickedTd && state) {
                const colIdx = Array.from(tr.children).indexOf(clickedTd);
                const cellVal = extractCellFilterDisplayText(clickedTd);
                const colTitle = (colIdx >= 0 && state.colNames[colIdx]) ? state.colNames[colIdx] : '此欄';
                if (cellVal) {
                    const shortVal = cellVal.length > 22 ? cellVal.substring(0, 22) + '...' : cellVal;
                    spContextMenuEl.appendChild(createCtxMenuItem('🔍', `以此儲存格值篩選【${colTitle}】`, shortVal, '', function () {
                        state.colSelect.value = colIdx >= 0 ? String(colIdx) : '-1';
                        populateToolbarColumnDistinctValues(state);
                        state.filterInput.value = cellVal;
                        applyToolbarTableFilter(state);
                        showToastNotice('🔍 已篩選【' + colTitle + '】包含「' + shortVal + '」的紀錄');
                    }));
                }
            }

            if (state) {
                spContextMenuEl.appendChild(createCtxMenuItem('🔄', '清除本表格所有篩選條件並重設排序', '顯示全部', '', function () {
                    state.colSelect.value = '-1';
                    state.valSelect.style.display = 'none';
                    state.valSelect.innerHTML = '';
                    state.filterInput.value = '';
                    applyToolbarTableFilter(state);
                    if (table.id === 'workItemDataTable') resetWorkItemFilter();
                    if (table.id === 'workItemTypeDataTable') resetWorkItemTypeFilter();
                    if (table.id === 'empDataTable') resetEmployeeFilter();
                    showToastNotice('🔄 已清除本表格所有篩選條件');
                }));
            }

            spContextMenuEl.appendChild(createCtxMenuItem('📋', '複製本列紀錄文字至剪貼簿', 'Tab 分隔', '', function () {
                const rowText = Array.from(tr.children).map(c => extractCellFilterDisplayText(c)).join('\t');
                if (navigator.clipboard && navigator.clipboard.writeText) {
                    navigator.clipboard.writeText(rowText).then(function () {
                        showToastNotice('📋 已複製本列紀錄至剪貼簿');
                    });
                }
            }));

            // 顯示並智慧定位於滑鼠座標（避免超出視窗右緣或底緣）
            spContextMenuEl.classList.add('visible', 'open');
            const menuRect = spContextMenuEl.getBoundingClientRect();
            let left = clientX;
            let top = clientY;
            if (left + menuRect.width > window.innerWidth - 12) {
                left = Math.max(8, window.innerWidth - menuRect.width - 12);
            }
            if (top + menuRect.height > window.innerHeight - 12) {
                top = Math.max(8, window.innerHeight - menuRect.height - 12);
            }
            spContextMenuEl.style.left = Math.round(left) + 'px';
            spContextMenuEl.style.top = Math.round(top) + 'px';
        }

        function filterWorkItemTable() {
            const kw = (document.getElementById('filterWiKeyword')?.value || '').trim().toLowerCase();
            const kind = document.getElementById('filterWiKind')?.value || '';
            const typeName = document.getElementById('filterWiType')?.value || '';
            const status = document.getElementById('filterWiStatus')?.value || '';
            const assignee = (document.getElementById('filterWiAssignee')?.value || '').trim().toLowerCase();

            const rows = document.querySelectorAll('#workItemDataTable tbody tr.wi-row');
            let visibleCount = 0;
            rows.forEach(r => {
                const matchKw = !kw || (r.getAttribute('data-search') || '').indexOf(kw) !== -1;
                const matchKind = !kind || r.getAttribute('data-wi-kind') === kind;
                const matchType = !typeName || r.getAttribute('data-wi-type-name') === typeName;
                let matchStatus = true;
                if (status === 'expired') {
                    matchStatus = r.getAttribute('data-wi-expired') === '1';
                } else if (status === 'activePt') {
                    matchStatus = r.getAttribute('data-wi-expired') !== '1';
                } else if (status) {
                    matchStatus = r.getAttribute('data-wi-status') === status;
                }
                const matchAssignee = !assignee || (r.getAttribute('data-wi-assignee') || '').toLowerCase().indexOf(assignee) !== -1;

                const customMatch = matchKw && matchKind && matchType && matchStatus && matchAssignee;
                r.setAttribute('data-custom-hidden', customMatch ? '0' : '1');
                if (syncRowDisplayState(r)) {
                    visibleCount++;
                }
            });

            const lbl = document.getElementById('wiFilterCountLabel');
            if (lbl) {
                lbl.textContent = '符合篩選：' + visibleCount + ' / ' + rows.length + ' 筆工項';
            }
            refreshToolbarCountBadgeForTable('workItemDataTable');
        }

        function resetWorkItemFilter() {
            if (document.getElementById('filterWiKeyword')) document.getElementById('filterWiKeyword').value = '';
            if (document.getElementById('filterWiKind')) document.getElementById('filterWiKind').value = '';
            if (document.getElementById('filterWiType')) document.getElementById('filterWiType').value = '';
            if (document.getElementById('filterWiStatus')) document.getElementById('filterWiStatus').value = '';
            if (document.getElementById('filterWiAssignee')) document.getElementById('filterWiAssignee').value = '';
            const tbl = document.getElementById('workItemDataTable');
            if (tbl && spTableStateMap.has(tbl)) {
                const st = spTableStateMap.get(tbl);
                st.colSelect.value = '-1';
                st.valSelect.style.display = 'none';
                st.filterInput.value = '';
                applyToolbarTableFilter(st);
            }
            filterWorkItemTable();
        }

        function filterWorkItemTypeTable() {
            const kw = (document.getElementById('filterTypeKeyword')?.value || '').trim().toLowerCase();
            const kind = document.getElementById('filterTypeKind')?.value || '';
            const status = document.getElementById('filterTypeStatus')?.value || '';

            const rows = document.querySelectorAll('#workItemTypeDataTable tbody tr.type-row');
            let visibleCount = 0;
            rows.forEach(r => {
                const matchKw = !kw || (r.getAttribute('data-search') || '').indexOf(kw) !== -1;
                const matchKind = !kind || r.getAttribute('data-type-kind') === kind;
                let matchStatus = true;
                if (status === 'voting') {
                    matchStatus = r.getAttribute('data-type-est-status') === 'voting';
                } else if (status) {
                    matchStatus = r.getAttribute('data-type-status') === status;
                }
                const customMatch = matchKw && matchKind && matchStatus;
                r.setAttribute('data-custom-hidden', customMatch ? '0' : '1');
                if (syncRowDisplayState(r)) {
                    visibleCount++;
                }
            });

            const lbl = document.getElementById('typeFilterCountLabel');
            if (lbl) {
                lbl.textContent = '符合篩選：' + visibleCount + ' / ' + rows.length + ' 筆代表類型';
            }
            refreshToolbarCountBadgeForTable('workItemTypeDataTable');
        }

        function resetWorkItemTypeFilter() {
            if (document.getElementById('filterTypeKeyword')) document.getElementById('filterTypeKeyword').value = '';
            if (document.getElementById('filterTypeKind')) document.getElementById('filterTypeKind').value = '';
            if (document.getElementById('filterTypeStatus')) document.getElementById('filterTypeStatus').value = '';
            const tbl = document.getElementById('workItemTypeDataTable');
            if (tbl && spTableStateMap.has(tbl)) {
                const st = spTableStateMap.get(tbl);
                st.colSelect.value = '-1';
                st.valSelect.style.display = 'none';
                st.filterInput.value = '';
                applyToolbarTableFilter(st);
            }
            filterWorkItemTypeTable();
        }

        function onWorkItemTypeChange() {
            const sel = document.getElementById('wiTypeSelect');
            if (!sel) return;
            const opt = sel.options[sel.selectedIndex];
            const pt = opt ? opt.getAttribute('data-pt') : '';
            const kind = opt ? opt.getAttribute('data-kind') : '';
            if (pt && pt !== '') {
                document.getElementById('wiStoryPoint').value = pt;
            }
            if (kind && kind !== '' && document.getElementById('wiKindSelect')) {
                document.getElementById('wiKindSelect').value = kind;
            }
        }

        function formatLocalIsoMinute(dt) {
            const pad = n => String(n).padStart(2, '0');
            return dt.getFullYear() + '-' + pad(dt.getMonth() + 1) + '-' + pad(dt.getDate()) + 'T' + pad(dt.getHours()) + ':' + pad(dt.getMinutes());
        }

        function applyWorkItemDurationPreset() {
            const startEl = document.getElementById('wiStartDate');
            const endEl = document.getElementById('wiEndDate');
            const qtyEl = document.getElementById('wiDurationQty');
            const unitEl = document.getElementById('wiDurationUnit');
            if (!startEl || !endEl) return;

            let baseDate = startEl.value ? new Date(startEl.value) : new Date();
            if (isNaN(baseDate.getTime())) {
                baseDate = new Date();
                startEl.value = formatLocalIsoMinute(baseDate);
            } else if (!startEl.value) {
                startEl.value = formatLocalIsoMinute(baseDate);
            }

            let qty = qtyEl ? parseFloat(qtyEl.value) : 1;
            if (isNaN(qty) || qty <= 0) {
                qty = 1;
            }
            const unit = unitEl ? unitEl.value : 'day';
            const endDate = new Date(baseDate.getTime());

            if (unit === 'day') {
                endDate.setDate(endDate.getDate() + qty);
            } else if (unit === 'week') {
                endDate.setDate(endDate.getDate() + (qty * 7));
            } else if (unit === 'month') {
                endDate.setMonth(endDate.getMonth() + qty);
            } else if (unit === 'quarter') {
                endDate.setMonth(endDate.getMonth() + (qty * 3));
            } else if (unit === 'year') {
                endDate.setFullYear(endDate.getFullYear() + qty);
            } else {
                endDate.setDate(endDate.getDate() + qty);
            }

            endEl.value = formatLocalIsoMinute(endDate);
        }

        function setWorkItemDatePreset(daysOffset) {
            const now = new Date();
            const startEl = document.getElementById('wiStartDate');
            const endEl = document.getElementById('wiEndDate');
            const qtyEl = document.getElementById('wiDurationQty');
            const unitEl = document.getElementById('wiDurationUnit');
            if (!startEl || !endEl) return;
            if (daysOffset < 0) {
                // 設為已到期（起時：7天前，迄時：10分鐘前），儲存後立即觸發自動轉換狀態為已完成、扣除 Story Point 並寄信給部門管理層與 HR
                const pastStart = new Date(now.getTime() - 7 * 24 * 3600 * 1000);
                const pastEnd = new Date(now.getTime() - 10 * 60 * 1000);
                startEl.value = formatLocalIsoMinute(pastStart);
                endEl.value = formatLocalIsoMinute(pastEnd);
            } else {
                startEl.value = formatLocalIsoMinute(now);
                if (qtyEl) qtyEl.value = String(daysOffset || 1);
                if (unitEl) unitEl.value = 'day';
                applyWorkItemDurationPreset();
            }
        }

        function loadWorkItemKindToForm(pk, code, name, icon, color, sortOrder, desc, tabSuffix) {
            const sfx = tabSuffix || 'workitems';
            const pkEl = document.getElementById('wikPk_' + sfx);
            const codeEl = document.getElementById('wikCode_' + sfx);
            const nameEl = document.getElementById('wikName_' + sfx);
            const iconEl = document.getElementById('wikIcon_' + sfx);
            const colorEl = document.getElementById('wikColor_' + sfx);
            const sortEl = document.getElementById('wikSort_' + sfx);
            const descEl = document.getElementById('wikDesc_' + sfx);
            const btnEl = document.getElementById('wikSubmitBtn_' + sfx);
            if (pkEl) pkEl.value = pk;
            if (codeEl) codeEl.value = code || '';
            if (nameEl) nameEl.value = name || '';
            if (iconEl) iconEl.value = icon || '📌';
            if (colorEl) colorEl.value = color || 'blue';
            if (sortEl) sortEl.value = sortOrder || 40;
            if (descEl) descEl.value = desc || '';
            if (btnEl) btnEl.textContent = '💾 儲存修改工項類型 (' + code + ')';
            openWorkItemKindEditModal(pk, code, name, icon, color, sortOrder, desc, sfx);
        }

        function resetWorkItemKindForm(tabSuffix) {
            const sfx = tabSuffix || 'workitems';
            const pkEl = document.getElementById('wikPk_' + sfx);
            const codeEl = document.getElementById('wikCode_' + sfx);
            const nameEl = document.getElementById('wikName_' + sfx);
            const iconEl = document.getElementById('wikIcon_' + sfx);
            const colorEl = document.getElementById('wikColor_' + sfx);
            const sortEl = document.getElementById('wikSort_' + sfx);
            const descEl = document.getElementById('wikDesc_' + sfx);
            const btnEl = document.getElementById('wikSubmitBtn_' + sfx);
            if (pkEl) pkEl.value = '';
            if (codeEl) codeEl.value = '';
            if (nameEl) nameEl.value = '';
            if (iconEl) iconEl.value = '📌';
            if (colorEl) colorEl.value = 'blue';
            if (sortEl) sortEl.value = '60';
            if (descEl) descEl.value = '';
            if (btnEl) btnEl.textContent = '➕ 新增 / 儲存工項類型';
        }

        function resetWorkItemForm(nextCode) {
            document.getElementById('wiPk').value = '';
            document.getElementById('wiCode').value = nextCode || '';
            document.getElementById('wiTitle').value = '';
            document.getElementById('wiTypeSelect').value = '0';
            document.getElementById('wiStoryPoint').value = '';
            document.getElementById('wiStatus').value = 'todo';
            document.getElementById('wiReason').value = '';
            if (document.getElementById('wiFormTitle')) {
                document.getElementById('wiFormTitle').textContent = '➕ 建立/修改工項（設定工項類型、起訖時間與 Story Point）';
            }
            if (document.getElementById('wiDurationQty')) {
                document.getElementById('wiDurationQty').value = '1';
            }
            if (document.getElementById('wiDurationUnit')) {
                document.getElementById('wiDurationUnit').value = 'day';
            }
            if (document.getElementById('wiStartDate')) {
                document.getElementById('wiStartDate').value = formatLocalIsoMinute(new Date());
            }
            applyWorkItemDurationPreset();
        }

        function loadWorkItemToForm(pk, code, title, typePk, pt, status, reason, kind, startIso, endIso) {
            if (document.getElementById('wiPk')) document.getElementById('wiPk').value = pk;
            if (document.getElementById('wiCode')) document.getElementById('wiCode').value = code;
            if (document.getElementById('wiTitle')) document.getElementById('wiTitle').value = title;
            if (document.getElementById('wiTypeSelect')) document.getElementById('wiTypeSelect').value = typePk;
            if (document.getElementById('wiStoryPoint')) document.getElementById('wiStoryPoint').value = pt;
            if (document.getElementById('wiStatus')) document.getElementById('wiStatus').value = status;
            if (document.getElementById('wiReason')) document.getElementById('wiReason').value = reason || '';
            if (kind && document.getElementById('wiKindSelect')) {
                document.getElementById('wiKindSelect').value = kind;
            }
            if (startIso && document.getElementById('wiStartDate')) {
                document.getElementById('wiStartDate').value = startIso;
            }
            if (endIso && document.getElementById('wiEndDate')) {
                document.getElementById('wiEndDate').value = endIso;
            }
            if (document.getElementById('wiFormTitle')) {
                document.getElementById('wiFormTitle').textContent = '✏️ 修改工項（正在編修：' + code + ' - ' + title + '）';
            }
            openWorkItemEditModal(pk, code, title, typePk, pt, status, reason, kind, startIso, endIso, '', false);
        }

        function resetTypeForm() {
            document.getElementById('typePk').value = '';
            document.getElementById('typeCode').value = '';
            document.getElementById('typeName').value = '';
            document.getElementById('typeCat').value = '後端與資料庫';
            document.getElementById('typeDesc').value = '';
            document.getElementById('typePt').value = '';
            if (document.getElementById('typeKind')) {
                document.getElementById('typeKind').value = 'project';
            }
            if (document.getElementById('typeFormTitle')) {
                document.getElementById('typeFormTitle').textContent = '🏷️ 新增或修改「工項代表類型」（可設定預設工項類型與標準 Story Point）';
            }
        }

        function loadTypeToForm(pk, code, name, cat, desc, pt, kind) {
            if (document.getElementById('typePk')) document.getElementById('typePk').value = pk;
            if (document.getElementById('typeCode')) document.getElementById('typeCode').value = code;
            if (document.getElementById('typeName')) document.getElementById('typeName').value = name;
            if (document.getElementById('typeCat')) document.getElementById('typeCat').value = cat;
            if (document.getElementById('typeDesc')) document.getElementById('typeDesc').value = desc;
            if (document.getElementById('typePt')) document.getElementById('typePt').value = pt;
            if (kind && document.getElementById('typeKind')) {
                document.getElementById('typeKind').value = kind;
            }
            if (document.getElementById('typeFormTitle')) {
                document.getElementById('typeFormTitle').textContent = '✏️ 修改「工項代表類型」（正在編修：' + code + ' - ' + name + '）';
            }
            openWorkItemTypeEditModal(pk, code, name, cat, desc, pt, kind);
        }

        function loadEmpToForm(pk, code, name, acc, mail, title, deptPk, role, isActiveStr, isDeptMgrStr, mgrPkStr) {
            if (document.getElementById('empPk')) document.getElementById('empPk').value = pk;
            if (document.getElementById('empCode')) document.getElementById('empCode').value = code;
            if (document.getElementById('empName')) document.getElementById('empName').value = name;
            if (document.getElementById('empAcc')) document.getElementById('empAcc').value = acc;
            if (document.getElementById('empMail')) document.getElementById('empMail').value = mail;
            if (document.getElementById('empTitle')) document.getElementById('empTitle').value = title;
            if (document.getElementById('empDept')) document.getElementById('empDept').value = deptPk;
            if (document.getElementById('empRole')) document.getElementById('empRole').value = role;
            if (document.getElementById('empPwd')) document.getElementById('empPwd').value = '';
            const qSel = document.getElementById('empQuickEditSelector');
            if (qSel) qSel.value = String(pk);
            if (document.getElementById('empActive')) {
                document.getElementById('empActive').value = isActiveStr || 'true';
            }
            if (document.getElementById('empIsDeptMgr')) {
                document.getElementById('empIsDeptMgr').value = (isDeptMgrStr === 'true' || isDeptMgrStr === true) ? 'true' : 'false';
            }
            if (document.getElementById('empManagerPk')) {
                document.getElementById('empManagerPk').value = mgrPkStr ? String(mgrPkStr) : '';
            }
            if (document.getElementById('empFormTitle')) {
                document.getElementById('empFormTitle').innerHTML = '✏️ 修改員工資料、部門主管註記與樹狀階層（正在編修：' + code + ' - ' + name + '）';
            }
            if (document.getElementById('empSubmitBtn')) {
                document.getElementById('empSubmitBtn').innerHTML = '💾 儲存修改員工與登入帳密';
            }
            openEmployeeEditModal(pk, code, name, acc, mail, title, deptPk, role, isActiveStr, isDeptMgrStr, mgrPkStr);
        }

        function onQuickSelectEmployeeToEdit(empPkVal) {
            const qSel = document.getElementById('empQuickEditSelector');
            if (!qSel || !empPkVal) return;
            const opt = qSel.options[qSel.selectedIndex];
            if (!opt) return;
            loadEmpToForm(
                empPkVal,
                opt.getAttribute('data-code') || '',
                opt.getAttribute('data-name') || '',
                opt.getAttribute('data-acc') || '',
                opt.getAttribute('data-mail') || '',
                opt.getAttribute('data-title') || '',
                opt.getAttribute('data-dept') || '1',
                opt.getAttribute('data-role') || 'participant',
                opt.getAttribute('data-active') || 'true',
                opt.getAttribute('data-ismgr') || 'false',
                opt.getAttribute('data-mgrpk') || ''
            );
            document.getElementById('empPwd')?.focus();
        }

        function autoMatchExistingEmpByAcc(accText) {
            const clean = (accText || '').trim().toLowerCase();
            if (!clean) return;
            const qSel = document.getElementById('empQuickEditSelector');
            if (!qSel) return;
            for (let i = 0; i < qSel.options.length; i++) {
                const opt = qSel.options[i];
                const optAcc = (opt.getAttribute('data-acc') || '').trim().toLowerCase();
                if (optAcc && optAcc === clean) {
                    qSel.value = opt.value;
                    document.getElementById('empPk').value = opt.value;
                    document.getElementById('empCode').value = opt.getAttribute('data-code') || '';
                    if (!document.getElementById('empName').value) {
                        document.getElementById('empName').value = opt.getAttribute('data-name') || '';
                    }
                    document.getElementById('empMail').value = opt.getAttribute('data-mail') || 'user@company.com';
                    document.getElementById('empTitle').value = opt.getAttribute('data-title') || '資深工程師';
                    document.getElementById('empDept').value = opt.getAttribute('data-dept') || '1';
                    document.getElementById('empRole').value = opt.getAttribute('data-role') || 'participant';
                    if (document.getElementById('empActive')) {
                        document.getElementById('empActive').value = opt.getAttribute('data-active') || 'true';
                    }
                    if (document.getElementById('empIsDeptMgr')) {
                        document.getElementById('empIsDeptMgr').value = opt.getAttribute('data-ismgr') || 'false';
                    }
                    if (document.getElementById('empManagerPk')) {
                        document.getElementById('empManagerPk').value = opt.getAttribute('data-mgrpk') || '';
                    }
                    if (document.getElementById('empFormTitle')) {
                        document.getElementById('empFormTitle').innerHTML = '✏️ 修改員工資料、部門主管註記與樹狀階層（已自動對應既有帳號：' + (opt.getAttribute('data-code') || '') + ' - ' + (opt.getAttribute('data-name') || '') + '）';
                    }
                    if (document.getElementById('empSubmitBtn')) {
                        document.getElementById('empSubmitBtn').innerHTML = '💾 儲存修改員工與登入帳密';
                    }
                    return;
                }
            }
        }

        function toggleEmpPasswordVisibility() {
            togglePasswordVisibilityById('empPwd', 'btnToggleEmpPwd');
        }

        function togglePasswordVisibilityById(inputId, btnId) {
            const pwdInput = document.getElementById(inputId);
            const btn = document.getElementById(btnId);
            if (!pwdInput || !btn) return;
            if (pwdInput.type === 'password') {
                pwdInput.type = 'text';
                btn.textContent = '🙈 隱藏';
            } else {
                pwdInput.type = 'password';
                btn.textContent = '👁️ 顯示';
            }
        }

        function validateSelfCredentialForm(formEl) {
            if (!formEl) return true;
            const accInput = formEl.querySelector('input[name=""newLoginAccount""]');
            const newPwdInput = formEl.querySelector('input[name=""newPlainPassword""]');
            const confirmPwdInput = formEl.querySelector('input[name=""confirmPlainPassword""]');
            const acc = (accInput?.value || '').trim();
            const newPwd = (newPwdInput?.value || '').trim();
            const confirmPwd = (confirmPwdInput?.value || '').trim();
            if (!acc) {
                alert('⚠️ 登入帳號不可為空白！');
                accInput?.focus();
                return false;
            }
            if ((newPwd || confirmPwd) && newPwd !== confirmPwd) {
                alert('⚠️ 兩次輸入的新登入密碼不一致，請重新確認！');
                confirmPwdInput?.focus();
                return false;
            }
            return true;
        }

        function resetEmpForm(nextCode) {
            document.getElementById('empPk').value = '';
            document.getElementById('empCode').value = nextCode;
            document.getElementById('empName').value = '';
            document.getElementById('empAcc').value = '';
            document.getElementById('empMail').value = 'user@company.com';
            document.getElementById('empTitle').value = '資深工程師';
            document.getElementById('empRole').value = 'participant';
            document.getElementById('empPwd').value = '';
            const qSel = document.getElementById('empQuickEditSelector');
            if (qSel) qSel.value = '';
            if (document.getElementById('empActive')) {
                document.getElementById('empActive').value = 'true';
            }
            if (document.getElementById('empIsDeptMgr')) {
                document.getElementById('empIsDeptMgr').value = 'false';
            }
            if (document.getElementById('empManagerPk')) {
                document.getElementById('empManagerPk').value = '';
            }
            if (document.getElementById('empFormTitle')) {
                document.getElementById('empFormTitle').innerHTML = '➕ 新增 / 修改員工資料、部門主管註記與樹狀階層 (tbEmployee)';
            }
            if (document.getElementById('empSubmitBtn')) {
                document.getElementById('empSubmitBtn').innerHTML = '💾 新增儲存員工與登入帳密';
            }
        }

        function filterEmployeeTable() {
            const kw = (document.getElementById('filterEmpKeyword')?.value || '').trim().toLowerCase();
            const dept = document.getElementById('filterEmpDept')?.value || '';
            const rank = document.getElementById('filterEmpRank')?.value || '';
            const role = document.getElementById('filterEmpRole')?.value || '';
            const status = document.getElementById('filterEmpStatus')?.value || '';

            const rows = document.querySelectorAll('#empDataTable tbody tr.emp-row');
            let visibleCount = 0;
            rows.forEach(r => {
                const matchKw = !kw || (r.getAttribute('data-search') || '').indexOf(kw) !== -1;
                const matchDept = !dept || r.getAttribute('data-dept') === dept;
                const matchRank = !rank || r.getAttribute('data-rank') === rank;
                const matchRole = !role || r.getAttribute('data-role') === role;
                const matchStatus = !status || r.getAttribute('data-active') === status;

                const customMatch = matchKw && matchDept && matchRank && matchRole && matchStatus;
                r.setAttribute('data-custom-hidden', customMatch ? '0' : '1');
                if (syncRowDisplayState(r)) {
                    visibleCount++;
                }
            });

            const lbl = document.getElementById('empFilterCountLabel');
            if (lbl) {
                lbl.textContent = '符合查詢條件：共 ' + visibleCount + ' 位員工（總計 ' + rows.length + ' 位）';
            }
            refreshToolbarCountBadgeForTable('empDataTable');
        }

        function resetEmployeeFilter() {
            if (document.getElementById('filterEmpKeyword')) document.getElementById('filterEmpKeyword').value = '';
            if (document.getElementById('filterEmpDept')) document.getElementById('filterEmpDept').value = '';
            if (document.getElementById('filterEmpRank')) document.getElementById('filterEmpRank').value = '';
            if (document.getElementById('filterEmpRole')) document.getElementById('filterEmpRole').value = '';
            if (document.getElementById('filterEmpStatus')) document.getElementById('filterEmpStatus').value = '';
            const tbl = document.getElementById('empDataTable');
            if (tbl && spTableStateMap.has(tbl)) {
                const st = spTableStateMap.get(tbl);
                st.colSelect.value = '-1';
                st.valSelect.style.display = 'none';
                st.filterInput.value = '';
                applyToolbarTableFilter(st);
            }
            filterEmployeeTable();
        }

        function filterSystemLogTable() {
            const kw = (document.getElementById('filterLogKeyword')?.value || '').trim().toLowerCase();
            const lvl = document.getElementById('filterLogLevel')?.value || '';
            const src = document.getElementById('filterLogSource')?.value || '';

            const rows = document.querySelectorAll('#sysLogDataTable tbody tr.syslog-row');
            let visibleCount = 0;
            rows.forEach(r => {
                const matchKw = !kw || (r.getAttribute('data-search') || '').indexOf(kw) !== -1;
                const matchLvl = !lvl || r.getAttribute('data-level') === lvl;
                const matchSrc = !src || r.getAttribute('data-source') === src;

                const customMatch = matchKw && matchLvl && matchSrc;
                r.setAttribute('data-custom-hidden', customMatch ? '0' : '1');
                if (syncRowDisplayState(r)) {
                    visibleCount++;
                }
            });

            const lbl = document.getElementById('sysLogFilterCountLabel');
            if (lbl) {
                lbl.textContent = '符合查詢條件：共 ' + visibleCount + ' 筆 Log（總計 ' + rows.length + ' 筆）';
            }
            refreshToolbarCountBadgeForTable('sysLogDataTable');
        }

        function fillSampleBatchEmployees() {
            const sample = [
                'EMP009,林志豪 (Leo),leo.lin,pass123,leo.lin@company.com,部門經理,開發,admin,1,1,byron.hu',
                'EMP010,許雅筑 (Chloe),chloe.hsu,pass123,chloe.hsu@company.com,資深工程師,開發,participant,1,0,leo.lin',
                'EMP011,鄭家銘 (Mark),mark.cheng,admin123,mark.cheng@company.com,部門經理 / 敏捷教練,BI,admin,1,1,byron.hu'
            ].join('\n');
            const box = document.getElementById('batchEmpTextarea');
            if (box) box.value = sample;
        }

        // 建立 SignalR 即時連線、新議程自動偵測、個人「待參與 ➔ 參與中」分流與同 Session 防重複進入機制：
        // 1. 進行中議程依個人狀態分為「📂 待參與」與「🟢 參與中」，是個人從「待參與」點選，才會進入「參與中」的獨立網頁
        // 2. 提供手動「🔄 重新整理」按鈕，可隨時刷新呈現最新投票議程
        // 3. 支援同一人同時以多個獨立網頁參與「不同」盲投 Session，並即時攔截「同一個 Session 重複進入」
        const currentEmployeePk = " + curEmpPk + @";
        const currentVotingSessionPk = " + activeSessionPk + @";
        let currentSessionStatus = '" + (activeSessionRow != null ? (activeSessionRow["sessionStatus"]?.ToString() ?? "voting") : "") + @"';
        let currentRoundNumber = " + (activeSessionRow != null && activeSessionRow["currentRoundNumber"] != DBNull.Value ? Convert.ToInt32(activeSessionRow["currentRoundNumber"]) : 1) + @";
        let currentRoundVoteCount = " + (activeSessionRow != null && activeSessionRow.Table.Columns.Contains("currentRoundVoteCount") && activeSessionRow["currentRoundVoteCount"] != DBNull.Value ? Convert.ToInt32(activeSessionRow["currentRoundVoteCount"]) : 0) + @";
        const currentMemberSessionPks = [" + string.Join(",", sessionTable.Rows.Cast<DataRow>().Select(r => Convert.ToInt32(r["votingSessionPk"]))) + @"];
        const currentSessionListSignature = '" + string.Join("|", sessionTable.Rows.Cast<DataRow>().Select(r => $"{r["votingSessionPk"]}:{r["sessionStatus"]}:{r["currentRoundNumber"]}:{(r.Table.Columns.Contains("currentRoundVoteCount") && r["currentRoundVoteCount"] != DBNull.Value ? r["currentRoundVoteCount"] : 0)}:{(r.Table.Columns.Contains("participantCount") && r["participantCount"] != DBNull.Value ? r["participantCount"] : 0)}")) + @"';
        let myActiveOpenSessionPks = [" + string.Join(",", sqlDataService.getActiveLockedSessionPksForEmployee(curEmpPk)) + @"];
        window.__justEnteredSessionTs = window.__justEnteredSessionTs || {};
        let isAutoReloading = false;

        // 檢查某個 Session 是否目前正由本人在某個獨立網頁開啟中（處於「🟢 參與中」狀態）
        function isSessionCurrentlyOpenByMe(sessionPk) {
            const pk = Number(sessionPk);
            if (Array.isArray(myActiveOpenSessionPks) && myActiveOpenSessionPks.indexOf(pk) !== -1) {
                return true;
            }
            if (window.__justEnteredSessionTs && window.__justEnteredSessionTs[pk] && (Date.now() - window.__justEnteredSessionTs[pk]) < 6000) {
                return true;
            }
            try {
                const raw = localStorage.getItem('sp_active_session_lock_' + currentEmployeePk + '_' + pk);
                if (raw) {
                    const obj = JSON.parse(raw);
                    if (obj && obj.lastHeartbeatMs && (Date.now() - Number(obj.lastHeartbeatMs)) < 45000) {
                        return true;
                    }
                }
            } catch (e) { }
            return false;
        }

        // 動態同步更新左側「📌 投票議程清單」與右側大廳的「📂 待參與」與「🟢 參與中」分組、徽章與按鈕狀態
        function updateLobbyActiveSessionLocks(serverActivePks) {
            if (Array.isArray(serverActivePks)) {
                const srvSet = serverActivePks.map(Number);
                // 保留剛從待參與點選 (6 秒內正在載入新分頁) 的 Session，並清理伺服器已釋放之過期 localStorage 鎖定
                currentMemberSessionPks.forEach(function (pk) {
                    const justClicked = window.__justEnteredSessionTs && window.__justEnteredSessionTs[pk] && (Date.now() - window.__justEnteredSessionTs[pk]) < 6000;
                    if (srvSet.indexOf(pk) === -1 && !justClicked) {
                        try {
                            localStorage.removeItem('sp_active_session_lock_' + currentEmployeePk + '_' + pk);
                        } catch (e) { }
                    } else if (justClicked && srvSet.indexOf(pk) === -1) {
                        srvSet.push(pk);
                    }
                });
                myActiveOpenSessionPks = srvSet;
            }

            const sidebarPendingBody = document.getElementById('sidebarFolderBodyPending');
            const sidebarParticipatingBody = document.getElementById('sidebarFolderBodyParticipating');
            const lobbyPendingGrid = document.getElementById('lobbyGridPending');
            const lobbyParticipatingGrid = document.getElementById('lobbyGridParticipating');

            currentMemberSessionPks.forEach(function (pk) {
                const isOpen = isSessionCurrentlyOpenByMe(pk);
                const sideCard = document.getElementById('sidebarSessionCard_' + pk);
                const isFinalized = sideCard && sideCard.getAttribute('data-session-status') === 'finalized';

                if (!isFinalized) {
                    // 1. 左側邊欄卡片在「📂 待參與」與「🟢 參與中」兩個 Folder 間即時移動
                    if (sideCard) {
                        if (isOpen && sidebarParticipatingBody && sideCard.parentElement !== sidebarParticipatingBody) {
                            sidebarParticipatingBody.appendChild(sideCard);
                            const partFolder = document.getElementById('folder-participating');
                            if (partFolder) partFolder.classList.remove('collapsed');
                        } else if (!isOpen && sidebarPendingBody && sideCard.parentElement !== sidebarPendingBody) {
                            sidebarPendingBody.appendChild(sideCard);
                        }
                        sideCard.style.border = isOpen ? '2px solid #16a34a' : '1.5px solid #cbd5e1';
                        sideCard.style.background = isOpen ? '#f0fdf4' : '#ffffff';
                    }

                    // 2. 右側大廳卡片在「📂 待參與議程」與「🟢 參與中議程」兩個區塊間即時移動
                    const lobbyCard = document.getElementById('lobbySessionCard_' + pk);
                    if (lobbyCard) {
                        if (isOpen && lobbyParticipatingGrid && lobbyCard.parentElement !== lobbyParticipatingGrid) {
                            lobbyParticipatingGrid.appendChild(lobbyCard);
                        } else if (!isOpen && lobbyPendingGrid && lobbyCard.parentElement !== lobbyPendingGrid) {
                            lobbyPendingGrid.appendChild(lobbyCard);
                        }
                        lobbyCard.style.border = isOpen ? '2px solid #16a34a' : '1.5px solid #cbd5e1';
                        lobbyCard.style.background = isOpen ? '#f0fdf4' : '#f8fafc';
                    }

                    // 3. 更新所有對應 Session 的狀態徽章 (⏳ 待參與 vs 🟢 參與中)
                    document.querySelectorAll('[data-session-open-badge=""' + pk + '""]').forEach(function (badge) {
                        badge.style.display = 'inline-block';
                        if (isOpen) {
                            badge.style.background = '#dcfce7';
                            badge.style.color = '#15803d';
                            badge.style.border = '1px solid #86efac';
                            badge.textContent = '🟢 參與中';
                        } else {
                            badge.style.background = '#eff6ff';
                            badge.style.color = '#1d4ed8';
                            badge.style.border = '1px solid #93c5fd';
                            badge.textContent = '⏳ 待參與';
                        }
                    });

                    // 4. 更新所有進入按鈕與結束參與按鈕
                    document.querySelectorAll('[data-session-enter-btn=""' + pk + '""]').forEach(function (enterBtn) {
                        if (isOpen) {
                            enterBtn.className = 'btn btn-outline';
                            enterBtn.style.background = '#dcfce7';
                            enterBtn.style.color = '#15803d';
                            enterBtn.style.borderColor = '#86efac';
                            enterBtn.innerHTML = '🟢 參與中 (獨立網頁進行中・禁重複進入)';
                        } else {
                            enterBtn.className = 'btn btn-primary';
                            enterBtn.style.background = '';
                            enterBtn.style.color = '';
                            enterBtn.style.borderColor = '';
                            enterBtn.innerHTML = '🚀 點選進入「參與中」獨立網頁';
                        }
                    });

                    document.querySelectorAll('[data-session-leave-btn=""' + pk + '""]').forEach(function (leaveBtn) {
                        leaveBtn.style.display = isOpen ? 'inline-block' : 'none';
                    });
                }
            });

            // 5. 更新「📂 待參與」與「🟢 參與中」場數統計徽章及空白提示
            const pendingCount = sidebarPendingBody ? sidebarPendingBody.querySelectorAll('.session-item-card').length : 0;
            const participatingCount = sidebarParticipatingBody ? sidebarParticipatingBody.querySelectorAll('.session-item-card').length : 0;

            const sidePendingBadge = document.getElementById('sidebarPendingCountBadge');
            if (sidePendingBadge) sidePendingBadge.textContent = pendingCount + ' 場';
            const sidePartBadge = document.getElementById('sidebarParticipatingCountBadge');
            if (sidePartBadge) sidePartBadge.textContent = participatingCount + ' 場';

            const lobbyPendingVal = document.getElementById('lobbyPendingSessionCountVal');
            if (lobbyPendingVal) lobbyPendingVal.textContent = pendingCount + ' 場待參與';
            const lobbyActiveVal = document.getElementById('lobbyActiveSessionCountVal');
            if (lobbyActiveVal) lobbyActiveVal.textContent = participatingCount + ' 場參與中';

            const lobbyPendingBadge = document.getElementById('lobbyPendingBadgeCount');
            if (lobbyPendingBadge) lobbyPendingBadge.textContent = pendingCount + ' 場待參與';
            const lobbyPartBadge = document.getElementById('lobbyParticipatingBadgeCount');
            if (lobbyPartBadge) lobbyPartBadge.textContent = participatingCount + ' 場參與中';

            const sideEmptyPending = document.getElementById('sidebarEmptyPendingMsg');
            if (sideEmptyPending) sideEmptyPending.style.display = pendingCount === 0 ? 'block' : 'none';
            const sideEmptyPart = document.getElementById('sidebarEmptyParticipatingMsg');
            if (sideEmptyPart) sideEmptyPart.style.display = participatingCount === 0 ? 'block' : 'none';

            const lobbyEmptyPending = document.getElementById('lobbyEmptyPendingMsg');
            if (lobbyEmptyPending) lobbyEmptyPending.style.display = pendingCount === 0 ? 'block' : 'none';
            const lobbyEmptyPart = document.getElementById('lobbyEmptyParticipatingMsg');
            if (lobbyEmptyPart) lobbyEmptyPart.style.display = participatingCount === 0 ? 'block' : 'none';
        }

        // 個人從「📂 待參與」點選議程時，開啟獨立盲投網頁並立即將該議程移入「🟢 參與中」；
        // 若該議程已在「🟢 參與中」，則攔截重複開啟並提示切換至已開啟的獨立網頁分頁
        function openIndependentVotingSession(evt, sessionPk, sessionTitle) {
            const pk = Number(sessionPk);
            const sideCard = document.getElementById('sidebarSessionCard_' + pk);
            const isFinalized = sideCard && sideCard.getAttribute('data-session-status') === 'finalized';
            if (isFinalized) {
                return true;
            }

            if (isSessionCurrentlyOpenByMe(pk)) {
                if (evt) evt.preventDefault();
                if (typeof BroadcastChannel !== 'undefined') {
                    try {
                        const ch = new BroadcastChannel('sp_voting_session_guard');
                        ch.postMessage({
                            type: 'FOCUS_EXISTING_SESSION',
                            employeePk: currentEmployeePk,
                            sessionPk: pk,
                            timestamp: new Date().toLocaleTimeString()
                        });
                    } catch (e) { }
                }
                alert('⚠️ 議程「' + (sessionTitle || ('#' + pk)) + '」目前已位於您的「🟢 參與中」清單（已在獨立網頁進行中），同一個 Session 不能重複進入！\n\n💡 請直接切換至已開啟的該場獨立盲投網頁分頁繼續操作；\n若要參與其他議程，請從「📂 待參與」清單點選其他不同議程。');
                updateLobbyActiveSessionLocks();
                return false;
            }

            // 個人從「📂 待參與」點選：立即標記進入「🟢 參與中」並同步移動卡片至「參與中」
            window.__justEnteredSessionTs = window.__justEnteredSessionTs || {};
            window.__justEnteredSessionTs[pk] = Date.now();
            if (myActiveOpenSessionPks.indexOf(pk) === -1) {
                myActiveOpenSessionPks.push(pk);
            }
            try {
                localStorage.setItem('sp_active_session_lock_' + currentEmployeePk + '_' + pk, JSON.stringify({
                    employeePk: currentEmployeePk,
                    sessionPk: pk,
                    tabInstanceId: 'pending_open_' + Date.now(),
                    lastHeartbeatMs: Date.now()
                }));
            } catch (e) { }
            updateLobbyActiveSessionLocks();
            return true;
        }

        // 從大廳點擊「🚪 結束參與」：釋放該議程的獨立網頁參與狀態並將議程移回「📂 待參與」
        function leaveParticipatingSessionFromLobby(evt, sessionPk) {
            if (evt) {
                evt.preventDefault();
                evt.stopPropagation();
            }
            const pk = Number(sessionPk);
            if (window.__justEnteredSessionTs) {
                delete window.__justEnteredSessionTs[pk];
            }
            myActiveOpenSessionPks = myActiveOpenSessionPks.filter(function (id) { return id !== pk; });
            try {
                localStorage.removeItem('sp_active_session_lock_' + currentEmployeePk + '_' + pk);
            } catch (e) { }
            if (typeof BroadcastChannel !== 'undefined') {
                try {
                    const ch = new BroadcastChannel('sp_voting_session_guard');
                    ch.postMessage({
                        type: 'FORCE_LEAVE_FROM_LOBBY',
                        employeePk: currentEmployeePk,
                        sessionPk: pk
                    });
                } catch (e) { }
            }
            updateLobbyActiveSessionLocks();
            fetch('/voting/session/leave?sessionPk=' + pk, {
                method: 'POST',
                credentials: 'same-origin'
            }).catch(function () { });
        }

        // 監聽跨分頁 BroadcastChannel 與 storage 事件，當獨立盲投網頁開啟或關閉時，大廳即時更新「待參與 / 參與中」分組
        if (typeof BroadcastChannel !== 'undefined') {
            try {
                const lobbyGuardChannel = new BroadcastChannel('sp_voting_session_guard');
                lobbyGuardChannel.onmessage = function (ev) {
                    const msg = ev.data || {};
                    if (Number(msg.employeePk) !== Number(currentEmployeePk)) return;
                    const sPk = Number(msg.sessionPk);
                    if (msg.type === 'SESSION_ACTIVE' && sPk > 0) {
                        if (myActiveOpenSessionPks.indexOf(sPk) === -1) {
                            myActiveOpenSessionPks.push(sPk);
                        }
                        updateLobbyActiveSessionLocks();
                    } else if ((msg.type === 'SESSION_CLOSED' || msg.type === 'FORCE_LEAVE_FROM_LOBBY') && sPk > 0) {
                        if (window.__justEnteredSessionTs) {
                            delete window.__justEnteredSessionTs[sPk];
                        }
                        myActiveOpenSessionPks = myActiveOpenSessionPks.filter(function (id) { return id !== sPk; });
                        updateLobbyActiveSessionLocks();
                    }
                };
            } catch (e) { }
        }
        window.addEventListener('storage', function (ev) {
            if (ev.key && ev.key.indexOf('sp_active_session_lock_' + currentEmployeePk + '_') === 0) {
                updateLobbyActiveSessionLocks();
            }
        });
        updateLobbyActiveSessionLocks();

        function isVotingPaneActive() {
            const pane = document.getElementById('pane-voting');
            return pane && pane.classList.contains('active');
        }

        function isUserTypingVoteComment() {
            const activeEl = document.activeElement;
            if (!activeEl) return false;
            const id = activeEl.id || '';
            const name = activeEl.name || '';
            return id === 'inputVoteComment' || name === 'roundDiscussionNote' || name === 'finalDecisionNote' || name === 'finalStoryPoint';
        }

        function triggerAutoFlipReload(targetSessionPk, reasonMsg) {
            if (isAutoReloading) return;
            isAutoReloading = true;
            const pk = targetSessionPk !== undefined && targetSessionPk !== null ? targetSessionPk : currentVotingSessionPk;
            const nextUrl = '/?tab=voting' + (pk > 0 ? '&sessionPk=' + pk : '') + (reasonMsg ? '&msg=' + encodeURIComponent(reasonMsg) : '');
            window.location.href = nextUrl;
        }

        // 手動點擊「🔄 重新整理」按鈕：立即向後端查詢屬於該成員的最新投票議程並刷新畫面
        function refreshVotingSessionsNow(btnEl) {
            if (btnEl) {
                btnEl.disabled = true;
                btnEl.textContent = '⏳ 重新整理中...';
            }
            fetch('/voting/liveStatus?sessionPk=' + currentVotingSessionPk + '&_t=' + Date.now(), { credentials: 'same-origin', cache: 'no-store' })
                .then(function (res) { return res.ok ? res.json() : null; })
                .then(function (data) {
                    if (data && data.found) {
                        const allPks = Array.isArray(data.allSessionPks) ? data.allSessionPks.map(Number) : [];
                        const newlyAddedPk = allPks.find(function (id) { return currentMemberSessionPks.indexOf(id) === -1; });
                        const targetPk = newlyAddedPk || data.latestOngoingSessionPk || data.votingSessionPk || currentVotingSessionPk;
                        const msg = newlyAddedPk
                            ? '🆕 已重新整理並載入屬於您的新投票議程！'
                            : '🔄 已重新整理最新投票議程清單與狀態！';
                        isAutoReloading = false;
                        triggerAutoFlipReload(targetPk, msg);
                    } else {
                        window.location.href = '/?tab=voting';
                    }
                })
                .catch(function () {
                    window.location.href = '/?tab=voting' + (currentVotingSessionPk > 0 ? '&sessionPk=' + currentVotingSessionPk : '');
                });
        }

        // 檢查並自動同步屬於該成員的最新投票議程與大廳鎖定狀態
        function pollMemberVotingStatus(preferSessionPk, customNewSessionMsg) {
            if (isAutoReloading) return;
            fetch('/voting/liveStatus?sessionPk=' + currentVotingSessionPk + '&_t=' + Date.now(), { credentials: 'same-origin', cache: 'no-store' })
                .then(function (res) { return res.ok ? res.json() : null; })
                .then(function (data) {
                    if (!data) return;
                    if (Array.isArray(data.myActiveOpenSessionPks)) {
                        updateLobbyActiveSessionLocks(data.myActiveOpenSessionPks);
                    }
                    if (!data.found) return;

                    const allPks = Array.isArray(data.allSessionPks) ? data.allSessionPks.map(Number) : [];
                    // 1. 檢查是否有「新的屬於該成員的投票議程」出現
                    const newlyAddedSessionPk = allPks.find(function (id) {
                        return currentMemberSessionPks.indexOf(id) === -1;
                    });

                    if (newlyAddedSessionPk) {
                        const targetNewPk = (preferSessionPk && allPks.indexOf(Number(preferSessionPk)) !== -1)
                            ? Number(preferSessionPk)
                            : newlyAddedSessionPk;
                        if (isVotingPaneActive()) {
                            triggerAutoFlipReload(targetNewPk, customNewSessionMsg || '🆕 有新的屬於您的投票議程出現，畫面已自動更新並切換至新議程！');
                        }
                        return;
                    }

                    if (!isVotingPaneActive()) return;

                    // 2. 檢查大廳議程清單狀態或票數是否變更
                    const statusChanged = data.sessionStatus && data.sessionStatus !== currentSessionStatus;
                    const roundChanged = Number(data.currentRoundNumber) !== Number(currentRoundNumber);
                    const voteCountChanged = Number(data.voteCount) !== Number(currentRoundVoteCount);
                    const listSigChanged = (data.sessionListSignature || '') !== currentSessionListSignature;

                    if ((statusChanged || roundChanged || voteCountChanged || listSigChanged) && !isUserTypingVoteComment()) {
                        const targetPk = (preferSessionPk && allPks.indexOf(Number(preferSessionPk)) !== -1)
                            ? Number(preferSessionPk)
                            : (currentVotingSessionPk || data.latestOngoingSessionPk || data.votingSessionPk);
                        triggerAutoFlipReload(targetPk, '');
                    }
                })
                .catch(function () { });
        }

        if (window.signalR) {
            const conn = new signalR.HubConnectionBuilder()
                .withUrl('/votingHub')
                .withAutomaticReconnect()
                .build();

            conn.on('onVotingSessionUpdated', function (payload) {
                const banner = document.getElementById('liveSignalRBanner');
                if (banner) {
                    banner.style.display = 'block';
                    banner.innerHTML = '⚡ <strong>即時同步通知 (' + payload.timestamp + ')：</strong>' + payload.messageText +
                        ' <a href=""/voting/session?sessionPk=' + payload.votingSessionPk + '"" target=""_blank"" rel=""noopener"" onclick=""return openIndependentVotingSession(event, ' + payload.votingSessionPk + ')"" class=""btn btn-primary"" style=""padding:4px 10px;font-size:12px;margin-left:10px;"">🚀 開啟此 Session 獨立盲投網頁 ↗</a>';
                }

                // 若為新議程建立、重新投票或回合設定更新，立即檢查該議程是否屬於本成員並自動更新大廳
                if (payload.eventName === 'sessionCreated' || payload.eventName === 'roundSettingUpdated' || payload.eventName === 'revote' || payload.eventName === 'resetRound') {
                    setTimeout(function () {
                        pollMemberVotingStatus(payload.votingSessionPk, payload.messageText);
                    }, 200);
                    return;
                }

                if (isVotingPaneActive() && !isUserTypingVoteComment()) {
                    setTimeout(function () {
                        pollMemberVotingStatus(null, '');
                    }, 300);
                }
            });

            conn.start().catch(function (err) { console.warn(err); });
        }

        // 每 2 秒輕量輪詢 /voting/liveStatus，同步新議程與獨立網頁鎖定狀態
        setInterval(function () {
            pollMemberVotingStatus(null, '');
        }, 2000);
    </script>
</body>
</html>");

        return sb.ToString();
    }

    /// <summary>
    /// 當同一位使用者嘗試重複進入已在另一個網頁分頁開啟的「同一個盲投 Session」時，
    /// 回傳專用的「禁止重複進入同一個盲投 Session」攔截網頁，並列出可開啟的其他不同 Session 供使用者多網頁並行參與。
    /// </summary>
    private string buildDuplicateVotingSessionBlockedHtml(
        int targetSessionPk,
        DataRow targetSessionRow,
        sqlDataService.votingSessionPresenceLease conflictLease,
        DataTable sessionTable)
    {
        string h(object? v) => WebUtility.HtmlEncode(v?.ToString() ?? string.Empty);
        int currentEmployeePk = getCurrentEmployeePk();
        string currentEmpName = User.FindFirstValue(ClaimTypes.Name) ?? "未知使用者";
        string currentDeptName = User.FindFirstValue("departmentName") ?? "";
        var myLockedPks = sqlDataService.getActiveLockedSessionPksForEmployee(currentEmployeePk);

        var otherSessionsSb = new StringBuilder();
        foreach (DataRow r in sessionTable.Rows)
        {
            int sPk = Convert.ToInt32(r["votingSessionPk"]);
            if (sPk == targetSessionPk) continue;
            bool isLocked = myLockedPks.Contains(sPk);
            string sStatus = r["sessionStatus"]?.ToString() ?? "voting";
            string sBadge = sStatus == "voting" ? "🙈 盲投進行中" : (sStatus == "revealed" ? "🎉 已翻牌揭曉" : "✅ 已拍板定案");
            otherSessionsSb.Append($@"
                <div style='display:flex;justify-content:space-between;align-items:center;padding:12px 16px;border:1px solid #cbd5e1;border-radius:10px;background:#fff;margin-bottom:10px;gap:12px;flex-wrap:wrap;'>
                    <div>
                        <div style='font-size:12px;color:#64748b;font-weight:700;'>{sBadge} ｜ 第 {h(r["currentRoundNumber"])} 回合 ｜ 標的：{h(r["targetDisplayName"])}</div>
                        <div style='font-size:15px;font-weight:800;color:#0f172a;margin-top:2px;'>{h(r["sessionCode"])} - {h(r["sessionTitle"])}</div>
                    </div>
                    <div>
                        {(isLocked
                            ? "<span style='display:inline-block;padding:6px 12px;border-radius:8px;background:#fee2e2;color:#991b1b;font-size:12.5px;font-weight:700;border:1px solid #fca5a5;'>🔒 已在另一個獨立網頁進行中</span>"
                            : $"<a href='/voting/session?sessionPk={sPk}' class='btn' style='display:inline-block;padding:8px 14px;border-radius:8px;background:#2563eb;color:#fff;text-decoration:none;font-weight:700;font-size:13px;'>🚀 在此網頁進入此不同 Session (#{sPk})</a>")}
                    </div>
                </div>");
        }

        return $@"<!DOCTYPE html>
<html lang=""zh-TW"">
<head>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />
    <title>禁止重複進入同一個盲投 Session - {h(targetSessionRow["sessionCode"])}</title>
    <style>
        body {{ font-family: 'Microsoft JhengHei', 'PingFang TC', sans-serif; background:#0f172a; color:#1e293b; margin:0; padding:32px 16px; }}
        .box {{ max-width:780px; margin:0 auto; background:#fff; border-radius:16px; padding:28px 32px; box-shadow:0 20px 40px rgba(0,0,0,0.35); border-top:6px solid #dc2626; }}
        .badge-err {{ display:inline-block; background:#fee2e2; color:#991b1b; border:1px solid #f87171; padding:4px 12px; border-radius:999px; font-size:13px; font-weight:800; }}
        .btn {{ cursor:pointer; transition:all 0.15s; }}
    </style>
</head>
<body>
    <div class=""box"">
        <div style=""display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:8px;margin-bottom:14px;"">
            <span class=""badge-err"">🚫 同一個盲投 Session 禁止重複進入</span>
            <span style=""font-size:13px;color:#64748b;"">👤 參與者：{h(currentEmpName)} ({h(currentDeptName)})</span>
        </div>
        <h2 style=""margin:0 0 10px 0;color:#991b1b;font-size:22px;"">⚠️ 您已經在另一個獨立網頁中參與此盲投 Session，不能重複進入！</h2>
        <p style=""font-size:14.5px;color:#334155;line-height:1.65;margin:0 0 16px 0;"">
            系統偵測到您的帳號（<strong>{h(currentEmpName)}</strong>）目前已開啟一個獨立網頁正在進行以下盲投議程。<br/>
            依照系統規範：<strong>每一個盲投 Session 皆以獨立網頁進行，允許您同時開啟多個網頁參與「不同」的盲投 Session，但「同一個 Session」嚴禁重複進入</strong>。
        </p>

        <div style=""background:#fef2f2;border:1px solid #fecaca;border-radius:12px;padding:16px 18px;margin-bottom:20px;"">
            <div style=""font-size:13px;color:#991b1b;font-weight:800;margin-bottom:6px;"">📌 目前已在其他獨立網頁開啟中的盲投 Session：</div>
            <div style=""font-size:17px;font-weight:800;color:#1e293b;margin-bottom:4px;"">{h(targetSessionRow["sessionCode"])} - {h(targetSessionRow["sessionTitle"])} (Session #{targetSessionPk})</div>
            <div style=""font-size:13px;color:#475569;"">
                估點標的：<strong>{h(targetSessionRow["targetDisplayName"])}</strong> ｜ 
                首次進入時間：<strong>{conflictLease.enteredAtLocal:HH:mm:ss}</strong> ｜ 
                最近心跳時間：<strong>{conflictLease.lastHeartbeatUtc.ToLocalTime():HH:mm:ss}</strong>
            </div>
        </div>

        <div style=""display:flex;gap:12px;flex-wrap:wrap;margin-bottom:24px;"">
            <button type=""button"" onclick=""notifyExistingTabAndClose()"" class=""btn"" style=""padding:10px 18px;border-radius:8px;border:none;background:#dc2626;color:#fff;font-weight:700;font-size:14px;"">
                🔦 閃爍提示已開啟的該 Session 網頁並關閉本重複分頁
            </button>
            <a href=""/?tab=voting"" class=""btn"" style=""padding:10px 18px;border-radius:8px;border:1px solid #cbd5e1;background:#f8fafc;color:#1e293b;text-decoration:none;font-weight:700;font-size:14px;"">
                🏠 返回盲投議程大廳
            </a>
        </div>

        <div style=""border-top:1px solid #e2e8f0;padding-top:18px;"">
            <h3 style=""margin:0 0 8px 0;font-size:16px;color:#0f172a;"">🌐 您可在此網頁改為參與「其他不同」的盲投 Session：</h3>
            <p style=""margin:0 0 12px 0;font-size:13px;color:#64748b;"">您可以同時開啟多個獨立網頁參與不同的盲投 Session，互不干擾：</p>
            {(otherSessionsSb.Length > 0 ? otherSessionsSb.ToString() : "<div style='padding:12px;background:#f8fafc;border-radius:8px;color:#64748b;font-size:13px;'>目前沒有其他可參與的不同盲投議程。</div>")}
        </div>
    </div>
    <script>
        const targetSessionPk = {targetSessionPk};
        const currentEmployeePk = {currentEmployeePk};
        // 透過 BroadcastChannel 通知原本已開啟該 Session 的獨立網頁閃爍提示使用者
        if (typeof BroadcastChannel !== 'undefined') {{
            try {{
                const ch = new BroadcastChannel('sp_voting_session_guard');
                ch.postMessage({{
                    type: 'DUPLICATE_ENTRY_ATTEMPT',
                    employeePk: currentEmployeePk,
                    sessionPk: targetSessionPk,
                    timestamp: new Date().toLocaleTimeString()
                }});
            }} catch (e) {{ }}
        }}

        function notifyExistingTabAndClose() {{
            if (typeof BroadcastChannel !== 'undefined') {{
                try {{
                    const ch = new BroadcastChannel('sp_voting_session_guard');
                    ch.postMessage({{
                        type: 'FOCUS_EXISTING_SESSION',
                        employeePk: currentEmployeePk,
                        sessionPk: targetSessionPk,
                        timestamp: new Date().toLocaleTimeString()
                    }});
                }} catch (e) {{ }}
            }}
            window.close();
            setTimeout(function () {{
                window.location.href = '/?tab=voting';
            }}, 300);
        }}

        // 持續檢查原本的獨立網頁是否已經關閉釋放鎖定，若已釋放則自動允許進入
        setInterval(function () {{
            fetch('/voting/liveStatus?sessionPk=' + targetSessionPk + '&_t=' + Date.now(), {{ credentials: 'same-origin', cache: 'no-store' }})
                .then(function (r) {{ return r.ok ? r.json() : null; }})
                .then(function (data) {{
                    if (data && Array.isArray(data.myActiveOpenSessionPks)) {{
                        if (data.myActiveOpenSessionPks.map(Number).indexOf(Number(targetSessionPk)) === -1) {{
                            window.location.href = '/voting/session?sessionPk=' + targetSessionPk;
                        }}
                    }}
                }})
                .catch(function () {{ }});
        }}, 2500);
    </script>
</body>
</html>";
    }

    /// <summary>
    /// 建構單一盲投 Session 的「專屬獨立網頁」（非 Modal 彈窗、非大廳內嵌）：
    /// - 讓參與者在獨立網頁中完成整個盲投 Session（包含：盲投出牌、自動翻牌揭曉、共識度檢核、開啟下一回合、拍板定案、歷次統計與具名出牌明細）
    /// - 支援同一人同時開啟多個獨立網頁參與「不同」的盲投 Session
    /// - 透過後端 Lease 心跳 + 前端 BroadcastChannel 嚴格防止「同一個 Session 重複進入」
    /// </summary>
    private string buildStandaloneVotingSessionHtml(
        int activeSessionPk,
        string assignedTabInstanceId,
        bool isAdmin,
        string? msg,
        string? err,
        DataRow activeSessionRow,
        DataTable sessionTable,
        DataTable roundTable,
        DataTable voteRecordTable,
        DataTable participantTable,
        DataTable deptTable,
        DataTable empTable,
        DataTable cardDeckTable)
    {
        string h(object? v) => WebUtility.HtmlEncode(v?.ToString() ?? string.Empty);
        int currentEmployeePk = getCurrentEmployeePk();
        string currentEmpName = User.FindFirstValue(ClaimTypes.Name) ?? "未知使用者";
        string currentDeptName = User.FindFirstValue("departmentName") ?? "未分配部門";
        string currentRoleText = isAdmin ? "👑 主持人 / 系統管理員 (admin)" : "🧑‍💻 一般參與成員 (participant)";

        string status = activeSessionRow["sessionStatus"].ToString() ?? "voting";
        int currentRoundNumber = Convert.ToInt32(activeSessionRow["currentRoundNumber"]);
        var currentRoundStatRow = roundTable.AsEnumerable()
            .FirstOrDefault(r => Convert.ToInt32(r["votingSessionPk"]) == activeSessionPk
                              && Convert.ToInt32(r["roundNumber"]) == currentRoundNumber);
        int currentRoundPk = currentRoundStatRow != null && currentRoundStatRow["votingRoundPk"] != DBNull.Value
            ? Convert.ToInt32(currentRoundStatRow["votingRoundPk"]) : 0;

        decimal sessionMinPt = activeSessionRow.Table.Columns.Contains("minStoryPoint") && activeSessionRow["minStoryPoint"] != DBNull.Value
            ? Convert.ToDecimal(activeSessionRow["minStoryPoint"]) : 0m;
        decimal sessionMaxPt = activeSessionRow.Table.Columns.Contains("maxStoryPoint") && activeSessionRow["maxStoryPoint"] != DBNull.Value
            ? Convert.ToDecimal(activeSessionRow["maxStoryPoint"]) : 21m;
        decimal sessionMaxSpread = activeSessionRow.Table.Columns.Contains("maxAllowedSpread") && activeSessionRow["maxAllowedSpread"] != DBNull.Value
            ? Convert.ToDecimal(activeSessionRow["maxAllowedSpread"]) : 5m;
        string consensusRule = activeSessionRow.Table.Columns.Contains("consensusRule") && activeSessionRow["consensusRule"] != DBNull.Value
            ? (activeSessionRow["consensusRule"].ToString() ?? "nearestFibonacci") : "nearestFibonacci";
        string consensusRuleText = activeSessionRow.Table.Columns.Contains("consensusRuleText") && activeSessionRow["consensusRuleText"] != DBNull.Value
            ? (activeSessionRow["consensusRuleText"].ToString() ?? "對齊最接近費氏牌面") : "對齊最接近費氏牌面";
        bool autoRevealWhenAllVoted = activeSessionRow.Table.Columns.Contains("autoRevealWhenAllVoted") && activeSessionRow["autoRevealWhenAllVoted"] != DBNull.Value
            && Convert.ToBoolean(activeSessionRow["autoRevealWhenAllVoted"]);
        int roundTimeLimitSecond = activeSessionRow.Table.Columns.Contains("roundTimeLimitSecond") && activeSessionRow["roundTimeLimitSecond"] != DBNull.Value
            ? Convert.ToInt32(activeSessionRow["roundTimeLimitSecond"]) : 180;
        string voteCommentPolicy = activeSessionRow.Table.Columns.Contains("voteCommentPolicy") && activeSessionRow["voteCommentPolicy"] != DBNull.Value
            ? (activeSessionRow["voteCommentPolicy"].ToString() ?? "optional") : "optional";
        string voteCommentPolicyText = activeSessionRow.Table.Columns.Contains("voteCommentPolicyText") && activeSessionRow["voteCommentPolicyText"] != DBNull.Value
            ? (activeSessionRow["voteCommentPolicyText"].ToString() ?? "自由選填") : "自由選填";
        bool restrictToParticipant = activeSessionRow.Table.Columns.Contains("restrictToParticipant") && activeSessionRow["restrictToParticipant"] != DBNull.Value
            && Convert.ToBoolean(activeSessionRow["restrictToParticipant"]);
        string roundTopicGoal = activeSessionRow.Table.Columns.Contains("roundGoalNote") && activeSessionRow["roundGoalNote"] != DBNull.Value
            ? (activeSessionRow["roundGoalNote"].ToString() ?? "") : "";

        var currentSessionParticipantRows = participantTable.AsEnumerable()
            .Where(r => Convert.ToInt32(r["votingSessionPk"]) == activeSessionPk)
            .ToList();
        var currentSessionParticipantIds = currentSessionParticipantRows
            .Select(r => Convert.ToInt32(r["employeePk"]))
            .ToHashSet();
        bool isCurrentUserAllowedToVote = !restrictToParticipant || isAdmin || currentSessionParticipantIds.Contains(currentEmployeePk);

        var currentRoundVotes = voteRecordTable.AsEnumerable()
            .Where(r => Convert.ToInt32(r["votingSessionPk"]) == activeSessionPk
                     && Convert.ToInt32(r["roundNumber"]) == currentRoundNumber)
            .ToList();

        var myCurrentVote = currentRoundVotes.FirstOrDefault(r => Convert.ToInt32(r["employeePk"]) == currentEmployeePk);
        string myVotedCardLabel = "";
        if (myCurrentVote != null)
        {
            string rawDisp = (myCurrentVote["displayCardValue"]?.ToString() ?? "").Trim();
            if (rawDisp.StartsWith("?")) myVotedCardLabel = "?";
            else if (rawDisp.StartsWith("☕")) myVotedCardLabel = "☕";
            else myVotedCardLabel = rawDisp.Replace(" pt", "").Trim();
        }
        string myVotedComment = myCurrentVote != null ? (myCurrentVote["voteComment"]?.ToString() ?? "") : "";

        string statusBannerClass = status == "voting" ? "badge-voting" : (status == "revealed" ? "badge-revealed" : "badge-finalized");
        int expectedVoterCount = restrictToParticipant && currentSessionParticipantRows.Count > 0
            ? currentSessionParticipantRows.Count
            : empTable.AsEnumerable().Count(r => Convert.ToBoolean(r["isActive"]));

        var myActiveLockedPks = sqlDataService.getActiveLockedSessionPksForEmployee(currentEmployeePk);

        var sb = new StringBuilder();
        sb.Append(@"<!DOCTYPE html>
<html lang=""zh-TW"">
<head>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />
    <title>🃏 [" + h(activeSessionRow["sessionCode"]) + @"] " + h(activeSessionRow["sessionTitle"]) + @" - 獨立盲投 Session 網頁</title>
    <style>
        :root {
            --primary: #1e40af;
            --primary-light: #3b82f6;
            --bg: #f1f5f9;
            --card-bg: #ffffff;
            --text: #0f172a;
            --muted: #64748b;
            --border: #cbd5e1;
            --success: #16a34a;
            --warning: #d97706;
            --danger: #dc2626;
        }
        * { box-sizing: border-box; }
        body {
            margin: 0;
            font-family: 'Microsoft JhengHei', 'PingFang TC', sans-serif;
            background: var(--bg);
            color: var(--text);
        }
        header {
            background: linear-gradient(135deg, #0f172a 0%, #1e3a8a 100%);
            color: #fff;
            padding: 14px 24px;
            display: flex;
            justify-content: space-between;
            align-items: center;
            flex-wrap: wrap;
            gap: 12px;
            box-shadow: 0 3px 10px rgba(0,0,0,0.2);
            position: sticky;
            top: 0;
            z-index: 200;
        }
        header h1 { margin: 0; font-size: 19px; display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
        .container { max-width: 1440px; margin: 18px auto; padding: 0 20px 48px 20px; }
        .card {
            background: var(--card-bg);
            border-radius: 12px;
            border: 1px solid var(--border);
            padding: 20px;
            margin-bottom: 20px;
            box-shadow: 0 2px 6px rgba(15,23,42,0.05);
        }
        .card-title {
            font-size: 17px;
            font-weight: 700;
            margin: 0 0 14px 0;
            color: #1e293b;
            display: flex;
            align-items: center;
            justify-content: space-between;
            flex-wrap: wrap;
            gap: 8px;
            border-bottom: 1px solid #e2e8f0;
            padding-bottom: 10px;
        }
        .grid-2 { display: grid; grid-template-columns: 1.25fr 1fr; gap: 20px; }
        @media (max-width: 1024px) { .grid-2 { grid-template-columns: 1fr; } }
        .badge {
            display: inline-block;
            padding: 3px 10px;
            border-radius: 999px;
            font-size: 12px;
            font-weight: 700;
        }
        .badge-voting { background: #fef3c7; color: #b45309; border: 1px solid #f59e0b; }
        .badge-revealed { background: #dbeafe; color: #1d4ed8; border: 1px solid #60a5fa; }
        .badge-finalized { background: #dcfce7; color: #15803d; border: 1px solid #4ade80; }
        .alert {
            padding: 12px 16px;
            border-radius: 8px;
            margin-bottom: 16px;
            font-weight: 600;
            font-size: 14px;
        }
        .alert-msg { background: #dcfce7; color: #166534; border: 1px solid #86efac; }
        .alert-err { background: #fee2e2; color: #991b1b; border: 1px solid #fca5a5; }
        .alert-live { background: #eff6ff; color: #1e40af; border: 1px solid #93c5fd; display: none; }
        .poker-table-box {
            background: radial-gradient(circle, #15803d 0%, #14532d 100%);
            border: 8px solid #78350f;
            border-radius: 24px;
            padding: 24px;
            color: #fff;
            box-shadow: inset 0 0 30px rgba(0,0,0,0.45), 0 8px 20px rgba(0,0,0,0.18);
            margin-bottom: 20px;
        }
        .seat-grid {
            display: grid;
            grid-template-columns: repeat(auto-fill, minmax(175px, 1fr));
            gap: 14px;
            margin-top: 16px;
        }
        .seat-card {
            background: rgba(255,255,255,0.12);
            border: 1px solid rgba(255,255,255,0.28);
            border-radius: 12px;
            padding: 12px;
            text-align: center;
            backdrop-filter: blur(4px);
        }
        .playing-card {
            width: 68px;
            height: 94px;
            margin: 8px auto;
            border-radius: 8px;
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            font-weight: 800;
            font-size: 22px;
            box-shadow: 0 4px 10px rgba(0,0,0,0.35);
            transition: transform 0.25s;
        }
        .playing-card-hidden {
            background: repeating-linear-gradient(45deg, #1e3a8a, #1e3a8a 8px, #1d4ed8 8px, #1d4ed8 16px);
            border: 2px solid #fff;
            color: #fbbf24;
        }
        .playing-card-revealed {
            background: #ffffff;
            border: 3px solid #f59e0b;
            color: #0f172a;
        }
        .playing-card-empty {
            background: rgba(255,255,255,0.08);
            border: 2px dashed rgba(255,255,255,0.4);
            color: rgba(255,255,255,0.6);
            font-size: 13px;
        }
        .deck-bar { display: flex; flex-wrap: wrap; gap: 10px; margin: 14px 0; }
        .deck-card-btn {
            width: 82px;
            min-height: 112px;
            padding: 6px 4px;
            border-radius: 10px;
            border: 2px solid #94a3b8;
            background: #fff;
            color: #0f172a;
            font-size: 21px;
            font-weight: 800;
            cursor: pointer;
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            box-shadow: 0 3px 6px rgba(0,0,0,0.08);
            transition: all 0.15s;
        }
        .deck-card-btn span {
            font-size: 11px;
            font-weight: 700;
            color: #475569;
            margin-top: 5px;
            line-height: 1.25;
            text-align: center;
            word-break: keep-all;
        }
        .deck-card-btn:hover, .deck-card-btn.selected {
            border-color: #2563eb;
            background: #eff6ff;
            transform: translateY(-5px);
            box-shadow: 0 8px 16px rgba(37,99,235,0.25);
        }
        table { width: 100%; border-collapse: collapse; font-size: 13.5px; }
        th, td { border: 1px solid #e2e8f0; padding: 9px 11px; text-align: left; vertical-align: top; }
        th { background: #f8fafc; color: #334155; font-weight: 700; }
        tr:nth-child(even) { background: #fcfcfd; }
        .table-scroll-wrap {
            max-height: 520px;
            overflow: auto;
            border: 1px solid #e2e8f0;
            border-radius: 8px;
            background: #fff;
        }
        .table-scroll-wrap table { border-collapse: separate; border-spacing: 0; }
        th.sp-sortable-th { position: relative; transition: background 0.15s; }
        th.sp-sortable-th:hover { background: #eef2ff; }
        th.sp-th-locked {
            position: sticky !important;
            top: 0 !important;
            z-index: 25 !important;
            background: #eff6ff !important;
            box-shadow: 0 2px 5px rgba(30, 64, 175, 0.15);
            border-bottom: 2px solid #3b82f6 !important;
        }
        .sp-th-toolbar { display: inline-flex; align-items: center; gap: 3px; margin-left: 5px; vertical-align: middle; }
        .sp-th-btn {
            border: 1px solid #cbd5e1;
            background: #ffffff;
            color: #475569;
            border-radius: 4px;
            padding: 1px 5px;
            font-size: 11px;
            line-height: 1.2;
            cursor: pointer;
            user-select: none;
            transition: all 0.15s;
        }
        .sp-th-btn:hover { background: #dbeafe; color: #1e40af; border-color: #93c5fd; }
        .sp-th-btn.active-sort { background: #2563eb; color: #ffffff; border-color: #1d4ed8; font-weight: 800; }
        .sp-th-btn.active-lock { background: #fef3c7; color: #b45309; border-color: #f59e0b; font-weight: 800; }
        input[type=text], input[type=number], select, textarea {
            width: 100%;
            padding: 8px 10px;
            border: 1px solid #cbd5e1;
            border-radius: 6px;
            font-size: 14px;
            font-family: inherit;
        }
        label {
            display: flex;
            align-items: center;
            gap: 5px;
            font-size: 13px;
            font-weight: 700;
            margin-bottom: 4px;
            color: #334155;
            white-space: nowrap;
        }
        .caption-help-icon {
            display: inline-flex;
            align-items: center;
            justify-content: center;
            width: 16px;
            height: 16px;
            border-radius: 50%;
            background: #e0e7ff;
            color: #1d4ed8;
            font-size: 11px;
            font-weight: 800;
            cursor: help;
            position: relative;
            flex-shrink: 0;
            user-select: none;
        }
        .caption-help-icon:hover::after {
            content: attr(data-tip);
            position: absolute;
            left: 0;
            bottom: calc(100% + 6px);
            background: #0f172a;
            color: #f8fafc;
            padding: 8px 11px;
            border-radius: 8px;
            font-size: 12px;
            font-weight: 500;
            white-space: normal;
            width: max-content;
            max-width: 320px;
            line-height: 1.5;
            z-index: 9999;
            box-shadow: 0 6px 18px rgba(0,0,0,0.28);
            pointer-events: none;
        }
        .btn {
            display: inline-block;
            padding: 9px 16px;
            border-radius: 8px;
            border: none;
            font-size: 14px;
            font-weight: 700;
            cursor: pointer;
            text-decoration: none;
        }
        .btn-primary { background: #2563eb; color: #fff; }
        .btn-success { background: #16a34a; color: #fff; }
        .btn-warning { background: #d97706; color: #fff; }
        .btn-danger { background: #dc2626; color: #fff; }
        .btn-outline { background: #fff; color: #1e293b; border: 1px solid #cbd5e1; }
        .form-row { margin-bottom: 12px; }
        table tbody tr { cursor: context-menu; }
        tr.sp-context-active-row td { background: #dbeafe !important; }
        .sp-context-menu { position: fixed; z-index: 10050; min-width: 260px; max-width: 360px; background: #ffffff; border: 1px solid #cbd5e1; border-radius: 12px; box-shadow: 0 14px 34px rgba(15, 23, 42, 0.24); padding: 6px; display: none; user-select: none; }
        .sp-context-menu.visible, .sp-context-menu.open { display: block !important; }
        .sp-ctx-header { padding: 7px 10px; font-size: 12px; font-weight: 800; color: #1e3a8a; background: #eff6ff; border-radius: 8px; margin-bottom: 5px; border: 1px solid #bfdbfe; }
        .sp-ctx-item { display: flex; align-items: center; justify-content: space-between; gap: 10px; width: 100%; text-align: left; border: none; background: transparent; padding: 8px 10px; font-size: 13px; font-weight: 700; color: #1e293b; border-radius: 7px; cursor: pointer; }
        .sp-ctx-item:hover { background: #eff6ff; color: #1d4ed8; }
        .sp-modal-overlay { position: fixed; inset: 0; z-index: 10040; background: rgba(15, 23, 42, 0.58); backdrop-filter: blur(2px); display: none; align-items: center; justify-content: center; padding: 18px; }
        .sp-modal-overlay.open { display: flex !important; }
        .sp-modal-dialog { background: #ffffff; border: 1px solid #cbd5e1; border-radius: 14px; width: 100%; max-width: 720px; max-height: 88vh; display: flex; flex-direction: column; box-shadow: 0 22px 48px rgba(15, 23, 42, 0.34); overflow: hidden; }
        .sp-modal-header { display: flex; align-items: center; justify-content: space-between; padding: 14px 18px; background: linear-gradient(135deg, #0f172a 0%, #1e3a8a 100%); color: #ffffff; font-weight: 800; }
        .sp-modal-body { padding: 18px; overflow-y: auto; }
        .sp-modal-footer { display: flex; align-items: center; justify-content: flex-end; gap: 10px; padding-top: 12px; border-top: 1px solid #e2e8f0; margin-top: 12px; }
    </style>
</head>
<body>
    <header>
        <div>
            <h1>
                <span>🃏 獨立盲投 Session 網頁</span>
                <span style=""background:#2563eb;padding:3px 10px;border-radius:999px;font-size:12.5px;"">" + h(activeSessionRow["sessionCode"]) + @" - " + h(activeSessionRow["sessionTitle"]) + @"</span>
                <span style=""background:#16a34a;padding:3px 10px;border-radius:999px;font-size:12px;"">🟢 參與中・單一 Session 防重複進入保護生效中</span>
            </h1>
            <div style=""font-size:12px;color:#cbd5e1;margin-top:4px;"">
                💡 您已從「📂 待參與」點選進入本場「🟢 參與中」獨立盲投 Session；您可以同時從「待參與」開啟多個獨立網頁參與「不同」Session，但同一個 Session 禁止重複進入。
            </div>
        </div>
        <div style=""display:flex;align-items:center;gap:10px;flex-wrap:wrap;"">
            <div style=""background:rgba(255,255,255,0.14);padding:6px 12px;border-radius:8px;font-size:12.5px;"">
                👤 <strong>" + h(currentEmpName) + @"</strong> (" + h(currentDeptName) + @") ｜ " + currentRoleText + @"
            </div>
            <button type=""button"" onclick=""refreshStandaloneSessionNow(this)"" class=""btn btn-outline"" style=""padding:6px 12px;font-size:12.5px;background:rgba(255,255,255,0.15);color:#fff;border-color:rgba(255,255,255,0.35);"">
                🔄 重新整理本頁
            </button>
            <a href=""/help?topic=topic-voting-standalone"" target=""_blank"" class=""btn btn-outline"" style=""padding:6px 12px;font-size:12.5px;background:#0d9488;color:#fff;border-color:#5eead4;font-weight:800;"" title=""開啟系統線上說明與獨立盲投操作畫面手冊"">
                📖 線上說明
            </a>
            <a href=""/voting/session/leave?sessionPk=" + activeSessionPk + @"&tabInstanceId=" + h(assignedTabInstanceId) + @"&redirectLobby=true"" onclick=""onExplicitLeaveSessionClick()"" class=""btn btn-danger"" style=""padding:6px 12px;font-size:12.5px;"">
                🚪 結束參與並返回大廳 (移回待參與)
            </a>
            <a href=""/logout"" onclick=""onExplicitLeaveSessionClick()"" class=""btn btn-outline"" style=""padding:6px 12px;font-size:12.5px;background:#dc2626;color:#fff;border-color:#f87171;font-weight:800;"" title=""安全登出目前帳號、釋放本議程鎖定並返回員工登入頁面"">
                🚪 登出系統
            </a>
        </div>
    </header>

    <div class=""container"">
        <div id=""duplicateAttemptBanner"" class=""alert"" style=""display:none;background:#fef3c7;color:#92400e;border:2px solid #f59e0b;""></div>
        <div id=""liveSignalRBanner"" class=""alert alert-live""></div>");

        if (!string.IsNullOrWhiteSpace(msg))
        {
            sb.Append($"<div class='alert alert-msg'>✅ {h(msg)}</div>");
        }
        if (!string.IsNullOrWhiteSpace(err))
        {
            sb.Append($"<div class='alert alert-err'>⚠️ {h(err)}</div>");
        }

        // 頂部快速開啟「其他不同盲投 Session」橫條（支援同時以多個獨立網頁參與不同 Session）
        sb.Append(@"
        <div class=""card"" style=""padding:14px 18px;margin-bottom:18px;background:linear-gradient(135deg,#f8fafc 0%,#eff6ff 100%);"">
            <div style=""display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:10px;"">
                <div style=""font-size:13px;font-weight:700;color:#1e3a8a;"">
                    🌐 個人「待參與 ／ 參與中」多網頁並行導覽列（點選「📂 待參與」議程將在新的獨立網頁開啟並進入「🟢 參與中」）：
                </div>
                <div style=""display:flex;gap:8px;flex-wrap:wrap;"">");

        foreach (DataRow sRow in sessionTable.Rows)
        {
            int sPk = Convert.ToInt32(sRow["votingSessionPk"]);
            string sCode = sRow["sessionCode"]?.ToString() ?? "";
            string sTitle = sRow["sessionTitle"]?.ToString() ?? "";
            string sSt = sRow["sessionStatus"]?.ToString() ?? "voting";
            if (sSt == "finalized" && sPk != activeSessionPk) continue;
            bool isCurrent = sPk == activeSessionPk;
            bool isOtherLocked = !isCurrent && myActiveLockedPks.Contains(sPk);

            if (isCurrent)
            {
                sb.Append($@"
                    <span style='padding:5px 11px;border-radius:8px;background:#15803d;color:#fff;font-size:12px;font-weight:800;'>
                        🟢 參與中 (本網頁進行中)：{h(sCode)} - {h(sTitle)}
                    </span>");
            }
            else
            {
                sb.Append($@"
                    <a href='/voting/session?sessionPk={sPk}' target='_blank' rel='noopener' onclick='return openOtherIndependentSession(event, {sPk})' id='otherSessionLink_{sPk}' style='padding:5px 11px;border-radius:8px;background:{(isOtherLocked ? "#dcfce7" : "#ffffff")};color:{(isOtherLocked ? "#15803d" : "#1e293b")};border:1px solid {(isOtherLocked ? "#86efac" : "#cbd5e1")};font-size:12px;font-weight:700;text-decoration:none;'>
                        {(isOtherLocked ? "🟢 參與中 (已在另一獨立網頁進行・禁重複進入)" : "📂 待參與 (點選進入參與中獨立網頁)")}：{h(sCode)} - {h(sTitle)} ↗
                    </a>");
            }
        }

        sb.Append(@"
                </div>
            </div>
        </div>");

        string savedFinalDecisionNote = activeSessionRow.Table.Columns.Contains("finalDecisionNote") && activeSessionRow["finalDecisionNote"] != DBNull.Value
            ? (activeSessionRow["finalDecisionNote"]?.ToString() ?? "")
            : "";
        bool hasFinalizedPoint = activeSessionRow["finalStoryPoint"] != DBNull.Value;

        // 綠色撲克牌桌面區塊
        sb.Append($@"
        <div class=""poker-table-box"">
            <div style=""display:flex;justify-content:space-between;align-items:flex-start;flex-wrap:wrap;gap:12px;"">
                <div>
                    <span class=""badge {statusBannerClass}"">{h(activeSessionRow["sessionStatusText"])}</span>
                    <span style=""margin-left:8px;font-size:13px;opacity:0.9;"">議程代碼：{h(activeSessionRow["sessionCode"])} ｜ 第 <strong>{currentRoundNumber}</strong> 回合</span>
                    <h2 style=""margin:8px 0 4px 0;font-size:22px;"">{h(activeSessionRow["sessionTitle"])}</h2>
                    <div style=""font-size:14px;color:#fde68a;"">
                        🎯 估點標的類型：<strong>{h(activeSessionRow["targetKindText"])}</strong> ｜ 標的名稱：<strong>{h(activeSessionRow["targetDisplayName"])}</strong>
                        {(hasFinalizedPoint ? $" ｜ 🏆 已拍板最終點數：<strong>{activeSessionRow["finalStoryPoint"]} pt</strong>" : "")}
                    </div>
                    <div style=""margin-top:8px;display:flex;flex-wrap:wrap;gap:8px;font-size:12px;"">
                        <span style=""background:rgba(0,0,0,0.35);padding:3px 10px;border-radius:999px;border:1px solid rgba(255,255,255,0.25);"">📏 點數範圍：<strong>{sessionMinPt:0.##} ~ {sessionMaxPt:0.##} pt</strong></span>
                        <span style=""background:rgba(0,0,0,0.35);padding:3px 10px;border-radius:999px;border:1px solid rgba(255,255,255,0.25);"">🤝 共識規則：<strong>{h(consensusRuleText)}</strong> (極差 ≤ {sessionMaxSpread:0.##} pt)</span>
                        <span style=""background:rgba(0,0,0,0.35);padding:3px 10px;border-radius:999px;border:1px solid rgba(255,255,255,0.25);"">⚡ 全員投完自動翻牌：<strong>{(autoRevealWhenAllVoted ? "開啟" : "關閉")}</strong></span>
                        <span style=""background:rgba(0,0,0,0.35);padding:3px 10px;border-radius:999px;border:1px solid rgba(255,255,255,0.25);"">⏱️ 單回合時限：<strong>{(roundTimeLimitSecond > 0 ? $"{roundTimeLimitSecond} 秒" : "不限時")}</strong></span>
                        <span style=""background:rgba(0,0,0,0.35);padding:3px 10px;border-radius:999px;border:1px solid rgba(255,255,255,0.25);"">💬 出牌理由政策：<strong>{h(voteCommentPolicyText)}</strong></span>
                        <span style=""background:rgba(0,0,0,0.35);padding:3px 10px;border-radius:999px;border:1px solid rgba(255,255,255,0.25);"">👥 參與模式：<strong>{(restrictToParticipant ? $"僅限指定參與者 ({currentSessionParticipantRows.Count} 人)" : $"全體在職員工 ({expectedVoterCount} 人)")}</strong></span>
                    </div>
                    {(!string.IsNullOrWhiteSpace(roundTopicGoal) ? $@"<div style=""margin-top:8px;background:rgba(254,243,199,0.18);border-left:4px solid #fbbf24;padding:6px 12px;border-radius:6px;font-size:13px;color:#fef08a;"">📢 <strong>本回合聚焦目標 (第 {currentRoundNumber} 回合)：</strong>{h(roundTopicGoal)}</div>" : "")}
                    {(hasFinalizedPoint ? $@"<div style=""margin-top:8px;background:rgba(220,252,231,0.22);border-left:4px solid #4ade80;padding:7px 12px;border-radius:6px;font-size:13.5px;color:#bbf7d0;"">🏆 <strong>已拍板定案決議（最終 Story Point：{activeSessionRow["finalStoryPoint"]} pt）：</strong>{(!string.IsNullOrWhiteSpace(savedFinalDecisionNote) ? h(savedFinalDecisionNote) : "（已完成拍板定案）")}</div>" : "")}
                </div>
                <div style=""text-align:right;font-size:13px;opacity:0.9;"">
                    主持人：{h(activeSessionRow["hostEmployeeName"])}<br/>
                    本回合已出牌：<strong>{currentRoundVotes.Count} / {expectedVoterCount}</strong> 人
                    {(roundTimeLimitSecond > 0 && status == "voting" ? $@"<div id=""roundCountdownBox"" data-limit=""{roundTimeLimitSecond}"" style=""margin-top:6px;background:#991b1b;color:#fef08a;padding:4px 10px;border-radius:8px;font-weight:800;font-size:13px;display:inline-block;"">⏳ 倒數計時：{roundTimeLimitSecond} 秒</div>" : "")}
                </div>
            </div>

            <div class=""seat-grid"">");

        var seatEmployees = restrictToParticipant && currentSessionParticipantRows.Count > 0
            ? empTable.AsEnumerable().Where(r => Convert.ToBoolean(r["isActive"]) && currentSessionParticipantIds.Contains(Convert.ToInt32(r["employeePk"]))).ToList()
            : empTable.AsEnumerable().Where(r => Convert.ToBoolean(r["isActive"])).ToList();

        foreach (DataRow emp in seatEmployees)
        {
            int empPk = Convert.ToInt32(emp["employeePk"]);
            string empName = emp["employeeName"].ToString() ?? "";
            string deptName = emp["departmentName"].ToString() ?? "";
            var vote = currentRoundVotes.FirstOrDefault(v => Convert.ToInt32(v["employeePk"]) == empPk);
            bool isSelf = empPk == currentEmployeePk;
            string voteCleanCard = "";
            if (vote != null)
            {
                string rawDisp = (vote["displayCardValue"]?.ToString() ?? "").Trim();
                if (rawDisp.StartsWith("?")) voteCleanCard = "?";
                else if (rawDisp.StartsWith("☕")) voteCleanCard = "☕";
                else voteCleanCard = rawDisp.Replace(" pt", "").Trim();
            }

            sb.Append($@"<div class=""seat-card"" style=""{(isSelf ? "border:2px solid #fbbf24;background:rgba(251,191,36,0.18);" : "")}"">
                <div style=""font-size:13px;font-weight:700;"">{h(empName)} {(isSelf ? "<span style='color:#fde047;'>(您)</span>" : "")}</div>
                <div style=""font-size:11px;opacity:0.8;"">{h(deptName)}</div>");

            if (vote == null)
            {
                sb.Append(@"<div class=""playing-card playing-card-empty"">思考中...</div>
                            <div style=""font-size:11.5px;color:#cbd5e1;"">尚未出牌</div>");
            }
            else if (status == "voting")
            {
                sb.Append($@"<div class=""playing-card playing-card-hidden"">🃏</div>
                            <div style=""font-size:12px;color:#fde047;font-weight:700;"">✅ 已蓋牌{(isSelf ? $" ({h(voteCleanCard)})" : " (盲投保密)")}</div>");
            }
            else
            {
                string cardMeaningSeat = getPokerCardMeaning(voteCleanCard).shortMeaning;
                sb.Append($@"<div class=""playing-card playing-card-revealed"" title=""{h(cardMeaningSeat)}"">
                                <div>{h(voteCleanCard)}</div>
                                <div style=""font-size:10px;font-weight:700;color:#475569;margin-top:3px;line-height:1.15;padding:0 2px;"">{h(cardMeaningSeat)}</div>
                            </div>
                            <div style=""font-size:11.5px;color:#fef08a;"">{h(vote["voteComment"])}</div>");
            }

            sb.Append("</div>");
        }

        sb.Append("</div></div>"); // end seat-grid & poker-table-box

        // 下方左右雙欄：左側為「出牌區 + 主持人控制台」，右側為「本 Session 歷次出牌統計與具名明細」
        sb.Append("<div class='grid-2'>");

        // 左欄
        sb.Append("<div>");
        if (status == "voting")
        {
            if (!isCurrentUserAllowedToVote)
            {
                sb.Append($@"
                <div class=""card"" style=""border-left:5px solid #f59e0b;"">
                    <div class=""card-title""><span>🔒 本議程採「僅限指定參與者名單」投票模式</span></div>
                    <div style=""font-size:14px;color:#475569;line-height:1.6;"">
                        您目前不在本議程的指定投票參與者名單中，可於上方撲克牌桌即時觀看盲投進度。
                    </div>
                </div>");
            }
            else
            {
                bool isCommentMandatory = voteCommentPolicy == "requiredAll" || voteCommentPolicy == "mandatoryAll";
                string commentPlaceholder = (voteCommentPolicy == "requiredExtreme" || voteCommentPolicy == "mandatoryExtreme")
                    ? "當出極端牌（最高/最低）或特殊牌（? / ☕）時必填理由；一般牌可選填..."
                    : (isCommentMandatory ? "【必填】請輸入您的複雜度評估依據與風險假設..." : "例如：需串接舊版 SOAP API 與處理重試交易，建議預留測試工時...");

                string? rawCardCsv = activeSessionRow.Table.Columns.Contains("cardValueCsv") && activeSessionRow["cardValueCsv"] != DBNull.Value
                    ? activeSessionRow["cardValueCsv"].ToString()
                    : "0,0.5,1,2,3,5,8,13,21,?,☕";
                string normalizedDeckCsv = normalizeCardDeckCsv(rawCardCsv);
                string[] deckCards = normalizedDeckCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);

                sb.Append($@"
                <div class=""card"">
                    <div class=""card-title"">
                        <span>🖐️ 選擇您的撲克牌進行盲投（第 {currentRoundNumber} 回合）</span>
                        <span style=""font-size:12px;color:#64748b;"">範圍：{sessionMinPt:0.##} ~ {sessionMaxPt:0.##} pt ｜ 理由政策：{h(voteCommentPolicyText)}</span>
                    </div>
                    <form method=""post"" action=""/voting/cast"" id=""formCastVote"" onsubmit=""window.__isInternalSessionNavigation=true; isAutoReloading=true;"">
                        <input type=""hidden"" name=""votingSessionPk"" value=""{activeSessionPk}"" />
                        <input type=""hidden"" name=""fromStandalone"" value=""true"" />
                        <input type=""hidden"" name=""tabInstanceId"" value=""{h(assignedTabInstanceId)}"" />
                        <input type=""hidden"" name=""cardValue"" id=""inputCardLabel"" value=""{(string.IsNullOrEmpty(myVotedCardLabel) ? "5" : h(myVotedCardLabel))}"" />

                        <div class=""form-row"">
                            <div style=""display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:6px;margin-bottom:6px;"">
                                <label style=""margin:0;"">1. 點選撲克牌面（依本議程允許點數範圍 {sessionMinPt:0.##} ~ {sessionMaxPt:0.##} pt 過濾｜<strong>雙擊牌面可直接出牌</strong>）：<span class=""caption-help-icon"" data-tip=""點擊下方撲克牌選擇您預估的 Story Point，或直接雙擊撲克牌一鍵送出。翻牌前所有人僅看得到您『已蓋牌』而看不到點數；在主持人翻牌前您可隨時改牌重投。"">ⓘ</span></label>
                                <label style=""display:inline-flex;align-items:center;gap:5px;font-size:12px;color:#1e40af;background:#eff6ff;border:1px solid #93c5fd;padding:3px 9px;border-radius:999px;cursor:pointer;margin:0;"">
                                    <input type=""checkbox"" id=""cbQuickSingleClickCast"" style=""width:auto;margin:0;"" {(!isCommentMandatory ? "checked" : "")} />
                                    <span>⚡ 點選牌面即直接送出盲投</span>
                                </label>
                            </div>
                            <div class=""deck-bar"">");

                foreach (string rawToken in deckCards)
                {
                    string cLabel = rawToken.Trim();
                    if (decimal.TryParse(cLabel, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal ptVal))
                    {
                        if (ptVal < sessionMinPt || ptVal > sessionMaxPt)
                        {
                            continue;
                        }
                    }

                    string cMeaning = getPokerCardMeaning(cLabel).shortMeaning;
                    bool isSelected = (!string.IsNullOrEmpty(myVotedCardLabel) && myVotedCardLabel == cLabel)
                                      || (string.IsNullOrEmpty(myVotedCardLabel) && cLabel == "5");
                    sb.Append($@"
                                <button type=""button"" class=""deck-card-btn {(isSelected ? "selected" : "")}"" onclick=""selectCard('{h(cLabel)}', this, false)"" ondblclick=""selectCard('{h(cLabel)}', this, true)"" title=""{h(cLabel)}：{h(cMeaning)}（單擊選牌／雙擊直接送出）"">
                                    {h(cLabel)}
                                    <span>{h(cMeaning)}</span>
                                </button>");
                }

                sb.Append($@"
                            </div>
                        </div>
                        <div class=""form-row"">
                            <label>2. 出牌理由 / 風險假設備註（{h(voteCommentPolicyText)}）：<span class=""caption-help-icon"" data-tip=""填寫您給出此點數的技術評估依據、未知風險或邊界條件，翻牌後將協助團隊快速聚焦最高/最低票差異原因。"">ⓘ</span></label>
                            <input type=""text"" name=""voteComment"" id=""inputVoteComment"" value=""{h(myVotedComment)}"" placeholder=""{h(commentPlaceholder)}"" {(isCommentMandatory ? "required" : "")} />
                        </div>
                        <button type=""submit"" class=""btn btn-primary"" style=""width:100%;padding:11px;"">
                            🃏 送出盲投出牌（目前已選：<span id=""selectedCardText"">{(string.IsNullOrEmpty(myVotedCardLabel) ? "5" : h(myVotedCardLabel))}</span>）
                        </button>
                    </form>
                </div>");
            }
        }

        if (isAdmin)
        {
            decimal curMin = currentRoundStatRow != null && currentRoundStatRow["minPoint"] != DBNull.Value
                ? Convert.ToDecimal(currentRoundStatRow["minPoint"]) : 0m;
            decimal curMax = currentRoundStatRow != null && currentRoundStatRow["maxPoint"] != DBNull.Value
                ? Convert.ToDecimal(currentRoundStatRow["maxPoint"]) : 0m;
            decimal curSpread = curMax - curMin;
            bool isSpreadOverLimit = status == "revealed" && curSpread > sessionMaxSpread;

            decimal suggestedFinalPt = 5m;
            if (currentRoundStatRow != null && currentRoundStatRow["averagePoint"] != DBNull.Value)
            {
                decimal avgVal = Convert.ToDecimal(currentRoundStatRow["averagePoint"]);
                decimal[] fibDeck = new[] { 0m, 0.5m, 1m, 2m, 3m, 5m, 8m, 13m, 21m, 34m, 55m, 89m };
                if (consensusRule == "nearestFibonacci" || consensusRule == "averageNearest")
                {
                    suggestedFinalPt = fibDeck.OrderBy(f => Math.Abs(f - avgVal)).First();
                }
                else
                {
                    suggestedFinalPt = Math.Round(avgVal, 1);
                }
            }

            string currentRoundSavedNote = currentRoundStatRow != null && currentRoundStatRow["roundDiscussionNote"] != DBNull.Value
                ? (currentRoundStatRow["roundDiscussionNote"]?.ToString() ?? "")
                : "";

            sb.Append($@"
            <div class=""card"">
                <div class=""card-title"">
                    <span>🎛️ 議程主持人控制台（翻牌揭曉 / 進入下一輪重投 / 拍板定案）</span>
                    <span class=""badge badge-revealed"">僅主持人 / 管理員可見</span>
                </div>
                {(isSpreadOverLimit ? $@"<div style=""background:#fef2f2;border:1px solid #f87171;color:#991b1b;padding:10px 14px;border-radius:8px;margin-bottom:12px;font-size:13px;font-weight:700;"">⚠️ 分歧度超標提醒：本回合點數極差為 <strong>{curSpread:0.##} pt</strong>，超過門檻 (<strong>{sessionMaxSpread:0.##} pt</strong>)！建議邀請最高票與最低票成員說明後，點擊「🔄 開啟下一回合重新盲投」。</div>" : "")}
                <form method=""post"" action=""/voting/control"" id=""formControlSession"" onsubmit=""window.__isInternalSessionNavigation=true; isAutoReloading=true;"">
                    <input type=""hidden"" name=""votingSessionPk"" value=""{activeSessionPk}"" />
                    <input type=""hidden"" name=""fromStandalone"" value=""true"" />
                    <input type=""hidden"" name=""tabInstanceId"" value=""{h(assignedTabInstanceId)}"" />
                    <input type=""hidden"" name=""controlAction"" id=""controlActionType"" value=""{(status == "revealed" ? "nextRound" : "reveal")}"" />
                    <div class=""form-row"">
                        <label>本回合討論紀錄 / 下回合聚焦目標（翻牌、開啟下一回合或定案時皆會保存）：<span class=""caption-help-icon"" data-tip=""記錄本回合翻牌後的團隊討論共識；若開啟下一回合，此欄內容會寫入本回合討論紀錄，並同時作為下一回合桌面頂部的聚焦目標提示。"">ⓘ</span></label>
                        <input type=""text"" name=""roundDiscussionNote"" id=""inputRoundDiscussionNote"" value=""{h(currentRoundSavedNote)}"" placeholder=""例如：第一輪極差較大(5 vs 13)，請最高與最低分同仁說明後進行第二輪盲投..."" />
                    </div>
                    <div style=""display:flex;gap:10px;flex-wrap:wrap;margin-bottom:14px;"">
                        <button type=""submit"" name=""submitAction"" value=""reveal"" class=""btn btn-warning"" onclick=""document.getElementById('controlActionType').value='reveal'"">
                            👀 1. 立即翻牌揭曉 (Reveal Cards)
                        </button>
                        <button type=""submit"" name=""submitAction"" value=""nextRound"" class=""btn btn-primary"" onclick=""document.getElementById('controlActionType').value='nextRound'"">
                            🔄 2. 開啟下一回合重新盲投 (Next Round)
                        </button>
                        <button type=""submit"" name=""submitAction"" value=""resetRound"" class=""btn btn-outline"" style=""border-color:#d97706;color:#b45309;background:#fffbeb;"" onclick=""document.getElementById('controlActionType').value='resetRound'"">
                            🔁 3. 重新投票 (清空本回合已出牌 / 重啟盲投)
                        </button>
                        <button type=""submit"" name=""submitAction"" value=""finalize"" class=""btn btn-success"" onclick=""document.getElementById('inputFinalStoryPoint').value='{suggestedFinalPt.ToString(CultureInfo.InvariantCulture)}'; document.getElementById('controlActionType').value='finalize';"">
                            🏆 4. 一鍵採用推薦點數 ({suggestedFinalPt:0.##} pt) 拍板定案
                        </button>
                    </div>
                    <div style=""background:#f8fafc;border:1px solid #cbd5e1;padding:12px;border-radius:8px;"">
                        <div style=""font-weight:700;margin-bottom:8px;color:#15803d;"">
                            ✅ 達成共識拍板定案（自動回寫工項之最終 Story Point｜依「{h(consensusRuleText)}」推薦值：<strong>{suggestedFinalPt:0.##} pt</strong>）
                        </div>
                        {(hasFinalizedPoint ? $@"<div style=""background:#dcfce7;border:1px solid #86efac;color:#166534;padding:9px 12px;border-radius:8px;margin-bottom:10px;font-size:13px;font-weight:700;"">🏆 本議程已完成拍板定案：最終核定點數 <strong>{activeSessionRow["finalStoryPoint"]} pt</strong> ｜ 定案決議說明：<strong>{(!string.IsNullOrWhiteSpace(savedFinalDecisionNote) ? h(savedFinalDecisionNote) : "（未填寫決議說明）")}</strong></div>" : "")}
                        <div style=""display:grid;grid-template-columns:140px 1fr auto;gap:8px;align-items:end;"">
                            <div>
                                <label>最終定案點數<span class=""caption-help-icon"" data-tip=""團隊達成共識後拍板的最終 Story Point，定案後將自動回寫至對應工項。"">ⓘ</span></label>
                                <input type=""number"" step=""0.5"" min=""{sessionMinPt:0.##}"" max=""{sessionMaxPt:0.##}"" name=""finalStoryPoint"" id=""inputFinalStoryPoint"" value=""{(activeSessionRow["finalStoryPoint"] != DBNull.Value ? activeSessionRow["finalStoryPoint"] : suggestedFinalPt)}"" />
                            </div>
                            <div>
                                <label>定案決議說明<span class=""caption-help-icon"" data-tip=""記錄團隊最終拍板此點數的決議理由與驗收前提假設。"">ⓘ</span></label>
                                <input type=""text"" name=""finalDecisionNote"" id=""inputFinalDecisionNote"" value=""{h(savedFinalDecisionNote)}"" placeholder=""例如：團隊第 {currentRoundNumber} 輪全數收斂於 {suggestedFinalPt:0.##} 點，包含單元測試與 API 文件"" />
                            </div>
                            <button type=""submit"" name=""submitAction"" value=""finalize"" class=""btn btn-success"" onclick=""document.getElementById('controlActionType').value='finalize'"">
                                🏆 拍板定案
                            </button>
                        </div>
                    </div>
                </form>
            </div>");
        }
        else
        {
            sb.Append(@"
            <div class=""card"" style=""background:#f8fafc;border-style:dashed;"">
                <div style=""font-size:13.5px;color:#475569;"">
                    💡 <strong>參與者提示：</strong>您目前以「一般參與成員」身分在獨立網頁參與此盲投 Session。當所有參與者皆完成投票（且開啟自動翻牌）或主持人點擊翻牌時，本網頁將即時自動翻牌揭曉！
                </div>
            </div>");
        }
        sb.Append("</div>"); // end 左欄

        // 右欄：本議程各回合統計與出牌明細
        sb.Append("<div>");
        sb.Append($@"
        <div class=""card"">
            <div class=""card-title"">
                <span>📊 本 Session 歷次盲投回合統計與{(isAdmin ? "全體具名" : "您個人")}出牌明細</span>
            </div>
            {(hasFinalizedPoint ? $@"<div style=""background:#dcfce7;border:1px solid #86efac;color:#166534;padding:10px 14px;border-radius:8px;margin-bottom:12px;font-size:13.5px;font-weight:700;"">🏆 拍板定案結果：最終核定 <strong>{activeSessionRow["finalStoryPoint"]} pt</strong> ｜ 定案決議說明：<strong>{(!string.IsNullOrWhiteSpace(savedFinalDecisionNote) ? h(savedFinalDecisionNote) : "（未填寫決議說明）")}</strong></div>" : "")}
            <div class=""table-scroll-wrap"">
                <table>
                    <thead>
                        <tr>
                            <th>回合</th>
                            <th>狀態</th>
                            <th>投票數</th>
                            <th>最低 / 最高 / 平均 / 極差</th>
                            <th>回合討論紀錄 / 定案決議</th>
                        </tr>
                    </thead>
                    <tbody>");

        var sessionRounds = roundTable.AsEnumerable()
            .Where(r => Convert.ToInt32(r["votingSessionPk"]) == activeSessionPk)
            .OrderBy(r => Convert.ToInt32(r["roundNumber"]))
            .ToList();

        foreach (DataRow rRow in sessionRounds)
        {
            int rNum = Convert.ToInt32(rRow["roundNumber"]);
            string rStatus = rRow["roundStatus"].ToString() ?? "";
            string rStatusText = rStatus == "voting" ? "🙈 盲投進行中" : "🎉 已翻牌揭曉";
            bool isMasked = rStatus == "voting" && status == "voting";
            decimal rMin = rRow["minPoint"] != DBNull.Value ? Convert.ToDecimal(rRow["minPoint"]) : 0m;
            decimal rMax = rRow["maxPoint"] != DBNull.Value ? Convert.ToDecimal(rRow["maxPoint"]) : 0m;
            decimal rSpread = rMax - rMin;
            string rNote = rRow["roundDiscussionNote"] != DBNull.Value ? (rRow["roundDiscussionNote"]?.ToString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(rNote) && hasFinalizedPoint && rNum == currentRoundNumber && !string.IsNullOrWhiteSpace(savedFinalDecisionNote))
            {
                rNote = savedFinalDecisionNote;
            }

            sb.Append($@"
                        <tr>
                            <td><strong>第 {rNum} 回合</strong></td>
                            <td>{rStatusText}</td>
                            <td>{rRow["voteCount"]} 票</td>
                            <td>{(isMasked ? "<span style='color:#b45309;'>🙈 盲投保密中</span>" : $"Min: <strong>{rRow["minPoint"]}</strong> / Max: <strong>{rRow["maxPoint"]}</strong> / Avg: <strong>{rRow["averagePoint"]}</strong> / 極差: <strong style='color:#dc2626;'>{rSpread:0.##}</strong>")}</td>
                            <td>{h(rNote)}</td>
                        </tr>");
        }

        sb.Append(@"
                    </tbody>
                </table>
            </div>

            <div style=""margin-top:14px;font-weight:700;font-size:14px;margin-bottom:6px;"">
                🔍 本議程各回合具名投票明細（盲投階段他人蓋牌，翻牌後公開點數與理由）：
            </div>
            <div class=""table-scroll-wrap"">
                <table>
                    <thead>
                        <tr>
                            <th>回合</th>
                            <th>投票員工（部門 / 職稱）</th>
                            <th>出牌</th>
                            <th>出牌理由與技術假設</th>
                            <th>投票時間</th>
                        </tr>
                    </thead>
                    <tbody>");

        var sessionVotes = voteRecordTable.AsEnumerable()
            .Where(r => Convert.ToInt32(r["votingSessionPk"]) == activeSessionPk)
            .ToList();

        foreach (DataRow vRow in sessionVotes)
        {
            string rStatus = vRow["roundStatus"].ToString() ?? "";
            string displayCard = vRow["displayCardValue"]?.ToString() ?? "";
            string displayComment = h(vRow["voteComment"]);
            sb.Append($@"
                        <tr>
                            <td>第 {vRow["roundNumber"]} 回合</td>
                            <td><strong>{h(vRow["employeeName"])}</strong> <span style='color:#64748b;font-size:12px;'>({h(vRow["departmentName"])} / {h(vRow["jobTitle"])})</span></td>
                            <td><span class='badge {(rStatus == "voting" ? "badge-voting" : "badge-revealed")}'>{h(displayCard)}</span></td>
                            <td>{displayComment}</td>
                            <td style='font-size:12px;color:#64748b;'>{Convert.ToDateTime(vRow["votedAt"]):MM-dd HH:mm:ss}</td>
                        </tr>");
        }

        sb.Append(@"
                    </tbody>
                </table>
            </div>
        </div>
        </div>
        </div>"); // end 右欄 & grid-2

        // JavaScript：單一 Session 獨立網頁防重複進入保護、心跳續約、自動翻牌同步、跨表排序與鎖定
        sb.Append(@"
    </div>
    <script src=""https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.0/signalr.min.js""></script>
    <script>
        const currentEmployeePk = " + currentEmployeePk + @";
        const currentVotingSessionPk = " + activeSessionPk + @";
        const assignedTabInstanceId = '" + h(assignedTabInstanceId) + @"';
        let currentSessionStatus = '" + h(status) + @"';
        let currentRoundNumber = " + currentRoundNumber + @";
        let currentRoundVoteCount = " + currentRoundVotes.Count + @";
        let myActiveOpenSessionPks = [" + string.Join(",", myActiveLockedPks) + @"];
        let isAutoReloading = false;
        window.__isInternalSessionNavigation = false;

        // 1. 確保本分頁 window.name 綁定 assignedTabInstanceId，並將網址列清理為乾淨的 /voting/session?sessionPk=X
        //    如此使用者若複製網址列貼到新分頁開啟，新分頁不會帶有 tabInstanceId，後端與前端會立即攔截重複進入！
        try {
            window.name = assignedTabInstanceId;
            if (window.history && window.history.replaceState) {
                const urlObj = new URL(window.location.href);
                if (urlObj.searchParams.has('tabInstanceId')) {
                    urlObj.searchParams.delete('tabInstanceId');
                    window.history.replaceState(null, '', urlObj.pathname + urlObj.search);
                }
            }
        } catch (e) { }

        // 2. 寫入前端 localStorage 與 BroadcastChannel 鎖定宣告，讓大廳與其他分頁即時得知本 Session 已在獨立網頁進行中
        function writeLocalSessionPresenceLock() {
            try {
                localStorage.setItem('sp_active_session_lock_' + currentEmployeePk + '_' + currentVotingSessionPk, JSON.stringify({
                    employeePk: currentEmployeePk,
                    sessionPk: currentVotingSessionPk,
                    tabInstanceId: assignedTabInstanceId,
                    lastHeartbeatMs: Date.now()
                }));
            } catch (e) { }
        }
        writeLocalSessionPresenceLock();

        let guardChannel = null;
        if (typeof BroadcastChannel !== 'undefined') {
            try {
                guardChannel = new BroadcastChannel('sp_voting_session_guard');
                guardChannel.postMessage({
                    type: 'SESSION_ACTIVE',
                    employeePk: currentEmployeePk,
                    sessionPk: currentVotingSessionPk,
                    tabInstanceId: assignedTabInstanceId
                });

                guardChannel.onmessage = function (ev) {
                    const msg = ev.data || {};
                    if (msg.type === 'USER_LOGGED_OUT') {
                        window.__isInternalSessionNavigation = true;
                        try {
                            localStorage.removeItem('sp_active_session_lock_' + currentEmployeePk + '_' + currentVotingSessionPk);
                        } catch (e) { }
                        window.location.href = '/login?msg=' + encodeURIComponent('您已安全登出系統，請重新登入。');
                        return;
                    }
                    if (Number(msg.employeePk) !== Number(currentEmployeePk)) return;
                    if ((msg.type === 'DUPLICATE_ENTRY_ATTEMPT' || msg.type === 'FOCUS_EXISTING_SESSION') && Number(msg.sessionPk) === Number(currentVotingSessionPk)) {
                        const b = document.getElementById('duplicateAttemptBanner');
                        if (b) {
                            b.style.display = 'block';
                            b.innerHTML = '🔔 <strong>防重複進入提醒 (' + (msg.timestamp || '') + ')：</strong>系統剛才已自動攔截您在另一個分頁重複開啟本盲投 Session (#' + currentVotingSessionPk + ')，請直接在本獨立網頁繼續進行！';
                        }
                        try { window.focus(); } catch (e) { }
                    } else if (msg.type === 'FORCE_LEAVE_FROM_LOBBY' && Number(msg.sessionPk) === Number(currentVotingSessionPk)) {
                        window.__isInternalSessionNavigation = true;
                        try {
                            localStorage.removeItem('sp_active_session_lock_' + currentEmployeePk + '_' + currentVotingSessionPk);
                            window.close();
                        } catch (e) { }
                        window.location.href = '/?tab=voting&msg=' + encodeURIComponent('您已結束盲投議程 (#' + currentVotingSessionPk + ') 的「參與中」狀態，議程已移回「📂 待參與」。');
                    }
                };
            } catch (e) { }
        }

        function onExplicitLeaveSessionClick() {
            window.__isInternalSessionNavigation = true;
            try {
                document.cookie = 'sp_reload_inst_' + currentVotingSessionPk + '=; path=/; max-age=0';
                localStorage.removeItem('sp_active_session_lock_' + currentEmployeePk + '_' + currentVotingSessionPk);
                if (guardChannel) {
                    guardChannel.postMessage({
                        type: 'SESSION_CLOSED',
                        employeePk: currentEmployeePk,
                        sessionPk: currentVotingSessionPk,
                        tabInstanceId: assignedTabInstanceId
                    });
                }
            } catch (e) { }
        }

        // 3. 當分頁重新整理(F5)、表單送出或關閉時：
        //    - 寫入短效 5 秒 cookie (sp_reload_inst_{sessionPk})，讓同分頁 F5 重新整理或表單重導向可無縫續用同一個 tabInstanceId
        //    - 若為真正關閉分頁（非內部重新載入/表單送出），透過 sendBeacon 通知後端釋放鎖定並廣播 SESSION_CLOSED
        window.addEventListener('beforeunload', function () {
            try {
                if (!window.__isInternalSessionNavigation) {
                    localStorage.removeItem('sp_active_session_lock_' + currentEmployeePk + '_' + currentVotingSessionPk);
                    if (guardChannel) {
                        guardChannel.postMessage({
                            type: 'SESSION_CLOSED',
                            employeePk: currentEmployeePk,
                            sessionPk: currentVotingSessionPk,
                            tabInstanceId: assignedTabInstanceId
                        });
                    }
                    if (navigator.sendBeacon) {
                        const fd = new FormData();
                        fd.append('sessionPk', String(currentVotingSessionPk));
                        fd.append('tabInstanceId', assignedTabInstanceId);
                        navigator.sendBeacon('/voting/session/leave', fd);
                    }
                } else {
                    document.cookie = 'sp_reload_inst_' + currentVotingSessionPk + '=' + encodeURIComponent(assignedTabInstanceId) + '; path=/; max-age=5; SameSite=Lax';
                }
            } catch (e) { }
        });

        // 4. 點擊上方導覽列開啟「其他不同 Session」時，若該不同 Session 也已經在某個獨立網頁開啟中，先予以攔截提醒
        function openOtherIndependentSession(evt, otherSessionPk) {
            const pk = Number(otherSessionPk);
            if (Array.isArray(myActiveOpenSessionPks) && myActiveOpenSessionPks.indexOf(pk) !== -1) {
                if (evt) evt.preventDefault();
                alert('⚠️ 盲投 Session (#' + pk + ') 您已經在另一個獨立網頁進行中，不能重複進入！\n請直接切換至已開啟的該 Session 網頁分頁。');
                return false;
            }
            return true;
        }

        function selectCard(label, btnEl, forceSubmit) {
            const input = document.getElementById('inputCardLabel');
            const text = document.getElementById('selectedCardText');
            if (input) input.value = label;
            if (text) text.textContent = label;
            document.querySelectorAll('.deck-card-btn').forEach(b => b.classList.remove('selected'));
            if (btnEl) btnEl.classList.add('selected');
            const quickCb = document.getElementById('cbQuickSingleClickCast');
            const shouldAutoSubmit = forceSubmit || (quickCb && quickCb.checked);
            if (shouldAutoSubmit) {
                const formEl = document.getElementById('formCastVote');
                const commentEl = document.getElementById('inputVoteComment');
                if (commentEl && commentEl.required && !commentEl.value.trim()) {
                    commentEl.focus();
                    return;
                }
                if (formEl) {
                    if (typeof formEl.requestSubmit === 'function') formEl.requestSubmit();
                    else formEl.submit();
                }
            }
        }

        // 5. 表格標題排序与鎖定 (Sticky Header)
        function parseCellSortValue(rawText) {
            const cleaned = (rawText || '').replace(/,/g, '').trim();
            if (!cleaned) return { type: 'empty', val: '' };
            const numMatch = cleaned.match(/^[-+]?\d+(\.\d+)?(\s*(pt|點|票|秒|%))?$/i);
            if (numMatch) return { type: 'num', val: parseFloat(cleaned) };
            const roundMatch = cleaned.match(/^第\s*(\d+)\s*回合$/);
            if (roundMatch) return { type: 'num', val: parseInt(roundMatch[1], 10) };
            const dateMatch = cleaned.match(/^(\d{4}[-/])?\d{2}[-/]\d{2}(\s+\d{2}:\d{2}(:\d{2})?)?$/);
            if (dateMatch) {
                const norm = cleaned.length <= 14 && !/^\d{4}/.test(cleaned) ? ('2026-' + cleaned.replace(/\//g, '-')) : cleaned.replace(/\//g, '-');
                const ts = Date.parse(norm);
                if (!isNaN(ts)) return { type: 'num', val: ts };
            }
            return { type: 'str', val: cleaned.toLowerCase() };
        }

        function sortTableByColumn(table, colIdx, sortBtn) {
            const tbody = table.tBodies[0];
            if (!tbody) return;
            const currentDir = sortBtn.getAttribute('data-sort-dir') || 'none';
            const nextDir = currentDir === 'asc' ? 'desc' : 'asc';
            table.querySelectorAll('thead .sp-sort-btn').forEach(btn => {
                btn.setAttribute('data-sort-dir', 'none');
                btn.classList.remove('active-sort');
                btn.textContent = '⇅';
            });
            sortBtn.setAttribute('data-sort-dir', nextDir);
            sortBtn.classList.add('active-sort');
            sortBtn.textContent = nextDir === 'asc' ? '▲' : '▼';
            const rows = Array.from(tbody.rows);
            rows.sort((rowA, rowB) => {
                const cellA = rowA.cells[colIdx] ? rowA.cells[colIdx].innerText : '';
                const cellB = rowB.cells[colIdx] ? rowB.cells[colIdx].innerText : '';
                const a = parseCellSortValue(cellA);
                const b = parseCellSortValue(cellB);
                if (a.type === 'empty' && b.type === 'empty') return 0;
                if (a.type === 'empty') return 1;
                if (b.type === 'empty') return -1;
                let cmp = 0;
                if (a.type === 'num' && b.type === 'num') cmp = a.val - b.val;
                else cmp = String(a.val).localeCompare(String(b.val), 'zh-Hant-TW', { numeric: true });
                return nextDir === 'asc' ? cmp : -cmp;
            });
            rows.forEach(r => tbody.appendChild(r));
        }

        function toggleTableHeaderLock(table) {
            const isLocked = table.getAttribute('data-header-locked') === 'true';
            const nextState = !isLocked;
            table.setAttribute('data-header-locked', nextState ? 'true' : 'false');
            table.querySelectorAll('thead th').forEach(th => {
                if (nextState) th.classList.add('sp-th-locked');
                else th.classList.remove('sp-th-locked');
            });
            table.querySelectorAll('thead .sp-lock-btn').forEach(btn => {
                if (nextState) {
                    btn.classList.add('active-lock');
                    btn.textContent = '🔒';
                } else {
                    btn.classList.remove('active-lock');
                    btn.textContent = '📌';
                }
            });
        }

        function initUniversalTableSortAndLock() {
            document.querySelectorAll('table').forEach(table => {
                const thead = table.querySelector('thead');
                const tbody = table.querySelector('tbody');
                if (!thead || !tbody) return;
                table.setAttribute('data-header-locked', 'true');

                // 建立表格上方即時關鍵字與欄位篩選工具列
                const filterBar = document.createElement('div');
                filterBar.style.cssText = 'display:flex;align-items:center;justify-content:space-between;gap:8px;flex-wrap:wrap;background:#f8fafc;border:1px solid #cbd5e1;border-bottom:none;border-radius:8px 8px 0 0;padding:7px 12px;font-size:12px;';
                const colSelect = document.createElement('select');
                colSelect.style.cssText = 'width:auto;padding:4px 8px;font-size:12px;border-radius:6px;border:1px solid #93c5fd;margin:0;';
                const allOpt = document.createElement('option');
                allOpt.value = '-1';
                allOpt.textContent = '🏷️ 全部欄位篩選';
                colSelect.appendChild(allOpt);

                const filterInput = document.createElement('input');
                filterInput.type = 'text';
                filterInput.placeholder = '🔍 輸入關鍵字即時篩選本表格...';
                filterInput.style.cssText = 'width:220px;padding:4px 9px;font-size:12px;border-radius:6px;border:1px solid #93c5fd;margin:0;';

                const countBadge = document.createElement('span');
                countBadge.style.cssText = 'font-weight:700;color:#1e40af;background:#dbeafe;padding:2px 8px;border-radius:999px;';
                const initTotal = tbody.querySelectorAll('tr').length;
                countBadge.textContent = '顯示 ' + initTotal + ' / ' + initTotal + ' 筆';

                const leftWrap = document.createElement('div');
                leftWrap.style.cssText = 'display:flex;align-items:center;gap:6px;flex-wrap:wrap;';
                leftWrap.appendChild(colSelect);
                leftWrap.appendChild(filterInput);
                filterBar.appendChild(leftWrap);
                filterBar.appendChild(countBadge);
                table.parentNode.insertBefore(filterBar, table);

                const runFilter = () => {
                    const cIdx = parseInt(colSelect.value || '-1', 10);
                    const kw = (filterInput.value || '').trim().toLowerCase();
                    let vis = 0;
                    const trs = Array.from(tbody.querySelectorAll('tr'));
                    trs.forEach(r => {
                        let ok = true;
                        if (kw) {
                            const txt = (cIdx >= 0 && r.cells[cIdx]) ? (r.cells[cIdx].innerText || '') : (r.innerText || '');
                            ok = txt.toLowerCase().indexOf(kw) !== -1;
                        }
                        r.style.display = ok ? '' : 'none';
                        if (ok) vis++;
                    });
                    countBadge.textContent = '顯示 ' + vis + ' / ' + trs.length + ' 筆';
                };
                colSelect.addEventListener('change', runFilter);
                filterInput.addEventListener('input', runFilter);

                thead.querySelectorAll('tr').forEach(tr => {
                    Array.from(tr.cells).forEach((th, colIdx) => {
                        if (th.tagName.toLowerCase() !== 'th') return;
                        const colTitle = (th.innerText || '').trim();
                        const opt = document.createElement('option');
                        opt.value = String(colIdx);
                        opt.textContent = '欄位：' + colTitle;
                        colSelect.appendChild(opt);

                        th.classList.add('sp-sortable-th', 'sp-th-locked');
                        const toolbar = document.createElement('span');
                        toolbar.className = 'sp-th-toolbar';
                        const sortBtn = document.createElement('button');
                        sortBtn.type = 'button';
                        sortBtn.className = 'sp-th-btn sp-sort-btn';
                        sortBtn.textContent = '⇅';
                        sortBtn.addEventListener('click', e => { e.stopPropagation(); sortTableByColumn(table, colIdx, sortBtn); });
                        const lockBtn = document.createElement('button');
                        lockBtn.type = 'button';
                        lockBtn.className = 'sp-th-btn sp-lock-btn active-lock';
                        lockBtn.textContent = '🔒';
                        lockBtn.addEventListener('click', e => { e.stopPropagation(); toggleTableHeaderLock(table); });
                        toolbar.appendChild(sortBtn);
                        toolbar.appendChild(lockBtn);
                        th.appendChild(toolbar);
                    });
                });
            });
        }
        initUniversalTableSortAndLock();

        (function initStandaloneTableContextMenu() {
            const ctxEl = document.createElement('div');
            ctxEl.id = 'spRowContextMenu';
            ctxEl.className = 'sp-context-menu';
            document.body.appendChild(ctxEl);
            const modalOverlay = document.createElement('div');
            modalOverlay.id = 'spRecordEditModalOverlay';
            modalOverlay.className = 'sp-modal-overlay';
            document.body.appendChild(modalOverlay);
            let activeRow = null;

            function closeCtx() {
                ctxEl.classList.remove('visible', 'open');
                if (activeRow) { activeRow.classList.remove('sp-context-active-row'); activeRow = null; }
            }
            function closeModal() {
                modalOverlay.classList.remove('open');
                modalOverlay.innerHTML = '';
            }
            modalOverlay.addEventListener('click', function (e) { if (e.target === modalOverlay) closeModal(); });
            document.addEventListener('click', function (e) {
                const t = (e.target && e.target.nodeType === 1) ? e.target : (e.target ? e.target.parentElement : null);
                if (t && t.closest && t.closest('#spRowContextMenu')) return;
                closeCtx();
            });
            document.addEventListener('keydown', function (e) { if (e.key === 'Escape') { closeCtx(); closeModal(); } });

            document.addEventListener('contextmenu', function (evt) {
                const rawTarget = evt.target;
                const target = (rawTarget && rawTarget.nodeType === 1) ? rawTarget : (rawTarget ? rawTarget.parentElement : null);
                if (!target || !target.closest) return;
                if (target.closest('input, textarea, select')) return;
                const tr = target.closest('table tbody tr');
                if (!tr) return;
                evt.preventDefault();
                if (activeRow) activeRow.classList.remove('sp-context-active-row');
                activeRow = tr;
                tr.classList.add('sp-context-active-row');

                const td = target.closest('td');
                const table = tr.closest('table');
                const headers = Array.from(table.querySelectorAll('thead th')).map(th => (th.innerText || '').replace(/[⇅▲▼🔒📌]/g, '').trim());
                ctxEl.innerHTML = `<div class='sp-ctx-header'>📋 表格紀錄滑鼠右鍵選單</div>`;

                const detailBtn = document.createElement('button');
                detailBtn.type = 'button';
                detailBtn.className = 'sp-ctx-item';
                detailBtn.innerHTML = `<span>🪟 彈出 Modal 視窗檢視此筆紀錄明細</span>`;
                detailBtn.onclick = function () {
                    closeCtx();
                    const rowsHtml = Array.from(tr.cells).map((c, idx) => `<div style='display:grid;grid-template-columns:160px 1fr;gap:10px;padding:8px 10px;border-bottom:1px solid #e2e8f0;font-size:13px;'><div style='font-weight:700;color:#1e3a8a;'>${headers[idx] || ('欄位 ' + (idx + 1))}</div><div>${(c.innerText || '').trim()}</div></div>`).join('');
                    modalOverlay.innerHTML = `<div class='sp-modal-dialog'><div class='sp-modal-header'><span>🪟 單筆紀錄完整明細</span><button type='button' class='btn btn-outline' style='padding:2px 8px;' id='btnCloseStandaloneModal'>✖</button></div><div class='sp-modal-body'><div style='border:1px solid #cbd5e1;border-radius:8px;background:#f8fafc;'>${rowsHtml}</div></div></div>`;
                    modalOverlay.classList.add('open');
                    document.getElementById('btnCloseStandaloneModal').onclick = closeModal;
                };
                ctxEl.appendChild(detailBtn);

                if (td) {
                    const cellText = (td.innerText || '').trim();
                    if (cellText) {
                        const filterBtn = document.createElement('button');
                        filterBtn.type = 'button';
                        filterBtn.className = 'sp-ctx-item';
                        filterBtn.innerHTML = `<span>🔍 以此儲存格值篩選本表格</span>`;
                        filterBtn.onclick = function () {
                            closeCtx();
                            const fInput = table.previousElementSibling ? table.previousElementSibling.querySelector('input[type=text]') : null;
                            if (fInput) { fInput.value = cellText; fInput.dispatchEvent(new Event('input')); }
                        };
                        ctxEl.appendChild(filterBtn);
                    }
                }

                const copyBtn = document.createElement('button');
                copyBtn.type = 'button';
                copyBtn.className = 'sp-ctx-item';
                copyBtn.innerHTML = `<span>📋 複製本列紀錄文字至剪貼簿</span>`;
                copyBtn.onclick = function () {
                    closeCtx();
                    const txt = Array.from(tr.cells).map(c => (c.innerText || '').trim()).join('\t');
                    if (navigator.clipboard && navigator.clipboard.writeText) navigator.clipboard.writeText(txt);
                };
                ctxEl.appendChild(copyBtn);

                ctxEl.classList.add('visible', 'open');
                const rect = ctxEl.getBoundingClientRect();
                ctxEl.style.left = Math.max(8, Math.min(evt.clientX, window.innerWidth - rect.width - 12)) + 'px';
                ctxEl.style.top = Math.max(8, Math.min(evt.clientY, window.innerHeight - rect.height - 12)) + 'px';
            });
        })();

        // 6. 單回合倒數計時器
        (function initRoundCountdown() {
            const box = document.getElementById('roundCountdownBox');
            if (!box) return;
            let remain = parseInt(box.getAttribute('data-limit') || '0', 10);
            if (isNaN(remain) || remain <= 0) return;
            const timer = setInterval(function () {
                remain--;
                if (remain <= 0) {
                    clearInterval(timer);
                    box.textContent = '⏰ 本回合計時已到！請儘速完成出牌或由主持人翻牌';
                    box.style.background = '#dc2626';
                } else {
                    const m = Math.floor(remain / 60);
                    const s = remain % 60;
                    box.textContent = '⏳ 倒數計時：' + (m > 0 ? (m + '分 ' + s + '秒') : (s + ' 秒'));
                }
            }, 1000);
        })();

        function isUserTypingInStandalone() {
            const activeEl = document.activeElement;
            if (!activeEl) return false;
            const id = activeEl.id || '';
            const name = activeEl.name || '';
            return id === 'inputVoteComment' || name === 'roundDiscussionNote' || name === 'finalDecisionNote' || name === 'finalStoryPoint';
        }

        function triggerStandaloneAutoReload(reasonMsg) {
            if (isAutoReloading) return;
            isAutoReloading = true;
            window.__isInternalSessionNavigation = true;
            const nextUrl = '/voting/session?sessionPk=' + currentVotingSessionPk +
                '&tabInstanceId=' + encodeURIComponent(assignedTabInstanceId) +
                (reasonMsg ? '&msg=' + encodeURIComponent(reasonMsg) : '');
            window.location.href = nextUrl;
        }

        function refreshStandaloneSessionNow(btnEl) {
            if (btnEl) {
                btnEl.disabled = true;
                btnEl.textContent = '⏳ 重新整理中...';
            }
            triggerStandaloneAutoReload('🔄 已重新整理本盲投 Session 最新桌面狀態！');
        }

        // 7. 每 2 秒向後端發送心跳續約並檢查本 Session 是否有新出牌或自動翻牌
        function pollStandaloneSessionStatus() {
            if (isAutoReloading) return;
            writeLocalSessionPresenceLock();
            fetch('/voting/liveStatus?sessionPk=' + currentVotingSessionPk + '&tabInstanceId=' + encodeURIComponent(assignedTabInstanceId) + '&_t=' + Date.now(), {
                credentials: 'same-origin',
                cache: 'no-store'
            })
                .then(function (res) { return res.ok ? res.json() : null; })
                .then(function (data) {
                    if (!data || !data.found) return;
                    if (Array.isArray(data.myActiveOpenSessionPks)) {
                        myActiveOpenSessionPks = data.myActiveOpenSessionPks.map(Number);
                    }
                    const statusChanged = data.sessionStatus && data.sessionStatus !== currentSessionStatus;
                    const roundChanged = Number(data.currentRoundNumber) !== Number(currentRoundNumber);
                    const voteCountChanged = Number(data.voteCount) !== Number(currentRoundVoteCount);

                    if (statusChanged || roundChanged) {
                        const msg = data.sessionStatus === 'revealed'
                            ? '🎉 所有參與者已完成投票，畫面已自動翻牌揭曉！'
                            : '';
                        triggerStandaloneAutoReload(msg);
                    } else if (voteCountChanged && !isUserTypingInStandalone()) {
                        triggerStandaloneAutoReload('');
                    }
                })
                .catch(function () { });
        }

        if (window.signalR) {
            const conn = new signalR.HubConnectionBuilder()
                .withUrl('/votingHub')
                .withAutomaticReconnect()
                .build();

            conn.on('onVotingSessionUpdated', function (payload) {
                if (Number(payload.votingSessionPk) !== Number(currentVotingSessionPk)) {
                    return;
                }
                const isMajorFlipEvent = payload.isAutoRevealed === true
                    || payload.eventName === 'autoRevealed'
                    || payload.eventName === 'reveal'
                    || payload.eventName === 'nextRound'
                    || payload.eventName === 'revote'
                    || payload.eventName === 'resetRound'
                    || payload.eventName === 'finalize'
                    || payload.sessionStatus === 'revealed'
                    || payload.sessionStatus === 'finalized';

                if (isMajorFlipEvent) {
                    setTimeout(function () {
                        triggerStandaloneAutoReload(payload.messageText);
                    }, 200);
                } else if (!isUserTypingInStandalone()) {
                    setTimeout(function () {
                        triggerStandaloneAutoReload('');
                    }, 300);
                }
            });

            conn.start().catch(function (err) { console.warn(err); });
        }

        setInterval(pollStandaloneSessionStatus, 2000);
    </script>
</body>
</html>");

        return sb.ToString();
    }

    // =========================================================================
    // 8. 系統線上說明與操作畫面搜尋中心 (Online Help Center with Search & Operation Screens)
    // =========================================================================

    /// <summary>
    /// 建構獨立線上說明專頁 (/help)，支援未登入或已登入狀態直接開啟、關鍵字即時搜尋、分類過濾與實機操作畫面放大檢視
    /// </summary>
    private string buildStandaloneOnlineHelpPageHtml(
        bool isAuthenticated,
        bool isAdmin,
        string? initialSearchQuery,
        string? initialCategory,
        string? initialTopic)
    {
        string userBanner = isAuthenticated
            ? $@"<span>👤 {h(getCurrentEmployeeName())} ({h(getCurrentLoginAccount())}) ｜ 部門：{h(getCurrentDepartmentName())}</span>
                 <a href='/?tab=voting' class='btn btn-primary' style='padding:6px 14px;font-size:12.5px;background:#2563eb;color:#fff;text-decoration:none;border-radius:8px;font-weight:800;'>🃏 返回系統主畫面</a>
                 <a href='/logout' class='btn btn-danger' style='padding:6px 12px;font-size:12.5px;background:#dc2626;color:#fff;text-decoration:none;border-radius:8px;font-weight:800;'>🚪 登出系統</a>"
            : @"<a href='/login' class='btn btn-primary' style='padding:6px 14px;font-size:12.5px;background:#2563eb;color:#fff;text-decoration:none;border-radius:8px;font-weight:800;'>🔐 前往員工帳號登入頁</a>";

        string innerHtml = buildOnlineHelpCenterInnerHtml(
            isAdmin: isAuthenticated ? isAdmin : true,
            isStandalonePage: true,
            initialSearchQuery: initialSearchQuery,
            initialCategory: initialCategory,
            initialTopic: initialTopic);

        return $@"<!DOCTYPE html>
<html lang=""zh-Hant"">
<head>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />
    <title>線上說明與操作畫面搜尋手冊 - Azure 多人參與 Story Point 估算與工項權重管理系統</title>
    <style>
        :root {{ --bg:#f1f5f9; --text:#0f172a; --muted:#64748b; --border:#cbd5e1; }}
        * {{ box-sizing:border-box; }}
        body {{ margin:0; font-family:'Microsoft JhengHei UI','Segoe UI',sans-serif; background:var(--bg); color:var(--text); }}
        .topbar {{ background:#0f172a; color:#fff; padding:12px 24px; display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:12px; position:sticky; top:0; z-index:990; box-shadow:0 4px 14px rgba(15,23,42,0.22); }}
        .topbar-title {{ display:flex; align-items:center; gap:12px; font-size:18px; font-weight:800; }}
        .topbar-user {{ display:flex; align-items:center; gap:10px; font-size:13px; flex-wrap:wrap; }}
        .container {{ max-width:1440px; margin:0 auto; padding:20px 24px 48px 24px; }}
        .badge {{ padding:4px 10px; border-radius:999px; font-size:12px; font-weight:700; display:inline-block; }}
        .badge-admin {{ background:#fef3c7; color:#b45309; border:1px solid #fde68a; }}
        .badge-user {{ background:#dbeafe; color:#1d4ed8; border:1px solid #93c5fd; }}
        .badge-finalized {{ background:#dcfce7; color:#166534; border:1px solid #86efac; }}
        .btn {{ display:inline-flex; align-items:center; justify-content:center; gap:6px; padding:8px 14px; border-radius:8px; font-size:13px; font-weight:700; border:1px solid transparent; cursor:pointer; text-decoration:none; }}
        .btn-primary {{ background:#2563eb; color:#fff; }}
        .btn-outline {{ background:#fff; border-color:#cbd5e1; color:#334155; }}
        .btn-danger {{ background:#dc2626; color:#fff; }}
        .help-hero-banner {{ background:linear-gradient(135deg,#0f172a 0%,#1e3a8a 65%,#1d4ed8 100%); color:#ffffff; border-radius:14px; padding:22px 24px; margin-bottom:18px; box-shadow:0 10px 28px rgba(15,23,42,0.22); border:1px solid #3b82f6; }}
        .help-search-grid {{ display:grid; grid-template-columns:1.6fr 0.7fr 0.7fr auto; gap:10px; margin-top:14px; align-items:center; }}
        @media (max-width:960px) {{ .help-search-grid {{ grid-template-columns:1fr; }} }}
        .help-search-input-wrap {{ position:relative; display:flex; align-items:center; }}
        .help-search-input {{ width:100% !important; padding:11px 14px 11px 38px !important; border-radius:10px !important; border:2px solid #60a5fa !important; background:#ffffff !important; color:#0f172a !important; font-size:14.5px !important; font-weight:700 !important; box-shadow:0 4px 14px rgba(0,0,0,0.18); }}
        .help-search-input:focus {{ outline:none; border-color:#f59e0b !important; box-shadow:0 0 0 3px rgba(245,158,11,0.35); }}
        .help-quick-tag {{ display:inline-flex; align-items:center; gap:4px; padding:4px 11px; border-radius:999px; background:rgba(255,255,255,0.14); border:1px solid rgba(255,255,255,0.3); color:#f8fafc; font-size:12px; font-weight:700; cursor:pointer; transition:all .15s; user-select:none; }}
        .help-quick-tag:hover, .help-quick-tag.active-tag {{ background:#f59e0b; border-color:#fbbf24; color:#0f172a; transform:translateY(-1px); }}
        .help-layout-grid {{ display:grid; grid-template-columns:280px 1fr; gap:18px; align-items:start; }}
        @media (max-width:1080px) {{ .help-layout-grid {{ grid-template-columns:1fr; }} }}
        .help-toc-sidebar {{ position:sticky; top:68px; background:#ffffff; border:1px solid #cbd5e1; border-radius:12px; padding:14px; box-shadow:0 2px 8px rgba(15,23,42,0.05); max-height:calc(100vh - 88px); overflow-y:auto; }}
        .help-toc-item {{ display:flex; align-items:center; justify-content:space-between; gap:8px; padding:8px 10px; border-radius:8px; color:#1e293b; font-size:12.5px; font-weight:700; text-decoration:none; cursor:pointer; transition:all .15s; margin-bottom:3px; border-left:3px solid transparent; }}
        .help-toc-item:hover {{ background:#eff6ff; color:#1d4ed8; border-left-color:#3b82f6; }}
        .help-toc-item.active-toc {{ background:#dbeafe; color:#1e3a8a; border-left-color:#1d4ed8; font-weight:800; }}
        .help-toc-item.toc-filtered-out {{ opacity:0.35; }}
        .help-topic-card {{ background:#ffffff; border:1.5px solid #cbd5e1; border-radius:14px; padding:20px; margin-bottom:20px; box-shadow:0 4px 12px rgba(15,23,42,0.05); transition:border-color .2s, box-shadow .2s; scroll-margin-top:74px; }}
        .help-topic-card:hover {{ border-color:#60a5fa; box-shadow:0 8px 22px rgba(37,99,235,0.1); }}
        .help-topic-header {{ display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:10px; border-bottom:2px solid #f1f5f9; padding-bottom:12px; margin-bottom:16px; }}
        .help-topic-body-grid {{ display:grid; grid-template-columns:1fr 1.18fr; gap:18px; align-items:start; }}
        @media (max-width:980px) {{ .help-topic-body-grid {{ grid-template-columns:1fr; }} }}
        .help-step-list {{ display:flex; flex-direction:column; gap:10px; margin:10px 0; }}
        .help-step-item {{ display:flex; align-items:flex-start; gap:10px; background:#f8fafc; border:1px solid #e2e8f0; border-left:4px solid #2563eb; border-radius:9px; padding:10px 12px; font-size:13px; line-height:1.55; color:#1e293b; }}
        .help-step-num {{ display:inline-flex; align-items:center; justify-content:center; width:24px; height:24px; border-radius:50%; background:#2563eb; color:#ffffff; font-size:12.5px; font-weight:800; flex-shrink:0; margin-top:1px; box-shadow:0 2px 6px rgba(37,99,235,0.3); }}
        .help-browser-frame {{ border:2px solid #334155; border-radius:12px; overflow:hidden; background:#0f172a; box-shadow:0 10px 26px rgba(15,23,42,0.22); }}
        .help-browser-bar {{ background:#1e293b; padding:7px 12px; display:flex; align-items:center; justify-content:space-between; gap:8px; border-bottom:1px solid #334155; color:#cbd5e1; font-size:11.5px; }}
        .help-browser-dots {{ display:flex; gap:5px; align-items:center; }}
        .help-browser-dot {{ width:10px; height:10px; border-radius:50%; display:inline-block; }}
        .help-browser-url {{ flex:1; background:#0f172a; color:#93c5fd; padding:3px 10px; border-radius:6px; font-family:Consolas,monospace; font-size:11.5px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; border:1px solid #334155; }}
        .help-screenshot-box {{ position:relative; background:#0f172a; cursor:zoom-in; overflow:hidden; border-bottom:1px solid #334155; }}
        .help-screenshot-box img {{ width:100%; height:auto; display:block; transition:transform .25s ease; }}
        .help-screenshot-box:hover img {{ transform:scale(1.015); }}
        .help-screenshot-zoom-hint {{ position:absolute; right:10px; bottom:10px; background:rgba(15,23,42,0.88); color:#ffffff; border:1px solid #60a5fa; border-radius:8px; padding:5px 10px; font-size:11.5px; font-weight:700; pointer-events:none; box-shadow:0 4px 12px rgba(0,0,0,0.4); }}
        .help-callout-legend {{ background:#f8fafc; padding:10px 12px; display:grid; grid-template-columns:repeat(auto-fit,minmax(195px,1fr)); gap:7px; border-top:1px solid #cbd5e1; }}
        .help-callout-chip {{ display:flex; align-items:center; gap:6px; font-size:12px; font-weight:700; color:#1e293b; background:#ffffff; border:1px solid #cbd5e1; border-radius:7px; padding:5px 8px; }}
        .help-callout-pin {{ display:inline-flex; align-items:center; justify-content:center; width:20px; height:20px; border-radius:50%; background:#dc2626; color:#ffffff; font-size:11px; font-weight:800; flex-shrink:0; }}
        .help-ui-diagram {{ background:#f1f5f9; padding:10px 12px; border-top:1px dashed #cbd5e1; font-size:12px; color:#334155; }}
        mark.help-highlight {{ background:#fef08a; color:#854d0e; padding:1px 4px; border-radius:4px; font-weight:800; box-shadow:0 0 0 1px #facc15; }}
        .help-faq-item {{ border:1px solid #cbd5e1; border-radius:10px; padding:12px 16px; margin-bottom:10px; background:#f8fafc; }}
        .help-faq-q {{ font-size:14px; font-weight:800; color:#1e3a8a; cursor:pointer; display:flex; align-items:center; justify-content:space-between; gap:8px; }}
        .help-faq-a {{ margin-top:8px; padding-top:8px; border-top:1px dashed #cbd5e1; font-size:13px; color:#334155; line-height:1.6; }}
        select {{ width:100%; padding:10px 12px; border-radius:9px; border:1.5px solid #93c5fd; font-size:13.5px; font-weight:700; font-family:inherit; background:#fff; color:#0f172a; }}
    </style>
</head>
<body>
    <div class=""topbar"">
        <div class=""topbar-title"">
            <span>📖 Azure 多人參與 Story Point 系統 — 線上說明與操作畫面搜尋中心</span>
            <span style=""font-size:12px;background:#1e293b;padding:4px 10px;border-radius:6px;color:#5eead4;"">全文關鍵字即時檢索 ｜ 12 幅實機操作畫面詳解</span>
        </div>
        <div class=""topbar-user"">
            {userBanner}
        </div>
    </div>
    <div class=""container"">
        {innerHtml}
    </div>
</body>
</html>";
    }

    /// <summary>
    /// 產生線上說明中心核心 HTML 結構（供主儀表板 #pane-help 頁籤與 /help 獨立專頁共用）
    /// 包含：
    /// 1. 關鍵字即時搜尋列（支援多關鍵字、分類篩選、角色篩選、熱門搜尋標籤與關鍵字黃底高亮標示）
    /// 2. 左側章節目錄導覽樹 (TOC) 與即時符合筆數統計
    /// 3. 12 大核心主題（每項皆包含「操作步驟 ①②③④」與「搭配實機操作畫面截圖 + 編號標註 + 互動模擬元件 + 點擊放大 Lightbox」）
    /// 4. 常見問題快速解答 (FAQ)
    /// </summary>
    private string buildOnlineHelpCenterInnerHtml(
        bool isAdmin,
        bool isStandalonePage,
        string? initialSearchQuery = null,
        string? initialCategory = null,
        string? initialTopic = null)
    {
        var sb = new StringBuilder();
        string safeInitQ = h(initialSearchQuery ?? "");
        string safeInitCat = h(initialCategory ?? "");
        string safeInitTopic = h(initialTopic ?? "");

        var topics = new[]
        {
            new
            {
                Id = "topic-storypoint-concept",
                Num = "00",
                Category = "concept",
                CategoryName = "💡 Story Point 核心概念與工作量透明",
                RoleScope = "all",
                RoleBadge = "<span class='badge' style='background:#fef3c7;color:#b45309;border:1px solid #f59e0b;'>⭐ 核心觀念必讀</span>",
                Title = "認識 Story Point：核心概念、用途、如何評估複雜度與讓團隊／個人工作量透明",
                Keywords = "story point 故事點 概念 用途 複雜度 評估 技術複雜度 工作規模 未知風險 不確定性 費氏數列 fibonacci 權重 加權總分 工作量透明 團隊負載 個人工作量 在製品 進行中點數 已完成點數 相對基準",
                RouteUrl = "http://localhost:5088/?tab=stats",
                ImageFile = "/help/images/screen_09_stats_table.png",
                ActionHtml = isStandalonePage
                    ? "<a href='/?tab=stats' class='btn btn-primary' style='padding:6px 12px;font-size:12px;'>📊 前往工作量透明統計畫面</a>"
                    : "<button type='button' class='btn btn-primary' style='padding:6px 12px;font-size:12px;' onclick=\"switchTab('stats')\">📊 查看團隊與個人工作量透明統計</button>",
                Summary = "Story Point（工作複雜度點數）不是估算「要做幾小時或幾天」，而是評估一項任務客觀的「綜合相對複雜度」。不必套用繁瑣教條，其最核心的用途就是透過一致的複雜度評分尺規，打破「只看工項件數」的盲點，讓整個團隊與每位成員的工作負荷與實質貢獻完全透明！",
                Steps = new[]
                {
                    "<strong>一、Story Point 核心概念（為何估「相對複雜度」而非「絕對工時」？）</strong>：同一件工作交給資深人員可能 2 小時完成，交給新進同仁可能需 2 天，直接估時間容易陷入「以誰的速度為準」的爭論；但「<strong>任務本身的複雜度與規模是客觀的</strong>」。Story Point 以相對倍數（例如：標準小改動為 <code>1 點</code>，某項跨模組任務難度與規模約為其 5 倍即為 <code>5 點</code>）建立全團隊共通的客觀度量衡。",
                    "<strong>二、如何評估複雜度？（綜合考量三大核心維度）</strong>：<br/>① <strong>技術複雜度 (Complexity)</strong>：程式或作業邏輯是否繁瑣、是否涉及跨系統串接、架構重構或高難度技術？<br/>② <strong>工作投入規模 (Effort / Volume)</strong>：即使技術不難，但需要處理的畫面、報表、資料表、API 或測試案例數量有多龐大？<br/>③ <strong>未知風險與不確定性 (Uncertainty / Risk)</strong>：需求邊界是否清楚？是否受制於外部單位或舊系統包袱？當這三項越高，給出的點數就越高。",
                    "<strong>三、費氏數列 (Fibonacci) 複雜度評分尺規對照</strong>：本系統採用 <code>0.5、1、2、3、5、8、13、20/21</code> 級距（任務越龐大，隱藏的不確定性呈非線性放大，拉開數字間距可避免糾結於「6 點還是 7 點」的假精確）：<br/>• <code>0.5 ~ 1 點（極簡基準）</code>：需求 100% 明確、零風險的微調（作為全團隊的 1 倍基準尺）。<br/>• <code>2 ~ 3 點（小型／標準常規）</code>：做法已知、單一模組內可完成的常規工作。<br/>• <code>5 ~ 8 點（中等／高複雜度）</code>：跨多張資料表、多畫面聯動或具外部整合風險，需透過多人盲投對齊認知。<br/>• <code>13 ~ 21 點以上（大型／巨型工項）</code>：複雜度與不確定性極高，建議先拆解為多個 3～5 點的子工項再指派；若資訊不足則出 <code>?（疑問牌）</code> 先釐清需求。",
                    "<strong>四、核心用途：讓團隊與個人的工作量完全透明化（Workload Transparency）</strong>：<br/>① <strong>打破「只算件數（張數）」造成的勞逸不均</strong>：手上有 5 件 <code>1 點</code> 小修改（共 5 點）與 2 件 <code>8 點</code> 核心重構（共 16 點），只看件數會誤以為前者較忙；透過 Story Point 乘上「<strong>重要性權重 (<code>weightMultiplier</code>)</strong>」算出加權總分，真實呈現工作含金量。<br/>② <strong>在途負載透明（進行中點數）</strong>：從統計表即時看清每位同仁與各部門目前手上的「<strong>進行中點數</strong>」，避免單點過載並及時調度支援。<br/>③ <strong>產出貢獻透明（已完成點數與加權實拿分數）</strong>：客觀記錄個人與部門累積完成的複雜度點數與加權分數，讓承擔高難度任務的貢獻一目了然。"
                },
                Callouts = new[]
                {
                    "① 維度一：技術複雜度 (邏輯難度與架構影響)",
                    "② 維度二：工作投入規模 (修改範圍與測試工作量)",
                    "③ 維度三：未知風險與不確定性 (相依性與變動率)",
                    "④ 透明化看板：進行中負載點數 vs 已完成加權貢獻"
                },
                UiDiagramHtml = @"<div style='display:flex;flex-direction:column;gap:8px;'>
                    <div style='font-weight:800;color:#0f172a;font-size:12.5px;'>📐 Story Point 複雜度評估尺規與工作量透明化速查表：</div>
                    <div style='display:grid;grid-template-columns:repeat(auto-fit,minmax(135px,1fr));gap:6px;font-size:11.5px;'>
                        <div style='background:#f0fdf4;border:1px solid #86efac;padding:6px 8px;border-radius:7px;'><strong style='color:#166534;'>🟢 0.5 ~ 1 點 (基準尺)</strong><br/>需求明確、單一微調、幾乎零風險</div>
                        <div style='background:#eff6ff;border:1px solid #93c5fd;padding:6px 8px;border-radius:7px;'><strong style='color:#1e40af;'>🔵 2 ~ 3 點 (常規標準)</strong><br/>邏輯清楚、單一模組前後端標準功能</div>
                        <div style='background:#fefce8;border:1px solid #fde047;padding:6px 8px;border-radius:7px;'><strong style='color:#854d0e;'>🟡 5 ~ 8 點 (中高複雜)</strong><br/>跨多表/多頁整合、具技術難度與風險</div>
                        <div style='background:#fef2f2;border:1px solid #fca5a5;padding:6px 8px;border-radius:7px;'><strong style='color:#991b1b;'>🔴 13 ~ 21+ 點 (大型)</strong><br/>高風險跨系統任務，建議先拆解細化</div>
                    </div>
                    <div style='background:#1e293b;color:#f8fafc;padding:7px 10px;border-radius:7px;font-size:11.5px;display:flex;flex-wrap:wrap;gap:10px;align-items:center;justify-content:space-between;'>
                        <span>⚖️ <strong>工作量透明化公式：</strong><code>工項加權總分 = Story Point (複雜度點數) × 重要性權重乘數</code></span>
                        <span style='color:#38bdf8;font-weight:700;'>📊 個人/部門總負載 ＝ 已完成點數 ＋ 進行中點數 ＋ 待處理點數</span>
                    </div>
                </div>"
            },
            new
            {
                Id = "topic-auth",
                Num = "01",
                Category = "auth",
                CategoryName = "🔐 登入驗證與帳密安全",
                RoleScope = "all",
                RoleBadge = "<span class='badge badge-user'>🙋 全體員工適用</span>",
                Title = "員工帳號密碼強制登入驗證與全系統安全登出",
                Keywords = "登入 密碼 驗證 login 登出 logout 角色 admin participant 權限隔離 顯示密碼 Cookie 釋放鎖定",
                RouteUrl = "http://localhost:5088/login",
                ImageFile = "/help/images/screen_01_login.png",
                ActionHtml = isStandalonePage
                    ? "<a href='/login' class='btn btn-primary' style='padding:6px 12px;font-size:12px;'>🔐 前往登入頁</a>"
                    : "<button type='button' class='btn btn-outline' style='padding:6px 12px;font-size:12px;' onclick=\"openHelpScreenLightbox('/help/images/screen_01_login.png','主題 01：員工帳號密碼強制登入驗證與全系統安全登出')\">🔍 放大操作畫面</button>",
                Summary = "系統採強制身分驗證與嚴格角色資料隔離（管理者 Admin 可管理全公司資料；一般參與者 Participant 僅可參與盲投及查看屬於自己的工項與個人分數）。未登入時存取任何頁面皆自動導向 /login 驗證。",
                Steps = new[]
                {
                    "在登入畫面輸入您的「<strong>員工登入帳號 (loginAccount)</strong>」（例如 <code>admin</code> 或員工個人帳號）。",
                    "輸入「<strong>登入密碼 (plainPassword)</strong>」，可點擊右側「<strong>👁️ 顯示 / 🙈 隱藏</strong>」按鈕確認輸入字元無誤。",
                    "點擊「<strong>🔐 驗證身分並登入系統</strong>」，系統透過預存程序 <code>storyPoint.spVerifyEmployeeLogin</code> 檢核 SHA-256 密碼雜湊與啟用狀態。",
                    "作業完畢後，於畫面右上角、置頂懸浮導覽列或獨立盲投頁頂部點擊紅色「<strong>🚪 登出系統</strong>」，自動釋放盲投 Session 鎖定、清除 Cookie 並廣播通知所有分頁安全登出。"
                },
                Callouts = new[]
                {
                    "① 輸入員工登入帳號 (loginAccount)",
                    "② 輸入密碼並支援 👁️ 顯示/隱藏切換",
                    "③ 點擊「驗證身分並登入系統」按鈕",
                    "④ 頂部常駐紅色「🚪 登出系統」安全清除憑證"
                },
                UiDiagramHtml = @"<div style='display:flex;flex-wrap:wrap;gap:8px;align-items:center;'>
                    <span class='badge' style='background:#1e293b;color:#fff;'>畫面元件示意：</span>
                    <span style='background:#fff;border:1px solid #cbd5e1;padding:3px 8px;border-radius:6px;font-weight:700;'>👤 員工登入帳號：[ ________ ]</span>
                    <span style='background:#fff;border:1px solid #cbd5e1;padding:3px 8px;border-radius:6px;font-weight:700;'>🔑 登入密碼：[ •••••••• ] <span style='background:#e2e8f0;padding:1px 5px;border-radius:4px;'>👁️ 顯示</span></span>
                    <span style='background:#2563eb;color:#fff;padding:3px 9px;border-radius:6px;font-weight:800;'>🔐 驗證身分並登入系統</span>
                    <span style='background:#dc2626;color:#fff;padding:3px 9px;border-radius:6px;font-weight:800;'>🚪 登出系統</span>
                </div>"
            },
            new
            {
                Id = "topic-my-account",
                Num = "02",
                Category = "auth",
                CategoryName = "🔐 登入驗證與帳密安全",
                RoleScope = "all",
                RoleBadge = "<span class='badge badge-user'>🙋 全體員工適用</span>",
                Title = "自助修改個人登入帳號與密碼（免重新登入即時生效）",
                Keywords = "修改帳密 修改密碼 修改帳號 個人帳密 loginAccount passwordHash spUpdateEmployeeSelfCredential 免重新登入",
                RouteUrl = "http://localhost:5088/?tab=myAccount",
                ImageFile = "/help/images/screen_11_my_account.png",
                ActionHtml = isStandalonePage
                    ? "<a href='/?tab=myAccount' class='btn btn-primary' style='padding:6px 12px;font-size:12px;'>⚡ 前往修改我的帳密畫面</a>"
                    : "<button type='button' class='btn btn-primary' style='padding:6px 12px;font-size:12px;' onclick=\"switchTab('myAccount')\">⚡ 立即切換至「🔑 修改我的帳密」畫面</button>",
                Summary = "不論是管理者 (Admin) 或一般參與者 (Participant)，每個人皆可隨時自助修改自己本人的「登入帳號 (loginAccount)」與「登入密碼」，系統內建帳號唯一性防呆檢核並自動刷新當前登入 Cookie。",
                Steps = new[]
                {
                    "點擊畫面右上角藍色「<strong>🔑 修改我的帳密</strong>」按鈕，或頂部主選單「<strong>🔑 修改帳密</strong>」。",
                    "於「<strong>👤 我的登入帳號 (newLoginAccount)</strong>」確認或輸入欲變更的新登入帳號（系統會自動檢核避免與其他同仁重複）。",
                    "於「<strong>🔒 新登入密碼</strong>」與「<strong>✅ 確認新登入密碼</strong>」輸入新密碼（若僅修改登入帳號，密碼欄可留空），支援點擊「<strong>👁️ 顯示</strong>」檢視明文。",
                    "點擊「<strong>💾 儲存修改我的登入帳號與密碼</strong>」，後端呼叫 <code>storyPoint.spUpdateEmployeeSelfCredential</code> 並即時更新瀏覽器登入憑證，無須重新登入即可繼續操作。"
                },
                Callouts = new[]
                {
                    "① 頂部工具列/主選單點擊「🔑 修改我的帳密」",
                    "② 修改個人登入帳號 (內建防重複檢核)",
                    "③ 輸入新密碼與確認新密碼 (支援 👁️ 顯示)",
                    "④ 點擊「💾 儲存修改」即時更新 Cookie 免重登"
                },
                UiDiagramHtml = @"<div style='display:flex;flex-wrap:wrap;gap:8px;align-items:center;'>
                    <span class='badge' style='background:#1e293b;color:#fff;'>畫面元件示意：</span>
                    <span style='background:#2563eb;color:#fff;padding:3px 9px;border-radius:6px;font-weight:800;'>🔑 修改我的帳密</span>
                    <span style='background:#fff;border:1px solid #cbd5e1;padding:3px 8px;border-radius:6px;font-weight:700;'>👤 我的登入帳號：[ admin ]</span>
                    <span style='background:#fff;border:1px solid #cbd5e1;padding:3px 8px;border-radius:6px;font-weight:700;'>🔒 新密碼 + ✅ 確認新密碼</span>
                    <span style='background:#1d4ed8;color:#fff;padding:3px 9px;border-radius:6px;font-weight:800;'>💾 儲存修改我的登入帳號與密碼</span>
                </div>"
            },
            new
            {
                Id = "topic-voting-lobby",
                Num = "03",
                Category = "voting",
                CategoryName = "🃏 多人即時盲投與回合設定",
                RoleScope = "all",
                RoleBadge = "<span class='badge badge-user'>🙋 全體員工適用</span>",
                Title = "多人即時盲投大廳：「📂 待參與」與「🟢 參與中」分類、邊欄收合與進入獨立網頁",
                Keywords = "盲投大廳 待參與 參與中 已拍板定案 資料夾收合 邊欄收合 重新整理 快速發起盲投 Planning Poker",
                RouteUrl = "http://localhost:5088/?tab=voting",
                ImageFile = "/help/images/screen_02_voting_lobby.png",
                ActionHtml = isStandalonePage
                    ? "<a href='/?tab=voting' class='btn btn-primary' style='padding:6px 12px;font-size:12px;'>⚡ 前往即時盲投大廳</a>"
                    : "<button type='button' class='btn btn-primary' style='padding:6px 12px;font-size:12px;' onclick=\"switchTab('voting')\">⚡ 立即切換至「🃏 即時盲投大廳」畫面</button>",
                Summary = "盲投大廳依登入者個人狀態將進行中議程自動分為「📂 待參與」與「🟢 參與中」兩大區塊（歷史已完成議程歸入預設收合之「📁 已拍板定案議程」），由個人從「待參與」點選才開啟專屬獨立網頁進入「參與中」。",
                Steps = new[]
                {
                    "在左側「<strong>📌 投票議程清單</strong>」或右側大廳總覽檢視屬於您的「<strong>📂 1. 待參與</strong>」議程清單（支援點擊 <code>▾ 詳情</code> 展開檢視點數範圍、共識規則與受邀名單）。",
                    "點選任一待參與議程的「<strong>🚀 點選進入『參與中』獨立網頁</strong>」按鈕，系統會在獨立瀏覽器分頁開啟專屬盲投桌面 <code>/voting/session?sessionPk=X</code>。",
                    "進入後，該筆議程在大廳會立即移入「<strong>🟢 2. 參與中 (獨立網頁進行中)</strong>」區塊并啟動防重複進入鎖定。",
                    "左側邊欄支援點擊「<strong>◀ 縮起邊欄</strong>」放大右側視野、「<strong>🔄 重新整理</strong>」即時同步最新議程，管理員亦可點擊「<strong>⚡ 快速發起盲投議程 (Modal)</strong>」一鍵建立新議程。"
                },
                Callouts = new[]
                {
                    "① 左側議程清單分「📂 待參與 / 🟢 參與中 / 📁 已定案」",
                    "② 點擊「🚀 點選進入『參與中』獨立網頁」開始盲投",
                    "③ 已開啟之議程自動歸入「🟢 參與中」並防重複進入",
                    "④ 支援「◀ 縮起邊欄」、「🔄 重新整理」與「⚡ 快速發起盲投」"
                },
                UiDiagramHtml = @"<div style='display:flex;flex-wrap:wrap;gap:8px;align-items:center;'>
                    <span class='badge' style='background:#1e293b;color:#fff;'>畫面元件示意：</span>
                    <span style='background:#fef3c7;color:#92400e;border:1px solid #f59e0b;padding:3px 8px;border-radius:6px;font-weight:800;'>📂 1. 待參與 (點選進入參與中)</span>
                    <span style='background:#2563eb;color:#fff;padding:3px 9px;border-radius:6px;font-weight:800;'>🚀 點選進入「參與中」獨立網頁 ➔</span>
                    <span style='background:#dcfce7;color:#166534;border:1px solid #4ade80;padding:3px 8px;border-radius:6px;font-weight:800;'>🟢 2. 參與中 (禁重複進入)</span>
                </div>"
            },
            new
            {
                Id = "topic-voting-standalone",
                Num = "04",
                Category = "voting",
                CategoryName = "🃏 多人即時盲投與回合設定",
                RoleScope = "all",
                RoleBadge = "<span class='badge badge-user'>🙋 全體員工適用</span>",
                Title = "獨立盲投 Session 網頁：撲克牌中文意義、全員投完自動翻牌、開啟下一輪與拍板定案",
                Keywords = "獨立網頁 出牌 撲克牌 費氏數列 疑問牌 ? 休息牌 ☕ 自動翻牌 下一回合 討論紀錄 拍板定案 定案決議說明 多網頁並行 禁止重複進入",
                RouteUrl = "http://localhost:5088/voting/session?sessionPk=1",
                ImageFile = "/help/images/screen_03_voting_standalone.png",
                ActionHtml = isStandalonePage
                    ? "<a href='/?tab=voting' class='btn btn-primary' style='padding:6px 12px;font-size:12px;'>🃏 前往盲投大廳選擇議程</a>"
                    : "<button type='button' class='btn btn-primary' style='padding:6px 12px;font-size:12px;' onclick=\"switchTab('voting')\">🃏 前往盲投大廳進入獨立盲投網頁</button>",
                Summary = "整個盲投估算過程在專屬獨立網頁進行。支援同一人同時開啟多個獨立網頁參與「不同」Session，但嚴格禁止對「同一個」Session 重複開啟第二個網頁。當全員完成出牌時，所有人的畫面即刻自動同步翻開撲克牌！",
                Steps = new[]
                {
                    "<strong>盲投出牌階段 (voting)</strong>：於撲克牌選擇區點選牌面（每張牌皆標示中文複雜度意義，如 <code>3: 標準一般</code>、<code>8: 高複雜度</code>、<code>?: 需求不明/需討論</code>、<code>☕: 暫停休息</code>），支援勾選「<strong>⚡ 點選牌面即直接送出盲投</strong>」或雙擊牌面快速出牌；翻牌前他人座位僅顯示 <code>🎴 已出牌(蓋牌)</code>。",
                    "<strong>全員投完即刻自動翻牌 (revealed)</strong>：當所有必投參與者皆完成投票時，<code>storyPoint.spCastVote</code> 自動將狀態切換為已翻牌，透過 SignalR 與 2 秒輪詢讓所有人畫面同步翻開撲克牌並顯示平均、最低、最高、分歧落差與共識推薦點數。",
                    "<strong>主持人開啟下一回合 (Next Round)</strong>：若點數分歧過大，主持人於控制台輸入「<strong>📝 本回合討論紀錄 / 下回合聚焦目標</strong>」並點擊「<strong>🔄 2. 開啟下一回合重新盲投</strong>」，討論紀錄永久保存於右側歷次回合表並更新至桌面頂部。",
                    "<strong>拍板定案 (Finalize) 與結束參與</strong>：達成共識後，主持人輸入「<strong>🏆 定案決議說明</strong>」並點擊「<strong>🏆 3. 一鍵採用推薦點數拍板定案</strong>」，系統自動回寫標準點數或工項點數；點擊頂部「<strong>🚪 結束參與並返回大廳</strong>」即可釋放鎖定。"
                },
                Callouts = new[]
                {
                    "① 綠色撲克牌桌座位區 (盲投蓋牌 ➔ 全員投完自動翻牌)",
                    "② 撲克牌出牌區 (標示繁體中文複雜度意義，支援雙擊快投)",
                    "③ 主持人控制台：輸入討論紀錄開啟下一輪或一鍵推薦點數定案",
                    "④ 右側歷次盲投回合統計表、具名出牌明細與頂部多網頁並行導覽列"
                },
                UiDiagramHtml = @"<div style='display:flex;flex-wrap:wrap;gap:8px;align-items:center;'>
                    <span class='badge' style='background:#1e293b;color:#fff;'>撲克牌與控制示意：</span>
                    <span style='background:#fff;border:2px solid #2563eb;padding:2px 8px;border-radius:6px;font-weight:800;color:#1e3a8a;'>3 <small>(標準一般)</small></span>
                    <span style='background:#fff;border:2px solid #2563eb;padding:2px 8px;border-radius:6px;font-weight:800;color:#1e3a8a;'>? <small>(需求不明)</small></span>
                    <span style='background:#fff;border:2px solid #2563eb;padding:2px 8px;border-radius:6px;font-weight:800;color:#1e3a8a;'>☕ <small>(暫停休息)</small></span>
                    <span style='background:#d97706;color:#fff;padding:3px 8px;border-radius:6px;font-weight:800;'>🔄 2. 開啟下一回合重新盲投</span>
                    <span style='background:#16a34a;color:#fff;padding:3px 8px;border-radius:6px;font-weight:800;'>🏆 3. 一鍵採用推薦點數拍板定案</span>
                </div>"
            },
            new
            {
                Id = "topic-round-setup",
                Num = "05",
                Category = "voting",
                CategoryName = "🃏 多人即時盲投與回合設定",
                RoleScope = "admin",
                RoleBadge = "<span class='badge badge-admin'>👑 管理者主持適用</span>",
                Title = "設定盲投回合（投什麼標的、高低點數上下限、誰可參與、共識演算法與快速範本）",
                Keywords = "設定回合 盲投開始前設定 估算標的 自動建立代表類型 最低點數 最高點數 共識規則 nearestFibonacci 分歧門檻 倒數計時 受邀參與者 快速範本",
                RouteUrl = "http://localhost:5088/?tab=roundSetup",
                ImageFile = "/help/images/screen_04_round_setup.png",
                ActionHtml = isStandalonePage
                    ? "<a href='/?tab=roundSetup' class='btn btn-primary' style='padding:6px 12px;font-size:12px;'>⚙️ 前往設定盲投回合</a>"
                    : "<div style='display:flex;gap:6px;flex-wrap:wrap;'><button type='button' class='btn btn-primary' style='padding:6px 12px;font-size:12px;' onclick=\"switchTab('roundSetup')\">⚡ 切換至「⚙️ 設定回合」頁面</button>" +
                      (isAdmin ? "<button type='button' class='btn btn-outline' style='padding:6px 12px;font-size:12px;border-color:#2563eb;color:#1d4ed8;' onclick=\"openSessionSetupEditModal(0)\">🪟 直接彈出「快速發起盲投 Modal」</button>" : "") + "</div>",
                Summary = "於盲投開始前完整設定本回合估算標的、允許出牌的最低/最高 Story Point 範圍、受邀參與成員名單、共識計算演算法與單回合思考倒數計時，並提供即時撲克牌面預覽與外鍵自動建立防呆。",
                Steps = new[]
                {
                    "點擊頂部「<strong>⚙️ 設定回合</strong>」主選單，或於大廳點擊「<strong>⚡ 快速發起盲投議程 (彈出 Modal)</strong>」；可一鍵套用上方「<strong>📐 標準 Scrum 範本</strong>」、「<strong>🚀 快速閃電範本</strong>」或「<strong>🏛️ 嚴謹架構評審範本</strong>」。",
                    "<strong>設定【1. 投什麼】</strong>：選擇「工項的代表類型 (<code>workItemType</code>)」或「個別特定工項 (<code>workItem</code>)」；若資料庫尚無項目，可直接選擇「<code>➕ 依下方盲投議程標題自動建立新代表類型/工項</code>」，系統會自動建立並綁定主鍵。",
                    "<strong>設定【2. 點數上下限與規則】</strong>：選擇撲克牌組、設定最低點數 (<code>minStoryPoint</code>) 與最高點數 (<code>maxStoryPoint</code>)、共識計算規則（對齊費氏數列、算術平均、中位數、眾數等）與計時秒數，下方會即時預覽本回合可投出的撲克牌面。",
                    "<strong>設定【3. 誰可以參與】</strong>：支援「全選啟用員工」、「依部門批次勾選」或「僅勾選該工項負責員工」，最後點擊「<strong>🚀 儲存回合設定並開啟盲投桌面</strong>」。"
                },
                Callouts = new[]
                {
                    "① 一鍵套用敏捷估算範本 (標準 Scrum / 快速閃電 / 嚴謹評審)",
                    "② 選擇估算標的 (支援依議程標題自動建立新代表類型或新工項)",
                    "③ 設定最低/最高 Story Point、共識規則與即時撲克牌面預覽",
                    "④ 依部門或全選勾選受邀參與員工並儲存啟動盲投"
                },
                UiDiagramHtml = @"<div style='display:flex;flex-wrap:wrap;gap:8px;align-items:center;'>
                    <span class='badge' style='background:#1e293b;color:#fff;'>畫面元件示意：</span>
                    <span style='background:#eff6ff;border:1px solid #93c5fd;color:#1d4ed8;padding:3px 8px;border-radius:6px;font-weight:700;'>⚡ 範本：標準 Scrum (0.5~21pt)</span>
                    <span style='background:#fff;border:1px solid #cbd5e1;padding:3px 8px;border-radius:6px;font-weight:700;'>📉 最低：0.5 pt ～ 📈 最高：21 pt</span>
                    <span style='background:#fff;border:1px solid #cbd5e1;padding:3px 8px;border-radius:6px;font-weight:700;'>🏢 依部門快速勾選參與者</span>
                    <span style='background:#2563eb;color:#fff;padding:3px 9px;border-radius:6px;font-weight:800;'>🚀 儲存回合設定並開啟盲投桌面</span>
                </div>"
            },
            new
            {
                Id = "topic-history",
                Num = "06",
                Category = "voting",
                CategoryName = "🃏 多人即時盲投與回合設定",
                RoleScope = "all",
                RoleBadge = "<span class='badge badge-user'>🙋 全員適用 (依角色自動隔離)</span>",
                Title = "事後查詢訂出 Story Point 的完整過程与點數異動稽核歷程",
                Keywords = "估算過程 投票歷史 出牌明細 回合統計 點數異動歷程 tbPointHistory 稽核紀錄 定案決議說明",
                RouteUrl = "http://localhost:5088/?tab=history",
                ImageFile = "/help/images/screen_05_history.png",
                ActionHtml = isStandalonePage
                    ? "<a href='/?tab=history' class='btn btn-primary' style='padding:6px 12px;font-size:12px;'>📜 前往估算過程查詢</a>"
                    : "<button type='button' class='btn btn-primary' style='padding:6px 12px;font-size:12px;' onclick=\"switchTab('history')\">⚡ 立即切換至「📜 1-3. 估算過程查詢」畫面</button>",
                Summary = "完整保留並可隨時回溯每一場盲投議程的各回合統計、討論摘要、定案決議說明、每位參與者各回合投出的撲克牌與理由，以及工項與代表類型的 Story Point 異動稽核軌跡。",
                Steps = new[]
                {
                    "由頂部選單選擇「<strong>🃏 多人估算 ➔ 📜 1-3. 估算過程查詢</strong>」進入歷史稽核頁面。",
                    "上方「<strong>歷次投票回合統計與討論過程</strong>」表格完整列出每場議程的第 1、2... 回合狀態、出牌人數、最低/最高/平均點數、回合討論紀錄與 <code>🏆 定案決議說明</code>。",
                    "中段「<strong>各回合參與者出牌明細</strong>」列出每一回合投出的撲克牌與估算理由（管理者可檢視全體具名紀錄；一般參與者依隱私保護僅顯示本人紀錄）。",
                    "下方「<strong>Story Point 點數異動稽核歷程表 (tbPointHistory)</strong>」完整記錄每一次點數變更來源（如 <code>typeDefault</code> 自動帶入、<code>votingFinalize</code> 投票定案、<code>manualAdjust</code> 手動微調、<code>autoExpireDeduct</code> 到期自動扣點）及變更前後點數。"
                },
                Callouts = new[]
                {
                    "① 歷次投票回合統計表 (含最低/最高/平均點數與定案決議說明)",
                    "② 各回合參與者具名出牌點數與估算理由明細表",
                    "③ Story Point 點數異動稽核歷程表 (變更前後點數與原因)",
                    "④ 支援匯出 CSV / Excel / 互動式 HTML 歷史稽核報表"
                },
                UiDiagramHtml = @"<div style='display:flex;flex-wrap:wrap;gap:8px;align-items:center;'>
                    <span class='badge' style='background:#1e293b;color:#fff;'>異動來源標籤：</span>
                    <span class='badge badge-user'>typeDefault 代表類型帶入</span>
                    <span class='badge badge-finalized'>votingFinalize 盲投拍板定案</span>
                    <span class='badge badge-admin'>manualAdjust 管理者手動微調</span>
                    <span class='badge' style='background:#fee2e2;color:#b91c1c;'>autoExpireDeduct 到期自動扣點</span>
                </div>"
            },
            new
            {
                Id = "topic-workitem",
                Num = "07",
                Category = "workitem",
                CategoryName = "📋 工項、代表類型與到期扣點",
                RoleScope = "all",
                RoleBadge = "<span class='badge badge-user'>🙋 全員適用 (管理員增刪修 / 員工查閱本人)</span>",
                Title = "工項管理、選擇代表類型自動帶入點數、起訖時間快速設定與到期自動扣點寄信通知",
                Keywords = "工項管理 新增工項 代表類型自動帶入 手動微調 起訖時間 天 周 月 季 年 到期自動扣點 Email 通知 部門經理 HR 員工指派 分攤比例 allocationPercent",
                RouteUrl = "http://localhost:5088/?tab=workitems",
                ImageFile = "/help/images/screen_06_workitems.png",
                ActionHtml = isStandalonePage
                    ? "<a href='/?tab=workitems' class='btn btn-primary' style='padding:6px 12px;font-size:12px;'>📋 前往工項管理</a>"
                    : "<div style='display:flex;gap:6px;flex-wrap:wrap;'><button type='button' class='btn btn-primary' style='padding:6px 12px;font-size:12px;' onclick=\"switchTab('workitems')\">⚡ 切換至「📋 2-1. 工項管理」畫面</button>" +
                      (isAdmin ? "<button type='button' class='btn btn-outline' style='padding:6px 12px;font-size:12px;border-color:#2563eb;color:#1d4ed8;' onclick=\"openWorkItemEditModal(0)\">🪟 直接彈出「新增工項與指派 Modal」</button>" : "") + "</div>",
                Summary = "建立工項時選擇「工項代表類型」即可自動帶入標準 Story Point（亦允許管理員依複雜度手動微調），支援單一 Modal 同時指派負責員工與分攤配額，並提供起訖時間「數量 + 天/周/月/季/年」快速計算與到期自動轉態、扣點、寄發 Email 給部門主管與 HR。",
                Steps = new[]
                {
                    "在「<strong>📋 2-1. 工項與員工指派</strong>」頂部快捷指令列點擊「<strong>➕ 新增工項與指派 (彈出 Modal)</strong>」，或展開管理表單抽屜。",
                    "選擇「<strong>🗂️ 工項類型</strong>（🏗️ 專案 / 🛠️ 維運 / ⏳ 顧問時數 / 🎓 教育訓練 / 🚀 開發產品）」與「<strong>🏷️ 工項代表類型</strong>」，系統自動將代表類型的標準點數帶入 <code>Story Point</code> 欄位（管理員亦可手動調整點數並填寫微調原因）。",
                    "設定「<strong>📅 開始時間 (起時)</strong>」，並於「<strong>⚡ 起訖時間快速設定</strong>」輸入<strong>數量</strong>（如 <code>1</code>、<code>2</code>）與選擇<strong>單位（<code>天</code> / <code>周</code> / <code>月</code> / <code>季</code> / <code>年</code>）</strong>，系統自動算出「<strong>⏰ 結束時間 (迄時)</strong>」；下方可直接選擇負責員工與分攤比例 (<code>allocationPercent</code>)。",
                    "<strong>到期自動轉態、扣減 Story Point 與 Email 通知</strong>：當系統時間達到工項的迄時 (<code>endDate</code>)，背景排程或點擊「<strong>⏰ 立即檢核到期工項</strong>」會自動將狀態轉為「已完成 (已到期扣點)」、將工項與負責員工/部門的有效 Story Point 扣減歸零（保留原核定點數與扣減紀錄），並透過 Email 自動寄信通知該部門所有<strong>部門主管 (<code>isDepartmentManager=1</code>)</strong> 與 <strong>HR 人資主管</strong>。"
                },
                Callouts = new[]
                {
                    "① 頂部快捷指令列：一鍵彈出新增工項 Modal 或多人配額指派 Modal",
                    "② 選擇工項代表類型自動帶入標準 Story Point (或手動微調)",
                    "③ 起訖時間快速設定：輸入【數量】+ 選擇【天/周/月/季/年】自動算迄時",
                    "④ 達到迄時自動轉已完成、扣除 Story Point 並寄 Email 給部門主管與 HR"
                },
                UiDiagramHtml = @"<div style='display:flex;flex-wrap:wrap;gap:8px;align-items:center;'>
                    <span class='badge' style='background:#1e293b;color:#fff;'>起訖與到期機制示意：</span>
                    <span style='background:#fff;border:1px solid #cbd5e1;padding:3px 8px;border-radius:6px;font-weight:700;'>⚡ 快速設定：數量 [ 1 ] + 單位 [ 月 ▼ ] ➔ 自動算迄時</span>
                    <span style='background:#fef3c7;color:#92400e;border:1px solid #f59e0b;padding:3px 8px;border-radius:6px;font-weight:800;'>⏰ 達迄時 ➔ 狀態轉 done + 點數扣減歸零 + 📧 寄信通知部門主管與 HR</span>
                </div>"
            },
            new
            {
                Id = "topic-context-modal",
                Num = "08",
                Category = "table",
                CategoryName = "🖱️ 表格右鍵 Modal、排序鎖定與報表",
                RoleScope = "all",
                RoleBadge = "<span class='badge badge-user'>🙋 全體員工適用</span>",
                Title = "全系統表格即時篩選、純滑鼠右鍵快捷選單與雙擊彈出置中單筆編輯 Modal（不捲動網頁）",
                Keywords = "滑鼠右鍵 右鍵選單 contextmenu 雙擊編輯 Modal 視窗 單筆編輯 不捲動網頁 快捷鍵 Enter F2 表格篩選 複製紀錄",
                RouteUrl = "http://localhost:5088/?tab=workitems",
                ImageFile = "/help/images/screen_07_context_modal.png",
                ActionHtml = isStandalonePage
                    ? "<a href='/?tab=workitems' class='btn btn-primary' style='padding:6px 12px;font-size:12px;'>🖱️ 前往工項表格體驗右鍵與雙擊 Modal</a>"
                    : "<button type='button' class='btn btn-primary' style='padding:6px 12px;font-size:12px;' onclick=\"switchTab('workitems')\">⚡ 前往表格體驗「滑鼠右鍵選單 / 雙擊彈出 Modal」</button>",
                Summary = "全系統所有資料表格皆直接置頂呈現，全面取消列內下拉按鈕，改採「滑鼠左鍵單擊選取、左鍵雙擊（或按 Enter/F2）直接彈出置中單筆編輯 Modal、滑鼠右鍵彈出專屬操作選單」，編輯時絕不上下捲動網頁位置！",
                Steps = new[]
                {
                    "<strong>表格工具列即時篩選</strong>：每張表格上方皆提供「<code>🏷️ 欄位選擇</code>」+「<code>🎯 欄位既有值下拉選單</code>」+「<code>🔍 關鍵字快速篩選</code>」，輸入或點選後即時過濾並顯示 <code>顯示 X / Y 筆</code>。",
                    "<strong>滑鼠左鍵雙擊 / Enter / F2 快速編輯</strong>：在任一筆資料列雙擊滑鼠左鍵（或單擊選取後按鍵盤 <code>Enter</code> / <code>F2</code>），立即原地彈出置中的「<strong>🪟 單筆編輯 Modal 視窗</strong>」（含起訖時間數量+單位快速設定與員工配額指派），完全不捲動背景網頁。",
                    "<strong>滑鼠右鍵快捷選單 (<code>contextmenu</code>)</strong>：在任一筆資料列按滑鼠右鍵，自動高亮選取該列並於游標處彈出操作選單（依表格類型提供：單筆編輯 Modal、修改賦予員工與配額 Modal、發起多人盲投 Modal、設為已到期測試扣點寄信、以此儲存格值篩選表格、複製本列文字、刪除紀錄）。",
                    "<strong>PowerShell GUI 同步體驗</strong>：在 <code>ps/ui/uiMain.ps1</code> 各頁籤的 <code>DataGridView</code> 中，同樣支援滑鼠右鍵快捷選單與雙擊列直接彈出 WinForms Modal 視窗進行單筆編輯。"
                },
                Callouts = new[]
                {
                    "① 表格上方通用工具列：指定欄位、欄位既有值下拉與關鍵字即時篩選",
                    "② 在資料列按【滑鼠右鍵】彈出專屬快捷操作選單 (#spRowContextMenu)",
                    "③ 點選右鍵編輯或【滑鼠左鍵雙擊列】原地彈出置中單筆編輯 Modal",
                    "④ 在 Modal 內直接完成修改與儲存，背景網頁保持原捲動位置不動"
                },
                UiDiagramHtml = @"<div style='display:flex;flex-wrap:wrap;gap:8px;align-items:center;'>
                    <span class='badge' style='background:#1e293b;color:#fff;'>表格互動操作示意：</span>
                    <span style='background:#dbeafe;color:#1e3a8a;border:1px solid #3b82f6;padding:3px 8px;border-radius:6px;font-weight:800;'>🖱️ 左鍵雙擊 / Enter / F2 ➔ 彈出單筆編輯 Modal</span>
                    <span style='background:#0f172a;color:#fff;padding:3px 8px;border-radius:6px;font-weight:800;'>🖱️ 滑鼠右鍵 ➔ ✏️ 單筆編輯 ｜ 👥 指派員工 ｜ 🃏 發起盲投 ｜ 🔍 以此值篩選 ｜ 🗑️ 刪除</span>
                </div>"
            },
            new
            {
                Id = "topic-types-kinds",
                Num = "09",
                Category = "workitem",
                CategoryName = "📋 工項、代表類型與到期扣點",
                RoleScope = "all",
                RoleBadge = "<span class='badge badge-user'>🙋 全員可查閱 / 👑 管理員可增刪修</span>",
                Title = "工項代表類型標準點數管理與工項類型（專案/維運/顧問時數/教育訓練/開發產品）CRUD",
                Keywords = "工項代表類型 標準點數 同步既有工項 工項類型 專案 維運 顧問時數 教育訓練 開發產品 tbWorkItemType tbWorkItemKind",
                RouteUrl = "http://localhost:5088/?tab=types",
                ImageFile = "/help/images/screen_08_types.png",
                ActionHtml = isStandalonePage
                    ? "<a href='/?tab=types' class='btn btn-primary' style='padding:6px 12px;font-size:12px;'>🏷️ 前往工項代表類型</a>"
                    : "<div style='display:flex;gap:6px;flex-wrap:wrap;'><button type='button' class='btn btn-primary' style='padding:6px 12px;font-size:12px;' onclick=\"switchTab('types')\">⚡ 切換至「🏷️ 2-2. 工項代表類型」畫面</button>" +
                      (isAdmin ? "<button type='button' class='btn btn-outline' style='padding:6px 12px;font-size:12px;border-color:#2563eb;color:#1d4ed8;' onclick=\"openWorkItemTypeEditModal(0)\">🪟 彈出「新增代表類型 Modal」</button>" : "") + "</div>",
                Summary = "管理已透過盲投定案或核定標準 Story Point 的「工項代表類型 (tbWorkItemType)」供建立工項時自動帶入點數；同時提供「工項類型主檔 (tbWorkItemKind)」完整增刪修功能。",
                Steps = new[]
                {
                    "切換至「<strong>🏷️ 2-2. 工項代表類型</strong>」頁籤，表格直接置頂列出所有代表類型的代碼、名稱、預設工項類型、複雜度驗收標準與標準 Story Point。",
                    "管理員點擊頂部「<strong>➕ 新增工項代表類型 (彈出 Modal)</strong>」或在表格列按右鍵/雙擊編輯；若勾選「<strong>同步更新目前未完成且未曾手動微調之既有工項點數</strong>」，儲存時會自動更新關聯工項與員工分攤點數。",
                    "點擊頂部「<strong>🗂️ 新增工項類型 (Modal)</strong>」或展開「工項類型增刪修管理」抽屜，可管理內建 5 大工項類型（<code>🏗️ 專案</code>、<code>🛠️ 維運</code>、<code>⏳ 顧問時數</code>、<code>🎓 教育訓練</code>、<code>🚀 開發產品</code>）或新增自訂工項類型、圖示與標籤配色。",
                    "針對尚未定案的代表類型，可在表格列按滑鼠右鍵選擇「<strong>🃏 針對此代表類型發起多人盲投估算</strong>」直接開啟盲投回合設定 Modal。"
                },
                Callouts = new[]
                {
                    "① 置頂快捷指令列：新增工項代表類型 Modal 與新增工項類型 Modal",
                    "② 工項代表類型一覽表：顯示標準 Story Point、估算狀態與套用工項數",
                    "③ 支援勾選「同步更新未完成且未微調之既有工項點數」",
                    "④ 工項類型主檔 (tbWorkItemKind) 完整支援新增、修改與安全刪除轉移"
                },
                UiDiagramHtml = @"<div style='display:flex;flex-wrap:wrap;gap:8px;align-items:center;'>
                    <span class='badge' style='background:#1e293b;color:#fff;'>內建工項類型：</span>
                    <span class='badge' style='background:#dbeafe;color:#1d4ed8;'>🏗️ 專案</span>
                    <span class='badge' style='background:#fef3c7;color:#b45309;'>🛠️ 維運</span>
                    <span class='badge' style='background:#f3e8ff;color:#6d28d9;'>⏳ 顧問時數</span>
                    <span class='badge' style='background:#dcfce7;color:#15803d;'>🎓 教育訓練</span>
                    <span class='badge' style='background:#ffe4e6;color:#be123c;'>🚀 開發產品</span>
                </div>"
            },
            new
            {
                Id = "topic-stats-export",
                Num = "10",
                Category = "table",
                CategoryName = "🖱️ 表格右鍵 Modal、排序鎖定與報表",
                RoleScope = "all",
                RoleBadge = "<span class='badge badge-user'>🙋 全員適用 (管理員看全公司 / 員工看個人)</span>",
                Title = "員工與部門總 Story Point 統計、表頭排序與凍結鎖定、三格式報表匯出 (CSV / Excel / 互動 HTML)",
                Keywords = "統計報表 部門總點數 員工總點數 表頭排序 欄位鎖定 Sticky Header 凍結欄位 匯出 CSV 匯出 Excel 互動 HTML 報表 群組彙總",
                RouteUrl = "http://localhost:5088/?tab=stats",
                ImageFile = "/help/images/screen_09_stats_table.png",
                ActionHtml = isStandalonePage
                    ? "<a href='/?tab=stats' class='btn btn-primary' style='padding:6px 12px;font-size:12px;'>📊 前往統計報表</a>"
                    : "<button type='button' class='btn btn-primary' style='padding:6px 12px;font-size:12px;' onclick=\"switchTab('stats')\">⚡ 立即切換至「📊 3-1. 員工與部門統計」畫面</button>",
                Summary = "即時彙總各部門與每位員工身上的待處理、進行中、已完成及總 Story Point 負載（含上一層主管與到期通知收件人）；全站表格皆具備表頭點擊排序、欄位凍結鎖定與三種格式報表匯出。",
                Steps = new[]
                {
                    "<strong>部門與員工點數統計</strong>：切換至「<strong>📊 3-1. 員工與部門統計</strong>」（一般參與者顯示「我的個人分數統計」），即時查看各部門在職人數、總點數、人均負載，以及每位員工的上一層主管與分攤總點數。",
                    "<strong>表頭排序 (<code>⇅ / ▲ / ▼</code>)</strong>：點擊全系統任一表格的 <code>&lt;th&gt;</code> 表頭標題，即可切換升冪 (<code>▲</code>)、降冪 (<code>▼</code>) 或原始順序 (<code>⇅</code>)，自動識別 Story Point 點數、件數、日期與中文編號（按住 <code>Shift</code> 點擊支援多欄位階層排序）。",
                    "<strong>欄位凍結鎖定 (<code>📌 ➔ 🔒</code>) 與 Sticky 表頭</strong>：點擊表頭右側圖釘按鈕 <code>📌</code> 將其變為 <code>🔒</code>，該欄位整欄即固定於表格最左側，水平捲動時保持凍結不動；表格上方工具列亦可切換頂部表頭列垂直捲動鎖定。",
                    "<strong>三種格式報表匯出 (CSV / Excel / 互動 HTML)</strong>：點擊「<code>📥 匯出 CSV</code>」、「<code>📊 匯出 Excel (.xlsx)</code>」或「<code>🌐 匯出互動 HTML</code>」，其中互動 HTML 報表可獨立離線開啟並內建排序、多條件篩選與動態群組彙總統計表。"
                },
                Callouts = new[]
                {
                    "① 部門總 Story Point 彙總表與員工個人 Story Point 統計表",
                    "② 點擊表頭文字或 ⇅ 切換升冪 ▲ / 降冪 ▼ 智慧型別排序",
                    "③ 點擊表頭右側 📌 切換為 🔒，將該欄凍結固定於表格左側",
                    "④ 支援匯出 CSV、原生 Excel (.xlsx) 與可群組彙總之互動式 HTML 報表"
                },
                UiDiagramHtml = @"<div style='display:flex;flex-wrap:wrap;gap:8px;align-items:center;'>
                    <span class='badge' style='background:#1e293b;color:#fff;'>表頭與匯出控制示意：</span>
                    <span style='background:#dbeafe;color:#1e3a8a;border:1px solid #3b82f6;padding:3px 8px;border-radius:6px;font-weight:800;'>員工姓名 ▲ 🔒 (已鎖定於左側)</span>
                    <span style='background:#f8fafc;border:1px solid #cbd5e1;padding:3px 8px;border-radius:6px;font-weight:700;'>總點數 ⇅ 📌</span>
                    <span style='background:#f0fdf4;color:#166534;border:1px solid #86efac;padding:3px 8px;border-radius:6px;font-weight:800;'>📊 匯出 Excel (.xlsx)</span>
                    <span style='background:#eff6ff;color:#1e40af;border:1px solid #93c5fd;padding:3px 8px;border-radius:6px;font-weight:800;'>🌐 匯出互動 HTML (含群組彙總)</span>
                </div>"
            },
            new
            {
                Id = "topic-org-employees",
                Num = "11",
                Category = "org",
                CategoryName = "👥 組織主管階層、批次匯入與系統 Log",
                RoleScope = "admin",
                RoleBadge = "<span class='badge badge-admin'>👑 管理者適用</span>",
                Title = "組織與員工帳密管理：同部門多位部門主管設定、樹狀遞迴階層（呈現上一層主管）與 CSV/Excel 批次匯入",
                Keywords = "員工管理 部門管理 部門主管 isDepartmentManager 樹狀階層 上一層主管 managerEmployeePk 批次匯入 CSV 範本 Excel 範本 啟用停用",
                RouteUrl = "http://localhost:5088/?tab=employees",
                ImageFile = "/help/images/screen_10_employees_org.png",
                ActionHtml = isStandalonePage
                    ? "<a href='/?tab=employees' class='btn btn-primary' style='padding:6px 12px;font-size:12px;'>👥 前往員工與組織管理</a>"
                    : (isAdmin
                        ? "<div style='display:flex;gap:6px;flex-wrap:wrap;'><button type='button' class='btn btn-primary' style='padding:6px 12px;font-size:12px;' onclick=\"switchTab('employees')\">⚡ 切換至「👥 4-1. 員工與組織管理」畫面</button><button type='button' class='btn btn-outline' style='padding:6px 12px;font-size:12px;border-color:#2563eb;color:#1d4ed8;' onclick=\"openEmployeeEditModal(0)\">🪟 彈出「新增員工帳號 Modal」</button></div>"
                        : "<button type='button' class='btn btn-primary' style='padding:6px 12px;font-size:12px;' onclick=\"switchTab('myAccount')\">🔑 前往個人帳號設定</button>"),
                Summary = "完整管理全公司部門與員工帳號（增、刪、修、查、啟用/停用），支援標記「是否為部門主管 (isDepartmentManager，同部門可設多位主管)」與「上一層主管 (managerEmployeePk)」樹狀遞迴階層，並提供 CSV 與 Excel 雙格式批次匯入。",
                Steps = new[]
                {
                    "在「<strong>👥 4-1. 員工與組織管理</strong>」頂部點擊「<strong>➕ 新增員工帳號 (彈出 Modal)</strong>」或「<strong>🏢 新增/修改部門 (Modal)</strong>」，亦可於員工列表直接雙擊或按右鍵單筆編輯。",
                    "<strong>部門主管與上一層主管設定</strong>：可勾選「<strong>⭐ 設為部門主管 (isDepartmentManager=1)</strong>」（同一個部門支援設定多位主管，工項到期時同部門所有主管與 HR 主管皆會收到通知信），並設定「<strong>🌲 上一層主管 (managerEmployeePk)</strong>」；員工列表與統計表僅精簡呈現每位員工的「上一層主管姓名」。",
                    "<strong>組織樹狀圖檢視</strong>：點擊頂部「<strong>🌳 組織與部門主管樹狀階層檢視</strong>」抽屜按鈕，可展開檢視 L1 頂層主管 ➔ L2 各部門主管 ➔ L3 部門成員的視覺化卡片，並可點擊卡片上「<code>🏢 修改部門</code>」直接彈出部門編輯 Modal。",
                    "<strong>CSV / Excel 批次匯入員工</strong>：點擊「<strong>📤 批次匯入員工 (CSV / Excel)</strong>」抽屜，可下載標準 CSV 或 Excel (<code>.xlsx</code>) 範本，上傳檔案或貼上文字即可批次新增/更新員工與自動建立部門。"
                },
                Callouts = new[]
                {
                    "① 頂部快捷指令列：新增員工 Modal、修改部門 Modal、組織樹與批次匯入抽屜",
                    "② 支援同部門設定多位部門主管 (isDepartmentManager) 並指定上一層主管",
                    "③ 員工一覧表精簡顯示「上一層主管」與到期通知對象清單",
                    "④ 支援上傳 CSV 或 Excel (.xlsx) 批次匯入員工名單與自動建立部門"
                },
                UiDiagramHtml = @"<div style='display:flex;flex-wrap:wrap;gap:8px;align-items:center;'>
                    <span class='badge' style='background:#1e293b;color:#fff;'>組織階層與匯入示意：</span>
                    <span style='background:#fef3c7;color:#92400e;border:1px solid #f59e0b;padding:3px 8px;border-radius:6px;font-weight:800;'>⭐ 部門主管 (同部門可多位，到期皆通知)</span>
                    <span style='background:#eff6ff;color:#1e40af;border:1px solid #93c5fd;padding:3px 8px;border-radius:6px;font-weight:800;'>🌲 列表僅呈現「上一層主管」</span>
                    <span style='background:#dcfce7;color:#166534;border:1px solid #86efac;padding:3px 8px;border-radius:6px;font-weight:800;'>📤 支援 CSV 與 Excel (.xlsx) 批次匯入</span>
                </div>"
            },
            new
            {
                Id = "topic-logs-powershell",
                Num = "12",
                Category = "org",
                CategoryName = "👥 組織主管階層、批次匯入與系統 Log",
                RoleScope = "admin",
                RoleBadge = "<span class='badge badge-admin'>👑 管理者與維運適用</span>",
                Title = "系統防崩潰保護、錯誤 Log 查詢中心 (tbLog) 與 PowerShell 多 Tab GUI 管理工具",
                Keywords = "系統 Log 錯誤 Log tbLog 防崩潰 不當機 StackTrace 錯誤測試 PowerShell uiMain.ps1 Console 模組 DPAPI 加密連線",
                RouteUrl = "http://localhost:5088/?tab=logs",
                ImageFile = "/help/images/screen_12_logs.png",
                ActionHtml = isStandalonePage
                    ? "<a href='/?tab=logs' class='btn btn-primary' style='padding:6px 12px;font-size:12px;'>🛡️ 前往系統 Log 查詢中心</a>"
                    : (isAdmin
                        ? "<button type='button' class='btn btn-primary' style='padding:6px 12px;font-size:12px;' onclick=\"switchTab('logs')\">⚡ 立即切換至「🛡️ 5-1. 系統 Log 查詢」畫面</button>"
                        : "<span class='badge badge-admin'>🔒 系統 Log 僅限管理者帳號存取</span>"),
                Summary = "採用三層式防崩潰保護架構（SQL TRY-CATCH + Controller 攔截 + 全域 Exception Middleware）確保發生任何錯誤絕不當機，並同步提供管理者系統 Log 查詢中心與 Windows PowerShell 多 Tab GUI 桌面管理工具。",
                Steps = new[]
                {
                    "<strong>查詢與過濾系統 Log</strong>：管理者點擊頂部「<strong>🛡️ 系統 Log</strong>」進入查詢中心，可透過「關鍵字」、「Log 嚴重層級（<code>1-錯誤</code>、<code>2-警告</code>、<code>3-資訊</code>、<code>4-除錯</code>）」、「來源模組」或點擊「<strong>❌ 只看錯誤異常</strong>」快速定位問題；長篇錯誤可點擊 <code>🔍 點擊展開 StackTrace 錯誤堆疊明細</code>。",
                    "<strong>管理者一鍵防崩潰測試</strong>：於上方測試面板選擇「情境 A：Controller 例外」、「情境 B：SQL 預存程序參數異常」或「情境 C：全域 Middleware 例外」並點擊「<strong>⚡ 立即觸發測試錯誤</strong>」，即可驗證系統攔截錯誤不當機且完整寫入 <code>storyPoint.tbLog</code> 與本機 CSV。",
                    "<strong>啟動 PowerShell 多 Tab 圖形化管理工具</strong>：於終端機執行 <code>pwsh -NoProfile -File c:\\temp\\storyPoint\\ps\\ui\\uiMain.ps1 -uiMode full</code>，通過員工帳號登入驗證後即可使用桌面版多頁籤介面（含盲投、代表類型、工項、員工、報表與<strong>📖 線上說明與搜尋</strong>頁籤）。",
                    "<strong>正式環境連線加密</strong>：執行 <code>pwsh -NoProfile -File c:\\temp\\storyPoint\\ps\\setEncryptedConfig.ps1</code> 可將 SQL 連線字串以 Windows DPAPI 加密寫入 <code>config.json</code>。"
                },
                Callouts = new[]
                {
                    "① 系統 Log KPI 指標卡 (總筆數、❌ 錯誤數、⚠️ 警告數、⏱️ 平均耗時)",
                    "② 管理者一鍵防崩潰與錯誤攔截模擬測試面板 (情境 A / B / C)",
                    "③ 多維度即時篩選工具列 (關鍵字、嚴重層級、來源模組、只看錯誤)",
                    "④ 緊湊置頂 Log 表格與可展開之 StackTrace 錯誤堆疊明細"
                },
                UiDiagramHtml = @"<div style='display:flex;flex-wrap:wrap;gap:8px;align-items:center;'>
                    <span class='badge' style='background:#1e293b;color:#fff;'>防護與桌面工具：</span>
                    <span class='badge' style='background:#fee2e2;color:#b91c1c;border:1px solid #fca5a5;'>🛡️ 三層防崩潰保護 (絕不出現 HTTP 500)</span>
                    <span style='background:#0f172a;color:#38bdf8;padding:3px 8px;border-radius:6px;font-family:Consolas,monospace;font-size:11.5px;'>pwsh -File ps\ui\uiMain.ps1 -uiMode full</span>
                </div>"
            }
        };

        var faqs = new[]
        {
            new
            {
                Q = "Q0-1. 什麼是 Story Point？它的核心概念與用途是什麼？（為什麼不直接估工時？）",
                A = "<strong>Story Point（故事點／工作複雜度點數）</strong>是用來評估一項任務「<strong>客觀相對複雜度與工作規模</strong>」的單位，而非估算「要做幾小時或幾天」。因為資深人員 2 小時完成的事，新進人員可能需 2 天，直接估時間容易陷入「以誰的速度為準」的爭執；但任務本身的難度是客觀的。不必套用繁瑣流程，Story Point 最核心的用途就是<strong>建立全團隊一致的複雜度度量衡</strong>，打破以往「只算工作件數（張數）」的盲點，讓整個團隊與每位成員的<strong>真實工作量與貢獻度完全透明</strong>。",
                Keywords = "story point 故事點 概念 用途 為什麼不用工時 相對複雜度 工作量透明"
            },
            new
            {
                Q = "Q0-2. 如何具體評估一個工項的複雜度？如何透過 Story Point 讓團隊與個人的工作量透明？",
                A = "評估複雜度時請綜合考量三大維度：<strong>① 技術複雜度 (Complexity)</strong>、<strong>② 工作投入規模 (Effort / Volume)</strong>、<strong>③ 未知風險與不確定性 (Risk / Uncertainty)</strong>，並以費氏數列（<code>1 點極簡基準</code>、<code>2~3 點標準常規</code>、<code>5~8 點中高複雜度</code>、<code>13~21 點大型高風險建議拆解</code>）進行相對比較。定案後搭配本系統的「<strong>重要性權重乘數 (<code>weightMultiplier</code>)</strong>」計算加權總分，即可在「📊 統計報表」即時透明呈現每位成員與各部門的「<strong>進行中點數（當前在途負載）</strong>」與「<strong>已完成點數（累積實質貢獻）</strong>」，主管能據此公平調配資源、避免勞逸不均。",
                Keywords = "如何評估複雜度 複雜度評估 三大維度 技術複雜度 工作規模 未知風險 費氏數列 團隊工作量 個人工作量 透明 進行中點數 已完成點數 權重"
            },
            new
            {
                Q = "Q1. 為什麼我用一般員工（參與者 participant）帳號登入後，看不到其他同事的工項或點數？",
                A = "這是系統內建的「<strong>個人資料隱私保護與角色資料隔離機制</strong>」。一般參與者 (<code>participant</code>) 登入後，在「我的工項查詢」與「我的分數統計」中僅會顯示賦予給您本人的工項與個人配額點數；唯有具備管理者 (<code>admin</code>) 權限的帳號才能查閱全公司所有員工與各部門的完整工項與統計。",
                Keywords = "看不到 別人的工項 權限 參與者 participant 隱私 資料隔離"
            },
            new
            {
                Q = "Q2. 為什麼盲投大廳的進行中議程分成「📂 待參與」與「🟢 參與中」？為什麼同一個議程無法開第二個分頁？",
                A = "為了支援同一人能以多個獨立瀏覽器分頁並行參與「不同」的盲投議程，並防止同一場議程重複開啟造成選票狀態互相覆蓋：尚未開啟獨立網頁的議程皆位於「<strong>📂 待參與</strong>」；當您點擊進入獨立盲投網頁 (<code>/voting/session?sessionPk=X</code>) 後，該議程即移入「<strong>🟢 參與中</strong>」並啟動伺服器端與瀏覽器雙重鎖定，禁止重複開啟同一場 Session。點擊獨立網頁頂部「<strong>🚪 結束參與並返回大廳</strong>」或關閉分頁即可釋放鎖定並移回待參與。",
                Keywords = "待參與 參與中 重複進入 第二個分頁 獨立網頁 鎖定 session"
            },
            new
            {
                Q = "Q3. 盲投出牌時，什麼時候會自動翻牌？需要主持人手動按翻牌嗎？",
                A = "議程預設啟用「<strong>⚡ 全員完成投票即刻自動翻牌 (<code>autoRevealWhenAllVoted = 1</code>)</strong>」。只要本回合所有受邀必投參與者皆完成出牌，資料庫預存程序 <code>storyPoint.spCastVote</code> 會立即自動將回合與議程狀態切換為 <code>revealed</code>（已翻牌揭曉），並透過 SignalR 與前端 2 秒輪詢讓所有人的畫面自動同步翻開撲克牌！若有人臨時缺席，主持人亦可隨時點擊控制台「<strong>👀 1. 立即翻牌揭曉</strong>」提前翻牌。",
                Keywords = "自動翻牌 翻牌揭曉 全員投完 SignalR revealed"
            },
            new
            {
                Q = "Q4. 撲克牌面上的「?」與「☕」以及各數字代表什麼意思？",
                A = "每張撲克牌下方皆直接標示繁體中文意義：<code>?</code> 代表「<strong>需求不明 / 需討論</strong>」（資訊不足無法估算，建議補充說明後重投）；<code>☕</code> 代表「<strong>暫停休息 / 稍後議</strong>」；數值牌面則代表相對複雜度：<code>0 (無需工時)</code>、<code>0.5 (極小微調)</code>、<code>1 (極簡變更)</code>、<code>2 (小型工項)</code>、<code>3 (標準一般)</code>、<code>5 (中等複雜)</code>、<code>8 (高複雜度)</code>、<code>13 (大型跨模組)</code>、<code>20/21 (極大工項)</code>、<code>40 (巨型需拆解)</code>、<code>100 (史詩級拆解)</code>。",
                Keywords = "撲克牌 牌面意義 疑問牌 ? 休息牌 ☕ 費氏數列 點數意義"
            },
            new
            {
                Q = "Q5. 如何在不捲動網頁的情況下快速修改表格中的某筆工項、員工或部門資料？",
                A = "在全系統任何資料表格 (<code>&lt;table&gt;</code>) 的任一列上，直接「<strong>雙擊滑鼠左鍵 (Double Click)</strong>」、或按「<strong>滑鼠右鍵 (Right Click)</strong>」點選選單中的「<code>✏️ 單筆編輯...（彈出 Modal 視窗）</code>」（亦可單擊選取列後按鍵盤 <code>Enter</code> 或 <code>F2</code>），系統會直接原地彈出置中的 Modal 對話框進行編輯與儲存，絕不會將網頁捲動到最上方。",
                Keywords = "滑鼠右鍵 雙擊 Modal 視窗 不捲動 單筆編輯 表格修改 Enter F2"
            },
            new
            {
                Q = "Q6. 工項的「起訖時間快速設定」怎麼用？到期後系統會自動做什麼？",
                A = "新增或單筆編輯工項時，設定好「開始時間（起時）」後，在「<strong>起訖時間快速設定</strong>」輸入<strong>數量</strong>（預設 1）並選擇<strong>單位（天 / 周 / 月 / 季 / 年）</strong>，系統會自動計算並填入「結束時間（迄時）」。當系統時間到達或超過工項的迄時 (<code>endDate</code>)，系統會自動將工項轉為「<strong>已完成 (已到期扣點)</strong>」、將該工項與負責員工/部門身上的有效 Story Point 扣減歸零（完整保留原點數與扣點紀錄），並自動寄發 Email 通知該部門所有<strong>部門主管</strong>與 <strong>HR 人資主管</strong>。",
                Keywords = "起訖時間 快速設定 天 周 月 季 年 到期 自動扣點 Email 寄信 部門主管 HR"
            },
            new
            {
                Q = "Q7. 如何鎖定（凍結）表格的特定欄位或排序表格資料？",
                A = "點擊任何表格表頭 (<code>&lt;th&gt;</code>) 的標題文字或 <code>⇅</code> 圖示，即可切換升冪 (<code>▲</code>) 或降冪 (<code>▼</code>) 排序；點擊表頭右側的圖釘按鈕 <code>📌</code> 將其切換為 <code>🔒</code>，該欄位（含表頭與所有資料列）就會凍結固定在表格最左側，水平捲動時不會移動。可同時鎖定多個欄位，或點擊表格上方工具列「<code>🔄 重設排序與解鎖</code>」一鍵恢復。",
                Keywords = "表頭排序 欄位鎖定 凍結 圖釘 📌 🔒 Sticky Header 水平捲動"
            },
            new
            {
                Q = "Q8. 每個員工都可以自己修改登入帳號與密碼嗎？",
                A = "是的！不論是管理者或一般參與者，登入後皆可隨時點擊畫面右上角或主選單的「<strong>🔑 修改我的帳密</strong>」，修改自己本人的登入帳號 (<code>loginAccount</code>) 與登入密碼。儲存後系統會自動更新資料庫與當前瀏覽器登入 Cookie，無須重新登入即可繼續使用。",
                Keywords = "修改我的帳密 修改密碼 修改登入帳號 個人密碼"
            }
        };

        // 1. 頂部搜尋英雄橫幅 (Hero Search Banner)
        sb.Append($@"
        <div class='help-hero-banner'>
            <div style='display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:12px;'>
                <div>
                    <h2 style='margin:0;font-size:21px;font-weight:800;display:flex;align-items:center;gap:10px;'>
                        <span>📖 系統線上說明與操作畫面搜尋手冊</span>
                        <span class='badge' style='background:#0d9488;color:#fff;font-size:12px;'>支援全文關鍵字即時檢索 + 12 幅實機操作畫面詳解</span>
                    </h2>
                    <div style='font-size:13px;color:#cbd5e1;margin-top:5px;'>
                        輸入任何功能關鍵字（如：<strong>盲投</strong>、<strong>待參與</strong>、<strong>自動翻牌</strong>、<strong>滑鼠右鍵</strong>、<strong>Modal</strong>、<strong>到期扣點</strong>、<strong>部門主管</strong>、<strong>排序鎖定</strong>、<strong>修改帳密</strong>），系統將即時過濾操作畫面與步驟並以黃底高亮標示關鍵字。
                    </div>
                </div>
                <div style='display:flex;gap:8px;flex-wrap:wrap;'>
                    {(isStandalonePage
                        ? "<a href='/?tab=voting' class='btn btn-outline' style='background:rgba(255,255,255,0.15);color:#fff;border-color:rgba(255,255,255,0.35);font-size:12.5px;padding:7px 12px;'>🃏 返回系統主畫面</a>"
                        : "<a href='/help' target='_blank' class='btn btn-outline' style='background:rgba(255,255,255,0.15);color:#fff;border-color:rgba(255,255,255,0.35);font-size:12.5px;padding:7px 12px;'>🗗 以獨立視窗開啟線上說明 (/help)</a>")}
                </div>
            </div>

            <!-- 搜尋輸入框與分類篩選列 -->
            <div class='help-search-grid'>
                <div class='help-search-input-wrap'>
                    <span style='position:absolute;left:12px;font-size:17px;pointer-events:none;'>🔍</span>
                    <input type='text' id='helpSearchInput' class='help-search-input' value='{safeInitQ}' placeholder='請輸入關鍵字搜尋說明與操作畫面（支援多關鍵字空白分隔，例如：盲投 自動翻牌 / 右鍵 Modal / 到期 寄信）...' oninput='filterOnlineHelpTopics()' autocomplete='off' />
                </div>
                <div>
                    <select id='helpCategorySelect' onchange='filterOnlineHelpTopics()' title='依功能模組分類篩選線上說明'>
                        <option value=''>📂 全部章節分類 (13 主題 + FAQ)</option>
                        <option value='concept' {(safeInitCat == "concept" ? "selected" : "")}>💡 Story Point 核心概念、複雜度評估與工作量透明 (1)</option>
                        <option value='auth' {(safeInitCat == "auth" ? "selected" : "")}>🔐 登入驗證、登出與個人帳密 (2)</option>
                        <option value='voting' {(safeInitCat == "voting" ? "selected" : "")}>🃏 多人即時盲投、獨立網頁與回合設定 (4)</option>
                        <option value='workitem' {(safeInitCat == "workitem" ? "selected" : "")}>📋 工項管理、代表類型與到期扣點寄信 (2)</option>
                        <option value='table' {(safeInitCat == "table" ? "selected" : "")}>🖱️ 表格右鍵 Modal、表頭排序鎖定與統計報表 (2)</option>
                        <option value='org' {(safeInitCat == "org" ? "selected" : "")}>👥 組織部門主管階層、批次匯入與系統 Log (2)</option>
                        <option value='faq' {(safeInitCat == "faq" ? "selected" : "")}>❓ 常見問題快速解答 FAQ (10)</option>
                    </select>
                </div>
                <div>
                    <select id='helpRoleSelect' onchange='filterOnlineHelpTopics()' title='依適用角色篩選線上說明'>
                        <option value=''>👥 全部角色適用說明</option>
                        <option value='all'>🙋 全體員工 (參與者 + 管理者) 必讀</option>
                        <option value='admin'>👑 管理者 (Admin) 專屬主持與管理</option>
                    </select>
                </div>
                <div>
                    <button type='button' class='btn btn-outline' style='background:#f8fafc;color:#0f172a;font-weight:800;padding:10px 14px;white-space:nowrap;' onclick='resetOnlineHelpFilter()'>🔄 清除搜尋條件</button>
                </div>
            </div>

            <!-- 熱門搜尋關鍵字快速標籤 -->
            <div style='display:flex;align-items:center;gap:7px;flex-wrap:wrap;margin-top:12px;'>
                <span style='font-size:12px;font-weight:800;color:#93c5fd;'>🔥 熱門搜尋關鍵字：</span>
                <span class='help-quick-tag' style='background:#f59e0b;color:#0f172a;border-color:#fde68a;' onclick=""applyHelpQuickKeyword('Story Point 概念 用途')"">💡 Story Point 概念與用途</span>
                <span class='help-quick-tag' style='background:#f59e0b;color:#0f172a;border-color:#fde68a;' onclick=""applyHelpQuickKeyword('評估複雜度 費氏數列')"">📐 如何評估複雜度</span>
                <span class='help-quick-tag' style='background:#f59e0b;color:#0f172a;border-color:#fde68a;' onclick=""applyHelpQuickKeyword('工作量透明 進行中點數')"">📊 團隊與個人工作量透明</span>
                <span class='help-quick-tag' onclick=""applyHelpQuickKeyword('待參與 參與中')"">📂 待參與 vs 參與中</span>
                <span class='help-quick-tag' onclick=""applyHelpQuickKeyword('自動翻牌')"">⚡ 全員投完自動翻牌</span>
                <span class='help-quick-tag' onclick=""applyHelpQuickKeyword('撲克牌 疑問牌')"">🃏 撲克牌面中文意義 (? / ☕)</span>
                <span class='help-quick-tag' onclick=""applyHelpQuickKeyword('滑鼠右鍵 Modal')"">🖱️ 滑鼠右鍵與雙擊彈出 Modal</span>
                <span class='help-quick-tag' onclick=""applyHelpQuickKeyword('起訖時間 到期自動扣點')"">⏰ 起訖時間與到期扣點寄信</span>
                <span class='help-quick-tag' onclick=""applyHelpQuickKeyword('部門主管 上一層主管')"">🌳 多位部門主管與上一層主管</span>
                <span class='help-quick-tag' onclick=""applyHelpQuickKeyword('表頭排序 欄位鎖定')"">📌 表頭排序與凍結鎖定</span>
                <span class='help-quick-tag' onclick=""applyHelpQuickKeyword('修改帳密')"">🔑 修改個人登入帳號與密碼</span>
                <span class='help-quick-tag' onclick=""applyHelpQuickKeyword('批次匯入 互動 HTML')"">📊 CSV/Excel 匯入與互動 HTML 報表</span>
                <span class='help-quick-tag' onclick=""applyHelpQuickKeyword('防崩潰 系統 Log')"">🛡️ 防崩潰保護與系統 Log</span>
                <span class='help-quick-tag' onclick=""applyHelpQuickKeyword('登出系統')"">🚪 全站安全登出</span>
            </div>
        </div>");

        // 2. 搜尋狀態提示橫幅
        sb.Append(@"
        <div id='helpSearchStatusBar' style='background:#eff6ff;border:1.5px solid #93c5fd;border-radius:10px;padding:10px 16px;margin-bottom:16px;display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:8px;font-size:13.5px;font-weight:700;color:#1e3a8a;'>
            <span id='helpSearchStatusText'>📊 目前顯示全部 <strong>13</strong> 個核心說明與操作主題（含 Story Point 概念與複雜度評估 + 12 幅實機操作畫面）及 <strong>10</strong> 則常見問題 FAQ。點擊任一操作畫面圖片可放大全螢幕檢視！</span>
            <span style='font-size:12px;color:#475569;'>💡 提示：點擊操作畫面圖片可開啟高解析度放大檢視視窗 (Lightbox)</span>
        </div>");

        // 3. 左右雙欄佈局：左側章節目錄樹 (TOC) + 右側圖文操作畫面卡片
        sb.Append(@"<div class='help-layout-grid'>");

        // 左側目錄側邊欄
        sb.Append(@"
            <div class='help-toc-sidebar'>
                <div style='font-size:13.5px;font-weight:800;color:#0f172a;padding-bottom:8px;margin-bottom:8px;border-bottom:2px solid #e2e8f0;display:flex;align-items:center;justify-content:space-between;'>
                    <span>📑 線上說明章節目錄</span>
                    <span id='helpTocMatchCountBadge' class='badge badge-user' style='font-size:11px;'>13 主題</span>
                </div>");

        foreach (var t in topics)
        {
            sb.Append($@"
                <a class='help-toc-item' id='toc-{t.Id}' data-target-topic='{t.Id}' onclick=""jumpToHelpTopic('{t.Id}')"">
                    <span style='overflow:hidden;text-overflow:ellipsis;white-space:nowrap;'><strong>{t.Num}.</strong> {h(t.Title)}</span>
                    <span class='toc-match-dot' style='font-size:11px;color:#2563eb;flex-shrink:0;'>▶</span>
                </a>");
        }

        sb.Append(@"
                <div style='margin-top:8px;padding-top:8px;border-top:1px dashed #cbd5e1;'>
                    <a class='help-toc-item' id='toc-help-faq-section' onclick=""jumpToHelpTopic('help-faq-section')"">
                        <span>❓ <strong>常見問題快速解答 (FAQ)</strong></span>
                        <span style='font-size:11px;color:#2563eb;'>▶</span>
                    </a>
                </div>
            </div>");

        // 右側主內容區
        sb.Append(@"<div id='helpMainContentContainer'>");

        // 無符合搜尋結果時的友善提示框
        sb.Append(@"
            <div id='helpNoResultBox' style='display:none;background:#ffffff;border:2px dashed #93c5fd;border-radius:14px;padding:32px 24px;text-align:center;margin-bottom:20px;'>
                <div style='font-size:36px;margin-bottom:8px;'>🔍</div>
                <h3 style='margin:0 0 8px 0;color:#1e3a8a;'>找不到符合目前搜尋關鍵字的說明主題</h3>
                <p style='color:#475569;font-size:13.5px;margin:0 0 16px 0;'>請嘗試簡化關鍵字，或點選下方熱門主題關鍵字快速查看對應的操作畫面：</p>
                <div style='display:flex;justify-content:center;gap:8px;flex-wrap:wrap;'>
                    <button type='button' class='btn btn-primary' onclick='resetOnlineHelpFilter()'>🔄 顯示全部 12 個操作畫面主題</button>
                    <button type='button' class='btn btn-outline' onclick=""applyHelpQuickKeyword('盲投')"">🃏 搜尋「盲投」</button>
                    <button type='button' class='btn btn-outline' onclick=""applyHelpQuickKeyword('右鍵 Modal')"">🖱️ 搜尋「右鍵 Modal」</button>
                    <button type='button' class='btn btn-outline' onclick=""applyHelpQuickKeyword('工項 到期')"">⏰ 搜尋「工項 到期」</button>
                </div>
            </div>");

        // 逐一渲染 12 大操作畫面主題卡片
        foreach (var t in topics)
        {
            string stepHtml = "";
            for (int i = 0; i < t.Steps.Length; i++)
            {
                stepHtml += $@"
                    <div class='help-step-item'>
                        <span class='help-step-num'>{i + 1}</span>
                        <div class='help-searchable-text'>{t.Steps[i]}</div>
                    </div>";
            }

            string calloutHtml = "";
            foreach (string c in t.Callouts)
            {
                string pinNum = c.Length > 0 ? c[..1] : "•";
                string pinText = c.Length > 1 ? c[1..].Trim() : c;
                calloutHtml += $@"
                    <div class='help-callout-chip'>
                        <span class='help-callout-pin'>{h(pinNum)}</span>
                        <span class='help-searchable-text'>{h(pinText)}</span>
                    </div>";
            }

            string searchAttr = $"{t.Num} {t.CategoryName} {t.Title} {t.Keywords} {t.Summary} {string.Join(" ", t.Steps)} {string.Join(" ", t.Callouts)}".ToLowerInvariant();

            sb.Append($@"
            <div class='help-topic-card' id='{t.Id}' data-category='{t.Category}' data-role='{t.RoleScope}' data-search-blob='{h(searchAttr)}'>
                <div class='help-topic-header'>
                    <div style='display:flex;align-items:center;gap:8px;flex-wrap:wrap;'>
                        <span class='badge' style='background:#1e3a8a;color:#fff;font-size:12.5px;padding:4px 10px;'>主題 {t.Num}</span>
                        <span class='badge' style='background:#f1f5f9;color:#334155;border:1px solid #cbd5e1;'>{t.CategoryName}</span>
                        {t.RoleBadge}
                        <h3 class='help-searchable-text' style='margin:0;font-size:17px;font-weight:800;color:#0f172a;'>{h(t.Title)}</h3>
                    </div>
                    <div>
                        {t.ActionHtml}
                    </div>
                </div>

                <div class='help-topic-body-grid'>
                    <!-- 左側：功能重點與步驟詳解 -->
                    <div>
                        <div class='help-searchable-text' style='font-size:13.5px;color:#334155;line-height:1.6;background:#eff6ff;border:1px solid #bfdbfe;border-radius:9px;padding:10px 13px;margin-bottom:12px;'>
                            💡 <strong>功能核心說明：</strong>{h(t.Summary)}
                        </div>
                        <div style='font-size:13px;font-weight:800;color:#1e3a8a;margin-bottom:6px;'>📋 操作步驟詳解（對照右側實機操作畫面編號 ①～④）：</div>
                        <div class='help-step-list'>
                            {stepHtml}
                        </div>
                        <div style='margin-top:10px;font-size:12px;color:#64748b;display:flex;align-items:center;gap:6px;flex-wrap:wrap;'>
                            <span>🏷️ <strong>搜尋關鍵字標籤：</strong></span>
                            <code class='help-searchable-text' style='background:#f1f5f9;padding:2px 7px;border-radius:5px;color:#475569;'>{h(t.Keywords)}</code>
                        </div>
                    </div>

                    <!-- 右側：搭配實機操作畫面與標註指引 -->
                    <div>
                        <div class='help-browser-frame'>
                            <div class='help-browser-bar'>
                                <div class='help-browser-dots'>
                                    <span class='help-browser-dot' style='background:#ef4444;'></span>
                                    <span class='help-browser-dot' style='background:#f59e0b;'></span>
                                    <span class='help-browser-dot' style='background:#22c55e;'></span>
                                    <span style='margin-left:4px;font-weight:700;color:#e2e8f0;'>🖥️ 實機操作畫面</span>
                                </div>
                                <div class='help-browser-url'>{h(t.RouteUrl)}</div>
                                <button type='button' class='btn btn-outline' style='padding:2px 8px;font-size:11px;background:#2563eb;color:#fff;border-color:#60a5fa;' onclick=""openHelpScreenLightbox('{t.ImageFile}', '主題 {t.Num}：{h(t.Title)}')"">🔍 放大畫面</button>
                            </div>
                            <div class='help-screenshot-box' onclick=""openHelpScreenLightbox('{t.ImageFile}', '主題 {t.Num}：{h(t.Title)}')"">
                                <img src='{t.ImageFile}' alt='主題 {t.Num} 實機操作畫面 - {h(t.Title)}' loading='lazy' onerror=""this.style.display='none';this.nextElementSibling.style.display='flex';"" />
                                <div style='display:none;min-height:220px;align-items:center;justify-content:center;flex-direction:column;gap:8px;padding:24px;color:#cbd5e1;text-align:center;'>
                                    <div style='font-size:28px;'>🖥️</div>
                                    <div style='font-weight:700;'>主題 {t.Num} 實機操作畫面預覽 ({h(t.Title)})</div>
                                </div>
                                <div class='help-screenshot-zoom-hint'>🔍 點擊放大檢視實機操作畫面</div>
                            </div>
                            <div class='help-callout-legend'>
                                {calloutHtml}
                            </div>
                            <div class='help-ui-diagram'>
                                {t.UiDiagramHtml}
                            </div>
                        </div>
                    </div>
                </div>
            </div>");
        }

        // 4. 常見問題快速解答 (FAQ) 區塊
        sb.Append(@"
            <div class='help-topic-card' id='help-faq-section' data-category='faq' data-role='all' data-search-blob='常見問題 faq 問答 權限 盲投 待參與 參與中 自動翻牌 右鍵 modal 到期扣點 排序 鎖定 修改帳密'>
                <div class='help-topic-header'>
                    <div style='display:flex;align-items:center;gap:8px;'>
                        <span class='badge' style='background:#0d9488;color:#fff;font-size:12.5px;padding:4px 10px;'>常見問題 FAQ</span>
                        <h3 style='margin:0;font-size:17px;font-weight:800;color:#0f172a;'>❓ 常見問題與排解快速解答（支援關鍵字即時檢索）</h3>
                    </div>
                </div>");

        foreach (var faq in faqs)
        {
            string faqBlob = $"{faq.Q} {faq.A} {faq.Keywords}".ToLowerInvariant();
            sb.Append($@"
                <div class='help-faq-item' data-faq-blob='{h(faqBlob)}'>
                    <div class='help-faq-q help-searchable-text'>{faq.Q}</div>
                    <div class='help-faq-a help-searchable-text'>{faq.A}</div>
                </div>");
        }

        sb.Append(@"
            </div>
        </div>
        </div>"); // end help-layout-grid

        // 5. 實機操作畫面全螢幕放大檢視 Lightbox Modal
        sb.Append(@"
        <div id='spHelpScreenLightbox' class='sp-modal-overlay' style='display:none;position:fixed;inset:0;z-index:100020;background:rgba(15,23,42,0.82);backdrop-filter:blur(4px);align-items:center;justify-content:center;padding:20px;' onclick='if(event.target===this) closeHelpScreenLightbox();'>
            <div style='background:#0f172a;border:2px solid #60a5fa;border-radius:14px;max-width:1360px;width:96vw;max-height:92vh;display:flex;flex-direction:column;overflow:hidden;box-shadow:0 25px 60px rgba(0,0,0,0.65);'>
                <div style='display:flex;align-items:center;justify-content:space-between;padding:12px 18px;background:#1e293b;border-bottom:1px solid #334155;color:#fff;'>
                    <div id='spHelpLightboxTitle' style='font-size:15.5px;font-weight:800;color:#93c5fd;'>🖥️ 實機操作畫面放大檢視</div>
                    <div style='display:flex;gap:8px;align-items:center;'>
                        <button type='button' class='btn btn-outline' style='padding:5px 12px;font-size:12px;background:#334155;color:#fff;border-color:#64748b;' onclick='stepHelpLightbox(-1)'>◀ 上一張操作畫面</button>
                        <button type='button' class='btn btn-outline' style='padding:5px 12px;font-size:12px;background:#334155;color:#fff;border-color:#64748b;' onclick='stepHelpLightbox(1)'>下一張操作畫面 ▶</button>
                        <button type='button' class='btn btn-danger' style='padding:5px 12px;font-size:12px;' onclick='closeHelpScreenLightbox()'>✖ 關閉放大檢視 (Esc)</button>
                    </div>
                </div>
                <div style='padding:14px;overflow:auto;flex:1;text-align:center;background:#0f172a;'>
                    <img id='spHelpLightboxImg' src='' alt='實機操作畫面放大預覽' style='max-width:100%;height:auto;border-radius:8px;border:1px solid #475569;box-shadow:0 12px 32px rgba(0,0,0,0.5);' />
                </div>
            </div>
        </div>");

        // 6. 線上說明搜尋、關鍵字高亮標示與操作畫面放大互動腳本
        sb.Append($@"
        <script>
            const helpScreensList = [
                {{ src: '/help/images/screen_01_login.png', title: '主題 01：員工帳號密碼強制登入驗證與全系統安全登出' }},
                {{ src: '/help/images/screen_11_my_account.png', title: '主題 02：自助修改個人登入帳號與密碼（免重新登入即時生效）' }},
                {{ src: '/help/images/screen_02_voting_lobby.png', title: '主題 03：多人即時盲投大廳：「📂 待參與」與「🟢 參與中」分類与進入獨立網頁' }},
                {{ src: '/help/images/screen_03_voting_standalone.png', title: '主題 04：獨立盲投 Session 網頁：撲克牌中文意義、全員投完自動翻牌與拍板定案' }},
                {{ src: '/help/images/screen_04_round_setup.png', title: '主題 05：設定盲投回合（投什麼標的、高低點數上下限、誰可參與、共識演算法）' }},
                {{ src: '/help/images/screen_05_history.png', title: '主題 06：事後查詢訂出 Story Point 的完整過程與點數異動稽核歷程' }},
                {{ src: '/help/images/screen_06_workitems.png', title: '主題 07：工項管理、選擇代表類型自動帶點、起訖時間快速設定與到期自動扣點寄信' }},
                {{ src: '/help/images/screen_07_context_modal.png', title: '主題 08：全系統表格即時篩選、純滑鼠右鍵選單與雙擊彈出置中單筆編輯 Modal' }},
                {{ src: '/help/images/screen_08_types.png', title: '主題 09：工項代表類型標準點數管理與工項類型 CRUD' }},
                {{ src: '/help/images/screen_09_stats_table.png', title: '主題 10：員工與部門總點數統計、表頭排序與凍結鎖定、CSV/Excel/互動 HTML 匯出' }},
                {{ src: '/help/images/screen_10_employees_org.png', title: '主題 11：組織與員工帳密管理：多位部門主管、上一層主管樹狀階層與批次匯入' }},
                {{ src: '/help/images/screen_12_logs.png', title: '主題 12：系統防崩潰保護、錯誤 Log 查詢中心與 PowerShell 多 Tab GUI 工具' }}
            ];
            let currentHelpLightboxIdx = 0;

            function openHelpScreenLightbox(imgSrc, titleText) {{
                const box = document.getElementById('spHelpScreenLightbox');
                const img = document.getElementById('spHelpLightboxImg');
                const titleEl = document.getElementById('spHelpLightboxTitle');
                if (!box || !img) return;
                const foundIdx = helpScreensList.findIndex(x => x.src === imgSrc);
                if (foundIdx >= 0) currentHelpLightboxIdx = foundIdx;
                img.src = imgSrc;
                if (titleEl) titleEl.textContent = '🖥️ ' + (titleText || '實機操作畫面放大檢視');
                box.style.display = 'flex';
                box.classList.add('open');
            }}

            function closeHelpScreenLightbox() {{
                const box = document.getElementById('spHelpScreenLightbox');
                if (!box) return;
                box.style.display = 'none';
                box.classList.remove('open');
            }}

            function stepHelpLightbox(delta) {{
                if (!helpScreensList.length) return;
                currentHelpLightboxIdx = (currentHelpLightboxIdx + delta + helpScreensList.length) % helpScreensList.length;
                const item = helpScreensList[currentHelpLightboxIdx];
                openHelpScreenLightbox(item.src, item.title);
            }}

            function clearHelpHighlights(rootEl) {{
                if (!rootEl) return;
                rootEl.querySelectorAll('.help-searchable-text').forEach(el => {{
                    if (el.dataset.origHtml !== undefined) {{
                        el.innerHTML = el.dataset.origHtml;
                    }} else {{
                        el.dataset.origHtml = el.innerHTML;
                    }}
                }});
            }}

            function highlightHelpKeywordsInElement(el, tokens) {{
                if (!el || !tokens || !tokens.length) return;
                if (el.dataset.origHtml === undefined) {{
                    el.dataset.origHtml = el.innerHTML;
                }}
                let html = el.dataset.origHtml;
                tokens.forEach(tok => {{
                    if (!tok) return;
                    const escaped = tok.replace(/[.*+?^${{}}()|[\]\\]/g, '\\$&');
                    // 僅替換 HTML 標籤外的純文字區段
                    const reg = new RegExp('(' + escaped + ')(?![^<]*>)', 'gi');
                    html = html.replace(reg, '<mark class=""help-highlight"">$1</mark>');
                }});
                el.innerHTML = html;
            }}

            function filterOnlineHelpTopics() {{
                const kwInput = document.getElementById('helpSearchInput');
                const catSelect = document.getElementById('helpCategorySelect');
                const roleSelect = document.getElementById('helpRoleSelect');
                const rawQuery = (kwInput ? kwInput.value : '').trim();
                const lowerQuery = rawQuery.toLowerCase();
                const tokens = lowerQuery ? lowerQuery.split(/\s+/).filter(Boolean) : [];
                const catVal = catSelect ? catSelect.value : '';
                const roleVal = roleSelect ? roleSelect.value : '';

                const container = document.getElementById('helpMainContentContainer');
                clearHelpHighlights(container);

                let matchedTopicCount = 0;
                let matchedFaqCount = 0;

                document.querySelectorAll('.help-topic-card').forEach(card => {{
                    const isFaqCard = card.id === 'help-faq-section';
                    const cardCat = card.getAttribute('data-category') || '';
                    const cardRole = card.getAttribute('data-role') || 'all';
                    const blob = (card.getAttribute('data-search-blob') || '').toLowerCase();

                    const catMatch = !catVal || cardCat === catVal;
                    const roleMatch = !roleVal || cardRole === roleVal || (roleVal === 'admin' && cardRole === 'all');

                    if (isFaqCard) {{
                        let anyFaqVisible = false;
                        card.querySelectorAll('.help-faq-item').forEach(faqEl => {{
                            const faqBlob = (faqEl.getAttribute('data-faq-blob') || '').toLowerCase();
                            const kwMatch = tokens.length === 0 || tokens.every(t => faqBlob.indexOf(t) >= 0);
                            const showFaq = catMatch && kwMatch;
                            faqEl.style.display = showFaq ? '' : 'none';
                            if (showFaq) {{
                                anyFaqVisible = true;
                                matchedFaqCount++;
                                if (tokens.length > 0) {{
                                    faqEl.querySelectorAll('.help-searchable-text').forEach(st => highlightHelpKeywordsInElement(st, tokens));
                                }}
                            }}
                        }});
                        card.style.display = anyFaqVisible ? '' : 'none';
                        return;
                    }}

                    const kwMatch = tokens.length === 0 || tokens.every(t => blob.indexOf(t) >= 0);
                    const visible = catMatch && roleMatch && kwMatch;
                    card.style.display = visible ? '' : 'none';

                    const tocItem = document.getElementById('toc-' + card.id);
                    if (tocItem) {{
                        tocItem.classList.toggle('toc-filtered-out', !visible);
                    }}

                    if (visible) {{
                        matchedTopicCount++;
                        if (tokens.length > 0) {{
                            card.querySelectorAll('.help-searchable-text').forEach(st => highlightHelpKeywordsInElement(st, tokens));
                        }}
                    }}
                }});

                const noResultBox = document.getElementById('helpNoResultBox');
                if (noResultBox) {{
                    noResultBox.style.display = (matchedTopicCount === 0 && matchedFaqCount === 0) ? 'block' : 'none';
                }}

                const tocBadge = document.getElementById('helpTocMatchCountBadge');
                if (tocBadge) {{
                    tocBadge.textContent = matchedTopicCount + ' 主題';
                }}

                const statusText = document.getElementById('helpSearchStatusText');
                if (statusText) {{
                    if (rawQuery || catVal || roleVal) {{
                        statusText.innerHTML = '🔍 搜尋結果：共找到 <strong>' + matchedTopicCount + '</strong> 個相符的操作畫面主題與 <strong>' + matchedFaqCount + '</strong> 則常見問題' +
                            (rawQuery ? '（關鍵字「<mark class=""help-highlight"">' + rawQuery.replace(/</g, '&lt;') + '</mark>」已於步驟與畫面圖解中高亮標示）' : '');
                    }} else {{
                        statusText.innerHTML = '📊 目前顯示全部 <strong>12</strong> 個核心操作主題（皆搭配實機操作畫面與步驟標註）及 <strong>8</strong> 則常見問題 FAQ。點擊任一操作畫面圖片可放大全螢幕檢視！';
                    }}
                }}

                document.querySelectorAll('.help-quick-tag').forEach(tag => {{
                    tag.classList.toggle('active-tag', rawQuery && tag.textContent.indexOf(rawQuery) >= 0);
                }});
            }}

            function applyHelpQuickKeyword(kw) {{
                const kwInput = document.getElementById('helpSearchInput');
                const catSelect = document.getElementById('helpCategorySelect');
                if (catSelect) catSelect.value = '';
                if (kwInput) {{
                    kwInput.value = kw;
                    filterOnlineHelpTopics();
                    kwInput.focus();
                }}
            }}

            function resetOnlineHelpFilter() {{
                const kwInput = document.getElementById('helpSearchInput');
                const catSelect = document.getElementById('helpCategorySelect');
                const roleSelect = document.getElementById('helpRoleSelect');
                if (kwInput) kwInput.value = '';
                if (catSelect) catSelect.value = '';
                if (roleSelect) roleSelect.value = '';
                filterOnlineHelpTopics();
            }}

            function jumpToHelpTopic(topicId) {{
                const target = document.getElementById(topicId);
                if (!target) return;
                if (target.style.display === 'none') {{
                    resetOnlineHelpFilter();
                }}
                document.querySelectorAll('.help-toc-item').forEach(el => el.classList.remove('active-toc'));
                const tocEl = document.getElementById('toc-' + topicId);
                if (tocEl) tocEl.classList.add('active-toc');
                target.scrollIntoView({{ behavior: 'smooth', block: 'start' }});
                target.classList.remove('sp-form-flash');
                void target.offsetWidth;
                target.classList.add('sp-form-flash');
            }}

            document.addEventListener('keydown', function (e) {{
                if (e.key === 'Escape') {{
                    closeHelpScreenLightbox();
                }}
            }});

            (function initOnlineHelpOnLoad() {{
                const initQ = '{safeInitQ}';
                const initCat = '{safeInitCat}';
                const initTopic = '{safeInitTopic}';
                if (initQ || initCat) {{
                    filterOnlineHelpTopics();
                }}
                if (initTopic) {{
                    setTimeout(function () {{ jumpToHelpTopic(initTopic); }}, 120);
                }}
            }})();
        </script>");

        return sb.ToString();
    }
}
