using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SteamPipeManager.Core.Models;

/// <summary>
/// Arayüzde doğrudan düzenlenen model nesnelerinin ortak tabanı.
///
/// Bunlar düz POCO iken iki sorun vardı: kod tarafından yapılan değişiklikler ekrana
/// yansımıyordu (ör. klasör seçtirince metin kutusu boş kalıyordu) ve arayüzün modele
/// yazdığını duyabilecek bir yer olmadığı için otomatik kaydetme yazılamıyordu.
///
/// Serileştirmeyi etkilemez: <c>System.Text.Json</c> yine yalnızca genel özellikleri yazar.
/// </summary>
public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
