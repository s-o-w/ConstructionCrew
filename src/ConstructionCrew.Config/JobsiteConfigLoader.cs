using ConstructionCrew.Core.Models;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ConstructionCrew.Config;

public sealed class JobsiteConfigLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>Missing file is not an error: jobsites.yaml starts empty until the first /hire flow creates one.</summary>
    public IReadOnlyList<JobsiteConfig> LoadFromFile(string path, string repoRoot)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var yaml = File.ReadAllText(path);

        JobsiteFileDto? document;
        try
        {
            document = _deserializer.Deserialize<JobsiteFileDto>(yaml);
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException(
                $"Could not load Jobsite config at '{path}': {ex.Message}. Check it still opens with a top-level " +
                "'jobsites:' key -- a hand edit that removed it, or an entry with a field YamlDotNet can't place, " +
                "will fail exactly like this.", ex);
        }

        document ??= new JobsiteFileDto();

        var configs = new List<JobsiteConfig>();
        // An empty "jobsites:" key deserializes to null, not an empty list.
        foreach (var dto in document.Jobsites ?? [])
        {
            var repoPath = dto.RepoPath?.Replace("${repoRoot}", repoRoot);

            if (string.IsNullOrWhiteSpace(dto.Name))
            {
                throw new InvalidOperationException($"A jobsite entry in '{path}' is missing 'name'.");
            }

            if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            {
                throw new InvalidOperationException($"Jobsite '{dto.Name}' repoPath does not exist: '{repoPath}'.");
            }

            // vaultFolders needs its own "?? []": the outer-list guard above
            // doesn't cover per-entry fields.
            configs.Add(new JobsiteConfig(
                dto.Name,
                repoPath,
                dto.Description ?? string.Empty,
                dto.RepoUrl,
                dto.Color,
                dto.DefaultBranch,
                dto.BuildCommand,
                dto.TestCommand,
                dto.Backlog,
                dto.VaultFolders ?? []));
        }

        return configs;
    }

    private sealed class JobsiteFileDto
    {
        public List<JobsiteDto> Jobsites { get; set; } = new();
    }

    private sealed class JobsiteDto
    {
        public string? Name { get; set; }
        public string? RepoPath { get; set; }
        public string? Description { get; set; }
        public string? RepoUrl { get; set; }
        public string? Color { get; set; }
        public string? DefaultBranch { get; set; }
        public string? BuildCommand { get; set; }
        public string? TestCommand { get; set; }
        public string? Backlog { get; set; }
        public List<string>? VaultFolders { get; set; }
    }
}
