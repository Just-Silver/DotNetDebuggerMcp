// DotNetDebugger.Web CodeViewer 的 Monaco 最小互操作桥（自研，参考 DebuggerExternals\BlazorMonaco）。
// v1 仅 5 方法：create / setValue / deltaDecorations / revealLineInCenter / dispose。
// 前置：App.razor 已定义 require.paths 并按序加载 loader.js + editor.main.js（全局 monaco 就绪）。
window.dotnetDebuggerMonaco = window.dotnetDebuggerMonaco || {};
window.dotnetDebuggerMonaco.editors = window.dotnetDebuggerMonaco.editors || {};
window.dotnetDebuggerMonaco._decorations = window.dotnetDebuggerMonaco._decorations || {};

// 建编辑器：options 需 AutomaticLayout:true 让 Monaco 自适配容器尺寸
window.dotnetDebuggerMonaco.create = function (id, language) {
    var holder = document.getElementById(id);
    if (!holder) {
        console.error('CodeViewer: 找不到容器 #' + id);
        return;
    }
    if (typeof monaco === 'undefined') {
        console.error('CodeViewer: monaco 未加载（检查 loader.js/editor.main.js script 顺序）');
        return;
    }
    if (window.dotnetDebuggerMonaco.editors[id]) {
        return; // 已建
    }
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
};

// 设文本（换文档 = 全量 setValue + 清装饰，v1 单文档够用）
window.dotnetDebuggerMonaco.setValue = function (id, text) {
    var editor = window.dotnetDebuggerMonaco.editors[id];
    if (!editor) return;
    editor.setValue(text || '');
    // 清旧装饰（换文本后 old 全失效）
    window.dotnetDebuggerMonaco._decorations[id] = editor.deltaDecorations(window.dotnetDebuggerMonaco._decorations[id] || [], []);
};

// 装饰：断点行（glyph 圆点）+ 当前行（背景）。全量重推（old 由 C# 侧维护传入）
window.dotnetDebuggerMonaco.deltaDecorations = function (id, oldIds, breakpointLines, currentLine) {
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
