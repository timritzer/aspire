#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [string]$Repository = 'microsoft/aspire-skills'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$scriptDir = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..\..')).Path
$embeddedDir = Join-Path $repoRoot 'src\Aspire.Cli\Agents\AspireSkills\Embedded'
$hooksDir = Join-Path $repoRoot 'src\Aspire.Cli\Agents\Hooks'
$installerPath = Join-Path $repoRoot 'src\Aspire.Cli\Agents\AspireSkills\AspireSkillsInstaller.cs'

. (Join-Path $scriptDir 'aspire-skills-bundles.common.ps1')

$bundleDefinitions = @(
    [pscustomobject]@{
        DisplayName = 'Aspire skills'
        MetadataFileName = 'aspire-skills.metadata.json'
        IncludesHooks = $true
    },
    [pscustomobject]@{
        DisplayName = 'Aspire extensions'
        MetadataFileName = 'aspire-extensions.metadata.json'
        IncludesHooks = $false
    }
)

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "The GitHub CLI ('gh') is required to verify the embedded Aspire Skills bundles."
}

function Test-EmbeddedBundle {
    param([Parameter(Mandatory = $true)]$Definition)

    $metadataPath = Join-Path $embeddedDir $Definition.MetadataFileName
    if (-not (Test-Path $metadataPath)) {
        throw "Embedded $($Definition.DisplayName) metadata was not found at '$metadataPath'."
    }

    $metadata = Get-Content -Raw -Path $metadataPath | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($metadata.version)) {
        throw "Embedded $($Definition.DisplayName) metadata must specify a version."
    }
    if ($metadata.repository -ne $Repository) {
        throw "Unexpected embedded $($Definition.DisplayName) repository '$($metadata.repository)'. Expected '$Repository'."
    }
    if ([string]::IsNullOrWhiteSpace($metadata.tag)) {
        throw "Embedded $($Definition.DisplayName) metadata must specify a GitHub release tag."
    }
    if ([string]::IsNullOrWhiteSpace($metadata.assetName)) {
        throw "Embedded $($Definition.DisplayName) metadata must specify a release asset name."
    }
    if ($metadata.assetName -ne [System.IO.Path]::GetFileName($metadata.assetName)) {
        throw "Embedded $($Definition.DisplayName) asset name '$($metadata.assetName)' must not contain path separators."
    }
    if ([string]::IsNullOrWhiteSpace($metadata.sha512)) {
        throw "Embedded $($Definition.DisplayName) metadata must specify the release asset SHA-512 hash."
    }

    $archivePath = Join-Path $embeddedDir $metadata.assetName
    if (-not (Test-Path $archivePath)) {
        throw "Embedded $($Definition.DisplayName) archive was not found at '$archivePath'."
    }

    $actualHash = (Get-FileHash -Algorithm SHA512 $archivePath).Hash.ToLowerInvariant()
    if ($actualHash -ne $metadata.sha512) {
        throw "Embedded $($Definition.DisplayName) bundle SHA-512 mismatch. Expected '$($metadata.sha512)', got '$actualHash'."
    }

    $certIdentity = "https://github.com/$($metadata.repository)/.github/workflows/publish.yml@refs/tags/$($metadata.tag)"
    & gh attestation verify $archivePath `
        --repo $metadata.repository `
        --cert-identity $certIdentity `
        --cert-oidc-issuer 'https://token.actions.githubusercontent.com' | Out-Host
    # Explicitly fail on a non-zero exit. This is the security-critical gate, and the native
    # command error-action auto-throw is not honored on older hosts (Windows PowerShell 5.1), where
    # a failed or abstained attestation would otherwise fall through and be reported as verified.
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub artifact attestation verification failed for '$archivePath' (exit code $LASTEXITCODE)."
    }

    Write-Host "Embedded $($Definition.DisplayName) bundle '$($metadata.assetName)' verified against GitHub artifact attestation."
    return $metadata
}

$verifiedMetadata = @{}
foreach ($definition in $bundleDefinitions) {
    $verifiedMetadata[$definition.MetadataFileName] = Test-EmbeddedBundle $definition
}

$installerContent = Get-Content -Raw -Path $installerPath
$versionMatches = [regex]::Matches($installerContent, 'internal const string Version = "([^"]+)";')
if ($versionMatches.Count -ne 1) {
    throw "Expected exactly one Aspire Skills bundle version constant in '$installerPath', but found $($versionMatches.Count)."
}
$expectedVersion = $versionMatches[0].Groups[1].Value
$embeddedVersions = @($verifiedMetadata.Values | ForEach-Object version | Select-Object -Unique)
if ($embeddedVersions.Count -ne 1 -or $embeddedVersions[0] -ne $expectedVersion) {
    throw "Embedded Aspire Skills bundle versions '$($embeddedVersions -join ', ')' must match AspireSkillsInstaller.Version '$expectedVersion'."
}

$skillsDefinitions = @($bundleDefinitions | Where-Object IncludesHooks)
if ($skillsDefinitions.Count -ne 1) {
    throw "Expected exactly one bundle definition to include telemetry hooks, but found $($skillsDefinitions.Count)."
}
$skillsDefinition = $skillsDefinitions[0]
$skillsMetadata = $verifiedMetadata[$skillsDefinition.MetadataFileName]

# Verify the embedded telemetry hook scripts when the bundle records them. The hooks block is only
# present once update-aspire-skills-bundles.ps1 has synced hooks from a release that contains them, so
# older bundles (which predate the feature) skip this check. When present, cross-check both that the
# embedded file matches the recorded hash AND that the recorded hash matches the canonical source at
# the pinned aspire-skills commit, so a hand-edit that also updates the metadata hash cannot pass.
# Hook scripts are sourced alongside the skills bundle but are not part of the extensions payload.
if ($skillsMetadata.PSObject.Properties.Name -contains 'hooks') {
    $hooks = $skillsMetadata.hooks

    if ([string]::IsNullOrWhiteSpace($hooks.commitSha)) {
        throw "Embedded Aspire Skills metadata 'hooks' block must specify the aspire-skills commit SHA the hooks were pinned to."
    }
    if (-not ($hooks.PSObject.Properties.Name -contains 'files')) {
        throw "Embedded Aspire Skills metadata 'hooks' block must record a 'files' map of hook hashes."
    }

    foreach ($hookFileName in Get-AspireSkillsHookFileNames) {
        if (-not ($hooks.files.PSObject.Properties.Name -contains $hookFileName)) {
            throw "Embedded Aspire Skills metadata 'hooks' block is missing a recorded hash for '$hookFileName'."
        }

        $recordedHash = $hooks.files.$hookFileName
        $embeddedHookPath = Join-Path $hooksDir $hookFileName
        if (-not (Test-Path $embeddedHookPath)) {
            throw "Embedded telemetry hook script was not found at '$embeddedHookPath'."
        }

        # Hash over LF-normalized bytes so .ps1 (text=auto) checked out with CRLF on Windows matches.
        $embeddedHash = Get-AspireSkillsSha512Hex -Bytes (ConvertTo-LfUtf8Bytes -Bytes ([System.IO.File]::ReadAllBytes($embeddedHookPath)))
        if ($embeddedHash -ne $recordedHash) {
            throw "Embedded telemetry hook '$hookFileName' SHA-512 mismatch. Expected '$recordedHash', got '$embeddedHash'. Re-run update-aspire-skills-bundles.ps1."
        }

        $sourceHash = Get-AspireSkillsSha512Hex -Bytes (Get-AspireSkillsHookContent -Repository $skillsMetadata.repository -CommitSha $hooks.commitSha -FileName $hookFileName)
        if ($sourceHash -ne $recordedHash) {
            throw "Telemetry hook '$hookFileName' does not match '$($skillsMetadata.repository)' at commit '$($hooks.commitSha)'. Expected '$recordedHash', got '$sourceHash'."
        }
    }

    Write-Host "Embedded telemetry hook scripts verified against '$($skillsMetadata.repository)' at commit '$($hooks.commitSha)'."
}
