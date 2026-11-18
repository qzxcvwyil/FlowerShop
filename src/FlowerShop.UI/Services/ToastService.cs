namespace FlowerShop.Client.Services;

public enum ToastKind { Info, Success, Error }
public sealed record Toast(Guid Id, string Text, ToastKind Kind);

public sealed class ToastService
{
    public event Action<Toast>? Shown;
    public void Show(string text, ToastKind kind = ToastKind.Info) => Shown?.Invoke(new Toast(Guid.NewGuid(), text, kind));
    public void Error(string text) => Show(text, ToastKind.Error);
    public void Success(string text) => Show(text, ToastKind.Success);
}
