using System.IO;
using System.Text.Json;

namespace tsky;

public sealed class UiState
{
    public double WindowWidth { get; set; } = 700;

    public double WindowHeight { get; set; } = 420;

    public double WindowLeft { get; set; } = double.NaN;

    public double WindowTop { get; set; } = double.NaN;

    public bool IsMaximized { get; set; }

    public double OutputHeight { get; set; } = 220;

    public double SidebarWidth { get; set; } = 220;

    public double HistoryListPaneRatio { get; set; } = 0.4;

    public List<string> FavoriteGroups { get; set; } = [];

    public List<string> NonFavoriteGroupsOrder { get; set; } = [];

    public double AddTaskDialogWidth { get; set; } = 760;

    public double AddTaskDialogHeight { get; set; } = 860;
}

public static class UiStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static string StateFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "tsky",
        "ui-state.json");

    public static UiState Load()
    {
        return JsonFileStorage.LoadOrDefault<UiState>(StateFilePath, JsonOptions);
    }

    public static void Save(UiState state)
    {
        JsonFileStorage.Save(StateFilePath, state, JsonOptions);
    }
}
