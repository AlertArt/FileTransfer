# ============================================================
# verify-apk-signature.ps1
# 校验 Android APK 的签名与 ABI（本地排查用；CI 里由构建产物保证）。
#   - v1（JAR，META-INF/*.RSA）：Android 6 及以下安装所需
#   - v2（APK Signing Block）：Android 7+ 校验
#   - 内含 ABI（fat APK 应为 arm64-v8a + armeabi-v7a + x86_64）
#
# 用法:
#   pwsh -File tools/verify-apk-signature.ps1
#   pwsh -File tools/verify-apk-signature.ps1 -Apk path\to\app.apk
# ============================================================
[CmdletBinding()]
param(
    [string]$Apk
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Apk)) {
    $Apk = Join-Path $repoRoot 'build/release/android/com.CompanyName.FileTransferApp-Signed.apk'
}
if (-not (Test-Path -LiteralPath $Apk)) {
    Write-Output "APK 不存在: $Apk"
    exit 1
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

# ---- v1 证书 / ABI / MANIFEST ----
$zip = [System.IO.Compression.ZipFile]::OpenRead($Apk)
try {
    $v1 = @($zip.Entries | Where-Object { $_.FullName -match '^META-INF/.*\.(RSA|DSA|EC)$' })
    $abis = $zip.Entries |
        Where-Object { $_.FullName -match '^lib/([^/]+)/' } |
        ForEach-Object { ($_.FullName -split '/')[1] } |
        Sort-Object -Unique
    $manifest = @($zip.Entries | Where-Object { $_.FullName -eq 'META-INF/MANIFEST.MF' }).Count
}
finally { $zip.Dispose() }

# ---- v2 签名块：magic "APK Sig Block 42" 紧贴 Central Directory 之前 ----
$fs = [System.IO.File]::OpenRead($Apk)
try {
    $fs.Position = $fs.Length - 22
    $eocd = New-Object byte[] 22
    [void]$fs.Read($eocd, 0, 22)
    $eocdSig = [BitConverter]::ToUInt32($eocd, 0)
    $cdOff = [BitConverter]::ToUInt32($eocd, 16)

    $scanStart = [Math]::Max(0, $cdOff - 4096)
    $len = $cdOff - $scanStart
    $fs.Position = $scanStart
    $buf = New-Object byte[] $len
    [void]$fs.Read($buf, 0, $len)
    $hay = [System.Text.Encoding]::ASCII.GetString($buf)
    $hasV2 = $hay.Contains('APK Sig Block 42')
}
finally { $fs.Dispose() }

$sizeMb = [Math]::Round((Get-Item -LiteralPath $Apk).Length / 1MB, 1)
$eocdOk = ($eocdSig -eq 0x06054B50)

Write-Output "APK             : $Apk"
Write-Output "Size            : $sizeMb MB"
Write-Output ("EOCD            : 0x{0:X8} {1}" -f $eocdSig, $(if ($eocdOk) { '(OK)' } else { '(损坏/截断?)' }))
Write-Output "v1 cert entries : $($v1.Count) $(if ($v1.Count -gt 0) { '(OK, Android<7 可装)' } else { '(缺 v1)' })"
Write-Output "v2 signing block: $(if ($hasV2) { 'PRESENT (OK)' } else { 'ABSENT' })"
Write-Output "MANIFEST.MF     : $manifest"
Write-Output "ABIs            : $($abis -join ', ')"

if ($eocdOk -and $v1.Count -gt 0 -and $hasV2) {
    Write-Output 'RESULT: PASS'
} else {
    Write-Output 'RESULT: CHECK REQUIRED'
    exit 2
}
