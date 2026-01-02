using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChromeosUpdateEngine;

namespace OPFlashTool.FastbootEnhance
{
    /// <summary>
    /// Payload.bin 直接刷入服务
    /// 借鉴 libxzr/FastbootEnhance 的 Payload 刷入逻辑
    /// </summary>
    public class PayloadFlasher : IDisposable
    {
        private readonly FastbootService _fastbootService;
        private readonly string _tempDirectory;
        private Payload _payload;
        private bool _disposed;

        public event Action<string> OnLog;
        public event Action<int, int, string> OnProgress; // current, total, partitionName
        public event Action<string, bool> OnPartitionFlashed; // partitionName, success

        public PayloadFlasher(FastbootService fastbootService, string tempDirectory = null)
        {
            _fastbootService = fastbootService;
            _tempDirectory = tempDirectory ?? Path.Combine(Path.GetTempPath(), "MultiFlashTool_PayloadFlash");
        }

        private void Log(string message) => OnLog?.Invoke(message);

        /// <summary>
        /// 从 Payload 直接刷入设备 (FastbootD 模式)
        /// </summary>
        public async Task<PayloadFlashResult> FlashPayloadAsync(
            string payloadPath,
            IEnumerable<string> selectedPartitions = null,
            bool ignoreUnknownPartitions = false,
            bool ignoreChecks = false,
            bool disableVbmetaVerity = false,
            CancellationToken ct = default)
        {
            var result = new PayloadFlashResult();

            try
            {
                // 检查设备连接
                if (!_fastbootService.IsConnected)
                {
                    result.Success = false;
                    result.ErrorMessage = "未连接设备";
                    return result;
                }

                var deviceData = _fastbootService.DeviceData;

                // 检查 FastbootD 模式
                if (!deviceData.IsFastbootD)
                {
                    Log("⚠ 设备不在 FastbootD 模式，某些逻辑分区可能无法刷入");
                }

                // 检查 VAB 状态
                var (shouldWarn, vabMessage, warningLevel) = VabManager.CheckVabStatus(deviceData);
                if (shouldWarn && warningLevel == VabWarningLevel.Critical)
                {
                    result.VabWarning = vabMessage;
                    result.VabWarningLevel = warningLevel;
                    // 继续执行，但记录警告
                    Log(vabMessage);
                }

                // 创建临时目录
                if (Directory.Exists(_tempDirectory))
                {
                    try { Directory.Delete(_tempDirectory, true); } catch { }
                }
                Directory.CreateDirectory(_tempDirectory);

                // 加载 Payload
                Log($"正在加载 Payload: {Path.GetFileName(payloadPath)}");
                _payload = new Payload(payloadPath, _tempDirectory);

                var initException = _payload.init();
                if (initException != null)
                {
                    result.Success = false;
                    result.ErrorMessage = $"Payload 初始化失败: {initException.Message}";
                    return result;
                }

                Log($"✓ Payload 版本: {_payload.file_format_version}");
                Log($"✓ 数据大小: {FastbootDeviceData.FormatSize((long)_payload.data_size)}");
                Log($"✓ 分区数量: {_payload.manifest.Partitions.Count}");

                // 确定要刷入的分区
                var partitionsToFlash = new List<PartitionUpdate>();
                var unknownPartitions = new List<string>();

                foreach (var partition in _payload.manifest.Partitions)
                {
                    // 如果指定了分区列表，只刷入指定的分区
                    if (selectedPartitions != null && !selectedPartitions.Contains(partition.PartitionName, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // 检查设备是否有该分区
                    var partitionName = partition.PartitionName;
                    var hasPartition = deviceData.PartitionSizes.ContainsKey(partitionName);
                    
                    // 尝试带槽位后缀
                    if (!hasPartition && deviceData.HasSlot)
                    {
                        var withSlot = $"{partitionName}_{deviceData.CurrentSlot}";
                        hasPartition = deviceData.PartitionSizes.ContainsKey(withSlot);
                    }

                    if (!hasPartition)
                    {
                        unknownPartitions.Add(partitionName);
                        if (!ignoreUnknownPartitions)
                        {
                            continue;
                        }
                    }

                    partitionsToFlash.Add(partition);
                }

                if (unknownPartitions.Count > 0)
                {
                    var msg = $"设备缺少以下分区: {string.Join(", ", unknownPartitions)}";
                    Log($"⚠ {msg}");
                    
                    if (!ignoreUnknownPartitions && !deviceData.IsFastbootD)
                    {
                        result.Success = false;
                        result.ErrorMessage = msg + "\n\n提示: 进入 FastbootD 模式可能可以刷入逻辑分区";
                        return result;
                    }
                }

                if (partitionsToFlash.Count == 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "没有可刷入的分区";
                    return result;
                }

                Log($"准备刷入 {partitionsToFlash.Count} 个分区");

                // 开始刷入流程
                int totalSteps = partitionsToFlash.Count * 2; // 提取 + 刷入
                int currentStep = 0;

                foreach (var partition in partitionsToFlash)
                {
                    ct.ThrowIfCancellationRequested();

                    var partitionName = partition.PartitionName;
                    
                    // 步骤1: 提取分区镜像
                    Log($"[{currentStep + 1}/{totalSteps}] 正在提取 {partitionName}...");
                    OnProgress?.Invoke(currentStep, totalSteps, partitionName);

                    var extractException = _payload.extract(partitionName, _tempDirectory, true, ignoreChecks);
                    if (extractException != null)
                    {
                        Log($"✗ 提取 {partitionName} 失败: {extractException.Message}");
                        result.FailedPartitions.Add(partitionName);
                        OnPartitionFlashed?.Invoke(partitionName, false);
                        currentStep += 2;
                        continue;
                    }

                    currentStep++;
                    Log($"✓ 提取 {partitionName} 完成");

                    // 步骤2: 刷入分区
                    var imagePath = Path.Combine(_tempDirectory, $"{partitionName}.img");
                    if (!File.Exists(imagePath))
                    {
                        Log($"✗ 镜像文件不存在: {imagePath}");
                        result.FailedPartitions.Add(partitionName);
                        OnPartitionFlashed?.Invoke(partitionName, false);
                        currentStep++;
                        continue;
                    }

                    Log($"[{currentStep + 1}/{totalSteps}] 正在刷入 {partitionName}...");
                    OnProgress?.Invoke(currentStep, totalSteps, partitionName);

                    // 确定实际刷入的分区名 (可能需要加槽位后缀)
                    var flashPartitionName = partitionName;
                    if (!deviceData.PartitionSizes.ContainsKey(partitionName) && deviceData.HasSlot)
                    {
                        flashPartitionName = $"{partitionName}_{deviceData.CurrentSlot}";
                    }

                    var (success, message) = await _fastbootService.FlashPartitionAsync(
                        flashPartitionName, imagePath,
                        disableVbmetaVerity && VbmetaHandler.IsVbmetaPartition(partitionName),
                        disableVbmetaVerity && VbmetaHandler.IsVbmetaPartition(partitionName),
                        ct);

                    if (success)
                    {
                        result.SuccessfulPartitions.Add(partitionName);
                        OnPartitionFlashed?.Invoke(partitionName, true);
                    }
                    else
                    {
                        Log($"✗ 刷入 {partitionName} 失败: {message}");
                        result.FailedPartitions.Add(partitionName);
                        OnPartitionFlashed?.Invoke(partitionName, false);
                    }

                    currentStep++;

                    // 删除临时文件以节省空间
                    try { File.Delete(imagePath); } catch { }
                }

                OnProgress?.Invoke(totalSteps, totalSteps, "完成");

                result.Success = result.FailedPartitions.Count == 0;
                result.TotalPartitions = partitionsToFlash.Count;

                Log($"\n========== 刷入完成 ==========");
                Log($"成功: {result.SuccessfulPartitions.Count}");
                Log($"失败: {result.FailedPartitions.Count}");
                
                if (result.FailedPartitions.Count > 0)
                {
                    Log($"失败分区: {string.Join(", ", result.FailedPartitions)}");
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = "操作已取消";
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"刷入异常: {ex.Message}";
                return result;
            }
            finally
            {
                // 清理
                _payload?.Dispose();
                _payload = null;

                try
                {
                    if (Directory.Exists(_tempDirectory))
                    {
                        Directory.Delete(_tempDirectory, true);
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// 获取 Payload 中的分区列表
        /// </summary>
        public async Task<List<PayloadPartitionInfo>> GetPayloadPartitionsAsync(string payloadPath)
        {
            var partitions = new List<PayloadPartitionInfo>();

            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "MultiFlashTool_PayloadInfo");
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
                Directory.CreateDirectory(tempDir);

                using var payload = new Payload(payloadPath, tempDir);
                var initException = payload.init();
                
                if (initException != null)
                {
                    throw new Exception($"Payload 初始化失败: {initException.Message}");
                }

                foreach (var partition in payload.manifest.Partitions)
                {
                    var info = new PayloadPartitionInfo
                    {
                        Name = partition.PartitionName,
                        Size = partition.NewPartitionInfo?.Size ?? 0,
                        Hash = partition.NewPartitionInfo?.Hash?.ToBase64() ?? "",
                        OperationCount = partition.Operations?.Count ?? 0
                    };
                    partitions.Add(info);
                }

                // 清理
                try { Directory.Delete(tempDir, true); } catch { }
            }
            catch (Exception ex)
            {
                Log($"获取 Payload 分区列表失败: {ex.Message}");
            }

            return partitions;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _payload?.Dispose();

            try
            {
                if (Directory.Exists(_tempDirectory))
                {
                    Directory.Delete(_tempDirectory, true);
                }
            }
            catch { }
        }
    }

    public class PayloadFlashResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public int TotalPartitions { get; set; }
        public List<string> SuccessfulPartitions { get; } = new List<string>();
        public List<string> FailedPartitions { get; } = new List<string>();
        public string VabWarning { get; set; }
        public VabWarningLevel VabWarningLevel { get; set; }
    }

    public class PayloadPartitionInfo
    {
        public string Name { get; set; }
        public ulong Size { get; set; }
        public string Hash { get; set; }
        public int OperationCount { get; set; }

        public string SizeFormatted => FastbootDeviceData.FormatSize((long)Size);
    }
}
