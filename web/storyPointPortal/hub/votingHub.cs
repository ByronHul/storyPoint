/*
================================================================================
檔案名稱: votingHub.cs
用途說明: Azure 多人參與 Story Point 系統之 SignalR 即時雙向同步 Hub。
          當有參與者投出撲克牌 (spCastVote)、或管理者執行翻牌揭曉 (reveal)、
          開啟下一回合 (nextRound)、或拍板核定最終點數 (finalize) 時，
          即時推播通知所有線上客户端同步更新畫面。
建立者  : Antigravity Senior Full-Stack Engineer
建立日期: 2026-10-23
================================================================================
*/

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace storyPointPortal.hub;

/// <summary>
/// 多人即時 Planning Poker 投票同步 Hub（強制要求完成帳號密碼登入授權後方可連線）
/// </summary>
[Authorize]
public class votingHub : Hub
{
    /// <summary>
    /// 加入指定投票議程群組
    /// </summary>
    /// <param name="votingSessionPk">投票議程主鍵</param>
    public async Task joinSessionGroup(int votingSessionPk)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"session_{votingSessionPk}");
    }

    /// <summary>
    /// 廣播投票狀態更新事件
    /// </summary>
    /// <param name="votingSessionPk">投票議程主鍵</param>
    /// <param name="eventName">事件名稱</param>
    /// <param name="messageText">繁體中文提示訊息</param>
    public async Task notifySessionChanged(int votingSessionPk, string eventName, string messageText)
    {
        await Clients.All.SendAsync("onVotingSessionUpdated", new
        {
            votingSessionPk = votingSessionPk,
            eventName = eventName,
            messageText = messageText,
            timestamp = DateTime.Now.ToString("HH:mm:ss")
        });
    }
}
