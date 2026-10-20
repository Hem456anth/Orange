<#
  Signs Orange's files when a signing secret is configured, and does nothing (exit 0) when none is.
  Option A, PFX:            SIGN_CERT (base64 .pfx), SIGN_PASS
  Option B, Azure Trusted Signing:
                            AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET,
                            TS_ENDPOINT (e.g. https://eus.codesigning.azure.net), TS_ACCOUNT, TS_PROFILE
  Usage: Sign-Files.ps1 <file> [<file> ...]   Prints "signed=<n>" or "signing skipped".
#>
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Files)
$ErrorActionPreference = 'Stop'
$Files = @($Files | Where-Object { $_ -and (Test-Path $_) })
if ($Files.Count -eq 0) { "nothing to sign"; exit 0 }
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe | Where-Object FullName -like '*x64*' | Sort-Object FullName | Select-Object -Last 1
$ts = 'http://timestamp.acs.microsoft.com'

if ($env:SIGN_CERT) {
  $pfx = Join-Path $env:RUNNER_TEMP 'orange-sign.pfx'
  [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:SIGN_CERT))
  try {
    & $signtool.FullName sign /f $pfx /p $env:SIGN_PASS /fd SHA256 /tr $ts /td SHA256 @Files
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
  } finally { Remove-Item $pfx -ErrorAction SilentlyContinue }
  "signed=$($Files.Count) (pfx)"; exit 0
}

if ($env:AZURE_CLIENT_ID -and $env:TS_ACCOUNT -and $env:TS_PROFILE -and $env:TS_ENDPOINT) {
  $pkg = Join-Path $env:RUNNER_TEMP 'trusted-signing'
  if (-not (Test-Path "$pkg\bin\x64\Azure.CodeSigning.Dlib.dll")) {
    Invoke-WebRequest 'https://www.nuget.org/api/v2/package/Microsoft.Trusted.Signing.Client' -OutFile "$pkg.zip"
    Expand-Archive "$pkg.zip" $pkg -Force
  }
  $dlib = Get-ChildItem $pkg -Recurse -Filter Azure.CodeSigning.Dlib.dll | Where-Object FullName -like '*x64*' | Select-Object -First 1
  $meta = Join-Path $env:RUNNER_TEMP 'ts-metadata.json'
  @{ Endpoint = $env:TS_ENDPOINT; CodeSigningAccountName = $env:TS_ACCOUNT; CertificateProfileName = $env:TS_PROFILE } | ConvertTo-Json | Set-Content $meta
  & $signtool.FullName sign /v /fd SHA256 /tr $ts /td SHA256 /dlib $dlib.FullName /dmdf $meta @Files
  if ($LASTEXITCODE -ne 0) { throw "Trusted Signing failed ($LASTEXITCODE)" }
  "signed=$($Files.Count) (azure trusted signing)"; exit 0
}

"signing skipped (no SIGN_CERT or Trusted Signing secrets)"
exit 0
