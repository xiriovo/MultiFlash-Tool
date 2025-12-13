using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using OPFlashTool.Qualcomm;

namespace OPFlashTool.Strategies
{
    public class OppoVipDeviceStrategy : StandardDeviceStrategy
    {
        public override string Name => "Oppo/Realme VIP";

        // 缓存每个 LUN 的第一个分区名称 (用于 gptmain 方案的分段伪装)
        private Dictionary<int, string> _lunFirstPartitions = new Dictionary<int, string>();

        // [已移除] 不再包含硬编码的 Token 和 PK
        // private const string OPPO_PK = "...";
        // private const string OPPO_TOKEN = "...";

        public override Task<bool> AuthenticateAsync(FirehoseClient client, string programmerPath, Action<string> log, Func<string, string> inputCallback = null, string digestPath = null, string signaturePath = null)
        {
            log("[Oppo] 准备执行 VIP 签名验证...");
            string finalDigest = digestPath;
            string finalSig = signaturePath;

            // 1. 自动查找逻辑 (兼容 .bin 和 .mbn)
            if (string.IsNullOrEmpty(finalDigest))
            {
                string dir = Path.GetDirectoryName(programmerPath);
                string t = Path.Combine(dir, "digest.bin");
                if (!File.Exists(t)) t = Path.Combine(dir, "digest.mbn");
                if (File.Exists(t)) finalDigest = t;
            }

            if (string.IsNullOrEmpty(finalSig))
            {
                string dir = Path.GetDirectoryName(programmerPath);
                string t = Path.Combine(dir, "signature.bin");
                if (!File.Exists(t)) t = Path.Combine(dir, "signature.mbn");
                if (File.Exists(t)) finalSig = t;
            }

            // 2. 检查文件
            if (string.IsNullOrEmpty(finalDigest) || !File.Exists(finalDigest) ||
                string.IsNullOrEmpty(finalSig) || !File.Exists(finalSig))
            {
                log($"[Oppo] 警告: 未找到 VIP 验证文件 (Digest/Signature)！\n请手动选择文件或将其放入引导目录。");
                // 如果没有文件，通常无法通过验证。
                // 但为了不卡死流程，我们允许返回 true 尝试后续步骤（设备可能会报错）。
                return Task.FromResult(true);
            }

            // 3. 执行验证
            return Task.FromResult(client.PerformVipAuth(finalDigest, finalSig));
        }

        // [核心逻辑] OPPO 专用的伪装读取策略 (参考项目逻辑)
        // 参考项目 VIP 模式: label=BackupGPT, filename=gpt_main{lun}.bin, 固定 6 扇区, 2 次重试
        public override async Task<List<PartitionInfo>> ReadGptAsync(FirehoseClient client, CancellationToken ct, Action<string> log)
        {
            var allPartitions = new List<PartitionInfo>();
            _lunFirstPartitions.Clear(); // 清空缓存
            
            // 参考项目: 固定扫描 LUN 0-5, 固定读取 6 扇区
            const int maxLun = 5;
            const int sectorsToRead = 6;
            int lunRead = 0;

            log("[Info] 开始读取分区表...");

            for (int lun = 0; lun <= maxLun; lun++)
            {
                if (ct.IsCancellationRequested) break;

                byte[]? data = null;
                bool success = false;

                // 参考项目: 2 次重试机制
                for (int retry = 0; retry < 2; retry++)
                {
                    try 
                    {
                        // 参考项目 VIP 模式的成功策略:
                        // label=BackupGPT, filename=gpt_main{lun}.bin (从保存路径获取)
                        data = await client.ReadGptPacketAsync(
                            lun.ToString(), 
                            0, 
                            sectorsToRead, 
                            "BackupGPT", 
                            $"gpt_main{lun}.bin", 
                            ct
                        );

                        if (data != null && data.Length > 0)
                        {
                            success = true;
                            break; // 成功则跳出重试
                        }
                    }
                    catch (Exception ex)
                    {
                        log($"[Debug] LUN{lun} 读取异常: {ex.Message}");
                    }
                    
                    // 失败等待后重试 (参考项目: Thread.Sleep(300))
                    if (!success && retry == 0) 
                    {
                        await Task.Delay(300);
                    }
                }

                if (success && data != null)
                {
                    // 解析 GPT 确认数据有效性
                    try
                    {
                        var parts = GptParser.ParseGptBytes(data, lun);
                        
                        if (parts != null && parts.Count > 0)
                        {
                            allPartitions.AddRange(parts);
                            lunRead++;
                            log($"[Success] LUN{lun}: 读取到 {parts.Count} 个分区");

                            // 缓存该 LUN 的第一个分区名称 (StartLba 最小的那个)
                            var firstPart = System.Linq.Enumerable.OrderBy(parts, p => p.StartLba).FirstOrDefault();
                            if (firstPart != null)
                            {
                                _lunFirstPartitions[lun] = firstPart.Name;
                            }
                        }
                        else
                        {
                            log($"[Info] LUN{lun}: 分区表为空或无效");
                        }
                    }
                    catch
                    {
                        log($"[Warn] LUN{lun}: 数据已获取但解析失败");
                    }
                }
                else
                {
                    // 参考项目: 只有当 LUN0 失败时才报严重错误，其他 LUN 可能是空的
                    if (lun == 0) log($"[Error] 无法读取 LUN0 分区表 (关键)");
                    else log($"[Info] LUN{lun} 无响应或不存在");
                }
            }

            log($"[GPT] 共读取到 {lunRead} 个 LUN，解析出 {allPartitions.Count} 个分区");
            
            if (allPartitions.Count == 0)
            {
                log("[警告] VIP 模式无法读取分区表 (Firehose 拒绝所有读取请求)");
                log("[提示] 您仍可使用 XML 刷写模式 (rawprogram*.xml) 进行刷机操作");
                // 不再抛出异常，返回空列表允许用户使用 XML 模式
            }
            
            return allPartitions;
        }

        private long ParseTotalSectors(string info)
        {
            if (string.IsNullOrEmpty(info)) return 0;

            // 1. 匹配 JSON 格式: "total_blocks":124186624
            var matchJson = System.Text.RegularExpressions.Regex.Match(info, "\"total_blocks\"\\s*:\\s*(\\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (matchJson.Success && long.TryParse(matchJson.Groups[1].Value, out long valJson))
            {
                return valJson;
            }

            // 2. 匹配文本格式: Device Total Logical Blocks: 0x766f000
            var matchHex = System.Text.RegularExpressions.Regex.Match(info, @"Total\s+Logical\s+Blocks\s*:\s*0x([0-9a-fA-F]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (matchHex.Success)
            {
                try 
                {
                    return Convert.ToInt64(matchHex.Groups[1].Value, 16);
                }
                catch {}
            }

            // 3. 匹配旧格式: num_partition_sectors: 123456
            var matchOld = System.Text.RegularExpressions.Regex.Match(info, @"num_partition_sectors\s*[:=]\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (matchOld.Success && long.TryParse(matchOld.Groups[1].Value, out long valOld))
            {
                return valOld;
            }

            return 0;
        }

        // [新增] 伪装读取分区 (Waterfall Strategy + GptMain Segmentation)
        public override async Task<bool> ReadPartitionAsync(FirehoseClient client, PartitionInfo part, string savePath, Action<long, long> progress, CancellationToken ct, Action<string> log)
        {
            // 瀑布流尝试列表: (filename, label, useRelativeSector, isSegmented)
            var strategies = new List<(string filename, string label, bool useRelativeSector, bool isSegmented)>();

            // 1. 优先尝试 gpt_main0.bin (Segmented) - 用户要求的 "gptmain方案"
            // 只有当读取范围涉及 Gap 扇区时，才会真正执行分段；否则会自动降级为普通 gpt_main0.bin 读取
            bool isUfs = client.StorageType.Contains("ufs");
            
            // [修改] 对 super 和 userdata 分区强制启用分段读取策略
            // 即使它们不一定跨越 Gap (通常在 Gap 之后)，但为了绕过限制，我们可能需要特殊处理
            // 这里我们简单地将它们视为需要 Segmented 处理的候选者
            string pName = part.Name.ToLower();
            bool isSpecialPartition = (pName == "super" || pName == "userdata");

            if (isUfs)
            {
                strategies.Add(("gpt_main0.bin", "gpt_main0.bin", false, true)); // UFS Scheme (Split 6)
            }
            else
            {
                strategies.Add(("gpt_main0.bin", "gpt_main0.bin", false, true)); // eMMC Scheme (Split 34)
            }

            // 2. 其次尝试 BackupGPT
            strategies.Add(("gpt_backup0.bin", "BackupGPT", false, false));

            // 3. 添加通用后备策略
            strategies.Add(("ssd", "ssd", false, false));                         // 策略: 尝试 ssd (Raw)
            // [修改] 移除 super 和 userdata 的后备策略，因为它们现在主要依赖 gpt_main0.bin 方案
            // strategies.Add(("super", "super", true, false));
            // strategies.Add(("userdata", "userdata", true, false));
            strategies.Add((part.Name, part.Name, true, false));                  // 策略: 真实名称 (Relative)

            // [已移除] super/userdata 不再优先尝试真实名称，而是遵循 gptmain -> BackupGPT 的顺序
            /*
            string nameLower = part.Name.ToLower();
            if (nameLower == "super" || nameLower == "userdata")
            {
                strategies.Insert(0, (part.Name, part.Name, true, false));
                strategies.Insert(1, (part.Name, part.Name, false, false));
            }
            */

            foreach (var (spoofName, spoofLabel, useRelative, isSegmented) in strategies)
            {
                if (ct.IsCancellationRequested) return false;

                try
                {
                    bool success = false;

                    if (isSegmented)
                    {
                        // 区分 eMMC 和 UFS 方案
                        // 根据 StorageType 决定 Scheme
                        bool isUfsScheme = isUfs;
                        
                        string schemeName = isUfsScheme ? "UFS (0-5,6,7+)" : "eMMC (0-33,34,35+)";
                        
                        // [Check] 检查读取范围是否涉及关键扇区 (Gap)
                        // 如果不涉及，则不需要分段，直接作为普通 gpt_main0.bin 读取
                        long gapSector = isUfsScheme ? 6 : 34;
                        long start = (long)part.StartLba;
                        long end = start + (long)part.Sectors - 1;
                        
                        // [修改] 对于 super 和 userdata，即使不跨越 Gap，也强制使用分段读取逻辑 (如果需要特殊处理)
                        // 但目前的 ReadSegmentedAsync 主要是为了跳过 Gap。
                        // 如果 super/userdata 在 Gap 之后 (通常如此)，它们会被降级为普通 gpt_main0.bin 读取。
                        // 这符合 "gptmain方案" 的要求：即用 gpt_main0.bin 伪装来读取任意分区。
                        // 只要 start > gapSector，就会走下面的降级逻辑，使用 gpt_main0.bin 读取。
                        
                        if (start > gapSector || end < gapSector)
                        {
                            // 范围不涉及 Gap，降级为普通读取
                            success = await client.ReadPartitionChunkedAsync(
                                savePath, 
                                part.StartLba.ToString(), 
                                (long)part.Sectors, 
                                part.Lun.ToString(), 
                                progress, 
                                ct, 
                                spoofLabel, 
                                spoofName,
                                append: false,
                                suppressError: true 
                            );
                        }
                        else
                        {
                            // 范围涉及 Gap，执行分段读取
                            // 获取当前 LUN 的首个分区名称 (用于 Gap 填充)
                            string firstPartName = _lunFirstPartitions.ContainsKey(part.Lun) ? _lunFirstPartitions[part.Lun] : part.Name;
                            
                            // 定义分段点
                            long[] splitPoints = new long[] { gapSector };
                            
                            success = await ReadSegmentedAsync(client, part, savePath, splitPoints, firstPartName, progress, ct, log);
                        }
                    }
                    else
                    {
                        string startSectorStr = useRelative ? "0" : part.StartLba.ToString();
                        string modeStr = useRelative ? "Relative" : "Absolute";
                        success = await client.ReadPartitionChunkedAsync(
                            savePath, 
                            startSectorStr, 
                            (long)part.Sectors, 
                            part.Lun.ToString(), 
                            progress, 
                            ct, 
                            spoofLabel, 
                            spoofName,
                            append: false,
                            suppressError: true 
                        );
                    }

                    if (success) return true;
                }
                catch (Exception ex)
                {
                    log($"[Error] {part.Name} 读取失败 ({spoofName}): {ex.Message}");
                }
                
                await Task.Delay(100, ct);
            }

            log($"[Error] 读取失败: {part.Name}");
            return false;
        }

        // [辅助] 分段读取逻辑 (GptMain Scheme)
        private async Task<bool> ReadSegmentedAsync(FirehoseClient client, PartitionInfo part, string savePath, long[] splitPoints, string firstPartName, Action<long, long> progress, CancellationToken ct, Action<string> log)
        {
            long currentSector = (long)part.StartLba;
            long remainingSectors = (long)part.Sectors;
            long totalBytes = remainingSectors * client.SectorSize;
            long currentBytesRead = 0;

            // 如果是追加模式，需要先清空文件
            if (File.Exists(savePath)) File.Delete(savePath);

            while (remainingSectors > 0)
            {
                if (ct.IsCancellationRequested) return false;

                // 确定当前分段的结束点
                long nextBoundary = -1;
                long splitPoint = splitPoints[0]; // 假设只有一个分段点 (34 或 6)
                
                // Segment 1: 0 - (splitPoint-1)
                // Segment 2: splitPoint
                // Segment 3: (splitPoint+1) - End

                string currentFilename = "gpt_main0.bin";
                string currentLabel = "gpt_main0.bin";

                long sectorsToReadThisChunk = remainingSectors;

                if (currentSector < splitPoint)
                {
                    // 在 Segment 1
                    long dist = splitPoint - currentSector;
                    sectorsToReadThisChunk = Math.Min(remainingSectors, dist);
                    // Filename/Label 保持 gpt_main0.bin
                }
                else if (currentSector == splitPoint)
                {
                    // 在 Segment 2 (Gap)
                    sectorsToReadThisChunk = 1;
                    // 使用首个分区名称
                    currentFilename = firstPartName;
                    currentLabel = firstPartName;
                }
                else
                {
                    // 在 Segment 3
                    // Filename/Label 保持 gpt_main0.bin
                }

                // 执行读取
                bool success = await client.ReadPartitionChunkedAsync(
                    savePath,
                    currentSector.ToString(),
                    sectorsToReadThisChunk,
                    part.Lun.ToString(),
                    (c, t) => progress?.Invoke(currentBytesRead + c, totalBytes),
                    ct,
                    currentLabel,
                    currentFilename,
                    append: true, // 必须追加
                    suppressError: true
                );

                if (!success) return false;

                currentSector += sectorsToReadThisChunk;
                remainingSectors -= sectorsToReadThisChunk;
                currentBytesRead += sectorsToReadThisChunk * client.SectorSize;
            }

            return true;
        }

        // [新增] 伪装写入分区 (Waterfall Strategy)
        public override async Task<bool> WritePartitionAsync(FirehoseClient client, PartitionInfo part, string imagePath, Action<long, long> progress, CancellationToken ct, Action<string> log)
        {
            // 写入通常使用绝对偏移，除非是文件模式写入 (较少见)
            // 这里主要关注伪装文件名
            var strategies = new List<(string filename, string label)>
            {
                ("gpt_backup0.bin", "BackupGPT"),       // 策略1
                ("gpt_backup0.bin", "gpt_backup0.bin"), // 策略2
                ("gpt_main0.bin", "gpt_main0.bin"),     // 策略3
                ("ssd", "ssd"),                         // 策略4
                (part.Name, part.Name)                  // 策略5
            };

            // [修改] 移除 super 和 userdata 的特殊插入逻辑，以及从默认列表中移除它们
            // string nameLower = part.Name.ToLower();
            // if (nameLower == "super" || nameLower == "userdata") ...

            foreach (var (spoofName, spoofLabel) in strategies)
            {
                if (ct.IsCancellationRequested) return false;

                try
                {
                    bool success = await client.FlashPartitionAsync(
                        imagePath, 
                        part.StartLba.ToString(), 
                        (long)part.Sectors, 
                        part.Lun.ToString(), 
                        progress, 
                        ct, 
                        spoofLabel, 
                        spoofName
                    );

                    if (success) return true;
                }
                catch (Exception ex)
                {
                    log($"[Error] {part.Name} 写入失败 ({spoofName}): {ex.Message}");
                }

                await Task.Delay(100, ct);
            }

            log($"[Error] 写入失败: {part.Name}");
            return false;
        }
    }
}
