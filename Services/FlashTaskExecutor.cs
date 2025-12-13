using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OPFlashTool.Qualcomm;
using OPFlashTool.Strategies; 

namespace OPFlashTool.Services
{
    public class FlashTaskExecutor
    {
        public FirehoseClient Client { get; }
        private Action<string> _log;
        private IDeviceStrategy _strategy; 

        public int SectorSize { get; }

        // 进度事件
        public event Action<long, long>? ProgressChanged; // 单文件进度 (0-100%)
        public event Action<int, int> TaskProgressChanged; // 总任务进度 (当前个/总个数)
        public event Action<string> StatusChanged; // 状态栏文字

        public FlashTaskExecutor(FirehoseClient client, IDeviceStrategy strategy, Action<string> log, int sectorSize)
        {
            Client = client;
            _strategy = strategy;
            _log = log;
            SectorSize = sectorSize;
        }

        // 云端功能已移除 - 进度通过事件通知
        private void UpdateProgress(long current, long total, bool isSingleTask, long batchProcessed = 0, long batchTotal = 0, Stopwatch sw = null)
        {
            // 触发进度事件供 UI 层处理
            ProgressChanged?.Invoke(current, total);
        }

        private string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec < 1024) return $"{bytesPerSec:F1} B/s";
            if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:F1} KB/s";
            return $"{bytesPerSec / 1024 / 1024:F1} MB/s";
        }

        // [新增] 获取用于进度计算的真实文件大小（Sparse 返回解压后的大小）
        private long GetRealImageSize(string filePath)
        {
            if (!File.Exists(filePath)) return 0;
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var br = new BinaryReader(fs))
                {
                    if (fs.Length > 28)
                    {
                        uint magic = br.ReadUInt32();
                        if (magic == 0xED26FF3A) // Sparse Magic
                        {
                            fs.Seek(12, SeekOrigin.Begin);
                            uint blkSz = br.ReadUInt32();
                            uint totalBlks = br.ReadUInt32();
                            return (long)blkSz * totalBlks;
                        }
                    }
                }
                return new FileInfo(filePath).Length;
            }
            catch
            {
                return new FileInfo(filePath).Length;
            }
        }

        // 代理方法：获取分区表
        public async Task<List<PartitionInfo>> GetPartitionsAsync(CancellationToken ct)
        {
            UpdateStatus("正在读取分区表 (GPT)...");
            return await _strategy.ReadGptAsync(Client, ct, _log);
        }

        // 代理方法：读取分区
        public async Task ReadPartitionAsync(PartitionInfo part, string savePath, CancellationToken ct)
        {
            UpdateStatus($"正在读取分区: {part.Name}");
            Stopwatch sw = new Stopwatch();
            sw.Start();

            bool success = await _strategy.ReadPartitionAsync(Client, part, savePath,
                (c, t) => UpdateProgress(c, t, true, 0, 0, sw),
                ct, _log);

            sw.Stop();
            if (!success) throw new Exception($"读取 {part.Name} 失败");
            UpdateProgress(100, 100, true);
        }

        // 代理方法：擦除分区
        public async Task ErasePartitionAsync(PartitionInfo part, CancellationToken ct)
        {
            UpdateStatus($"正在擦除分区: {part.Name}");
            UpdateProgress(0, 100, true);
            bool success = await _strategy.ErasePartitionAsync(Client, part, ct, _log);
            if (!success) throw new Exception($"擦除 {part.Name} 失败");
            UpdateProgress(100, 100, true);
        }
        
        // [优化] 批量写入任务（使用 Sparse 展开大小计算总进度）
        public async Task ExecuteFlashTasksAsync(List<FlashPartitionInfo> tasks, bool protectLun5, List<string> patchFiles, CancellationToken ct)
        {
            int successCount = 0;
            int failCount = 0;

            long totalBatchBytes = 0;
            foreach (var t in tasks)
            {
                // 优先使用 GPT 提供的分区大小来统计总进度，这样批量任务的大进度条不会和单任务进度重复
                if (t.NumSectors > 0)
                {
                    totalBatchBytes += t.NumSectors * SectorSize;
                }
                else if (File.Exists(t.Filename))
                {
                    totalBatchBytes += GetRealImageSize(t.Filename);
                }
            }

            long processedBatchBytes = 0;
            UpdateProgress(0, 1, false, 0, totalBatchBytes);

            int taskIndex = 0;
            foreach (var task in tasks)
            {
                taskIndex++;
                if (ct.IsCancellationRequested) return;

                UpdateStatus($"正在写入: {task.Name} ({taskIndex}/{tasks.Count})");

                long currentTaskBytes = 0;
                if (task.NumSectors > 0)
                {
                    currentTaskBytes = task.NumSectors * SectorSize;
                }
                else if (File.Exists(task.Filename))
                {
                    currentTaskBytes = GetRealImageSize(task.Filename);
                }

                if (protectLun5 && task.Lun == "5")
                {
                    _log($"[Skip] LUN5 保护已启用，跳过: {task.Name}");
                    processedBatchBytes += currentTaskBytes;
                    UpdateProgress(0, 0, false, processedBatchBytes, totalBatchBytes);
                    continue;
                }

                if (!File.Exists(task.Filename))
                {
                    _log($"[Skip] 文件不存在: {task.Name}");
                    failCount++;
                    continue;
                }

                try
                {
                    Stopwatch sw = new Stopwatch();
                    sw.Start();

                    var partInfo = new PartitionInfo 
                    {
                        Name = task.Name,
                        StartLba = ulong.TryParse(task.StartSector, out ulong lba) ? lba : 0,
                        StartLbaStr = task.StartSector,
                        Sectors = (ulong)task.NumSectors,
                        Lun = int.Parse(task.Lun),
                        SectorSize = SectorSize
                    };

                    bool result = await _strategy.WritePartitionAsync(Client, partInfo, task.Filename,
                        (currentFileBytes, totalFileBytes) =>
                            UpdateProgress(currentFileBytes, totalFileBytes, false, processedBatchBytes, totalBatchBytes, sw),
                        ct, _log);

                    sw.Stop();

                    if (result)
                    {
                        successCount++;
                        _log($"[Success] {task.Name} 写入成功");
                    }
                    else
                    {
                        failCount++;
                        _log($"[Fail] {task.Name} 写入失败");
                    }
                }
                catch (Exception ex)
                {
                    failCount++;
                    _log($"[Error] {task.Name}: {ex.Message}");
                }

                processedBatchBytes += currentTaskBytes;
                UpdateProgress(0, 0, false, processedBatchBytes, totalBatchBytes);
            }

            if (patchFiles != null && patchFiles.Count > 0)
            {
                UpdateStatus("正在应用补丁...");
                foreach (var patch in patchFiles)
                {
                    if (File.Exists(patch))
                    {
                        _log($"[Patch] 应用补丁: {Path.GetFileName(patch)}");
                        string content = File.ReadAllText(patch);
                        Client.ApplyPatch(content);
                    }
                }
            }

            UpdateStatus("刷机任务完成");
            UpdateProgress(100, 100, true);
        }

        // [优化] 批量读取任务
        public async Task ExecuteReadTasksAsync(List<FlashPartitionInfo> tasks, string outputDirectory, CancellationToken ct)
        {
            if (!Directory.Exists(outputDirectory))
            {
                try { Directory.CreateDirectory(outputDirectory); }
                catch (Exception ex) 
                {
                    _log($"[Error] 无法创建输出目录: {ex.Message}");
                    return;
                }
            }

            int total = tasks.Count;
            int current = 0;
            
            long totalBatchBytes = 0;
            foreach(var t in tasks) totalBatchBytes += t.NumSectors * SectorSize;
            
            long processedBatchBytes = 0;

            UpdateProgress(0, 1, false, 0, totalBatchBytes);

            foreach (var task in tasks)
            {
                if (ct.IsCancellationRequested) return;
                current++;
                UpdateStatus($"正在读取: {task.Name} ({current}/{total})");

                string safeFileName = !string.IsNullOrWhiteSpace(task.Filename) 
                    ? Path.GetFileName(task.Filename) 
                    : $"{task.Name}.bin";

                string savePath = Path.Combine(outputDirectory, safeFileName);
                long taskBytes = task.NumSectors * SectorSize;

                try
                {
                    var partInfo = new PartitionInfo
                    {
                        Name = task.Name,
                        StartLba = ulong.TryParse(task.StartSector, out ulong lba) ? lba : 0,
                        StartLbaStr = task.StartSector,
                        Sectors = (ulong)task.NumSectors,
                        Lun = int.Parse(task.Lun),
                        SectorSize = SectorSize
                    };

                    Stopwatch sw = new Stopwatch();
                    sw.Start();

                    bool success = await _strategy.ReadPartitionAsync(Client, partInfo, savePath,
                        (c, t) => UpdateProgress(c, t, false, processedBatchBytes, totalBatchBytes, sw),
                        ct, _log);

                    sw.Stop();

                    if (success)
                    {
                        _log($"[Success] {task.Name} -> {safeFileName}");
                    }
                    else
                    {
                        _log($"[Fail] 读取 {task.Name} 失败");
                    }
                }
                catch (Exception ex)
                {
                    _log($"[Error] {task.Name}: {ex.Message}");
                }

                processedBatchBytes += taskBytes;
                UpdateProgress(0, 0, false, processedBatchBytes, totalBatchBytes);
            }

            UpdateStatus("批量读取完成");
            UpdateProgress(100, 100, true);
        }

        private void UpdateStatus(string msg)
        {
            StatusChanged?.Invoke(msg);
        }

        // 批量擦除任务
        public async Task ExecuteEraseTasksAsync(List<FlashPartitionInfo> tasks, bool protectLun5, CancellationToken ct)
        {
            int total = tasks.Count;
            int current = 0;
            UpdateProgress(0, total, false, 0, total);

            foreach (var task in tasks)
            {
                if (ct.IsCancellationRequested) return;

                UpdateStatus($"正在擦除: {task.Name} ({current + 1}/{total})");

                if (protectLun5 && task.Lun == "5")
                {
                    _log($"[Skip] LUN5 保护: {task.Name}");
                    current++;
                    UpdateProgress(0, 0, false, current, total);
                    continue;
                }

                _log($"[Erase] 正在擦除 {task.Name}...");
                
                var partInfo = new PartitionInfo 
                {
                    Name = task.Name,
                    StartLbaStr = task.StartSector,
                    Sectors = (ulong)task.NumSectors,
                    Lun = int.Parse(task.Lun),
                    SectorSize = SectorSize
                };

                // 擦除时，小进度条模拟 0->100
                UpdateProgress(0, 100, false, current, total);
                if (await _strategy.ErasePartitionAsync(Client, partInfo, ct, _log))
                {
                    _log($"[Success] {task.Name} 擦除成功");
                }
                else
                {
                    _log($"[Fail] {task.Name} 擦除失败");
                }
                UpdateProgress(100, 100, false, current, total);

                current++;
                UpdateProgress(0, 0, false, current, total);
            }
            UpdateStatus("擦除完成");
            UpdateProgress(100, 100, true);
        }
        
        /// <summary>
        /// 智能入口：只需选择固件根目录，自动寻找 META 和 IMAGES
        /// </summary>
        public async Task FlashSuperFromRootDirectoryAsync(string rootDirectory, bool protectLun5, bool metaSuper, CancellationToken ct)
        {
            _log($"[流程] 启动智能直刷模式 (根目录: {Path.GetFileName(rootDirectory)})");

            if (!Directory.Exists(rootDirectory))
            {
                _log("[错误] 目录不存在。");
                return;
            }

            // 1. 自动寻找配置文件 (META/*.json)
            string metaDir = Path.Combine(rootDirectory, "META");
            if (!Directory.Exists(metaDir))
            {
                // 尝试兼容模式：也许 json 直接在根目录？
                metaDir = rootDirectory;
            }

            // 查找 super_def*.json 或 *.json
            var jsonFiles = Directory.GetFiles(metaDir, "*.json");
            string jsonPath = jsonFiles.FirstOrDefault(f => Path.GetFileName(f).StartsWith("super_def", StringComparison.OrdinalIgnoreCase));
            
            // 如果没找到 super_def 开头的，就找任意 json，但排除常见的非配置 json
            if (string.IsNullOrEmpty(jsonPath))
            {
                jsonPath = jsonFiles.FirstOrDefault(f => 
                    !Path.GetFileName(f).Equals("config.json", StringComparison.OrdinalIgnoreCase) && 
                    !Path.GetFileName(f).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                );
            }

            if (string.IsNullOrEmpty(jsonPath))
            {
                _log("[错误] 在 META 文件夹中未找到有效的 super 分区定义文件 (.json)。");
                return;
            }

            _log($"[锁定] 配置文件: {Path.GetFileName(jsonPath)}");

            // 2. 验证 IMAGES 目录 (虽然 SuperMaker 会自己拼路径，但这步为了快速报错)
            string imagesDir = Path.Combine(rootDirectory, "IMAGES");
            if (!Directory.Exists(imagesDir))
            {
                _log("[警告] 根目录下未找到 IMAGES 文件夹，如果 JSON 中的路径是相对路径，可能会失败。");
            }

            // 3. 调用核心方法
            await FlashSuperNoMergeAsync(jsonPath, rootDirectory, protectLun5, metaSuper, ct);
        }

        /// <summary>
        /// 执行 Super 分区不合并直刷流程
        /// </summary>
        public async Task FlashSuperNoMergeAsync(
            string jsonPath,          // super_def.json 的路径
            string imageSearchDir,    // 存放 system.img, vendor.img 等文件的根目录
            bool protectLun5,
            bool metaSuper,
            CancellationToken ct)
        {
            _log("[流程] 开始 Super 分区无损直刷模式...");

            // 1. 读取设备 GPT，获取 super 分区的绝对物理位置
            _log("[1/4] 正在读取设备分区表 (GPT)...");
            
            byte[] gptData = await Client.ReadGptPacketAsync("0", 0, 34, "PrimaryGPT", "gpt_main0.bin", ct);
            if (gptData == null)
            {
                _log("[错误] 无法读取设备 GPT，流程终止。");
                return;
            }

            var partitions = GptParser.ParseGptBytes(gptData, 0);
            
            var superPartition = partitions.FirstOrDefault(p => p.Name.Equals("super", StringComparison.OrdinalIgnoreCase));
            if (superPartition == null)
            {
                _log("[错误] 设备分区表中未找到 'super' 分区！无法继续。");
                return;
            }

            ulong superStartSector = superPartition.StartLba;
            _log($"[信息] 定位到 Super 分区绝对起始扇区: {superStartSector}");

            // 2. 计算 Super 内部布局 (调用 SuperMaker)
            _log("[2/4] 正在计算 Super 内部布局并准备镜像...");
            
            var superMaker = new SuperMaker(AppDomain.CurrentDomain.BaseDirectory, _log);
            var actions = await superMaker.PrepareDirectFlashActionsAsync(jsonPath, imageSearchDir);
            
            if (actions == null || actions.Count == 0)
            {
                _log("[错误] 布局计算失败或未找到有效分区。");
                return;
            }

            // 3. 构造刷写任务列表
            _log($"[3/4] 生成刷写任务列表 ({actions.Count} 个子分区)...");
            
            var flashTasks = new List<FlashPartitionInfo>();

            foreach (var action in actions)
            {
                int deviceSectorSize = Client.SectorSize > 0 ? Client.SectorSize : 4096;
                long relativeOffsetInBytes = action.RelativeSectorOffset * 512;
                long relativeOffsetInDeviceSectors = relativeOffsetInBytes / deviceSectorSize;
                long finalAbsoluteSector = (long)superStartSector + relativeOffsetInDeviceSectors;
                long numSectors = (action.SizeInBytes + deviceSectorSize - 1) / deviceSectorSize;

                var task = new FlashPartitionInfo(
                    "0",
                    action.PartitionName,
                    finalAbsoluteSector.ToString(),
                    numSectors,
                    action.FilePath,
                    0
                );

                flashTasks.Add(task);
                _log($"   -> {action.PartitionName.PadRight(15)} | OffsetBytes: {relativeOffsetInBytes,10} | AbsSector: {finalAbsoluteSector}");
            }

            // 4. 执行刷写
            _log("[4/4] 开始批量写入...");
            
            try
            {
                await ExecuteFlashTasksAsync(flashTasks, protectLun5, new List<string>(), ct);
                _log("[完成] Super 分区直刷流程结束！");
            }
            finally
            {
                _log("[Cleanup] 正在清理临时文件...");
                
                foreach (var action in actions)
                {
                    try
                    {
                        if (action.FilePath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.Exists(action.FilePath))
                            {
                                File.Delete(action.FilePath);
                            }
                        }
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Cleanup] 删除临时文件失败: {ex.Message}"); }
                }
                
                if (actions.Count > 0)
                {
                    try
                    {
                        string tempDir = Path.GetDirectoryName(actions[0].FilePath);
                        if (tempDir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) 
                            && tempDir.Length > Path.GetTempPath().Length 
                            && Directory.Exists(tempDir))
                        {
                            for (int i = 0; i < 3; i++)
                            {
                                try 
                                {
                                    Directory.Delete(tempDir, true);
                                    break;
                                }
                                catch 
                                { 
                                    await Task.Delay(500); 
                                }
                            }
                        }
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Cleanup] 删除临时目录失败: {ex.Message}"); }
                }
            }
        }
    }
}
