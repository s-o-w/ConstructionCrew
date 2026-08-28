using ConstructionCrew.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ConstructionCrew.Config;

/// <summary>Loads Foreman (and GC, which is just a Foreman with a reserved name) definitions from YAML.</summary>
public sealed class ForemanConfigLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>
    /// Loads Foreman entries. Any "${repoRoot}" token in workingDirectory or
    /// instructionsFilePath is expanded to <paramref name="repoRoot"/> first,
    /// so the sample config stays portable across machines/clones.
    /// </summary>
    public IReadOnlyList<ForemanConfig> LoadFromFile(string path, string repoRoot)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Foreman config file not found: {path}", path);
        }

        var yaml = File.ReadAllText(path);
        var document = _deserializer.Deserialize<ForemanFileDto>(yaml) ?? new ForemanFileDto();

        var configs = new List<ForemanConfig>();
        // Same null-vs-empty-list deserialization gotcha as JobsiteConfigLoader --
        // an empty "foremen:" key would otherwise NRE here.
        foreach (var dto in document.Foremen ?? [])
        {
            dto.WorkingDirectory = ExpandTokens(dto.WorkingDirectory, repoRoot);
            dto.InstructionsFilePath = ExpandTokens(dto.InstructionsFilePath, repoRoot);

            Validate(dto, path);
            configs.Add(new ForemanConfig(
                dto.Name!,
                dto.Provider!,
                dto.WorkingDirectory!,
                dto.InstructionsFilePath!,
                dto.ProviderOptions ?? new Dictionary<string, string>(),
                dto.JobsiteName));
        }

        return configs;
    }

    private static string? ExpandTokens(string? value, string repoRoot) =>
        value?.Replace("${repoRoot}", repoRoot);

    private static void Validate(ForemanDto dto, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new InvalidOperationException($"A Foreman entry in '{sourcePath}' is missing 'name'.");
        }

        if (string.IsNullOrWhiteSpace(dto.Provider))
        {
            throw new InvalidOperationException($"Foreman '{dto.Name}' is missing 'provider'.");
        }

        if (string.IsNullOrWhiteSpace(dto.WorkingDirectory) || !Directory.Exists(dto.WorkingDirectory))
        {
            throw new InvalidOperationException($"Foreman '{dto.Name}' workingDirectory does not exist: '{dto.WorkingDirectory}'.");
        }

        if (string.IsNullOrWhiteSpace(dto.InstructionsFilePath) || !File.Exists(dto.InstructionsFilePath))
        {
            throw new InvalidOperationException($"Foreman '{dto.Name}' instructionsFilePath does not exist: '{dto.InstructionsFilePath}'.");
        }
    }

    private sealed class ForemanFileDto
    {
        public List<ForemanDto> Foremen { get; set; } = new();
    }

    private sealed class ForemanDto
    {
        public string? Name { get; set; }
        public string? Provider { get; set; }
        public string? WorkingDirectory { get; set; }
        public string? InstructionsFilePath { get; set; }
        public Dictionary<string, string>? ProviderOptions { get; set; }
        public string? JobsiteName { get; set; }
    }
}
