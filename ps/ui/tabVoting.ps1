<#
.SYNOPSIS
    Azure 多人參與 Story Point 系統 - [Tab 1] 多人即時盲投與投票過程查詢頁籤 (tabVoting.ps1)
.DESCRIPTION
    獨立實作「發起工項代表類型/工項投票、參與者投出撲克牌、管理者翻牌/下一回合/定案、以及查詢訂出 Story Point 過程」頁籤。
    所有功能一律呼叫 ps/manageVotingSession.ps1。
.PARAMETER psRootPath
    ps 模組根目錄路徑
.PARAMETER statusLabel
    主視窗底部狀態列控制項
.OUTPUTS
    System.Windows.Forms.TabPage
.NOTES
    檔案名稱: tabVoting.ps1
    建立者  : Antigravity Senior Full-Stack Engineer
    建立日期: 2026-10-23
#>
[cmdletbinding()]
param(
    [parameter(mandatory = $true)]
    [string]$psRootPath,
    [parameter(mandatory = $false)]
    $statusLabel = $null
)

process {
    $votingScript = join-path -path $psRootPath -childpath 'manageVotingSession.ps1'
    $tabPage = new-object system.windows.forms.tabpage
    $tabPage.text = '1. 多人盲投與訂定過程查詢'
    $tabPage.padding = new-object system.windows.forms.padding(10)

    $lblSession = new-object system.windows.forms.label
    $lblSession.text = '投票議程清單 (點選可檢視各回合出牌過程)：'
    $lblSession.location = new-object system.drawing.point(12, 12)
    $lblSession.autosize = $true

    $btnRefresh = new-object system.windows.forms.button
    $btnRefresh.text = '🔄 重新整理議程與投票過程'
    $btnRefresh.location = new-object system.drawing.point(320, 8)
    $btnRefresh.size = new-object system.drawing.size(200, 28)

    $gridSession = new-object system.windows.forms.datagridview
    $gridSession.location = new-object system.drawing.point(12, 42)
    $gridSession.size = new-object system.drawing.size(1130, 190)
    $gridSession.readonly = $true
    $gridSession.allowusertoaddrows = $false
    $gridSession.autosizecolumnsmode = 'Fill'
    $gridSession.selectionmode = 'FullRowSelect'
    enableGridHeaderSortAndLock -grid $gridSession -statusLabel $statusLabel

    $lblCast = new-object system.windows.forms.label
    $lblCast.text = '議程 ID：'
    $lblCast.location = new-object system.drawing.point(12, 246)
    $lblCast.autosize = $true

    $txtSessionPk = new-object system.windows.forms.textbox
    $txtSessionPk.location = new-object system.drawing.point(75, 243)
    $txtSessionPk.width = 55
    $txtSessionPk.text = '3'

    $lblEmp = new-object system.windows.forms.label
    $lblEmp.text = '員工 ID：'
    $lblEmp.location = new-object system.drawing.point(140, 246)
    $lblEmp.autosize = $true

    $txtEmpPk = new-object system.windows.forms.textbox
    $txtEmpPk.location = new-object system.drawing.point(205, 243)
    $txtEmpPk.width = 50
    $txtEmpPk.text = '1'

    $lblCard = new-object system.windows.forms.label
    $lblCard.text = '出牌點數：'
    $lblCard.location = new-object system.drawing.point(265, 246)
    $lblCard.autosize = $true

    $cmbCard = new-object system.windows.forms.combobox
    $cmbCard.location = new-object system.drawing.point(340, 243)
    $cmbCard.width = 140
    $cmbCard.dropdownstyle = 'DropDownList'
    $null = $cmbCard.items.addrange(@(
        '0 (無需工時)',
        '0.5 (極小微調)',
        '1 (極簡變更)',
        '2 (小型工項)',
        '3 (標準一般)',
        '5 (中等複雜)',
        '8 (高複雜度)',
        '13 (大型跨模組)',
        '21 (極大工項)',
        '? (需求不明需討論)',
        '☕ (暫停休息)'
    ))
    $cmbCard.selectedindex = 5

    $lblComment = new-object system.windows.forms.label
    $lblComment.text = '估算理由：'
    $lblComment.location = new-object system.drawing.point(490, 246)
    $lblComment.autosize = $true

    $txtComment = new-object system.windows.forms.textbox
    $txtComment.location = new-object system.drawing.point(565, 243)
    $txtComment.width = 145
    $txtComment.text = '依代表類型複雜度評估'

    $btnVote = new-object system.windows.forms.button
    $btnVote.text = '🃏 投出 Story Point'
    $btnVote.location = new-object system.drawing.point(720, 240)
    $btnVote.size = new-object system.drawing.size(140, 30)
    $btnVote.backcolor = [system.drawing.color]::fromargb(37, 99, 235)
    $btnVote.forecolor = [system.drawing.color]::white

    $btnReveal = new-object system.windows.forms.button
    $btnReveal.text = '👁️ 翻牌揭曉'
    $btnReveal.location = new-object system.drawing.point(870, 240)
    $btnReveal.size = new-object system.drawing.size(120, 30)

    $btnNextRound = new-object system.windows.forms.button
    $btnNextRound.text = '⏭️ 下一回合'
    $btnNextRound.location = new-object system.drawing.point(1000, 240)
    $btnNextRound.size = new-object system.drawing.size(120, 30)

    $lblHistory = new-object system.windows.forms.label
    $lblHistory.text = '訂出 Story Point 的各回合出牌與討論過程明細：'
    $lblHistory.location = new-object system.drawing.point(12, 285)
    $lblHistory.autosize = $true

    $gridProcess = new-object system.windows.forms.datagridview
    $gridProcess.location = new-object system.drawing.point(12, 310)
    $gridProcess.size = new-object system.drawing.size(1130, 310)
    $gridProcess.readonly = $true
    $gridProcess.allowusertoaddrows = $false
    $gridProcess.autosizecolumnsmode = 'Fill'
    enableGridHeaderSortAndLock -grid $gridProcess -statusLabel $statusLabel

    $loadVotingData = {
        $ds = & $votingScript -actionType 'getProcess'
        $gridSession.datasource = $ds.tables[0]
        $gridProcess.datasource = $ds.tables[2]
        if ($null -ne $statusLabel) {
            $statusLabel.text = "已載入 $($ds.tables[0].rows.count) 場投票議程與 $($ds.tables[2].rows.count) 筆出牌過程紀錄"
        }
    }

    $btnRefresh.add_click($loadVotingData)
    $btnVote.add_click({
        $rawSelected = $cmbCard.selecteditem.tostring()
        $cleanCardVal = ($rawSelected -split '\s+')[0].trim()
        $null = & $votingScript -actionType 'castVote' -votingSessionPk ([int]$txtSessionPk.text) -employeePk ([int]$txtEmpPk.text) -cardValue $cleanCardVal -voteComment $txtComment.text
        & $loadVotingData
    })
    $btnReveal.add_click({
        $null = & $votingScript -actionType 'revealRound' -votingSessionPk ([int]$txtSessionPk.text) -employeePk ([int]$txtEmpPk.text) -roundDiscussionNote $txtComment.text
        & $loadVotingData
    })
    $btnNextRound.add_click({
        $null = & $votingScript -actionType 'nextRound' -votingSessionPk ([int]$txtSessionPk.text) -employeePk ([int]$txtEmpPk.text) -roundDiscussionNote $txtComment.text
        & $loadVotingData
    })

    $tabPage.controls.addrange(@(
        $lblSession, $btnRefresh, $gridSession,
        $lblCast, $txtSessionPk, $lblEmp, $txtEmpPk, $lblCard, $cmbCard, $lblComment, $txtComment,
        $btnVote, $btnReveal, $btnNextRound,
        $lblHistory, $gridProcess
    ))

    return $tabPage
}
