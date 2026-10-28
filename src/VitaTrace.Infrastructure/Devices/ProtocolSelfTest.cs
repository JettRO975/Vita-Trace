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

        // Razer 90B：status=0x02、echo 正确、raw=0xC0 → 75%。
        var razer = new byte[91];
        razer[1] = 0x02;
        razer[2] = 0x1F;
        razer[6] = 0x02;
        razer[7] = 0x07;
        razer[10] = 0xC0;
        Check(
            "Razer 90B",
            RazerBatteryReader.TryParseResponse(razer, out var rpct, out _) && rpct == 75,
            $"pct={rpct}");

        // raw=0 视为休眠，不显示 0%。
        var razerSleep = (byte[])razer.Clone();
        razerSleep[10] = 0;
        Check(
            "Razer 拒绝休眠 0",
            !RazerBatteryReader.TryParseResponse(razerSleep, out _, out _),
            "休眠 raw=0 被错误接受");

        // 回显不匹配必须拒绝。
        var razerBad = (byte[])razer.Clone();
        razerBad[7] = 0x08;
        Check(
            "Razer 拒绝错误回显",
            !RazerBatteryReader.TryParseResponse(razerBad, out _, out _),
            "错误回显被接受");

        // 查询帧长度、CRC 覆盖范围与尾部 0 必须正确。
        var razerRequest = RazerBatteryReader.BuildRequest();
        byte razerCrc = 0;
        for (var i = 3; i <= 88; i++)
        {
            razerCrc ^= razerRequest[i];
        }
        Check(
            "Razer 查询帧",
            razerRequest.Length == 91 && razerRequest[89] == razerCrc && razerRequest[90] == 0,
            $"len={razerRequest.Length} crc=0x{razerRequest[89]:X2} expect=0x{razerCrc:X2}");

        // MCHOSE Realtek 0x13/0x1D：公开样例 0xA2 ^ 0xFF = 0x5D = 93%。
        var mchoseRealtek = new byte[]
        {
            0x13, 0x1D, 0xFE, 0xFE, 0xFF, 0xA2,
            0xFD, 0xFD, 0xF9, 0xD9, 0xB3, 0xC8, 0xDF, 0xAF, 0x8D, 0x90
        };
        Check(
            "MCHOSE Realtek 0x1D",
            MchoseRealtekBatteryReader.TryParseFrame(mchoseRealtek, out var mrPct, out var mrWired, out _) &&
            mrPct == 93 && !mrWired,
            $"pct={mrPct} wired={mrWired}");

        var mchoseWired = (byte[])mchoseRealtek.Clone();
        mchoseWired[4] = 0xFE; // ^0xFF = 1，有线
        Check(
            "MCHOSE Realtek 有线标志",
            MchoseRealtekBatteryReader.TryParseFrame(mchoseWired, out _, out var mrWired2, out _) && mrWired2,
            "有线标志未识别");

        // Cherry：前缀 + byte8=50、byte9=1。
        var cherry = new byte[64];
        cherry[0] = 0x04;
        cherry[1] = 0x20;
        cherry[2] = 0x00;
        cherry[3] = 0x1A;
        cherry[4] = 0x06;
        cherry[8] = 50;
        cherry[9] = 1;
        Check(
            "Cherry 64B",
            CherryBatteryReader.TryParseResponse(cherry, out var chPct, out var chCharging, out _) &&
            chPct == 50 && chCharging,
            $"pct={chPct} charging={chCharging}");

        var cherryBad = (byte[])cherry.Clone();
        cherryBad[3] = 0x99;
        Check(
            "Cherry 拒绝坏前缀",
            !CherryBatteryReader.TryParseResponse(cherryBad, out _, out _, out _),
            "坏前缀被接受");

        logger.Info($"SELFTEST 完成：{passed} passed, {failed} failed");
        return failed == 0;
    }
}
