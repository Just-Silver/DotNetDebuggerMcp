// DotNetDebugger.Web CodeViewer 的 Monaco 最小互操作桥（自研，参考 DebuggerExternals\BlazorMonaco）。
// v1 仅 5 方法：create / setValue / deltaDecorations / revealLineInCenter / dispose。
// 前置：App.razor 已定义 require.paths 并按序加载 loader.js + editor.main.js（全局 monaco 就绪）。
window.dotnetDebuggerMonaco = window.dotnetDebuggerMonaco || {};
window.dotnetDebuggerMonaco.editors = window.dotnetDebuggerMonaco.editors || {};
window.dotnetDebuggerMonaco._decorations = window.dotnetDebuggerMonaco._decorations || {};
// setValue 先于编辑器创建到达时的文本暂存（create 完成后回放）
window.dotnetDebuggerMonaco._pendingValues = window.dotnetDebuggerMonaco._pendingValues || {};
// 光标行回调（.NET → JS 注册 DotNetObjectReference；编辑器就绪即挂钩）
window.dotnetDebuggerMonaco._cursorRefs = window.dotnetDebuggerMonaco._cursorRefs || {};
// setValue 程序性移动光标产生的首个事件抑制标记（防联动树误跳）
window.dotnetDebuggerMonaco._suppressCursor = window.dotnetDebuggerMonaco._suppressCursor || {};

// 挂光标行监听（编辑器创建时若已有回调引用则调用；e.position.lineNumber 推给 .NET）。
// 只派发用户交互（点击/按键）后的光标事件——setPosition/setValue 等程序性移动一律过滤，
// 防止行→方法映射（行 1 → 其后最近方法）覆盖停点跟随/agent 联动的树选中。
// 不用 hasTextFocus 门禁：点击时光标事件先于焦点建立会被误滤（实测）。
// 同一引用顺带挂 glyph 区点击（OnGlyphClick，行号）——断点红点区点击设/删断点。
function dotnetdbgHookCursor(editor) {
    if (editor.__cursorHooked) return;
    editor.__cursorHooked = true;
    editor.onMouseDown(function () { editor.__userInteract = true; });
    editor.onKeyDown(function () { editor.__userInteract = true; });
    // glyph 区（断点红点槽）点击 → OnGlyphClick（行号）。
    // 不用 monaco.MouseTargetType：该全局枚举在 AMD min 构建未导出（实测 undefined），glyph 点击 target.type 也报 TEXT_WITHIN——
    // 改坐标判定：点击 x 落在 glyphMarginWidth 内即视为 glyph 区。
    editor.onMouseDown(function (e) {
        var id = editor.__id;
        if (!e.target || !e.target.position) return;
        var layout = editor.getLayoutInfo();
        var rect = editor.getDomNode().getBoundingClientRect();
        var x = (e.event && typeof e.event.posx === 'number') ? e.event.posx - (rect.left + window.scrollX) : -1;
        if (x < 0 || x >= (layout.glyphMarginWidth || 0)) return;
        var ref = window.dotnetDebuggerMonaco._cursorRefs[id];
        if (ref) ref.invokeMethodAsync('OnGlyphClick', e.target.position.lineNumber);
    });
    editor.onDidChangeCursorPosition(function (e) {
        var id = editor.__id;
        if (!editor.__userInteract) return;
        if (window.dotnetDebuggerMonaco._suppressCursor[id]) {
            window.dotnetDebuggerMonaco._suppressCursor[id] = false;
            return;
        }
        var ref = window.dotnetDebuggerMonaco._cursorRefs[id];
        if (ref) ref.invokeMethodAsync('OnCursorLine', e.position.lineNumber);
    });
}

// 立即建编辑器（monaco/容器就绪时）；返回是否完成。建好后回放暂存文本。
function dotnetdbgCreateNow(id, language) {
    var holder = document.getElementById(id);
    if (!holder) return false;
    if (typeof monaco === 'undefined') return false;
    if (window.dotnetDebuggerMonaco.editors[id]) return true;
    var editor = monaco.editor.create(holder, {
        value: '',
        language: language || 'csharp',
        readOnly: true,
        automaticLayout: true,
        theme: 'vs-dark',           // 配合全局暗色（默认 data-bs-theme=dark）
        glyphMargin: true,          // 断点圆点槽
        lineNumbers: 'on',
        scrollBeyondLastLine: false
    });
    window.dotnetDebuggerMonaco.editors[id] = editor;
    editor.__id = id;
    if (window.dotnetDebuggerMonaco._cursorRefs[id]) dotnetdbgHookCursor(editor);
    var pending = window.dotnetDebuggerMonaco._pendingValues[id];
    if (typeof pending === 'string') {
        delete window.dotnetDebuggerMonaco._pendingValues[id];
        editor.setValue(pending);
    }
    return true;
}

// 建编辑器：monaco 全局由 AMD editor.main.js 异步初始化（晚于 script 标签执行完），
// 首渲互操作调用可能赶在其前——轮询重试而非静默放弃（否则编辑器永久空白，装饰全失效）。
window.dotnetDebuggerMonaco.create = function (id, language) {
    if (window.dotnetDebuggerMonaco.editors[id]) return; // 已建
    if (dotnetdbgCreateNow(id, language)) return;
    var tries = 0;
    (function retry() {
        if (window.dotnetDebuggerMonaco.editors[id]) return;
        if (++tries > 150) { console.error('CodeViewer: monaco/容器 15s 未就绪，放弃创建 #' + id); return; }
        if (dotnetdbgCreateNow(id, language)) return;
        setTimeout(retry, 100);
    })();
};

// 设文本（换文档 = 全量 setValue + 清装饰，v1 单文档够用）。编辑器未建时暂存文本并兜底排程创建。
window.dotnetDebuggerMonaco.setValue = function (id, text) {
    var editor = window.dotnetDebuggerMonaco.editors[id];
    if (!editor) {
        window.dotnetDebuggerMonaco.create(id);
        editor = window.dotnetDebuggerMonaco.editors[id];
        if (!editor) {
            window.dotnetDebuggerMonaco._pendingValues[id] = text || '';
            return;
        }
    }
    editor.setValue(text || '');
    // setValue 会程序性移动光标到起始行：抑制由此产生的首个光标事件（防联动树误跳）
    window.dotnetDebuggerMonaco._suppressCursor[id] = true;
    // 清旧装饰（换文本后 old 全失效）
    window.dotnetDebuggerMonaco._decorations[id] = editor.deltaDecorations(window.dotnetDebuggerMonaco._decorations[id] || [], []);
};

// 注册光标行回调（编辑器已建则立即挂钩；未建则暂存，create 完成时挂钩）
window.dotnetDebuggerMonaco.setCursorCallback = function (id, dotnetRef) {
    window.dotnetDebuggerMonaco._cursorRefs[id] = dotnetRef;
    var editor = window.dotnetDebuggerMonaco.editors[id];
    if (editor) dotnetdbgHookCursor(editor);
};

// 装饰：断点行（glyph 圆点）+ 当前行（背景）+ 选中成员行区间（背景）。全量重推（old 由 C# 侧维护传入）。
// 编辑器未建时忽略（C# 侧在 文档换页/断点变化/停点跃迁 时会重推，无需暂存）。
window.dotnetDebuggerMonaco.deltaDecorations = function (id, oldIds, breakpointLines, currentLine, memberStart, memberEnd) {
    var editor = window.dotnetDebuggerMonaco.editors[id];
    if (!editor) return [];
    var newDeco = [];
    (breakpointLines || []).forEach(function (line) {
        newDeco.push({
            range: new monaco.Range(line, 1, line, 1),
            options: {
                isWholeLine: false,
                glyphMarginClassName: 'dotnetdbg-breakpoint-glyph',
                glyphMarginHoverMessage: { value: '断点' }
            }
        });
    });
    if (currentLine && currentLine > 0) {
        newDeco.push({
            range: new monaco.Range(currentLine, 1, currentLine, 1),
            options: {
                isWholeLine: true,
                className: 'dotnetdbg-current-line'
            }
        });
    }
    if (memberStart > 0 && memberEnd >= memberStart) {
        newDeco.push({
            range: new monaco.Range(memberStart, 1, memberEnd, 1),
            options: {
                isWholeLine: true,
                className: 'dotnetdbg-selected-member'
            }
        });
    }
    var newIds = editor.deltaDecorations(oldIds || [], newDeco);
    window.dotnetDebuggerMonaco._decorations[id] = newIds;
    return newIds;
};

// 滚动定位到行（停点命中时）
window.dotnetDebuggerMonaco.revealLineInCenter = function (id, line) {
    var editor = window.dotnetDebuggerMonaco.editors[id];
    if (!editor || !line || line < 1) return;
    editor.revealLineInCenter(line);
};

// 销毁编辑器（组件 Dispose 时）
window.dotnetDebuggerMonaco.dispose = function (id) {
    var editor = window.dotnetDebuggerMonaco.editors[id];
    if (editor) {
        editor.dispose();
        delete window.dotnetDebuggerMonaco.editors[id];
    }
    delete window.dotnetDebuggerMonaco._pendingValues[id];
    delete window.dotnetDebuggerMonaco._cursorRefs[id];
    delete window.dotnetDebuggerMonaco._suppressCursor[id];
};

// === Monaco 跟随 BB 明暗主题切换 ===
// BB 切主题经 setTheme 改 html[data-bs-theme] 并触发全局事件 changed.bb.theme（utility.js 官方机制）。
// 监听该事件，把实际主题（读 html 属性，auto 已被解析成实际值）映射到 Monaco theme。
(function () {
    var listenersAttached = false;
    function applyTheme() {
        var theme = document.documentElement.getAttribute('data-bs-theme');
        var monacoTheme = theme === 'dark' ? 'vs-dark' : 'vs';
        if (typeof monaco !== 'undefined') {
            monaco.editor.setTheme(monacoTheme);
        }
    }
    if (listenersAttached) return;
    listenersAttached = true;
    document.addEventListener('changed.bb.theme', applyTheme);
    // 初始应用一次（页面加载时 html 已带 data-bs-theme）
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', applyTheme);
    } else {
        applyTheme();
    }
})();
