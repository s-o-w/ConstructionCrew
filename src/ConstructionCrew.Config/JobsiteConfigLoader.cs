using ConstructionCrew.Core.Models;
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
        var document = _deserializer.Deserialize<JobsiteFileDto>(yaml) ?? new JobsiteFileDto();

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

            // vaultFolders and upstream need their own "?? []": the outer-list
            // guard above doesn't cover per-entry fields.
            configs.Add(new JobsiteConfig(
                dto.Name,
                repoPath,
                dto.Description ?? string.Empty,
                dto.RepoUrl,
                dto.Color,
                dto.DefaultBranch,
                dto.BuildCommand,
                dto.TestCommand,
                dto.Upstream ?? new Dictionary<string, string>(),
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
        public Dictionary<string, string>? Upstream { get; set; }
        public List<string>? VaultFolders { get; set; }
    }
}
