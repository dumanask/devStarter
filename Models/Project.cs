using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace DevStarter.Models;

// Tek tıkla birlikte açılan hedefler grubu (ör. OyunProjem = Unity + VS Code)
public class Project : ObservableObject
{
    string name = "";
    string color = "";
    string iconPath = "";
    bool favorite;
    bool launching;
    ObservableCollection<LaunchTarget> targets = new();

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => name;
        set
        {
            if (Set(ref name, value ?? ""))
            {
                Raise(nameof(Initials));
                if (color.Length == 0) Raise(nameof(Brush));
            }
        }
    }

    // "#RRGGBB" — boşsa addan türetilir
    public string Color { get => color; set { if (Set(ref color, value ?? "")) Raise(nameof(Brush)); } }

    // Özel simge (PNG, %APPDATA%\devStarter\icons altında). Boşsa baş harfler gösterilir.
    public string IconPath
    {
        get => iconPath;
        set
        {
            if (Set(ref iconPath, value ?? ""))
            {
                Raise(nameof(IconImage));
                Raise(nameof(HasIcon));
            }
        }
    }

    public bool IsFavorite { get => favorite; set => Set(ref favorite, value); }

    public ObservableCollection<LaunchTarget> Targets
    {
        get => targets;
        set { targets = value ?? new(); Raise(); }
    }

    [JsonIgnore] public bool IsLaunching { get => launching; set => Set(ref launching, value); }
    [JsonIgnore] public bool HasError => targets.Any(t => t.HasError);
    [JsonIgnore] public string Initials => Palette.Initials(name);
    [JsonIgnore] public Brush Brush => Palette.BrushFor(color.Length > 0 ? color : Palette.ForName(name));
    [JsonIgnore] public ImageSource? IconImage => DevStarter.Services.ProjectIcons.Load(iconPath);
    [JsonIgnore] public bool HasIcon => IconImage != null;

    public void RaiseStatus() => Raise(nameof(HasError));

    // ---- Bu projenin klasöründe açık ajan oturumları (AgentMonitor günceller, kaydedilmez) ----
    int agentWorking, agentWaiting, agentIdle;
    string agentKinds = "";

    [JsonIgnore] public int AgentWorking => agentWorking;
    [JsonIgnore] public int AgentWaiting => agentWaiting;
    [JsonIgnore] public int AgentIdle => agentIdle;
    [JsonIgnore] public bool HasAgents => agentWorking + agentWaiting + agentIdle > 0;
    [JsonIgnore] public bool HasWaitingAgent => agentWaiting > 0;

    // İkon modundaki nokta: sarı (bekliyor) > yeşil (çalışıyor) > gri (boşta)
    [JsonIgnore]
    public Brush? AgentDot => agentWaiting > 0 ? DevStarter.Agents.AgentColors.Waiting
        : agentWorking > 0 ? DevStarter.Agents.AgentColors.Working
        : agentIdle > 0 ? DevStarter.Agents.AgentColors.Idle
        : null;

    [JsonIgnore]
    public string AgentTip
    {
        get
        {
            var parts = new List<string>();
            if (agentWaiting > 0) parts.Add($"{agentWaiting} seni bekliyor");
            if (agentWorking > 0) parts.Add($"{agentWorking} çalışıyor");
            if (agentIdle > 0) parts.Add($"{agentIdle} boşta");
            if (parts.Count == 0) return "";
            return $"Ajanlar ({agentKinds}): {string.Join(" · ", parts)}\nTıkla: ajan panelini aç";
        }
    }

    public void SetAgentCounts(int working, int waiting, int idle, string kinds)
    {
        if (working == agentWorking && waiting == agentWaiting && idle == agentIdle && kinds == agentKinds) return;
        agentWorking = working;
        agentWaiting = waiting;
        agentIdle = idle;
        agentKinds = kinds;
        Raise(nameof(AgentWorking));
        Raise(nameof(AgentWaiting));
        Raise(nameof(AgentIdle));
        Raise(nameof(HasAgents));
        Raise(nameof(HasWaitingAgent));
        Raise(nameof(AgentDot));
        Raise(nameof(AgentTip));
    }

    public Project Clone() => new()
    {
        Id = Id,
        Name = name,
        Color = color,
        IconPath = iconPath,
        IsFavorite = favorite,
        Targets = new ObservableCollection<LaunchTarget>(targets.Select(t => t.Clone()))
    };

    public void CopyFrom(Project other)
    {
        Name = other.Name;
        Color = other.Color;
        IconPath = other.IconPath;
        IsFavorite = other.IsFavorite;
        targets.Clear();
        foreach (var t in other.Targets) targets.Add(t.Clone());
        RaiseStatus();
    }
}
