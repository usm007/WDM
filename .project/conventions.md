# Conventions

**FACT (observed in source):**
- `Nullable=enable`, `ImplicitUsings=disable` — new files must add needed `using`s explicitly;
  check `GlobalUsings.cs` first (System, IO, Linq, `Net.Http`, Threading + `TaskStatus` alias).
- Async suffix (`…Async`), `CancellationToken` plumbed through engine/media paths; `ConfigureAwait`
  discipline follows existing file — match neighbors.
- Static service classes (`static class XService`) with static events; `sealed` engine/VM classes;
  `INotifyPropertyChanged` on VM + model; `RelayCommand` for commands; converters live in `ViewModels/`.
- Docs/comments: XML `///` on public service methods (see `UpdateChecker.LaunchInstaller`); inline
  `[Update]`-style `Debug.WriteLine` tags in update path.
- XAML: WPF-UI controls (`ui:`), resources via `Themes/Theme.xaml` + palette dictionaries;
  custom chrome via `Controls/WdmWindow.cs` (`WindowStyle=None`, `WindowChrome`).
- Persistence via `TaskStore` + `AtomicFile.Write` (never raw `File.WriteAllText` for state).
- Filenames: `SanitizeFileName`/`FileNameHelper` for anything from network; extension→category via
  `DownloadTask.Categorize`.

**INFERENCE:** Commit style is conventional-commits-ish (`fix:`, `chore:`, `release:`) per `git log`.
Match it. PowerShell scripts stay PS 5.1-compatible (see `1fd26e8 Join-Path` fix).

**Do not:** add DI frameworks, cross-platform abstractions, new test frameworks, or per-host
embed branches (`EmbedParsers` is shape-based by design — keep it generic).
