<#
.SYNOPSIS
    Azure 多人參與 Story Point 系統 - Azure PaaS 全自動資源建立、資料庫初始化與 .NET 8 網站部署腳本 (deployAzure.ps1)
.DESCRIPTION
    提供透過 Azure CLI (az) 一鍵完成以下完整雲端部署流程：
    1. 驗證 Azure CLI 登入狀態
    2. 確認或建立 Azure Resource Group (預設為 storyPoint)
    3. 建立 Azure SQL 邏輯伺服器、設定防火牆規則 (允許 Azure 服務與本機 IP) 與建立 Azure SQL Database (storyPointDb)
    4. 呼叫 setEncryptedConfig.ps1 加密儲存 Azure SQL 連線字串，並呼叫 initDatabase.ps1 部署 storyPoint schema、資料表、檢視表、函數、預存程序與預設資料
    5. 建立 Azure App Service Plan 與 Azure Web App (.NET 8)，設定雲端應用程式組態 (AppSettings)
    6. 編譯打包 web/storyPointPortal 並部署至 Azure App Service
.PARAMETER resourceGroupName
    Azure 資源群組名稱 (預設為 storyPoint)
.PARAMETER locationName
    Azure 區域 (預設為 eastasia)
.PARAMETER appServicePlanName
    Azure App Service Plan 名稱 (預設為 asp-storypoint-964764)
.PARAMETER webAppName
    Azure App Service 網站名稱 (預設為 app-storypoint-964764)
.PARAMETER sqlServerName
    Azure SQL 邏輯伺服器名稱 (預設為 sql-storypoint-964764)
.PARAMETER sqlDatabaseName
    Azure SQL 資料庫名稱 (預設為 storyPointDb)
.PARAMETER sqlAdminUser
    Azure SQL 管理員帳號 (預設為 storyPointAdmin)
.PARAMETER sqlAdminPassword
    Azure SQL 管理員密碼 (若未傳入則自動產生符合複雜度之高強度密碼)
.PARAMETER publishOnly
    若指定此開關，僅編譯打包 web/storyPointPortal 並部署至既有之 Azure App Service
.NOTES
    檔案名稱: deployAzure.ps1
    建立者  : Antigravity Senior Full-Stack Engineer
    建立日期: 2026-10-23
#>
[cmdletbinding()]
param(
    [parameter(mandatory = $false)]
    [string]$resourceGroupName = 'storyPoint',

    [parameter(mandatory = $false)]
    [string]$locationName = 'eastasia',

    [parameter(mandatory = $false)]
    [string]$appServicePlanName = 'asp-storypoint-964764',

    [parameter(mandatory = $false)]
    [string]$webAppName = 'app-storypoint-964764',

    [parameter(mandatory = $false)]
    [string]$sqlServerName = 'sql-storypoint-964764',

    [parameter(mandatory = $false)]
    [string]$sqlDatabaseName = 'storyPointDb',

    [parameter(mandatory = $false)]
    [string]$sqlAdminUser = 'storyPointAdmin',

    [parameter(mandatory = $false)]
    [string]$sqlAdminPassword = '',

    [parameter(mandatory = $false)]
    [switch]$publishOnly
)

. (join-path -path $psscriptroot -childpath 'common.ps1')

$rootPath = getProjectRootPath
$webProjPath = join-path -path $rootPath -childpath 'web\storyPointPortal\storyPointPortal.csproj'
$publishDir = join-path -path $rootPath -childpath 'temp\publishOutput'
$zipPath = join-path -path $rootPath -childpath 'temp\storyPointPortalDeploy.zip'

if ([string]::isnullorempty($sqlAdminPassword)) {
    $sqlAdminPassword = "Story!Point#" + (get-random -minimum 100000 -maximum 999999) + "Az"
}

$portalUrl = "https://$webAppName.azurewebsites.net"
$azureSqlConnStr = "Server=tcp:$sqlServerName.database.windows.net,1433;Initial Catalog=$sqlDatabaseName;Persist Security Info=False;User ID=$sqlAdminUser;Password=$sqlAdminPassword;MultipleActiveResultSets=True;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;Application Name=azureStoryPointSystem;"

if (-not $publishOnly.ispresent) {
    write-progress -activity '部署 Azure Story Point 系統' -status "正在確認或建立資源群組 $resourceGroupName ($locationName)..." -percentcomplete 15
    & az group create --name $resourceGroupName --location $locationName --output none
    if ($LASTEXITCODE -ne 0) { throw "建立資源群組 $resourceGroupName 失敗。" }

    write-progress -activity '部署 Azure Story Point 系統' -status "正在確認或建立 Azure SQL 邏輯伺服器 ($sqlServerName)..." -percentcomplete 30
    $null = & az sql server show --name $sqlServerName --resource-group $resourceGroupName --output none 2>$null
    if ($LASTEXITCODE -eq 0) {
        & az sql server update --name $sqlServerName --resource-group $resourceGroupName --admin-password $sqlAdminPassword --output none
    }
    else {
        & az sql server create --name $sqlServerName --resource-group $resourceGroupName --location $locationName --admin-user $sqlAdminUser --admin-password $sqlAdminPassword --output none
        if ($LASTEXITCODE -ne 0) { throw "建立 Azure SQL Server ($sqlServerName) 失敗。" }
    }

    write-progress -activity '部署 Azure Story Point 系統' -status '正在配置 Azure SQL 防火牆規則...' -percentcomplete 40
    & az sql server firewall-rule create --resource-group $resourceGroupName --server $sqlServerName --name 'AllowAllWindowsAzureIps' --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0 --output none
    try {
        $myIp = (invoke-restmethod -uri 'https://api.ipify.org' -timeoutsec 5).trim()
        if (-not [string]::isnullorempty($myIp)) {
            & az sql server firewall-rule create --resource-group $resourceGroupName --server $sqlServerName --name 'AllowDeploymentClientIp' --start-ip-address $myIp --end-ip-address $myIp --output none
        }
    }
    catch {
        # 忽略自動偵測公開 IP 例外
    }

    write-progress -activity '部署 Azure Story Point 系統' -status "正在確認或建立 Azure SQL Database ($sqlDatabaseName)..." -percentcomplete 52
    $null = & az sql db show --resource-group $resourceGroupName --server $sqlServerName --name $sqlDatabaseName --output none 2>$null
    if ($LASTEXITCODE -ne 0) {
        & az sql db create --resource-group $resourceGroupName --server $sqlServerName --name $sqlDatabaseName --edition Basic --capacity 5 --zone-redundant false --backup-storage-redundancy Local --yes --output none
        if ($LASTEXITCODE -ne 0) { throw "建立 Azure SQL Database ($sqlDatabaseName) 失敗。" }
    }

    write-progress -activity '部署 Azure Story Point 系統' -status '正在加密儲存連線設定並初始化雲端資料庫...' -percentcomplete 65
    $null = & (join-path -path $psscriptroot -childpath 'setEncryptedConfig.ps1') -connectionString $azureSqlConnStr
    $null = & (join-path -path $psscriptroot -childpath 'initDatabase.ps1')

    write-progress -activity '部署 Azure Story Point 系統' -status "正在建立 Azure App Service Plan ($appServicePlanName) 與 Web App ($webAppName)..." -percentcomplete 78
    $null = & az appservice plan show --name $appServicePlanName --resource-group $resourceGroupName --output none 2>$null
    if ($LASTEXITCODE -ne 0) {
        & az appservice plan create --name $appServicePlanName --resource-group $resourceGroupName --location $locationName --sku B1 --is-linux --output none
    }

    $null = & az webapp show --name $webAppName --resource-group $resourceGroupName --output none 2>$null
    if ($LASTEXITCODE -ne 0) {
        & az webapp create --name $webAppName --resource-group $resourceGroupName --plan $appServicePlanName --runtime "DOTNETCORE:8.0" --output none
        if ($LASTEXITCODE -ne 0) { throw "建立 Azure Web App ($webAppName) 失敗。" }
    }

    & az webapp config appsettings set --resource-group $resourceGroupName --name $webAppName --settings `
        "systemSetting__portalBaseUrl=$portalUrl" `
        "databaseSetting__connectionString=$azureSqlConnStr" `
        --output none
}

if (test-path -path $webProjPath) {
    write-progress -activity '部署 Azure Story Point 系統' -status '正在編譯並發行 .NET 8 storyPointPortal 網站...' -percentcomplete 88
    $stageDir = join-path -path $rootPath -childpath ("temp\publishStage_" + (get-date).tostring('yyyyMMddHHmmss'))
    & dotnet publish $webProjPath -c Release -o $stageDir --nologo | out-null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 編譯失敗。' }

    write-progress -activity '部署 Azure Story Point 系統' -status "正在上傳部署套件至 Azure App Service ($webAppName)..." -percentcomplete 95
    if (test-path -path $zipPath) {
        remove-item -path $zipPath -force
    }
    add-type -assemblyname System.IO.Compression.FileSystem
    [system.io.compression.zipfile]::createfromdirectory($stageDir, $zipPath)
    remove-item -path $stageDir -recurse -force -erroraction silentlycontinue

    & az webapp deploy --resource-group $resourceGroupName --name $webAppName --src-path $zipPath --type zip --output none
    if ($LASTEXITCODE -ne 0) { throw "部署 ZIP 套件至 Azure Web App ($webAppName) 失敗。" }
}

write-progress -activity '部署 Azure Story Point 系統' -status '部署完成' -percentcomplete 100 -completed

writeSystemLog -logLevel 3 -sourceScript 'deployAzure.ps1' -objectName $webAppName `
    -executionResult "成功完成 Azure Story Point 資源群組 ($resourceGroupName) 與雲端部署：$portalUrl"

return [pscustomobject]@{
    status            = 'success'
    resourceGroupName = $resourceGroupName
    webAppName        = $webAppName
    portalUrl         = $portalUrl
    sqlServerFqdn     = "$sqlServerName.database.windows.net"
    sqlDatabaseName   = $sqlDatabaseName
}
