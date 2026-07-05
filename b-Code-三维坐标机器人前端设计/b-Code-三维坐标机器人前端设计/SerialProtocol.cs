using System.Text;

namespace b_Code_三维坐标机器人前端设计;

public static class SerialProtocol
{
    public const string FrameSuffix = "###";
    public const string HandshakeSend = "PING";
    public const string HandshakeReply = "PONG";
    public const string StepDoneReply = "OK";

    public static byte[] PackFrame(string payload)
        => Encoding.ASCII.GetBytes(payload + FrameSuffix);

    public static string FormatHzCommand(int fx, int fy, int fz, int dirX, int dirY, int dirZ)
        => $"{fx},{fy},{fz},{dirX},{dirY},{dirZ}";

    public static bool TryExtractFrames(List<byte> buffer, out List<string> frames)
    {
        frames = [];
        byte[] pattern = Encoding.ASCII.GetBytes(FrameSuffix);

        while (true)
        {
            int idx = IndexOfPattern(buffer, pattern);
            if (idx < 0)
                break;

            string frame = Encoding.ASCII.GetString([.. buffer.Take(idx)]).Trim();
            buffer.RemoveRange(0, idx + pattern.Length);

            if (frame.Length > 0)
                frames.Add(frame);
        }

        return frames.Count > 0;
    }

    private static int IndexOfPattern(List<byte> buffer, byte[] pattern)
    {
        if (buffer.Count < pattern.Length)
            return -1;

        for (int i = 0; i <= buffer.Count - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (buffer[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return i;
        }

        return -1;
    }
}
