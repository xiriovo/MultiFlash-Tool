<div align="center">
  <img src="assets/logo.jpg" alt="MultiFlash Tool Logo" width="200"/>
  
  # 贡献指南
  
  **感谢您考虑为 MultiFlash Tool 做出贡献！**
  
  我们欢迎所有形式的贡献，包括但不限于代码、文档、Bug 报告和功能建议。
  
</div>

---

## 📜 行为准则

参与本项目即表示您同意遵守以下行为准则：

### 我们的承诺
- ✅ 尊重所有贡献者，无论其经验水平
- ✅ 保持友好和建设性的讨论
- ✅ 接受建设性的批评
- ✅ 关注对社区最有利的事情

### 不可接受的行为
- ❌ 使用性化的语言或图像
- ❌ 人身攻击或侮辱性评论
- ❌ 骚扰行为（公开或私下）
- ❌ 未经许可发布他人的私人信息
- ❌ 其他不道德或不专业的行为

## 如何贡献

### 🐛 报告 Bug

发现 Bug？请帮助我们改进！

1. **搜索已有 Issues**
   - 检查是否已有相同的问题报告
   - 如果有，可以添加补充信息

2. **创建新 Issue**
   - 使用 Bug 报告模板
   - 提供以下信息：
     - 📝 清晰的问题描述
     - 🔄 详细的复现步骤
     - ✅ 预期行为
     - ❌ 实际行为
     - 💻 系统环境（Windows 版本、.NET 版本）
     - 📸 截图或日志（如适用）

**示例 Bug 报告：**
```markdown
**问题描述**
EDL 模式下刷写分区时程序崩溃

**复现步骤**
1. 连接设备到 EDL 模式
2. 选择 Programmer 文件
3. 点击"刷写分区"
4. 程序崩溃

**预期行为**
应该正常刷写分区

**实际行为**
程序崩溃并显示错误信息

**环境信息**
- Windows 11 22H2
- .NET Framework 4.8
- MultiFlash Tool v1.0.0
```

### 💡 提交功能请求

有好的想法？我们很乐意听取！

1. **创建 Feature Request**
   - 使用功能请求模板
   - 清晰描述功能需求

2. **说明使用场景**
   - 为什么需要这个功能？
   - 它将如何改善用户体验？
   - 有哪些替代方案？

3. **等待社区讨论**
   - 维护者和社区成员会参与讨论
   - 可能需要进一步澄清需求

### 💻 提交代码

准备贡献代码？太棒了！

#### 前置准备
1. ⭐ Star 本项目（可选但推荐）
2. 🍴 Fork 项目到您的账户
3. 📥 克隆到本地
   ```bash
   git clone https://github.com/your-username/MultiFlash-Tool.git
   cd MultiFlash-Tool
   ```

#### 开发流程
1. **创建功能分支**
   ```bash
   git checkout -b feature/your-feature-name
   # 或
   git checkout -b fix/bug-description
   ```

2. **编写代码**
   - 遵循代码规范（参见 `.editorconfig`）
   - 参考 [DEVELOPMENT.md](DEVELOPMENT.md) 了解项目结构
   - 添加必要的注释
   - 编写或更新测试

3. **提交更改**
   ```bash
   git add .
   git commit -m 'Add: 功能描述'
   ```
   提交信息格式见下文

4. **保持同步**
   ```bash
   git remote add upstream https://github.com/original/MultiFlash-Tool.git
   git fetch upstream
   git rebase upstream/main
   ```

5. **推送分支**
   ```bash
   git push origin feature/your-feature-name
   ```

6. **创建 Pull Request**
   - 访问 GitHub 上的 Fork 仓库
   - 点击 "New Pull Request"
   - 填写 PR 描述（使用模板）
   - 等待代码审查

## 📝 代码规范

### 提交信息格式

使用语义化的提交信息，格式如下：

```
类型: 简短描述（不超过 50 字符）

详细描述（可选，每行不超过 72 字符）

关联 Issue: #123
```

#### 提交类型
| 类型 | 说明 | 示例 |
|------|------|------|
| `Add:` | 新功能 | `Add: 支持 Super 分区合并` |
| `Fix:` | Bug 修复 | `Fix: 修复 EDL 模式下的崩溃问题` |
| `Refactor:` | 代码重构 | `Refactor: 优化设备检测逻辑` |
| `Docs:` | 文档更新 | `Docs: 更新 README 安装说明` |
| `Style:` | 代码格式 | `Style: 统一代码缩进格式` |
| `Test:` | 测试相关 | `Test: 添加 Fastboot 单元测试` |
| `Chore:` | 构建/工具 | `Chore: 更新依赖包版本` |
| `Perf:` | 性能优化 | `Perf: 优化大文件读取性能` |

#### 示例提交
```bash
# 好的提交
git commit -m "Add: 支持 Payload.bin 提取功能"
git commit -m "Fix: 修复设备断开连接时的空指针异常"
git commit -m "Docs: 添加 EDL 模式使用说明"

# 不好的提交
git commit -m "update"
git commit -m "fix bug"
git commit -m "修改了一些东西"
```

### 代码风格

#### 基本规范
- ✅ 遵循 `.editorconfig` 配置
- ✅ 使用 **4 空格缩进**（不使用 Tab）
- ✅ 使用 UTF-8 编码
- ✅ 文件末尾保留一个空行
- ✅ 行尾不留空格

#### C# 编码规范
```csharp
// 命名规范
public class DeviceManager { }           // 类名：PascalCase
public void ConnectDevice() { }          // 方法名：PascalCase
private string deviceName;               // 字段：camelCase
public string DeviceName { get; set; }   // 属性：PascalCase
const int MAX_RETRY = 3;                 // 常量：UPPER_CASE

// 代码组织
#region 设备检测
// 相关代码
#endregion

// 异常处理
SafeExecuteAsync(async () =>
{
    // 异步操作
}, "操作名称");

// 资源释放
using (var stream = File.OpenRead(path))
{
    // 使用 stream
}
```

#### 注释规范
```csharp
/// <summary>
/// 连接到指定的设备
/// </summary>
/// <param name="deviceId">设备 ID</param>
/// <returns>连接是否成功</returns>
public async Task<bool> ConnectDevice(string deviceId)
{
    // 验证设备 ID
    if (string.IsNullOrEmpty(deviceId))
        return false;
    
    // 尝试连接
    return await TryConnect(deviceId);
}
```

### ✅ Pull Request 检查清单

提交 PR 前，请确认以下事项：

#### 代码质量
- [ ] 代码可以成功编译
- [ ] 没有编译警告
- [ ] 遵循项目代码规范
- [ ] 添加了必要的注释
- [ ] 代码通过了静态分析

#### 功能测试
- [ ] 新功能已经过手动测试
- [ ] 没有破坏现有功能
- [ ] 边界情况已考虑
- [ ] 错误处理已完善

#### 文档更新
- [ ] 更新了相关文档
- [ ] 更新了 CHANGELOG.md
- [ ] 添加了使用示例（如适用）
- [ ] 更新了 README（如适用）

#### 其他
- [ ] PR 描述清晰完整
- [ ] 关联了相关 Issue
- [ ] 提交历史清晰（无多余提交）
- [ ] 通过了 CI 检查（如有）

## 📋 Pull Request 模板

创建 PR 时，请使用以下模板：

```markdown
## 变更类型
- [ ] Bug 修复
- [ ] 新功能
- [ ] 代码重构
- [ ] 文档更新
- [ ] 其他

## 变更说明
<!-- 简要描述此 PR 的变更内容 -->

## 关联 Issue
<!-- 关联的 Issue 编号，如 #123 -->
Closes #

## 测试情况
<!-- 描述如何测试此变更 -->
- [ ] 已在本地测试
- [ ] 已测试边界情况
- [ ] 已测试错误处理

## 截图（如适用）
<!-- 添加截图或 GIF 展示变更效果 -->

## 检查清单
- [ ] 代码遵循项目规范
- [ ] 已添加必要的注释
- [ ] 已更新相关文档
- [ ] 已通过所有测试
```

## 🎯 代码审查

### 审查流程
1. 维护者会在 1-3 个工作日内审查 PR
2. 可能会提出修改建议
3. 请及时回复审查意见
4. 修改完成后重新请求审查

### 审查标准
- 代码质量和可读性
- 是否符合项目架构
- 性能影响
- 安全性考虑
- 文档完整性

## 📄 许可证

**重要提示**：提交代码即表示您同意：

1. 您的贡献将以项目的 **非商业许可证** 发布
2. 您拥有提交代码的合法权利
3. 您的贡献不侵犯任何第三方权利
4. 您理解并同意项目的非商业使用限制

## 🙏 致谢

感谢所有为 MultiFlash Tool 做出贡献的开发者！

您的名字将出现在：
- 项目贡献者列表
- Release Notes
- 项目文档

---

<div align="center">
  
  **再次感谢您的贡献！** ❤️
  
  有问题？欢迎在 [Discussions](../../discussions) 中讨论
  
</div>
