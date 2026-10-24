using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Tarjeta "el asistente quiere hacer esto, ¿lo permites?" dentro de la respuesta en curso. El
/// servidor del vault espera a <see cref="Decision"/>; cerrar el turno, detenerlo o dejar pasar
/// <see cref="AiVaultToolbox.ApprovalTimeout"/> cuenta como denegar.</summary>
public sealed partial class AiApprovalViewModel(AiApprovalRequest request) : ObservableObject
{
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<bool> Decision => _decision.Task;

    public string Title => LocalizedTextFormatter.Resolve(request.Title);

    public IReadOnlyList<string> Items => request.Items;

    [RelayCommand]
    private void Allow() => _decision.TrySetResult(true);

    [RelayCommand]
    private void Deny() => _decision.TrySetResult(false);

    /// <summary>El turno terminó sin que el usuario decidiera.</summary>
    public void Cancel() => _decision.TrySetResult(false);

    public void RefreshLocalizedLabels() => OnPropertyChanged(nameof(Title));
}
