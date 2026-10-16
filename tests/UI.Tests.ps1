param(
    [Parameter(Mandatory)][int]$AppPid,
    [ValidateSet('setup', 'demo', 'resume', 'layout', 'table', 'inbox', 'reading', 'queries', 'builder', 'polling', 'refresh')][string]$Mode = 'demo',
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [int]$ExpectedUnread = 2
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()

function Invoke-Ui([string[]]$Arguments, [string]$Window) {
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $selector = if ($Window) { @('-w', $Window) } else { @('-a', "$AppPid") }
        $output = @(& winapp ui @Arguments @selector 2>&1)
        if ($LASTEXITCODE -eq 0) { return $output }
        $readOrSelection = $Arguments[0] -in @('inspect', 'get-property', 'wait-for') -or
            ($Arguments[0] -eq 'invoke' -and $Arguments -contains '--action' -and $Arguments -contains 'select')
        if ($attempt -eq 0 -and $readOrSelection -and ($output -join "`n") -match '"code"\s*:\s*"stale_element"|no longer accessible') {
            continue
        }
        throw "winapp ui $($Arguments -join ' ') failed: $($output -join "`n")"
    }
}

function Assert-Ui([string]$Name, [scriptblock]$Test) {
    try {
        & $Test | Out-Null
        $results.Add(@{ name = $Name; status = 'PASS' })
    } catch {
        $results.Add(@{ name = $Name; status = 'FAIL'; detail = "$_" })
    }
}

function Get-Nodes($nodes) {
    foreach ($node in $nodes) {
        if ($null -ne $node) {
            $node
            Get-Nodes $node.children
        }
    }
}

function Get-Tree {
    for ($attempt = 1; ; $attempt++) {
        try { $tree = Invoke-Ui @('inspect', '--json', '-d', '8', '--hide-offscreen') | ConvertFrom-Json; break }
        catch { if ($attempt -ge 3 -or "$_" -notmatch 'stale_element') { throw }; Start-Sleep -Milliseconds 500 }
    }
    $nodes = @(Get-Nodes @($tree.windows | ForEach-Object { $_.elements }))
    $nodes
    if ($nodes | Where-Object automationId -EQ 'DetailsPane') {
        $details = Invoke-Ui @('inspect', 'DetailsPane', '--json', '-d', '16') | ConvertFrom-Json
        Get-Nodes @($details.windows | ForEach-Object { $_.elements })
    }
}

function Save-Image([string]$Name) {
    $file = Join-Path $ArtifactDirectory $Name
    if (Test-Path -LiteralPath $file) { throw "Evidence already exists: $file" }
    Invoke-Ui @('screenshot', '--output', $file, '--json') -Window $hwnd | Out-Null
    if (-not (Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).Length -eq 0) {
        throw "Screenshot was not delivered: $file"
    }
    $screenshots.Add($file)
}

function Assert-Counts([int]$Count) {
    $nodes = Get-Tree
    $queries = @($nodes | Where-Object { $_.automationId -like 'Query_*' -and $_.type -eq 'ListItem' })
    if ($queries.Count -lt 3) { throw "Expected at least 3 query items, got $($queries.Count)." }
    foreach ($query in $queries | Select-Object -First 3) {
        if ($query.name -notmatch "\b$Count unread\b") {
            throw "Expected $Count unread in query '$($query.name)'."
        }
        $textId = $query.automationId -replace '^Query_', 'QueryName_'
        $property = Invoke-Ui @('get-property', $textId, '-p', 'FontWeight', '--json') | ConvertFrom-Json
        $weight = [double]$property.properties.FontWeight
        if (($Count -gt 0 -and $weight -lt 600) -or ($Count -eq 0 -and $weight -ge 600)) {
            throw "Incorrect font weight $weight for $Count unread."
        }
    }
}

function Assert-CommentCount([string]$Id, [int]$Count) {
    Invoke-Ui @('wait-for', "Item_$Id", '-p', 'Name', '--value', "$Count unread comments and reviews", '--contains') | Out-Null
}

function Assert-VisibleComment([string]$Text) {
    $pane = Invoke-Ui @('get-property', 'DetailsPane', '-p', 'BoundingRectangle', '--json') | ConvertFrom-Json
    $paragraphs = @(Get-Tree | Where-Object {
        $_.name -like "*$Text*" -and -not $_.isOffscreen -and $_.y -ge $pane.element.y -and
        $_.height -gt 0 -and ($_.y + $_.height) -le ($pane.element.y + $pane.element.height)
    })
    if (-not $paragraphs.Count) { throw "Actual comment body is not fully visible without scrolling: $Text" }
}

function Open-QueryMenu {
    $bar = Invoke-Ui @('inspect', 'QueryActions', '-d', '4', '--json') | ConvertFrom-Json
    $more = @(Get-Nodes @($bar.windows | ForEach-Object { $_.elements }) |
        Where-Object automationId -EQ 'MoreButton')
    if ($more.Count -ne 1) { throw 'Expected one query actions overflow button.' }
    Invoke-Ui @('invoke', $more[0].selector) | Out-Null
}

function Invoke-CtrlClick([string]$Selector) {
    if (-not ('GitHubToDoUiModifiers' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class GitHubToDoUiModifiers {
    [DllImport("user32.dll")]
    public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
'@
    }
    Invoke-Ui @('focus', 'ItemList') | Out-Null
    [GitHubToDoUiModifiers]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
    try { Invoke-Ui @('click', $Selector) | Out-Null }
    finally { [GitHubToDoUiModifiers]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero) }
}

function Get-CustomQuery {
    @(Get-Tree | Where-Object {
        $_.automationId -like 'Query_*' -and $_.type -eq 'ListItem' -and $_.name.StartsWith('UI custom reviews')
    }) | Select-Object -First 1
}

function Assert-NarrowLayout {
    Invoke-Ui @('invoke', 'Item_demo-external-item', '--action', 'select')
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class GitHubToDoUiWindow {
    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
'@
    $hwnd = [IntPtr]($windows | Where-Object { $_.hwnd -and $_.title -eq 'GitHubToDo' } | Select-Object -First 1).hwnd
    try {
        if (-not [GitHubToDoUiWindow]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, 720, 760, 6)) { throw 'Window resize failed.' }
        Invoke-Ui @('wait-for', 'PART_PaneToggleButton')
        Invoke-Ui @('wait-for', 'QueryActions')
        Invoke-Ui @('wait-for', 'ItemList')
        Invoke-Ui @('wait-for', 'SelectedItemReadStatus', '--value', 'Read')
        Save-Image '02-narrow.png'
        Invoke-Ui @('invoke', 'PART_PaneToggleButton')
        Invoke-Ui @('wait-for', 'AddQuerySidebar')
        Invoke-Ui @('invoke', 'NavigationSettings')
        Invoke-Ui @('wait-for', 'IgnoredAccounts')
        Invoke-Ui @('invoke', 'BackToQueries')
        Invoke-Ui @('wait-for', 'SelectedItemReadStatus', '--value', 'Read')
        Open-QueryMenu
        Invoke-Ui @('wait-for', 'AddQueryOverflow')
        Invoke-Ui @('invoke', 'AddQueryOverflow')
        Invoke-Ui @('wait-for', 'QueryName')
        $initial = Get-BuilderInitialField
        Invoke-Ui @('wait-for', $initial, '-p', 'HasKeyboardFocus', '--value', 'True')
        Invoke-Ui @('send-keys', 'esc', '--target', $initial, '--via', 'send-input')
        Invoke-Ui @('wait-for', 'QueryEditor', '--gone')
    } finally {
        [GitHubToDoUiWindow]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, 1200, 800, 6) | Out-Null
    }
}

function Get-BuilderInitialField {
    # With on-device AI the description box is the natural starting point; otherwise the name.
    if (Test-UiElement 'GenerateQuery') { 'QueryDescription' } else { 'QueryName' }
}

function Test-UiElement([string]$Id) {
    try { Invoke-Ui @('wait-for', $Id, '-t', '500') | Out-Null; $true } catch { $false }
}
try {
    New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
    $windows = @(Invoke-Ui @('list-windows', '--json') | ConvertFrom-Json)
    if (-not ($windows | Where-Object { $_.hwnd -and $_.width -gt 0 })) { throw 'No responsive application window.' }
    $hwnd = [string]($windows | Where-Object { $_.hwnd -and $_.title -eq 'GitHubToDo' } | Select-Object -First 1).hwnd
    if (-not $hwnd) { throw 'No GitHubToDo main window.' }
    if ($Mode -ne 'setup') {
        Invoke-Ui @('wait-for', 'DemoBanner', '-p', 'IsOffscreen', '--value', 'False', '-t', '5000') | Out-Null
    }
    if ($Mode -eq 'setup') {
        Assert-Ui 'Unconfigured application explains OAuth setup' {
            Invoke-Ui @('wait-for', 'OAuthClientId', '-t', '5000')
            Invoke-Ui @('wait-for', 'ApplicationStatus', '--value', 'Configure your own GitHub OAuth', '--contains')
        }
        Assert-Ui 'Setup controls are discoverable' {
            Invoke-Ui @('wait-for', 'PrivateScope')
            Invoke-Ui @('wait-for', 'SaveConfiguration')
            Invoke-Ui @('wait-for', 'SignIn')
        }
        Assert-Ui 'Setup screenshot' { Save-Image 'setup.png' }
    } elseif ($Mode -in @('polling', 'refresh')) {
        Assert-Ui 'Application displays the saved query instead of refresh diagnostics' {
            Invoke-Ui @('wait-for', 'ApplicationStatus', '--value', 'Checked at', '--contains', '-t', '10000')
            Invoke-Ui @('wait-for', 'RefreshProgress', '--gone')
            Invoke-Ui @('wait-for', 'QuerySearchExpression', '--value', 'state:open archived:false involves:@me')
            Invoke-Ui @('wait-for', 'QueryCoverageStatus', '--gone')
        }
        if ($Mode -eq 'polling') {
            Assert-Ui 'The real two-minute timer refreshes without changing the displayed query' {
                $previous = (Invoke-Ui @('get-property', 'ApplicationStatus', '-p', 'Name', '--json') | ConvertFrom-Json).properties.Name
                $deadline = [DateTime]::UtcNow.AddSeconds(140)
                do {
                    Start-Sleep -Seconds 1
                    $current = (Invoke-Ui @('get-property', 'ApplicationStatus', '-p', 'Name', '--json') | ConvertFrom-Json).properties.Name
                } while ($current -eq $previous -and [DateTime]::UtcNow -lt $deadline)
                if ($current -eq $previous) { throw 'The polling timer did not update the application status.' }
                Invoke-Ui @('wait-for', 'ApplicationStatus', '--value', 'Checked at', '--contains', '-t', '10000')
                Invoke-Ui @('wait-for', 'QuerySearchExpression', '--value', 'state:open archived:false involves:@me')
                Save-Image '01-incremental-poll.png'
            }
        }
        Assert-Ui 'Refresh button preserves the displayed query' {
            Invoke-Ui @('invoke', 'Refresh')
            Invoke-Ui @('wait-for', 'QuerySearchExpression', '--value', 'state:open archived:false involves:@me')
            Invoke-Ui @('wait-for', 'ApplicationStatus', '--value', 'Checked at', '--contains', '-t', '10000')
            Invoke-Ui @('wait-for', 'RefreshProgress', '--gone')
            Save-Image '02-manual-incremental-refresh.png'
        }
        Assert-Ui 'Full reconciliation is an explicit separate overflow command' {
            Open-QueryMenu
            Invoke-Ui @('wait-for', 'FullRefresh')
            Save-Image '03-full-reconciliation-command.png'
            Invoke-Ui @('invoke', 'FullRefresh')
            Invoke-Ui @('wait-for', 'QuerySearchExpression', '--value', 'state:open archived:false involves:@me')
            Invoke-Ui @('wait-for', 'ApplicationStatus', '--value', 'Checked at', '--contains', '-t', '10000')
            Invoke-Ui @('wait-for', 'RefreshProgress', '--gone')
            Save-Image '04-explicit-full-refresh.png'
        }
        Assert-Ui 'F5 refresh preserves the displayed query' {
            Invoke-Ui @('send-keys', 'f5', '--target', 'ItemList', '--via', 'send-input')
            Invoke-Ui @('wait-for', 'QuerySearchExpression', '--value', 'state:open archived:false involves:@me')
            Invoke-Ui @('wait-for', 'ApplicationStatus', '--value', 'Checked at', '--contains', '-t', '10000')
            Invoke-Ui @('wait-for', 'RefreshProgress', '--gone')
            Save-Image '05-keyboard-incremental-refresh.png'
        }
    } elseif ($Mode -eq 'queries') {
        Invoke-Ui @('wait-for', 'QueryUnreadCount', '--value', '2 unread', '-t', '10000') | Out-Null
        $queries = @(foreach ($name in @('Issues involving me', 'My pull requests', 'Review requested')) {
            (Invoke-Ui @('get-property', "$name, 2 unread, 0 pending", '-p', 'AutomationId', '--json') | ConvertFrom-Json).element
        })
        if ($queries.Count -ne 3) { throw "Expected three saved demo queries, got $($queries.Count)." }
        Assert-Ui 'Query counts are numeric, right aligned, and on the same compact row' {
            $rightEdges = foreach ($query in $queries) {
                $nameId = $query.automationId -replace '^Query_', 'QueryName_'
                $badgeId = $query.automationId -replace '^Query_', 'QueryBadge_'
                Invoke-Ui @('wait-for', $badgeId, '-p', 'Name', '--value', '2') | Out-Null
                $name = (Invoke-Ui @('get-property', $nameId, '-p', 'BoundingRectangle', '--json') | ConvertFrom-Json).element
                $badge = (Invoke-Ui @('get-property', $badgeId, '-p', 'BoundingRectangle', '--json') | ConvertFrom-Json).element
                if ($badge.x -lt ($name.x + $name.width) -or [Math]::Abs(($name.y + $name.height / 2) - ($badge.y + $badge.height / 2)) -gt 2) {
                    throw 'The unread count must sit to the right of the name on the same line.'
                }
                if ($query.height -gt 48) { throw "Query row is unexpectedly tall: $($query.height)." }
                $badge.x + $badge.width
            }
            if (($rightEdges | Measure-Object -Maximum).Maximum - ($rightEdges | Measure-Object -Minimum).Minimum -gt 1) {
                throw 'Query counts do not share a common right edge.'
            }
            Save-Image '01-query-sidebar.png'
        }
        Assert-Ui 'Right-click offers edit and remove for the clicked, unselected query' {
            Invoke-Ui @('click', $queries[1].automationId, '--right')
            Invoke-Ui @('wait-for', 'QueryContextEdit')
            Invoke-Ui @('wait-for', 'QueryContextRemove')
            Save-Image '02-query-context-menu.png'
            Invoke-Ui @('invoke', 'QueryContextEdit')
            Invoke-Ui @('wait-for', 'QueryName', '--value', 'My pull requests')
            Invoke-Ui @('wait-for', 'QueryExpression', '--value', 'is:pr author:@me state:open sort:updated-desc')
        }
        Assert-Ui 'Context editing saves the clicked query without renaming the selected query' {
            Invoke-Ui @('set-value', 'QueryName', 'UI custom reviews renamed')
            Invoke-Ui @('invoke', 'Primary')
            $renamedId = $queries[1].automationId -replace '^Query_', 'QueryName_'
            $originalId = $queries[0].automationId -replace '^Query_', 'QueryName_'
            Invoke-Ui @('wait-for', $renamedId, '-p', 'Name', '--value', 'UI custom reviews renamed')
            Invoke-Ui @('wait-for', $originalId, '-p', 'Name', '--value', 'Issues involving me')
        }
        Assert-Ui 'Context removal confirms the clicked query and Cancel preserves it' {
            Invoke-Ui @('click', $queries[2].automationId, '--right')
            Invoke-Ui @('invoke', 'QueryContextRemove')
            Invoke-Ui @('wait-for', 'ConfirmDeleteQuery', '-p', 'Name', '--value', 'Review requested', '--contains')
            Save-Image '03-remove-confirmation.png'
            Invoke-Ui @('send-keys', 'esc', '--via', 'send-input')
            Invoke-Ui @('wait-for', 'ConfirmDeleteQuery', '--gone')
            Invoke-Ui @('wait-for', $queries[2].automationId)
        }
        Assert-Ui 'Confirmed context removal deletes only the clicked saved query' {
            Invoke-Ui @('click', $queries[2].automationId, '--right')
            Invoke-Ui @('invoke', 'QueryContextRemove')
            Invoke-Ui @('wait-for', 'ConfirmDeleteQuery')
            Invoke-Ui @('invoke', 'Primary')
            Invoke-Ui @('wait-for', $queries[2].automationId, '--gone')
            Invoke-Ui @('wait-for', $queries[0].automationId)
            Invoke-Ui @('wait-for', $queries[1].automationId)
        }
        Assert-Ui 'Follow-ups does not offer saved-query edit or removal' {
            Invoke-Ui @('click', 'Query___follow-ups', '--right')
            Invoke-Ui @('wait-for', 'QueryContextEdit', '--gone')
            Invoke-Ui @('wait-for', 'QueryContextRemove', '--gone')
        }
        Assert-Ui 'Keyboard context menu supports the same saved-query actions' {
            Invoke-Ui @('focus', $queries[0].automationId)
            Invoke-Ui @('send-keys', 'shift+f10', '--via', 'send-input')
            Invoke-Ui @('wait-for', 'QueryContextEdit')
            Invoke-Ui @('wait-for', 'QueryContextRemove')
            Invoke-Ui @('send-keys', 'esc', '--via', 'send-input')
        }
        Assert-Ui 'Read acknowledgements update right-aligned query counts immediately' {
            Invoke-Ui @('click', $queries[0].automationId)
            Invoke-Ui @('click', 'Item_demo-external-item')
            Invoke-Ui @('invoke', 'MarkRead')
            foreach ($query in $queries | Select-Object -First 2) {
                $badgeId = $query.automationId -replace '^Query_', 'QueryBadge_'
                Invoke-Ui @('wait-for', $badgeId, '-p', 'Name', '--value', '1')
                Invoke-Ui @('wait-for', $query.automationId, '-p', 'Name', '--value', '1 unread', '--contains')
            }
            Save-Image '04-query-count-update.png'
        }
        Assert-Ui 'Query rows expose an actual named automation tree and distinct count labels' {
            foreach ($id in @($queries[0].automationId, $queries[1].automationId, 'Query___follow-ups')) {
                $tree = Invoke-Ui @('inspect', $id, '--json', '-d', '4') -Window $hwnd | ConvertFrom-Json
                $nodes = @(Get-Nodes @($tree.windows | ForEach-Object { $_.elements }))
                if ($nodes.Count -lt 3 -or @($nodes | Where-Object { -not $_.name -or -not $_.automationId }).Count) {
                    throw "Query $id is missing its accessible row, name, or unread count."
                }
            }
        }
    } elseif ($Mode -eq 'builder') {
        Invoke-Ui @('wait-for', 'QueryUnreadCount', '--value', '2 unread', '-t', '10000') | Out-Null
        Assert-Ui 'New query opens the builder with documented qualifiers' {
            Invoke-Ui @('invoke', 'AddQuerySidebar')
            Invoke-Ui @('wait-for', 'QueryExpression')
            Invoke-Ui @('wait-for', 'QualifierList')
            Invoke-Ui @('wait-for', 'Qualifier_is')
            Invoke-Ui @('wait-for', 'QualifierSummary', '-p', 'Name', '--value', 'Filters by kind', '--contains')
            Invoke-Ui @('wait-for', 'QueryPreviewStatus', '-p', 'Name', '--value', 'Build or describe', '--contains')
            if (-not ((Test-UiElement 'GenerateQuery') -or (Test-UiElement 'QueryAiUnavailable'))) {
                throw 'Neither AI generation nor its unavailability notice is shown.'
            }
            Save-Image '01-builder.png'
        }
        Assert-Ui 'Qualifier search filters the documentation' {
            Invoke-Ui @('send-keys', 'ctrl+a', '--target', 'QualifierSearch', '--via', 'send-input')
            Invoke-Ui @('send-keys', 'author', '--target', 'QualifierSearch', '--via', 'send-input')
            Invoke-Ui @('wait-for', 'Qualifier_author')
            Invoke-Ui @('wait-for', 'Qualifier_is', '--gone')
        }
        Assert-Ui 'Adding a documented qualifier writes valid syntax and previews matches' {
            Invoke-Ui @('click', 'Qualifier_author')
            Invoke-Ui @('wait-for', 'QualifierSummary', '-p', 'Name', '--value', 'author', '--contains')
            Invoke-Ui @('set-value', 'QualifierValue', '@me')
            Invoke-Ui @('invoke', 'AddQualifier')
            Invoke-Ui @('wait-for', 'QueryExpression', '--value', 'author:@me')
            Invoke-Ui @('wait-for', 'QueryValidation', '-p', 'Name', '--value', 'Looks good', '--contains')
            Invoke-Ui @('wait-for', 'QueryPreviewStatus', '-p', 'Name', '--value', '3 match', '--contains', '-t', '5000')
            Invoke-Ui @('wait-for', 'QueryPreviewList')
        }
        Assert-Ui 'Exclusion prefixes the qualifier with a minus sign' {
            Invoke-Ui @('set-value', 'QualifierValue', 'dependabot[bot]')
            Invoke-Ui @('invoke', 'ExcludeQualifier')
            Invoke-Ui @('invoke', 'AddQualifier')
            Invoke-Ui @('wait-for', 'QueryExpression', '--value', 'author:@me -author:dependabot[bot]')
            Save-Image '02-builder-preview.png'
        }
        Assert-Ui 'Unknown qualifiers are flagged with a suggestion' {
            Invoke-Ui @('set-value', 'QueryExpression', 'is:pr athor:@me')
            Invoke-Ui @('wait-for', 'QueryValidation', '-p', 'Name', '--value', 'Did you mean author:', '--contains', '-t', '3000')
            Save-Image '03-builder-validation.png'
        }
        Assert-Ui 'Saving requires a name and keeps the builder open' {
            Invoke-Ui @('set-value', 'QueryExpression', 'is:pr author:@me')
            Invoke-Ui @('set-value', 'QueryName', '')
            Invoke-Ui @('invoke', 'Primary')
            Invoke-Ui @('wait-for', 'QueryEditorError')
            Invoke-Ui @('wait-for', 'Enter a display name.')
            Invoke-Ui @('wait-for', 'QueryEditor')
        }
        Assert-Ui 'Saved builder query appears in the sidebar' {
            Invoke-Ui @('set-value', 'QueryName', 'Builder PRs')
            Invoke-Ui @('invoke', 'Primary')
            Invoke-Ui @('wait-for', 'QueryEditor', '--gone')
            Invoke-Ui @('wait-for', 'Builder PRs', '--contains', '-t', '5000')
        }
        Assert-Ui 'Editing a saved query previews it immediately' {
            $query = (Invoke-Ui @('get-property', 'Builder PRs', '-p', 'AutomationId', '--json') | ConvertFrom-Json).element
            Invoke-Ui @('click', $query.automationId, '--right')
            if (-not (Test-UiElement 'QueryContextEdit')) { Invoke-Ui @('click', $query.automationId, '--right') }
            Invoke-Ui @('wait-for', 'QueryContextEdit')
            Invoke-Ui @('invoke', 'QueryContextEdit')
            Invoke-Ui @('wait-for', 'QueryExpression', '--value', 'is:pr author:@me')
            Invoke-Ui @('wait-for', 'QueryPreviewStatus', '-p', 'Name', '--value', '3 match', '--contains', '-t', '5000')
            Invoke-Ui @('send-keys', 'esc', '--target', 'QueryName', '--via', 'send-input')
            Invoke-Ui @('wait-for', 'QueryEditor', '--gone')
        }
        Assert-Ui 'Narrow windows switch between composer and reference' {
            if (-not ('GitHubToDoUiWindow' -as [type])) {
                Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class GitHubToDoUiWindow { [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags); }'
            }
            $window = [IntPtr][long]$hwnd
            try {
                if (-not [GitHubToDoUiWindow]::SetWindowPos($window, [IntPtr]::Zero, 0, 0, 720, 900, 6)) { throw 'Window resize failed.' }
                Start-Sleep -Milliseconds 500
                Invoke-Ui @('invoke', 'PART_PaneToggleButton')
                Invoke-Ui @('invoke', 'AddQuerySidebar')
                Invoke-Ui @('wait-for', 'ComposePane')
                Invoke-Ui @('wait-for', 'QueryExpression')
                Invoke-Ui @('wait-for', 'QualifierList', '--gone')
                Save-Image '04-builder-narrow.png'
                Invoke-Ui @('click', 'ReferencePane')
                Invoke-Ui @('wait-for', 'QualifierList')
                Invoke-Ui @('wait-for', 'AddQualifier', '-p', 'IsOffscreen', '--value', 'False')
                Invoke-Ui @('wait-for', 'QueryExpression', '--gone')
                Invoke-Ui @('invoke', 'AddQualifier')
                Save-Image '05-builder-narrow-reference.png'
                Invoke-Ui @('click', 'ComposePane')
                Invoke-Ui @('wait-for', 'QueryExpression', '--value', 'is:issue')
                Invoke-Ui @('send-keys', 'esc', '--target', 'QueryName', '--via', 'send-input')
                Invoke-Ui @('wait-for', 'QueryEditor', '--gone')
            } finally {
                [GitHubToDoUiWindow]::SetWindowPos($window, [IntPtr]::Zero, 0, 0, 1200, 800, 6) | Out-Null
            }
        }
    } elseif ($Mode -eq 'reading') {
        Invoke-Ui @('wait-for', 'QueryUnreadCount', '--value', '2 unread', '-t', '10000') | Out-Null
        $saved = Get-Tree | Where-Object { $_.automationId -like 'Query_*' -and $_.name.StartsWith('Issues involving me') } | Select-Object -First 1
        if (-not $saved) { throw 'No saved issue query.' }
        Assert-Ui 'Unread comment counts and local Follow-ups view are present' {
            Invoke-Ui @('wait-for', 'AddQuerySidebar', '-p', 'IsEnabled', '--value', 'True')
            Assert-CommentCount 'demo-external-item' 2
            Assert-CommentCount 'demo-bot-item' 1
            Assert-CommentCount 'demo-self-item' 0
            Invoke-Ui @('wait-for', 'Query___follow-ups')
            Save-Image '00-counts.png'
        }
        Assert-Ui 'Opening jumps directly to the first unread body without acknowledging it' {
            Invoke-Ui @('invoke', 'Item_demo-external-item', '--action', 'select')
            Invoke-Ui @('wait-for', 'UnreadActivity_demo-external-latest', '-p', 'IsOffscreen', '--value', 'False')
            Assert-VisibleComment 'First unread comment: please check the repro'
            Assert-CommentCount 'demo-external-item' 2
            Assert-Counts 2
            Save-Image '01-first-unread.png'
        }
        Assert-Ui 'Flagging a comment or item does not mark anything read' {
            Invoke-Ui @('invoke', 'FlagComment_demo-external-latest')
            Invoke-Ui @('wait-for', 'FlagComment_demo-external-latest', '--value', 'On')
            Assert-CommentCount 'demo-external-item' 2
            Invoke-Ui @('send-keys', 'space', '--target', 'FlagComment_demo-external-latest', '--via', 'send-input')
            Invoke-Ui @('wait-for', 'FlagComment_demo-external-latest', '--value', 'Off')
            Assert-CommentCount 'demo-external-item' 2
            Invoke-Ui @('send-keys', 'space', '--target', 'FlagComment_demo-external-latest', '--via', 'send-input')
            Invoke-Ui @('wait-for', 'FlagComment_demo-external-latest', '--value', 'On')
            Assert-CommentCount 'demo-external-item' 2
            Invoke-Ui @('invoke', 'Item_demo-bot-item', '--action', 'select')
            Invoke-Ui @('invoke', 'FlagItem')
            Invoke-Ui @('wait-for', 'Item_demo-bot-item', '-p', 'Name', '--value', 'flagged for follow-up', '--contains')
            Assert-CommentCount 'demo-bot-item' 1
            Invoke-Ui @('invoke', 'Item_demo-external-item', '--action', 'select')
            Invoke-Ui @('wait-for', 'UnreadActivity_demo-external-latest', '-p', 'IsOffscreen', '--value', 'False')
            Assert-VisibleComment 'First unread comment: please check the repro'
            Save-Image '02-flags.png'
        }
        Assert-Ui 'Scrolling past the first comment reads only that comment and retains its row' {
            Invoke-Ui @('scroll', 'DetailsPane', '--wheel', '-1')
            Assert-CommentCount 'demo-external-item' 1
            Invoke-Ui @('wait-for', 'UnreadActivity_demo-external-latest')
            Invoke-Ui @('wait-for', 'FlagComment_demo-external-latest', '--value', 'On')
            Assert-Counts 2
        }
        Assert-Ui 'Reopening with full history positions at the first unread review, not the read comment' {
            Invoke-Ui @('invoke', 'Query___follow-ups', '--action', 'select')
            Invoke-Ui @('invoke', 'Item_demo-external-item', '--action', 'select')
            Invoke-Ui @('wait-for', 'UnreadActivity_demo-external-followup', '-p', 'IsOffscreen', '--value', 'False')
            $prior = try { Invoke-Ui @('get-property', 'UnreadActivity_demo-external-latest', '-p', 'IsOffscreen', '--json') | ConvertFrom-Json } catch { $null }
            if ($prior -and -not $prior.properties.IsOffscreen) { throw 'The read comment should be above the viewport.' }
            Assert-CommentCount 'demo-external-item' 1
            Save-Image '03-resume-first-unread.png'
        }
        Assert-Ui 'A long comment stays unread until its bottom is reached' {
            Invoke-Ui @('scroll', 'DetailsPane', '--wheel', '-1')
            Assert-CommentCount 'demo-external-item' 1
        }
        Assert-Ui 'Scrolling to the end reads all comments without clearing flags or removing content' {
            Invoke-Ui @('send-keys', 'ctrl+end', '--target', 'DetailsPane', '--via', 'send-input')
            Assert-CommentCount 'demo-external-item' 0
            Assert-Counts 1
            Invoke-Ui @('wait-for', 'Item_demo-external-item', '-p', 'Name', '--value', 'flagged for follow-up', '--contains')
            Invoke-Ui @('wait-for', 'UnreadActivity_demo-external-followup')
            Save-Image '04-read-with-follow-ups.png'
        }
        Assert-Ui 'Refresh preserves item and comment flags and comment acknowledgements' {
            Invoke-Ui @('invoke', 'Refresh')
            Invoke-Ui @('wait-for', 'ApplicationStatus', '--value', 'Checked at', '--contains', '-t', '10000')
            Assert-CommentCount 'demo-external-item' 0
            foreach ($id in @('demo-external-item', 'demo-bot-item')) {
                Invoke-Ui @('wait-for', "Item_$id", '-p', 'Name', '--value', 'flagged for follow-up', '--contains')
            }
        }
        Assert-Ui 'Clearing the item flag retains the independently flagged comment' {
            Invoke-Ui @('invoke', 'Item_demo-bot-item', '--action', 'select')
            Invoke-Ui @('invoke', 'FlagItem')
            Invoke-Ui @('wait-for', 'Item_demo-bot-item', '--gone')
            Invoke-Ui @('invoke', 'Item_demo-external-item', '--action', 'select')
            Invoke-Ui @('wait-for', 'FlagComment_demo-external-latest', '-p', 'IsOffscreen', '--value', 'False')
            Invoke-Ui @('wait-for', 'FlagComment_demo-external-latest', '--value', 'On')
            Assert-CommentCount 'demo-external-item' 0
        }
        Assert-Ui 'Clearing the last comment flag removes the item from Follow-ups, not from saved queries' {
            Invoke-Ui @('invoke', 'FlagComment_demo-external-latest')
            Invoke-Ui @('wait-for', 'Item_demo-external-item', '--gone')
            Save-Image '05-no-follow-ups.png'
            Invoke-Ui @('invoke', $saved.automationId, '--action', 'select')
            Assert-CommentCount 'demo-external-item' 0
            Assert-CommentCount 'demo-bot-item' 1
        }
        Assert-Ui 'Bulk Mark read remains available alongside scroll reading' {
            Invoke-Ui @('invoke', 'Item_demo-bot-item', '--action', 'select')
            Invoke-Ui @('send-keys', 'ctrl+a', '--target', 'ItemList', '--via', 'send-input')
            Invoke-Ui @('wait-for', 'SelectionStatus', '--value', '3 selected', '--contains')
            Invoke-Ui @('send-keys', 'ctrl+q', '--target', 'ItemList', '--via', 'send-input')
            foreach ($id in @('demo-external-item', 'demo-self-item', 'demo-bot-item')) { Assert-CommentCount $id 0 }
            Assert-Counts 0
            Save-Image '06-bulk-read.png'
        }
        Assert-Ui 'An individual comment can be restored unread even when the latest activity is yours' {
            Invoke-Ui @('invoke', 'Item_demo-self-item', '--action', 'select')
            Invoke-Ui @('wait-for', 'ReadComment_demo-self-earlier', '-p', 'IsOffscreen', '--value', 'False')
            Invoke-Ui @('invoke', 'ReadComment_demo-self-earlier')
            Assert-CommentCount 'demo-self-item' 1
            Invoke-Ui @('wait-for', 'ReadComment_demo-self-earlier', '-p', 'Name', '--value', 'Mark comment read')
            Assert-Counts 1
        }
        Assert-Ui 'Ctrl+U restores the whole item unread and Ctrl+Q clears the manual override' {
            Invoke-Ui @('send-keys', 'ctrl+u', '--target', 'ItemList', '--via', 'send-input')
            Assert-CommentCount 'demo-self-item' 2
            Assert-Counts 1
            Invoke-Ui @('send-keys', 'ctrl+q', '--target', 'ItemList', '--via', 'send-input')
            Assert-CommentCount 'demo-self-item' 0
            Assert-Counts 0
        }
        Assert-Ui 'Mark unread works on multiple selected items through the context menu' {
            Invoke-Ui @('send-keys', 'ctrl+a', '--target', 'ItemList', '--via', 'send-input')
            Invoke-Ui @('wait-for', 'SelectionStatus', '--value', '3 selected', '--contains')
            Invoke-Ui @('click', 'Item_demo-external-item', '--right')
            Invoke-Ui @('wait-for', 'ContextMarkUnread')
            Invoke-Ui @('invoke', 'ContextMarkUnread')
            Assert-CommentCount 'demo-external-item' 2
            Assert-CommentCount 'demo-self-item' 2
            Assert-CommentCount 'demo-bot-item' 1
            Assert-Counts 3
            Save-Image '07-marked-unread.png'
            Invoke-Ui @('send-keys', 'ctrl+q', '--target', 'ItemList', '--via', 'send-input')
            Assert-Counts 0
        }
    } elseif ($Mode -in @('table', 'inbox')) {
        Invoke-Ui @('wait-for', 'ApplicationStatus', '--value', 'Checked at', '--contains', '-t', '10000') | Out-Null
        if ($ExpectedUnread -eq 0) {
            Invoke-Ui @('wait-for', 'QueryUnreadCount', '--gone', '-t', '5000') | Out-Null
        } else {
            Invoke-Ui @('wait-for', 'QueryUnreadCount', '--value', "$ExpectedUnread unread", '-t', '5000') | Out-Null
        }
        # Column layout persists across runs; start from defaults.
        Invoke-Ui @('invoke', 'TableViewOptions') | Out-Null
        Invoke-Ui @('invoke', 'ResetTableLayout') | Out-Null
        Save-Image '00-initial.png'
        Assert-Ui 'Native TableView and selectable rows are exposed' {
            Invoke-Ui @('wait-for', 'DemoBanner', '-t', '5000')
            $table = Invoke-Ui @('get-property', 'ItemList', '-p', 'ClassName', '--json') | ConvertFrom-Json
            if ($table.properties.ClassName -notmatch 'TableView') { throw "Item list is not the native TableView: $($table.properties.ClassName)." }
            foreach ($id in @('demo-external-item', 'demo-self-item', 'demo-bot-item')) {
                Invoke-Ui @('wait-for', "Item_$id")
            }
            Assert-Counts $ExpectedUnread
            $rows = @(Get-Tree | Where-Object { $_.automationId -like 'Item_demo-*' } | Sort-Object y)
            if (($rows.automationId -join ',') -ne 'Item_demo-bot-item,Item_demo-external-item,Item_demo-self-item') {
                throw "Rows are not ordered by latest activity: $($rows.automationId -join ',')."
            }
            if (Get-Tree | Where-Object { $_.automationId -like 'Select_demo-*' }) { throw 'A checkbox selection column must not exist.' }
        }
        Assert-Ui 'Table selection shows details without acknowledging activity' {
            Invoke-Ui @('invoke', 'Item_demo-external-item', '--action', 'select')
            Invoke-Ui @('wait-for', 'DetailsPane')
            Invoke-Ui @('wait-for', 'SelectedItemReadStatus', '--value', 'Unread')
            Invoke-Ui @('wait-for', 'SelectionStatus', '--value', '1 selected', '--contains')
            Invoke-Ui @('wait-for', 'AllDiscussionStatus', '--value', '2 unread', '--contains')
            Invoke-Ui @('wait-for', 'Item_demo-external-item', '-p', 'IsSelected', '--value', 'True')
            Assert-Counts $ExpectedUnread
            Assert-VisibleComment 'First unread comment: please check the repro'
            Save-Image '00b-first-unread-comment.png'
        }
        Assert-Ui 'Unread column counts discussion events rather than items' {
            Assert-CommentCount 'demo-external-item' 2
            Assert-CommentCount 'demo-bot-item' 1
            Assert-CommentCount 'demo-self-item' 0
        }
        Assert-Ui 'Unread comments and reviews are chronological and render Markdown' {
            Invoke-Ui @('scroll', 'DetailsPane', '--to', 'bottom')
            Invoke-Ui @('wait-for', 'UnreadActivity_demo-external-followup')
            $nodes = Get-Tree
            if (-not ($nodes | Where-Object { $_.name -like '*Demo Markdown smoke*' })) {
                throw 'Markdown heading was not rendered as accessible text.'
            }
            if (-not ($nodes | Where-Object { $_.name -like '*Bold smoke text*' })) {
                throw 'Markdown emphasis paragraph was not rendered.'
            }
            if (-not ($nodes | Where-Object { $_.name -like '*Deterministic fenced smoke code*' })) {
                throw 'Fenced Markdown code was not rendered.'
            }
            Save-Image '01-unread-markdown.png'
        }
        Assert-Ui 'Long Markdown scrolls and unsafe links do not launch or acknowledge' {
            Invoke-Ui @('scroll', 'DetailsPane', '--to', 'bottom')
            $preview = Invoke-Ui @('inspect', 'UnreadActivity_demo-external-followup', '-d', '12', '--json') | ConvertFrom-Json
            $link = Get-Nodes @($preview.windows | ForEach-Object { $_.elements }) |
                Where-Object { $_.type -eq 'Hyperlink' -and $_.name -eq 'Blocked HTTP link' } |
                Select-Object -First 1
            if (-not $link -or $link.isOffscreen) { throw 'Rendered activity links were not reachable by scrolling.' }
            Invoke-Ui @('invoke', $link.selector)
            Invoke-Ui @('wait-for', 'ApplicationStatus', '--value', 'Preview link blocked.', '--contains')
            Assert-Counts $ExpectedUnread
            Invoke-Ui @('wait-for', 'SelectedItemReadStatus', '--value', 'Unread')
            Invoke-Ui @('scroll', 'DetailsPane', '--to', 'top')
        }
        Assert-Ui 'Read discussion is reachable and changing item or filter resets its scroll' {
            Invoke-Ui @('scroll', 'DetailsPane', '--to', 'bottom')
            Invoke-Ui @('invoke', 'Item_demo-self-item', '--action', 'select')
            Invoke-Ui @('wait-for', 'SelectedItemReadStatus', '--value', 'Read')
            Invoke-Ui @('wait-for', 'AllDiscussionStatus', '--value', 'All 2 comment(s) and review(s), oldest first · 0 unread.', '--contains')
            Assert-VisibleComment 'Please review this.'
            Assert-CommentCount 'demo-self-item' 0
            Invoke-Ui @('wait-for', 'SelectedItemReadStatus', '--value', 'Read')
            Assert-Counts $ExpectedUnread
            Save-Image '01b-read-discussion.png'
            Invoke-Ui @('scroll', 'DetailsPane', '--to', 'bottom')
            Invoke-Ui @('invoke', 'Item_demo-external-item', '--action', 'select')
            Invoke-Ui @('wait-for', 'AllDiscussionStatus', '--value', '2 unread.', '--contains')
            Assert-VisibleComment 'First unread comment: please check the repro'
            Invoke-Ui @('scroll', 'DetailsPane', '--to', 'bottom')
            Invoke-Ui @('wait-for', 'AllDiscussionStatus', '--value', '2 unread', '--contains')
            Assert-VisibleComment 'First unread comment: please check the repro'
            Assert-Counts $ExpectedUnread
        }
        Assert-Ui 'Shift+arrow extends and contracts selection without checkboxes' {
            Invoke-Ui @('invoke', 'Item_demo-bot-item', '--action', 'select')
            Invoke-Ui @('send-keys', 'shift+down', '--target', 'ItemList', '--via', 'send-input')
            Invoke-Ui @('wait-for', 'SelectionStatus', '--value', '2 selected', '--contains')
            $selected = @(Get-Tree | Where-Object { $_.automationId -like 'Item_demo-*' -and $_.name.EndsWith(', selected') })
            if ($selected.Count -ne 2) { throw 'Both selected rows must expose selected accessible names.' }
            Invoke-Ui @('send-keys', 'shift+up', '--target', 'ItemList', '--via', 'send-input')
            Invoke-Ui @('wait-for', 'SelectionStatus', '--value', '1 selected', '--contains')
            Invoke-Ui @('send-keys', 'shift+down', '--target', 'ItemList', '--via', 'send-input')
            Invoke-Ui @('wait-for', 'SelectionStatus', '--value', '2 selected', '--contains')
            Save-Image '02-multiple-selected.png'
        }
        Assert-Ui 'Ctrl-click adds and removes individual rows' {
            Invoke-CtrlClick 'Item_demo-self-item'
            Invoke-Ui @('wait-for', 'SelectionStatus', '--value', '3 selected', '--contains')
            Invoke-CtrlClick 'Item_demo-self-item'
            Invoke-Ui @('wait-for', 'SelectionStatus', '--value', '2 selected', '--contains')
            Invoke-Ui @('click', 'Item_demo-external-item', '--right')
            Invoke-Ui @('wait-for', 'ContextMarkRead')
            Invoke-Ui @('send-keys', 'esc', '--via', 'send-input')
            Invoke-Ui @('wait-for', 'SelectionStatus', '--value', '2 selected', '--contains')
        }
        Assert-Ui 'Refresh preserves multiple selection and counts' {
            Invoke-Ui @('invoke', 'Refresh')
            Invoke-Ui @('wait-for', 'ApplicationStatus', '--value', 'Checked at', '--contains', '-t', '10000')
            Invoke-Ui @('wait-for', 'SelectionStatus', '--value', '2 selected', '--contains')
            Invoke-Ui @('wait-for', 'Item_demo-external-item', '-p', 'IsSelected', '--value', 'True')
            Assert-Counts $ExpectedUnread
        }
        Assert-Ui 'All item actions sit above details and below the table' {
            $list = Invoke-Ui @('get-property', 'ItemList', '-p', 'BoundingRectangle', '--json') | ConvertFrom-Json
            $detail = Invoke-Ui @('get-property', 'DetailsPane', '-p', 'BoundingRectangle', '--json') | ConvertFrom-Json
            foreach ($id in @('OpenInGitHub', 'MarkRead', 'Summarize')) {
                $action = Invoke-Ui @('get-property', $id, '-p', 'BoundingRectangle', '--json') | ConvertFrom-Json
                if ($action.element.y -lt ($list.element.y + $list.element.height) -or
                    ($action.element.y + $action.element.height) -gt $detail.element.y) { throw "$id is not above details and below the table." }
            }
        }
        Assert-Ui 'Local AI summarizes selected unread discussion or explains unavailability' {
            $enabled = Invoke-Ui @('get-property', 'Summarize', '-p', 'IsEnabled', '--json') | ConvertFrom-Json
            if ($enabled.properties.IsEnabled -eq $true -or $enabled.properties.IsEnabled -eq 'True') {
                Invoke-Ui @('invoke', 'Summarize')
                try {
                    Invoke-Ui @('wait-for', 'SummaryStatus', '--value', 'AI-generated locally', '--contains', '-t', '120000')
                } catch {
                    Save-Image '03-local-summary-error.png'
                    throw
                }
                Invoke-Ui @('wait-for', 'SummaryMarkdown')
                Assert-Counts $ExpectedUnread
                Save-Image '03-local-summary.png'
            } else {
                $hint = Invoke-Ui @('get-property', 'Summarize', '-p', 'HelpText', '--json') | ConvertFrom-Json
                if ($hint.properties.HelpText -notmatch 'unavailable|not ready') { throw 'Unavailable local AI must explain why.' }
            }
        }
        Assert-Ui 'Ctrl+A is scoped to the table and selects all rows' {
            Invoke-Ui @('click', 'Item_demo-self-item')
            Invoke-Ui @('wait-for', 'SelectionStatus', '--value', '1 selected', '--contains')
            Invoke-Ui @('invoke', 'AddQuerySidebar')
            Invoke-Ui @('wait-for', 'QueryName')
            Invoke-Ui @('send-keys', 'ctrl+a', '--target', 'QueryName', '--via', 'send-input')
            Invoke-Ui @('send-keys', 'esc', '--target', 'QueryName', '--via', 'send-input')
            Invoke-Ui @('wait-for', 'QueryEditor', '--gone')
            Invoke-Ui @('wait-for', 'SelectionStatus', '--value', '1 selected', '--contains')
            Invoke-Ui @('send-keys', 'ctrl+a', '--target', 'ItemList', '--via', 'send-input')
            Invoke-Ui @('wait-for', 'SelectionStatus', '--value', '3 selected', '--contains')
            Assert-Counts $ExpectedUnread
        }
        Assert-Ui 'Details toolbar acts only on the previewed item, even with all rows selected' {
            Invoke-Ui @('wait-for', 'SelectedItemTitle', '--value', 'Your latest reply', '--contains')
            Invoke-Ui @('invoke', 'MarkUnread')
            Assert-Counts ($ExpectedUnread + 1)
            Assert-CommentCount 'demo-external-item' 2
            Invoke-Ui @('wait-for', 'SelectionStatus', '--value', '3 selected', '--contains')
            Invoke-Ui @('invoke', 'MarkRead')
            Assert-Counts $ExpectedUnread
            Assert-CommentCount 'demo-external-item' 2
            Assert-CommentCount 'demo-bot-item' 1
        }
        Assert-Ui 'Bulk read from the table context menu updates all shared badges' {
            Invoke-Ui @('click', 'Item_demo-external-item', '--right')
            Invoke-Ui @('wait-for', 'ContextMarkRead', '--value', 'Mark 3 items read', '--contains')
            Invoke-Ui @('invoke', 'ContextMarkRead')
            Invoke-Ui @('wait-for', 'SelectedItemReadStatus', '--value', 'Read')
            Assert-Counts 0
            foreach ($id in @('demo-external-item', 'demo-self-item', 'demo-bot-item')) { Assert-CommentCount $id 0 }
            Invoke-Ui @('invoke', 'Item_demo-external-item', '--action', 'select')
            Invoke-Ui @('wait-for', 'AllDiscussionStatus', '--value', '0 unread', '--contains')
            Invoke-Ui @('scroll', 'DetailsPane', '--to', 'top')
            Save-Image '04-bulk-read.png'
        }
        Assert-Ui 'Marked-read comments remain visible without changing read state' {
            Invoke-Ui @('wait-for', 'AllDiscussionStatus', '--value', 'All 2 comment(s) and review(s), oldest first · 0 unread.', '--contains')
            Assert-VisibleComment 'First unread comment: please check the repro'
            Invoke-Ui @('wait-for', 'SelectedItemReadStatus', '--value', 'Read')
            Assert-CommentCount 'demo-external-item' 0
            Assert-Counts 0
            Save-Image '04b-marked-read-discussion.png'
        }
        Assert-Ui 'Original description remains available as expandable context' {
            Invoke-Ui @('scroll', 'DetailsPane', '--to', 'bottom')
            Invoke-Ui @('invoke', 'DescriptionContext', '--action', 'expand')
            Invoke-Ui @('wait-for', 'DescriptionMarkdown')
            Invoke-Ui @('invoke', 'DescriptionContext', '--action', 'collapse')
        }
        Assert-Ui 'Narrow layout retains table, details and commands' { Assert-NarrowLayout }
        if ($Mode -eq 'table') {
            Assert-Ui 'Column headers sort the table and keep selection aligned' {
                $header = @(Get-Tree | Where-Object { $_.type -eq 'Group' -and $_.name -eq 'Title' }) | Select-Object -First 1
                if (-not $header) { throw 'Title column header is not exposed.' }
                Invoke-Ui @('click', $header.selector) | Out-Null
                Start-Sleep -Milliseconds 400
                $rows = @(Get-Tree | Where-Object { $_.automationId -like 'Item_demo-*' } | Sort-Object y)
                if (($rows.automationId -join ',') -ne 'Item_demo-bot-item,Item_demo-external-item,Item_demo-self-item') {
                    throw "Title ascending order is wrong: $($rows.automationId -join ',')."
                }
                Invoke-Ui @('click', $header.selector) | Out-Null
                Start-Sleep -Milliseconds 400
                $rows = @(Get-Tree | Where-Object { $_.automationId -like 'Item_demo-*' } | Sort-Object y)
                if (($rows.automationId -join ',') -ne 'Item_demo-self-item,Item_demo-external-item,Item_demo-bot-item') {
                    throw "Title descending order is wrong: $($rows.automationId -join ',')."
                }
                Invoke-Ui @('invoke', 'Item_demo-self-item', '--action', 'select')
                Invoke-Ui @('send-keys', 'shift+down', '--target', 'ItemList', '--via', 'send-input')
                Invoke-Ui @('wait-for', 'SelectionStatus', '--value', '2 selected', '--contains')
                $selected = @(Get-Tree | Where-Object { $_.automationId -like 'Item_demo-*' -and $_.name.EndsWith(', selected') }).automationId | Sort-Object
                if (($selected -join ',') -ne 'Item_demo-external-item,Item_demo-self-item') { throw "Range selection did not follow the sorted order: $($selected -join ',')." }
                Save-Image '06-sorted-by-title.png'
            }
            Assert-Ui 'View menu changes density, hides columns and resets the layout' {
                $before = Invoke-Ui @('get-property', 'Item_demo-bot-item', '-p', 'BoundingRectangle', '--json') | ConvertFrom-Json
                Invoke-Ui @('invoke', 'TableViewOptions')
                Invoke-Ui @('invoke', 'DensityCompact')
                Start-Sleep -Milliseconds 400
                $after = Invoke-Ui @('get-property', 'Item_demo-bot-item', '-p', 'BoundingRectangle', '--json') | ConvertFrom-Json
                if ($after.element.height -ge $before.element.height) { throw "Compact density did not shrink rows ($($before.element.height) -> $($after.element.height))." }
                Invoke-Ui @('invoke', 'TableViewOptions')
                Invoke-Ui @('invoke', 'ColumnsMenu', '--action', 'expand')
                Invoke-Ui @('invoke', 'ColumnToggle_Repository')
                Start-Sleep -Milliseconds 400
                if (Get-Tree | Where-Object { $_.type -eq 'Group' -and $_.name -eq 'Repository' -and -not $_.isOffscreen }) { throw 'Repository column is still visible.' }
                Save-Image '07-compact-no-repository.png'
                Invoke-Ui @('invoke', 'TableViewOptions')
                Invoke-Ui @('invoke', 'ResetTableLayout')
                Start-Sleep -Milliseconds 400
                if (-not (Get-Tree | Where-Object { $_.type -eq 'Group' -and $_.name -eq 'Repository' })) { throw 'Reset did not restore the Repository column.' }
                $rows = @(Get-Tree | Where-Object { $_.automationId -like 'Item_demo-*' } | Sort-Object y)
                if (($rows.automationId -join ',') -ne 'Item_demo-bot-item,Item_demo-external-item,Item_demo-self-item') { throw 'Reset did not restore activity order.' }
            }
        }
        Assert-Ui 'Final table layout screenshot' { Save-Image '05-restored.png' }
    } elseif ($Mode -eq 'layout') {
        Assert-Ui 'Narrow layout retains navigation and commands' { Assert-NarrowLayout }
        Assert-Ui 'Restored layout screenshot' { Save-Image '03-restored.png' }
    } elseif ($Mode -eq 'resume') {
        Assert-Ui 'Restart preserves saved queries and shared read state' {
            Invoke-Ui @('wait-for', 'DemoBanner', '-t', '5000')
            Assert-Counts $ExpectedUnread
            if (-not (Get-CustomQuery)) { throw 'Custom query did not survive restart.' }
            Invoke-Ui @('invoke', 'Item_demo-external-item', '--action', 'select')
            Invoke-Ui @('wait-for', 'SelectedItemReadStatus', '--value', 'Read')
        }
        Assert-Ui 'Details are below the item list' {
            $list = Invoke-Ui @('get-property', 'ItemList', '-p', 'BoundingRectangle', '--json') | ConvertFrom-Json
            $detail = Invoke-Ui @('get-property', 'DetailsPane', '-p', 'BoundingRectangle', '--json') | ConvertFrom-Json
            if ($detail.element.y -lt ($list.element.y + $list.element.height)) {
                throw 'The detail pane overlaps or sits beside the item list.'
            }
        }
        Assert-Ui 'Refresh preserves read state and selection' {
            Invoke-Ui @('invoke', 'Refresh')
            Invoke-Ui @('wait-for', 'ApplicationStatus', '--value', 'Checked at', '--contains', '-t', '10000')
            Assert-Counts $ExpectedUnread
            Invoke-Ui @('wait-for', 'SelectedItemReadStatus', '--value', 'Read')
        }
        Assert-Ui 'Ignore and unignore update weight and counts immediately' {
            Invoke-Ui @('invoke', 'NavigationSettings')
            Invoke-Ui @('set-value', 'IgnoredAccounts', 'dependabot[bot]')
            Invoke-Ui @('invoke', 'SaveIgnoredAccounts')
            Invoke-Ui @('invoke', 'BackToQueries')
            Assert-Counts 0
            Save-Image '01-no-unread.png'
            Invoke-Ui @('invoke', 'NavigationSettings')
            Invoke-Ui @('set-value', 'IgnoredAccounts', '')
            Invoke-Ui @('invoke', 'SaveIgnoredAccounts')
            Invoke-Ui @('invoke', 'BackToQueries')
            Assert-Counts 1
        }
        Assert-Ui 'Custom queries retain editable search expressions' {
            $query = Get-CustomQuery
            Invoke-Ui @('invoke', $query.automationId, '--action', 'select')
            Open-QueryMenu
            Invoke-Ui @('invoke', 'EditQuery')
            Invoke-Ui @('wait-for', 'QueryExpression', '--value', 'is:pr review-requested:@me state:open archived:false sort:updated-desc')
            Invoke-Ui @('set-value', 'QueryName', 'UI custom reviews renamed')
            Invoke-Ui @('invoke', 'Primary')
            if (-not (Get-CustomQuery).name.StartsWith('UI custom reviews renamed')) { throw 'Query name was not saved.' }
        }
        Assert-Ui 'Saved queries can be reordered' {
            Open-QueryMenu
            Invoke-Ui @('invoke', 'MoveQueryUp')
            $queries = @(Get-Tree | Where-Object { $_.automationId -like 'Query_*' -and $_.type -eq 'ListItem' })
            if (-not $queries[2].name.StartsWith('UI custom reviews renamed')) { throw 'Query was not moved up.' }
        }
        Assert-Ui 'Deletion confirms the selected custom query' {
            Open-QueryMenu
            Invoke-Ui @('invoke', 'DeleteQuery')
            Invoke-Ui @('wait-for', 'ConfirmDeleteQuery')
            Invoke-Ui @('invoke', 'Primary')
            if (Get-CustomQuery) { throw 'The confirmed custom query still exists.' }
        }
        Assert-Ui 'Narrow layout retains navigation and commands' {
            Assert-NarrowLayout
        }
        Assert-Ui 'Verified final layout screenshot' { Save-Image '03-restored.png' }
    } else {
        Assert-Ui 'Explicit demo mode is visible' { Invoke-Ui @('wait-for', 'DemoBanner', '-t', '5000') }
        Assert-Ui 'Three saved queries show correct unread counts' { Assert-Counts $ExpectedUnread }
        Assert-Ui 'Demo screenshot' { Save-Image '01-demo.png' }
        Assert-Ui 'Selecting external issue does not acknowledge it' {
            Invoke-Ui @('invoke', 'Item_demo-external-item', '--action', 'select')
            Invoke-Ui @('wait-for', 'DetailsPane')
            Assert-Counts $ExpectedUnread
        }
        Assert-Ui 'Explicit acknowledgement updates every query' {
            Invoke-Ui @('invoke', 'MarkRead')
            Assert-Counts ($ExpectedUnread - 1)
        }
        Assert-Ui 'Read-state screenshot' { Save-Image '02-read.png' }
        Assert-Ui 'Open ignored-account settings' {
            Invoke-Ui @('invoke', 'NavigationSettings')
            Invoke-Ui @('wait-for', 'IgnoredAccounts')
        }
        Assert-Ui 'Ignoring a bot updates all badges without refresh' {
            Invoke-Ui @('set-value', 'IgnoredAccounts', 'dependabot[bot]')
            Invoke-Ui @('invoke', 'SaveIgnoredAccounts')
            Invoke-Ui @('invoke', 'BackToQueries')
            Assert-Counts 0
            Save-Image '02b-ignored.png'
        }
        Assert-Ui 'Removing ignore reveals unacknowledged bot activity' {
            Invoke-Ui @('invoke', 'NavigationSettings')
            Invoke-Ui @('set-value', 'IgnoredAccounts', '')
            Invoke-Ui @('invoke', 'SaveIgnoredAccounts')
            Invoke-Ui @('invoke', 'BackToQueries')
            Assert-Counts 1
        }
        Assert-Ui 'Custom review query can be added' {
            Invoke-Ui @('invoke', 'AddQuerySidebar')
            Invoke-Ui @('wait-for', 'QueryName')
            Invoke-Ui @('set-value', 'QueryName', 'UI custom reviews')
            Invoke-Ui @('set-value', 'QueryExpression', 'is:pr review-requested:@me state:open archived:false sort:updated-desc')
            Invoke-Ui @('invoke', 'Primary')
            Invoke-Ui @('wait-for', 'UI custom reviews', '--contains')
        }
        Assert-Ui 'Final screenshot' { Save-Image '03-final.png' }
    }
    if ($Mode -notin @('queries', 'builder', 'polling', 'refresh')) {
        Assert-Ui 'Nonempty real automation tree and named application controls' {
            $nodes = Get-Tree
            if ($nodes.Count -lt 10) { throw 'No meaningful automation tree.' }
            foreach ($id in @('QueryNavigation', 'ApplicationStatus')) {
                if (-not ($nodes | Where-Object automationId -EQ $id)) { throw "Missing $id." }
            }
            $unnamed = @($nodes | Where-Object {
                $_.type -in @('Button', 'Edit', 'CheckBox') -and -not $_.name -and -not $_.isOffscreen
            })
            if ($unnamed.Count) { throw "Found $($unnamed.Count) unnamed visible interactive controls." }
        }
    }
} catch {
    $results.Add(@{ name = 'UI execution'; status = 'FAIL'; detail = "$_" })
} finally {
    try {
        & winapp ui yield | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Failed to yield UI workflow.' }
    } catch {
        $results.Add(@{ name = 'Release UI workflow'; status = 'FAIL'; detail = "$_" })
    } finally {
        $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow
    }
}

$failed = @($results | Where-Object status -EQ 'FAIL').Count
$report = @{
    target = 'local'; appPid = $AppPid; mode = $Mode
    passed = $results.Count - $failed; failed = $failed
    results = @($results.ToArray()); screenshots = @($screenshots.ToArray())
}
$report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ArtifactDirectory 'results.json') -Encoding utf8
$report.results | Format-Table -AutoSize
if ($failed) { exit 1 }
