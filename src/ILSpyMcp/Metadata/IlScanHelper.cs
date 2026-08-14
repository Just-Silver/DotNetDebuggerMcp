using System.Reflection.Metadata;
using ICSharpCode.Decompiler.Disassembler;

namespace ILSpyMcp.Metadata;

internal static class IlScanHelper
{
    public readonly record struct IlInstruction(ILOpCode Opcode, int RawToken);

    public static void DecodeMethodBody(BlobReader il, Action<IlInstruction> onInstruction, Action? onAbort = null)
    {
        try
        {
            while (il.RemainingBytes > 0)
            {
                var code = ILParser.DecodeOpCode(ref il);
                switch (code)
                {
                    case ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Jmp
                         or ILOpCode.Ldftn or ILOpCode.Ldvirtftn or ILOpCode.Calli:   // 方法/签名 token
                        onInstruction(new IlInstruction(code, il.ReadInt32()));
                        break;
                    case ILOpCode.Ldstr:                                              // UserString token
                        onInstruction(new IlInstruction(code, il.ReadInt32()));
                        break;
                    case ILOpCode.Ldfld or ILOpCode.Ldsfld or ILOpCode.Stfld or ILOpCode.Stsfld
                         or ILOpCode.Ldflda or ILOpCode.Ldsflda                       // 字段 token
                         or ILOpCode.Castclass or ILOpCode.Isinst or ILOpCode.Box or ILOpCode.Newarr
                         or ILOpCode.Ldtoken or ILOpCode.Constrained or ILOpCode.Sizeof: // 类型/字段等 token 类，消费方按需处理
                        onInstruction(new IlInstruction(code, il.ReadInt32()));
                        break;
                    default:
                        ILParser.SkipOperand(ref il, code);                            // 权威跳过，覆盖全部 opcode
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or OverflowException or IndexOutOfRangeException)
        {
            onAbort?.Invoke();   // 解码中止，保留已收集部分
        }
    }
}
