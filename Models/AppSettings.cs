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

    public AgentSettings Agents { get; set; } = new();

    // Ajanlar panelinin son konumu/boyutu
    public WindowBounds? AgentsWindow { get; set; }
}

public class AgentSettings
{
    // Codex / Gemini / OpenCode hook'larının durum bildirdiği yerel port (yalnızca 127.0.0.1)
    public int HookPort { get; set; } = 47820;
    public bool NotifyOnWaiting { get; set; } = true;
    public bool NotifyOnFinished { get; set; } = true;
}

public class WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
