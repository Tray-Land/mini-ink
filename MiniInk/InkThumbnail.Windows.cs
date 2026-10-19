using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Streams;
using Windows.UI.Input.Inking;

namespace MiniInk;

public static partial class InkThumbnail
{
    // Drawn from saved ink on a worker thread, so a large note never stalls the window.
    // Ink that can't be read gets no thumbnail; opening the note reports why.
    public static Task<string> FromInkAsync(string inkBase64) => Task.Run(async () =>
    {
        try
        {
            using InMemoryRandomAccessStream stream = new();
            await stream.WriteAsync(Convert.FromBase64String(inkBase64).AsBuffer());
            stream.Seek(0);
            InkStrokeContainer ink = new();
            await ink.LoadAsync(stream);
            // Points are kept as drawn; moving a stroke only changes its transform.
            return Create(ink.GetStrokes().Select(stroke => (IReadOnlyList<Vector2>)stroke.GetInkPoints()
                .Select(p => Vector2.Transform(new Vector2((float)p.Position.X, (float)p.Position.Y), stroke.PointTransform))
                .ToArray()));
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or FormatException or InvalidCastException)
        {
            Debug.WriteLine($"MiniInk: drawing a thumbnail failed: {ex}");
            return "";
        }
    });

    public static Geometry? ToGeometry(string thumbnail)
    {
        if (thumbnail.Length == 0)
            return null;
        try
        {
            return (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), thumbnail);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            Debug.WriteLine($"MiniInk: unreadable thumbnail: {ex}");
            return null;
        }
    }
}
