using System.IO;

namespace tsky;

public sealed class ImportExportService
{
    public sealed class ImportMergeResult
    {
        public required TaskConfiguration ValidatedMergedConfiguration { get; init; }

        public required TaskConfiguration ValidatedWrittenUserProfileConfiguration { get; init; }
    }

    public string SerializeConfiguration(TaskConfiguration payload)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"tsky-share-{Guid.NewGuid():N}.json");
        try
        {
            TaskConfiguration.Save(tempPath, payload);
            return File.ReadAllText(tempPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }


    public ImportMergeResult MergeAndPersistImportedConfiguration(
        string userProfileConfigPath,
        TaskConfiguration importedConfig,
        string importSourceLabel)
    {
        var importedAtUtc = DateTime.UtcNow.ToString("O");
        foreach (var importedTask in importedConfig.Tasks)
        {
            importedTask.ImportedFromFile = importSourceLabel;
            importedTask.ImportedAtUtc = importedAtUtc;
        }

        var userProfileConfig = TaskConfiguration.Load(userProfileConfigPath);

        // User profile tasks take precedence on identity collisions.
        var mergedConfig = TaskConfiguration.MergeWithOverlay(importedConfig, userProfileConfig);

        var tempPath = Path.Combine(Path.GetTempPath(), $"tsky-import-{Guid.NewGuid():N}.json");
        try
        {
            TaskConfiguration.Save(tempPath, mergedConfig);
            var validatedMergedConfig = TaskConfiguration.Load(tempPath);

            TaskConfiguration.Save(userProfileConfigPath, validatedMergedConfig);
            var validatedWrittenProfile = TaskConfiguration.Load(userProfileConfigPath);

            return new ImportMergeResult
            {
                ValidatedMergedConfiguration = validatedMergedConfig,
                ValidatedWrittenUserProfileConfiguration = validatedWrittenProfile
            };
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        }
    }
}
