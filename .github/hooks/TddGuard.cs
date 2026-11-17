// Single-file .NET 10 handler for Copilot hooks (camelCase event names) and phase verification.
//   git-safety                  preToolUse: block git push and destructive git commands for every agent
//   guard <role>                preToolUse: only let the role write files it owns (path allowlist)
//   autocommit <role>           agentStop:  block if the role's changes break its allowlist, else commit leftovers
//   verify <role> <baseCommit>  run by the /tdd skill after a phase: diff, test-count and dotnet test checks
// Hook output on stdout is a single JSON object. "{}" means no decision.
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var mode = args.ElementAtOrDefault(0) ?? string.Empty;
var role = args.ElementAtOrDefault(1) ?? string.Empty;

if (mode == "verify")
{
    var baseRef = args.ElementAtOrDefault(2);
    if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(baseRef))
    {
        Console.Error.WriteLine("Usage: verify <role> <baseCommit>");
        return 2;
    }
    return Verifier.Run(Directory.GetCurrentDirectory(), role, baseRef);
}

if (mode is not ("git-safety" or "guard" or "autocommit"))
{
    Console.Error.WriteLine($"Unknown mode '{mode}'. Expected git-safety, guard, autocommit or verify.");
    return 2;
}

JsonNode? payload = null;
try { payload = JsonNode.Parse(Console.In.ReadToEnd()); } catch (JsonException) { }

var repoRoot = payload?["cwd"]?.GetValue<string>() ?? Directory.GetCurrentDirectory();

if (mode == "autocommit")
{
    var alreadyBlocked = payload?["stop_hook_active"] is JsonValue flag && flag.TryGetValue<bool>(out var active) && active;
    Console.WriteLine(Hooks.StopHook(repoRoot, role, alreadyBlocked));
}
else
{
    Console.WriteLine(Hooks.ToolCall(mode, role, payload, repoRoot));
}

return 0;

static class Hooks
{
    static readonly Regex ShellTool = new("^(bash|powershell)$", RegexOptions.IgnoreCase);
    static readonly Regex EditTool = new(
        "^(edit|create|write|apply_patch|multi_edit|str_replace_editor|create_file|replace_string_in_file|insert_edit_into_file)$",
        RegexOptions.IgnoreCase);
    static readonly Regex ShellWrite = new(
        @"(>|\bSet-Content\b|\bAdd-Content\b|\bOut-File\b|\bNew-Item\b|\bRemove-Item\b|\bMove-Item\b|\bCopy-Item\b|\btee\b|\bsed\s+-i\b|\bmv\b|\bcp\b|\brm\b)",
        RegexOptions.IgnoreCase);
    static readonly Regex ShellPath = new(
        @"[\w\-./\\:~]+\.(?:cs|csproj|sln|slnx|props|targets|json|config|ya?ml|razor|cshtml|md)\b",
        RegexOptions.IgnoreCase);
    static readonly Regex PatchHeader = new(
        @"^(?:\*\*\* (?:Add File|Update File|Delete File|Move to):\s*|\+\+\+ b/)(.+)$",
        RegexOptions.Multiline);

    public static string ToolCall(string mode, string role, JsonNode? payload, string repoRoot)
    {
        var toolName = payload?["toolName"]?.GetValue<string>() ?? string.Empty;
        var toolArgs = NormalizeArgs(payload?["toolArgs"]);
        var strings = AllStrings(toolArgs).ToList();
        var text = string.Join('\n', strings);

        if (mode == "git-safety")
        {
            if (!ShellTool.IsMatch(toolName)) return Allow();

            if (Regex.IsMatch(text, @"\bgit\s+(-\S+\s+)*push\b", RegexOptions.IgnoreCase))
                return Deny("Git Safety Guard: 'git push' is blocked for AI agents. Review local commits and push manually.");

            if (Regex.IsMatch(text, @"\bgit\s+(-\S+\s+)*(reset\s+--hard|clean\s+-[a-z]*f|checkout\s+--\s+\.|restore\s+\.|branch\s+-D)", RegexOptions.IgnoreCase))
                return Deny("Git Safety Guard: destructive git commands (reset --hard, clean -f, checkout -- ., restore ., branch -D) are blocked.");

            return Allow();
        }

        if (EditTool.IsMatch(toolName))
        {
            var targets = ExtractTargets(toolArgs, strings);
            return targets.Count == 0
                ? Deny($"TDD Guard: cannot tell which file {role} is editing, so the edit is blocked.")
                : CheckTargets(role, targets, repoRoot);
        }

        if (ShellTool.IsMatch(toolName) && ShellWrite.IsMatch(text))
            return CheckTargets(role, ShellPath.Matches(text).Select(m => m.Value), repoRoot);

        return Allow();
    }

    public static string StopHook(string repoRoot, string role, bool alreadyBlocked)
    {
        var changed = Git.ChangedFiles(repoRoot);
        var candidates = new List<string>(changed);

        var prefix = Policy.CommitPrefix(role);
        if (prefix is not null && Git.Run(repoRoot, "log", "-1", "--format=%s").Out.StartsWith(prefix, StringComparison.Ordinal))
            candidates.AddRange(Git.Lines(Git.Run(repoRoot, "show", "--name-only", "--format=", "HEAD").Out));

        var violations = Policy.Violations(role, candidates, repoRoot);
        if (violations.Count > 0)
        {
            // After one forced retry, leave the tree dirty so the /tdd skill's verify step stops the loop.
            return alreadyBlocked
                ? Allow()
                : Block($"Phase boundary violation for {role}: {string.Join("; ", violations)}. Undo these changes (without git reset --hard) and commit only files you own.");
        }

        if (changed.Count > 0)
        {
            Git.Run(repoRoot, "add", "-A");
            Git.Run(repoRoot, "commit", "-m", Policy.AutoCommitMessage(role));
        }

        return Allow();
    }

    static string CheckTargets(string role, IEnumerable<string> targets, string repoRoot)
    {
        var violations = Policy.Violations(role, targets, repoRoot);
        return violations.Count == 0 ? Allow() : Deny($"TDD Guard: {violations[0]}");
    }

    static JsonNode? NormalizeArgs(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var s))
        {
            var trimmed = s.TrimStart();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            {
                try { return JsonNode.Parse(s); } catch (JsonException) { }
            }
        }
        return node;
    }

    static IEnumerable<string> AllStrings(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => [s],
        JsonObject o => o.SelectMany(p => AllStrings(p.Value)),
        JsonArray a => a.SelectMany(AllStrings),
        _ => []
    };

    static List<string> ExtractTargets(JsonNode? args, List<string> strings)
    {
        var targets = new List<string>();

        if (args is JsonObject obj)
        {
            foreach (var key in new[] { "path", "filePath", "file_path", "file", "filename" })
            {
                if (obj[key] is JsonValue pathValue && pathValue.TryGetValue<string>(out var path)) targets.Add(path);
            }

            if (obj["paths"] is JsonArray paths)
            {
                foreach (var item in paths)
                {
                    if (item is JsonValue itemValue && itemValue.TryGetValue<string>(out var itemPath)) targets.Add(itemPath);
                }
            }
        }

        foreach (var s in strings)
        {
            foreach (Match m in PatchHeader.Matches(s)) targets.Add(m.Groups[1].Value.Trim());
        }

        return targets;
    }

    static string Allow() => "{}";

    static string Deny(string reason) => new JsonObject
    {
        ["permissionDecision"] = "deny",
        ["permissionDecisionReason"] = reason
    }.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    static string Block(string reason) => new JsonObject
    {
        ["decision"] = "block",
        ["reason"] = reason
    }.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

static class Policy
{
    static readonly Regex TestSegment = new(@"(^|/)([^/]*\.(Tests?|Specs?)|tests?|specs?|__tests__)(/|$)", RegexOptions.IgnoreCase);
    static readonly Regex TestFile = new(@"(Tests?|Specs?)\.cs$", RegexOptions.IgnoreCase);
    static readonly Regex TestBuildInfra = new(@"(^|/)(Directory\.Packages\.props|[^/]+\.slnx?)$", RegexOptions.IgnoreCase);

    public static bool IsTestPath(string rel) => TestSegment.IsMatch(rel) || TestFile.IsMatch(rel);

    public static string Normalize(string path, string repoRoot)
    {
        var p = path.Trim().Trim('"', '\'');
        if (Path.IsPathRooted(p)) p = Path.GetRelativePath(repoRoot, p);
        p = p.Replace('\\', '/');
        while (p.StartsWith("./")) p = p[2..];
        return p;
    }

    public static List<string> Violations(string role, IEnumerable<string> paths, string repoRoot) =>
        paths.Select(p => Normalize(p, repoRoot))
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .Select(rel => Reason(role, rel))
             .OfType<string>()
             .ToList();

    static string? Reason(string role, string rel)
    {
        if (rel.StartsWith(".github/", StringComparison.OrdinalIgnoreCase) || rel.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))
            return $"'{rel}': agent and hook definitions are read-only";

        return role switch
        {
            "tdd-tester" when !IsTestPath(rel) && !TestBuildInfra.IsMatch(rel) =>
                $"tdd-tester may only change test projects, not '{rel}'",
            "tdd-implementer" or "tdd-refactorer" when IsTestPath(rel) =>
                $"{role} may not change tests, but touched '{rel}'",
            "intent-author" when !rel.StartsWith("intent/", StringComparison.OrdinalIgnoreCase) =>
                $"intent-author may only write under intent/, not '{rel}'",
            _ => null
        };
    }

    public static string? CommitPrefix(string role) => role switch
    {
        "intent-author" => "docs(intent):",
        "tdd-tester" => "test(red):",
        "tdd-implementer" => "feat(green):",
        "tdd-refactorer" => "refactor:",
        _ => null
    };

    public static string AutoCommitMessage(string role) => role switch
    {
        "intent-author" => "docs(intent): update intent specification",
        "tdd-tester" => "test(red): add failing behavioural test",
        "tdd-implementer" => "feat(green): implement minimal passing slice",
        "tdd-refactorer" => "refactor: clean up implementation",
        _ => $"chore({role}): auto-commit completed phase"
    };
}

static class Git
{
    public record Result(int Code, string Out);

    public static Result Run(string workingDir, params string[] gitArgs)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in gitArgs) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi);
        if (proc is null) return new Result(-1, string.Empty);
        var stderr = proc.StandardError.ReadToEndAsync();
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(10000);
        stderr.Wait(1000);
        return new Result(proc.HasExited ? proc.ExitCode : -1, output);
    }

    public static List<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static List<string> ChangedFiles(string repoRoot)
    {
        var parts = Run(repoRoot, "status", "--porcelain", "-z", "-uall").Out.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var files = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            var entry = parts[i];
            if (entry.Length < 4) continue;
            files.Add(entry[3..]);
            if ((entry[0] is 'R' or 'C' || entry[1] is 'R' or 'C') && i + 1 < parts.Length) files.Add(parts[++i]);
        }
        return files;
    }
}

static class Verifier
{
    static readonly Regex NewTest = new(@"^\+\s*\[(?:Fact|Theory|Test|TestMethod)\b", RegexOptions.Multiline);
    static readonly Regex BuildError = new(@"error (?:CS|MSB)\d+");

    public static int Run(string repoRoot, string role, string baseRef)
    {
        var violations = new List<string>();
        var warnings = new List<string>();
        string testTail = string.Empty;

        if (Git.Run(repoRoot, "rev-parse", "--verify", "--quiet", $"{baseRef}^{{commit}}").Code != 0)
        {
            Console.Error.WriteLine($"Base commit '{baseRef}' not found.");
            return 2;
        }

        var dirty = Git.ChangedFiles(repoRoot);
        if (dirty.Count > 0) violations.Add($"Uncommitted changes remain: {string.Join(", ", dirty)}");

        var committed = Git.Lines(Git.Run(repoRoot, "diff", "--name-only", $"{baseRef}..HEAD").Out);
        violations.AddRange(Policy.Violations(role, dirty.Concat(committed), repoRoot));

        int.TryParse(Git.Run(repoRoot, "rev-list", "--count", $"{baseRef}..HEAD").Out.Trim(), out var commitCount);
        if (commitCount == 0 && role is "tdd-tester" or "tdd-implementer" or "intent-author")
            violations.Add($"{role} made no commit.");

        if (role == "tdd-tester")
        {
            var diff = Git.Run(repoRoot, "diff", $"{baseRef}..HEAD", "--", "*.cs").Out;
            var added = NewTest.Matches(diff).Count;
            if (added != 1) violations.Add($"Red phase must add exactly one test, found {added}.");
        }

        if (role is "tdd-tester" or "tdd-implementer" or "tdd-refactorer")
        {
            var (code, output) = RunTests(repoRoot);
            testTail = output.Length > 2000 ? output[^2000..] : output;

            if (output.Contains("MSB1003"))
                violations.Add("No project or solution found to run dotnet test.");
            else if (role == "tdd-tester")
            {
                if (code == 0) violations.Add("The new test passes. The Red phase needs a failing test.");
                else if (BuildError.IsMatch(output)) warnings.Add("Red failed on a build error, not an assertion. Prefer a test that compiles and fails on behaviour.");
            }
            else if (code != 0)
            {
                violations.Add("dotnet test is not green.");
            }
        }

        var report = new JsonObject
        {
            ["role"] = role,
            ["ok"] = violations.Count == 0,
            ["violations"] = new JsonArray(violations.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
            ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()),
            ["testOutputTail"] = testTail
        };
        Console.WriteLine(report.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return violations.Count == 0 ? 0 : 1;
    }

    static (int Code, string Output) RunTests(string repoRoot)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add("--nologo");

        using var proc = Process.Start(psi);
        if (proc is null) return (-1, "Could not start dotnet.");
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(TimeSpan.FromMinutes(10)))
        {
            proc.Kill(true);
            return (-1, "dotnet test timed out after 10 minutes.");
        }

        proc.WaitForExit();
        return (proc.ExitCode, stdout.Result + stderr.Result);
    }
}
