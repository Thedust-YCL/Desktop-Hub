# Task 1 — data migration and business foundations

Status: complete. Implemented on `codex/full-usability` from the preserved `e145f51` baseline. No user settings, user source directories, Windows integration, desktop assets, or release folder were modified. Root separately owns the baseline diagnostic correction. Production changes began only after the root sent `baseline complete`.

## Delivered behavior

- Settings schema 5; resources schema 2. Existing legacy launch-target, app import, IDs, pin IDs/order, scene dependencies, resource/tag relationships, onboarding, desktop notice, opacity, desktop-icon and startup preferences remain supported.
- Independent application favorite state and flat named groups, including identity-preserving rename, membership movement, group removal without app/file deletion, and atomic complete group reorder.
- Resource batches enforce 1–500 unique active resource IDs, validate the whole request, and use one existing atomic metadata transaction. Invalid requests and failed replacement leave both in-memory state and prior file bytes unchanged. Adding/removing tags is incremental.
- Saved resource filters persist name/query/tag IDs/extension/favorites/sort, survive restart, preserve references to deleted tags, expose explicit invalid status and refuse to execute invalid filters. Text-only filters with an empty tag list are valid.
- Additive scene mode/window-layout models are deeply cloned and validated. Runtime simple-mode scheduling and window capture/restore remain Task 5/8 work, as assigned; current explicitly constructed scenes with dependencies keep their existing scheduler behavior.

## Exact additive public interfaces

All types are in `DesktopHub.Core`.

```csharp
// New mutable properties; defaults shown.
AppSettings.SchemaVersion : int = 5
AppSettings.UiDensity : string = "compact"      // compact | comfortable
AppSettings.PerformanceMode : string = "quick" // quick | lightweight
AppSettings.AutoCloseDock : bool = false
AppSettings.AppGroups : List<AppGroup> = []
AppEntry.Favorite : bool = false
AppEntry.GroupId : string? = null

public sealed record AppGroup(string Id, string Name, int DisplayOrder = 0);

public AppSettings AppLibraryService.SetFavorite(string appId, bool favorite);
public AppSettings AppLibraryService.SaveGroup(string? groupId, string name);
public AppSettings AppLibraryService.RemoveGroup(string groupId);
public AppSettings AppLibraryService.MoveToGroup(string appId, string? groupId);
public AppSettings AppLibraryService.ReorderGroups(IReadOnlyList<string> groupIds);

ResourceLibrary.SchemaVersion : int = 2
ResourceLibrary.SavedFilters : List<SavedResourceFilter> = []

public sealed record SavedResourceFilter
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Query { get; set; } = "";
    public List<string> TagIds { get; set; } = [];
    public string? Extension { get; set; }
    public bool FavoritesOnly { get; set; }
    public string Sort { get; set; } = "type";
}
public sealed record SavedResourceFilterStatus(
    SavedResourceFilter Filter, List<string> MissingTagIds)
{
    public bool Valid => MissingTagIds.Count == 0;
}

public Task ResourceLibraryService.BatchAsync(
    IReadOnlyList<string> resourceIds, string operation, IReadOnlyList<string> values);
public Task ResourceLibraryService.BatchAcceptSuggestionsAsync(
    IReadOnlyList<string> resourceIds, string suggestionName, string suggestionType);
public Task ResourceLibraryService.SaveFilterAsync(SavedResourceFilter filter);
public Task ResourceLibraryService.DeleteFilterAsync(string filterId);
public SavedResourceFilterStatus ResourceLibraryService.ResolveFilter(string filterId);
public ResourcePage ResourceLibraryService.QueryFilter(string filterId, int page = 0);

// Existing positional query gained only a final optional parameter.
public sealed record ResourceQuery(
    string Query = "", List<string>? TagIds = null, bool FavoritesOnly = false,
    bool InvalidOnly = false, string? Status = null, int Page = 0,
    string? Extension = null, string Sort = "type");

Scene.EditorMode : string = "simple"          // simple | advanced
Scene.ExecutionMode : string = "simultaneous" // simultaneous | sequential
Scene.WindowLayout : SceneWindowLayout? = null

public sealed record SceneWindowLayout
{
    public bool RestoreAfterRun { get; set; }
    public int WaitTimeoutMs { get; set; } = 15000;
    public List<SceneWindowPlacement> Windows { get; set; } = [];
}
public sealed record SceneWindowPlacement
{
    public string AppEntryId { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string TitleContains { get; set; } = "";
    public string MonitorId { get; set; } = "";
    public WindowBounds Bounds { get; set; } = new();
    public string State { get; set; } = "normal";
    public double Dpi { get; set; } = 96;
}
```

Group IDs use nonempty D-format GUIDs. Names are trimmed, required, at most 80 characters, contain no control characters, and must be unique ignoring case. `SaveGroup(null,name)` creates a group; an existing ID renames it. `MoveToGroup(appId,null)` clears membership. Reorder requires the complete unique existing ID permutation. All return an isolated `AppSettings` snapshot; obtain newly created group IDs from its `AppGroups`.

Filter IDs accept nonempty GUIDs. Names are required, trimmed and unique ignoring case (80 characters). Query length is at most 200, extension at most 260, and at most 500 unique GUID tag IDs are allowed. A new/edited filter requires currently existing tags; persisted missing tag references are retained. Sort values are `type`, `name`, `modifiedAsc`, `modifiedDesc`, with stable ID tie-breaking and sorting before pagination. `type` retains the existing folder/extension/name ordering. `ResolveFilter` returns isolated filter/tag-list copies; `QueryFilter` throws for missing tag conditions rather than widening results.

Window bounds are **relative to the saved monitor work area in DIP**, never pixels. No HWND/PID is persisted. Placement app IDs must be nonempty D-format GUIDs; titles and monitor identifiers are at most 200 characters. A nonempty effective executable path is validated as a local safe EXE; an empty path can defer identity resolution to the registered app. Bounds must be finite with positive dimensions; DPI must be finite within 48–768. State is `normal` or `maximized`; wait timeout is 1–600000 ms, default 15000. Layouts/windows/bounds, action argument lists and dependencies are deeply cloned. Layout reference resolution/capture/restore remains for the Windows layout service.

## Batch operation contract

The exact supported operation strings are:

| operation | values |
| --- | --- |
| `addTags` | 1–500 unique existing label IDs; preserve other bindings |
| `removeTags` | 1–500 unique existing label IDs; remove only requested bindings |
| `favorite` | empty list |
| `unfavorite` | empty list |
| `acceptSuggestions` | exactly one unique Pending suggestion ID per selected resource; every chosen suggestion must have the same trimmed name (case-insensitive) and tag type |

`BatchAcceptSuggestionsAsync` is a name/type convenience for UI callers: it resolves all Pending suggestions of that name and type for the resource selection **inside the same transaction**, then applies the identical full-request checks. No match, incomplete coverage, duplicate comparable suggestions for one resource, existing same-name label with a different type, stale IDs, invalid operation, duplicate selection, selection over 500, or non-Active resource causes a partial commit. Source cleanup remains separate. Batch inputs and saved-filter tag lists are copied before asynchronous queuing.

## Migration behavior

- Settings schemas 1–4 normalize to 5. Schema 1 still performs the prior additive stable-ID legacy `LaunchTarget` migration and preserves the original legacy field and its warning/recovery behavior.
- During known legacy migration, an app becomes favorite from its old `PinnedToDock` flag or enabled canonical Dock pin. Existing pin records/IDs/order remain; legacy compatibility flags without corresponding pins generate the missing pin only during legacy migration. Schema 5 derives only compatibility pin flags from `LauncherPins` and never derives/changes independent `Favorite` from pin state.
- Existing scenes from schema <5 become `advanced`; dependency graphs, IDs and action parameters remain. Newly constructed scenes default `simple` / `simultaneous`; execution semantics are deferred to Task 5.
- Clean known settings migrations are persisted atomically. Existing legacy warnings and damaged-scene isolation/backup remain; a locked replacement retains the original JSON and usable migrated in-memory data.
- Unknown settings versions are detected before strict field deserialization: original `settings.json` stays at its original path with original bytes; `Load` exposes defaults plus a recovery warning; `Save`/`Update` refuse writes. Write-time version checking also protects callers that save before loading or encounter a newly replaced future-version file. Corrupt known/non-versioned JSON retains the prior quarantine-and-recovery behavior.
- Resource schema 1 validates/normalizes to 2 and persists an atomic migration, preserving sources, resources, identities, relationships, categories, suggestions and time-tag consent. Unknown/corrupt resource files stay at `resources.json`, produce a recovery warning and block writes as before. Saved-filter tag references deliberately may be missing after a tag is deleted; status resolution makes this visible and execution remains blocked until edited/deleted.

## RED / GREEN evidence

Before production edits, new tests compiled against the old assembly using a temporary reflection invocation helper, allowing actual failing behavior rather than a missing-symbol compile failure. After implementation those tests were changed to direct typed API calls; no reflection test shim remains.

Initial RED:

```text
Core: RESULT 33 passed; 8 failed.
  Expected schema 5, got 4; future settings file was moved;
  Missing usability API: SetFavorite / SaveGroup;
  unmapped scene editorMode; existing schema migration expectations now expected 5.
Resource: RESOURCE RESULT 27 passed; 5 failed.
  Missing usability API: BatchAsync (three new behavior tests);
  Expected resource schema 2, got 1;
  existing owned junction fixture: Could not create owned junction fixture (sandbox).
```

An additional write-entry audit added a second RED regression before its fix:

```text
FAIL A future settings schema cannot be overwritten by saving before Load:
  Expected InvalidOperationException.
RESULT 41 passed; 1 failed.
```

Final explicit offline restore commands (all exit 0; no package feeds enabled):

```powershell
.tools/dotnet/dotnet.exe restore tests/DesktopHub.Core.Tests --configfile tests/DesktopHub.Core.Tests/NuGet.Config
.tools/dotnet/dotnet.exe restore tests/DesktopHub.Resource.Tests --configfile tests/DesktopHub.Core.Tests/NuGet.Config
.tools/dotnet/dotnet.exe restore tests/DesktopHub.Scene.Tests --configfile tests/DesktopHub.Core.Tests/NuGet.Config
```

Final fresh verification commands/output (all exit 0):

```text
.tools/dotnet/dotnet.exe run --project tests/DesktopHub.Core.Tests -c Release --no-restore
RESULT 42 passed; 0 failed.

.tools/dotnet/dotnet.exe run --project tests/DesktopHub.Resource.Tests -c Release --no-restore
RESOURCE RESULT 32 passed; 0 failed.

.tools/dotnet/dotnet.exe run --project tests/DesktopHub.Scene.Tests -c Release --no-restore
Scene scheduler/storage: 17 passed, 0 failed.
```

Resource verification used `require_escalated` only so the existing owned temporary `mklink /J` fixture could run. The previously sandbox-blocked junction escape/cycle test passed without changing the fixture or weakening its assertions. Resource fixtures are below `tests/DesktopHub.Resource.Tests/bin/Release/net8.0/m22-owned-*`, never user sources.

Final full output is retained locally as `artifacts/usability-development/task-1-core-green.log`, `task-1-resource-green.log`, `task-1-scene-green.log`. The extra RED output is `task-1-core-final-red.log`. These generated logs/fixtures are excluded from the commit. `git diff --check` is clean; Git may emit the existing LF-to-CRLF advisory for source files.

Coverage includes migration repeat/restart, pin/favorite four-way combinations, group identity/order/deletion, saved-filter nested-list isolation/empty tags/missing tags, layout nested bounds isolation/mode choices, selection 500/501, invalid/duplicate IDs, active-only batch selection, incremental tag preservation, comparable suggestion types, failed replacement, and all existing Core/Resource/Scene harness checks.

## Scope and concerns

The root approved a narrow ownership exception for `Scenes.cs` to add model fields, clone and normalize them. Scheduling, retry, layout capture/restore, frontend bridge/UI integration and lifecycle/performance work are intentionally left to their assigned later tasks. Controlled executable launch integration is opt-in and was skipped by the Core harness; the Scene runner is controlled, so these results do not claim Windows launch/layout acceptance. This task adds no external package dependencies and changes no existing import limits or Mini limits.

## Review fix round 1 — future-version classification

The review identified one P1 in the previous version guard: parsing a whole document with current `MaxDepth=16` before classification hid schema 6 when a preceding future field nested 20 levels; `TryGetInt32` failure also let numeric version `2147483648` bypass protection. Both `Load` and save-before-load could then remove/replace the original. The root independently confirmed this finding.

Added four isolated regressions for versions `6` / `2147483648`, each with load-first and save-first entry. In every fixture, `schemaVersion` appears **after** a future field with 20 nested objects. They assert original path existence and identical bytes, both `Save`/`Update` throwing, the update callback never running, and no corrupt-file backup. RED was observed before production edits:

```text
FAIL Future schema 6 ... (loadFirst=True): Assertion failed.
FAIL Future schema 6 ... (loadFirst=False): Expected InvalidOperationException.
FAIL Future schema 2147483648 ... (loadFirst=True): Assertion failed.
FAIL Future schema 2147483648 ... (loadFirst=False): Expected InvalidOperationException.
RESULT 42 passed; 4 failed.
```

`SettingsStore` now shares a private `Utf8JsonReader` header classifier across loading and write checking. It examines only top-level version tokens, uses a depth setting independent from current payload validation, skips unknown subtrees without building their object model, and recognizes every numeric token not representable as a supported integer 1–5 as unsupported. UTF-8 BOMs are handled. The original stream position is restored before the existing strict schema/field/depth parser runs. A detected unsupported schema blocks writes before either that parser or an update callback can run. Current file byte limits and the known/nonversioned corrupt recovery policy remain unchanged.

Two additional regressions retain the separate corrupt recovery policy for deeply nested known-schema and nonversioned settings: the corrupt JSON is quarantined with exact original bytes and a subsequent default save remains possible.

Only `SettingsStore.cs`, `tests/DesktopHub.Core.Tests/Program.cs`, and this report changed. No public interface changed. Fresh covering verification, exit 0:

```text
.tools/dotnet/dotnet.exe run --project tests/DesktopHub.Core.Tests -c Release --no-restore
PASS all four future-version entry cases
PASS both known/nonversioned corrupt recovery cases
RESULT 48 passed; 0 failed.
```

Full round-1 outputs are retained locally as `task-1-round1-red.log` / `task-1-round1-green.log` under the report directory and excluded from the commit. `git diff --check` passed. Resource/Scene were not rerun for this settings-only header fix; their prior 32/0 and 17/0 evidence above remains the applicable verification. User configuration, release folders and sources were untouched.
