# Publishes this repository to GitHub using the REST API only - no git or gh required.
#
#   .\tools\publish.ps1 -Owner <you> -Repo <name> -CreateRepo -TokenFile "$env:USERPROFILE\.github-token"
#   .\tools\publish.ps1 -Owner <you> -Repo <name> -Tag v1.6.2 -Exe dist\HPD20Companion.exe
#   .\tools\publish.ps1 -Owner <you> -Repo <name> -DryRun          # show the plan, touch nothing
#
# The token needs, for a fine-grained token: Contents (read/write), Metadata (read), and
# Administration (read/write) only if -CreateRepo is used. A classic token needs just "repo".
# The token is never printed; pass it as -Token, -TokenFile, or the GITHUB_TOKEN environment variable.

param(
    [Parameter(Mandatory = $true)][string]$Owner,
    [Parameter(Mandatory = $true)][string]$Repo,
    [string]$Token,
    [string]$TokenFile = (Join-Path $env:USERPROFILE '.github-token'),
    [switch]$CreateRepo,
    [ValidateSet('public', 'private')][string]$Visibility = 'public',
    [string]$Branch = 'main',
    [string]$Message,
    [string]$Tag,
    [string]$Exe,
    [string]$Notes,
    [string[]]$Delete = @(),
    [string[]]$Exclude = @(),
    [switch]$Prune,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $root          # tools\ -> repo root

# ---------------------------------------------------------------- token
if (-not $Token) {
    if (Test-Path $TokenFile) {
        $raw = Get-Content $TokenFile -Raw
        if ($raw) { $Token = $raw.Trim() }        # an empty file must not throw here
    }
    elseif ($env:GITHUB_TOKEN) { $Token = $env:GITHUB_TOKEN.Trim() }
}
if ($Token) { $Token = $Token.Trim() }
if (-not $Token -and -not $DryRun) {
    $exists = Test-Path $TokenFile
    if ($exists -and (Get-Item $TokenFile).Length -eq 0) {
        throw "The token file '$TokenFile' exists but is EMPTY. Put the token in it as a single line, e.g.`n  Set-Content '$TokenFile' -Value 'github_pat_...' -NoNewline"
    }
    throw "No token. Put one in '$TokenFile' (one line, no quotes), or set GITHUB_TOKEN, or pass -Token."
}

$api = "https://api.github.com"
$headers = @{
    'Authorization'        = "Bearer $Token"
    'Accept'               = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent'           = 'hpd20-companion-publisher'
}

function Invoke-GitHub {
    param([string]$Method, [string]$Uri, $Body, [string]$ContentType = 'application/json')
    $params = @{ Method = $Method; Uri = $Uri; Headers = $headers; UseBasicParsing = $true }
    if ($null -ne $Body) {
        if ($Body -is [string]) { $params['Body'] = $Body }
        else { $params['Body'] = ($Body | ConvertTo-Json -Depth 10 -Compress) }
        $params['ContentType'] = $ContentType
    }
    try {
        return Invoke-RestMethod @params
    }
    catch {
        $detail = ''
        try {
            $resp = $_.Exception.Response
            if ($resp) {
                $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
                $detail = $reader.ReadToEnd()
            }
        } catch { }
        throw ("GitHub {0} {1} failed: {2} {3}" -f $Method, $Uri, $_.Exception.Message, $detail)
    }
}

# ---------------------------------------------------------------- what to publish
$excludeDirs = @('dist', '.git', '.vs', '.vscode', '.idea')
$files = Get-ChildItem $root -Recurse -File | Where-Object {
    $p = $_.FullName
    foreach ($e in $excludeDirs) { if ($p -match ('\\' + [regex]::Escape($e) + '\\')) { return $false } }
    if ($p -match '\.(exe|pdb|obj|tmp|log)$') { return $false }
    if ($_.Name -eq 'Thumbs.db' -or $_.Name -eq 'desktop.ini') { return $false }
    return $true
}

$items = @()
foreach ($f in $files) {
    $rel = $f.FullName.Substring($root.Length + 1) -replace '\\', '/'
    $skip = $false
    foreach ($x in $Exclude) {
        if (-not $x) { continue }
        $needle = ($x -replace '\\', '/')
        if ($needle.StartsWith('./')) { $needle = $needle.Substring(2) }
        if ($rel.StartsWith($needle, [StringComparison]::OrdinalIgnoreCase)) { $skip = $true }
    }
    if ($skip) { continue }
    $items += [pscustomobject]@{ Path = $rel; FullName = $f.FullName; Bytes = $f.Length }
}
if ($Exclude.Count -gt 0) { Write-Host ("excluding : {0}" -f ($Exclude -join ', ')) }

Write-Host ("repository : {0}/{1}  ({2})" -f $Owner, $Repo, $Visibility)
Write-Host ("branch     : {0}" -f $Branch)
Write-Host ("files      : {0} ({1:N1} KB)" -f $items.Count, (($items | Measure-Object Bytes -Sum).Sum / 1KB))
if ($Tag) { Write-Host ("release    : {0}  asset: {1}" -f $Tag, $(if ($Exe) { $Exe } else { '(none)' })) }

if ($DryRun) {
    Write-Host ''
    Write-Host 'dry run - nothing sent. Files that would be published:'
    $items | Sort-Object Path | ForEach-Object { Write-Host ("  {0,-48} {1,8:N0} B" -f $_.Path, $_.Bytes) }
    if ($items.Count -eq 0) { throw "no files matched - check the exclusion rules" }
    if ($items | Where-Object { $_.Bytes -gt 40MB }) { Write-Warning 'a file is larger than 40 MB; the blob API may refuse it' }
    return
}

# ---------------------------------------------------------------- who are we
try {
    $me = Invoke-GitHub -Method GET -Uri "$api/user"
    Write-Host ("token      : authenticated as {0}" -f $me.login)
}
catch {
    throw "The token was rejected by GitHub. Check that it is valid, not expired, and has Contents: read/write.`n$($_.Exception.Message)"
}

# ---------------------------------------------------------------- repository
$repoUri = "$api/repos/$Owner/$Repo"
$exists = $true
try { $info = Invoke-GitHub -Method GET -Uri $repoUri }
catch {
    if ($_.Exception.Message -match '404') { $exists = $false } else { throw }
}

if (-not $exists) {
    if (-not $CreateRepo) {
        throw "Repository $Owner/$Repo does not exist. Re-run with -CreateRepo to create it (needs Administration: write)."
    }
    $body = @{ name = $Repo; private = ($Visibility -eq 'private'); description = 'Windows companion app for the Roland HandSonic HPD-20'; has_issues = $true; has_wiki = $false }
    $info = Invoke-GitHub -Method POST -Uri "$api/user/repos" -Body $body
    Write-Host ("created    : {0}" -f $info.html_url)
} else {
    Write-Host ("found      : {0}" -f $info.html_url)
}

# ---------------------------------------------------------------- current head (if any)
$headSha = $null
$baseTree = $null
try {
    $ref = Invoke-GitHub -Method GET -Uri "$api/repos/$Owner/$Repo/git/ref/heads/$Branch"
    $headSha = $ref.object.sha
    $commit = Invoke-GitHub -Method GET -Uri "$api/repos/$Owner/$Repo/git/commits/$headSha"
    $baseTree = $commit.tree.sha
    Write-Host ("head       : {0} ({1})" -f $headSha.Substring(0, 7), $commit.message.Split("`n")[0])
} catch {
    Write-Host "head       : none yet (this will be the first commit)"
}

# ---------------------------------------------------------------- blobs
$tree = @()
$n = 0
foreach ($it in $items) {
    $n++
    $bytes = [System.IO.File]::ReadAllBytes($it.FullName)
    $b64 = [Convert]::ToBase64String($bytes)
    $blob = Invoke-GitHub -Method POST -Uri "$api/repos/$Owner/$Repo/git/blobs" -Body @{ content = $b64; encoding = 'base64' }
    $tree += @{ path = $it.Path; mode = '100644'; type = 'blob'; sha = $blob.sha }
    if ($n % 10 -eq 0) { Write-Host ("  uploaded {0}/{1}" -f $n, $items.Count) }
}
Write-Host ("blobs      : {0}" -f $tree.Count)

# ---------------------------------------------------------------- tree + commit
# ---- optional deletions: stale paths that the ignore rules exclude
$removals = @()
if ($Prune -and $baseTree) {
    $remote = Invoke-GitHub -Method GET -Uri "$api/repos/$Owner/$Repo/git/trees/$baseTree`?recursive=1"
    $localPaths = @{}
    foreach ($it in $items) { $localPaths[$it.Path.ToLowerInvariant()] = $true }
    foreach ($e in $remote.tree) {
        if ($e.type -ne 'blob') { continue }
        if (-not $localPaths.ContainsKey($e.path.ToLowerInvariant())) { $removals += $e.path }
    }
}
foreach ($d in $Delete) { if ($d) { $removals += $d } }
$removals = $removals | Sort-Object -Unique
if ($removals.Count -gt 0) {
    Write-Host ("deleting   : {0} path(s)" -f $removals.Count)
    foreach ($r in $removals) { Write-Host ("  - {0}" -f $r) }
    foreach ($r in $removals) { $tree += @{ path = $r; mode = '100644'; type = 'blob'; sha = $null } }
}

$treeBody = @{ tree = $tree }
if ($baseTree) { $treeBody['base_tree'] = $baseTree }
$newTree = Invoke-GitHub -Method POST -Uri "$api/repos/$Owner/$Repo/git/trees" -Body $treeBody

if (-not $Message) {
    $Message = "Publish HPD-20 Companion source`n`nGenerated by tools/publish.ps1."
}
$commitBody = @{ message = $Message; tree = $newTree.sha }
if ($headSha) { $commitBody['parents'] = @($headSha) }
$commit = Invoke-GitHub -Method POST -Uri "$api/repos/$Owner/$Repo/git/commits" -Body $commitBody

if ($headSha) {
    Invoke-GitHub -Method PATCH -Uri "$api/repos/$Owner/$Repo/git/refs/heads/$Branch" -Body @{ sha = $commit.sha; force = $false } | Out-Null
} else {
    Invoke-GitHub -Method POST -Uri "$api/repos/$Owner/$Repo/git/refs" -Body @{ ref = "refs/heads/$Branch"; sha = $commit.sha } | Out-Null
}
Write-Host ("commit     : {0} on {1}" -f $commit.sha.Substring(0, 7), $Branch)

# ---------------------------------------------------------------- release
if ($Tag) {
    if (-not $Notes) {
        $Notes = "Source and executable for this build.`n`nRun ``dist\HPD20Companion.exe``; see README.md for details."
    }
    $release = $null
    try { $release = Invoke-GitHub -Method GET -Uri "$api/repos/$Owner/$Repo/releases/tags/$Tag" }
    catch {
        $release = Invoke-GitHub -Method POST -Uri "$api/repos/$Owner/$Repo/releases" -Body @{
            tag_name = $Tag; target_commitish = $Branch; name = "HPD-20 Companion $Tag"; body = $Notes; draft = $false; prerelease = $false
        }
    }
    Write-Host ("release    : {0}" -f $release.html_url)

    if ($Exe) {
        $exePath = if ([System.IO.Path]::IsPathRooted($Exe)) { $Exe } else { Join-Path $root $Exe }
        if (-not (Test-Path $exePath)) { throw "executable not found: $exePath" }
        $name = [System.IO.Path]::GetFileName($exePath)
        $uploadUri = ("https://uploads.github.com/repos/{0}/{1}/releases/{2}/assets?name={3}" -f $Owner, $Repo, $release.id, $name)
        $bytes = [System.IO.File]::ReadAllBytes($exePath)
        $asset = Invoke-GitHub -Method POST -Uri $uploadUri -Body $bytes -ContentType 'application/octet-stream'
        Write-Host ("asset      : {0} ({1:N0} bytes) {2}" -f $asset.name, $asset.size, $asset.browser_download_url)
    }
}

Write-Host ''
Write-Host ("published  : https://github.com/{0}/{1}" -f $Owner, $Repo)
