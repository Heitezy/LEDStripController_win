namespace LEDStripController.Ble;

/// <summary>Byte-frame builders for ELK-BLEDOM (9-byte frames) and BJ_LED.</summary>
public static class Protocol
{
    public static byte[] Power(StripType t, bool on) => t == StripType.BjLed
        ? new byte[] { 0x69, 0x96, 0x02, 0x01, (byte)(on ? 1 : 0) }
        : on
            ? new byte[] { 0x7E, 0x04, 0x04, 0xF0, 0x00, 0x01, 0xFF, 0x00, 0xEF }
            : new byte[] { 0x7E, 0x04, 0x04, 0x00, 0x00, 0x00, 0xFF, 0x00, 0xEF };

    public static byte[] Color(StripType t, Rgb c) => t == StripType.BjLed
        ? new byte[] { 0x69, 0x96, 0x05, 0x02, c.R, c.G, c.B }
        : new byte[] { 0x7E, 0x07, 0x05, 0x03, c.R, c.G, c.B, 0x10, 0xEF };

    /// <summary>ELK-BLEDOM only. BJ_LED has no brightness command (RGB is scaled instead).</summary>
    public static byte[] Brightness(int percent) =>
        new byte[] { 0x7E, 0x04, 0x01, (byte)Math.Clamp(percent, 0, 100), 0x00, 0x00, 0x00, 0x00, 0xEF };
}
