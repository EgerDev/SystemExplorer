namespace SystemExplorer.Core;

public enum BloatConfidence { Low, Medium, High }
public sealed record DeveloperLocationContext(string UserProfile, string LocalAppData, string RoamingAppData, string ProgramData, string Temp,
    IReadOnlyDictionary<string, string>? EnvironmentOverrides = null);
public sealed record BloatItem(string Category, string Tool, string Path, long AllocatedBytes, long? ReclaimableBytes,
    DateTime? LatestWrite, BloatConfidence Confidence, string Reason, string? CleanupCommand, string? CleanupWarning, string Kind, bool IsLead = false);
public sealed record DeveloperProject(string Path, StorageMarkers Kinds, long AllocatedBytes, long RegenerableBytes,
    IReadOnlyList<BloatItem> Artifacts, DateTime? LastActivity, bool IsStale, bool HasAIMarkers);
public sealed record BloatCategoryTotal(string Category, long AllocatedBytes, long? ReclaimableBytes, int UnknownItems = 0);
public sealed record DeveloperBloatReport(IReadOnlyList<BloatItem> Items, IReadOnlyList<DeveloperProject> Projects,
    IReadOnlyList<BloatCategoryTotal> CategoryTotals, IReadOnlyList<StorageProblem> NotScanned, int AbsentCatalogEntries)
{
    public long KnownReclaimableBytes => CategoryTotals.Sum(x => x.ReclaimableBytes ?? 0);
    public int UnknownItems => CategoryTotals.Sum(x => x.UnknownItems);
}

public static class DeveloperBloatAnalyzer
{
    private const StorageMarkers ProjectMarkers = StorageMarkers.Git | StorageMarkers.Worktree | StorageMarkers.Node | StorageMarkers.DotNet | StorageMarkers.Solution | StorageMarkers.Rust | StorageMarkers.Python | StorageMarkers.Go | StorageMarkers.Maven | StorageMarkers.Gradle | StorageMarkers.Terraform;
    public static DeveloperBloatReport Analyze(StorageScanResult scan, DeveloperLocationContext ctx, DateTimeOffset now, TimeSpan staleAfter)
    {
        var paths = Enumerable.Range(0, scan.Nodes.Count).Select(scan.FullPath).ToArray();
        var index = paths.Select((p, i) => (p, i)).ToDictionary(x => StoragePaths.Normalize(x.p), x => x.i, StringComparer.OrdinalIgnoreCase);
        var items = new List<BloatItem>(); var outside = new List<StorageProblem>(); var absent = 0;
        var cacheRoots = new HashSet<int>();
        string At(string root, string tail) => StoragePaths.Join(root, tail);
        string Env(string key, string fallback) => ctx.EnvironmentOverrides?.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } v ? v : fallback;
        void Catalog(string tool, string path, string kind = "Cache", string? command = null, string? warning = null, BloatConfidence? confidence = null, string? reason = null)
        {
            path = StoragePaths.Native(path);
            if (!StoragePaths.Contains(scan.Root, path)) { outside.Add(new(path, "Not scanned (outside scanned folder)")); return; }
            var matches = Resolve(path).ToArray();
            if (matches.Length == 0) absent++;
            foreach (var i in matches)
            {
                cacheRoots.Add(i);
                var n = scan.Nodes[i];
                var user = kind is "AI tool data" or "User data";
                var model = kind == "AI model store";
                items.Add(new(kind.StartsWith("AI") ? "AI tools and models" : "Global caches and tools", tool, paths[i], n.AllocatedBytes,
                    kind == "Virtual disk" ? null : user ? 0 : n.AllocatedBytes, n.LatestWrite, confidence ?? (model ? BloatConfidence.Low : BloatConfidence.High),
                    reason ?? (user ? "contains your sessions/config; not regenerable" : model ? "Model store; re-download required" : kind == "Virtual disk" ? "Virtual disk size is not reclaimable space; compaction may be required" : "Known tool location; review before cleanup"),
                    command, warning ?? "Not executed. Review contents and tool documentation before cleanup.", kind));
            }
        }
        IEnumerable<int> Resolve(string pattern)
        {
            var norm = StoragePaths.Normalize(pattern);
            if (!norm.Contains('*')) return index.TryGetValue(norm, out var i) ? [i] : [];
            var relative = norm[(StoragePaths.Normalize(scan.Root).Length)..].TrimStart('/').Split('/');
            IEnumerable<int> current = [0];
            foreach (var segment in relative)
            {
                var s = segment;
                current = current.SelectMany(i => scan.Nodes[i].Children).Where(i => Match(scan.Nodes[i].Name, s)).ToArray();
            }
            return current;
        }
        var home = ctx.UserProfile; var local = ctx.LocalAppData; var roaming = ctx.RoamingAppData;
        foreach (var (tool, tail, cmd) in new (string, string, string?)[] {
            ("npm", "npm-cache", "npm cache clean --force"), ("pnpm", "pnpm/store", "pnpm store prune"), ("Yarn classic cache", "Yarn/Cache", "yarn cache clean"),
            ("NuGet HTTP cache", "NuGet/v3-cache", "dotnet nuget locals all --clear"), ("pip", "pip/Cache", "pip cache purge"), ("uv", "uv/cache", "uv cache clean"),
            ("Poetry", "pypoetry/Cache", null), ("Go build", "go-build", "go clean -cache"), ("JetBrains", "JetBrains", null), ("Visual Studio", "Microsoft/VisualStudio", null) }) Catalog(tool, At(local, tail), command: cmd);
        foreach (var (tool, tail, cmd) in new (string, string, string?)[] {
            ("Yarn Berry cache", ".yarn/berry/cache", "yarn cache clean"), ("Bun", ".bun/install/cache", null),
            ("conda user packages", ".conda/pkgs", "conda clean --all"), ("Anaconda packages", "anaconda3/pkgs", "conda clean --all"), ("Miniconda packages", "miniconda3/pkgs", "conda clean --all"),
            ("Maven", ".m2/repository", null) }) Catalog(tool, At(home, tail), command: cmd);
        Catalog("NuGet packages", Env("NUGET_PACKAGES", At(home, ".nuget/packages")), command: "dotnet nuget locals all --clear");
        var cargo = Env("CARGO_HOME", At(home, ".cargo"));
        Catalog("Cargo registry", At(cargo, "registry")); Catalog("Cargo Git cache", At(cargo, "git"));
        Catalog("rustup", At(home, ".rustup/toolchains"), "User data");
        Catalog("Go modules", Env("GOMODCACHE", At(Env("GOPATH", At(home, "go")), "pkg/mod")), command: "go clean -modcache");
        var gradle = Env("GRADLE_USER_HOME", At(home, ".gradle"));
        Catalog("Gradle caches", At(gradle, "caches")); Catalog("Gradle distributions", At(gradle, "wrapper/dists"));
        Catalog("Docker Desktop", At(local, "Docker/wsl"), "Virtual disk", "docker system prune; compact VHDX separately", "Destroys unused images/containers. Not executed.");
        Catalog("WSL", At(local, "Packages/*Canonical*/LocalState"), "Virtual disk"); Catalog("WSL", At(local, "wsl/*"), "Virtual disk");
        Catalog("Android SDK", At(local, "Android/Sdk"), "User data"); Catalog("Android AVD", At(home, ".android/avd"), "User data");
        foreach (var tail in new[] { "Cache", "CachedData", "User/workspaceStorage" }) Catalog("VS Code " + (tail == "Cache" ? "cache" : tail == "CachedData" ? "cached data" : "workspace storage"), At(roaming, "Code/" + tail), tail == "User/workspaceStorage" ? "User data" : "Cache");
        Catalog("VS Code extensions", At(home, ".vscode/extensions"), "User data");
        Catalog("VS package cache", At(ctx.ProgramData, "Package Cache")); Catalog("User temp", ctx.Temp, confidence: BloatConfidence.Medium, reason: "files may be in use by running programs; delete only items not in use");
        foreach (var (tool, tail) in new[] { ("Claude Code", ".claude"), ("Claude transcripts", ".claude/projects"), ("Codex", ".codex"), ("Codex sessions", ".codex/sessions"), ("Codex worktrees", ".codex/worktrees"), ("Grok CLI", ".grok"), ("Gemini CLI", ".gemini"), ("Cursor user data", ".cursor"), ("Windsurf user data", ".windsurf"), ("Codeium", ".codeium"), ("Continue", ".continue") }) Catalog(tool, At(home, tail), "AI tool data");
        foreach (var (tool, tail) in new[] { ("Claude desktop", "Claude"), ("Cursor application data", "Cursor"), ("Windsurf application data", "Windsurf"), ("Cline", "Code/User/globalStorage/saoudrizwan.claude-dev"), ("Roo", "Code/User/globalStorage/rooveterinaryinc.roo-cline") }) Catalog(tool, At(roaming, tail), "AI tool data");
        Catalog("Claude desktop local data", At(local, "AnthropicClaude"), "AI tool data"); Catalog("Cursor installation", At(local, "Programs/cursor"), "User data"); Catalog("GitHub Copilot", At(local, "github-copilot"), "AI tool data");
        Catalog("Ollama", Env("OLLAMA_MODELS", At(home, ".ollama/models")), "AI model store");
        Catalog("Hugging Face", Env("HF_HOME", At(home, ".cache/huggingface")), "AI model store");
        foreach (var (tool, tail) in new[] { ("LM Studio models", ".lmstudio"), ("LM Studio cache", ".cache/lm-studio"), ("PyTorch hub", ".cache/torch") }) Catalog(tool, At(home, tail), "AI model store");
        for (var i = 0; i < scan.Nodes.Count; i++)
            if (scan.Nodes[i].Name.Equals("ComfyUI", StringComparison.OrdinalIgnoreCase) || scan.Nodes[i].Name.Equals("stable-diffusion-webui", StringComparison.OrdinalIgnoreCase))
                Catalog(scan.Nodes[i].Name, At(paths[i], "models"), "AI model store");

        var duplicateTools = items.GroupBy(i => i.Tool, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
            if (duplicateTools.Contains(items[i].Tool)) items[i] = items[i] with { Tool = items[i].Tool + " — " + items[i].Path };
        var artifactRoots = new HashSet<int>(); var artifacts = new Dictionary<int, List<BloatItem>>();
        var owners = new int[scan.Nodes.Count]; Array.Fill(owners, -1);
        var projects = new List<int>();
        bool Under(int i, HashSet<int> roots)
        {
            for (; i >= 0; i = scan.Nodes[i].Parent) if (roots.Contains(i)) return true;
            return false;
        }
        for (var i = 0; i < scan.Nodes.Count; i++)
        {
            var n = scan.Nodes[i]; var parent = n.Parent;
            var owner = parent >= 0 ? owners[parent] : -1;
            owners[i] = owner;
            if (Under(i, cacheRoots) || parent >= 0 && Under(parent, artifactRoots)) continue;
            var rule = parent >= 0 ? Artifact(n, scan.Nodes[parent].Markers) : null;
            if (rule is { } r)
            {
                artifactRoots.Add(i);
                var item = new BloatItem("Project artifacts", n.Name, paths[i], n.AllocatedBytes, n.AllocatedBytes, n.LatestWrite, r.Confidence,
                    r.Reason, r.Command, "Not executed. Review local changes and reproducibility before cleanup.", "Regenerable artifact");
                items.Add(item);
                if (owner >= 0) artifacts[owner].Add(item);
                continue;
            }
            if ((n.Markers & ProjectMarkers) == 0) continue;
            var gitAncestor = owner >= 0 && (scan.Nodes[owner].Markers & StorageMarkers.Git) != 0;
            if (!gitAncestor || (n.Markers & (StorageMarkers.Git | StorageMarkers.Worktree)) != 0)
            {
                owners[i] = i; projects.Add(i); artifacts[i] = [];
            }
        }
        var projectAI = new bool[scan.Nodes.Count];
        var aiFolderRoots = items.Where(x => x.Kind == "AI tool data").Select(x => index[StoragePaths.Normalize(x.Path)]).ToHashSet();
        var inAIFolder = new bool[scan.Nodes.Count];
        for (var i = 0; i < scan.Nodes.Count; i++)
        {
            if (owners[i] >= 0 && (scan.Nodes[i].Markers & StorageMarkers.AI) != 0) projectAI[owners[i]] = true;
            inAIFolder[i] = aiFolderRoots.Contains(i) || scan.Nodes[i].Parent >= 0 && inAIFolder[scan.Nodes[i].Parent];
        }
        var projectRows = projects.Select(i =>
        {
            var n = scan.Nodes[i];
            return new DeveloperProject(paths[i], n.Markers, n.AllocatedBytes, artifacts[i].Sum(a => a.AllocatedBytes), artifacts[i].AsReadOnly(), n.LatestWrite,
                n.LatestWrite is { } last && now.UtcDateTime - last > staleAfter, projectAI[i]);
        }).ToArray();
        void Lead(int i, string tool, BloatConfidence confidence, string reason) => items.Add(new("AI coding leads", tool, paths[i], scan.Nodes[i].AllocatedBytes, 0, scan.Nodes[i].LatestWrite, confidence, reason, null, null, "Heuristic lead", true));
        for (var i = 0; i < scan.Nodes.Count; i++)
        {
            var n = scan.Nodes[i];
            if ((n.Markers & StorageMarkers.AI) != 0) Lead(i, "AI instructions", BloatConfidence.Medium, "Lead: configured for AI coding agents; does not prove AI authorship (includes Aider marker files when present).");
            if ((n.Markers & StorageMarkers.Worktree) != 0)
            {
                var known = StoragePaths.Normalize(paths[i]).Split('/').Any(s => new[] { "worktrees", ".claude", ".cursor", "conductor" }.Contains(s, StringComparer.OrdinalIgnoreCase));
                Lead(i, "Git worktree", known ? BloatConfidence.Medium : BloatConfidence.Low, "Lead: .git file indicates a worktree" + (known ? " under an agent-associated location" : "; creator unknown") + "; not proof of AI authorship.");
            }
            var aiProject = owners[i] >= 0 && projectAI[owners[i]];
            var aiFolder = inAIFolder[i];
            if (n.DirectFiles >= 200 && n.MinCreationUtc is { } min && n.MaxCreationUtc is { } max && min != default && max - min <= TimeSpan.FromMinutes(15)
                && min <= now.UtcDateTime && min >= now.UtcDateTime.AddDays(-30) && max <= now.UtcDateTime && (aiProject || aiFolder))
                Lead(i, "Bulk creation", BloatConfidence.Low, "Lead: at least 200 direct files created within 15 minutes in the last 30 days in an AI-associated folder; copying/extraction can also explain this.");
        }
        foreach (var f in scan.LargestFiles.Where(f => f.LogicalBytes >= 100L * 1024 * 1024 && new[] { ".safetensors", ".gguf", ".ckpt", ".pt", ".pth", ".onnx" }.Any(e => f.Path.EndsWith(e, StringComparison.OrdinalIgnoreCase))))
            items.Add(new("AI tools and models", "Model file candidate", f.Path, f.AllocatedBytes, f.AllocatedBytes, f.LastWriteUtc, BloatConfidence.Low, "Model-like extension; re-download required if removed; contents not inspected", null, "Not executed", "Model file"));
        var totals = Summarize(items);
        return new(Array.AsReadOnly(items.OrderByDescending(x => x.ReclaimableBytes ?? -1).ThenByDescending(x => x.AllocatedBytes).ToArray()),
            Array.AsReadOnly(projectRows), totals, outside.AsReadOnly(), absent);
    }
    public static IReadOnlyList<BloatCategoryTotal> Summarize(IEnumerable<BloatItem> source)
    {
        var items = source.Where(x => !x.IsLead).OrderByDescending(x => x.ReclaimableBytes ?? -1).ThenByDescending(x => x.AllocatedBytes)
            .GroupBy(x => StoragePaths.Normalize(x.Path), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        var paths = items.Select(x => StoragePaths.Normalize(x.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = items.Where(x =>
        {
            var path = StoragePaths.Normalize(x.Path);
            for (var slash = path.LastIndexOf('/'); slash >= 0; slash = path.LastIndexOf('/', slash - 1))
            {
                if (paths.Contains(path[..slash])) return false;
                if (slash == 0) break;
            }
            return true;
        });
        return Array.AsReadOnly(kept.GroupBy(x => x.Category).Select(g => new BloatCategoryTotal(g.Key, g.Sum(x => x.AllocatedBytes),
            g.Any(x => x.ReclaimableBytes.HasValue) ? g.Sum(x => x.ReclaimableBytes ?? 0) : null, g.Count(x => !x.ReclaimableBytes.HasValue))).ToArray());
    }
    private static bool Match(string name, string pattern)
    {
        var parts = pattern.Split('*'); var offset = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var at = name.IndexOf(parts[i], offset, StringComparison.OrdinalIgnoreCase);
            if (at < 0 || i == 0 && at != 0) return false;
            offset = at + parts[i].Length;
        }
        return pattern.EndsWith('*') || offset == name.Length;
    }
    private static (BloatConfidence Confidence, string Reason, string Command)? Artifact(StorageNode n, StorageMarkers parent)
    {
        var name = n.Name.ToLowerInvariant();
        bool Has(StorageMarkers m) => (parent & m) != 0;
        var confidence = BloatConfidence.High;
        var valid = name switch
        {
            "node_modules" => true,
            ".venv" or "venv" or "env" => (n.Markers & StorageMarkers.PythonEnvironment) != 0,
            "__pycache__" or ".pytest_cache" or ".mypy_cache" or ".ruff_cache" or ".tox" => true,
            "target" => Has(StorageMarkers.Rust | StorageMarkers.Maven), "bin" or "obj" => Has(StorageMarkers.DotNet),
            "dist" or "build" or "out" => Has(StorageMarkers.Node | StorageMarkers.Python | StorageMarkers.Gradle),
            ".next" or ".nuxt" or ".turbo" or ".parcel-cache" or ".svelte-kit" or ".angular" => Has(StorageMarkers.Node),
            "coverage" or ".dart_tool" or "deriveddata" => true, ".gradle" => Has(StorageMarkers.Gradle),
            ".terraform" => Has(StorageMarkers.Terraform), ".vs" => Has(StorageMarkers.Solution | StorageMarkers.DotNet), _ => false
        };
        if (!valid) return null;
        if (name is "dist" or "build" or "out" or "coverage" or ".dart_tool" or "deriveddata" || name == "node_modules" && !Has(StorageMarkers.Node)) confidence = BloatConfidence.Medium;
        return (confidence, $"Conventional artifact directory {name}; validated using directory/parent name markers (not file contents)",
            name == "node_modules" ? "Delete the folder; restore with npm install" : $"Remove {n.Name} only after review; restore using the project's build/package tools");
    }
}
