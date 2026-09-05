using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace DotNetDebugger.Web.Components.Pages;

/// <summary>首页：产品说明 + 调试工作台入口。</summary>
public partial class Index
{
    [Inject] private NavigationManager Nav { get; set; } = default!;

    private void GoDebugger() => Nav.NavigateTo("/debugger");
}
