<#
.SYNOPSIS
    Azure 多人參與 Story Point 系統 - 多 Tab 圖形化管理主介面 (uiMain.ps1)
.DESCRIPTION
    遵循開發規範之 UI 設計原則：
    1. 模組化：先有完整的 Console 相關 .ps1，提供完整參數。
    2. 靈活性：UI 可選擇使用「完整功能 (full)」或「簡化版本 (simplified)」，透過多 Tab 呈現，每個 Tab 由獨立的 .ps1 實作。
    3. 獨立性：Console 腳本仍可完全獨立運作。
    4. 一致性：UI 所有功能都是呼叫 Console 對應的 .ps1，任何商業邏輯只要修改 Console 的 .ps1。
.PARAMETER uiMode
    介面模式：full (完整 5 個頁籤) / simplified (簡化版 3 個核心頁籤：多人盲投、工項與員工賦予、點數統計報表)
.PARAMETER loginAccount
    選填：登入員工帳號（若未傳入或未通過驗證，啟動時強制彈出員工帳號登入驗證視窗）
.PARAMETER plainPassword
    選填：登入員工密碼（搭配 loginAccount 進行 storyPoint.spVerifyEmployeeLogin 驗證）
.PARAMETER noDialog
    測試驗證模式：僅初始化並回傳主表單與 Tab 控制項物件而不阻塞顯示對話框
.NOTES
    檔案名稱: uiMain.ps1
    建立者  : Antigravity Senior Full-Stack Engineer
    建立日期: 2026-10-23
#>
[cmdletbinding()]
param(
    [parameter(mandatory = $false)]
    [validateset('full', 'simplified', '')]
    [string]$uiMode = '',

    [parameter(mandatory = $false)]
    [string]$loginAccount = '',

    [parameter(mandatory = $false)]
    [string]$plainPassword = '',

    [parameter(mandatory = $false)]
    [switch]$noDialog
)

begin {
    $psRoot = split-path -path $psscriptroot -parent
    . (join-path -path $psRoot -childpath 'common.ps1')
    add-type -assemblyname System.Windows.Forms
    add-type -assemblyname System.Drawing

    <#
    .SYNOPSIS
        顯示強制員工帳號登入驗證對話框，禁止未驗證直接進入系統主畫面
    #>
    function showEmployeeLoginDialog {
        param(
            [parameter(mandatory = $true)]
            [string]$empScriptPath
        )

        $loginForm = new-object system.windows.forms.form
        $loginForm.text = '員工帳號登入驗證 - Azure 多人 Story Point 系統'
        $loginForm.size = new-object system.drawing.size(450, 320)
        $loginForm.startposition = 'CenterScreen'
        $loginForm.formborderstyle = 'FixedDialog'
        $loginForm.maximizebox = $false
        $loginForm.minimizebox = $false
        $loginForm.font = new-object system.drawing.font('Microsoft JhengHei UI', 9.5)

        $headerPanel = new-object system.windows.forms.panel
        $headerPanel.dock = 'Top'
        $headerPanel.height = 54
        $headerPanel.backcolor = [system.drawing.color]::fromargb(15, 23, 42)

        $lblHeader = new-object system.windows.forms.label
        $lblHeader.text = '🔐 員工帳號密碼登入驗證'
        $lblHeader.forecolor = [system.drawing.color]::white
        $lblHeader.font = new-object system.drawing.font('Microsoft JhengHei UI', 12, [system.drawing.fontstyle]::Bold)
        $lblHeader.autosize = $true
        $lblHeader.location = new-object system.drawing.point(18, 14)
        $headerPanel.controls.add($lblHeader)

        $lblNotice = new-object system.windows.forms.label
        $lblNotice.text = '請輸入您的員工登入帳號與密碼驗證身分，未登入不可直接進入系統：'
        $lblNotice.location = new-object system.drawing.point(22, 68)
        $lblNotice.size = new-object system.drawing.size(395, 24)

        $lblAcc = new-object system.windows.forms.label
        $lblAcc.text = '員工登入帳號：'
        $lblAcc.location = new-object system.drawing.point(22, 104)
        $lblAcc.autosize = $true

        $txtAcc = new-object system.windows.forms.textbox
        $txtAcc.location = new-object system.drawing.point(135, 100)
        $txtAcc.width = 270
        $txtAcc.text = ''

        $lblPwd = new-object system.windows.forms.label
        $lblPwd.text = '員工登入密碼：'
        $lblPwd.location = new-object system.drawing.point(22, 146)
        $lblPwd.autosize = $true

        $txtPwd = new-object system.windows.forms.textbox
        $txtPwd.location = new-object system.drawing.point(135, 142)
        $txtPwd.width = 270
        $txtPwd.useSystemPasswordChar = $true
        $txtPwd.text = ''

        $lblErr = new-object system.windows.forms.label
        $lblErr.text = ''
        $lblErr.forecolor = [system.drawing.color]::fromargb(185, 28, 28)
        $lblErr.location = new-object system.drawing.point(22, 180)
        $lblErr.size = new-object system.drawing.size(390, 26)

        $btnLogin = new-object system.windows.forms.button
        $btnLogin.text = '🔐 驗證並登入系統'
        $btnLogin.location = new-object system.drawing.point(135, 214)
        $btnLogin.size = new-object system.drawing.size(155, 34)
        $btnLogin.backcolor = [system.drawing.color]::fromargb(37, 99, 235)
        $btnLogin.forecolor = [system.drawing.color]::white

        $btnCancel = new-object system.windows.forms.button
        $btnCancel.text = '取消離開'
        $btnCancel.location = new-object system.drawing.point(300, 214)
        $btnCancel.size = new-object system.drawing.size(105, 34)
        $btnCancel.dialogresult = [system.windows.forms.dialogresult]::Cancel

        $script:verifiedEmployeeRow = $null

        $btnLogin.add_click({
            $inputAcc = $txtAcc.text.trim()
            $inputPwd = $txtPwd.text
            if ([string]::isnullorempty($inputAcc) -or [string]::isnullorempty($inputPwd)) {
                $lblErr.text = '⚠️ 請輸入完整的員工登入帳號與密碼！'
                return
            }

            try {
                $dt = & $empScriptPath -actionType 'verifyLogin' -loginAccount $inputAcc -plainPassword $inputPwd
                if ($null -ne $dt -and $dt.rows.count -gt 0) {
                    $script:verifiedEmployeeRow = $dt.rows[0]
                    $loginForm.dialogresult = [system.windows.forms.dialogresult]::OK
                    $loginForm.close()
                }
                else {
                    $lblErr.text = '⚠️ 帳號或密碼錯誤，或該員工帳號已停用。'
                }
            }
            catch {
                $lblErr.text = "⚠️ 登入驗證失敗：$($_.exception.message)"
            }
        })

        $loginForm.acceptbutton = $btnLogin
        $loginForm.cancelbutton = $btnCancel
        $loginForm.controls.addrange(@(
            $headerPanel, $lblNotice, $lblAcc, $txtAcc,
            $lblPwd, $txtPwd, $lblErr, $btnLogin, $btnCancel
        ))

        $dlgResult = $loginForm.showdialog()
        if ($dlgResult -eq [system.windows.forms.dialogresult]::OK -and $null -ne $script:verifiedEmployeeRow) {
            return $script:verifiedEmployeeRow
        }
        return $null
    }

    <#
    .SYNOPSIS
        顯示員工自助修改個人登入帳號與密碼對話框，支援每個人修改自己的帳/密
    #>
    function showChangeOwnCredentialDialog {
        param(
            [parameter(mandatory = $true)]
            [string]$empScriptPath,
            [parameter(mandatory = $true)]
            [int]$employeePk,
            [parameter(mandatory = $true)]
            [string]$currentLoginAccount,
            [parameter(mandatory = $true)]
            [string]$employeeName
        )

        $credForm = new-object system.windows.forms.form
        $credForm.text = "修改我的個人登入帳號與密碼 - $employeeName"
        $credForm.size = new-object system.drawing.size(460, 350)
        $credForm.startposition = 'CenterParent'
        $credForm.formborderstyle = 'FixedDialog'
        $credForm.maximizebox = $false
        $credForm.minimizebox = $false
        $credForm.font = new-object system.drawing.font('Microsoft JhengHei UI', 9.5)

        $hdrPanel = new-object system.windows.forms.panel
        $hdrPanel.dock = 'Top'
        $hdrPanel.height = 50
        $hdrPanel.backcolor = [system.drawing.color]::fromargb(30, 58, 138)

        $lblHdr = new-object system.windows.forms.label
        $lblHdr.text = "🔑 修改我的登入帳號與密碼 ($employeeName)"
        $lblHdr.forecolor = [system.drawing.color]::white
        $lblHdr.font = new-object system.drawing.font('Microsoft JhengHei UI', 11.5, [system.drawing.fontstyle]::Bold)
        $lblHdr.autosize = $true
        $lblHdr.location = new-object system.drawing.point(16, 13)
        $hdrPanel.controls.add($lblHdr)

        $lblNewAcc = new-object system.windows.forms.label
        $lblNewAcc.text = '我的登入帳號：'
        $lblNewAcc.location = new-object system.drawing.point(22, 72)
        $lblNewAcc.autosize = $true

        $txtNewAcc = new-object system.windows.forms.textbox
        $txtNewAcc.location = new-object system.drawing.point(145, 68)
        $txtNewAcc.width = 270
        $txtNewAcc.text = $currentLoginAccount

        $lblNewPwd = new-object system.windows.forms.label
        $lblNewPwd.text = '新登入密碼：'
        $lblNewPwd.location = new-object system.drawing.point(22, 114)
        $lblNewPwd.autosize = $true

        $txtNewPwd = new-object system.windows.forms.textbox
        $txtNewPwd.location = new-object system.drawing.point(145, 110)
        $txtNewPwd.width = 270
        $txtNewPwd.useSystemPasswordChar = $true
        $txtNewPwd.text = ''

        $lblConfirmPwd = new-object system.windows.forms.label
        $lblConfirmPwd.text = '確認新密碼：'
        $lblConfirmPwd.location = new-object system.drawing.point(22, 156)
        $lblConfirmPwd.autosize = $true

        $txtConfirmPwd = new-object system.windows.forms.textbox
        $txtConfirmPwd.location = new-object system.drawing.point(145, 152)
        $txtConfirmPwd.width = 270
        $txtConfirmPwd.useSystemPasswordChar = $true
        $txtConfirmPwd.text = ''

        $chkShowPwd = new-object system.windows.forms.checkbox
        $chkShowPwd.text = '👁️ 顯示輸入的密碼內容（若僅修改帳號可將密碼留空）'
        $chkShowPwd.location = new-object system.drawing.point(145, 186)
        $chkShowPwd.autosize = $true
        $chkShowPwd.add_checkedchanged({
            $hide = -not $chkShowPwd.checked
            $txtNewPwd.useSystemPasswordChar = $hide
            $txtConfirmPwd.useSystemPasswordChar = $hide
        })

        $lblMsg = new-object system.windows.forms.label
        $lblMsg.text = ''
        $lblMsg.forecolor = [system.drawing.color]::fromargb(185, 28, 28)
        $lblMsg.location = new-object system.drawing.point(22, 216)
        $lblMsg.size = new-object system.drawing.size(400, 30)

        $btnSaveSelf = new-object system.windows.forms.button
        $btnSaveSelf.text = '💾 儲存修改我的帳密'
        $btnSaveSelf.location = new-object system.drawing.point(145, 254)
        $btnSaveSelf.size = new-object system.drawing.size(160, 34)
        $btnSaveSelf.backcolor = [system.drawing.color]::fromargb(37, 99, 235)
        $btnSaveSelf.forecolor = [system.drawing.color]::white

        $btnCloseSelf = new-object system.windows.forms.button
        $btnCloseSelf.text = '取消'
        $btnCloseSelf.location = new-object system.drawing.point(315, 254)
        $btnCloseSelf.size = new-object system.drawing.size(100, 34)
        $btnCloseSelf.dialogresult = [system.windows.forms.dialogresult]::Cancel

        $script:updatedSelfRow = $null

        $btnSaveSelf.add_click({
            $accVal = $txtNewAcc.text.trim()
            $pwdVal = $txtNewPwd.text.trim()
            $cfmVal = $txtConfirmPwd.text.trim()

            if ([string]::isnullorempty($accVal)) {
                $lblMsg.text = '⚠️ 登入帳號不可為空白！'
                return
            }
            if ($pwdVal -ne $cfmVal) {
                $lblMsg.text = '⚠️ 兩次輸入的新密碼不一致，請重新確認！'
                return
            }
            if ($accVal -eq $currentLoginAccount -and [string]::isnullorempty($pwdVal)) {
                $lblMsg.text = '⚠️ 請輸入新的登入帳號或新的登入密碼！'
                return
            }

            try {
                $dt = & $empScriptPath -actionType 'updateSelfCredential' `
                    -employeePk $employeePk `
                    -newLoginAccount $accVal `
                    -plainPassword $pwdVal `
                    -operatorAccount $currentLoginAccount
                if ($null -ne $dt -and $dt.rows.count -gt 0) {
                    $script:updatedSelfRow = $dt.rows[0]
                    $credForm.dialogresult = [system.windows.forms.dialogresult]::OK
                    $credForm.close()
                }
            }
            catch {
                $lblMsg.text = "⚠️ 修改失敗：$($_.exception.message)"
            }
        })

        $credForm.acceptbutton = $btnSaveSelf
        $credForm.cancelbutton = $btnCloseSelf
        $credForm.controls.addrange(@(
            $hdrPanel, $lblNewAcc, $txtNewAcc, $lblNewPwd, $txtNewPwd,
            $lblConfirmPwd, $txtConfirmPwd, $chkShowPwd, $lblMsg,
            $btnSaveSelf, $btnCloseSelf
        ))

        $res = $credForm.showdialog()
        if ($res -eq [system.windows.forms.dialogresult]::OK -and $null -ne $script:updatedSelfRow) {
            return $script:updatedSelfRow
        }
        return $null
    }
}

process {
    $configObj = getSystemConfig
    if ([string]::isnullorempty($uiMode)) {
        $uiMode = [string]$configObj.systemSetting.uiMode
        if ([string]::isnullorempty($uiMode)) {
            $uiMode = 'full'
        }
    }

    [system.windows.forms.application]::enablevisualstyles()

    $empScriptPath = join-path -path $psRoot -childpath 'manageEmployee.ps1'
    $loggedInEmployee = $null

    if (-not [string]::isnullorempty($loginAccount) -and -not [string]::isnullorempty($plainPassword)) {
        $verifyDt = & $empScriptPath -actionType 'verifyLogin' -loginAccount $loginAccount.trim() -plainPassword $plainPassword
        if ($null -ne $verifyDt -and $verifyDt.rows.count -gt 0) {
            $loggedInEmployee = $verifyDt.rows[0]
        }
        elseif ($noDialog.ispresent) {
            throw "員工帳號密碼驗證失敗：帳號不存在、已停用或密碼錯誤 ($loginAccount)。"
        }
    }

    if ($null -eq $loggedInEmployee) {
        if ($noDialog.ispresent) {
            throw '安全性限制：禁止未提供有效員工帳號與密碼直接進入系統，請傳入 -loginAccount 與 -plainPassword 進行登入驗證。'
        }
        $loggedInEmployee = showEmployeeLoginDialog -empScriptPath $empScriptPath
        if ($null -eq $loggedInEmployee) {
            writeSystemLog -logLevel 2 -sourceScript 'uiMain.ps1' -objectName 'uiMain' `
                -executionResult '使用者取消或未通過員工帳號登入驗證，拒絕直接進入系統主畫面'
            return $null
        }
    }

    $currentEmpPk = [int]$loggedInEmployee.employeePk
    $currentEmpName = [string]$loggedInEmployee.employeeName
    $currentLoginAcc = [string]$loggedInEmployee.loginAccount
    $currentRoleName = [string]$loggedInEmployee.roleName
    $currentDeptName = [string]$loggedInEmployee.departmentName

    $mainForm = new-object system.windows.forms.form
    $modeTitle = if ($uiMode -eq 'simplified') { '簡化快速模式' } else { '完整專業模式' }
    $mainForm.text = "Azure 多人參與 Story Point 估算與工項權重管理系統 - [$modeTitle] - 登入者：$currentEmpName ($currentLoginAcc)"
    $mainForm.size = new-object system.drawing.size(1200, 780)
    $mainForm.startposition = 'CenterScreen'
    $mainForm.font = new-object system.drawing.font('Microsoft JhengHei UI', 9.5)

    $topPanel = new-object system.windows.forms.panel
    $topPanel.dock = 'Top'
    $topPanel.height = 46
    $topPanel.backcolor = [system.drawing.color]::fromargb(15, 23, 42)

    $lblTitle = new-object system.windows.forms.label
    $lblTitle.text = "🃏 Azure 多人參與 Story Point 估算系統 ($modeTitle) ｜ 👤 $currentEmpName ($currentLoginAcc / $currentDeptName / $currentRoleName)"
    $lblTitle.forecolor = [system.drawing.color]::white
    $lblTitle.font = new-object system.drawing.font('Microsoft JhengHei UI', 11.5, [system.drawing.fontstyle]::Bold)
    $lblTitle.autosize = $true
    $lblTitle.location = new-object system.drawing.point(16, 11)
    $topPanel.controls.add($lblTitle)

    $btnChangeMyAccount = new-object system.windows.forms.button
    $btnChangeMyAccount.text = '🔑 修改我的帳密'
    $btnChangeMyAccount.size = new-object system.drawing.size(150, 30)
    $btnChangeMyAccount.location = new-object system.drawing.point(1015, 8)
    $btnChangeMyAccount.anchor = 'Top, Right'
    $btnChangeMyAccount.backcolor = [system.drawing.color]::fromargb(37, 99, 235)
    $btnChangeMyAccount.forecolor = [system.drawing.color]::white
    $btnChangeMyAccount.add_click({
        $updatedRow = showChangeOwnCredentialDialog -empScriptPath $empScriptPath `
            -employeePk $currentEmpPk -currentLoginAccount $currentLoginAcc -employeeName $currentEmpName
        if ($null -ne $updatedRow) {
            $script:currentLoginAcc = [string]$updatedRow.loginAccount
            $currentLoginAcc = [string]$updatedRow.loginAccount
            $lblTitle.text = "🃏 Azure 多人參與 Story Point 估算系統 ($modeTitle) ｜ 👤 $currentEmpName ($currentLoginAcc / $currentDeptName / $currentRoleName)"
            $mainForm.text = "Azure 多人參與 Story Point 估算與工項權重管理系統 - [$modeTitle] - 登入者：$currentEmpName ($currentLoginAcc)"
            if ($null -ne $statusLabel) {
                $statusLabel.text = "✅ 已成功修改您的個人登入帳號與密碼！目前登入帳號：$currentLoginAcc"
            }
        }
    })
    $topPanel.controls.add($btnChangeMyAccount)

    $statusStrip = new-object system.windows.forms.statusstrip
    $statusLabel = new-object system.windows.forms.toolstripstatuslabel
    $statusLabel.text = "已登入：$currentEmpName ($currentLoginAcc) | 角色：$currentRoleName | Azure RG：$($configObj.azureSetting.resourceGroupName)"
    $null = $statusStrip.items.add($statusLabel)

    $tabControl = new-object system.windows.forms.tabcontrol
    $tabControl.dock = 'Fill'
    $tabControl.padding = new-object system.drawing.point(14, 6)

    $tabScriptList = if ($uiMode -eq 'simplified' -or $currentRoleName -ne 'admin') {
        @(
            'tabVoting.ps1',
            'tabWorkItem.ps1',
            'tabReport.ps1'
        )
    }
    else {
        @(
            'tabVoting.ps1',
            'tabWorkItemType.ps1',
            'tabWorkItem.ps1',
            'tabEmployee.ps1',
            'tabReport.ps1'
        )
    }

    foreach ($tabScriptName in $tabScriptList) {
        $tabScriptPath = join-path -path $psscriptroot -childpath $tabScriptName
        if (test-path -path $tabScriptPath) {
            $tabPage = & $tabScriptPath -psRootPath $psRoot -statusLabel $statusLabel
            if ($null -ne $tabPage) {
                $null = $tabControl.tabpages.add($tabPage)
            }
        }
    }

    $mainForm.controls.add($tabControl)
    $mainForm.controls.add($topPanel)
    $mainForm.controls.add($statusStrip)

    writeSystemLog -logLevel 3 -sourceScript 'uiMain.ps1' -objectName 'uiMain' `
        -executionResult "啟動多 Tab 圖形化管理介面成功 (模式：$uiMode，載入頁籤數：$($tabControl.tabpages.count))"

    if ($noDialog.ispresent) {
        return [pscustomobject]@{
            formObject = $mainForm
            tabControl = $tabControl
            tabCount   = $tabControl.tabpages.count
            uiMode     = $uiMode
        }
    }
    else {
        $null = $mainForm.showdialog()
    }
}
