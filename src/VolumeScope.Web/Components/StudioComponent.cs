using Microsoft.AspNetCore.Components;
using VolumeScope.Web.State;

namespace VolumeScope.Web.Components;

/// <summary>状態が変わったら描き直す部品の元</summary>
public abstract class StudioComponent : ComponentBase, IDisposable
{
    [Inject] protected Studio S { get; set; } = default!;

    protected override void OnInitialized() => S.Changed += OnStudioChanged;

    protected virtual void OnStudioChanged() => InvokeAsync(StateHasChanged);

    protected static string Fmt(double v, string format = "0.#") => Studio.Fmt(v, format);

    public virtual void Dispose()
    {
        S.Changed -= OnStudioChanged;
        GC.SuppressFinalize(this);
    }
}
