/*
================================================================================
檔案名稱: program.cs
用途說明: Azure 多人參與 Story Point 估算與工項權重管理系統 (.NET 8 Web App) 啟動入口。
          配置：
          1. Cookie 身分驗證 (區分 admin 管理者與 participant 參與者角色)
          2. SignalR 即時雙向通訊 Hub (/votingHub) 支援多人盲投、同步翻牌與多回合估算
          3. 注入 sqlDataService (純 ADO.NET 參數化呼叫 SQL Server Stored Procedure)
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
================================================================================
*/

using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using storyPointPortal.hub;
using storyPointPortal.service;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddSingleton<sqlDataService>();
builder.Services.AddSingleton<emailNotificationService>();
builder.Services.AddHostedService<workItemExpiryBackgroundService>();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/login";
        options.Cookie.Name = "storyPointStrictAuthCookie";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(4);
        options.SlidingExpiration = true;
        options.Events = new CookieAuthenticationEvents
        {
            OnValidatePrincipal = async context =>
            {
                string? empPk = context.Principal?.FindFirstValue("employeePk");
                string? loginAcc = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

                // 嚴格檢核：必須具備有效員工主鍵與登入帳號，否則立即拒絕並強制導向 /login 帳號登入頁
                if (string.IsNullOrWhiteSpace(empPk) ||
                    !int.TryParse(empPk, out int parsedPk) ||
                    parsedPk <= 0 ||
                    string.IsNullOrWhiteSpace(loginAcc))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
            }
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// 全域防當機例外攔截與雙軌 Log 寫入中介層 (Global Exception Guard & Audit Logger)
// 確保任何未預期例外絕不導致網站產生 HTTP ERROR 500 空白當機畫面，並自動寫入 tbLog 供管理者查詢
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        var sqlSvc = context.RequestServices.GetService<sqlDataService>();
        string opAcc = context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        bool isAdmin = string.Equals(context.User?.FindFirstValue(ClaimTypes.Role), "admin", StringComparison.OrdinalIgnoreCase);
        string reqTarget = $"{context.Request.Method} {context.Request.Path}{context.Request.QueryString}";
        int errLine = sqlDataService.extractErrorLineFromStackTrace(ex);

        if (sqlSvc != null)
        {
            await sqlSvc.writeSystemLogAsync(
                logLevel: 1,
                sourceName: "webPortal.middleware",
                objectName: reqTarget.Length > 120 ? reqTarget[..120] : reqTarget,
                durationMillisecond: 0,
                executionResult: "全域防當機保護攔截未預期例外錯誤",
                errorCode: ex.HResult,
                errorLine: errLine,
                errorMessage: $"{ex.GetType().Name}: {ex.Message} | Stack: {ex.StackTrace}",
                operatorAccount: opAcc
            );
        }

        if (!context.Response.HasStarted)
        {
            string friendlyErr = $"系統已自動攔截例外錯誤並記錄至系統 Log（位置：{reqTarget}，行號：{errLine}）：{ex.Message}";
            bool isAuthenticated = context.User?.Identity?.IsAuthenticated == true;
            if (!isAuthenticated)
            {
                context.Response.Redirect("/login?err=" + Uri.EscapeDataString("請先輸入員工帳號與密碼登入系統。"));
                return;
            }
            if (string.Equals(context.Request.Method, "POST", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(context.Request.Path, "/", StringComparison.OrdinalIgnoreCase))
            {
                string targetTab = isAdmin ? "logs" : "voting";
                context.Response.Redirect($"/?tab={targetTab}&err=" + Uri.EscapeDataString(friendlyErr));
                return;
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            string safeMsg = WebUtility.HtmlEncode(ex.Message);
            string safeStack = WebUtility.HtmlEncode(ex.StackTrace ?? "");
            string html = $@"<!DOCTYPE html>
<html lang=""zh-Hant"">
<head>
    <meta charset=""utf-8"" />
    <title>系統防當機保護通知 - Azure Story Point 系統</title>
    <style>
        body {{ font-family:'Microsoft JhengHei UI',sans-serif; background:#f8fafc; color:#0f172a; padding:32px; }}
        .box {{ max-width:920px; margin:0 auto; background:#fff; border:2px solid #fca5a5; border-radius:14px; padding:26px; box-shadow:0 10px 25px rgba(0,0,0,0.06); }}
        .btn {{ display:inline-block; padding:10px 18px; border-radius:8px; background:#2563eb; color:#fff; text-decoration:none; font-weight:700; margin-right:10px; }}
        pre {{ background:#0f172a; color:#f8fafc; padding:14px; border-radius:8px; overflow-x:auto; font-size:12.5px; }}
    </style>
</head>
<body>
    <div class=""box"">
        <h2 style=""margin-top:0;color:#b91c1c;"">🛡️ 系統防當機保護已啟動（錯誤已自動寫入系統 Log）</h2>
        <p>系統在處理您的請求（<code>{WebUtility.HtmlEncode(reqTarget)}</code>）時偵測到例外狀況，已自動攔截錯誤並寫入 <code>storyPoint.tbLog</code> 與本機 Log 檔案，系統服務維持正常運作。</p>
        <p><strong>錯誤摘要：</strong>{safeMsg}（行號：{errLine}）</p>
        {(isAdmin ? $"<pre>{safeStack}</pre>" : "")}
        <div style=""margin-top:18px;"">
            <a class=""btn"" href=""/?tab=logs"">📜 前往管理者系統 Log 查詢中心</a>
            <a class=""btn"" style=""background:#475569;"" href=""/?tab=voting"">🃏 返回多人即時盲投大廳</a>
        </div>
    </div>
</body>
</html>";
            await context.Response.WriteAsync(html, Encoding.UTF8);
        }
    }
});

app.MapControllers();
app.MapHub<votingHub>("/votingHub");

app.Run();
