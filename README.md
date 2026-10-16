# GitHubToDo

Packaged WinUI 3 desktop reader for saved GitHub.com issue and pull-request searches. Query badges share device-local item read state. Opening an item jumps to its first unread comment without acknowledging it. Scrolling down acknowledges comments as their bottoms are reached; **Mark read** in the details toolbar acts on the previewed item; the table context menu and `Ctrl+Q`/`Ctrl+U` mark every selected row read/unread. `F5` refreshes; `Ctrl+O` opens the selected item in GitHub.

**Mark unread** (`Ctrl+U`) restores unread state for all selected items, including items automatically read because your reply was latest. It is available above details and in the table context menu. Each comment also has a read/unread action: a checkmark marks it read, and an envelope restores it unread. Explicit unread state is saved locally, overrides automatic own-latest read, and is cleared when the relevant activity is read again. It never changes follow-up flags.

## Build and run

Requires Windows, .NET 10, WinApp CLI 0.7+, and Developer Mode.

```powershell
dotnet restore src\GitHubToDo\GitHubToDo.csproj -p:Platform=x64
dotnet build src\GitHubToDo\GitHubToDo.csproj -p:Platform=x64 --no-restore
winapp run .\src\GitHubToDo\GitHubToDo.csproj --arch x64 -p Platform=x64
```

The project supports x64 and ARM64. Keep packaged activation; do not launch the executable directly.

### Experimental native table

The app pins **Microsoft.WindowsAppSDK 2.5.4-experimental** (no floating versions), restored through the repository's NuGet feed mirror. Its resolved native controls package is **Microsoft.WindowsAppSDK.WinUI 2.3.10-experimental**; the matching runtime is **2.5.4-experimental**, not the installed stable runtime or another experimental channel.

Following WinUI Gallery, the app sets **`WindowsAppSDKSelfContained=true`** while retaining packaged/MSIX activation. The SDK bundles its native components and merges their PRI resources into the app's `resources.pri`, including `Microsoft.UI.Xaml.Controls.Tabular/Themes/themeresources.xbf` and `generic.xbf`. This avoids the framework-dependent resource-map lookup failure observed with this experimental control; no hand-copied styles or custom table template are used. The generated package no longer requires the `Microsoft.WindowsAppRuntime.2-experimentalF` framework dependency (the corresponding framework-dependent runtime identity is version `2.5.4.0`). This setting bundles the Windows App SDK, not the .NET runtime, and increases package size.

`App.xaml` must also merge the SDK's **`TabularControlsResources`** after `XamlControlsResources`, following WinUI Gallery. No additional TableView feature toggle is required; this experimental control may change in future SDK releases.

Issues and pull requests use the native `Microsoft.UI.Xaml.Controls.Tabular.TableView` as a showcase: type/state, a flexible title with the latest activity line, an unread-count pill, repository, author, updated time and follow-up flag columns. Drag column edges to resize; click a header to sort (with a native sort indicator). The **View** menu switches Compact/Standard/Comfortable density, shows/hides columns and resets the layout; widths, visibility, density and sort persist per device. The app theme (System/Light/Dark) is under Settings > Appearance. Saved queries stay left (an overlay navigation pane below 1008 DIPs), and the selected item's details stay below a keyboard-focusable resizable divider. Refresh/query commands are above the table. **Open in GitHub, Mark read/unread, Flag and Summarize are directly above the details** and act only on the previewed item (Summarize covers the selection). The table context menu (Mark N items read/unread, Flag N items) and `Ctrl+Q`/`Ctrl+U` act on every selected row.

The **Unread** column counts unread comments, inline review comments and submitted reviews for each item, using the same policy as the preview and summary. It excludes acknowledged/ignored activity and initial-item markers; latest-own-activity items show zero unless manually restored unread. Pending items show `…`, not a misleading zero. Query badges still count unread **items**, not comments.

**Ctrl-click** toggles individual rows, **Shift-click** or **Shift+Up/Down** extends/contracts a range, and **Ctrl+A** selects every loaded row while the table has focus. There is no checkbox column. The restored experimental TableView exposes only native `None`/`Single` selection, so the app maintains a stable extended-selection set and themed row highlights while native selection tracks the preview row. Selected accessible row names and a live selection count expose the bulk selection. Refreshes/reordering preserve it; removed rows are pruned. Bulk Mark read (context menu or Ctrl+Q) acknowledges all selected rows in one durable transaction, capturing observed activity IDs before awaiting; later-discovered events remain unread. Pending/unhydrated selections cannot be acknowledged. Open in GitHub opens only the previewed item.

By default items are ordered by their latest relevant activity, newest first, using the same non-ignored activity timestamp shown in the table rather than GitHub's item-update timestamp. Items without relevant activity follow dated items; saved-query order breaks timestamp ties. Header sorting is applied by the view model (the native sort mirrors it through a custom comparer), so the chosen order survives refreshes and ignored-account changes and Shift-range selection follows what is shown. Hydration updates retained observable rows and granular collection changes rather than resetting the table. Selecting a native row, including via UI Automation, updates the preview without marking it read. `ItemList` identifies the table; realized selectable native `TableViewRow` containers retain `Item_<id>` IDs and accessible names that follow title/read-state changes. Details retain `DetailsPane`, `SelectedItemTitle`, and `SelectedItemReadStatus`.

The real client is `GraphQlGitHubClient`: refresh batches GraphQL search aliases (up to **8 per request**) and activity aliases (up to **10 per request**), with independent connection pagination and per-alias errors. There is **no silent REST fallback** for search/activity. REST remains for numeric identity and ignored-account lookup; GraphQL cannot reuse REST conditional GET/ETag caches. Demo mode keeps the existing fixture interface and never calls GitHub.

Searches always use `sort:updated-desc`; sort qualifiers in saved expressions are ignored, while quoted search text is preserved. Automatic polling and manual Refresh/F5 add an `updated:>=...` window with a five-minute overlap around the last successful search's start time. Empty incremental results retain cached membership and full-search totals. Only new/changed items and retries of pending or failed discussion loads need hydration; selecting or flagging an unchanged item does not cause its discussion to be downloaded again. Manual Refresh/F5 never upgrades to a full discussion scan merely because the cache is old. Query watermarks and full-refresh times survive restarts, and incomplete/capped incremental searches do not advance the watermark. The initial load, the explicit **Full reconciliation** overflow command, and a scheduled reconciliation every 30 minutes perform full searches and discussion checks. Reconciliation also monitors flagged items outside saved queries and catches items leaving a query, search-index delays, and reviews whose activity did not change the searchable update timestamp. These can therefore take up to a reconciliation interval to appear, plus any GitHub indexing delay.

Batching reduces HTTP round trips, **not GitHub's GraphQL point cost**. Long activity histories can still exhaust the rate limit and pause polling; cached data is retained. Batching does not change local read/ignore policy or make partial/failed coverage complete. **Live authenticated GraphQL has not been validated by the TableView integration/build checks.**

### Markdown details

The preview opens at **the first unread comment, inline review comment or submitted review**, using the same ignored-account/automatic-read policy as query badges. Scrolling down with the wheel, keyboard, scrollbar or touch acknowledges observed comments through the last message bottom reached. Long comments remain unread until their bottoms are reached; scrolling to the end reads all observed discussion. Opening, programmatic positioning, resizing, flagging and browser launch do not acknowledge activity. Items with no discussion still require explicit **Mark read**; once later discussion is fully read, an older initial-item marker is also acknowledged.

Read comments stay in place during the current preview so acknowledgements do not remove rows or jump the discussion. Newly discovered comments append without moving existing content; reopening restores chronological order and positions at the first unread message. The **Unread** column and all overlapping query badges update after each durable acknowledgement. Unknown, stale and no-unread states are explicit. The original description remains in a collapsed context expander; the constrained discussion ListView virtualizes activity rows rather than nesting a list inside a ScrollViewer.

The details always show the full discussion (earlier read comments and your own replies included), with unread activity labelled. Ignored accounts remain excluded. Switching the previewed item positions at the first unread message, or the first comment if all are read. **Summarize** still uses only unread discussion and never acknowledges activity itself.

### Follow-ups

Use the **flag icon** beside a comment/review to toggle its follow-up flag, or **Flag item** above details to flag the previewed item. Flags are device-local, account-isolated and independent of acknowledgements: reading or marking an item read never clears a flag. The **Follow-ups** sidebar view includes items with either type of flag, even after they stop matching saved searches, and flagged items continue to be monitored. It opens full discussion and, when nothing is unread, positions at the first available flagged comment.

Toggle the same flag to clear it. Clearing an item flag leaves comment flags intact; **Clear all follow-ups for this item** in the details overflow clears both. Flags on ignored or no-longer-available comments still keep an item in Follow-ups and can be cleared with that command. Flags never change GitHub labels or remote state.

Discussion bodies and the optional description use the requested **CommunityToolkit.WinUI.UI.Controls.Markdown 7.1.2** `MarkdownTextBlock`. Feed metadata targets `net5.0-windows10.0.18362`; the restored control derives from `Microsoft.UI.Xaml.Controls.Control` and uses the app's pinned experimental SDK. This is the WinUI package, not its similarly named UWP equivalent.

Both previews support text selection and themed headings, lists, quotes and code blocks without a WebView. Syntax coloring is disabled to retain semantic foreground colors. Image resolution is intercepted and blocked (no automatic remote tracking/image requests or local-file loading). Links launch only following an explicit click, and only absolute HTTPS URLs on exactly `github.com`, with no credentials or nondefault port. Relative, external-site, file and custom-protocol links are blocked with a status message; use **Open in GitHub** for the full website. Preview rendering/link clicks never acknowledge read state.

Automation IDs: `DescriptionMarkdown`, `UnreadActivity_<activity-id>`, and `UnreadDiscussionStatus`, inside the existing `DetailsPane`. A safe isolated-fixture smoke-test body is below (no need to create or modify real GitHub issues):

````markdown
## Preview heading
**Bold** and *italic* with `inline code`.
- First item
- Second item

> Quoted activity

```text
Wrapped code block
```

[Allowed explicit browser link](https://github.com/dotMorten/GitHubToDo)
[Blocked external link](https://example.com/)
[Blocked custom protocol](custom-protocol:preview-test)
![Blocked remote image](https://example.com/preview.png)
````

Expected: formatted selectable text; no browser/image network activity on selection; only the first link may launch on explicit activation; blocked links report a status; read state remains unchanged until **Mark read**.

### Optional local AI catch-up

**Summarize** processes unread discussion across the selected rows using the installed Windows App SDK `Microsoft.Windows.AI.Text.LanguageModel`. Ctrl+A covers the loaded query. It checks model readiness without calling `EnsureReadyAsync`: **no automatic model downloads, cloud inference or fallback providers**. Availability depends on Windows, compatible hardware/model installation, and applicable Windows AI access requirements; Refresh rechecks readiness. Unavailable models have an explanatory disabled action.

Long discussions are split into context-checked parts, each retaining item identity, with no silent input truncation. Summaries are AI-generated snapshots and may be inaccurate; verify the original discussion. Generation is cancellable and selection/unread-content changes invalidate it. Failures/content filtering are shown explicitly, never as a successful partial summary. **Summarizing does not mark anything read.**

If the Phi Silica Limited Access Feature must be unlocked for this package, set `GITHUBTODO_LAF_TOKEN`/`GITHUBTODO_LAF_ATTESTATION` or put `{ "lafToken": "...", "lafAttestation": "..." }` in a git-ignored `githubtodo.local.json` in the app's LocalState. Non-content diagnostics (ready state, status codes) are written to LocalState `local-ai.log`. If generation fails with RPC `0x800706BE` while the model reports Ready, the Windows AI service itself is failing on that PC (other apps are affected too); the app reports this rather than faking a result.

## Real GitHub sign-in

Official release builds (see [Releases](#releases)) have the OAuth client ID built in: just choose **Sign in**. Builds you make yourself need your own OAuth app:

1. Register **your own** GitHub OAuth application and enable **Device Flow**.
2. In Settings, enter its public client ID. No client secret is needed or stored. (Alternatively, build with the `GITHUBTODO_CLIENT_ID` environment variable or `-p:GitHubClientId=...` to bake it in; the Settings field is then hidden.)
3. Leave private access off for public-only access. Private repositories require the broad `repo` OAuth scope, although this app performs no repository writes. Your organization may require SSO authorization or application approval.
4. Choose **Sign in**, open the authorization page, and enter the displayed code. Cancel or retry if the code expires.

Tokens are stored in Windows Credential Locker, never in SQLite or ordinary settings. The client ID and requested private-scope flag are nonsecret local settings. Cached account data is isolated by immutable GitHub user ID; sign-out cancels monitoring and hides the private cache. Offline startup can show only the account associated with the stored credential. Explicit read acknowledgements are committed locally before successful UI feedback.

**Live OAuth and authenticated GitHub behavior have not been validated without a user-owned OAuth client ID and consented safe test repository/account access.** Demo mode is not evidence of real authentication or API correctness.

## Releases

The on-demand **Release** workflow (`.github/workflows/release.yml`; Actions > Release > Run workflow) takes a `major.minor.build` version, runs the core tests, builds x64 and ARM64 MSIX packages with the official client ID baked in, signs them with Azure Artifact (Trusted) Signing, and publishes them to a GitHub release tagged `v<version>`.

It needs these repository settings (Settings > Secrets and variables > Actions):

| Kind | Name | Value |
|------|------|-------|
| Variable | `GITHUBTODO_CLIENT_ID` | Official OAuth app client ID (public, Device Flow enabled) |
| Variable | `MSIX_PUBLISHER` | Signing certificate subject; must exactly match, e.g. `CN=Name, O=Name, L=City, S=State, C=US` |
| Variable | `MSIX_PUBLISHER_DISPLAY_NAME` | Publisher name shown by the installer (optional) |
| Variable | `AZURE_SIGNING_ENDPOINT` | Signing account region endpoint, e.g. `https://wus2.codesigning.azure.net/` |
| Variable | `AZURE_SIGNING_ACCOUNT` | Signing account name |
| Variable | `AZURE_SIGNING_PROFILE` | Certificate profile name |
| Secret | `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` | App registration used through OpenID Connect (no client secret). It needs a federated credential for `repo:dotMorten/GitHubToDo:ref:refs/heads/main` and the *Artifact Signing Certificate Profile Signer* role on the certificate profile |

The CI build restores from nuget.org instead of the checked-in mirror `NuGet.Config`. Packages are self-contained (.NET and Windows App SDK), so they install without extra runtimes.

## Debug-only demo

A Debug build accepts the explicit activation argument `--demo` (pass it as app launch arguments). Release builds do not include the fixture client or demo activation switch.

```powershell
winapp run .\src\GitHubToDo\GitHubToDo.csproj --arch x64 -p Platform=x64 --args "--demo" --detach --json
```

The explicit platform property is important: it keeps WinApp's evaluated output path aligned with the `bin\x64` build layout. Debug startup recognizes the exact `--demo` token from either XAML activation arguments or parsed process command-line arguments; there is no implicit demo fallback.

For repeatable UI checks without resetting existing demo acknowledgements, add `--demo-profile=<GUID in N format>` alongside `--demo`. Each profile uses its own `githubtodo-demo-<GUID>.db`; reusing the same profile verifies persistence. This switch is Debug-only and never changes the real account database.

Demo mode displays a persistent **DEMO** banner, uses a separate `githubtodo-demo.db`, never accesses PasswordVault or performs OAuth/GitHub API calls, and supports real local query CRUD/reordering, read persistence, and ignored-account settings. Every valid demo query intentionally returns the same three fixtures; it is not a search emulator:

| Fixture | Fresh demo state |
|---|---|
| #101 External activity | Unread |
| #102 Your latest reply | Read, derived from latest own activity |
| #103 Bot activity | Unread until `dependabot[bot]` is globally ignored |

Each default query initially has **2 unread, 0 pending** after hydration. Marking #101 read changes all three badges to **1**. Ignoring `dependabot[bot]` then changes them to **0**. Removing the ignore reveals #103 as unread again. Demo changes persist across restarts in the separate database.

The external fixture `demo-external-item` (#101) supplies a comment followed by a review with deterministic Markdown: heading **Demo Markdown smoke**, bold/emphasis, list, quote and fenced code. Link labels are **Allowed GitHub link**, **Blocked JavaScript link**, **Blocked HTTP link**, and **Blocked file link**; **Blocked remote smoke image** is the image alt text. These exercise the actual description and unread-discussion controls. Different publication times deliberately put the bot first, external item second, and own-reply item third. Use a fresh demo profile when validating initially-unread behavior.

## Coverage and limitations

- Polls recent updates every two minutes **while the window is open**, with scheduled full reconciliation every 30 minutes. Refresh/F5 checks recent changes; use **Full reconciliation** in the query-actions overflow menu to explicitly recheck everything. A refresh requested during an existing scan shares that in-flight operation. No tray service or monitoring after shutdown.
- GitHub search is eventually consistent and limited to the first 1,000 results per query. Badges cover loaded items only; unknown activity, partial search coverage, stale cached content, and failures are labelled separately.
- Tracks published comments, inline review comments, and submitted reviews. Description edits and other repository events do not independently create unread activity.
- **Review requested** is a direct-review-request preset, not a complete reproduction of team requests or personalized GitHub dashboards.
- Markdown uses the toolkit's supported Markdown dialect, not full GitHub-flavored HTML. Images are intentionally blocked and preview links restricted to HTTPS GitHub.com. There is no WebView, posting, or review submission.
- English UI only; localization resources and production signing/Store distribution are not included.
