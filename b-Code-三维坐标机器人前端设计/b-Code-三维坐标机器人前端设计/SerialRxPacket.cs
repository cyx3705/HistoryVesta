namespace b_Code_三维坐标机器人前端设计;

public sealed class SerialRxPacket
{
    public required byte[] Bytes { get; init; }
    public required string Hex { get; init; }
    public required string AsciiPreview { get; init; }
    public string? CompletedLine { get; init; }

    public static SerialRxPacket FromBytes(byte[] bytes, string? completedLine = null)
    {
        return new SerialRxPacket
        {
            Bytes = bytes,
            Hex = FormatHex(bytes),
            AsciiPreview = FormatAscii(bytes),
            CompletedLine = completedLine
        };
    }

    private static string FormatHex(byte[] bytes)
    {
        if (bytes.Length == 0)
            return "(空)";

        return string.Join(" ", bytes.Select(b => b.ToString("X2")));
    }

    private static string FormatAscii(byte[] bytes)
    {
        if (bytes.Length == 0)
            return "(空)";

        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            chars[i] = b switch
            {
                0x0D => '⏎',
                0x0A => '↵',
                >= 0x20 and <= 0x7E => (char)b,
                _ => '·'
            };
        }

        return new string(chars);
    }
}