namespace DotNetDebugger.Engine.Models;

/// <summary>
/// 执行点位置三元组：全局唯一标识一个执行点（spec §4.2 契约）。v1 断点定位与事件都用它，
/// 天然对接现有元数据层（methodToken 为 0x06 开头的 mdMethodDef）。
/// </summary>
public sealed record FrameLocation(string ModuleName, int MethodToken, int IlOffset)
{
    /// <summary>按元数据 token 格式渲染方法 token（0x06000005）。</summary>
    public string MethodTokenText => $"0x{MethodToken:x8}";

    public override string ToString() => $"{ModuleName}!{MethodTokenText}+0x{IlOffset:x}";
}
