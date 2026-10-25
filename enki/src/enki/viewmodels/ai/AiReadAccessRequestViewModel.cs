using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;

namespace enki.ViewModels;

/// <summary>Tarjeta "¿Permitir que este chat busque y consulte documentos de …?" dentro de la respuesta en curso.
/// Aparece la primera vez que el asistente quiere leer por su cuenta en un chat sin ese permiso. Dice qué
/// ámbito se autoriza y qué proveedor recibirá el contenido, y deja claro lo que no autoriza: otras sesiones ni
/// más permisos de edición. Detener la respuesta, desactivar la IA, cambiar de vault o dejar pasar el tiempo de
/// espera cuentan como "Ahora no".</summary>
public sealed partial class AiReadAccessRequestViewModel(string scopeLabel, string providerLabel) : ObservableObject
{
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<bool> Decision => _decision.Task;

    public string ScopeLabel { get; } = scopeLabel;

    public string Question => string.Format(LocalizationService.Instance.CurrentCulture,
        LocalizationService.Instance["Assistant_ReadAccess_Question"], ScopeLabel);

    public string Detail => string.Format(LocalizationService.Instance.CurrentCulture,
        LocalizationService.Instance["Assistant_ReadAccess_Detail"], providerLabel);

    [RelayCommand]
    private void Allow() => _decision.TrySetResult(true);

    [RelayCommand]
    private void Deny() => _decision.TrySetResult(false);

    public void Cancel() => _decision.TrySetResult(false);

    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(Question));
        OnPropertyChanged(nameof(Detail));
    }
}
