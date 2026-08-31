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
    /// Loads Foreman entries, expanding "${repoRoot}"/"${vaultRoot}" tokens in
    /// path values first. <paramref name="vaultRoot"/> is nullable (first run has
    /// no Vault yet), but a value that uses the token with no Vault configured is
    /// a load error, not a silent expansion to empty string.
    /// </summary>
    public IReadOnlyList<ForemanConfig> LoadFromFile(string path, string repoRoot, string? vaultRoot, string expectedGcName)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Foreman config file not found: {path}", path);
        }

        var yaml = File.ReadAllText(path);
        var document = _deserializer.Deserialize<ForemanFileDto>(yaml) ?? new ForemanFileDto();

        var configs = new List<ForemanConfig>();
        // An empty "foremen:" key deserializes to null, not []; guards the outer
        // list only, each per-entry list field needs its own "?? []" below.
        foreach (var dto in document.Foremen ?? [])
        {
            dto.WorkingDirectory = ExpandTokens(dto.WorkingDirectory, repoRoot, vaultRoot, dto.Name, path);
            dto.InstructionsFilePath = ExpandTokens(dto.InstructionsFilePath, repoRoot, vaultRoot, dto.Name, path);

            if (dto.AddDirs is not null)
            {
                for (var i = 0; i < dto.AddDirs.Count; i++)
                {
                    dto.AddDirs[i] = ExpandTokens(dto.AddDirs[i], repoRoot, vaultRoot, dto.Name, path)!;
                }
            }

            Validate(dto, path);
            configs.Add(new ForemanConfig(
                dto.Name!,
                ParseRole(dto.Role, dto.Name!, path),
                dto.Provider!,
                dto.WorkingDirectory!,
                dto.InstructionsFilePath!,
                dto.ProviderOptions ?? new Dictionary<string, string>(),
                dto.JobsiteName,
                dto.DisplayName,
                dto.AddDirs ?? [],
                dto.VaultFolders ?? []));
        }

        ValidateCollection(configs, expectedGcName, path);

        return configs;
    }

    private static CrewRole ParseRole(string? raw, string foremanName, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return CrewRole.Foreman;
        }

        return Enum.TryParse<CrewRole>(raw.Trim(), ignoreCase: true, out var role)
            ? role
            : throw new InvalidOperationException(
                $"Foreman '{foremanName}' in '{sourcePath}' has an unrecognized role '{raw}'. " +
                $"Valid roles: {string.Join(", ", Enum.GetNames<CrewRole>())}.");
    }

    /// <summary>
    /// Collection-level invariants that need a view of the whole file, not just
    /// one entry. Zero GC entries is not an error here: Program.cs's own
    /// "no Foreman named GC" check is the hard fail for that case.
    /// </summary>
    private static void ValidateCollection(IReadOnlyList<ForemanConfig> configs, string expectedGcName, string sourcePath)
    {
        var gcs = configs.Where(c => c.Role == CrewRole.GC).ToList();

        if (gcs.Count > 1)
        {
            throw new InvalidOperationException(
                $"'{sourcePath}' declares {gcs.Count} Foremen with role GC ({string.Join(", ", gcs.Select(g => $"'{g.Name}'"))}). " +
                $"Exactly one entry may hold the GC role, and it must be named '{expectedGcName}'.");
        }

        var gc = gcs.FirstOrDefault();
        if (gc is not null && !gc.Name.Equals(expectedGcName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Foreman '{gc.Name}' in '{sourcePath}' holds role GC but the GC's reserved name is '{expectedGcName}'. " +
                $"Rename the entry to '{expectedGcName}' or drop its GC role.");
        }

        var named = configs.FirstOrDefault(c => c.Name.Equals(expectedGcName, StringComparison.OrdinalIgnoreCase));
        if (named is not null && named.Role != CrewRole.GC)
        {
            throw new InvalidOperationException(
                $"Foreman '{named.Name}' in '{sourcePath}' uses the GC's reserved name '{expectedGcName}' but has role {named.Role}. " +
                $"Set 'role: GC' on it.");
        }
    }

    private static string? ExpandTokens(string? value, string repoRoot, string? vaultRoot, string? foremanName, string sourcePath)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Contains("${vaultRoot}", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(vaultRoot))
            {
                throw new InvalidOperationException(
                    $"Foreman '{foremanName}' in '{sourcePath}' uses '${{vaultRoot}}' but no Vault is configured. " +
                    "Set VaultRoot in appsettings.json or pass --vault-root.");
            }

            value = value.Replace("${vaultRoot}", vaultRoot);
        }

        return value.Replace("${repoRoot}", repoRoot);
    }

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
        public string? Role { get; set; }
        public string? Provider { get; set; }
        public string? WorkingDirectory { get; set; }
        public string? InstructionsFilePath { get; set; }
        public Dictionary<string, string>? ProviderOptions { get; set; }
        public string? JobsiteName { get; set; }
        public string? DisplayName { get; set; }
        public List<string>? AddDirs { get; set; }
        public List<string>? VaultFolders { get; set; }
    }
}
