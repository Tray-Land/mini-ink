using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MiniInk;

public sealed partial class ShellBackdrop : SystemBackdrop
{
    private readonly SystemBackdropConfiguration _configuration = new() { IsInputActive = true };
    private DesktopAcrylicController? _controller;
    private Color? _accent;
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        if (!DesktopAcrylicController.IsSupported())
            return;
        _controller = new DesktopAcrylicController();
        ApplyTint();
        _controller.SetSystemBackdropConfiguration(_configuration);
        _controller.AddSystemBackdropTarget(target);
    }
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
        base.OnTargetDisconnected(target);
    }
    public void Update(bool light, Color? accent)
    {
        _configuration.Theme = light ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;
        _accent = accent;
        ApplyTint();
    }
    private void ApplyTint()
    {
        if (_controller is null)
            return;
        if (_accent is Color tint)
        {
            _controller.TintColor = _controller.FallbackColor = tint;
            _controller.TintOpacity = 0.7f;
            _controller.LuminosityOpacity = 0.9f;
        }
        else
            _controller.ResetProperties();
    }
}
