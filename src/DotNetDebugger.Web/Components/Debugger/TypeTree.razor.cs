using BootstrapBlazor.Components;
using DotNetDebugger.Web.Services;
using Microsoft.AspNetCore.Components;

namespace DotNetDebugger.Web.Components.Debugger;

/// <summary>左侧类型浏览树（对标 dnSpyEx）：程序集→命名空间→类型→成员，数据驱动全量 + 虚拟滚动；
/// 外部（Debugger 页 / agent 上下文驱动）经 LoadAssembly / SelectTypeAsync 展开路径并选中。</summary>
public partial class TypeTree
{
    private TreeView<TypeTreeNode>? _tree;
    private readonly TypeTreeData _data = new();
    private List<TreeViewItem<TypeTreeNode>> _items = [];
    private bool _ready;
    private readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // 待选中目标：LoadAssembly(SetItems) 后 Items 变化触发的渲染完成前 SetActiveItem 会被覆盖，
    // 故缓存目标，待 OnAfterRenderAsync（渲染完成）后执行。
    private TreeViewItem<TypeTreeNode>? _pendingActive;
    private TreeViewItem<TypeTreeNode>? _pendingScrollOnce;
    private bool _itemsChangedSinceRender = true;

    /// <summary>选中自动滚动对齐：Start = 选中项滚到树组件顶部（默认 nearest 会出现在底部边缘，体验差）。</summary>
    private readonly ScrollIntoViewOptions _scrollOptions = new()
    {
        Behavior = ScrollIntoViewBehavior.Smooth,
        Block = ScrollIntoViewBlock.Start,
        Inline = ScrollIntoViewInline.Nearest,
    };

    /// <summary>组件渲染就绪（_tree ref 赋值 + BB 首次渲染完成）。此前的 SetActiveItem 可能被 BB 初始化覆盖/忽略，须等就绪。</summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _ready = true;
            _readyTcs.TrySetResult();
            MemoryLog.Write("TypeTree", $"TypeTree firstRender，触发 OnReady（_tree={( _tree is null ? "null" : "ok")}）");
            if (OnReady is not null)
            {
                try { await OnReady(); }
                catch (Exception ex) { MemoryLog.Write("TypeTree", $"OnReady 回调异常: {ex.Message}"); }
            }
        }
        else if (_pendingActive is not null)
        {
            // 渲染完成（Items 已按当前展开状态渲染）：执行待选中的 SetActiveItem
            var target = _pendingActive;
            _pendingActive = null;
            _itemsChangedSinceRender = false;
            MemoryLog.Write("TypeTree", $"渲染后补 SetActiveItem: {target.Value.Label}");
            _tree?.SetActiveItem(target);
            // 首次补选中的滚动可能因树刚加载布局未稳而不生效：下一渲染（布局稳定）后再 SetActiveItem 一次触发滚动
            _pendingScrollOnce = target;
        }
        else if (_pendingScrollOnce is not null)
        {
            // 布局稳定后重设 active：触发 BB scrollIntoView 滚到目标顶部
            var target = _pendingScrollOnce;
            _pendingScrollOnce = null;
            MemoryLog.Write("TypeTree", $"稳定后重设 active 触发滚动: {target.Value.Label}");
            _tree?.SetActiveItem(target);
        }
        else
        {
            // 无 pending 但也标记一次渲染完成（LoadAssembly SetItems 后无选中请求时也要复位，下次可直接选中）
            _itemsChangedSinceRender = false;
        }
        await base.OnAfterRenderAsync(firstRender);
    }

    /// <summary>节点 model：Kind=assembly/namespace/type/member；Label 显示文本；AssemblyPath 供子级枚举；TypeFullName 供反编译；MethodToken 供方法停点匹配。</summary>
    public sealed record TypeTreeNode(string Label, string Kind, string? AssemblyPath = null,
        string? TypeFullName = null, int MethodToken = 0, TypeMemberKind? MemberKind = null);

    /// <summary>类型节点点击回调（参数：类型全名 + 程序集路径）。Debugger 页接此反编译显示。</summary>
    [Parameter] public Func<string, string, Task>? TypeClicked { get; set; }

    /// <summary>程序集节点点击/加载后调用的可选通知（参数：程序集路径）。agent 上下文驱动/高亮用。</summary>
    [Parameter] public Func<string, Task>? AssemblyLoaded { get; set; }

    /// <summary>树组件首次渲染就绪回调（_tree ref + BB 初始化完成）。宿主页在此补执行默认/待处理跳转——比宿主页自身 OnAfterRender 时序可靠。</summary>
    [Parameter] public Func<Task>? OnReady { get; set; }

    /// <summary>加载一个程序集进树（作为根节点，全量嵌套已建）。已加载则忽略；文件不存在返回 false。</summary>
    public bool LoadAssembly(string assemblyPath)
    {
        var full = Path.GetFullPath(assemblyPath);
        if (_items.Any(i => string.Equals(i.Value.AssemblyPath, full, StringComparison.OrdinalIgnoreCase))) return true;
        if (_data.GetNamespaces(full) is null) return false; // 非程序集/不存在：不入树
        var root = BuildAssemblyTree(full);
        _items.Add(root);
        _itemsChangedSinceRender = true;   // Items 变化未渲染：后续 SelectTypeAsync 走渲染后补选中
        // BB TreeView 按 Items 引用变化刷新：经 SetItems 换新引用并 StateHasChanged
        if (_tree is not null) _tree.SetItems(_items);
        else StateHasChanged();
        return true;
    }

    /// <summary>定位到指定类型（可带方法 token 下钻到方法叶子）：沿路径设 IsExpand + SetActiveItem。返回是否成功。
    /// 组件未就绪时等待首次渲染完成再执行（BB 初始化会覆盖此前的 SetActiveItem）。</summary>
    public async Task<bool> SelectTypeAsync(string assemblyPath, string typeFullName, int methodToken = 0)
    {
        if (!_ready) await _readyTcs.Task; // 等树组件首次渲染就绪，避免 SetActiveItem 被 BB 初始化吞掉
        var full = Path.GetFullPath(assemblyPath);
        var root = _items.FirstOrDefault(i => string.Equals(i.Value.AssemblyPath, full, StringComparison.OrdinalIgnoreCase));
        if (root is null)
        {
            if (!LoadAssembly(full)) return false;
            root = _items.FirstOrDefault(i => string.Equals(i.Value.AssemblyPath, full, StringComparison.OrdinalIgnoreCase));
            if (root is null) return false;
        }
        // 数据全量在树：直接沿路径找节点并展开（不建节点）
        var nsName = NamespaceOf(typeFullName);
        var nsNode = root.Items.FirstOrDefault(n => string.Equals(n.Value.Label, nsName, StringComparison.Ordinal));
        if (nsNode is null) { MemoryLog.Write("TypeTree", $"SelectType: ns '{nsName}' 未找到"); return false; }
        var typeNode = nsNode.Items.FirstOrDefault(t => string.Equals(t.Value.TypeFullName, typeFullName, StringComparison.Ordinal));
        if (typeNode is null) { MemoryLog.Write("TypeTree", $"SelectType: 类型 '{typeFullName}' 未找到（ns '{nsName}' 下 {nsNode.Items.Count} 个）"); return false; }

        TreeViewItem<TypeTreeNode>? target = typeNode;
        if (methodToken > 0)
        {
            var methodNode = typeNode.Items.FirstOrDefault(m => m.Value.Kind == "member"
                && m.Value.MemberKind == TypeMemberKind.Method && m.Value.MethodToken == methodToken);
            if (methodNode is not null) target = methodNode;
        }
        // 展开路径：程序集 → ns → 类型 → 成员（agent 看类型也展开成员层，能看到方法列表）
        root.IsExpand = true;
        nsNode.IsExpand = true;
        typeNode.IsExpand = true;
        if (_itemsChangedSinceRender)
        {
            // Items 刚 SetItems 尚未渲染：缓存目标，待 OnAfterRenderAsync（渲染完成）后 SetActiveItem，
            // 避免 SetItems 触发的渲染/BB 初始化覆盖此处的选中。
            _pendingActive = target;
            MemoryLog.Write("TypeTree", $"SelectType: 定位 {typeFullName} -> 渲染后补选中（pending）");
        }
        else
        {
            _tree?.SetActiveItem(target);
            MemoryLog.Write("TypeTree", $"SelectType: 定位 {typeFullName} -> {(target == typeNode ? "类型" : "方法")}，SetActiveItem");
        }
        return true;
    }

    /// <summary>组装一个程序集根节点：ns → 类型 → 成员全量嵌套（默认收缩），Text/短名就位。</summary>
    private TreeViewItem<TypeTreeNode> BuildAssemblyTree(string assemblyPath)
    {
        var fileName = Path.GetFileName(assemblyPath);
        var root = new TreeViewItem<TypeTreeNode>(new(fileName, "assembly", assemblyPath))
        { Text = fileName, HasChildren = true };
        foreach (var ns in _data.GetNamespaces(assemblyPath) ?? [])
        {
            var types = _data.GetTypes(assemblyPath, ns);
            var nsNode = new TreeViewItem<TypeTreeNode>(new(ns, "namespace", assemblyPath))
            { Text = ns, HasChildren = types.Count > 0 };
            foreach (var type in types)
            {
                var members = _data.GetMembers(assemblyPath, type);
                var typeNode = new TreeViewItem<TypeTreeNode>(new(ShortName(type), "type", assemblyPath, type))
                { Text = ShortName(type), HasChildren = members.Count > 0 };
                foreach (var m in members) typeNode.Items.Add(MemberItem(typeNode, m));
                nsNode.Items.Add(typeNode);
            }
            root.Items.Add(nsNode);
        }
        return root;
    }

    /// <summary>成员叶子节点：显示名带种类前缀（dnSpyEx 风格），方法/属性/事件/字段。方法叶子带 MethodToken 供停点匹配。</summary>
    private static TreeViewItem<TypeTreeNode> MemberItem(TreeViewItem<TypeTreeNode> parent, TypeMember m)
    {
        var kindTag = m.Kind switch
        {
            TypeMemberKind.Method => "M ",
            TypeMemberKind.Property => "P ",
            TypeMemberKind.Event => "E ",
            TypeMemberKind.Field => "F ",
            _ => "",
        };
        // 方法显示带 () 示意可调用（无参签名 v1；完整签名需反编译签名，后置）
        var text = m.Kind == TypeMemberKind.Method ? $"{kindTag}{m.Name}()" : $"{kindTag}{m.Name}";
        return new TreeViewItem<TypeTreeNode>(new(text, "member", parent.Value.AssemblyPath,
            parent.Value.TypeFullName, m.Token, m.Kind)) { Text = text };
    }

    private async Task OnNodeClickedAsync(TreeViewItem<TypeTreeNode> item)
    {
        var v = item.Value;
        if (v.Kind == "type" && v.AssemblyPath is not null && v.TypeFullName is not null && TypeClicked is not null)
        {
            await TypeClicked(v.AssemblyPath, v.TypeFullName);
        }
    }

    /// <summary>类型全名 → 所属命名空间（与 TypeTreeData.GetNamespace 同规则）：最后一个 . 之前为命名空间；无 . 时 (全局)。</summary>
    private static string NamespaceOf(string typeFullName)
    {
        var plus = typeFullName.IndexOf('+');
        if (plus > 0) typeFullName = typeFullName[..plus];
        var lastDot = typeFullName.LastIndexOf('.');
        return lastDot <= 0 ? "(全局)" : typeFullName[..lastDot];
    }

    private static string ShortName(string fullName)
    {
        var plus = fullName.LastIndexOf('+');
        var dot = fullName.LastIndexOf('.');
        var start = plus > dot ? plus + 1 : dot + 1;
        return start > 0 ? fullName[start..] : fullName;
    }
}
