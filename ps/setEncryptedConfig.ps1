<#
.SYNOPSIS
    加密設定 SQL Server 正式環境連線字串工具 (setEncryptedConfig.ps1)
.DESCRIPTION
    提供將正式環境 (如 Azure SQL Database 或正式 SQL Server) 之連線字串以 Windows DPAPI 加密，
    並自動補齊 Application Name=azureStoryPointSystem 後寫回 config.json。
.PARAMETER connectionString
    欲加密儲存之 SQL Server 連線字串明文
.PARAMETER clearPlainDevString
    加密寫入後，是否一併清空 config.json 內的 plainConnectionStringForDev 欄位 (正式環境建議設為 $true)
.NOTES
    檔案名稱: setEncryptedConfig.ps1
    建立者  : Antigravity Senior Full-Stack Engineer
    建立日期: 2026-10-23
#>
[cmdletbinding()]
param(
    [parameter(mandatory = $true)]
    [string]$connectionString,
    [parameter(mandatory = $false)]
    [switch]$clearPlainDevString
)

. (join-path -path $psscriptroot -childpath 'common.ps1')

invokeWithRetry -operationTitle '設定並加密 SQL Server 連線字串' -scriptBlock {
    $config = getSystemConfig
    $appName = $config.databaseSetting.applicationName
    if ([string]::isnullorempty($appName)) {
        $appName = 'azureStoryPointSystem'
    }

    if ($connectionString -notmatch 'Application Name\s*=') {
        $connectionString = $connectionString.trimend(';') + ";Application Name=$appName;"
    }

    $encryptedStr = protectSensitiveText -plainText $connectionString
    $config.databaseSetting.encryptedConnectionString = $encryptedStr

    if ($clearPlainDevString.ispresent) {
        $config.databaseSetting.plainConnectionStringForDev = ''
    }

    $rootPath = getProjectRootPath
    $configPath = join-path -path $rootPath -childpath 'config.json'
    $jsonOut = $config | convertto-json -depth 10
    saveFileWithStandardEncoding -filePath $configPath -content $jsonOut

    writeSystemLog -logLevel 3 -sourceScript 'setEncryptedConfig.ps1' -objectName 'protectSensitiveText' `
        -executionResult '已成功加密 SQL Server 連線字串並寫入 config.json'

    return [pscustomobject]@{
        status = 'success'
        message = '已成功加密 SQL Server 連線字串並更新至 config.json'
        configPath = $configPath
    }
}
