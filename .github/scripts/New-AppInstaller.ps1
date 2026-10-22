param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [ValidateSet('x64', 'arm64')][string]$Architecture,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "Expected an owner/repository name, got '$Repository'."
}

[xml]$manifest = Get-Content -LiteralPath $ManifestPath -Raw
$identity = $manifest.Package.Identity
if (-not $identity.Name -or -not $identity.Publisher -or
    $identity.Version -notmatch '^\d+\.\d+\.\d+\.0$') {
    throw 'The package manifest must contain a name, publisher, and release version major.minor.build.0.'
}
$version = [version]$identity.Version
if ($version.Major -gt 65535 -or $version.Minor -gt 65535 -or $version.Build -gt 65535) {
    throw 'MSIX version components must not exceed 65535.'
}
$releaseVersion = "$($version.Major).$($version.Minor).$($version.Build)"
$releaseTag = [uri]::EscapeDataString("release/v$releaseVersion")
$baseUri = "https://github.com/$Repository/releases"
$namespace = 'http://schemas.microsoft.com/appx/appinstaller/2021'
$document = [xml]::new()
[void]$document.AppendChild($document.CreateXmlDeclaration('1.0', 'utf-8', $null))
$installer = $document.CreateElement('AppInstaller', $namespace)
$installer.SetAttribute('Version', $identity.Version)
$feedName = if ($Architecture) { "GitHubToDo_$Architecture.appinstaller" } else { 'GitHubToDo.appinstaller' }
$installer.SetAttribute('Uri', "$baseUri/latest/download/$feedName")
[void]$document.AppendChild($installer)

$package = $document.CreateElement('MainBundle', $namespace)
$package.SetAttribute('Name', $identity.Name)
$package.SetAttribute('Publisher', $identity.Publisher)
$package.SetAttribute('Version', $identity.Version)
$package.SetAttribute('Uri', "$baseUri/download/$releaseTag/GitHubToDo_$releaseVersion.msixbundle")
[void]$installer.AppendChild($package)

$settings = $document.CreateElement('UpdateSettings', $namespace)
$onLaunch = $document.CreateElement('OnLaunch', $namespace)
$onLaunch.SetAttribute('HoursBetweenUpdateChecks', '0')
$onLaunch.SetAttribute('ShowPrompt', 'false')
$onLaunch.SetAttribute('UpdateBlocksActivation', 'false')
[void]$settings.AppendChild($onLaunch)
[void]$settings.AppendChild($document.CreateElement('AutomaticBackgroundTask', $namespace))
[void]$installer.AppendChild($settings)

$output = [IO.Path]::GetFullPath($OutputPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
$document.Save($output)
Write-Host "Created $output"
