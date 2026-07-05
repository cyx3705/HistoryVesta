using System.Text;

namespace b_Code_三维坐标机器人前端设计;

public static class SerialDiagnostics
{
    public static string? AnalyzeRawNoise(IReadOnlyList<byte> samples, int totalBytes)
    {
        if (totalBytes == 0)
            return null;

        if (samples.Count == 0)
            return $"已收 {totalBytes} 字节，但无法解析为 ### 帧 → 多为波特率不匹配。";

        int lCount = samples.Count(b => b == 0x4C);
        int hCount = samples.Count(b => b == 0x48);
        int oCount = samples.Count(b => b == 0x4F);
        double ratioL = (double)lCount / samples.Count;
        double ratioH = (double)hCount / samples.Count;
        double ratioO = (double)oCount / samples.Count;

        if (ratioL + ratioH >= 0.75)
            return "检测到持续 H/L 输出：板子运行的是「轴输出方波与上报」旧测试程序。"
                + " 请用 STC-ISP 烧录「b-Code-51三维坐标控制机器人嵌入式软件」目录下的 text1.hex。";

        if (ratioL >= 0.75)
            return "检测到持续 0x4C('L')：多为波特率/FOSC 不匹配，请确认 9600、FOSC≈11.064MHz。";

        if (ratioO >= 0.75)
            return "检测到持续 0x4F('O')：波特率不匹配时把 OK### 解成 O，请点「扫描波特率」或确认 9600。";

        int b62 = samples.Count(b => b == 0x62);
        int bB9 = samples.Count(b => b == 0xB9);
        if (b62 + bB9 >= samples.Count * 0.6)
            return "检测到 62 B9 乱码：MCU 波特率 TH1 算错。请烧录机器人工程 text1.hex（9600 12T）。";

        int mCount = samples.Count(b => b == 0x4D);
        if (mCount >= samples.Count * 0.75)
            return "检测到持续 'M'(0x4D)：多为错误波特率下的乱码。请用 CMD 扫描或点「CMD串口诊断」。";

        return null;
    }

    public static bool ContainsAscii(IReadOnlyList<byte> buffer, string text)
    {
        if (buffer.Count == 0 || string.IsNullOrEmpty(text))
            return false;

        byte[] pattern = Encoding.ASCII.GetBytes(text);
        if (pattern.Length > buffer.Count)
            return false;

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
                return true;
        }

        return false;
    }
}