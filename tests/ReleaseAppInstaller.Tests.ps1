$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$generator = Join-Path $root '.github\scripts\New-AppInstaller.ps1'
$verifier = Join-Path $root '.github\scripts\Test-AppInstallerPackage.ps1'
$directory = Join-Path ([IO.Path]::GetTempPath()) "GitHubToDo-AppInstaller-$([guid]::NewGuid().ToString('N'))"
[void][IO.Directory]::CreateDirectory($directory)

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Fails([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    Assert-True $failed $Message
}

try {
    $workflow = Get-Content (Join-Path $root '.github\workflows\release.yml') -Raw
    Assert-True ($workflow -match "(?m)^\s+- 'release/v\*'\r?$") 'Releases must be triggered by release/v tags.'
    Assert-True ($workflow -notmatch 'workflow_dispatch|inputs\.') 'Releases must not depend on manual inputs.'
    Assert-True ($workflow -match '(?m)^    environment: release\r?$') 'Signing must use the release OIDC environment.'
    $validation = [regex]::Match($workflow,
        '(?ms)- name: Validate release tag and configuration.*?        run: \|\r?\n(?<body>.*?)(?=\r?\n      -)')
    Assert-True $validation.Success 'The release validation step must exist.'
    $validationPath = Join-Path $directory 'Validate-Release.ps1'
    $validationBody = $validation.Groups['body'].Value -replace '(?m)^          ', ''
    [IO.File]::WriteAllText($validationPath,
        "function gh { `$global:LASTEXITCODE = 1 }`n" + $validationBody)
    $powershell = (Get-Process -Id $PID).Path
    $variableNames = @('GITHUB_REF_NAME','GITHUB_ENV','GITHUBTODO_CLIENT_ID','MSIX_PUBLISHER',
        'AZURE_SIGNING_ENDPOINT','AZURE_SIGNING_ACCOUNT','AZURE_SIGNING_PROFILE')
    $saved = @{}
    $savedExitCode = $global:LASTEXITCODE
    try {
        foreach ($name in $variableNames) {
            $saved[$name] = [Environment]::GetEnvironmentVariable($name)
            [Environment]::SetEnvironmentVariable($name, 'test')
        }
        $env:GITHUB_ENV = Join-Path $directory 'github-env'
        foreach ($tag in 'release/v1.0.0','release/v65535.65535.65535') {
            $env:GITHUB_REF_NAME = $tag
            & $powershell -NoProfile -File $validationPath *> $null
            Assert-True ($LASTEXITCODE -eq 0) "Must accept valid tag $tag."
            Assert-True ((Get-Content $env:GITHUB_ENV) -contains "VERSION=$($tag.Substring(9))") 'Must export the version derived from the tag.'
        }
        foreach ($tag in 'v1.0.0','release/v01.0.0','release/v65536.0.0','release/v1.0',
            'release/v1.0.0.0','release/v1.0.0-beta','release/v1.0.0/extra') {
            $env:GITHUB_REF_NAME = $tag
            & $powershell -NoProfile -File $validationPath *> $null
            Assert-True ($LASTEXITCODE -ne 0) "Must reject invalid tag $tag."
        }
    } finally {
        foreach ($name in $variableNames) {
            [Environment]::SetEnvironmentVariable($name, $saved[$name])
        }
        $global:LASTEXITCODE = $savedExitCode
    }

    $manifestPath = Join-Path $directory 'Package.appxmanifest'
    [xml]$manifest = Get-Content (Join-Path $root 'src\GitHubToDo\Package.appxmanifest') -Raw
    $manifest.Package.Identity.Version = '1.2.3.0'
    $publisher = 'CN=Test & Release, O="Example"'
    $manifest.Package.Identity.Publisher = $publisher
    $namespace = 'http://schemas.microsoft.com/appx/manifest/uap/windows10/13'
    $manifest.Package.SetAttribute('xmlns:uap13', $namespace)
    foreach ($existing in @($manifest.Package.Properties.ChildNodes | Where-Object LocalName -eq 'AutoUpdate')) {
        [void]$manifest.Package.Properties.RemoveChild($existing)
    }
    $autoUpdate = $manifest.CreateElement('uap13', 'AutoUpdate', $namespace)
    $reference = $manifest.CreateElement('uap13', 'AppInstaller', $namespace)
    $reference.SetAttribute('File', 'Update.appinstaller')
    [void]$autoUpdate.AppendChild($reference)
    [void]$manifest.Package.Properties.AppendChild($autoUpdate)
    $manifest.Save($manifestPath)

    foreach ($architecture in '', 'x64', 'arm64') {
        $feedName = if ($architecture) { "GitHubToDo_$architecture.appinstaller" } else { 'GitHubToDo.appinstaller' }
        $output = Join-Path $directory $feedName
        $arguments = @{ ManifestPath = $manifestPath; Repository = 'dotMorten/GitHubToDo'; OutputPath = $output }
        if ($architecture) { $arguments.Architecture = $architecture }
        & $generator @arguments
        [xml]$document = Get-Content -LiteralPath $output -Raw
        $installer = $document.AppInstaller
        $main = $installer.MainBundle
        Assert-True ($installer.NamespaceURI -ceq 'http://schemas.microsoft.com/appx/appinstaller/2021') 'Wrong App Installer schema.'
        Assert-True ($installer.Version -ceq '1.2.3.0') 'Wrong feed version.'
        Assert-True ($installer.Uri -ceq "https://github.com/dotMorten/GitHubToDo/releases/latest/download/$feedName") 'Feed URI must be stable across versions, including legacy URLs.'
        Assert-True ($main.Uri -ceq 'https://github.com/dotMorten/GitHubToDo/releases/download/release%2Fv1.2.3/GitHubToDo_1.2.3.msixbundle') 'Bundle URI must be pinned to the URL-escaped release tag.'
        Assert-True ($main.Name -ceq $manifest.Package.Identity.Name) 'Wrong package name.'
        Assert-True ($main.Publisher -ceq $publisher) 'Publisher XML escaping did not round-trip.'
        Assert-True ($main.Version -ceq '1.2.3.0') 'Wrong package version.'
        Assert-True (-not $main.HasAttribute('ProcessorArchitecture')) 'A bundle feed must not select an architecture.'
        Assert-True ($null -eq $installer.MainPackage) 'Must describe a bundle, not an individual package.'
        Assert-True ($installer.UpdateSettings.OnLaunch.HoursBetweenUpdateChecks -ceq '0') 'Must check on every launch.'
        Assert-True ($installer.UpdateSettings.OnLaunch.ShowPrompt -ceq 'false') 'Updates should be silent.'
        Assert-True ($installer.UpdateSettings.OnLaunch.UpdateBlocksActivation -ceq 'false') 'Updates must not block activation.'
        Assert-True ($null -ne $installer.UpdateSettings.SelectSingleNode("*[local-name()='AutomaticBackgroundTask']")) 'Background updates must be enabled.'
        Assert-True ($null -eq $installer.UpdateSettings.SelectSingleNode("*[local-name()='ForceUpdateFromAnyVersion']")) 'Downgrades must not be enabled.'

        $manifest.Package.Identity.Version = '1.2.4.0'
        $manifest.Save($manifestPath)
        & $generator @arguments
        [xml]$next = Get-Content -LiteralPath $output -Raw
        Assert-True ($next.AppInstaller.Uri -ceq $installer.Uri) 'The next release must keep the same update feed URL.'
        Assert-True ($next.AppInstaller.MainBundle.Uri -ceq 'https://github.com/dotMorten/GitHubToDo/releases/download/release%2Fv1.2.4/GitHubToDo_1.2.4.msixbundle') 'The next release must point to the new bundle.'
        $manifest.Package.Identity.Version = '1.2.3.0'
        $manifest.Save($manifestPath)
    }

    $output = Join-Path $directory 'GitHubToDo.appinstaller'
    & $generator -ManifestPath $manifestPath -Repository 'dotMorten/GitHubToDo' -OutputPath $output
    [xml]$feed = Get-Content $output -Raw
    [xml]$bundleManifest = '<Bundle xmlns="http://schemas.microsoft.com/appx/2013/bundle"><Identity Name="" Publisher="" Version=""/><Packages/></Bundle>'
    foreach ($attribute in 'Name','Publisher','Version') {
        $bundleManifest.Bundle.Identity.SetAttribute($attribute, $manifest.Package.Identity.GetAttribute($attribute))
    }
    foreach ($architecture in 'x64','arm64') {
        $manifest.Package.Identity.SetAttribute('ProcessorArchitecture', $architecture)
        $manifest.Save($manifestPath)
        $innerPath = Join-Path $directory "$architecture.msix"
        $archive = [IO.Compression.ZipFile]::Open($innerPath, [IO.Compression.ZipArchiveMode]::Create)
        try {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $manifestPath, 'AppxManifest.xml')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $output, 'Update.appinstaller')
        } finally { $archive.Dispose() }
        $entry = $bundleManifest.CreateElement('Package', $bundleManifest.Bundle.NamespaceURI)
        $entry.SetAttribute('Type', 'application')
        $entry.SetAttribute('Architecture', $architecture)
        $entry.SetAttribute('Version', '1.2.3.0')
        $entry.SetAttribute('FileName', "$architecture.msix")
        [void]$bundleManifest.Bundle.SelectSingleNode("*[local-name()='Packages']").AppendChild($entry)
    }
    $bundleManifestPath = Join-Path $directory 'AppxBundleManifest.xml'
    function New-TestBundle {
        $bundleManifest.Save($bundleManifestPath)
        $path = Join-Path $directory 'GitHubToDo_1.2.3.msixbundle'
        if (Test-Path $path) { Remove-Item -LiteralPath $path }
        $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
        try {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $bundleManifestPath, 'AppxMetadata/AppxBundleManifest.xml')
            foreach ($architecture in 'x64','arm64') {
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $directory "$architecture.msix"), "$architecture.msix")
            }
        } finally { $archive.Dispose() }
        return $path
    }
    $bundlePath = New-TestBundle
    & $verifier -PackagePath $bundlePath -AppInstallerPath $output
    $feed.AppInstaller.MainBundle.SetAttribute('Version', '1.2.4.0')
    $feed.Save($output)
    Assert-Fails { & $verifier -PackagePath $bundlePath -AppInstallerPath $output } 'Must reject mismatched bundle/feed versions.'
    & $generator -ManifestPath $manifestPath -Repository 'dotMorten/GitHubToDo' -OutputPath $output
    [xml]$feed = Get-Content $output -Raw
    $feed.AppInstaller.UpdateSettings.OnLaunch.SetAttribute('HoursBetweenUpdateChecks', '1')
    $feed.Save($output)
    Assert-Fails { & $verifier -PackagePath $bundlePath -AppInstallerPath $output } 'Must reject mismatched embedded/published feeds.'
    & $generator -ManifestPath $manifestPath -Repository 'dotMorten/GitHubToDo' -OutputPath $output
    $second = $bundleManifest.Bundle.Packages.Package[1]
    $second.SetAttribute('Architecture', 'x64')
    $bundlePath = New-TestBundle
    Assert-Fails { & $verifier -PackagePath $bundlePath -AppInstallerPath $output } 'Must reject duplicate architectures.'
    $second.SetAttribute('Architecture', 'arm64')
    $second.SetAttribute('Version', '1.2.4.0')
    $bundlePath = New-TestBundle
    Assert-Fails { & $verifier -PackagePath $bundlePath -AppInstallerPath $output } 'Must reject mismatched bundled package versions.'
    $second.SetAttribute('Version', '1.2.3.0')
    $second.SetAttribute('FileName', 'missing.msix')
    $bundlePath = New-TestBundle
    Assert-Fails { & $verifier -PackagePath $bundlePath -AppInstallerPath $output } 'Must reject missing package entries.'
    [void]$bundleManifest.Bundle.Packages.RemoveChild($second)
    $bundlePath = New-TestBundle
    Assert-Fails { & $verifier -PackagePath $bundlePath -AppInstallerPath $output } 'Must require both application architectures.'

    $output = Join-Path $directory 'invalid.appinstaller'
    Assert-Fails { & $generator -ManifestPath $manifestPath -Architecture x64 -Repository 'invalid/repo/path' -OutputPath $output } 'Must reject invalid repositories.'
    Assert-Fails { & $generator -ManifestPath $manifestPath -Architecture x86 -Repository 'dotMorten/GitHubToDo' -OutputPath $output } 'Must reject unsupported architectures.'
    foreach ($version in '65536.0.0.0', '1.2.3.4', 'invalid') {
        $manifest.Package.Identity.Version = $version
        $manifest.Save($manifestPath)
        Assert-Fails { & $generator -ManifestPath $manifestPath -Repository 'dotMorten/GitHubToDo' -OutputPath $output } "Must reject version $version."
    }
    Write-Host 'App Installer generation and package verification tests passed.'
} finally {
    Remove-Item -LiteralPath $directory -Recurse -Force
}
