using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Input;

namespace Content.Client.Photography.UI;

/// <summary>
/// A mouse-driven rotary control for the camera viewfinder. It intentionally has no keyboard bindings.
/// </summary>
public sealed class PhotoCameraDial : Control
{
    private const float StartAngle = MathF.PI * 0.75f;
    private const float SweepAngle = MathF.PI * 1.5f;

    private float _value;
    private bool _grabbed;

    public event Action<PhotoCameraDial>? OnValueChanged;

    public float MinValue { get; set; }
    public float MaxValue { get; set; } = 1f;
    public float Step { get; set; } = 0.1f;
    public bool Disabled { get; set; }

    public float Value
    {
        get => _value;
        set
        {
            var next = Math.Clamp(value, MinValue, MaxValue);
            if (Step > 0f)
                next = MathF.Round(next / Step) * Step;

            if (MathHelper.CloseTo(_value, next))
                return;

            _value = next;
            OnValueChanged?.Invoke(this);
        }
    }

    public PhotoCameraDial()
    {
        MinSize = new Vector2(72f, 72f);
        MouseFilter = MouseFilterMode.Stop;
        DefaultCursorShape = CursorShape.Pointer;
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);

        if (Disabled || args.Function != EngineKeyFunctions.UIClick)
            return;

        _grabbed = true;
        SetFromPosition(args.RelativePosition);
        args.Handle();
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        base.KeyBindUp(args);

        if (args.Function != EngineKeyFunctions.UIClick || !_grabbed)
            return;

        _grabbed = false;
        args.Handle();
    }

    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);

        if (_grabbed && !Disabled)
            SetFromPosition(args.RelativePosition);
    }

    protected override void MouseWheel(GUIMouseWheelEventArgs args)
    {
        base.MouseWheel(args);

        if (Disabled)
            return;

        Value += MathF.Sign(args.Delta.Y) * Step;
        args.Handle();
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        var center = (Vector2) PixelSize / 2f;
        var radius = MathF.Max(12f, MathF.Min(PixelWidth, PixelHeight) / 2f - 5f);
        var dim = Disabled ? 0.45f : 1f;

        for (var i = 0; i <= 10; i++)
        {
            var angle = StartAngle + SweepAngle * i / 10f;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            handle.DrawLine(
                center + direction * (radius + 1f),
                center + direction * (radius + (i % 5 == 0 ? 7f : 4f)),
                Color.FromHex("#aeb5bd").WithAlpha(dim));
        }

        handle.DrawCircle(center + new Vector2(1f, 2f), radius, Color.FromHex("#14171b").WithAlpha(dim));
        handle.DrawCircle(center, radius, Color.FromHex("#6b737d").WithAlpha(dim));
        handle.DrawCircle(center, radius - 5f, Color.FromHex("#343a42").WithAlpha(dim));
        handle.DrawCircle(center, radius - 5f, Color.FromHex("#15191e").WithAlpha(dim), false);

        var ratio = MaxValue > MinValue ? (Value - MinValue) / (MaxValue - MinValue) : 0f;
        var indicatorAngle = StartAngle + SweepAngle * ratio;
        var indicator = new Vector2(MathF.Cos(indicatorAngle), MathF.Sin(indicatorAngle));
        handle.DrawLine(center, center + indicator * (radius - 10f), Color.FromHex("#f0c568").WithAlpha(dim));
        handle.DrawCircle(center, 3f, Color.FromHex("#aeb5bd").WithAlpha(dim));
    }

    public void SetValueWithoutEvent(float value)
    {
        _value = Math.Clamp(value, MinValue, MaxValue);
    }

    private void SetFromPosition(Vector2 position)
    {
        var center = (Vector2) PixelSize / 2f;
        var delta = position - center;
        if (delta.LengthSquared() < 1f)
            return;

        var angle = MathF.Atan2(delta.Y, delta.X);
        if (angle < StartAngle)
            angle += MathF.Tau;

        Value = MinValue + Math.Clamp((angle - StartAngle) / SweepAngle, 0f, 1f) * (MaxValue - MinValue);
    }
}
