# 向 tunnelserver.json 的 Admin.AllowedIps 追加白名单 IP（保留 127.0.0.1 回环）
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File patch-admin-ips.ps1 -Config <json路径> -Ips "ip1,ip2"
param(
    [Parameter(Mandatory = $true)][string]$Config,
    [Parameter(Mandatory = $true)][string]$Ips
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Config)) {
    Write-Error "配置文件不存在：$Config"
    exit 1
}

# 解析用户输入：逗号分隔，去空白，剔除空项
$add = $Ips -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' }
if (@($add).Count -eq 0) {
    Write-Host '[跳过] 未提供有效 IP，保持原白名单不变'
    exit 0
}

# 任一条目为 * → 整体通配（放行所有来源），不再拼接回环
if (@($add) -contains '*') {
    $arr  = @('*')
    $items = '"*"'
    $note = '*（允许所有来源——务必确认 5103 未对公网放行）'
}
else {
    # 总是保留回环
    $arr = @('127.0.0.1') + @($add)
    $items = ($arr | ForEach-Object { '"' + $_ + '"' }) -join ', '
    $note = $arr -join ', '
}

$json = Get-Content $Config -Raw
$patched = $json -replace '"AllowedIps"\s*:\s*\[[^\]]*\]', ('"AllowedIps": [ ' + $items + ' ]')
if ($patched -eq $json) {
    Write-Error '未匹配到 AllowedIps 段，替换失败（检查配置文件格式）'
    exit 1
}

Set-Content -Path $Config -Value $patched -Encoding UTF8
Write-Host "[配置] 管理台白名单已更新：$note"
