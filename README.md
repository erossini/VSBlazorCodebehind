# Visual Studio 2026 Blazor Code-Behind extension

A Visual Studio 2026 right-click extension that extracts the `@code` block from a Razor
component into a partial-class code-behind file — filling the gap left when VS 2026 removed
the built-in "extract to code-behind" feature. (VS still has **Add `Component.razor.cs`**,
but that only creates an *empty* code-behind; it does not move your `@code` into it.)

<img width="528" height="929" alt="image" src="https://github.com/user-attachments/assets/4a1dfed1-1500-42b5-bd35-44f108d72771" />

## Usage

1. In **Solution Explorer**, right-click a `.razor` file.
2. Choose **Extract Blazor Code-Behind**.

The command:

- Finds the first `@code { … }` (or `@functions { … }`) block.
- Splits it into individual members and moves every member that is plain C# into
  `Component.razor.cs` — a `public partial class Component` with a file-scoped namespace
  computed as *root namespace + folder path* (the way Blazor names components).
- Carries over the component's `@using` directives, plus `System` and
  `Microsoft.AspNetCore.Components`.
- Any member that contains inline Razor markup (`@<…>`) stays behind in a residual `@code`
  block (see the next section). If none do, the `@code` block is removed entirely.
- Updates the `.razor` by editing the live editor buffer if the file is open, so VS doesn't
  raise a "file changed externally" prompt.

The item only appears for `.razor` files (dynamic visibility via `BeforeQueryStatus`).

## Building & installing

This is a `net472` VSIX package — build it with **MSBuild from Visual Studio**, not
`dotnet build`:

```
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" ^
    BlazorCodeBehindGenerator\BlazorCodeBehindGenerator.csproj -t:Rebuild -restore
```

Output: `BlazorCodeBehindGenerator\bin\Debug\net472\BlazorCodeBehindGenerator.vsix`.

Building **produces** the `.vsix` but does **not** install it. To try it:

- **F5** the project → launches a separate *Experimental Instance* of VS (a sandbox). Open a
  Blazor solution there and right-click a `.razor` file. Nothing is permanently installed.
- **Install:** close all VS windows, double-click the `.vsix`, install, reopen VS.

## Inline Razor markup (`@<…>`) stays in the `.razor` — partial extraction

Razor lets you write render templates directly inside `@code`, e.g. a `RenderFragment<T>`:

```csharp
Template = job => @<span class="badge">@job.Status</span>
```

That `@<span>` / `@<div>` — the `@<…>` **markup transition** — is a *Razor language* feature.
The Razor compiler rewrites it into render-tree code, but it is **not valid C#**. Moved
verbatim into a plain `.cs` file it will not compile. This is a hard rule of Blazor, not a
limitation of the extension.

Rather than refuse the whole block, the extension does a **member-level partial extraction**:

- It splits the `@code` block into individual members (fields, methods, properties) by tracking
  `()`/`[]`/`{}` nesting and skipping strings and comments.
- Every member that is **plain C#** moves to the code-behind.
- Any member that **contains `@<…>` (or `@:`)** stays behind in a residual `@code` block in the
  `.razor`. Because it's one partial class, members reference each other freely across the two
  files.
- Afterwards it reports which members were kept behind, e.g. *"BuildColumns() contains inline
  Razor markup and was kept in the .razor @code block."*
- If **every** member contains markup, there's nothing to move, so it warns and changes nothing.

So a component whose only markup-bearing member is `BuildColumns` gets everything else — all
its fields, `OnInitializedAsync`, `LoadAsync`, event handlers, etc. — moved to code-behind
automatically, leaving just `BuildColumns` in the `.razor`.

### How to move the *rest* (the markup members) into code-behind too

Move the markup out of C# and into a `.razor` — the logic can still go to code-behind.

1. **Pull each `@<…>` template into its own child component.** Put the badge markup in a
   `JobStatusBadge.razor`, the row buttons in a `JobRowActions.razor`, etc. Pass data in as
   `[Parameter]`s and surface parent actions as `EventCallback<T>` parameters instead of
   calling parent methods directly.

2. **Keep a tiny `@code` block in the `.razor`** that holds only the cell templates — because
   the templates *reference* those child components, they still contain a `@<…>` transition
   and must live in the `.razor`:

   ```razor
   @code {
       private RenderFragment<Job> _statusTemplate  = default!;
       private RenderFragment<Job> _actionsTemplate = default!;

       private void BuildCellTemplates()
       {
           _statusTemplate  = job => @<JobStatusBadge Status="job.Status" />;
           _actionsTemplate = job => @<JobRowActions Job="job" OnDetails="OpenDetails" OnHide="HideAsync" />;
       }
   }
   ```

   > These can't be field initializers (C# forbids referencing instance members like
   > `OpenDetails` there) and can't move to the `.cs` (they contain `@<…>`), so assigning them
   > in a small method inside the `.razor` is the right home.

3. **Move everything else to the code-behind.** All fields and methods — including
   `BuildColumns` — become plain C#, referencing the templates by field:

   ```csharp
   new() { Title = "Status", Sort = j => j.Status, Template = _statusTemplate },
   new() { Title = "",       CellClass = "actions", Template = _actionsTemplate },
   ```

After this, only ~6 lines of template glue remain in the `.razor`; all the real logic lives in
`Component.razor.cs`.

> Reminder about namespaces: `_Imports.razor` global usings do **not** apply to `.cs` files.
> If the extracted code-behind references types that were only reachable via `_Imports.razor`,
> add explicit `using` statements to the `.cs`.
