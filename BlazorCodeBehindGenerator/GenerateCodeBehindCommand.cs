using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace BlazorCodeBehindGenerator
{
    /// <summary>
    /// Right-click command that extracts the <c>@code</c> / <c>@functions</c> block from a
    /// Razor component into a partial class code-behind file (<c>Component.razor.cs</c>).
    /// </summary>
    internal sealed class GenerateCodeBehindCommand
    {
        public const int CommandId = 0x0100;

        // MUST match guidBlazorCodeBehindGeneratorPackageCmdSet in the .vsct file.
        public static readonly Guid CommandSet = new Guid("6679ea19-51ad-483c-901c-c18d87136def");

        private readonly AsyncPackage package;

        private GenerateCodeBehindCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new OleMenuCommand(Execute, menuCommandID);
            menuItem.BeforeQueryStatus += OnBeforeQueryStatus;
            commandService.AddCommand(menuItem);
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
                new GenerateCodeBehindCommand(package, commandService);
        }

        /// <summary>Only show the command when every selected item is a .razor file.</summary>
        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (sender is not OleMenuCommand cmd)
                return;

            bool visible = false;
            var dte = (DTE)Package.GetGlobalService(typeof(DTE));
            if (dte?.SelectedItems != null && dte.SelectedItems.Count > 0)
            {
                visible = true;
                foreach (SelectedItem item in dte.SelectedItems)
                {
                    var path = TryGetFilePath(item);
                    if (path == null || !path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                    {
                        visible = false;
                        break;
                    }
                }
            }

            cmd.Visible = visible;
            cmd.Enabled = visible;
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dte = (DTE)Package.GetGlobalService(typeof(DTE));
            if (dte?.SelectedItems == null || dte.SelectedItems.Count == 0)
                return;

            foreach (SelectedItem item in dte.SelectedItems)
            {
                var projectItem = item.ProjectItem;
                var filePath = TryGetFilePath(item);
                if (projectItem == null || filePath == null ||
                    !filePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                GenerateCodeBehind(projectItem, filePath);
            }
        }

        private void GenerateCodeBehind(ProjectItem razorItem, string razorPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var csPath = razorPath + ".cs";
            if (File.Exists(csPath))
            {
                ShowMessage($"A code-behind file already exists:\n{Path.GetFileName(csPath)}", OLEMSGICON.OLEMSGICON_INFO);
                return;
            }

            var razorText = GetDocumentText(razorItem, razorPath);

            if (!TryFindCodeBlock(razorText, out var block))
            {
                ShowMessage("No @code or @functions block was found in this component.", OLEMSGICON.OLEMSGICON_INFO);
                return;
            }

            // Split the block into individual members and separate those that can move (plain C#)
            // from those that must stay in the .razor because they contain inline Razor markup
            // (an "@<tag>..." render template or "@:" line — that syntax is not legal C#).
            var members = SplitMembers(block.Body);
            var movable = new List<string>();
            var kept = new List<string>();
            foreach (var member in members)
            {
                if (ContainsRazorMarkupTransition(member))
                    kept.Add(member);
                else
                    movable.Add(member);
            }

            if (movable.Count == 0)
            {
                ShowMessage(
                    "Every member in this @code block contains inline Razor markup (e.g. \"@<div>...\"), " +
                    "which only compiles inside a .razor file. There is nothing that can be moved to a " +
                    "code-behind.\n\nNothing was changed.",
                    OLEMSGICON.OLEMSGICON_WARNING);
                return;
            }

            var className = Path.GetFileNameWithoutExtension(razorPath); // Foo.razor -> Foo
            var ns = GetNamespace(razorItem, razorPath);
            var usings = ExtractUsings(razorText);

            var csContent = BuildCodeBehind(ns, className, string.Join("\r\n\r\n", movable), usings);

            // If some members must stay behind, leave them in a residual @code block; otherwise
            // remove the block entirely.
            var newRazorText = kept.Count == 0
                ? RemoveBlock(block)
                : ReplaceBlockBody(block, Reindent(string.Join("\r\n\r\n", kept), "    "));

            // Write the new code-behind file first, then update the .razor.
            File.WriteAllText(csPath, csContent, new UTF8Encoding(true));
            SetDocumentText(razorItem, razorPath, newRazorText);

            // Add the code-behind to the project. Harmless (and swallowed) if the SDK
            // globbing already includes it or the editor auto-nests it.
            try
            {
                razorItem.ProjectItems.AddFromFile(csPath);
            }
            catch
            {
                // ignored — file is on disk and will be picked up by the project system
            }

            if (kept.Count > 0)
            {
                var names = string.Join(", ", kept.Select(MemberName).Take(4));
                if (kept.Count > 4)
                    names += ", ...";
                ShowMessage(
                    $"Extracted {movable.Count} member(s) to {Path.GetFileName(csPath)}.\n\n" +
                    $"{kept.Count} member(s) contain inline Razor markup (@<...>) and were kept in the " +
                    $".razor @code block:\n{names}\n\n" +
                    "Refactor those into child components if you want them in the code-behind too.",
                    OLEMSGICON.OLEMSGICON_INFO);
            }
        }

        /// <summary>
        /// Builds a partial-class code-behind file with a file-scoped namespace.
        /// </summary>
        private static string BuildCodeBehind(string ns, string className, string codeBody, IEnumerable<string> razorUsings)
        {
            var usings = new List<string> { "System", "Microsoft.AspNetCore.Components" };
            foreach (var u in razorUsings)
            {
                if (!usings.Contains(u))
                    usings.Add(u);
            }

            var sb = new StringBuilder();
            foreach (var u in usings)
                sb.Append("using ").Append(u).AppendLine(";");
            sb.AppendLine();
            sb.Append("namespace ").Append(ns).AppendLine(";");
            sb.AppendLine();
            sb.Append("public partial class ").AppendLine(className);
            sb.AppendLine("{");
            sb.AppendLine(Reindent(codeBody, "    "));
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>The located <c>@code</c>/<c>@functions</c> block and the markup around it.</summary>
        private sealed class CodeBlock
        {
            public string Keyword = "@code";
            public string Body = string.Empty;
            public string Before = string.Empty;
            public string After = string.Empty;
        }

        /// <summary>
        /// Finds the first <c>@code { ... }</c> or <c>@functions { ... }</c> block and returns its
        /// inner body together with the markup that precedes and follows it.
        /// </summary>
        private static bool TryFindCodeBlock(string razor, out CodeBlock block)
        {
            block = new CodeBlock();

            var keyword = "@code";
            int directive = FindDirective(razor, keyword);
            if (directive < 0)
            {
                keyword = "@functions";
                directive = FindDirective(razor, keyword);
            }
            if (directive < 0)
                return false;

            int open = razor.IndexOf('{', directive);
            if (open < 0)
                return false;

            int close = FindMatchingBrace(razor, open);
            if (close < 0)
                return false;

            block.Keyword = keyword;
            block.Body = razor.Substring(open + 1, close - open - 1);
            block.Before = razor.Substring(0, directive).TrimEnd();
            block.After = razor.Substring(close + 1).TrimStart('\r', '\n');
            return true;
        }

        /// <summary>Rebuilds the Razor markup with the whole code block removed.</summary>
        private static string RemoveBlock(CodeBlock block)
        {
            var sb = new StringBuilder();
            sb.Append(block.Before);
            if (block.Before.Length > 0 && block.After.Length > 0)
                sb.Append("\r\n");
            sb.Append(block.After);
            return sb.ToString().TrimEnd() + "\r\n";
        }

        /// <summary>Rebuilds the Razor markup with a residual code block containing <paramref name="body"/>.</summary>
        private static string ReplaceBlockBody(CodeBlock block, string body)
        {
            var sb = new StringBuilder();
            sb.Append(block.Before);
            if (block.Before.Length > 0)
                sb.Append("\r\n\r\n");
            sb.Append(block.Keyword).Append(" {\r\n");
            sb.Append(body);
            sb.Append("\r\n}");
            if (block.After.Length > 0)
                sb.Append("\r\n\r\n").Append(block.After);
            return sb.ToString().TrimEnd() + "\r\n";
        }

        /// <summary>
        /// Splits a class-body into its top-level members (fields, methods, properties, ...).
        /// Tracks (), [] and {} nesting and skips strings/comments so a member only ends at a
        /// top-level <c>;</c> or at the <c>}</c> that closes a member body (allowing for an
        /// auto-property initializer or an object/collection initializer that follows).
        /// </summary>
        private static List<string> SplitMembers(string body)
        {
            var members = new List<string>();
            int paren = 0, bracket = 0, brace = 0;
            int start = 0;

            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                switch (c)
                {
                    case '"':
                        i = (i > 0 && body[i - 1] == '@') ? SkipVerbatimString(body, i) : SkipString(body, i, '"');
                        continue;
                    case '\'':
                        i = SkipString(body, i, '\'');
                        continue;
                    case '/' when i + 1 < body.Length && body[i + 1] == '/':
                        i = SkipLineComment(body, i);
                        continue;
                    case '/' when i + 1 < body.Length && body[i + 1] == '*':
                        i = SkipBlockComment(body, i);
                        continue;
                    case '(': paren++; break;
                    case ')': if (paren > 0) paren--; break;
                    case '[': bracket++; break;
                    case ']': if (bracket > 0) bracket--; break;
                    case '{': brace++; break;
                    case '}':
                        if (brace > 0) brace--;
                        if (brace == 0 && paren == 0 && bracket == 0)
                        {
                            // A member body just closed. If what follows is "=" (auto-property
                            // initializer) or ";" (object/collection initializer), the member
                            // continues; the ";" rule below will end it. Otherwise it ends here.
                            char next = PeekNonTrivia(body, i + 1);
                            if (next != '=' && next != ';')
                            {
                                AddMember(members, body, start, i);
                                start = i + 1;
                            }
                        }
                        break;
                    case ';':
                        if (brace == 0 && paren == 0 && bracket == 0)
                        {
                            AddMember(members, body, start, i);
                            start = i + 1;
                        }
                        break;
                }
            }

            // Trailing content with no terminator (rare) — keep it if it has real code.
            if (start < body.Length && body.Substring(start).Trim().Length > 0)
                AddMember(members, body, start, body.Length - 1);

            return members;
        }

        private static void AddMember(List<string> members, string body, int start, int endInclusive)
        {
            var text = body.Substring(start, endInclusive - start + 1).Trim();
            if (text.Length > 0)
                members.Add(text);
        }

        /// <summary>Returns the next non-whitespace, non-comment character at or after <paramref name="index"/>, or '\0'.</summary>
        private static char PeekNonTrivia(string text, int index)
        {
            for (int i = index; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                    continue;
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { i = SkipLineComment(text, i); continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') { i = SkipBlockComment(text, i); continue; }
                return c;
            }
            return '\0';
        }

        /// <summary>Best-effort friendly name for a member, for display in the summary dialog.</summary>
        private static string MemberName(string member)
        {
            // Drop leading attribute lines, comments, and blank lines.
            var lines = member.Replace("\r\n", "\n").Split('\n')
                .SkipWhile(l =>
                {
                    var t = l.TrimStart();
                    return t.Length == 0 || t.StartsWith("//") || t.StartsWith("[");
                });
            var decl = string.Join(" ", lines);

            int cut = decl.Length;
            foreach (var tok in new[] { "=>", "{", ";", "=" })
            {
                int idx = decl.IndexOf(tok, StringComparison.Ordinal);
                if (idx >= 0)
                    cut = Math.Min(cut, idx);
            }

            var head = decl.Substring(0, cut);
            int openParen = head.IndexOf('(');
            var scan = openParen >= 0 ? head.Substring(0, openParen) : head;

            var ids = Regex.Matches(scan, @"[A-Za-z_]\w*");
            if (ids.Count > 0)
                return ids[ids.Count - 1].Value + (openParen >= 0 ? "()" : string.Empty);
            return "(member)";
        }

        /// <summary>
        /// Detects a Razor markup transition (<c>@&lt;tag&gt;</c> render template or a <c>@:</c> line)
        /// inside a code block. Such members must stay in the .razor file. Skips strings and comments
        /// so occurrences inside literals don't count.
        /// </summary>
        private static bool ContainsRazorMarkupTransition(string code)
        {
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                switch (c)
                {
                    case '"':
                        i = (i > 0 && code[i - 1] == '@') ? SkipVerbatimString(code, i) : SkipString(code, i, '"');
                        continue;
                    case '\'':
                        i = SkipString(code, i, '\'');
                        continue;
                    case '/' when i + 1 < code.Length && code[i + 1] == '/':
                        i = SkipLineComment(code, i);
                        continue;
                    case '/' when i + 1 < code.Length && code[i + 1] == '*':
                        i = SkipBlockComment(code, i);
                        continue;
                    case '@' when i + 1 < code.Length && (code[i + 1] == '<' || code[i + 1] == ':'):
                        return true;
                }
            }
            return false;
        }

        /// <summary>Collects <c>@using</c> directives so the code-behind can compile against the same types.</summary>
        private static List<string> ExtractUsings(string razor)
        {
            var result = new List<string>();
            using var reader = new StringReader(razor);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("@using ", StringComparison.Ordinal))
                {
                    var ns = trimmed.Substring("@using ".Length).Trim().TrimEnd(';');
                    if (ns.Length > 0)
                        result.Add(ns);
                }
            }
            return result;
        }

        /// <summary>
        /// Finds a Razor directive block (e.g. <c>@code</c>) — the keyword must be followed,
        /// after optional whitespace, by an opening brace. This rejects the keyword when it
        /// merely appears as text in the markup (e.g. "the @code block").
        /// </summary>
        private static int FindDirective(string text, string directive)
        {
            int from = 0;
            while (true)
            {
                int idx = text.IndexOf(directive, from, StringComparison.Ordinal);
                if (idx < 0)
                    return -1;

                int after = idx + directive.Length;
                int j = after;
                while (j < text.Length && char.IsWhiteSpace(text[j]))
                    j++;

                // Require whitespace (or brace) right after the keyword AND a brace as the
                // next non-whitespace character, so "@codeword" and "@code word" don't match.
                bool boundary = after >= text.Length || char.IsWhiteSpace(text[after]) || text[after] == '{';
                if (boundary && j < text.Length && text[j] == '{')
                    return idx;

                from = after;
            }
        }

        /// <summary>
        /// Given the index of an opening brace, returns the index of the matching closing brace,
        /// skipping braces that appear inside strings, chars, and comments.
        /// </summary>
        private static int FindMatchingBrace(string text, int openIndex)
        {
            int depth = 0;
            for (int i = openIndex; i < text.Length; i++)
            {
                char c = text[i];

                switch (c)
                {
                    case '"':
                        i = (i > 0 && text[i - 1] == '@') ? SkipVerbatimString(text, i) : SkipString(text, i, '"');
                        continue;
                    case '\'':
                        i = SkipString(text, i, '\'');
                        continue;
                    case '/' when i + 1 < text.Length && text[i + 1] == '/':
                        i = SkipLineComment(text, i);
                        continue;
                    case '/' when i + 1 < text.Length && text[i + 1] == '*':
                        i = SkipBlockComment(text, i);
                        continue;
                    case '{':
                        depth++;
                        break;
                    case '}':
                        depth--;
                        if (depth == 0)
                            return i;
                        break;
                }
            }
            return -1;
        }

        private static int SkipString(string text, int start, char quote)
        {
            for (int i = start + 1; i < text.Length; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == quote) return i;
                if (text[i] == '\n') return i; // unterminated; bail on this line
            }
            return text.Length - 1;
        }

        private static int SkipVerbatimString(string text, int start)
        {
            for (int i = start + 1; i < text.Length; i++)
            {
                if (text[i] == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { i++; continue; } // "" escape
                    return i;
                }
            }
            return text.Length - 1;
        }

        private static int SkipLineComment(string text, int start)
        {
            int nl = text.IndexOf('\n', start);
            return nl < 0 ? text.Length - 1 : nl;
        }

        private static int SkipBlockComment(string text, int start)
        {
            int end = text.IndexOf("*/", start + 2, StringComparison.Ordinal);
            return end < 0 ? text.Length - 1 : end + 1;
        }

        /// <summary>Removes the common leading indentation from a block and re-indents it with <paramref name="indent"/>.</summary>
        private static string Reindent(string body, string indent)
        {
            var lines = body.Replace("\r\n", "\n").Split('\n').ToList();

            while (lines.Count > 0 && lines[0].Trim().Length == 0)
                lines.RemoveAt(0);
            while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0)
                lines.RemoveAt(lines.Count - 1);

            if (lines.Count == 0)
                return string.Empty;

            int common = int.MaxValue;
            foreach (var line in lines)
            {
                if (line.Trim().Length == 0)
                    continue;
                int leading = line.Length - line.TrimStart(' ', '\t').Length;
                common = Math.Min(common, leading);
            }
            if (common == int.MaxValue)
                common = 0;

            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.Trim().Length == 0)
                {
                    // keep blank lines blank
                }
                else
                {
                    sb.Append(indent).Append(line.Substring(Math.Min(common, line.Length)).TrimEnd());
                }
                if (i < lines.Count - 1)
                    sb.Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Computes the component namespace the way Blazor does: root namespace plus the
        /// folder path relative to the project directory.
        /// </summary>
        private string GetNamespace(ProjectItem projectItem, string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var project = projectItem.ContainingProject;
            string root;
            try
            {
                root = project.Properties.Item("DefaultNamespace").Value.ToString();
            }
            catch
            {
                root = SanitizeSegment(Path.GetFileNameWithoutExtension(project.FullName));
            }

            try
            {
                var projDir = Path.GetDirectoryName(project.FullName);
                var fileDir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(projDir) && !string.IsNullOrEmpty(fileDir) &&
                    fileDir.StartsWith(projDir, StringComparison.OrdinalIgnoreCase))
                {
                    var rel = fileDir.Substring(projDir.Length).Trim('\\', '/');
                    if (rel.Length > 0)
                    {
                        var segments = rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
                                          .Select(SanitizeSegment);
                        root = root + "." + string.Join(".", segments);
                    }
                }
            }
            catch
            {
                // fall back to the root namespace
            }

            return root;
        }

        private static string SanitizeSegment(string segment)
        {
            var sb = new StringBuilder(segment.Length);
            foreach (var c in segment)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            if (sb.Length > 0 && char.IsDigit(sb[0]))
                sb.Insert(0, '_');
            return sb.ToString();
        }

        /// <summary>Reads the file from its open editor buffer if available, otherwise from disk.</summary>
        private static string GetDocumentText(ProjectItem item, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var textDoc = TryGetOpenTextDocument(item);
            if (textDoc != null)
            {
                var start = textDoc.StartPoint.CreateEditPoint();
                return start.GetText(textDoc.EndPoint);
            }
            return File.ReadAllText(path);
        }

        /// <summary>Writes back to the open editor buffer if the file is open, otherwise to disk.</summary>
        private static void SetDocumentText(ProjectItem item, string path, string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var textDoc = TryGetOpenTextDocument(item);
            if (textDoc != null)
            {
                var start = textDoc.StartPoint.CreateEditPoint();
                start.ReplaceText(textDoc.EndPoint, text, (int)vsEPReplaceTextOptions.vsEPReplaceTextKeepMarkers);
                return;
            }
            File.WriteAllText(path, text, new UTF8Encoding(true));
        }

        private static TextDocument? TryGetOpenTextDocument(ProjectItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (item.Document != null)
                    return item.Document.Object("TextDocument") as TextDocument;
            }
            catch
            {
                // not open, or not a text document
            }
            return null;
        }

        private static string? TryGetFilePath(SelectedItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return item.ProjectItem?.FileNames[1];
            }
            catch
            {
                return null;
            }
        }

        private void ShowMessage(string message, OLEMSGICON icon)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.ShowMessageBox(
                package,
                message,
                "Blazor Code-Behind Generator",
                icon,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }
}
