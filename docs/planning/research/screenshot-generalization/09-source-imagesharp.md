# 09 · ImageSharp 源码深挖（WebP API / 许可 / 体积 / 像素导入）

> **来源等级：A 源码核实**（本地 clone `Externals/ImageSharp` @ `f80843e60`（main = v5.0 开发线），另核对 tag `v3.1.12`/`v4.1.2`；+ NuGet 实拉包）。

## ① 结论与推荐（先给答案）
- **选型：用 ImageSharp，不要 SkiaSharp。版本锁 `3.1.12`（v3 线），不要 v4.x。**
- 理由：本项目是 `PackAsTool` + NETSDK1146 MSBuild hack 的工具包。SkiaSharp win-x64 原生 dll **13.4MB** 会引入 RID 原生资产打包/还原复杂度，与现有 hack 正面冲突；ImageSharp v4 会在**每次 Release 构建**（含 CI/每贡献者）强制 `sixlabors.lic` 密钥。**v3.1.12 同时满足「纯托管、零原生资产、WebP 能力最全、OSS 免费且无需构建密钥」。**

| 维度 | ImageSharp 3.1.12 | ImageSharp 4.1.2 | SkiaSharp 4.152.1 |
|---|---|---|---|
| 原生依赖 | 无（纯托管） | 无 | **`libSkiaSharp.dll` win-x64 13.4MB** |
| PackAsTool/NETSDK1146 | 无影响 | 无影响 | 需 RID 原生资产，与 hack 冲突 |
| WebP 控制 | 全（Lossy/Lossless+Quality+Method0-6+nearLossless+sns/filter/entropy） | 同 | **仅 Compression+float Quality** |
| 构建密钥 | **不需要** | **Release 强制 `sixlabors.lic`** | 不需要 |
| nupkg / dll | ~1.01MB / 2.10MB | ~1.30MB / 2.60MB | 托管~0.6MB + 原生13.4MB |
| 许可（OSS 消费） | Split License 命中 Apache-2.0 | 同 + 构建强制密钥 | MIT |

## ② WebP 编码 API（`src/SixLabors.ImageSharp/Formats/Webp/WebpEncoder.cs`）
| 属性 | 行 | 语义/默认 |
|---|---|---|
| `FileFormat` | :23 | `WebpFileFormatType?`，**只有 Lossy/Lossless**，默认 null（回落元数据，元数据默认 Lossy） |
| `Quality` | :32 | 0–100，默认 **75** |
| `Method` | :38 | `WebpEncodingMethod`，`Default=Level4=4` |
| `UseAlphaCompression` | :44 | 默认 true（alpha 用 lossless） |
| `EntropyPasses` | :50 | 1–10，默认 1 |
| `SpatialNoiseShaping` | :58 | 0–100，默认 50 |
| `FilterStrength` | :67 | 0–100，默认 60 |
| `NearLossless` | :73 | **独立 bool**（不是 FileFormat 枚举值） |
| `NearLosslessQuality` | :79 | 0–100，默认 100 |

- **纠正**：`FileFormat` 枚举**没有** NearLossless（`WebpFileFormatType.cs:9-20`）；near-lossless 是 `NearLossless`+`NearLosslessQuality` 两个属性。
- `WebpEncodingMethod`（`WebpEncodingMethod.cs:9-60`）：`Level0/Fastest=0`…`Default=Level4=4`…`Level6/BestQuality=6`。
- lossy 默认（不显式赋值）：`FileFormat=null→Lossy`、Quality=75、Method=4、EntropyPasses=1、SNS=50、Filter=60、UseAlphaCompression=true、TransparentColorMode=Clear（:14-17）。
- 校验：quality **只夹上界**（`Vp8Encoder.cs:130`），entropy/sns/filter 夹范围（:133-135）→ **调用方务必自行 `Clamp(0,100)`**。
- 格式选择：`WebpEncoderCore.Encode`（`WebpEncoderCore.cs:129-149`）先判 `fileFormat!=null` 否则读 `WebpMetadata.FileFormat`（默认 Lossy）；超 `MaxDimension=16383` 抛（:135，常量 `WebpConstants.cs:141`）。
- 默认注册：`WebpConfigurationModule.Configure`（`Webp/WebpConfigurationModule.cs:13-18`），属 `Configuration.Default`（`Configuration.cs:225,239`）；保存入口 `ImageExtensions.SaveAsWebp`（`Formats/_Generated/ImageExtensions.Save.cs:1154`）。

### 与 SkiaSharp 差异（源码级）
`SkiaSharp/binding/SkiaSharp/SKWebpEncoder.cs:28` + `Generated/SKWebpEncoderOptions.generated.cs:15-20`：结构**只有** `fCompression(Lossy=0/Lossless=1)+fQuality(float)`，**无** Method/EntropyPasses/SNS/FilterStrength/NearLossless。ImageSharp 是完整移植 libwebp VP8/VP8L 编码器。

## ③ 从像素导入（喂 `Bitmap.LockBits`）
- **`Bgra32` 与 GDI+ `Format32bppArgb` 二进制兼容**（`PixelFormats/PixelImplementations/Bgra32.cs:14,25-40`，布局 `B,G,R,A`）→ `Scan0` 可直接喂，无需换序。
- **拷贝（推荐）**：`Image.LoadPixelData<Bgra32>(ReadOnlySpan<byte>, int width, int height, int rowStrideInBytes)`（`Image.LoadPixelData.cs:79`）。`rowStrideInBytes` 须能被 `sizeof(TPixel)` 整除（4 的倍数恒满足，`:128`）；内部 `PixelBuffer.CopyFrom`（`:180`）**会拷贝**，源 Bitmap 可立即 UnlockBits；长度校验 `data.Length >= (h-1)*rowStride + width`（:176-177）。
- **零拷贝（unsafe）**：`Image.WrapMemory`（`Image.WrapMemory.cs:550/602`）——**不转移非托管内存所有权**，须保持 Bitmap 锁定直到 Image 释放（:522-537）；stride 同样须整除。

## ④ 许可与合规（关键结论）
- 仓库根 `LICENSE:1-3`＝**Six Labors Split License v1.0（2022-06）**；`:27-28` 双许可 Apache-2.0 或商业。
- `LICENSE:33-40` 命中 Apache-2.0 的四种情形：①消费方本身 OSS/Source-Available；②作为传递依赖；③直接依赖且年营收<100 万美元；④非营利。**本项目 MIT OSS → 命中①，免费。**
- 版本分界：`v2.1.13` 纯 Apache；**`v3.0.0` 起** Split License；**v3.1.12 与 v4.1.2 LICENSE 文本逐字相同**（条款没变）。
- **v4 的真正差异＝构建期强制密钥**：v4.1.2 包含 `build/SixLabors.ImageSharp.targets` + `build/net8.0/SixLabors.Licensing.dll`，`SixLabors_ValidateLicense` target `BeforeTargets=CoreCompile`，`ContinueOnError=$(Configuration.StartsWith('Debug'))` → **Debug 只报错不中断，Release 直接失败**。**v3.1.12 包只有空 `props`，无 targets/无 Licensing.dll → 不需要任何密钥。**
- 官方：密钥从 v4.0/ImageSharp.Drawing v3.0/Web v4.0/Fonts v3.0/PolygonClipper v1.0 起强制；OSS 可免费申请社区密钥但**不得入库**、按年轮换。
- **合规结论**：v3.1.12 → 低风险（无密钥/无 CI secret）；v4.1.2 → 条款合规但工程摩擦（申请/轮换/入库禁忌）；SkiaSharp → 许可不冲突但换原生资产换打包风险。

## ⑤ TFM / 依赖 / 体积
| | v3.1.12 | v4.1.2 |
|---|---|---|
| TargetFrameworks | `net7.0;net6.0`→发布仅 `lib/net6.0` | `net8.0;net10.0`→`lib/net8.0` |
| 依赖 | **无** | `System.IO.Hashing 8.0.0` |
| nupkg | 1,059,559 B ≈1.01MB | 1,362,792 B ≈1.30MB |
| 主 dll | 2.10MB | 2.60MB |
| 原生 | **0 P/Invoke**（全仓 `DllImport/LibraryImport/NativeLibrary` 计数 0） | 0 |

本项目 Engine TFM `net10.0-windows10.0.22621.0` 消费 `lib/net6.0` 兼容。

## ⑥ 其他能力
- **灰度**：`Mutate(x=>x.Grayscale())`（`GrayscaleExtensions.cs:20/29/38/48`，`GrayscaleMode` BT601/BT709）或 `CloneAs<L8>()`（`Image.cs:148/157`）——核心包内。
- **缩放**：`Resize(...)`（`ResizeExtensions.cs:21/43/67/92/173`），高质量重采样器核心包内。
- **画框/画字（SoM）**：**核心包只有 `DrawImage`**；形状/文字在独立包 `SixLabors.ImageSharp.Drawing`（3.1.2 依赖 **ImageSharp 4.1.2** + Fonts + PolygonClipper，**自带 v4 密钥链**）。→ **建议 SoM 继续用 System.Drawing**（`ImagePipeline` 已用 GDI+），不引 ImageSharp.Drawing。

## ⑦ 落地建议与代价
- `DotNetDebugger.Engine.csproj` 加 `<PackageReference Include="SixLabors.ImageSharp" Version="3.1.12" />`；`ImagePipeline.Encode` 加 `case "webp"`：`LockBits(Format32bppArgb)` → `Image.LoadPixelData<Bgra32>(data,w,h,bd.Stride)` → `new WebpEncoder{FileFormat=Lossy, Quality=Clamp(quality,0,100), Method=Default}` → `SaveAsWebp(ms, enc)`。
- 纯托管，PackAsTool 无新增原生资产，NETSDK1146 hack 不受影响。代价：新增 1 包（~1MB 包 / 2.1MB dll），`lib/net6.0` 资产在 net10 运行正常。
- 回归：`ImagePipeline` 是 internal，测试在 `Engine.Tests`；加 webp 冒烟断言（文件头 `RIFF....WEBP`）。
- 文档纪律：新增截图格式属工具行为变更 → 同步根 `README.md` + 握手 `AppText.HandshakeFeatureIntro` + `DotNetDebuggerMcpCmdTests.HandshakeFeatureIntro_覆盖全部能力族触发条件`；CHANGELOG 记录新依赖/许可。

**版本锁定**：`SixLabors.ImageSharp` = **3.1.12**（不要 ≤3.1.11；不要 4.x）；**不要引 `SixLabors.ImageSharp.Drawing`**。
