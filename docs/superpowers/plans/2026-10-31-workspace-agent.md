# Workspace Agent Implementation Plan

Goal: deliver a native WPF local coding agent using a user-configured chat/completions model with function tools.
Architecture: independent workspace tools, model client/session loop, and UI. No new runtime dependencies. No git repository exists here; do not create a worktree or commits.

## Tasks

- [x] 1. WorkspaceTools: typed file/search/edit/journal/command tools and temporary-directory checks. Tool calls accept JSON arguments; execute actual operations, return JSON/text error information. Restrict filesystem tools to validated workspace, use SHA-256 compare-before-write and conservative restore. Terminal commands separately authorized, cancellable, output bounded. Native process execution, no shell command string construction by host.
- [x] 2. AgentModelClient and AgentSession: chat/completions tools protocol with intact tool_call IDs, error handling, bounded context and turns, repeated calls, cancellation, encrypted task record. HTTP fixture checks exercise write/build/fix loop. Session captures settings once, no mixing keys or endpoints during a task. Shared workspace locking prevents concurrent agents for overlapping directories.
- [x] 3. Settings + WPF AgentWindow: model reuses Model connection; editable Agent prompt/limits. Workbench lists tasks, input, controls, native approval dialogs, changes and output; open from Settings and Chat without changing pet right-click menu. Async operations never block UI; shutdown cancels all sessions.
- [x] 4. Integration and review: run tools/model checks, existing Store/Chat/Integration checks, actual WPF mock-HTTP path, publish to new directory then smoke-test. Update README with precise capabilities and limitations. Preserve any live user program while publishing.

## Shared contracts

`AgentToolCall(string Id,string Name,string Arguments)`, `AgentTurn(string Text,IReadOnlyList<AgentToolCall> ToolCalls)`, `AgentEvent(DateTimeOffset At,string Kind,string Text)` in `Naifrog.Agent`.

`WorkspaceTools(string workspace,string journalDirectory,bool allowEdits,Func<string,string,CancellationToken,Task<bool>> approveCommand,int commandTimeoutSeconds=120)`; properties `Root`, `Changes` (`IReadOnlyList<AgentFileChange>`), `Definitions` (`JsonArray`); `ExecuteAsync(AgentToolCall,CancellationToken)` returns `Task<string>`; `UndoAsync(CancellationToken)` returns `Task<string>`; `AgentFileChange` has `Path`, `Before`, `After` (nullable string), `BeforeHash`, `AfterHash`. Journal encryption may use Core.LocalVault and AtomicFile. Tool schemas names: list_files, read_file, search_text, write_file, replace_text, delete_file, run_command, git_status, git_diff. File tools' arguments documented in schemas.

`AgentModelClient(SettingsStore,HttpMessageHandler?=null)` disposable; `CompleteAsync(AppSettings,string apiKey,JsonArray messages,JsonArray tools,CancellationToken)` returns `Task<AgentTurn>`. No mutable connection state.

`AgentSession(SettingsStore,string workspace,bool allowEdits,Func<string,string,CancellationToken,Task<bool>> approveCommand,HttpMessageHandler? handler=null)` disposable. Properties `Id`, `Workspace`, `Status`, `IsRunning`, `Events` (`IReadOnlyList<AgentEvent>`), `Tools` (`WorkspaceTools`). Event `Action? Changed`. `RunAsync(string prompt,CancellationToken)` returns `Task<string>`; `Cancel()`; `UndoAsync(CancellationToken)` returns `Task<string>`. Session captures settings per run; continuation preserves messages. Durable record DPAPI protected in data dir/AgentTasks/ID. Overlapping workspace task runs rejected globally in process; file mutations use compare hashes. Max 3 active tasks. No automatic retries after tools execute. User can retry by continuing with explicit instruction.

`AppSettings.Agent` (`AgentSettings`) root-owned: SystemPrompt string, MaxRounds int=40 [1..100], MaxContextChars int=160000 [16000..400000], CommandTimeoutSeconds int=120 [5..1800], MaxTokens int=4096 [512..16384]. Root owns AppSettings/AgentSettings. Other agents only read these contracts.
