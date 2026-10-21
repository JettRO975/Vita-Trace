using System.Text;
using System.Text.Json;
using VitaTrace.Core.Abstractions;
using VitaTrace.Core.Models;
using VitaTrace.Infrastructure.Storage;

namespace VitaTrace.Infrastructure.Storage;

/// <summary>
/// 用单个 JSON 文件保存电量历史。
///
/// 为什么不用 SQLite：
/// - 当前项目不引入第三方 NuGet（开发机离线），SQLite 会增加部署与原生库依赖；
/// - 历史是低频、小体量数据（每设备最多 90 天、变化时才采点）；
/// - JSON 便于用户备份/查看，也足够当前规模。
///
/// 写入使用“临时文件 + File.Move 覆盖”，避免进程中断留下半个 JSON。
/// 读写都在进程内锁内完成；界面层通过接口访问，不直接碰文件。
/// </summary>
public sealed class JsonHistoryStore : IHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly string _path;
    private readonly IAppLogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<BatterySample>> _data =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _loaded;

    public JsonHistoryStore(string path, IAppLogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger.Instance;
    }

    public IReadOnlyList<BatterySample> GetSamples(string deviceId, DateTimeOffset since)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (!_data.TryGetValue(deviceId, out var samples))
            {
                return Array.Empty<BatterySample>();
            }

            return samples
                .Where(s => s.Timestamp >= since)
                .OrderBy(s => s.Timestamp)
                .ToList();
        }
    }

    public void Append(string deviceId, BatterySample sample)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (!_data.TryGetValue(deviceId, out var samples))
            {
                samples = new List<BatterySample>();
                _data[deviceId] = samples;
            }

            samples.Add(sample);
            SaveLocked();
        }
    }

    public void Prune(DateTimeOffset olderThan)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var removed = 0;
            foreach (var samples in _data.Values)
            {
                removed += samples.RemoveAll(s => s.Timestamp < olderThan);
            }

            if (removed > 0)
            {
                SaveLocked();
                _logger.Info($"历史清理：删除 {removed} 个早于 {olderThan:yyyy-MM-dd} 的采样点");
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var file = JsonSerializer.Deserialize<HistoryFile>(json, JsonOptions);
            if (file?.Devices is null)
            {
                return;
            }

            foreach (var pair in file.Devices)
            {
                var samples = pair.Value
                    .Where(s => s.Percent is null or >= 0 and <= 100)
                    .Select(s => new BatterySample(s.Timestamp, s.Percent))
                    .OrderBy(s => s.Timestamp)
                    .ToList();

                if (samples.Count > 0)
                {
                    _data[pair.Key] = samples;
                }
            }
        }
        catch (Exception ex)
        {
            // 历史损坏不能让应用启动失败；保留原文件供排查，内存里从空历史开始。
            _logger.Error($"读取历史失败：{_path}", ex);
            TryBackupCorruptFile();
            _data.Clear();
        }
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            var backup = $"{_path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(_path, backup, overwrite: true);
            _logger.Warn($"历史文件已备份为 {backup}");
        }
        catch (Exception ex)
        {
            _logger.Warn($"备份损坏的历史文件失败：{ex.Message}");
        }
    }

    private void SaveLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var file = new HistoryFile
            {
                Version = 1,
                Devices = _data.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value
                        .Select(s => new SampleDto { Timestamp = s.Timestamp, Percent = s.Percent })
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase)
            };

            var json = JsonSerializer.Serialize(file, JsonOptions);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // 写盘失败不能影响电量监控本身；日志保留原因，下次采样会再试。
            _logger.Error($"保存历史失败：{_path}", ex);
        }
    }

    private sealed class HistoryFile
    {
        public int Version { get; set; } = 1;

        public Dictionary<string, List<SampleDto>> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SampleDto
    {
        public DateTimeOffset Timestamp { get; set; }

        public int? Percent { get; set; }
    }
}
