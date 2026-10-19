namespace MiniInk;

// What a mouse or a finger does on the canvas. The pen always draws.
// The order matches the choices in the Mouse and touch flyout.
public enum InputTool
{
    Select,
    Draw,
    Text
}

public static class InputTools
{
    // Clicking types and touch moves things, as before the tools could be chosen.
    public const InputTool DefaultMouse = InputTool.Text;
    public const InputTool DefaultTouch = InputTool.Select;

    // Settings hold the tool as a number; anything unknown falls back to the default.
    public static InputTool Parse(object? stored, InputTool fallback) =>
        stored is int value && Enum.IsDefined((InputTool)value) ? (InputTool)value : fallback;
}
