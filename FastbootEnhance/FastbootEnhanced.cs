using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OPFlashTool.FastbootEnhance
{
    /// <summary>
    /// 增强的 Fastboot 命令执行器
    /// 借鉴 libxzr/FastbootEnhance 项目设计
    /// </summary>
    public class FastbootExecutor : IDisposable
    {
        private Process _process;
        private readonly string _fastbootPath;
        private readonly string _serial;
        private bool _disposed;

        public StreamReader StdOut { get; private set; }
        public StreamReader StdErr { get; private set; }
        public StreamWriter StdIn { get; private set; }

        public FastbootExecutor(string fastbootPath, string serial, string arguments)
        {
            _fastbootPath = fastbootPath;
            _serial = serial;

            _process = new Process();
            _process.StartInfo = new ProcessStartInfo
            {
                FileName = fastbootPath,
                Arguments = string.IsNullOrEmpty(serial) 
                    ? arguments 
                    : $"-s \"{serial}\" {arguments}",
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            _process.Start();
            StdOut = _process.StandardOutput;
            StdErr = _process.StandardError;
            StdIn = _process.StandardInput;
        }

        public void WaitForExit(int timeoutMs = -1)
        {
            if (timeoutMs > 0)
                _process.WaitForExit(timeoutMs);
            else
                _process.WaitForExit();
        }

        public int ExitCode => _process.ExitCode;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                }
                _process?.Close();
                _process?.Dispose();
            }
            catch { }
        }

        ~FastbootExecutor()
        {
            Dispose();
        }
    }

    /// <summary>
    /// Fastboot 设备数据模型 - 完整解析 getvar all
    /// </summary>
    public class FastbootDeviceData
    {
        // 基础设备信息
        public string Product { get; set; }
        public string SerialNo { get; set; }
        public string Variant { get; set; }
        public string HwRevision { get; set; }
        public bool Secure { get; set; }
        public bool Unlocked { get; set; }

        // 槽位信息 (A/B 设备)
        public string CurrentSlot { get; set; }
        public bool HasSlot => !string.IsNullOrEmpty(CurrentSlot);
        public string SlotSuffixA => "_a";
        public string SlotSuffixB => "_b";

        // Fastboot 模式
        public bool IsFastbootD { get; set; }
        public bool IsUserspace => IsFastbootD;

        // 传输限制
        public long MaxDownloadSize { get; set; } = -1;
        public long MaxFetchSize { get; set; } = -1;

        // VAB (Virtual A/B) 状态
        public string SnapshotUpdateStatus { get; set; }
        public bool IsVabMerging => SnapshotUpdateStatus == "merging";
        public bool IsVabSnapshotted => SnapshotUpdateStatus == "snapshotted";
        public bool HasPendingVabUpdate => IsVabMerging || IsVabSnapshotted;

        // 分区信息
        public Dictionary<string, long> PartitionSizes { get; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, bool> PartitionIsLogical { get; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> PartitionTypes { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, bool> SlotUnbootable { get; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, bool> SlotSuccessful { get; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> SlotRetryCount { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // 额外变量
        public Dictionary<string, string> ExtraVars { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // COW 分区检测
        public bool HasCowPartitions => PartitionSizes.Keys.Any(k => 
            k.EndsWith("-cow", StringComparison.OrdinalIgnoreCase) || 
            k.EndsWith("_cow", StringComparison.OrdinalIgnoreCase));

        public List<string> CowPartitions => PartitionSizes.Keys
            .Where(k => k.EndsWith("-cow", StringComparison.OrdinalIgnoreCase) || 
                       k.EndsWith("_cow", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 逻辑分区列表
        public List<string> LogicalPartitions => PartitionIsLogical
            .Where(kvp => kvp.Value)
            .Select(kvp => kvp.Key)
            .ToList();

        // 物理分区列表
        public List<string> PhysicalPartitions => PartitionIsLogical
            .Where(kvp => !kvp.Value)
            .Select(kvp => kvp.Key)
            .ToList();

        /// <summary>
        /// 从 getvar all 输出解析设备数据
        /// </summary>
        public static FastbootDeviceData Parse(string rawOutput)
        {
            var data = new FastbootDeviceData();

            foreach (var line in rawOutput.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // 格式: (bootloader) key:value 或 key: value
                var trimmed = line.Trim();
                if (!trimmed.Contains(":")) continue;

                // 移除 (bootloader) 前缀
                if (trimmed.StartsWith("(bootloader)"))
                    trimmed = trimmed.Substring(12).Trim();

                var colonIndex = trimmed.IndexOf(':');
                if (colonIndex <= 0) continue;

                var key = trimmed.Substring(0, colonIndex).Trim();
                var value = trimmed.Substring(colonIndex + 1).Trim();

                // 解析分区相关变量 (partition-size:xxx, is-logical:xxx)
                if (key.StartsWith("partition-size", StringComparison.OrdinalIgnoreCase))
                {
                    var partName = key.Length > 15 ? key.Substring(15) : value;
                    var sizeStr = key.Length > 15 ? value : "";
                    
                    // 尝试解析第二个值作为大小
                    var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        partName = parts.Length >= 2 ? parts[0].Replace("partition-size:", "") : partName;
                        sizeStr = parts.Length >= 2 ? parts[parts.Length - 1] : sizeStr;
                    }

                    if (!string.IsNullOrEmpty(partName))
                    {
                        data.PartitionSizes[partName] = ParseHexOrDecimal(sizeStr);
                    }
                    continue;
                }

                if (key.StartsWith("is-logical", StringComparison.OrdinalIgnoreCase))
                {
                    var partName = key.Length > 11 ? key.Substring(11) : "";
                    var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        partName = parts[0].Replace("is-logical:", "");
                        value = parts[parts.Length - 1];
                    }

                    if (!string.IsNullOrEmpty(partName))
                    {
                        data.PartitionIsLogical[partName] = value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                    }
                    continue;
                }

                if (key.StartsWith("partition-type", StringComparison.OrdinalIgnoreCase))
                {
                    var partName = key.Length > 15 ? key.Substring(15) : "";
                    if (!string.IsNullOrEmpty(partName))
                    {
                        data.PartitionTypes[partName] = value;
                    }
                    continue;
                }

                // 解析槽位状态
                if (key.StartsWith("slot-unbootable", StringComparison.OrdinalIgnoreCase))
                {
                    var slot = key.Length > 16 ? key.Substring(16) : "";
                    if (!string.IsNullOrEmpty(slot))
                    {
                        data.SlotUnbootable[slot] = value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                    }
                    continue;
                }

                if (key.StartsWith("slot-successful", StringComparison.OrdinalIgnoreCase))
                {
                    var slot = key.Length > 16 ? key.Substring(16) : "";
                    if (!string.IsNullOrEmpty(slot))
                    {
                        data.SlotSuccessful[slot] = value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                    }
                    continue;
                }

                if (key.StartsWith("slot-retry-count", StringComparison.OrdinalIgnoreCase))
                {
                    var slot = key.Length > 17 ? key.Substring(17) : "";
                    if (!string.IsNullOrEmpty(slot) && int.TryParse(value, out var count))
                    {
                        data.SlotRetryCount[slot] = count;
                    }
                    continue;
                }

                // 解析标准变量
                switch (key.ToLowerInvariant())
                {
                    case "product":
                        data.Product = value;
                        break;
                    case "serialno":
                        data.SerialNo = value;
                        break;
                    case "variant":
                        data.Variant = value;
                        break;
                    case "hw-revision":
                        data.HwRevision = value;
                        break;
                    case "secure":
                        data.Secure = value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "unlocked":
                        data.Unlocked = value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "current-slot":
                        data.CurrentSlot = value;
                        break;
                    case "is-userspace":
                        data.IsFastbootD = value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "max-download-size":
                        data.MaxDownloadSize = ParseHexOrDecimal(value);
                        break;
                    case "max-fetch-size":
                        data.MaxFetchSize = ParseHexOrDecimal(value);
                        break;
                    case "snapshot-update-status":
                        data.SnapshotUpdateStatus = value;
                        break;
                    default:
                        data.ExtraVars[key] = value;
                        break;
                }
            }

            return data;
        }

        private static long ParseHexOrDecimal(string value)
        {
            if (string.IsNullOrEmpty(value)) return -1;

            value = value.Trim();
            
            try
            {
                if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return Convert.ToInt64(value.Substring(2), 16);
                }
                else if (value.All(c => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                {
                    // 尝试作为十六进制解析
                    if (long.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var hexResult))
                        return hexResult;
                }
                
                return long.Parse(value);
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// 获取分区的完整名称 (包含槽位后缀)
        /// </summary>
        public string GetPartitionWithSlot(string baseName, string slot = null)
        {
            if (!HasSlot) return baseName;
            
            slot = slot ?? CurrentSlot;
            if (string.IsNullOrEmpty(slot)) return baseName;

            return $"{baseName}_{slot}";
        }

        /// <summary>
        /// 格式化大小为可读字符串
        /// </summary>
        public static string FormatSize(long bytes)
        {
            if (bytes < 0) return "Unknown";
            if (bytes >= 1024L * 1024 * 1024)
                return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
            if (bytes >= 1024L * 1024)
                return $"{bytes / 1024.0 / 1024:F2} MB";
            if (bytes >= 1024)
                return $"{bytes / 1024.0:F2} KB";
            return $"{bytes} B";
        }
    }

    /// <summary>
    /// VAB (Virtual A/B) 管理器
    /// </summary>
    public class VabManager
    {
        private readonly string _fastbootPath;
        private readonly string _serial;

        public VabManager(string fastbootPath, string serial)
        {
            _fastbootPath = fastbootPath;
            _serial = serial;
        }

        public enum VabStatus
        {
            None,           // 无更新
            Snapshotted,    // 已快照，等待合并
            Merging,        // 正在合并
            Cancelled,      // 已取消
            Unknown         // 未知状态
        }

        public static VabStatus ParseStatus(string status)
        {
            if (string.IsNullOrEmpty(status)) return VabStatus.None;

            return status.ToLowerInvariant() switch
            {
                "none" => VabStatus.None,
                "snapshotted" => VabStatus.Snapshotted,
                "merging" => VabStatus.Merging,
                "cancelled" => VabStatus.Cancelled,
                _ => VabStatus.Unknown
            };
        }

        public static string GetStatusDescription(VabStatus status)
        {
            return status switch
            {
                VabStatus.None => "无待处理更新",
                VabStatus.Snapshotted => "更新已快照，等待下次启动合并",
                VabStatus.Merging => "正在合并更新 (危险状态)",
                VabStatus.Cancelled => "更新已取消",
                VabStatus.Unknown => "未知状态",
                _ => "未知"
            };
        }

        /// <summary>
        /// 检查 VAB 状态并返回警告信息
        /// </summary>
        public static (bool shouldWarn, string message, VabWarningLevel level) CheckVabStatus(FastbootDeviceData data)
        {
            var status = ParseStatus(data.SnapshotUpdateStatus);

            // 检查 Merging 状态 - 最危险
            if (status == VabStatus.Merging)
            {
                return (true, 
                    "⚠️ 危险: 设备正在合并 VAB 更新!\n\n" +
                    "此时刷机极可能导致设备变砖!\n" +
                    "强烈建议等待合并完成后再操作。\n\n" +
                    "如果必须继续，请先执行 'snapshot-update cancel'",
                    VabWarningLevel.Critical);
            }

            // 检查 Snapshotted 状态 - 警告
            if (status == VabStatus.Snapshotted)
            {
                return (true,
                    "⚠️ 警告: 检测到 VAB 更新等待合并\n\n" +
                    "设备在下次启动时将合并更新。\n" +
                    "刷机可能导致系统无法启动。\n\n" +
                    "建议: 先重启到系统完成更新，或执行 'snapshot-update cancel'",
                    VabWarningLevel.Warning);
            }

            // 检查 COW 分区
            if (data.HasCowPartitions)
            {
                return (true,
                    $"⚠️ 警告: 检测到 {data.CowPartitions.Count} 个 COW 分区\n\n" +
                    $"分区: {string.Join(", ", data.CowPartitions.Take(5))}" +
                    (data.CowPartitions.Count > 5 ? "..." : "") + "\n\n" +
                    "这表明 VAB 更新尚未完全清理。\n" +
                    "建议重启到系统清理 COW 分区后再刷机。",
                    VabWarningLevel.Warning);
            }

            return (false, "", VabWarningLevel.None);
        }

        /// <summary>
        /// 取消 VAB 更新
        /// </summary>
        public async Task<(bool success, string message)> CancelUpdateAsync(CancellationToken ct = default)
        {
            try
            {
                using var executor = new FastbootExecutor(_fastbootPath, _serial, "snapshot-update cancel");
                
                var output = await Task.Run(() => executor.StdErr.ReadToEnd(), ct);
                executor.WaitForExit(10000);

                if (output.Contains("OKAY") || executor.ExitCode == 0)
                {
                    return (true, "VAB 更新已取消");
                }

                return (false, $"取消失败: {output}");
            }
            catch (Exception ex)
            {
                return (false, $"执行异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 合并 VAB 更新 (仅限 FastbootD)
        /// </summary>
        public async Task<(bool success, string message)> MergeUpdateAsync(CancellationToken ct = default)
        {
            try
            {
                using var executor = new FastbootExecutor(_fastbootPath, _serial, "snapshot-update merge");
                
                var output = await Task.Run(() => executor.StdErr.ReadToEnd(), ct);
                executor.WaitForExit(300000); // 5分钟超时

                if (output.Contains("OKAY") || executor.ExitCode == 0)
                {
                    return (true, "VAB 更新合并完成");
                }

                return (false, $"合并失败: {output}");
            }
            catch (Exception ex)
            {
                return (false, $"执行异常: {ex.Message}");
            }
        }
    }

    public enum VabWarningLevel
    {
        None,
        Warning,
        Critical
    }

    /// <summary>
    /// 逻辑分区管理器 (仅支持 FastbootD 模式)
    /// </summary>
    public class LogicalPartitionManager
    {
        private readonly string _fastbootPath;
        private readonly string _serial;
        private readonly FastbootDeviceData _deviceData;

        public LogicalPartitionManager(string fastbootPath, string serial, FastbootDeviceData deviceData)
        {
            _fastbootPath = fastbootPath;
            _serial = serial;
            _deviceData = deviceData;
        }

        public bool IsSupported => _deviceData.IsFastbootD;

        /// <summary>
        /// 创建逻辑分区
        /// </summary>
        public async Task<(bool success, string message)> CreatePartitionAsync(
            string name, long sizeBytes, CancellationToken ct = default)
        {
            if (!IsSupported)
                return (false, "逻辑分区操作仅支持 FastbootD 模式");

            if (string.IsNullOrEmpty(name))
                return (false, "分区名称不能为空");

            if (sizeBytes <= 0)
                return (false, "分区大小必须大于 0");

            if (_deviceData.PartitionSizes.ContainsKey(name))
                return (false, $"分区 {name} 已存在");

            try
            {
                using var executor = new FastbootExecutor(_fastbootPath, _serial, 
                    $"create-logical-partition \"{name}\" {sizeBytes}");

                var output = await Task.Run(() => executor.StdErr.ReadToEnd(), ct);
                executor.WaitForExit(30000);

                if (output.Contains("OKAY") || output.Contains("Finished") || executor.ExitCode == 0)
                {
                    return (true, $"成功创建逻辑分区 {name} ({FastbootDeviceData.FormatSize(sizeBytes)})");
                }

                return (false, $"创建失败: {output}");
            }
            catch (Exception ex)
            {
                return (false, $"执行异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 删除逻辑分区
        /// </summary>
        public async Task<(bool success, string message)> DeletePartitionAsync(
            string name, CancellationToken ct = default)
        {
            if (!IsSupported)
                return (false, "逻辑分区操作仅支持 FastbootD 模式");

            if (string.IsNullOrEmpty(name))
                return (false, "分区名称不能为空");

            if (!_deviceData.PartitionIsLogical.TryGetValue(name, out var isLogical) || !isLogical)
                return (false, $"分区 {name} 不是逻辑分区，无法删除");

            try
            {
                using var executor = new FastbootExecutor(_fastbootPath, _serial, 
                    $"delete-logical-partition \"{name}\"");

                var output = await Task.Run(() => executor.StdErr.ReadToEnd(), ct);
                executor.WaitForExit(30000);

                if (output.Contains("OKAY") || output.Contains("Finished") || executor.ExitCode == 0)
                {
                    return (true, $"成功删除逻辑分区 {name}");
                }

                return (false, $"删除失败: {output}");
            }
            catch (Exception ex)
            {
                return (false, $"执行异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 调整逻辑分区大小 (只能扩大)
        /// </summary>
        public async Task<(bool success, string message)> ResizePartitionAsync(
            string name, long newSizeBytes, CancellationToken ct = default)
        {
            if (!IsSupported)
                return (false, "逻辑分区操作仅支持 FastbootD 模式");

            if (string.IsNullOrEmpty(name))
                return (false, "分区名称不能为空");

            if (!_deviceData.PartitionIsLogical.TryGetValue(name, out var isLogical) || !isLogical)
                return (false, $"分区 {name} 不是逻辑分区，无法调整大小");

            var currentSize = _deviceData.PartitionSizes.TryGetValue(name, out var size) ? size : 0;
            if (newSizeBytes < currentSize)
                return (false, $"逻辑分区只能扩大，不能缩小 (当前: {FastbootDeviceData.FormatSize(currentSize)})");

            if (newSizeBytes == currentSize)
                return (true, "大小未变更");

            try
            {
                using var executor = new FastbootExecutor(_fastbootPath, _serial, 
                    $"resize-logical-partition \"{name}\" {newSizeBytes}");

                var output = await Task.Run(() => executor.StdErr.ReadToEnd(), ct);
                executor.WaitForExit(30000);

                if (output.Contains("OKAY") || output.Contains("Finished") || executor.ExitCode == 0)
                {
                    return (true, $"成功调整分区 {name} 大小: {FastbootDeviceData.FormatSize(currentSize)} -> {FastbootDeviceData.FormatSize(newSizeBytes)}");
                }

                return (false, $"调整大小失败: {output}");
            }
            catch (Exception ex)
            {
                return (false, $"执行异常: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// vbmeta 特殊处理器
    /// </summary>
    public static class VbmetaHandler
    {
        private static readonly HashSet<string> VbmetaPartitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "vbmeta", "vbmeta_a", "vbmeta_b",
            "vbmeta_system", "vbmeta_system_a", "vbmeta_system_b",
            "vbmeta_vendor", "vbmeta_vendor_a", "vbmeta_vendor_b"
        };

        /// <summary>
        /// 检查是否是 vbmeta 分区
        /// </summary>
        public static bool IsVbmetaPartition(string partitionName)
        {
            if (string.IsNullOrEmpty(partitionName)) return false;
            return VbmetaPartitions.Contains(partitionName) ||
                   partitionName.StartsWith("vbmeta", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 获取刷写 vbmeta 的额外参数
        /// </summary>
        public static string GetFlashArgs(bool disableVerity, bool disableVerification)
        {
            var args = new List<string>();
            
            if (disableVerity)
                args.Add("--disable-verity");
            
            if (disableVerification)
                args.Add("--disable-verification");

            return args.Count > 0 ? string.Join(" ", args) + " " : "";
        }

        /// <summary>
        /// 获取警告消息
        /// </summary>
        public static string GetWarningMessage()
        {
            return "检测到 vbmeta 分区\n\n" +
                   "是否禁用 Android Verified Boot?\n\n" +
                   "• 禁用后可以刷入第三方 ROM/内核\n" +
                   "• 可能影响 SafetyNet/Play Integrity\n" +
                   "• 某些银行/支付 App 可能无法使用";
        }
    }

    /// <summary>
    /// Fastboot 高级功能服务
    /// </summary>
    public class FastbootService
    {
        private readonly string _fastbootPath;
        private string _currentSerial;
        private FastbootDeviceData _deviceData;

        public event Action<string> OnLog;
        public event Action<int> OnProgress;
        public event Action<FastbootDeviceData> OnDeviceDataLoaded;

        public FastbootDeviceData DeviceData => _deviceData;
        public string CurrentSerial => _currentSerial;
        public bool IsConnected => !string.IsNullOrEmpty(_currentSerial);

        public FastbootService(string fastbootPath)
        {
            _fastbootPath = fastbootPath;
        }

        private void Log(string message) => OnLog?.Invoke(message);
        private void Progress(int percent) => OnProgress?.Invoke(percent);

        /// <summary>
        /// 获取所有连接的 Fastboot 设备
        /// </summary>
        public async Task<List<(string serial, bool isFastbootD)>> GetDevicesAsync(CancellationToken ct = default)
        {
            var devices = new List<(string serial, bool isFastbootD)>();

            try
            {
                using var executor = new FastbootExecutor(_fastbootPath, null, "devices");
                var output = await Task.Run(() => executor.StdOut.ReadToEnd(), ct);
                executor.WaitForExit(5000);

                foreach (var line in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        var serial = parts[0];
                        var mode = parts[1].ToLowerInvariant();
                        var isFastbootD = mode == "fastbootd";

                        // 进一步验证 is-userspace
                        if (!isFastbootD)
                        {
                            using var checkExecutor = new FastbootExecutor(_fastbootPath, serial, "getvar is-userspace");
                            var checkOutput = await Task.Run(() => checkExecutor.StdErr.ReadToEnd(), ct);
                            isFastbootD = checkOutput.Contains("yes");
                        }

                        devices.Add((serial, isFastbootD));
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"获取设备列表失败: {ex.Message}");
            }

            return devices;
        }

        /// <summary>
        /// 连接到设备并加载数据
        /// </summary>
        public async Task<bool> ConnectAsync(string serial, CancellationToken ct = default)
        {
            try
            {
                Log($"正在连接设备: {serial}");
                _currentSerial = serial;

                // 加载设备数据
                await RefreshDeviceDataAsync(ct);

                if (_deviceData != null)
                {
                    Log($"✓ 设备: {_deviceData.Product ?? "Unknown"}");
                    Log($"✓ 模式: {(_deviceData.IsFastbootD ? "FastbootD" : "Fastboot")}");
                    if (_deviceData.HasSlot)
                        Log($"✓ 当前槽位: {_deviceData.CurrentSlot}");
                    if (_deviceData.HasPendingVabUpdate)
                        Log($"⚠ VAB 状态: {VabManager.GetStatusDescription(VabManager.ParseStatus(_deviceData.SnapshotUpdateStatus))}");

                    OnDeviceDataLoaded?.Invoke(_deviceData);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Log($"连接失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 刷新设备数据
        /// </summary>
        public async Task RefreshDeviceDataAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_currentSerial)) return;

            try
            {
                using var executor = new FastbootExecutor(_fastbootPath, _currentSerial, "getvar all");
                
                // Fastboot 的 getvar 输出在 stderr
                var output = await Task.Run(() => executor.StdErr.ReadToEnd(), ct);
                executor.WaitForExit(10000);

                _deviceData = FastbootDeviceData.Parse(output);
            }
            catch (Exception ex)
            {
                Log($"刷新设备数据失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 刷写分区 (支持 vbmeta 特殊处理)
        /// </summary>
        public async Task<(bool success, string message)> FlashPartitionAsync(
            string partition, string imagePath,
            bool disableVbmetaVerity = false, bool disableVbmetaVerification = false,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_currentSerial))
                return (false, "未连接设备");

            if (!File.Exists(imagePath))
                return (false, $"镜像文件不存在: {imagePath}");

            var extraArgs = "";
            
            // vbmeta 特殊处理
            if (VbmetaHandler.IsVbmetaPartition(partition))
            {
                extraArgs = VbmetaHandler.GetFlashArgs(disableVbmetaVerity, disableVbmetaVerification);
            }

            try
            {
                Log($"正在刷写 {partition}...");

                using var executor = new FastbootExecutor(_fastbootPath, _currentSerial, 
                    $"flash {extraArgs}\"{partition}\" \"{imagePath}\"");

                var output = await Task.Run(() => executor.StdErr.ReadToEnd(), ct);
                executor.WaitForExit(300000); // 5分钟超时

                if (output.Contains("OKAY") || output.Contains("Finished") || executor.ExitCode == 0)
                {
                    Log($"✓ {partition} 刷写成功");
                    return (true, $"刷写成功: {partition}");
                }

                Log($"✗ {partition} 刷写失败: {output}");
                return (false, output);
            }
            catch (Exception ex)
            {
                return (false, $"执行异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 擦除分区
        /// </summary>
        public async Task<(bool success, string message)> ErasePartitionAsync(
            string partition, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_currentSerial))
                return (false, "未连接设备");

            try
            {
                Log($"正在擦除 {partition}...");

                using var executor = new FastbootExecutor(_fastbootPath, _currentSerial, 
                    $"erase \"{partition}\"");

                var output = await Task.Run(() => executor.StdErr.ReadToEnd(), ct);
                executor.WaitForExit(60000);

                if (output.Contains("OKAY") || output.Contains("Finished") || executor.ExitCode == 0)
                {
                    Log($"✓ {partition} 擦除成功");
                    return (true, $"擦除成功: {partition}");
                }

                Log($"✗ {partition} 擦除失败: {output}");
                return (false, output);
            }
            catch (Exception ex)
            {
                return (false, $"执行异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 切换活动槽位
        /// </summary>
        public async Task<(bool success, string message)> SetActiveSlotAsync(
            string slot, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_currentSerial))
                return (false, "未连接设备");

            if (!_deviceData.HasSlot)
                return (false, "设备不支持 A/B 分区");

            slot = slot.ToLowerInvariant().Replace("_", "");
            if (slot != "a" && slot != "b")
                return (false, "槽位必须是 a 或 b");

            try
            {
                Log($"正在切换到槽位 {slot}...");

                using var executor = new FastbootExecutor(_fastbootPath, _currentSerial, 
                    $"set_active {slot}");

                var output = await Task.Run(() => executor.StdErr.ReadToEnd(), ct);
                executor.WaitForExit(10000);

                if (output.Contains("OKAY") || output.Contains("Setting current slot") || executor.ExitCode == 0)
                {
                    Log($"✓ 已切换到槽位 {slot}");
                    return (true, $"已切换到槽位 {slot}");
                }

                return (false, output);
            }
            catch (Exception ex)
            {
                return (false, $"执行异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 重启设备
        /// </summary>
        public async Task<(bool success, string message)> RebootAsync(
            RebootTarget target = RebootTarget.System, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_currentSerial))
                return (false, "未连接设备");

            var command = target switch
            {
                RebootTarget.System => "reboot",
                RebootTarget.Bootloader => "reboot bootloader",
                RebootTarget.FastbootD => "reboot fastboot",
                RebootTarget.Recovery => "reboot recovery",
                RebootTarget.Edl => "oem edl",
                _ => "reboot"
            };

            try
            {
                Log($"正在重启到 {target}...");

                using var executor = new FastbootExecutor(_fastbootPath, _currentSerial, command);
                
                var output = await Task.Run(() => executor.StdErr.ReadToEnd(), ct);
                executor.WaitForExit(10000);

                // 重启后断开连接
                _currentSerial = null;
                _deviceData = null;

                return (true, $"已发送重启命令: {target}");
            }
            catch (Exception ex)
            {
                return (false, $"执行异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 执行自定义命令
        /// </summary>
        public async Task<(bool success, string output)> ExecuteCommandAsync(
            string command, int timeoutMs = 30000, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_currentSerial))
                return (false, "未连接设备");

            try
            {
                using var executor = new FastbootExecutor(_fastbootPath, _currentSerial, command);
                
                var stdout = await Task.Run(() => executor.StdOut.ReadToEnd(), ct);
                var stderr = await Task.Run(() => executor.StdErr.ReadToEnd(), ct);
                executor.WaitForExit(timeoutMs);

                var output = (stdout + "\n" + stderr).Trim();
                var success = !output.Contains("FAILED") && executor.ExitCode == 0;

                return (success, output);
            }
            catch (Exception ex)
            {
                return (false, $"执行异常: {ex.Message}");
            }
        }
    }

    public enum RebootTarget
    {
        System,
        Bootloader,
        FastbootD,
        Recovery,
        Edl
    }
}
