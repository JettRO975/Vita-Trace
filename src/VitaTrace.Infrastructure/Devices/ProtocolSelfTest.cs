using VitaTrace.Core.Abstractions;

namespace VitaTrace.Infrastructure.Devices;

/// <summary>
/// 无硬件时也能跑的协议自检：只验证“帧解析”和校验规则，
/// 不打开任何设备、不发任何 HID 命令。
///
/// 用法：`VitaTrace.exe --selftest`；返回码 0 = 全部通过。
/// </summary>
public static class ProtocolSelfTest
{
    public static bool Run(IAppLogger logger)
    {
        var passed = 0;
        var failed = 0;

        void Check(string name, bool condition, string detail)
        {
            if (condition)
            {
                passed++;
                logger.Info($"SELFTEST PASS {name}");
            }
            else
            {
                failed++;
                logger.Warn($"SELFTEST FAIL {name}: {detail}");
            }
        }

        // CompX 17 字节 0x04：真实 README 样例，byte6=0x23=35%，byte8/9=0x0E94=3732mV。
        var compx04 = new byte[]
        {
            0x08, 0x04, 0x00, 0x00, 0x00, 0x02,
            0x23, 0x00, 0x0E, 0x94, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x82
        };
        Check(
            "Compx17 0x04",
            ProtocolFrameParser.TryParseCompx17(compx04, ProtocolFrameParser.CompxQueryCommand, out var pct04, out var wired04, out var mv04, out _) &&
            pct04 == 0x23 && !wired04 && mv04 == 0x0E94,
            $"pct={pct04} wired={wired04} mV={mv04}");

        // CompX 0x17：byte5=0x19 → 50%；校验和按 0x55 规则现算。
        var compx17 = new byte[ProtocolFrameParser.CompxFrameLength];
        compx17[0] = 0x08;
        compx17[1] = 0x17;
        compx17[5] = 0x19;
        compx17[8] = 0x0E;
        compx17[9] = 0x94;
        var sum = 0;
        for (var i = 0; i < compx17.Length - 1; i++)
        {
            sum = (sum + compx17[i]) & 0xFF;
        }
        compx17[16] = (byte)((0x55 - sum) & 0xFF);
        Check(
            "Compx17 0x17",
            ProtocolFrameParser.TryParseCompx17(compx17, ProtocolFrameParser.Compx17QueryCommand, out var pct17, out _, out var mv17, out _) &&
            pct17 == 50 && mv17 == 0x0E94,
            $"pct={pct17} mV={mv17}");

        // 改坏校验和必须拒绝，避免把噪声当电量。
        var broken = (byte[])compx04.Clone();
        broken[16] ^= 0xFF;
        Check(
            "Compx17 拒绝坏校验",
            !ProtocolFrameParser.TryParseCompx17(broken, ProtocolFrameParser.CompxQueryCommand, out _, out _, out _, out _),
            "坏校验被错误接受");

        // Nordic 54：byte7=50%，byte8/9=3732mV。
        var nordic = new byte[ProtocolFrameParser.Nordic54FrameLength];
        nordic[1] = 0x72;
        nordic[2] = 0x00;
        nordic[5] = 0x07;
        nordic[7] = 50;
        nordic[8] = 0x0E;
        nordic[9] = 0x94;
        Check(
            "Nordic54",
            ProtocolFrameParser.TryParseNordic54(nordic, out var npct, out var nmv, out _) &&
            npct == 50 && nmv == 0x0E94,
            $"pct={npct} mV={nmv}");

        logger.Info($"SELFTEST 完成：{passed} passed, {failed} failed");
        return failed == 0;
    }
}
