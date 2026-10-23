<#
.SYNOPSIS
    多人參與 Story Point 盲投、翻牌控制與過程歷史查詢 Console 模組 (manageVotingSession.ps1)
.DESCRIPTION
    提供：
    1. 建立針對「工項代表類型 (workItemType)」或「個別工項 (workItem)」的多人投票議程
    2. 參與者針對當前盲投回合投出撲克牌點數與理由 (spCastVote)
    3. 管理者控制「翻牌揭曉 (reveal)」、「開啟下一回合重新投票 (nextRound)」與「拍板定案最終點數 (finalize)」
    4. 事後查詢訂出 Story Point 的完整過程 (包含每回合每位參與者的出牌點數、平均值、最高/最低值、討論備註與最終核定理由)
.PARAMETER actionType
    執行動作：
    - 'listSession'     : 查詢投票議程清單
    - 'getProcess'      : 查詢指定投票議程的多回合訂定過程完整明細 (回傳 DataSet 含 4 張表)
    - 'createSession'   : 建立新投票議程
    - 'castVote'        : 員工出牌投票
    - 'revealRound'     : 管理者翻牌揭曉本回合點數
    - 'nextRound'       : 管理者開啟下一回合重新投票
    - 'finalizeSession' : 管理者拍板定案最終 Story Point
.NOTES
    檔案名稱: manageVotingSession.ps1
    建立者  : Antigravity Senior Full-Stack Engineer
    建立日期: 2026-10-23
#>
[cmdletbinding()]
param(
    [parameter(mandatory = $false)]
    [validateset('listSession', 'getProcess', 'createSession', 'saveRoundSetting', 'castVote', 'revealRound', 'nextRound', 'finalizeSession')]
    [string]$actionType = 'listSession',

    [parameter(mandatory = $false)]
    [int]$votingSessionPk = 0,

    [parameter(mandatory = $false)]
    [string]$sessionCode = '',

    [parameter(mandatory = $false)]
    [string]$sessionTitle = '',

    [parameter(mandatory = $false)]
    [validateset('workItemType', 'workItem')]
    [string]$targetKind = 'workItemType',

    [parameter(mandatory = $false)]
    [int]$workItemTypePk = 0,

    [parameter(mandatory = $false)]
    [int]$workItemPk = 0,

    [parameter(mandatory = $false)]
    [int]$cardDeckPk = 0,

    [parameter(mandatory = $false)]
    [int]$hostEmployeePk = 1,

    [parameter(mandatory = $false)]
    [string]$participantPkCsv = '',

    [parameter(mandatory = $false)]
    [decimal]$minStoryPoint = 0.00,

    [parameter(mandatory = $false)]
    [decimal]$maxStoryPoint = 21.00,

    [parameter(mandatory = $false)]
    [decimal]$maxAllowedSpread = 5.00,

    [parameter(mandatory = $false)]
    [string]$consensusRule = 'nearestFibonacci',

    [parameter(mandatory = $false)]
    [bool]$autoRevealWhenAllVoted = $true,

    [parameter(mandatory = $false)]
    [int]$roundTimeLimitSecond = 120,

    [parameter(mandatory = $false)]
    [bool]$allowQuestionCard = $true,

    [parameter(mandatory = $false)]
    [bool]$allowCoffeeCard = $true,

    [parameter(mandatory = $false)]
    [string]$voteCommentPolicy = 'optional',

    [parameter(mandatory = $false)]
    [bool]$restrictToParticipant = $true,

    [parameter(mandatory = $false)]
    [string]$roundGoalNote = '',

    [parameter(mandatory = $false)]
    [int]$employeePk = 1,

    [parameter(mandatory = $false)]
    [string]$cardValue = '5',

    [parameter(mandatory = $false)]
    [string]$voteComment = '',

    [parameter(mandatory = $false)]
    [string]$roundDiscussionNote = '',

    [parameter(mandatory = $false)]
    [decimal]$finalStoryPoint = 0.00,

    [parameter(mandatory = $false)]
    [string]$finalDecisionNote = '',

    [parameter(mandatory = $false)]
    [bool]$syncExistingOpenWorkItem = $false,

    [parameter(mandatory = $false)]
    [bool]$isViewerAdmin = $true,

    [parameter(mandatory = $false)]
    [string]$operatorAccount = $env:username
)

. (join-path -path $psscriptroot -childpath 'common.ps1')

switch ($actionType) {
    'listSession' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spGetVotingProcessHistory' -parameters @{
            votingSessionPk = $(if ($votingSessionPk -gt 0) { $votingSessionPk } else { $null })
            workItemTypePk = $(if ($workItemTypePk -gt 0) { $workItemTypePk } else { $null })
            workItemPk = $(if ($workItemPk -gt 0) { $workItemPk } else { $null })
            viewerEmployeePk = $employeePk
            isViewerAdmin = $isViewerAdmin
        }
        return ,$ds.tables[0]
    }

    'getProcess' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spGetVotingProcessHistory' -parameters @{
            votingSessionPk = $(if ($votingSessionPk -gt 0) { $votingSessionPk } else { $null })
            workItemTypePk = $(if ($workItemTypePk -gt 0) { $workItemTypePk } else { $null })
            workItemPk = $(if ($workItemPk -gt 0) { $workItemPk } else { $null })
            viewerEmployeePk = $employeePk
            isViewerAdmin = $isViewerAdmin
        }
        writeSystemLog -logLevel 3 -sourceScript 'manageVotingSession.ps1' -objectName 'getProcess' `
            -executionResult "查詢投票議程過程紀錄完成 (議程數: $($ds.tables[0].rows.count), 回合數: $($ds.tables[1].rows.count), 出牌數: $($ds.tables[2].rows.count))" -operatorAccount $operatorAccount
        return ,$ds
    }

    { $_ -in @('createSession', 'saveRoundSetting') } {
        if ([string]::isnullorempty($sessionCode)) {
            $sessionCode = "VOTE-" + (get-date).tostring('yyyyMMdd-HHmmss')
        }
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spCreateVotingSession' -parameters @{
            votingSessionPk = $(if ($votingSessionPk -gt 0) { $votingSessionPk } else { $null })
            sessionCode = $sessionCode
            sessionTitle = $sessionTitle
            targetKind = $targetKind
            workItemTypePk = $(if ($workItemTypePk -gt 0) { $workItemTypePk } else { $null })
            workItemPk = $(if ($workItemPk -gt 0) { $workItemPk } else { $null })
            cardDeckPk = $(if ($cardDeckPk -gt 0) { $cardDeckPk } else { $null })
            hostEmployeePk = $hostEmployeePk
            participantPkCsv = $participantPkCsv
            minStoryPoint = $minStoryPoint
            maxStoryPoint = $maxStoryPoint
            maxAllowedSpread = $maxAllowedSpread
            consensusRule = $consensusRule
            autoRevealWhenAllVoted = $autoRevealWhenAllVoted
            roundTimeLimitSecond = $roundTimeLimitSecond
            allowQuestionCard = $allowQuestionCard
            allowCoffeeCard = $allowCoffeeCard
            voteCommentPolicy = $voteCommentPolicy
            restrictToParticipant = $restrictToParticipant
            roundGoalNote = $roundGoalNote
            operatorAccount = $operatorAccount
        }
        return ,$ds.tables[0]
    }

    'castVote' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spCastVote' -parameters @{
            votingSessionPk = $votingSessionPk
            employeePk = $employeePk
            cardValue = $cardValue
            voteComment = $voteComment
            operatorAccount = $operatorAccount
        }
        return ,$ds.tables[0]
    }

    'revealRound' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spControlVotingSession' -parameters @{
            votingSessionPk = $votingSessionPk
            controlAction = 'reveal'
            roundDiscussionNote = $roundDiscussionNote
            operatorEmployeePk = $employeePk
            operatorAccount = $operatorAccount
        }
        return ,$ds.tables[0]
    }

    'nextRound' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spControlVotingSession' -parameters @{
            votingSessionPk = $votingSessionPk
            controlAction = 'nextRound'
            roundDiscussionNote = $roundDiscussionNote
            operatorEmployeePk = $employeePk
            operatorAccount = $operatorAccount
        }
        return ,$ds.tables[0]
    }

    'finalizeSession' {
        $ds = invokeStoredProcedureDataSet -procedureName 'storyPoint.spControlVotingSession' -parameters @{
            votingSessionPk = $votingSessionPk
            controlAction = 'finalize'
            roundDiscussionNote = $roundDiscussionNote
            finalStoryPoint = $finalStoryPoint
            finalDecisionNote = $finalDecisionNote
            syncExistingOpenWorkItem = $syncExistingOpenWorkItem
            operatorEmployeePk = $employeePk
            operatorAccount = $operatorAccount
        }
        return ,$ds.tables[0]
    }
}
