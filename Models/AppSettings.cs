using System.Collections.ObjectModel;

namespace DevStarter.Models;

public enum DockSide { Left, Right, Top }

public enum ViewMode { Icons, Cards }

public class AppSettings
{
    public DockSide Dock { get; set; } = DockSide.Right;
    public ViewMode View { get; set; } = ViewMode.Cards;
    public bool AutoHide { get; set; } = true;

    // Otomatik bulunamayan programlar için kullanıcının seçtiği yollar (anahtar: ToolLocator sabitleri)
    public Dictionary<string, string> ToolPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<Project> Projects { get; set; } = new();
}
