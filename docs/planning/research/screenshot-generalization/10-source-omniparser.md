# 10 · OmniParser 源码深挖（SoM 去重/合并/编号/标签摆放纯算法）

> **来源等级：A 源码核实**（本地 clone `Externals/OmniParser` @ `3540212`；核心在 `util/utils.py` 与 `util/box_annotator.py`）。
> **重要**：本克隆**只有「图标检测+OCR+字幕模型」视觉线**，`util/`、`omnitool/` 内**无任何 DOM/无障碍树解析代码**。以下仅借**纯算法**（元素来源将用 UIA + Windows OCR，不引 ML）。

## ① 可直接借鉴的算法（先给答案）
1. **去重＝"重叠则保留更小的框"**，用**非标准 IoU**：度量 `max(交并比, 交/面积A, 交/面积B)`（`utils.py:209`、`box_annotator.py:184`）。判定：box1 与任一**更小** box2 的该度量 `> iou_threshold` 则丢 box1。阈值实际 **0.7**（`omniparser.py:30`；函数默认 0.9 仅占位）。因含"包含比"，小框被大框完全包住时度量=1 → **"大框包小框"必判重、保留小框**。
2. **OCR 文本并入图标框**：`is_inside`（交/被检框面积 `>0.80`，`utils.py:273`）双向——OCR 框 80% 在图标框内 → 文本累加进图标框 `content` 并移除该 OCR 框；图标框 80% 在 OCR 框内 → 丢弃图标框保留 OCR 文本框（`utils.py:294-316`）。用 **0.80**（新函数；旧 `remove_overlap` 用 0.95，已不在主链路）。
3. **编号＝0 基连续整数**，顺序＝"有文本内容优先、纯图标在后"（稳定排序，`utils.py:449`）；`ID i ⇔ 清单[i] ⇔ label_coordinates[i]` 严格一致。
4. **标签摆放＝4 锚点依序试、最小重叠**：**上左→左外→右外→上右**；候选标签底框与**任意**已画框重叠 `>0.3` **或越界**则不合格；全不合格回退最后（上右）（`box_annotator.py:189-262`）。
5. **坐标空间**：内部全程归一化 [0,1]；对外元素 `bbox` 是**归一化 xyxy**，`label_coordinates` 是**归一化 xywh**；点击点=框中心 `[x+w/2,y+h/2]`。
6. **过滤极弱**：算法层只做 `int 像素面积>0` 剔除（`utils.py:444-445`）；噪声主要靠检测器 `conf` + NMS(iou=0.1)。**本项目必须自加更严过滤**。
7. **绘制**：`cv2.rectangle` 画框 + 实心底 + `putText`；字色按框色亮度自动黑/白（`:148-150`）；线宽/字号按 `max(图尺寸)/3200` 缩放（`omniparser.py:21-27`）。

## ② 源码级事实

### 流水线（`get_som_labeled_img`，`utils.py:417-496`）
```
输入: 原图 + OCR 框(像素 xyxy) + 检测框(像素 xyxy)
1. 检测框归一化 /[w,h,w,h]                            # :432
2. OCR 框归一化 /[w,h,w,h]                            # :438
3. 构造 text_elem{type:'text',...} / icon_elem{type:'icon',interactivity:True,content:None}  # :444-445
   （均先过 int_box_area>0）                          # :444-445
4. remove_overlap_new(icon_elems, iou_threshold, text_elems)   # 含 OCR 合并  :446
5. sorted(filtered, key=x['content'] is None)         # 有内容在前 :449
6. starting_idx=第一个 content is None 的下标          # :451
7. （可选）字幕模型回填 caption                       # :457-472
8. bbox xyxy→cxcywh（仅绘图用）                        # :478
9. phrases=[0..N-1] → annotate() 画框 + label_coordinates   # :480-486
10. output_coord_in_ratio 时 xywh 再 /[w,h]           # :492-493
返回 (base64 PNG, label_coordinates, filtered_boxes_elem)   # :496
```
注：返回的元素清单 `bbox` 仍是**归一化 xyxy**（步骤 8 只作用于新张量）；`parsed_content_merged` 算完未返回，是死代码。

### 去重与包含（`remove_overlap_new`，`utils.py:241-319`）
```
overlap(a,b)=max(inter/union, inter/area(a), inter/area(b))   # 非标准 :259-267
is_inside(b1,b2)=inter(b1,b2)/area(b1) > 0.80                 # b1 有 80% 在 b2 内 :269-273

filtered=ocr_bbox 先入列                                        # :277-278
for box1 in icon_boxes:
    if 存在更小 box2 使 overlap(box1,box2)>iou_threshold 且 area(box1)>area(box2): 跳过 box1   # :283-288
    else:
        for box3 in ocr_bbox:
            if is_inside(box3,box1): 吸收文本、filtered.remove(box3)   # OCR 在图标内 :297-305
            elif is_inside(box1,box3): box_added=True; break           # 图标在 OCR 内 :307-309
        if not box_added: 追加 icon（content=吸收文本 或 None）           # :312-316
```
易错：先做"保留更小框"的图标间去重，再做图标↔OCR 包含合并；`is_inside(第一参数 80% 落在第二参数)`；**两个等面积框高度重叠时都不删**（`>` 为 False）。

### 编号（`utils.py:449-451,480`）
`sorted(key=x['content'] is None)`（False<True 稳定）→ `[OCR 文本框]+[吸收过文本的图标]+[纯图标]`；`phrases=[0..N-1]`；`label_coordinates={str(i): xywh}`。**ID i ⇔ 元素清单[i] ⇔ label_coordinates[i]**。

### 标签摆放（`get_optimal_label_pos`，`box_annotator.py:189-262`）
```
重叠判定: 对每个 detection IoU_maxratio(标签底框,detection)>0.3 → 重叠；或标签底框越界 → 重叠   # :195-205
依次试: ①上左(:208-218) ②左外(:221-231) ③右外(:235-246) ④上右(:249-260)；全不合格回退④(:262)
```
（docstring 顺序与实际代码不符，以代码为准。）

### 检测侧 NMS（`utils.py:388-409`、`yolov9.py:116-136`）
`predict_yolo(..., iou_threshold=0.1)`（:431）硬编码；`batched_nms(...)[:max_det]`，**`max_det=300`**（唯一数量上限）；坐标 clamp 图像。

### OCR 集成（`utils.py:514-550`）
`check_ocr_box` 返回 `(text_list, coord_list)`；`coord` 四点多边形取左上/右下角为轴对齐框（`utils.py:504-507`）；对齐全部发生在 `remove_overlap_new`。

## ③ 本项目「UIA 元素 + OCR 词 → SoM」可移植规则（C#/System.Drawing）
- **R1 归一化空间**：UIA 矩形与 OCR 词框都转 [0,1] xyxy 做比较，绘图前 ×宽高；返回清单保留**像素 xyxy**对 agent 更直观。
- **R2 重叠度量**：`overlap(a,b)=max(inter/union, inter/area(a), inter/area(b))`。
- **R3 去重（保留更小框）**：对每个元素 e，若存在更小面积 f 使 `overlap(e,f)>0.7` 则丢 e。UIA 用 **0.6~0.7**；"面板包按钮"会自动保留按钮、丢容器。**例外修正**：容器类（Group/Pane/Window）不参与与子控件去重，避免丢区域语义。
- **R4 OCR↔元素合并（0.80）**：OCR 词框 ≥80% 落在元素内 → 词文本追加进元素描述并移除该词框；元素 ≥80% 落在词框内 → 丢元素保留文本；未被吸收的 OCR 词作独立 `type:text`（interactivity=false）。**先查"OCR 在元素内"再查"元素在 OCR 内"**。
- **R5 过滤（UIA 需更严）**：面积 ≥4px²；丢 `IsOffscreen=true` 且不在截图范围；丢宽/高 ≤2px；丢无 Name/AutomationId 且非可交互 ControlType 的纯容器（除非含可交互子控件）；`IsEnabled=false` 标 interactivity=false；**丢覆盖 >~85% 屏幕且无 Name 的顶层容器**（防一个巨框吞掉所有元素）。
- **R6 编号顺序**：改为阅读顺序（先 y 后 x 或 UIA 树序）；有文本优先、纯图标在后；组内稳定；**ID 从 0 连续，`ID i ⇔ 清单[i] ⇔ label_coordinates[i]` 严格一致**。
- **R7 标签摆放**：候选 **上左→左外→右外→上右**；标签底框与任意已画框 `overlap>0.3` 或越界则不合格；全不合格回退上右；**改进**：回退仍越界则 clamp 到图内；底色用高对比纯色（亮黄/白底黑字）而非跟随框色。
- **R8 绘制参数**：`r=max(W,H)/3200`；`thickness=max(3r,1)`、字号≈0.8r、padding max(3r,1)；实心底 + 文本；**字号下限（≥10px）**否则缩略图编号不可读。
- **R9 上限**：工具设元素上限（如 100~150），超出优先保留"有文本/可交互"并注明"已截断"；响应同时给元素清单 + `label_coordinates`（像素 xywh 与归一化双份）。

## ④ 必须丢弃的 ML 部分
YOLO/yolov9 检测器、torchvision `batched_nms`(iou=0.1)、`box_convert`（保留概念、手写 xyxy↔cxcywh）、Florence-2/BLIP2 字幕模型、EasyOCR/PaddleOCR（保留"返回 text[]+框[]"接口约定，换 Windows.Media.Ocr）、torch/numpy/cv2/supervision、死代码 `parsed_content_merged`、硬编码 `interactivity`（改用 UIA `IsEnabled`/控件类型）。

**保留（纯算法可 1:1 重写）**：`remove_overlap_new` 的 max-ratio 重叠 + 保留更小 + `is_inside` 0.80 合并；`get_optimal_label_pos` 4 锚点 + `overlap>0.3`/越界拒收；稳定排序编号；归一化坐标约定；亮度选字色；`/3200` 缩放系数。

## 附：关键行号速查
| 事项 | 位置 |
|---|---|
| 保留更小框 + max-ratio IoU | `util/utils.py:259-267, 283-288` |
| OCR↔图标包含合并(0.80) | `util/utils.py:269-273, 294-316` |
| 面积>0 过滤 | `util/utils.py:444-445` |
| 编号排序(content 先) | `util/utils.py:449-451` |
| 编号序列/坐标表 | `util/utils.py:480, 484-486`（`357, 363`） |
| 坐标归一化/ratio 输出 | `util/utils.py:432, 438, 492-493` |
| 标签 4 锚点 + 回退 | `util/box_annotator.py:189-262`（重叠 0.3 `:199`；越界 `:203`） |
| 字色亮度 | `util/box_annotator.py:148-150` |
| NMS iou=0.1 / max_det=300 | `util/utils.py:431`；`util/yolov9.py:116,131` |
| 缩放系数 /3200 | `util/omniparser.py:21-27` |
| 点击中心 | `eval/ss_pro_gpt4o_omniv2.py:201`；`omnitool/gradio/agent/vlm_agent.py:152-153` |
