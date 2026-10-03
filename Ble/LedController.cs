namespace LEDStripController.Ble;

/// <summary>High-level LED commands on top of <see cref="BleManager"/>.</summary>
public sealed class LedController
{
    Rgb _last = Rgb.White;

    public BleManager Ble { get; } = new();
    public int Brightness { get; private set; } = 100;

    public Task<bool> SetPowerAsync(bool on) => Ble.WriteAsync(Protocol.Power(Ble.Type, on), false);

    public Task<bool> SetColorAsync(Rgb c, bool dropIfBusy)
    {
        _last = c;
        // BJ_LED has no brightness command -> scale the RGB values instead.
        var send = Ble.Type == StripType.BjLed ? c.Scale(Brightness / 100.0) : c;
        return Ble.WriteAsync(Protocol.Color(Ble.Type, send), dropIfBusy);
    }

    public async Task SetBrightnessAsync(int percent)
    {
        Brightness = Math.Clamp(percent, 1, 100);
        if (Ble.Type == StripType.ElkBledom)
            await Ble.WriteAsync(Protocol.Brightness(Brightness), false);
        else
            await SetColorAsync(_last, false);
    }
}
