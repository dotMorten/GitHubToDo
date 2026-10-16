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

    foreach ($architecture in 'x64', 'arm64') {
        $output = Join-Path $directory "$architecture\GitHubToDo_$architecture.appinstaller"
        & $generator -ManifestPath $manifestPath -Architecture $architecture -Repository 'dotMorten/GitHubToDo' -OutputPath $output
        [xml]$document = Get-Content -LiteralPath $output -Raw
        $installer = $document.AppInstaller
        $main = $installer.MainPackage
        Assert-True ($installer.NamespaceURI -ceq 'http://schemas.microsoft.com/appx/appinstaller/2021') 'Wrong App Installer schema.'
        Assert-True ($installer.Version -ceq '1.2.3.0') 'Wrong feed version.'
        Assert-True ($installer.Uri -ceq "https://github.com/dotMorten/GitHubToDo/releases/latest/download/GitHubToDo_$architecture.appinstaller") 'Feed URI must be stable across versions.'
        Assert-True ($main.Uri -ceq "https://github.com/dotMorten/GitHubToDo/releases/download/v1.2.3/GitHubToDo_1.2.3_$architecture.msix") 'Package URI must be pinned to this version.'
        Assert-True ($main.Name -ceq $manifest.Package.Identity.Name) 'Wrong package name.'
        Assert-True ($main.Publisher -ceq $publisher) 'Publisher XML escaping did not round-trip.'
        Assert-True ($main.Version -ceq '1.2.3.0') 'Wrong package version.'
        Assert-True ($main.ProcessorArchitecture -ceq $architecture) 'Wrong package architecture.'
        Assert-True ($installer.UpdateSettings.OnLaunch.HoursBetweenUpdateChecks -ceq '0') 'Must check on every launch.'
        Assert-True ($installer.UpdateSettings.OnLaunch.ShowPrompt -ceq 'false') 'Updates should be silent.'
        Assert-True ($installer.UpdateSettings.OnLaunch.UpdateBlocksActivation -ceq 'false') 'Updates must not block activation.'
        Assert-True ($null -ne $installer.UpdateSettings.SelectSingleNode("*[local-name()='AutomaticBackgroundTask']")) 'Background updates must be enabled.'
        Assert-True ($null -eq $installer.UpdateSettings.SelectSingleNode("*[local-name()='ForceUpdateFromAnyVersion']")) 'Downgrades must not be enabled.'

        $manifest.Package.Identity.SetAttribute('ProcessorArchitecture', $architecture)
        $manifest.Save($manifestPath)
        $packagePath = Join-Path $directory "GitHubToDo_1.2.3_$architecture.msix"
        $archive = [IO.Compression.ZipFile]::Open($packagePath, [IO.Compression.ZipArchiveMode]::Create)
        try {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $manifestPath, 'AppxManifest.xml')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $output, 'Update.appinstaller')
        } finally { $archive.Dispose() }
        & $verifier -PackagePath $packagePath -AppInstallerPath $output
        $main.SetAttribute('Version', '1.2.4.0')
        $document.Save($output)
        Assert-Fails { & $verifier -PackagePath $packagePath -AppInstallerPath $output } 'Must reject mismatched embedded/published feeds.'
        $manifest.Package.Identity.Version = '1.2.4.0'
        $manifest.Save($manifestPath)
        & $generator -ManifestPath $manifestPath -Architecture $architecture -Repository 'dotMorten/GitHubToDo' -OutputPath $output
        [xml]$next = Get-Content -LiteralPath $output -Raw
        Assert-True ($next.AppInstaller.Uri -ceq $installer.Uri) 'The next release must keep the same update feed URL.'
        Assert-True ($next.AppInstaller.MainPackage.Uri -ceq "https://github.com/dotMorten/GitHubToDo/releases/download/v1.2.4/GitHubToDo_1.2.4_$architecture.msix") 'The next release must point to the new package.'
        $manifest.Package.Identity.Version = '1.2.3.0'
        $manifest.Save($manifestPath)
    }

    $output = Join-Path $directory 'invalid.appinstaller'
    Assert-Fails { & $generator -ManifestPath $manifestPath -Architecture x64 -Repository 'invalid/repo/path' -OutputPath $output } 'Must reject invalid repositories.'
    Assert-Fails { & $generator -ManifestPath $manifestPath -Architecture x86 -Repository 'dotMorten/GitHubToDo' -OutputPath $output } 'Must reject unsupported architectures.'
    foreach ($version in '65536.0.0.0', '1.2.3.4', 'invalid') {
        $manifest.Package.Identity.Version = $version
        $manifest.Save($manifestPath)
        Assert-Fails { & $generator -ManifestPath $manifestPath -Architecture x64 -Repository 'dotMorten/GitHubToDo' -OutputPath $output } "Must reject version $version."
    }
    Write-Host 'App Installer generation and package verification tests passed.'
} finally {
    Remove-Item -LiteralPath $directory -Recurse -Force
}
