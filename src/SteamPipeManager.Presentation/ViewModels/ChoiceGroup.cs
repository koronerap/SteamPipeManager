using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamPipeManager.Presentation.ViewModels;

/// <summary>
/// Bir seçenek grubunun tek düğmesi (ör. "30 gün"). Arayüz grubu yan yana, birbirini
/// dışlayan düğmeler olarak çiziyor ve <see cref="IsSelected"/>'ı iki yönlü bağlıyor.
/// </summary>
public sealed partial class ChoiceOption : ObservableObject
{
    private readonly Action<ChoiceOption> _chosen;

    internal ChoiceOption(object value, string label, Action<ChoiceOption> chosen)
    {
        Value = value;
        Label = label;
        _chosen = chosen;
    }

    public object Value { get; }

    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            _chosen(this);
        }
    }

    public override string ToString() => Label;
}

/// <summary>Birbirini dışlayan seçenekler; seçim değişince <see cref="Changed"/>.</summary>
public sealed class ChoiceGroup<T> where T : struct, Enum
{
    public ChoiceGroup(T initial, Func<T, string> label)
    {
        Options = [.. Enum.GetValues<T>().Select(value => new ChoiceOption(value, label(value), OnChosen))];
        Selected = initial;
        Mark();
    }

    public IReadOnlyList<ChoiceOption> Options { get; }

    public T Selected { get; private set; }

    public event EventHandler? Changed;

    public void Select(T value)
    {
        if (EqualityComparer<T>.Default.Equals(value, Selected))
        {
            return;
        }

        Selected = value;
        Mark();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnChosen(ChoiceOption option) => Select((T)option.Value);

    private void Mark()
    {
        foreach (var option in Options)
        {
            option.IsSelected = EqualityComparer<T>.Default.Equals((T)option.Value, Selected);
        }
    }
}
