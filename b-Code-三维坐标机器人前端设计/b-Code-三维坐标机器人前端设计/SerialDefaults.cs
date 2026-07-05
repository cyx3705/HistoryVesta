namespace b_Code_三维坐标机器人前端设计;

public static class SerialDefaults
{
    public const int BaudRate = 9600;
    public const string BaudRateText = "9600";
    public const string HandshakeSend = SerialProtocol.HandshakeSend;
    public const string HandshakeReply = SerialProtocol.HandshakeReply;
    public const int HandshakeTimeoutMs = 3000;
    public const int HandshakeBootWaitMs = 300;
    public const int HandshakeRetries = 3;
}