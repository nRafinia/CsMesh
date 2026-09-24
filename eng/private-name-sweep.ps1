<#
.SYNOPSIS
    Sweeps this repository's tracked files and a commit series for names that belong to the
    private test solutions, so none leaks into anything tracked or released.

.DESCRIPTION
    The pattern follows AGENTS.md:

      * compound PascalCase type and interface names (at least two capitalized words) from the
        private solutions' indexes, matched as whole identifier tokens;
      * minus every name, or dotted component of a name, this repository declares when indexed
        with --all, every external type name the private solutions use, every package identifier
        they restore, and every public type or member name of the shared framework and of those
        packages, taken from the reference-pack and package XML documentation;
      * plus the literal list from AGENTS.private.md or -Literal.

    Member names are not terms on their own: a private method or property named after a framework
    or package member (SearchOption, AddAsync, ...) is a public name, and the subtraction above is
    what separates those from a name that is actually the solution's own.

    Private roots and literals come from -PrivateRoot / -Literal or from AGENTS.private.md
    (lines "root: ..." and "literal: ..."; any other non-comment line is taken as a literal).
    No private term is stored in this file and none is printed: the output reports only distinct
    counts and, on a hit, a file:line or a commit hash.

    Exit 0 when nothing hit, 1 when anything hit, 2 on a usage problem.

.PARAMETER Base
    The series base revision, e.g. v0.7.0. The series swept is <Base>..HEAD.

.PARAMETER Repo
    The repository root. Defaults to the current directory.

.PARAMETER CsMesh
    The csmesh executable used to build the private indexes. Defaults to "csmesh".

.PARAMETER AgentsPrivate
    The private measurements file, relative to -Repo. Read when present.

.PARAMETER PrivateRoot
    One or more private solution roots to index and draw names from.

.PARAMETER Literal
    Additional literal terms to sweep for.

.PARAMETER NoIndex
    Skip the indexing step and use whatever graphs already exist.

.EXAMPLE
    ./eng/private-name-sweep.ps1 -Base v0.7.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Base,
    [string]$Repo = (Get-Location).Path,
    [string]$CsMesh = 'csmesh',
    [string]$AgentsPrivate = 'AGENTS.private.md',
    [string[]]$PrivateRoot = @(),
    [string[]]$Literal = @(),
    [switch]$NoIndex
)

$ErrorActionPreference = 'Stop'

function Get-PropertyValue($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $wanted = $Name.Replace('_', '').ToLowerInvariant()
    foreach ($property in $Object.PSObject.Properties) {
        if ($property.Name.Replace('_', '').ToLowerInvariant() -eq $wanted) { return $property.Value }
    }
    return $null
}

function Get-IdentifierComponents([string]$Name) {
    if ([string]::IsNullOrEmpty($Name)) { return @() }
    return @($Name -split '[^A-Za-z0-9_]' | Where-Object { $_ -match '[A-Za-z]' })
}

# At least two capitalized words: a leading capital, a lower-case body, then another capital.
$compound = [regex]'^I?[A-Z][a-z0-9]+[A-Z]'

$privateFile = Join-Path $Repo $AgentsPrivate
if (Test-Path -LiteralPath $privateFile) {
    foreach ($line in (Get-Content -LiteralPath $privateFile)) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) { continue }
        if ($trimmed -match '^(?i)root\s*[:=]\s*(.+)$') { $PrivateRoot += $Matches[1].Trim() }
        elseif ($trimmed -match '^(?i)literal\s*[:=]\s*(.+)$') { $Literal += $Matches[1].Trim() }
        else { $Literal += $trimmed }
    }
}

if (($PrivateRoot.Count + $Literal.Count) -eq 0) {
    [Console]::Error.WriteLine('no private roots or literals: pass -PrivateRoot/-Literal or write AGENTS.private.md')
    exit 2
}

function Update-Index([string]$Root) {
    if ($NoIndex) { return }
    Push-Location -LiteralPath $Root
    try { & $CsMesh index --all --no-telemetry | Out-Null }
    finally { Pop-Location }
}

function Read-Graph([string]$Root) {
    $path = Join-Path $Root '.csmesh\graph.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

$terms = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
$declared = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
$packageDirectories = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)

# The private solutions: their own short names are terms; the external names they use are not.
foreach ($root in $PrivateRoot) {
    Update-Index $root
    $graph = Read-Graph $root
    if ($null -eq $graph) { continue }

    foreach ($node in @(Get-PropertyValue $graph 'nodes')) {
        $kind = [string](Get-PropertyValue $node 'kind')
        if ($kind -ne 'type' -and $kind -ne 'interface') { continue }

        foreach ($component in (Get-IdentifierComponents ([string](Get-PropertyValue $node 'short')))) {
            if ($compound.IsMatch($component)) { [void]$terms.Add($component) }
        }
    }

    foreach ($external in @(Get-PropertyValue $graph 'external_types')) {
        foreach ($component in (Get-IdentifierComponents ([string](Get-PropertyValue $external 'name')))) {
            [void]$declared.Add($component)
        }
    }

    # Package identifiers the solution restores: a namespace a private class happens to share with a
    # package is a public name, not a leak.
    foreach ($assetsFile in (Get-ChildItem -LiteralPath $root -Recurse -File -Filter 'project.assets.json' -ErrorAction SilentlyContinue)) {
        if ($assetsFile.FullName -notmatch '\\obj\\') { continue }

        $assets = $null
        try { $assets = Get-Content -LiteralPath $assetsFile.FullName -Raw | ConvertFrom-Json } catch { continue }
        $libraries = Get-PropertyValue $assets 'libraries'
        if ($null -eq $libraries) { continue }

        $folders = @()
        $packageFolders = Get-PropertyValue $assets 'packageFolders'
        if ($null -ne $packageFolders) { foreach ($property in $packageFolders.PSObject.Properties) { $folders += $property.Name } }

        foreach ($library in $libraries.PSObject.Properties) {
            $identifier = ($library.Name -split '/')[0]
            foreach ($component in ($identifier -split '\.')) { [void]$declared.Add($component) }

            $relative = [string](Get-PropertyValue $library.Value 'path')
            if ($relative.Length -eq 0) { continue }
            foreach ($folder in $folders) {
                [void]$packageDirectories.Add((Join-Path $folder ($relative -replace '/', '\')))
            }
        }
    }
}

# The public surface of every package the private solutions restore, from the XML documentation the
# packages ship beside their assemblies: a private name that is also a package member is public.
foreach ($packageDirectory in $packageDirectories) {
    if (-not (Test-Path -LiteralPath $packageDirectory)) { continue }

    foreach ($xml in (Get-ChildItem -LiteralPath $packageDirectory -Recurse -File -Filter '*.xml' -ErrorAction SilentlyContinue)) {
        $text = Get-Content -LiteralPath $xml.FullName -Raw -ErrorAction SilentlyContinue
        if ($null -eq $text) { continue }

        foreach ($match in [regex]::Matches($text, 'name="[TMPFE]:([^"(]+)')) {
            $full = $match.Groups[1].Value
            $simple = $full.Substring($full.LastIndexOf('.') + 1)
            $tick = $simple.IndexOf('`')
            if ($tick -ge 0) { $simple = $simple.Substring(0, $tick) }
            if ($simple.Length -gt 0) { [void]$declared.Add($simple) }
        }
    }
}

# The shared framework's public surface, parsed from the reference-pack documentation: a private name
# that is also a framework type or member name is a public name, not a leak.
$packsRoot = Join-Path $env:ProgramFiles 'dotnet\packs'
if (Test-Path -LiteralPath $packsRoot) {
    foreach ($xml in (Get-ChildItem -LiteralPath $packsRoot -Recurse -File -Filter '*.xml' -ErrorAction SilentlyContinue)) {
        if ($xml.FullName -notmatch '\\ref\\') { continue }

        $text = Get-Content -LiteralPath $xml.FullName -Raw -ErrorAction SilentlyContinue
        if ($null -eq $text) { continue }

        foreach ($match in [regex]::Matches($text, 'name="[TMPFE]:([^"(]+)')) {
            $full = $match.Groups[1].Value
            $simple = $full.Substring($full.LastIndexOf('.') + 1)
            $tick = $simple.IndexOf('`')
            if ($tick -ge 0) { $simple = $simple.Substring(0, $tick) }
            if ($simple.Length -gt 0) { [void]$declared.Add($simple) }
        }
    }
}

# This repository's own declarations, from an --all index, are not leaks either.
Update-Index $Repo
$repoGraph = Read-Graph $Repo
if ($null -ne $repoGraph) {
    foreach ($node in @(Get-PropertyValue $repoGraph 'nodes')) {
        [void]$declared.Add([string](Get-PropertyValue $node 'short'))
        foreach ($component in (Get-IdentifierComponents ([string](Get-PropertyValue $node 'name')))) {
            [void]$declared.Add($component)
        }
    }
}

foreach ($literal in $Literal) { [void]$terms.Add($literal) }
if ($declared.Count -gt 0) { $terms.ExceptWith($declared) }

if ($terms.Count -eq 0) {
    Write-Output 'pattern terms: 0 (every candidate was declared by this repository or a referenced assembly)'
    exit 0
}

$alternation = ($terms | ForEach-Object { [regex]::Escape($_) }) -join '|'
$rx = [regex]('\b(?:' + $alternation + ')\b')

# Every tracked file.
$tracked = @(& git -C $Repo ls-files)
$hitFiles = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
$fileHits = 0
foreach ($relative in $tracked) {
    $full = Join-Path $Repo $relative
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }

    $lines = @(Get-Content -LiteralPath $full -ErrorAction SilentlyContinue)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($rx.IsMatch($lines[$i])) {
            $fileHits++
            [void]$hitFiles.Add($relative)
            Write-Output ("hit: {0}:{1}" -f $relative, ($i + 1))
        }
    }
}

# The series' commit messages and author fields.
$raw = ((& git -C $Repo log --format="%H%x1f%an%x1f%ae%x1f%s%x1f%b%x1e" "$Base..HEAD") -join "`n")
$records = @($raw -split [char]0x1e | Where-Object { $_.Trim().Length -gt 0 })
$hitCommits = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
foreach ($record in $records) {
    if (-not $rx.IsMatch($record)) { continue }
    $hash = ($record -split [char]0x1f)[0].Trim()
    [void]$hitCommits.Add($hash)
    Write-Output ("hit commit: {0}" -f $hash.Substring(0, [Math]::Min(12, $hash.Length)))
}

$hits = $fileHits + $hitCommits.Count
Write-Output ("pattern terms: {0}" -f $terms.Count)
Write-Output ("tracked files: {0}" -f $tracked.Count)
Write-Output ("series commits: {0}" -f $records.Count)
Write-Output ("hit files: {0}" -f $hitFiles.Count)
Write-Output ("hit commits: {0}" -f $hitCommits.Count)
Write-Output ("hits: {0}" -f $hits)

if ($hits -eq 0) { exit 0 }
exit 1
