using System.Text.Json.Serialization;
using OPFlashTool.Services;

namespace OPFlashTool
{
    // 云端功能已移除 - 保留空实现以保持编译兼容性
    [JsonSerializable(typeof(AppSettings))]
    [JsonSerializable(typeof(SuperDef))]
    public partial class AppJsonContext : JsonSerializerContext
    {
    }
}
