using System.Buffers.Binary;

namespace VitaTrace.Infrastructure.Devices;

/// <summary>
/// 纯协议帧解析。这里不打开设备，便于用固定字节做回归测试。
///
/// 当前只放进已经能从公开资料确认的结构：
/// - CompX / Nordic 52 的 17 字节查询（ATK/VXE/Zaopin 等）；
/// - Nordic 54 的 64 字节查询（ATK/VXE 新接收器）。
///
/// 解析失败一律返回 false，调用方必须把“没有读数”与“0%”分开。
/// </summary>
public static class ProtocolFrameParser
{
    public const byte CompxReportId = 0x08;
    public const byte CompxQueryCommand = 0x04;
    public const byte Compx17QueryCommand = 0x17;
    public const int CompxFrameLength = 17;
    public const int Nordic54FrameLength = 64;
    public const byte Nordic54ReportId = 0x08;

    /// <summary>
    /// CompX 17 字节响应：
    /// <code>
    /// 08 04 00 00 00 02 23 00 0E 94 ... 82
    /// ^id ^cmd          ^pct ^wired ^mV   ^sum=0x55
    /// </code>
    /// `0x17` 变体只有电量位置不同：byte5 × 2。
    /// </summary>
    public static bool TryParseCompx17(
        ReadOnlySpan<byte> data,
        byte command,
        out int percent,
        out bool wired,
        out int millivolts,
        out string reason)
    {
        percent = 0;
        wired = false;
        millivolts = 0;
        reason = string.Empty;

        if (data.Length < 10)
        {
            reason = $"帧太短：{data.Length}";
            return false;
        }

        if (data[0] != CompxReportId || data[1] != command)
        {
            reason = $"帧头不符：{data[0]:X2} {data[1]:X2}";
            return false;
        }

        // 完整 17 字节帧的校验和：所有字节和 ≡ 0x55 (mod 256)。
        // 某些固件用较短响应时没有最后一个校验字节，这时只跳过校验。
        if (data.Length >= CompxFrameLength)
        {
            var sum = 0;
            for (var i = 0; i < CompxFrameLength; i++)
            {
                sum = (sum + data[i]) & 0xFF;
            }

            if (sum != 0x55)
            {
                reason = $"校验和错误：{sum:X2}";
                return false;
            }
        }

        var pct = command == CompxQueryCommand ? data[6] : data[5] * 2;
        if (pct is < 0 or > 100)
        {
            reason = $"电量越界：{pct}";
            return false;
        }

        percent = pct;
        wired = data.Length > 7 && data[7] != 0;
        if (data.Length >= 10)
        {
            millivolts = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(8, 2));
        }

        return true;
    }

    /// <summary>
    /// Nordic 54 响应：
    /// <code>
    /// [1]=0x72, [5]=0x07, [2]=0 表示在线；[7]=电量，[8..9]=mV。
    /// </code>
    /// </summary>
    public static bool TryParseNordic54(
        ReadOnlySpan<byte> data,
        out int percent,
        out int millivolts,
        out string reason)
    {
        percent = 0;
        millivolts = 0;
        reason = string.Empty;

        if (data.Length < 10)
        {
            reason = $"帧太短：{data.Length}";
            return false;
        }

        if (data[1] != 0x72 || data[5] != 0x07)
        {
            reason = $"响应标记不符：{data[1]:X2}/{data[5]:X2}";
            return false;
        }

        if (data[2] != 0)
        {
            reason = "外设休眠或未就绪";
            return false;
        }

        var pct = data[7];
        if (pct > 100)
        {
            reason = $"电量越界：{pct}";
            return false;
        }

        percent = pct;
        millivolts = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(8, 2));
        return true;
    }

    /// <summary>构造 CompX 17 字节请求，主要是为了让测试夹具与设备端一致。</summary>
    public static byte[] BuildCompx17Request(byte command)
    {
        var request = new byte[CompxFrameLength];
        request[0] = CompxReportId;
        request[1] = command;
        request[16] = CompxChecksum(request);
        return request;
    }

    /// <summary>构造 Nordic 54 的 64 字节请求；link=1 表示 2.4G 接收器后的设备。</summary>
    public static byte[] BuildNordic54Request(byte link = 1)
    {
        var request = new byte[Nordic54FrameLength];
        request[0] = Nordic54ReportId;
        request[1] = 0x7D;
        request[2] = 0x72;
        request[3] = 0x02;
        request[4] = 0x00;
        request[5] = link;
        request[6] = 0x07;
        request[7] = 0x01;
        return request;
    }

    private static byte CompxChecksum(ReadOnlySpan<byte> request)
    {
        var sum = 0;
        for (var i = 0; i < request.Length - 1; i++)
        {
            sum = (sum + request[i]) & 0xFF;
        }

        return (byte)((0x55 - sum) & 0xFF);
    }
}
