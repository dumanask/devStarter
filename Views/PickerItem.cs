using System.ComponentModel;
using System.Windows.Media;
using DevStarter.Models;
using DevStarter.Services;

namespace DevStarter.Views;

// Seçici penceredeki tek satır. Arka planda oluşturulabilir.
// Dosya/uygulama simgeleri arka planda yüklenir; gelene kadar tür simgesi ya da yedek karakter görünür.
public class PickerItem : INotifyPropertyChanged
{
    public string Group { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string? IconPath { get; init; }
    public TargetKind? Kind { get; init; }
    public string Glyph { get; init; } = "";
    public Action? Run { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    ImageSource? icon;
    bool iconRequested;

    // UI iş parçacığından (bağlama) çağrılır
    public ImageSource? Icon
    {
        get
        {
            if (iconRequested) return icon;
            iconRequested = true;
            try
            {
                if (IconPath != null)
                {
                    if (IconService.TryGetCached(IconPath, out var cached) && cached != null)
                        return icon = cached;
                    icon = Kind is TargetKind k1 && k1 != TargetKind.Custom ? KindInfo.KindIcon(k1) : null;
                    IconLoader.Request(IconPath, loaded =>
                    {
                        if (loaded == null) return;
                        icon = loaded;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
                    });
                }
                else if (Kind is TargetKind k)
                {
                    icon = KindInfo.KindIcon(k);
                }
            }
            catch { }
            return icon;
        }
    }
}
